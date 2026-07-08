using CodexQueue.Api.Domain;

namespace CodexQueue.Api.Endpoints;

public sealed record MachineDto(
    Guid Id,
    string Name,
    MachineKind Kind,
    string? Host,
    int Port,
    string? UserName,
    string? SshKeyPath,
    string? WorkingRoot,
    MachinePlatform Platform,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SaveMachineRequest(
    string Name,
    MachineKind Kind,
    string? Host,
    int? Port,
    string? UserName,
    string? SshKeyPath,
    string? WorkingRoot,
    MachinePlatform? Platform);

public sealed record ProjectDto(
    Guid Id,
    string Name,
    string Path,
    Guid MachineId,
    string MachineName,
    MachineKind MachineKind,
    string? DefaultModel,
    string? DefaultModelEffort,
    string? DefaultModelSpeed,
    string? DefaultCommitModel,
    string? DefaultCommitModelEffort,
    string? DefaultCommitModelSpeed,
    bool DefaultGenerateCommit,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SaveProjectRequest(
    string Name,
    string Path,
    Guid MachineId,
    string? DefaultModel,
    string? DefaultModelEffort,
    string? DefaultModelSpeed,
    string? DefaultCommitModel,
    string? DefaultCommitModelEffort,
    string? DefaultCommitModelSpeed,
    bool? DefaultGenerateCommit);

public sealed record CreateQueueRequest(
    Guid ProjectId,
    string Prompt,
    IReadOnlyList<QueueAttachmentDto>? Attachments,
    string Model,
    string? ModelEffort,
    string? ModelSpeed,
    bool GenerateCommit,
    string? CommitModel,
    string? CommitModelEffort,
    string? CommitModelSpeed);

public sealed record QueueAttachmentDto(string Name, string ContentType, long Size, string ContentBase64);

public sealed record RequestAttachmentDto(string Name, string ContentType, long Size);

public sealed record CodexRunDto(
    Guid Id,
    RunKind Kind,
    string Model,
    string? ModelEffort,
    string? ModelSpeed,
    QueueStatus Status,
    string? CommandPreview,
    string Output,
    int? ExitCode,
    DateTimeOffset? RetryAfter,
    string? RetryReason,
    string? AvailableModel,
    string? CommitMessage,
    string? CommitSha,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record CodexRequestDto(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    string ProjectPath,
    Guid MachineId,
    string MachineName,
    MachineKind MachineKind,
    string Prompt,
    IReadOnlyList<RequestAttachmentDto> Attachments,
    string Model,
    string? ModelEffort,
    string? ModelSpeed,
    QueueStatus Status,
    bool GenerateCommit,
    string? CommitModel,
    string? CommitModelEffort,
    string? CommitModelSpeed,
    DateTimeOffset? RetryAfter,
    string? RetryReason,
    string? AvailableModel,
    string? Summary,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset? DeletedAt,
    IReadOnlyList<CodexRunDto> Runs);

public sealed record SessionDto(
    Guid RunId,
    Guid RequestId,
    string ProjectName,
    string MachineName,
    RunKind Kind,
    string Model,
    QueueStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? CommitSha);

public sealed record FileTreeEntryDto(string Name, string Path, bool IsDirectory, long? Size);

public sealed record FileContentDto(string Path, string Content, long Size, bool Truncated);

public sealed record TerminalCommandRequest(string Command);

public sealed record TerminalCommandDto(bool Success, string Output, int ExitCode, string CommandPreview);

public sealed record ModelOptionDto(string Label, string Model, bool SupportsPriority);

public sealed record ApiConfigDto(bool RequiresToken, IReadOnlyList<ModelOptionDto> Models);

public sealed record MachineTestDto(bool Success, string Output);

public sealed record QueueWorkerDiagnosticsDto(
    DateTimeOffset? LastHeartbeat,
    DateTimeOffset? LastDispatch,
    DateTimeOffset? LastIdle,
    string? LastError,
    IReadOnlyCollection<Guid> ActiveRequestIds,
    bool IsProcessing);
