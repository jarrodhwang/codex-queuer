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
        var configuration = scope.ServiceProvider.GetService<IConfiguration>();
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureAiProviderProfilesTableAsync(db, cancellationToken);
        await EnsureColumnAsync(db, "AiProviderProfiles", "LocalAiServerType", "ALTER TABLE \"AiProviderProfiles\" ADD COLUMN \"LocalAiServerType\" TEXT NOT NULL DEFAULT 'Ollama'", cancellationToken);
        await EnsureColumnAsync(db, "AiProviderProfiles", "ServerMachineId", "ALTER TABLE \"AiProviderProfiles\" ADD COLUMN \"ServerMachineId\" TEXT NULL REFERENCES \"Machines\" (\"Id\") ON DELETE SET NULL", cancellationToken);
        await EnsureAiProviderProfileIndexesAsync(db, cancellationToken);
        await EnsureQueueTabsTableAsync(db, cancellationToken);
        await EnsureColumnAsync(db, "QueueTabs", "DeletedAt", "ALTER TABLE \"QueueTabs\" ADD COLUMN \"DeletedAt\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "QueueTabs", "OpenHandsConversationId", "ALTER TABLE \"QueueTabs\" ADD COLUMN \"OpenHandsConversationId\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "QueueTabs", "LocalCodexSessionId", "ALTER TABLE \"QueueTabs\" ADD COLUMN \"LocalCodexSessionId\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "QueueTabs", "LocalCodexSessionRouteKey", "ALTER TABLE \"QueueTabs\" ADD COLUMN \"LocalCodexSessionRouteKey\" TEXT NULL", cancellationToken);
        await EnsureQueueTabIndexesAsync(db, cancellationToken);
        await EnsureColumnAsync(db, "Machines", "Platform", "ALTER TABLE \"Machines\" ADD COLUMN \"Platform\" TEXT NOT NULL DEFAULT 'Auto'", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "QueueTabId", "ALTER TABLE \"Requests\" ADD COLUMN \"QueueTabId\" TEXT NULL REFERENCES \"QueueTabs\" (\"Id\") ON DELETE SET NULL", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Requests_QueueTabId\" ON \"Requests\" (\"QueueTabId\")", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "ExecutionRunner", "ALTER TABLE \"Requests\" ADD COLUMN \"ExecutionRunner\" TEXT NOT NULL DEFAULT 'CodexCli'", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "ProviderProfileId", "ALTER TABLE \"Requests\" ADD COLUMN \"ProviderProfileId\" TEXT NULL REFERENCES \"AiProviderProfiles\" (\"Id\") ON DELETE RESTRICT", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "OpenHandsAlwaysApproveConfirmed", "ALTER TABLE \"Requests\" ADD COLUMN \"OpenHandsAlwaysApproveConfirmed\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "QueueWaitReason", "ALTER TABLE \"Requests\" ADD COLUMN \"QueueWaitReason\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "ExecutionProjectPath", "ALTER TABLE \"Requests\" ADD COLUMN \"ExecutionProjectPath\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "ExecutionMachineUpdatedAt", "ALTER TABLE \"Requests\" ADD COLUMN \"ExecutionMachineUpdatedAt\" TEXT NULL", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Requests_ProviderProfileId\" ON \"Requests\" (\"ProviderProfileId\")", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "ModelEffort", "ALTER TABLE \"Requests\" ADD COLUMN \"ModelEffort\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "ModelSpeed", "ALTER TABLE \"Requests\" ADD COLUMN \"ModelSpeed\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "QueueOrder", "ALTER TABLE \"Requests\" ADD COLUMN \"QueueOrder\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "CommitModelEffort", "ALTER TABLE \"Requests\" ADD COLUMN \"CommitModelEffort\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "CommitModelSpeed", "ALTER TABLE \"Requests\" ADD COLUMN \"CommitModelSpeed\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "SeparateCommitSession", "ALTER TABLE \"Requests\" ADD COLUMN \"SeparateCommitSession\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "PermissionMode", "ALTER TABLE \"Requests\" ADD COLUMN \"PermissionMode\" TEXT NOT NULL DEFAULT 'ApproveForMe'", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "InternetSearchEnabled", "ALTER TABLE \"Requests\" ADD COLUMN \"InternetSearchEnabled\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "CommitExecutionRunner", "ALTER TABLE \"Requests\" ADD COLUMN \"CommitExecutionRunner\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Requests", "CommitProviderProfileId", "ALTER TABLE \"Requests\" ADD COLUMN \"CommitProviderProfileId\" TEXT NULL", cancellationToken);
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
        await EnsureColumnAsync(db, "Projects", "DefaultPermissionMode", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultPermissionMode\" TEXT NOT NULL DEFAULT 'ApproveForMe'", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DefaultInternetSearchEnabled", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultInternetSearchEnabled\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DefaultCommitExecutionRunner", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultCommitExecutionRunner\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DefaultCommitLocalProviderProfileId", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultCommitLocalProviderProfileId\" TEXT NULL REFERENCES \"AiProviderProfiles\" (\"Id\") ON DELETE SET NULL", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DefaultExecutionRunner", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultExecutionRunner\" TEXT NOT NULL DEFAULT 'CodexCli'", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DefaultLocalProviderProfileId", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultLocalProviderProfileId\" TEXT NULL REFERENCES \"AiProviderProfiles\" (\"Id\") ON DELETE SET NULL", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DefaultLocalModel", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultLocalModel\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DefaultLocalModelEffort", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultLocalModelEffort\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DefaultLocalModelSpeed", "ALTER TABLE \"Projects\" ADD COLUMN \"DefaultLocalModelSpeed\" TEXT NULL", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Projects_DefaultLocalProviderProfileId\" ON \"Projects\" (\"DefaultLocalProviderProfileId\")", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "SeparateQueuesByTab", "ALTER TABLE \"Projects\" ADD COLUMN \"SeparateQueuesByTab\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "ModelEffort", "ALTER TABLE \"Runs\" ADD COLUMN \"ModelEffort\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "ModelSpeed", "ALTER TABLE \"Runs\" ADD COLUMN \"ModelSpeed\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "ExecutionRunner", "ALTER TABLE \"Runs\" ADD COLUMN \"ExecutionRunner\" TEXT NOT NULL DEFAULT 'CodexCli'", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "ProviderProfileId", "ALTER TABLE \"Runs\" ADD COLUMN \"ProviderProfileId\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "ProviderProfileName", "ALTER TABLE \"Runs\" ADD COLUMN \"ProviderProfileName\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "ProviderSource", "ALTER TABLE \"Runs\" ADD COLUMN \"ProviderSource\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "CodexSessionId", "ALTER TABLE \"Runs\" ADD COLUMN \"CodexSessionId\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "OpenHandsConversationId", "ALTER TABLE \"Runs\" ADD COLUMN \"OpenHandsConversationId\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "LocalCodexSessionId", "ALTER TABLE \"Runs\" ADD COLUMN \"LocalCodexSessionId\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "RawDiagnosticOutput", "ALTER TABLE \"Runs\" ADD COLUMN \"RawDiagnosticOutput\" TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "RetryAfter", "ALTER TABLE \"Runs\" ADD COLUMN \"RetryAfter\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "RetryReason", "ALTER TABLE \"Runs\" ADD COLUMN \"RetryReason\" TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Runs", "AvailableModel", "ALTER TABLE \"Runs\" ADD COLUMN \"AvailableModel\" TEXT NULL", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Runs_ProviderProfileId\" ON \"Runs\" (\"ProviderProfileId\")", cancellationToken);
        await EnsureQueueOrderAsync(db, cancellationToken);
        await EnsureDefaultLocalProviderProfileAsync(db, configuration, logger, cancellationToken);

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

            var interruptedLocalCodexDetected = interruptedRequests.Any(x =>
                x.ExecutionRunner == ExecutionRunner.OpenHandsCli
                && x.Status is QueueStatus.Running or QueueStatus.CancelRequested);
            var repairedRequests = false;
            foreach (var request in interruptedRequests)
            {
                if (request.Status == QueueStatus.CancelRequested)
                {
                    MarkRequestCancelled(request, "Cancelled by user.");
                    repairedRequests = true;
                    continue;
                }

                if (RepairInterruptedRequest(request))
                {
                    repairedRequests = true;
                    continue;
                }

                if (request.Status == QueueStatus.Running
                    && request.ExecutionRunner == ExecutionRunner.OpenHandsCli)
                {
                    request.Status = QueueStatus.Failed;
                    request.QueueWaitReason = null;
                    request.FinishedAt = DateTimeOffset.UtcNow;
                    request.Error =
                        "Local Codex run was interrupted by an API server restart. "
                        + "Verify the selected machine has no orphaned Codex process before retrying.";
                    foreach (var run in request.Runs.Where(x =>
                                 x.Status is QueueStatus.Running
                                     or QueueStatus.CancelRequested
                                     or QueueStatus.UsageLimited))
                    {
                        run.Status = QueueStatus.Failed;
                        run.FinishedAt = request.FinishedAt;
                        run.Error = request.Error;
                    }

                    repairedRequests = true;
                    continue;
                }

                if (request.Status == QueueStatus.Running)
                {
                    request.Status = QueueStatus.Queued;
                    request.StartedAt = null;
                    request.FinishedAt = null;
                    request.Error = null;
                    request.RetryAfter = null;
                    request.RetryReason = null;
                    request.AvailableModel = null;
                    foreach (var run in request.Runs)
                    {
                        if (run.Status is QueueStatus.Running or QueueStatus.CancelRequested or QueueStatus.UsageLimited)
                        {
                            ResetRunForQueue(run);
                        }
                    }

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

            if (interruptedLocalCodexDetected)
            {
                var queuedLocalCodexRequests = await db.Requests
                    .Include(x => x.Runs)
                    .Where(x => x.ExecutionRunner == ExecutionRunner.OpenHandsCli
                        && x.Status == QueueStatus.Queued)
                    .ToArrayAsync(cancellationToken);
                var pausedAt = DateTimeOffset.UtcNow;
                const string pauseReason =
                    "Local Codex request was paused after an API server restart because another Local Codex run "
                    + "may still be active. Verify the selected machines have no orphaned Codex process, "
                    + "then resume this request.";
                foreach (var request in queuedLocalCodexRequests)
                {
                    request.Status = QueueStatus.Failed;
                    request.QueueWaitReason = null;
                    request.FinishedAt = pausedAt;
                    request.Error = pauseReason;
                    foreach (var run in request.Runs.Where(x =>
                                 x.Status is QueueStatus.Queued
                                     or QueueStatus.Running
                                     or QueueStatus.CancelRequested
                                     or QueueStatus.UsageLimited))
                    {
                        run.Status = QueueStatus.Failed;
                        run.FinishedAt = pausedAt;
                        run.Error = pauseReason;
                    }
                }

                repairedRequests |= queuedLocalCodexRequests.Length > 0;
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

    private static async Task EnsureQueueTabsTableAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "QueueTabs" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_QueueTabs" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "Name" TEXT COLLATE NOCASE NOT NULL,
                "CodexSessionId" TEXT NULL,
                "OpenHandsConversationId" TEXT NULL,
                "LocalCodexSessionId" TEXT NULL,
                "LocalCodexSessionRouteKey" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "DeletedAt" TEXT NULL,
                CONSTRAINT "FK_QueueTabs_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE
            )
            """,
            cancellationToken);
    }

    private static async Task EnsureAiProviderProfilesTableAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "AiProviderProfiles" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AiProviderProfiles" PRIMARY KEY,
                "Name" TEXT COLLATE NOCASE NOT NULL,
                "Source" TEXT NOT NULL,
                "LocalAiServerType" TEXT NOT NULL DEFAULT 'Ollama',
                "BaseUrl" TEXT NOT NULL,
                "ModelDiscoveryMode" TEXT NOT NULL,
                "ApiKeyEnvironmentVariable" TEXT NULL,
                "Enabled" INTEGER NOT NULL,
                "MaximumConcurrency" INTEGER NOT NULL DEFAULT 1,
                "ConfiguredContextWindow" INTEGER NULL,
                "DefaultModel" TEXT NULL,
                "ServerMachineId" TEXT NULL REFERENCES "Machines" ("Id") ON DELETE SET NULL,
                "LastHealthStatus" TEXT NOT NULL DEFAULT 'Unknown',
                "LastHealthAt" TEXT NULL,
                "LastHealthError" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            )
            """,
            cancellationToken);
    }

    private static async Task EnsureAiProviderProfileIndexesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_AiProviderProfiles_Name\" ON \"AiProviderProfiles\" (\"Name\")",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_AiProviderProfiles_Source_BaseUrl\" ON \"AiProviderProfiles\" (\"Source\", \"BaseUrl\")",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_AiProviderProfiles_ServerMachineId\" ON \"AiProviderProfiles\" (\"ServerMachineId\")",
            cancellationToken);
    }

    private static async Task EnsureDefaultLocalProviderProfileAsync(
        AppDbContext db,
        IConfiguration? configuration,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (await db.AiProviderProfiles.AnyAsync(x => x.Source == AiProviderSource.Local, cancellationToken))
        {
            return;
        }

        const string fallbackBaseUrl = "http://localhost:11434/v1";
        var configuredBaseUrl = Environment.GetEnvironmentVariable("CQ_LOCAL_AI_BASE_URL");
        if (string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            configuredBaseUrl = configuration?["LocalAi:BaseUrl"];
        }

        var requestedBaseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? fallbackBaseUrl
            : configuredBaseUrl.Trim();
        if (!AiProviderService.TryNormalizeBaseUrl(
                AiProviderSource.Local,
                requestedBaseUrl,
                out var normalizedBaseUrl,
                out var baseUrlError))
        {
            logger.LogWarning(
                "Ignoring invalid Local AI base URL while creating the default profile: {Reason}",
                baseUrlError);
            normalizedBaseUrl = fallbackBaseUrl;
        }

        // GPT-OSS advertises a 128K context window. Individual requests and
        // model metadata still enforce lower limits where applicable.
        const int defaultContextWindow = 131_072;
        var configuredContext = Environment.GetEnvironmentVariable("CQ_LOCAL_AI_CONTEXT_WINDOW");
        if (string.IsNullOrWhiteSpace(configuredContext))
        {
            configuredContext = configuration?["LocalAi:ConfiguredContextWindow"];
        }

        var contextWindow = defaultContextWindow;
        if (!string.IsNullOrWhiteSpace(configuredContext)
            && (!int.TryParse(configuredContext, out contextWindow) || contextWindow <= 0))
        {
            logger.LogWarning(
                "Ignoring invalid Local AI configured context window {ConfiguredContextWindow}; using {DefaultContextWindow}.",
                configuredContext,
                defaultContextWindow);
            contextWindow = defaultContextWindow;
        }

        var configuredDefaultModel = Environment.GetEnvironmentVariable("CQ_LOCAL_AI_DEFAULT_MODEL");
        if (string.IsNullOrWhiteSpace(configuredDefaultModel))
        {
            configuredDefaultModel = configuration?["LocalAi:DefaultModel"];
        }

        string? defaultModel = null;
        if (!string.IsNullOrWhiteSpace(configuredDefaultModel))
        {
            try
            {
                defaultModel = AiProviderService.QualifyModel(
                    AiProviderSource.Local,
                    configuredDefaultModel);
            }
            catch (ArgumentException)
            {
                logger.LogWarning(
                    "Ignoring an invalid Local AI default model identifier while creating the default profile.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        db.AiProviderProfiles.Add(new AiProviderProfile
        {
            Name = "Local Ollama",
            Source = AiProviderSource.Local,
            LocalAiServerType = LocalAiServerType.Ollama,
            BaseUrl = normalizedBaseUrl,
            ModelDiscoveryMode = ModelDiscoveryMode.Auto,
            ApiKeyEnvironmentVariable = null,
            Enabled = true,
            MaximumConcurrency = 1,
            ConfiguredContextWindow = contextWindow,
            DefaultModel = defaultModel,
            LastHealthStatus = ProviderHealthStatus.Unknown,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureQueueTabIndexesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        // Create the replacement before dropping the legacy index so uniqueness
        // remains enforced even if startup is interrupted between schema statements.
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_QueueTabs_ProjectId_ActiveName\" ON \"QueueTabs\" (\"ProjectId\", \"Name\") WHERE \"DeletedAt\" IS NULL",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "DROP INDEX IF EXISTS \"IX_QueueTabs_ProjectId_Name\"",
            cancellationToken);
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
            ExecutionRunner = request.ExecutionRunner,
            ProviderProfileId = request.ProviderProfileId,
            ProviderProfileName = request.ProviderProfile?.Name,
            ProviderSource = request.ProviderProfile?.Source,
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
    }

    private static void ClearRequestRetryState(CodexRequest request)
    {
        request.RetryAfter = null;
        request.RetryReason = null;
        request.AvailableModel = null;
        request.QueueWaitReason = null;
    }

}
