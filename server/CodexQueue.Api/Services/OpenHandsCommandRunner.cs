using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodexQueue.Api.Domain;

namespace CodexQueue.Api.Services;

public interface IOpenHandsCommandRunner
{
    Task<OpenHandsCommandResult> RunAsync(
        TargetMachine machine,
        string projectPath,
        string model,
        string baseUrl,
        string apiKey,
        string? conversationId,
        string prompt,
        bool alwaysApproveConfirmed,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken);

    Task<OpenHandsMachineCheck> TestMachineAsync(
        TargetMachine machine,
        CancellationToken cancellationToken,
        string? localAiBaseUrl = null,
        string? selectedModel = null);
}

public sealed record OpenHandsCommandResult(
    int ExitCode,
    string Output,
    string RawDiagnosticOutput,
    string CommandPreview,
    string? ConversationId,
    bool ReportedError,
    bool ReportedFinished = false)
{
    public bool Success => ExitCode == 0 && !ReportedError;
}

public sealed record OpenHandsMachineCheck(
    bool Available,
    string? Version,
    bool RequiresWsl,
    string Message,
    bool TargetLocalAiChecked = false,
    bool? TargetLocalAiReachable = null,
    bool? TargetSelectedModelAvailable = null,
    string? TargetLocalAiMessage = null);

public sealed record OpenHandsLocalAiCheck(
    bool Reachable,
    bool? SelectedModelAvailable,
    string Message);

public sealed class OpenHandsRunCancelledException(
    string rawDiagnosticOutput,
    string? conversationId,
    CancellationToken cancellationToken)
    : OperationCanceledException("OpenHands run was cancelled.", cancellationToken)
{
    public string RawDiagnosticOutput { get; } = rawDiagnosticOutput;
    public string? ConversationId { get; } = conversationId;
}

public sealed record OpenHandsCommandOptions(
    string LocalExecutable = "openhands",
    Func<TargetMachine, string, string?, CancellationToken, Task<OpenHandsLocalAiCheck>>?
        LocalAiProbeOverride = null);

