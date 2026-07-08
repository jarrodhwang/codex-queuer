using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexQueue.Api.Domain;

namespace CodexQueue.Api.Services;

public interface ITargetCommandRunner
{
    Task<CommandResult> RunCodexAsync(
        TargetMachine machine,
        string projectPath,
        string model,
        string? modelEffort,
        string? modelSpeed,
        string? codexSessionId,
        IReadOnlyList<string>? imagePaths,
        string prompt,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken);

    Task<CommandResult> RunShellAsync(
        TargetMachine machine,
        string projectPath,
        string shellCommand,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken);

    Task<CommandResult> TestMachineAsync(
        TargetMachine machine,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken);
}

public sealed class TargetCommandRunner(ILogger<TargetCommandRunner> logger) : ITargetCommandRunner
{
    private const string UnixRemotePathPrefix = "export PATH=\"$HOME/.local/bin:$HOME/bin:$PATH\";";

    public Task<CommandResult> RunCodexAsync(
        TargetMachine machine,
        string projectPath,
        string model,
        string? modelEffort,
        string? modelSpeed,
        string? codexSessionId,
        IReadOnlyList<string>? imagePaths,
        string prompt,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (machine.Kind == MachineKind.Local)
        {
            var arguments = BuildCodexArguments(projectPath, model, modelEffort, modelSpeed, codexSessionId, imagePaths, prompt);
            return RunProcessAsync("codex", arguments, projectPath, BuildCodexPreview(model, modelEffort, modelSpeed, codexSessionId), onOutput, cancellationToken);
        }

        if (machine.TargetsWindows())
        {
            var windowsCommand = "Set-Location -LiteralPath " + QuotePowerShellValue(projectPath) + "; "
                + "codex " + string.Join(" ", BuildCodexArguments(projectPath, model, modelEffort, modelSpeed, codexSessionId, imagePaths, prompt).Select(QuotePowerShellValue));

            return RunSshAsync(machine, BuildPowerShellRemoteCommand(windowsCommand), "ssh " + machine.Host + " " + BuildCodexPreview(model, modelEffort, modelSpeed, codexSessionId), onOutput, cancellationToken);
        }

        var remoteCommand = string.Join(" ", new[]
        {
            UnixRemotePathPrefix,
            "cd",
            Quote(projectPath),
            "&&",
            "codex",
            string.Join(" ", BuildCodexArguments(projectPath, model, modelEffort, modelSpeed, codexSessionId, imagePaths, prompt).Select(Quote))
        });

        return RunSshAsync(machine, remoteCommand, "ssh " + machine.Host + " " + BuildCodexPreview(model, modelEffort, modelSpeed, codexSessionId), onOutput, cancellationToken);
    }

    public Task<CommandResult> RunShellAsync(
        TargetMachine machine,
        string projectPath,
        string shellCommand,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (machine.Kind == MachineKind.Local)
        {
            if (machine.TargetsWindows())
            {
                var command = string.IsNullOrWhiteSpace(projectPath)
                    ? shellCommand
                    : "Set-Location -LiteralPath " + QuotePowerShellValue(projectPath) + "; " + shellCommand;
                return RunProcessAsync(
                    "powershell",
                    new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command },
                    null,
                    shellCommand,
                    onOutput,
                    cancellationToken);
            }

            return RunProcessAsync(
                "/bin/bash",
                new[] { "-lc", shellCommand },
                projectPath,
                shellCommand,
                onOutput,
                cancellationToken);
        }

        if (machine.TargetsWindows())
        {
            var command = string.IsNullOrWhiteSpace(projectPath)
                ? shellCommand
                : "Set-Location -LiteralPath " + QuotePowerShellValue(projectPath) + "; " + shellCommand;
            var windowsRemoteCommand = BuildPowerShellRemoteCommand(command);
            return RunSshAsync(machine, windowsRemoteCommand, "ssh " + machine.Host + " " + shellCommand, onOutput, cancellationToken);
        }

