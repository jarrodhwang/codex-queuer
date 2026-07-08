using CodexQueue.Api.Domain;

namespace CodexQueue.Api.Services;

public static class DefaultPaths
{
    private const string DefaultMachineName = "Local shell";

    public static TargetMachine DefaultMachine()
    {
        var kind = DefaultMachineKind();
        var platform = DefaultMachinePlatform();
        return new TargetMachine
        {
            Name = TrimToNull(Environment.GetEnvironmentVariable("CQ_DEFAULT_MACHINE_NAME")) ?? DefaultMachineName,
            Kind = kind,
            Host = TrimToNull(Environment.GetEnvironmentVariable("CQ_DEFAULT_SSH_HOST")),
            Port = DefaultSshPort(),
            UserName = TrimToNull(Environment.GetEnvironmentVariable("CQ_DEFAULT_SSH_USER")),
            SshKeyPath = TrimToNull(Environment.GetEnvironmentVariable("CQ_DEFAULT_SSH_KEY_PATH")),
            WorkingRoot = DefaultWorkingRoot(kind, platform),
            Platform = platform
        };
    }

    public static string LocalWorkingRoot()
    {
        var configuredLocal = TrimToNull(Environment.GetEnvironmentVariable("CQ_DEFAULT_LOCAL_WORKING_ROOT"));
        if (configuredLocal is not null)
        {
            return configuredLocal;
        }

        var configuredMount = TrimToNull(Environment.GetEnvironmentVariable("CQ_CONTAINER_HOST_MOUNT_ROOT"));
        if (configuredMount is not null)
        {
            return configuredMount;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return userProfile;
        }

        if (!OperatingSystem.IsWindows())
        {
            return "/";
        }

        var systemPath = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return Path.GetPathRoot(systemPath) ?? "C:\\";
    }

    public static string DefaultWorkingRoot(MachineKind kind, MachinePlatform platform)
    {
        if (kind == MachineKind.Local)
        {
            return LocalWorkingRoot();
        }

        if (kind == MachineKind.Ssh && platform == MachinePlatform.Windows)
        {
            return "C:\\Users";
        }

        if (kind == MachineKind.Ssh && platform == MachinePlatform.MacOs)
        {
            return "/Users";
        }

        var configured = TrimToNull(Environment.GetEnvironmentVariable("CQ_DEFAULT_WORKING_ROOT"));
        if (configured is not null)
        {
            return configured;
        }

        return LocalWorkingRoot();
    }

    public static bool IsOldLocalDefault(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        var oldHomeRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.Equals(path, "/home/app", StringComparison.Ordinal)
            || string.Equals(path, "/home", StringComparison.Ordinal)
            || string.Equals(path, "/home/jarrod", StringComparison.Ordinal)
            || string.Equals(path, ContainerHostMountRoot(), StringComparison.Ordinal)
            || string.Equals(path, "/", StringComparison.Ordinal)
            || string.Equals(path, oldHomeRoot, StringComparison.Ordinal);
    }

    public static bool IsDefaultMachineName(string name)
    {
        var configuredName = TrimToNull(Environment.GetEnvironmentVariable("CQ_DEFAULT_MACHINE_NAME"));
        return string.Equals(name, DefaultMachineName, StringComparison.Ordinal)
            || (configuredName is not null && string.Equals(name, configuredName, StringComparison.Ordinal));
    }

    public static string ContainerHostMountRoot()
    {
        var configured = Environment.GetEnvironmentVariable("CQ_CONTAINER_HOST_MOUNT_ROOT");
        return string.IsNullOrWhiteSpace(configured) ? "/host/home/jarrod" : configured.Trim();
    }

    public static bool TargetsWindows(this TargetMachine machine)
    {
        if (machine.Platform == MachinePlatform.Windows)
        {
            return true;
        }

        if (machine.Platform is MachinePlatform.Linux or MachinePlatform.MacOs)
        {
            return false;
        }

        if (machine.Kind == MachineKind.Local)
        {
            return OperatingSystem.IsWindows();
        }

        var root = machine.WorkingRoot ?? "";
        return root.Contains('\\', StringComparison.Ordinal)
            || (root.Length >= 3 && char.IsLetter(root[0]) && root[1] == ':' && (root[2] == '\\' || root[2] == '/'));
    }

    private static MachineKind DefaultMachineKind()
    {
        var configured = Environment.GetEnvironmentVariable("CQ_DEFAULT_MACHINE_KIND");
        return Enum.TryParse<MachineKind>(configured, ignoreCase: true, out var parsed) ? parsed : MachineKind.Local;
    }

    private static MachinePlatform DefaultMachinePlatform()
    {
        var configured = Environment.GetEnvironmentVariable("CQ_DEFAULT_MACHINE_PLATFORM");
        return Enum.TryParse<MachinePlatform>(configured, ignoreCase: true, out var parsed) ? parsed : MachinePlatform.Auto;
    }

    private static int DefaultSshPort()
    {
        var configured = Environment.GetEnvironmentVariable("CQ_DEFAULT_SSH_PORT");
        return int.TryParse(configured, out var parsed) && parsed is >= 1 and <= 65535 ? parsed : 22;
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
