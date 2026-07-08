namespace CodexQueue.Api.Services;

public sealed record CommandResult(int ExitCode, string Output, string CommandPreview, string? CodexSessionId = null)
{
    public bool Success => ExitCode == 0;
}
