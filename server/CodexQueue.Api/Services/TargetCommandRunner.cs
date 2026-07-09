using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexQueue.Api.Domain;

namespace CodexQueue.Api.Services;

public interface ITargetCommandRunner
{
    Task<CommandResult> ReadRateLimitsAsync(
        TargetMachine machine,
        CancellationToken cancellationToken);

    Task<CommandResult> RunCodexAsync(
        TargetMachine machine,
        string projectPath,
        string model,
        string? modelEffort,
        string? modelSpeed,
        string? codexSessionId,
        IReadOnlyList<string>? imagePaths,
        string prompt,
        bool allowGitWrites,
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
    private static readonly TimeSpan CodexFirstOutputTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan RateLimitsTimeout = TimeSpan.FromSeconds(20);

    public Task<CommandResult> ReadRateLimitsAsync(TargetMachine machine, CancellationToken cancellationToken)
    {
        var initialize = JsonSerializer.Serialize(new
        {
            method = "initialize",
            id = 1,
            @params = new
            {
                clientInfo = new { name = "codex-queue", title = "Codex Queue", version = "1.0" },
                capabilities = new { experimentalApi = false }
            }
        });
        var initialized = "{\"method\":\"initialized\"}";
        var readRateLimits = "{\"method\":\"account/rateLimits/read\",\"id\":2}";
        var input = string.Join(Environment.NewLine, new[] { initialize, initialized, readRateLimits }) + Environment.NewLine;

        if (machine.Kind == MachineKind.Local)
        {
            return RunProcessAsync(
                "codex",
                new[] { "app-server", "--stdio" },
                null,
                "codex app-server --stdio (account/rateLimits/read)",
                static _ => Task.CompletedTask,
                cancellationToken,
                firstProcessOutputTimeout: RateLimitsTimeout,
                standardInput: input);
        }

        var remoteCommand = machine.TargetsWindows()
            ? BuildPowerShellRemoteCommand(BuildPowerShellCodexCommandSetup() + "; & $codexCommand app-server --stdio")
            : UnixRemotePathPrefix + " codex app-server --stdio";
        return RunSshAsync(
            machine,
            remoteCommand,
            "ssh " + machine.Host + " codex app-server --stdio (account/rateLimits/read)",
            static _ => Task.CompletedTask,
            cancellationToken,
            firstProcessOutputTimeout: RateLimitsTimeout,
            standardInput: input);
    }

    public Task<CommandResult> RunCodexAsync(
        TargetMachine machine,
        string projectPath,
        string model,
        string? modelEffort,
        string? modelSpeed,
        string? codexSessionId,
        IReadOnlyList<string>? imagePaths,
        string prompt,
        bool allowGitWrites,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (machine.Kind == MachineKind.Local)
        {
            var arguments = BuildCodexArguments(
                projectPath,
                model,
                modelEffort,
                modelSpeed,
                codexSessionId,
                imagePaths,
                allowGitWrites,
                useUnelevatedWindowsSandbox: false);
            if (machine.TargetsWindows())
            {
                var command = BuildPowerShellCodexCommandSetup() + "; & $codexCommand " + string.Join(" ", arguments.Select(QuotePowerShellValue));
                return RunProcessAsync(
                    "powershell",
                    new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command },
                    projectPath,
                    BuildCodexPreview(model, modelEffort, modelSpeed, codexSessionId),
                    onOutput,
                    cancellationToken,
                    firstProcessOutputTimeout: CodexFirstOutputTimeout,
                    standardInput: prompt);
            }

            return RunProcessAsync(
                "codex",
                arguments,
                projectPath,
                BuildCodexPreview(model, modelEffort, modelSpeed, codexSessionId),
                onOutput,
                cancellationToken,
                firstProcessOutputTimeout: CodexFirstOutputTimeout,
                standardInput: prompt);
        }

        if (machine.TargetsWindows())
        {
            var windowsCommand = BuildPowerShellSetLocationCommand(projectPath) + "; "
                + BuildPowerShellCodexCommandSetup() + "; & $codexCommand "
                + string.Join(" ", BuildCodexArguments(
                    projectPath,
                    model,
                    modelEffort,
                    modelSpeed,
                    codexSessionId,
                    imagePaths,
                    allowGitWrites,
                    useUnelevatedWindowsSandbox: true).Select(QuotePowerShellValue));

            return RunSshAsync(
                machine,
                BuildPowerShellRemoteCommand(windowsCommand),
                "ssh " + machine.Host + " " + BuildCodexPreview(model, modelEffort, modelSpeed, codexSessionId),
                onOutput,
                cancellationToken,
                firstProcessOutputTimeout: CodexFirstOutputTimeout,
                standardInput: prompt);
        }

        var remoteCommand = string.Join(" ", new[]
        {
            UnixRemotePathPrefix,
            "cd",
            Quote(projectPath),
            "&&",
            "codex",
            string.Join(" ", BuildCodexArguments(
                projectPath,
                model,
                modelEffort,
                modelSpeed,
                codexSessionId,
                imagePaths,
                allowGitWrites,
                useUnelevatedWindowsSandbox: false).Select(Quote))
        });

        return RunSshAsync(
            machine,
            remoteCommand,
            "ssh " + machine.Host + " " + BuildCodexPreview(model, modelEffort, modelSpeed, codexSessionId),
            onOutput,
            cancellationToken,
            firstProcessOutputTimeout: CodexFirstOutputTimeout,
            standardInput: prompt);
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
                    : BuildPowerShellSetLocationCommand(projectPath) + "; " + shellCommand;
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
                : BuildPowerShellSetLocationCommand(projectPath) + "; " + shellCommand;
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
            if (machine.TargetsWindows())
            {
                return RunProcessAsync(
                    "powershell",
                    new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", BuildPowerShellCodexCommandSetup() + "; & $codexCommand --version" },
                    null,
                    "codex --version",
                    onOutput,
                    cancellationToken);
            }

            return RunProcessAsync("codex", new[] { "--version" }, null, "codex --version", onOutput, cancellationToken);
        }

