using Rush;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for the Rush scripting language Lexer — tokenizes Rush source code.
/// Covers: keywords, literals, operators, strings, comments, interpolation.
/// </summary>
public class LexerTests
{
    private static List<RushToken> Lex(string input)
    {
        return new Lexer(input).Tokenize();
    }

    // ── Keywords ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("if", RushTokenType.If)]
    [InlineData("elsif", RushTokenType.Elsif)]
    [InlineData("else", RushTokenType.Else)]
    [InlineData("end", RushTokenType.End)]
    [InlineData("for", RushTokenType.For)]
    [InlineData("in", RushTokenType.In)]
    [InlineData("while", RushTokenType.While)]
    [InlineData("unless", RushTokenType.Unless)]
    [InlineData("until", RushTokenType.Until)]
    [InlineData("def", RushTokenType.Def)]
    [InlineData("return", RushTokenType.Return)]
    [InlineData("try", RushTokenType.Try)]
    [InlineData("rescue", RushTokenType.Rescue)]
    [InlineData("true", RushTokenType.True)]
    [InlineData("false", RushTokenType.False)]
    [InlineData("nil", RushTokenType.Nil)]
    [InlineData("next", RushTokenType.Next)]
    [InlineData("break", RushTokenType.Break)]
    public void Keywords_TokenizedCorrectly(string input, RushTokenType expectedType)
    {
        var tokens = Lex(input);
        Assert.Equal(2, tokens.Count); // keyword + EOF
        Assert.Equal(expectedType, tokens[0].Type);
    }

    [Fact]
    public void Keywords_CaseInsensitive()
    {
        var tokens = Lex("IF");
        Assert.Equal(RushTokenType.If, tokens[0].Type);
    }

    // ── Integer Literals ────────────────────────────────────────────────

    [Theory]
    [InlineData("42")]
    [InlineData("0")]
    [InlineData("1000000")]
    public void IntegerLiterals_Parsed(string input)
    {
        var tokens = Lex(input);
        Assert.Equal(RushTokenType.Integer, tokens[0].Type);
        Assert.Equal(input, tokens[0].Value);
    }

    // ── Float Literals ──────────────────────────────────────────────────

    [Theory]
    [InlineData("3.14")]
    [InlineData("0.5")]
    public void FloatLiterals_Parsed(string input)
    {
        var tokens = Lex(input);
        Assert.Equal(RushTokenType.Float, tokens[0].Type);
        Assert.Equal(input, tokens[0].Value);
    }

    // ── String Literals ─────────────────────────────────────────────────

    [Fact]
    public void SingleQuotedString_Parsed()
    {
        var tokens = Lex("'hello world'");
        Assert.Equal(RushTokenType.StringLiteral, tokens[0].Type);
        // Lexer preserves quotes in value for all string types
        Assert.Equal("'hello world'", tokens[0].Value);
    }

    [Fact]
    public void DoubleQuotedString_Parsed()
    {
        var tokens = Lex("\"hello world\"");
        Assert.Equal(RushTokenType.StringLiteral, tokens[0].Type);
        // Double-quoted strings preserve the quotes in value (for interpolation handling)
        Assert.Equal("\"hello world\"", tokens[0].Value);
    }

    // ── Operators ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("=", RushTokenType.Assign)]
    [InlineData("==", RushTokenType.Equals)]
    [InlineData("!=", RushTokenType.NotEquals)]
    [InlineData("<", RushTokenType.LessThan)]
    [InlineData(">", RushTokenType.GreaterThan)]
    [InlineData("<=", RushTokenType.LessEqual)]
    [InlineData(">=", RushTokenType.GreaterEqual)]
    [InlineData("+", RushTokenType.Plus)]
    [InlineData("-", RushTokenType.Minus)]
    [InlineData("*", RushTokenType.Star)]
    [InlineData("/", RushTokenType.Slash)]
    [InlineData("%", RushTokenType.Percent)]
    [InlineData(".", RushTokenType.Dot)]
    [InlineData("..", RushTokenType.DotDot)]
    [InlineData("|", RushTokenType.Pipe)]
    [InlineData("&&", RushTokenType.AmpAmp)]
    [InlineData("||", RushTokenType.PipePipe)]
    [InlineData("+=", RushTokenType.PlusAssign)]
    [InlineData("-=", RushTokenType.MinusAssign)]
    public void Operators_TokenizedCorrectly(string input, RushTokenType expectedType)
    {
        var tokens = Lex(input);
        Assert.Equal(expectedType, tokens[0].Type);
    }

