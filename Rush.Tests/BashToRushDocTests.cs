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

    // ══════════════════════════════════════════════════════════════════
    // ── Section 3: Pipeline Operators ────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    // Count

    [Fact]
    public void Pipeline_Count()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("printf \"a\\nb\\nc\\n\" | count");
        Assert.Equal(0, exitCode);
        Assert.Equal("3", stdout);
    }

    // First / Last / Skip

    [Fact]
    public void Pipeline_First()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("printf \"a\\nb\\nc\\nd\\ne\\n\" | first 2");
        Assert.Equal(0, exitCode);
        Assert.Contains("a", stdout);
        Assert.Contains("b", stdout);
        Assert.DoesNotContain("e", stdout);
    }

    [Fact]
    public void Pipeline_Last()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("printf \"a\\nb\\nc\\nd\\ne\\n\" | last 2");
        Assert.Equal(0, exitCode);
        Assert.Contains("d", stdout);
        Assert.Contains("e", stdout);
        Assert.DoesNotContain("a", stdout);
    }

    [Fact]
    public void Pipeline_Skip()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("printf \"a\\nb\\nc\\nd\\n\" | skip 2");
        Assert.Equal(0, exitCode);
        Assert.Contains("c", stdout);
        Assert.Contains("d", stdout);
        Assert.DoesNotContain("a", stdout);
    }

    [Fact]
    public void Pipeline_SkipThenFirst()
    {
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
    public void Pipeline_Sort()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("printf \"c\\na\\nb\\n\" | sort");
        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("a", lines[0]);
        Assert.Equal("b", lines[1]);
        Assert.Equal("c", lines[2]);
    }

    [Fact]
    public void Pipeline_SortReverse()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("printf \"a\\nb\\nc\\n\" | sort -r");
        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("c", lines[0]);
        Assert.Equal("b", lines[1]);
        Assert.Equal("a", lines[2]);
    }

    // Distinct

    [Fact]
    public void Pipeline_Distinct()
    {
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
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{tmpJson}\" | from json | where v > 10 | .n");
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
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{tmpJson}\" | from json | where v < 10 | .n");
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
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{tmpJson}\" | from json | where n == b | .v");
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
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{tmpJson}\" | from json | select name | as json");
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
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{tmpJson}\" | from json | .name");
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
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{tmpJson}\" | from json | sum v");
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
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{tmpJson}\" | from json | avg v");
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
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{tmpJson}\" | from json | min v");
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
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{tmpJson}\" | from json | max v");
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
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{tmpJson}\" | from json | as json");
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
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{tmpJson}\" | from json | as csv");
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
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{tmpJson}\" | from json | .x");
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
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{tmpCsv}\" | from csv | .name");
            Assert.Equal(0, exitCode);
            Assert.Contains("a", stdout);
            Assert.Contains("b", stdout);
        }
        finally { File.Delete(tmpCsv); }
    }

    // Tee

    [Fact]
    public void Pipeline_Tee()
    {
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
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{tmpJson}\" | from json | sort n | .n");
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
            var (stdout, _, exitCode) = TestHelper.RunRush($"cat \"{tmpJson}\" | from json | distinct n | .n");
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
}
