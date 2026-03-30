using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Rush;

// ── JSON Data Objects ────────────────────────────────────────────────

public class LlmContext
{
    [JsonPropertyName("ready")] public bool Ready { get; set; } = true;
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("cwd")] public string Cwd { get; set; } = "";
    [JsonPropertyName("git_branch")] public string? GitBranch { get; set; }
    [JsonPropertyName("git_dirty")] public bool GitDirty { get; set; }
    [JsonPropertyName("last_exit_code")] public int LastExitCode { get; set; }
    [JsonPropertyName("shell")] public string Shell { get; set; } = "rush";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

public class LlmResult
{
    [JsonPropertyName("status")] public string Status { get; set; } = "success";
    [JsonPropertyName("exit_code")] public int ExitCode { get; set; }
    [JsonPropertyName("cwd")] public string Cwd { get; set; } = "";
    [JsonPropertyName("stdout")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public object? Stdout { get; set; }
    [JsonPropertyName("stdout_type")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StdoutType { get; set; }
    [JsonPropertyName("stderr")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Stderr { get; set; }
    [JsonPropertyName("duration_ms")] public long DurationMs { get; set; }

    // For syntax errors
    [JsonPropertyName("errors")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Errors { get; set; }

    // For output_limit
    [JsonPropertyName("stdout_lines")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int StdoutLines { get; set; }
    [JsonPropertyName("stdout_bytes")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int StdoutBytes { get; set; }
    [JsonPropertyName("preview")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Preview { get; set; }
    [JsonPropertyName("preview_bytes")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int PreviewBytes { get; set; }
    [JsonPropertyName("hint")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Hint { get; set; }

    // For TTY errors
    [JsonPropertyName("error_type")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ErrorType { get; set; }
    [JsonPropertyName("command")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Command { get; set; }

    // For lcat
    [JsonPropertyName("file")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? File { get; set; }
    [JsonPropertyName("mime")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Mime { get; set; }
    [JsonPropertyName("size_bytes")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long SizeBytes { get; set; }
    [JsonPropertyName("encoding")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Encoding { get; set; }
    [JsonPropertyName("content")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Content { get; set; }
    [JsonPropertyName("lines")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Lines { get; set; }

    // For spool
    [JsonPropertyName("spool_position")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int SpoolPosition { get; set; }
    [JsonPropertyName("spool_total")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int SpoolTotal { get; set; }
}

// ── Output Spool ─────────────────────────────────────────────────────

public class LlmSpool
{
    private List<string> _lines = new();
    private int _totalBytes;

    public int TotalLines => _lines.Count;
    public int TotalBytes => _totalBytes;
    public bool HasData => _lines.Count > 0;

    public void Store(string output)
    {
        _lines = output.Split('\n').ToList();
        _totalBytes = System.Text.Encoding.UTF8.GetByteCount(output);
    }

    public void Clear()
    {
        _lines.Clear();
        _totalBytes = 0;
    }

    public (string content, int newPosition) Read(int offset, int count)
    {
        // Negative offset = from end
        if (offset < 0)
            offset = Math.Max(0, _lines.Count + offset);

        offset = Math.Min(offset, _lines.Count);
        count = Math.Min(count, _lines.Count - offset);
        if (count <= 0)
            return ("", offset);

        var slice = _lines.Skip(offset).Take(count);
        return (string.Join("\n", slice), offset + count);
    }

    public string Grep(string pattern)
    {
        var sb = new StringBuilder();
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);
        for (int i = 0; i < _lines.Count; i++)
        {
            if (regex.IsMatch(_lines[i]))
                sb.AppendLine($"{i}: {_lines[i]}");
        }
        return sb.ToString().TrimEnd();
    }

    public string GetPreview(int maxBytes = 512)
    {
        var sb = new StringBuilder();
        foreach (var line in _lines)
        {
            if (sb.Length + line.Length + 1 > maxBytes)
                break;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString();
    }
}

// ── File Reader (lcat) ───────────────────────────────────────────────

public static class LlmFileReader
{
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Text
        { ".txt", "text/plain" }, { ".md", "text/markdown" }, { ".csv", "text/csv" },
        { ".json", "application/json" }, { ".xml", "text/xml" }, { ".yaml", "text/yaml" },
        { ".yml", "text/yaml" }, { ".toml", "text/toml" }, { ".ini", "text/plain" },
        { ".cfg", "text/plain" }, { ".conf", "text/plain" }, { ".log", "text/plain" },

        // Source code
        { ".cs", "text/x-csharp" }, { ".rs", "text/x-rust" }, { ".py", "text/x-python" },
        { ".js", "text/javascript" }, { ".ts", "text/typescript" }, { ".go", "text/x-go" },
        { ".java", "text/x-java" }, { ".c", "text/x-c" }, { ".cpp", "text/x-c++" },
        { ".h", "text/x-c" }, { ".rb", "text/x-ruby" }, { ".sh", "text/x-shellscript" },
        { ".bash", "text/x-shellscript" }, { ".rush", "text/x-rush" },
        { ".html", "text/html" }, { ".css", "text/css" }, { ".sql", "text/x-sql" },
        { ".swift", "text/x-swift" }, { ".kt", "text/x-kotlin" },
        { ".ps1", "text/x-powershell" }, { ".psm1", "text/x-powershell" },
        { ".el", "text/x-emacs-lisp" }, { ".vim", "text/x-vim" },
        { ".lua", "text/x-lua" }, { ".r", "text/x-r" },
        { ".dockerfile", "text/x-dockerfile" }, { ".makefile", "text/x-makefile" },

        // Binary — images
        { ".png", "image/png" }, { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" },
        { ".gif", "image/gif" }, { ".svg", "image/svg+xml" }, { ".ico", "image/x-icon" },
        { ".webp", "image/webp" }, { ".bmp", "image/bmp" },

        // Binary — documents
        { ".pdf", "application/pdf" }, { ".doc", "application/msword" },
        { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        { ".xls", "application/vnd.ms-excel" },
        { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
        { ".ppt", "application/vnd.ms-powerpoint" },
        { ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },

        // Binary — archives
        { ".zip", "application/zip" }, { ".gz", "application/gzip" },
        { ".tar", "application/x-tar" }, { ".7z", "application/x-7z-compressed" },

        // Binary — other
        { ".exe", "application/x-executable" }, { ".dll", "application/x-msdownload" },
        { ".so", "application/x-sharedlib" }, { ".dylib", "application/x-mach-binary" },
        { ".wasm", "application/wasm" },
    };

    public static string GetMimeType(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
            return "application/octet-stream";
        return MimeTypes.TryGetValue(ext, out var mime) ? mime : "application/octet-stream";
    }

    public static bool IsTextMime(string mime)
    {
        return mime.StartsWith("text/") || mime == "application/json"
            || mime == "text/xml" || mime == "text/yaml" || mime == "text/toml";
    }

    public static LlmResult ReadFile(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return new LlmResult
            {
                Status = "error",
                ExitCode = 1,
                Stderr = $"lcat: {path}: No such file or directory",
                Cwd = Directory.GetCurrentDirectory()
            };
        }

        var fullPath = Path.GetFullPath(path);
        var mime = GetMimeType(path);
        var fileInfo = new FileInfo(fullPath);

        if (IsTextMime(mime))
        {
            var content = System.IO.File.ReadAllText(fullPath);
            var lineCount = content.Split('\n').Length;
            return new LlmResult
            {
                Status = "success",
                File = fullPath,
                Mime = mime,
                SizeBytes = fileInfo.Length,
                Encoding = "utf8",
                Content = content,
                Lines = lineCount,
                Cwd = Directory.GetCurrentDirectory()
            };
        }
        else
        {
            var bytes = System.IO.File.ReadAllBytes(fullPath);
            var base64 = Convert.ToBase64String(bytes);
            return new LlmResult
            {
                Status = "success",
                File = fullPath,
                Mime = mime,
                SizeBytes = fileInfo.Length,
                Encoding = "base64",
                Content = base64,
                Cwd = Directory.GetCurrentDirectory()
            };
        }
    }
}

// ── TTY Command Detection ────────────────────────────────────────────

public static class TtyBlocklist
{
    private static readonly Dictionary<string, string> BlockedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        { "vim", "Use lcat {args} to read, File.write(\"{args}\", content) to write." },
        { "vi", "Use lcat {args} to read, File.write(\"{args}\", content) to write." },
        { "nano", "Use lcat {args} to read, File.write(\"{args}\", content) to write." },
        { "emacs", "Use lcat {args} to read, File.write(\"{args}\", content) to write." },
        { "less", "Use lcat {args} to read. Output is captured automatically in LLM mode." },
        { "more", "Use lcat {args} to read. Output is captured automatically in LLM mode." },
        { "top", "Use ps aux for process listing." },
        { "htop", "Use ps aux for process listing." },
    };

    public static (bool blocked, LlmResult? result) Check(string input, string cwd)
    {
        var parts = input.Trim().Split(' ', 2);
        var cmd = parts[0];
        var args = parts.Length > 1 ? parts[1] : "";

        if (BlockedCommands.TryGetValue(cmd, out var hintTemplate))
        {
            var hint = hintTemplate.Replace("{args}", args).Trim();
            return (true, new LlmResult
            {
                Status = "error",
                ErrorType = "tty_required",
                Command = cmd,
                Hint = hint,
                Cwd = cwd
            });
        }
        return (false, null);
    }
}

// ── LLM Mode REPL ───────────────────────────────────────────────────

public class LlmMode
{
    private readonly Runspace _runspace;
    private readonly ScriptEngine _scriptEngine;
    private readonly CommandTranslator _translator;
    private readonly LlmSpool _spool = new();
    private int _lastExitCode;
    private readonly string _version;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public LlmMode(Runspace runspace, ScriptEngine scriptEngine, CommandTranslator translator, string version)
    {
        _runspace = runspace;
        _scriptEngine = scriptEngine;
        _translator = translator;
        _version = version;
    }

    public void Run()
    {
        // Force machine-friendly settings
        Environment.SetEnvironmentVariable("NO_COLOR", "1");

        // Prevent hidden interactive prompts from hanging the session
        Environment.SetEnvironmentVariable("CI", "true");
        Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "0");
        Environment.SetEnvironmentVariable("DEBIAN_FRONTEND", "noninteractive");

        while (true)
        {
            // Emit context prompt
            var context = GetContext();
            Console.WriteLine(JsonSerializer.Serialize(context, JsonOpts));

            // Read command — JSON string or plain text
            var raw = Console.ReadLine();
            if (raw == null) break; // EOF

            if (string.IsNullOrWhiteSpace(raw)) continue;

            // JSON-quoted input: unwrap to get newlines. Plain text: use as-is.
            string input;
            if (raw.StartsWith('"') && raw.EndsWith('"'))
            {
                try { input = JsonSerializer.Deserialize<string>(raw) ?? raw; }
                catch { input = raw; }
            }
            else
            {
                input = raw;
            }

            // Execute and emit result envelope
            var result = ExecuteCommand(input);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOpts));
        }
    }

    /// <summary>
    /// Get current shell context (host, user, cwd, git info, last exit code).
    /// Used by agent mode to include context in system prompts.
    /// </summary>
    public LlmContext GetContext()
    {
        var cwd = GetCwd();
        var (branch, dirty) = GetGitBranchAndDirty(cwd);

        return new LlmContext
        {
            Host = GetShortHostname(),
            User = Environment.UserName,
            Cwd = cwd,
            GitBranch = branch,
            GitDirty = dirty,
            LastExitCode = _lastExitCode,
            Version = _version
        };
    }

    /// <summary>
    /// Execute a command and return structured JSON result.
    /// Public for agent mode; also called by the wire-protocol REPL.
    /// </summary>
    public LlmResult ExecuteCommand(string input)
    {
        var cwd = GetCwd();
        var sw = Stopwatch.StartNew();

        // ── Handle builtins first ─────────────────────────────────────
        var firstWord = input.Trim().Split(' ', 2)[0].ToLowerInvariant();

        // lcat
        if (firstWord == "lcat")
        {
            var args = input.Trim().Length > 5 ? input.Trim()[5..].Trim() : "";
            if (string.IsNullOrEmpty(args))
                return new LlmResult { Status = "error", ExitCode = 1, Stderr = "lcat: missing file path", Cwd = cwd, DurationMs = sw.ElapsedMilliseconds };
            var result = LlmFileReader.ReadFile(args);
            result.DurationMs = sw.ElapsedMilliseconds;
            return result;
        }

        // spool
        if (firstWord == "spool")
            return HandleSpool(input.Trim(), cwd, sw);

        // timeout
        if (firstWord == "timeout")
            return HandleTimeout(input, cwd, sw);

        // help — topic-based reference
        if (firstWord == "help")
        {
            var helpArg = input.Trim().Length > 5 ? input.Trim()[5..].Trim() : null;
            var helpOutput = HelpCommand.Execute(helpArg);
            return new LlmResult { Status = "success", ExitCode = 0, Stdout = helpOutput, Cwd = cwd, DurationMs = sw.ElapsedMilliseconds };
        }

        // sql — native database queries with structured JSON output
        if (firstWord == "sql")
            return SqlCommand.ExecuteForLlm(input.Trim(), cwd, sw);

        // ── TTY blocklist ─────────────────────────────────────────────
        var (blocked, ttyResult) = TtyBlocklist.Check(input, cwd);
        if (blocked)
        {
            ttyResult!.DurationMs = sw.ElapsedMilliseconds;
            return ttyResult;
        }

        // ── Rush syntax → transpile and execute ───────────────────────
        if (_scriptEngine.IsRushSyntax(input.Split('\n')[0].Trim()))
        {
            return ExecuteRushSyntax(input, cwd, sw);
        }

        // ── Shell command → translate and execute ─────────────────────
        return ExecuteShellCommand(input, cwd, sw);
    }

    private LlmResult HandleTimeout(string input, string cwd, Stopwatch sw)
    {
        var parts = input.Trim().Split(' ', 3);
        if (parts.Length < 3 || !int.TryParse(parts[1], out var seconds) || seconds <= 0)
        {
            return new LlmResult
            {
                Status = "error",
                ExitCode = 1,
                Stderr = "Usage: timeout <seconds> <command>",
                Cwd = cwd,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var innerCommand = parts[2];
        var timeoutMs = seconds * 1000;

        // Dispatch the inner command through the same triage as Execute(),
        // but with a timeout applied to the actual execution.
        if (_scriptEngine.IsRushSyntax(innerCommand.Split('\n')[0].Trim()))
            return ExecuteRushSyntax(innerCommand, cwd, sw, timeoutMs);

        return ExecuteShellCommand(innerCommand, cwd, sw, timeoutMs);
    }

    private LlmResult ExecuteRushSyntax(string input, string cwd, Stopwatch sw, int? timeoutMs = null)
    {
        string? psCode;
        try
        {
            psCode = _scriptEngine.TranspileFile(input);
        }
        catch (Exception ex)
        {
            return new LlmResult
            {
                Status = "syntax_error",
                Errors = new List<string> { ex.Message },
                Cwd = cwd,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (psCode == null)
        {
            return new LlmResult
            {
                Status = "syntax_error",
                Errors = new List<string> { "Failed to transpile Rush code" },
                Cwd = cwd,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        return ExecutePowerShell(psCode, cwd, sw, timeoutMs);
    }

    private LlmResult ExecuteShellCommand(string input, string cwd, Stopwatch sw, int? timeoutMs = null)
    {
        var translated = _translator.Translate(input);

        // Native commands (not translated to PowerShell cmdlets) run via Process.Start
        // so they use the .NET process PATH — which includes paths added by init.rush.
        // PowerShell's command discovery caches PATH at runspace open and doesn't refresh.
        bool hasPipe = CommandTranslator.HasUnquotedPipe(input);
        bool hasRedirect = CommandTranslator.HasUnquotedRedirection(input);
        if (translated == null && !hasPipe && !hasRedirect)
            return ExecuteNativeCommand(input, cwd, sw, timeoutMs);

        return ExecutePowerShell(translated ?? input, cwd, sw, timeoutMs);
    }

    private LlmResult ExecuteNativeCommand(string input, string cwd, Stopwatch sw, int? timeoutMs = null)
    {
        try
        {
            var trimmed = input.Trim();
            var firstSpace = trimmed.IndexOf(' ');
            var exe = firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
            var args = firstSpace > 0 ? trimmed[(firstSpace + 1)..] : "";

            // SSH keepalive injection — detect dead connections in ~45s
            if (exe.Equals("ssh", StringComparison.OrdinalIgnoreCase))
            {
                args = "-o ServerAliveInterval=15 -o ServerAliveCountMax=3 " + args;
            }

            var psi = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return new LlmResult { Status = "error", ExitCode = 1, Stderr = $"Failed to start: {exe}", Cwd = cwd, DurationMs = sw.ElapsedMilliseconds };

            // Read streams concurrently to avoid deadlock on large output
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (timeoutMs.HasValue)
            {
                if (!proc.WaitForExit(timeoutMs.Value))
                {
                    // Timed out — kill the child process
                    try { proc.Kill(true); proc.WaitForExit(1000); } catch { }
                    _lastExitCode = 124;
                    var partialOut = stdoutTask.Wait(500) ? stdoutTask.Result.TrimEnd('\n', '\r') : "";
                    var partialErr = stderrTask.Wait(500) ? stderrTask.Result.TrimEnd('\n', '\r') : "";
                    return new LlmResult
                    {
                        Status = "error",
                        ExitCode = 124,
                        ErrorType = "timeout",
                        Stderr = $"Command timed out after {timeoutMs.Value / 1000}s",
                        Stdout = string.IsNullOrEmpty(partialOut) ? null : partialOut,
                        Cwd = cwd,
                        DurationMs = sw.ElapsedMilliseconds
                    };
                }
            }
            else
            {
                proc.WaitForExit(); // existing behavior — no timeout
            }

            var stdout = stdoutTask.GetAwaiter().GetResult().TrimEnd('\n', '\r');
            var stderr = stderrTask.GetAwaiter().GetResult().TrimEnd('\n', '\r');
            var exitCode = proc.ExitCode;
            _lastExitCode = exitCode;

            // Sync CWD — if the command was cd-like, .NET dir doesn't change
            // (Process.Start runs in a child process). This is fine — cd is
            // translated by CommandTranslator and goes through PowerShell.

            // Check output limit (4KB)
            const int OutputLimit = 4096;
            if (System.Text.Encoding.UTF8.GetByteCount(stdout) > OutputLimit && !string.IsNullOrEmpty(stdout))
            {
                _spool.Store(stdout);
                var preview = _spool.GetPreview(512);
                return new LlmResult
                {
                    Status = "output_limit",
                    ExitCode = exitCode,
                    Cwd = cwd,
                    StdoutLines = _spool.TotalLines,
                    StdoutBytes = _spool.TotalBytes,
                    Preview = preview,
                    PreviewBytes = System.Text.Encoding.UTF8.GetByteCount(preview),
                    Stderr = string.IsNullOrEmpty(stderr) ? null : stderr,
                    Hint = $"Output spooled ({FormatBytes(_spool.TotalBytes)}, {_spool.TotalLines} lines). Use spool to retrieve: spool 0:50, spool --tail=20, spool --grep=<pattern>, spool --all",
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            return new LlmResult
            {
                Status = exitCode == 0 ? "success" : "error",
                ExitCode = exitCode,
                Cwd = cwd,
                Stdout = string.IsNullOrEmpty(stdout) ? null : stdout,
                Stderr = string.IsNullOrEmpty(stderr) ? null : stderr,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Command not found as native — fall back to PowerShell
            // (might be a PS function, alias, or cmdlet not in CommandTranslator)
            return ExecutePowerShell(input, cwd, sw, timeoutMs);
        }
        catch (Exception ex)
        {
            _lastExitCode = 1;
            return new LlmResult
            {
                Status = "error",
                ExitCode = 1,
                Cwd = cwd,
                Stderr = ex.Message,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    private LlmResult ExecutePowerShell(string script, string cwd, Stopwatch sw, int? timeoutMs = null)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript(script);

            List<PSObject> results;

            if (timeoutMs.HasValue)
            {
                // Async invocation with timeout
                var output = new PSDataCollection<PSObject>();
                var asyncResult = ps.BeginInvoke<PSObject, PSObject>(null, output);
                if (!asyncResult.AsyncWaitHandle.WaitOne(timeoutMs.Value))
                {
                    ps.Stop();
                    _lastExitCode = 124;
                    // Collect any partial output
                    var partialOut = string.Join("\n", output.Where(r => r != null).Select(r => r.ToString()));
                    return new LlmResult
                    {
                        Status = "error",
                        ExitCode = 124,
                        ErrorType = "timeout",
                        Stderr = $"Command timed out after {timeoutMs.Value / 1000}s",
                        Stdout = string.IsNullOrEmpty(partialOut) ? null : partialOut,
                        Cwd = cwd,
                        DurationMs = sw.ElapsedMilliseconds
                    };
                }
                ps.EndInvoke(asyncResult);
                results = output.Where(r => r != null).ToList();
            }
            else
            {
                results = ps.Invoke().Where(r => r != null).ToList(); // existing — no timeout
            }

            // ── Format object detection ───────────────────────────
            // Format-Table/Format-List/Format-Wide produce Format objects
            // that aren't data — pipe through Out-String to render them.
            if (results.Count > 0 && IsFormatOutput(results))
            {
                try
                {
                    using var fmtPs = PowerShell.Create();
                    fmtPs.Runspace = _runspace;
                    fmtPs.AddCommand("Out-String");
                    var fmtResults = fmtPs.Invoke(results);
                    results = fmtResults.Where(r => r != null).ToList();
                }
                catch { /* fall through to normal rendering */ }
            }

            // ── Object-mode detection ──────────────────────────────
            bool objectMode = IsObjectOutput(results);
            string? stdoutType = objectMode ? "objects" : null;
            object? stdoutValue;
            string stdoutText; // always needed for byte-count / spool

            if (objectMode)
            {
                stdoutValue = SerializeObjects(results);
                // For spool / byte-count, serialize each object as one JSONL line
                stdoutText = string.Join("\n", results
                    .Where(r => r?.BaseObject != null)
                    .Select(r => JsonSerializer.Serialize(ProjectObject(r))));
            }
            else
            {
                var text = string.Join("\n", results.Select(r => r.ToString()));
                stdoutText = text;
                stdoutValue = string.IsNullOrEmpty(text) ? null : text;
            }

            var stderr = "";
            if (ps.Streams.Error.Count > 0)
                stderr = string.Join("\n", ps.Streams.Error.Select(e => e.ToString()));

            // Get exit code from PowerShell
            int exitCode = 0;
            if (ps.HadErrors) exitCode = 1;
            try
            {
                using var exitPs = PowerShell.Create();
                exitPs.Runspace = _runspace;
                exitPs.AddScript("$LASTEXITCODE");
                var exitResult = exitPs.Invoke();
                if (exitResult.Count > 0 && exitResult[0] != null)
                {
                    if (int.TryParse(exitResult[0].ToString(), out var lastExit) && lastExit != 0)
                        exitCode = lastExit;
                }
            }
            catch { /* ignore */ }

            _lastExitCode = exitCode;
            cwd = GetCwd(); // May have changed after cd

            // Check output limit (4KB)
            const int OutputLimit = 4096;
            if (System.Text.Encoding.UTF8.GetByteCount(stdoutText) > OutputLimit && !string.IsNullOrEmpty(stdoutText))
            {
                _spool.Store(stdoutText);
                var preview = _spool.GetPreview(512);
                return new LlmResult
                {
                    Status = "output_limit",
                    ExitCode = exitCode,
                    Cwd = cwd,
                    StdoutType = stdoutType,
                    StdoutLines = _spool.TotalLines,
                    StdoutBytes = _spool.TotalBytes,
                    Preview = preview,
                    PreviewBytes = System.Text.Encoding.UTF8.GetByteCount(preview),
                    Stderr = string.IsNullOrEmpty(stderr) ? null : stderr,
                    Hint = objectMode
                        ? $"Output spooled ({FormatBytes(_spool.TotalBytes)}, {results.Count} objects). Use spool to retrieve: spool 0:50, spool --tail=20, spool --grep=<pattern>, spool --all"
                        : $"Output spooled ({FormatBytes(_spool.TotalBytes)}, {_spool.TotalLines} lines). Use spool to retrieve: spool 0:50, spool --tail=20, spool --grep=<pattern>, spool --all",
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            return new LlmResult
            {
                Status = exitCode == 0 ? "success" : "error",
                ExitCode = exitCode,
                Cwd = cwd,
                StdoutType = stdoutType,
                Stdout = stdoutValue,
                Stderr = string.IsNullOrEmpty(stderr) ? null : stderr,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (PipelineStoppedException)
        {
            _lastExitCode = 130;
            return new LlmResult
            {
                Status = "error",
                ExitCode = 130,
                Cwd = cwd,
                Stderr = "Command interrupted",
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (ActionPreferenceStopException ex)
        {
            _lastExitCode = 1;
            var msg = ex.InnerException?.Message ?? ex.Message;
            // Strip verbose PS prefix
            const string prefix = "The running command stopped because the preference variable";
            if (msg.Contains(prefix))
            {
                var colonIdx = msg.IndexOf(": ", msg.IndexOf("Stop:") + 1);
                if (colonIdx > 0) msg = msg[(colonIdx + 2)..].Trim();
            }
            return new LlmResult
            {
                Status = "error",
                ExitCode = 1,
                Cwd = cwd,
                Stderr = msg,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            _lastExitCode = 1;
            return new LlmResult
            {
                Status = "error",
                ExitCode = 1,
                Cwd = cwd,
                Stderr = ex.Message,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    private LlmResult HandleSpool(string input, string cwd, Stopwatch sw)
    {
        if (!_spool.HasData)
        {
            return new LlmResult
            {
                Status = "error",
                ExitCode = 1,
                Stderr = "No spooled output. Run a command first.",
                Cwd = cwd,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var args = input.Length > 5 ? input[6..].Trim() : "";

        // spool --all
        if (args == "--all")
        {
            var (content, _) = _spool.Read(0, _spool.TotalLines);
            _lastExitCode = 0;
            return new LlmResult
            {
                Status = "success",
                Stdout = content,
                SpoolPosition = _spool.TotalLines,
                SpoolTotal = _spool.TotalLines,
                Cwd = cwd,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        // spool --head=N
        if (args.StartsWith("--head="))
        {
            if (int.TryParse(args[7..], out var n))
            {
                var (content, pos) = _spool.Read(0, n);
                _lastExitCode = 0;
                return new LlmResult
                {
                    Status = "success",
                    Stdout = content,
                    SpoolPosition = pos,
                    SpoolTotal = _spool.TotalLines,
                    Cwd = cwd,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }
        }

        // spool --tail=N
        if (args.StartsWith("--tail="))
        {
            if (int.TryParse(args[7..], out var n))
            {
                var (content, pos) = _spool.Read(-n, n);
                _lastExitCode = 0;
                return new LlmResult
                {
                    Status = "success",
                    Stdout = content,
                    SpoolPosition = pos,
                    SpoolTotal = _spool.TotalLines,
                    Cwd = cwd,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }
        }

        // spool --grep=pattern
        if (args.StartsWith("--grep="))
        {
            var pattern = args[7..];
            try
            {
                var content = _spool.Grep(pattern);
                _lastExitCode = 0;
                return new LlmResult
                {
                    Status = "success",
                    Stdout = string.IsNullOrEmpty(content) ? null : content,
                    SpoolTotal = _spool.TotalLines,
                    Cwd = cwd,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }
            catch (RegexParseException ex)
            {
                return new LlmResult
                {
                    Status = "error",
                    ExitCode = 1,
                    Stderr = $"Invalid regex: {ex.Message}",
                    Cwd = cwd,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }
        }

        // spool offset:count
        var colonIdx = args.IndexOf(':');
        if (colonIdx > 0 || (colonIdx == 0 && args.Length > 1))
        {
            // Allow negative offset like -50:50
            var offsetStr = args[..colonIdx];
            var countStr = args[(colonIdx + 1)..];

            // Handle just "spool :count" as "spool 0:count"
            if (string.IsNullOrEmpty(offsetStr)) offsetStr = "0";

            if (int.TryParse(offsetStr, out var offset) && int.TryParse(countStr, out var count))
            {
                var (content, pos) = _spool.Read(offset, count);
                _lastExitCode = 0;
                return new LlmResult
                {
                    Status = "success",
                    Stdout = string.IsNullOrEmpty(content) ? null : content,
                    SpoolPosition = pos,
                    SpoolTotal = _spool.TotalLines,
                    Cwd = cwd,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }
        }

        // Unknown spool syntax
        return new LlmResult
        {
            Status = "error",
            ExitCode = 1,
            Stderr = "Usage: spool <offset>:<count>, spool --head=N, spool --tail=N, spool --grep=<pattern>, spool --all",
            Cwd = cwd,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    // ── Object-Mode Helpers ─────────────────────────────────────────

    /// <summary>
    /// Detect whether PowerShell results are structured objects (FileInfo, Process, etc.)
    /// Detect Format-Table/Format-List/Format-Wide output objects.
    /// These are rendering instructions, not data — need Out-String to render.
    /// </summary>
    private static bool IsFormatOutput(List<PSObject> results)
    {
        foreach (var r in results)
        {
            if (r?.BaseObject == null) continue;
            var typeName = r.BaseObject.GetType().FullName ?? "";
            if (typeName.StartsWith("Microsoft.PowerShell.Commands.Internal.Format."))
                return true;
        }
        return false;
    }

    /// <summary>
    /// rather than simple values (string, int, bool, etc.) that should stay as text.
    /// </summary>
    private static bool IsObjectOutput(List<PSObject> results)
    {
        if (results.Count == 0) return false;
        foreach (var r in results)
        {
            if (r?.BaseObject == null) continue;
            var baseObj = r.BaseObject;
            // Simple values → text mode (matches OutputRenderer.IsSimpleValue)
            if (baseObj is string or int or long or double or float or bool
                or decimal or DateTime or Guid)
                return false;
            // PathInfo → text mode (pwd/cd output)
            if (baseObj.GetType().FullName == "System.Management.Automation.PathInfo")
                return false;
        }
        return true;
    }

    /// <summary>
    /// Safe property access — processes can exit between enumeration and property read.
    /// </summary>
    private static object? GetPropValue(PSObject obj, string name)
    {
        try { return obj.Properties[name]?.Value; }
        catch { return null; }
    }

    /// <summary>
    /// Serialize structured PSObject results to a JsonElement array for inline JSON in the envelope.
    /// </summary>
    private static JsonElement SerializeObjects(List<PSObject> results)
    {
        var projections = new List<Dictionary<string, object?>>();
        foreach (var r in results)
        {
            if (r?.BaseObject == null) continue;
            projections.Add(ProjectObject(r));
        }
        var json = JsonSerializer.Serialize(projections);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>
    /// Route to type-specific projection or fallback.
    /// </summary>
    private static Dictionary<string, object?> ProjectObject(PSObject obj)
    {
        var typeName = obj.BaseObject?.GetType().FullName ?? "";
        return typeName switch
        {
            "System.IO.FileInfo" => ProjectFileInfo(obj),
            "System.IO.DirectoryInfo" => ProjectDirectoryInfo(obj),
            "System.Diagnostics.Process" => ProjectProcess(obj),
            "System.Management.Automation.PSDriveInfo" => ProjectPSDrive(obj),
            _ => ProjectGeneric(obj)
        };
    }

    private static Dictionary<string, object?> ProjectFileInfo(PSObject obj)
    {
        var fi = (System.IO.FileInfo)obj.BaseObject;
        return new Dictionary<string, object?>
        {
            ["name"] = fi.Name,
            ["size"] = fi.Length,
            ["modified"] = fi.LastWriteTimeUtc.ToString("o"),
            ["type"] = "file",
            ["path"] = fi.FullName
        };
    }

    private static Dictionary<string, object?> ProjectDirectoryInfo(PSObject obj)
    {
        var di = (System.IO.DirectoryInfo)obj.BaseObject;
        return new Dictionary<string, object?>
        {
            ["name"] = di.Name,
            ["modified"] = di.LastWriteTimeUtc.ToString("o"),
            ["type"] = "directory",
            ["path"] = di.FullName
        };
    }

    private static Dictionary<string, object?> ProjectProcess(PSObject obj)
    {
        var proc = (System.Diagnostics.Process)obj.BaseObject;
        var dict = new Dictionary<string, object?>
        {
            ["pid"] = proc.Id,
            ["name"] = proc.ProcessName
        };
        try { dict["memory"] = proc.WorkingSet64; } catch { dict["memory"] = null; }
        try { dict["cpu"] = Math.Round(proc.TotalProcessorTime.TotalSeconds, 1); } catch { dict["cpu"] = null; }
        return dict;
    }

    private static Dictionary<string, object?> ProjectPSDrive(PSObject obj)
    {
        return new Dictionary<string, object?>
        {
            ["name"] = GetPropValue(obj, "Name"),
            ["used"] = GetPropValue(obj, "Used"),
            ["free"] = GetPropValue(obj, "Free"),
            ["provider"] = GetPropValue(obj, "Provider")?.ToString()
        };
    }

    /// <summary>
    /// Fallback: enumerate non-PS properties, up to 10.
    /// Handles PSCustomObject, Hashtable, and unknown types.
    /// </summary>
    private static Dictionary<string, object?> ProjectGeneric(PSObject obj)
    {
        var dict = new Dictionary<string, object?>();
        int count = 0;
        foreach (var prop in obj.Properties)
        {
            if (count >= 10) break;
            // Skip PowerShell internal properties
            if (prop.Name.StartsWith("PS") && prop.MemberType == PSMemberTypes.Property)
                continue;
            try { dict[prop.Name] = prop.Value; }
            catch { dict[prop.Name] = null; }
            count++;
        }
        return dict;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private string GetCwd()
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-Location");
            var loc = ps.Invoke();
            return loc.Count > 0 ? loc[0].ToString()! : Directory.GetCurrentDirectory();
        }
        catch
        {
            return Directory.GetCurrentDirectory();
        }
    }

    private static string GetShortHostname()
    {
        var name = Environment.MachineName;
        var dot = name.IndexOf('.');
        if (dot > 0) name = name[..dot];
        return name.ToLowerInvariant();
    }

    private static (string? Branch, bool IsDirty) GetGitBranchAndDirty(string cwd)
    {
        string? branch = null;
        bool isDirty = false;

        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --abbrev-ref HEAD")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (null, false);
            branch = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(1000);
            if (proc.ExitCode != 0 || string.IsNullOrEmpty(branch))
                return (null, false);
        }
        catch { return (null, false); }

        try
        {
            var psi2 = new ProcessStartInfo("git", "status --porcelain")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc2 = Process.Start(psi2);
            if (proc2 != null)
            {
                var output = proc2.StandardOutput.ReadToEnd();
                proc2.WaitForExit(1000);
                isDirty = !string.IsNullOrEmpty(output.Trim());
            }
        }
        catch { /* just show branch without * */ }

        return (branch, isDirty);
    }

    private static string FormatBytes(int bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
        return $"{bytes / (1024.0 * 1024.0):F1}MB";
    }
}
