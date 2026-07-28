using CodexQueue.Api.Domain;
using CodexQueue.Api.Endpoints;

namespace CodexQueue.Api.Tests;

public sealed class LocalCodexProjectPathValidationTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/.")]
    [InlineData("/./")]
    [InlineData("/..")]
    [InlineData("/repo/..")]
    public void LocalRootAliases_AreRejected(string path)
    {
        var machine = new TargetMachine
        {
            Kind = MachineKind.Local,
            Platform = OperatingSystem.IsWindows()
                ? MachinePlatform.Windows
                : MachinePlatform.Linux,
        };

        Assert.False(ApiEndpoints.IsLocalCodexProjectPathScoped(machine, path));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/.")]
    [InlineData("/./")]
    [InlineData("/..")]
    [InlineData("/repo/..")]
    [InlineData("relative/repository")]
    public void RemoteRootAliasesAndRelativePaths_AreRejected(string path)
    {
        var machine = new TargetMachine
        {
            Kind = MachineKind.Ssh,
            Platform = MachinePlatform.Linux,
        };

        Assert.False(ApiEndpoints.IsLocalCodexProjectPathScoped(machine, path));
    }

    [Theory]
    [InlineData("/repo")]
    [InlineData("/srv/projects/example")]
    [InlineData("/srv/../repo")]
    [InlineData("/../repo")]
    public void RemoteScopedAbsolutePaths_AreAccepted(string path)
    {
        var machine = new TargetMachine
        {
            Kind = MachineKind.Ssh,
            Platform = MachinePlatform.Linux,
        };

        Assert.True(ApiEndpoints.IsLocalCodexProjectPathScoped(machine, path));
    }
}