        var remoteCommand = UnixRemotePathPrefix + " cd " + Quote(projectPath) + " && " + shellCommand;
        return RunSshAsync(machine, remoteCommand, "ssh " + machine.Host + " " + shellCommand, onOutput, cancellationToken);
    }

    public Task<CommandResult> TestMachineAsync(
        TargetMachine machine,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (machine.Kind == MachineKind.Local)
        {
            return RunProcessAsync("codex", new[] { "--version" }, null, "codex --version", onOutput, cancellationToken);
        }

        if (machine.TargetsWindows())
        {
            return RunSshAsync(machine, BuildPowerShellRemoteCommand("codex --version; Get-Location"), "ssh " + machine.Host + " codex --version", onOutput, cancellationToken);
        }

        return RunSshAsync(machine, UnixRemotePathPrefix + " codex --version && pwd", "ssh " + machine.Host + " codex --version", onOutput, cancellationToken);
    }

    private Task<CommandResult> RunSshAsync(
        TargetMachine machine,
        string remoteCommand,
        string preview,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(machine.Host))
        {
            throw new InvalidOperationException("SSH machine host is required.");
        }

        var destination = string.IsNullOrWhiteSpace(machine.UserName)
            ? machine.Host
            : machine.UserName + "@" + machine.Host;

        var arguments = new List<string>
        {
            "-o",
            "BatchMode=yes",
            "-o",
            "StrictHostKeyChecking=accept-new",
            "-p",
            machine.Port.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(machine.SshKeyPath))
        {
            var keyPath = ResolveSshKeyPath(machine.SshKeyPath);
            if (!File.Exists(keyPath))
            {
                throw new InvalidOperationException("SSH key file is not accessible inside the API runtime: " + keyPath + ". Check the machine SSH key path and the Docker SSH mount.");
            }

            arguments.Add("-i");
            arguments.Add(keyPath);
        }

        arguments.Add(destination);
        arguments.Add(remoteCommand);
        return RunProcessAsync("ssh", arguments, null, preview, onOutput, cancellationToken);
    }

    private async Task<CommandResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        string preview,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start process " + fileName + ".");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start {FileName}", fileName);
            throw;
        }

        await onOutput("$ " + preview + Environment.NewLine);

        async Task ReadStreamAsync(StreamReader reader)
        {
            var buffer = new char[4096];
            while (true)
            {
                var read = await reader.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                var chunk = new string(buffer, 0, read);
                output.Append(chunk);
                await onOutput(chunk);
            }
        }

        var stdout = ReadStreamAsync(process.StandardOutput);
        var stderr = ReadStreamAsync(process.StandardError);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var outputText = output.ToString();
        return new CommandResult(process.ExitCode, outputText, preview, ExtractCodexSessionId(outputText));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cancellation. The queue worker records the run as cancelled.
        }
    }

    public static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    public static string QuotePowerShellValue(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string ResolveSshKeyPath(string configuredPath)
    {
        var trimmed = configuredPath.Trim();
        var fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(fileName) || File.Exists(trimmed))
        {
            return trimmed;
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            var homeCandidate = Path.Combine(home, ".ssh", fileName);
            if (File.Exists(homeCandidate))
            {
                return homeCandidate;
            }
        }

        var mountedCandidate = Path.Combine("/home/app/.ssh", fileName);
        return File.Exists(mountedCandidate) ? mountedCandidate : trimmed;
    }

    private static string BuildPowerShellRemoteCommand(string command)
    {
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        return "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encodedCommand;
    }

    private static IEnumerable<string> BuildModelConfigArguments(string? modelEffort, string? modelSpeed)
    {
        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(modelEffort))
        {
            arguments.Add("-c");
            arguments.Add("model_reasoning_effort=\"" + modelEffort + "\"");
        }

        if (string.Equals(modelSpeed, "priority", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("-c");
            arguments.Add("service_tier=\"priority\"");
        }

        return arguments;
    }

    private static IReadOnlyList<string> BuildCodexArguments(
        string projectPath,
        string model,
        string? modelEffort,
        string? modelSpeed,
        string? codexSessionId,
        IReadOnlyList<string>? imagePaths,
        string prompt)
    {
        var arguments = new List<string> { "exec" };

        if (!string.IsNullOrWhiteSpace(codexSessionId))
        {
            arguments.Add("resume");
        }

        arguments.Add("--json");
        if (string.IsNullOrWhiteSpace(codexSessionId))
        {
            arguments.Add("--color");
            arguments.Add("never");
        }

        arguments.AddRange(BuildModelConfigArguments(modelEffort, modelSpeed));
        arguments.Add("-m");
        arguments.Add(model);
        arguments.Add("--skip-git-repo-check");

        foreach (var imagePath in imagePaths ?? Array.Empty<string>())
        {
            arguments.Add("-i");
            arguments.Add(imagePath);
        }

        if (string.IsNullOrWhiteSpace(codexSessionId))
        {
            arguments.Add("-C");
            arguments.Add(projectPath);
            arguments.Add("-c");
            arguments.Add("approval_policy=\"never\"");
            arguments.Add("-s");
            arguments.Add("workspace-write");
        }
        else
        {
            arguments.Add("-c");
            arguments.Add("approval_policy=\"never\"");
            arguments.Add("-c");
            arguments.Add("sandbox_mode=\"workspace-write\"");
            arguments.Add(codexSessionId);
        }

        arguments.Add(prompt);
        return arguments;
    }

    private static string? ExtractCodexSessionId(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type)
                    && type.GetString() == "thread.started"
                    && root.TryGetProperty("thread_id", out var threadId))
                {
                    return threadId.GetString();
                }
            }
            catch (JsonException)
            {
                // Non-JSON progress or mixed stderr can appear in the combined stream.
            }
        }

        return null;
    }

    private static string BuildCodexPreview(string model, string? modelEffort, string? modelSpeed, string? codexSessionId)
    {
        var parts = new List<string> { string.IsNullOrWhiteSpace(codexSessionId) ? "codex exec -m " + model : "codex exec resume -m " + model };
        if (!string.IsNullOrWhiteSpace(modelEffort))
        {
            parts.Add("-c model_reasoning_effort=\"" + modelEffort + "\"");
        }

        if (string.Equals(modelSpeed, "priority", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("--speed x1.5");
        }

        if (!string.IsNullOrWhiteSpace(codexSessionId))
        {
            parts.Add(codexSessionId[..Math.Min(codexSessionId.Length, 12)]);
        }

        return string.Join(" ", parts);
    }
}
