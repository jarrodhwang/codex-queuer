using System.Diagnostics;
using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;

namespace CodexQueue.Api.Tests;

public sealed class TargetCommandRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildCodexArguments_CloudRunContainsNoLocalProviderSettings(bool resume)
    {
        var arguments = TargetCommandRunner.BuildCodexArguments(
            "/workspace/project",
            "gpt-5.6",
            "high",
            "priority",
            resume ? "cloud-session-id" : null,
            ["/workspace/image.png"],
            PermissionMode.ApproveForMe,
            disableWindowsSandbox: false);

        Assert.DoesNotContain(arguments, argument =>
            argument.Contains("codex_queue_local", StringComparison.Ordinal)
            || argument.StartsWith("model_context_window=", StringComparison.Ordinal)
            || argument.Contains("requires_openai_auth", StringComparison.Ordinal)
            || argument.Contains("base_url", StringComparison.Ordinal)
            || argument.Contains("wire_api", StringComparison.Ordinal));
        Assert.DoesNotContain("--oss", arguments);
        Assert.DoesNotContain("--local-provider", arguments);
        AssertOptionValue(arguments, "-m", "gpt-5.6");
        AssertConfig(arguments, "model_reasoning_effort=\"high\"");
        AssertConfig(arguments, "service_tier=\"priority\"");
        Assert.Equal("exec", arguments[0]);
        Assert.Equal(resume ? "resume" : "--json", arguments[1]);
        Assert.Equal("-", arguments[^1]);
    }

    [Theory]
    [InlineData(LocalAiServerType.Ollama, "Ollama", false)]
    [InlineData(LocalAiServerType.Ollama, "Ollama", true)]
    [InlineData(LocalAiServerType.LmStudio, "LM Studio", false)]
    [InlineData(LocalAiServerType.LmStudio, "LM Studio", true)]
    [InlineData(LocalAiServerType.LlamaCpp, "llama.cpp", false)]
    [InlineData(LocalAiServerType.LlamaCpp, "llama.cpp", true)]
    public void BuildCodexArguments_LocalRunUsesCustomProviderForFreshAndResume(
        LocalAiServerType serverType,
        string serverName,
        bool resume)
    {
        const string projectPath = "/workspace/project with spaces";
        const string model = "openai/acme/model-exact";
        const string baseUrl = "http://10.20.30.40:8080/v1";
        const string promptMarker = "prompt-must-stay-on-stdin-7a93";
        const string sessionId = "local-session-id";
        var arguments = TargetCommandRunner.BuildCodexArguments(
            projectPath,
            model,
            "HIGH",
            modelSpeed: null,
            resume ? sessionId : null,
            imagePaths: null,
            PermissionMode.FullAccess,
            disableWindowsSandbox: false,
            new LocalCodexProviderOptions(serverType, baseUrl, 131_072));

        Assert.Equal("exec", arguments[0]);
        Assert.Equal(resume ? "resume" : "--json", arguments[1]);
        AssertOptionValue(arguments, "-m", model);
        AssertConfig(arguments, "model_provider=\"codex_queue_local\"");
        AssertConfig(
            arguments,
            "model_providers.codex_queue_local.name=\"" + serverName + "\"");
        AssertConfig(
            arguments,
            "model_providers.codex_queue_local.base_url=\"" + baseUrl + "\"");
        AssertConfig(
            arguments,
            "model_providers.codex_queue_local.wire_api=\"responses\"");
        AssertConfig(
            arguments,
            "model_providers.codex_queue_local.requires_openai_auth=false");
        AssertConfig(arguments, "model_context_window=131072");
        AssertConfig(arguments, "model_reasoning_effort=\"HIGH\"");
        AssertConfig(arguments, "approval_policy=\"never\"");
        Assert.DoesNotContain("--oss", arguments);
        Assert.DoesNotContain("--local-provider", arguments);
        Assert.DoesNotContain(promptMarker, arguments);
        Assert.Equal("-", arguments[^1]);

        if (resume)
        {
            Assert.DoesNotContain("-C", arguments);
            Assert.DoesNotContain("-s", arguments);
            AssertConfig(arguments, "sandbox_mode=\"danger-full-access\"");
            Assert.Equal(sessionId, arguments[^2]);
            Assert.DoesNotContain("--color", arguments);
        }
        else
        {
            AssertOptionValue(arguments, "--color", "never");
            AssertOptionValue(arguments, "-C", projectPath);
            AssertOptionValue(arguments, "-s", "danger-full-access");
            Assert.DoesNotContain(sessionId, arguments);
        }
    }

    [Fact]
    public void ResolveTargetLocalAiBaseUrl_RewritesDockerHostOnlyForMatchingSshTarget()
    {
        var dockerHostMachine = new TargetMachine
        {
            Kind = MachineKind.Ssh,
            Host = "HOST.DOCKER.INTERNAL",
        };
        var remoteMachine = new TargetMachine
        {
            Kind = MachineKind.Ssh,
            Host = "worker.example.test",
        };

        var rewritten = TargetCommandRunner.ResolveTargetLocalAiBaseUrl(
            dockerHostMachine,
            "http://host.docker.internal:11434/v1");
        var preserved = TargetCommandRunner.ResolveTargetLocalAiBaseUrl(
            remoteMachine,
            "http://10.20.30.40:8080/v1");

        Assert.Equal("http://127.0.0.1:11434/v1", rewritten);
        Assert.Equal("http://10.20.30.40:8080/v1", preserved);
    }

    [Fact]
    public void ParseLocalAiProbeResponse_ReadsExactModelIdsFromOpenAiCatalog()
    {
        const string output =
            "$ check Local AI /v1/models\n"
            + """{"object":"list","data":[{"id":"foo"},{"id":"openai/foo"}]}""";

        var exact = TargetCommandRunner.ParseLocalAiProbeResponse(
            output,
            "openai/foo");
        var missing = TargetCommandRunner.ParseLocalAiProbeResponse(
            output,
            "missing");
        var wrongCase = TargetCommandRunner.ParseLocalAiProbeResponse(
            output,
            "OpenAI/foo");
        var unreadable = TargetCommandRunner.ParseLocalAiProbeResponse(
            "not-json",
            "foo");

        Assert.True(exact.Reachable);
        Assert.True(exact.SelectedModelAvailable);
        Assert.Contains(
            "Responses generation is checked when a task starts",
            exact.Message,
            StringComparison.Ordinal);
        Assert.True(missing.Reachable);
        Assert.False(missing.SelectedModelAvailable);
        Assert.False(wrongCase.SelectedModelAvailable);
        Assert.True(unreadable.Reachable);
        Assert.Null(unreadable.SelectedModelAvailable);
        Assert.Contains(
            "/v1/models",
            unreadable.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSanitizedLocalCodexEnvironment_ExcludesApiAndCloudSecrets()
    {
        const string queueSecret = "CQ_LOCAL_CODEX_TEST_SECRET";
        var previousQueueSecret = Environment.GetEnvironmentVariable(queueSecret);
        try
        {
            Environment.SetEnvironmentVariable(queueSecret, "must-not-reach-local-agent");

            var environment =
                TargetCommandRunner.BuildSanitizedLocalCodexEnvironment();

            Assert.DoesNotContain(queueSecret, environment.Keys);
            Assert.DoesNotContain("OPENAI_API_KEY", environment.Keys);
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PATH")))
            {
                Assert.Contains("PATH", environment.Keys);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(queueSecret, previousQueueSecret);
        }
    }

    [Fact]
    public void BuildRemoteLocalCodexEnvironmentSanitizers_KeepRuntimeVariablesOnly()
    {
        var unix =
            TargetCommandRunner.BuildUnixLocalCodexEnvironmentSanitizer();
        var powerShell =
            TargetCommandRunner.BuildPowerShellLocalCodexEnvironmentSanitizer();

        Assert.StartsWith("set -- env -i;", unix, StringComparison.Ordinal);
        Assert.Contains("exec \"$@\"", unix, StringComparison.Ordinal);
        Assert.Contains("PATH", unix, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENAI_API_KEY", unix, StringComparison.Ordinal);
        Assert.DoesNotContain("CQ_", unix, StringComparison.Ordinal);

        Assert.Contains("Remove-Item", powerShell, StringComparison.Ordinal);
        Assert.Contains("'PATH'", powerShell, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENAI_API_KEY", powerShell, StringComparison.Ordinal);
        Assert.DoesNotContain("CQ_", powerShell, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnixLocalCodexEnvironmentSanitizer_ExecutesWithAllowlistedEnvironmentOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            TargetCommandRunner.BuildUnixLocalCodexEnvironmentSanitizer()
            + " env");
        startInfo.Environment.Clear();
        startInfo.Environment["HOME"] = "/tmp/codex-queue-test-home";
        startInfo.Environment["PATH"] = "/usr/bin:/bin";
        startInfo.Environment["OPENAI_API_KEY"] = "must-not-reach-codex";
        startInfo.Environment["CQ_LOCAL_CODEX_TEST_SECRET"] =
            "must-not-reach-codex";

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        Assert.True(
            process.ExitCode == 0,
            "Environment sanitizer failed: " + error);
        Assert.Contains(
            "HOME=/tmp/codex-queue-test-home",
            output,
            StringComparison.Ordinal);
        Assert.Contains("PATH=/usr/bin:/bin", output, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENAI_API_KEY", output, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CQ_LOCAL_CODEX_TEST_SECRET",
            output,
            StringComparison.Ordinal);
    }

    private static void AssertConfig(
        IReadOnlyList<string> arguments,
        string expected)
    {
        var index = arguments.ToList().IndexOf(expected);
        Assert.True(index > 0, "Missing Codex config argument: " + expected);
        Assert.Equal("-c", arguments[index - 1]);
    }

    private static void AssertOptionValue(
        IReadOnlyList<string> arguments,
        string option,
        string expected)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.True(index >= 0, "Missing Codex option: " + option);
        Assert.True(index + 1 < arguments.Count, "Codex option has no value: " + option);
        Assert.Equal(expected, arguments[index + 1]);
    }
}
