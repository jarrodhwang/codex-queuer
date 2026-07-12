using CodexQueue.Api.Domain;
using System.Text.Json;

namespace CodexQueue.Api.Endpoints;

public static class Mapping
{
    public static MachineDto ToDto(this TargetMachine machine) =>
        new(
            machine.Id,
            machine.Name,
            machine.Kind,
            machine.Host,
            machine.Port,
            machine.UserName,
            machine.SshKeyPath,
            machine.WorkingRoot,
            machine.Platform,
            machine.CreatedAt,
            machine.UpdatedAt);

    public static ProjectDto ToDto(this Project project) =>
        new(
            project.Id,
            project.Name,
            project.Path,
            project.MachineId,
            project.Machine?.Name ?? "",
            project.Machine?.Kind ?? MachineKind.Local,
            project.DefaultModel,
            project.DefaultModelEffort,
            project.DefaultModelSpeed,
            project.DefaultCommitModel,
            project.DefaultCommitModelEffort,
            project.DefaultCommitModelSpeed,
            project.DefaultGenerateCommit,
            project.DefaultSeparateCommitSession,
            project.CreatedAt,
            project.UpdatedAt);

    public static QueueTabDto ToDto(this QueueTab tab) =>
        new(
            tab.Id,
            tab.ProjectId,
            tab.Name,
            tab.CreatedAt,
            tab.UpdatedAt);

    public static CodexRequestDto ToDto(this CodexRequest request, bool includeRunOutput = true) =>
        new(
            request.Id,
            request.ProjectId,
            request.QueueTabId,
            request.QueueTab?.Name,
            request.Project?.Name ?? "",
            request.Project?.Path ?? "",
            request.MachineId,
            request.Machine?.Name ?? "",
            request.Machine?.Kind ?? MachineKind.Local,
            request.Prompt,
            ReadAttachmentMetadata(request.AttachmentsJson),
            request.Model,
            request.ModelEffort,
            request.ModelSpeed,
            request.QueueOrder,
            request.Status,
            request.GenerateCommit,
            request.SeparateCommitSession,
            request.CommitModel,
            request.CommitModelEffort,
            request.CommitModelSpeed,
            request.RetryAfter,
            request.RetryReason,
            request.AvailableModel,
            request.Summary,
            request.Error,
            request.CreatedAt,
            request.StartedAt,
            request.FinishedAt,
            request.ArchivedAt,
            request.DeletedAt,
            request.Runs.OrderBy(x => x.CreatedAt).Select(x => x.ToDto(includeRunOutput)).ToArray());

    public static CodexRunDto ToDto(this CodexRun run, bool includeOutput = true) =>
        new(
            run.Id,
            run.Kind,
            run.Model,
            run.ModelEffort,
            run.ModelSpeed,
            run.Status,
            run.CommandPreview,
            includeOutput ? run.Output : "",
            run.ExitCode,
            run.RetryAfter,
            run.RetryReason,
            run.AvailableModel,
            run.CommitMessage,
            run.CommitSha,
            run.Error,
            run.CreatedAt,
            run.StartedAt,
            run.FinishedAt);

    private static IReadOnlyList<RequestAttachmentDto> ReadAttachmentMetadata(string? attachmentsJson)
    {
        if (string.IsNullOrWhiteSpace(attachmentsJson))
        {
            return Array.Empty<RequestAttachmentDto>();
        }

        try
        {
            return JsonSerializer.Deserialize<QueueAttachmentDto[]>(attachmentsJson)?
                .Select(x => new RequestAttachmentDto(x.Name, x.ContentType, x.Size))
                .ToArray() ?? Array.Empty<RequestAttachmentDto>();
        }
        catch (JsonException)
        {
            return Array.Empty<RequestAttachmentDto>();
        }
    }
}
