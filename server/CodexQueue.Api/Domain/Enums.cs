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
