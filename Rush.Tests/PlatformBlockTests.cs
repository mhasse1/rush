using System.Diagnostics;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for Rush platform blocks: macos/win64/win32/linux with optional property conditions.
/// </summary>
public class PlatformBlockTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private readonly ScriptEngine _engine = new(new CommandTranslator());

    private static string Transpile(string rushCode)
    {
        var lexer = new Lexer(rushCode);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens, rushCode);
        var nodes = parser.Parse();
        var transpiler = new RushTranspiler(new CommandTranslator());
        return string.Join("\n", nodes.Select(s => transpiler.TranspileNode(s)));
    }

    private static RushNode ParseSingle(string rushCode)
    {
        var lexer = new Lexer(rushCode);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens, rushCode);
        var nodes = parser.Parse();
        return nodes.First();
    }

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
        proc.WaitForExit();
        return (stdout.TrimEnd(), stderr.TrimEnd(), proc.ExitCode);
    }

    // ── Lexer Tests ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("macos", RushTokenType.Macos)]
    [InlineData("win64", RushTokenType.Win64)]
    [InlineData("win32", RushTokenType.Win32)]
    [InlineData("linux", RushTokenType.Linux)]
    [InlineData("MACOS", RushTokenType.Macos)]
    [InlineData("Win32", RushTokenType.Win32)]
    [InlineData("Linux", RushTokenType.Linux)]
    public void Lexer_TokenizesPlatformKeywords(string keyword, RushTokenType expected)
    {
        var lexer = new Lexer(keyword);
        var tokens = lexer.Tokenize();
        Assert.Equal(expected, tokens[0].Type);
    }

    // ── Triage Tests ────────────────────────────────────────────────────

    [Theory]
    [InlineData("macos")]
    [InlineData("win64")]
    [InlineData("win32")]
    [InlineData("linux")]
    [InlineData("macos.version >= \"25.0\"")]
    [InlineData("linux.arch == \"x64\"")]
    public void Triage_PlatformKeywordsAreRushSyntax(string input)
    {
        Assert.True(_engine.IsRushSyntax(input));
    }

    // ── Block Depth Tests ───────────────────────────────────────────────

    [Theory]
    [InlineData("macos", 1)]
    [InlineData("macos\n  puts \"hello\"\nend", 0)]
    [InlineData("linux\n  puts \"hi\"", 1)]
    [InlineData("win32\n  Write-Output 'hi'\nend", 0)]
    public void BlockDepth_PlatformBlocks(string input, int expected)
    {
        Assert.Equal(expected, _engine.GetBlockDepth(input));
    }

    [Theory]
    [InlineData("macos\n  puts \"hello\"")]
    [InlineData("win32\n  Get-ChildItem")]
    public void IsIncomplete_OpenPlatformBlock(string input)
    {
        Assert.True(_engine.IsIncomplete(input));
    }

    // ── Parser Tests ────────────────────────────────────────────────────

    [Theory]
    [InlineData("macos", "macos")]
    [InlineData("linux", "linux")]
    [InlineData("win64", "win64")]
    public void Parser_PlatformBlock_HasCorrectPlatform(string keyword, string expected)
    {
        var code = $"{keyword}\n  puts \"hello\"\nend";
        var node = ParseSingle(code);
        var pb = Assert.IsType<PlatformBlockNode>(node);
        Assert.Equal(expected, pb.Platform);
        Assert.NotNull(pb.Body);
        Assert.Null(pb.RawBody);
        Assert.False(pb.IsRaw);
    }

    [Fact]
    public void Parser_Win32Block_CapturesRawBody()
    {
        var code = "win32\n  Get-ChildItem -Path C:\\\nend";
        var node = ParseSingle(code);
        var pb = Assert.IsType<PlatformBlockNode>(node);
        Assert.Equal("win32", pb.Platform);
        Assert.True(pb.IsRaw);
        Assert.NotNull(pb.RawBody);
        Assert.Contains("Get-ChildItem", pb.RawBody);
    }

    [Fact]
    public void Parser_PlatformBlock_WithArchCondition()
    {
        var code = "linux.arch == \"x64\"\n  puts \"64-bit\"\nend";
        var node = ParseSingle(code);
        var pb = Assert.IsType<PlatformBlockNode>(node);
        Assert.Equal("linux", pb.Platform);
        Assert.Equal("arch", pb.Property);
        Assert.Equal("==", pb.Operator);
        Assert.Equal("x64", pb.PropertyValue);
        Assert.NotNull(pb.Body);
    }

    [Fact]
    public void Parser_PlatformBlock_WithVersionCondition()
    {
        var code = "macos.version >= \"25.0\"\n  puts \"new mac\"\nend";
        var node = ParseSingle(code);
        var pb = Assert.IsType<PlatformBlockNode>(node);
        Assert.Equal("macos", pb.Platform);
        Assert.Equal("version", pb.Property);
        Assert.Equal(">=", pb.Operator);
        Assert.Equal("25.0", pb.PropertyValue);
    }

    // ── Transpiler Tests ────────────────────────────────────────────────

    [Fact]
    public void Transpiler_MacosBlock_GeneratesIfOs()
    {
        var code = "macos\n  puts \"hello\"\nend";
        var ps = Transpile(code);
        Assert.Contains("if ($os -eq 'macos')", ps);
        Assert.Contains("Write-Output", ps);
    }

    [Fact]
    public void Transpiler_Win64Block_MapsToWindows()
    {
        var code = "win64\n  puts \"hello\"\nend";
        var ps = Transpile(code);
        Assert.Contains("if ($os -eq 'windows')", ps);
    }

    [Fact]
    public void Transpiler_LinuxBlock_GeneratesIfOs()
    {
        var code = "linux\n  puts \"hello\"\nend";
        var ps = Transpile(code);
        Assert.Contains("if ($os -eq 'linux')", ps);
    }

    [Fact]
    public void Transpiler_Win32Block_GeneratesBase64Call()
    {
        var code = "win32\n  Write-Output 'hello'\nend";
        var ps = Transpile(code);
        Assert.Contains("if ($os -eq 'windows')", ps);
        Assert.Contains("__rush_win32", ps);
        // Body should be base64 encoded
        Assert.DoesNotContain("Write-Output", ps);
    }

    [Fact]
    public void Transpiler_MacosWithArchCondition()
    {
        var code = "macos.arch == \"arm64\"\n  puts \"Apple Silicon\"\nend";
        var ps = Transpile(code);
        Assert.Contains("$os -eq 'macos'", ps);
        Assert.Contains("$__rush_arch -eq 'arm64'", ps);
    }

    [Fact]
    public void Transpiler_MacosWithVersionCondition()
    {
        var code = "macos.version >= \"25.0\"\n  puts \"new\"\nend";
        var ps = Transpile(code);
        Assert.Contains("$os -eq 'macos'", ps);
        Assert.Contains("[version]$__rush_os_version -ge [version]'25.0'", ps);
    }

    [Fact]
    public void Transpiler_LinuxWithArchCondition()
    {
        var code = "linux.arch == \"x64\"\n  puts \"amd64\"\nend";
        var ps = Transpile(code);
        Assert.Contains("$os -eq 'linux'", ps);
        Assert.Contains("$__rush_arch -eq 'x64'", ps);
    }

    // ── Integration Tests (rush -c) ─────────────────────────────────────

    [Fact]
    public void Integration_MatchingPlatformBlockExecutes()
    {
        // This test runs on the current platform — detect which block to test
        var platform = OperatingSystem.IsMacOS() ? "macos" :
            OperatingSystem.IsLinux() ? "linux" : "win64";

        var (stdout, _, exitCode) = RunRush($"{platform}\n  puts \"platform_ok\"\nend");
        Assert.Equal(0, exitCode);
        Assert.Contains("platform_ok", stdout);
    }

    [Fact]
    public void Integration_NonMatchingPlatformBlockSkipped()
    {
        // Use a platform that definitely doesn't match
        var platform = OperatingSystem.IsMacOS() ? "linux" :
            OperatingSystem.IsLinux() ? "macos" : "linux";

        var (stdout, _, exitCode) = RunRush($"{platform}\n  puts \"should_not_appear\"\nend");
        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("should_not_appear", stdout);
    }

    [Fact]
    public void Integration_Win32BlockSkippedOnNonWindows()
    {
        if (OperatingSystem.IsWindows()) return; // skip on Windows

        var (stdout, _, exitCode) = RunRush("win32\n  Write-Output 'should_not_appear'\nend");
        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("should_not_appear", stdout);
    }

    [Fact]
    public void Integration_PlatformBlockWithArchCondition()
    {
        if (!OperatingSystem.IsMacOS()) return; // macOS-specific test

        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

        var (stdout, _, exitCode) = RunRush(
            $"macos.arch == \"{arch}\"\n  puts \"arch_ok\"\nend");
        Assert.Equal(0, exitCode);
        Assert.Contains("arch_ok", stdout);
    }

    [Fact]
    public void Integration_PlatformBlockWithWrongArchSkipped()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var (stdout, _, exitCode) = RunRush(
            "macos.arch == \"x86\"\n  puts \"should_not_appear\"\nend");
        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("should_not_appear", stdout);
    }

    [Fact]
    public void Integration_TranspileFile_PlatformBlockInScript()
    {
        var engine = new ScriptEngine(new CommandTranslator());
        var script = "macos\n  puts \"from_script\"\nend";
        var ps = engine.TranspileFile(script);
        Assert.NotNull(ps);
        Assert.Contains("$os -eq 'macos'", ps);
    }
}
