using CodexQueue.Api.Data;
using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CodexQueue.Api.Endpoints;

public static class ApiEndpoints
{
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

            Apply(input, machine);
            machine.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
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

        api.MapPost("/projects", async (SaveProjectRequest input, AppDbContext db, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Path))
            {
                return Results.BadRequest(new { error = "Project name and path are required." });
            }

            if (!await db.Machines.AnyAsync(x => x.Id == input.MachineId, cancellationToken))
            {
                return Results.BadRequest(new { error = "Machine does not exist." });
            }

            var project = new Project
            {
                Name = input.Name.Trim(),
                Path = input.Path.Trim(),
                MachineId = input.MachineId,
                DefaultModel = NormalizeOptional(input.DefaultModel),
                DefaultModelEffort = NormalizeEffort(input.DefaultModelEffort),
                DefaultModelSpeed = NormalizeOptionalSpeed(input.DefaultModelSpeed),
                DefaultCommitModel = NormalizeOptional(input.DefaultCommitModel),
                DefaultCommitModelEffort = NormalizeEffort(input.DefaultCommitModelEffort),
                DefaultCommitModelSpeed = NormalizeOptionalSpeed(input.DefaultCommitModelSpeed),
                DefaultGenerateCommit = input.DefaultGenerateCommit ?? true
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync(cancellationToken);
            await db.Entry(project).Reference(x => x.Machine).LoadAsync(cancellationToken);
            return Results.Created($"/api/projects/{project.Id}", project.ToDto());
        });

        api.MapPut("/projects/{id:guid}", async (Guid id, SaveProjectRequest input, AppDbContext db, CancellationToken cancellationToken) =>
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

            project.Name = input.Name.Trim();
            project.Path = input.Path.Trim();
            project.MachineId = input.MachineId;
            project.DefaultModel = NormalizeOptional(input.DefaultModel);
            project.DefaultModelEffort = NormalizeEffort(input.DefaultModelEffort);
            project.DefaultModelSpeed = NormalizeOptionalSpeed(input.DefaultModelSpeed);
            project.DefaultCommitModel = NormalizeOptional(input.DefaultCommitModel);
            project.DefaultCommitModelEffort = NormalizeEffort(input.DefaultCommitModelEffort);
            project.DefaultCommitModelSpeed = NormalizeOptionalSpeed(input.DefaultCommitModelSpeed);
            project.DefaultGenerateCommit = input.DefaultGenerateCommit ?? true;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await db.Entry(project).Reference(x => x.Machine).LoadAsync(cancellationToken);
            return Results.Ok(project.ToDto());
        });

        api.MapDelete("/projects/{id:guid}", async (Guid id, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (project is null)
            {
                return Results.NotFound();
            }

            db.Projects.Remove(project);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
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

        api.MapGet("/requests", async (Guid? projectId, bool? includeDeleted, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var query = db.Requests
                .Include(x => x.Project)
                .Include(x => x.Machine)
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

            var requests = await query.ToArrayAsync(cancellationToken);
            return requests
                .OrderByDescending(x => x.CreatedAt)
                .Take(200)
                .Select(x => x.ToDto())
                .ToArray();
        });

        api.MapGet("/requests/{id:guid}", async (Guid id, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var request = await db.Requests
                .Include(x => x.Project)
                .Include(x => x.Machine)
                .Include(x => x.Runs)
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

            return request is null ? Results.NotFound() : Results.Ok(request.ToDto());
        });

        api.MapPost("/requests", async (CreateQueueRequest input, AppDbContext db, CancellationToken cancellationToken) =>
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

            var attachments = NormalizeAttachments(input.Attachments, out var attachmentError);
            if (attachmentError is not null)
            {
                return Results.BadRequest(new { error = attachmentError });
            }

            var request = new CodexRequest
            {
                ProjectId = project.Id,
                MachineId = project.MachineId,
                Prompt = input.Prompt.Trim(),
                AttachmentsJson = attachments.Length == 0 ? null : JsonSerializer.Serialize(attachments),
                Model = input.Model.Trim(),
                ModelEffort = NormalizeEffort(input.ModelEffort),
                ModelSpeed = NormalizeSpeed(input.ModelSpeed),
                GenerateCommit = input.GenerateCommit,
                CommitModel = string.IsNullOrWhiteSpace(input.CommitModel) ? null : input.CommitModel.Trim(),
                CommitModelEffort = NormalizeEffort(input.CommitModelEffort),
                CommitModelSpeed = NormalizeSpeed(input.CommitModelSpeed),
                Status = QueueStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow
            };
            request.Runs.Add(new CodexRun
            {
                Kind = RunKind.Request,
                Model = request.Model,
                ModelEffort = request.ModelEffort,
                ModelSpeed = request.ModelSpeed,
                Status = QueueStatus.Queued,
                CreatedAt = request.CreatedAt
            });

            db.Requests.Add(request);
            await db.SaveChangesAsync(cancellationToken);
            await db.Entry(request).Reference(x => x.Project).LoadAsync(cancellationToken);
            await db.Entry(request).Reference(x => x.Machine).LoadAsync(cancellationToken);
            return Results.Created($"/api/requests/{request.Id}", request.ToDto());
        });

        api.MapPost("/requests/{id:guid}/cancel", async (Guid id, IQueueCoordinator queue, CancellationToken cancellationToken) =>
            await queue.CancelRequestAsync(id, cancellationToken) ? Results.Ok(new { ok = true }) : Results.NotFound());

        api.MapPost("/requests/{id:guid}/resume", async (Guid id, IQueueCoordinator queue, CancellationToken cancellationToken) =>
            await queue.ResumeRequestAsync(id, cancellationToken) ? Results.Ok(new { ok = true }) : Results.NotFound());

        api.MapPost("/requests/{id:guid}/archive", async (Guid id, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var request = await db.Requests
                .Include(x => x.Project)
                .Include(x => x.Machine)
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
                    x.CommitSha))
                .ToArray();
        });
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

    private static string? NormalizeEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        var normalized = effort.Trim().ToLowerInvariant();
        return normalized is "low" or "medium" or "high" or "xhigh" ? normalized : null;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
                attachment.ContentBase64));
        }

        return normalized.ToArray();
    }

    private static string SanitizeAttachmentName(string name)
    {
        var fileName = Path.GetFileName(name.Trim());
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '_');
        }

        return fileName;
    }

    private static string? NormalizeOptionalSpeed(string? speed) =>
        string.IsNullOrWhiteSpace(speed) ? null : NormalizeSpeed(speed);

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
        new("GPT-5.5", "gpt-5.5", true),
        new("GPT-5.4", "gpt-5.4", true),
        new("GPT-5.4 Mini", "gpt-5.4-mini", false),
        new("GPT-5.3 Codex Spark", "gpt-5.3-codex-spark", false)
    };

}
