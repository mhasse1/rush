using System.Runtime.InteropServices;
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
    [Trait("Category", "Unix")]
    public void WhatStays_Ls()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var (stdout, _, exitCode) = TestHelper.RunRush("ls /tmp");
        Assert.Equal(0, exitCode);
        Assert.False(string.IsNullOrEmpty(stdout));
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void WhatStays_LsLa()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var (stdout, _, exitCode) = TestHelper.RunRush("ls -la /tmp");
        Assert.Equal(0, exitCode);
        Assert.False(string.IsNullOrEmpty(stdout));
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void WhatStays_Grep()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
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
    [Trait("Category", "Unix")]
    public void WhatStays_Find()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
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
    [Trait("Category", "Unix")]
    public void WhatStays_Pipe()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
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
    [Trait("Category", "Unix")]
    public void WhatStays_OrOr()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var (stdout, _, _) = TestHelper.RunRush("false || echo fallback");
        Assert.Equal("fallback", stdout);
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void WhatStays_MixedChain()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var (stdout, _, _) = TestHelper.RunRush("true && echo ok || echo fail");
        Assert.Equal("ok", stdout);
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void WhatStays_AndAndShortCircuit()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
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
    [Trait("Category", "Unix")]
    public void WhatStays_PushdPopd()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var (_, _, exitCode) = TestHelper.RunRush("pushd /tmp && popd");
        Assert.Equal(0, exitCode);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Section 2: Clean, Intent-Driven Syntax ───────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void CleanSyntax_VariableAssignment()
    {
        var (stdout, _, _) = TestHelper.RunRush("name = \"world\"\nputs name");
        Assert.Equal("world", stdout);
    }

    [Fact]
    public void CleanSyntax_StringInterpolation()
    {
        var (stdout, _, _) = TestHelper.RunRush("name = \"world\"\nputs \"Hello #{name}\"");
        Assert.Equal("Hello world", stdout);
    }

    [Fact]
    public void CleanSyntax_IfEnd()
    {
        var (stdout, _, _) = TestHelper.RunRush("x = 10\nif x > 5\n  puts \"big\"\nend");
        Assert.Equal("big", stdout);
    }

    [Fact]
    public void CleanSyntax_IfFileExist()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), "rush_test_exist_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllText(tmpFile, "data");
            var (stdout, _, _) = TestHelper.RunRush($"if File.exist?(\"{TestHelper.RushPath(tmpFile)}\")\n  puts \"found\"\nend");
            Assert.Equal("found", stdout);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void CleanSyntax_ForInArray()
    {
        var (stdout, _, _) = TestHelper.RunRush("for x in [\"a\", \"b\", \"c\"]\n  puts x\nend");
        Assert.Contains("a", stdout);
        Assert.Contains("b", stdout);
        Assert.Contains("c", stdout);
    }

    [Fact]
    public void CleanSyntax_ForInDirList()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "rush_test_fordir_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "a.txt"), "");
            File.WriteAllText(Path.Combine(tmpDir, "b.txt"), "");
            var (stdout, _, _) = TestHelper.RunRush($"for f in Dir.list(\"{TestHelper.RushPath(tmpDir)}\")\n  puts f\nend");
            Assert.Contains("a.txt", stdout);
            Assert.Contains("b.txt", stdout);
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public void CleanSyntax_ArrayIndex()
    {
        var (stdout, _, _) = TestHelper.RunRush("arr = [1, 2, 3]\nputs arr[0]");
        Assert.Equal("1", stdout);
    }

    [Fact]
    public void CleanSyntax_Arithmetic()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts 5 + 3");
        Assert.Equal("8", stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Section 3: Pipeline Operators ────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    // Count

    [Fact]
    [Trait("Category", "Unix")]
    public void Pipeline_Count()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var (stdout, _, exitCode) = TestHelper.RunRush("printf \"a\\nb\\nc\\n\" | count");
        Assert.Equal(0, exitCode);
        Assert.Equal("3", stdout);
    }

    // First / Last / Skip

    [Fact]
    [Trait("Category", "Unix")]
    public void Pipeline_First()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var (stdout, _, exitCode) = TestHelper.RunRush("printf \"a\\nb\\nc\\nd\\ne\\n\" | first 2");
        Assert.Equal(0, exitCode);
        Assert.Contains("a", stdout);
        Assert.Contains("b", stdout);
        Assert.DoesNotContain("e", stdout);
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Pipeline_Last()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var (stdout, _, exitCode) = TestHelper.RunRush("printf \"a\\nb\\nc\\nd\\ne\\n\" | last 2");
        Assert.Equal(0, exitCode);
        Assert.Contains("d", stdout);
        Assert.Contains("e", stdout);
        Assert.DoesNotContain("a", stdout);
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Pipeline_Skip()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var (stdout, _, exitCode) = TestHelper.RunRush("printf \"a\\nb\\nc\\nd\\n\" | skip 2");
        Assert.Equal(0, exitCode);
        Assert.Contains("c", stdout);
        Assert.Contains("d", stdout);
        Assert.DoesNotContain("a", stdout);
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Pipeline_SkipThenFirst()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        // Doc example: ls | skip 2 | first 3
        var (stdout, _, exitCode) = TestHelper.RunRush("printf \"a\\nb\\nc\\nd\\ne\\n\" | skip 2 | first 2");
        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("c", lines[0]);
        Assert.Equal("d", lines[1]);
    }

    // Sort

    [Fact]
    [Trait("Category", "Unix")]
    public void Pipeline_Sort()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var (stdout, _, exitCode) = TestHelper.RunRush("printf \"c\\na\\nb\\n\" | sort");
        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("a", lines[0]);
        Assert.Equal("b", lines[1]);
        Assert.Equal("c", lines[2]);
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Pipeline_SortReverse()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var (stdout, _, exitCode) = TestHelper.RunRush("printf \"a\\nb\\nc\\n\" | sort -r");
        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("c", lines[0]);
        Assert.Equal("b", lines[1]);
        Assert.Equal("a", lines[2]);
    }

    // Distinct

    [Fact]
    [Trait("Category", "Unix")]
    public void Pipeline_Distinct()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var (stdout, _, exitCode) = TestHelper.RunRush("printf \"a\\nb\\na\\nc\\nb\\n\" | distinct");
        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
    }

    // Where (filter)

    [Fact]
    public void Pipeline_Where_NumericGreaterThan()
    {
        var tmpJson = Path.Combine(Path.GetTempPath(), "rush_test_where_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpJson, "[{\"n\":\"a\",\"v\":5},{\"n\":\"b\",\"v\":15}]");
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{TestHelper.RushPath(tmpJson)}\" | from json | where v > 10 | .n");
            Assert.Equal(0, exitCode);
            Assert.Equal("b", stdout);
        }
        finally { File.Delete(tmpJson); }
    }

    [Fact]
    public void Pipeline_Where_NumericLessThan()
    {
        var tmpJson = Path.Combine(Path.GetTempPath(), "rush_test_wherelt_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpJson, "[{\"n\":\"a\",\"v\":5},{\"n\":\"b\",\"v\":15}]");
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{TestHelper.RushPath(tmpJson)}\" | from json | where v < 10 | .n");
            Assert.Equal(0, exitCode);
            Assert.Equal("a", stdout);
        }
        finally { File.Delete(tmpJson); }
    }

    [Fact]
    public void Pipeline_Where_StringEquals()
    {
        var tmpJson = Path.Combine(Path.GetTempPath(), "rush_test_whereeq_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpJson, "[{\"n\":\"a\",\"v\":5},{\"n\":\"b\",\"v\":15}]");
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{TestHelper.RushPath(tmpJson)}\" | from json | where n == b | .v");
            Assert.Equal(0, exitCode);
            Assert.Equal("15", stdout);
        }
        finally { File.Delete(tmpJson); }
    }

    // Select (columns)

    [Fact]
    public void Pipeline_Select()
    {
        var tmpJson = Path.Combine(Path.GetTempPath(), "rush_test_select_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpJson, "{\"name\":\"a\",\"val\":5}");
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{TestHelper.RushPath(tmpJson)}\" | from json | select name | as json");
            Assert.Equal(0, exitCode);
            Assert.Contains("name", stdout);
            Assert.DoesNotContain("val", stdout);
        }
        finally { File.Delete(tmpJson); }
    }

    // Dot property extraction

    [Fact]
    public void Pipeline_DotProperty()
    {
        var tmpJson = Path.Combine(Path.GetTempPath(), "rush_test_dot_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpJson, "{\"name\":\"hello\"}");
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{TestHelper.RushPath(tmpJson)}\" | from json | .name");
            Assert.Equal(0, exitCode);
            Assert.Equal("hello", stdout);
        }
        finally { File.Delete(tmpJson); }
    }

    // Aggregate: sum, avg, min, max

    [Fact]
    public void Pipeline_Sum()
    {
        var tmpJson = Path.Combine(Path.GetTempPath(), "rush_test_sum_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpJson, "[{\"v\":10},{\"v\":20},{\"v\":30}]");
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{TestHelper.RushPath(tmpJson)}\" | from json | sum v");
            Assert.Equal(0, exitCode);
            Assert.Equal("60", stdout);
        }
        finally { File.Delete(tmpJson); }
    }

    [Fact]
    public void Pipeline_Avg()
    {
        var tmpJson = Path.Combine(Path.GetTempPath(), "rush_test_avg_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpJson, "[{\"v\":10},{\"v\":20},{\"v\":30}]");
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{TestHelper.RushPath(tmpJson)}\" | from json | avg v");
            Assert.Equal(0, exitCode);
            Assert.Equal("20", stdout);
        }
        finally { File.Delete(tmpJson); }
    }

    [Fact]
    public void Pipeline_Min()
    {
        var tmpJson = Path.Combine(Path.GetTempPath(), "rush_test_min_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpJson, "[{\"v\":10},{\"v\":20},{\"v\":30}]");
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{TestHelper.RushPath(tmpJson)}\" | from json | min v");
            Assert.Equal(0, exitCode);
            Assert.Equal("10", stdout);
        }
        finally { File.Delete(tmpJson); }
    }

    [Fact]
    public void Pipeline_Max()
    {
        var tmpJson = Path.Combine(Path.GetTempPath(), "rush_test_max_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpJson, "[{\"v\":10},{\"v\":20},{\"v\":30}]");
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{TestHelper.RushPath(tmpJson)}\" | from json | max v");
            Assert.Equal(0, exitCode);
            Assert.Equal("30", stdout);
        }
        finally { File.Delete(tmpJson); }
    }

    // Format: as json, as csv

    [Fact]
    public void Pipeline_AsJson()
    {
        var tmpJson = Path.Combine(Path.GetTempPath(), "rush_test_asjson_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpJson, "{\"a\":1}");
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{TestHelper.RushPath(tmpJson)}\" | from json | as json");
            Assert.Equal(0, exitCode);
            Assert.Contains("\"a\"", stdout);
            Assert.Contains("1", stdout);
        }
        finally { File.Delete(tmpJson); }
    }

    [Fact]
    public void Pipeline_AsCsv()
    {
        var tmpJson = Path.Combine(Path.GetTempPath(), "rush_test_ascsv_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpJson, "{\"a\":1,\"b\":2}");
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{TestHelper.RushPath(tmpJson)}\" | from json | as csv");
            Assert.Equal(0, exitCode);
            // CSV should have header row with column names
            Assert.Contains("a", stdout);
            Assert.Contains("b", stdout);
        }
        finally { File.Delete(tmpJson); }
    }

    // Parse: from json, from csv

    [Fact]
    public void Pipeline_FromJson()
    {
        var tmpJson = Path.Combine(Path.GetTempPath(), "rush_test_fromjson_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpJson, "{\"x\":42}");
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{TestHelper.RushPath(tmpJson)}\" | from json | .x");
            Assert.Equal(0, exitCode);
            Assert.Equal("42", stdout);
        }
        finally { File.Delete(tmpJson); }
    }

    [Fact]
    public void Pipeline_FromCsv()
    {
        var tmpCsv = Path.Combine(Path.GetTempPath(), "rush_test_fromcsv_" + Guid.NewGuid().ToString("N")[..8] + ".csv");
        try
        {
            File.WriteAllText(tmpCsv, "name,val\na,1\nb,2\n");
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{TestHelper.RushPath(tmpCsv)}\" | from csv | .name");
            Assert.Equal(0, exitCode);
            Assert.Contains("a", stdout);
            Assert.Contains("b", stdout);
        }
        finally { File.Delete(tmpCsv); }
    }

    // Tee

    [Fact]
    [Trait("Category", "Unix")]
    public void Pipeline_Tee()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var tmpFile = Path.Combine(Path.GetTempPath(), "rush_test_tee_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var (stdout, _, exitCode) = TestHelper.RunRush($"printf \"hello\\nworld\\n\" | tee \"{tmpFile}\" | count");
            Assert.Equal(0, exitCode);
            Assert.Equal("2", stdout);
            // tee should also write to file
            Assert.True(File.Exists(tmpFile));
            var content = File.ReadAllText(tmpFile);
            Assert.Contains("hello", content);
            Assert.Contains("world", content);
        }
        finally { if (File.Exists(tmpFile)) File.Delete(tmpFile); }
    }

    // Sort with property (on structured data)

    [Fact]
    public void Pipeline_SortByProperty()
    {
        var tmpJson = Path.Combine(Path.GetTempPath(), "rush_test_sortprop_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpJson, "[{\"n\":\"c\",\"v\":3},{\"n\":\"a\",\"v\":1},{\"n\":\"b\",\"v\":2}]");
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{TestHelper.RushPath(tmpJson)}\" | from json | sort n | .n");
            Assert.Equal(0, exitCode);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("a", lines[0]);
            Assert.Equal("b", lines[1]);
            Assert.Equal("c", lines[2]);
        }
        finally { File.Delete(tmpJson); }
    }

    // Distinct with property

    [Fact]
    public void Pipeline_DistinctByProperty()
    {
        var tmpJson = Path.Combine(Path.GetTempPath(), "rush_test_distinctprop_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpJson, "[{\"n\":\"a\",\"v\":1},{\"n\":\"b\",\"v\":2},{\"n\":\"a\",\"v\":3}]");
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{TestHelper.RushPath(tmpJson)}\" | from json | distinct n | .n");
            Assert.Equal(0, exitCode);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
        }
        finally { File.Delete(tmpJson); }
    }

    // Regression: =~ operator should not trigger tilde expansion

    [Fact]
    public void Pipeline_RegexMatchOperator()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("x = \"hello world\"\nif x =~ \"hello\"\n  puts \"matched\"\nend");
        Assert.Equal(0, exitCode);
        Assert.Equal("matched", stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Section 4: objectify — Text to Objects ───────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Unix")]
    public void Objectify_PsEf_WhereSelect()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        // ps -ef | objectify | where CMD =~ rush | select PID, CMD
        var (stdout, _, exitCode) = TestHelper.RunRush("ps -ef | objectify | select PID, CMD | first 2");
        Assert.Equal(0, exitCode);
        Assert.Contains("PID", stdout);
        Assert.Contains("CMD", stdout);
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Objectify_WhereRegex()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        // objectify + where with =~ regex match
        var (stdout, _, exitCode) = TestHelper.RunRush("ps -ef | objectify | where CMD =~ launchd | first 1 | .CMD");
        Assert.Equal(0, exitCode);
        Assert.Contains("launchd", stdout);
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Objectify_Count()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        // objectify + count
        var (stdout, _, exitCode) = TestHelper.RunRush("ps -ef | objectify | count");
        Assert.Equal(0, exitCode);
        int count = int.Parse(stdout);
        Assert.True(count > 1, $"Expected more than 1 process, got {count}");
    }

    // ── Auto-objectify (ps is in ObjectifyConfig defaults) ──────────

    [Fact]
    [Trait("Category", "Unix")]
    public void AutoObjectify_Ps_Where()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        // ps is a known command — auto-objectify injects objectify when piped
        // No explicit "| objectify |" needed
        var (stdout, _, exitCode) = TestHelper.RunRush("ps -ef | where CMD =~ launchd | first 1 | .CMD");
        Assert.Equal(0, exitCode);
        Assert.Contains("launchd", stdout);
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void AutoObjectify_Ps_SelectCount()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        // ps with auto-objectify: select + count
        var (stdout, _, exitCode) = TestHelper.RunRush("ps -ef | select PID, CMD | count");
        Assert.Equal(0, exitCode);
        int count = int.Parse(stdout);
        Assert.True(count > 1, $"Expected more than 1 process, got {count}");
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void AutoObjectify_Ps_StandaloneNoInject()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        // ps alone (no pipe) should NOT inject objectify — just runs natively
        var (stdout, _, exitCode) = TestHelper.RunRush("ps -ef");
        Assert.Equal(0, exitCode);
        Assert.Contains("PID", stdout); // raw text header
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Section 5: String Methods ────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void StringMethod_Strip()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts \"  hello  \".strip");
        Assert.Equal("hello", stdout);
    }

    [Fact]
    public void StringMethod_Upcase()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts \"hello\".upcase");
        Assert.Equal("HELLO", stdout);
    }

    [Fact]
    public void StringMethod_Split()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts \"hello world\".split(\" \")");
        Assert.Contains("hello", stdout);
        Assert.Contains("world", stdout);
    }

    [Fact]
    public void StringMethod_Include()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts \"hello\".include?(\"ell\")");
        Assert.Equal("True", stdout);
    }

    [Fact]
    public void StringMethod_IncludeFalse()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts \"hello\".include?(\"xyz\")");
        Assert.Equal("False", stdout);
    }

    [Fact]
    public void StringMethod_StartWith()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts \"hello\".start_with?(\"hel\")");
        Assert.Equal("True", stdout);
    }

    [Fact]
    public void StringMethod_Replace()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts \"hello\".replace(\"l\", \"L\")");
        Assert.Equal("heLLo", stdout);
    }

    [Fact]
    public void StringMethod_Red()
    {
        // .red adds ANSI escape codes — just verify it doesn't error
        var (stdout, _, exitCode) = TestHelper.RunRush("puts \"error\".red");
        Assert.Equal(0, exitCode);
        Assert.Contains("error", stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Section 6: File & Dir Stdlib ─────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void FileStdlib_Read()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), "rush_test_read_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllText(tmpFile, "test data");
            var (stdout, _, _) = TestHelper.RunRush($"puts File.read(\"{TestHelper.RushPath(tmpFile)}\")");
            Assert.Contains("test data", stdout);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void FileStdlib_Write()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), "rush_test_write_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            TestHelper.RunRush($"File.write(\"{TestHelper.RushPath(tmpFile)}\", \"hello\")");
            Assert.True(File.Exists(tmpFile));
            Assert.Contains("hello", File.ReadAllText(tmpFile));
        }
        finally { if (File.Exists(tmpFile)) File.Delete(tmpFile); }
    }

    [Fact]
    public void FileStdlib_Append()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), "rush_test_append_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllText(tmpFile, "hello");
            TestHelper.RunRush($"File.append(\"{TestHelper.RushPath(tmpFile)}\", \"world\")");
            var content = File.ReadAllText(tmpFile);
            Assert.Contains("hello", content);
            Assert.Contains("world", content);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void FileStdlib_Exist()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), "rush_test_exist_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllText(tmpFile, "data");
            var (stdout, _, _) = TestHelper.RunRush($"puts File.exist?(\"{TestHelper.RushPath(tmpFile)}\")");
            Assert.Equal("True", stdout);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void FileStdlib_ExistFalse()
    {
        var nonexistent = TestHelper.RushPath(Path.Combine(Path.GetTempPath(), "rush_nonexistent_file_12345"));
        var (stdout, _, _) = TestHelper.RunRush($"puts File.exist?(\"{nonexistent}\")");
        Assert.Equal("False", stdout);
    }

    [Fact]
    public void FileStdlib_Size()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), "rush_test_size_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllText(tmpFile, "12345");
            var (stdout, _, _) = TestHelper.RunRush($"puts File.size(\"{TestHelper.RushPath(tmpFile)}\")");
            Assert.Equal("5", stdout);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void FileStdlib_ReadJson()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), "rush_test_rjson_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpFile, "{\"key\":\"value\"}");
            var (stdout, _, exitCode) = TestHelper.RunRush($"data = File.read_json(\"{TestHelper.RushPath(tmpFile)}\")\nputs data.key");
            Assert.Equal(0, exitCode);
            Assert.Equal("value", stdout);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void DirStdlib_List()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "rush_test_dirlist_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "a.txt"), "");
            File.WriteAllText(Path.Combine(tmpDir, "b.txt"), "");
            var (stdout, _, exitCode) = TestHelper.RunRush($"for f in Dir.list(\"{TestHelper.RushPath(tmpDir)}\")\n  puts f\nend");
            Assert.Equal(0, exitCode);
            Assert.Contains("a.txt", stdout);
            Assert.Contains("b.txt", stdout);
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public void DirStdlib_ListRecursive()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "rush_test_dirlistrec_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(tmpDir, "sub"));
        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "a.txt"), "");
            File.WriteAllText(Path.Combine(tmpDir, "sub", "b.txt"), "");
            var (stdout, _, exitCode) = TestHelper.RunRush($"puts Dir.list(\"{TestHelper.RushPath(tmpDir)}\", recursive: true)");
            Assert.Equal(0, exitCode);
            Assert.Contains("a.txt", stdout);
            Assert.Contains("b.txt", stdout);
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public void DirStdlib_Mkdir()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "rush_test_mkdir_" + Guid.NewGuid().ToString("N")[..8], "sub", "deep");
        try
        {
            var (_, _, exitCode) = TestHelper.RunRush($"Dir.mkdir(\"{TestHelper.RushPath(tmpDir)}\")");
            Assert.Equal(0, exitCode);
            Assert.True(Directory.Exists(tmpDir));
        }
        finally
        {
            // Clean up root
            var root = Path.GetDirectoryName(Path.GetDirectoryName(tmpDir));
            if (root != null && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Section 7: Duration Literals ─────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Duration_Seconds()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts 2.seconds");
        Assert.Equal("00:00:02", stdout);
    }

    [Fact]
    public void Duration_Minutes()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts 5.minutes");
        Assert.Equal("00:05:00", stdout);
    }

    [Fact]
    public void Duration_Arithmetic()
    {
        var (stdout, _, _) = TestHelper.RunRush("puts 1.hours + 30.minutes");
        Assert.Equal("01:30:00", stdout);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Section 8: Loops ─────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Loop_ForInArray()
    {
        // Doc: for x in [1, 2, 3] \n puts x \n end
        var (stdout, _, exitCode) = TestHelper.RunRush("for x in [1, 2, 3]\n  puts x\nend");
        Assert.Equal(0, exitCode);
        Assert.Contains("1", stdout);
        Assert.Contains("2", stdout);
        Assert.Contains("3", stdout);
    }

    [Fact]
    public void Loop_ForInDirList()
    {
        // Doc: for f in Dir.list(".") \n puts f \n end
        var tmpDir = Path.Combine(Path.GetTempPath(), "rush_test_loop_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "x.txt"), "");
            File.WriteAllText(Path.Combine(tmpDir, "y.txt"), "");
            var (stdout, _, exitCode) = TestHelper.RunRush($"for f in Dir.list(\"{TestHelper.RushPath(tmpDir)}\")\n  puts f\nend");
            Assert.Equal(0, exitCode);
            Assert.Contains("x.txt", stdout);
            Assert.Contains("y.txt", stdout);
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Section 9: Platform Blocks ───────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void PlatformBlock_Macos()
    {
        // macos block should execute on macOS, skip on Linux
        var (stdout, _, exitCode) = TestHelper.RunRush("macos\n  echo \"on mac\"\nend");
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(0, exitCode);
            Assert.Equal("on mac", stdout);
        }
        else
        {
            // On non-macOS, the block should be skipped
            Assert.Equal(0, exitCode);
        }
    }

    [Fact]
    public void PlatformBlock_Linux()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("linux\n  echo \"on linux\"\nend");
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(0, exitCode);
            Assert.Equal("on linux", stdout);
        }
        else
        {
            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("on linux", stdout);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Section 10: Side by Side ────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════
    // These verify the exact Rush examples from the comparison table.
    // Many overlap with earlier sections — the point is the doc examples work.

    [Fact]
    [Trait("Category", "Unix")]
    public void SideBySide_FilterProcesses()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        // Doc: ps aux | where COMMAND =~ chrome (auto-objectify)
        // We use a term we know exists: rush itself
        var (stdout, _, exitCode) = TestHelper.RunRush("ps aux | where COMMAND =~ rush | count");
        Assert.Equal(0, exitCode);
        int count = int.Parse(stdout);
        Assert.True(count >= 1, $"Expected at least 1 rush process, got {count}");
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void SideBySide_CountFiles()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        // Doc: ls | count
        var (stdout, _, exitCode) = TestHelper.RunRush("ls /tmp | count");
        Assert.Equal(0, exitCode);
        int count = int.Parse(stdout);
        Assert.True(count >= 0);
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void SideBySide_First5()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        // Doc: | first 5
        var (stdout, _, exitCode) = TestHelper.RunRush("printf 'a\\nb\\nc\\nd\\ne\\nf\\ng\\n' | first 5");
        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.Equal("a", lines[0]);
        Assert.Equal("e", lines[4]);
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void SideBySide_Distinct()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        // Doc: | distinct
        var (stdout, _, exitCode) = TestHelper.RunRush("printf 'a\\nb\\na\\nc\\nb\\n' | distinct");
        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void SideBySide_ParseJson()
    {
        // Doc: File.read_json("f").name
        var tmpFile = Path.Combine(Path.GetTempPath(), "rush_test_sbs_json_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpFile, "{\"name\":\"alice\"}");
            var (stdout, _, exitCode) = TestHelper.RunRush($"puts File.read_json(\"{TestHelper.RushPath(tmpFile)}\").name");
            Assert.Equal(0, exitCode);
            Assert.Equal("alice", stdout);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void SideBySide_CheckFileExists()
    {
        // Doc: File.exist?("file")
        var tmpFile = Path.Combine(Path.GetTempPath(), "rush_test_sbs_exist_" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(tmpFile, "");
        try
        {
            var (stdout, _, _) = TestHelper.RunRush($"puts File.exist?(\"{TestHelper.RushPath(tmpFile)}\")");
            Assert.Equal("True", stdout);
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void SideBySide_StringInterpolation()
    {
        // Doc: "Hello #{name}"
        var (stdout, _, _) = TestHelper.RunRush("name = \"world\"\nputs \"Hello #{name}\"");
        Assert.Equal("Hello world", stdout);
    }

    [Fact]
    public void SideBySide_IfStatement()
    {
        // Doc: if x > 5 ... end
        var (stdout, _, _) = TestHelper.RunRush("x = 10\nif x > 5\n  puts \"yes\"\nend");
        Assert.Equal("yes", stdout);
    }

    [Fact]
    public void SideBySide_ForLoop()
    {
        // Doc: for x in [1,2,3] ... end
        var (stdout, _, exitCode) = TestHelper.RunRush("for x in [1,2,3]\n  puts x\nend");
        Assert.Equal(0, exitCode);
        Assert.Contains("1", stdout);
        Assert.Contains("2", stdout);
        Assert.Contains("3", stdout);
    }

    [Fact]
    public void SideBySide_LoopOverFiles()
    {
        // Doc: for f in Dir.list(".") ... end
        var tmpDir = Path.Combine(Path.GetTempPath(), "rush_test_sbs_loop_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "m.txt"), "");
            var (stdout, _, exitCode) = TestHelper.RunRush($"for f in Dir.list(\"{TestHelper.RushPath(tmpDir)}\")\n  puts f\nend");
            Assert.Equal(0, exitCode);
            Assert.Contains("m.txt", stdout);
        }
        finally { Directory.Delete(tmpDir, true); }
    }

    [Fact]
    public void SideBySide_SumColumn()
    {
        // Doc: | sum ColumnName
        var tmpFile = Path.Combine(Path.GetTempPath(), "rush_test_sbs_sum_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(tmpFile, "[{\"v\":10},{\"v\":20},{\"v\":30}]");
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{TestHelper.RushPath(tmpFile)}\" | from json | sum v");
            Assert.Equal(0, exitCode);
            Assert.Equal("60", stdout);
        }
        finally { File.Delete(tmpFile); }
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Section 11: Configuration ───────────────────────────────────
    // ══════════════════════════════════════════════════════════════════
    // Most config commands are REPL-only (set, path). We test what works via rush -c.

    [Fact]
    public void Config_SetVi()
    {
        // "set vi" should not error (even in non-interactive mode)
        var (_, _, exitCode) = TestHelper.RunRush("set vi");
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Config_SetEmacs()
    {
        var (_, _, exitCode) = TestHelper.RunRush("set emacs");
        Assert.Equal(0, exitCode);
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Config_PathShow_IsReplOnly()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        // "path" is a REPL-only builtin — not available via rush -c
        var (_, stderr, exitCode) = TestHelper.RunRush("path");
        Assert.Equal(1, exitCode);
        Assert.Contains("not found", stderr);
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Config_PathCheck_IsReplOnly()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        // "path check" is a REPL-only builtin — not available via rush -c
        var (_, stderr, exitCode) = TestHelper.RunRush("path check");
        Assert.Equal(1, exitCode);
        Assert.Contains("not found", stderr);
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Section 12: Gotchas ─────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Gotcha_NoDollarOnVariables()
    {
        // "name = 'world'" not "$name = 'world'"
        var (stdout, _, exitCode) = TestHelper.RunRush("name = \"world\"\nputs name");
        Assert.Equal(0, exitCode);
        Assert.Equal("world", stdout);
    }

    [Fact]
    public void Gotcha_HashInterpolation()
    {
        // #{} not ${}
        var (stdout, _, _) = TestHelper.RunRush("name = \"world\"\nputs \"Hello #{name}\"");
        Assert.Equal("Hello world", stdout);
    }

    [Fact]
    public void Gotcha_EndNotFi()
    {
        // blocks close with 'end'
        var (stdout, _, exitCode) = TestHelper.RunRush("if true\n  puts \"yes\"\nend");
        Assert.Equal(0, exitCode);
        Assert.Equal("yes", stdout);
    }

    [Fact]
    public void Gotcha_PutsForExpressions()
    {
        // "puts" handles rush expressions, "echo" is for simple strings
        var (stdout, _, _) = TestHelper.RunRush("arr = [1, 2, 3]\nputs arr.length");
        Assert.Equal("3", stdout);
    }

    [Fact]
    public void Gotcha_SemicolonsAsNewlines()
    {
        // Doc: "if x > 5; puts 'yes'; end" on one line
        var (stdout, _, _) = TestHelper.RunRush("x = 10; if x > 5; puts \"yes\"; end");
        Assert.Equal("yes", stdout);
    }
}
