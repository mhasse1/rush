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
        var session = new SshLlmSession(host);
        if (!session.StartProcess())
        {
            session.Dispose();
            return null;
        }

        // Read the first line — should be LlmContext JSON with ready:true
        var ctx = session.ReadContextLine(ConnectTimeoutMs);
        if (ctx == null || !ctx.Ready)
        {
            Console.Error.WriteLine($"[rush-ssh] Rush not available on {host}, using raw shell");
            session.Dispose();
            return null;
        }

        Console.Error.WriteLine($"[rush-ssh] Persistent Rush session on {host} (v{ctx.Version})");
        session._lastContext = ctx;
        return session;
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

            if (!StartProcess())
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

    /// <summary>
    /// Get the cached LlmContext from the last interaction.
    /// Zero latency — no SSH roundtrip needed.
    /// </summary>
    internal LlmContext? GetCachedContext() => _lastContext;

    // ── Internals ────────────────────────────────────────────────────────

    private bool StartProcess()
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
        SshPool.Apply(psi);
        psi.ArgumentList.Add(_host);
        psi.ArgumentList.Add("rush --llm");

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
