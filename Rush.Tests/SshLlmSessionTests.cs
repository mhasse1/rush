using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Integration tests for the rush --llm wire protocol used by SshLlmSession.
/// Tests run against a local rush --llm process (no SSH required).
/// Validates: context emission, command execution, state persistence,
/// lcat file reading, and process lifecycle.
/// </summary>
public class SshLlmSessionTests : IDisposable
{
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Start a local rush --llm process for testing the wire protocol.
    /// </summary>
    private void StartLlmProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = TestHelper.RushBinary,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--llm");

        _process = Process.Start(psi);
        Assert.NotNull(_process);

        _stdin = _process!.StandardInput;
        _stdout = _process.StandardOutput;
        _stdin.AutoFlush = true;

        // Drain stderr in background to prevent buffer deadlock
        var stderr = _process.StandardError;
        _ = Task.Run(() =>
        {
            try { while (stderr.ReadLine() != null) { } } catch { }
        });
    }

    private string? ReadLineWithTimeout(int timeoutMs = 15000)
    {
        var task = _stdout!.ReadLineAsync();
        if (task.Wait(timeoutMs))
            return task.Result;
        return null;
    }

    private LlmContext? ReadContext()
    {
        var line = ReadLineWithTimeout();
        if (line == null) return null;
        return JsonSerializer.Deserialize<LlmContext>(line, JsonOpts);
    }

    private LlmResult? SendCommandAndReadResult(string command)
    {
        _stdin!.WriteLine(command);

        // Read result (skip any unexpected context lines)
        while (true)
        {
            var line = ReadLineWithTimeout();
            if (line == null) return null;
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Skip context lines (have "ready" field)
            if (line.Contains("\"ready\""))
            {
                try
                {
                    var ctx = JsonSerializer.Deserialize<LlmContext>(line, JsonOpts);
                    if (ctx != null && ctx.Ready) continue;
                }
                catch { }
            }

            return JsonSerializer.Deserialize<LlmResult>(line, JsonOpts);
        }
    }

    public void Dispose()
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
    }

    // ── Tests ────────────────────────────────────────────────────────────

    [Fact]
    public void LlmMode_EmitsValidContext_OnStartup()
    {
        StartLlmProcess();
        var ctx = ReadContext();

        Assert.NotNull(ctx);
        Assert.True(ctx!.Ready);
        Assert.Equal("rush", ctx.Shell);
        Assert.False(string.IsNullOrEmpty(ctx.Host));
        Assert.False(string.IsNullOrEmpty(ctx.Cwd));
        Assert.False(string.IsNullOrEmpty(ctx.Version));
    }

    [Fact]
    public void LlmMode_ExecuteCommand_ReturnsResult()
    {
        StartLlmProcess();
        var ctx = ReadContext();
        Assert.NotNull(ctx);

        var result = SendCommandAndReadResult("echo hello");
        Assert.NotNull(result);
        Assert.Equal("success", result!.Status);
        Assert.Equal(0, result.ExitCode);
        // stdout could be string or JsonElement
        var stdout = result.Stdout?.ToString() ?? "";
        Assert.Contains("hello", stdout);
    }

    [Fact]
    public void LlmMode_LastExitCode_TrackedInContext()
    {
        StartLlmProcess();
        var ctx1 = ReadContext();
        Assert.NotNull(ctx1);
        Assert.Equal(0, ctx1!.LastExitCode);

        // Run a successful command
        var result1 = SendCommandAndReadResult("echo ok");
        Assert.NotNull(result1);
        Assert.Equal(0, result1!.ExitCode);

        // Read context — exit code should still be 0
        var ctx2 = ReadContext();
        Assert.NotNull(ctx2);
        Assert.Equal(0, ctx2!.LastExitCode);

        // Run a failing command
        var result2 = SendCommandAndReadResult("ls /nonexistent_path_xyz_abc");
        Assert.NotNull(result2);

        // Read context — exit code should be non-zero
        var ctx3 = ReadContext();
        Assert.NotNull(ctx3);
        Assert.True(ctx3!.LastExitCode != 0,
            $"Expected non-zero exit code after failed command, got {ctx3.LastExitCode}");
    }

    [Fact]
    public void LlmMode_VariablesPersist_AcrossCommands()
    {
        StartLlmProcess();
        var ctx = ReadContext();
        Assert.NotNull(ctx);

        // Set a variable
        var setResult = SendCommandAndReadResult("test_var = 42");
        Assert.NotNull(setResult);

        // Read next context
        ReadContext();

        // Use the variable
        var getResult = SendCommandAndReadResult("puts test_var");
        Assert.NotNull(getResult);
        Assert.Equal("success", getResult!.Status);
        var stdout = getResult.Stdout?.ToString() ?? "";
        Assert.Contains("42", stdout);
    }

    [Fact]
    public void LlmMode_LcatReadsFile_WithMetadata()
    {
        StartLlmProcess();
        var ctx = ReadContext();
        Assert.NotNull(ctx);

        // Create a temp file with .txt extension so lcat detects it as text (not binary)
        var tmpDir = Path.GetTempPath();
        var tmpFile = Path.Combine(tmpDir, $"rush_test_{Guid.NewGuid()}.txt");
        File.WriteAllText(tmpFile, "hello from lcat test");

        try
        {
            var result = SendCommandAndReadResult($"lcat {tmpFile}");
            Assert.NotNull(result);
            Assert.Equal("success", result!.Status);
            // lcat returns structured file data with mime, encoding, content fields
            Assert.NotNull(result.File);
            Assert.NotNull(result.Mime);
            Assert.NotNull(result.Encoding);
            Assert.Equal("utf8", result.Encoding);
            Assert.NotNull(result.Content);
            Assert.Contains("hello from lcat test", result.Content);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void LlmMode_ErrorCommand_ReturnsErrorStatus()
    {
        StartLlmProcess();
        var ctx = ReadContext();
        Assert.NotNull(ctx);

        // Run a command that will fail
        var result = SendCommandAndReadResult("ls /nonexistent_path_xyz");
        Assert.NotNull(result);
        // Should have non-zero exit code or error status
        Assert.True(result!.ExitCode != 0 || result.Status == "error",
            $"Expected error but got status={result.Status}, exit_code={result.ExitCode}");
    }

    [Fact]
    public void LlmMode_MultiLineCommand_ViaJsonQuoting()
    {
        StartLlmProcess();
        var ctx = ReadContext();
        Assert.NotNull(ctx);

        // Send a multi-line command via JSON quoting (per wire protocol)
        var multiLine = "if true\n  puts 99\nend";
        var jsonQuoted = JsonSerializer.Serialize(multiLine);
        _stdin!.WriteLine(jsonQuoted);

        // Read result
        while (true)
        {
            var line = ReadLineWithTimeout();
            if (line == null) { Assert.Fail("Timeout reading result"); return; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains("\"ready\"")) continue;

            var result = JsonSerializer.Deserialize<LlmResult>(line, JsonOpts);
            Assert.NotNull(result);
            Assert.Equal("success", result!.Status);
            var stdout = result.Stdout?.ToString() ?? "";
            Assert.Contains("99", stdout);
            break;
        }
    }

    [Fact]
    public void LlmMode_ProcessExits_OnEofStdin()
    {
        StartLlmProcess();
        var ctx = ReadContext();
        Assert.NotNull(ctx);

        // Close stdin to signal EOF
        _stdin!.Close();

        // Process should exit
        var exited = _process!.WaitForExit(5000);
        Assert.True(exited, "Process should exit after stdin EOF");
    }
}
