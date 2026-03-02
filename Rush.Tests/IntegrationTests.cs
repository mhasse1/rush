using System.Diagnostics;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Integration tests that run Rush scripts end-to-end via the rush binary.
/// These tests exercise the full pipeline: parse → triage → transpile → execute.
/// </summary>
public class IntegrationTests
{
    /// <summary>
    /// Path to the rush binary built by dotnet build.
    /// </summary>
    private static string RushBinary
    {
        get
        {
            // Walk up from test output dir to find the Rush binary
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Rush.csproj")))
                dir = Path.GetDirectoryName(dir);

            if (dir == null)
                throw new InvalidOperationException("Could not find Rush project root");

            // The rush binary is built to bin/Debug/net8.0/osx-arm64/rush
            var binary = Path.Combine(dir, "bin", "Debug", "net8.0", "osx-arm64", "rush");
            if (!File.Exists(binary))
            {
                // Try linux path
                binary = Path.Combine(dir, "bin", "Debug", "net8.0", "linux-x64", "rush");
            }
            if (!File.Exists(binary))
            {
                // Try generic path
                binary = Path.Combine(dir, "bin", "Debug", "net8.0", "rush");
            }
            return binary;
        }
    }

    /// <summary>
    /// Path to the test fixtures directory.
    /// </summary>
    private static string FixturesDir
    {
        get
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Rush.Tests.csproj")))
                dir = Path.GetDirectoryName(dir);

            return dir != null
                ? Path.Combine(dir, "Fixtures")
                : throw new InvalidOperationException("Could not find Rush.Tests project root");
        }
    }

    /// <summary>
    /// Run a rush command via `rush -c` and capture stdout + stderr.
    /// </summary>
    private static (string stdout, string stderr, int exitCode) RunRushCommand(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = RushBinary,
            Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return (stdout.Trim(), stderr.Trim(), proc.ExitCode);
    }

    /// <summary>
    /// Run a .rush script file and capture output.
    /// </summary>
    private static (string stdout, string stderr, int exitCode) RunRushScript(string scriptPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = RushBinary,
            Arguments = $"\"{scriptPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return (stdout, stderr, proc.ExitCode);
    }

    // ── Full Integration Script ──────────────────────────────────────────

    [Fact]
    public void IntegrationScript_AllTestsPass()
    {
        var script = Path.Combine(FixturesDir, "integration_test.rush");
        Assert.True(File.Exists(script), $"Script not found: {script}");

        var (stdout, stderr, exitCode) = RunRushScript(script);

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
        var (stdout, _, exitCode) = RunRushCommand(command);
        Assert.Equal(expected, stdout);
    }

    [Fact]
    public void RushC_Version()
    {
        var psi = new ProcessStartInfo
        {
            FileName = RushBinary,
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
            var (_, _, exitCode) = RunRushCommand($"echo redirect test > {tmpFile}");
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
            RunRushCommand($"echo line1 > {tmpFile}");
            RunRushCommand($"echo line2 >> {tmpFile}");
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
}
