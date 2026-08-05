using CodexQueue.Api.Data;
using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CodexQueue.Api.Endpoints;

public static class ApiEndpoints
{
    private const string LocalCodexAttachmentsUnavailableError =
        "Attachments are not available for Local Codex in this release because their "
        + "project-scoped transfer path has not yet been validated against symbolic-link escapes.";

    public static void MapCodexQueueApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));

        api.MapGet("/config", (IConfiguration configuration) =>
        {
            var token = configuration["CQ_API_TOKEN"] ?? configuration["Security:ApiToken"];
            var configuredModels = configuration.GetSection("Codex:Models").Get<string[]>();
            var models = configuredModels is { Length: > 0 }
                ? configuredModels.Select(ParseModelOption).ToArray()
                : DefaultModels;
            return new ApiConfigDto(!string.IsNullOrWhiteSpace(token), models);
        });

        api.MapPost("/auth/verify", () => Results.Ok(new { ok = true }));

        api.MapGet("/machines", async (AppDbContext db, CancellationToken cancellationToken) =>
            await db.Machines
                .AsNoTracking()
                .OrderBy(x => x.Name)
                .Select(x => x.ToDto())
                .ToArrayAsync(cancellationToken));

        api.MapPost("/machines", async (SaveMachineRequest input, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var validation = Validate(input);
            if (validation is not null)
            {
                return Results.BadRequest(new { error = validation });
            }

            var machine = new TargetMachine();
            Apply(input, machine);
            db.Machines.Add(machine);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/machines/{machine.Id}", machine.ToDto());
        });

        api.MapPut("/machines/{id:guid}", async (Guid id, SaveMachineRequest input, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var machine = await db.Machines.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (machine is null)
            {
                return Results.NotFound();
            }

            var validation = Validate(input);
            if (validation is not null)
            {
                return Results.BadRequest(new { error = validation });
            }

            if (await db.Requests.AnyAsync(
                    x => x.MachineId == id
                        && x.ExecutionRunner == ExecutionRunner.OpenHandsCli
                        && x.DeletedAt == null
                        && x.ArchivedAt == null
                        && (x.Status == QueueStatus.Queued
                            || x.Status == QueueStatus.Running
                            || x.Status == QueueStatus.CancelRequested
                            || x.Status == QueueStatus.UsageLimited),
                    cancellationToken))
            {
                return Results.Conflict(new
                {
                    error = "Finish or cancel active Local Codex requests before changing this machine.",
                });
            }

            var previousExecutionContext = (
                machine.Kind,
                machine.Host,
                machine.Port,
                machine.UserName,
                machine.SshKeyPath,
                machine.WorkingRoot,
                machine.Platform);
            Apply(input, machine);
            machine.UpdatedAt = DateTimeOffset.UtcNow;
            var executionContextChanged = previousExecutionContext != (
                machine.Kind,
                machine.Host,
                machine.Port,
                machine.UserName,
                machine.SshKeyPath,
                machine.WorkingRoot,
                machine.Platform);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            if (executionContextChanged)
            {
                await db.QueueTabs
                    .Where(tab => tab.DeletedAt == null
                        && (tab.OpenHandsConversationId != null || tab.LocalCodexSessionId != null)
                        && db.Projects.Any(project =>
                            project.Id == tab.ProjectId
                            && project.MachineId == id))
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(tab => tab.OpenHandsConversationId, (string?)null)
                            .SetProperty(tab => tab.LocalCodexSessionId, (string?)null)
                            .SetProperty(tab => tab.LocalCodexSessionRouteKey, (string?)null)
                            .SetProperty(tab => tab.UpdatedAt, machine.UpdatedAt),
                        cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(machine.ToDto());
        });

        api.MapDelete("/machines/{id:guid}", async (Guid id, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var machine = await db.Machines.Include(x => x.Projects).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (machine is null)
            {
                return Results.NotFound();
            }

            if (machine.Projects.Count > 0)
            {
                return Results.BadRequest(new { error = "Remove projects from this machine before deleting it." });
            }

            db.Machines.Remove(machine);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        api.MapPost("/machines/{id:guid}/test", async (Guid id, AppDbContext db, ITargetCommandRunner runner, CancellationToken cancellationToken) =>
        {
            var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (machine is null)
            {
                return Results.NotFound();
            }

            try
            {
                var output = "";
                var result = await runner.TestMachineAsync(machine, chunk =>
                {
                    output += chunk;
                    return Task.CompletedTask;
                }, cancellationToken);
                return Results.Ok(new MachineTestDto(result.Success, output));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return Results.Ok(new MachineTestDto(false, ex.Message));
            }
        });

        api.MapGet("/machines/{id:guid}/resources", async (
            Guid id,
            AppDbContext db,
            IMachineResourceTelemetryService telemetryService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var machine = await db.Machines
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (machine is null)
            {
                return Results.NotFound();
            }

            // Resource readings are sampled on demand and must not be replayed from a
            // browser/proxy cache as if they were current.
            httpContext.Response.Headers.CacheControl = "no-store";
            var telemetry = await telemetryService.CollectAsync(machine, cancellationToken);
            return Results.Ok(new MachineResourceTelemetryDto(
                machine.Id,
                machine.Name,
                telemetry.Available,
                telemetry.Error,
                telemetry.CpuUsagePercent,
                telemetry.MemoryUsagePercent,
                telemetry.MemoryUsedBytes,
                telemetry.MemoryTotalBytes,
                telemetry.CpuTemperatureCelsius,
                telemetry.SystemTemperatureCelsius,
                telemetry.SystemPowerWatts,
                telemetry.SystemPowerSource,
                telemetry.Gpus.Select(gpu => new GpuResourceTelemetryDto(
                    gpu.Index,
                    gpu.Name,
                    gpu.UtilizationPercent,
                    gpu.MemoryUsagePercent,
                    gpu.MemoryUsedBytes,
                    gpu.MemoryTotalBytes,
                    gpu.TemperatureCelsius,
                    gpu.PowerWatts)).ToArray(),
                telemetry.CollectedAt,
                telemetry.CpuName,
                telemetry.MemoryName));
        });

        api.MapGet("/machines/{id:guid}/local-codex/test", async (
            Guid id,
            Guid? providerProfileId,
            string? model,
            AppDbContext db,
            IAiProviderService providers,
            ITargetCommandRunner runner,
            CancellationToken cancellationToken) =>
        {
            var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (machine is null)
            {
                return Results.NotFound();
            }

            string? localAiBaseUrl = null;
            string? selectedModel = null;
            if (providerProfileId is not null)
            {
                var profile = await db.AiProviderProfiles
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == providerProfileId, cancellationToken);
                if (profile is null)
                {
                    return Results.BadRequest(new { error = "Selected Local AI Server profile was not found." });
                }

                var validation = providers.Validate(profile);
                if (profile.Source != AiProviderSource.Local
                    || !profile.Enabled
                    || !validation.IsValid
                    || validation.NormalizedBaseUrl is null)
                {
                    var detail = validation.IsValid
                        ? ""
                        : " " + string.Join(" ", validation.Errors);
                    return Results.BadRequest(new
                    {
                        error = "Selected Local AI Server profile is unavailable or invalid." + detail,
                    });
                }

                try
                {
                    localAiBaseUrl = validation.NormalizedBaseUrl;
                    var requestedModel = string.IsNullOrWhiteSpace(model)
                        ? validation.NormalizedDefaultModel
                        : model;
                    if (!string.IsNullOrWhiteSpace(requestedModel))
                    {
                        var discovery = await providers.DiscoverModelsAsync(profile, cancellationToken);
                        selectedModel = AiProviderService.FindLocalModel(
                                discovery.Models,
                                requestedModel)
                            ?.Model
                            ?? AiProviderService.QualifyModel(AiProviderSource.Local, requestedModel);
                    }
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }
            else if (!string.IsNullOrWhiteSpace(model))
            {
                return Results.BadRequest(new
                {
                    error = "A Local AI Server profile is required when checking a selected model.",
                });
            }

            var result = await runner.TestLocalCodexAsync(
                machine,
                cancellationToken,
                localAiBaseUrl,
                selectedModel);
            return Results.Ok(new LocalCodexMachineCheckDto(
                result.Available,
                result.Version,
                RequiresWsl: false,
                result.Message,
                result.TargetLocalAiChecked,
                result.TargetLocalAiReachable,
                result.TargetSelectedModelAvailable,
                result.TargetLocalAiMessage));
        });

        api.MapGet("/provider-profiles", async (AppDbContext db, CancellationToken cancellationToken) =>
            await db.AiProviderProfiles
                .AsNoTracking()
                .OrderBy(x => x.Name)
                .Select(x => x.ToDto())
                .ToArrayAsync(cancellationToken));

        api.MapPost("/provider-profiles", async (
            SaveAiProviderProfileRequest input,
            AppDbContext db,
            IAiProviderService providers,
            CancellationToken cancellationToken) =>
        {
            if (input.ServerMachineId is { } serverMachineId
                && !await db.Machines.AnyAsync(x => x.Id == serverMachineId, cancellationToken))
            {
                return Results.BadRequest(new { error = "Selected AI server machine does not exist." });
            }

            var profile = new AiProviderProfile();
            Apply(input, profile);
            var validation = providers.Validate(profile);
            if (!validation.IsValid)
            {
                return Results.BadRequest(new { error = string.Join(" ", validation.Errors) });
            }

            if (await db.AiProviderProfiles.AnyAsync(
                    x => x.Name.ToUpper() == profile.Name.ToUpper(),
                    cancellationToken))
            {
                return Results.Conflict(new { error = "A provider profile with this name already exists." });
            }

            ApplyNormalizedProviderValues(profile, validation);
            db.AiProviderProfiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/provider-profiles/{profile.Id}", profile.ToDto());
        });

        api.MapPut("/provider-profiles/{id:guid}", async (
            Guid id,
            SaveAiProviderProfileRequest input,
            AppDbContext db,
            IAiProviderService providers,
            CancellationToken cancellationToken) =>
        {
            var profile = await db.AiProviderProfiles.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (profile is null)
            {
                return Results.NotFound();
            }

            var hasActiveRequests = await db.Requests.AnyAsync(
                x => x.ProviderProfileId == id
                    && x.DeletedAt == null
                    && x.ArchivedAt == null
                    && (x.Status == QueueStatus.Queued
                        || x.Status == QueueStatus.Running
                        || x.Status == QueueStatus.CancelRequested
                        || x.Status == QueueStatus.UsageLimited),
                cancellationToken);
            if (hasActiveRequests)
            {
                return Results.Conflict(new
                {
                    error = "Finish or cancel active requests before changing this provider profile.",
                });
            }

            if (input.ServerMachineId is { } serverMachineId
                && !await db.Machines.AnyAsync(x => x.Id == serverMachineId, cancellationToken))
            {
                return Results.BadRequest(new { error = "Selected AI server machine does not exist." });
            }

            Apply(input, profile);
            var validation = providers.Validate(profile);
            if (!validation.IsValid)
            {
                return Results.BadRequest(new { error = string.Join(" ", validation.Errors) });
            }

            if (await db.AiProviderProfiles.AnyAsync(
                    x => x.Id != id && x.Name.ToUpper() == profile.Name.ToUpper(),
                    cancellationToken))
            {
                return Results.Conflict(new { error = "A provider profile with this name already exists." });
            }

            ApplyNormalizedProviderValues(profile, validation);
            profile.LastHealthStatus = ProviderHealthStatus.Unknown;
            profile.LastHealthAt = null;
            profile.LastHealthError = null;
            profile.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(profile.ToDto());
        });

        api.MapDelete("/provider-profiles/{id:guid}", async (
            Guid id,
            AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            var profile = await db.AiProviderProfiles.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (profile is null)
            {
                return Results.NotFound();
            }

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (await db.Requests.AnyAsync(x => x.ProviderProfileId == id, cancellationToken))
            {
                return Results.Conflict(new
                {
                    error = "This provider profile is retained because request history references it. Disable it instead.",
                });
            }

            await db.Projects
                .Where(x => x.DefaultLocalProviderProfileId == id)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.DefaultExecutionRunner, ExecutionRunner.CodexCli)
                        .SetProperty(x => x.DefaultLocalProviderProfileId, (Guid?)null)
                        .SetProperty(x => x.DefaultLocalModel, (string?)null)
                        .SetProperty(x => x.DefaultLocalModelEffort, (string?)null)
                        .SetProperty(x => x.DefaultLocalModelSpeed, (string?)null)
                        .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
                    cancellationToken);
            db.AiProviderProfiles.Remove(profile);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.NoContent();
        });

        api.MapGet("/provider-profiles/{id:guid}/models", async (
            Guid id,
            bool? refresh,
            AppDbContext db,
            IAiProviderService providers,
            CancellationToken cancellationToken) =>
        {
            var profile = await db.AiProviderProfiles.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (profile is null)
            {
                return Results.NotFound();
            }

            var discovery = await providers.DiscoverModelsAsync(
                profile,
                cancellationToken,
                forceRefresh: refresh == true);
            profile.LastHealthStatus = discovery.HealthStatus;
            profile.LastHealthAt = discovery.CheckedAt;
            profile.LastHealthError = Truncate(discovery.Error, 2_000);
            await db.SaveChangesAsync(cancellationToken);
            var warning = providers.GetContextWarning(profile);
            return Results.Ok(new AiProviderModelsDto(
                profile.Id,
                discovery.HealthStatus == ProviderHealthStatus.Healthy,
                discovery.HealthStatus,
                discovery.Error,
                discovery.CheckedAt,
                profile.ConfiguredContextWindow,
                warning?.Message,
                discovery.Models.Select(x => new AiProviderModelDto(
                    x.Model,
                    x.Name,
                    x.MaximumContextWindow,
                    x.SupportsTools,
                    x.SupportsReasoning,
                    x.SupportsReasoningEffort,
                    x.ToolSupportKnown)).ToArray()));
        });

        api.MapGet("/provider-profiles/{id:guid}/resources", async (
            Guid id,
            AppDbContext db,
            IMachineResourceTelemetryService telemetryService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var profile = await db.AiProviderProfiles
                .AsNoTracking()
                .Include(x => x.ServerMachine)
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (profile is null)
            {
                return Results.NotFound();
            }

            var machine = profile.ServerMachine;
            if (machine is null)
            {
                return Results.BadRequest(new { error = "Select the machine that hosts this AI server before viewing its resources." });
            }

            httpContext.Response.Headers.CacheControl = "no-store";
            var telemetry = await telemetryService.CollectAsync(machine, cancellationToken);
            return Results.Ok(new MachineResourceTelemetryDto(
                machine.Id,
                machine.Name,
                telemetry.Available,
                telemetry.Error,
                telemetry.CpuUsagePercent,
                telemetry.MemoryUsagePercent,
                telemetry.MemoryUsedBytes,
                telemetry.MemoryTotalBytes,
                telemetry.CpuTemperatureCelsius,
                telemetry.SystemTemperatureCelsius,
                telemetry.SystemPowerWatts,
                telemetry.SystemPowerSource,
                telemetry.Gpus.Select(gpu => new GpuResourceTelemetryDto(
                    gpu.Index,
                    gpu.Name,
                    gpu.UtilizationPercent,
                    gpu.MemoryUsagePercent,
                    gpu.MemoryUsedBytes,
                    gpu.MemoryTotalBytes,
                    gpu.TemperatureCelsius,
                    gpu.PowerWatts)).ToArray(),
                telemetry.CollectedAt,
                telemetry.CpuName,
                telemetry.MemoryName));
        });

        api.MapGet("/machines/{id:guid}/usage", async (Guid id, AppDbContext db, ITargetCommandRunner runner, CancellationToken cancellationToken) =>
        {
            var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (machine is null)
            {
                return Results.NotFound();
            }

            try
            {
                var result = await runner.ReadRateLimitsAsync(machine, cancellationToken);
                var snapshot = ParseRateLimits(result.Output);
                return Results.Ok(new MachineRateLimitsDto(machine.Id, machine.Name, snapshot is not null, snapshot is null ? "Codex did not return rate-limit data." : null, snapshot?.Limits ?? []));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                return Results.Ok(new MachineRateLimitsDto(machine.Id, machine.Name, false, ex.Message, []));
            }
        });

        api.MapGet("/machines/{id:guid}/folders", async (Guid id, string? path, AppDbContext db, IProjectFileService files, CancellationToken cancellationToken) =>
        {
            var machine = await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (machine is null)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await files.ListMachineFoldersAsync(machine, path, cancellationToken));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapGet("/projects", async (AppDbContext db, CancellationToken cancellationToken) =>
            await db.Projects
                .Include(x => x.Machine)
                .AsNoTracking()
                .OrderBy(x => x.Name)
                .Select(x => x.ToDto())
                .ToArrayAsync(cancellationToken));

        api.MapGet("/queue-tabs", async (Guid? projectId, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var query = db.QueueTabs
                .AsNoTracking()
                .Where(x => x.DeletedAt == null);
            if (projectId.HasValue)
            {
                query = query.Where(x => x.ProjectId == projectId.Value);
            }

            var tabs = await query.ToArrayAsync(cancellationToken);
            return tabs
                .OrderBy(x => x.CreatedAt)
                .ThenBy(x => x.Name)
                .Select(x => x.ToDto())
                .ToArray();
        });

        api.MapPost("/queue-tabs", async (CreateQueueTabRequest input, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var name = NormalizeQueueTabName(input.Name, out var validationError);
            if (validationError is not null)
            {
                return Results.BadRequest(new { error = validationError });
            }

            if (!await db.Projects.AnyAsync(x => x.Id == input.ProjectId, cancellationToken))
            {
                return Results.BadRequest(new { error = "Project does not exist." });
            }

            var normalizedName = name.ToUpperInvariant();
            if (await db.QueueTabs.AnyAsync(x =>
                    x.ProjectId == input.ProjectId
                    && x.DeletedAt == null
                    && x.Name.ToUpper() == normalizedName,
                cancellationToken))
            {
                return Results.Conflict(new { error = "A tab with this name already exists." });
            }

            var tab = new QueueTab
            {
                ProjectId = input.ProjectId,
                Name = name,
            };
            db.QueueTabs.Add(tab);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/queue-tabs/{tab.Id}", tab.ToDto());
        });

        api.MapPut("/queue-tabs/{id:guid}", async (Guid id, RenameQueueTabRequest input, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var tab = await db.QueueTabs.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, cancellationToken);
            if (tab is null)
            {
                return Results.NotFound();
            }

            var name = NormalizeQueueTabName(input.Name, out var validationError);
            if (validationError is not null)
            {
                return Results.BadRequest(new { error = validationError });
            }

            var normalizedName = name.ToUpperInvariant();
            if (await db.QueueTabs.AnyAsync(x =>
                    x.ProjectId == tab.ProjectId
                    && x.Id != tab.Id
                    && x.DeletedAt == null
                    && x.Name.ToUpper() == normalizedName,
                cancellationToken))
            {
                return Results.Conflict(new { error = "A tab with this name already exists." });
            }

            tab.Name = name;
            tab.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(tab.ToDto());
        });

        api.MapDelete("/queue-tabs/{id:guid}", async (Guid id, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var tab = await db.QueueTabs.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, cancellationToken);
            if (tab is null)
            {
                return Results.NotFound();
            }

            var hasActiveRequests = await db.Requests.AnyAsync(x =>
                x.QueueTabId == id
                && x.DeletedAt == null
                && x.ArchivedAt == null
                && (x.Status == QueueStatus.Queued
                    || x.Status == QueueStatus.Running
                    || x.Status == QueueStatus.CancelRequested
                    || x.Status == QueueStatus.UsageLimited),
                cancellationToken);
            if (hasActiveRequests)
            {
                return Results.Conflict(new { error = "Finish, cancel, or remove active requests before deleting this tab." });
            }

            tab.CodexSessionId = null;
            tab.OpenHandsConversationId = null;
            tab.LocalCodexSessionId = null;
            tab.LocalCodexSessionRouteKey = null;
            tab.DeletedAt = DateTimeOffset.UtcNow;
            tab.UpdatedAt = tab.DeletedAt.Value;
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        api.MapPost("/projects", async (
            SaveProjectRequest input,
            AppDbContext db,
            IAiProviderService providers,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Path))
            {
                return Results.BadRequest(new { error = "Project name and path are required." });
            }

            if (!await db.Machines.AnyAsync(x => x.Id == input.MachineId, cancellationToken))
            {
                return Results.BadRequest(new { error = "Machine does not exist." });
            }

            var localDefaults = await NormalizeLocalProjectDefaultsAsync(input, db, providers, cancellationToken);
            if (localDefaults.Error is not null)
            {
                return Results.BadRequest(new { error = localDefaults.Error });
            }

            var project = new Project
            {
                Name = input.Name.Trim(),
                Path = input.Path.Trim(),
                MachineId = input.MachineId,
                DefaultModel = NormalizeOptional(input.DefaultModel),
                DefaultModelEffort = NormalizeEffort(input.DefaultModelEffort, input.DefaultModel),
                DefaultModelSpeed = NormalizeOptionalSpeed(input.DefaultModelSpeed),
                DefaultCommitModel = NormalizeOptional(input.DefaultCommitModel),
                DefaultCommitModelEffort = NormalizeEffort(input.DefaultCommitModelEffort, input.DefaultCommitModel ?? input.DefaultModel),
                DefaultCommitModelSpeed = NormalizeOptionalSpeed(input.DefaultCommitModelSpeed),
                DefaultGenerateCommit = input.DefaultPermissionMode != PermissionMode.ReadOnly && (input.DefaultGenerateCommit ?? true),
                DefaultSeparateCommitSession = input.DefaultPermissionMode != PermissionMode.ReadOnly && (input.DefaultSeparateCommitSession ?? false),
                DefaultPermissionMode = input.DefaultPermissionMode ?? PermissionMode.ApproveForMe,
                DefaultInternetSearchEnabled = input.DefaultInternetSearchEnabled ?? false,
                DefaultCommitExecutionRunner = input.DefaultCommitExecutionRunner,
                DefaultCommitLocalProviderProfileId = input.DefaultCommitLocalProviderProfileId,
                DefaultExecutionRunner = input.DefaultExecutionRunner ?? ExecutionRunner.CodexCli,
                DefaultLocalProviderProfileId = localDefaults.ProviderProfileId,
                DefaultLocalModel = localDefaults.Model,
                DefaultLocalModelEffort = localDefaults.Effort,
                DefaultLocalModelSpeed = localDefaults.ContextWindow,
                SeparateQueuesByTab = input.SeparateQueuesByTab ?? false
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync(cancellationToken);
            await db.Entry(project).Reference(x => x.Machine).LoadAsync(cancellationToken);
            return Results.Created($"/api/projects/{project.Id}", project.ToDto());
        });

        api.MapPut("/projects/{id:guid}", async (
            Guid id,
            SaveProjectRequest input,
            AppDbContext db,
            IQueueCoordinator queue,
            IAiProviderService providers,
            CancellationToken cancellationToken) =>
        {
            var project = await db.Projects.Include(x => x.Machine).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (project is null)
            {
                return Results.NotFound();
            }

            if (!await db.Machines.AnyAsync(x => x.Id == input.MachineId, cancellationToken))
            {
                return Results.BadRequest(new { error = "Machine does not exist." });
            }

            if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Path))
            {
                return Results.BadRequest(new { error = "Project name and path are required." });
            }

            var hasLocalDefaultsInput = HasLocalProjectDefaultsInput(input);
            var requestedDefaultRunner = hasLocalDefaultsInput
                ? input.DefaultExecutionRunner ?? project.DefaultExecutionRunner
                : project.DefaultExecutionRunner;
            if (!Enum.IsDefined(requestedDefaultRunner))
            {
                return Results.BadRequest(new { error = "Default execution runner is invalid." });
            }

            var mergedLocalDefaultsInput = MergeLocalProjectDefaultsInput(
                input,
                project,
                requestedDefaultRunner);
            var localValuesChanged = hasLocalDefaultsInput
                && (mergedLocalDefaultsInput.DefaultLocalProviderProfileId != project.DefaultLocalProviderProfileId
                    || !string.Equals(
                        NormalizeOptional(mergedLocalDefaultsInput.DefaultLocalModel),
                        project.DefaultLocalModel,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        NormalizeLocalCodexReasoningEffort(mergedLocalDefaultsInput.DefaultLocalModelEffort),
                        project.DefaultLocalModelEffort,
                        StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(mergedLocalDefaultsInput.DefaultLocalModelEffort)
                        && NormalizeLocalCodexReasoningEffort(mergedLocalDefaultsInput.DefaultLocalModelEffort) is null)
                    || !string.Equals(
                        NormalizeLocalCodexContextWindow(mergedLocalDefaultsInput.DefaultLocalModelSpeed)?.ToString(),
                        project.DefaultLocalModelSpeed,
                        StringComparison.Ordinal)
                    || (!string.IsNullOrWhiteSpace(mergedLocalDefaultsInput.DefaultLocalModelSpeed)
                        && NormalizeLocalCodexContextWindow(mergedLocalDefaultsInput.DefaultLocalModelSpeed) is null));
            var mustValidateLocalDefaults = localValuesChanged
                || (requestedDefaultRunner == ExecutionRunner.OpenHandsCli
                    && project.DefaultExecutionRunner != ExecutionRunner.OpenHandsCli);
            var localDefaults = mustValidateLocalDefaults
                ? await NormalizeLocalProjectDefaultsAsync(
                    mergedLocalDefaultsInput,
                    db,
                    providers,
                    cancellationToken,
                    requestedDefaultRunner)
                : new NormalizedLocalProjectDefaults(
                    project.DefaultLocalProviderProfileId,
                    project.DefaultLocalModel,
                    project.DefaultLocalModelEffort,
                    project.DefaultLocalModelSpeed,
                    null);
            if (localDefaults.Error is not null)
            {
                return Results.BadRequest(new { error = localDefaults.Error });
            }

            var normalizedPath = input.Path.Trim();
            var executionContextChanged = project.MachineId != input.MachineId
                || !string.Equals(project.Path, normalizedPath, StringComparison.Ordinal);
            if (executionContextChanged && await db.Requests.AnyAsync(x =>
                    x.ProjectId == project.Id
                    && x.DeletedAt == null
                    && x.ArchivedAt == null
                    && (x.Status == QueueStatus.Queued
                        || x.Status == QueueStatus.Running
                        || x.Status == QueueStatus.CancelRequested
                        || x.Status == QueueStatus.UsageLimited),
                cancellationToken))
            {
                return Results.Conflict(new { error = "Finish or cancel active requests before changing the project machine or path." });
            }

            var queueModeChanged = project.SeparateQueuesByTab != (input.SeparateQueuesByTab ?? false);
            if (queueModeChanged)
            {
                var modeChange = await queue.ChangeQueueModeAsync(project.Id, input.SeparateQueuesByTab ?? false, cancellationToken);
                if (modeChange == QueueModeChangeResult.ActiveRequests)
                {
                    return Results.Conflict(new { error = "Finish or cancel running requests before changing the queue mode." });
                }
                if (modeChange == QueueModeChangeResult.NotFound)
                {
                    return Results.NotFound();
                }

                await db.Entry(project).ReloadAsync(cancellationToken);
            }

            project.Name = input.Name.Trim();
            project.Path = normalizedPath;
            project.MachineId = input.MachineId;
            project.DefaultModel = NormalizeOptional(input.DefaultModel);
            project.DefaultModelEffort = NormalizeEffort(input.DefaultModelEffort, input.DefaultModel);
            project.DefaultModelSpeed = NormalizeOptionalSpeed(input.DefaultModelSpeed);
            project.DefaultCommitModel = NormalizeOptional(input.DefaultCommitModel);
            project.DefaultCommitModelEffort = NormalizeEffort(input.DefaultCommitModelEffort, input.DefaultCommitModel ?? input.DefaultModel);
            project.DefaultCommitModelSpeed = NormalizeOptionalSpeed(input.DefaultCommitModelSpeed);
            project.DefaultGenerateCommit = input.DefaultPermissionMode != PermissionMode.ReadOnly && (input.DefaultGenerateCommit ?? true);
            project.DefaultSeparateCommitSession = input.DefaultPermissionMode != PermissionMode.ReadOnly && (input.DefaultSeparateCommitSession ?? false);
            project.DefaultPermissionMode = input.DefaultPermissionMode ?? PermissionMode.ApproveForMe;
            project.DefaultInternetSearchEnabled = input.DefaultInternetSearchEnabled ?? false;
            project.DefaultCommitExecutionRunner = input.DefaultCommitExecutionRunner;
            project.DefaultCommitLocalProviderProfileId = input.DefaultCommitLocalProviderProfileId;
            project.DefaultExecutionRunner = requestedDefaultRunner;
            project.DefaultLocalProviderProfileId = localDefaults.ProviderProfileId;
            project.DefaultLocalModel = localDefaults.Model;
            project.DefaultLocalModelEffort = localDefaults.Effort;
            project.DefaultLocalModelSpeed = localDefaults.ContextWindow;
            project.SeparateQueuesByTab = input.SeparateQueuesByTab ?? false;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            if (executionContextChanged)
            {
                await db.QueueTabs
                    .Where(x => x.ProjectId == project.Id
                        && x.DeletedAt == null
                        && (x.CodexSessionId != null
                            || x.OpenHandsConversationId != null
                            || x.LocalCodexSessionId != null))
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(x => x.CodexSessionId, (string?)null)
                            .SetProperty(x => x.OpenHandsConversationId, (string?)null)
                            .SetProperty(x => x.LocalCodexSessionId, (string?)null)
                            .SetProperty(x => x.LocalCodexSessionRouteKey, (string?)null)
                            .SetProperty(x => x.UpdatedAt, project.UpdatedAt),
                        cancellationToken);
            }
            await db.SaveChangesAsync(cancellationToken);
            await db.Entry(project).Reference(x => x.Machine).LoadAsync(cancellationToken);
            return Results.Ok(project.ToDto());
        });

        api.MapDelete("/projects/{id:guid}", async (Guid id, IQueueCoordinator queue, CancellationToken cancellationToken) =>
        {
            return await queue.RemoveProjectAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        });

        api.MapGet("/projects/{id:guid}/tree", async (Guid id, string? path, AppDbContext db, IProjectFileService files, CancellationToken cancellationToken) =>
        {
            var project = await db.Projects.Include(x => x.Machine).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (project is null)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await files.ListAsync(project, path, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapGet("/projects/{id:guid}/file", async (Guid id, string path, AppDbContext db, IProjectFileService files, CancellationToken cancellationToken) =>
        {
            var project = await db.Projects.Include(x => x.Machine).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (project is null)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await files.ReadAsync(project, path, cancellationToken));
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapGet("/projects/{id:guid}/git/status", async (Guid id, AppDbContext db, ITargetCommandRunner runner, CancellationToken cancellationToken, bool summary = false) =>
        {
            var project = await LoadProjectWithMachineAsync(id, db, cancellationToken);
            if (project is null)
            {
                return Results.NotFound();
            }

            if (project.Machine is null)
            {
                return Results.BadRequest(new { error = "Project machine is missing." });
            }

            try
            {
                var statusResult = await runner.RunShellAsync(
                    project.Machine,
                    project.Path,
                    "git status --porcelain=v1 -b --untracked-files=all -- .",
                    _ => Task.CompletedTask,
                    cancellationToken);

                if (!statusResult.Success)
                {
                    return Results.BadRequest(new { error = StripCommandPreview(statusResult.Output).Trim() });
                }

                var statusOutput = StripCommandPreview(statusResult.Output);
                var changes = ParseGitChanges(statusOutput, out var branch);
                var diffStat = "";
                if (!summary)
                {
                    var diffStatResult = await runner.RunShellAsync(
                        project.Machine,
                        project.Path,
                        "git diff --stat --no-ext-diff -- .; git diff --cached --stat --no-ext-diff -- .",
                        _ => Task.CompletedTask,
                        cancellationToken);
                    diffStat = diffStatResult.Success ? StripCommandPreview(diffStatResult.Output).Trim() : "";
                }
                return Results.Ok(new GitStatusDto(branch, changes.Count == 0, changes, diffStat, statusOutput.Trim()));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapPost("/projects/{id:guid}/git/commit", async (Guid id, GitCommitRequest input, AppDbContext db, ITargetCommandRunner runner, CancellationToken cancellationToken) =>
        {
            var project = await LoadProjectWithMachineAsync(id, db, cancellationToken);
            if (project is null)
            {
                return Results.NotFound();
            }

            if (project.Machine is null)
            {
                return Results.BadRequest(new { error = "Project machine is missing." });
            }

            var message = SanitizeGitCommitMessage(input.Message);
            if (string.IsNullOrWhiteSpace(message))
            {
                return Results.BadRequest(new { error = "Commit message is required." });
            }

            try
            {
                var result = await runner.RunShellAsync(
                    project.Machine,
                    project.Path,
                    GitCommitShellHelper.BuildCommitCommand(project.Machine, message),
                    _ => Task.CompletedTask,
                    cancellationToken);
                var output = StripCommandPreview(result.Output).Trim();
                if (!result.Success)
                {
                    return Results.BadRequest(new { error = string.IsNullOrWhiteSpace(output) ? "Git commit failed." : output });
                }

                var commitInfo = await ReadGitCommitInfoAsync(runner, project.Machine, project.Path, cancellationToken);
                var commitSha = commitInfo.Sha ?? ExtractCommitSha(output);
                var formattedOutput = commitSha is null
                    ? output
                    : GitCommitResultFormatter.Format(commitSha, commitInfo.Message ?? message);
                return Results.Ok(new GitCommitDto(result.Success, formattedOutput, result.ExitCode, result.CommandPreview, commitSha));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapPost("/projects/{id:guid}/git/codex-commit", async (
            Guid id,
            CodexGitCommitRequest input,
            AppDbContext db,
            ITargetCommandRunner runner,
            IAiProviderService providers,
            IQueueAgentRunnerResolver agentRunnerResolver,
            CancellationToken cancellationToken) =>
        {
            var project = await LoadProjectWithMachineAsync(id, db, cancellationToken);
            if (project is null)
            {
                return Results.NotFound();
            }

            if (project.Machine is null)
            {
                return Results.BadRequest(new { error = "Project machine is missing." });
            }

            if (string.IsNullOrWhiteSpace(input.Model))
            {
                return Results.BadRequest(new { error = "Model is required." });
            }

            var executionRunner = input.ExecutionRunner ?? ExecutionRunner.CodexCli;
            RunnerSelectionValidation? localSelection = null;
            string? localEffort = null;
            if (executionRunner == ExecutionRunner.OpenHandsCli)
            {
                localSelection = await ValidateRunnerSelectionAsync(
                    executionRunner,
                    input.ProviderProfileId,
                    input.Model,
                    input.ModelSpeed,
                    PermissionMode.FullAccess,
                    project.Machine,
                    project.Path,
                    db,
                    providers,
                    cancellationToken);
                if (localSelection.Error is not null)
                {
                    return Results.BadRequest(new { error = localSelection.Error });
                }

                localEffort = NormalizeLocalCodexReasoningEffort(input.ModelEffort);
                if (!string.IsNullOrWhiteSpace(input.ModelEffort) && localEffort is null)
                {
                    return Results.BadRequest(new { error = "Local reasoning effort must be low, medium, or high." });
                }

                await db.SaveChangesAsync(cancellationToken);
            }
            else if (executionRunner != ExecutionRunner.CodexCli)
            {
                return Results.BadRequest(new { error = "Selected execution runner is invalid." });
            }

            try
            {
                var statusResult = await ReadGitStatusPorcelainAsync(runner, project.Machine, project.Path, cancellationToken);
                if (!statusResult.Success)
                {
                    return Results.BadRequest(new { error = StripCommandPreview(statusResult.Output).Trim() });
                }

                var statusOutput = StripCommandPreview(statusResult.Output).Trim();
                if (string.IsNullOrWhiteSpace(statusOutput))
                {
                    return Results.BadRequest(new { error = "No git changes to commit." });
                }

                var beforeHead = await ReadGitHeadAsync(runner, project.Machine, project.Path, cancellationToken);
                var prompt = BuildProjectScopedPrompt(project.Path, GitCommitMessageHelper.BuildCommitPrompt());
                int exitCode;
                string commandPreview;
                string output;
                bool succeeded;
                if (localSelection is not null)
                {
                    var request = new CodexRequest
                    {
                        ProjectId = project.Id,
                        Project = project,
                        MachineId = project.MachineId,
                        Machine = project.Machine,
                        ExecutionRunner = executionRunner,
                        ProviderProfileId = localSelection.Profile?.Id,
                        ProviderProfile = localSelection.Profile,
                        Model = localSelection.Model,
                        ModelEffort = localEffort,
                        ModelSpeed = localSelection.LocalCodexContextWindow?.ToString(),
                        Prompt = prompt,
                        PermissionMode = PermissionMode.FullAccess,
                        InternetSearchEnabled = false
                    };
                    var run = new CodexRun
                    {
                        Request = request,
                        Kind = RunKind.Commit,
                        ExecutionRunner = executionRunner,
                        ProviderProfileId = localSelection.Profile?.Id,
                        ProviderProfileName = localSelection.Profile?.Name,
                        ProviderSource = localSelection.Profile?.Source,
                        Model = localSelection.Model,
                        ModelEffort = localEffort,
                        ModelSpeed = localSelection.LocalCodexContextWindow?.ToString()
                    };
                    var localResult = await agentRunnerResolver
                        .Resolve(executionRunner)
                        .RunAsync(
                            new QueueAgentRunContext(
                                request,
                                run,
                                project.Machine,
                                project.Path,
                                prompt,
                                ImagePaths: null,
                                StartNewSession: true,
                                ProviderProfile: localSelection.Profile),
                            _ => Task.CompletedTask,
                            cancellationToken);
                    exitCode = localResult.ExitCode;
                    commandPreview = localResult.CommandPreview;
                    output = StripCommandPreview(localResult.Output).Trim();
                    succeeded = localResult.Success;
                }
                else
                {
                    var codexResult = await runner.RunCodexAsync(
                        project.Machine,
                        project.Path,
                        input.Model.Trim(),
                        NormalizeEffort(input.ModelEffort, input.Model),
                        NormalizeOptionalSpeed(input.ModelSpeed),
                        null,
                        null,
                        prompt,
                        PermissionMode.FullAccess,
                        internetSearchEnabled: false,
                        _ => Task.CompletedTask,
                        cancellationToken);
                    exitCode = codexResult.ExitCode;
                    commandPreview = codexResult.CommandPreview;
                    output = StripCommandPreview(codexResult.Output).Trim();
                    succeeded = codexResult.Success;
                }

                if (!succeeded)
                {
                    return Results.BadRequest(new { error = string.IsNullOrWhiteSpace(output) ? "AI commit failed." : output });
                }

                var afterHead = await ReadGitHeadAsync(runner, project.Machine, project.Path, cancellationToken);
                if (!string.IsNullOrWhiteSpace(afterHead) && !string.Equals(beforeHead, afterHead, StringComparison.OrdinalIgnoreCase))
                {
                    var commitInfo = await ReadGitCommitInfoAsync(runner, project.Machine, project.Path, cancellationToken);
                    return Results.Ok(new GitCommitDto(
                        true,
                        GitCommitResultFormatter.Format(afterHead, commitInfo.Message ?? GitCommitMessageHelper.ExtractFromOutput(output)),
                        exitCode,
                        commandPreview,
                        afterHead));
                }

                var afterStatusResult = await ReadGitStatusPorcelainAsync(runner, project.Machine, project.Path, cancellationToken);
                var afterStatusOutput = afterStatusResult.Success ? StripCommandPreview(afterStatusResult.Output).Trim() : statusOutput;
                var agentLabel = localSelection is null ? "Codex" : "Local Codex";
                var error = string.IsNullOrWhiteSpace(afterStatusOutput)
                    ? agentLabel + " finished without creating a git commit."
                    : agentLabel + " finished without creating a git commit; project changes remain.";
                var errorOutput = string.IsNullOrWhiteSpace(output) ? error : output + Environment.NewLine + error;
                return Results.BadRequest(new { error = errorOutput });
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapPost("/projects/{id:guid}/git/suggest-message", async (Guid id, SuggestGitCommitMessageRequest input, AppDbContext db, ITargetCommandRunner runner, CancellationToken cancellationToken) =>
        {
            var project = await LoadProjectWithMachineAsync(id, db, cancellationToken);
            if (project is null)
            {
                return Results.NotFound();
            }

            if (project.Machine is null)
            {
                return Results.BadRequest(new { error = "Project machine is missing." });
            }

            if (string.IsNullOrWhiteSpace(input.Model))
            {
                return Results.BadRequest(new { error = "Model is required." });
            }

            try
            {
                var statusResult = await runner.RunShellAsync(
                    project.Machine,
                    project.Path,
                    "git status --porcelain=v1 --untracked-files=all -- .",
                    _ => Task.CompletedTask,
                    cancellationToken);
                if (!statusResult.Success)
                {
                    return Results.BadRequest(new { error = StripCommandPreview(statusResult.Output).Trim() });
                }

                var statusOutput = StripCommandPreview(statusResult.Output).Trim();
                if (string.IsNullOrWhiteSpace(statusOutput))
                {
                    return Results.BadRequest(new { error = "No git changes to describe." });
                }

                var diffStatResult = await runner.RunShellAsync(
                    project.Machine,
                    project.Path,
                    "git diff --stat --no-ext-diff -- .; git diff --cached --stat --no-ext-diff -- .",
                    _ => Task.CompletedTask,
                    cancellationToken);

                var diffResult = await runner.RunShellAsync(
                    project.Machine,
                    project.Path,
                    "git diff --no-ext-diff -- .; git diff --cached --no-ext-diff -- .",
                    _ => Task.CompletedTask,
                    cancellationToken);

                var prompt = GitCommitMessageHelper.BuildPrompt(
                    statusOutput,
                    StripCommandPreview(diffStatResult.Output).Trim(),
                    diffResult.Success ? StripCommandPreview(diffResult.Output).Trim() : "");
                var result = await runner.RunCodexAsync(
                    project.Machine,
                    project.Path,
                    input.Model.Trim(),
                    NormalizeEffort(input.ModelEffort, input.Model),
                    NormalizeOptionalSpeed(input.ModelSpeed),
                    null,
                    null,
                    prompt,
                    PermissionMode.ReadOnly,
                    internetSearchEnabled: false,
                    _ => Task.CompletedTask,
                    cancellationToken);

                var message = GitCommitMessageHelper.ExtractFromOutput(result.Output);
                if (string.IsNullOrWhiteSpace(message))
                {
                    return Results.BadRequest(new { error = "Codex did not return a commit message." });
                }

                return Results.Ok(new SuggestGitCommitMessageDto(message, StripCommandPreview(result.Output).Trim()));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapPost("/projects/{id:guid}/terminal", async (Guid id, TerminalCommandRequest input, AppDbContext db, ITargetCommandRunner runner, CancellationToken cancellationToken) =>
        {
            var project = await db.Projects.Include(x => x.Machine).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (project is null)
            {
                return Results.NotFound();
            }

            if (project.Machine is null)
            {
                return Results.BadRequest(new { error = "Project machine is missing." });
            }

            var command = input.Command.Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                return Results.BadRequest(new { error = "Command is required." });
            }

            if (command.Length > 4000)
            {
                return Results.BadRequest(new { error = "Command is too long." });
            }

            try
            {
                var result = await runner.RunShellAsync(project.Machine, project.Path, command, _ => Task.CompletedTask, cancellationToken);
                return Results.Ok(new TerminalCommandDto(result.Success, result.Output, result.ExitCode, result.CommandPreview));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapGet("/projects/{id:guid}/terminal/ttyd", async (Guid id, bool? restart, AppDbContext db, ITerminalSessionService terminal, CancellationToken cancellationToken) =>
        {
            var project = await db.Projects.Include(x => x.Machine).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (project is null)
            {
                return Results.NotFound();
            }

            if (project.Machine is null)
            {
                return Results.BadRequest(new { error = "Project machine is missing." });
            }

            try
            {
                var session = await terminal.StartAsync(project, restart is true, cancellationToken);
                // Keep this redirect relative to the terminal-start endpoint. A reverse
                // proxy mounted below a path (for example Tailscale Serve at /codex)
                // removes that prefix before forwarding to the API, so a root-relative
                // Location header would send the browser outside the mounted app.
                return Results.Redirect("../../../" + session.EntryPath["/api/".Length..]);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapGet("/terminal-sessions/{sessionId:guid}/{sessionToken}", async (Guid sessionId, string sessionToken, HttpContext context, ITerminalSessionService terminal) =>
            await terminal.ProxyAsync(sessionId, sessionToken, context));

        api.MapMethods("/terminal-sessions/{sessionId:guid}/{sessionToken}/{**targetPath}", new[] { "GET", "POST", "PUT", "DELETE", "OPTIONS" }, async (Guid sessionId, string sessionToken, HttpContext context, ITerminalSessionService terminal) =>
            await terminal.ProxyAsync(sessionId, sessionToken, context));

        api.MapGet("/requests", async (Guid? projectId, bool? includeDeleted, bool? includeOutput, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var query = db.Requests
                .Include(x => x.Project)
                .Include(x => x.QueueTab)
                .Include(x => x.Machine)
                .Include(x => x.ProviderProfile)
                .Include(x => x.Runs)
                .AsNoTracking();

            if (includeDeleted != true)
            {
                query = query.Where(x => x.DeletedAt == null);
            }

            if (projectId.HasValue)
            {
                query = query.Where(x => x.ProjectId == projectId);
            }

            query = query.Where(x => x.QueueTabId == null || x.QueueTab!.DeletedAt == null);

            var requests = await query.ToArrayAsync(cancellationToken);
            var orderedRequests = requests
                .Order(Comparer<CodexRequest>.Create(QueuePriority.CompareForDisplay));

            // Keep every actionable queue item in the polling response. Applying the
            // history limit to the whole ordered list could omit a newly-created item
            // behind older requests, causing its optimistic UI card to disappear on
            // the next refresh.
            var activeRequests = orderedRequests.Where(x =>
                x.DeletedAt is null
                && x.ArchivedAt is null
                && x.Status != QueueStatus.Succeeded);
            var remainingSlots = Math.Max(0, 200 - activeRequests.Count());
            var historyRequests = requests
                .Where(x => x.DeletedAt is not null
                    || x.ArchivedAt is not null
                    || x.Status == QueueStatus.Succeeded)
                .OrderByDescending(x => x.FinishedAt ?? x.DeletedAt ?? x.ArchivedAt ?? x.CreatedAt)
                .ThenByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.Id);
            return activeRequests
                .Concat(historyRequests.Take(remainingSlots))
                .Select(x => x.ToDto(includeOutput == true))
                .ToArray();
        });

        api.MapGet("/requests/{id:guid}", async (Guid id, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var request = await db.Requests
                .Include(x => x.Project)
                .Include(x => x.QueueTab)
                .Include(x => x.Machine)
                .Include(x => x.ProviderProfile)
                .Include(x => x.Runs)
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

            return request is null ? Results.NotFound() : Results.Ok(request.ToDto());
        });

        api.MapPost("/requests", async (
            CreateQueueRequest input,
            AppDbContext db,
            IAiProviderService providers,
            IQueueCoordinator queue,
            CancellationToken cancellationToken) =>
        {
            var project = await db.Projects.Include(x => x.Machine).FirstOrDefaultAsync(x => x.Id == input.ProjectId, cancellationToken);
            if (project is null)
            {
                return Results.BadRequest(new { error = "Project does not exist." });
            }

            if (string.IsNullOrWhiteSpace(input.Prompt) || string.IsNullOrWhiteSpace(input.Model))
            {
                return Results.BadRequest(new { error = "Prompt and model are required." });
            }

            var runnerValidation = await ValidateRunnerSelectionAsync(
                input.ExecutionRunner,
                input.ProviderProfileId,
                input.Model,
                input.ModelSpeed,
                input.PermissionMode,
                project.Machine,
                project.Path,
                db,
                providers,
                cancellationToken);
            if (runnerValidation.Profile is not null)
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            if (runnerValidation.Error is not null)
            {
                return Results.BadRequest(new { error = runnerValidation.Error });
            }
            RunnerSelectionValidation? commitValidation = null;
            var commitExecutionRunner = input.CommitExecutionRunner ?? input.ExecutionRunner;
            if (input.GenerateCommit && input.SeparateCommitSession)
            {
                commitValidation = await ValidateRunnerSelectionAsync(
                    commitExecutionRunner,
                    input.CommitProviderProfileId,
                    input.CommitModel ?? input.Model,
                    input.CommitModelSpeed ?? input.ModelSpeed,
                    input.PermissionMode,
                    project.Machine,
                    project.Path,
                    db,
                    providers,
                    cancellationToken);
                if (commitValidation.Error is not null)
                {
                    return Results.BadRequest(new { error = "Separate commit session: " + commitValidation.Error });
                }
            }

            QueueTab? queueTab = null;
            if (input.QueueTabId.HasValue)
            {
                queueTab = await db.QueueTabs.FirstOrDefaultAsync(
                    x => x.Id == input.QueueTabId.Value
                        && x.ProjectId == project.Id
                        && x.DeletedAt == null,
                    cancellationToken);
                if (queueTab is null)
                {
                    return Results.BadRequest(new { error = "Queue tab does not exist for this project." });
                }
            }

            var attachments = NormalizeAttachments(input.Attachments, out var attachmentError);
            if (attachmentError is not null)
            {
                return Results.BadRequest(new { error = attachmentError });
            }
            if (input.ExecutionRunner == ExecutionRunner.OpenHandsCli
                && attachments.Length > 0)
            {
                return Results.BadRequest(new
                {
                    error = LocalCodexAttachmentsUnavailableError,
                });
            }

            var request = new CodexRequest
            {
                ProjectId = project.Id,
                QueueTabId = queueTab?.Id,
                QueueTab = queueTab,
                MachineId = project.MachineId,
                Prompt = input.Prompt.Trim(),
                AttachmentsJson = attachments.Length == 0 ? null : JsonSerializer.Serialize(attachments),
                Model = runnerValidation.Model,
                ModelEffort = input.ExecutionRunner == ExecutionRunner.CodexCli
                    ? NormalizeEffort(input.ModelEffort, input.Model)
                    : runnerValidation.SupportsReasoningEffort
                        ? NormalizeLocalCodexReasoningEffort(input.ModelEffort)
                        : null,
                ModelSpeed = input.ExecutionRunner == ExecutionRunner.CodexCli
                    ? NormalizeSpeed(input.ModelSpeed)
                    : runnerValidation.LocalCodexContextWindow?.ToString(),
                GenerateCommit = input.PermissionMode != PermissionMode.ReadOnly
                    && input.GenerateCommit,
                SeparateCommitSession = input.PermissionMode != PermissionMode.ReadOnly
                    && input.GenerateCommit
                    && input.SeparateCommitSession,
                PermissionMode = input.PermissionMode,
                InternetSearchEnabled = input.InternetSearchEnabled,
                CommitExecutionRunner = commitExecutionRunner,
                CommitProviderProfileId = commitValidation?.Profile?.Id,
                CommitModel = commitValidation?.Model ?? (!string.IsNullOrWhiteSpace(input.CommitModel)
                    ? input.CommitModel.Trim()
                    : null),
                CommitModelEffort = commitExecutionRunner == ExecutionRunner.CodexCli
                    ? NormalizeEffort(input.CommitModelEffort, input.CommitModel ?? input.Model)
                    : commitValidation?.SupportsReasoningEffort == true
                        ? NormalizeLocalCodexReasoningEffort(input.CommitModelEffort)
                    : null,
                CommitModelSpeed = commitExecutionRunner == ExecutionRunner.CodexCli
                    ? NormalizeSpeed(input.CommitModelSpeed)
                    : commitValidation?.LocalCodexContextWindow?.ToString(),
                ExecutionRunner = input.ExecutionRunner,
                ProviderProfileId = runnerValidation.Profile?.Id,
                ProviderProfile = runnerValidation.Profile,
                OpenHandsAlwaysApproveConfirmed = input.ExecutionRunner == ExecutionRunner.OpenHandsCli
                    && input.OpenHandsAlwaysApproveConfirmed,
                ExecutionProjectPath = input.ExecutionRunner == ExecutionRunner.OpenHandsCli
                    ? project.Path
                    : null,
                ExecutionMachineUpdatedAt = input.ExecutionRunner == ExecutionRunner.OpenHandsCli
                    ? project.Machine?.UpdatedAt
                    : null,
                QueueOrder = await NextQueueOrderAsync(db, project.Id, cancellationToken),
                Status = QueueStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow
            };
            request.Runs.Add(new CodexRun
            {
                Kind = RunKind.Request,
                Model = request.Model,
                ModelEffort = request.ModelEffort,
                ModelSpeed = request.ModelSpeed,
                ExecutionRunner = request.ExecutionRunner,
                ProviderProfileId = request.ProviderProfileId,
                ProviderProfileName = request.ProviderProfile?.Name,
                ProviderSource = request.ProviderProfile?.Source,
                Status = QueueStatus.Queued,
                CreatedAt = request.CreatedAt
            });

            db.Requests.Add(request);
            await db.SaveChangesAsync(cancellationToken);
            await queue.KickQueueAsync(cancellationToken);
            await db.Entry(request).Reference(x => x.Project).LoadAsync(cancellationToken);
            await db.Entry(request).Reference(x => x.Machine).LoadAsync(cancellationToken);
            return Results.Created($"/api/requests/{request.Id}", request.ToDto());
        });

        api.MapPut("/requests/{id:guid}", async (
            Guid id,
            UpdateQueueRequest input,
            AppDbContext db,
            IAiProviderService providers,
            CancellationToken cancellationToken) =>
        {
            var request = await db.Requests
                .Include(x => x.Project)
                .Include(x => x.QueueTab)
                .Include(x => x.Machine)
                .Include(x => x.ProviderProfile)
                .Include(x => x.Runs)
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (request is null || request.DeletedAt is not null || request.ArchivedAt is not null)
            {
                return Results.NotFound();
            }

            if (request.Status != QueueStatus.Queued || request.Runs.Any(x => x.Status != QueueStatus.Queued))
            {
                return Results.BadRequest(new { error = "Only queued requests can be edited." });
            }

            if (string.IsNullOrWhiteSpace(input.Prompt) || string.IsNullOrWhiteSpace(input.Model))
            {
                return Results.BadRequest(new { error = "Prompt and model are required." });
            }

            if (!input.ExecutionRunner.HasValue
                && request.ExecutionRunner == ExecutionRunner.OpenHandsCli)
            {
                return Results.BadRequest(new
                {
                    error =
                        "This Local Codex request requires runner metadata when edited. "
                        + "Refresh the browser before changing it.",
                });
            }

            var executionRunner = input.ExecutionRunner ?? request.ExecutionRunner;
            var runnerValidation = await ValidateRunnerSelectionAsync(
                executionRunner,
                input.ProviderProfileId,
                input.Model,
                input.ModelSpeed,
                input.PermissionMode,
                request.Machine,
                request.Project?.Path,
                db,
                providers,
                cancellationToken);
            if (runnerValidation.Profile is not null)
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            if (runnerValidation.Error is not null)
            {
                return Results.BadRequest(new { error = runnerValidation.Error });
            }
            RunnerSelectionValidation? commitValidation = null;
            var commitExecutionRunner = input.CommitExecutionRunner ?? executionRunner;
            if (input.GenerateCommit && input.SeparateCommitSession)
            {
                commitValidation = await ValidateRunnerSelectionAsync(
                    commitExecutionRunner,
                    input.CommitProviderProfileId,
                    input.CommitModel ?? input.Model,
                    input.CommitModelSpeed ?? input.ModelSpeed,
                    input.PermissionMode,
                    request.Machine,
                    request.Project?.Path,
                    db,
                    providers,
                    cancellationToken);
                if (commitValidation.Error is not null)
                {
                    return Results.BadRequest(new { error = "Separate commit session: " + commitValidation.Error });
                }
            }

            if (input.Attachments is not null)
            {
                var attachments = NormalizeAttachments(input.Attachments, out var attachmentError);
                if (attachmentError is not null)
                {
                    return Results.BadRequest(new { error = attachmentError });
                }
                if (executionRunner == ExecutionRunner.OpenHandsCli
                    && attachments.Length > 0)
                {
                    return Results.BadRequest(new
                    {
                        error = LocalCodexAttachmentsUnavailableError,
                    });
                }

                request.AttachmentsJson = attachments.Length == 0 ? null : JsonSerializer.Serialize(attachments);
            }
            else if (executionRunner == ExecutionRunner.OpenHandsCli
                     && !string.IsNullOrWhiteSpace(request.AttachmentsJson))
            {
                return Results.BadRequest(new
                {
                    error =
                        "Remove this request's attachments before changing it to Local Codex. "
                        + "Attachments are not available for Local Codex in this release.",
                });
            }

            request.Prompt = input.Prompt.Trim();
            request.Model = runnerValidation.Model;
            request.ModelEffort = executionRunner == ExecutionRunner.CodexCli
                ? NormalizeEffort(input.ModelEffort, input.Model)
                : runnerValidation.SupportsReasoningEffort
                    ? NormalizeLocalCodexReasoningEffort(input.ModelEffort)
                    : null;
            request.ModelSpeed = executionRunner == ExecutionRunner.CodexCli
                ? NormalizeSpeed(input.ModelSpeed)
                : runnerValidation.LocalCodexContextWindow?.ToString();
            request.GenerateCommit = input.PermissionMode != PermissionMode.ReadOnly
                && input.GenerateCommit;
            request.SeparateCommitSession = input.PermissionMode != PermissionMode.ReadOnly
                && input.GenerateCommit
                && input.SeparateCommitSession;
            request.PermissionMode = input.PermissionMode;
            request.InternetSearchEnabled = input.InternetSearchEnabled;
            request.CommitExecutionRunner = commitExecutionRunner;
            request.CommitProviderProfileId = commitValidation?.Profile?.Id;
            request.CommitModel = commitValidation?.Model ?? NormalizeOptional(input.CommitModel);
            request.CommitModelEffort = commitExecutionRunner == ExecutionRunner.CodexCli
                ? NormalizeEffort(input.CommitModelEffort, input.CommitModel ?? input.Model)
                : commitValidation?.SupportsReasoningEffort == true
                    ? NormalizeLocalCodexReasoningEffort(input.CommitModelEffort)
                    : null;
            request.CommitModelSpeed = commitExecutionRunner == ExecutionRunner.CodexCli
                ? NormalizeSpeed(input.CommitModelSpeed)
                : commitValidation?.LocalCodexContextWindow?.ToString();
            request.ExecutionRunner = executionRunner;
            request.ProviderProfileId = runnerValidation.Profile?.Id;
            request.ProviderProfile = runnerValidation.Profile;
            request.OpenHandsAlwaysApproveConfirmed = executionRunner == ExecutionRunner.OpenHandsCli
                && input.OpenHandsAlwaysApproveConfirmed;
            request.ExecutionProjectPath = executionRunner == ExecutionRunner.OpenHandsCli
                ? request.Project?.Path
                : null;
            request.ExecutionMachineUpdatedAt = executionRunner == ExecutionRunner.OpenHandsCli
                ? request.Machine?.UpdatedAt
                : null;
            request.QueueWaitReason = null;
            request.Error = null;
            request.Summary = null;
            request.RetryAfter = null;
            request.RetryReason = null;
            request.AvailableModel = null;

            var requestRun = request.Runs
                .Where(x => x.Kind == RunKind.Request)
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.Id)
                .FirstOrDefault();
            if (requestRun is null)
            {
                requestRun = new CodexRun { Kind = RunKind.Request, Status = QueueStatus.Queued, CreatedAt = request.CreatedAt };
                requestRun.Request = request;
                db.Runs.Add(requestRun);
            }

            requestRun.Model = request.Model;
            requestRun.ModelEffort = request.ModelEffort;
            requestRun.ModelSpeed = request.ModelSpeed;
            requestRun.ExecutionRunner = request.ExecutionRunner;
            requestRun.ProviderProfileId = request.ProviderProfileId;
            requestRun.ProviderProfileName = request.ProviderProfile?.Name;
            requestRun.ProviderSource = request.ProviderProfile?.Source;
            requestRun.OpenHandsConversationId = null;
            requestRun.LocalCodexSessionId = null;
            requestRun.RawDiagnosticOutput = "";
            requestRun.Output = "";
            requestRun.Error = null;
            requestRun.RetryAfter = null;
            requestRun.RetryReason = null;
            requestRun.AvailableModel = null;
            requestRun.CommandPreview = null;
            requestRun.ExitCode = null;

            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(request.ToDto());
        });

        api.MapPost("/requests/reorder", async (ReorderQueueRequest input, AppDbContext db, CancellationToken cancellationToken) =>
        {
            if (input.RequestIds.Count == 0)
            {
                return Results.BadRequest(new { error = "Request order is required." });
            }

            var requestIds = input.RequestIds.Distinct().ToArray();
            if (requestIds.Length != input.RequestIds.Count)
            {
                return Results.BadRequest(new { error = "Request order contains duplicates." });
            }

            var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == input.ProjectId, cancellationToken);
            if (project is null)
            {
                return Results.BadRequest(new { error = "Project does not exist." });
            }

            var submittedRequests = await db.Requests
                .Where(x => requestIds.Contains(x.Id))
                .ToArrayAsync(cancellationToken);
            if (submittedRequests.Length != requestIds.Length || submittedRequests.Any(x => x.ProjectId != input.ProjectId))
            {
                return Results.BadRequest(new { error = "Request order contains unknown requests." });
            }

            if (submittedRequests.Any(x => x.Status != QueueStatus.Queued || x.DeletedAt is not null || x.ArchivedAt is not null))
            {
                return Results.BadRequest(new { error = "Only queued requests can be reordered." });
            }

            var queueTabId = submittedRequests[0].QueueTabId;
            if (project.SeparateQueuesByTab && submittedRequests.Any(x => x.QueueTabId != queueTabId))
            {
                return Results.BadRequest(new { error = "Requests from separate tab queues cannot be reordered together." });
            }

            var projectPriorityRequests = await db.Requests
                .Where(x => x.ProjectId == input.ProjectId
                    && (!project.SeparateQueuesByTab || x.QueueTabId == queueTabId)
                    && x.DeletedAt == null
                    && x.ArchivedAt == null
                    && (x.Status == QueueStatus.Queued
                        || x.Status == QueueStatus.Running
                        || x.Status == QueueStatus.CancelRequested))
                .ToArrayAsync(cancellationToken);
            QueuePriority.ReorderQueuedAfterActive(projectPriorityRequests, requestIds);

            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new { ok = true });
        });

        api.MapPost("/requests/{id:guid}/cancel", async (Guid id, IQueueCoordinator queue, CancellationToken cancellationToken) =>
            await queue.CancelRequestAsync(id, cancellationToken) ? Results.Ok(new { ok = true }) : Results.NotFound());

        api.MapPost("/requests/{id:guid}/resume", async (Guid id, IQueueCoordinator queue, CancellationToken cancellationToken) =>
            await queue.ResumeRequestAsync(id, cancellationToken) ? Results.Ok(new { ok = true }) : Results.NotFound());

        api.MapPost("/requests/{id:guid}/archive", async (Guid id, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var request = await db.Requests
                .Include(x => x.Project)
                .Include(x => x.QueueTab)
                .Include(x => x.Machine)
                .Include(x => x.ProviderProfile)
                .Include(x => x.Runs)
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (request is null || request.DeletedAt is not null)
            {
                return Results.NotFound();
            }

            if (request.Status != QueueStatus.Succeeded)
            {
                return Results.BadRequest(new { error = "Only succeeded requests can be marked done." });
            }

            request.ArchivedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(request.ToDto());
        });

        api.MapDelete("/requests/{id:guid}", async (Guid id, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var request = await db.Requests.Include(x => x.Runs).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (request is null)
            {
                return Results.NotFound();
            }

            if (request.Status is QueueStatus.Running or QueueStatus.CancelRequested)
            {
                return Results.BadRequest(new { error = "Cancel the running request before deleting it." });
            }

            request.DeletedAt ??= DateTimeOffset.UtcNow;
            request.ArchivedAt ??= request.DeletedAt;
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        api.MapGet("/queue/diagnostics", (IQueueCoordinator queue) =>
        {
            var diagnostics = queue.GetDiagnostics();
            return Results.Ok(new QueueWorkerDiagnosticsDto(
                diagnostics.LastHeartbeat,
                diagnostics.LastDispatch,
                diagnostics.LastIdle,
                diagnostics.LastError,
                diagnostics.ActiveRequestIds,
                diagnostics.IsProcessing));
        });

        api.MapPost("/queue/kick", async (IQueueCoordinator queue, CancellationToken cancellationToken) =>
            Results.Ok(new { accepted = await queue.KickQueueAsync(cancellationToken) }));

        api.MapGet("/sessions", async (AppDbContext db, CancellationToken cancellationToken) =>
        {
            var runs = await db.Runs
                .Include(x => x.Request).ThenInclude(x => x!.Project)
                .Include(x => x.Request).ThenInclude(x => x!.Machine)
                .AsNoTracking()
                .ToArrayAsync(cancellationToken);

            return runs
                .OrderByDescending(x => x.CreatedAt)
                .Take(250)
                .Select(x => new SessionDto(
                    x.Id,
                    x.RequestId,
                    x.Request!.Project!.Name,
                    x.Request.Machine!.Name,
                    x.Kind,
                    x.Model,
                    x.Status,
                    x.CreatedAt,
                    x.StartedAt,
                    x.FinishedAt,
                    x.CommitSha,
                    x.ExecutionRunner,
                    x.ProviderProfileName,
                    x.ProviderSource,
                    x.OpenHandsConversationId,
                    x.LocalCodexSessionId))
                .ToArray();
        });
    }

    private sealed record RateLimitSnapshot(IReadOnlyList<RateLimitDto> Limits);

    private static RateLimitSnapshot? ParseRateLimits(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || id.GetInt32() != 2
                    || !root.TryGetProperty("result", out var result)
                    || !result.TryGetProperty("rateLimits", out var rateLimits))
                {
                    continue;
                }

                var limits = new List<RateLimitDto> { ParseRateLimit("codex", rateLimits) };
                if (result.TryGetProperty("rateLimitsByLimitId", out var byLimitId) && byLimitId.ValueKind == JsonValueKind.Object)
                {
                    foreach (var limit in byLimitId.EnumerateObject())
                    {
                        if (!string.Equals(limit.Name, "codex", StringComparison.OrdinalIgnoreCase) && limit.Value.ValueKind == JsonValueKind.Object)
                        {
                            limits.Add(ParseRateLimit(limit.Name, limit.Value));
                        }
                    }
                }

                return new RateLimitSnapshot(limits);
            }
            catch (JsonException)
            {
                // The process preview and stderr may be mixed into the captured output.
            }
        }

        return null;
    }

    private static RateLimitDto ParseRateLimit(string fallbackId, JsonElement rateLimit)
    {
        var id = rateLimit.TryGetProperty("limitId", out var idValue) && idValue.ValueKind == JsonValueKind.String
            ? idValue.GetString() ?? fallbackId
            : fallbackId;
        var name = rateLimit.TryGetProperty("limitName", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
            ? nameValue.GetString() ?? id
            : string.Equals(id, "codex", StringComparison.OrdinalIgnoreCase) ? "Codex" : id;
        var reachedType = rateLimit.TryGetProperty("rateLimitReachedType", out var reachedValue) && reachedValue.ValueKind == JsonValueKind.String
            ? reachedValue.GetString()
            : null;
        return new RateLimitDto(id, name, ParseRateLimitWindow(rateLimit, "primary"), ParseRateLimitWindow(rateLimit, "secondary"), reachedType);
    }

    private static RateLimitWindowDto? ParseRateLimitWindow(JsonElement rateLimits, string propertyName)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("usedPercent", out var usedPercent) || !usedPercent.TryGetInt32(out var used))
        {
            return null;
        }

        var duration = window.TryGetProperty("windowDurationMins", out var durationElement) && durationElement.TryGetInt32(out var durationValue)
            ? durationValue
            : (int?)null;
        var resetsAt = window.TryGetProperty("resetsAt", out var resetsElement) && resetsElement.TryGetInt64(out var resetValue)
            ? resetValue
            : (long?)null;
        return new RateLimitWindowDto(Math.Clamp(used, 0, 100), duration, resetsAt);
    }

    private static void Apply(SaveMachineRequest input, TargetMachine machine)
    {
        machine.Name = input.Name.Trim();
        machine.Kind = input.Kind;
        machine.Host = string.IsNullOrWhiteSpace(input.Host) ? null : input.Host.Trim();
        machine.Port = input.Port.GetValueOrDefault(22);
        machine.UserName = string.IsNullOrWhiteSpace(input.UserName) ? null : input.UserName.Trim();
        machine.SshKeyPath = string.IsNullOrWhiteSpace(input.SshKeyPath) ? null : input.SshKeyPath.Trim();
        machine.Platform = input.Platform ?? MachinePlatform.Auto;
        machine.WorkingRoot = string.IsNullOrWhiteSpace(input.WorkingRoot)
            ? DefaultPaths.DefaultWorkingRoot(machine.Kind, machine.Platform)
            : input.WorkingRoot.Trim();
    }

    private static void Apply(SaveAiProviderProfileRequest input, AiProviderProfile profile)
    {
        profile.Name = (input.Name ?? "").Trim();
        profile.Source = input.Source;
        profile.LocalAiServerType = input.LocalAiServerType;
        profile.BaseUrl = (input.BaseUrl ?? "").Trim();
        profile.ModelDiscoveryMode = input.ModelDiscoveryMode;
        profile.ApiKeyEnvironmentVariable = NormalizeOptional(input.ApiKeyEnvironmentVariable);
        profile.Enabled = input.Enabled;
        profile.MaximumConcurrency = input.MaximumConcurrency;
        profile.ConfiguredContextWindow = input.ConfiguredContextWindow;
        profile.DefaultModel = NormalizeOptional(input.DefaultModel);
        profile.ServerMachineId = input.ServerMachineId;
    }

    private static void ApplyNormalizedProviderValues(
        AiProviderProfile profile,
        AiProviderValidationResult validation)
    {
        profile.BaseUrl = validation.NormalizedBaseUrl
            ?? throw new InvalidOperationException("Validated provider profile did not have a base URL.");
        profile.DefaultModel = validation.NormalizedDefaultModel;
    }

    private static string? Truncate(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maximumLength ? value : value[..maximumLength];

    private static async Task<RunnerSelectionValidation> ValidateRunnerSelectionAsync(
        ExecutionRunner executionRunner,
        Guid? providerProfileId,
        string model,
        string? localContextWindow,
        PermissionMode permissionMode,
        TargetMachine? machine,
        string? projectPath,
        AppDbContext db,
        IAiProviderService providers,
        CancellationToken cancellationToken)
    {
        var normalizedModel = model?.Trim() ?? "";
        if (!Enum.IsDefined(typeof(ExecutionRunner), executionRunner))
        {
            return new RunnerSelectionValidation(null, normalizedModel, "Selected execution runner is invalid.");
        }

        if (!Enum.IsDefined(typeof(PermissionMode), permissionMode))
        {
            return new RunnerSelectionValidation(null, normalizedModel, "Selected permission mode is invalid.");
        }

        if (executionRunner == ExecutionRunner.CodexCli)
        {
            return new RunnerSelectionValidation(null, normalizedModel, null);
        }

        if (normalizedModel.Length > 256 || normalizedModel.Any(char.IsControl))
        {
            return new RunnerSelectionValidation(
                null,
                normalizedModel,
                "Local model identifier must be 256 characters or fewer and contain no control characters.");
        }

        if (machine is null)
        {
            return new RunnerSelectionValidation(null, normalizedModel, "Selected project machine is unavailable.");
        }

        if (!IsLocalCodexProjectPathScoped(machine, projectPath))
        {
            return new RunnerSelectionValidation(
                null,
                normalizedModel,
                "Local Codex requires a project-scoped path and cannot run against a filesystem root.");
        }

        if (!providerProfileId.HasValue)
        {
            return new RunnerSelectionValidation(
                null,
                normalizedModel,
                "Select a Local AI Server profile for Local Codex.");
        }

        var profile = await db.AiProviderProfiles.FirstOrDefaultAsync(
            x => x.Id == providerProfileId.Value,
            cancellationToken);
        if (profile is null)
        {
            return new RunnerSelectionValidation(null, normalizedModel, "Selected provider profile does not exist.");
        }

        var profileError = ValidateLocalCodexProviderProfile(profile, providers);
        if (profileError is not null)
        {
            return new RunnerSelectionValidation(
                profile,
                normalizedModel,
                profileError);
        }

        var validation = providers.Validate(profile);
        normalizedModel = AiProviderService.QualifyModel(AiProviderSource.Local, normalizedModel);
        var discovery = await providers.DiscoverModelsAsync(profile, cancellationToken);
        profile.LastHealthStatus = discovery.HealthStatus;
        profile.LastHealthAt = discovery.CheckedAt;
        profile.LastHealthError = Truncate(discovery.Error, 2_000);
        if (discovery.HealthStatus != ProviderHealthStatus.Healthy)
        {
            return new RunnerSelectionValidation(
                profile,
                normalizedModel,
                "Local AI server is offline or unavailable: " + (discovery.Error ?? "health check failed."));
        }

        var selectedModel = AiProviderService.FindLocalModel(
            discovery.Models,
            normalizedModel);
        if (selectedModel is null)
        {
            return new RunnerSelectionValidation(
                profile,
                normalizedModel,
                "Selected model is not installed on the Local AI server.");
        }
        normalizedModel = selectedModel.Model;

        if (selectedModel.ToolSupportKnown && !selectedModel.SupportsTools)
        {
            return new RunnerSelectionValidation(
                profile,
                normalizedModel,
                selectedModel.Name
                + " does not advertise tool calling support. Local Codex requires a tool-capable model to inspect files, apply changes, and create commits.");
        }

        if (selectedModel.MaximumContextWindow is { } modelContextWindow
            && modelContextWindow < AiProviderService.MinimumContextWindow)
        {
            return new RunnerSelectionValidation(
                profile,
                normalizedModel,
                selectedModel.Name
                + " supports at most "
                + modelContextWindow.ToString("N0")
                + " tokens; Local Codex requires at least "
                + AiProviderService.MinimumContextWindow.ToString("N0")
                + ".");
        }

        var requestedContextWindow = NormalizeLocalCodexContextWindow(localContextWindow);
        if (!string.IsNullOrWhiteSpace(localContextWindow) && requestedContextWindow is null)
        {
            return new RunnerSelectionValidation(
                profile,
                normalizedModel,
                "Local context size must use a supported preset between 4K and 256K.");
        }

        requestedContextWindow ??= profile.ConfiguredContextWindow;
        if (requestedContextWindow is null
            || requestedContextWindow < AiProviderService.MinimumContextWindow)
        {
            return new RunnerSelectionValidation(
                profile,
                normalizedModel,
                "Local Codex requires a context size of at least "
                + AiProviderService.MinimumContextWindow.ToString("N0")
                + " tokens.");
        }

        if (profile.ConfiguredContextWindow is { } configuredContextWindow
            && requestedContextWindow > configuredContextWindow)
        {
            return new RunnerSelectionValidation(
                profile,
                normalizedModel,
                "The selected context size exceeds this Local AI server's configured "
                + configuredContextWindow.ToString("N0")
                + "-token limit.");
        }

        if (selectedModel.MaximumContextWindow is { } maximumContextWindow
            && requestedContextWindow > maximumContextWindow)
        {
            return new RunnerSelectionValidation(
                profile,
                normalizedModel,
                "The selected context size exceeds "
                + selectedModel.Name
                + "'s "
                + maximumContextWindow.ToString("N0")
                + "-token limit.");
        }

        return new RunnerSelectionValidation(
            profile,
            normalizedModel,
            null,
            selectedModel.SupportsReasoningEffort,
            requestedContextWindow);
    }

    public static bool IsLocalCodexProjectPathScoped(
        TargetMachine machine,
        string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)
            || projectPath.Any(char.IsControl))
        {
            return false;
        }

        var trimmedPath = projectPath.Trim();
        if (machine.Kind == MachineKind.Local)
        {
            try
            {
                var canonicalPath = Path.GetFullPath(trimmedPath);
                var filesystemRoot = Path.GetPathRoot(canonicalPath);
                return !string.IsNullOrWhiteSpace(filesystemRoot)
                    && !string.Equals(
                        Path.TrimEndingDirectorySeparator(canonicalPath),
                        Path.TrimEndingDirectorySeparator(filesystemRoot),
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException)
            {
                return false;
            }
        }

        if (!trimmedPath.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var depth = 0;
        foreach (var segment in trimmedPath.Split(
                     '/',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            depth++;
        }

        return depth > 0;
    }

    private sealed record RunnerSelectionValidation(
        AiProviderProfile? Profile,
        string Model,
        string? Error,
        bool SupportsReasoningEffort = false,
        int? LocalCodexContextWindow = null);

    private static string? Validate(SaveMachineRequest input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Machine name is required.";
        }

        if (input.Kind == MachineKind.Ssh && string.IsNullOrWhiteSpace(input.Host))
        {
            return "SSH machine host is required.";
        }

        if (input.Port is < 1 or > 65535)
        {
            return "SSH port must be between 1 and 65535.";
        }

        return null;
    }

    internal sealed record NormalizedLocalProjectDefaults(
        Guid? ProviderProfileId,
        string? Model,
        string? Effort,
        string? ContextWindow,
        string? Error);

    internal static bool HasLocalProjectDefaultsInput(SaveProjectRequest input) =>
        input.DefaultExecutionRunner is not null
        || input.DefaultLocalProviderProfileId is not null
        || input.DefaultLocalModel is not null
        || input.DefaultLocalModelEffort is not null
        || input.DefaultLocalModelSpeed is not null;

    internal static SaveProjectRequest MergeLocalProjectDefaultsInput(
        SaveProjectRequest input,
        Project project,
        ExecutionRunner effectiveRunner)
    {
        // ASP.NET binds both an omitted nullable property and an explicit JSON null
        // to null. Preserve stored fields for genuinely partial updates, but when a
        // caller supplies a profile/model selection, a null effort means that the
        // newly selected model has no selectable reasoning effort.
        var localSelectionSupplied =
            input.DefaultLocalProviderProfileId is not null
            || input.DefaultLocalModel is not null;
        return input with
        {
            DefaultExecutionRunner = effectiveRunner,
            DefaultLocalProviderProfileId =
                input.DefaultLocalProviderProfileId
                ?? project.DefaultLocalProviderProfileId,
            DefaultLocalModel =
                input.DefaultLocalModel
                ?? project.DefaultLocalModel,
            DefaultLocalModelEffort =
                input.DefaultLocalModelEffort
                ?? (localSelectionSupplied ? null : project.DefaultLocalModelEffort),
            DefaultLocalModelSpeed =
                input.DefaultLocalModelSpeed
                ?? project.DefaultLocalModelSpeed,
        };
    }

    internal static async Task<NormalizedLocalProjectDefaults> NormalizeLocalProjectDefaultsAsync(
        SaveProjectRequest input,
        AppDbContext db,
        IAiProviderService providers,
        CancellationToken cancellationToken,
        ExecutionRunner? effectiveRunner = null)
    {
        var runner = effectiveRunner ?? input.DefaultExecutionRunner ?? ExecutionRunner.CodexCli;
        if (!Enum.IsDefined(runner))
        {
            return new(null, null, null, null, "Default execution runner is invalid.");
        }

        var model = NormalizeOptional(input.DefaultLocalModel);
        if (input.DefaultLocalProviderProfileId is not { } profileId)
        {
            if (runner == ExecutionRunner.OpenHandsCli)
            {
                return new(null, null, null, null, "A Local AI Server profile is required when Local is the default runner.");
            }

            return new(null, null, null, null, null);
        }

        var profile = await db.AiProviderProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == profileId, cancellationToken);
        if (profile is null || profile.Source != AiProviderSource.Local)
        {
            return new(null, null, null, null, "The selected Local AI Server profile does not exist or is not a Local provider.");
        }

        var profileError = ValidateLocalCodexProviderProfile(profile, providers);
        if (profileError is not null)
        {
            return new(null, null, null, null, profileError);
        }

        if (model is null)
        {
            if (runner == ExecutionRunner.OpenHandsCli)
            {
                return new(null, null, null, null, "A Local model is required when Local is the default runner.");
            }

            return new(profileId, null, null, null, null);
        }

        try
        {
            model = AiProviderService.QualifyModel(AiProviderSource.Local, model);
        }
        catch (ArgumentException ex)
        {
            return new(null, null, null, null, ex.Message);
        }

        var effort = NormalizeLocalCodexReasoningEffort(input.DefaultLocalModelEffort);
        if (!string.IsNullOrWhiteSpace(input.DefaultLocalModelEffort) && effort is null)
        {
            return new(null, null, null, null, "Local reasoning effort must be low, medium, or high.");
        }

        var contextWindow = NormalizeLocalCodexContextWindow(input.DefaultLocalModelSpeed);
        if (!string.IsNullOrWhiteSpace(input.DefaultLocalModelSpeed) && contextWindow is null)
        {
            return new(null, null, null, null, "Local context size must use a supported preset between 4K and 256K.");
        }

        contextWindow ??= profile.ConfiguredContextWindow;
        if (contextWindow is null || contextWindow < AiProviderService.MinimumContextWindow)
        {
            return new(null, null, null, null, "Local context size must be at least "
                + AiProviderService.MinimumContextWindow.ToString("N0") + " tokens for Local Codex.");
        }

        if (profile.ConfiguredContextWindow is { } configuredContextWindow
            && contextWindow > configuredContextWindow)
        {
            return new(null, null, null, null, "Local context size exceeds this Local AI server's configured limit.");
        }

        return new(profileId, model, effort, contextWindow.ToString(), null);
    }

    private static string? ValidateLocalCodexProviderProfile(
        AiProviderProfile profile,
        IAiProviderService providers)
    {
        if (!profile.Enabled)
        {
            return "Selected Local AI Server profile is disabled.";
        }

        if (profile.Source != AiProviderSource.Local)
        {
            return "Local Codex executes only Local AI Server profiles. "
                + "Authenticated cloud profiles remain disabled to prevent credential exposure to agent-launched commands.";
        }

        if (!string.IsNullOrWhiteSpace(profile.ApiKeyEnvironmentVariable))
        {
            return "Authenticated Local AI profiles are not available in this release. "
                + "Use an unauthenticated Local AI endpoint protected by LAN/VPN access.";
        }

        var validation = providers.Validate(profile);
        if (!validation.IsValid)
        {
            return "Local AI Server profile is invalid: " + string.Join(" ", validation.Errors);
        }

        if (profile.ConfiguredContextWindow is not { } configuredContextWindow
            || configuredContextWindow < AiProviderService.MinimumContextWindow)
        {
            return "Local Codex requires a configured Local AI context window of at least "
                + AiProviderService.MinimumContextWindow.ToString("N0")
                + " tokens.";
        }

        return null;
    }

    private static string? NormalizeEffort(string? effort, string? model)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        var normalized = effort.Trim().ToLowerInvariant();
        if (normalized == "ultra" && !SupportsUltraEffort(model))
        {
            return "xhigh";
        }

        return normalized is "low" or "medium" or "high" or "xhigh" or "ultra" ? normalized : null;
    }

    private static string? NormalizeLocalCodexReasoningEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        var normalized = effort.Trim().ToLowerInvariant();
        return normalized is "low" or "medium" or "high" ? normalized : null;
    }

    private static int? NormalizeLocalCodexContextWindow(string? contextWindow)
    {
        if (string.IsNullOrWhiteSpace(contextWindow))
        {
            return null;
        }

        return int.TryParse(contextWindow.Trim(), out var parsed)
               && parsed is 4_096 or 8_192 or 16_384 or 32_768 or 65_536
                   or 131_072 or 262_144
            ? parsed
            : null;
    }

    private static bool SupportsUltraEffort(string? model) =>
        !string.IsNullOrWhiteSpace(model) && System.Text.RegularExpressions.Regex.IsMatch(model.Trim(), @"^gpt-5\.6(?:$|[-.])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeQueueTabName(string? value, out string? error)
    {
        var name = value?.Trim() ?? "";
        error = name.Length switch
        {
            0 => "Tab name is required.",
            > 80 => "Tab name must be 80 characters or fewer.",
            _ => null,
        };
        return name;
    }

    private static QueueAttachmentDto[] NormalizeAttachments(IReadOnlyList<QueueAttachmentDto>? attachments, out string? error)
    {
        error = null;
        if (attachments is null || attachments.Count == 0)
        {
            return Array.Empty<QueueAttachmentDto>();
        }

        if (attachments.Count > 8)
        {
            error = "Attach up to 8 files per request.";
            return Array.Empty<QueueAttachmentDto>();
        }

        var normalized = new List<QueueAttachmentDto>();
        var storageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attachment in attachments)
        {
            var name = SanitizeAttachmentName(attachment.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "Attachment file name is required.";
                return Array.Empty<QueueAttachmentDto>();
            }

            if (attachment.Size is < 0 or > 5_000_000)
            {
                error = "Each attachment must be 5 MB or smaller.";
                return Array.Empty<QueueAttachmentDto>();
            }

            try
            {
                var bytes = Convert.FromBase64String(attachment.ContentBase64);
                if (bytes.LongLength != attachment.Size)
                {
                    error = "Attachment size did not match uploaded content.";
                    return Array.Empty<QueueAttachmentDto>();
                }
            }
            catch (FormatException)
            {
                error = "Attachment content was not valid base64.";
                return Array.Empty<QueueAttachmentDto>();
            }

            normalized.Add(new QueueAttachmentDto(
                name,
                string.IsNullOrWhiteSpace(attachment.ContentType) ? "application/octet-stream" : attachment.ContentType.Trim(),
                attachment.Size,
                attachment.ContentBase64,
                CreateUniqueAttachmentStorageName(name, storageNames)));
        }

        return normalized.ToArray();
    }

    private static string CreateUniqueAttachmentStorageName(string name, ISet<string> names)
    {
        if (names.Add(name))
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        var stem = extension.Length == 0 ? name : name[..^extension.Length];
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{stem} ({suffix}){extension}";
            if (names.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string SanitizeAttachmentName(string name)
    {
        // The request can be sent to a target with a different path separator than
        // the API host, so normalize both separators before taking the leaf name.
        var fileName = Path.GetFileName(name.Trim().Replace('\\', '/'));
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '_');
        }
        foreach (var invalid in "<>:\"/\\|?*")
        {
            fileName = fileName.Replace(invalid, '_');
        }

        return fileName is "." or ".." ? "" : fileName;
    }

    private static string? NormalizeOptionalSpeed(string? speed) =>
        string.IsNullOrWhiteSpace(speed) ? null : NormalizeSpeed(speed);

    private static async Task<int> NextQueueOrderAsync(AppDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        var maxOrder = await db.Requests
            .Where(x => x.ProjectId == projectId)
            .MaxAsync(x => (int?)x.QueueOrder, cancellationToken);
        return (maxOrder ?? 0) + 1;
    }

    private static Task<Project?> LoadProjectWithMachineAsync(Guid id, AppDbContext db, CancellationToken cancellationToken) =>
        db.Projects.Include(x => x.Machine).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    private static IReadOnlyList<GitFileChangeDto> ParseGitChanges(string output, out string branch)
    {
        branch = "unknown";
        var changes = new List<GitFileChangeDto>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                branch = ParseGitBranch(line[3..]);
                continue;
            }

            if (line.Length < 4)
            {
                continue;
            }

            var code = line[..2];
            var path = line[3..].Trim();
            var renameSeparator = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (renameSeparator >= 0)
            {
                path = path[(renameSeparator + 4)..].Trim();
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            changes.Add(new GitFileChangeDto(path, GitStatusLabel(code), IsGitStatusStaged(code), IsGitStatusUnstaged(code)));
        }

        return changes
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ParseGitBranch(string line)
    {
        var trimmed = line.Trim();
        var upstreamIndex = trimmed.IndexOf("...", StringComparison.Ordinal);
        if (upstreamIndex >= 0)
        {
            trimmed = trimmed[..upstreamIndex];
        }

        return string.IsNullOrWhiteSpace(trimmed) ? "unknown" : trimmed;
    }

    private static string GitStatusLabel(string status)
    {
        if (status == "??") return "untracked";
        if (status.Contains('D')) return "deleted";
        if (status.Contains('R')) return "renamed";
        if (status.Contains('A')) return "added";
        if (status.Contains('M')) return "modified";
        return "changed";
    }

    private static bool IsGitStatusStaged(string status) =>
        status.Length > 0 && status[0] is not ' ' and not '?';

    private static bool IsGitStatusUnstaged(string status) =>
        status == "??" || status.Length > 1 && status[1] != ' ';

    private static string StripCommandPreview(string output)
    {
        if (!output.StartsWith("$ ", StringComparison.Ordinal))
        {
            return output;
        }

        var newline = output.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? "" : output[(newline + 1)..];
    }

    private static Task<CommandResult> ReadGitStatusPorcelainAsync(
        ITargetCommandRunner runner,
        TargetMachine machine,
        string projectPath,
        CancellationToken cancellationToken) =>
        runner.RunShellAsync(
            machine,
            projectPath,
            "git status --porcelain -- .",
            _ => Task.CompletedTask,
            cancellationToken);

    private static async Task<string?> ReadGitHeadAsync(
        ITargetCommandRunner runner,
        TargetMachine machine,
        string projectPath,
        CancellationToken cancellationToken)
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

        return StripCommandPreview(result.Output)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.Length == 40 && line.All(IsHex));
    }

    private static async Task<(string? Sha, string? Message)> ReadGitCommitInfoAsync(
        ITargetCommandRunner runner,
        TargetMachine machine,
        string projectPath,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunShellAsync(
            machine,
            projectPath,
            "git rev-parse HEAD && git log -1 --pretty=%B",
            _ => Task.CompletedTask,
            cancellationToken);

        if (!result.Success)
        {
            return (null, null);
        }

        var lines = StripCommandPreview(result.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var shaIndex = Array.FindIndex(lines, line =>
        {
            var trimmed = line.Trim();
            return trimmed.Length == 40 && trimmed.All(IsHex);
        });

        if (shaIndex < 0)
        {
            return (null, null);
        }

        var sha = lines[shaIndex].Trim();
        var message = string.Join('\n', lines.Skip(shaIndex + 1)).Trim();
        return (sha, string.IsNullOrWhiteSpace(message) ? null : message);
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

    private static string? ExtractCommitSha(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var line = lines[index];
            if (line.Length is >= 7 and <= 40 && line.All(IsHex))
            {
                return line;
            }
        }

        return null;
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static string SanitizeGitCommitMessage(string message)
    {
        var normalized = string.Join(" ", message.Replace('\r', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.StartsWith("\"", StringComparison.Ordinal) && normalized.EndsWith("\"", StringComparison.Ordinal) && normalized.Length > 1)
        {
            normalized = normalized.Trim('"');
        }

        if (normalized.StartsWith("`", StringComparison.Ordinal) && normalized.EndsWith("`", StringComparison.Ordinal) && normalized.Length > 1)
        {
            normalized = normalized.Trim('`');
        }

        return normalized.Length <= 180 ? normalized : normalized[..180];
    }

    private static string NormalizeSpeed(string? speed)
    {
        if (string.IsNullOrWhiteSpace(speed))
        {
            return "normal";
        }

        var normalized = speed.Trim().ToLowerInvariant();
        return normalized is "priority" or "x1.5" or "fast" ? "priority" : "normal";
    }

    private static ModelOptionDto ParseModelOption(string value)
    {
        var parts = value.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            var supportsPriority = bool.TryParse(parts.ElementAtOrDefault(2), out var parsed) && parsed;
            return new ModelOptionDto(parts[0], parts[1], supportsPriority);
        }

        return new ModelOptionDto(value, value, false);
    }

    private static readonly ModelOptionDto[] DefaultModels =
    {
        new("GPT-5.6 Sol", "gpt-5.6-sol", true),
        new("GPT-5.6 Terra", "gpt-5.6-terra", true),
        new("GPT-5.6 Luna", "gpt-5.6-luna", true),
        new("GPT-5.5", "gpt-5.5", true),
        new("GPT-5.4", "gpt-5.4", true),
        new("GPT-5.4 Mini", "gpt-5.4-mini", true),
        new("GPT-5.3 Codex Spark", "gpt-5.3-codex-spark", false)
    };

}
