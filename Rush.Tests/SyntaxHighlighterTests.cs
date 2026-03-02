using Rush;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for SyntaxHighlighter's tokenizer — verifies correct token type
/// assignment for operators, pipes, strings, flags, words, and bangs.
/// Tests the internal Tokenize method directly via InternalsVisibleTo.
/// </summary>
public class SyntaxHighlighterTests
{
    // Helper: assert a single-token input has the expected type
    private static void AssertSingleToken(string input, SyntaxHighlighter.TokenType expectedType)
    {
        var tokens = SyntaxHighlighter.Tokenize(input);
        Assert.Single(tokens);
        Assert.Equal(expectedType, tokens[0].Type);
        Assert.Equal(input, tokens[0].Text);
    }

    // ── Operator Tokens ─────────────────────────────────────────────────

    [Fact] public void Gt_IsOperator() => AssertSingleToken(">", SyntaxHighlighter.TokenType.Operator);
    [Fact] public void GtGt_IsOperator() => AssertSingleToken(">>", SyntaxHighlighter.TokenType.Operator);
    [Fact] public void Lt_IsOperator() => AssertSingleToken("<", SyntaxHighlighter.TokenType.Operator);
    [Fact] public void Stderr_IsOperator() => AssertSingleToken("2>", SyntaxHighlighter.TokenType.Operator);
    [Fact] public void StderrAppend_IsOperator() => AssertSingleToken("2>>", SyntaxHighlighter.TokenType.Operator);
    [Fact] public void MergeStderr_IsOperator() => AssertSingleToken("2>&1", SyntaxHighlighter.TokenType.Operator);
    [Fact] public void DoubleAmpersand_IsOperator() => AssertSingleToken("&&", SyntaxHighlighter.TokenType.Operator);
    [Fact] public void DoublePipe_IsOperator() => AssertSingleToken("||", SyntaxHighlighter.TokenType.Operator);
    [Fact] public void Semicolon_IsOperator() => AssertSingleToken(";", SyntaxHighlighter.TokenType.Operator);

    [Fact]
    public void Pipe_TokenizedAsPipe()
    {
        var tokens = SyntaxHighlighter.Tokenize("|");
        Assert.Single(tokens);
        Assert.Equal(SyntaxHighlighter.TokenType.Pipe, tokens[0].Type);
    }

    // ── Word and Flag Tokens ────────────────────────────────────────────

    [Fact]
    public void SimpleWord_TokenizedAsWord()
    {
        var tokens = SyntaxHighlighter.Tokenize("hello");
        Assert.Single(tokens);
        Assert.Equal(SyntaxHighlighter.TokenType.Word, tokens[0].Type);
        Assert.Equal("hello", tokens[0].Text);
    }

    [Fact]
    public void Flag_TokenizedAsFlag()
    {
        var tokens = SyntaxHighlighter.Tokenize("-la");
        Assert.Single(tokens);
        Assert.Equal(SyntaxHighlighter.TokenType.Flag, tokens[0].Type);
        Assert.Equal("-la", tokens[0].Text);
    }

    [Fact]
    public void LongFlag_TokenizedAsFlag()
    {
        var tokens = SyntaxHighlighter.Tokenize("--verbose");
        Assert.Single(tokens);
        Assert.Equal(SyntaxHighlighter.TokenType.Flag, tokens[0].Type);
    }

    // ── String Tokens ───────────────────────────────────────────────────

    [Fact]
    public void SingleQuotedString_TokenizedAsString()
    {
        var tokens = SyntaxHighlighter.Tokenize("'hello world'");
        Assert.Single(tokens);
        Assert.Equal(SyntaxHighlighter.TokenType.String, tokens[0].Type);
        Assert.Equal("'hello world'", tokens[0].Text);
    }

    [Fact]
    public void DoubleQuotedString_TokenizedAsString()
    {
        var tokens = SyntaxHighlighter.Tokenize("\"hello world\"");
        Assert.Single(tokens);
        Assert.Equal(SyntaxHighlighter.TokenType.String, tokens[0].Type);
        Assert.Equal("\"hello world\"", tokens[0].Text);
    }

    [Fact]
    public void OperatorInsideQuotes_NotTokenizedAsOperator()
    {
        var tokens = SyntaxHighlighter.Tokenize("'hello > world'");
        Assert.Single(tokens);
        Assert.Equal(SyntaxHighlighter.TokenType.String, tokens[0].Type);
    }

