using Rush;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for TabCompleter's static helper methods:
/// ExtractToken, ExtractFirstWord, FindTokenEnd.
/// These are pure functions — no PowerShell runtime needed.
/// </summary>
public class TabCompleterHelperTests
{
    // ── ExtractToken ────────────────────────────────────────────────────

    [Fact]
    public void ExtractToken_CursorAtEnd_ReturnsLastToken()
    {
        var (token, start) = TabCompleter.ExtractToken("ls -la /tmp", 11);
        Assert.Equal("/tmp", token);
        Assert.Equal(7, start);
    }

    [Fact]
    public void ExtractToken_CursorAtFirstWord_ReturnsFirstWord()
    {
        var (token, start) = TabCompleter.ExtractToken("ls", 2);
        Assert.Equal("ls", token);
        Assert.Equal(0, start);
    }

    [Fact]
    public void ExtractToken_CursorAtMiddle_ReturnsPartialToken()
    {
        var (token, start) = TabCompleter.ExtractToken("grep patt file.txt", 9);
        Assert.Equal("patt", token);
        Assert.Equal(5, start);
    }

    [Fact]
    public void ExtractToken_CursorAfterSpace_ReturnsEmpty()
    {
        var (token, start) = TabCompleter.ExtractToken("ls ", 3);
        Assert.Equal("", token);
        Assert.Equal(3, start);
    }

    [Fact]
    public void ExtractToken_EmptyInput_ReturnsEmpty()
    {
        var (token, start) = TabCompleter.ExtractToken("", 0);
        Assert.Equal("", token);
        Assert.Equal(0, start);
    }

    [Fact]
    public void ExtractToken_CursorZero_ReturnsEmpty()
    {
        var (token, start) = TabCompleter.ExtractToken("hello", 0);
        Assert.Equal("", token);
        Assert.Equal(0, start);
    }

    [Fact]
    public void ExtractToken_FlagAtCursor_ReturnsFlag()
    {
        var (token, start) = TabCompleter.ExtractToken("ls -l", 5);
        Assert.Equal("-l", token);
        Assert.Equal(3, start);
    }

    [Fact]
    public void ExtractToken_EnvVar_ReturnsWithDollar()
    {
        var (token, start) = TabCompleter.ExtractToken("echo $HO", 8);
        Assert.Equal("$HO", token);
        Assert.Equal(5, start);
    }

    [Fact]
    public void ExtractToken_PathWithSlash_ReturnsFullPath()
    {
        var (token, start) = TabCompleter.ExtractToken("cat /usr/lo", 11);
        Assert.Equal("/usr/lo", token);
        Assert.Equal(4, start);
    }

    [Fact]
    public void ExtractToken_CursorBeyondInput_Clamped()
    {
        var (token, start) = TabCompleter.ExtractToken("ls", 100);
        Assert.Equal("ls", token);
        Assert.Equal(0, start);
    }

    // ── ExtractFirstWord ────────────────────────────────────────────────

    [Fact]
    public void ExtractFirstWord_SimpleCommand_ReturnsCommand()
    {
        Assert.Equal("grep", TabCompleter.ExtractFirstWord("grep pattern"));
    }

    [Fact]
    public void ExtractFirstWord_NoArgs_ReturnsFullText()
    {
        Assert.Equal("ls", TabCompleter.ExtractFirstWord("ls"));
    }

    [Fact]
    public void ExtractFirstWord_NoLeadingSpaces_ReturnsCommand()
    {
        // ExtractFirstWord doesn't trim leading spaces (by design — its callers always TrimEnd)
        Assert.Equal("cat", TabCompleter.ExtractFirstWord("cat file.txt"));
    }

    [Fact]
    public void ExtractFirstWord_AfterPipe_ReturnsPipeCommand()
    {
        // Pipe-aware: returns command after last pipe
        Assert.Equal("grep", TabCompleter.ExtractFirstWord("ls | grep"));
    }

    [Fact]
    public void ExtractFirstWord_MultiplePipes_ReturnsLastCommand()
    {
        Assert.Equal("wc", TabCompleter.ExtractFirstWord("cat file | sort | wc"));
    }

    [Fact]
    public void ExtractFirstWord_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", TabCompleter.ExtractFirstWord(""));
    }

    [Fact]
    public void ExtractFirstWord_PipeWithSpaces_ReturnsCorrectCommand()
    {
        Assert.Equal("grep", TabCompleter.ExtractFirstWord("ls -la | grep"));
    }

    // ── FindTokenEnd ────────────────────────────────────────────────────

    [Fact]
    public void FindTokenEnd_FindsSpace()
    {
        Assert.Equal(2, TabCompleter.FindTokenEnd("ls -la", 0));
    }

    [Fact]
    public void FindTokenEnd_AtEnd_ReturnsLength()
    {
        Assert.Equal(5, TabCompleter.FindTokenEnd("hello", 0));
    }

    [Fact]
    public void FindTokenEnd_MiddleToken()
    {
        // Starting at position 3 ('-'), scans to position 6 (space before '/tmp')
        Assert.Equal(6, TabCompleter.FindTokenEnd("ls -la /tmp", 3));
    }

    [Fact]
    public void FindTokenEnd_EmptyString()
    {
        Assert.Equal(0, TabCompleter.FindTokenEnd("", 0));
    }

    [Fact]
    public void FindTokenEnd_StartAtSpace_ReturnsStart()
    {
        Assert.Equal(2, TabCompleter.FindTokenEnd("ls -la", 2));
    }

    [Fact]
    public void FindTokenEnd_PathWithSlashes()
    {
        Assert.Equal(12, TabCompleter.FindTokenEnd("/usr/local/b file", 0));
    }

    // ── Quote-Aware ExtractToken ─────────────────────────────────────

    [Fact]
    public void ExtractToken_InsideQuotes_ReturnsFromOpeningQuote()
    {
        // rm "Boo — cursor at end, inside open quote
        var (token, start) = TabCompleter.ExtractToken("rm \"Boo", 7);
        Assert.Equal("\"Boo", token);
        Assert.Equal(3, start);
    }

    [Fact]
    public void ExtractToken_InsideQuotesWithSpace_ReturnsFullQuotedToken()
    {
        // rm "Book 2 — cursor at end, inside open quote with space
        var (token, start) = TabCompleter.ExtractToken("rm \"Book 2", 10);
        Assert.Equal("\"Book 2", token);
        Assert.Equal(3, start);
    }

    [Fact]
    public void ExtractToken_ClosedQuotes_StandardWalkBack()
    {
        // rm "Book 2.xlsx" — cursor at end, quotes are closed (even count)
        // Standard walk-back treats it as unquoted — finds space inside the string
        // This is fine: closed quotes mean the user is done completing
        var (token, start) = TabCompleter.ExtractToken("rm \"Book 2.xlsx\"", 16);
        Assert.Equal("2.xlsx\"", token);
        Assert.Equal(9, start);
    }

    // ── Quote-Aware FindTokenEnd ─────────────────────────────────────

    [Fact]
    public void FindTokenEnd_QuotedToken_FindsClosingQuote()
    {
        Assert.Equal(16, TabCompleter.FindTokenEnd("rm \"Book 2.xlsx\" next", 3));
    }

    [Fact]
    public void FindTokenEnd_UnclosedQuote_ReturnsEnd()
    {
        Assert.Equal(10, TabCompleter.FindTokenEnd("rm \"Book 2", 3));
    }
}
