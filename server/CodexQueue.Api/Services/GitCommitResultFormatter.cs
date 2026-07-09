namespace CodexQueue.Api.Services;

internal static class GitCommitResultFormatter
{
    public static string Format(string sha, string? message)
    {
        var output = "Commit created:" + Environment.NewLine + sha.Trim();
        if (!string.IsNullOrWhiteSpace(message))
        {
            output += Environment.NewLine + "Message: " + GitCommitMessageHelper.Sanitize(message);
        }

        return output;
    }
}
