using CodexQueue.Api.Domain;

namespace CodexQueue.Api.Services;

public static class LocalCodexPermissionPolicy
{
    public static string? Validate(
        PermissionMode permissionMode,
        bool alwaysApproveConfirmed) =>
        permissionMode != PermissionMode.FullAccess
            ? "Local Codex requires Full access. Select Full access before queueing a Local coding task."
            : !alwaysApproveConfirmed
                ? "Confirm that Local Codex may run with Full access on the selected machine."
                : null;
}
