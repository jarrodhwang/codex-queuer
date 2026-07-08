using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CodexQueue.Api.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureColumnAsync(db, "Machines", "Platform", "ALTER TABLE \"Machines\" ADD COLUMN \"Platform\" TEXT NOT NULL DEFAULT 'Auto'", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "ModelEffort", "ALTER TABLE \"Requests\" ADD COLUMN \"ModelEffort\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "ModelSpeed", "ALTER TABLE \"Requests\" ADD COLUMN \"ModelSpeed\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "QueueOrder", "ALTER TABLE \"Requests\" ADD COLUMN \"QueueOrder\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "CommitModelEffort", "ALTER TABLE \"Requests\" ADD COLUMN \"CommitModelEffort\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "CommitModelSpeed", "ALTER TABLE \"Requests\" ADD COLUMN \"CommitModelSpeed\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "SeparateCommitSession", "ALTER TABLE \"Requests\" ADD COLUMN \"SeparateCommitSession\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "AttachmentsJson", "ALTER TABLE \"Requests\" ADD COLUMN \"AttachmentsJson\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "RetryAfter", "ALTER TABLE \"Requests\" ADD COLUMN \"RetryAfter\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "RetryReason", "ALTER TABLE \"Requests\" ADD COLUMN \"RetryReason\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "AvailableModel", "ALTER TABLE \"Requests\" ADD COLUMN \"AvailableModel\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "ArchivedAt", "ALTER TABLE \"Requests\" ADD COLUMN \"ArchivedAt\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "DeletedAt", "ALTER TABLE \"Requests\" ADD COLUMN \"DeletedAt\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "CodexSessionId", "ALTER TABLE \"Projects\" ADD COLUMN \"CodexSessionId\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DefaultModel", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultModel\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DefaultModelEffort", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultModelEffort\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DefaultModelSpeed", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultModelSpeed\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DefaultCommitModel", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultCommitModel\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DefaultCommitModelEffort", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultCommitModelEffort\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DefaultCommitModelSpeed", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultCommitModelSpeed\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DefaultGenerateCommit", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultGenerateCommit\" INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DefaultSeparateCommitSession", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultSeparateCommitSession\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "ModelEffort", "ALTER TABLE \"Runs\" ADD COLUMN \"ModelEffort\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "ModelSpeed", "ALTER TABLE \"Runs\" ADD COLUMN \"ModelSpeed\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "CodexSessionId", "ALTER TABLE \"Runs\" ADD COLUMN \"CodexSessionId\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "RetryAfter", "ALTER TABLE \"Runs\" ADD COLUMN \"RetryAfter\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "RetryReason", "ALTER TABLE \"Runs\" ADD COLUMN \"RetryReason\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "AvailableModel", "ALTER TABLE \"Runs\" ADD COLUMN \"AvailableModel\" TEXT NULL", cancellationToken);
        await EnsureQueueOrderAsync(db, cancellationToken);

        var defaultMachine = DefaultPaths.DefaultMachine();
        if (!await db.Machines.AnyAsync(cancellationToken))
        {
            db.Machines.Add(defaultMachine);
            await db.SaveChangesAsync(cancellationToken);
        }

        var machines = await db.Machines
            .Include(x => x.Projects)
            .ToArrayAsync(cancellationToken);
        var localShell = machines.FirstOrDefault(x => DefaultPaths.IsDefaultMachineName(x.Name));
        var savedMachineDefaults = false;
        if (localShell is not null && localShell.Kind == MachineKind.Local && DefaultPaths.IsOldLocalDefault(localShell.WorkingRoot))
        {
            var oldRoot = localShell.WorkingRoot;
            ApplyDefaultMachine(localShell, defaultMachine);
            RemapDefaultLocalProjects(localShell, oldRoot, defaultMachine.WorkingRoot);
            localShell.UpdatedAt = DateTimeOffset.UtcNow;
            savedMachineDefaults = true;
        }

        foreach (var machine in machines)
        {
            var defaultWorkingRoot = DefaultPaths.DefaultWorkingRoot(machine.Kind, machine.Platform);
            var shouldCorrectRoot = string.IsNullOrWhiteSpace(machine.WorkingRoot)
                || (machine.Kind == MachineKind.Local
                    && DefaultPaths.IsOldLocalDefault(machine.WorkingRoot)
                    && !string.Equals(machine.WorkingRoot.TrimEnd('/', '\\'), defaultWorkingRoot.TrimEnd('/', '\\'), StringComparison.Ordinal));

            if (!shouldCorrectRoot)
            {
                continue;
            }

            machine.WorkingRoot = defaultWorkingRoot;
            machine.UpdatedAt = DateTimeOffset.UtcNow;
            savedMachineDefaults = true;
        }

        if (savedMachineDefaults)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        try
        {
            var interruptedRequests = await db.Requests
                .Include(x => x.Runs)
                .Where(x => x.Status == QueueStatus.Running
                    || x.Status == QueueStatus.CancelRequested
                    || x.Status == QueueStatus.Failed)
                .ToArrayAsync(cancellationToken);

            var repairedRequests = false;
            foreach (var request in interruptedRequests)
            {
                if (RepairInterruptedRequest(request))
                {
                    repairedRequests = true;
                    continue;
                }

                if (request.Status != QueueStatus.Running && request.Status != QueueStatus.CancelRequested)
                {
                    continue;
                }

                request.Status = QueueStatus.Failed;
                request.FinishedAt = DateTimeOffset.UtcNow;
                request.Error = "Run was interrupted by API server restart.";
                foreach (var run in request.Runs.Where(x => x.Status == QueueStatus.Running || x.Status == QueueStatus.CancelRequested))
                {
                    run.Status = QueueStatus.Failed;
                    run.FinishedAt = request.FinishedAt;
                    run.Error = request.Error;
                }

                repairedRequests = true;
            }

            if (repairedRequests)
            {
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Queue startup repair failed. Continuing API startup so diagnostics remain available.");
        }
    }

    private static async Task EnsureQueueOrderAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var unorderedRequests = await db.Requests
            .Where(x => x.QueueOrder == 0)
            .ToArrayAsync(cancellationToken);

        if (unorderedRequests.Length == 0)
        {
            return;
        }

        foreach (var group in unorderedRequests
                     .OrderBy(x => x.ProjectId)
                     .ThenBy(x => x.CreatedAt)
                     .GroupBy(x => x.ProjectId))
        {
            var nextOrder = await db.Requests
                .Where(x => x.ProjectId == group.Key)
                .MaxAsync(x => (int?)x.QueueOrder, cancellationToken) ?? 0;
            foreach (var request in group)
            {
                request.QueueOrder = ++nextOrder;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        AppDbContext db,
        string tableName,
        string columnName,
        string alterSql,
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"" + tableName + "\")";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await db.Database.ExecuteSqlRawAsync(alterSql, cancellationToken);
    }

    private static void ApplyDefaultMachine(TargetMachine machine, TargetMachine defaults)
    {
        machine.Name = defaults.Name;
        machine.Kind = defaults.Kind;
        machine.Host = defaults.Host;
        machine.Port = defaults.Port;
        machine.UserName = defaults.UserName;
        machine.SshKeyPath = defaults.SshKeyPath;
        machine.WorkingRoot = defaults.WorkingRoot;
        machine.Platform = defaults.Platform;
    }

    private static void RemapDefaultLocalProjects(TargetMachine machine, string? oldRoot, string? newRoot)
    {
        if (string.IsNullOrWhiteSpace(oldRoot) || string.IsNullOrWhiteSpace(newRoot))
        {
            return;
        }

        var oldRootNormalized = oldRoot.TrimEnd('/', '\\');
        var newRootNormalized = newRoot.TrimEnd('/', '\\');
        if (string.IsNullOrWhiteSpace(oldRootNormalized)
            || string.IsNullOrWhiteSpace(newRootNormalized)
            || string.Equals(oldRootNormalized, newRootNormalized, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var project in machine.Projects)
        {
            if (!project.Path.Equals(oldRootNormalized, StringComparison.Ordinal)
                && !project.Path.StartsWith(oldRootNormalized + "/", StringComparison.Ordinal)
                && !project.Path.StartsWith(oldRootNormalized + "\\", StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = project.Path[oldRootNormalized.Length..].TrimStart('/', '\\');
            project.Path = string.IsNullOrWhiteSpace(suffix)
                ? newRootNormalized
                : newRootNormalized + "/" + suffix.Replace('\\', '/');
            project.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static bool RepairInterruptedRequest(CodexRequest request)
    {
        var requestRun = request.Runs
            .Where(x => x.Kind == RunKind.Request)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();
        if (requestRun?.Status != QueueStatus.Succeeded)
        {
            return false;
        }

        var commitRun = request.Runs
            .Where(x => x.Kind == RunKind.Commit)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();
        if (!request.GenerateCommit || !request.SeparateCommitSession)
        {
            CancelUnusedCommitRun(commitRun);
            MarkRequestSucceeded(request, requestRun);
            return true;
        }

        if (commitRun?.Status == QueueStatus.Succeeded)
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

        if (request.Status is QueueStatus.Running or QueueStatus.CancelRequested
            && commitRun.Status is QueueStatus.Queued or QueueStatus.Running or QueueStatus.CancelRequested or QueueStatus.UsageLimited)
        {
            if (commitRun.Status != QueueStatus.Queued)
            {
                ResetRunForQueue(commitRun);
            }
            MarkRequestQueued(request);
            return true;
        }

        return false;
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

    private static void ResetRunForQueue(CodexRun run)
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

    private static string? LastUsefulLine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
    }
}
