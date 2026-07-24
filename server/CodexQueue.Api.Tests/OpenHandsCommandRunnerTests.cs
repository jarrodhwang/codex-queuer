using System.Text.Json;
using System.Diagnostics;
using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexQueue.Api.Tests;

public sealed class OpenHandsCommandRunnerTests
{
    [Fact]
    public void BuildArgumentsAndPreview_DoNotExposePromptOrApiKey()
    {
        const string prompt = "fix the parser; prompt-marker-84f2";
        const string apiKey = "api-key-marker-b399";
        const string conversationId = "12345678-1234-1234-1234-123456789abc";
        const string taskPath = "/repo/.codex-queue/openhands/run/task.md";

        var arguments = OpenHandsCommandRunner.BuildArguments(conversationId, taskPath);
        var preview = OpenHandsCommandRunner.BuildCommandPreview(
            "openai/qwen2.5-coder:32b",
            conversationId);
        var environment = OpenHandsCommandRunner.BuildExecutionEnvironment(
            "openai/qwen2.5-coder:32b",
            "http://ollama.test:11434/v1",
            apiKey,
            "/repo/.git/codex-queue/openhands/run/tmux");
        var renderedArguments = string.Join(" ", arguments);

        Assert.Equal(
            [
                "--headless",
                "--json",
                "--override-with-envs",
                "--always-approve",
                "--resume",
                conversationId,
                "-f",
                taskPath,
            ],
            arguments);
        Assert.DoesNotContain(prompt, renderedArguments, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, renderedArguments, StringComparison.Ordinal);
        Assert.DoesNotContain(prompt, preview, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, preview, StringComparison.Ordinal);
        Assert.DoesNotContain(taskPath, preview, StringComparison.Ordinal);
        Assert.DoesNotContain(conversationId, preview, StringComparison.Ordinal);
        Assert.Contains("<temporary-task-file>", preview, StringComparison.Ordinal);
        Assert.Contains("<conversation-id>", preview, StringComparison.Ordinal);
        Assert.Equal(apiKey, environment["LLM_API_KEY"]);
        Assert.Equal("openai/qwen2.5-coder:32b", environment["LLM_MODEL"]);
        Assert.Equal("http://ollama.test:11434/v1", environment["LLM_BASE_URL"]);
    }

