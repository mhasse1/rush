namespace Rush;

/// <summary>
/// Unix-like `cat` builtin command. Bypasses PowerShell pipeline for direct
/// .NET file I/O — supports concatenation, stdin reading, line numbering,
/// and output redirection.
///
/// When cat is piped (cat file | grep), it falls through to native /bin/cat
/// so pipeline semantics work naturally.
/// </summary>
public static class CatCommand
{
    /// <summary>
    /// Execute the cat builtin.
    /// </summary>
    /// <param name="argsStr">Arguments after "cat " (flags + file paths)</param>
    /// <param name="redirect">Stdout redirection info (> or >>), or null</param>
    /// <param name="stdinContent">Content from stdin redirection (&lt; file), or null</param>
    /// <returns>true on success, false on any error</returns>
    public static bool Execute(string argsStr, RedirectInfo? redirect, string? stdinContent,
        CancellationToken token = default)
    {
        var (numberLines, paths) = ParseArgs(argsStr);

        TextWriter output;
        StreamWriter? fileWriter = null;
        try
        {
            // Set up output destination
            if (redirect != null)
            {
                var fullPath = Path.GetFullPath(redirect.FilePath);
                fileWriter = redirect.Append
                    ? File.AppendText(fullPath)
                    : File.CreateText(fullPath);
                output = fileWriter;
            }
            else
            {
                output = Console.Out;
            }

            int lineNum = 0;
            bool anyError = false;

            if (paths.Count == 0 && stdinContent == null)
            {
                // No files, no stdin redirect → read from console stdin
                ReadStdin(output, numberLines, ref lineNum, token);
            }
            else
            {
                // Process stdin content first (if piped via < file)
                if (stdinContent != null)
                {
                    WriteContent(output, stdinContent, numberLines, ref lineNum);
                }

                // Then concatenate file arguments
                foreach (var path in paths)
                {
                    if (path == "-")
                    {
                        // Explicit stdin marker
                        if (stdinContent != null)
                            WriteContent(output, stdinContent, numberLines, ref lineNum);
                        else
                            ReadStdin(output, numberLines, ref lineNum, token);
                        continue;
                    }

                    var expanded = ExpandPath(path);
                    if (!File.Exists(expanded))
                    {
                        Console.Error.WriteLine($"cat: {path}: No such file or directory");
                        anyError = true;
                        continue;
                    }

                    try
                    {
                        using var reader = new StreamReader(expanded);
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (numberLines)
                            {
                                lineNum++;
                                output.WriteLine($"{lineNum,6}\t{line}");
                            }
                            else
                            {
                                output.WriteLine(line);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"cat: {path}: {ex.Message}");
                        anyError = true;
                    }
                }
            }

            output.Flush();
            return !anyError;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cat: {ex.Message}");
            return false;
        }
        finally
        {
            fileWriter?.Dispose();
        }
    }

    private static void WriteContent(TextWriter output, string content, bool numberLines, ref int lineNum)
    {
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (numberLines)
            {
                lineNum++;
                output.WriteLine($"{lineNum,6}\t{line}");
            }
            else
            {
                output.WriteLine(line);
            }
        }
    }

    private static void ReadStdin(TextWriter output, bool numberLines, ref int lineNum,
        CancellationToken token)
    {
        // Read stdin character-by-character via KeyAvailable/ReadKey.
        // This avoids orphaned Console.In.ReadLine() threads that compete
        // with LineEditor for stdin after Ctrl+C cancellation.
        string? line;
        while ((line = ReadLineInterruptible(token)) != null)
        {
            if (numberLines)
            {
                lineNum++;
                output.WriteLine($"{lineNum,6}\t{line}");
            }
            else
            {
                output.WriteLine(line);
            }
        }
    }

    /// <summary>
    /// Read a line from console stdin, interruptible by CancellationToken.
    /// Returns null on EOF (Ctrl+D) or cancellation (Ctrl+C).
    /// </summary>
    internal static string? ReadLineInterruptible(CancellationToken token)
    {
        var sb = new System.Text.StringBuilder();
        while (!token.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(20);
                continue;
            }
            var key = Console.ReadKey(intercept: true);

            // Ctrl+D = EOF
            if (key.Key == ConsoleKey.D && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                if (sb.Length == 0)
                {
                    Console.WriteLine();
                    return null;
                }
                // Ctrl+D with content: flush current line (like bash)
                Console.WriteLine();
                return sb.ToString();
            }

            // Enter = complete line
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return sb.ToString();
            }

            // Backspace
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Remove(sb.Length - 1, 1);
                    Console.Write("\b \b");
                }
                continue;
            }

            // Regular character
            if (key.KeyChar != '\0')
            {
                sb.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
        return null; // Cancelled
    }

    private static (bool numberLines, List<string> paths) ParseArgs(string argsStr)
    {
        if (string.IsNullOrWhiteSpace(argsStr))
            return (false, new List<string>());

        var parts = CommandTranslator.SplitCommandLine(argsStr);
        bool numberLines = false;
        var paths = new List<string>();

        foreach (var part in parts)
        {
            if (part.StartsWith('-') && part.Length > 1 && !part.StartsWith("--"))
            {
                // Parse combined flags like -n or -nb
                foreach (var ch in part[1..])
                {
                    if (ch == 'n') numberLines = true;
                    // Silently ignore unknown flags for compatibility
                }
            }
            else if (part == "--number")
            {
                numberLines = true;
            }
            else
            {
                // Strip surrounding quotes if present
                var clean = part;
                if ((clean.StartsWith('\'') && clean.EndsWith('\'')) ||
                    (clean.StartsWith('"') && clean.EndsWith('"')))
                    clean = clean[1..^1];
                paths.Add(clean);
            }
        }

        return (numberLines, paths);
    }

    private static string ExpandPath(string path)
    {
        if (path.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path[1..].TrimStart('/', '\\'));
        }
        return Path.GetFullPath(path);
    }
}
