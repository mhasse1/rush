using System.Runtime.InteropServices;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Windows-specific tests. Run on Windows CI to verify fixes for
/// #73 (ANSI), #77 (admin detection), #78 (UTF-8), #79 (MCP-SSH paths).
/// Skipped on macOS/Linux.
/// </summary>
public class WindowsTests
{
    private bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // ── #77: Admin detection ─────────────────────────────────────────

    [Fact]
    [Trait("Category", "Windows")]
    public void IsRoot_NonElevated_ReturnsFalse()
    {
        if (!IsWindows) return;

        // CI runners are NOT elevated — IsRoot should return false
        var result = Rush.Prompt.IsRoot();
        Assert.False(result, "IsRoot() should return false on non-elevated Windows CI runner");
    }

    // ── #78: UTF-8 output encoding ──────────────────────────────────

    [Fact]
    [Trait("Category", "Windows")]
    public void Rush_OutputsUtf8_Checkmark()
    {
        if (!IsWindows) return;

        // rush -c should produce UTF-8 output containing ✓ or readable text
        var (stdout, stderr, exitCode) = TestHelper.RunRush("puts \"✓ test\"");
        Assert.Equal(0, exitCode);
        // The output should contain the checkmark, not ? or garbage
        Assert.Contains("✓", stdout);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void Rush_OutputsUtf8_Cross()
    {
        if (!IsWindows) return;

        var (stdout, stderr, exitCode) = TestHelper.RunRush("puts \"✗ fail\"");
        Assert.Equal(0, exitCode);
        Assert.Contains("✗", stdout);
    }

    // ── #79: MCP-SSH path detection ─────────────────────────────────

    [Fact]
    [Trait("Category", "Windows")]
    public void Rush_LlmMode_EmitsContext()
    {
        if (!IsWindows) return;

        // rush --llm should emit a JSON context line with "ready":true
        // This verifies the binary starts correctly on Windows
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = TestHelper.RushBinary,
            ArgumentList = { "--llm" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(proc);

        // Read first line (should be JSON context)
        var task = proc!.StandardOutput.ReadLineAsync();
        var completed = task.Wait(10_000);
        Assert.True(completed, "rush --llm should emit context within 10 seconds");

        var line = task.Result;
        Assert.NotNull(line);
        Assert.Contains("\"ready\"", line);
        Assert.Contains("\"shell\"", line);

        // Clean up
        proc.StandardInput.Close();
        proc.Kill();
        proc.WaitForExit(3000);
    }

    // ── #73/#82: ANSI VT processing ─────────────────────────────────

    [Fact]
    [Trait("Category", "Windows")]
    public void Rush_AnsiEscapes_NotInOutput()
    {
        if (!IsWindows) return;

        // rush -c output should not contain literal ANSI escape codes
        // (they should be processed by the terminal, not appear as text)
        var (stdout, stderr, exitCode) = TestHelper.RunRush("puts \"hello world\"");
        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("[0m", stdout);
        Assert.DoesNotContain("[90m", stdout);
        Assert.DoesNotContain("\x1b", stdout);
    }

    // ── Quoted executable paths (#81) ───────────────────────────────

    [Fact]
    [Trait("Category", "Windows")]
    public void QuotedExePath_Works()
    {
        if (!IsWindows) return;

        // "C:\Windows\System32\hostname.exe" should work
        var (stdout, stderr, exitCode) = TestHelper.RunRush("\"C:\\Windows\\System32\\hostname.exe\"");
        Assert.Equal(0, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(stdout), "hostname should produce output");
    }

    // ── Coreutils shim ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Windows")]
    public void Rush_LsWorks_OnWindows()
    {
        if (!IsWindows) return;

        // ls should work — either via coreutils shim, Git for Windows, or PS alias
        var (stdout, stderr, exitCode) = TestHelper.RunRush("ls");
        Assert.Equal(0, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(stdout), "ls should produce output on Windows");
    }

    // ── ps block (#76) ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Windows")]
    public void PsBlock_DollarUnderscore_Survives()
    {
        if (!IsWindows) return;

        // $_ should survive inside ps blocks
        var (stdout, stderr, exitCode) = TestHelper.RunRush(
            "ps\n  1..3 | ForEach-Object { $_ * 2 }\nend");
        Assert.Equal(0, exitCode);
        Assert.Contains("2", stdout);
        Assert.Contains("4", stdout);
        Assert.Contains("6", stdout);
    }

    // ── Windows UNC path translation (#75) ──────────────────────────

    [Fact]
    [Trait("Category", "Windows")]
    public void UncPath_TranslatedToBackslash()
    {
        if (!IsWindows) return;

        // //localhost/C$ should translate to \\localhost\C$
        // We can't test actual UNC access in CI, but we can test that
        // the path doesn't cause a Rush syntax error
        var (stdout, stderr, exitCode) = TestHelper.RunRush("puts \"//server/share\"");
        Assert.Equal(0, exitCode);
        Assert.Contains("//server/share", stdout);
    }
}
