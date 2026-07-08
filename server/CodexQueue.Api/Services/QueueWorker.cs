using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexQueue.Api.Data;
using CodexQueue.Api.Domain;
using CodexQueue.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace CodexQueue.Api.Services;

public interface IQueueCoordinator
{
    Task<bool> CancelRequestAsync(Guid requestId, CancellationToken cancellationToken);
    Task<bool> ResumeRequestAsync(Guid requestId, CancellationToken cancellationToken);
    Task<bool> KickQueueAsync(CancellationToken cancellationToken);
    QueueWorkerDiagnostics GetDiagnostics();
}

public sealed record QueueWorkerDiagnostics(
    DateTimeOffset? LastHeartbeat,
    DateTimeOffset? LastDispatch,
    DateTimeOffset? LastIdle,
    string? LastError,
    IReadOnlyCollection<Guid> ActiveRequestIds,
    bool IsProcessing);

public sealed class QueueWorker(
    IServiceScopeFactory scopeFactory,
    ITargetCommandRunner runner,
    ILogger<QueueWorker> logger) : BackgroundService, IQueueCoordinator
{
    private const int MaxStoredOutput = 512_000;
    private static readonly TimeSpan CommitRunTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DefaultUsageLimitBackoff = TimeSpan.FromMinutes(1);
    private const string UsageLimitUnknownReason = "Usage limit reached.";
    private static readonly Regex RetryAfterHeaderRegex = new(@"(?i)retry[-_\s]*after\s*[:=]\s*([^\s,;]+)", RegexOptions.Compiled);
    private static readonly Regex RetryAfterDurationRegex = new(@"(?i)(\d+)\s*(s|sec|secs?|seconds?|m|min|mins?|minutes?|h|hrs?|hours?)\b", RegexOptions.Compiled);
    private static readonly Regex RetryAfterDateRegex = new(@"(?i)(?:retry[-_\s]*after\s*[:=]\s*)([^;\n\r]+)", RegexOptions.Compiled);
    private static readonly Regex UsageLimitKeywordRegex = new(@"(?i)(rate\s*-?\s*limit|quota|throttle|too\s+many\s+requests|429|rate\s*limit|usage\s+limit)", RegexOptions.Compiled);
    private static readonly Regex ModelRegex = new(@"(?i)(gpt-[a-z0-9._-]+)", RegexOptions.Compiled);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeRequests = new();
    private readonly SemaphoreSlim _processLock = new(1, 1);
    private DateTimeOffset? _lastHeartbeat;
    private DateTimeOffset? _lastDispatch;
    private DateTimeOffset? _lastIdle;
    private string? _lastError;

    public async Task<bool> CancelRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.Requests.Include(x => x.Runs).FirstOrDefaultAsync(x => x.Id == requestId, cancellationToken);
        if (request is null)
        {
            return false;
        }

        if (request.Status is QueueStatus.Succeeded or QueueStatus.Failed or QueueStatus.Cancelled)
        {
            return true;
        }

        var isActive = _activeRequests.ContainsKey(requestId);
        request.Status = isActive ? QueueStatus.CancelRequested : QueueStatus.Cancelled;
        request.FinishedAt = isActive ? request.FinishedAt : DateTimeOffset.UtcNow;
        request.Error = isActive ? request.Error : "Cancelled before start.";
        foreach (var run in request.Runs.Where(x =>
                     x.Status is QueueStatus.Queued or QueueStatus.Running or QueueStatus.CancelRequested or QueueStatus.UsageLimited))
        {
            run.Status = isActive ? QueueStatus.CancelRequested : QueueStatus.Cancelled;
            run.FinishedAt = isActive ? run.FinishedAt : request.FinishedAt;
            run.Error = isActive ? run.Error : "Cancelled by user.";
        }

        if (!await TrySaveChangesAsync(db, "cancel request", cancellationToken))
        {
            return true;
        }

        if (isActive && _activeRequests.TryGetValue(requestId, out var tokenSource))
        {
            await tokenSource.CancelAsync();
        }

        return true;
    }

    public async Task<bool> ResumeRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.Requests.Include(x => x.Runs).FirstOrDefaultAsync(x => x.Id == requestId, cancellationToken);
        if (request is null)
        {
            return false;
        }

        if (_activeRequests.ContainsKey(requestId))
        {
            return true;
        }

        ResumeRequest(request);
        await TrySaveChangesAsync(db, "resume request", cancellationToken);
        return true;
    }

    public Task<bool> KickQueueAsync(CancellationToken cancellationToken)
    {
        if (!_processLock.Wait(0))
        {
            return Task.FromResult(false);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessNextAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                logger.LogError(ex, "Queue kick failed.");
            }
            finally
            {
                _processLock.Release();
            }
        }, CancellationToken.None);

        return Task.FromResult(true);
    }

    public QueueWorkerDiagnostics GetDiagnostics() =>
        new(
            _lastHeartbeat,
            _lastDispatch,
            _lastIdle,
            _lastError,
            _activeRequests.Keys.ToArray(),
            _processLock.CurrentCount == 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessNextLockedAsync(stoppingToken);
                if (!processed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                logger.LogError(ex, "Queue loop failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessNextLockedAsync(CancellationToken stoppingToken)
    {
        await _processLock.WaitAsync(stoppingToken);
        try
        {
            return await ProcessNextAsync(stoppingToken);
        }
        finally
        {
            _processLock.Release();
        }
    }

    private async Task<bool> ProcessNextAsync(CancellationToken stoppingToken)
    {
        _lastHeartbeat = DateTimeOffset.UtcNow;
        CodexRequest? request;
        RunKind? nextRunKind = null;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await ResumeExpiredUsageLimitedRequestsAsync(db, stoppingToken);
            await ReconcileQueueStateAsync(db, stoppingToken);

            var now = DateTimeOffset.UtcNow;
            var queuedRequests = await db.Requests
                .Include(x => x.Runs)
                .Where(x => x.DeletedAt == null && (x.Status == QueueStatus.Queued || x.Status == QueueStatus.Running))
                .ToArrayAsync(stoppingToken);

            Guid? nextRequestId = null;
            Guid? nextRunId = null;
            foreach (var candidate in queuedRequests.OrderBy(x => x.QueueOrder).ThenBy(x => x.CreatedAt))
            {
                var candidateRun = NextRunForDispatch(candidate);
                if (candidateRun is null)
                {
                    continue;
                }

                var modelLimited = await IsRunModelUsageLimitedAsync(db, candidate.MachineId, candidateRun.Model, now, stoppingToken);
                if (!modelLimited)
                {
                    nextRequestId = candidate.Id;
                    nextRunId = candidateRun.Id;
                    nextRunKind = candidateRun.Kind;
                    break;
                }
            }

            if (nextRequestId is null)
            {
                _lastIdle = DateTimeOffset.UtcNow;
                return false;
            }

            request = await db.Requests
                .Include(x => x.Project).ThenInclude(x => x!.Machine)
                .Include(x => x.Machine)
                .Include(x => x.Runs)
                .FirstAsync(x => x.Id == nextRequestId, stoppingToken);

            if (request is null)
            {
                return false;
            }

            request.Status = QueueStatus.Running;
            request.StartedAt ??= DateTimeOffset.UtcNow;
            var run = nextRunId.HasValue
                ? request.Runs.First(x => x.Id == nextRunId.Value)
                : request.Runs.First(x => x.Kind == RunKind.Request);
            run.Status = QueueStatus.Running;
            run.StartedAt = DateTimeOffset.UtcNow;
            _lastDispatch = DateTimeOffset.UtcNow;
            _lastError = null;
            if (!await TrySaveChangesAsync(db, "mark request running", stoppingToken))
            {
                return false;
            }
        }

        if (nextRunKind == RunKind.Commit)
        {
            await RunCommitAsync(request.Id, stoppingToken);
        }
        else
        {
            await RunRequestAsync(request.Id, stoppingToken);
        }

        return true;
    }

    private async Task RunRequestAsync(Guid requestId, CancellationToken stoppingToken)
    {
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _activeRequests[requestId] = requestCancellation;

        try
        {
            var requestRunSucceeded = await IsRunSucceededAsync(requestId, RunKind.Request, requestCancellation.Token)
                || await ExecuteRunAsync(requestId, RunKind.Request, requestCancellation.Token);
            if (!requestRunSucceeded)
            {
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var request = await db.Requests.Include(x => x.Runs).FirstAsync(x => x.Id == requestId, stoppingToken);
            if (!request.GenerateCommit)
            {
                request.Status = QueueStatus.Succeeded;
                request.FinishedAt = DateTimeOffset.UtcNow;
                request.Summary = LastUsefulLine(request.Runs.First(x => x.Kind == RunKind.Request).Output);
                await TrySaveChangesAsync(db, "complete request without commit", stoppingToken);
                return;
            }

            var commitRun = request.Runs.FirstOrDefault(x => x.Kind == RunKind.Commit);
            if (commitRun?.Status == QueueStatus.Succeeded)
            {
                request.Status = QueueStatus.Succeeded;
                request.FinishedAt = commitRun.FinishedAt ?? DateTimeOffset.UtcNow;
                request.Error = null;
                request.Summary = LastUsefulLine(commitRun.Output);
                await TrySaveChangesAsync(db, "complete already committed request", stoppingToken);
                return;
            }

            commitRun ??= new CodexRun
            {
                RequestId = request.Id,
                Kind = RunKind.Commit,
                Status = QueueStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow
            };
            ApplyCommitModel(request, commitRun);
            if (!request.Runs.Contains(commitRun))
            {
                request.Runs.Add(commitRun);
            }

            if (commitRun.Status is QueueStatus.Failed or QueueStatus.Cancelled or QueueStatus.UsageLimited)
            {
                ResetRunForResume(commitRun);
            }

            request.Status = request.SeparateCommitSession ? QueueStatus.Queued : QueueStatus.Running;
            request.Error = null;
            request.FinishedAt = null;
            if (!await TrySaveChangesAsync(db, "prepare commit run", stoppingToken))
            {
                return;
            }

            if (!request.SeparateCommitSession)
            {
                await RunCommitAsync(requestId, stoppingToken);
            }
        }
        finally
        {
            _activeRequests.TryRemove(requestId, out _);
        }
    }

    private async Task RunCommitAsync(Guid requestId, CancellationToken stoppingToken)
    {
        using var commitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var commitTimedOut = false;
        using var commitTimeout = new Timer(_ =>
        {
            commitTimedOut = true;
            commitCancellation.Cancel();
        }, null, CommitRunTimeout, Timeout.InfiniteTimeSpan);
        _activeRequests[requestId] = commitCancellation;
        try
        {
            var committed = await ExecuteRunAsync(requestId, RunKind.Commit, commitCancellation.Token);
            if (!committed && commitTimedOut)
            {
                await MarkFailedAsync(
                    requestId,
                    Guid.Empty,
                    RunKind.Commit,
                    new TimeoutException("Commit run exceeded the 3 minute timeout."),
                    CancellationToken.None);
            }
        }
        finally
        {
            _activeRequests.TryRemove(requestId, out _);
        }
    }

    private async Task<bool> ExecuteRunAsync(Guid requestId, RunKind kind, CancellationToken cancellationToken)
    {
        CodexRequest request;
        CodexRun run;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            request = await db.Requests
                .Include(x => x.Project).ThenInclude(x => x!.Machine)
                .Include(x => x.Machine)
                .Include(x => x.Runs)
                .FirstAsync(x => x.Id == requestId, cancellationToken);
            run = request.Runs.First(x => x.Kind == kind);
            run.Status = QueueStatus.Running;
            run.StartedAt ??= DateTimeOffset.UtcNow;
            if (!await TrySaveChangesAsync(db, "mark run running", cancellationToken))
            {
                return false;
            }
        }

        var machine = request.Project?.Machine ?? request.Machine ?? throw new InvalidOperationException("Request machine is missing.");
        var project = request.Project ?? throw new InvalidOperationException("Request project is missing.");
        var projectPath = project.Path;

        try
        {
            if (kind == RunKind.Commit)
            {
                var commitResult = request.SeparateCommitSession
                    ? await runner.RunCodexAsync(
                        machine,
                        projectPath,
                        run.Model,
                        run.ModelEffort,
                        run.ModelSpeed,
                        project.CodexSessionId,
                        null,
                        BuildProjectScopedPrompt(projectPath, BuildCommitPrompt()),
                        chunk => AppendOutputAsync(run.Id, chunk, CancellationToken.None),
                        cancellationToken)
                    : await runner.RunShellAsync(
                        machine,
                        projectPath,
                        BuildCommitShellCommand(machine, BuildCommitMessage(request.Prompt)),
                        chunk => AppendOutputAsync(run.Id, chunk, CancellationToken.None),
                        cancellationToken);

                await CompleteRunAsync(requestId, run.Id, kind, commitResult, cancellationToken);
                return commitResult.Success;
            }

            var prompt = BuildProjectScopedPrompt(projectPath, BuildRequestPrompt(request));
            var attachments = await MaterializeAttachmentsAsync(request, project, machine, cancellationToken);
            if (!string.IsNullOrWhiteSpace(attachments.PromptSection))
            {
                prompt += Environment.NewLine + Environment.NewLine + attachments.PromptSection;
            }
            var codexSessionId = project.CodexSessionId;
            var result = await runner.RunCodexAsync(
                machine,
                projectPath,
                run.Model,
                run.ModelEffort,
                run.ModelSpeed,
                codexSessionId,
                attachments.ImagePaths,
                prompt,
                chunk => AppendOutputAsync(run.Id, chunk, CancellationToken.None),
                cancellationToken);

            if (TryParseUsageLimit(result, out var usageLimit))
            {
                await MarkUsageLimitedAsync(request, run, kind, result, usageLimit, cancellationToken);
                return false;
            }

            await CompleteRunAsync(requestId, run.Id, kind, result, cancellationToken);
            return result.Success;
        }
        catch (OperationCanceledException)
        {
            await MarkCancelledAsync(requestId, run.Id, kind, CancellationToken.None);
            return false;
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(requestId, run.Id, kind, ex, CancellationToken.None);
            return false;
        }
    }

    private async Task<bool> IsRunSucceededAsync(Guid requestId, RunKind kind, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Runs.AnyAsync(x =>
                x.RequestId == requestId &&
                x.Kind == kind &&
                x.Status == QueueStatus.Succeeded,
            cancellationToken);
    }

    private async Task ReconcileQueueStateAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var requests = await db.Requests
            .Include(x => x.Runs)
            .Where(x => x.DeletedAt == null
                && (x.Status == QueueStatus.Queued
                || x.Status == QueueStatus.Running
                || x.Status == QueueStatus.Failed
                || x.Status == QueueStatus.UsageLimited))
            .ToArrayAsync(cancellationToken);

        var changed = false;
        foreach (var request in requests)
        {
            changed |= ReconcileRequestState(request);
        }

        if (changed)
        {
            await TrySaveChangesAsync(db, "reconcile queue state", cancellationToken);
        }
    }

    private static bool ReconcileRequestState(CodexRequest request)
    {
        var requestRun = request.Runs.FirstOrDefault(x => x.Kind == RunKind.Request);
        var commitRun = request.Runs.FirstOrDefault(x => x.Kind == RunKind.Commit);
        if (requestRun is null)
        {
            return false;
        }

        var changed = false;
        if (RepairFalseUsageLimit(requestRun))
        {
            changed = true;
        }

        if (commitRun is not null && RepairFalseUsageLimit(commitRun))
        {
            changed = true;
        }

        if (requestRun.Status == QueueStatus.Succeeded && !request.GenerateCommit && request.Status != QueueStatus.Succeeded)
        {
            CancelUnusedCommitRun(commitRun);
            MarkRequestSucceeded(request, requestRun);
            return true;
        }

        if (requestRun.Status == QueueStatus.Succeeded && request.GenerateCommit)
        {
            if (commitRun?.Status == QueueStatus.Succeeded && request.Status != QueueStatus.Succeeded)
            {
                MarkRequestSucceeded(request, commitRun);
                return true;
            }

            if (commitRun is null)
            {
                request.Runs.Add(CreateCommitRun(request));
                MarkRequestQueued(request);
                return true;
            }

            if (commitRun.Status == QueueStatus.Queued && request.Status != QueueStatus.Queued)
            {
                MarkRequestQueued(request);
                return true;
            }

            if (commitRun.Status == QueueStatus.Running && request.Status != QueueStatus.Running)
            {
                request.Status = QueueStatus.Running;
                request.Error = null;
                request.FinishedAt = null;
                return true;
            }
        }

        return changed;
    }

    private static bool RepairFalseUsageLimit(CodexRun run)
    {
        if (run.Status != QueueStatus.UsageLimited || run.ExitCode != 0)
        {
            return false;
        }

        run.Status = QueueStatus.Succeeded;
        run.Error = null;
        run.RetryAfter = null;
        run.RetryReason = null;
        run.AvailableModel = null;
        run.FinishedAt ??= DateTimeOffset.UtcNow;
        return true;
    }

    private static void CancelUnusedCommitRun(CodexRun? commitRun)
    {
        if (commitRun is null || commitRun.Status is QueueStatus.Succeeded or QueueStatus.Failed or QueueStatus.Cancelled)
        {
            return;
        }

        commitRun.Status = QueueStatus.Cancelled;
        commitRun.Error = "Commit handled by the main request session.";
        commitRun.FinishedAt ??= DateTimeOffset.UtcNow;
    }

    private static CodexRun? NextRunForDispatch(CodexRequest request)
    {
        var requestRun = request.Runs.FirstOrDefault(x => x.Kind == RunKind.Request);
        if (requestRun is null)
        {
            return null;
        }

        if (requestRun.Status != QueueStatus.Succeeded)
        {
            return requestRun.Status == QueueStatus.Queued ? requestRun : null;
        }

        if (!request.GenerateCommit)
        {
            return null;
        }

        var commitRun = request.Runs.FirstOrDefault(x => x.Kind == RunKind.Commit);
        return commitRun?.Status == QueueStatus.Queued ? commitRun : null;
    }

    private static void ResumeRequest(CodexRequest request)
    {
        var requestRun = request.Runs.FirstOrDefault(x => x.Kind == RunKind.Request);
        var commitRun = request.Runs.FirstOrDefault(x => x.Kind == RunKind.Commit);

        if (requestRun is null)
        {
            requestRun = new CodexRun
            {
                RequestId = request.Id,
                Kind = RunKind.Request,
                Model = request.Model,
                ModelEffort = request.ModelEffort,
                ModelSpeed = request.ModelSpeed,
                CreatedAt = DateTimeOffset.UtcNow
            };
            request.Runs.Add(requestRun);
        }

        ClearRequestRetryState(request);

        if (requestRun.Status != QueueStatus.Succeeded)
        {
            ResetRunForResume(requestRun);
            foreach (var staleCommitRun in request.Runs.Where(x => x.Kind == RunKind.Commit))
            {
                ResetRunForResume(staleCommitRun);
                staleCommitRun.Status = QueueStatus.Cancelled;
                staleCommitRun.Error = "Commit run reset because request stage is being resumed.";
                staleCommitRun.FinishedAt = DateTimeOffset.UtcNow;
            }

            request.Status = QueueStatus.Queued;
            request.StartedAt = null;
            request.FinishedAt = null;
            request.Error = null;
            return;
        }

        if (!request.GenerateCommit)
        {
            MarkRequestSucceeded(request, requestRun);
            return;
        }

        if (commitRun is null)
        {
            request.Runs.Add(CreateCommitRun(request));
        }
        else if (commitRun.Status != QueueStatus.Succeeded)
        {
            ResetRunForResume(commitRun);
        }

        request.Status = QueueStatus.Queued;
        request.FinishedAt = null;
        request.Error = null;
    }

    private static CodexRun CreateCommitRun(CodexRequest request) =>
        ApplyCommitModel(request, new CodexRun
        {
            RequestId = request.Id,
            Kind = RunKind.Commit,
            Status = QueueStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow
        });

    private static CodexRun ApplyCommitModel(CodexRequest request, CodexRun run)
    {
        var useRequestModel = string.IsNullOrWhiteSpace(request.CommitModel);
        run.Model = useRequestModel ? request.Model : request.CommitModel!;
        run.ModelEffort = useRequestModel ? request.ModelEffort : request.CommitModelEffort;
        run.ModelSpeed = useRequestModel ? request.ModelSpeed : request.CommitModelSpeed;
        return run;
    }

    private static void ResetRunForResume(CodexRun run)
    {
        run.Status = QueueStatus.Queued;
        run.Error = null;
        run.RetryAfter = null;
        run.RetryReason = null;
        run.AvailableModel = null;
        run.ExitCode = null;
        run.StartedAt = null;
        run.FinishedAt = null;
    }

    private static void MarkRequestQueued(CodexRequest request)
    {
        ClearRequestRetryState(request);
        request.Status = QueueStatus.Queued;
        request.Error = null;
        request.FinishedAt = null;
    }

    private static void MarkRequestSucceeded(CodexRequest request, CodexRun run)
    {
        ClearRequestRetryState(request);
        request.Status = QueueStatus.Succeeded;
        request.Error = null;
        request.FinishedAt = run.FinishedAt ?? DateTimeOffset.UtcNow;
        request.Summary = LastUsefulLine(run.Output);
    }

    private static void ClearRequestRetryState(CodexRequest request)
    {
        request.RetryAfter = null;
        request.RetryReason = null;
        request.AvailableModel = null;
    }

    private async Task CompleteRunAsync(Guid requestId, Guid runId, RunKind kind, CommandResult result, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.Requests
            .Include(x => x.Project)
            .Include(x => x.Runs)
            .FirstAsync(x => x.Id == requestId, cancellationToken);
        var run = runId == Guid.Empty
            ? request.Runs.First(x => x.Kind == kind)
            : request.Runs.First(x => x.Id == runId);
        var sessionId = result.CodexSessionId ?? request.Project?.CodexSessionId;

        run.CommandPreview = result.CommandPreview;
        run.CodexSessionId = sessionId;
        run.ExitCode = result.ExitCode;
        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Status = result.Success ? QueueStatus.Succeeded : QueueStatus.Failed;
        run.Error = result.Success ? null : LastUsefulLine(result.Output);

        if (kind == RunKind.Request && !string.IsNullOrWhiteSpace(sessionId) && request.Project is not null)
        {
            request.Project.CodexSessionId = sessionId;
            request.Project.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (kind == RunKind.Commit)
        {
            request.FinishedAt = run.FinishedAt;
            request.Status = result.Success ? QueueStatus.Succeeded : QueueStatus.Failed;
            request.Error = result.Success ? null : run.Error;
            request.Summary = LastUsefulLine(result.Output);
            if (result.Output.Contains("Commit created:", StringComparison.OrdinalIgnoreCase))
            {
                await PopulateCommitMetadataAsync(db, request, run, cancellationToken);
            }
        }
        else if (!request.GenerateCommit)
        {
            request.FinishedAt = run.FinishedAt;
            request.Status = result.Success ? QueueStatus.Succeeded : QueueStatus.Failed;
            request.Error = result.Success ? null : run.Error;
            request.Summary = LastUsefulLine(result.Output);
        }
        else if (!result.Success)
        {
            request.FinishedAt = run.FinishedAt;
            request.Status = QueueStatus.Failed;
            request.Error = run.Error;
        }

        await TrySaveChangesAsync(db, "complete run", cancellationToken);
    }

    private sealed record UsageLimitMetadata(
        DateTimeOffset RetryAfter,
        string Reason,
        string? AvailableModel);

    private bool TryParseUsageLimit(CommandResult result, out UsageLimitMetadata metadata)
    {
        if (result.Success)
        {
            metadata = new UsageLimitMetadata(DateTimeOffset.UtcNow, UsageLimitUnknownReason, null);
            return false;
        }

        var output = result.Output;
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var outputText = output.Trim();
        var hasUsageSignal = outputText.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                             || outputText.Contains("rate-limit", StringComparison.OrdinalIgnoreCase)
                             || outputText.Contains("quota", StringComparison.OrdinalIgnoreCase)
                             || outputText.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
                             || outputText.Contains("429", StringComparison.OrdinalIgnoreCase)
                             || outputText.Contains("throttle", StringComparison.OrdinalIgnoreCase)
                             || UsageLimitKeywordRegex.IsMatch(outputText);
        if (!hasUsageSignal)
        {
            metadata = new UsageLimitMetadata(DateTimeOffset.UtcNow, UsageLimitUnknownReason, null);
            return false;
        }

        DateTimeOffset retryAfter = FindRetryAfter(outputText, lines);
        var reason = FindReason(lines, outputText);
        var availableModel = FindAvailableModel(lines, outputText);
        metadata = new UsageLimitMetadata(
            RetryAfter: retryAfter,
            Reason: reason,
            AvailableModel: availableModel);
        return true;
    }

    private static string FindReason(string[] lines, string outputText)
    {
        foreach (var line in lines)
        {
            if (UsageLimitKeywordRegex.IsMatch(line))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    return trimmed.Length > 512 ? trimmed[..512] + "..." : trimmed;
                }
            }
        }

        foreach (var line in lines)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("error", out var errorElement))
                {
                    var message = errorElement.ValueKind switch
                    {
                        JsonValueKind.String => errorElement.GetString(),
                        JsonValueKind.Object when errorElement.TryGetProperty("message", out var messageElement) => messageElement.GetString(),
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message.Trim();
                    }
                }

                if (document.RootElement.TryGetProperty("message", out var messageProperty))
                {
                    var message = messageProperty.GetString();
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        var value = message.Trim();
                        if (UsageLimitKeywordRegex.IsMatch(value))
                        {
                            return value.Length > 512 ? value[..512] + "..." : value;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore mixed output that is not JSON.
            }
        }

        return !string.IsNullOrWhiteSpace(outputText)
            ? outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? UsageLimitUnknownReason
            : UsageLimitUnknownReason;
    }

    private static DateTimeOffset FindRetryAfter(string outputText, string[] lines)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var line in lines)
        {
            var headerMatch = RetryAfterHeaderRegex.Match(line);
            if (headerMatch.Success)
            {
                var value = headerMatch.Groups[1].Value;
                if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var retryAfterDate))
                {
                    return retryAfterDate;
                }

                if (TryParseRetrySeconds(value, out var retryAfter))
                {
                    return now.AddSeconds(retryAfter);
                }
            }

            if (RetryAfterDateRegex.IsMatch(line))
            {
                var match = RetryAfterDateRegex.Match(line);
                if (match.Groups.Count > 1
                    && DateTimeOffset.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var retryAfterDate))
                {
                    return retryAfterDate;
                }
            }
        }

        var durationMatchLine = lines.FirstOrDefault(x => RetryAfterDurationRegex.IsMatch(x));
        if (durationMatchLine is not null)
        {
            var match = RetryAfterDurationRegex.Match(durationMatchLine);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var value))
            {
                var unit = match.Groups[2].Value.ToLowerInvariant();
                return unit.StartsWith("h") ? now.AddHours(value) : unit.StartsWith("m") && !unit.StartsWith("ms") ? now.AddMinutes(value) : now.AddSeconds(value);
            }
        }

        return now.Add(DefaultUsageLimitBackoff);
    }

    private static bool TryParseRetrySeconds(string value, out int seconds)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
        {
            return true;
        }

        var match = RetryAfterDurationRegex.Match(value);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
        {
            var unit = match.Groups[2].Value.ToLowerInvariant();
            seconds = unit.StartsWith("m") && !unit.StartsWith("ms") ? seconds * 60 : unit.StartsWith("h") ? seconds * 3_600 : seconds;
            return true;
        }

        seconds = 0;
        return false;
    }

    private static string? FindAvailableModel(string[] lines, string outputText)
    {
        foreach (var line in lines)
        {
            if (line.Contains("model", StringComparison.OrdinalIgnoreCase))
            {
                var match = ModelRegex.Match(line);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
        }

        var matchAll = ModelRegex.Match(outputText);
        return matchAll.Success ? matchAll.Groups[1].Value : null;
    }

    private async Task MarkUsageLimitedAsync(CodexRequest request, CodexRun run, RunKind kind, CommandResult result, UsageLimitMetadata metadata, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        request = await db.Requests
            .Include(x => x.Runs)
            .FirstAsync(x => x.Id == request.Id, cancellationToken);
        run = request.Runs.First(x => x.Kind == kind);
        var finishedAt = DateTimeOffset.UtcNow;
        run.CommandPreview = result.CommandPreview;
        run.CodexSessionId = result.CodexSessionId ?? run.CodexSessionId;
        run.ExitCode = result.ExitCode;

        SetUsageLimitedState(request, metadata, finishedAt);
        SetUsageLimitedState(run, metadata, finishedAt);
        await PauseQueuedRequestsForModelAsync(db, request.MachineId, run.Model, request.Id, metadata, cancellationToken);

        await TrySaveChangesAsync(db, "mark usage limited", cancellationToken);
    }

    private static void SetUsageLimitedState(CodexRequest request, UsageLimitMetadata metadata, DateTimeOffset finishedAt)
    {
        request.Status = QueueStatus.UsageLimited;
        request.FinishedAt = finishedAt;
        request.Error = metadata.Reason;
        request.RetryAfter = metadata.RetryAfter;
        request.RetryReason = metadata.Reason;
        request.AvailableModel = metadata.AvailableModel;
    }

    private static void SetUsageLimitedState(CodexRun run, UsageLimitMetadata metadata, DateTimeOffset finishedAt)
    {
        run.Status = QueueStatus.UsageLimited;
        run.FinishedAt = finishedAt;
        run.Error = metadata.Reason;
        run.RetryAfter = metadata.RetryAfter;
        run.RetryReason = metadata.Reason;
        run.AvailableModel = metadata.AvailableModel;
    }

    private async Task PauseQueuedRequestsForModelAsync(
        AppDbContext db,
        Guid machineId,
        string limitingModel,
        Guid excludingRequestId,
        UsageLimitMetadata metadata,
        CancellationToken cancellationToken)
    {
        var normalizedModel = limitingModel.Trim();
        var queuedRequests = await db.Requests
            .Include(x => x.Runs)
            .Where(x => x.Status == QueueStatus.Queued &&
                        x.DeletedAt == null &&
                        x.Id != excludingRequestId &&
                        x.MachineId == machineId)
            .ToArrayAsync(cancellationToken);

        foreach (var queuedRequest in queuedRequests)
        {
            var queuedRun = NextRunForDispatch(queuedRequest);
            if (queuedRun is null || !string.Equals(queuedRun.Model, normalizedModel, StringComparison.Ordinal))
            {
                continue;
            }

            queuedRequest.Status = QueueStatus.UsageLimited;
            queuedRequest.Error = metadata.Reason;
            queuedRequest.RetryAfter = metadata.RetryAfter;
            queuedRequest.RetryReason = metadata.Reason;
            queuedRequest.AvailableModel = metadata.AvailableModel;

            queuedRun.Status = QueueStatus.UsageLimited;
            queuedRun.Error = metadata.Reason;
            queuedRun.RetryAfter = metadata.RetryAfter;
            queuedRun.RetryReason = metadata.Reason;
            queuedRun.AvailableModel = metadata.AvailableModel;
        }
    }

    private async Task PopulateCommitMetadataAsync(AppDbContext db, CodexRequest request, CodexRun run, CancellationToken cancellationToken)
    {
        var project = await db.Projects.Include(x => x.Machine).FirstAsync(x => x.Id == request.ProjectId, cancellationToken);
        var machine = project.Machine ?? throw new InvalidOperationException("Project machine is missing.");
        var result = await runner.RunShellAsync(
            machine,
            project.Path,
            "git rev-parse HEAD && git log -1 --pretty=%B",
            _ => Task.CompletedTask,
            cancellationToken);

        if (!result.Success)
        {
            return;
        }

        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 0)
        {
            run.CommitSha = lines[0].Trim();
            run.CommitMessage = string.Join('\n', lines.Skip(1)).Trim();
        }
    }

    private async Task AppendOutputAsync(Guid runId, string chunk, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.Runs.FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
        if (run is null)
        {
            return;
        }

        run.Output = TrimOutput(run.Output + chunk);
        await TrySaveChangesAsync(db, "append run output", cancellationToken);
    }

    private async Task MarkCancelledAsync(Guid requestId, Guid runId, RunKind kind, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.Requests.Include(x => x.Runs).FirstAsync(x => x.Id == requestId, cancellationToken);
        var run = runId == Guid.Empty
            ? request.Runs.First(x => x.Kind == kind)
            : request.Runs.First(x => x.Id == runId);
        run.Status = QueueStatus.Cancelled;
        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Error = "Cancelled by user.";
        request.Status = QueueStatus.Cancelled;
        request.FinishedAt = run.FinishedAt;
        request.Error = kind == RunKind.Commit ? "Commit run cancelled by user." : "Request cancelled by user.";

        foreach (var queuedRun in request.Runs.Where(x => x.Status is QueueStatus.Queued or QueueStatus.CancelRequested))
        {
            queuedRun.Status = QueueStatus.Cancelled;
            queuedRun.FinishedAt = run.FinishedAt;
            queuedRun.Error = "Cancelled by user.";
        }

        await TrySaveChangesAsync(db, "mark cancelled", cancellationToken);
    }

    private async Task MarkFailedAsync(Guid requestId, Guid runId, RunKind kind, Exception exception, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.Requests.Include(x => x.Runs).FirstAsync(x => x.Id == requestId, cancellationToken);
        var run = runId == Guid.Empty
            ? request.Runs.First(x => x.Kind == kind)
            : request.Runs.First(x => x.Id == runId);
        run.Status = QueueStatus.Failed;
        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Error = exception.Message;
        request.Status = QueueStatus.Failed;
        request.FinishedAt = run.FinishedAt;
        request.Error = kind == RunKind.Commit ? "Commit run failed: " + exception.Message : exception.Message;
        await TrySaveChangesAsync(db, "mark failed", cancellationToken);
    }

    private async Task ResumeExpiredUsageLimitedRequestsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var limitedRequests = await db.Requests
            .Include(x => x.Runs)
            .Where(x => x.DeletedAt == null && x.Status == QueueStatus.UsageLimited && x.RetryAfter != null)
            .ToArrayAsync(cancellationToken);
        var readyRequests = limitedRequests
            .Where(x => x.RetryAfter <= now)
            .ToArray();

        foreach (var request in readyRequests)
        {
            request.Status = QueueStatus.Queued;
            request.Error = null;
            request.RetryAfter = null;
            request.RetryReason = null;
            request.AvailableModel = null;
            request.StartedAt = null;
            request.FinishedAt = null;

            foreach (var run in request.Runs.Where(x => x.Status == QueueStatus.UsageLimited))
            {
                run.Status = QueueStatus.Queued;
                run.Error = null;
                run.RetryAfter = null;
                run.RetryReason = null;
                run.AvailableModel = null;
                run.StartedAt = null;
                run.FinishedAt = null;
                run.ExitCode = null;
            }
        }

        if (readyRequests.Length > 0)
        {
            await TrySaveChangesAsync(db, "resume expired usage limits", cancellationToken);
        }
    }

    private async Task<bool> IsRunModelUsageLimitedAsync(AppDbContext db, Guid machineId, string model, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var normalizedModel = model.Trim();
        var limitedRequests = await db.Requests
            .Include(x => x.Runs)
            .Where(x =>
            x.DeletedAt == null &&
            x.Status == QueueStatus.UsageLimited &&
            x.MachineId == machineId &&
            x.Runs.Any(run => run.Status == QueueStatus.UsageLimited && run.Model == normalizedModel) &&
            x.RetryAfter != null)
            .Select(x => x.RetryAfter)
            .ToArrayAsync(cancellationToken);

        return limitedRequests.Any(x => x > now);
    }

    private async Task<bool> TrySaveChangesAsync(AppDbContext db, string operation, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _lastError = null;
            logger.LogWarning(ex, "Queue save skipped during {Operation} because the row changed before save.", operation);
            foreach (var entry in ex.Entries)
            {
                entry.State = EntityState.Detached;
            }

            return false;
        }
    }

    private sealed record MaterializedAttachments(string PromptSection, IReadOnlyList<string> ImagePaths);

    private async Task<MaterializedAttachments> MaterializeAttachmentsAsync(CodexRequest request, Project project, TargetMachine machine, CancellationToken cancellationToken)
    {
        var attachments = ReadAttachments(request.AttachmentsJson);
        if (attachments.Length == 0)
        {
            return new MaterializedAttachments("", Array.Empty<string>());
        }

        var prompt = new List<string>
        {
            "Prompt attachments:",
            "The following files were attached to this request."
        };
        var imagePaths = new List<string>();
        var localAttachmentRoot = Path.Combine(project.Path, ".codex-queue", "attachments", request.Id.ToString("N"));

        if (machine.Kind == MachineKind.Local)
        {
            Directory.CreateDirectory(localAttachmentRoot);
        }

        foreach (var attachment in attachments)
        {
            var bytes = Convert.FromBase64String(attachment.ContentBase64);
            var relativePath = ".codex-queue/attachments/" + request.Id.ToString("N") + "/" + attachment.Name;
            if (machine.Kind == MachineKind.Local)
            {
                var targetPath = Path.Combine(localAttachmentRoot, attachment.Name);
                await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken);
                prompt.Add("- " + attachment.Name + " (" + attachment.ContentType + ", " + attachment.Size + " bytes) saved at `" + relativePath + "`.");
                if (attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    imagePaths.Add(targetPath);
                }
            }
            else
            {
                prompt.Add("- " + attachment.Name + " (" + attachment.ContentType + ", " + attachment.Size + " bytes).");
            }

            var preview = AttachmentTextPreview(attachment, bytes);
            if (!string.IsNullOrWhiteSpace(preview))
            {
                prompt.Add("  Preview:");
                prompt.Add("  ```");
                prompt.Add(preview);
                prompt.Add("  ```");
            }
        }

        return new MaterializedAttachments(string.Join(Environment.NewLine, prompt), imagePaths);
    }

    private static QueueAttachmentDto[] ReadAttachments(string? attachmentsJson)
    {
        if (string.IsNullOrWhiteSpace(attachmentsJson))
        {
            return Array.Empty<QueueAttachmentDto>();
        }

        try
        {
            return JsonSerializer.Deserialize<QueueAttachmentDto[]>(attachmentsJson) ?? Array.Empty<QueueAttachmentDto>();
        }
        catch (JsonException)
        {
            return Array.Empty<QueueAttachmentDto>();
        }
    }

    private static string AttachmentTextPreview(QueueAttachmentDto attachment, byte[] bytes)
    {
        if (!IsTextLikeAttachment(attachment))
        {
            return "";
        }

        var text = System.Text.Encoding.UTF8.GetString(bytes);
        return text.Length <= 12_000 ? text : text[..12_000] + Environment.NewLine + "...truncated...";
    }

    private static bool IsTextLikeAttachment(QueueAttachmentDto attachment)
    {
        var name = attachment.Name.ToLowerInvariant();
        return attachment.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || attachment.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || attachment.ContentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".csv")
            || name.EndsWith(".json")
            || name.EndsWith(".xml")
            || name.EndsWith(".xaml")
            || name.EndsWith(".html")
            || name.EndsWith(".css")
            || name.EndsWith(".js")
            || name.EndsWith(".ts")
            || name.EndsWith(".tsx")
            || name.EndsWith(".py")
            || name.EndsWith(".cs")
            || name.EndsWith(".c")
            || name.EndsWith(".cpp")
            || name.EndsWith(".h")
            || name.EndsWith(".md")
            || name.EndsWith(".txt");
    }

    private static string BuildCommitShellCommand(TargetMachine machine, string message) =>
        machine.TargetsWindows() ? BuildWindowsCommitShellCommand(message) : BuildUnixCommitShellCommand(message);

    private static string BuildUnixCommitShellCommand(string message)
    {
        var quotedMessage = TargetCommandRunner.Quote(message);
        return "before=$(git status --porcelain); "
            + "if [ -z \"$before\" ]; then printf 'No changes to commit.\\n'; exit 0; fi; "
            + "printf 'Changed files before commit:\\n%s\\n' \"$before\"; "
            + "git add -A; "
            + "diff_exit=0; git diff --cached --quiet || diff_exit=$?; "
            + "if [ \"$diff_exit\" -eq 0 ]; then printf 'No changes staged after git add.\\n'; exit 0; fi; "
            + "if [ \"$diff_exit\" -ne 1 ]; then exit \"$diff_exit\"; fi; "
            + "git commit -m " + quotedMessage + "; "
            + "commit_exit=$?; if [ \"$commit_exit\" -ne 0 ]; then exit \"$commit_exit\"; fi; "
            + "printf '\\nCommit created:\\n'; git rev-parse HEAD";
    }

    private static string BuildWindowsCommitShellCommand(string message)
    {
        var quotedMessage = TargetCommandRunner.QuotePowerShellValue(message);
        return "$before = git status --porcelain; "
            + "if (-not $before) { Write-Output 'No changes to commit.'; exit 0 }; "
            + "Write-Output 'Changed files before commit:'; $before; "
            + "git add -A; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; "
            + "git diff --cached --quiet; $diffExit = $LASTEXITCODE; "
            + "if ($diffExit -eq 0) { Write-Output 'No changes staged after git add.'; exit 0 }; "
            + "if ($diffExit -ne 1) { exit $diffExit }; "
            + "git commit -m " + quotedMessage + "; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; "
            + "Write-Output ''; Write-Output 'Commit created:'; git rev-parse HEAD";
    }

    private static string BuildCommitMessage(string prompt)
    {
        var normalized = Regex.Replace(prompt, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Update project files";
        }

        return normalized.Length <= 72 ? normalized : normalized[..72].TrimEnd();
    }

    private static string BuildRequestPrompt(CodexRequest request)
    {
        if (!request.GenerateCommit || request.SeparateCommitSession)
        {
            return request.Prompt;
        }

        return request.Prompt.TrimEnd()
            + """

            Do not run git commit in this run.
            Leave file changes for the queue-managed commit step.
            """;
    }

    private static string BuildCommitPrompt() =>
        """
        You are running after a separate Codex implementation session.

        Create a git commit for the current repository only if there are actual changes.
        Requirements:
        1. Inspect git status and the diff.
        2. Write one concise commit message.
        3. Run git add -A.
        4. Run git commit -m "<message>".
        5. Return the commit SHA, commit message, and changed files. Include a line exactly named "Commit created:" immediately before the SHA.

        If there are no changes, do not create an empty commit. Say that there were no changes.
        """;

    private static string BuildProjectScopedPrompt(string projectPath, string userPrompt) =>
        $"""
        Project root: {projectPath}

        Run all commands from this project root.
        Treat this project root as the workspace boundary.
        Do not create, edit, delete, move, or commit files outside this project root.
        If the requested task appears to require changes outside this project root, stop and explain what is needed instead of modifying outside the project.

        User request:
        {userPrompt}
        """;

    private static string TrimOutput(string value) =>
        value.Length <= MaxStoredOutput ? value : value[^MaxStoredOutput..];

    private static string? LastUsefulLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
}
