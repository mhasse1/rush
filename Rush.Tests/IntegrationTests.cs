using System.Diagnostics;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Integration tests that run Rush scripts end-to-end via the rush binary.
/// These tests exercise the full pipeline: parse → triage → transpile → execute.
/// </summary>
public class IntegrationTests
{
    // ── Full Integration Script ──────────────────────────────────────────

    [Fact]
    public void IntegrationScript_AllTestsPass()
    {
        var script = Path.Combine(TestHelper.FixturesDir, "integration_test.rush");
        Assert.True(File.Exists(script), $"Script not found: {script}");

        var (stdout, stderr, exitCode) = TestHelper.RunRushScript(script);

        // Count PASS/FAIL lines
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var passed = lines.Count(l => l.TrimStart().StartsWith("PASS:"));
        var failed = lines.Count(l => l.TrimStart().StartsWith("FAIL:"));

        // Report failures
        if (failed > 0)
        {
            var failLines = lines.Where(l => l.TrimStart().StartsWith("FAIL:")).ToArray();
            Assert.Fail($"{failed} test(s) failed:\n{string.Join("\n", failLines)}\n\nStderr: {stderr}");
        }

        Assert.True(passed > 0, $"No PASS lines found. stdout: {stdout}\nstderr: {stderr}");
        Assert.Equal(0, exitCode);
    }

    // ── Command Translation (rush -c) ────────────────────────────────────

    [Theory]
    [InlineData("echo hello", "hello")]
    [InlineData("echo hello world", "hello world")]
    public void RushC_BasicCommands(string command, string expected)
    {
        var (stdout, _, exitCode) = TestHelper.RunRush(command);
        Assert.Equal(expected, stdout);
    }

    [Fact]
    public void RushC_Version()
    {
        var psi = new ProcessStartInfo
        {
            FileName = TestHelper.RushBinary,
            Arguments = "--version",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit(10_000);
        Assert.StartsWith("rush ", output);
        Assert.Equal(0, proc.ExitCode);
    }

    // ── I/O Redirection (rush -c with redirects) ─────────────────────────

    [Fact]
    public void RushC_StdoutRedirect_WritesFile()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var (_, _, exitCode) = TestHelper.RunRush($"echo redirect test > {tmpFile}");
            Assert.Equal(0, exitCode);
            var content = File.ReadAllText(tmpFile).Trim();
            Assert.Equal("redirect test", content);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void RushC_AppendRedirect_AppendsFile()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            TestHelper.RunRush($"echo line1 > {tmpFile}");
            TestHelper.RunRush($"echo line2 >> {tmpFile}");
            var lines = File.ReadAllLines(tmpFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            Assert.Equal(2, lines.Length);
            Assert.Equal("line1", lines[0]);
            Assert.Equal("line2", lines[1]);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    // ── Escape Sequences in Strings ────────────────────────────────────

    [Fact]
    public void RushC_Escape_Newline_InDoubleQuotes()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush(@"puts ""hello\nworld""");
        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("hello", lines[0]);
        Assert.Equal("world", lines[1]);
    }

    [Fact]
    public void RushC_Escape_Tab_InDoubleQuotes()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush(@"puts ""col1\tcol2""");
        Assert.Equal(0, exitCode);
        Assert.Contains("\t", stdout);
        Assert.StartsWith("col1", stdout);
    }

    [Fact]
    public void RushC_Escape_SingleQuotes_StayLiteral()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("puts 'hello\\nworld'");
        Assert.Equal(0, exitCode);
        Assert.Equal("hello\\nworld", stdout);
    }

    [Fact]
    public void RushC_Escape_DoubleBackslash()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush(@"puts ""path\\to\\file""");
        Assert.Equal(0, exitCode);
        Assert.Equal("path\\to\\file", stdout);
    }

    [Fact]
    public void RushC_Escape_InInterpolatedString()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush(@"name = ""Rush""; puts ""hello #{name}\ngoodbye""");
        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("hello Rush", lines[0]);
        Assert.Equal("goodbye", lines[1]);
    }

    [Fact]
    public void RushC_Escape_Esc_ProducesControlChar()
    {
        // \e should produce ESC (0x1B) — verify the raw byte is present
        var (stdout, _, exitCode) = TestHelper.RunRush(@"print ""\e[31mred\e[0m""");
        Assert.Equal(0, exitCode);
        Assert.Contains("\x1b[31m", stdout);
    }
}