    [Fact]
    public void BuildRemoteConversationStateReadCommand_UsesBoundedRegularFileCheck()
    {
        const string requestedId = "01234567-89AB-CDEF-0123-456789ABCDEF";
        const string normalizedId = "0123456789abcdef0123456789abcdef";

        var command = OpenHandsCommandRunner.BuildRemoteConversationStateReadCommand(
            "/srv/project with spaces",
            requestedId);

        Assert.Contains(
            "cd '/srv/project with spaces'",
            command,
            StringComparison.Ordinal);
        Assert.Contains("OPENHANDS_CONVERSATIONS_DIR", command, StringComparison.Ordinal);
        Assert.Contains("OPENHANDS_PERSISTENCE_DIR", command, StringComparison.Ordinal);
        Assert.Contains(normalizedId, command, StringComparison.Ordinal);
        Assert.DoesNotContain(requestedId, command, StringComparison.Ordinal);
        Assert.Contains("[ ! -f \"$state_file\" ]", command, StringComparison.Ordinal);
        Assert.Contains("[ -L \"$state_file\" ]", command, StringComparison.Ordinal);
        Assert.Contains("wc -c", command, StringComparison.Ordinal);
        Assert.Contains("2097152", command, StringComparison.Ordinal);
        Assert.Contains("head -c 2097153", command, StringComparison.Ordinal);
        Assert.DoesNotContain("LLM_API_KEY", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteProjectLocationProbe_RejectsSymlinkThatPhysicallyResolvesToRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-remote-root-link-tests",
            Guid.NewGuid().ToString("N"));
        var projectLink = Path.Combine(testRoot, "project");
        Directory.CreateDirectory(testRoot);
        Directory.CreateSymbolicLink(
            projectLink,
            Path.GetPathRoot(testRoot)!);

        try
        {
            var command = OpenHandsCommandRunner.BuildRemoteProjectLocationProbeCommand(
                projectLink);
            Assert.Contains("pwd -P", command, StringComparison.Ordinal);
            Assert.Contains(
                "[ \"$resolved_project_root\" = / ]",
                command,
                StringComparison.Ordinal);

            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
            using var process = Process.Start(startInfo)!;
            var standardOutput = await process.StandardOutput.ReadToEndAsync();
            var standardError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(64, process.ExitCode);
            Assert.Empty(standardOutput);
            Assert.Contains(
                "resolves to the filesystem root",
                standardError,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(projectLink))
            {
                Directory.Delete(projectLink);
            }
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void BuildExecutionEnvironment_DoesNotInheritUnrelatedServerSecrets()
    {
        const string secretName = "CQ_OPENHANDS_TEST_SERVER_SECRET";
        var previousValue = Environment.GetEnvironmentVariable(secretName);
        Environment.SetEnvironmentVariable(secretName, "must-not-reach-agent");
        try
        {
            var environment = OpenHandsCommandRunner.BuildExecutionEnvironment(
                "openai/qwen2.5-coder:32b",
                "http://ollama.test:11434/v1",
                AiProviderService.LocalPlaceholderApiKey,
                "/repo/.git/codex-queue/openhands/run/tmux");

            Assert.DoesNotContain(secretName, environment.Keys);
            Assert.Contains("PATH", environment.Keys);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, previousValue);
        }
    }

    [Fact]
    public void BuildDiagnosticEnvironment_DoesNotInheritProviderOrServerSecrets()
    {
        var secretNames = new[]
        {
            "CQ_OPENHANDS_TEST_SERVER_SECRET",
            "OPENAI_API_KEY",
            "ANTHROPIC_API_KEY",
            "LLM_API_KEY",
        };
        var previousValues = secretNames.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        try
        {
            foreach (var secretName in secretNames)
            {
                Environment.SetEnvironmentVariable(secretName, "must-not-reach-diagnostic");
            }

            var environment = OpenHandsCommandRunner.BuildDiagnosticEnvironment();

            Assert.Contains("PATH", environment.Keys);
            foreach (var secretName in secretNames)
            {
                Assert.DoesNotContain(secretName, environment.Keys);
            }
        }
        finally
        {
            foreach (var pair in previousValues)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }

    [Fact]
    public async Task RemoteEnvironmentSanitizer_KeepsRunnerVariablesAndRemovesUnrelatedSecrets()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var sanitizer = OpenHandsCommandRunner.BuildRemoteEnvironmentSanitizer();
        Assert.Contains("LLM_API_KEY", sanitizer, StringComparison.Ordinal);
        Assert.Contains("OPENHANDS_WORK_DIR", sanitizer, StringComparison.Ordinal);
        Assert.Contains("OPENHANDS_CONVERSATIONS_DIR", sanitizer, StringComparison.Ordinal);
        Assert.DoesNotContain("CQ_REMOTE_SECRET", sanitizer, StringComparison.Ordinal);
        Assert.DoesNotContain("remote-secret-value", sanitizer, StringComparison.Ordinal);

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(sanitizer + "; env");
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = "/usr/bin:/bin";
        startInfo.Environment["HOME"] = "/tmp";
        startInfo.Environment["LLM_API_KEY"] = "local-llm";
        startInfo.Environment["LLM_BASE_URL"] = "http://ollama.test:11434/v1";
        startInfo.Environment["LLM_MODEL"] = "openai/qwen2.5-coder:32b";
        startInfo.Environment["OPENHANDS_WORK_DIR"] = "/repo";
        startInfo.Environment["CQ_REMOTE_SECRET"] = "remote-secret-value";

        using var process = Process.Start(startInfo)!;
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Empty(standardError);
        Assert.Contains("LLM_API_KEY=local-llm", standardOutput, StringComparison.Ordinal);
        Assert.Contains("OPENHANDS_WORK_DIR=/repo", standardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("CQ_REMOTE_SECRET", standardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("remote-secret-value", standardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteDiagnosticEnvironmentSanitizer_RemovesProviderAndUnrelatedSecrets()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var sanitizer = OpenHandsCommandRunner.BuildRemoteDiagnosticEnvironmentSanitizer();
        Assert.Contains("OPENHANDS_CONVERSATIONS_DIR", sanitizer, StringComparison.Ordinal);
        Assert.DoesNotContain("LLM_API_KEY", sanitizer, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENAI_API_KEY", sanitizer, StringComparison.Ordinal);

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(sanitizer + "; env");
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = "/usr/bin:/bin";
        startInfo.Environment["HOME"] = "/tmp";
        startInfo.Environment["OPENHANDS_CONVERSATIONS_DIR"] = "/tmp/conversations";
        startInfo.Environment["LLM_API_KEY"] = "provider-secret-value";
        startInfo.Environment["OPENAI_API_KEY"] = "cloud-secret-value";
        startInfo.Environment["CQ_REMOTE_SECRET"] = "remote-secret-value";

        using var process = Process.Start(startInfo)!;
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Empty(standardError);
        Assert.Contains("OPENHANDS_CONVERSATIONS_DIR=/tmp/conversations", standardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("LLM_API_KEY", standardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret-value", standardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENAI_API_KEY", standardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("cloud-secret-value", standardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("CQ_REMOTE_SECRET", standardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("remote-secret-value", standardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildLocalAiProbeCommand_IsBoundedAndContainsNoCredentialsOrProjectPaths()
    {
        var command = OpenHandsCommandRunner.BuildLocalAiProbeCommand(
            "http://ollama.internal:11434/v1");

        Assert.Contains(
            "probe_url='http://ollama.internal:11434/v1/models'",
            command,
            StringComparison.Ordinal);
        Assert.Contains("command -v curl", command, StringComparison.Ordinal);
        Assert.Contains("command -v wget", command, StringComparison.Ordinal);
        Assert.Contains("umask 077", command, StringComparison.Ordinal);
        Assert.Contains("ulimit -f 2048", command, StringComparison.Ordinal);
        Assert.Contains("--connect-timeout 4", command, StringComparison.Ordinal);
        Assert.Contains("--max-time 8", command, StringComparison.Ordinal);
        Assert.Contains("--max-filesize 1048576", command, StringComparison.Ordinal);
        Assert.Contains("--proto-redir '=http,https'", command, StringComparison.Ordinal);
        Assert.Contains("probe_size", command, StringComparison.Ordinal);
        Assert.DoesNotContain("LLM_API_KEY", command, StringComparison.Ordinal);
        Assert.DoesNotContain("local-llm", command, StringComparison.Ordinal);
        Assert.DoesNotContain("/repo", command, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() =>
            OpenHandsCommandRunner.BuildLocalAiProbeCommand(
                "http://user:secret@ollama.internal:11434/v1"));
        Assert.Throws<ArgumentException>(() =>
            OpenHandsCommandRunner.BuildLocalAiProbeCommand(
                "http://ollama.internal:11434/v1?token=secret"));
    }

    [Fact]
    public void ParseLocalAiProbeResponse_ReportsReachabilityAndModelAvailabilitySeparately()
    {
        const string response =
            """{"object":"list","data":[{"id":"qwen2.5-coder:32b"},{"id":"devstral:24b"}]}""";

        var available = OpenHandsCommandRunner.ParseLocalAiProbeResponse(
            response,
            "openai/qwen2.5-coder:32b");
        var missing = OpenHandsCommandRunner.ParseLocalAiProbeResponse(
            response,
            "openai/missing:latest");
        var malformed = OpenHandsCommandRunner.ParseLocalAiProbeResponse(
            """{"models":[]}""",
            "openai/qwen2.5-coder:32b");

        Assert.True(available.Reachable);
        Assert.True(available.SelectedModelAvailable);
        Assert.True(missing.Reachable);
        Assert.False(missing.SelectedModelAvailable);
        Assert.True(malformed.Reachable);
        Assert.Null(malformed.SelectedModelAvailable);
        Assert.Contains("unreadable", malformed.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_RejectsMissingUnrestrictedAccessConfirmationBeforeStarting()
    {
        var runner = new OpenHandsCommandRunner(NullLogger<OpenHandsCommandRunner>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            new TargetMachine
            {
                Kind = MachineKind.Local,
                Platform = MachinePlatform.Linux,
            },
            "/a/path/that/does/not/need/to/exist",
            "openai/qwen2.5-coder:32b",
            "http://ollama.test:11434/v1",
            AiProviderService.LocalPlaceholderApiKey,
            null,
            "do not execute",
            alwaysApproveConfirmed: false,
            _ => Task.CompletedTask,
            CancellationToken.None));

        Assert.Contains(
            "Explicit unrestricted-access confirmation is required",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/.")]
    [InlineData("/..")]
    [InlineData("/tmp/..")]
    public async Task RunAsync_RejectsCanonicalLocalFileSystemRootBeforePreflight(
        string projectPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var probeCalls = 0;
        var runner = new OpenHandsCommandRunner(
            NullLogger<OpenHandsCommandRunner>.Instance,
            new OpenHandsCommandOptions(
                "must-not-launch-openhands",
                (_, _, _, _) =>
                {
                    probeCalls++;
                    return Task.FromResult(new OpenHandsLocalAiCheck(
                        true,
                        true,
                        "healthy"));
                }));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
            new TargetMachine
            {
                Kind = MachineKind.Local,
                Platform = MachinePlatform.Linux,
            },
            projectPath,
            "openai/qwen2.5-coder:32b",
            "http://ollama.test:11434/v1",
            AiProviderService.LocalPlaceholderApiKey,
            null,
            "do not execute",
            alwaysApproveConfirmed: true,
            _ => Task.CompletedTask,
            CancellationToken.None));

        Assert.Equal(0, probeCalls);
        Assert.Contains("filesystem root", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_RejectsLocalProjectSymlinkThatPhysicallyResolvesToRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-local-root-link-tests",
            Guid.NewGuid().ToString("N"));
        var projectLink = Path.Combine(testRoot, "project");
        Directory.CreateDirectory(testRoot);
        Directory.CreateSymbolicLink(
            projectLink,
            Path.GetPathRoot(testRoot)!);

        try
        {
            var runner = new OpenHandsCommandRunner(
                NullLogger<OpenHandsCommandRunner>.Instance,
                HealthyLocalAiOptions("must-not-launch-openhands"));
            var exception = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
                new TargetMachine
                {
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                },
                projectLink,
                "openai/qwen2.5-coder:32b",
                "http://ollama.test:11434/v1",
                AiProviderService.LocalPlaceholderApiKey,
                null,
                "do not execute",
                alwaysApproveConfirmed: true,
                _ => Task.CompletedTask,
                CancellationToken.None));

            Assert.Contains(
                "filesystem root",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(projectLink))
            {
                Directory.Delete(projectLink);
            }
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_AllowsLocalProjectSymlinkToNonRootRepository()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-local-repository-link-tests",
            Guid.NewGuid().ToString("N"));
        var repositoryRoot = Path.Combine(testRoot, "repository");
        var projectLink = Path.Combine(testRoot, "project");
        var executable = Path.Combine(testRoot, "fake-openhands");
        Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
        Directory.CreateSymbolicLink(projectLink, repositoryRoot);
        await File.WriteAllTextAsync(
            executable,
            "#!/bin/sh\nprintf '%s\\n' 'expected fake CLI stop' >&2\nexit 127\n");
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var runner = new OpenHandsCommandRunner(
                NullLogger<OpenHandsCommandRunner>.Instance,
                HealthyLocalAiOptions(executable));
            var result = await runner.RunAsync(
                new TargetMachine
                {
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                },
                projectLink,
                "openai/qwen2.5-coder:32b",
                "http://ollama.test:11434/v1",
                AiProviderService.LocalPlaceholderApiKey,
                null,
                "perform the test task",
                alwaysApproveConfirmed: true,
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains(
                "not installed",
                result.Output,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(projectLink))
            {
                Directory.Delete(projectLink);
            }
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/.")]
    [InlineData("/..")]
    [InlineData("/srv/..")]
    [InlineData("/srv/project/../..")]
    [InlineData("///./../")]
    public async Task RunAsync_RejectsRemotePosixPathsThatResolveToRootBeforePreflight(
        string projectPath)
    {
        var probeCalls = 0;
        var runner = new OpenHandsCommandRunner(
            NullLogger<OpenHandsCommandRunner>.Instance,
            new OpenHandsCommandOptions(
                "must-not-launch-openhands",
                (_, _, _, _) =>
                {
                    probeCalls++;
                    return Task.FromResult(new OpenHandsLocalAiCheck(
                        true,
                        true,
                        "healthy"));
                }));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
            new TargetMachine
            {
                Kind = MachineKind.Ssh,
                Platform = MachinePlatform.Linux,
                Host = "must-not-connect.invalid",
            },
            projectPath,
            "openai/qwen2.5-coder:32b",
            "http://ollama.test:11434/v1",
            AiProviderService.LocalPlaceholderApiKey,
            null,
            "do not execute",
            alwaysApproveConfirmed: true,
            _ => Task.CompletedTask,
            CancellationToken.None));

        Assert.Equal(0, probeCalls);
        Assert.Contains("invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("repo")]
    [InlineData("./repo")]
    [InlineData("../repo")]
    [InlineData("srv/project")]
    public async Task RunAsync_RejectsRemoteNonAbsolutePosixPathBeforePreflight(
        string projectPath)
    {
        var probeCalls = 0;
        var runner = new OpenHandsCommandRunner(
            NullLogger<OpenHandsCommandRunner>.Instance,
            new OpenHandsCommandOptions(
                "must-not-launch-openhands",
                (_, _, _, _) =>
                {
                    probeCalls++;
                    return Task.FromResult(new OpenHandsLocalAiCheck(
                        true,
                        true,
                        "healthy"));
                }));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
            new TargetMachine
            {
                Kind = MachineKind.Ssh,
                Platform = MachinePlatform.Linux,
                Host = "must-not-connect.invalid",
            },
            projectPath,
            "openai/qwen2.5-coder:32b",
            "http://ollama.test:11434/v1",
            AiProviderService.LocalPlaceholderApiKey,
            null,
            "do not execute",
            alwaysApproveConfirmed: true,
            _ => Task.CompletedTask,
            CancellationToken.None));

        Assert.Equal(0, probeCalls);
        Assert.Contains("invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestMachineAsync_LocalCliDoesNotInheritProviderOrServerSecrets()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-machine-environment-tests",
            Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(testRoot, "fake-openhands");
        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(
            executable,
            """
            #!/bin/sh
            if [ -n "${CQ_OPENHANDS_TEST_SERVER_SECRET:-}" ] || [ -n "${OPENAI_API_KEY:-}" ] || [ -n "${LLM_API_KEY:-}" ]; then
              printf '%s\n' 'diagnostic inherited a provider or server secret' >&2
              exit 91
            fi
            if [ "$1" = "--version" ]; then
              printf '%s\n' 'OpenHands CLI 9.9.0'
              exit 0
            fi
            printf '%s\n' 'usage: openhands --headless --json --override-with-envs --always-approve --resume ID -f FILE'
            """);
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var secretNames = new[]
        {
            "CQ_OPENHANDS_TEST_SERVER_SECRET",
            "OPENAI_API_KEY",
            "LLM_API_KEY",
        };
        var previousValues = secretNames.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        try
        {
            foreach (var secretName in secretNames)
            {
                Environment.SetEnvironmentVariable(secretName, "must-not-reach-diagnostic");
            }

            var runner = new OpenHandsCommandRunner(
                NullLogger<OpenHandsCommandRunner>.Instance,
                HealthyLocalAiOptions(executable));
            var result = await runner.TestMachineAsync(
                new TargetMachine
                {
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                },
                CancellationToken.None);

            Assert.True(result.Available, result.Message);
            Assert.Equal("OpenHands CLI 9.9.0", result.Version);
        }
        finally
        {
            foreach (var pair in previousValues)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TestMachineAsync_ReportsCliVersionAfterDiagnosticLines()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-machine-tests",
            Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(testRoot, "fake-openhands");
        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(
            executable,
            """
            #!/bin/sh
            if [ "$1" = "--version" ]; then
              printf '%s\n' 'dependency warning'
              printf '%s\n' 'OpenHands CLI 9.9.0'
              exit 0
            fi
            printf '%s\n' 'usage: openhands --headless --json --override-with-envs --always-approve --resume ID -f FILE'
            """);
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var runner = new OpenHandsCommandRunner(
                NullLogger<OpenHandsCommandRunner>.Instance,
                HealthyLocalAiOptions(executable));

            var result = await runner.TestMachineAsync(
                new TargetMachine
                {
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                },
                CancellationToken.None,
                "http://ollama.test:11434/v1",
                "openai/qwen2.5-coder:32b");

            Assert.True(result.Available);
            Assert.Equal("OpenHands CLI 9.9.0", result.Version);
            Assert.True(result.TargetLocalAiChecked);
            Assert.True(result.TargetLocalAiReachable);
            Assert.True(result.TargetSelectedModelAvailable);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TestMachineAsync_RejectsCliMissingAnInvokedFlag()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-machine-flag-tests",
            Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(testRoot, "fake-openhands");
        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(
            executable,
            """
            #!/bin/sh
            if [ "$1" = "--version" ]; then
              printf '%s\n' 'OpenHands CLI 9.9.0'
              exit 0
            fi
            printf '%s\n' 'usage: openhands --headless --json --override-with-envs --resume ID -f FILE'
            """);
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var runner = new OpenHandsCommandRunner(
                NullLogger<OpenHandsCommandRunner>.Instance,
                HealthyLocalAiOptions(executable));

            var result = await runner.TestMachineAsync(
                new TargetMachine
                {
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                },
                CancellationToken.None);

            Assert.False(result.Available);
            Assert.Contains("--always-approve", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void SanitizeOutputLine_RemovesHiddenFieldsRedactsKeyAndKeepsVisibleEvents()
    {
        const string apiKey = "exact-secret-value-704d";

        var action = OpenHandsCommandRunner.SanitizeOutputLine(
            """
            {"kind":"ActionEvent","action":{"command":"echo exact-secret-value-704d","thought":"private action thought","completion":"private completion"},"reasoning":"private reasoning","api_key":"exact-secret-value-704d"}
            """,
            apiKey);
        var observation = OpenHandsCommandRunner.SanitizeOutputLine(
            """
            {"kind":"ObservationEvent","observation":{"output":"tests passed","internal_reasoning":"private nested reasoning"},"completion_log":"private completion log"}
            """,
            apiKey);
        var message = OpenHandsCommandRunner.SanitizeOutputLine(
            """
            {"kind":"MessageEvent","thought":"private message thought","llm_message":{"role":"assistant","content":[{"type":"reasoning","text":"semantic hidden reasoning"},{"type":"text","text":"Visible final answer"}],"reasoning":"private llm reasoning"}}
            """,
            apiKey);

        AssertSafeJson(action, "ActionEvent", "echo [REDACTED]");
        AssertSafeJson(observation, "ObservationEvent", "tests passed");
        AssertSafeJson(message, "MessageEvent", "Visible final answer");

        var combined = action.Content + observation.Content + message.Content;
        Assert.DoesNotContain(apiKey, combined, StringComparison.Ordinal);
        Assert.DoesNotContain("thought", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reasoning", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("llm_message", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("completion", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("semantic hidden", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", combined, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-json model thought")]
    [InlineData("{malformed")]
    public void SanitizeOutputLine_SuppressesNonJsonDiagnostics(string output)
    {
        var line = OpenHandsCommandRunner.SanitizeOutputLine(
            output,
            AiProviderService.LocalPlaceholderApiKey);

        Assert.Null(line.Content);
        Assert.False(line.ReportedError);
    }

    [Fact]
    public void SanitizeOutputLine_DoesNotReflectUnknownEventKind()
    {
        var line = OpenHandsCommandRunner.SanitizeOutputLine(
            """{"kind":"prompt-marker-that-must-not-be-reflected","value":"hidden"}""",
            AiProviderService.LocalPlaceholderApiKey);

        AssertSafeJson(line, "OpenHandsEvent", "OpenHands status updated.");
        Assert.DoesNotContain("prompt-marker", line.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", line.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeOutputLine_ConversationErrorEventReportsFailure()
    {
        var line = OpenHandsCommandRunner.SanitizeOutputLine(
            """{"kind":"ConversationErrorEvent","message":"model request failed","reasoning":"hidden"}""",
            AiProviderService.LocalPlaceholderApiKey);

        Assert.True(line.ReportedError);
        AssertSafeJson(
            line,
            "ConversationErrorEvent",
            "model request failed",
            expectedReportedError: true);
        Assert.False(new OpenHandsCommandResult(
            0,
            line.Content!,
            line.Content!,
            "safe preview",
            null,
            ReportedError: true).Success);
    }

    [Fact]
    public void SanitizeOutputLine_DistinguishesRecoverableAgentErrorFromTerminalState()
    {
        var agentError = OpenHandsCommandRunner.SanitizeOutputLine(
            """{"kind":"AgentErrorEvent","message":"tool failed and agent may recover"}""",
            AiProviderService.LocalPlaceholderApiKey);
        var failedState = OpenHandsCommandRunner.SanitizeOutputLine(
            """{"kind":"ConversationStateUpdateEvent","state":"failed"}""",
            AiProviderService.LocalPlaceholderApiKey);
        var stuckCurrentSchema = OpenHandsCommandRunner.SanitizeOutputLine(
            """{"kind":"ConversationStateUpdateEvent","key":"execution_status","value":"stuck"}""",
            AiProviderService.LocalPlaceholderApiKey);
        var finishedCurrentSchema = OpenHandsCommandRunner.SanitizeOutputLine(
            """{"kind":"ConversationStateUpdateEvent","key":"execution_status","value":"finished"}""",
            AiProviderService.LocalPlaceholderApiKey);

        Assert.False(agentError.ReportedError);
        Assert.True(failedState.ReportedError);
        Assert.True(stuckCurrentSchema.ReportedError);
        Assert.False(finishedCurrentSchema.ReportedError);
        Assert.True(finishedCurrentSchema.ReportedFinished);
    }

    [Fact]
    public void BuildSafeProcessFailureEvent_DoesNotReflectRawDiagnostics()
    {
        const string rawMarker = "private-traceback-marker-f807";

        var failure = OpenHandsCommandRunner.BuildSafeProcessFailureEvent(
            "ssh",
            255,
            "connection refused " + rawMarker);

        Assert.Contains("SshUnavailable", failure, StringComparison.Ordinal);
        Assert.Contains("Could not reach", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(rawMarker, failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "Conversation ID: 0123456789abcdef0123456789ABCDEF",
        "0123456789abcdef0123456789ABCDEF")]
    [InlineData(
        "Conversation ID: 01234567-89ab-cdef-0123-456789abcdef",
        "01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData(
        "\u001b[36mConversation ID: 01234567-89ab-cdef-0123-456789abcdef\u001b[0m",
        "01234567-89ab-cdef-0123-456789abcdef")]
    public void ExtractConversationId_AcceptsDashedAndUndashedIds(
        string output,
        string expected)
    {
        Assert.Equal(expected, OpenHandsCommandRunner.ExtractConversationId(output));
    }

    [Fact]
    public void ExtractConversationId_RejectsUnrelatedOutput()
    {
        Assert.Null(OpenHandsCommandRunner.ExtractConversationId("Conversation ID: not-a-uuid"));
    }

    [Fact]
    public void ExtractConversationId_RejectsConversationMarkerInsideJsonToolOutput()
    {
        const string observation =
            """{"kind":"ObservationEvent","observation":{"output":"Conversation ID: 01234567-89ab-cdef-0123-456789abcdef"}}""";

        Assert.Null(OpenHandsCommandRunner.ExtractConversationId(observation));
    }

    [Fact]
    public async Task RunAsync_UsesPersistedFinishedStateForSuccess()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunWithPersistedStateAsync("finished");

        Assert.True(result.Success);
        Assert.True(result.ReportedFinished);
        Assert.Equal("0123456789abcdef0123456789abcdef", result.ConversationId);
        Assert.DoesNotContain("Conversation ID:", result.Output, StringComparison.Ordinal);
        Assert.Contains("Conversation ID:", result.RawDiagnosticOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_TruncatesOversizedJsonlEventBeforeParsingOrStreaming()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunWithPersistedStateAsync(
            "finished",
            """
            #!/bin/sh
            awk 'BEGIN { printf "{\"kind\":\"ObservationEvent\",\"content\":\""; for (i = 0; i < 300000; i++) printf "x"; print "\"}" }'
            printf '%s\n' 'Conversation ID: 0123456789abcdef0123456789abcdef'
            exit 0
            """);

        Assert.True(result.Success);
        Assert.Contains("oversized output event", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(new string('x', 1_000), result.Output, StringComparison.Ordinal);
        Assert.True(result.RawDiagnosticOutput.Length <= 512_000);
    }

    [Fact]
    public async Task RunAsync_ResumePreflightNormalizesAndRunsMatchingConversation()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string requestedId = "01234567-89AB-CDEF-0123-456789ABCDEF";
        const string normalizedId = "0123456789abcdef0123456789abcdef";
        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-resume-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var conversationsRoot = Path.Combine(testRoot, "conversations");
        var conversationRoot = Path.Combine(conversationsRoot, normalizedId);
        var executable = Path.Combine(testRoot, "fake-openhands");
        var argumentsPath = Path.Combine(projectRoot, "openhands-arguments.txt");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".git"));
        Directory.CreateDirectory(conversationRoot);
        await File.WriteAllTextAsync(
            Path.Combine(conversationRoot, "base_state.json"),
            JsonSerializer.Serialize(new
            {
                id = "01234567-89ab-cdef-0123-456789abcdef",
                execution_status = "finished",
            }));
        await File.WriteAllTextAsync(
            executable,
            """
            #!/bin/sh
            printf '%s\n' "$@" > "$PWD/openhands-arguments.txt"
            printf '%s\n' 'Conversation ID: 0123456789abcdef0123456789abcdef'
            exit 0
            """);
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var priorConversationsDirectory = Environment.GetEnvironmentVariable(
            "OPENHANDS_CONVERSATIONS_DIR");
        Environment.SetEnvironmentVariable(
            "OPENHANDS_CONVERSATIONS_DIR",
            conversationsRoot);
        try
        {
            var runner = new OpenHandsCommandRunner(
                NullLogger<OpenHandsCommandRunner>.Instance,
                HealthyLocalAiOptions(executable));

            var result = await runner.RunAsync(
                new TargetMachine
                {
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                },
                projectRoot,
                "openai/qwen2.5-coder:32b",
                "http://ollama.test:11434/v1",
                AiProviderService.LocalPlaceholderApiKey,
                requestedId,
                "perform the continued task",
                alwaysApproveConfirmed: true,
                _ => Task.CompletedTask,
                CancellationToken.None);

            var arguments = await File.ReadAllLinesAsync(argumentsPath);
            var resumeIndex = Array.IndexOf(arguments, "--resume");
            Assert.True(result.Success);
            Assert.Equal(normalizedId, result.ConversationId);
            Assert.True(resumeIndex >= 0);
            Assert.Equal(normalizedId, arguments[resumeIndex + 1]);
            Assert.DoesNotContain(requestedId, arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "OPENHANDS_CONVERSATIONS_DIR",
                priorConversationsDirectory);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ResumePreflightRejectsMissingStateBeforeLaunch()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = await RunRejectedResumePreflightAsync(
            static (_, _) => Task.CompletedTask);

        Assert.Contains(
            "unavailable or invalid",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ResumePreflightRejectsMismatchedConversationId()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = await RunRejectedResumePreflightAsync(
            (statePath, _) => File.WriteAllTextAsync(
                statePath,
                JsonSerializer.Serialize(new
                {
                    id = "11111111-1111-1111-1111-111111111111",
                    execution_status = "finished",
                })));

        Assert.Contains(
            "does not match this queue tab",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ResumePreflightRejectsOversizedState()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = await RunRejectedResumePreflightAsync(
            static (statePath, _) => File.WriteAllBytesAsync(
                statePath,
                new byte[(2 * 1024 * 1024) + 1]));

        Assert.Contains(
            "unavailable or invalid",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ResumePreflightRejectsSymbolicLinkState()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = await RunRejectedResumePreflightAsync(
            async (statePath, normalizedId) =>
            {
                var targetPath = Path.Combine(
                    Path.GetDirectoryName(Path.GetDirectoryName(statePath))!,
                    "outside-state.json");
                await File.WriteAllTextAsync(
                    targetPath,
                    JsonSerializer.Serialize(new
                    {
                        id = normalizedId,
                        execution_status = "finished",
                    }));
                File.CreateSymbolicLink(statePath, targetPath);
            });

        Assert.Contains(
            "unavailable or invalid",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_TreatsPersistedStuckStateAsFailureEvenWhenCliExitsZero()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunWithPersistedStateAsync("stuck");

        Assert.False(result.Success);
        Assert.True(result.ReportedError);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("became stuck", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ConversationErrorEvent", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ConvertsNonJsonCliFailureToActionableSafeError()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string rawMarker = "private-cli-traceback-marker-14b2";
        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-failure-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var executable = Path.Combine(testRoot, "fake-openhands");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".git"));
        await File.WriteAllTextAsync(
            executable,
            "#!/bin/sh\nprintf '%s\\n' '" + rawMarker + "' >&2\nexit 127\n");
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var runner = new OpenHandsCommandRunner(
                NullLogger<OpenHandsCommandRunner>.Instance,
                HealthyLocalAiOptions(executable));
            var result = await runner.RunAsync(
                new TargetMachine
                {
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                },
                projectRoot,
                "openai/qwen2.5-coder:32b",
                "http://ollama.test:11434/v1",
                AiProviderService.LocalPlaceholderApiKey,
                null,
                "perform the test task",
                alwaysApproveConfirmed: true,
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(result.ReportedError);
            Assert.Contains("not installed", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(rawMarker, result.Output, StringComparison.Ordinal);
            Assert.Contains(rawMarker, result.RawDiagnosticOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_DoesNotCreateAMissingSelectedProject()
    {
        var missingProject = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-missing-project-tests",
            Guid.NewGuid().ToString("N"),
            "missing");
        var runner = new OpenHandsCommandRunner(
            NullLogger<OpenHandsCommandRunner>.Instance,
            HealthyLocalAiOptions());

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => runner.RunAsync(
            new TargetMachine
            {
                Kind = MachineKind.Local,
                Platform = MachinePlatform.Linux,
            },
            missingProject,
            "openai/qwen2.5-coder:32b",
            "http://ollama.test:11434/v1",
            AiProviderService.LocalPlaceholderApiKey,
            null,
            "do not execute",
            alwaysApproveConfirmed: true,
            _ => Task.CompletedTask,
            CancellationToken.None));

        Assert.False(Directory.Exists(missingProject));
    }

    [Fact]
    public async Task RunAsync_TargetLocalAiPreflightFailsBeforeProjectAccessOrAgentLaunch()
    {
        var missingProject = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-target-preflight-tests",
            Guid.NewGuid().ToString("N"),
            "missing");
        var probeCalls = 0;
        var runner = new OpenHandsCommandRunner(
            NullLogger<OpenHandsCommandRunner>.Instance,
            new OpenHandsCommandOptions(
                "must-not-launch-openhands",
                (_, _, model, _) =>
                {
                    probeCalls++;
                    Assert.Equal("openai/qwen2.5-coder:32b", model);
                    return Task.FromResult(new OpenHandsLocalAiCheck(
                        false,
                        null,
                        "No route from the selected machine."));
                }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            new TargetMachine
            {
                Kind = MachineKind.Local,
                Platform = MachinePlatform.Linux,
            },
            missingProject,
            "openai/qwen2.5-coder:32b",
            "http://ollama.test:11434/v1",
            AiProviderService.LocalPlaceholderApiKey,
            null,
            "prompt-marker-must-not-be-used",
            alwaysApproveConfirmed: true,
            _ => Task.CompletedTask,
            CancellationToken.None));

        Assert.Equal(1, probeCalls);
        Assert.Contains("cannot reach", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(missingProject));
    }

    [Fact]
    public async Task RunAsync_CancellationKillsLocalProcessTreeAndRemovesTaskFiles()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-cancellation-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var gitDirectory = Path.Combine(projectRoot, ".git");
        var executable = Path.Combine(testRoot, "fake-openhands");
        var childPidPath = Path.Combine(projectRoot, "fake-child.pid");
        Directory.CreateDirectory(gitDirectory);
        await File.WriteAllTextAsync(
            executable,
            """
            #!/bin/sh
            printf '%s\n' '{"kind":"MessageEvent","llm_message":{"role":"assistant","content":"started"}}'
            sleep 30 &
            child=$!
            printf '%s\n' "$child" > "$PWD/fake-child.pid"
            wait "$child"
            """);
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var runner = new OpenHandsCommandRunner(
                NullLogger<OpenHandsCommandRunner>.Instance,
                HealthyLocalAiOptions(executable));
            using var cancellation = new CancellationTokenSource();
            var run = runner.RunAsync(
                new TargetMachine
                {
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                },
                projectRoot,
                "openai/qwen2.5-coder:32b",
                "http://ollama.test:11434/v1",
                AiProviderService.LocalPlaceholderApiKey,
                null,
                "perform the test task",
                alwaysApproveConfirmed: true,
                _ => Task.CompletedTask,
                cancellation.Token);

            await WaitUntilAsync(() => File.Exists(childPidPath), TimeSpan.FromSeconds(5));
            var childPid = int.Parse((await File.ReadAllTextAsync(childPidPath)).Trim());
            cancellation.Cancel();

            var exception = await Assert.ThrowsAsync<OpenHandsRunCancelledException>(() => run);
            Assert.Contains("MessageEvent", exception.RawDiagnosticOutput, StringComparison.Ordinal);
            await WaitUntilAsync(() => !ProcessExists(childPid), TimeSpan.FromSeconds(5));

            var taskRoot = Path.Combine(gitDirectory, "codex-queue", "openhands");
            Assert.False(
                Directory.Exists(taskRoot)
                && Directory.EnumerateFileSystemEntries(taskRoot, "*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RemoteProcessTreeFunction_TerminatesChildrenAndGrandchildren()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-remote-tree-tests",
            Guid.NewGuid().ToString("N"));
        var childPidPath = Path.Combine(testRoot, "child.pid");
        var grandchildPidPath = Path.Combine(testRoot, "grandchild.pid");
        var executable = Path.Combine(testRoot, "process-tree");
        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(
            executable,
            "#!/bin/sh\n"
            + "sh -c 'sleep 60 & echo $! > \""
            + grandchildPidPath
            + "\"; wait' &\n"
            + "echo $! > \""
            + childPidPath
            + "\"\n"
            + "wait\n");
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var rootProcess = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
        })!;
        try
        {
            await WaitUntilAsync(
                () => File.Exists(childPidPath) && File.Exists(grandchildPidPath),
                TimeSpan.FromSeconds(5));
            var childPid = int.Parse((await File.ReadAllTextAsync(childPidPath)).Trim());
            var grandchildPid = int.Parse((await File.ReadAllTextAsync(grandchildPidPath)).Trim());

            using var terminator = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList =
                {
                    "-c",
                    OpenHandsCommandRunner.BuildRemoteProcessTreeFunctions()
                    + "kill_openhands_tree "
                    + rootProcess.Id,
                },
                UseShellExecute = false,
            })!;
            await terminator.WaitForExitAsync();

            Assert.Equal(0, terminator.ExitCode);
            try
            {
                await WaitUntilAsync(
                    () => !ProcessIsRunning(childPid) && !ProcessIsRunning(grandchildPid),
                    TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                Assert.Fail(
                    $"Remote cleanup left a live process: child={DescribeProcess(childPid)}, "
                    + $"grandchild={DescribeProcess(grandchildPid)}.");
            }
        }
        finally
        {
            if (!rootProcess.HasExited)
            {
                rootProcess.Kill(entireProcessTree: true);
            }

            await rootProcess.WaitForExitAsync();
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsSymbolicLinkInTemporaryTaskPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-symlink-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var outsideDirectory = Path.Combine(testRoot, "outside");
        var temporaryLink = Path.Combine(projectRoot, ".codex-queue");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(outsideDirectory);
        Directory.CreateSymbolicLink(temporaryLink, outsideDirectory);

        try
        {
            var runner = new OpenHandsCommandRunner(
                NullLogger<OpenHandsCommandRunner>.Instance,
                HealthyLocalAiOptions());

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
                new TargetMachine
                {
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                },
                projectRoot,
                "openai/qwen2.5-coder:32b",
                "http://ollama.test:11434/v1",
                AiProviderService.LocalPlaceholderApiKey,
                null,
                "do not execute",
                alwaysApproveConfirmed: true,
                _ => Task.CompletedTask,
                CancellationToken.None));

            Assert.Contains("symbolic link", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outsideDirectory));
        }
        finally
        {
            if (Directory.Exists(temporaryLink))
            {
                Directory.Delete(temporaryLink);
            }
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Test condition was not reached before the timeout.");
            }

            await Task.Delay(25);
        }
    }

    private static bool ProcessExists(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool ProcessIsRunning(int processId)
    {
        if (!ProcessExists(processId))
        {
            return false;
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{processId}/stat");
                var commandEnd = stat.LastIndexOf(')');
                if (commandEnd >= 0 && commandEnd + 2 < stat.Length)
                {
                    return stat[commandEnd + 2] != 'Z';
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // Fall back to Process.HasExited below.
            }
        }

        return true;
    }

    private static string DescribeProcess(int processId)
    {
        if (!ProcessExists(processId))
        {
            return "exited";
        }

        try
        {
            return File.ReadAllText($"/proc/{processId}/stat");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.GetType().Name;
        }
    }

    private static async Task<OpenHandsCommandResult> RunWithPersistedStateAsync(
        string executionStatus,
        string? executableContents = null)
    {
        const string conversationId = "0123456789abcdef0123456789abcdef";
        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-state-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var conversationsRoot = Path.Combine(testRoot, "conversations");
        var conversationRoot = Path.Combine(conversationsRoot, conversationId);
        var executable = Path.Combine(testRoot, "fake-openhands");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".git"));
        Directory.CreateDirectory(conversationRoot);
        await File.WriteAllTextAsync(
            Path.Combine(conversationRoot, "base_state.json"),
            JsonSerializer.Serialize(new { execution_status = executionStatus }));
        await File.WriteAllTextAsync(
            executable,
            executableContents
            ?? """
            #!/bin/sh
            printf '%s\n' '{"kind":"MessageEvent","source":"agent","llm_message":{"role":"assistant","content":[{"type":"text","text":"Task complete"}]}}'
            printf '%s\n' 'Conversation ID: 0123456789abcdef0123456789abcdef'
            exit 0
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var priorConversationsDirectory = Environment.GetEnvironmentVariable(
            "OPENHANDS_CONVERSATIONS_DIR");
        Environment.SetEnvironmentVariable(
            "OPENHANDS_CONVERSATIONS_DIR",
            conversationsRoot);
        try
        {
            var runner = new OpenHandsCommandRunner(
                NullLogger<OpenHandsCommandRunner>.Instance,
                HealthyLocalAiOptions(executable));
            return await runner.RunAsync(
                new TargetMachine
                {
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                },
                projectRoot,
                "openai/qwen2.5-coder:32b",
                "http://ollama.test:11434/v1",
                AiProviderService.LocalPlaceholderApiKey,
                null,
                "perform the test task",
                alwaysApproveConfirmed: true,
                _ => Task.CompletedTask,
                CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "OPENHANDS_CONVERSATIONS_DIR",
                priorConversationsDirectory);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task<InvalidOperationException> RunRejectedResumePreflightAsync(
        Func<string, string, Task> arrangeState)
    {
        const string conversationId = "0123456789abcdef0123456789abcdef";
        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-rejected-resume-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var conversationsRoot = Path.Combine(testRoot, "conversations");
        var conversationRoot = Path.Combine(conversationsRoot, conversationId);
        var statePath = Path.Combine(conversationRoot, "base_state.json");
        var executable = Path.Combine(testRoot, "fake-openhands");
        var launchedPath = Path.Combine(projectRoot, "openhands-launched");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".git"));
        Directory.CreateDirectory(conversationRoot);
        await arrangeState(statePath, conversationId);
        await File.WriteAllTextAsync(
            executable,
            """
            #!/bin/sh
            : > "$PWD/openhands-launched"
            exit 0
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var priorConversationsDirectory = Environment.GetEnvironmentVariable(
            "OPENHANDS_CONVERSATIONS_DIR");
        Environment.SetEnvironmentVariable(
            "OPENHANDS_CONVERSATIONS_DIR",
            conversationsRoot);
        try
        {
            var runner = new OpenHandsCommandRunner(
                NullLogger<OpenHandsCommandRunner>.Instance,
                HealthyLocalAiOptions(executable));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
                new TargetMachine
                {
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                },
                projectRoot,
                "openai/qwen2.5-coder:32b",
                "http://ollama.test:11434/v1",
                AiProviderService.LocalPlaceholderApiKey,
                conversationId,
                "do not execute this continued task",
                alwaysApproveConfirmed: true,
                _ => Task.CompletedTask,
                CancellationToken.None));

            Assert.False(File.Exists(launchedPath));
            Assert.DoesNotContain(testRoot, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(conversationId, exception.Message, StringComparison.Ordinal);
            return exception;
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "OPENHANDS_CONVERSATIONS_DIR",
                priorConversationsDirectory);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static OpenHandsCommandOptions HealthyLocalAiOptions(
        string localExecutable = "openhands") =>
        new(
            localExecutable,
            static (_, _, _, _) => Task.FromResult(
                new OpenHandsLocalAiCheck(
                    true,
                    true,
                    "Target-side Local AI check passed.")));

    private static void AssertSafeJson(
        OpenHandsSafeLine line,
        string expectedKind,
        string expectedVisibleText,
        bool expectedReportedError = false)
    {
        Assert.Equal(expectedReportedError, line.ReportedError);
        Assert.NotNull(line.Content);
        using var document = JsonDocument.Parse(line.Content);
        Assert.Equal(expectedKind, document.RootElement.GetProperty("kind").GetString());
        Assert.Contains(expectedVisibleText, line.Content, StringComparison.Ordinal);
    }
}
