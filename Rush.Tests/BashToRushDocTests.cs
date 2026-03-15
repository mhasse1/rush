using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for every example in docs/bash-to-rush.md.
/// Section by section validation that documented behavior works.
/// </summary>
public class BashToRushDocTests
{
    // ══════════════════════════════════════════════════════════════════
    // ── Section 1: What Stays the Same ────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    // Native commands

    [Fact]
    public void WhatStays_Ls()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("ls /tmp");
        Assert.Equal(0, exitCode);
        Assert.False(string.IsNullOrEmpty(stdout));
    }

    [Fact]
    public void WhatStays_LsLa()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("ls -la /tmp");
        Assert.Equal(0, exitCode);
        Assert.False(string.IsNullOrEmpty(stdout));
    }

    [Fact]
    public void WhatStays_Grep()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), "rush_test_grep_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllText(tmpFile, "Hello World\nGoodbye World\n");
            var (stdout, _, exitCode) = TestHelper.RunRush($"grep -i hello \"{tmpFile}\"");
            Assert.Equal(0, exitCode);
            Assert.Contains("Hello World", stdout);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void WhatStays_Find()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "rush_test_find_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "test.txt"), "");
            var (stdout, _, exitCode) = TestHelper.RunRush($"find \"{tmpDir}\" -name \"*.txt\"");
            Assert.Equal(0, exitCode);
            Assert.Contains("test.txt", stdout);
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    // Pipes

    [Fact]
    public void WhatStays_Pipe()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("echo \"hello world\" | tr a-z A-Z");
        Assert.Equal(0, exitCode);
        Assert.Equal("HELLO WORLD", stdout);
    }

    // Chain operators

    [Fact]
    public void WhatStays_AndAnd()
    {
        var (stdout, _, _) = TestHelper.RunRush("echo hello && echo world");
        Assert.Contains("hello", stdout);
        Assert.Contains("world", stdout);
    }

    [Fact]
    public void WhatStays_OrOr()
    {
        var (stdout, _, _) = TestHelper.RunRush("false || echo fallback");
        Assert.Equal("fallback", stdout);
    }

    [Fact]
    public void WhatStays_MixedChain()
    {
        var (stdout, _, _) = TestHelper.RunRush("true && echo ok || echo fail");
        Assert.Equal("ok", stdout);
    }

    [Fact]
    public void WhatStays_AndAndShortCircuit()
    {
        // false && echo should NOT print
        var (stdout, _, _) = TestHelper.RunRush("false && echo nope");
        Assert.DoesNotContain("nope", stdout);
    }

    // Redirects

    [Fact]
    public void WhatStays_RedirectStdout()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), "rush_test_redir_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            TestHelper.RunRush($"echo hello > \"{tmpFile}\"");
            Assert.True(File.Exists(tmpFile));
            Assert.Contains("hello", File.ReadAllText(tmpFile));
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void WhatStays_RedirectAppend()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), "rush_test_append_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            TestHelper.RunRush($"echo first > \"{tmpFile}\"");
            TestHelper.RunRush($"echo second >> \"{tmpFile}\"");
            var content = File.ReadAllText(tmpFile);
            Assert.Contains("first", content);
            Assert.Contains("second", content);
        }
        finally { File.Delete(tmpFile); }
    }

    // Background

    [Fact]
    public void WhatStays_Background()
    {
        // Background command should not hang
        var (_, _, exitCode) = TestHelper.RunRush("echo bg &");
        Assert.Equal(0, exitCode);
    }

    // Tilde expansion

    [Fact]
    public void WhatStays_TildeCd()
    {
        var (_, _, exitCode) = TestHelper.RunRush("cd ~");
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void WhatStays_TildePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (stdout, _, _) = TestHelper.RunRush("echo ~/Documents");
        Assert.Equal($"{home}/Documents", stdout);
    }

    // Env vars

    [Fact]
    public void WhatStays_EnvVar_HOME()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (stdout, _, _) = TestHelper.RunRush("echo $HOME");
        Assert.Equal(home, stdout);
    }

    [Fact]
    public void WhatStays_EnvVar_PATH()
    {
        var (stdout, _, _) = TestHelper.RunRush("echo $PATH");
        Assert.False(string.IsNullOrEmpty(stdout));
    }

    // Dir stack (pushd/popd)

    [Fact]
    public void WhatStays_PushdPopd()
    {
        var (_, _, exitCode) = TestHelper.RunRush("pushd /tmp && popd");
        Assert.Equal(0, exitCode);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Section 2: Ruby-Like Syntax ──────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void RubySyntax_VariableAssignment()
    {
        var (stdout, _, _) = TestHelper.RunRush("name = \"world\"\nputs name");
        Assert.Equal("world", stdout);
    }

    [Fact]
    public void RubySyntax_StringInterpolation()
    {
        var (stdout, _, _) = TestHelper.RunRush("name = \"world\"\nputs \"Hello #{name}\"");
        Assert.Equal("Hello world", stdout);
    }

    [Fact]
    public void RubySyntax_IfEnd()
    {
        var (stdout, _, _) = TestHelper.RunRush("x = 10\nif x > 5\n  puts \"big\"\nend");
        Assert.Equal("big", stdout);
    }

    [Fact]
    public void RubySyntax_IfFileExist()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), "rush_test_exist_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllText(tmpFile, "data");
            var (stdout, _, _) = TestHelper.RunRush($"if File.exist?(\"{tmpFile}\")\n  puts \"found\"\nend");
            Assert.Equal("found", stdout);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void RubySyntax_ForInArray()
    {
        var (stdout, _, _) = TestHelper.RunRush("for x in [\"a\", \"b\", \"c\"]\n  puts x\nend");
        Assert.Contains("a", stdout);
        Assert.Contains("b", stdout);
        Assert.Contains("c", stdout);
    }

    [Fact]
    public void RubySyntax_ForInDirList()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "rush_test_fordir_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "a.txt"), "");
            File.WriteAllText(Path.Combine(tmpDir, "b.txt"), "");
            var (stdout, _, _) = TestHelper.RunRush($"for f in Dir.list(\"{tmpDir}\")\n  puts f.Name\nend");
            Assert.Contains("a.txt", stdout);
            Assert.Contains("b.txt", stdout);
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public void RubySyntax_ArrayIndex()
    {
        var (stdout, _, _) = TestHelper.RunRush("arr = [1, 2, 3]\nputs arr[0]");
        Assert.Equal("1", stdout);
    }

    [Fact]
    public void RubySyntax_Arithmetic()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts 5 + 3");
        Assert.Equal("8", stdout);
    }
}
