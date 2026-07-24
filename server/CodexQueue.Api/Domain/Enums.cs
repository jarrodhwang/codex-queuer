namespace CodexQueue.Api.Domain;

public enum MachineKind
{
    Local,
    Ssh
}

public enum MachinePlatform
{
    Auto,
    Linux,
    MacOs,
    Windows
}

public enum QueueStatus
{
    Queued,
    Running,
    UsageLimited,
    Succeeded,
    Failed,
    CancelRequested,
    Cancelled
}

public enum RunKind
{
    Request,
    Commit
}

public enum PermissionMode
{
    ReadOnly,
    AskForApproval,
    ApproveForMe,
    FullAccess
}

public enum ExecutionRunner
{
    CodexCli = 0,
    OpenHandsCli = 1
}

public enum AiProviderSource
{
    OpenAi,
    Anthropic,
    Local
}

public enum ModelDiscoveryMode
{
    Auto,
    Ollama,
    OpenAi
}

public enum ProviderHealthStatus
{
    Unknown,
    Healthy,
    Offline
}
