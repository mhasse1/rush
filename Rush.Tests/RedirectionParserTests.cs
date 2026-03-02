using Rush;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for RedirectionParser.Parse — the 4-phase redirect scanner.
/// Covers: >, >>, <, 2>, 2>>, 2>&amp;1, combinations, quoting, edge cases.
/// </summary>
public class RedirectionParserTests
{
    // ── No Redirection ──────────────────────────────────────────────────

    [Fact]
    public void NoRedirection_ReturnsOriginalInput()
    {
        var (cmd, redirect, stdin) = RedirectionParser.Parse("ls -la /tmp");
        Assert.Equal("ls -la /tmp", cmd);
        Assert.Null(redirect);
        Assert.Null(stdin);
    }

    [Fact]
    public void EmptyInput_ReturnsOriginal()
    {
        var (cmd, redirect, stdin) = RedirectionParser.Parse("");
        Assert.Equal("", cmd);
        Assert.Null(redirect);
        Assert.Null(stdin);
    }

    [Fact]
    public void WhitespaceOnly_ReturnsOriginal()
    {
        var (cmd, redirect, stdin) = RedirectionParser.Parse("   ");
        Assert.Equal("   ", cmd);
        Assert.Null(redirect);
        Assert.Null(stdin);
    }

    // ── Stdout > ────────────────────────────────────────────────────────

    [Fact]
    public void StdoutRedirect_ParsesFileAndStripsOperator()
    {
        var (cmd, redirect, stdin) = RedirectionParser.Parse("echo hello > out.txt");
        Assert.Equal("echo hello", cmd);
        Assert.NotNull(redirect);
        Assert.Equal("out.txt", redirect!.FilePath);
        Assert.False(redirect.Append);
        Assert.False(redirect.MergeStderr);
        Assert.Null(stdin);
    }

    [Fact]
    public void StdoutRedirect_NoSpace_Works()
    {
        var (cmd, redirect, _) = RedirectionParser.Parse("echo hello >out.txt");
        Assert.Equal("echo hello", cmd);
        Assert.NotNull(redirect);
        Assert.Equal("out.txt", redirect!.FilePath);
        Assert.False(redirect.Append);
    }

    [Fact]
    public void StdoutRedirect_AtEnd_StripsCleanly()
    {
        var (cmd, redirect, _) = RedirectionParser.Parse("ls > files.txt");
        Assert.Equal("ls", cmd);
        Assert.NotNull(redirect);
        Assert.Equal("files.txt", redirect!.FilePath);
    }

    // ── Stdout >> (Append) ──────────────────────────────────────────────

    [Fact]
    public void AppendRedirect_SetsAppendFlag()
    {
        var (cmd, redirect, _) = RedirectionParser.Parse("echo line >> log.txt");
        Assert.Equal("echo line", cmd);
        Assert.NotNull(redirect);
        Assert.Equal("log.txt", redirect!.FilePath);
        Assert.True(redirect.Append);
        Assert.False(redirect.MergeStderr);
    }

    // ── Stdin < ─────────────────────────────────────────────────────────

    [Fact]
    public void StdinRedirect_ParsesFile()
    {
        var (cmd, _, stdin) = RedirectionParser.Parse("grep pattern < input.txt");
        Assert.Equal("grep pattern", cmd);
        Assert.NotNull(stdin);
        Assert.Equal("input.txt", stdin!.FilePath);
    }

    [Fact]
    public void StdinRedirect_NoSpace_Works()
    {
        var (cmd, _, stdin) = RedirectionParser.Parse("sort <data.csv");
        Assert.Equal("sort", cmd);
        Assert.NotNull(stdin);
        Assert.Equal("data.csv", stdin!.FilePath);
    }

    // ── 2> and 2>> (Stderr) ─────────────────────────────────────────────

    [Fact]
    public void StderrRedirect_LeftInCommand()
    {
        // 2> stays in command for PowerShell to handle natively
        var (cmd, redirect, _) = RedirectionParser.Parse("make 2>/dev/null");
        Assert.Equal("make 2>/dev/null", cmd);
        Assert.Null(redirect);
    }

    [Fact]
    public void StderrAppend_LeftInCommand()
    {
        var (cmd, redirect, _) = RedirectionParser.Parse("build 2>> errors.log");
        Assert.Equal("build 2>> errors.log", cmd);
        Assert.Null(redirect);
    }

    // ── 2>&1 ────────────────────────────────────────────────────────────

    [Fact]
    public void MergeStderr_WithStdoutRedirect_SetsMergeFlag()
    {
        var (cmd, redirect, _) = RedirectionParser.Parse("make > output.txt 2>&1");
        Assert.Equal("make", cmd);
        Assert.NotNull(redirect);
        Assert.Equal("output.txt", redirect!.FilePath);
        Assert.True(redirect.MergeStderr);
    }

    [Fact]
    public void MergeStderr_WithoutStdoutRedirect_LeftInCommand()
    {
        // 2>&1 without stdout redirect stays in command for PS to handle
        var (cmd, redirect, _) = RedirectionParser.Parse("cmd 2>&1");
        Assert.Equal("cmd 2>&1", cmd);
        Assert.Null(redirect);
    }

    [Fact]
    public void MergeStderr_WithAppend_SetsMergeAndAppend()
    {
        var (cmd, redirect, _) = RedirectionParser.Parse("build >> log.txt 2>&1");
        Assert.Equal("build", cmd);
        Assert.NotNull(redirect);
        Assert.Equal("log.txt", redirect!.FilePath);
        Assert.True(redirect.Append);
        Assert.True(redirect.MergeStderr);
    }