    // ── Bang Tokens ─────────────────────────────────────────────────────

    [Fact]
    public void BangBang_TokenizedAsBang()
    {
        var tokens = SyntaxHighlighter.Tokenize("!!");
        Assert.Single(tokens);
        Assert.Equal(SyntaxHighlighter.TokenType.Bang, tokens[0].Type);
        Assert.Equal("!!", tokens[0].Text);
    }

    [Fact]
    public void BangDollar_TokenizedAsBang()
    {
        var tokens = SyntaxHighlighter.Tokenize("!$");
        Assert.Single(tokens);
        Assert.Equal(SyntaxHighlighter.TokenType.Bang, tokens[0].Type);
        Assert.Equal("!$", tokens[0].Text);
    }

    // ── Complex Input ───────────────────────────────────────────────────

    [Fact]
    public void CompleteCommand_TokenizedCorrectly()
    {
        var tokens = SyntaxHighlighter.Tokenize("ls -la /tmp");
        // Expected: Word("ls"), Whitespace, Flag("-la"), Whitespace, Word("/tmp")
        Assert.Equal(5, tokens.Count);
        Assert.Equal(SyntaxHighlighter.TokenType.Word, tokens[0].Type);
        Assert.Equal("ls", tokens[0].Text);
        Assert.Equal(SyntaxHighlighter.TokenType.Whitespace, tokens[1].Type);
        Assert.Equal(SyntaxHighlighter.TokenType.Flag, tokens[2].Type);
        Assert.Equal("-la", tokens[2].Text);
        Assert.Equal(SyntaxHighlighter.TokenType.Whitespace, tokens[3].Type);
        Assert.Equal(SyntaxHighlighter.TokenType.Word, tokens[4].Type);
        Assert.Equal("/tmp", tokens[4].Text);
    }

    [Fact]
    public void PipelineCommand_TokenizedCorrectly()
    {
        var tokens = SyntaxHighlighter.Tokenize("ls | grep test");
        // Word("ls"), WS, Pipe("|"), WS, Word("grep"), WS, Word("test")
        Assert.Equal(7, tokens.Count);
        Assert.Equal(SyntaxHighlighter.TokenType.Word, tokens[0].Type);
        Assert.Equal(SyntaxHighlighter.TokenType.Pipe, tokens[2].Type);
        Assert.Equal(SyntaxHighlighter.TokenType.Word, tokens[4].Type);
        Assert.Equal("grep", tokens[4].Text);
    }

    [Fact]
    public void RedirectInCommand_TokenizedCorrectly()
    {
        var tokens = SyntaxHighlighter.Tokenize("echo hello > out.txt");
        var opTokens = tokens.Where(t => t.Type == SyntaxHighlighter.TokenType.Operator).ToList();
        Assert.Single(opTokens);
        Assert.Equal(">", opTokens[0].Text);
    }

    [Fact]
    public void StderrRedirect_TokenizedAsOperator()
    {
        var tokens = SyntaxHighlighter.Tokenize("make 2>/dev/null");
        var opTokens = tokens.Where(t => t.Type == SyntaxHighlighter.TokenType.Operator).ToList();
        Assert.Single(opTokens);
        Assert.Equal("2>", opTokens[0].Text);
    }

    [Fact]
    public void MergeStderrInCommand_TokenizedAsOperator()
    {
        var tokens = SyntaxHighlighter.Tokenize("cmd 2>&1");
        var opTokens = tokens.Where(t => t.Type == SyntaxHighlighter.TokenType.Operator).ToList();
        Assert.Single(opTokens);
        Assert.Equal("2>&1", opTokens[0].Text);
    }

    [Fact]
    public void StdinRedirect_TokenizedAsOperator()
    {
        var tokens = SyntaxHighlighter.Tokenize("sort < data.txt");
        var opTokens = tokens.Where(t => t.Type == SyntaxHighlighter.TokenType.Operator).ToList();
        Assert.Single(opTokens);
        Assert.Equal("<", opTokens[0].Text);
    }

    [Fact]
    public void DoubleAmpersandInCommand_TokenizedAsSingleOperator()
    {
        var tokens = SyntaxHighlighter.Tokenize("cmd1 && cmd2");
        var opTokens = tokens.Where(t => t.Type == SyntaxHighlighter.TokenType.Operator).ToList();
        Assert.Single(opTokens);
        Assert.Equal("&&", opTokens[0].Text);
    }

