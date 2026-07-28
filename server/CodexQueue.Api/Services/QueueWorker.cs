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
    Task<bool> RemoveProjectAsync(Guid projectId, CancellationToken cancellationToken);
    Task<bool> ResumeRequestAsync(Guid requestId, CancellationToken cancellationToken);
    Task<bool> KickQueueAsync(CancellationToken cancellationToken);
    Task<QueueModeChangeResult> ChangeQueueModeAsync(Guid projectId, bool separateQueuesByTab, CancellationToken cancellationToken);
    QueueWorkerDiagnostics GetDiagnostics();
}

public enum QueueModeChangeResult
{
    Updated,
    ActiveRequests,
    NotFound
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
    IQueueAgentRunnerResolver agentRunnerResolver,
    IProviderConcurrencyGate providerConcurrency,
    ILogger<QueueWorker> logger) : BackgroundService, IQueueCoordinator
{
    private const int MaxStoredOutput = 512_000;
    private static readonly TimeSpan StaleRunningRecoveryDelay = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DefaultUsageLimitBackoff = TimeSpan.FromMinutes(1);
    private const string UsageLimitUnknownReason = "Usage limit reached.";
    private static readonly Regex RetryAfterHeaderRegex = new(@"(?i)retry[-_\s]*after\s*[:=]\s*([^\s,;]+)", RegexOptions.Compiled);
    private static readonly Regex RetryAfterDurationRegex = new(@"(?i)(\d+)\s*(s|sec|secs?|seconds?|m|min|mins?|minutes?|h|hrs?|hours?)\b", RegexOptions.Compiled);
    private static readonly Regex RetryAfterDateRegex = new(@"(?i)(?:retry[-_\s]*after\s*[:=]\s*)([^;\n\r]+)", RegexOptions.Compiled);
    private static readonly Regex UsageLimitKeywordRegex = new(@"(?i)(rate\s*-?\s*limit|quota|throttle|too\s+many\s+requests|429|rate\s*limit|usage\s+limit)", RegexOptions.Compiled);
    private static readonly Regex ModelRegex = new(@"(?i)(gpt-[a-z0-9._-]+)", RegexOptions.Compiled);
    private static readonly Regex CommitCreatedOutputRegex = new(@"(?im)(Commit created:|Created commit\b|commit created\b|\[[^\]\r\n]+\s+[0-9a-f]{7,40}\])", RegexOptions.Compiled);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeRequests = new();
    private readonly ConcurrentDictionary<QueueLaneKey, QueueExecution> _activeQueues = new();
    private readonly SemaphoreSlim _dispatchLock = new(1, 1);
    private DateTimeOffset? _lastHeartbeat;
    private DateTimeOffset? _lastDispatch;
    private DateTimeOffset? _lastIdle;
    private string? _lastError;
    private CancellationToken _workerStoppingToken;

    public async Task<bool> CancelRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        // Serialize cancellation with dispatch so a queued request cannot be marked
        // cancelled and then immediately claimed by an in-flight dispatch pass.
        await _dispatchLock.WaitAsync(cancellationToken);
        try
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
            request.QueueWaitReason = null;
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
        finally
        {
            _dispatchLock.Release();
        }
    }

    public async Task<bool> RemoveProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        // Keep dispatch paused from cancellation through deletion. Otherwise a queued
        // request for this project could be dispatched after its active request stops.
        await _dispatchLock.WaitAsync(cancellationToken);
        try
        {
            var projectExecutions = _activeQueues
                .Where(x => x.Key.ProjectId == projectId)
                .Select(x => x.Value)
                .ToArray();
            foreach (var execution in projectExecutions)
            {
                await execution.Cancellation.CancelAsync();
            }
            foreach (var execution in projectExecutions)
            {
                await execution.Completion.Task.WaitAsync(cancellationToken);
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
            if (project is null)
            {
                return false;
            }

            // Explicitly remove dependents instead of relying on database-level cascade
            // constraints. Existing SQLite databases are initialized with EnsureCreated,
            // so their foreign key actions may predate the current model configuration.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Runs
                .Where(x => db.Requests.Any(request => request.ProjectId == projectId && request.Id == x.RequestId))
                .ExecuteDeleteAsync(cancellationToken);
            await db.Requests
                .Where(x => x.ProjectId == projectId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.QueueTabs
                .Where(x => x.ProjectId == projectId)
                .ExecuteDeleteAsync(cancellationToken);
            db.Projects.Remove(project);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _dispatchLock.Release();
        }
    }

    public async Task<bool> ResumeRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.Requests
            .Include(x => x.Project)
            .Include(x => x.QueueTab)
            .Include(x => x.ProviderProfile)
            .Include(x => x.Runs)
            .FirstOrDefaultAsync(x => x.Id == requestId, cancellationToken);
        if (request is null || request.QueueTab?.DeletedAt is not null)
        {
            return false;
        }

        if (_activeRequests.ContainsKey(requestId))
        {
            return true;
        }

        ResumeRequest(db, request);
        if (request.Status == QueueStatus.Queued)
        {
            var projectPriorityRequests = await db.Requests
                .Where(x => x.ProjectId == request.ProjectId
                    && (!request.Project!.SeparateQueuesByTab || x.QueueTabId == request.QueueTabId)
                    && (x.Id == request.Id
                        || (x.DeletedAt == null
                            && x.ArchivedAt == null
                            && (x.Status == QueueStatus.Queued
                                || x.Status == QueueStatus.Running
                                || x.Status == QueueStatus.CancelRequested))))
                .ToArrayAsync(cancellationToken);
            QueuePriority.MoveQueuedRequestAfterActive(projectPriorityRequests, request);
        }

        if (await TrySaveChangesAsync(db, "resume request", cancellationToken))
        {
            await KickQueueAsync(cancellationToken);
        }

        return true;
    }

    public async Task<bool> KickQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A kick is a durable scheduling request. Waiting for the current
            // dispatch pass prevents a stage handoff from being silently dropped
            // when the main request and the background worker finish at the same
            // time.
            await DispatchAvailableProjectsLockedAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            logger.LogError(ex, "Queue kick failed.");
            return false;
        }
    }

    public async Task<QueueModeChangeResult> ChangeQueueModeAsync(
        Guid projectId,
        bool separateQueuesByTab,
        CancellationToken cancellationToken)
    {
        await _dispatchLock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
            if (project is null)
            {
                return QueueModeChangeResult.NotFound;
            }

            var hasActiveRequests = _activeQueues.Keys.Any(x => x.ProjectId == projectId)
                || await db.Requests.AnyAsync(x =>
                    x.ProjectId == projectId
                    && (x.Status == QueueStatus.Running || x.Status == QueueStatus.CancelRequested),
                    cancellationToken);
            if (hasActiveRequests)
            {
                return QueueModeChangeResult.ActiveRequests;
            }

            project.SeparateQueuesByTab = separateQueuesByTab;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return QueueModeChangeResult.Updated;
        }
        finally
        {
            _dispatchLock.Release();
        }
    }

    public QueueWorkerDiagnostics GetDiagnostics() =>
        new(
            _lastHeartbeat,
            _lastDispatch,
            _lastIdle,
            _lastError,
            _activeRequests.Keys.ToArray(),
            !_activeQueues.IsEmpty || _dispatchLock.CurrentCount == 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _workerStoppingToken = stoppingToken;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var dispatched = await DispatchAvailableProjectsLockedAsync(stoppingToken);
                    if (!dispatched)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    logger.LogError(ex, "Queue loop failed.");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        finally
        {
            await WaitForActiveQueuesAsync();
        }
    }

    private async Task<bool> DispatchAvailableProjectsLockedAsync(CancellationToken stoppingToken)
    {
        await _dispatchLock.WaitAsync(stoppingToken);
        try
        {
            return await DispatchAvailableProjectsAsync(stoppingToken);
        }
        finally
        {
            _dispatchLock.Release();
        }
    }

    private async Task<bool> DispatchAvailableProjectsAsync(CancellationToken stoppingToken)
    {
        _lastHeartbeat = DateTimeOffset.UtcNow;
        var dispatches = new List<PendingDispatch>();
        try
        {
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await ResumeExpiredUsageLimitedRequestsAsync(db, stoppingToken);
                await ReconcileQueueStateAsync(db, stoppingToken);

                var now = DateTimeOffset.UtcNow;
                var queuedRequests = await db.Requests
                    .Include(x => x.Project).ThenInclude(x => x!.Machine)
                    .Include(x => x.QueueTab)
                    .Include(x => x.Machine)
                    .Include(x => x.ProviderProfile)
                    .Include(x => x.Runs)
                    .Where(x =>
                        x.DeletedAt == null
                        && (x.QueueTabId == null || x.QueueTab!.DeletedAt == null)
                        && (x.Status == QueueStatus.Queued || x.Status == QueueStatus.Running))
                    .ToArrayAsync(stoppingToken);
                var enabledProviderProfiles = await db.AiProviderProfiles
                    .AsNoTracking()
                    .Where(x => x.Enabled)
                    .ToArrayAsync(stoppingToken);
                var providerLimits = BuildProviderConcurrencyLimits(enabledProviderProfiles);
                var lanesWithUnclaimedRunningRequests = queuedRequests
                    .Where(x => HasUnclaimedRunningStage(x)
                        && !_activeQueues.ContainsKey(QueueLaneFor(x)))
                    .Select(QueueLaneFor)
                    .ToHashSet();
                var providerBlockedQueueLanes = new HashSet<QueueLaneKey>();

                var queueMetadataChanged = false;
                foreach (var candidate in queuedRequests.OrderBy(x => x.QueueOrder).ThenBy(x => x.CreatedAt))
                {
                    var queueLane = QueueLaneFor(candidate);
                    if (_activeQueues.ContainsKey(queueLane)
                        || lanesWithUnclaimedRunningRequests.Contains(queueLane)
                        || providerBlockedQueueLanes.Contains(queueLane))
                    {
                        continue;
                    }

                    var candidateRun = NextRunForDispatch(candidate);
                    if (candidateRun is null)
                    {
                        continue;
                    }

                    var modelLimited = candidateRun.ExecutionRunner == ExecutionRunner.CodexCli
                        && await IsRunModelUsageLimitedAsync(db, candidate.MachineId, candidateRun.Model, now, stoppingToken);
                    if (modelLimited)
                    {
                        continue;
                    }

                    IDisposable? providerLease = null;
                    var executionProviderProfile = candidateRun.ProviderProfileId.HasValue
                        ? enabledProviderProfiles.FirstOrDefault(profile => profile.Id == candidateRun.ProviderProfileId.Value)
                        : candidate.ProviderProfile;
                    if (candidateRun.ExecutionRunner == ExecutionRunner.OpenHandsCli
                        && executionProviderProfile is { } profile)
                    {
                        var providerKey = ProviderConcurrencyKey(profile);
                        var maximumConcurrency = providerLimits.GetValueOrDefault(
                            providerKey,
                            Math.Max(1, profile.MaximumConcurrency));
                        if (!providerConcurrency.TryAcquire(
                                providerKey,
                                maximumConcurrency,
                                out providerLease))
                        {
                            var waitReason = "Waiting for shared "
                                + (profile.Source == AiProviderSource.Local ? "Local AI" : profile.Name)
                                + " capacity ("
                                + maximumConcurrency
                                + " concurrent run"
                                + (maximumConcurrency == 1 ? "" : "s")
                                + ").";
                            if (!string.Equals(candidate.QueueWaitReason, waitReason, StringComparison.Ordinal))
                            {
                                candidate.QueueWaitReason = waitReason;
                                queueMetadataChanged = true;
                            }
                            providerBlockedQueueLanes.Add(queueLane);
                            continue;
                        }
                    }

                    var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_workerStoppingToken);
                    var execution = new QueueExecution(requestCancellation, providerLease);
                    if (!_activeQueues.TryAdd(queueLane, execution))
                    {
                        providerLease?.Dispose();
                        requestCancellation.Dispose();
                        continue;
                    }

                    if (!_activeRequests.TryAdd(candidate.Id, requestCancellation))
                    {
                        _activeQueues.TryRemove(queueLane, out _);
                        providerLease?.Dispose();
                        requestCancellation.Dispose();
                        continue;
                    }

                    if (candidate.QueueWaitReason is not null)
                    {
                        candidate.QueueWaitReason = null;
                        queueMetadataChanged = true;
                    }
                    candidate.Status = QueueStatus.Running;
                    candidate.StartedAt ??= DateTimeOffset.UtcNow;
                    candidateRun.Status = QueueStatus.Running;
                    candidateRun.StartedAt ??= DateTimeOffset.UtcNow;
                    var runnerName = candidateRun.ExecutionRunner == ExecutionRunner.OpenHandsCli ? "Local Codex" : "Codex";
                    candidateRun.Output = TrimOutput(candidateRun.Output + "Dispatching " + runnerName + " " + candidateRun.Kind.ToString().ToLowerInvariant() + " run on " + (candidate.Machine?.Name ?? candidate.Project?.Machine?.Name ?? "target machine") + "..." + Environment.NewLine);
                    dispatches.Add(new PendingDispatch(queueLane, candidate.Id, candidateRun.Kind, execution));
                }

                if (dispatches.Count == 0)
                {
                    if (queueMetadataChanged)
                    {
                        await TrySaveChangesAsync(db, "update provider queue wait state", stoppingToken);
                    }
                    _lastIdle = DateTimeOffset.UtcNow;
                    return false;
                }

                _lastDispatch = DateTimeOffset.UtcNow;
                _lastError = null;
                if (!await TrySaveChangesAsync(db, "mark requests running", stoppingToken))
                {
                    foreach (var dispatch in dispatches)
                    {
                        ReleaseQueueExecution(dispatch);
                    }

                    return false;
                }
            }
        }
        catch
        {
            foreach (var dispatch in dispatches)
            {
                ReleaseQueueExecution(dispatch);
            }

            throw;
        }

        foreach (var dispatch in dispatches)
        {
            StartQueueExecution(dispatch);
        }

        return true;
    }

    private void StartQueueExecution(PendingDispatch dispatch)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (dispatch.RunKind == RunKind.Commit)
                {
                    await RunCommitAsync(dispatch.RequestId, dispatch.Execution.Cancellation.Token);
                }
                else
                {
                    await RunRequestAsync(
                        dispatch.RequestId,
                        dispatch.Execution,
                        dispatch.Execution.Cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (dispatch.Execution.Cancellation.IsCancellationRequested)
            {
                // Cancellation state is recorded by the request operation or recovered on restart.
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                logger.LogError(ex, "Queue execution failed for project {ProjectId}, tab {QueueTabId}, and request {RequestId}.", dispatch.QueueLane.ProjectId, dispatch.QueueLane.QueueTabId, dispatch.RequestId);
                await RecordUnhandledExecutionFailureAsync(dispatch.RequestId, dispatch.RunKind, ex);
            }
            finally
            {
                ReleaseQueueExecution(dispatch);
                if (!_workerStoppingToken.IsCancellationRequested)
                {
                    await KickQueueAsync(CancellationToken.None);
                }
            }
        }, CancellationToken.None);
    }

    private void ReleaseQueueExecution(PendingDispatch dispatch)
    {
        _activeRequests.TryRemove(dispatch.RequestId, out _);
        _activeQueues.TryRemove(dispatch.QueueLane, out _);
        dispatch.Execution.ReleaseProviderLease();
        dispatch.Execution.Cancellation.Dispose();
        dispatch.Execution.Completion.TrySetResult();
    }

    private async Task WaitForActiveQueuesAsync()
    {
        await _dispatchLock.WaitAsync(CancellationToken.None);
        Task[] completions;
        try
        {
            completions = _activeQueues.Values.Select(x => x.Completion.Task).ToArray();
        }
        finally
        {
            _dispatchLock.Release();
        }

        await Task.WhenAll(completions);
    }

    private async Task RunRequestAsync(
        Guid requestId,
        QueueExecution execution,
        CancellationToken cancellationToken)
    {
        var requestRunSucceeded = await IsRunSucceededAsync(requestId, RunKind.Request, cancellationToken)
            || await ExecuteRunAsync(requestId, RunKind.Request, cancellationToken);
        if (!requestRunSucceeded)
        {
            return;
        }

        if (!await PrepareCommitRunAfterRequestAsync(requestId, cancellationToken))
        {
            return;
        }

        // A separate commit session is the second stage of the same ordered queue
        // item. Keep ownership of the lane so another request cannot overtake it,
        // but exchange the main stage's provider lease for the independently
        // selected commit runner before starting the second Codex session.
        if (await TryClaimPreparedCommitRunAsync(requestId, execution, cancellationToken))
        {
            await RunCommitAsync(requestId, cancellationToken);
        }
    }

    private async Task<bool> TryClaimPreparedCommitRunAsync(
        Guid requestId,
        QueueExecution execution,
        CancellationToken cancellationToken)
    {
        execution.ReleaseProviderLease();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.Requests
            .Include(x => x.Project)
            .Include(x => x.ProviderProfile)
            .Include(x => x.Runs)
            .FirstAsync(x => x.Id == requestId, cancellationToken);
        var commitRun = GetLatestRunOfKind(request.Runs, RunKind.Commit);
        if (request.Status == QueueStatus.CancelRequested
            || commitRun?.Status != QueueStatus.Queued)
        {
            return false;
        }

        if (commitRun.ExecutionRunner == ExecutionRunner.CodexCli
            && await IsRunModelUsageLimitedAsync(
                db,
                request.MachineId,
                commitRun.Model,
                DateTimeOffset.UtcNow,
                cancellationToken))
        {
            return false;
        }

        IDisposable? commitProviderLease = null;
        if (commitRun.ExecutionRunner == ExecutionRunner.OpenHandsCli
            && commitRun.ProviderProfileId.HasValue)
        {
            var enabledProviderProfiles = await db.AiProviderProfiles
                .AsNoTracking()
                .Where(profile => profile.Enabled)
                .ToArrayAsync(cancellationToken);
            var profile = enabledProviderProfiles.FirstOrDefault(
                candidate => candidate.Id == commitRun.ProviderProfileId.Value);
            if (profile is not null)
            {
                var providerKey = ProviderConcurrencyKey(profile);
                var providerLimits = BuildProviderConcurrencyLimits(enabledProviderProfiles);
                var maximumConcurrency = providerLimits.GetValueOrDefault(
                    providerKey,
                    Math.Max(1, profile.MaximumConcurrency));
                if (!providerConcurrency.TryAcquire(
                        providerKey,
                        maximumConcurrency,
                        out commitProviderLease))
                {
                    request.QueueWaitReason = "Waiting for shared "
                        + (profile.Source == AiProviderSource.Local ? "Local AI" : profile.Name)
                        + " capacity ("
                        + maximumConcurrency
                        + " concurrent run"
                        + (maximumConcurrency == 1 ? "" : "s")
                        + ").";
                    await TrySaveChangesAsync(
                        db,
                        "wait for separate commit provider capacity",
                        cancellationToken);
                    return false;
                }
            }
        }

        execution.ReplaceProviderLease(commitProviderLease);
        request.QueueWaitReason = null;
        request.Status = QueueStatus.Running;
        request.Error = null;
        request.FinishedAt = null;
        commitRun.Status = QueueStatus.Running;
        commitRun.StartedAt ??= DateTimeOffset.UtcNow;
        commitRun.Output = TrimOutput(
            commitRun.Output
            + "Starting separate "
            + (commitRun.ExecutionRunner == ExecutionRunner.OpenHandsCli ? "Local Codex" : "Codex")
            + " commit run..."
            + Environment.NewLine);
        return await TrySaveChangesAsync(
            db,
            "claim separate commit run",
            cancellationToken);
    }

    private async Task<bool> PrepareCommitRunAfterRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.Requests.Include(x => x.Runs).FirstAsync(x => x.Id == requestId, cancellationToken);
        var requestRun = GetLatestRunOfKind(request.Runs, RunKind.Request);

        if (!request.GenerateCommit || !request.SeparateCommitSession)
        {
            CancelUnusedCommitRun(GetLatestRunOfKind(request.Runs, RunKind.Commit));
            request.Status = QueueStatus.Succeeded;
            request.FinishedAt = DateTimeOffset.UtcNow;
            UpdateRequestSummary(request);
            await TrySaveChangesAsync(db, "complete request without separate commit", cancellationToken);
            return false;
        }

        var commitRun = GetLatestRunOfKind(request.Runs, RunKind.Commit);
        if (commitRun?.Status == QueueStatus.Succeeded)
        {
            request.Status = QueueStatus.Succeeded;
            request.FinishedAt = commitRun.FinishedAt ?? DateTimeOffset.UtcNow;
            request.Error = null;
            UpdateRequestSummary(request);
            await TrySaveChangesAsync(db, "complete already committed request", cancellationToken);
            return false;
        }

        if (commitRun is null)
        {
            commitRun = CreateCommitRun(request);
            commitRun.Request = request;
            db.Runs.Add(commitRun);
        }
        else
        {
            ApplyCommitModel(request, commitRun);
            if (commitRun.Status is QueueStatus.Failed or QueueStatus.Cancelled or QueueStatus.UsageLimited)
            {
                ResetRunForResume(commitRun);
                ApplyCommitModel(request, commitRun);
            }
        }

        if (await IsRunModelUsageLimitedAsync(db, request.MachineId, commitRun.Model, DateTimeOffset.UtcNow, cancellationToken))
        {
            MarkRequestQueued(request);
            await TrySaveChangesAsync(db, "queue commit run behind usage limit", cancellationToken);
            return false;
        }

        MarkRequestQueued(request);
        return await TrySaveChangesAsync(db, "queue separate commit run", cancellationToken);
    }

    private async Task RunCommitAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await ExecuteRunAsync(requestId, RunKind.Commit, cancellationToken);
    }

    private async Task<bool> ExecuteRunAsync(Guid requestId, RunKind kind, CancellationToken cancellationToken)
    {
        CodexRequest request;
        CodexRun run;
        AiProviderProfile? executionProviderProfile = null;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            request = await db.Requests
                .Include(x => x.Project).ThenInclude(x => x!.Machine)
                .Include(x => x.QueueTab)
                .Include(x => x.Machine)
                .Include(x => x.ProviderProfile)
                .Include(x => x.Runs)
                .FirstAsync(x => x.Id == requestId, cancellationToken);
            var existingRun = GetLatestRunOfKind(request.Runs, kind);
            run = FindOrRestoreRunForExecution(request, kind)
                ?? throw new InvalidOperationException(
                    kind == RunKind.Commit
                        ? "Separate commit run was not available after the request stage completed."
                        : "Request run was not found.");
            if (existingRun is null)
            {
                db.Entry(run).State = EntityState.Added;
            }
            if (run.ProviderProfileId.HasValue)
            {
                executionProviderProfile = run.ProviderProfileId == request.ProviderProfileId
                    ? request.ProviderProfile
                    : await db.AiProviderProfiles.FirstOrDefaultAsync(
                        x => x.Id == run.ProviderProfileId.Value,
                        cancellationToken);
            }
            request.Status = QueueStatus.Running;
            request.StartedAt ??= DateTimeOffset.UtcNow;
            request.FinishedAt = null;
            request.Error = null;
            run.Status = QueueStatus.Running;
            run.StartedAt ??= DateTimeOffset.UtcNow;
            if (!await TrySaveChangesAsync(db, "mark run running", cancellationToken))
            {
                return false;
            }
        }

        var project = request.Project ?? throw new InvalidOperationException("Request project is missing.");
        var executionRunner = kind == RunKind.Commit ? run.ExecutionRunner : request.ExecutionRunner;
        var machine = executionRunner == ExecutionRunner.CodexCli
            ? request.Project?.Machine ?? request.Machine ?? throw new InvalidOperationException("Request machine is missing.")
            : request.Machine ?? throw new InvalidOperationException("Local Codex request machine snapshot is missing.");
        var projectPath = executionRunner == ExecutionRunner.CodexCli
            ? project.Path
            : request.ExecutionRunner == ExecutionRunner.OpenHandsCli
                ? ValidateLocalCodexExecutionTarget(request, project, machine)
                : project.Path;
        var attachmentsCleaned = false;
        var attachmentMaterializationAttempted = false;

        try
        {
            if (kind == RunKind.Commit)
            {
                await AppendOutputAsync(run.Id, "Preparing commit run..." + Environment.NewLine, CancellationToken.None);
                var commitResult = await RunCodexCommitSessionAsync(
                    request,
                    run,
                    machine,
                    projectPath,
                    executionProviderProfile,
                    cancellationToken);
                if (TryParseUsageLimit(commitResult, out var commitUsageLimit))
                {
                    await MarkUsageLimitedAsync(request, run, kind, commitResult, commitUsageLimit, cancellationToken);
                    return false;
                }

                await CompleteRunAsync(
                    requestId,
                    run.Id,
                    kind,
                    new QueueAgentRunResult(
                        commitResult.ExitCode,
                        commitResult.Output,
                        commitResult.CommandPreview,
                        CodexSessionId: run.ExecutionRunner == ExecutionRunner.CodexCli
                            ? commitResult.CodexSessionId
                            : null,
                        LocalCodexSessionId: run.ExecutionRunner == ExecutionRunner.OpenHandsCli
                            ? commitResult.CodexSessionId
                            : null),
                    cancellationToken);
                return commitResult.Success;
            }

            var runnerName = request.ExecutionRunner == ExecutionRunner.OpenHandsCli ? "Local Codex" : "Codex";
            await AppendOutputAsync(run.Id, "Preparing " + runnerName + " request..." + Environment.NewLine, CancellationToken.None);
            var prompt = BuildProjectScopedPrompt(projectPath, BuildRequestPrompt(request));
            if (request.ExecutionRunner == ExecutionRunner.OpenHandsCli
                && !string.IsNullOrWhiteSpace(request.AttachmentsJson))
            {
                throw new InvalidOperationException(
                    "Attachments are not available for Local Codex in this release. "
                    + "Remove the attachments or create a new Local Codex request.");
            }

            attachmentMaterializationAttempted = !string.IsNullOrWhiteSpace(request.AttachmentsJson);
            var attachments = await MaterializeAttachmentsAsync(request, run.Id, project, machine, cancellationToken);
            if (!string.IsNullOrWhiteSpace(attachments.PromptSection))
            {
                prompt += Environment.NewLine + Environment.NewLine + attachments.PromptSection;
            }

            var beforeCommitHead = request.GenerateCommit && !request.SeparateCommitSession
                ? await ReadGitHeadAsync(machine, projectPath, cancellationToken)
                : null;

            var selectedRunner = agentRunnerResolver.Resolve(request.ExecutionRunner);
            var result = await selectedRunner.RunAsync(
                new QueueAgentRunContext(
                    request,
                    run,
                    machine,
                    projectPath,
                    prompt,
                    attachments.ImagePaths,
                    ProviderProfile: executionProviderProfile),
                chunk => AppendOutputAsync(run.Id, chunk, CancellationToken.None),
                cancellationToken);

            // Remove temporary inputs before evaluating Git state so they cannot be
            // mistaken for user changes or accidentally included in a commit.
            if (attachmentMaterializationAttempted)
            {
                await RemoveMaterializedAttachmentsAsync(request, project, machine);
            }
            attachmentsCleaned = true;

            var commandResult = new CommandResult(
                result.ExitCode,
                result.Output,
                result.CommandPreview,
                result.CodexSessionId ?? result.LocalCodexSessionId);
            if (request.ExecutionRunner == ExecutionRunner.CodexCli
                && TryParseUsageLimit(commandResult, out var usageLimit))
            {
                await MarkUsageLimitedAsync(request, run, kind, commandResult, usageLimit, cancellationToken);
                return false;
            }

            if (commandResult.Success
                && request.GenerateCommit
                && !request.SeparateCommitSession)
            {
                commandResult = await ValidateCodexCommitAsync(
                    run,
                    machine,
                    projectPath,
                    commandResult,
                    beforeCommitHead,
                    // Inline commit is part of this request's contract. A clean
                    // working tree alone is not enough: Git HEAD must advance so
                    // the request has a durable, traceable result.
                    commitRequired: true,
                    cancellationToken);
                result = result with
                {
                    ExitCode = commandResult.ExitCode,
                    Output = commandResult.Output,
                    CommandPreview = commandResult.CommandPreview,
                };
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
        finally
        {
            if (kind == RunKind.Request
                && attachmentMaterializationAttempted
                && !attachmentsCleaned)
            {
                await RemoveMaterializedAttachmentsAsync(request, project, machine);
            }
        }
    }

    private async Task<CommandResult> RunCodexCommitSessionAsync(
        CodexRequest request,
        CodexRun run,
        TargetMachine machine,
        string projectPath,
        AiProviderProfile? executionProviderProfile,
        CancellationToken cancellationToken)
    {
        var beforeCommitHead = await ReadGitHeadAsync(machine, projectPath, cancellationToken);
        var beforeStatus = await ReadGitStatusPorcelainAsync(machine, projectPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(beforeStatus))
        {
            var output = "No changes to commit." + Environment.NewLine;
            await AppendOutputAsync(run.Id, output, CancellationToken.None);
            return new CommandResult(0, output, "git status --porcelain -- .");
        }

        await AppendOutputAsync(run.Id, "Starting Codex commit session..." + Environment.NewLine, CancellationToken.None);
        var prompt = BuildProjectScopedPrompt(projectPath, BuildSeparateCommitPrompt());
        var selectedRunner = agentRunnerResolver.Resolve(run.ExecutionRunner);
        var agentResult = await selectedRunner.RunAsync(
            new QueueAgentRunContext(
                request,
                run,
                machine,
                projectPath,
                prompt,
                ImagePaths: null,
                StartNewSession: true,
                ProviderProfile: executionProviderProfile),
            chunk => AppendOutputAsync(run.Id, chunk, CancellationToken.None),
            cancellationToken);
        var result = new CommandResult(
            agentResult.ExitCode,
            agentResult.Output,
            agentResult.CommandPreview,
            agentResult.CodexSessionId ?? agentResult.LocalCodexSessionId);

        return await ValidateCodexCommitAsync(
            run,
            machine,
            projectPath,
            result,
            beforeCommitHead,
            commitRequired: !string.IsNullOrWhiteSpace(beforeStatus),
            cancellationToken);
    }

    private static string BuildSeparateCommitPrompt() =>
        """
        Review Changes and Create Commit

        Inspect the git changes and create exactly one git commit yourself.
        Stage only changes under this project root. Prefer pathspec-limited commands such as `git add -A -- .`.
        Choose one concise imperative commit message.
        Do not amend existing commits.
        Do not push.
        """;

    private async Task<CommandResult> ValidateCodexCommitAsync(
        CodexRun run,
        TargetMachine machine,
        string projectPath,
        CommandResult result,
        string? beforeHead,
        bool commitRequired,
        CancellationToken cancellationToken)
    {
        if (!result.Success)
        {
            return result;
        }

        var afterHead = await ReadGitHeadAsync(machine, projectPath, cancellationToken);
        if (!string.IsNullOrWhiteSpace(afterHead) && !string.Equals(beforeHead, afterHead, StringComparison.OrdinalIgnoreCase))
        {
            if (CommitOutputClaimsCreated(result.Output))
            {
                return result;
            }

            var marker = Environment.NewLine + "Commit created:" + Environment.NewLine + afterHead + Environment.NewLine;
            await AppendOutputAsync(run.Id, marker, CancellationToken.None);
            return result with { Output = result.Output.TrimEnd() + marker };
        }

        var currentStatus = await ReadGitStatusPorcelainAsync(machine, projectPath, cancellationToken);
        if (!commitRequired && string.IsNullOrWhiteSpace(currentStatus))
        {
            return result;
        }

        var message = commitRequired
            ? string.IsNullOrWhiteSpace(currentStatus)
                ? "Codex exited successfully, but inline commit was enabled and Git HEAD did not change. No commit was created."
                : "Codex exited successfully, but inline commit was enabled and Git HEAD did not change; project changes remain uncommitted."
            : string.IsNullOrWhiteSpace(currentStatus)
                ? "Codex finished without creating a git commit."
                : "Codex finished without creating a git commit; project changes remain.";
        var output = Environment.NewLine + message + Environment.NewLine;
        await AppendOutputAsync(run.Id, output, CancellationToken.None);
        return result with { ExitCode = 12, Output = result.Output.TrimEnd() + output };
    }

    private async Task<string?> ReadGitHeadAsync(TargetMachine machine, string projectPath, CancellationToken cancellationToken)
    {
        var result = await runner.RunShellAsync(
            machine,
            projectPath,
            "git rev-parse HEAD",
            _ => Task.CompletedTask,
            cancellationToken);

        if (!result.Success)
        {
            return null;
        }

        return StripShellCommandPreview(result.Output)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.Length == 40 && line.All(IsHex));
    }

    private async Task<string> ReadGitStatusPorcelainAsync(TargetMachine machine, string projectPath, CancellationToken cancellationToken)
    {
        var result = await runner.RunShellAsync(
            machine,
            projectPath,
            "git status --porcelain -- .",
            _ => Task.CompletedTask,
            cancellationToken);

        return result.Success ? StripShellCommandPreview(result.Output).Trim() : "";
    }

    private async Task<bool> IsRunSucceededAsync(Guid requestId, RunKind kind, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var runs = await db.Runs
            .Where(x => x.RequestId == requestId && x.Kind == kind)
            .ToArrayAsync(cancellationToken);
        var latest = runs
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();
        return latest?.Status == QueueStatus.Succeeded;
    }

    private async Task ReconcileQueueStateAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var requests = await db.Requests
            .Include(x => x.Runs)
            .Where(x => x.DeletedAt == null
                && (x.Status == QueueStatus.Queued
                || x.Status == QueueStatus.Running
                || x.Status == QueueStatus.CancelRequested
                || x.Status == QueueStatus.Failed
                || x.Status == QueueStatus.UsageLimited
                || (x.Status == QueueStatus.Succeeded
                    && x.GenerateCommit
                    && x.SeparateCommitSession
                    && x.Runs.Any(run => run.Kind == RunKind.Commit && run.Status != QueueStatus.Succeeded))))
            .ToArrayAsync(cancellationToken);

        var activeRequestIds = _activeRequests.Keys.ToHashSet();
        var changed = FailClosedInterruptedLocalCodexRequests(requests, activeRequestIds);
        foreach (var request in requests)
        {
            changed |= ReconcileRequestState(
                db,
                request,
                activeRequestIds.Contains(request.Id));
        }

        if (changed)
        {
            await TrySaveChangesAsync(db, "reconcile queue state", cancellationToken);
        }
    }

    public static bool FailClosedInterruptedLocalCodexRequests(
        IReadOnlyCollection<CodexRequest> requests,
        IReadOnlySet<Guid> activeRequestIds)
    {
        var interruptedRequests = requests
            .Where(request =>
                request.ExecutionRunner == ExecutionRunner.OpenHandsCli
                && request.Status is QueueStatus.Running or QueueStatus.CancelRequested
                && !activeRequestIds.Contains(request.Id)
                && GetLatestRunOfKind(request.Runs, RunKind.Request)?.Status
                    is QueueStatus.Running or QueueStatus.CancelRequested)
            .ToArray();
        if (interruptedRequests.Length == 0)
        {
            return false;
        }

        var failedAt = DateTimeOffset.UtcNow;
        const string interruptedReason =
            "Local Codex run lost its server-side process owner. "
            + "Verify the selected machine has no orphaned Codex process before retrying.";
        foreach (var request in interruptedRequests)
        {
            request.Status = QueueStatus.Failed;
            request.QueueWaitReason = null;
            request.FinishedAt = failedAt;
            request.Error = interruptedReason;
            foreach (var run in request.Runs.Where(run =>
                         run.Status is QueueStatus.Running
                             or QueueStatus.CancelRequested
                             or QueueStatus.UsageLimited))
            {
                run.Status = QueueStatus.Failed;
                run.FinishedAt = failedAt;
                run.Error = interruptedReason;
            }
        }

        const string queuedReason =
            "Local Codex request was paused because another Local Codex run may still be active without "
            + "a server-side process owner. Verify the selected machines have no orphaned Codex "
            + "process, then resume this request.";
        foreach (var request in requests.Where(request =>
                     request.ExecutionRunner == ExecutionRunner.OpenHandsCli
                     && request.Status == QueueStatus.Queued))
        {
            request.Status = QueueStatus.Failed;
            request.QueueWaitReason = null;
            request.FinishedAt = failedAt;
            request.Error = queuedReason;
            foreach (var run in request.Runs.Where(run =>
                         run.Status is QueueStatus.Queued
                             or QueueStatus.Running
                             or QueueStatus.CancelRequested
                             or QueueStatus.UsageLimited))
            {
                run.Status = QueueStatus.Failed;
                run.FinishedAt = failedAt;
                run.Error = queuedReason;
            }
        }

        return true;
    }

    private static bool ReconcileRequestState(
        AppDbContext db,
        CodexRequest request,
        bool isActive)
    {
        var requestRun = GetLatestRunOfKind(request.Runs, RunKind.Request);
        var commitRun = GetLatestRunOfKind(request.Runs, RunKind.Commit);
        if (requestRun is null)
        {
            return false;
        }

        var changed = false;
        if (RepairFalseUsageLimit(requestRun))
        {
            changed = true;
        }

        if (request.Status == QueueStatus.CancelRequested && !isActive)
        {
            MarkRequestCancelled(request, "Cancelled by user.");
            return true;
        }

        if (requestRun.Status == QueueStatus.Running && !isActive && IsStaleRunningRun(requestRun))
        {
            ResetRunForResume(requestRun);
            request.Error = null;
            request.StartedAt = null;
            MarkRequestQueued(request);
            return true;
        }

        if (requestRun.Status == QueueStatus.Running && request.Status != QueueStatus.Running)
        {
            request.Status = QueueStatus.Running;
            request.Error = null;
            request.FinishedAt = null;
            return true;
        }

        if (commitRun is not null && RepairFalseUsageLimit(commitRun))
        {
            changed = true;
        }

        if (requestRun.Status == QueueStatus.Succeeded && (!request.GenerateCommit || !request.SeparateCommitSession))
        {
            var cancelledCommitRun = CancelUnusedCommitRun(commitRun);
            if (request.Status != QueueStatus.Succeeded)
            {
                MarkRequestSucceeded(request, requestRun);
                return true;
            }

            return changed || cancelledCommitRun;
        }

        if (requestRun.Status == QueueStatus.Succeeded && request.GenerateCommit && request.SeparateCommitSession)
        {
            if (commitRun?.Status == QueueStatus.Succeeded && request.Status != QueueStatus.Succeeded)
            {
                MarkRequestSucceeded(request, commitRun);
                return true;
            }

            if (commitRun is null)
            {
                var createdCommitRun = CreateCommitRun(request);
                createdCommitRun.Request = request;
                db.Runs.Add(createdCommitRun);
                MarkRequestQueued(request);
                return true;
            }

            if (commitRun.Status == QueueStatus.Queued && request.Status != QueueStatus.Queued)
            {
                MarkRequestQueued(request);
                return true;
            }

            if (commitRun.Status == QueueStatus.Failed && request.Status != QueueStatus.Failed)
            {
                request.Status = QueueStatus.Failed;
                request.FinishedAt = commitRun.FinishedAt ?? DateTimeOffset.UtcNow;
                request.Error = commitRun.Error ?? LastUsefulLine(commitRun.Output);
                UpdateRequestSummary(request);
                return true;
            }

            if (commitRun.Status == QueueStatus.Cancelled && request.Status != QueueStatus.Cancelled)
            {
                request.Status = QueueStatus.Cancelled;
                request.FinishedAt = commitRun.FinishedAt ?? DateTimeOffset.UtcNow;
                request.Error = commitRun.Error ?? "Commit run cancelled.";
                UpdateRequestSummary(request);
                return true;
            }

            if (commitRun.Status == QueueStatus.UsageLimited && request.Status != QueueStatus.UsageLimited)
            {
                request.Status = QueueStatus.UsageLimited;
                request.FinishedAt = commitRun.FinishedAt ?? DateTimeOffset.UtcNow;
                request.Error = commitRun.Error;
                request.RetryAfter = commitRun.RetryAfter;
                request.RetryReason = commitRun.RetryReason;
                request.AvailableModel = commitRun.AvailableModel;
                return true;
            }

            if ((commitRun.Status is QueueStatus.Running or QueueStatus.CancelRequested) && !isActive && IsStaleRunningRun(commitRun))
            {
                ResetRunForResume(commitRun);
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

    private static bool IsStaleRunningRun(CodexRun run)
    {
        var startedAt = run.StartedAt ?? run.CreatedAt;
        return DateTimeOffset.UtcNow - startedAt >= StaleRunningRecoveryDelay;
    }

    private static bool CancelUnusedCommitRun(CodexRun? commitRun)
    {
        if (commitRun is null || commitRun.Status is QueueStatus.Succeeded or QueueStatus.Failed or QueueStatus.Cancelled)
        {
            return false;
        }

        commitRun.Status = QueueStatus.Cancelled;
        commitRun.Error = "Commit handled by the main request session.";
        commitRun.FinishedAt ??= DateTimeOffset.UtcNow;
        return true;
    }

    private static CodexRun? NextRunForDispatch(CodexRequest request)
    {
        var requestRun = GetLatestRunOfKind(request.Runs, RunKind.Request);
        if (requestRun is null)
        {
            return null;
        }

        if (requestRun.Status != QueueStatus.Succeeded)
        {
            return requestRun.Status == QueueStatus.Queued ? requestRun : null;
        }

        var commitRun = GetLatestRunOfKind(request.Runs, RunKind.Commit);
        if (!request.GenerateCommit || !request.SeparateCommitSession)
        {
            CancelUnusedCommitRun(commitRun);
            return null;
        }

        return commitRun?.Status == QueueStatus.Queued ? commitRun : null;
    }

    private static CodexRun? GetLatestRunOfKind(ICollection<CodexRun> runs, RunKind kind) =>
        runs
            .Where(x => x.Kind == kind)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();

    internal static bool HasUnclaimedRunningStage(CodexRequest request) =>
        request.Status == QueueStatus.Running
        && request.Runs.Any(run => run.Status is QueueStatus.Running or QueueStatus.CancelRequested);

    internal static CodexRun? FindOrRestoreRunForExecution(CodexRequest request, RunKind kind)
    {
        var existing = GetLatestRunOfKind(request.Runs, kind);
        if (existing is not null || kind != RunKind.Commit)
        {
            return existing;
        }

        var requestRun = GetLatestRunOfKind(request.Runs, RunKind.Request);
        if (!request.GenerateCommit
            || !request.SeparateCommitSession
            || requestRun?.Status != QueueStatus.Succeeded)
        {
            return null;
        }

        var restored = CreateCommitRun(request);
        request.Runs.Add(restored);
        return restored;
    }

    private static void ResumeRequest(AppDbContext db, CodexRequest request)
    {
        var requestRun = GetLatestRunOfKind(request.Runs, RunKind.Request);
        var commitRun = GetLatestRunOfKind(request.Runs, RunKind.Commit);

        if (requestRun is null)
        {
            requestRun = new CodexRun
            {
                RequestId = request.Id,
                Request = request,
                Kind = RunKind.Request,
                Model = request.Model,
                ModelEffort = request.ModelEffort,
                ModelSpeed = request.ModelSpeed,
                ExecutionRunner = request.ExecutionRunner,
                ProviderProfileId = request.ProviderProfileId,
                ProviderProfileName = request.ProviderProfile?.Name,
                ProviderSource = request.ProviderProfile?.Source,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Runs.Add(requestRun);
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

        if (!request.GenerateCommit || !request.SeparateCommitSession)
        {
            CancelUnusedCommitRun(commitRun);
            MarkRequestSucceeded(request, requestRun);
            return;
        }

        if (commitRun is null)
        {
            var createdCommitRun = CreateCommitRun(request);
            createdCommitRun.Request = request;
            db.Runs.Add(createdCommitRun);
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
            ExecutionRunner = request.CommitExecutionRunner ?? request.ExecutionRunner,
            ProviderProfileId = request.CommitProviderProfileId
                ?? (request.CommitExecutionRunner is null ? request.ProviderProfileId : null),
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
        run.CodexSessionId = null;
        run.OpenHandsConversationId = null;
        run.LocalCodexSessionId = null;
        run.CommandPreview = null;
        run.Output = "";
        run.RawDiagnosticOutput = "";
        run.CommitMessage = null;
        run.CommitSha = null;
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

    private static void MarkRequestCancelled(CodexRequest request, string reason)
    {
        var finishedAt = DateTimeOffset.UtcNow;
        ClearRequestRetryState(request);
        request.Status = QueueStatus.Cancelled;
        request.Error = reason;
        request.FinishedAt = finishedAt;

        foreach (var run in request.Runs.Where(x =>
                     x.Status is QueueStatus.Queued or QueueStatus.Running or QueueStatus.CancelRequested or QueueStatus.UsageLimited))
        {
            run.Status = QueueStatus.Cancelled;
            run.FinishedAt = finishedAt;
            run.Error = reason;
        }
    }

    private static void MarkRequestSucceeded(CodexRequest request, CodexRun run)
    {
        ClearRequestRetryState(request);
        request.Status = QueueStatus.Succeeded;
        request.Error = null;
        request.FinishedAt = run.FinishedAt ?? DateTimeOffset.UtcNow;
        UpdateRequestSummary(request);
    }

    private static void UpdateRequestSummary(CodexRequest request, string? requestOutput = null)
    {
        var output = requestOutput ?? GetLatestRunOfKind(request.Runs, RunKind.Request)?.Output;
        var summary = LastAssistantMessage(output ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(summary))
        {
            request.Summary = summary;
        }
    }

    private static void ClearRequestRetryState(CodexRequest request)
    {
        request.RetryAfter = null;
        request.RetryReason = null;
        request.AvailableModel = null;
        request.QueueWaitReason = null;
    }

    private async Task CompleteRunAsync(
        Guid requestId,
        Guid runId,
        RunKind kind,
        QueueAgentRunResult result,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.Requests
            .Include(x => x.Project)
            .Include(x => x.QueueTab)
            .Include(x => x.Runs)
            .FirstAsync(x => x.Id == requestId, cancellationToken);
        var run = runId == Guid.Empty
            ? GetLatestRunOfKind(request.Runs, kind)
            : request.Runs.First(x => x.Id == runId);
        if (run is null)
        {
            return;
        }
        run.CommandPreview = result.CommandPreview;
        // Streaming stdout and stderr can arrive concurrently. AppendOutputAsync is
        // intentionally best-effort for live updates, so reconcile the complete
        // process capture here before marking the run finished. This guarantees the
        // final assistant event is retained even when two live-output writes raced.
        run.Output = MergeCompletedOutput(run.Output, result.Output);
        var executionRunner = kind == RunKind.Commit
            ? run.ExecutionRunner
            : request.ExecutionRunner;
        if (executionRunner == ExecutionRunner.CodexCli)
        {
            var sessionId = result.CodexSessionId ?? run.CodexSessionId;
            run.CodexSessionId = sessionId;
            if (kind == RunKind.Request && request.QueueTab is not null && !string.IsNullOrWhiteSpace(sessionId))
            {
                request.QueueTab.CodexSessionId = sessionId;
                request.QueueTab.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        else
        {
            var sessionId = result.LocalCodexSessionId ?? run.LocalCodexSessionId;
            run.LocalCodexSessionId = sessionId;
            if (kind == RunKind.Request
                && request.QueueTab is not null
                && !string.IsNullOrWhiteSpace(sessionId))
            {
                request.QueueTab.LocalCodexSessionId = sessionId;
                request.QueueTab.LocalCodexSessionRouteKey =
                    result.LocalCodexSessionRouteKey;
                request.QueueTab.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        run.ExitCode = result.ExitCode;
        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Status = result.Success ? QueueStatus.Succeeded : QueueStatus.Failed;
        run.Error = result.Success ? null : LastUsefulLine(result.Output);
        request.QueueWaitReason = null;

        if (kind == RunKind.Commit)
        {
            request.FinishedAt = run.FinishedAt;
            request.Status = result.Success ? QueueStatus.Succeeded : QueueStatus.Failed;
            request.Error = result.Success ? null : run.Error;
            UpdateRequestSummary(request);
            if (CommitOutputClaimsCreated(result.Output))
            {
                await PopulateCommitMetadataAsync(db, request, run, cancellationToken);
            }
        }
        else
        {
            if (result.Success)
            {
                UpdateRequestSummary(request, result.Output);
            }

            if (!request.GenerateCommit || !request.SeparateCommitSession)
            {
                request.FinishedAt = run.FinishedAt;
                request.Status = result.Success ? QueueStatus.Succeeded : QueueStatus.Failed;
                request.Error = result.Success ? null : run.Error;
                if (result.Success && request.GenerateCommit && CommitOutputClaimsCreated(result.Output))
                {
                    await PopulateCommitMetadataAsync(db, request, run, cancellationToken);
                }
            }
            else if (!result.Success)
            {
                request.FinishedAt = run.FinishedAt;
                request.Status = QueueStatus.Failed;
                request.Error = run.Error;
            }
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
            .Include(x => x.QueueTab)
            .Include(x => x.Runs)
            .FirstAsync(x => x.Id == request.Id, cancellationToken);
        run = request.Runs.FirstOrDefault(candidate => candidate.Id == run.Id)
            ?? GetLatestRunOfKind(request.Runs, kind)
            ?? throw new InvalidOperationException(
                kind == RunKind.Commit
                    ? "Separate commit run disappeared while recording its usage limit."
                    : "Request run was not found.");
        var finishedAt = DateTimeOffset.UtcNow;
        run.CommandPreview = result.CommandPreview;
        run.CodexSessionId = result.CodexSessionId ?? run.CodexSessionId;
        if (kind == RunKind.Request && request.QueueTab is not null && !string.IsNullOrWhiteSpace(run.CodexSessionId))
        {
            request.QueueTab.CodexSessionId = run.CodexSessionId;
            request.QueueTab.UpdatedAt = DateTimeOffset.UtcNow;
        }
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

        var output = StripShellCommandPreview(result.Output);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var shaIndex = Array.FindIndex(lines, line =>
        {
            var trimmed = line.Trim();
            return trimmed.Length == 40 && trimmed.All(IsHex);
        });

        if (shaIndex >= 0)
        {
            run.CommitSha = lines[shaIndex].Trim();
            var message = string.Join('\n', lines.Skip(shaIndex + 1)).Trim();
            run.CommitMessage = string.IsNullOrWhiteSpace(message) ? null : message;
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
            ? GetLatestRunOfKind(request.Runs, kind)
            : request.Runs.First(x => x.Id == runId);
        if (run is null)
        {
            return;
        }
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
            ? GetLatestRunOfKind(request.Runs, kind)
            : request.Runs.First(x => x.Id == runId);
        if (run is null)
        {
            return;
        }
        run.Status = QueueStatus.Failed;
        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Error = exception.Message;
        run.Output = TrimOutput(run.Output + Environment.NewLine + "Run failed: " + exception.Message + Environment.NewLine);
        request.Status = QueueStatus.Failed;
        request.FinishedAt = run.FinishedAt;
        request.Error = kind == RunKind.Commit ? "Commit run failed: " + exception.Message : exception.Message;
        await TrySaveChangesAsync(db, "mark failed", cancellationToken);
    }

    private async Task RecordUnhandledExecutionFailureAsync(Guid requestId, RunKind kind, Exception exception)
    {
        // ExecuteRunAsync normally records failures itself. This is a final safety net
        // for failures before its guarded section or while persisting that result; a
        // released queue lane must never leave its request permanently "Running".
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var request = await db.Requests.Include(x => x.Runs).FirstOrDefaultAsync(x => x.Id == requestId);
            if (request is null || request.Status is QueueStatus.Succeeded or QueueStatus.Failed or QueueStatus.Cancelled or QueueStatus.UsageLimited)
            {
                return;
            }

            if (request.Status == QueueStatus.CancelRequested)
            {
                MarkRequestCancelled(request, "Cancelled by user.");
            }
            else
            {
                var run = GetLatestRunOfKind(request.Runs, kind);
                if (run is not null && run.Status is QueueStatus.Queued or QueueStatus.Running or QueueStatus.CancelRequested)
                {
                    run.Status = QueueStatus.Failed;
                    run.FinishedAt = DateTimeOffset.UtcNow;
                    run.Error = exception.Message;
                    run.Output = TrimOutput(run.Output + Environment.NewLine + "Run failed: " + exception.Message + Environment.NewLine);
                }

                request.Status = QueueStatus.Failed;
                request.FinishedAt = DateTimeOffset.UtcNow;
                request.Error = kind == RunKind.Commit ? "Commit run failed: " + exception.Message : exception.Message;
            }

            await TrySaveChangesAsync(db, "record unhandled queue execution failure", CancellationToken.None);
        }
        catch (Exception persistenceException)
        {
            _lastError = persistenceException.Message;
            logger.LogError(persistenceException, "Could not record the failure for queue request {RequestId}.", requestId);
        }
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

    private async Task<MaterializedAttachments> MaterializeAttachmentsAsync(
        CodexRequest request,
        Guid runId,
        Project project,
        TargetMachine machine,
        CancellationToken cancellationToken)
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
        var attachmentRoot = BuildAttachmentRoot(project.Path, request.Id, machine);

        foreach (var attachment in attachments)
        {
            var bytes = Convert.FromBase64String(attachment.ContentBase64);
            var storageName = attachment.StorageName ?? attachment.Name;
            var relativePath = ".codex-queue/attachments/" + request.Id.ToString("N") + "/" + storageName;
            var targetPath = CombineTargetPath(attachmentRoot, storageName, machine);
            await AppendOutputAsync(runId, "Transferring attachment " + attachment.Name + "..." + Environment.NewLine, CancellationToken.None);
            await runner.WriteAttachmentAsync(machine, targetPath, bytes, cancellationToken);
            await AppendOutputAsync(runId, "Transferred attachment " + attachment.Name + "." + Environment.NewLine, CancellationToken.None);
            prompt.Add("- " + attachment.Name + " (" + attachment.ContentType + ", " + attachment.Size + " bytes) available temporarily at `" + relativePath + "`.");
            if (attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                imagePaths.Add(targetPath);
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

    private static string BuildAttachmentRoot(string projectPath, Guid requestId, TargetMachine machine) =>
        CombineTargetPath(
            CombineTargetPath(CombineTargetPath(projectPath, ".codex-queue", machine), "attachments", machine),
            requestId.ToString("N"),
            machine);

    private async Task RemoveMaterializedAttachmentsAsync(CodexRequest request, Project project, TargetMachine machine)
    {
        try
        {
            await runner.DeleteAttachmentDirectoryAsync(
                machine,
                BuildAttachmentRoot(project.Path, request.Id, machine),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A cleanup failure must not replace the result of the user's request, but it
            // must be visible to operators because it can leave an untracked project file.
            logger.LogWarning(ex, "Could not remove temporary attachments for request {RequestId}.", request.Id);
        }
    }

    private static string CombineTargetPath(string parent, string child, TargetMachine machine)
    {
        var separator = machine.TargetsWindows() ? '\\' : '/';
        return parent.TrimEnd('\\', '/') + separator + child;
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

    private static bool CommitOutputClaimsCreated(string output) =>
        CommitCreatedOutputRegex.IsMatch(output);

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static string BuildRequestPrompt(CodexRequest request)
    {
        var attachmentBoundary = string.IsNullOrWhiteSpace(request.AttachmentsJson)
            ? ""
            : "\n\nDo not modify, stage, or commit `.codex-queue/attachments`; it contains temporary prompt inputs.";
        var runnerBoundary = request.ExecutionRunner == ExecutionRunner.OpenHandsCli
            ? """

              Local Codex safety constraints for this queued run:
              - Work only inside the selected project root, except for normal read-only operating-system files needed by build tools.
              - Do not modify, stage, or commit anything under `.codex-queue`; it contains temporary control-plane task inputs that are removed after this run.
              - Do not create Git commits, push, force-push, rewrite history, or alter remote branches.
              - Do not use sudo, administrator elevation, or change machine-level configuration.
              - Do not inspect or access credentials, secret stores, or unrelated environment variables. Use only the inference configuration supplied by the launcher.
              - When the user asks for a code or file change, make the change in this run. Do not substitute a recipe, sample code, or proposed paths for an edit.
              - Before finishing an implementation request, inspect the affected files and verify the workspace diff. If no change was made, say so plainly instead of claiming the implementation is complete.
              """
            : "";
        if (!request.GenerateCommit)
        {
            return request.Prompt + attachmentBoundary + runnerBoundary;
        }

        if (request.SeparateCommitSession)
        {
            return request.Prompt.TrimEnd()
                + """

                Do not run git commit in this run.
                Leave file changes for the follow-up Codex commit step.
                """ + attachmentBoundary + runnerBoundary;
        }

        return request.Prompt.TrimEnd()
            + """

            After making the requested file changes, inspect the git changes and create exactly one git commit yourself.
            Stage only changes under this project root. Prefer pathspec-limited commands such as `git add -A -- .`.
            Choose one concise imperative commit message.
            Do not amend existing commits.
            Do not push.
            """ + attachmentBoundary + runnerBoundary;
    }

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

    private static string ValidateLocalCodexExecutionTarget(
        CodexRequest request,
        Project project,
        TargetMachine machine)
    {
        if (project.MachineId != request.MachineId || machine.Id != request.MachineId)
        {
            throw new InvalidOperationException(
                "The selected machine changed after this Local Codex request was queued. The task was not moved; create a new request for the new machine.");
        }

        if (string.IsNullOrWhiteSpace(request.ExecutionProjectPath)
            || !string.Equals(request.ExecutionProjectPath, project.Path, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The selected project path changed after this Local Codex request was queued. The task was not moved; create a new request for the new path.");
        }

        if (!request.ExecutionMachineUpdatedAt.HasValue
            || request.ExecutionMachineUpdatedAt.Value != machine.UpdatedAt)
        {
            throw new InvalidOperationException(
                "The selected machine configuration changed after this Local Codex request was queued. The task was not moved; review the machine and create a new request.");
        }

        return request.ExecutionProjectPath;
    }

    private static string TrimOutput(string value) =>
        value.Length <= MaxStoredOutput ? value : value[^MaxStoredOutput..];

    private static string MergeCompletedOutput(string streamedOutput, string completedOutput)
    {
        if (string.IsNullOrWhiteSpace(completedOutput)
            || streamedOutput.Contains(completedOutput, StringComparison.Ordinal))
        {
            return streamedOutput;
        }

        return string.IsNullOrWhiteSpace(streamedOutput)
            ? TrimOutput(completedOutput)
            : TrimOutput(streamedOutput + Environment.NewLine + completedOutput);
    }

    private static string StripShellCommandPreview(string output)
    {
        if (!output.StartsWith("$ ", StringComparison.Ordinal))
        {
            return output;
        }

        var newline = output.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? "" : output[(newline + 1)..];
    }

    private static string? LastUsefulLine(string output)
    {
        string? lastPlainText = null;
        string? lastAssistantText = null;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eventText = line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? line["data:".Length..].Trim()
                : line;

            if (TryReadCodexEvent(eventText, out var eventJson))
            {
                using (eventJson)
                {
                    if (TryExtractAssistantText(eventJson.RootElement, out var assistantText))
                    {
                        lastAssistantText = assistantText;
                    }

                    if (!IsTelemetryCompletionEvent(eventJson.RootElement)
                        && !CompletionTextCleaner.IsNoiseLine(line))
                    {
                        lastPlainText = line;
                    }
                }

                continue;
            }

            if (!CompletionTextCleaner.IsNoiseLine(line))
            {
                lastPlainText = line;
            }
        }

        return lastAssistantText ?? lastPlainText;
    }

    private static string? LastAssistantMessage(string output)
    {
        string? lastAssistantText = null;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eventText = line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? line["data:".Length..].Trim()
                : line;
            if (!TryReadCodexEvent(eventText, out var eventJson))
            {
                continue;
            }

            using (eventJson)
            {
                if (TryExtractAssistantText(eventJson.RootElement, out var assistantText))
                {
                    lastAssistantText = assistantText;
                }
            }
        }

        return lastAssistantText;
    }

    private static bool TryReadCodexEvent(string line, out JsonDocument document)
    {
        document = null!;
        try
        {
            document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            document.Dispose();
            document = null!;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryExtractAssistantText(JsonElement root, out string? text)
    {
        text = null;
        var hasItem = root.TryGetProperty("item", out var itemElement) && itemElement.ValueKind == JsonValueKind.Object;
        var item = hasItem
            ? itemElement
            : root;

        var eventType = ReadString(root, "type");
        var fallback = hasItem ? root : default;
        if (TryExtractAssistantTextFromRecord(item, eventType, fallback, out text))
        {
            return true;
        }

        return TryExtractAssistantTextFromNestedOutput(root, eventType, out text);
    }

    private static bool TryExtractAssistantTextFromRecord(JsonElement item, string? eventType, JsonElement fallback, out string? text)
    {
        text = null;
        var role = ReadString(item, "role");
        var itemType = ReadString(item, "type");
        var looksLikeCompletedMessage = IsCompletedType(eventType) && IsMessageType(itemType);
        var looksLikeAssistantMessage = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                                        || looksLikeCompletedMessage
                                        || IsAssistantTextEventType(eventType)
                                        || IsAssistantTextEventType(itemType)
                                        || IsAssistantMessageType(eventType)
                                        || IsAssistantMessageType(itemType);
        if (!looksLikeAssistantMessage)
        {
            return false;
        }

        text = ReadContentText(item)
            ?? ReadString(item, "message")
            ?? ReadString(item, "text")
            ?? ReadString(item, "output_text")
            ?? ReadString(fallback, "message")
            ?? ReadString(fallback, "text")
            ?? ReadString(fallback, "output_text");

        text = CompletionTextCleaner.Sanitize(text);
        return text is not null;
    }

    private static bool TryExtractAssistantTextFromNestedOutput(JsonElement root, string? eventType, out string? text)
    {
        text = null;
        if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
        {
            if (TryExtractAssistantTextFromRecord(response, eventType, default, out text)
                || TryExtractAssistantTextFromOutputArray(response, eventType, out text))
            {
                return true;
            }
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (TryExtractAssistantTextFromRecord(data, eventType, default, out text)
                || TryExtractAssistantTextFromOutputArray(data, eventType, out text))
            {
                return true;
            }
        }

        return TryExtractAssistantTextFromOutputArray(root, eventType, out text);
    }

    private static bool TryExtractAssistantTextFromOutputArray(JsonElement source, string? eventType, out string? text)
    {
        text = null;
        if (!source.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in output.EnumerateArray().Reverse())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (TryExtractAssistantTextFromRecord(item, eventType, default, out text))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadContentText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = content.EnumerateArray()
            .Select(ReadContentPartText)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .ToArray();

        return parts.Length > 0 ? string.Join(Environment.NewLine + Environment.NewLine, parts) : null;
    }

    private static string? ReadContentPartText(JsonElement part)
    {
        if (part.ValueKind == JsonValueKind.String)
        {
            return part.GetString();
        }

        if (part.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadString(part, "text") ?? ReadString(part, "content") ?? ReadString(part, "message") ?? ReadString(part, "output_text");
    }

    private static bool IsTelemetryCompletionEvent(JsonElement root)
    {
        var type = ReadString(root, "type");
        if (!string.Equals(type, "turn.completed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(type, "turn_completed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(type, "turn-completed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !TryExtractAssistantText(root, out _)
               && (root.TryGetProperty("usage", out _) || root.TryGetProperty("token_usage", out _));
    }

    private static bool IsCompletedType(string? type) =>
        !string.IsNullOrWhiteSpace(type)
        && (type.EndsWith(".completed", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith("_completed", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith("-completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "completed", StringComparison.OrdinalIgnoreCase));

    private static bool IsMessageType(string? type) =>
        !string.IsNullOrWhiteSpace(type)
        && HasNormalizedTypeSuffix(type, "message");

    private static bool IsAssistantMessageType(string? type) =>
        !string.IsNullOrWhiteSpace(type)
        && (HasNormalizedTypeSuffix(type, "agent_message")
            || HasNormalizedTypeSuffix(type, "assistant_message"));

    private static bool IsAssistantTextEventType(string? type) =>
        !string.IsNullOrWhiteSpace(type)
        && HasNormalizedTypeSuffix(type, "output_text_done");

    private static bool HasNormalizedTypeSuffix(string type, string suffix)
    {
        var normalized = type.Replace('.', '_').Replace('-', '_');
        return string.Equals(normalized, suffix, StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("_" + suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static QueueLaneKey QueueLaneFor(CodexRequest request) =>
        new(request.ProjectId, request.Project?.SeparateQueuesByTab == true ? request.QueueTabId : null);

    public static string ProviderConcurrencyKey(AiProviderProfile profile)
    {
        if (AiProviderService.TryNormalizeBaseUrl(
                profile.Source,
                profile.BaseUrl,
                out var normalized,
                out _)
            && Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return profile.Source + "|" + uri.Scheme + "://" + uri.Authority;
        }

        return profile.Source + "|" + profile.BaseUrl.Trim().TrimEnd('/');
    }

    public static IReadOnlyDictionary<string, int> BuildProviderConcurrencyLimits(
        IEnumerable<AiProviderProfile> profiles) =>
        profiles
            .GroupBy(ProviderConcurrencyKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Min(profile => Math.Max(1, profile.MaximumConcurrency)),
                StringComparer.Ordinal);

    private readonly record struct QueueLaneKey(Guid ProjectId, Guid? QueueTabId);

    private sealed record PendingDispatch(
        QueueLaneKey QueueLane,
        Guid RequestId,
        RunKind RunKind,
        QueueExecution Execution);

    private sealed class QueueExecution(
        CancellationTokenSource cancellation,
        IDisposable? providerLease)
    {
        private IDisposable? _providerLease = providerLease;

        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReplaceProviderLease(IDisposable? providerLease)
        {
            var previousLease = Interlocked.Exchange(ref _providerLease, providerLease);
            previousLease?.Dispose();
        }

        public void ReleaseProviderLease() =>
            Interlocked.Exchange(ref _providerLease, null)?.Dispose();
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
