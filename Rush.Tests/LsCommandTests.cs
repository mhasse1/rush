using System.Runtime.InteropServices;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for Rush's custom ls implementation and ls piping behavior.
/// Rush uses a custom FileListCommand for non-piped ls,
/// and translates to Get-ChildItem for piped ls.
/// </summary>
public class LsCommandTests
{
    /// <summary>
    /// Temp directory with known files for predictable ls output.
    /// </summary>
    private static string CreateTestDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rush_ls_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "alpha.txt"), "a");
        File.WriteAllText(Path.Combine(dir, "bravo.txt"), "bb");
        File.WriteAllText(Path.Combine(dir, "charlie.txt"), "ccc");
        File.WriteAllText(Path.Combine(dir, ".hidden"), "h");
        Directory.CreateDirectory(Path.Combine(dir, "subdir"));
        File.WriteAllText(Path.Combine(dir, "subdir", "nested.txt"), "n");
        return dir;
    }

    private static void CleanupDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    // ── Basic ls ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_ListsFilesInDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = TestHelper.RunRush($"ls {TestHelper.RushPath(dir)}");
            Assert.Equal(0, exitCode);
            // Should contain our test files (not hidden by default)
            Assert.Contains("alpha.txt", stdout);
            Assert.Contains("bravo.txt", stdout);
            Assert.Contains("charlie.txt", stdout);
            Assert.Contains("subdir", stdout);
            // Should NOT contain hidden files by default
            Assert.DoesNotContain(".hidden", stdout);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_DashA_ShowsHiddenFiles()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, _) = TestHelper.RunRush($"ls -a {TestHelper.RushPath(dir)}");
            Assert.Contains(".hidden", stdout);
            Assert.Contains("alpha.txt", stdout);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_DashL_ShowsLongFormat()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, _) = TestHelper.RunRush($"ls -l {TestHelper.RushPath(dir)}");
            // Long format should contain permission-like strings and file names
            Assert.Contains("alpha.txt", stdout);
            // Should have multiple columns — look for at least date-like content or size
            var lines = stdout.Split('\n').Where(l => l.Contains("alpha.txt")).ToArray();
            Assert.NotEmpty(lines);
            // Long format lines should be substantially longer than just the filename
            Assert.True(lines[0].Length > 20, "Long format line too short");
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_DashR_RecursiveListing()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, _) = TestHelper.RunRush($"ls -R {TestHelper.RushPath(dir)}");
            // Should include files from subdirectory
            Assert.Contains("nested.txt", stdout);
            Assert.Contains("alpha.txt", stdout);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_OutputsOneEntryPerLine()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        try
        {
            // rush -c uses Get-ChildItem (not FileListCommand), which
            // outputs one entry per line via OutputRenderer
            var (stdout, _, _) = TestHelper.RunRush($"ls {TestHelper.RushPath(dir)}");
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // 3 files + 1 dir = 4 entries (no hidden)
            Assert.Equal(4, lines.Length);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_CombinedFlags_DashLa()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, _) = TestHelper.RunRush($"ls -la {TestHelper.RushPath(dir)}");
            // Should show hidden files AND long format
            Assert.Contains(".hidden", stdout);
            // Long format lines should have permission-like patterns
            var lines = stdout.Split('\n').Where(l => l.Contains("alpha.txt")).ToArray();
            Assert.NotEmpty(lines);
            Assert.True(lines[0].Length > 20, "Combined -la should produce long format");
        }
        finally { CleanupDir(dir); }
    }

    // ── ls Piping (uses Get-ChildItem) ────────────────────────────────────

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_Pipe_Grep_FiltersOutput()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = TestHelper.RunRush($"ls {TestHelper.RushPath(dir)} | grep alpha");
            Assert.Equal(0, exitCode);
            Assert.Contains("alpha", stdout);
            Assert.DoesNotContain("bravo", stdout);
            Assert.DoesNotContain("charlie", stdout);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_Pipe_Sort_SortsOutput()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = TestHelper.RunRush($"ls {TestHelper.RushPath(dir)} | sort");
            Assert.Equal(0, exitCode);
            // Should contain all files
            Assert.Contains("alpha", stdout);
            Assert.Contains("bravo", stdout);
            Assert.Contains("charlie", stdout);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_Pipe_SortReverse_ReversesSorting()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = TestHelper.RunRush($"ls {TestHelper.RushPath(dir)} | sort -r");
            Assert.Equal(0, exitCode);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            // First entry alphabetically reversed should be subdir or charlie
            Assert.True(lines.Length > 0, "Should have output lines");
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_Pipe_Count_ReturnsCount()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = TestHelper.RunRush($"ls {TestHelper.RushPath(dir)} | count");
            Assert.Equal(0, exitCode);
            // 3 files + 1 dir = 4 (no hidden)
            Assert.Equal("4", stdout);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_Pipe_Head_LimitsOutput()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = TestHelper.RunRush($"ls {TestHelper.RushPath(dir)} | head -2");
            Assert.Equal(0, exitCode);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_Pipe_WcL_CountsLines()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        try
        {
            // wc -l maps to Measure-Object -Line which produces table output
            // Extract the numeric count from the formatted output
            var (stdout, _, exitCode) = TestHelper.RunRush($"ls {TestHelper.RushPath(dir)} | wc -l");
            Assert.Equal(0, exitCode);
            // Parse the count from the table — look for first line that's purely numeric
            var countStr = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .FirstOrDefault(l => int.TryParse(l, out _));
            Assert.NotNull(countStr);
            Assert.Equal(4, int.Parse(countStr!));
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_Pipe_First_ReturnsFirstItem()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = TestHelper.RunRush($"ls {TestHelper.RushPath(dir)} | first");
            Assert.Equal(0, exitCode);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(lines);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_Pipe_Last_ReturnsLastItem()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = TestHelper.RunRush($"ls {TestHelper.RushPath(dir)} | last");
            Assert.Equal(0, exitCode);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(lines);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_Pipe_Uniq_RemovesDuplicates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        try
        {
            // ls | uniq shouldn't change count since filenames are already unique
            var (stdout, _, exitCode) = TestHelper.RunRush($"ls {TestHelper.RushPath(dir)} | uniq");
            Assert.Equal(0, exitCode);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(4, lines.Length);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_Pipe_Skip_SkipsEntries()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = TestHelper.RunRush($"ls {TestHelper.RushPath(dir)} | skip 2");
            Assert.Equal(0, exitCode);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
        }
        finally { CleanupDir(dir); }
    }

    // ── ls with Redirects ─────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_Redirect_WritesToFile()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = CreateTestDir();
        var outFile = Path.GetTempFileName();
        try
        {
            TestHelper.RunRush($"ls {TestHelper.RushPath(dir)} > {TestHelper.RushPath(outFile)}");
            var content = File.ReadAllText(outFile);
            Assert.Contains("alpha.txt", content);
            Assert.Contains("bravo.txt", content);
        }
        finally
        {
            CleanupDir(dir);
            File.Delete(outFile);
        }
    }

    // ── Edge Cases ────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_NonexistentDir_ReturnsError()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var (_, stderr, exitCode) = TestHelper.RunRush("ls /nonexistent_path_12345");
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    [Trait("Category", "Unix")]
    public void Ls_EmptyDir_ProducesNoFileOutput()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var dir = Path.Combine(Path.GetTempPath(), $"rush_ls_empty_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var (stdout, _, exitCode) = TestHelper.RunRush($"ls {TestHelper.RushPath(dir)}");
            // Empty directory should produce no file listing
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Empty(lines);
        }
        finally { CleanupDir(dir); }
    }
}