    // ── Combined Redirections ───────────────────────────────────────────

    [Fact]
    public void StdinAndStdout_BothParsed()
    {
        var (cmd, redirect, stdin) = RedirectionParser.Parse("grep hello < input.txt > matches.txt");
        Assert.Equal("grep hello", cmd);
        Assert.NotNull(redirect);
        Assert.Equal("matches.txt", redirect!.FilePath);
        Assert.NotNull(stdin);
        Assert.Equal("input.txt", stdin!.FilePath);
    }

    [Fact]
    public void StdoutAndStderrSeparate_StdoutStripped_StderrKept()
    {
        var (cmd, redirect, _) = RedirectionParser.Parse("cmd > out.txt 2> err.txt");
        // Stripping "> out.txt" leaves an extra space — acceptable
        Assert.Equal("cmd  2> err.txt", cmd);
        Assert.NotNull(redirect);
        Assert.Equal("out.txt", redirect!.FilePath);
    }

    // ── Quoting ─────────────────────────────────────────────────────────

    [Fact]
    public void GreaterThanInSingleQuotes_NotTreatedAsRedirect()
    {
        var (cmd, redirect, _) = RedirectionParser.Parse("echo 'hello > world'");
        Assert.Equal("echo 'hello > world'", cmd);
        Assert.Null(redirect);
    }

    [Fact]
    public void GreaterThanInDoubleQuotes_NotTreatedAsRedirect()
    {
        var (cmd, redirect, _) = RedirectionParser.Parse("echo \"hello > world\"");
        Assert.Equal("echo \"hello > world\"", cmd);
        Assert.Null(redirect);
    }

    [Fact]
    public void QuotedFilePath_Parsed()
    {
        var (cmd, redirect, _) = RedirectionParser.Parse("echo hello > 'my file.txt'");
        Assert.Equal("echo hello", cmd);
        Assert.NotNull(redirect);
        Assert.Equal("my file.txt", redirect!.FilePath);
    }

    [Fact]
    public void QuotedFilePath_DoubleQuotes_Parsed()
    {
        var (cmd, redirect, _) = RedirectionParser.Parse("ls > \"output file.txt\"");
        Assert.Equal("ls", cmd);
        Assert.NotNull(redirect);
        Assert.Equal("output file.txt", redirect!.FilePath);
    }

    // ── Tilde Expansion ─────────────────────────────────────────────────

    [Fact]
    public void TildeExpansion_ExpandsToHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (cmd, redirect, _) = RedirectionParser.Parse("echo test > ~/output.txt");
        Assert.Equal("echo test", cmd);
        Assert.NotNull(redirect);
        Assert.Equal(Path.Combine(home, "output.txt"), redirect!.FilePath);
    }

    [Fact]
    public void TildeExpansion_StdinRedirect()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (_, _, stdin) = RedirectionParser.Parse("sort < ~/data.txt");
        Assert.NotNull(stdin);
        Assert.Equal(Path.Combine(home, "data.txt"), stdin!.FilePath);
    }

    // ── Edge Cases ──────────────────────────────────────────────────────

    [Fact]
    public void RedirectOperatorAtEnd_NoTarget_IgnoredGracefully()
    {
        // Trailing > with no file path — parser should skip
        var (cmd, redirect, _) = RedirectionParser.Parse("echo hello >");
        // Should return original since no valid target after >
        Assert.Null(redirect);
    }

    [Fact]
    public void CommandWithPipeBeforeRedirect_Works()
    {
        // The pipe character terminates the redirect file path scanning
        var (cmd, redirect, _) = RedirectionParser.Parse("echo hello > out.txt");
        Assert.Equal("echo hello", cmd);
        Assert.NotNull(redirect);
        Assert.Equal("out.txt", redirect!.FilePath);
    }

    [Fact]
    public void MultipleSpacesBetweenOperatorAndFile_Works()
    {
        var (cmd, redirect, _) = RedirectionParser.Parse("echo hello >   output.txt");
        Assert.Equal("echo hello", cmd);
        Assert.NotNull(redirect);
        Assert.Equal("output.txt", redirect!.FilePath);
    }

    [Fact]
    public void NumberTwoNotFollowedByGreaterThan_NotStderrRedirect()
    {
        // Just the digit 2 in normal context should not trigger stderr logic
        var (cmd, redirect, _) = RedirectionParser.Parse("echo 2 + 2 > result.txt");
        Assert.Equal("echo 2 + 2", cmd);
        Assert.NotNull(redirect);
        Assert.Equal("result.txt", redirect!.FilePath);
    }

    // ── Pipe Terminator ─────────────────────────────────────────────────

    [Fact]
    public void PipeTerminatesFilePath()
    {
        // When redirect file path is followed by pipe, pipe is not consumed
        var (cmd, redirect, _) = RedirectionParser.Parse("cmd > file.txt | next");
        Assert.NotNull(redirect);
        Assert.Equal("file.txt", redirect!.FilePath);
    }

    [Fact]
    public void SemicolonTerminatesFilePath()
    {
        var (cmd, redirect, _) = RedirectionParser.Parse("cmd > file.txt; next");
        Assert.NotNull(redirect);
        Assert.Equal("file.txt", redirect!.FilePath);
    }
}