    // ── Delimiters ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("(", RushTokenType.LParen)]
    [InlineData(")", RushTokenType.RParen)]
    [InlineData("[", RushTokenType.LBracket)]
    [InlineData("]", RushTokenType.RBracket)]
    [InlineData("{", RushTokenType.LBrace)]
    [InlineData("}", RushTokenType.RBrace)]
    [InlineData(",", RushTokenType.Comma)]
    [InlineData(":", RushTokenType.Colon)]
    public void Delimiters_TokenizedCorrectly(string input, RushTokenType expectedType)
    {
        var tokens = Lex(input);
        Assert.Equal(expectedType, tokens[0].Type);
    }

    // ── Identifiers ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("foo")]
    [InlineData("my_var")]
    [InlineData("camelCase")]
    [InlineData("empty?")]    // Ruby-style predicate
    public void Identifiers_Parsed(string input)
    {
        var tokens = Lex(input);
        Assert.Equal(RushTokenType.Identifier, tokens[0].Type);
        Assert.Equal(input, tokens[0].Value);
    }

    // ── Comments ────────────────────────────────────────────────────────

    [Fact]
    public void Comment_Skipped()
    {
        var tokens = Lex("# this is a comment");
        Assert.Single(tokens); // just EOF
        Assert.Equal(RushTokenType.EOF, tokens[0].Type);
    }

    [Fact]
    public void InlineComment_OnlyCommentSkipped()
    {
        var tokens = Lex("x = 5 # assign five");
        // Should have: Identifier, Assign, Integer, EOF
        var nonEof = tokens.Where(t => t.Type != RushTokenType.EOF).ToList();
        Assert.Equal(3, nonEof.Count);
        Assert.Equal(RushTokenType.Identifier, nonEof[0].Type);
        Assert.Equal(RushTokenType.Assign, nonEof[1].Type);
        Assert.Equal(RushTokenType.Integer, nonEof[2].Type);
    }

    // ── Complex Expressions ─────────────────────────────────────────────

    [Fact]
    public void Assignment_Tokenized()
    {
        var tokens = Lex("x = 42");
        var nonEof = tokens.Where(t => t.Type != RushTokenType.EOF).ToList();
        Assert.Equal(3, nonEof.Count);
        Assert.Equal("x", nonEof[0].Value);
        Assert.Equal(RushTokenType.Assign, nonEof[1].Type);
        Assert.Equal("42", nonEof[2].Value);
    }

    [Fact]
    public void MethodCall_Tokenized()
    {
        var tokens = Lex("items.each");
        var nonEof = tokens.Where(t => t.Type != RushTokenType.EOF).ToList();
        Assert.Equal(3, nonEof.Count);
        Assert.Equal(RushTokenType.Identifier, nonEof[0].Type);
        Assert.Equal(RushTokenType.Dot, nonEof[1].Type);
        Assert.Equal(RushTokenType.Identifier, nonEof[2].Type);
    }

    [Fact]
    public void IfCondition_Tokenized()
    {
        var tokens = Lex("if x > 5");
        var nonEof = tokens.Where(t => t.Type != RushTokenType.EOF).ToList();
        Assert.Equal(4, nonEof.Count);
        Assert.Equal(RushTokenType.If, nonEof[0].Type);
        Assert.Equal(RushTokenType.Identifier, nonEof[1].Type);
        Assert.Equal(RushTokenType.GreaterThan, nonEof[2].Type);
        Assert.Equal(RushTokenType.Integer, nonEof[3].Type);
    }

    // ── Special Tokens ──────────────────────────────────────────────────

    [Fact]
    public void DollarQuestion_Tokenized()
    {
        var tokens = Lex("$?");
        Assert.Equal(RushTokenType.DollarQuestion, tokens[0].Type);
    }

    [Fact]
    public void SafeNavigation_Tokenized()
    {
        var tokens = Lex("&.");
        Assert.Equal(RushTokenType.SafeNav, tokens[0].Type);
    }

    // ── EOF ─────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyInput_OnlyEof()
    {
        var tokens = Lex("");
        Assert.Single(tokens);
        Assert.Equal(RushTokenType.EOF, tokens[0].Type);
    }

    // ── Symbols ─────────────────────────────────────────────────────────

    [Fact]
    public void Symbol_Tokenized()
    {
        var tokens = Lex(":name");
        Assert.Equal(RushTokenType.Symbol, tokens[0].Type);
        // Lexer preserves the leading colon in symbol values
        Assert.Equal(":name", tokens[0].Value);
    }

    // ── Range Operator ──────────────────────────────────────────────────

    [Fact]
    public void Range_BetweenIntegers()
    {
        var tokens = Lex("1..10");
        var nonEof = tokens.Where(t => t.Type != RushTokenType.EOF).ToList();
        Assert.Equal(3, nonEof.Count);
        Assert.Equal(RushTokenType.Integer, nonEof[0].Type);
        Assert.Equal(RushTokenType.DotDot, nonEof[1].Type);
        Assert.Equal(RushTokenType.Integer, nonEof[2].Type);
    }
}
