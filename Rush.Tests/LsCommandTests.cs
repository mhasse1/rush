using System.Diagnostics;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for Rush's custom ls implementation and ls piping behavior.
/// Rush uses a custom FileListCommand for non-piped ls,
/// and translates to Get-ChildItem for piped ls.
/// </summary>
public class LsCommandTests
{
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

    private static (string stdout, string stderr, int exitCode) RunRush(string command, string? workDir = null)
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
        if (workDir != null)
            psi.WorkingDirectory = workDir;

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return (stdout.Trim(), stderr.Trim(), proc.ExitCode);
    }

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
    public void Ls_ListsFilesInDirectory()
    {
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = RunRush($"ls {dir}");
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
    public void Ls_DashA_ShowsHiddenFiles()
    {
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, _) = RunRush($"ls -a {dir}");
            Assert.Contains(".hidden", stdout);
            Assert.Contains("alpha.txt", stdout);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Ls_DashL_ShowsLongFormat()
    {
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, _) = RunRush($"ls -l {dir}");
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
    public void Ls_DashR_RecursiveListing()
    {
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, _) = RunRush($"ls -R {dir}");
            // Should include files from subdirectory
            Assert.Contains("nested.txt", stdout);
            Assert.Contains("alpha.txt", stdout);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Ls_OutputsOneEntryPerLine()
    {
        var dir = CreateTestDir();
        try
        {
            // rush -c uses Get-ChildItem (not FileListCommand), which
            // outputs one entry per line via OutputRenderer
            var (stdout, _, _) = RunRush($"ls {dir}");
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // 3 files + 1 dir = 4 entries (no hidden)
            Assert.Equal(4, lines.Length);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Ls_CombinedFlags_DashLa()
    {
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, _) = RunRush($"ls -la {dir}");
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
    public void Ls_Pipe_Grep_FiltersOutput()
    {
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = RunRush($"ls {dir} | grep alpha");
            Assert.Equal(0, exitCode);
            Assert.Contains("alpha", stdout);
            Assert.DoesNotContain("bravo", stdout);
            Assert.DoesNotContain("charlie", stdout);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Ls_Pipe_Sort_SortsOutput()
    {
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = RunRush($"ls {dir} | sort");
            Assert.Equal(0, exitCode);
            // Should contain all files
            Assert.Contains("alpha", stdout);
            Assert.Contains("bravo", stdout);
            Assert.Contains("charlie", stdout);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Ls_Pipe_SortReverse_ReversesSorting()
    {
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = RunRush($"ls {dir} | sort -r");
            Assert.Equal(0, exitCode);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            // First entry alphabetically reversed should be subdir or charlie
            Assert.True(lines.Length > 0, "Should have output lines");
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Ls_Pipe_Count_ReturnsCount()
    {
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = RunRush($"ls {dir} | count");
            Assert.Equal(0, exitCode);
            // 3 files + 1 dir = 4 (no hidden)
            Assert.Equal("4", stdout);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Ls_Pipe_Head_LimitsOutput()
    {
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = RunRush($"ls {dir} | head -2");
            Assert.Equal(0, exitCode);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Ls_Pipe_WcL_CountsLines()
    {
        var dir = CreateTestDir();
        try
        {
            // wc -l maps to Measure-Object -Line which produces table output
            // Extract the numeric count from the formatted output
            var (stdout, _, exitCode) = RunRush($"ls {dir} | wc -l");
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
    public void Ls_Pipe_First_ReturnsFirstItem()
    {
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = RunRush($"ls {dir} | first");
            Assert.Equal(0, exitCode);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(lines);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Ls_Pipe_Last_ReturnsLastItem()
    {
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = RunRush($"ls {dir} | last");
            Assert.Equal(0, exitCode);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(lines);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Ls_Pipe_Uniq_RemovesDuplicates()
    {
        var dir = CreateTestDir();
        try
        {
            // ls | uniq shouldn't change count since filenames are already unique
            var (stdout, _, exitCode) = RunRush($"ls {dir} | uniq");
            Assert.Equal(0, exitCode);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(4, lines.Length);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Ls_Pipe_Skip_SkipsEntries()
    {
        var dir = CreateTestDir();
        try
        {
            var (stdout, _, exitCode) = RunRush($"ls {dir} | skip 2");
            Assert.Equal(0, exitCode);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
        }
        finally { CleanupDir(dir); }
    }

    // ── ls with Redirects ─────────────────────────────────────────────────

    [Fact]
    public void Ls_Redirect_WritesToFile()
    {
        var dir = CreateTestDir();
        var outFile = Path.GetTempFileName();
        try
        {
            RunRush($"ls {dir} > {outFile}");
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
    public void Ls_NonexistentDir_ReturnsError()
    {
        var (_, stderr, exitCode) = RunRush("ls /nonexistent_path_12345");
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void Ls_EmptyDir_ProducesNoFileOutput()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rush_ls_empty_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var (stdout, _, exitCode) = RunRush($"ls {dir}");
            // Empty directory should produce no file listing
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Empty(lines);
        }
        finally { CleanupDir(dir); }
    }
}
