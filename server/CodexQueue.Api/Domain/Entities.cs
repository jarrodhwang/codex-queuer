namespace CodexQueue.Api.Domain;

public sealed class TargetMachine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public MachineKind Kind { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 22;
    public string? UserName { get; set; }
    public string? SshKeyPath { get; set; }
    public string? WorkingRoot { get; set; }
    public MachinePlatform Platform { get; set; } = MachinePlatform.Auto;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Project> Projects { get; set; } = new List<Project>();
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? CodexSessionId { get; set; }
    public string? DefaultModel { get; set; }
    public string? DefaultModelEffort { get; set; }
    public string? DefaultModelSpeed { get; set; }
    public string? DefaultCommitModel { get; set; }
    public string? DefaultCommitModelEffort { get; set; }
    public string? DefaultCommitModelSpeed { get; set; }
    public bool DefaultGenerateCommit { get; set; } = true;
    public bool DefaultSeparateCommitSession { get; set; }
    public Guid MachineId { get; set; }
    public TargetMachine? Machine { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<CodexRequest> Requests { get; set; } = new List<CodexRequest>();
    public ICollection<QueueTab> QueueTabs { get; set; } = new List<QueueTab>();
}

public sealed class QueueTab
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public string Name { get; set; } = "";
    public string? CodexSessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<CodexRequest> Requests { get; set; } = new List<CodexRequest>();
}

public sealed class CodexRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid? QueueTabId { get; set; }
    public QueueTab? QueueTab { get; set; }
    public Guid MachineId { get; set; }
    public TargetMachine? Machine { get; set; }
    public string Prompt { get; set; } = "";
    public string? AttachmentsJson { get; set; }
    public string Model { get; set; } = "";
    public string? ModelEffort { get; set; }
    public string? ModelSpeed { get; set; }
    public int QueueOrder { get; set; }
    public QueueStatus Status { get; set; } = QueueStatus.Queued;
    public bool GenerateCommit { get; set; }
    public bool SeparateCommitSession { get; set; }
    public string? CommitModel { get; set; }
    public string? CommitModelEffort { get; set; }
    public string? CommitModelSpeed { get; set; }
    public string? Summary { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? RetryAfter { get; set; }
    public string? RetryReason { get; set; }
    public string? AvailableModel { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<CodexRun> Runs { get; set; } = new List<CodexRun>();
}

public sealed class CodexRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public CodexRequest? Request { get; set; }
    public RunKind Kind { get; set; }
    public string Model { get; set; } = "";
    public string? ModelEffort { get; set; }
    public string? ModelSpeed { get; set; }
    public QueueStatus Status { get; set; } = QueueStatus.Queued;
    public string? CodexSessionId { get; set; }
    public string? CommandPreview { get; set; }
    public string Output { get; set; } = "";
    public int? ExitCode { get; set; }
    public string? CommitMessage { get; set; }
    public string? CommitSha { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? RetryAfter { get; set; }
    public string? RetryReason { get; set; }
    public string? AvailableModel { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
