using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;

namespace CodexQueue.Api.Tests;

public sealed class LocalCodexPermissionTests
{
    [Theory]
    [InlineData(PermissionMode.ReadOnly)]
    [InlineData(PermissionMode.AskForApproval)]
    [InlineData(PermissionMode.ApproveForMe)]
    public void ValidateLocalCodexPermission_RejectsNonFullAccessModes(
        PermissionMode permissionMode)
    {
        var error = LocalCodexPermissionPolicy.Validate(
            permissionMode,
            alwaysApproveConfirmed: true);

        Assert.Contains("Full access", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateLocalCodexPermission_RequiresExplicitConfirmation()
    {
        var error = LocalCodexPermissionPolicy.Validate(
            PermissionMode.FullAccess,
            alwaysApproveConfirmed: false);

        Assert.Contains("Confirm", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateLocalCodexPermission_AllowsConfirmedFullAccess()
    {
        Assert.Null(LocalCodexPermissionPolicy.Validate(
            PermissionMode.FullAccess,
            alwaysApproveConfirmed: true));
    }
}