        if (machine.TargetsWindows())
        {
            return RunSshAsync(machine, BuildPowerShellRemoteCommand(BuildPowerShellCodexCommandSetup() + "; & $codexCommand --version; Get-Location"), "ssh " + machine.Host + " codex --version", onOutput, cancellationToken);
        }

        return RunSshAsync(machine, UnixRemotePathPrefix + " codex --version && pwd", "ssh " + machine.Host + " codex --version", onOutput, cancellationToken);
    }

    private Task<CommandResult> RunSshAsync(
        TargetMachine machine,
        string remoteCommand,
        string preview,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken,
        TimeSpan? firstProcessOutputTimeout = null,
        string? standardInput = null)
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
        return RunProcessAsync("ssh", arguments, null, preview, onOutput, cancellationToken, firstProcessOutputTimeout, standardInput);
    }

    private async Task<CommandResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        string preview,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken,
        TimeSpan? firstProcessOutputTimeout = null,
        string? standardInput = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardInput = true,
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
        var previewLine = "$ " + preview + Environment.NewLine;
        output.Append(previewLine);
        await onOutput(previewLine);

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

        var firstProcessOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
                if (HasUsefulProcessOutput(chunk))
                {
                    firstProcessOutput.TrySetResult();
                }
                await onOutput(chunk);
            }
        }

        var stdout = ReadStreamAsync(process.StandardOutput);
        var stderr = ReadStreamAsync(process.StandardError);
        var waitForExit = process.WaitForExitAsync(cancellationToken);

        try
        {
            try
            {
                if (standardInput is not null)
                {
                    await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
                }
            }
            catch (IOException) when (process.HasExited)
            {
                // The child process exited before consuming stdin; collect its output below.
            }
            catch (ObjectDisposedException) when (process.HasExited)
            {
                // The child process exited before consuming stdin; collect its output below.
            }
            finally
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch (IOException) when (process.HasExited)
                {
                    // The child process exited before stdin could be closed.
                }
                catch (ObjectDisposedException)
                {
                    // Standard input may already be disposed after an early process exit.
                }
            }

            if (firstProcessOutputTimeout is { } timeout)
            {
                var timeoutTask = Task.Delay(timeout, cancellationToken);
                var firstSignal = await Task.WhenAny(firstProcessOutput.Task, waitForExit, timeoutTask);
                if (firstSignal == timeoutTask)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var message = "Process produced no useful stdout/stderr for " + Math.Round(timeout.TotalSeconds) + " seconds after launch. Check target machine SSH, Codex auth, model availability, project path, and whether Codex is waiting for stdin." + Environment.NewLine;
                    output.Append(message);
                    await onOutput(message);
                    TryKill(process);
                    throw new TimeoutException(message.Trim());
                }
            }

            await waitForExit;
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

    private static bool HasUsefulProcessOutput(string chunk)
    {
        foreach (var line in chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Equals("Reading additional input from stdin...", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return true;
        }

        return false;
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

    public static string BuildPowerShellSetLocationCommand(string path) =>
        "Set-Location -LiteralPath " + QuotePowerShellValue(path) + " -ErrorAction Stop";

    private static string BuildPowerShellCodexCommandSetup() =>
        "$persistedPath = @([Environment]::GetEnvironmentVariable('Path', 'Machine'), [Environment]::GetEnvironmentVariable('Path', 'User')) -join ';'; "
        + "if ($persistedPath) { $env:Path = $persistedPath + ';' + $env:Path }; "
        + "$env:PATHEXT = if ($env:PATHEXT) { $env:PATHEXT } else { '.COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC;.CPL;.PS1' }; "
        + "$codexPathCandidates = @($env:APPDATA + '\\npm', $env:LOCALAPPDATA + '\\Programs\\OpenAI\\Codex\\bin', $env:LOCALAPPDATA + '\\Microsoft\\WinGet\\Links', $env:USERPROFILE + '\\.volta\\bin', $env:USERPROFILE + '\\scoop\\shims', $env:ProgramData + '\\chocolatey\\bin', $env:ProgramFiles + '\\nodejs') | Where-Object { $_ -and (Test-Path -LiteralPath $_) }; "
        + "$npmCommand = Get-Command npm.cmd,npm.exe,npm -CommandType Application,ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1; "
        + "if ($npmCommand) { try { $npmPrefix = & $npmCommand.Path prefix -g 2>$null | Select-Object -First 1; if ($npmPrefix) { $npmPrefix = $npmPrefix.Trim(); $codexPathCandidates += @($npmPrefix, (Join-Path $npmPrefix 'bin')) } } catch {} }; "
        + "foreach ($codexPath in $codexPathCandidates) { $env:Path = $env:Path + ';' + $codexPath }; "
        + "$codexCommand = Get-Command codex.exe,codex.cmd,codex.bat,codex.ps1,codex -CommandType Application,ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1; "
        + "if ($codexCommand) { $codexCommand = $codexCommand.Path }; "
        + "if (-not $codexCommand) { throw 'Codex CLI was not found for this Windows SSH user. Install it for this user with: npm.cmd install -g @openai/codex. Then reconnect and run: codex --version.' }";

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
        var sshSafeCommand = "$ProgressPreference = 'SilentlyContinue'; try { " + command
            + "; if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }"
            + "; } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(sshSafeCommand));
        // Suppressing progress records and writing caught errors directly prevents Windows
        // PowerShell from serializing its non-success streams as CLIXML through OpenSSH.
        return "powershell -NoLogo -NoProfile -NonInteractive -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand " + encodedCommand;
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
        bool allowGitWrites,
        bool useUnelevatedWindowsSandbox)
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

        if (useUnelevatedWindowsSandbox && !allowGitWrites)
        {
            // Windows SSH sessions cannot service the setup or logon requirements of the
            // elevated native sandbox or private desktop reliably. Keep workspace isolation
            // with its ACL fallback while using the SSH session's compatible desktop.
            arguments.Add("-c");
            arguments.Add("windows.sandbox=\"unelevated\"");
            arguments.Add("-c");
            arguments.Add("windows.sandbox_private_desktop=false");
        }

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
            arguments.Add(allowGitWrites ? "danger-full-access" : "workspace-write");
        }
        else
        {
            arguments.Add("-c");
            arguments.Add("approval_policy=\"never\"");
            arguments.Add("-c");
            arguments.Add(allowGitWrites ? "sandbox_mode=\"danger-full-access\"" : "sandbox_mode=\"workspace-write\"");
            arguments.Add(codexSessionId);
        }

        // Keep prompts off process command lines. This avoids the Windows cmd.exe command-length
        // limit and prevents prompt contents from appearing in process listings.
        arguments.Add("-");
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
