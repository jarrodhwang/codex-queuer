using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using CodexQueue.Api.Domain;

namespace CodexQueue.Api.Services;

public interface ITerminalSessionService
{
    Task<TerminalSessionHandle> StartAsync(Project project, CancellationToken cancellationToken);

    Task ProxyAsync(Guid sessionId, string sessionToken, HttpContext context);
}

public sealed record TerminalSessionHandle(string EntryPath);

public sealed class TerminalSessionService(ILogger<TerminalSessionService> logger, IHttpClientFactory httpClientFactory) : ITerminalSessionService
{
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromHours(8);
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient(nameof(TerminalSessionService));
    private readonly ConcurrentDictionary<Guid, TerminalSession> _sessionsByProject = new();
    private readonly ConcurrentDictionary<Guid, TerminalSession> _sessionsById = new();

    public async Task<TerminalSessionHandle> StartAsync(Project project, CancellationToken cancellationToken)
    {
        var machine = project.Machine ?? throw new InvalidOperationException("Project machine is missing.");
        var fingerprint = BuildFingerprint(project, machine);
        CleanupExpiredSessions();

        if (_sessionsByProject.TryGetValue(project.Id, out var existing)
            && existing.Fingerprint == fingerprint
            && IsProcessAlive(existing.Process))
        {
            existing.Touch();
            return new TerminalSessionHandle(existing.EntryPath);
        }

        if (existing is not null)
        {
            RemoveSession(existing);
        }

        var session = StartSession(project, machine, fingerprint);
        _sessionsByProject[project.Id] = session;
        _sessionsById[session.Id] = session;

        try
        {
            await WaitForReadyAsync(session, cancellationToken);
        }
        catch
        {
            RemoveSession(session);
            throw;
        }

        return new TerminalSessionHandle(session.EntryPath);
    }

    public async Task ProxyAsync(Guid sessionId, string sessionToken, HttpContext context)
    {
        if (!_sessionsById.TryGetValue(sessionId, out var session)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(session.Token),
                Encoding.UTF8.GetBytes(sessionToken)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!IsProcessAlive(session.Process))
        {
            RemoveSession(session);
            context.Response.StatusCode = StatusCodes.Status410Gone;
            await context.Response.WriteAsync("Terminal session closed.", context.RequestAborted);
            return;
        }

        session.Touch();
        if (context.WebSockets.IsWebSocketRequest)
        {
            await ProxyWebSocketAsync(session, context);
            return;
        }

        await ProxyHttpAsync(session, context);
    }

    private TerminalSession StartSession(Project project, TargetMachine machine, string fingerprint)
    {
        var sessionId = Guid.NewGuid();
        var token = CreateUrlToken();
        var entryPath = $"/api/terminal-sessions/{sessionId:N}/{token}/";
        var port = ReserveTcpPort();
        var startInfo = BuildTtydStartInfo(project, machine, entryPath, port);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start ttyd.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            logger.LogError(ex, "Failed to start ttyd for project {ProjectId}", project.Id);
            throw;
        }

        _ = DrainProcessStreamAsync(process.StandardOutput, sessionId);
        _ = DrainProcessStreamAsync(process.StandardError, sessionId);
        process.Exited += (_, _) => logger.LogInformation("ttyd session {SessionId} exited with code {ExitCode}", sessionId, SafeExitCode(process));

