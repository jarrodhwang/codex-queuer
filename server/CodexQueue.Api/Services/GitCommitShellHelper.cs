using CodexQueue.Api.Domain;

namespace CodexQueue.Api.Services;

internal static class GitCommitShellHelper
{
    public static string BuildCommitCommand(TargetMachine machine, string message) =>
        machine.TargetsWindows() ? BuildWindowsCommitCommand(message) : BuildUnixCommitCommand(message);

    private static string BuildUnixCommitCommand(string message)
    {
        var quotedMessage = TargetCommandRunner.Quote(message);
        return "before_head=$(git rev-parse HEAD 2>/dev/null || true); "
            + "before=$(git status --porcelain -- .); "
            + "if [ -z \"$before\" ]; then printf 'No changes to commit.\\n'; exit 0; fi; "
            + "printf 'Changed files before commit:\\n%s\\n' \"$before\"; "
            + "git add -A -- .; "
            + "diff_exit=0; git diff --cached --quiet -- . || diff_exit=$?; "
            + "if [ \"$diff_exit\" -eq 0 ]; then printf 'No changes staged after git add.\\n'; exit 0; fi; "
            + "if [ \"$diff_exit\" -ne 1 ]; then exit \"$diff_exit\"; fi; "
            + "git commit -m " + quotedMessage + " -- .; "
            + "commit_exit=$?; if [ \"$commit_exit\" -ne 0 ]; then exit \"$commit_exit\"; fi; "
            + "after_head=$(git rev-parse HEAD); "
            + "if [ \"$before_head\" = \"$after_head\" ]; then printf 'No changes were committed; HEAD did not change.\\n'; exit 12; fi; "
            + "printf '\\nCommit created:\\n'; echo \"$after_head\"";
    }

    private static string BuildWindowsCommitCommand(string message)
    {
        var quotedMessage = TargetCommandRunner.QuotePowerShellValue(message);
        return "$beforeHead = git rev-parse HEAD 2>$null; "
            + "$before = git status --porcelain -- .; "
            + "if (-not $before) { Write-Output 'No changes to commit.'; exit 0 }; "
            + "Write-Output 'Changed files before commit:'; $before; "
            + "git add -A -- .; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; "
            + "git diff --cached --quiet -- .; $diffExit = $LASTEXITCODE; "
            + "if ($diffExit -eq 0) { Write-Output 'No changes staged after git add.'; exit 0 }; "
            + "if ($diffExit -ne 1) { exit $diffExit }; "
            + "git commit -m " + quotedMessage + " -- .; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; "
            + "$afterHead = git rev-parse HEAD; "
            + "if ($afterHead -eq $beforeHead) { Write-Output 'No changes were committed; HEAD did not change.'; exit 12 }; "
            + "Write-Output ''; Write-Output 'Commit created:'; Write-Output $afterHead";
    }
}
