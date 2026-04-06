using System.Diagnostics;
using System.Text.Json;

namespace Rush;

/// <summary>
/// Manages a persistent SSH process running `rush --llm` on a remote host.
/// Wraps the LlmMode wire protocol: write command text to stdin, read
/// LlmContext/LlmResult JSON lines from stdout.
///
/// Wire protocol (per command):
///   1. Remote Rush emits LlmContext JSON  (ready, host, cwd, git info)
///   2. We write command to stdin           (plain text or JSON-quoted)
///   3. Remote Rush emits LlmResult JSON   (status, exit_code, stdout/stderr)
///   4. Repeat
///
/// The first LlmContext is consumed during TryCreate(); subsequent contexts
/// are read after each LlmResult and cached for GetCachedContext().
/// </summary>
internal class SshLlmSession : IDisposable
{
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private LlmContext? _lastContext;
    private readonly string _host;
    private readonly object _lock = new();
    private bool _disposed;

    private const int ReadTimeoutMs = 30_000;     // 30s per command
    private const int ConnectTimeoutMs = 15_000;   // 15s for initial connect

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private SshLlmSession(string host)
    {
        _host = host;
    }

    // ── Factory ──────────────────────────────────────────────────────────

    /// <summary>
    /// Attempt to create a persistent Rush session on a remote host.
    /// Returns null if Rush is not installed or the SSH connection fails.
    /// </summary>
    internal static SshLlmSession? TryCreate(string host)
    {
        // Try rush in PATH, then common install locations.
        // Windows OpenSSH doesn't load the user's PATH profile for ssh host 'command',
        // so we try several common locations including where.exe discovery.
        var commands = new List<string>
        {
            "rush --llm",
            "rush.exe --llm",
            "/usr/local/bin/rush --llm",
            "C:\\bin\\rush.exe --llm",
            "C:/bin/rush.exe --llm",
            "& 'C:\\bin\\rush.exe' --llm",  // PowerShell invoke syntax
        };

        // On any host, try to discover rush via where/which and use the full path
        var discoveredPath = DiscoverRushPath(host);
        if (discoveredPath != null)
        {
            // Wrap in PS single quotes if path has backslashes (Windows) —
            // prevents PS 5.1 from interpreting \b \n etc. as escape sequences
            if (discoveredPath.Contains('\\'))
                commands.Insert(0, $"& '{discoveredPath}' --llm");
            else
                commands.Insert(0, $"{discoveredPath} --llm");
        }

        foreach (var cmd in commands)
        {
            Console.Error.WriteLine($"[rush-ssh] Trying: ssh {host} '{cmd}'");
            var session = new SshLlmSession(host);
            if (!session.StartProcessWith(cmd))
            {
                Console.Error.WriteLine($"[rush-ssh]   → process start failed");
                session.Dispose();
                continue;
            }

            var ctx = session.ReadContextLine(ConnectTimeoutMs);
            if (ctx != null && ctx.Ready)
            {
                Console.Error.WriteLine($"[rush-ssh] Persistent Rush session on {host} (v{ctx.Version}) via: {cmd}");
                session._lastContext = ctx;
                return session;
            }

            Console.Error.WriteLine($"[rush-ssh]   → no valid context (timeout or bad response)");
            session.Dispose();
        }

        Console.Error.WriteLine($"[rush-ssh] Rush not available on {host}, using raw shell");
        return null;
    }

    /// <summary>
    /// Try to discover Rush's full path on a remote host using where.exe (Windows)
    /// or which (Unix). Returns the path or null if not found.
    /// Uses a short SSH call — not a persistent session.
    /// </summary>
    private static string? DiscoverRushPath(string host)
    {
        // Try where.exe (Windows) then which (Unix)
        string[] probes = new[] { "where.exe rush", "which rush" };

        foreach (var probe in probes)
        {
            try
            {
                var psi = new ProcessStartInfo("ssh")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
                psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ConnectTimeout=5");
                SshPool.Apply(psi);
                psi.ArgumentList.Add(host);
                psi.ArgumentList.Add(probe);

                using var proc = Process.Start(psi);
                if (proc == null) continue;
                SshPool.Track(host);

                var stdout = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(5000);

                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(stdout))
                {
                    // where.exe may return multiple lines — take the first
                    var path = stdout.Split('\n')[0].Trim().TrimEnd('\r');
                    if (path.Length > 0)
                    {
                        Console.Error.WriteLine($"[rush-ssh] Discovered rush on {host}: {path}");
                        return path;
                    }
                }
            }
            catch { }
        }

