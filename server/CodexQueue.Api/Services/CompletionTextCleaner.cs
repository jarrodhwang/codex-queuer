using System.Text.RegularExpressions;

namespace CodexQueue.Api.Services;

public static partial class CompletionTextCleaner
{
    public static string? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lines = text.Split('\n');
        var usefulLines = lines
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !IsNoiseLine(line))
            .ToArray();

        var sanitized = string.Join(Environment.NewLine, usefulLines).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }

    public static bool IsNoiseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        return trimmed.StartsWith("$ ", StringComparison.Ordinal)
               || StandaloneHexIdentifierRegex().IsMatch(trimmed);
    }

    [GeneratedRegex("^[0-9a-fA-F]{32,64}$")]
    private static partial Regex StandaloneHexIdentifierRegex();
}
