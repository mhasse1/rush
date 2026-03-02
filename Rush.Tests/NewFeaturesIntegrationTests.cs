using System.Diagnostics;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Integration tests for new shell features: brace expansion, printf,
/// arithmetic expansion, ~user, process substitution, and set options.
/// All tests run via `rush -c` to exercise the full pipeline.
/// Uses ArgumentList (not Arguments) to avoid parent-shell $ interpretation.
/// </summary>
public class NewFeaturesIntegrationTests
{
    /// <summary>
    /// Path to the rush binary built by dotnet build.
    /// </summary>
    private static string RushBinary
    {
        get
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Rush.csproj")))
                dir = Path.GetDirectoryName(dir);

            if (dir == null)
                throw new InvalidOperationException("Could not find Rush project root");

            var binary = Path.Combine(dir, "bin", "Debug", "net8.0", "osx-arm64", "rush");
            if (!File.Exists(binary))
                binary = Path.Combine(dir, "bin", "Debug", "net8.0", "linux-x64", "rush");
            if (!File.Exists(binary))
                binary = Path.Combine(dir, "bin", "Debug", "net8.0", "rush");
            return binary;
        }
    }

    /// <summary>
    /// Run a rush command via `rush -c` and capture stdout + stderr.
    /// Uses ArgumentList to pass args directly via execv, avoiding
    /// shell interpretation of $, *, (, ), etc.
    /// </summary>
    private static (string stdout, string stderr, int exitCode) RunRush(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = RushBinary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return (stdout.Trim(), stderr.Trim(), proc.ExitCode);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Brace Expansion ──────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("echo {a,b,c}", "a b c")]
    [InlineData("echo file.{bak,txt}", "file.bak file.txt")]
    [InlineData("echo pre{A,B}post", "preApost preBpost")]
    public void BraceExpansion_BasicPatterns(string command, string expected)
    {
        var (stdout, _, exitCode) = RunRush(command);
        Assert.Equal(expected, stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void BraceExpansion_PathPattern()
    {
        var (stdout, _, _) = RunRush("echo src/{a,b,c}/main.rs");
        Assert.Equal("src/a/main.rs src/b/main.rs src/c/main.rs", stdout);
    }

    [Fact]
    public void BraceExpansion_NestedBraces()
    {
        var (stdout, _, _) = RunRush("echo {a,{b,c}}");
        Assert.Equal("a b c", stdout);
    }

    [Fact]
    public void BraceExpansion_NoBraces_PassThrough()
    {
        var (stdout, _, _) = RunRush("echo hello world");
        Assert.Equal("hello world", stdout);
    }

    [Fact]
    public void BraceExpansion_SingleItemNoBrace()
    {
        // Single item (no comma) should not expand
        var (stdout, _, _) = RunRush("echo {solo}");
        Assert.Equal("{solo}", stdout);
    }

    [Fact]
    public void BraceExpansion_EmptyBraces_NoExpansion()
    {
        // Empty braces or braces without commas should pass through
        var (stdout, _, _) = RunRush("echo {}");
        Assert.Equal("{}", stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Printf Builtin ───────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Printf_StringFormat()
    {
        var (stdout, _, exitCode) = RunRush("printf '%s' hello");
        Assert.Equal("hello", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Printf_IntegerFormat()
    {
        var (stdout, _, _) = RunRush("printf '%d' 42");
        Assert.Equal("42", stdout);
    }

    [Fact]
    public void Printf_HexFormat()
    {
        var (stdout, _, _) = RunRush("printf '%x' 255");
        Assert.Equal("ff", stdout);
    }

    [Fact]
    public void Printf_PercentLiteral()
    {
        var (stdout, _, _) = RunRush("printf '100%%'");
        Assert.Equal("100%", stdout);
    }

    [Fact]
    public void Printf_MultipleArgs()
    {
        var (stdout, _, _) = RunRush("printf '%s is %d' name 42");
        Assert.Equal("name is 42", stdout);
    }

    [Fact]
    public void Printf_FloatFormat()
    {
        var (stdout, _, _) = RunRush("printf '%f' 3.14");
        Assert.StartsWith("3.14", stdout);
    }

    [Fact]
    public void Printf_NewlineEscape()
    {
        var (stdout, _, _) = RunRush("printf 'line1\\nline2'");
        Assert.Contains("line1", stdout);
        Assert.Contains("line2", stdout);
    }

    [Fact]
    public void Printf_TabEscape()
    {
        var (stdout, _, _) = RunRush("printf 'a\\tb'");
        Assert.Contains("a\tb", stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Arithmetic Expansion ─────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("echo $((2 + 3))", "5")]
    [InlineData("echo $((10 - 4))", "6")]
    [InlineData("echo $((3 * 7))", "21")]
    [InlineData("echo $((20 / 4))", "5")]
    [InlineData("echo $((17 % 5))", "2")]
    public void ArithmeticExpansion_BasicOperations(string command, string expected)
    {
        var (stdout, _, exitCode) = RunRush(command);
        Assert.Equal(expected, stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void ArithmeticExpansion_NestedParens()
    {
        var (stdout, _, _) = RunRush("echo $(( (2 + 3) * 4 ))");
        Assert.Equal("20", stdout);
    }

    [Fact]
    public void ArithmeticExpansion_InContext()
    {
        // Arithmetic inside a larger string
        var (stdout, _, _) = RunRush("echo result=$((5+5))");
        Assert.Equal("result=10", stdout);
    }

    [Fact]
    public void ArithmeticExpansion_NoArithmetic_PassThrough()
    {
        var (stdout, _, _) = RunRush("echo no math here");
        Assert.Equal("no math here", stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Tilde Expansion ──────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void TildeExpansion_HomeDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (stdout, _, _) = RunRush("echo ~");
        Assert.Equal(home, stdout);
    }

    [Fact]
    public void TildeExpansion_HomeSlashPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (stdout, _, _) = RunRush("echo ~/Documents");
        Assert.Equal($"{home}/Documents", stdout);
    }

    [Fact]
    public void TildeExpansion_QuotedTilde_NoExpansion()
    {
        var (stdout, _, _) = RunRush("echo '~'");
        Assert.Equal("~", stdout);
    }

    [Fact]
    public void TildeUser_CurrentUser_Resolves()
    {
        var username = Environment.UserName;
        var userHome = OperatingSystem.IsMacOS() ? $"/Users/{username}" : $"/home/{username}";

        // Only test if the user's home directory exists at the expected path
        if (Directory.Exists(userHome))
        {
            var (stdout, _, _) = RunRush($"echo ~{username}");
            Assert.Equal(userHome, stdout);
        }
    }

    [Fact]
    public void TildeUser_UnknownUser_PassThrough()
    {
        // A user that definitely doesn't exist
        var (stdout, _, _) = RunRush("echo ~zzz_nonexistent_user_zzz");
        Assert.Equal("~zzz_nonexistent_user_zzz", stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Process Substitution ─────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void ProcessSubstitution_BasicCommand()
    {
        // <(echo hello) creates a temp file containing "hello", substitutes the path
        var (stdout, _, exitCode) = RunRush("cat <(echo hello)");
        Assert.Equal("hello", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void ProcessSubstitution_WithSort()
    {
        // Process substitution with a different command
        var (stdout, _, _) = RunRush("cat <(echo sorted)");
        Assert.Equal("sorted", stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Environment Variables ────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void EnvVar_HOME_Expands()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (stdout, _, _) = RunRush("echo $HOME");
        Assert.Equal(home, stdout);
    }

    [Fact]
    public void EnvVar_PATH_Expands()
    {
        var (stdout, _, _) = RunRush("echo $PATH");
        Assert.NotEmpty(stdout);
        // PATH should contain at least one path separator
        Assert.Contains(Path.PathSeparator.ToString(), stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Redirect + Expansion Combo ───────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void BraceExpansion_WithRedirect()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            RunRush($"echo {{alpha,beta}} > {tmpFile}");
            var content = File.ReadAllText(tmpFile).Trim();
            Assert.Equal("alpha beta", content);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ArithmeticExpansion_WithRedirect()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            RunRush($"echo $((6 * 7)) > {tmpFile}");
            var content = File.ReadAllText(tmpFile).Trim();
            Assert.Equal("42", content);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }
}
