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
    bool DefaultSeparateCommitSession,
    PermissionMode DefaultPermissionMode,
    bool SeparateQueuesByTab,
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
    bool? DefaultGenerateCommit,
    bool? DefaultSeparateCommitSession,
    PermissionMode? DefaultPermissionMode,
    bool? SeparateQueuesByTab);

public sealed record QueueTabDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? OpenHandsConversationId);

public sealed record CreateQueueTabRequest(Guid ProjectId, string Name);

public sealed record RenameQueueTabRequest(string Name);

public sealed record CreateQueueRequest(
    Guid ProjectId,
    Guid? QueueTabId,
    string Prompt,
    IReadOnlyList<QueueAttachmentDto>? Attachments,
    string Model,
    string? ModelEffort,
    string? ModelSpeed,
    bool GenerateCommit,
    bool SeparateCommitSession,
    PermissionMode PermissionMode,
    string? CommitModel,
    string? CommitModelEffort,
    string? CommitModelSpeed,
    ExecutionRunner ExecutionRunner = ExecutionRunner.CodexCli,
    Guid? ProviderProfileId = null,
    bool OpenHandsAlwaysApproveConfirmed = false);

public sealed record UpdateQueueRequest(
    string Prompt,
    IReadOnlyList<QueueAttachmentDto>? Attachments,
    string Model,
    string? ModelEffort,
    string? ModelSpeed,
    bool GenerateCommit,
    bool SeparateCommitSession,
    PermissionMode PermissionMode,
    string? CommitModel,
    string? CommitModelEffort,
    string? CommitModelSpeed,
    ExecutionRunner? ExecutionRunner = null,
    Guid? ProviderProfileId = null,
    bool OpenHandsAlwaysApproveConfirmed = false);

public sealed record ReorderQueueRequest(Guid ProjectId, IReadOnlyList<Guid> RequestIds);

public sealed record QueueAttachmentDto(
    string Name,
    string ContentType,
    long Size,
    string ContentBase64,
    string? StorageName = null);

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
    DateTimeOffset? FinishedAt,
    ExecutionRunner ExecutionRunner,
    Guid? ProviderProfileId,
    string? ProviderProfileName,
    AiProviderSource? ProviderSource,
    string? OpenHandsConversationId);

public sealed record CodexRequestDto(
    Guid Id,
    Guid ProjectId,
    Guid? QueueTabId,
    string? QueueTabName,
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
    int QueueOrder,
    QueueStatus Status,
    bool GenerateCommit,
    bool SeparateCommitSession,
    PermissionMode PermissionMode,
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
    IReadOnlyList<CodexRunDto> Runs,
    ExecutionRunner ExecutionRunner,
    Guid? ProviderProfileId,
    string? ProviderProfileName,
    AiProviderSource? ProviderSource,
    string? QueueWaitReason,
    bool OpenHandsAlwaysApproveConfirmed);

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
    string? CommitSha,
    ExecutionRunner ExecutionRunner,
    string? ProviderProfileName,
    AiProviderSource? ProviderSource,
    string? OpenHandsConversationId);

public sealed record FileTreeEntryDto(string Name, string Path, bool IsDirectory, long? Size);

public sealed record FileContentDto(string Path, string Content, long Size, bool Truncated);

public sealed record TerminalCommandRequest(string Command);

public sealed record TerminalCommandDto(bool Success, string Output, int ExitCode, string CommandPreview);

public sealed record GitFileChangeDto(string Path, string Status, bool Staged, bool Unstaged);

public sealed record GitStatusDto(string Branch, bool IsClean, IReadOnlyList<GitFileChangeDto> Changes, string DiffStat, string Output);

public sealed record GitCommitRequest(string Message);

public sealed record GitCommitDto(bool Success, string Output, int ExitCode, string CommandPreview, string? CommitSha);

public sealed record CodexGitCommitRequest(string Model, string? ModelEffort, string? ModelSpeed);

public sealed record SuggestGitCommitMessageRequest(string Model, string? ModelEffort, string? ModelSpeed);

public sealed record SuggestGitCommitMessageDto(string Message, string Output);

public sealed record ModelOptionDto(string Label, string Model, bool SupportsPriority);

public sealed record ApiConfigDto(bool RequiresToken, IReadOnlyList<ModelOptionDto> Models);

public sealed record MachineTestDto(bool Success, string Output);

public sealed record OpenHandsMachineCheckDto(
    bool Available,
    string? Version,
    bool RequiresWsl,
    string Message,
    bool TargetLocalAiChecked = false,
    bool? TargetLocalAiReachable = null,
    bool? TargetSelectedModelAvailable = null,
    string? TargetLocalAiMessage = null);

public sealed record AiProviderProfileDto(
    Guid Id,
    string Name,
    AiProviderSource Source,
    string BaseUrl,
    ModelDiscoveryMode ModelDiscoveryMode,
    string? ApiKeyEnvironmentVariable,
    bool Enabled,
    int MaximumConcurrency,
    int? ConfiguredContextWindow,
    string? DefaultModel,
    ProviderHealthStatus LastHealthStatus,
    DateTimeOffset? LastHealthAt,
    string? LastHealthError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SaveAiProviderProfileRequest(
    string Name,
    AiProviderSource Source,
    string BaseUrl,
    ModelDiscoveryMode ModelDiscoveryMode,
    string? ApiKeyEnvironmentVariable,
    bool Enabled,
    int MaximumConcurrency,
    int? ConfiguredContextWindow,
    string? DefaultModel);

public sealed record AiProviderModelDto(
    string Id,
    string Name,
    int? MaximumContextWindow = null,
    bool SupportsTools = false,
    bool SupportsReasoning = false,
    bool SupportsReasoningEffort = false);

public sealed record AiProviderModelsDto(
    Guid ProfileId,
    bool Healthy,
    ProviderHealthStatus Status,
    string? Error,
    DateTimeOffset CheckedAt,
    int? ConfiguredContextWindow,
    string? ContextWarning,
    IReadOnlyList<AiProviderModelDto> Models);

public sealed record RateLimitWindowDto(int UsedPercent, int? WindowDurationMins, long? ResetsAt);

public sealed record RateLimitDto(
    string Id,
    string Name,
    RateLimitWindowDto? Primary,
    RateLimitWindowDto? Secondary,
    string? RateLimitReachedType);

public sealed record MachineRateLimitsDto(
    Guid MachineId,
    string MachineName,
    bool Available,
    string? Error,
    IReadOnlyList<RateLimitDto> Limits);

public sealed record QueueWorkerDiagnosticsDto(
    DateTimeOffset? LastHeartbeat,
    DateTimeOffset? LastDispatch,
    DateTimeOffset? LastIdle,
    string? LastError,
    IReadOnlyCollection<Guid> ActiveRequestIds,
    bool IsProcessing);