        return null;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────

    internal bool IsAlive => _process != null && !_process.HasExited && !_disposed;

    /// <summary>
    /// Kill the current process and start a fresh one.
    /// Session state (cwd, variables) on the remote is lost.
    /// </summary>
    internal bool Reconnect()
    {
        lock (_lock)
        {
            KillProcess();

            // Try same paths as TryCreate
            bool started = false;
            foreach (var cmd in new[] { "rush --llm", "rush.exe --llm", "C:\\bin\\rush.exe --llm", "/usr/local/bin/rush --llm" })
            {
                if (StartProcessWith(cmd)) { started = true; break; }
            }
            if (!started)
                return false;

            var ctx = ReadContextLine(ConnectTimeoutMs);
            if (ctx == null || !ctx.Ready)
            {
                KillProcess();
                return false;
            }

            _lastContext = ctx;
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        KillProcess();
    }

    // ── Wire Protocol ────────────────────────────────────────────────────

    /// <summary>
    /// Execute a command on the remote host via the persistent Rush session.
    /// Writes command to stdin, reads LlmResult from stdout, then reads
    /// the next LlmContext and caches it.
    /// </summary>
    internal LlmResult Execute(string command)
    {
        lock (_lock)
        {
            if (!IsAlive)
                throw new InvalidOperationException($"Session to {_host} is not alive");

            // Send command (JSON-quote if it contains newlines, per wire protocol)
            try
            {
                if (command.Contains('\n'))
                    _stdin!.WriteLine(JsonSerializer.Serialize(command));
                else
                    _stdin!.WriteLine(command);
            }
            catch (Exception ex)
            {
                return new LlmResult
                {
                    Status = "error",
                    ExitCode = 1,
                    Stderr = $"Failed to send command to {_host}: {ex.Message}"
                };
            }

            // Read LlmResult
            var result = ReadResultLine();
            if (result == null)
            {
                return new LlmResult
                {
                    Status = "error",
                    ExitCode = 1,
                    Stderr = $"Lost connection to {_host}"
                };
            }

            // After the result, remote Rush emits next LlmContext — cache it
            var ctx = ReadContextLine(ReadTimeoutMs);
            if (ctx != null) _lastContext = ctx;

            return result;
        }
    }

    /// <summary>
    /// Read a file on the remote host using the `lcat` builtin (built into rush --llm).
    /// Returns structured result with mime type, encoding, and content.
    /// </summary>
    internal LlmResult ReadFile(string path)
    {
        return Execute($"lcat {path}");
    }

    // ── Envelope Protocol ───────────────────────────────────────────────

    /// <summary>
    /// Execute a command using the JSON envelope protocol.
    /// Supports optional cwd, timeout, and env vars.
    /// Falls back to plain text if no optional params are set.
    /// </summary>
    internal LlmResult Execute(string command, string? cwd = null,
        int? timeoutSeconds = null, Dictionary<string, string>? env = null)
    {
        // If no optional params, use the original plain text path
        if (cwd == null && timeoutSeconds == null && env == null)
            return Execute(command);

        var envelope = new Dictionary<string, object> { ["cmd"] = command };
        if (cwd != null) envelope["cwd"] = cwd;
        if (timeoutSeconds != null) envelope["timeout"] = timeoutSeconds;
        if (env != null) envelope["env"] = env;

        return SendEnvelope(envelope);
    }

    /// <summary>
    /// Write a file to the remote host via the JSON transfer protocol.
    /// Content is base64-encoded and sent in-band (suitable for files under ~1MB).
    /// For larger files, use PutFileViaScp().
    /// </summary>
    internal LlmResult PutFile(string remotePath, byte[] content, string? mode = null, bool append = false)
    {
        if (content.Length > 1_048_576)
            return PutFileViaScp(remotePath, content, mode);

        var envelope = new Dictionary<string, object>
        {
            ["transfer"] = "put",
            ["path"] = remotePath,
            ["content"] = Convert.ToBase64String(content)
        };
        if (mode != null) envelope["mode"] = mode;
        if (append) envelope["append"] = true;

        return SendEnvelope(envelope);
    }

    /// <summary>
    /// Read a file from the remote host via the JSON transfer protocol.
    /// Returns structured result with content (utf8 or base64), mime type, size.
    /// </summary>
    internal LlmResult GetFile(string remotePath)
    {
        var envelope = new Dictionary<string, object>
        {
            ["transfer"] = "get",
            ["path"] = remotePath
        };
        return SendEnvelope(envelope);
    }

    /// <summary>
    /// Push a script to the remote host, execute it, and return results.
    /// Script content is base64-encoded and transferred via the JSON protocol.
    /// The remote side writes it to a temp file, executes with the appropriate shell,
    /// captures output, and cleans up.
    /// </summary>
    internal LlmResult ExecScript(string filename, byte[] content, string? shell = null,
        string[]? args = null, int? timeoutSeconds = null, bool cleanup = true)
    {
        var envelope = new Dictionary<string, object>
        {
            ["transfer"] = "exec",
            ["filename"] = filename,
            ["content"] = Convert.ToBase64String(content)
        };
        if (shell != null) envelope["shell"] = shell;
        if (args != null && args.Length > 0) envelope["args"] = args;
        if (timeoutSeconds != null) envelope["timeout"] = timeoutSeconds;
        if (!cleanup) envelope["cleanup"] = false;

        return SendEnvelope(envelope);
    }

    /// <summary>
    /// Send a JSON envelope to the remote Rush session and read the result.
    /// </summary>
    private LlmResult SendEnvelope(Dictionary<string, object> envelope)
    {
        var json = JsonSerializer.Serialize(envelope, JsonOpts);
        lock (_lock)
        {
            if (!IsAlive)
                throw new InvalidOperationException($"Session to {_host} is not alive");

            try
            {
                _stdin!.WriteLine(json);
            }
            catch (Exception ex)
            {
                return new LlmResult
                {
                    Status = "error", ExitCode = 1,
                    Stderr = $"Failed to send envelope to {_host}: {ex.Message}"
                };
            }

            var result = ReadResultLine();
            if (result == null)
            {
                return new LlmResult
                {
                    Status = "error", ExitCode = 1,
                    Stderr = $"Lost connection to {_host}"
                };
            }

            var ctx = ReadContextLine(ReadTimeoutMs);
            if (ctx != null) _lastContext = ctx;

            return result;
        }
    }

    /// <summary>
    /// Large file upload via SCP using the existing ControlMaster socket.
    /// </summary>
    private LlmResult PutFileViaScp(string remotePath, byte[] content, string? mode)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, content);