    [Fact]
    public void DoublePipeInCommand_TokenizedAsSingleOperator()
    {
        var tokens = SyntaxHighlighter.Tokenize("cmd1 || cmd2");
        var opTokens = tokens.Where(t => t.Type == SyntaxHighlighter.TokenType.Operator).ToList();
        Assert.Single(opTokens);
        Assert.Equal("||", opTokens[0].Text);
    }

    [Fact]
    public void SemicolonInCommand_TokenizedAsOperator()
    {
        var tokens = SyntaxHighlighter.Tokenize("cmd1; cmd2");
        var opTokens = tokens.Where(t => t.Type == SyntaxHighlighter.TokenType.Operator).ToList();
        Assert.Single(opTokens);
        Assert.Equal(";", opTokens[0].Text);
    }

    // ── IsBreak Tests ───────────────────────────────────────────────────

    [Fact] public void IsBreak_Space_True() => Assert.True(SyntaxHighlighter.IsBreak(" ", 0));
    [Fact] public void IsBreak_Tab_True() => Assert.True(SyntaxHighlighter.IsBreak("\t", 0));
    [Fact] public void IsBreak_Pipe_True() => Assert.True(SyntaxHighlighter.IsBreak("|", 0));
    [Fact] public void IsBreak_Gt_True() => Assert.True(SyntaxHighlighter.IsBreak(">", 0));
    [Fact] public void IsBreak_Lt_True() => Assert.True(SyntaxHighlighter.IsBreak("<", 0));
    [Fact] public void IsBreak_SQ_True() => Assert.True(SyntaxHighlighter.IsBreak("'", 0));
    [Fact] public void IsBreak_DQ_True() => Assert.True(SyntaxHighlighter.IsBreak("\"", 0));
    [Fact] public void IsBreak_Semi_True() => Assert.True(SyntaxHighlighter.IsBreak(";", 0));
    [Fact] public void IsBreak_Letter_False() => Assert.False(SyntaxHighlighter.IsBreak("a", 0));
    [Fact] public void IsBreak_Dash_False() => Assert.False(SyntaxHighlighter.IsBreak("-", 0));
    [Fact] public void IsBreak_Slash_False() => Assert.False(SyntaxHighlighter.IsBreak("/", 0));
    [Fact] public void IsBreak_Dot_False() => Assert.False(SyntaxHighlighter.IsBreak(".", 0));

    [Fact]
    public void IsBreak_DoubleAmpersand_IsBreak()
    {
        Assert.True(SyntaxHighlighter.IsBreak("&&", 0));
    }

    [Fact]
    public void IsBreak_SingleAmpersand_NotBreak()
    {
        Assert.False(SyntaxHighlighter.IsBreak("& ", 0));
    }

    [Fact]
    public void IsBreak_BangBang_IsBreak()
    {
        Assert.True(SyntaxHighlighter.IsBreak("!!", 0));
    }

    [Fact]
    public void IsBreak_BangDollar_IsBreak()
    {
        Assert.True(SyntaxHighlighter.IsBreak("!$", 0));
    }

    [Fact]
    public void IsBreak_SingleBang_NotBreak()
    {
        Assert.False(SyntaxHighlighter.IsBreak("!a", 0));
    }

    // ── Empty/Edge Cases ────────────────────────────────────────────────

    [Fact]
    public void EmptyInput_NoTokens()
    {
        var tokens = SyntaxHighlighter.Tokenize("");
        Assert.Empty(tokens);
    }

    [Fact]
    public void WhitespaceOnly_SingleWhitespaceToken()
    {
        var tokens = SyntaxHighlighter.Tokenize("   ");
        Assert.Single(tokens);
        Assert.Equal(SyntaxHighlighter.TokenType.Whitespace, tokens[0].Type);
    }

    // ── 2>> vs >> Priority ──────────────────────────────────────────────

    [Fact]
    public void StderrAppendNotConfusedWithStdoutAppend()
    {
        var tokens = SyntaxHighlighter.Tokenize("cmd 2>> err.log");
        var opTokens = tokens.Where(t => t.Type == SyntaxHighlighter.TokenType.Operator).ToList();
        Assert.Single(opTokens);
        Assert.Equal("2>>", opTokens[0].Text);
    }
}
