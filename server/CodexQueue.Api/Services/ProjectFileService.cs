using CodexQueue.Api.Domain;
using CodexQueue.Api.Endpoints;

namespace CodexQueue.Api.Services;

public interface IProjectFileService
{
    Task<IReadOnlyList<FileTreeEntryDto>> ListMachineFoldersAsync(TargetMachine machine, string? path, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileTreeEntryDto>> ListAsync(Project project, string? relativePath, CancellationToken cancellationToken);
    Task<FileContentDto> ReadAsync(Project project, string relativePath, CancellationToken cancellationToken);
}

public sealed class ProjectFileService(ITargetCommandRunner runner) : IProjectFileService
{
    private const int MaxEntries = 500;
    private const long MaxFileBytes = 1_000_000;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".svn",
        ".hg",
        "node_modules",
        "bin",
        "obj",
        "dist",
        "build",
        ".next",
        ".cache"
    };

    public async Task<IReadOnlyList<FileTreeEntryDto>> ListMachineFoldersAsync(TargetMachine machine, string? path, CancellationToken cancellationToken)
    {
        if (machine.Kind == MachineKind.Ssh)
        {
            if (machine.TargetsWindows())
            {
                return await ListRemoteWindowsMachineFoldersAsync(machine, path, cancellationToken);
            }

            return await ListRemoteMachineFoldersAsync(machine, path, cancellationToken);
        }

        var root = GetLocalMachineRoot(machine);
        var absolute = ResolveLocalMachinePath(root, path);
        if (!Directory.Exists(absolute))
        {
            return Array.Empty<FileTreeEntryDto>();
        }

        return Directory.EnumerateDirectories(absolute)
            .Select(entry => new DirectoryInfo(entry))
            .Where(info => !IgnoredDirectories.Contains(info.Name))
            .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxEntries)
            .Select(info => new FileTreeEntryDto(info.Name, info.FullName, true, null))
            .ToArray();
    }

    public async Task<IReadOnlyList<FileTreeEntryDto>> ListAsync(Project project, string? relativePath, CancellationToken cancellationToken)
    {
        var path = NormalizeRelativePath(relativePath);
        if (project.Machine?.Kind == MachineKind.Ssh)
        {
            if (project.Machine.TargetsWindows())
            {
                return await ListRemoteWindowsAsync(project, path, cancellationToken);
            }

            return await ListRemoteAsync(project, path, cancellationToken);
        }

        var absolute = ResolveLocalPath(project.Path, path);
        if (!Directory.Exists(absolute))
        {
            return Array.Empty<FileTreeEntryDto>();
        }

        return Directory.EnumerateFileSystemEntries(absolute)
            .Select(entry =>
            {
                var info = new FileInfo(entry);
                var isDirectory = Directory.Exists(entry);
                var name = Path.GetFileName(entry);
                return new FileTreeEntryDto(
                    name,
                    CombineRelative(path, name),
                    isDirectory,
                    isDirectory ? null : info.Length);
            })
            .Where(x => !x.IsDirectory || !IgnoredDirectories.Contains(x.Name))
            .OrderByDescending(x => x.IsDirectory)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxEntries)
            .ToArray();
    }

    public async Task<FileContentDto> ReadAsync(Project project, string relativePath, CancellationToken cancellationToken)
    {
        var path = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("File path is required.");
        }

        if (project.Machine?.Kind == MachineKind.Ssh)
        {
            if (project.Machine.TargetsWindows())
            {
                return await ReadRemoteWindowsAsync(project, path, cancellationToken);
            }

            return await ReadRemoteAsync(project, path, cancellationToken);
        }

        var absolute = ResolveLocalPath(project.Path, path);
        if (!File.Exists(absolute))
        {
            throw new FileNotFoundException("File was not found.", path);
        }

        var info = new FileInfo(absolute);
        var truncated = info.Length > MaxFileBytes;
        await using var stream = File.OpenRead(absolute);
        using var reader = new StreamReader(stream);
        var buffer = new char[Math.Min((int)Math.Min(info.Length, MaxFileBytes), (int)MaxFileBytes)];
        var read = await reader.ReadBlockAsync(buffer, cancellationToken);
        return new FileContentDto(path, new string(buffer, 0, read), info.Length, truncated);
    }

    private async Task<IReadOnlyList<FileTreeEntryDto>> ListRemoteAsync(Project project, string relativePath, CancellationToken cancellationToken)
    {
        var machine = project.Machine ?? throw new InvalidOperationException("Project machine is missing.");
        var absolute = CombineRemotePath(project.Path, relativePath);
        var command = BuildUnixListEntriesCommand(absolute);

        var result = await runner.RunShellAsync(machine, project.Path, command, _ => Task.CompletedTask, cancellationToken);
        if (!result.Success)
        {
            return Array.Empty<FileTreeEntryDto>();
        }

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length >= 3)
            .Select(parts =>
            {
                var name = parts[0];
                var isDirectory = parts[1] == "d";
                var size = long.TryParse(parts[2], out var parsed) ? parsed : (long?)null;
                return new FileTreeEntryDto(name, CombineRelative(relativePath, name), isDirectory, isDirectory ? null : size);
            })
            .Where(x => !x.IsDirectory || !IgnoredDirectories.Contains(x.Name))
            .OrderByDescending(x => x.IsDirectory)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<FileTreeEntryDto>> ListRemoteMachineFoldersAsync(TargetMachine machine, string? path, CancellationToken cancellationToken)
    {
        var root = GetMachineRoot(machine);
        var target = string.IsNullOrWhiteSpace(path) ? root : path.Trim();
        var command = BuildUnixListFoldersCommand(target);

        var result = await runner.RunShellAsync(machine, root, command, _ => Task.CompletedTask, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException("Could not list folders on " + machine.Name + ": " + LastUsefulLine(result.Output));
        }

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length >= 2)
            .Where(parts => !IgnoredDirectories.Contains(parts[0]))
            .OrderBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .Select(parts => new FileTreeEntryDto(parts[0], parts[1], true, null))
            .ToArray();
    }

    private async Task<IReadOnlyList<FileTreeEntryDto>> ListRemoteWindowsMachineFoldersAsync(TargetMachine machine, string? path, CancellationToken cancellationToken)
    {
        var root = GetMachineRoot(machine);
        var target = string.IsNullOrWhiteSpace(path) ? root : path.Trim();
        var targetExpression = TargetCommandRunner.QuotePowerShellValue(target);
        var command = "$target = " + targetExpression + "; "
            + "if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw ('Directory not found: ' + $target) }; "
            + "Get-ChildItem -LiteralPath $target -Directory -Force -ErrorAction SilentlyContinue "
            + "| Select-Object -First " + MaxEntries + " "
            + "| ForEach-Object { Write-Output ($_.Name + \"`t\" + $_.FullName) }";

        var result = await runner.RunShellAsync(machine, root, command, _ => Task.CompletedTask, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException("Could not list folders on " + machine.Name + ": " + LastUsefulLine(result.Output));
        }

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(parts => parts.Length >= 2)
            .Where(parts => !IgnoredDirectories.Contains(parts[0]))
            .OrderBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .Select(parts => new FileTreeEntryDto(parts[0], parts[1], true, null))
            .ToArray();
    }

    private async Task<IReadOnlyList<FileTreeEntryDto>> ListRemoteWindowsAsync(Project project, string relativePath, CancellationToken cancellationToken)
    {
        var machine = project.Machine ?? throw new InvalidOperationException("Project machine is missing.");
        var absolute = CombineWindowsPath(project.Path, relativePath);
        var command = "$target = " + TargetCommandRunner.QuotePowerShellValue(absolute) + "; "
            + "Get-ChildItem -LiteralPath $target -Force -ErrorAction SilentlyContinue "
            + "| Select-Object -First " + MaxEntries + " "
            + "| ForEach-Object { "
            + "$kind = if ($_.PSIsContainer) { 'd' } else { 'f' }; "
            + "$size = if ($_.PSIsContainer) { 0 } else { $_.Length }; "
            + "Write-Output ($_.Name + \"`t\" + $kind + \"`t\" + $size) }";

        var result = await runner.RunShellAsync(machine, project.Path, command, _ => Task.CompletedTask, cancellationToken);
        if (!result.Success)
        {
            return Array.Empty<FileTreeEntryDto>();
        }

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(parts => parts.Length >= 3)
            .Select(parts =>
            {
                var name = parts[0];
                var isDirectory = parts[1] == "d";
                var size = long.TryParse(parts[2], out var parsed) ? parsed : (long?)null;
                return new FileTreeEntryDto(name, CombineRelative(relativePath, name), isDirectory, isDirectory ? null : size);
            })
            .Where(x => !x.IsDirectory || !IgnoredDirectories.Contains(x.Name))
            .OrderByDescending(x => x.IsDirectory)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<FileContentDto> ReadRemoteAsync(Project project, string relativePath, CancellationToken cancellationToken)
    {
        var machine = project.Machine ?? throw new InvalidOperationException("Project machine is missing.");
        var absolute = CombineRemotePath(project.Path, relativePath);
        var quoted = TargetCommandRunner.Quote(absolute);
        var command = "bytes=$(wc -c < " + quoted + "); "
            + "if [ \"$bytes\" -gt " + MaxFileBytes + " ]; then head -c " + MaxFileBytes + " " + quoted + "; else cat " + quoted + "; fi";

        var result = await runner.RunShellAsync(machine, project.Path, command, _ => Task.CompletedTask, cancellationToken);
        if (!result.Success)
        {
            throw new FileNotFoundException("File was not found or could not be read.", relativePath);
        }

        var sizeCommand = "wc -c < " + quoted;
        var sizeResult = await runner.RunShellAsync(machine, project.Path, sizeCommand, _ => Task.CompletedTask, cancellationToken);
        var size = long.TryParse(sizeResult.Output.Trim(), out var parsed) ? parsed : result.Output.Length;
        return new FileContentDto(relativePath, result.Output, size, size > MaxFileBytes);
    }

    private async Task<FileContentDto> ReadRemoteWindowsAsync(Project project, string relativePath, CancellationToken cancellationToken)
    {
        var machine = project.Machine ?? throw new InvalidOperationException("Project machine is missing.");
        var absolute = CombineWindowsPath(project.Path, relativePath);
        var quoted = TargetCommandRunner.QuotePowerShellValue(absolute);
        var command = "$path = " + quoted + "; "
            + "$bytes = [System.IO.File]::ReadAllBytes($path); "
            + "$take = [Math]::Min($bytes.Length, " + MaxFileBytes + "); "
            + "[Console]::OpenStandardOutput().Write($bytes, 0, $take)";

        var result = await runner.RunShellAsync(machine, project.Path, command, _ => Task.CompletedTask, cancellationToken);
        if (!result.Success)
        {
            throw new FileNotFoundException("File was not found or could not be read.", relativePath);
        }

        var sizeCommand = "(Get-Item -LiteralPath " + quoted + ").Length";
        var sizeResult = await runner.RunShellAsync(machine, project.Path, sizeCommand, _ => Task.CompletedTask, cancellationToken);
        var size = long.TryParse(sizeResult.Output.Trim(), out var parsed) ? parsed : result.Output.Length;
        return new FileContentDto(relativePath, result.Output, size, size > MaxFileBytes);
    }

    private static string ResolveLocalPath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullPath.Equals(fullRoot, StringComparison.Ordinal)
            && !fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Path is outside the project root.");
        }

        return fullPath;
    }

    private static string ResolveLocalMachinePath(string root, string? path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = string.IsNullOrWhiteSpace(path)
            ? fullRoot
            : Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(fullRoot, path));
        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullPath.Equals(fullRoot, StringComparison.Ordinal)
            && !fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Path is outside the machine working root.");
        }

        return fullPath;
    }

    private static string GetLocalMachineRoot(TargetMachine machine)
    {
        var root = GetMachineRoot(machine);
        return string.IsNullOrWhiteSpace(root) ? DefaultPaths.LocalWorkingRoot() : root;
    }

    private static string GetMachineRoot(TargetMachine machine) =>
        string.IsNullOrWhiteSpace(machine.WorkingRoot)
            ? DefaultPaths.DefaultWorkingRoot(machine.Kind, machine.Platform)
            : machine.WorkingRoot.Trim();

    private static string NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidOperationException("Invalid relative path.");
        }

        return normalized;
    }

    private static string CombineRelative(string basePath, string name) =>
        string.IsNullOrWhiteSpace(basePath) ? name : basePath.TrimEnd('/') + "/" + name;

    private static string CombineRemotePath(string root, string relativePath) =>
        string.IsNullOrWhiteSpace(relativePath)
            ? root
            : root.TrimEnd('/') + "/" + relativePath.TrimStart('/');

    private static string CombineWindowsPath(string root, string relativePath) =>
        string.IsNullOrWhiteSpace(relativePath)
            ? root
            : root.TrimEnd('\\', '/') + "\\" + relativePath.Replace('/', '\\').TrimStart('\\');

    private static string BuildUnixListFoldersCommand(string target) =>
        "target=" + TargetCommandRunner.Quote(target) + "; "
        + "target=${target%/}; [ -n \"$target\" ] || target=/; "
        + "[ -d \"$target\" ] || { printf 'Directory not found: %s\\n' \"$target\" >&2; exit 2; }; "
        + "prefix=$target; [ \"$target\" = / ] && prefix=; "
        + "for entry in \"$prefix\"/* \"$prefix\"/.[!.]* \"$prefix\"/..?*; do "
        + "[ -d \"$entry\" ] || continue; "
        + "name=${entry##*/}; "
        + "printf '%s\\t%s\\n' \"$name\" \"$entry\"; "
        + "done | sort -f | head -n " + MaxEntries;

    private static string BuildUnixListEntriesCommand(string target) =>
        "target=" + TargetCommandRunner.Quote(target) + "; "
        + "target=${target%/}; [ -n \"$target\" ] || target=/; "
        + "[ -d \"$target\" ] || { printf 'Directory not found: %s\\n' \"$target\" >&2; exit 2; }; "
        + "prefix=$target; [ \"$target\" = / ] && prefix=; "
        + "for entry in \"$prefix\"/* \"$prefix\"/.[!.]* \"$prefix\"/..?*; do "
        + "[ -e \"$entry\" ] || continue; "
        + "name=${entry##*/}; "
        + "if [ -d \"$entry\" ]; then kind=d; size=0; "
        + "elif [ -f \"$entry\" ]; then kind=f; size=$(wc -c < \"$entry\" 2>/dev/null | tr -d '[:space:]') || continue; "
        + "else continue; fi; "
        + "printf '%s\\t%s\\t%s\\n' \"$name\" \"$kind\" \"$size\"; "
        + "done | sort -f | head -n " + MaxEntries;

    private static string LastUsefulLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()
        ?? "No command output.";
}
