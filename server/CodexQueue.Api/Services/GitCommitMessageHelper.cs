using System.Text.Json;

namespace CodexQueue.Api.Services;

internal static class GitCommitMessageHelper
{
    private const int MaxDiffChars = 24_000;

    public static string BuildPrompt(string gitStatus, string diffStat, string diff, string? target = null)
    {
        var targetText = string.IsNullOrWhiteSpace(target) ? "repository" : target.Trim();
        var statusText = string.IsNullOrWhiteSpace(gitStatus) ? "No status output." : gitStatus.Trim();
        var diffStatText = string.IsNullOrWhiteSpace(diffStat) ? "No diff stat output." : diffStat.Trim();
        var diffText = string.IsNullOrWhiteSpace(diff) ? "No diff output." : TruncateDiff(diff.Trim());

        return $"""
        Inspect the current git changes for this {targetText} and write a commit message.

        Do not modify files.
        Do not stage files.
        Do not run git commit.
        If there are no changes, return exactly: No changes to commit.
        Return exactly one concise imperative commit message line.
        Do not include numbering, bullets, markdown, quotes, or commentary.

        git status --porcelain:
        ```
        {statusText}
        ```

        git diff --stat --no-ext-diff, including staged changes:
        ```
        {diffStatText}
        ```

        git diff --no-ext-diff, including staged changes:
        ```
        {diffText}
        ```
        """;
    }

    public static string BuildCommitPrompt(string? target = null)
    {
        var targetText = string.IsNullOrWhiteSpace(target) ? "repository" : target.Trim();
        return $"""
        Inspect the current git changes for this {targetText} and create exactly one git commit yourself.

        Run git status and git diff as needed.
        Stage only changes under the current project path. Prefer pathspec-limited commands such as `git add -A -- .`.
        Choose one concise imperative commit message.
        If there are no changes, do not create a commit and return exactly: No changes to commit.
        Do not amend existing commits.
        Do not push.
        After committing, report the commit SHA and commit message.
        """;
    }

    public static string? ExtractFromOutput(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var line = NormalizeEventLine(lines[index]);
            if (SkipLine(line))
            {
                continue;
            }

            if (TryReadCommitMessageFromJsonLine(line, out var jsonMessage) && jsonMessage is not null)
            {
                return Sanitize(jsonMessage);
            }
        }

        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var line = NormalizeEventLine(lines[index]);
            if (SkipLine(line) || LooksLikeJsonObject(line))
            {
                continue;
            }

            var labelPrefix = line.IndexOf(':', StringComparison.Ordinal);
            if (labelPrefix > 0 && line[..labelPrefix].Contains("message", StringComparison.OrdinalIgnoreCase))
            {
                return Sanitize(line[(labelPrefix + 1)..]);
            }
        }

        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var line = NormalizeEventLine(lines[index]);
            if (SkipLine(line) || LooksLikeJsonObject(line))
            {
                continue;
            }

            return Sanitize(line);
        }

        return null;
    }

    public static string Sanitize(string message)
    {
        var normalized = string.Join(" ", message.Replace('\r', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.StartsWith("\"", StringComparison.Ordinal) && normalized.EndsWith("\"", StringComparison.Ordinal) && normalized.Length > 1)
        {
            normalized = normalized.Trim('"');
        }

        if (normalized.StartsWith("`", StringComparison.Ordinal) && normalized.EndsWith("`", StringComparison.Ordinal) && normalized.Length > 1)
        {
            normalized = normalized.Trim('`');
        }

        var labelPrefix = normalized.IndexOf(':', StringComparison.Ordinal);
        if (labelPrefix > 0 && normalized[..labelPrefix].Contains("message", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[(labelPrefix + 1)..].Trim();
        }

        return normalized.Length <= 180 ? normalized : normalized[..180];
    }

    private static string TruncateDiff(string diff) =>
        diff.Length <= MaxDiffChars
            ? diff
            : diff[..MaxDiffChars] + Environment.NewLine + "[diff truncated]";

    private static bool SkipLine(string line) =>
        string.IsNullOrWhiteSpace(line)
        || line.StartsWith("$ ", StringComparison.Ordinal)
        || line.StartsWith("```", StringComparison.Ordinal)
        || CompletionTextCleaner.IsNoiseLine(line);

    private static string NormalizeEventLine(string line) =>
        line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? line["data:".Length..].Trim()
            : line;

    private static bool LooksLikeJsonObject(string line) =>
        line.TrimStart().StartsWith("{", StringComparison.Ordinal);

    private static bool TryReadCommitMessageFromJsonLine(string line, out string? message)
    {
        message = null;
        if (!LooksLikeJsonObject(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("commitMessage", out var commitMessage))
            {
                message = commitMessage.GetString();
                return !string.IsNullOrWhiteSpace(message);
            }

            if (root.ValueKind == JsonValueKind.Object && TryExtractCodexAssistantText(root, out var assistantText))
            {
                message = assistantText;
                return !string.IsNullOrWhiteSpace(message);
            }
        }
        catch (JsonException)
        {
            // Ignore non-JSON output from Codex.
        }

        return false;
    }

    private static bool TryExtractCodexAssistantText(JsonElement root, out string? text)
    {
        text = null;
        var item = root.TryGetProperty("item", out var itemElement) && itemElement.ValueKind == JsonValueKind.Object
            ? itemElement
            : root;

        var role = ReadJsonString(item, "role");
        var eventType = ReadJsonString(root, "type");
        var itemType = ReadJsonString(item, "type");
        var looksLikeCompletedMessage = IsCompletedEventType(eventType)
            && string.Equals(itemType, "message", StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) && !looksLikeCompletedMessage)
        {
            return false;
        }

        text = ReadJsonContentText(item)
            ?? ReadJsonString(item, "message")
            ?? ReadJsonString(item, "text")
            ?? ReadJsonString(root, "text");

        return !string.IsNullOrWhiteSpace(text);
    }

    private static string? ReadJsonContentText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = content.EnumerateArray()
            .Select(ReadJsonContentPartText)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .ToArray();

        return parts.Length > 0 ? string.Join(Environment.NewLine + Environment.NewLine, parts) : null;
    }

    private static string? ReadJsonContentPartText(JsonElement part)
    {
        if (part.ValueKind == JsonValueKind.String)
        {
            return part.GetString();
        }

        if (part.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadJsonString(part, "text") ?? ReadJsonString(part, "content") ?? ReadJsonString(part, "message");
    }

    private static string? ReadJsonString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool IsCompletedEventType(string? type) =>
        !string.IsNullOrWhiteSpace(type)
        && type.Split('.', '_', '-').Any(part => string.Equals(part, "completed", StringComparison.OrdinalIgnoreCase));
}
