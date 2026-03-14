namespace Rush;

/// <summary>
/// Parses I/O redirect operators from a command string.
/// Handles >, >>, <, 2>, 2>>, 2>&amp;1 with proper quote awareness.
/// </summary>
public static class RedirectionParser
{
    /// <summary>
    /// Parse redirect operators from a command string.
    /// Returns (cleanCommand, stdoutRedirect, stdinRedirect, stderrRedirect).
    /// </summary>
    public static (string command, RedirectInfo? redirect, StdinInfo? stdin, StderrInfo? stderr) Parse(string input)
    {
        var trimmed = input.TrimEnd();
        if (string.IsNullOrEmpty(trimmed)) return (input, null, null, null);

        bool inSQ = false, inDQ = false;

        // Phase 1: Scan for redirect operators (respecting quotes).
        // 2> and 2>> are recognised so their '>' isn't mistaken for stdout,
        // but they stay in the command string for PowerShell to handle natively.
        var ops = new List<(int pos, string op)>();

        for (int i = 0; i < trimmed.Length; i++)
        {
            char ch = trimmed[i];
            if (ch == '\'' && !inDQ) { inSQ = !inSQ; continue; }
            if (ch == '"' && !inSQ) { inDQ = !inDQ; continue; }
            if (inSQ || inDQ) continue;

            // 2>&1 — tracked (may need stripping when combined with stdout redirect)
            if (ch == '2' && i + 3 < trimmed.Length
                && trimmed[i + 1] == '>' && trimmed[i + 2] == '&' && trimmed[i + 3] == '1')
            { ops.Add((i, "2>&1")); i += 3; }
            // 2>> — stderr append redirect
            else if (ch == '2' && i + 2 < trimmed.Length
                     && trimmed[i + 1] == '>' && trimmed[i + 2] == '>')
            { ops.Add((i, "2>>")); i += 2; }
            // 2> — stderr redirect
            else if (ch == '2' && i + 1 < trimmed.Length && trimmed[i + 1] == '>')
            { ops.Add((i, "2>")); i += 1; }
            // >>
            else if (ch == '>' && i + 1 < trimmed.Length && trimmed[i + 1] == '>')
            { ops.Add((i, ">>")); i += 1; }
            // >
            else if (ch == '>')
            { ops.Add((i, ">")); }
            // <
            else if (ch == '<')
            { ops.Add((i, "<")); }
        }

        if (ops.Count == 0) return (input, null, null, null);

        // Phase 2: Process operators — parse file targets, decide what to strip.
        RedirectInfo? stdoutRedirect = null;
        StdinInfo? stdinRedirect = null;
        StderrInfo? stderrRedirect = null;
        bool hasMerge = false;
        var stripRanges = new List<(int start, int end)>(); // [start, end)

        foreach (var (pos, op) in ops)
        {
            int opEnd = pos + op.Length;

            if (op == "2>&1")
            {
                hasMerge = true;
                // Stripping decision deferred to Phase 3
                continue;
            }

            // For >, >>, <, 2>, 2>> — parse the target file path
            int j = opEnd;
            while (j < trimmed.Length && trimmed[j] == ' ') j++;
            if (j >= trimmed.Length) continue; // no target — leave as-is

            string filePath;
            if (trimmed[j] is '\'' or '"')
            {
                char q = trimmed[j];
                int pathStart = j + 1;
                j++;
                while (j < trimmed.Length && trimmed[j] != q) j++;
                filePath = trimmed[pathStart..j];
                if (j < trimmed.Length) j++; // skip closing quote
            }
            else
            {
                int pathStart = j;
                while (j < trimmed.Length
                       && trimmed[j] != ' ' && trimmed[j] != '\t'
                       && trimmed[j] != '|' && trimmed[j] != ';'
                       && trimmed[j] != '>' && trimmed[j] != '<')
                    j++;
                filePath = trimmed[pathStart..j];
            }

            if (string.IsNullOrEmpty(filePath)) continue;

            // Resolve ~ paths
            if (filePath == "~" || filePath.StartsWith("~/"))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                filePath = filePath == "~" ? home : Path.Combine(home, filePath[2..]);
            }

            stripRanges.Add((pos, j));

            if (op == "<")
                stdinRedirect = new StdinInfo(filePath);
            else if (op is "2>" or "2>>")
                stderrRedirect = new StderrInfo(filePath, op == "2>>");
            else
                stdoutRedirect = new RedirectInfo(filePath, op == ">>");
        }

        // Phase 3: If 2>&1 appears with a stdout redirect, merge stderr into the
        // captured output and strip 2>&1 from the command.  Without a stdout
        // redirect, leave 2>&1 in the command for PowerShell to handle inline.
        if (hasMerge && stdoutRedirect != null)
        {
            stdoutRedirect = stdoutRedirect with { MergeStderr = true };
            foreach (var (pos, op) in ops)
                if (op == "2>&1") stripRanges.Add((pos, pos + 4));
        }

        if (stripRanges.Count == 0)
            return (input, stdoutRedirect, stdinRedirect, null);

        // Phase 4: Build clean command by removing strip ranges.
        var sorted = stripRanges.OrderBy(r => r.start).ToList();
        var sb = new System.Text.StringBuilder();
        int cursor = 0;
        foreach (var (start, end) in sorted)
        {
            if (start > cursor) sb.Append(trimmed[cursor..start]);
            cursor = end;
        }
        if (cursor < trimmed.Length) sb.Append(trimmed[cursor..]);

        return (sb.ToString().Trim(), stdoutRedirect, stdinRedirect, stderrRedirect);
    }
}

/// <summary>Stdout/stderr redirect target.</summary>
public record RedirectInfo(string FilePath, bool Append, bool MergeStderr = false);

/// <summary>Stdin redirect source.</summary>
public record StdinInfo(string FilePath);

/// <summary>Stderr redirect target (2> or 2>>).</summary>
public record StderrInfo(string FilePath, bool Append);
