using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for regex literal support: /pattern/flags
/// Covers lexer disambiguation, parser AST, transpiler output, and end-to-end execution.
/// </summary>
public class RegexTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private static List<RushToken> Lex(string input)
    {
        return new Lexer(input).Tokenize();
    }

    private static RushNode ParseSingle(string rushCode)
    {
        var lexer = new Lexer(rushCode);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        var nodes = parser.Parse();
        return nodes.First();
    }

    private static string Transpile(string rushCode)
    {
        var lexer = new Lexer(rushCode);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        var nodes = parser.Parse();
        var transpiler = new RushTranspiler(new CommandTranslator());
        return string.Join("\n", nodes.Select(s => transpiler.TranspileNode(s)));
    }

    // ── Lexer Tests ─────────────────────────────────────────────────────

    [Fact]
    public void Lexer_RegexLiteral_BasicPattern()
    {
        var tokens = Lex("/^test/");
        Assert.Equal(RushTokenType.Regex, tokens[0].Type);
        Assert.Equal("^test", tokens[0].Value);
    }

    [Fact]
    public void Lexer_RegexLiteral_WithFlags()
    {
        var tokens = Lex("/error|warn/i");
        Assert.Equal(RushTokenType.Regex, tokens[0].Type);
        Assert.Equal("error|warn\0i", tokens[0].Value);
    }

    [Fact]
    public void Lexer_RegexLiteral_MultipleFlags()
    {
        var tokens = Lex("/pattern/imx");
        Assert.Equal(RushTokenType.Regex, tokens[0].Type);
        Assert.Equal("pattern\0imx", tokens[0].Value);
    }

    [Fact]
    public void Lexer_RegexLiteral_EscapedSlash()
    {
        var tokens = Lex(@"/path\/to/");
        Assert.Equal(RushTokenType.Regex, tokens[0].Type);
        Assert.Equal(@"path\/to", tokens[0].Value);
    }

    [Fact]
    public void Lexer_Division_AfterIdentifier()
    {
        // a / b → Identifier Slash Identifier (division)
        var tokens = Lex("a / b");
        Assert.Equal(RushTokenType.Identifier, tokens[0].Type);
        Assert.Equal(RushTokenType.Slash, tokens[1].Type);
        Assert.Equal(RushTokenType.Identifier, tokens[2].Type);
    }

    [Fact]
    public void Lexer_Division_AfterNumber()
    {
        // 10 / 2 → Integer Slash Integer
        var tokens = Lex("10 / 2");
        Assert.Equal(RushTokenType.Integer, tokens[0].Type);
        Assert.Equal(RushTokenType.Slash, tokens[1].Type);
        Assert.Equal(RushTokenType.Integer, tokens[2].Type);
    }

    [Fact]
    public void Lexer_Division_AfterRParen()
    {
        // (x) / 2 → ... RParen Slash Integer
        var tokens = Lex("(x) / 2");
        var slashToken = tokens.First(t => t.Type == RushTokenType.Slash);
        Assert.NotNull(slashToken);
    }

    [Fact]
    public void Lexer_Regex_AfterAssignment()
    {
        // x = /pattern/ → Identifier Assign Regex
        var tokens = Lex("x = /pattern/");
        Assert.Equal(RushTokenType.Identifier, tokens[0].Type);
        Assert.Equal(RushTokenType.Assign, tokens[1].Type);
        Assert.Equal(RushTokenType.Regex, tokens[2].Type);
        Assert.Equal("pattern", tokens[2].Value);
    }

    [Fact]
    public void Lexer_Regex_AfterMatchOp()
    {
        // name =~ /^test/ → Identifier MatchOp Regex
        var tokens = Lex("name =~ /^test/");
        Assert.Equal(RushTokenType.Identifier, tokens[0].Type);
        Assert.Equal(RushTokenType.MatchOp, tokens[1].Type);
        Assert.Equal(RushTokenType.Regex, tokens[2].Type);
    }

    [Fact]
    public void Lexer_Regex_AfterKeyword()
    {
        // if /pattern/ → If Regex
        var tokens = Lex("if /pattern/");
        Assert.Equal(RushTokenType.If, tokens[0].Type);
        Assert.Equal(RushTokenType.Regex, tokens[1].Type);
    }

    [Fact]
    public void Lexer_Regex_AfterComma()
    {
        // sub(/old/, "new") — regex after open paren and after comma
        var tokens = Lex("(/old/, /new/)");
        var regexTokens = tokens.Where(t => t.Type == RushTokenType.Regex).ToList();
        Assert.Equal(2, regexTokens.Count);
    }

    // ── Parser Tests ────────────────────────────────────────────────────

    [Fact]
    public void Parser_RegexLiteral_Basic()
    {
        var node = ParseSingle("/^test/");
        var rx = Assert.IsType<RegexLiteralNode>(node);
        Assert.Equal("^test", rx.Pattern);
        Assert.Equal("", rx.Flags);
    }

    [Fact]
    public void Parser_RegexLiteral_WithFlags()
    {
        var node = ParseSingle("/warn/i");
        var rx = Assert.IsType<RegexLiteralNode>(node);
        Assert.Equal("warn", rx.Pattern);
        Assert.Equal("i", rx.Flags);
    }

    [Fact]
    public void Parser_MatchOp_WithRegex()
    {
        var node = ParseSingle("name =~ /^test/");
        var binary = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal("=~", binary.Op);
        Assert.IsType<VariableRefNode>(binary.Left);
        var rx = Assert.IsType<RegexLiteralNode>(binary.Right);
        Assert.Equal("^test", rx.Pattern);
    }

    [Fact]
    public void Parser_NotMatchOp_WithRegex()
    {
        var node = ParseSingle("name !~ /admin/i");
        var binary = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal("!~", binary.Op);
        var rx = Assert.IsType<RegexLiteralNode>(binary.Right);
        Assert.Equal("admin", rx.Pattern);
        Assert.Equal("i", rx.Flags);
    }

    // ── Transpiler Tests ────────────────────────────────────────────────

    [Fact]
    public void Transpile_RegexLiteral_NoFlags()
    {
        var result = Transpile("/^test/");
        Assert.Contains("'^test'", result);
    }

    [Fact]
    public void Transpile_RegexLiteral_WithFlag()
    {
        var result = Transpile("/error/i");
        Assert.Contains("'(?i)error'", result);
    }

    [Fact]
    public void Transpile_RegexLiteral_MultipleFlags()
    {
        var result = Transpile("/multi/imx");
        Assert.Contains("'(?imx)multi'", result);
    }

    [Fact]
    public void Transpile_MatchOp_WithRegex()
    {
        var result = Transpile("name =~ /^test/");
        Assert.Contains("-match", result);
        Assert.Contains("'^test'", result);
    }

    [Fact]
    public void Transpile_NotMatchOp_WithRegex()
    {
        var result = Transpile("name !~ /admin/i");
        Assert.Contains("-notmatch", result);
        Assert.Contains("'(?i)admin'", result);
    }

    [Fact]
    public void Transpile_RegexLiteral_EscapedSingleQuote()
    {
        // Pattern containing a single quote should be escaped for PS
        var result = Transpile("/it's/");
        Assert.Contains("'it''s'", result);
    }

    // ── Integration Tests ───────────────────────────────────────────────

    [Fact]
    public void Integration_MatchOp_ReturnsTrue()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("puts \"hello\" =~ /^h/");
        Assert.Equal(0, exitCode);
        Assert.Equal("True", stdout);
    }

    [Fact]
    public void Integration_NotMatchOp_ReturnsTrue()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("puts \"hello\" !~ /^x/");
        Assert.Equal(0, exitCode);
        Assert.Equal("True", stdout);
    }

    [Fact]
    public void Integration_MatchOp_CaseInsensitiveByDefault()
    {
        // PowerShell -match is case-insensitive by default
        var (stdout, _, exitCode) = TestHelper.RunRush("puts \"HELLO\" =~ /hello/");
        Assert.Equal(0, exitCode);
        Assert.Equal("True", stdout);
    }

    [Fact]
    public void Integration_Gsub_WithRegex()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("result = \"hello world\".gsub(/o/, \"0\"); puts result");
        Assert.Equal(0, exitCode);
        Assert.Equal("hell0 w0rld", stdout);
    }

    [Fact]
    public void Integration_RegexWithFlags()
    {
        // /i flag should ensure case-insensitive even if PS default changes
        var (stdout, _, exitCode) = TestHelper.RunRush("puts \"ABC\" =~ /abc/i");
        Assert.Equal(0, exitCode);
        Assert.Equal("True", stdout);
    }

    [Fact]
    public void Integration_Sub_WithRegex()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("result = \"hello\".sub(/l/, \"r\"); puts result");
        Assert.Equal(0, exitCode);
        Assert.Equal("herlo", stdout);
    }

    [Fact]
    public void Integration_Scan_WithRegex()
    {
        var (stdout, _, exitCode) = TestHelper.RunRush("result = \"cat bat hat\".scan(/[cbh]at/); puts result.length");
        Assert.Equal(0, exitCode);
        Assert.Equal("3", stdout);
    }
}