public sealed class OpenHandsCommandRunner : IOpenHandsCommandRunner
{
    private const int MaximumCapturedCharacters = 512_000;
    private const int MaximumOutputLineCharacters = 256_000;
    private const int MaximumConversationStateBytes = 2 * 1024 * 1024;
    private const int MaximumCapturedProcessCharacters = MaximumConversationStateBytes + 4_096;
    private const int MaximumLocalAiProbeBytes = 1024 * 1024;
    private const string RemoteProcessTreeFunctions =
        "kill_openhands_tree() { "
        + "root_pid=$1; case \"$root_pid\" in *[!0-9]*|'') return 64;; esac; "
        + "kill -STOP \"$root_pid\" >/dev/null 2>&1 || return 0; "
        + "descendants=''; frontier=\"$root_pid\"; "
        + "while [ -n \"$frontier\" ]; do "
        + "next_frontier=''; "
        + "for parent_pid in $frontier; do "
        + "for descendant_pid in $(ps -axo pid=,ppid= | awk -v parent=\"$parent_pid\" '$2 == parent { print $1 }'); do "
        + "kill -STOP \"$descendant_pid\" >/dev/null 2>&1 || true; "
        + "descendants=\"$descendants $descendant_pid\"; "
        + "next_frontier=\"$next_frontier $descendant_pid\"; "
        + "done; done; "
        + "frontier=\"$next_frontier\"; "
        + "done; "
        + "for process_pid in $descendants $root_pid; do "
        + "kill -TERM \"$process_pid\" >/dev/null 2>&1 || true; "
        + "kill -CONT \"$process_pid\" >/dev/null 2>&1 || true; "
        + "done; "
        + "sleep 1; "
        + "for process_pid in $descendants $root_pid; do "
        + "kill -KILL \"$process_pid\" >/dev/null 2>&1 || true; "
        + "done; "
        + "}; ";
    private static readonly TimeSpan MachineCheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LocalAiProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FirstOutputTimeout = TimeSpan.FromSeconds(90);
    private static readonly Regex ConversationIdPattern = new(
        @"\A[ \t]*Conversation[ \t]+ID:[ \t]*(?<id>[0-9a-fA-F]{32}|[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})[ \t]*\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> TerminalConversationStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "error",
        "failed",
        "stuck",
    };
    private static readonly HashSet<string> BrowserVisibleEventKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "ActionEvent",
        "AgentErrorEvent",
        "ConversationErrorEvent",
        "ConversationStateUpdateEvent",
        "MessageEvent",
        "ObservationEvent",
    };
    private static readonly string[] HiddenFieldFragments =
    [
        "thought",
        "reasoning",
        "thinking",
        "chain_of_thought",
        "internal_reasoning",
        "analysis",
        "completion",
        "completion_log",
        "responses_reasoning_item",
        "prompt",
        "api_key",
    ];
    private static readonly string[] AllowedInheritedEnvironmentVariables =
    [
        "HOME",
        "LANG",
        "LC_ALL",
        "LC_CTYPE",
        "LOGNAME",
        "OPENHANDS_CONVERSATIONS_DIR",
        "OPENHANDS_PERSISTENCE_DIR",
        "PATH",
        "REQUESTS_CA_BUNDLE",
        "SHELL",
        "SSL_CERT_DIR",
        "SSL_CERT_FILE",
        "TEMP",
        "TERM",
        "TMP",
        "TMPDIR",
        "TZ",
        "USER",
        "XDG_CACHE_HOME",
        "XDG_CONFIG_HOME",
        "XDG_DATA_HOME",
        "XDG_STATE_HOME",
    ];
    private readonly ILogger<OpenHandsCommandRunner> _logger;
    private readonly OpenHandsCommandOptions _options;

    public OpenHandsCommandRunner(ILogger<OpenHandsCommandRunner> logger)
        : this(logger, new OpenHandsCommandOptions())
    {
    }

    public OpenHandsCommandRunner(
        ILogger<OpenHandsCommandRunner> logger,
        OpenHandsCommandOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public async Task<OpenHandsCommandResult> RunAsync(
        TargetMachine machine,
        string projectPath,
        string model,
        string baseUrl,
        string apiKey,
        string? conversationId,
        string prompt,
        bool alwaysApproveConfirmed,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (!alwaysApproveConfirmed)
        {
            throw new InvalidOperationException(
                "OpenHands headless mode auto-approves actions. Explicit unrestricted-access confirmation is required.");
        }

        if (machine.TargetsWindows())
        {
            throw new PlatformNotSupportedException(
                "Native Windows OpenHands CLI is not supported. Configure a Linux or macOS target; Windows requires a separately configured WSL target.");
        }

        ValidateExecutionInput(machine, projectPath, model, baseUrl, apiKey);
        var localAiCheck = await ProbeLocalAiAsync(
            machine,
            baseUrl,
            model,
            cancellationToken);
        EnsureLocalAiPreflightPassed(localAiCheck);
        var normalizedConversationId = NormalizeOptionalConversationId(conversationId);
        var runToken = Guid.NewGuid().ToString("N");
        var preview = BuildCommandPreview(model, normalizedConversationId);

        return machine.Kind == MachineKind.Local
            ? await RunLocalAsync(
                projectPath,
                runToken,
                model,
                baseUrl,
                apiKey,
                normalizedConversationId,
                prompt,
                preview,
                onOutput,
                cancellationToken)
            : await RunSshAsync(
                machine,
                projectPath,
                runToken,
                model,
                baseUrl,
                apiKey,
                normalizedConversationId,
                prompt,
                preview,
                onOutput,
                cancellationToken);
    }

    public async Task<OpenHandsMachineCheck> TestMachineAsync(
        TargetMachine machine,
        CancellationToken cancellationToken,
        string? localAiBaseUrl = null,
        string? selectedModel = null)
    {
        var cliCheck = await TestCliAsync(machine, cancellationToken);
        if (string.IsNullOrWhiteSpace(localAiBaseUrl))
        {
            return cliCheck;
        }

        var localAiCheck = await ProbeLocalAiAsync(
            machine,
            localAiBaseUrl,
            selectedModel,
            cancellationToken);
        return cliCheck with
        {
            TargetLocalAiChecked = true,
            TargetLocalAiReachable = localAiCheck.Reachable,
            TargetSelectedModelAvailable = localAiCheck.SelectedModelAvailable,
            TargetLocalAiMessage = localAiCheck.Message,
        };
    }

    private async Task<OpenHandsMachineCheck> TestCliAsync(
        TargetMachine machine,
        CancellationToken cancellationToken)
    {
        if (machine.TargetsWindows())
        {
            return new OpenHandsMachineCheck(
                false,
                null,
                true,
                "Native Windows OpenHands CLI is unsupported. OpenHands requires WSL; configure WSL as a separate SSH target after validating it manually.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(MachineCheckTimeout);
            ProcessCapture capture;
            string? versionOutput = null;
            if (machine.Kind == MachineKind.Local)
            {
                var versionCapture = await RunCapturedProcessAsync(
                    _options.LocalExecutable,
                    ["--version"],
                    null,
                    null,
                    timeout.Token);
                var helpCapture = await RunCapturedProcessAsync(
                    _options.LocalExecutable,
                    ["--help"],
                    null,
                    null,
                    timeout.Token);
                versionOutput = StripAnsi(versionCapture.Output).Trim();
                capture = new ProcessCapture(
                    versionCapture.ExitCode != 0 ? versionCapture.ExitCode : helpCapture.ExitCode,
                    versionCapture.Output + Environment.NewLine + helpCapture.Output,
                    versionCapture.StandardOutput
                    + Environment.NewLine
                    + helpCapture.StandardOutput);
            }
            else
            {
                var command = TargetCommandRunner.UnixRemotePathSetup
                    + " " + BuildRemoteDiagnosticEnvironmentSanitizer()
                    + "; if command -v openhands >/dev/null 2>&1; then openhands --version; openhands --help; else "
                    + "printf '%s\\n' 'OpenHands CLI was not found on this SSH session PATH.' >&2; exit 127; fi";
                capture = await RunCapturedProcessAsync(
                    "ssh",
                    BuildSshArguments(machine, command),
                    null,
                    null,
                    timeout.Token);
            }

            var output = StripAnsi(capture.Output).Trim();
            if (capture.ExitCode != 0)
            {
                return new OpenHandsMachineCheck(
                    false,
                    null,
                    false,
                    string.IsNullOrWhiteSpace(output)
                        ? "OpenHands CLI is unavailable on this machine."
                        : output);
            }

            var requiredFlags = new[]
            {
                "--headless",
                "--json",
                "--override-with-envs",
                "--always-approve",
                "--resume",
                "-f",
            };
            var missingFlags = requiredFlags
                .Where(flag => !output.Contains(flag, StringComparison.Ordinal))
                .ToArray();
            if (missingFlags.Length > 0)
            {
                return new OpenHandsMachineCheck(
                    false,
                    null,
                    false,
                    "Installed OpenHands CLI is incompatible. Missing required flags: "
                    + string.Join(", ", missingFlags)
                    + ".");
            }

            var versionLines = (string.IsNullOrWhiteSpace(versionOutput) ? output : versionOutput)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var version = versionLines.FirstOrDefault(line =>
                    line.StartsWith("OpenHands CLI ", StringComparison.OrdinalIgnoreCase))
                ?? versionLines.FirstOrDefault(line =>
                    line.Contains("OpenHands SDK v", StringComparison.OrdinalIgnoreCase))
                ?? "OpenHands CLI (version not reported)";
            return new OpenHandsMachineCheck(
                true,
                version,
                false,
                "OpenHands CLI is available and supports headless JSON execution.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new OpenHandsMachineCheck(false, null, false, "OpenHands CLI check timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenHands CLI check failed for machine {MachineId}", machine.Id);
            return new OpenHandsMachineCheck(false, null, false, "OpenHands CLI check failed: " + ex.Message);
        }
    }

    public async Task<OpenHandsLocalAiCheck> ProbeLocalAiAsync(
        TargetMachine machine,
        string baseUrl,
        string? selectedModel,
        CancellationToken cancellationToken)
    {
        var normalizedModel = string.IsNullOrWhiteSpace(selectedModel)
            ? null
            : AiProviderService.QualifyModel(AiProviderSource.Local, selectedModel);
        if (_options.LocalAiProbeOverride is not null)
        {
            return await _options.LocalAiProbeOverride(
                machine,
                baseUrl,
                normalizedModel,
                cancellationToken);
        }

        var command = BuildLocalAiProbeCommand(baseUrl);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(LocalAiProbeTimeout);
            var capture = machine.Kind == MachineKind.Local
                ? await RunCapturedProcessAsync(
                    "/bin/sh",
                    ["-c", command],
                    null,
                    null,
                    timeout.Token)
                : await RunCapturedProcessAsync(
                    "ssh",
                    BuildSshArguments(machine, command),
                    null,
                    null,
                    timeout.Token);

            if (capture.ExitCode != 0)
            {
                var diagnostic = SafeProbeDiagnostic(capture.Output);
                var message = capture.ExitCode == 127
                    && diagnostic.Contains("curl or wget", StringComparison.OrdinalIgnoreCase)
                        ? "The selected machine needs curl or wget to check its Local AI server route."
                        : "The selected machine could not reach the Local AI server /v1/models endpoint."
                          + (string.IsNullOrWhiteSpace(diagnostic) ? "" : " " + diagnostic);
                return new OpenHandsLocalAiCheck(false, null, message);
            }

            return ParseLocalAiProbeResponse(capture.StandardOutput, normalizedModel);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new OpenHandsLocalAiCheck(
                false,
                null,
                "The Local AI server check timed out on the selected machine.");
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or System.ComponentModel.Win32Exception
                                   or IOException)
        {
            _logger.LogWarning(
                ex,
                "Target-side Local AI check failed for machine {MachineId}",
                machine.Id);
            return new OpenHandsLocalAiCheck(
                false,
                null,
                "Codex Queue could not run the Local AI server check on the selected machine.");
        }
    }

    public static string BuildLocalAiProbeCommand(string baseUrl)
    {
        if (!AiProviderService.TryNormalizeBaseUrl(
                AiProviderSource.Local,
                baseUrl,
                out var normalizedBaseUrl,
                out var error))
        {
            throw new ArgumentException(error, nameof(baseUrl));
        }

        var modelsEndpoint = normalizedBaseUrl.TrimEnd('/') + "/models";
        return TargetCommandRunner.UnixRemotePathSetup
            + " " + BuildRemoteDiagnosticEnvironmentSanitizer()
            + "; umask 077; ulimit -f 2048 >/dev/null 2>&1 || true"
            + "; probe_url=" + TargetCommandRunner.Quote(modelsEndpoint)
            + "; probe_file=$(mktemp \"${TMPDIR:-/tmp}/codex-queue-ollama.XXXXXX\")"
            + " || { printf '%s\\n' 'Could not create a temporary Local AI probe file.' >&2; exit 74; }"
            + "; cleanup_local_ai_probe() { status=$?; trap - EXIT HUP INT TERM"
            + "; rm -f -- \"$probe_file\"; exit \"$status\"; }"
            + "; trap cleanup_local_ai_probe EXIT HUP INT TERM"
            + "; fetch_status=0"
            + "; if command -v curl >/dev/null 2>&1; then"
            + " curl --fail --silent --show-error --connect-timeout 4 --max-time 8"
            + " --max-filesize " + MaximumLocalAiProbeBytes
            + " --proto '=http,https' --proto-redir '=http,https'"
            + " \"$probe_url\" > \"$probe_file\" || fetch_status=$?"
            + "; elif command -v wget >/dev/null 2>&1; then"
            + " wget -q -T 8 -t 1 -O \"$probe_file\" \"$probe_url\" || fetch_status=$?"
            + "; else printf '%s\\n' 'curl or wget is required for the Local AI server check.' >&2; exit 127"
            + "; fi"
            + "; if [ \"$fetch_status\" -ne 0 ]; then"
            + " printf '%s\\n' 'Target Local AI request failed.' >&2; exit \"$fetch_status\"; fi"
            + "; probe_size=$(wc -c < \"$probe_file\" | tr -d '[:space:]')"
            + "; case \"$probe_size\" in *[!0-9]*|'')"
            + " printf '%s\\n' 'Could not determine Local AI response size.' >&2; exit 65;; esac"
            + "; if [ \"$probe_size\" -gt " + MaximumLocalAiProbeBytes + " ]; then"
            + " printf '%s\\n' 'Local AI model catalog exceeded the 1 MiB response limit.' >&2; exit 65; fi"
            + "; cat \"$probe_file\"";
    }

    public static OpenHandsLocalAiCheck ParseLocalAiProbeResponse(
        string response,
        string? selectedModel)
    {
        var normalizedModel = string.IsNullOrWhiteSpace(selectedModel)
            ? null
            : AiProviderService.QualifyModel(AiProviderSource.Local, selectedModel);
        try
        {
            using var document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return new OpenHandsLocalAiCheck(
                    true,
                    null,
                    "The selected machine reached the Local AI server, but /v1/models returned an unreadable model catalog.");
            }

            var models = data.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object
                               && item.TryGetProperty("id", out var id)
                               && id.ValueKind == JsonValueKind.String)
                .Select(item => item.GetProperty("id").GetString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!.Trim())
                .Take(1_001)
                .ToArray();
            if (models.Length > 1_000)
            {
                return new OpenHandsLocalAiCheck(
                    true,
                    null,
                    "The selected machine reached the Local AI server, but its model catalog exceeded the 1,000-model limit.");
            }

            if (normalizedModel is null)
            {
                return new OpenHandsLocalAiCheck(
                    true,
                    null,
                    "The selected machine reached the Local AI server and read its model catalog.");
            }

            var unqualifiedModel = normalizedModel["openai/".Length..];
            var available = models.Any(model =>
                string.Equals(model, unqualifiedModel, StringComparison.OrdinalIgnoreCase)
                || string.Equals(model, normalizedModel, StringComparison.OrdinalIgnoreCase));
            return new OpenHandsLocalAiCheck(
                true,
                available,
                available
                    ? "The selected machine can reach the Local AI server and the selected model is available."
                    : "The selected machine can reach the Local AI server, but the selected model is not installed.");
        }
        catch (JsonException)
        {
            return new OpenHandsLocalAiCheck(
                true,
                null,
                "The selected machine reached the Local AI server, but /v1/models did not return valid JSON.");
        }
    }

    private static void EnsureLocalAiPreflightPassed(OpenHandsLocalAiCheck check)
    {
        if (!check.Reachable)
        {
            throw new InvalidOperationException(
                "OpenHands was not started because the selected machine cannot reach the Local AI server. "
                + check.Message);
        }

        if (check.SelectedModelAvailable == false)
        {
            throw new InvalidOperationException(
                "OpenHands was not started because the selected model is not installed on the Local AI server as seen from the selected machine.");
        }

        if (check.SelectedModelAvailable is null)
        {
            throw new InvalidOperationException(
                "OpenHands was not started because the selected machine could not verify the selected model. "
                + check.Message);
        }
    }

    private static string SafeProbeDiagnostic(string value)
    {
        var diagnostic = StripAnsi(value)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (diagnostic.Length > 500)
        {
            diagnostic = diagnostic[..500];
        }

        return diagnostic;
    }

    public static IReadOnlyList<string> BuildArguments(string? conversationId, string taskFilePath)
    {
        var arguments = new List<string>
        {
            "--headless",
            "--json",
            "--override-with-envs",
            "--always-approve",
        };
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            arguments.Add("--resume");
            arguments.Add(conversationId.Trim());
        }

        arguments.Add("-f");
        arguments.Add(taskFilePath);
        return arguments;
    }

    public static string BuildRemoteProcessTreeFunctions() =>
        RemoteProcessTreeFunctions;

    public static string BuildRemoteConversationStateReadCommand(
        string projectRoot,
        string conversationId)
    {
        ValidateRemotePath(projectRoot);
        if (!TryNormalizeConversationId(conversationId, out var normalizedConversationId))
        {
            throw new ArgumentException(
                "OpenHands conversation ID must be a UUID.",
                nameof(conversationId));
        }

        return TargetCommandRunner.UnixRemotePathSetup
            + " " + BuildRemoteDiagnosticEnvironmentSanitizer()
            + "; cd " + TargetCommandRunner.Quote(projectRoot.TrimEnd('/'))
            + "; conversations_dir=${OPENHANDS_CONVERSATIONS_DIR:-${OPENHANDS_PERSISTENCE_DIR:-$HOME/.openhands}/conversations}"
            + "; state_file=\"$conversations_dir/"
            + normalizedConversationId
            + "/base_state.json\""
            + BuildConversationStateReadSuffix();
    }

    public static string BuildRemoteProjectLocationProbeCommand(string projectRoot)
    {
        ValidateRemotePath(projectRoot);
        return TargetCommandRunner.UnixRemotePathSetup
            + " " + BuildRemoteDiagnosticEnvironmentSanitizer()
            + "; project_root=" + TargetCommandRunner.Quote(projectRoot.TrimEnd('/'))
            + "; if [ ! -d \"$project_root\" ]; then"
            + " printf '%s\\n' 'Selected remote project directory is unavailable.' >&2; exit 66; fi"
            + "; resolved_project_root=$(cd -- \"$project_root\" 2>/dev/null && pwd -P)"
            + " || { printf '%s\\n' 'Selected remote project directory could not be resolved.' >&2; exit 66; }"
            + "; if [ \"$resolved_project_root\" = / ]; then"
            + " printf '%s\\n' 'Selected remote project resolves to the filesystem root.' >&2; exit 64; fi"
            + "; if [ -d \"$project_root/.git\" ] && [ ! -L \"$project_root/.git\" ]; then"
            + " printf '%s\\n' git; else printf '%s\\n' fallback; fi";
    }

    public static string BuildCommandPreview(string model, string? conversationId)
    {
        var continuation = string.IsNullOrWhiteSpace(conversationId) ? "" : " --resume <conversation-id>";
        return "openhands --headless --json --override-with-envs --always-approve"
            + continuation
            + " -f <temporary-task-file> [model "
            + model
            + "]";
    }

    public static string Redact(string value, string apiKey)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(apiKey))
        {
            return value;
        }

        return value.Replace(apiKey, "[REDACTED]", StringComparison.Ordinal);
    }

    public static string? ExtractConversationId(string value)
    {
        var lifecycleLine = StripAnsi(value).TrimEnd('\r', '\n');
        var match = ConversationIdPattern.Match(lifecycleLine);
        return match.Success ? match.Groups["id"].Value : null;
    }

    public static OpenHandsSafeLine SanitizeOutputLine(string line, string apiKey)
    {
        var redacted = Redact(StripAnsi(line), apiKey).TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(redacted))
        {
            return new OpenHandsSafeLine(null, false);
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(redacted);
        }
        catch (JsonException)
        {
            // Keep banners and other non-JSON diagnostics in the bounded raw stream,
            // but do not reflect arbitrary text that may contain internal model data.
            return new OpenHandsSafeLine(null, false);
        }

        if (parsed is not JsonObject eventObject)
        {
            return new OpenHandsSafeLine(
                JsonSerializer.Serialize(new { kind = "OpenHandsEvent", message = "OpenHands emitted an event." })
                    + Environment.NewLine,
                false);
        }

        var kind = ReadString(eventObject, "kind") ?? ReadString(eventObject, "type") ?? "OpenHandsEvent";
        var terminalState = ReadConversationTerminalState(eventObject, kind);
        var isError = terminalState is not null
            && TerminalConversationStates.Contains(terminalState);
        var isFinished = string.Equals(
            terminalState,
            "finished",
            StringComparison.OrdinalIgnoreCase);
        if (!BrowserVisibleEventKinds.Contains(kind))
        {
            return new OpenHandsSafeLine(
                JsonSerializer.Serialize(new
                {
                    kind = "OpenHandsEvent",
                    message = "OpenHands status updated.",
                })
                    + Environment.NewLine,
                isError,
                isFinished);
        }

        JsonObject safe;
        if (kind.Equals("MessageEvent", StringComparison.OrdinalIgnoreCase))
        {
            safe = BuildSafeMessageEvent(eventObject, kind);
        }
        else
        {
            safe = (JsonObject)eventObject.DeepClone();
            RemoveHiddenFields(safe);
        }

        safe["kind"] = kind;
        return new OpenHandsSafeLine(
            Redact(safe.ToJsonString(), apiKey) + Environment.NewLine,
            isError,
            isFinished);
    }

    private async Task<OpenHandsCommandResult> RunLocalAsync(
        string projectPath,
        string runToken,
        string model,
        string baseUrl,
        string apiKey,
        string? conversationId,
        string prompt,
        string preview,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        var projectRoot = Path.GetFullPath(projectPath);
        if (!Directory.Exists(projectRoot))
        {
            throw new DirectoryNotFoundException(
                "The selected local project directory is unavailable.");
        }
        var physicalProjectRoot = ResolvePhysicalDirectoryPath(projectRoot);
        EnsureLocalProjectIsNotFileSystemRoot(physicalProjectRoot);

        var gitDirectory = Path.Combine(projectRoot, ".git");
        var temporaryParent = Directory.Exists(gitDirectory) && !IsSymbolicLink(gitDirectory)
            ? Path.Combine(projectRoot, ".git", "codex-queue", "openhands")
            : Path.Combine(projectRoot, ".codex-queue", "openhands");
        var temporaryRoot = Path.GetFullPath(Path.Combine(temporaryParent, runToken));
        EnsureChildPath(projectRoot, temporaryRoot);
        EnsureNoSymbolicLinkDescendant(projectRoot, temporaryParent);
        var taskPath = Path.Combine(temporaryRoot, "task.md");
        var tmuxDirectory = Path.Combine(temporaryRoot, "tmux");
        var environment = BuildExecutionEnvironment(
            model,
            baseUrl,
            apiKey,
            tmuxDirectory,
            projectRoot);

        await EnsureConversationCanResumeAsync(
            conversationId,
            id => ReadLocalConversationStateAsync(environment, id, cancellationToken));

        try
        {
            Directory.CreateDirectory(tmuxDirectory);
            EnsureNoSymbolicLinkDescendant(projectRoot, temporaryRoot);
            TrySetDirectoryMode(temporaryRoot);
            TrySetDirectoryMode(tmuxDirectory);
            await File.WriteAllTextAsync(taskPath, prompt, new UTF8Encoding(false), cancellationToken);
            TrySetFileMode(taskPath);
            var result = await RunStreamingProcessAsync(
                _options.LocalExecutable,
                BuildArguments(conversationId, taskPath),
                projectRoot,
                environment,
                null,
                preview,
                apiKey,
                conversationId,
                onOutput,
                cancellationToken);
            return await VerifyFinalConversationStateAsync(
                result,
                id => ReadLocalConversationStateAsync(environment, id, cancellationToken),
                onOutput);
        }
        finally
        {
            await StopTmuxServerAsync(tmuxDirectory);
            DeleteNarrowDirectory(temporaryRoot);
        }
    }

    private async Task<OpenHandsCommandResult> RunSshAsync(
        TargetMachine machine,
        string projectPath,
        string runToken,
        string model,
        string baseUrl,
        string apiKey,
        string? conversationId,
        string prompt,
        string preview,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        ValidateRemotePath(projectPath);
        var projectRoot = projectPath.TrimEnd('/');
        var gitScopedRoot = projectRoot + "/.git/codex-queue/openhands/" + runToken;
        var fallbackRoot = projectRoot + "/.codex-queue/openhands/" + runToken;
        var locationProbe = await RunCapturedProcessAsync(
            "ssh",
            BuildSshArguments(
                machine,
                BuildRemoteProjectLocationProbeCommand(projectRoot)),
            null,
            null,
            cancellationToken);
        if (locationProbe.ExitCode != 0)
        {
            throw new IOException(
                "Could not inspect the selected remote project before preparing OpenHands task files. "
                + Redact(locationProbe.Output, apiKey).Trim());
        }

        var useGitDirectory = locationProbe.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() == "git";

        await EnsureConversationCanResumeAsync(
            conversationId,
            id => ReadRemoteConversationStateAsync(
                machine,
                projectRoot,
                id,
                cancellationToken));

        var temporaryRoot = useGitDirectory ? gitScopedRoot : fallbackRoot;
        var protectedAncestors = useGitDirectory
            ? new[]
            {
                projectRoot + "/.git",
                projectRoot + "/.git/codex-queue",
                projectRoot + "/.git/codex-queue/openhands",
            }
            : new[]
            {
                projectRoot + "/.codex-queue",
                projectRoot + "/.codex-queue/openhands",
            };
        var taskPath = temporaryRoot + "/task.md";
        var environmentPath = temporaryRoot + "/runner.env";
        var pidPath = temporaryRoot + "/openhands.pid";
        var tmuxDirectory = temporaryRoot + "/tmux";
        var environmentFile = BuildPosixEnvironmentFile(
            model,
            baseUrl,
            apiKey,
            tmuxDirectory,
            projectRoot);
        var uploadInput = Convert.ToBase64String(Encoding.UTF8.GetBytes(prompt))
            + "\n"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(environmentFile))
            + "\n";

        var setupCommand = TargetCommandRunner.UnixRemotePathSetup
            + " " + BuildRemoteDiagnosticEnvironmentSanitizer()
            + "; for guarded_path in "
            + string.Join(" ", protectedAncestors.Select(TargetCommandRunner.Quote))
            + "; do if [ -L \"$guarded_path\" ]; then printf '%s\\n' 'OpenHands temporary path contains a symbolic link.' >&2; exit 73; fi; done"
            + "; umask 077; mkdir -p -- " + TargetCommandRunner.Quote(tmuxDirectory)
            + "; IFS= read -r task_data; IFS= read -r env_data"
            + "; decode_data() { if base64 --help 2>&1 | grep -q -- '--decode'; then base64 --decode; else base64 -D; fi; }"
            + "; printf '%s' \"$task_data\" | decode_data > " + TargetCommandRunner.Quote(taskPath)
            + "; printf '%s' \"$env_data\" | decode_data > " + TargetCommandRunner.Quote(environmentPath)
            + "; chmod 600 -- " + TargetCommandRunner.Quote(taskPath) + " " + TargetCommandRunner.Quote(environmentPath);

        try
        {
            var setup = await RunCapturedProcessAsync(
                "ssh",
                BuildSshArguments(machine, setupCommand),
                null,
                uploadInput,
                cancellationToken);
            if (setup.ExitCode != 0)
            {
                var setupOutput = Redact(setup.Output, apiKey).Trim();
                throw new IOException(
                    "Could not prepare the project-scoped OpenHands task files on the selected machine."
                    + (string.IsNullOrWhiteSpace(setupOutput) ? "" : " " + setupOutput));
            }

            var arguments = string.Join(" ", BuildArguments(conversationId, taskPath).Select(TargetCommandRunner.Quote));
            var runCommand = TargetCommandRunner.UnixRemotePathSetup
                + " " + RemoteProcessTreeFunctions
                + "run_dir=" + TargetCommandRunner.Quote(temporaryRoot)
                + "; pid_file=" + TargetCommandRunner.Quote(pidPath)
                + "; tmux_dir=" + TargetCommandRunner.Quote(tmuxDirectory)
                + "; child=''"
                + "; cleanup_openhands() { status=$?; trap - EXIT HUP INT TERM"
                + "; if [ -n \"$child\" ]; then kill_openhands_tree \"$child\"; wait \"$child\" >/dev/null 2>&1 || true; fi"
                + "; TMUX_TMPDIR=\"$tmux_dir\" tmux -L openhands kill-server >/dev/null 2>&1 || true"
                + "; rm -rf -- \"$run_dir\"; exit \"$status\"; }"
                + "; trap cleanup_openhands EXIT HUP INT TERM"
                + "; set -a; . " + TargetCommandRunner.Quote(environmentPath) + "; set +a"
                + "; rm -f -- " + TargetCommandRunner.Quote(environmentPath)
                + "; " + BuildRemoteEnvironmentSanitizer()
                + "; cd " + TargetCommandRunner.Quote(projectRoot)
                + "; openhands " + arguments + " & child=$!"
                + "; printf '%s\\n' \"$child\" > \"$pid_file\""
                + "; wait \"$child\"; status=$?; child=''; exit \"$status\"";

            var result = await RunStreamingProcessAsync(
                "ssh",
                BuildSshArguments(machine, runCommand),
                null,
                null,
                null,
                preview,
                apiKey,
                conversationId,
                onOutput,
                cancellationToken);
            return await VerifyFinalConversationStateAsync(
                result,
                id => ReadRemoteConversationStateAsync(
                    machine,
                    projectRoot,
                    id,
                    cancellationToken),
                onOutput);
        }
        finally
        {
            await CleanupRemoteRunAsync(
                machine,
                temporaryRoot,
                taskPath,
                pidPath,
                tmuxDirectory);
        }
    }

    private async Task<OpenHandsCommandResult> RunStreamingProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        string? standardInput,
        string preview,
        string apiKey,
        string? existingConversationId,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        var startInfo = BuildStartInfo(
            fileName,
            arguments,
            workingDirectory,
            environment ?? BuildDiagnosticEnvironment(),
            replaceEnvironment: true);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var safeOutput = new BoundedTextBuffer(MaximumCapturedCharacters);
        var rawOutput = new BoundedTextBuffer(MaximumCapturedCharacters);
        var emitLock = new SemaphoreSlim(1, 1);
        var firstOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportedError = false;
        var reportedFinished = false;
        var conversationId = existingConversationId;
        var previewLine = "$ " + preview + Environment.NewLine;
        safeOutput.Append(previewLine);
        rawOutput.Append(previewLine);
        await onOutput(previewLine);

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start OpenHands.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start OpenHands process {FileName}", fileName);
            throw new InvalidOperationException(
                string.Equals(fileName, "ssh", StringComparison.Ordinal)
                    ? "Could not start the SSH client for the selected OpenHands machine."
                    : "OpenHands is not installed or could not be started on the selected machine.",
                ex);
        }

        async Task ReadStreamAsync(StreamReader reader)
        {
            await foreach (var outputLine in ReadBoundedLinesAsync(
                               reader,
                               cancellationToken))
            {
                firstOutput.TrySetResult();
                var line = outputLine.Content;
                var redactedRaw = Redact(line, apiKey) + Environment.NewLine;
                rawOutput.Append(redactedRaw);
                if (outputLine.Truncated)
                {
                    var truncatedEvent = JsonSerializer.Serialize(new
                    {
                        kind = "ObservationEvent",
                        source = "tool",
                        message =
                            "OpenHands emitted an oversized output event. "
                            + "The event was truncated to protect queue memory; inspect the selected machine if more detail is needed.",
                    })
                        + Environment.NewLine;
                    rawOutput.Append("[oversized OpenHands output event truncated]" + Environment.NewLine);
                    safeOutput.Append(truncatedEvent);
                    await emitLock.WaitAsync(cancellationToken);
                    try
                    {
                        await onOutput(truncatedEvent);
                    }
                    finally
                    {
                        emitLock.Release();
                    }
                    continue;
                }

                var discoveredId = ExtractConversationId(redactedRaw);
                if (!string.IsNullOrWhiteSpace(discoveredId))
                {
                    conversationId = discoveredId;
                }

                var safeLine = SanitizeOutputLine(line, apiKey);
                if (safeLine.ReportedError)
                {
                    reportedError = true;
                }
                if (safeLine.ReportedFinished)
                {
                    reportedFinished = true;
                }

                if (safeLine.Content is null)
                {
                    continue;
                }

                safeOutput.Append(safeLine.Content);
                await emitLock.WaitAsync(cancellationToken);
                try
                {
                    await onOutput(safeLine.Content);
                }
                finally
                {
                    emitLock.Release();
                }
            }
        }

        var stdout = ReadStreamAsync(process.StandardOutput);
        var stderr = ReadStreamAsync(process.StandardError);
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            }

            process.StandardInput.Close();
            var waitForExit = process.WaitForExitAsync(cancellationToken);
            var firstSignal = await Task.WhenAny(
                firstOutput.Task,
                waitForExit,
                Task.Delay(FirstOutputTimeout, cancellationToken));
            if (firstSignal != firstOutput.Task && firstSignal != waitForExit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryKill(process);
                try
                {
                    await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // The process tree was already terminated; do not let a stuck pipe
                    // obscure the actionable first-output timeout.
                }
                throw new TimeoutException(
                    "OpenHands produced no output within 90 seconds. Check the selected machine, OpenHands installation, Local AI server route, and selected model.");
            }

            await waitForExit;
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try
            {
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // Best effort after process-tree termination.
            }
            throw new OpenHandsRunCancelledException(
                rawOutput.ToString(),
                conversationId,
                cancellationToken);
        }

        if (process.ExitCode != 0 && !reportedError)
        {
            var failure = BuildSafeProcessFailureEvent(
                fileName,
                process.ExitCode,
                rawOutput.ToString());
            safeOutput.Append(failure);
            await emitLock.WaitAsync(CancellationToken.None);
            try
            {
                await onOutput(failure);
            }
            finally
            {
                emitLock.Release();
            }
            reportedError = true;
        }

        var effectiveExitCode = reportedError && process.ExitCode == 0 ? 1 : process.ExitCode;
        return new OpenHandsCommandResult(
            effectiveExitCode,
            safeOutput.ToString(),
            rawOutput.ToString(),
            preview,
            conversationId,
            reportedError,
            reportedFinished);
    }

    private static async IAsyncEnumerable<BoundedOutputLine> ReadBoundedLinesAsync(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new char[8_192];
        var line = new StringBuilder(Math.Min(MaximumOutputLineCharacters, buffer.Length));
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                break;
            }

            for (var index = 0; index < count; index++)
            {
                var value = buffer[index];
                if (value == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r')
                    {
                        line.Length--;
                    }

                    yield return new BoundedOutputLine(line.ToString(), truncated);
                    line.Clear();
                    truncated = false;
                    continue;
                }

                if (line.Length < MaximumOutputLineCharacters)
                {
                    line.Append(value);
                }
                else
                {
                    truncated = true;
                }
            }
        }

        if (line.Length > 0 || truncated)
        {
            yield return new BoundedOutputLine(line.ToString(), truncated);
        }
    }

    public static string BuildSafeProcessFailureEvent(
        string processName,
        int exitCode,
        string rawDiagnosticOutput)
    {
        var executable = Path.GetFileNameWithoutExtension(processName);
        var isSsh = string.Equals(executable, "ssh", StringComparison.OrdinalIgnoreCase);
        string code;
        string message;

        if (exitCode == 127
            || ContainsDiagnostic(rawDiagnosticOutput, "openhands: not found")
            || ContainsDiagnostic(rawDiagnosticOutput, "openhands: command not found"))
        {
            code = "OpenHandsNotInstalled";
            message =
                "OpenHands is not installed or is not on the selected machine's SSH PATH. Run the OpenHands machine check and install or expose the CLI.";
        }
        else if (isSsh && ContainsDiagnostic(rawDiagnosticOutput, "Permission denied"))
        {
            code = "SshAuthenticationFailed";
            message =
                "SSH authentication failed for the selected machine. Check its user, key path, and SSH access.";
        }
        else if (isSsh
                 && (ContainsDiagnostic(rawDiagnosticOutput, "Could not resolve hostname")
                     || ContainsDiagnostic(rawDiagnosticOutput, "Name or service not known")))
        {
            code = "SshHostResolutionFailed";
            message =
                "The selected machine's SSH host could not be resolved. Check the machine host and network configuration.";
        }
        else if (isSsh && (exitCode == 255
                           || ContainsDiagnostic(rawDiagnosticOutput, "Connection refused")
                           || ContainsDiagnostic(rawDiagnosticOutput, "Connection timed out")
                           || ContainsDiagnostic(rawDiagnosticOutput, "No route to host")))
        {
            code = "SshUnavailable";
            message =
                "Could not reach the selected machine over SSH. Check that it is online and that its host, port, VPN, and SSH service are available.";
        }
        else if (ContainsDiagnostic(rawDiagnosticOutput, "Connection refused")
                 || ContainsDiagnostic(rawDiagnosticOutput, "Connection timed out"))
        {
            code = "LocalAiUnavailable";
            message =
                "OpenHands could not reach the Local AI server. Check the Local AI profile, Ollama service, LAN/VPN route, and selected model.";
        }
        else
        {
            code = "OpenHandsProcessFailed";
            message =
                "OpenHands exited with code "
                + exitCode
                + ". Run the OpenHands machine check and verify the Local AI profile and selected model.";
        }

        return BuildSafeErrorEvent(code, message);
    }

    private async Task<OpenHandsCommandResult> VerifyFinalConversationStateAsync(
        OpenHandsCommandResult result,
        Func<string, Task<ConversationStateInspection>> inspect,
        Func<string, Task> onOutput)
    {
        if (result.ExitCode != 0 || result.ReportedError)
        {
            return result;
        }

        if (!TryNormalizeConversationId(result.ConversationId, out var conversationId))
        {
            return await WithReportedErrorAsync(
                result,
                "ConversationIdUnavailable",
                "OpenHands exited without reporting a valid conversation ID. The task was not marked successful; rerun the OpenHands machine check.",
                onOutput);
        }

        var inspection = await inspect(conversationId);
        if (string.Equals(
                inspection.ExecutionStatus,
                "finished",
                StringComparison.OrdinalIgnoreCase))
        {
            return result with { ConversationId = conversationId, ReportedFinished = true };
        }

        if (inspection.ExecutionStatus is { } executionStatus
            && TerminalConversationStates.Contains(executionStatus))
        {
            var message = string.Equals(
                    executionStatus,
                    "stuck",
                    StringComparison.OrdinalIgnoreCase)
                ? "OpenHands stopped because its conversation became stuck. Review the visible tool activity and refine the task before retrying."
                : "OpenHands ended in an error state. Review the visible error and raw diagnostics, then verify the Local AI server and selected model.";
            return await WithReportedErrorAsync(
                result with { ConversationId = conversationId },
                "OpenHands" + executionStatus.ToUpperInvariant(),
                message,
                onOutput);
        }

        // A future compatible CLI may emit a typed FINISHED state without using
        // today's local persistence layout. Prefer that explicit event over a
        // filesystem-layout assumption.
        if (result.ReportedFinished)
        {
            return result with { ConversationId = conversationId };
        }

        return await WithReportedErrorAsync(
            result with { ConversationId = conversationId },
            "ConversationFinalStateUnverified",
            "OpenHands exited without a verifiable finished conversation state. The task was not marked successful; update or recheck the OpenHands CLI before retrying.",
            onOutput);
    }

    private static async Task<OpenHandsCommandResult> WithReportedErrorAsync(
        OpenHandsCommandResult result,
        string code,
        string message,
        Func<string, Task> onOutput)
    {
        var errorEvent = BuildSafeErrorEvent(code, message);
        await onOutput(errorEvent);
        return result with
        {
            ExitCode = result.ExitCode == 0 ? 1 : result.ExitCode,
            Output = AppendBounded(result.Output, errorEvent),
            RawDiagnosticOutput = AppendBounded(result.RawDiagnosticOutput, errorEvent),
            ReportedError = true,
        };
    }

    private static async Task EnsureConversationCanResumeAsync(
        string? conversationId,
        Func<string, Task<ConversationStateInspection>> inspect)
    {
        if (conversationId is null)
        {
            return;
        }

        var inspection = await inspect(conversationId);
        if (!inspection.Readable)
        {
            throw new InvalidOperationException(
                "The saved OpenHands conversation is unavailable or invalid on the selected machine. "
                + "Restore it on this same machine or start a new queue tab before retrying.");
        }

        if (!string.Equals(
                inspection.ConversationId,
                conversationId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The saved OpenHands conversation does not match this queue tab on the selected machine. "
                + "Restore the matching conversation on this same machine or start a new queue tab before retrying.");
        }
    }

    private async Task<ConversationStateInspection> ReadLocalConversationStateAsync(
        IReadOnlyDictionary<string, string> environment,
        string conversationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var projectRoot = environment.GetValueOrDefault("OPENHANDS_WORK_DIR");
            var conversationsDirectory = environment.GetValueOrDefault(
                "OPENHANDS_CONVERSATIONS_DIR");
            if (string.IsNullOrWhiteSpace(conversationsDirectory))
            {
                var persistenceDirectory = environment.GetValueOrDefault(
                    "OPENHANDS_PERSISTENCE_DIR");
                if (string.IsNullOrWhiteSpace(persistenceDirectory))
                {
                    var homeDirectory = environment.GetValueOrDefault("HOME");
                    if (string.IsNullOrWhiteSpace(homeDirectory))
                    {
                        return ConversationStateInspection.Unavailable();
                    }

                    persistenceDirectory = Path.Combine(homeDirectory, ".openhands");
                }

                conversationsDirectory = Path.Combine(
                    ResolvePersistencePath(persistenceDirectory, projectRoot),
                    "conversations");
            }
            else
            {
                conversationsDirectory = ResolvePersistencePath(
                    conversationsDirectory,
                    projectRoot);
            }

            var statePath = Path.Combine(
                conversationsDirectory,
                conversationId,
                "base_state.json");
            var capture = await RunCapturedProcessAsync(
                "/bin/sh",
                [
                    "-c",
                    "state_file=$1" + BuildConversationStateReadSuffix(),
                    "openhands-state-inspection",
                    statePath,
                ],
                projectRoot,
                null,
                cancellationToken);
            if (capture.ExitCode != 0
                || Encoding.UTF8.GetByteCount(capture.StandardOutput)
                > MaximumConversationStateBytes)
            {
                return ConversationStateInspection.Unavailable();
            }

            return ParseConversationState(capture.StandardOutput);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or InvalidOperationException)
        {
            _logger.LogWarning(
                ex,
                "Could not inspect persisted OpenHands conversation {ConversationId}",
                conversationId);
            return ConversationStateInspection.Unavailable();
        }
    }

    private async Task<ConversationStateInspection> ReadRemoteConversationStateAsync(
        TargetMachine machine,
        string projectRoot,
        string conversationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var command = BuildRemoteConversationStateReadCommand(
                projectRoot,
                conversationId);
            var capture = await RunCapturedProcessAsync(
                "ssh",
                BuildSshArguments(machine, command),
                null,
                null,
                cancellationToken);
            if (capture.ExitCode != 0
                || Encoding.UTF8.GetByteCount(capture.StandardOutput)
                > MaximumConversationStateBytes)
            {
                return ConversationStateInspection.Unavailable();
            }

            return ParseConversationState(capture.StandardOutput);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or JsonException
                                   or InvalidOperationException)
        {
            _logger.LogWarning(
                ex,
                "Could not inspect remote OpenHands conversation {ConversationId}",
                conversationId);
            return ConversationStateInspection.Unavailable();
        }
    }

    private static ConversationStateInspection ParseConversationState(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ConversationStateInspection.Unavailable();
        }

        string? conversationId = null;
        if (root.TryGetProperty("id", out var persistedId)
            && persistedId.ValueKind == JsonValueKind.String
            && TryNormalizeConversationId(persistedId.GetString(), out var normalizedId))
        {
            conversationId = normalizedId;
        }

        string? executionStatus = null;
        if (root.TryGetProperty("execution_status", out var persistedStatus)
            && persistedStatus.ValueKind == JsonValueKind.String)
        {
            executionStatus = persistedStatus.GetString();
        }

        return new ConversationStateInspection(
            Readable: true,
            ConversationId: conversationId,
            ExecutionStatus: executionStatus);
    }

    private static string BuildConversationStateReadSuffix() =>
        "; if [ ! -f \"$state_file\" ] || [ -L \"$state_file\" ] || [ ! -r \"$state_file\" ]; then exit 74; fi"
        + "; state_size=$(LC_ALL=C wc -c < \"$state_file\" | tr -d '[:space:]')"
        + "; case \"$state_size\" in ''|*[!0-9]*) exit 74;; esac"
        + "; if [ \"$state_size\" -gt " + MaximumConversationStateBytes + " ]; then exit 75; fi"
        + "; head -c " + (MaximumConversationStateBytes + 1) + " < \"$state_file\"";

    private static string ResolvePersistencePath(string path, string? projectRoot) =>
        Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(projectRoot ?? Environment.CurrentDirectory, path));

    private static string? NormalizeOptionalConversationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!TryNormalizeConversationId(value, out var normalized))
        {
            throw new InvalidOperationException(
                "The saved OpenHands conversation ID for this queue tab is invalid. "
                + "Start a new queue tab before retrying.");
        }

        return normalized;
    }

    private static bool TryNormalizeConversationId(string? value, out string normalized)
    {
        normalized = "";
        if (!Guid.TryParse(value, out var conversationId))
        {
            return false;
        }

        normalized = conversationId.ToString("N");
        return true;
    }

    private static string AppendBounded(string existing, string value)
    {
        var buffer = new BoundedTextBuffer(MaximumCapturedCharacters);
        buffer.Append(existing);
        buffer.Append(value);
        return buffer.ToString();
    }

    private static string BuildSafeErrorEvent(string code, string message) =>
        JsonSerializer.Serialize(new
        {
            kind = "ConversationErrorEvent",
            code,
            message,
        })
        + Environment.NewLine;

    private static bool ContainsDiagnostic(string value, string expected) =>
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static ProcessStartInfo BuildStartInfo(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        bool replaceEnvironment = false)
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

        if (environment is not null)
        {
            if (replaceEnvironment)
            {
                startInfo.Environment.Clear();
            }

            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return startInfo;
    }

    private static async Task<ProcessCapture> RunCapturedProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        var startInfo = BuildStartInfo(
            fileName,
            arguments,
            workingDirectory,
            BuildDiagnosticEnvironment(),
            replaceEnvironment: true);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = ReadBoundedToEndAsync(
            process.StandardOutput,
            MaximumCapturedProcessCharacters,
            cancellationToken);
        var stderr = ReadBoundedToEndAsync(
            process.StandardError,
            MaximumCapturedProcessCharacters,
            cancellationToken);
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var standardOutput = await stdout;
        var standardError = await stderr;
        return new ProcessCapture(
            process.ExitCode,
            standardOutput + standardError,
            standardOutput);
    }

    private static async Task<string> ReadBoundedToEndAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder(Math.Min(maximumCharacters, 8_192));
        var buffer = new char[8_192];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                return output.ToString();
            }

            var remaining = maximumCharacters - output.Length;
            if (remaining > 0)
            {
                output.Append(buffer, 0, Math.Min(remaining, count));
            }
        }
    }

    public static IReadOnlyDictionary<string, string> BuildExecutionEnvironment(
        string model,
        string baseUrl,
        string apiKey,
        string tmuxDirectory,
        string? projectRoot = null)
    {
        var environment = new Dictionary<string, string>(
            BuildDiagnosticEnvironment(),
            StringComparer.Ordinal);

        environment["LLM_MODEL"] = model;
        environment["LLM_BASE_URL"] = baseUrl;
        environment["LLM_API_KEY"] = apiKey;
        environment["OPENHANDS_SUPPRESS_BANNER"] = "1";
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            environment["OPENHANDS_WORK_DIR"] = projectRoot;
        }
        environment["TMUX_TMPDIR"] = tmuxDirectory;
        return environment;
    }

    public static IReadOnlyDictionary<string, string> BuildDiagnosticEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in AllowedInheritedEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    public static string BuildRemoteEnvironmentSanitizer()
        => BuildRemoteEnvironmentSanitizer(includeRunnerVariables: true);

    public static string BuildRemoteDiagnosticEnvironmentSanitizer()
        => BuildRemoteEnvironmentSanitizer(includeRunnerVariables: false);

    private static string BuildRemoteEnvironmentSanitizer(bool includeRunnerVariables)
    {
        IEnumerable<string> allowedNames = AllowedInheritedEnvironmentVariables;
        if (includeRunnerVariables)
        {
            allowedNames = allowedNames.Concat(
            [
                "LLM_API_KEY",
                "LLM_BASE_URL",
                "LLM_MODEL",
                "OPENHANDS_SUPPRESS_BANNER",
                "OPENHANDS_WORK_DIR",
                "TMUX_TMPDIR",
            ]);
        }

        var allowedPattern = allowedNames
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);
        return "sanitize_openhands_environment() { "
            + "for environment_name in $(env | sed -n 's/^\\([A-Za-z_][A-Za-z0-9_]*\\)=.*/\\1/p'); do "
            + "case \"$environment_name\" in "
            + string.Join("|", allowedPattern)
            + ") ;; *) unset \"$environment_name\";; esac; "
            + "done; }; sanitize_openhands_environment";
    }

    private static string BuildPosixEnvironmentFile(
        string model,
        string baseUrl,
        string apiKey,
        string tmuxDirectory,
        string projectRoot) =>
        "LLM_MODEL=" + TargetCommandRunner.Quote(model) + "\n"
        + "LLM_BASE_URL=" + TargetCommandRunner.Quote(baseUrl) + "\n"
        + "LLM_API_KEY=" + TargetCommandRunner.Quote(apiKey) + "\n"
        + "OPENHANDS_SUPPRESS_BANNER='1'\n"
        + "OPENHANDS_WORK_DIR=" + TargetCommandRunner.Quote(projectRoot) + "\n"
        + "TMUX_TMPDIR=" + TargetCommandRunner.Quote(tmuxDirectory) + "\n";

    private static IReadOnlyList<string> BuildSshArguments(TargetMachine machine, string remoteCommand)
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
            "-o",
            "ConnectTimeout=15",
            "-o",
            "ServerAliveInterval=15",
            "-o",
            "ServerAliveCountMax=2",
            "-p",
            machine.Port.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(machine.SshKeyPath))
        {
            var keyPath = ResolveSshKeyPath(machine.SshKeyPath);
            if (!File.Exists(keyPath))
            {
                throw new InvalidOperationException(
                    "SSH key file is not accessible inside the API runtime: "
                    + keyPath
                    + ". Check the machine SSH key path and the Docker SSH mount.");
            }

            arguments.Add("-i");
            arguments.Add(keyPath);
        }

        arguments.Add(destination);
        arguments.Add(remoteCommand);
        return arguments;
    }

    private static string ResolveSshKeyPath(string configuredPath)
    {
        var trimmed = configuredPath.Trim();
        var fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(fileName) || File.Exists(trimmed))
        {
            return trimmed;
        }

        var userHome = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(userHome))
        {
            var homeCandidate = Path.Combine(userHome, ".ssh", fileName);
            if (File.Exists(homeCandidate))
            {
                return homeCandidate;
            }
        }

        var mountedCandidate = Path.Combine("/home/app/.ssh", fileName);
        return File.Exists(mountedCandidate) ? mountedCandidate : trimmed;
    }

    private async Task CleanupRemoteRunAsync(
        TargetMachine machine,
        string temporaryRoot,
        string taskPath,
        string pidPath,
        string tmuxDirectory)
    {
        var command = TargetCommandRunner.UnixRemotePathSetup
            + " " + RemoteProcessTreeFunctions
            + BuildRemoteDiagnosticEnvironmentSanitizer()
            + "; cleanup_status=0; task_file=" + TargetCommandRunner.Quote(taskPath) + "; "
            + "if [ -r " + TargetCommandRunner.Quote(pidPath) + " ]; then "
            + "child=$(cat " + TargetCommandRunner.Quote(pidPath) + " 2>/dev/null || true); "
            + "case \"$child\" in *[!0-9]*|'') "
            + "printf '%s\\n' 'OpenHands cleanup rejected an invalid PID file.' >&2; cleanup_status=75;; "
            + "*) child_args=$(ps -p \"$child\" -o args= 2>/dev/null || true); "
            + "case \"$child_args\" in '') ;; "
            + "*openhands*\"$task_file\"*|*\"$task_file\"*openhands*) "
            + "kill_openhands_tree \"$child\" || cleanup_status=75;; "
            + "*) printf '%s\\n' 'OpenHands cleanup did not find the expected process identity.' >&2; cleanup_status=75;; "
            + "esac;; esac; fi; "
            + "TMUX_TMPDIR=" + TargetCommandRunner.Quote(tmuxDirectory)
            + " tmux -L openhands kill-server >/dev/null 2>&1 || true; "
            + "rm -rf -- " + TargetCommandRunner.Quote(temporaryRoot)
            + " || cleanup_status=76; exit \"$cleanup_status\"";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var capture = await RunCapturedProcessAsync(
                "ssh",
                BuildSshArguments(machine, command),
                null,
                null,
                timeout.Token);
            if (capture.ExitCode != 0)
            {
                var output = StripAnsi(capture.Output).Trim();
                if (output.Length > 2_000)
                {
                    output = output[..2_000];
                }

                _logger.LogWarning(
                    "OpenHands remote cleanup returned exit code {ExitCode} for run directory {TemporaryRoot} on machine {MachineId}. {Output}",
                    capture.ExitCode,
                    temporaryRoot,
                    machine.Id,
                    output);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not confirm cleanup of OpenHands run directory {TemporaryRoot} on machine {MachineId}",
                temporaryRoot,
                machine.Id);
        }
    }

    private async Task StopTmuxServerAsync(string tmuxDirectory)
    {
        try
        {
            var environment = new Dictionary<string, string>(
                BuildDiagnosticEnvironment(),
                StringComparer.Ordinal)
            {
                ["TMUX_TMPDIR"] = tmuxDirectory,
            };
            var startInfo = BuildStartInfo(
                "tmux",
                ["-L", "openhands", "kill-server"],
                null,
                environment,
                replaceEnvironment: true);
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "OpenHands per-run tmux server was absent or could not be stopped in {TmuxDirectory}",
                tmuxDirectory);
        }
    }

    private static JsonObject BuildSafeMessageEvent(JsonObject source, string kind)
    {
        var safe = new JsonObject { ["kind"] = kind };
        CopySafeProperty(source, safe, "source");
        CopySafeProperty(source, safe, "timestamp");
        CopySafeProperty(source, safe, "role");
        CopyVisibleProperty(source, safe, "message");
        CopyVisibleProperty(source, safe, "content");

        if (source["llm_message"] is JsonObject llmMessage)
        {
            CopySafeProperty(llmMessage, safe, "role");
            CopyVisibleProperty(llmMessage, safe, "content");
            CopyVisibleProperty(llmMessage, safe, "message");
        }

        RemoveHiddenFields(safe);
        if (safe.Count == 1)
        {
            safe["message"] = "OpenHands emitted a user-visible message.";
        }

        return safe;
    }

    private static void CopySafeProperty(JsonObject source, JsonObject destination, string name)
    {
        if (source[name] is { } value)
        {
            destination[name] = value.DeepClone();
        }
    }

    private static void CopyVisibleProperty(JsonObject source, JsonObject destination, string name)
    {
        if (source[name] is { } value && FilterVisibleContent(value) is { } safeValue)
        {
            destination[name] = safeValue;
        }
    }

    private static JsonNode? FilterVisibleContent(JsonNode value)
    {
        if (value is JsonValue scalar)
        {
            return scalar.TryGetValue<string>(out var text)
                ? JsonValue.Create(text)
                : null;
        }

        if (value is JsonArray array)
        {
            var safeArray = new JsonArray();
            foreach (var child in array)
            {
                if (child is not null && FilterVisibleContent(child) is { } safeChild)
                {
                    safeArray.Add(safeChild);
                }
            }

            return safeArray.Count == 0 ? null : safeArray;
        }

        if (value is not JsonObject jsonObject)
        {
            return null;
        }

        var contentType = ReadString(jsonObject, "type")?.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(contentType)
            && contentType is not ("text" or "output_text" or "message" or "content" or "image" or "image_url"))
        {
            return null;
        }

        var safeObject = new JsonObject();
        foreach (var pair in jsonObject)
        {
            if (IsHiddenField(pair.Key) || pair.Value is null)
            {
                continue;
            }

            if (FilterVisibleContent(pair.Value) is { } safeValue)
            {
                safeObject[pair.Key] = safeValue;
            }
        }

        return safeObject.Count == 0 ? null : safeObject;
    }

    private static void RemoveHiddenFields(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var key in jsonObject.Select(pair => pair.Key).ToArray())
            {
                if (IsHiddenField(key))
                {
                    jsonObject.Remove(key);
                    continue;
                }

                if (jsonObject[key] is { } child)
                {
                    RemoveHiddenFields(child);
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var child in jsonArray)
            {
                if (child is not null)
                {
                    RemoveHiddenFields(child);
                }
            }
        }
    }

    private static bool IsHiddenField(string key) =>
        HiddenFieldFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase))
        || key.Equals("llm_message", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonObject value, string name) =>
        value[name] is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text : null;

    private static string? ReadConversationTerminalState(JsonObject value, string kind)
    {
        if (kind.Equals("ConversationErrorEvent", StringComparison.OrdinalIgnoreCase))
        {
            return "error";
        }

        if (!kind.Equals("ConversationStateUpdateEvent", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var state = ReadString(value, "execution_status")
            ?? ReadString(value, "state")
            ?? ReadString(value, "status");
        if (state is null && value["conversation_state"] is JsonObject conversationState)
        {
            state = ReadString(conversationState, "execution_status")
                ?? ReadString(conversationState, "state")
                ?? ReadString(conversationState, "status");
        }

        var key = ReadString(value, "key");
        if (state is null
            && string.Equals(key, "execution_status", StringComparison.OrdinalIgnoreCase))
        {
            state = ReadJsonString(value["value"]);
        }
        else if (state is null
                 && string.Equals(key, "full_state", StringComparison.OrdinalIgnoreCase)
                 && value["value"] is JsonObject fullState)
        {
            state = ReadString(fullState, "execution_status")
                ?? ReadString(fullState, "state")
                ?? ReadString(fullState, "status");
        }

        return state;
    }

    private static string? ReadJsonString(JsonNode? value) =>
        value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;

    private static void ValidateExecutionInput(
        TargetMachine machine,
        string projectPath,
        string model,
        string baseUrl,
        string apiKey)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("Project path is required.", nameof(projectPath));
        }

        if (machine.Kind == MachineKind.Local)
        {
            EnsureLocalProjectIsNotFileSystemRoot(Path.GetFullPath(projectPath));
        }
        else
        {
            ValidateRemotePath(projectPath);
        }

        if (projectPath.Trim().TrimEnd('/', '\\').Length == 0)
        {
            throw new ArgumentException(
                "OpenHands cannot use a filesystem root as the selected project path.",
                nameof(projectPath));
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("OpenHands model is required.", nameof(model));
        }
        if (model.Length > 256 || model.Any(char.IsControl))
        {
            throw new ArgumentException(
                "OpenHands model identifier must be 256 characters or fewer and contain no control characters.",
                nameof(model));
        }

        if (!model.StartsWith("openai/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Local Ollama models must use the OpenHands/LiteLLM identifier openai/<ollama-model-name>.",
                nameof(model));
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Local AI base URL must be an absolute HTTP(S) URL without credentials.", nameof(baseUrl));
        }

        if (!string.Equals(apiKey, AiProviderService.LocalPlaceholderApiKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "This OpenHands release supports only unauthenticated Local/Ollama profiles using the non-secret local-llm placeholder.");
        }
    }

    private static void ValidateRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Contains('\0')
            || path.Contains('\r')
            || path.Contains('\n')
            || !path.Trim().StartsWith("/", StringComparison.Ordinal)
            || path.Trim().TrimEnd('/').Length == 0
            || ResolvesToPosixFileSystemRoot(path))
        {
            throw new ArgumentException("Selected project path is invalid.", nameof(path));
        }
    }

    private static bool ResolvesToPosixFileSystemRoot(string path)
    {
        var normalized = path.Trim();
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var depth = 0;
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            depth++;
        }

        return depth == 0;
    }

    private static void EnsureLocalProjectIsNotFileSystemRoot(string projectPath)
    {
        var root = Path.GetPathRoot(projectPath);
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(
                projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                comparison))
        {
            throw new ArgumentException(
                "OpenHands cannot use a filesystem root as the selected project path.",
                nameof(projectPath));
        }
    }

    private static string ResolvePhysicalDirectoryPath(string path)
    {
        const int maximumSymbolicLinkResolutions = 64;
        var fullPath = Path.GetFullPath(path);
        var pendingSegments = new Queue<string>();
        EnqueuePathSegments(fullPath, pendingSegments);
        var currentRoot = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException(
                "The selected local project directory has no filesystem root.");
        var currentPath = currentRoot;
        var symbolicLinkResolutions = 0;

        while (pendingSegments.TryDequeue(out var segment))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!IsSymbolicLink(currentPath))
            {
                continue;
            }

            if (++symbolicLinkResolutions > maximumSymbolicLinkResolutions)
            {
                throw new InvalidOperationException(
                    "The selected local project directory contains too many symbolic links.");
            }

            var target = new DirectoryInfo(currentPath).ResolveLinkTarget(
                returnFinalTarget: false)
                ?? throw new InvalidOperationException(
                    "The selected local project directory contains an unreadable symbolic link.");
            var remainingSegments = pendingSegments.ToArray();
            pendingSegments.Clear();
            fullPath = Path.GetFullPath(target.FullName);
            currentRoot = Path.GetPathRoot(fullPath)
                ?? throw new InvalidOperationException(
                    "The selected local project directory symbolic link has no filesystem root.");
            currentPath = currentRoot;
            EnqueuePathSegments(fullPath, pendingSegments);
            foreach (var remainingSegment in remainingSegments)
            {
                pendingSegments.Enqueue(remainingSegment);
            }
        }

        return Path.GetFullPath(currentPath);
    }

    private static void EnqueuePathSegments(
        string fullPath,
        Queue<string> destination)
    {
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException(
                "The selected local project directory has no filesystem root.");
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath == ".")
        {
            return;
        }

        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            destination.Enqueue(segment);
        }
    }

    private static void EnsureChildPath(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("OpenHands temporary task path escaped the selected project root.");
        }
    }

    private static void EnsureNoSymbolicLinkDescendant(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        var current = parent;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) && IsSymbolicLink(current))
            {
                throw new InvalidOperationException(
                    "OpenHands temporary task path contains a symbolic link and was rejected.");
            }
        }
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void TrySetDirectoryMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static void TrySetFileMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private void DeleteNarrowDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: !IsSymbolicLink(path));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not remove generated OpenHands run directory {TemporaryRoot}",
                path);
        }
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
            // Best-effort process-tree cancellation; the caller records cancellation.
        }
    }

    private static string StripAnsi(string value) =>
        Regex.Replace(value, @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", "");

    private sealed record ProcessCapture(
        int ExitCode,
        string Output,
        string StandardOutput);

    private sealed record BoundedOutputLine(string Content, bool Truncated);

    private sealed record ConversationStateInspection(
        bool Readable,
        string? ConversationId,
        string? ExecutionStatus)
    {
        public static ConversationStateInspection Unavailable() =>
            new(
                Readable: false,
                ConversationId: null,
                ExecutionStatus: null);
    }

    private sealed class BoundedTextBuffer(int maximumCharacters)
    {
        private readonly StringBuilder _value = new();
        private readonly object _sync = new();

        public void Append(string value)
        {
            lock (_sync)
            {
                _value.Append(value);
                if (_value.Length > maximumCharacters)
                {
                    _value.Remove(0, _value.Length - maximumCharacters);
                }
            }
        }

        public override string ToString()
        {
            lock (_sync)
            {
                return _value.ToString();
            }
        }
    }
}

public sealed record OpenHandsSafeLine(
    string? Content,
    bool ReportedError,
    bool ReportedFinished = false);
