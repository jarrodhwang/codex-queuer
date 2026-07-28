using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexQueue.Api.Tests;

public sealed class MachineResourceTelemetryServiceTests
{
    [Fact]
    public void Parse_ReturnsCpuMemoryTemperaturesPowerAndNvidiaGpu()
    {
        var result = new ResourceTelemetryCommandResult(
            0,
            """
            CQ_CPU|23.46
            CQ_MEM|8589934592|17179869184|50.0
            CQ_TEMP|coretemp|Package id 0|72125
            CQ_TEMP|acpitz|System|41500
            CQ_SYSPOWER|battery discharge|45600000
            CQ_NVIDIA_GPU|0| NVIDIA RTX 4090 |87|1024|4096|67|125.44|
            """,
            "");

        var telemetry = MachineResourceTelemetryService.Parse(result);

        Assert.True(telemetry.Available);
        Assert.Null(telemetry.Error);
        Assert.Equal(23.5, telemetry.CpuUsagePercent);
        Assert.Equal(50.0, telemetry.MemoryUsagePercent);
        Assert.Equal(8_589_934_592, telemetry.MemoryUsedBytes);
        Assert.Equal(17_179_869_184, telemetry.MemoryTotalBytes);
        Assert.Equal(72.1, telemetry.CpuTemperatureCelsius);
        Assert.Equal(41.5, telemetry.SystemTemperatureCelsius);
        Assert.Equal(45.6, telemetry.SystemPowerWatts);
        Assert.Equal("battery discharge", telemetry.SystemPowerSource);

        var gpu = Assert.Single(telemetry.Gpus);
        Assert.Equal(0, gpu.Index);
        Assert.Equal("NVIDIA RTX 4090", gpu.Name);
        Assert.Equal(87.0, gpu.UtilizationPercent);
        Assert.Equal(25.0, gpu.MemoryUsagePercent);
        Assert.Equal(1_073_741_824, gpu.MemoryUsedBytes);
        Assert.Equal(4_294_967_296, gpu.MemoryTotalBytes);
        Assert.Equal(67.0, gpu.TemperatureCelsius);
        Assert.Equal(125.4, gpu.PowerWatts);
    }

    [Fact]
    public void Parse_PrefersPsysPowerAndHandlesDrmGpuUnits()
    {
        var result = new ResourceTelemetryCommandResult(
            0,
            """
            CQ_SYSPOWER|battery discharge|42000000
            CQ_SYSPOWER|coretemp Psys|83000000
            CQ_DRM_GPU|1|AMD GPU 1|42|2147483648|8589934592|63500|112500000|
            """,
            "");

        var telemetry = MachineResourceTelemetryService.Parse(result);

        Assert.True(telemetry.Available);
        Assert.Equal(83.0, telemetry.SystemPowerWatts);
        Assert.Equal("coretemp Psys", telemetry.SystemPowerSource);
        var gpu = Assert.Single(telemetry.Gpus);
        Assert.Equal(42.0, gpu.UtilizationPercent);
        Assert.Equal(25.0, gpu.MemoryUsagePercent);
        Assert.Equal(63.5, gpu.TemperatureCelsius);
        Assert.Equal(112.5, gpu.PowerWatts);
    }

    [Fact]
    public void Parse_ReturnsMacCpuMemoryAndAppleGpuUtilization()
    {
        var result = new ResourceTelemetryCommandResult(
            0,
            """
            CQ_CPU_INFO|Apple M4 Max
            CQ_CPU|31.7
            CQ_MEM_INFO|64 GB unified memory
            CQ_MEM|27487790694|68719476736|40.0
            CQ_DRM_GPU|0|Apple M4 Max|54|||||
            """,
            "");

        var telemetry = MachineResourceTelemetryService.Parse(result);

        Assert.True(telemetry.Available);
        Assert.Equal("Apple M4 Max", telemetry.CpuName);
        Assert.Equal(31.7, telemetry.CpuUsagePercent);
        Assert.Equal("64 GB unified memory", telemetry.MemoryName);
        Assert.Equal(40.0, telemetry.MemoryUsagePercent);
        var gpu = Assert.Single(telemetry.Gpus);
        Assert.Equal("Apple M4 Max", gpu.Name);
        Assert.Equal(54.0, gpu.UtilizationPercent);
    }

    [Fact]
    public void Parse_ReturnsGracefulErrorWhenNoMetricsAreAvailable()
    {
        var result = new ResourceTelemetryCommandResult(
            255,
            "CQ_UNSUPPORTED|Resource monitoring currently supports Linux machines.\n",
            "remote diagnostic details");

        var telemetry = MachineResourceTelemetryService.Parse(result);

        Assert.False(telemetry.Available);
        Assert.Equal("Resource monitoring currently supports Linux machines.", telemetry.Error);
        Assert.Empty(telemetry.Gpus);
        Assert.Null(telemetry.CpuUsagePercent);
    }

