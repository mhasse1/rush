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
    [JsonPropertyName("stdout")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Stdout { get; set; }
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

        while (true)
        {
            // Emit context prompt
            var context = GetContextData();
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
            var result = Execute(input);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOpts));
        }
    }

    private LlmContext GetContextData()
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

    private LlmResult Execute(string input)
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

    private LlmResult ExecuteRushSyntax(string input, string cwd, Stopwatch sw)
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

        return ExecutePowerShell(psCode, cwd, sw);
    }

    private LlmResult ExecuteShellCommand(string input, string cwd, Stopwatch sw)
    {
        var translated = _translator.Translate(input) ?? input;
        return ExecutePowerShell(translated, cwd, sw);
    }

    private LlmResult ExecutePowerShell(string script, string cwd, Stopwatch sw)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript(script);

            var results = ps.Invoke().Where(r => r != null).ToList();
            var stdout = string.Join("\n", results.Select(r => r.ToString()));

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