        return new TerminalSession(sessionId, project.Id, token, fingerprint, entryPath, port, process);
    }

    private ProcessStartInfo BuildTtydStartInfo(Project project, TargetMachine machine, string basePath, int port)
    {
        var command = BuildTerminalCommand(project, machine);
        var startInfo = new ProcessStartInfo
        {
            FileName = "ttyd",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in new[]
        {
            "--interface",
            "127.0.0.1",
            "--port",
            port.ToString(),
            "--writable",
            "--base-path",
            basePath,
            "--max-clients",
            "4",
            "--once"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (machine.Kind == MachineKind.Local && !machine.TargetsWindows() && !string.IsNullOrWhiteSpace(project.Path))
        {
            startInfo.ArgumentList.Add("--cwd");
            startInfo.ArgumentList.Add(project.Path);
        }

        startInfo.ArgumentList.Add(command.FileName);
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static TerminalCommand BuildTerminalCommand(Project project, TargetMachine machine)
    {
        if (machine.Kind == MachineKind.Local)
        {
            if (machine.TargetsWindows())
            {
                return new TerminalCommand(
                    "powershell",
                    new[] { "-NoLogo", "-NoExit", "-ExecutionPolicy", "Bypass", "-Command", TargetCommandRunner.BuildPowerShellSetLocationCommand(project.Path) });
            }

            return new TerminalCommand(Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash", new[] { "-l" });
        }

        if (string.IsNullOrWhiteSpace(machine.Host))
        {
            throw new InvalidOperationException("SSH machine host is required.");
        }

        var arguments = BuildSshArguments(machine);
        arguments.Add(machine.TargetsWindows()
            ? "powershell -NoLogo -NoExit -ExecutionPolicy Bypass -Command \"" + TargetCommandRunner.BuildPowerShellSetLocationCommand(project.Path) + "\""
            : BuildInteractiveUnixShellCommand(project.Path));
        return new TerminalCommand("ssh", arguments);
    }

    private static List<string> BuildSshArguments(TargetMachine machine)
    {
        var destination = string.IsNullOrWhiteSpace(machine.UserName)
            ? machine.Host!
            : machine.UserName + "@" + machine.Host;
        var arguments = new List<string>
        {
            "-tt",
            "-o",
            "StrictHostKeyChecking=accept-new",
            "-p",
            machine.Port.ToString()
        };

        if (!string.IsNullOrWhiteSpace(machine.SshKeyPath))
        {
            var keyPath = ResolveSshKeyPath(machine.SshKeyPath);
            if (!File.Exists(keyPath))
            {
                throw new InvalidOperationException("SSH key file is not accessible inside the API runtime: " + keyPath + ".");
            }

            arguments.Add("-i");
            arguments.Add(keyPath);
        }

        arguments.Add(destination);
        return arguments;
    }

    private async Task ProxyHttpAsync(TerminalSession session, HttpContext context)
    {
        using var request = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            BuildUpstreamUri(session, context));

        foreach (var header in context.Request.Headers)
        {
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                request.Content ??= new StreamContent(context.Request.Body);
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        if (context.Request.ContentLength > 0 && request.Content is null)
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        CopyHeaders(response.Headers, context.Response.Headers);
        CopyHeaders(response.Content.Headers, context.Response.Headers);
        context.Response.Headers.Remove("transfer-encoding");
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static async Task ProxyWebSocketAsync(TerminalSession session, HttpContext context)
    {
        using var upstream = new ClientWebSocket();
        if (context.Request.Headers.TryGetValue("Sec-WebSocket-Protocol", out var protocols))
        {
            foreach (var protocol in protocols.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                upstream.Options.AddSubProtocol(protocol);
            }
        }

        await upstream.ConnectAsync(BuildUpstreamWebSocketUri(session, context), context.RequestAborted);
        using var downstream = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
        await Task.WhenAny(
            PumpWebSocketAsync(downstream, upstream, context.RequestAborted),
            PumpWebSocketAsync(upstream, downstream, context.RequestAborted));
    }

    private static async Task PumpWebSocketAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (!cancellationToken.IsCancellationRequested
            && source.State == WebSocketState.Open
            && destination.State == WebSocketState.Open)
        {
            var result = await source.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await destination.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
                return;
            }

            await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken);
        }
    }

    private async Task WaitForReadyAsync(TerminalSession session, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsProcessAlive(session.Process))
            {
                throw new InvalidOperationException("ttyd exited before the terminal was ready.");
            }

            try
            {
                using var response = await _httpClient.GetAsync(BuildUpstreamUri(session, session.EntryPath, ""), cancellationToken);
                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        throw new TimeoutException("Timed out waiting for ttyd to start.");
    }

    private static Uri BuildUpstreamUri(TerminalSession session, HttpContext context) =>
        BuildUpstreamUri(session, context.Request.Path.ToString(), context.Request.QueryString.ToString());

    private static Uri BuildUpstreamWebSocketUri(TerminalSession session, HttpContext context)
    {
        var builder = new UriBuilder(BuildUpstreamUri(session, context))
        {
            Scheme = "ws"
        };
        return builder.Uri;
    }

    private static Uri BuildUpstreamUri(TerminalSession session, string path, string query) =>
        new("http://127.0.0.1:" + session.Port + path + query);

    private static void CopyHeaders(System.Net.Http.Headers.HttpHeaders source, IHeaderDictionary destination)
    {
        foreach (var header in source)
        {
            destination[header.Key] = header.Value.ToArray();
        }
    }

    private void CleanupExpiredSessions()
    {
        foreach (var session in _sessionsById.Values)
        {
            if (!IsProcessAlive(session.Process) || DateTimeOffset.UtcNow - session.LastAccessedAt > SessionIdleTimeout)
            {
                RemoveSession(session);
            }
        }
    }

    private void RemoveSession(TerminalSession session)
    {
        _sessionsByProject.TryRemove(session.ProjectId, out _);
        _sessionsById.TryRemove(session.Id, out _);
        try
        {
            if (!session.Process.HasExited)
            {
                session.Process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup for terminal processes.
        }
        finally
        {
            session.Process.Dispose();
        }
    }

    private async Task DrainProcessStreamAsync(StreamReader reader, Guid sessionId)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                logger.LogDebug("ttyd {SessionId}: {Line}", sessionId, line);
            }
        }
        catch (ObjectDisposedException)
        {
            // Process disposal closes the redirected streams.
        }
    }

    private static bool IsProcessAlive(Process process) => !process.HasExited;

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static int ReserveTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string CreateUrlToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string BuildFingerprint(Project project, TargetMachine machine) =>
        string.Join("|", project.Path, machine.Id, machine.Kind, machine.Host, machine.Port, machine.UserName, machine.SshKeyPath, machine.Platform);

    private static string BuildInteractiveUnixShellCommand(string projectPath) =>
        TargetCommandRunner.UnixRemotePathSetup + " cd " + QuoteShell(projectPath) + " && exec ${SHELL:-/bin/bash} -l";

    private static string QuoteShell(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

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

    private sealed record TerminalCommand(string FileName, IReadOnlyList<string> Arguments);

    private sealed class TerminalSession(
        Guid id,
        Guid projectId,
        string token,
        string fingerprint,
        string entryPath,
        int port,
        Process process)
    {
        public Guid Id { get; } = id;
        public Guid ProjectId { get; } = projectId;
        public string Token { get; } = token;
        public string Fingerprint { get; } = fingerprint;
        public string EntryPath { get; } = entryPath;
        public int Port { get; } = port;
        public Process Process { get; } = process;
        public DateTimeOffset LastAccessedAt { get; private set; } = DateTimeOffset.UtcNow;

        public void Touch() => LastAccessedAt = DateTimeOffset.UtcNow;
    }
}
