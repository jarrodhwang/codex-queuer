using CodexQueue.Api.Data;
using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
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
                DefaultGenerateCommit = input.DefaultGenerateCommit ?? true,
                DefaultSeparateCommitSession = input.DefaultSeparateCommitSession ?? false
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
            project.DefaultSeparateCommitSession = input.DefaultSeparateCommitSession ?? false;
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

        api.MapGet("/projects/{id:guid}/git/status", async (Guid id, AppDbContext db, ITargetCommandRunner runner, CancellationToken cancellationToken) =>
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

                var diffStatResult = await runner.RunShellAsync(
                    project.Machine,
                    project.Path,
                    "git diff --stat --no-ext-diff -- .; git diff --cached --stat --no-ext-diff -- .",
                    _ => Task.CompletedTask,
                    cancellationToken);

                var statusOutput = StripCommandPreview(statusResult.Output);
                var changes = ParseGitChanges(statusOutput, out var branch);
                var diffStat = diffStatResult.Success ? StripCommandPreview(diffStatResult.Output).Trim() : "";
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

        api.MapPost("/projects/{id:guid}/git/codex-commit", async (Guid id, CodexGitCommitRequest input, AppDbContext db, ITargetCommandRunner runner, CancellationToken cancellationToken) =>
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
                var result = await runner.RunCodexAsync(
                    project.Machine,
                    project.Path,
                    input.Model.Trim(),
                    NormalizeEffort(input.ModelEffort),
                    NormalizeOptionalSpeed(input.ModelSpeed),
                    null,
                    null,
                    prompt,
                    true,
                    _ => Task.CompletedTask,
                    cancellationToken);

                var output = StripCommandPreview(result.Output).Trim();
                if (!result.Success)
                {
                    return Results.BadRequest(new { error = string.IsNullOrWhiteSpace(output) ? "Codex commit failed." : output });
                }

                var afterHead = await ReadGitHeadAsync(runner, project.Machine, project.Path, cancellationToken);
                if (!string.IsNullOrWhiteSpace(afterHead) && !string.Equals(beforeHead, afterHead, StringComparison.OrdinalIgnoreCase))
                {
                    var commitInfo = await ReadGitCommitInfoAsync(runner, project.Machine, project.Path, cancellationToken);
                    return Results.Ok(new GitCommitDto(
                        true,
                        GitCommitResultFormatter.Format(afterHead, commitInfo.Message ?? GitCommitMessageHelper.ExtractFromOutput(output)),
                        result.ExitCode,
                        result.CommandPreview,
                        afterHead));
                }

                var afterStatusResult = await ReadGitStatusPorcelainAsync(runner, project.Machine, project.Path, cancellationToken);
                var afterStatusOutput = afterStatusResult.Success ? StripCommandPreview(afterStatusResult.Output).Trim() : statusOutput;
                var error = string.IsNullOrWhiteSpace(afterStatusOutput)
                    ? "Codex finished without creating a git commit."
                    : "Codex finished without creating a git commit; project changes remain.";
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
                    NormalizeEffort(input.ModelEffort),
                    NormalizeOptionalSpeed(input.ModelSpeed),
                    null,
                    null,
                    prompt,
                    false,
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

        api.MapGet("/projects/{id:guid}/terminal/ws", async (Guid id, HttpContext context, AppDbContext db) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "WebSocket request is required." });
                return;
            }

            var project = await db.Projects.Include(x => x.Machine).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, context.RequestAborted);
            if (project?.Machine is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await RunInteractiveTerminalAsync(project, socket, context.RequestAborted);
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

        api.MapPost("/requests", async (CreateQueueRequest input, AppDbContext db, IQueueCoordinator queue, CancellationToken cancellationToken) =>
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
                SeparateCommitSession = input.GenerateCommit && input.SeparateCommitSession,
                CommitModel = string.IsNullOrWhiteSpace(input.CommitModel) ? null : input.CommitModel.Trim(),
                CommitModelEffort = NormalizeEffort(input.CommitModelEffort),
                CommitModelSpeed = NormalizeSpeed(input.CommitModelSpeed),
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

        api.MapPut("/requests/{id:guid}", async (Guid id, UpdateQueueRequest input, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var request = await db.Requests
                .Include(x => x.Project)
                .Include(x => x.Machine)
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

            if (input.Attachments is not null)
            {
                var attachments = NormalizeAttachments(input.Attachments, out var attachmentError);
                if (attachmentError is not null)
                {
                    return Results.BadRequest(new { error = attachmentError });
                }

                request.AttachmentsJson = attachments.Length == 0 ? null : JsonSerializer.Serialize(attachments);
            }

            request.Prompt = input.Prompt.Trim();
            request.Model = input.Model.Trim();
            request.ModelEffort = NormalizeEffort(input.ModelEffort);
            request.ModelSpeed = NormalizeSpeed(input.ModelSpeed);
            request.GenerateCommit = input.GenerateCommit;
            request.SeparateCommitSession = input.GenerateCommit && input.SeparateCommitSession;
            request.CommitModel = string.IsNullOrWhiteSpace(input.CommitModel) ? null : input.CommitModel.Trim();
            request.CommitModelEffort = NormalizeEffort(input.CommitModelEffort);
            request.CommitModelSpeed = NormalizeSpeed(input.CommitModelSpeed);
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
                request.Runs.Add(requestRun);
            }

            requestRun.Model = request.Model;
            requestRun.ModelEffort = request.ModelEffort;
            requestRun.ModelSpeed = request.ModelSpeed;
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

            var requests = await db.Requests
                .Where(x => x.ProjectId == input.ProjectId && requestIds.Contains(x.Id))
                .ToArrayAsync(cancellationToken);
            if (requests.Length != requestIds.Length)
            {
                return Results.BadRequest(new { error = "Request order contains unknown requests." });
            }

            if (requests.Any(x => x.Status != QueueStatus.Queued || x.DeletedAt is not null || x.ArchivedAt is not null))
            {
                return Results.BadRequest(new { error = "Only queued requests can be reordered." });
            }

            var requestsById = requests.ToDictionary(x => x.Id);
            for (var index = 0; index < requestIds.Length; index++)
            {
                requestsById[requestIds[index]].QueueOrder = index + 1;
            }

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

    private static async Task<int> NextQueueOrderAsync(AppDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        var maxOrder = await db.Requests
            .Where(x => x.ProjectId == projectId)
            .MaxAsync(x => (int?)x.QueueOrder, cancellationToken);
        return (maxOrder ?? 0) + 1;
    }

    private static async Task RunInteractiveTerminalAsync(Project project, WebSocket socket, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo;
        try
        {
            startInfo = BuildInteractiveTerminalStartInfo(project);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            await SendTerminalTextAsync(socket, ex.Message + "\n", cancellationToken);
            return;
        }

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                await SendTerminalTextAsync(socket, "Failed to start terminal process.\n", cancellationToken);
                return;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            await SendTerminalTextAsync(socket, ex.Message + "\n", cancellationToken);
            return;
        }

        var stdoutTask = PumpTerminalReaderAsync(process.StandardOutput, socket, cancellationToken);
        var stderrTask = PumpTerminalReaderAsync(process.StandardError, socket, cancellationToken);
        var inputTask = PumpTerminalInputAsync(socket, process.StandardInput, cancellationToken);
        var exitTask = process.WaitForExitAsync(cancellationToken);

        await Task.WhenAny(inputTask, exitTask);
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort cleanup after the browser closes the terminal.
            }
        }

        await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ContinueWith(_ => { });
        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "terminal closed", CancellationToken.None);
        }
    }

    private static ProcessStartInfo BuildInteractiveTerminalStartInfo(Project project)
    {
        var machine = project.Machine ?? throw new InvalidOperationException("Project machine is missing.");
        if (machine.Kind == MachineKind.Local)
        {
            if (machine.TargetsWindows())
            {
                return TerminalStartInfo(
                    "powershell",
                    new[] { "-NoLogo", "-NoExit", "-Command", "Set-Location -LiteralPath " + TargetCommandRunner.QuotePowerShellValue(project.Path) },
                    null);
            }

            var scriptBinary = ResolveScriptBinary();
            if (scriptBinary is not null)
            {
                return TerminalStartInfo(
                    scriptBinary,
                    new[] { "-q", "-f", "-e", "-c", BuildInteractiveUnixShellCommand(project.Path), "/dev/null" },
                    null);
            }

            return TerminalStartInfo("/bin/bash", new[] { "-li" }, project.Path);
        }

        if (string.IsNullOrWhiteSpace(machine.Host))
        {
            throw new InvalidOperationException("SSH machine host is required.");
        }

        var destination = string.IsNullOrWhiteSpace(machine.UserName)
            ? machine.Host
            : machine.UserName + "@" + machine.Host;
        var arguments = new List<string>
        {
            "-tt",
            "-o",
            "StrictHostKeyChecking=accept-new",
            "-p",
            machine.Port.ToString()
        };

        if (!string.IsNullOrWhiteSpace(machine.SshKeyPath))
        {
            var keyPath = ResolveSshKeyPath(machine.SshKeyPath);
            if (!File.Exists(keyPath))
            {
                throw new InvalidOperationException("SSH key file is not accessible inside the API runtime: " + keyPath + ".");
            }

            arguments.Add("-i");
            arguments.Add(keyPath);
        }

        arguments.Add(destination);
        arguments.Add(machine.TargetsWindows()
            ? "powershell -NoLogo"
            : BuildInteractiveUnixShellCommand(project.Path));
        return TerminalStartInfo("ssh", arguments, null);
    }

    private static ProcessStartInfo TerminalStartInfo(string fileName, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        startInfo.Environment["TERM"] = "xterm-256color";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string BuildInteractiveUnixShellCommand(string projectPath) =>
        "cd " + QuoteShell(projectPath) + " && exec ${SHELL:-/bin/bash} -li";

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

    private static string? ResolveScriptBinary()
    {
        foreach (var path in new[] { "/usr/bin/script", "/bin/script" })
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static async Task PumpTerminalReaderAsync(StreamReader reader, WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await SendTerminalTextAsync(socket, new string(buffer, 0, read), cancellationToken);
        }
    }

    private static async Task PumpTerminalInputAsync(WebSocket socket, StreamWriter input, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var received = await socket.ReceiveAsync(buffer, cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (received.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, received.Count);
            await input.WriteAsync(text.AsMemory(), cancellationToken);
            await input.FlushAsync(cancellationToken);
        }
    }

    private static Task SendTerminalTextAsync(WebSocket socket, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static string QuoteShell(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string ResolveSshKeyPath(string configuredPath)
    {
        var expanded = configuredPath.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal);
        if (Path.IsPathRooted(expanded))
        {
            return expanded;
        }

        var fileName = Path.GetFileName(expanded);
        return Path.Combine("/home/app/.ssh", fileName);
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
        new("GPT-5.5", "gpt-5.5", true),
        new("GPT-5.4", "gpt-5.4", true),
        new("GPT-5.4 Mini", "gpt-5.4-mini", false),
        new("GPT-5.3 Codex Spark", "gpt-5.3-codex-spark", false)
    };

}