    [Fact]
    public async Task CollectAsync_ExecutesCommandsForWindowsMachines()
    {
        var executor = new RecordingExecutor();
        var service = new MachineResourceTelemetryService(executor);
        var machine = new TargetMachine
        {
            Kind = MachineKind.Ssh,
            Platform = MachinePlatform.Windows,
        };

        var telemetry = await service.CollectAsync(machine, CancellationToken.None);

        Assert.True(telemetry.Available);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task CollectAsync_CoalescesConcurrentCollectionsForTheSameMachine()
    {
        var executor = new BlockingExecutor(
            new ResourceTelemetryCommandResult(0, "CQ_CPU|42\n", ""));
        var service = new MachineResourceTelemetryService(executor);
        var machine = new TargetMachine
        {
            Id = Guid.NewGuid(),
            Kind = MachineKind.Ssh,
            Platform = MachinePlatform.Linux,
        };

        var firstCollection = service.CollectAsync(machine, CancellationToken.None);
        await executor.Started;
        var secondCollection = service.CollectAsync(machine, CancellationToken.None);
        executor.Release();

        var telemetry = await Task.WhenAll(firstCollection, secondCollection);

        Assert.Equal(1, executor.CallCount);
        Assert.Same(telemetry[0], telemetry[1]);
        Assert.Equal(42.0, telemetry[0].CpuUsagePercent);
    }

    [Fact]
    public async Task CollectAsync_CachesUnavailableSamples()
    {
        var executor = new RecordingExecutor(
            new ResourceTelemetryCommandResult(
                0,
                "CQ_UNSUPPORTED|No supported resource sensors were found.\n",
                ""));
        var service = new MachineResourceTelemetryService(executor);
        var machine = new TargetMachine
        {
            Id = Guid.NewGuid(),
            Kind = MachineKind.Ssh,
            Platform = MachinePlatform.Linux,
        };

        var first = await service.CollectAsync(machine, CancellationToken.None);
        var second = await service.CollectAsync(machine, CancellationToken.None);

        Assert.False(first.Available);
        Assert.Equal(1, executor.CallCount);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task CollectAsync_InvalidatesCacheWhenMachineIsUpdated()
    {
        var executor = new RecordingExecutor();
        var service = new MachineResourceTelemetryService(executor);
        var machine = new TargetMachine
        {
            Id = Guid.NewGuid(),
            Kind = MachineKind.Ssh,
            Platform = MachinePlatform.Linux,
        };

        var first = await service.CollectAsync(machine, CancellationToken.None);
        machine.UpdatedAt = machine.UpdatedAt.AddSeconds(1);
        var second = await service.CollectAsync(machine, CancellationToken.None);

        Assert.Equal(2, executor.CallCount);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task CollectAsync_BoundsCollectionsAcrossDifferentMachines()
    {
        var executor = new ConcurrencyRecordingExecutor(
            new ResourceTelemetryCommandResult(0, "CQ_CPU|42\n", ""),
            expectedConcurrentCalls: 4);
        var service = new MachineResourceTelemetryService(executor);
        var collections = Enumerable.Range(0, 5)
            .Select(_ => service.CollectAsync(
                new TargetMachine
                {
                    Id = Guid.NewGuid(),
                    Kind = MachineKind.Ssh,
                    Platform = MachinePlatform.Linux,
                },
                CancellationToken.None))
            .ToArray();

        await executor.ExpectedCallsStarted.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4, executor.CallCount);

        executor.Release();
        await Task.WhenAll(collections);

        Assert.Equal(5, executor.CallCount);
        Assert.Equal(4, executor.MaximumConcurrentCalls);
    }

    [Fact]
    public void BuildSshStartInfo_UsesFixedBoundedConnectionOptions()
    {
        var machine = new TargetMachine
        {
            Kind = MachineKind.Ssh,
            Platform = MachinePlatform.Linux,
            Host = "zbook.example.test",
            UserName = "agent_user",
            Port = 2222,
        };

        var startInfo = ResourceTelemetryCommandExecutor.BuildSshStartInfo(machine);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Equal("ssh", startInfo.FileName);
        Assert.Contains("BatchMode=yes", arguments);
        Assert.Contains("ConnectTimeout=3", arguments);
        Assert.Contains("ConnectionAttempts=1", arguments);
        Assert.Contains("ServerAliveCountMax=1", arguments);
        Assert.Contains("2222", arguments);
        Assert.Contains("agent_user@zbook.example.test", arguments);
        Assert.StartsWith("LC_ALL=C /bin/sh -c ", arguments[^1]);
    }

    [Fact]
    public void BuildSshStartInfo_AutoPlatformDispatchesToMacCollector()
    {
        var machine = new TargetMachine
        {
            Kind = MachineKind.Ssh,
            Platform = MachinePlatform.Auto,
            Host = "mac.example.test",
            Port = 22,
        };

        var command = ResourceTelemetryCommandExecutor
            .BuildSshStartInfo(machine)
            .ArgumentList[^1];

        Assert.Contains("Darwin", command);
        Assert.Contains("vm_stat", command);
        Assert.Contains("memory_pressure", command);
        Assert.Contains("AGXAccelerator", command);
    }

    [Fact]
    public void BuildLocalStartInfo_MacOsUsesMacCollector()
    {
        var startInfo = ResourceTelemetryCommandExecutor.BuildLocalStartInfo(
            new TargetMachine
            {
                Kind = MachineKind.Local,
                Platform = MachinePlatform.MacOs,
            });

        Assert.Equal("/bin/sh", startInfo.FileName);
        Assert.Contains("vm_stat", startInfo.ArgumentList[^1]);
        Assert.Contains("system_profiler SPDisplaysDataType", startInfo.ArgumentList[^1]);
    }

    [Theory]
    [InlineData("-oProxyCommand=touch")]
    [InlineData("host;touch")]
    [InlineData("host name")]
    public void BuildSshStartInfo_RejectsUnsafeHost(string host)
    {
        var machine = new TargetMachine
        {
            Kind = MachineKind.Ssh,
            Platform = MachinePlatform.Linux,
            Host = host,
            Port = 22,
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => ResourceTelemetryCommandExecutor.BuildSshStartInfo(machine));

        Assert.Contains("SSH host", exception.Message);
    }

    [Fact]
    public async Task ExecuteAsync_CollectsCoreMetricsOnLocalLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var executor = new ResourceTelemetryCommandExecutor(
            NullLogger<ResourceTelemetryCommandExecutor>.Instance);
        var result = await executor.ExecuteAsync(
            new TargetMachine
            {
                Kind = MachineKind.Local,
                Platform = MachinePlatform.Linux,
            },
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("CQ_CPU|", result.StandardOutput);
        Assert.Contains("CQ_MEM|", result.StandardOutput);
        var telemetry = MachineResourceTelemetryService.Parse(result);
        Assert.True(telemetry.Available, telemetry.Error);
        Assert.NotNull(telemetry.CpuUsagePercent);
        Assert.NotNull(telemetry.MemoryUsagePercent);
    }

    private sealed class RecordingExecutor : IResourceTelemetryCommandExecutor
    {
        private readonly ResourceTelemetryCommandResult result;
        private int callCount;

        public RecordingExecutor(ResourceTelemetryCommandResult? result = null)
        {
            this.result = result
                ?? new ResourceTelemetryCommandResult(0, "CQ_CPU|1\n", "");
        }

        public int CallCount => Volatile.Read(ref callCount);

        public Task<ResourceTelemetryCommandResult> ExecuteAsync(
            TargetMachine machine,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingExecutor(
        ResourceTelemetryCommandResult result) : IResourceTelemetryCommandExecutor
    {
        private readonly TaskCompletionSource started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);
        public Task Started => started.Task;

        public void Release() => release.TrySetResult();

        public async Task<ResourceTelemetryCommandResult> ExecuteAsync(
            TargetMachine machine,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class ConcurrencyRecordingExecutor(
        ResourceTelemetryCommandResult result,
        int expectedConcurrentCalls) : IResourceTelemetryCommandExecutor
    {
        private readonly TaskCompletionSource expectedCallsStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;
        private int concurrentCalls;
        private int maximumConcurrentCalls;

        public int CallCount => Volatile.Read(ref callCount);
        public int MaximumConcurrentCalls => Volatile.Read(ref maximumConcurrentCalls);
        public Task ExpectedCallsStarted => expectedCallsStarted.Task;

        public void Release() => release.TrySetResult();

        public async Task<ResourceTelemetryCommandResult> ExecuteAsync(
            TargetMachine machine,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            var currentConcurrency = Interlocked.Increment(ref concurrentCalls);
            UpdateMaximum(currentConcurrency);
            if (currentConcurrency >= expectedConcurrentCalls)
            {
                expectedCallsStarted.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref concurrentCalls);
            }
        }

        private void UpdateMaximum(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref maximumConcurrentCalls);
                if (candidate <= current
                    || Interlocked.CompareExchange(
                        ref maximumConcurrentCalls,
                        candidate,
                        current) == current)
                {
                    return;
                }
            }
        }
    }
}
