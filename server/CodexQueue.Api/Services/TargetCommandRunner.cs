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
        PermissionMode permissionMode,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken);

    Task WriteAttachmentAsync(
        TargetMachine machine,
        string targetPath,
        byte[] content,
        CancellationToken cancellationToken);

    Task DeleteAttachmentDirectoryAsync(
        TargetMachine machine,
        string directoryPath,
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
    // sshd commonly supplies a deliberately small, non-login PATH. Include the package-manager
    // locations used by macOS and Linux without sourcing user shell startup files, which could
    // be interactive or have side effects in a queued command. zsh rejects unmatched nvm globs
    // by default, so explicitly preserve an unmatched glob when the user has no nvm installation.
    public const string UnixRemotePathSetup = "export PATH=\"$HOME/.local/bin:$HOME/bin:$HOME/.npm-global/bin:$HOME/.volta/bin:$HOME/.asdf/shims:$HOME/.cargo/bin:$HOME/.local/share/pnpm:/opt/homebrew/bin:/opt/homebrew/sbin:/usr/local/bin:/usr/local/sbin:$PATH\"; if [ -n \"${ZSH_VERSION-}\" ]; then setopt nonomatch; fi; for nodeBin in \"$HOME\"/.nvm/versions/node/*/bin; do [ -d \"$nodeBin\" ] && PATH=\"$nodeBin:$PATH\"; done; export PATH;";
    private static readonly TimeSpan CodexFirstOutputTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan RateLimitsTimeout = TimeSpan.FromSeconds(20);
    // Attachment transfer commands intentionally produce no output, so the normal
    // first-output watchdog cannot protect them. Bound the entire operation instead
    // so an unavailable SSH target cannot hold a queue lane indefinitely.
    private static readonly TimeSpan AttachmentTransferTimeout = TimeSpan.FromSeconds(45);

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
                standardInput: input,
                completionOutputPredicate: static chunk => chunk.Contains("\"id\":2", StringComparison.Ordinal));
        }

        var remoteCommand = machine.TargetsWindows()
            ? BuildPowerShellRemoteCommand(BuildPowerShellCodexCommandSetup() + "; & $codexCommand app-server --stdio")
            : UnixRemotePathSetup + " codex app-server --stdio";
        return RunSshAsync(
            machine,
            remoteCommand,
            "ssh " + machine.Host + " codex app-server --stdio (account/rateLimits/read)",
            static _ => Task.CompletedTask,
            cancellationToken,
            firstProcessOutputTimeout: RateLimitsTimeout,
            standardInput: input,
            completionOutputPredicate: static chunk => chunk.Contains("\"id\":2", StringComparison.Ordinal));
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
        PermissionMode permissionMode,
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
                permissionMode,
                disableWindowsSandbox: false);
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
                    permissionMode,
                    disableWindowsSandbox: true).Select(QuotePowerShellValue));

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
            UnixRemotePathSetup,
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
                permissionMode,
                disableWindowsSandbox: false).Select(Quote))
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

    public async Task WriteAttachmentAsync(
        TargetMachine machine,
        string targetPath,
        byte[] content,
        CancellationToken cancellationToken)
    {
        if (machine.Kind == MachineKind.Local)
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("Attachment target path must include a directory.");
            }

            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(targetPath, content, cancellationToken);
            return;
        }

        var encodedContent = Convert.ToBase64String(content);
        CommandResult result;
        if (machine.TargetsWindows())
        {
            var command = "$attachmentPath = " + QuotePowerShellValue(targetPath)
                // Split-Path rejects -Parent with -LiteralPath on Windows PowerShell. Use the
                // .NET APIs so the target path is always treated literally and no wildcard
                // expansion or PowerShell parameter-set selection is involved.
                + "; $attachmentDirectory = [IO.Path]::GetDirectoryName($attachmentPath)"
                + "; if ([string]::IsNullOrWhiteSpace($attachmentDirectory)) { throw 'Attachment target path must include a directory.' }"
                + "; [IO.Directory]::CreateDirectory($attachmentDirectory) | Out-Null"
                // Some Windows OpenSSH sessions do not propagate stdin EOF to the remote
                // PowerShell process. Read one newline-terminated Base64 record instead of
                // waiting for EOF, then validate it before exposing the attachment to Codex.
                + "; $attachmentBase64 = [Console]::In.ReadLine()"
                + "; if ($null -eq $attachmentBase64) { throw 'Attachment data was not received.' }"
                + "; $attachmentBytes = [Convert]::FromBase64String($attachmentBase64)"
                + "; if ($attachmentBytes.LongLength -ne " + content.LongLength + ") { throw 'Attachment data was incomplete.' }"
                + "; [IO.File]::WriteAllBytes($attachmentPath, $attachmentBytes)"
                + "; if (([IO.FileInfo]::new($attachmentPath)).Length -ne " + content.LongLength + ") { throw 'Attachment file validation failed.' }";
            result = await RunSshAsync(
                machine,
                BuildPowerShellRemoteCommand(command),
                "ssh " + machine.Host + " write attachment",
                static _ => Task.CompletedTask,
                cancellationToken,
                standardInput: encodedContent + "\n",
                executionTimeout: AttachmentTransferTimeout);
        }
        else
        {
            var remoteCommand = UnixRemotePathSetup
                + " mkdir -p -- " + Quote(Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("Attachment target path must include a directory."))
                + " && if base64 --help 2>&1 | grep -q -- '--decode'; then base64 --decode; else base64 -D; fi > " + Quote(targetPath);
            result = await RunSshAsync(
                machine,
                remoteCommand,
                "ssh " + machine.Host + " write attachment",
                static _ => Task.CompletedTask,
                cancellationToken,
                standardInput: encodedContent,
                executionTimeout: AttachmentTransferTimeout);
        }

        if (!result.Success)
        {
            throw new IOException("Could not transfer an attachment to the target machine: " + StripCommandPreview(result.Output));
        }
    }

    public async Task DeleteAttachmentDirectoryAsync(
        TargetMachine machine,
        string directoryPath,
        CancellationToken cancellationToken)
    {
        if (machine.Kind == MachineKind.Local)
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }

            return;
        }

        CommandResult result;
        if (machine.TargetsWindows())
        {
            var command = "if (Test-Path -LiteralPath " + QuotePowerShellValue(directoryPath) + ") { Remove-Item -LiteralPath "
                + QuotePowerShellValue(directoryPath) + " -Recurse -Force }";
            result = await RunSshAsync(
                machine,
                BuildPowerShellRemoteCommand(command),
                "ssh " + machine.Host + " remove attachments",
                static _ => Task.CompletedTask,
                cancellationToken);
        }
        else
        {
            result = await RunSshAsync(
                machine,
                UnixRemotePathSetup + " rm -rf -- " + Quote(directoryPath),
                "ssh " + machine.Host + " remove attachments",
                static _ => Task.CompletedTask,
                cancellationToken);
        }

        if (!result.Success)
        {
            throw new IOException("Could not remove temporary attachments from the target machine: " + StripCommandPreview(result.Output));
        }
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

        var remoteCommand = UnixRemotePathSetup + " cd " + Quote(projectPath) + " && " + shellCommand;
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

        var unixTestCommand = UnixRemotePathSetup
            + " printf '%s\\n' 'SSH connection established.'; "
            + "printf 'Remote OS: '; uname -s; "
            + "if command -v codex >/dev/null 2>&1; then "
            + "printf 'Codex CLI: '; codex --version; "
            + "printf 'Codex path: '; command -v codex; "
            + "printf 'Working directory: '; pwd; "
            + "else printf '%s\\n' 'Codex CLI was not found on this SSH session PATH. Install it for this SSH user, or expose its bin directory in PATH.' >&2; exit 127; fi";
        return RunSshAsync(machine, unixTestCommand, "ssh " + machine.Host + " test Codex CLI", onOutput, cancellationToken);
    }

    private Task<CommandResult> RunSshAsync(
        TargetMachine machine,
        string remoteCommand,
        string preview,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken,
        TimeSpan? firstProcessOutputTimeout = null,
        string? standardInput = null,
        Func<string, bool>? completionOutputPredicate = null,
        TimeSpan? executionTimeout = null)
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
        return RunProcessAsync("ssh", arguments, null, preview, onOutput, cancellationToken, firstProcessOutputTimeout, standardInput, completionOutputPredicate, executionTimeout);
    }

    private async Task<CommandResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        string preview,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken,
        TimeSpan? firstProcessOutputTimeout = null,
        string? standardInput = null,
        Func<string, bool>? completionOutputPredicate = null,
        TimeSpan? executionTimeout = null)
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
        var completionOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
                if (completionOutputPredicate?.Invoke(chunk) == true)
                {
                    completionOutput.TrySetResult();
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
                if (completionOutputPredicate is null)
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
            }

            if (firstProcessOutputTimeout is { } timeout)
            {
                if (completionOutputPredicate is not null)
                {
                    var completionSignal = await Task.WhenAny(completionOutput.Task, waitForExit, Task.Delay(timeout, cancellationToken));
                    if (completionSignal != completionOutput.Task)
                    {
                        if (completionSignal != waitForExit)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            TryKill(process);
                            throw new TimeoutException("Codex did not return usage data before the request timed out.");
                        }
                    }
                    else
                    {
                        TryKill(process);
                    }
                }
                else
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
            }

            if (executionTimeout is { } maximumDuration)
            {
                var completionSignal = await Task.WhenAny(waitForExit, Task.Delay(maximumDuration, cancellationToken));
                if (completionSignal != waitForExit)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TryKill(process);
                    throw new TimeoutException("Process did not finish within " + Math.Round(maximumDuration.TotalSeconds) + " seconds.");
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

    private static string StripCommandPreview(string output)
    {
        var newline = output.IndexOf(Environment.NewLine, StringComparison.Ordinal);
        return (newline < 0 ? output : output[(newline + Environment.NewLine.Length)..]).Trim();
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
        PermissionMode permissionMode,
        bool disableWindowsSandbox)
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

        // Approval policy used to be exposed by `codex exec -a`, but newer CLI releases
        // removed that option. The config override is supported by both new sessions and
        // `exec resume`, so use one stable representation on every target OS.
        arguments.Add("-c");
        arguments.Add("approval_policy=\"" + (permissionMode == PermissionMode.AskForApproval ? "untrusted" : "never") + "\"");

        foreach (var imagePath in imagePaths ?? Array.Empty<string>())
        {
            arguments.Add("-i");
            arguments.Add(imagePath);
        }

        if (string.IsNullOrWhiteSpace(codexSessionId))
        {
            arguments.Add("-C");
            arguments.Add(projectPath);
            arguments.Add("-s");
            arguments.Add(permissionMode == PermissionMode.ReadOnly ? "read-only" : permissionMode == PermissionMode.FullAccess || disableWindowsSandbox ? "danger-full-access" : "workspace-write");
        }
        else
        {
            arguments.Add("-c");
            arguments.Add("sandbox_mode=\"" + (permissionMode == PermissionMode.ReadOnly ? "read-only" : permissionMode == PermissionMode.FullAccess || disableWindowsSandbox ? "danger-full-access" : "workspace-write") + "\"");
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