            var psi = new ProcessStartInfo("scp")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            SshPool.Apply(psi);
            psi.ArgumentList.Add(tempFile);
            psi.ArgumentList.Add($"{_host}:{remotePath}");

            using var proc = Process.Start(psi);
            if (proc == null)
                return new LlmResult { Status = "error", ExitCode = 1, Stderr = "Failed to start scp" };

            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(60_000);

            if (proc.ExitCode != 0)
                return new LlmResult { Status = "error", ExitCode = proc.ExitCode, Stderr = $"scp failed: {stderr}" };

            // Set permissions if requested
            if (mode != null)
                Execute($"chmod {mode} {remotePath}");

            return new LlmResult
            {
                Status = "success", ExitCode = 0,
                File = remotePath,
                SizeBytes = content.Length
            };
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    /// <summary>
    /// Get the cached LlmContext from the last interaction.
    /// Zero latency — no SSH roundtrip needed.
    /// </summary>
    internal LlmContext? GetCachedContext() => _lastContext;

    // ── Internals ────────────────────────────────────────────────────────

    private bool StartProcessWith(string remoteCommand)
    {
        var psi = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // SSH options: keepalive, batch mode, connection pooling
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ServerAliveInterval=15");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ServerAliveCountMax=3");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ConnectTimeout=5");
        SshPool.Apply(psi);
        psi.ArgumentList.Add(_host);
        psi.ArgumentList.Add(remoteCommand);

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rush-ssh] Failed to start SSH to {_host}: {ex.Message}");
            return false;
        }

        if (_process == null)
        {
            Console.Error.WriteLine($"[rush-ssh] Process.Start returned null for {_host}");
            return false;
        }

        SshPool.Track(_host);
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
        _stdin.AutoFlush = true;

        // Drain stderr in background to prevent buffer deadlock
        var stderr = _process.StandardError;
        var host = _host;
        _ = Task.Run(() =>
        {
            try
            {
                string? line;
                while ((line = stderr.ReadLine()) != null)
                    Console.Error.WriteLine($"[rush-ssh:{host}] {line}");
            }
            catch { /* process exited */ }
        });

        return true;
    }

    /// <summary>
    /// Read a LlmContext JSON line from stdout.
    /// Context lines have a "ready" field.
    /// </summary>
    private LlmContext? ReadContextLine(int timeoutMs)
    {
        while (true)
        {
            var line = ReadLineWithTimeout(timeoutMs);
            if (line == null) return null;
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var ctx = JsonSerializer.Deserialize<LlmContext>(line, JsonOpts);
                if (ctx != null && ctx.Ready)
                    return ctx;
            }
            catch
            {
                // Not valid context JSON — might be startup noise, skip
                Console.Error.WriteLine($"[rush-ssh:{_host}] Unexpected: {line}");
            }
        }
    }

    /// <summary>
    /// Read a LlmResult JSON line from stdout.
    /// Skips any interleaved LlmContext lines (which have "ready" field).
    /// Result lines have a "status" field.
    /// </summary>
    private LlmResult? ReadResultLine()
    {
        while (true)
        {
            var line = ReadLineWithTimeout(ReadTimeoutMs);
            if (line == null) return null;
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Check if this is a context line (has "ready") rather than a result
            if (line.Contains("\"ready\""))
            {
                try
                {
                    var ctx = JsonSerializer.Deserialize<LlmContext>(line, JsonOpts);
                    if (ctx != null && ctx.Ready)
                    {
                        _lastContext = ctx;
                        continue; // Skip context lines, keep reading for result
                    }
                }
                catch { /* not valid context, try as result */ }
            }

            try
            {
                return JsonSerializer.Deserialize<LlmResult>(line, JsonOpts);
            }
            catch
            {
                Console.Error.WriteLine($"[rush-ssh:{_host}] Unexpected output: {line}");
                return null;
            }
        }
    }

    /// <summary>
    /// Read a line from stdout with a timeout to prevent hangs.
    /// Returns null if timeout expires or stream ends.
    /// </summary>
    private string? ReadLineWithTimeout(int timeoutMs)
    {
        if (_stdout == null) return null;

        try
        {
            var task = _stdout.ReadLineAsync();
            if (task.Wait(timeoutMs))
                return task.Result;

            Console.Error.WriteLine($"[rush-ssh:{_host}] Read timeout ({timeoutMs}ms)");
            return null;
        }
        catch
        {
            return null;
        }
    }

    private void KillProcess()
    {
        try { _stdin?.Close(); } catch { }
        try
        {
            if (_process != null && !_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(3000);
            }
        }
        catch { }
        _process?.Dispose();
        _process = null;
        _stdin = null;
        _stdout = null;
    }
}
