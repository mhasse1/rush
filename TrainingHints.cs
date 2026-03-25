using Rush;
using System.Text.RegularExpressions;

/// <summary>
/// Detects bash/Unix command patterns and suggests Rush-native alternatives.
/// Shows hints after successful command execution in the REPL.
/// Gated by config.ShowHints, with a frequency cap per pattern.
/// </summary>
static class TrainingHints
{
    // Track how many times each hint pattern has been shown
    private static readonly Dictionary<string, int> _impressions = new();
    private const int MaxImpressions = 3;

    /// <summary>
    /// Check the command for bash patterns and show a hint if appropriate.
    /// Call after command execution, before the next prompt.
    /// </summary>
    internal static void TryShowHint(string command, bool failed, RushConfig config)
    {
        if (!config.ShowHints || failed) return;

        var hint = MatchPattern(command);
        if (hint == null) return;

        // Frequency cap — don't nag
        if (_impressions.TryGetValue(hint.PatternKey, out var count) && count >= MaxImpressions)
            return;

        _impressions[hint.PatternKey] = (count + 1);

        // Render as dim hint with blank line separator
        Console.WriteLine();
        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine($"  ~ Rush: {hint.Suggestion}");
        Console.ResetColor();
    }

    private record Hint(string PatternKey, string Suggestion);

    private static Hint? MatchPattern(string cmd)
    {
        cmd = cmd.Trim();

        // ── find patterns ───────────────────────────────────────────
        // find . -name "*.ext"
        var findName = Regex.Match(cmd, @"^find\s+(\S+)\s+.*-i?name\s+[""']?\*\.(\w+)[""']?", RegexOptions.IgnoreCase);
        if (findName.Success)
        {
            var dir = findName.Groups[1].Value;
            var ext = findName.Groups[2].Value;
            var iFlag = cmd.Contains("-iname") ? "i" : "";
            return new Hint("find-name", $"Dir.list(\"{dir}\", :recurse) | where /\\.{ext}$/{iFlag}");
        }

        // find . -type f
        if (Regex.IsMatch(cmd, @"^find\s+(\S+)\s+.*-type\s+f\b"))
        {
            var dir = Regex.Match(cmd, @"^find\s+(\S+)").Groups[1].Value;
            return new Hint("find-type-f", $"Dir.list(\"{dir}\", :recurse, :files)");
        }

        // find . -type d
        if (Regex.IsMatch(cmd, @"^find\s+(\S+)\s+.*-type\s+d\b"))
        {
            var dir = Regex.Match(cmd, @"^find\s+(\S+)").Groups[1].Value;
            return new Hint("find-type-d", $"Dir.list(\"{dir}\", :recurse, :dirs)");
        }

        // ── cat | grep ─────────────────────────────────────────────
        var catGrep = Regex.Match(cmd, @"^cat\s+(\S+)\s*\|\s*grep\s+(.+)$");
        if (catGrep.Success)
        {
            var file = catGrep.Groups[1].Value;
            var pat = catGrep.Groups[2].Value.Trim().Trim('\"', '\'');
            return new Hint("cat-grep", $"File.read_lines(\"{file}\") | where /{pat}/");
        }

        // ── cat | wc -l ────────────────────────────────────────────
        var catWc = Regex.Match(cmd, @"^cat\s+(\S+)\s*\|\s*wc\s+-l\b");
        if (catWc.Success)
        {
            var file = catWc.Groups[1].Value;
            return new Hint("cat-wc", $"File.read_lines(\"{file}\").count");
        }

        // ── wc -l < file ───────────────────────────────────────────
        var wcRedirect = Regex.Match(cmd, @"^wc\s+-l\s*<\s*(\S+)");
        if (wcRedirect.Success)
        {
            var file = wcRedirect.Groups[1].Value;
            return new Hint("wc-redirect", $"File.read_lines(\"{file}\").count");
        }

        // ── test -f / [ -f path ] ──────────────────────────────────
        var testFile = Regex.Match(cmd, @"^(?:test\s+-f|(?:\[|\[\[)\s+-f)\s+(\S+)");
        if (testFile.Success)
        {
            var path = testFile.Groups[1].Value.Trim(']', ' ');
            return new Hint("test-f", $"File.exist?(\"{path}\")");
        }

        // ── test -d / [ -d path ] ──────────────────────────────────
        var testDir = Regex.Match(cmd, @"^(?:test\s+-d|(?:\[|\[\[)\s+-d)\s+(\S+)");
        if (testDir.Success)
        {
            var path = testDir.Groups[1].Value.Trim(']', ' ');
            return new Hint("test-d", $"Dir.exist?(\"{path}\")");
        }

        // ── sort | uniq ────────────────────────────────────────────
        if (Regex.IsMatch(cmd, @"\|\s*sort\s*\|\s*uniq\b"))
            return new Hint("sort-uniq", "| distinct  (works on unsorted data too)");

        // ── grep -c ────────────────────────────────────────────────
        var grepCount = Regex.Match(cmd, @"\bgrep\s+-c\s+[""']?(\S+?)[""']?\s");
        if (grepCount.Success)
        {
            var pat = grepCount.Groups[1].Value;
            return new Hint("grep-c", $"| where /{pat}/ | count");
        }

        // ── grep -r pattern dir ────────────────────────────────────
        var grepR = Regex.Match(cmd, @"^grep\s+-r\w*\s+[""']?(\S+?)[""']?\s+(\S+)");
        if (grepR.Success)
        {
            var pat = grepR.Groups[1].Value;
            var dir = grepR.Groups[2].Value;
            return new Hint("grep-r", $"Dir.list(\"{dir}\", :recurse) | where /{pat}/");
        }

        // ── head -N / tail -N (standalone, not after pipe) ─────────
        var headN = Regex.Match(cmd, @"^head\s+-(\d+)\s+(\S+)");
        if (headN.Success)
        {
            var n = headN.Groups[1].Value;
            var file = headN.Groups[2].Value;
            return new Hint("head-file", $"File.read_lines(\"{file}\").first({n})");
        }

        var tailN = Regex.Match(cmd, @"^tail\s+-(\d+)\s+(\S+)");
        if (tailN.Success)
        {
            var n = tailN.Groups[1].Value;
            var file = tailN.Groups[2].Value;
            return new Hint("tail-file", $"File.read_lines(\"{file}\").last({n})");
        }

        // ── awk '{print $N}' ───────────────────────────────────────
        var awkPrint = Regex.Match(cmd, @"\bawk\s+[""']\{?\s*print\s+\$(\d+)\s*\}?[""']");
        if (awkPrint.Success)
        {
            var col = awkPrint.Groups[1].Value;
            return new Hint("awk-print", $"| columns {col}");
        }

        // ── cat file (bare, no pipe) ───────────────────────────────
        var bareCat = Regex.Match(cmd, @"^cat\s+(\S+)$");
        if (bareCat.Success)
        {
            var file = bareCat.Groups[1].Value;
            return new Hint("cat-bare", $"File.read(\"{file}\")");
        }

        return null;
    }
}
