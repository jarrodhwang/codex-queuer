using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexQueue.Api.Domain;

namespace CodexQueue.Api.Services;

public sealed record LocalCodexMachineCheck(
    bool Available,
    string? Version,
    string Message,
    bool TargetLocalAiChecked = false,
    bool? TargetLocalAiReachable = null,
    bool? TargetSelectedModelAvailable = null,
    string? TargetLocalAiMessage = null);

internal sealed record LocalCodexProviderOptions(
    LocalAiServerType ServerType,
    string BaseUrl,
    int ContextWindow);

internal sealed record LocalAiRouteCheck(
    bool Reachable,
    bool? SelectedModelAvailable,
    string Message);

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
        bool internetSearchEnabled,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken);

    Task<CommandResult> RunLocalCodexAsync(
        TargetMachine machine,
        string projectPath,
        LocalAiServerType serverType,
        string baseUrl,
        string model,
        int contextWindow,
        string? modelEffort,
        string? codexSessionId,
        string prompt,
        PermissionMode permissionMode,
        bool internetSearchEnabled,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken);

    Task<LocalCodexMachineCheck> TestLocalCodexAsync(
        TargetMachine machine,
        CancellationToken cancellationToken,
        string? localAiBaseUrl = null,
        string? selectedModel = null);

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
    private static readonly TimeSpan LocalAiProbeTimeout = TimeSpan.FromSeconds(15);
    private const int MaximumLocalAiProbeBytes = 1024 * 1024;
    private static readonly string[] AllowedLocalCodexEnvironmentVariables =
    [
        "APPDATA",
        "ASDF_DATA_DIR",
        "CARGO_HOME",
        "CODEX_HOME",
        "COMSPEC",
        "CUDA_HOME",
        "DOTNET_ROOT",
        "DOTNET_ROOT_X64",
        "GOPATH",
        "GOROOT",
        "GRADLE_HOME",
        "HOME",
        "HOMEDRIVE",
        "HOMEPATH",
        "JAVA_HOME",
        "LANG",
        "LC_ALL",
        "LC_CTYPE",
        "LOCALAPPDATA",
        "LOGNAME",
        "M2_HOME",
        "NPM_CONFIG_PREFIX",
        "NVM_BIN",
        "NVM_DIR",
        "PATH",
        "PATHEXT",
        "PNPM_HOME",
        "ProgramData",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "PSModulePath",
        "PYENV_ROOT",
        "REQUESTS_CA_BUNDLE",
        "ROCM_PATH",
        "SHELL",
        "SSL_CERT_DIR",
        "SSL_CERT_FILE",
        "SystemDrive",
        "SystemRoot",
        "TEMP",
        "TERM",
        "TMP",
        "TMPDIR",
        "TZ",
        "USER",
        "USERNAME",
        "USERPROFILE",
        "VIRTUAL_ENV",
        "VOLTA_HOME",
        "WINDIR",
        "XDG_CACHE_HOME",
        "XDG_CONFIG_HOME",
        "XDG_DATA_HOME",
        "XDG_STATE_HOME",
    ];
    private static readonly Regex CodexVersionPattern = new(
        @"\bcodex(?:-cli)?\s+(?<version>[0-9]+(?:\.[0-9]+){1,3}(?:[-+][0-9A-Za-z.-]+)?)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
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
        bool internetSearchEnabled,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken)
        => RunCodexCoreAsync(
            machine,
            projectPath,
            model,
            modelEffort,
            modelSpeed,
            codexSessionId,
            imagePaths,
            prompt,
            permissionMode,
            internetSearchEnabled,
            onOutput,
            localProvider: null,
            cancellationToken);

    public async Task<CommandResult> RunLocalCodexAsync(
        TargetMachine machine,
        string projectPath,
        LocalAiServerType serverType,
        string baseUrl,
        string model,
        int contextWindow,
        string? modelEffort,
        string? codexSessionId,
        string prompt,
        PermissionMode permissionMode,
        bool internetSearchEnabled,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(serverType))
        {
            throw new ArgumentOutOfRangeException(nameof(serverType), "Local AI server type is invalid.");
        }

        if (contextWindow <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contextWindow),
                "Local model context window must be greater than zero.");
        }

        if (!AiProviderService.TryNormalizeBaseUrl(
                AiProviderSource.Local,
                baseUrl,
                out var normalizedBaseUrl,
                out var baseUrlError))
        {
            throw new ArgumentException(baseUrlError, nameof(baseUrl));
        }

        var targetBaseUrl = ResolveTargetLocalAiBaseUrl(machine, normalizedBaseUrl);
        var localModel = AiProviderService.QualifyModel(AiProviderSource.Local, model);
        var routeCheck = await ProbeLocalAiFromTargetAsync(
            machine,
            targetBaseUrl,
            localModel,
            cancellationToken);
        if (!routeCheck.Reachable)
        {
            throw new InvalidOperationException(
                "Local Codex was not started because the selected machine cannot reach the Local AI server. "
                + routeCheck.Message);
        }

        if (routeCheck.SelectedModelAvailable == false)
        {
            throw new InvalidOperationException(
                "Local Codex was not started because the selected model is not installed on the Local AI server as seen from the selected machine.");
        }

        if (routeCheck.SelectedModelAvailable is null)
        {
            throw new InvalidOperationException(
                "Local Codex was not started because the selected machine could not verify the selected model. "
                + routeCheck.Message);
        }

        return await RunCodexCoreAsync(
            machine,
            projectPath,
            localModel,
            modelEffort,
            modelSpeed: null,
            codexSessionId,
            imagePaths: null,
            prompt,
            permissionMode,
            internetSearchEnabled,
            onOutput,
            new LocalCodexProviderOptions(serverType, targetBaseUrl, contextWindow),
            cancellationToken);
    }

    private Task<CommandResult> RunCodexCoreAsync(
        TargetMachine machine,
        string projectPath,
        string model,
        string? modelEffort,
        string? modelSpeed,
        string? codexSessionId,
        IReadOnlyList<string>? imagePaths,
        string prompt,
        PermissionMode permissionMode,
        bool internetSearchEnabled,
        Func<string, Task> onOutput,
        LocalCodexProviderOptions? localProvider,
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
                internetSearchEnabled,
                disableWindowsSandbox: false,
                localProvider);
            if (machine.TargetsWindows())
            {
                var command = BuildPowerShellCodexCommandSetup() + "; & $codexCommand " + string.Join(" ", arguments.Select(QuotePowerShellValue));
                return RunProcessAsync(
                    "powershell",
                    new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command },
                    projectPath,
                    BuildCodexPreview(model, modelEffort, modelSpeed, codexSessionId, internetSearchEnabled, localProvider),
                    onOutput,
                    cancellationToken,
                    firstProcessOutputTimeout: CodexFirstOutputTimeout,
                    standardInput: prompt,
                    environment: localProvider is null
                        ? null
                        : BuildSanitizedLocalCodexEnvironment());
            }

            return RunProcessAsync(
                "codex",
                arguments,
                projectPath,
                BuildCodexPreview(model, modelEffort, modelSpeed, codexSessionId, internetSearchEnabled, localProvider),
                onOutput,
                cancellationToken,
                firstProcessOutputTimeout: CodexFirstOutputTimeout,
                standardInput: prompt,
                environment: localProvider is null
                    ? null
                    : BuildSanitizedLocalCodexEnvironment());
        }

        if (machine.TargetsWindows())
        {
            var windowsCommand = BuildPowerShellSetLocationCommand(projectPath) + "; "
                + (localProvider is null
                    ? ""
                    : BuildPowerShellLocalCodexEnvironmentSanitizer() + "; ")
                + BuildPowerShellCodexCommandSetup() + "; & $codexCommand "
                + string.Join(" ", BuildCodexArguments(
                    projectPath,
                    model,
                    modelEffort,
                    modelSpeed,
                    codexSessionId,
                    imagePaths,
                    permissionMode,
                    internetSearchEnabled,
                    disableWindowsSandbox: true,
                    localProvider).Select(QuotePowerShellValue));

            return RunSshAsync(
                machine,
                BuildPowerShellRemoteCommand(windowsCommand),
                "ssh " + machine.Host + " " + BuildCodexPreview(model, modelEffort, modelSpeed, codexSessionId, internetSearchEnabled, localProvider),
                onOutput,
                cancellationToken,
                firstProcessOutputTimeout: CodexFirstOutputTimeout,
                standardInput: prompt);
        }

        var remoteCommandParts = new List<string>();
        remoteCommandParts.AddRange([
            UnixRemotePathSetup,
            "cd",
            Quote(projectPath),
            "&&",
        ]);
        if (localProvider is not null)
        {
            remoteCommandParts.Add("{");
            remoteCommandParts.Add(BuildUnixLocalCodexEnvironmentSanitizer());
        }

        remoteCommandParts.AddRange([
            "codex",
            string.Join(" ", BuildCodexArguments(
                projectPath,
                model,
                modelEffort,
                modelSpeed,
                codexSessionId,
                imagePaths,
                permissionMode,
                internetSearchEnabled,
                disableWindowsSandbox: false,
                localProvider).Select(Quote))
        ]);
        if (localProvider is not null)
        {
            remoteCommandParts.Add("; }");
        }

        var remoteCommand = string.Join(" ", remoteCommandParts);

        return RunSshAsync(
            machine,
            remoteCommand,
            "ssh " + machine.Host + " " + BuildCodexPreview(model, modelEffort, modelSpeed, codexSessionId, internetSearchEnabled, localProvider),
            onOutput,
            cancellationToken,
            firstProcessOutputTimeout: CodexFirstOutputTimeout,
            standardInput: prompt);
    }

    public async Task<LocalCodexMachineCheck> TestLocalCodexAsync(
        TargetMachine machine,
        CancellationToken cancellationToken,
        string? localAiBaseUrl = null,
        string? selectedModel = null)
    {
        CommandResult cliCheck;
        try
        {
            cliCheck = await TestMachineAsync(
                machine,
                static _ => Task.CompletedTask,
                cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or System.ComponentModel.Win32Exception
                                   or IOException
                                   or TimeoutException)
        {
            logger.LogWarning(ex, "Local Codex readiness check failed for machine {MachineId}", machine.Id);
            return new LocalCodexMachineCheck(
                false,
                null,
                "Codex CLI could not be checked on the selected machine.");
        }

        var versionMatch = CodexVersionPattern.Match(cliCheck.Output);
        var version = versionMatch.Success ? versionMatch.Groups["version"].Value : null;
        var available = cliCheck.Success;
        var message = available
            ? "Codex CLI is available on the selected machine."
            : "Codex CLI is missing or unavailable on the selected machine.";

        if (string.IsNullOrWhiteSpace(localAiBaseUrl))
        {
            return new LocalCodexMachineCheck(available, version, message);
        }

        LocalAiRouteCheck routeCheck;
        try
        {
            routeCheck = await ProbeLocalAiFromTargetAsync(
                machine,
                ResolveTargetLocalAiBaseUrl(machine, localAiBaseUrl),
                selectedModel,
                cancellationToken);
        }
        catch (ArgumentException ex)
        {
            routeCheck = new LocalAiRouteCheck(false, null, ex.Message);
        }

        return new LocalCodexMachineCheck(
            available,
            version,
            message,
            TargetLocalAiChecked: true,
            TargetLocalAiReachable: routeCheck.Reachable,
            TargetSelectedModelAvailable: routeCheck.SelectedModelAvailable,
            TargetLocalAiMessage: routeCheck.Message);
    }

    internal async Task<LocalAiRouteCheck> ProbeLocalAiFromTargetAsync(
        TargetMachine machine,
        string baseUrl,
        string? selectedModel,
        CancellationToken cancellationToken)
    {
        if (!AiProviderService.TryNormalizeBaseUrl(
                AiProviderSource.Local,
                baseUrl,
                out var normalizedBaseUrl,
                out var baseUrlError))
        {
            throw new ArgumentException(baseUrlError, nameof(baseUrl));
        }

        var modelsEndpoint = normalizedBaseUrl.TrimEnd('/') + "/models";
        CommandResult capture;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(LocalAiProbeTimeout);
            if (machine.TargetsWindows())
            {
                var command = BuildPowerShellLocalAiProbeCommand(modelsEndpoint);
                capture = machine.Kind == MachineKind.Local
                    ? await RunProcessAsync(
                        "powershell",
                        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command],
                        null,
                        "check Local AI /v1/models",
                        static _ => Task.CompletedTask,
                        timeout.Token,
                        executionTimeout: LocalAiProbeTimeout)
                    : await RunSshAsync(
                        machine,
                        BuildPowerShellRemoteCommand(command),
                        "ssh " + machine.Host + " check Local AI /v1/models",
                        static _ => Task.CompletedTask,
                        timeout.Token,
                        executionTimeout: LocalAiProbeTimeout);
            }
            else
            {
                var command = BuildUnixLocalAiProbeCommand(modelsEndpoint);
                capture = machine.Kind == MachineKind.Local
                    ? await RunProcessAsync(
                        "/bin/sh",
                        ["-c", command],
                        null,
                        "check Local AI /v1/models",
                        static _ => Task.CompletedTask,
                        timeout.Token,
                        executionTimeout: LocalAiProbeTimeout)
                    : await RunSshAsync(
                        machine,
                        command,
                        "ssh " + machine.Host + " check Local AI /v1/models",
                        static _ => Task.CompletedTask,
                        timeout.Token,
                        executionTimeout: LocalAiProbeTimeout);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LocalAiRouteCheck(
                false,
                null,
                "The Local AI server check timed out on the selected machine.");
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or System.ComponentModel.Win32Exception
                                   or IOException
                                   or TimeoutException)
        {
            logger.LogWarning(ex, "Target-side Local AI check failed for machine {MachineId}", machine.Id);
            return new LocalAiRouteCheck(
                false,
                null,
                "Codex Queue could not run the Local AI server check on the selected machine.");
        }

        if (!capture.Success)
        {
            var diagnostic = SafeProbeDiagnostic(capture.Output);
            return new LocalAiRouteCheck(
                false,
                null,
                "The selected machine could not reach the Local AI server /v1/models endpoint."
                + (string.IsNullOrWhiteSpace(diagnostic) ? "" : " " + diagnostic));
        }

        return ParseLocalAiProbeResponse(capture.Output, selectedModel);
    }

    internal static string ResolveTargetLocalAiBaseUrl(TargetMachine machine, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(machine);

        if (machine.Kind != MachineKind.Ssh
            || !string.Equals(
                machine.Host?.Trim(),
                "host.docker.internal",
                StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || !string.Equals(
                uri.Host,
                "host.docker.internal",
                StringComparison.OrdinalIgnoreCase))
        {
            return baseUrl;
        }

        return new UriBuilder(uri)
        {
            Host = "127.0.0.1",
        }.Uri.AbsoluteUri.TrimEnd('/');
    }

    internal static string BuildUnixLocalAiProbeCommand(string modelsEndpoint) =>
        UnixRemotePathSetup
        + " umask 077; ulimit -f 2048 >/dev/null 2>&1 || true"
        + "; probe_url=" + Quote(modelsEndpoint)
        + "; probe_file=$(mktemp \"${TMPDIR:-/tmp}/codex-queue-local-ai.XXXXXX\")"
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

    internal static string BuildPowerShellLocalAiProbeCommand(string modelsEndpoint) =>
        "$response = Invoke-WebRequest -UseBasicParsing -Method Get -Uri "
        + QuotePowerShellValue(modelsEndpoint)
        + " -TimeoutSec 10 -ErrorAction Stop"
        + "; $content = [string]$response.Content"
        + "; if ([Text.Encoding]::UTF8.GetByteCount($content) -gt "
        + MaximumLocalAiProbeBytes
        + ") { throw 'Local AI model catalog exceeded the 1 MiB response limit.' }"
        + "; [Console]::Out.Write($content)";

    internal static LocalAiRouteCheck ParseLocalAiProbeResponse(
        string output,
        string? selectedModel)
    {
        var jsonStart = output.IndexOf('{');
        var jsonEnd = output.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd < jsonStart)
        {
            return new LocalAiRouteCheck(
                true,
                null,
                "The selected machine reached the Local AI server, but /v1/models returned an unreadable model catalog.");
        }

        try
        {
            using var document = JsonDocument.Parse(output[jsonStart..(jsonEnd + 1)]);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return new LocalAiRouteCheck(
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
                return new LocalAiRouteCheck(
                    true,
                    null,
                    "The selected machine reached the Local AI server, but its model catalog exceeded the 1,000-model limit.");
            }

            if (string.IsNullOrWhiteSpace(selectedModel))
            {
                return new LocalAiRouteCheck(
                    true,
                    null,
                    "The selected machine reached the Local AI server and read its /v1/models catalog. "
                    + "Responses generation is checked when a task starts.");
            }

            var available = models.Any(model =>
                string.Equals(model, selectedModel.Trim(), StringComparison.Ordinal));
            return new LocalAiRouteCheck(
                true,
                available,
                available
                    ? "The selected machine can reach /v1/models and the selected model is available. "
                      + "Responses generation is checked when a task starts."
                    : "The selected machine can reach /v1/models, but the selected model is not installed.");
        }
        catch (JsonException)
        {
            return new LocalAiRouteCheck(
                true,
                null,
                "The selected machine reached the Local AI server, but /v1/models did not return valid JSON.");
        }
    }

    private static string SafeProbeDiagnostic(string value)
    {
        var diagnostic = StripCommandPreview(value)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return diagnostic.Length <= 500 ? diagnostic : diagnostic[..500];
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
        TimeSpan? executionTimeout = null,
        IReadOnlyDictionary<string, string>? environment = null)
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
            startInfo.Environment.Clear();
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
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

    internal static IReadOnlyDictionary<string, string> BuildSanitizedLocalCodexEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in AllowedLocalCodexEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    internal static string BuildUnixLocalCodexEnvironmentSanitizer()
    {
        var allowedNames = AllowedLocalCodexEnvironmentVariables
            .Where(IsPosixEnvironmentVariableName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);
        // exec replaces the SSH login shell, so the Codex process cannot inspect
        // a still-running parent shell that retained excluded environment values.
        return "set -- env -i; for environment_name in "
            + string.Join(" ", allowedNames)
            + "; do eval \"environment_value=\\${$environment_name-}\""
            + "; if [ -n \"$environment_value\" ]; then"
            + " set -- \"$@\" \"$environment_name=$environment_value\"; fi"
            + "; done; exec \"$@\"";
    }

    internal static string BuildPowerShellLocalCodexEnvironmentSanitizer()
    {
        var allowedNames = AllowedLocalCodexEnvironmentVariables
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(QuotePowerShellValue);
        return "$allowedLocalCodexEnvironment = @("
            + string.Join(",", allowedNames)
            + "); Get-ChildItem Env: | Where-Object { $_.Name -notin $allowedLocalCodexEnvironment } "
            + "| Remove-Item -ErrorAction SilentlyContinue";
    }

    private static bool IsPosixEnvironmentVariableName(string name) =>
        name.Length > 0
        && (name[0] == '_' || char.IsAsciiLetter(name[0]))
        && name.Skip(1).All(character =>
            character == '_'
            || char.IsAsciiLetterOrDigit(character));

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

    private static IEnumerable<string> BuildModelConfigArguments(
        string? modelEffort,
        string? modelSpeed,
        LocalCodexProviderOptions? localProvider)
    {
        var arguments = new List<string>();
        if (localProvider is not null)
        {
            arguments.Add("-c");
            arguments.Add("model_provider=\"codex_queue_local\"");
            arguments.Add("-c");
            arguments.Add(
                "model_providers.codex_queue_local.name="
                + ToTomlString(LocalServerDisplayName(localProvider.ServerType)));
            arguments.Add("-c");
            arguments.Add(
                "model_providers.codex_queue_local.base_url="
                + ToTomlString(localProvider.BaseUrl));
            arguments.Add("-c");
            arguments.Add("model_providers.codex_queue_local.wire_api=\"responses\"");
            arguments.Add("-c");
            arguments.Add("model_providers.codex_queue_local.requires_openai_auth=false");
            arguments.Add("-c");
            arguments.Add("model_context_window=" + localProvider.ContextWindow);
        }

        if (!string.IsNullOrWhiteSpace(modelEffort))
        {
            arguments.Add("-c");
            arguments.Add("model_reasoning_effort=" + ToTomlString(modelEffort));
        }

        if (string.Equals(modelSpeed, "priority", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("-c");
            arguments.Add("service_tier=\"priority\"");
        }

        return arguments;
    }

    internal static IReadOnlyList<string> BuildCodexArguments(
        string projectPath,
        string model,
        string? modelEffort,
        string? modelSpeed,
        string? codexSessionId,
        IReadOnlyList<string>? imagePaths,
        PermissionMode permissionMode,
        bool internetSearchEnabled,
        bool disableWindowsSandbox,
        LocalCodexProviderOptions? localProvider = null)
    {
        var arguments = new List<string>();
        if (internetSearchEnabled)
        {
            // `--search` is a Codex-wide flag. Placing it after `exec` makes
            // current CLI releases reject it as an unexpected exec argument.
            arguments.Add("--search");
        }
        arguments.Add("exec");

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

        arguments.AddRange(BuildModelConfigArguments(modelEffort, modelSpeed, localProvider));
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

    private static string BuildCodexPreview(
        string model,
        string? modelEffort,
        string? modelSpeed,
        string? codexSessionId,
        bool internetSearchEnabled,
        LocalCodexProviderOptions? localProvider)
    {
        var parts = new List<string>
        {
            string.IsNullOrWhiteSpace(codexSessionId)
                ? "codex " + (internetSearchEnabled ? "--search " : "") + "exec -m " + model
                : "codex " + (internetSearchEnabled ? "--search " : "") + "exec resume -m " + model
        };
        if (localProvider is not null)
        {
            parts.Add(
                "["
                + LocalServerDisplayName(localProvider.ServerType)
                + " @ "
                + localProvider.BaseUrl
                + ", context "
                + localProvider.ContextWindow
                + "]");
        }

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

    private static string ToTomlString(string value) => JsonSerializer.Serialize(value);

    private static string LocalServerDisplayName(LocalAiServerType serverType) =>
        serverType switch
        {
            LocalAiServerType.Ollama => "Ollama",
            LocalAiServerType.LmStudio => "LM Studio",
            LocalAiServerType.LlamaCpp => "llama.cpp",
            _ => throw new ArgumentOutOfRangeException(nameof(serverType)),
        };
}
