using CodexQueue.Api.Services;
using CodexQueue.Api.Domain;

namespace CodexQueue.Api.Tests;

public sealed class ProviderConcurrencyGateTests
{
    [Fact]
    public void TryAcquire_EnforcesSharedMaximumAndReleasesCapacity()
    {
        var gate = new ProviderConcurrencyGate();

        Assert.True(gate.TryAcquire("local-profile", 1, out var firstLease));
        Assert.NotNull(firstLease);
        Assert.Equal(1, gate.ActiveCount("LOCAL-PROFILE"));
        Assert.False(gate.TryAcquire("LOCAL-PROFILE", 1, out var blockedLease));
        Assert.Null(blockedLease);

        firstLease.Dispose();

        Assert.Equal(0, gate.ActiveCount("local-profile"));
        Assert.True(gate.TryAcquire("local-profile", 1, out var nextLease));
        nextLease!.Dispose();
    }

    [Fact]
    public void Lease_DisposeIsIdempotent()
    {
        var gate = new ProviderConcurrencyGate();
        Assert.True(gate.TryAcquire("local-profile", 2, out var firstLease));
        Assert.True(gate.TryAcquire("local-profile", 2, out var secondLease));

        firstLease!.Dispose();
        firstLease.Dispose();

        Assert.Equal(1, gate.ActiveCount("local-profile"));
        secondLease!.Dispose();
        Assert.Equal(0, gate.ActiveCount("local-profile"));
    }

    [Fact]
    public void TryAcquire_IsIndependentAcrossProviderResources()
    {
        var gate = new ProviderConcurrencyGate();

        Assert.True(gate.TryAcquire("local-a", 1, out var first));
        Assert.True(gate.TryAcquire("local-b", 1, out var second));

        first!.Dispose();
        second!.Dispose();
    }

    [Fact]
    public void LocalProfilesForSameServerShareCapacityAcrossMachines()
    {
        var firstProfile = new AiProviderProfile
        {
            Source = AiProviderSource.Local,
            BaseUrl = "http://OLLAMA.test:11434/v1",
        };
        var secondProfile = new AiProviderProfile
        {
            Source = AiProviderSource.Local,
            BaseUrl = "http://ollama.test:11434/v1/",
        };
        var firstKey = QueueWorker.ProviderConcurrencyKey(firstProfile);
        var secondKey = QueueWorker.ProviderConcurrencyKey(secondProfile);
        var gate = new ProviderConcurrencyGate();

        Assert.Equal(firstKey, secondKey, ignoreCase: true);
        Assert.True(gate.TryAcquire(firstKey, 1, out var firstMachineLease));
        Assert.False(gate.TryAcquire(secondKey, 1, out var secondMachineLease));
        Assert.Null(secondMachineLease);

        firstMachineLease!.Dispose();
    }

    [Fact]
    public void SharedProviderUsesMostRestrictiveConfiguredConcurrency()
    {
        var profiles = new[]
        {
            new AiProviderProfile
            {
                Source = AiProviderSource.Local,
                BaseUrl = "http://ollama.test:11434/v1",
                MaximumConcurrency = 3,
            },
            new AiProviderProfile
            {
                Source = AiProviderSource.Local,
                BaseUrl = "http://ollama.test:11434/v1",
                MaximumConcurrency = 1,
            },
        };

        var limits = QueueWorker.BuildProviderConcurrencyLimits(profiles);

        Assert.Equal(1, limits[QueueWorker.ProviderConcurrencyKey(profiles[0])]);
    }
}
