using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for Rush enum support: lexing, parsing, transpilation, triage, and end-to-end execution.
/// </summary>
public class EnumTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private readonly ScriptEngine _engine = new(new CommandTranslator());

    private static string Transpile(string rushCode)
    {
        var lexer = new Lexer(rushCode);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        var nodes = parser.Parse();
        var transpiler = new RushTranspiler(new CommandTranslator());
        return string.Join("\n", nodes.Select(s => transpiler.TranspileNode(s)));
    }

    private static RushNode ParseSingle(string rushCode)
    {
        var lexer = new Lexer(rushCode);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        var nodes = parser.Parse();
        return nodes.First();
    }

    // ── Lexer Tests ─────────────────────────────────────────────────────

    [Fact]
    public void Lexer_EnumKeyword()
    {
        var tokens = new Lexer("enum").Tokenize();
        Assert.Equal(RushTokenType.Enum, tokens[0].Type);
    }

    [Fact]
    public void Lexer_EnumDoesNotConflictWithEnd()
    {
        var tokens = new Lexer("enum end").Tokenize();
        Assert.Equal(RushTokenType.Enum, tokens[0].Type);
        Assert.Equal(RushTokenType.End, tokens[1].Type);
    }

    // ── Parser Tests ────────────────────────────────────────────────────

    [Fact]
    public void Parse_SimpleEnum()
    {
        var code = "enum Color\n  red\n  green\n  blue\nend";
        var node = Assert.IsType<EnumDefNode>(ParseSingle(code));
        Assert.Equal("Color", node.Name);
        Assert.Equal(3, node.Members.Count);
        Assert.Equal("red", node.Members[0].Name);
        Assert.Equal("green", node.Members[1].Name);
        Assert.Equal("blue", node.Members[2].Name);
        Assert.All(node.Members, m => Assert.Null(m.Value));
    }

    [Fact]
    public void Parse_EnumWithExplicitValues()
    {
        var code = "enum Status\n  pending = 0\n  active = 1\n  inactive = 2\nend";
        var node = Assert.IsType<EnumDefNode>(ParseSingle(code));
        Assert.Equal("Status", node.Name);
        Assert.Equal(3, node.Members.Count);
        Assert.Equal("pending", node.Members[0].Name);
        Assert.NotNull(node.Members[0].Value);
        Assert.Equal("active", node.Members[1].Name);
        Assert.NotNull(node.Members[1].Value);
        Assert.Equal("inactive", node.Members[2].Name);
        Assert.NotNull(node.Members[2].Value);
    }

    [Fact]
    public void Parse_EnumSingleMember()
    {
        var code = "enum Singleton\n  only\nend";
        var node = Assert.IsType<EnumDefNode>(ParseSingle(code));
        Assert.Equal("Singleton", node.Name);
        Assert.Single(node.Members);
        Assert.Equal("only", node.Members[0].Name);
    }

    [Fact]
    public void Parse_EnumMixedValues()
    {
        var code = "enum Priority\n  low\n  medium = 5\n  high\nend";
        var node = Assert.IsType<EnumDefNode>(ParseSingle(code));
        Assert.Equal(3, node.Members.Count);
        Assert.Null(node.Members[0].Value);
        Assert.NotNull(node.Members[1].Value);
        Assert.Null(node.Members[2].Value);
    }

    // ── Transpiler Tests ────────────────────────────────────────────────

    [Fact]
    public void Transpile_SimpleEnum()
    {
        var ps = Transpile("enum Color\n  red\n  green\n  blue\nend");
        Assert.Contains("enum Color {", ps);
        Assert.Contains("Red", ps);
        Assert.Contains("Green", ps);
        Assert.Contains("Blue", ps);
    }

    [Fact]
    public void Transpile_EnumWithValues()
    {
        var ps = Transpile("enum Status\n  pending = 0\n  active = 1\n  inactive = 2\nend");
        Assert.Contains("enum Status {", ps);
        Assert.Contains("Pending = 0", ps);
        Assert.Contains("Active = 1", ps);
        Assert.Contains("Inactive = 2", ps);
    }

    [Fact]
    public void Transpile_EnumMemberAccess()
    {
        // Enum member access uses the same uppercase receiver generalization
        var ps = Transpile("Color.red");
        Assert.Contains("[Color]::Red", ps);
    }

    [Fact]
    public void Transpile_EnumAssignment()
    {
        var ps = Transpile("favorite = Color.red");
        Assert.Contains("[Color]::Red", ps);
    }

    // ── Triage Tests ────────────────────────────────────────────────────

    [Fact]
    public void Triage_EnumIsRushSyntax()
    {
        Assert.True(_engine.IsRushSyntax("enum Color"));
    }

    [Fact]
    public void Triage_EnumIsIncomplete()
    {
        Assert.True(_engine.IsIncomplete("enum Color\n  red"));
    }

    [Fact]
    public void Triage_EnumIsComplete()
    {
        Assert.False(_engine.IsIncomplete("enum Color\n  red\nend"));
    }

    [Fact]
    public void Triage_EnumMemberAccess_IsRushSyntax()
    {
        Assert.True(_engine.IsRushSyntax("Color.red"));
    }

    // ── SyntaxHighlighter Tests ─────────────────────────────────────────

    [Fact]
    public void Highlight_EnumKeyword()
    {
        var highlighter = new SyntaxHighlighter(new CommandTranslator());
        var result = highlighter.Colorize("enum Color");
        // "enum" should be highlighted as a keyword (not plain text)
        Assert.NotEqual("enum Color", result);
    }

    [Fact]
    public void Highlight_SuperKeyword()
    {
        var highlighter = new SyntaxHighlighter(new CommandTranslator());
        var result = highlighter.Colorize("super");
        Assert.NotEqual("super", result);
    }

    // ── Integration Tests (rush -c) ─────────────────────────────────────

    [Fact]
    public void Integration_SimpleEnum()
    {
        var script = @"
enum Color
  red
  green
  blue
end

puts Color.red";

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Equal("Red", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Integration_EnumWithExplicitValues()
    {
        var script = @"
enum Status
  pending = 0
  active = 1
  inactive = 2
end

puts Status.active";

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Equal("Active", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Integration_EnumAssignment()
    {
        var script = @"
enum Color
  red
  green
  blue
end

favorite = Color.blue
puts favorite";

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Equal("Blue", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Integration_EnumComparison()
    {
        var script = @"
enum Color
  red
  green
  blue
end

c = Color.red
if c == Color.red
  puts ""it is red""
end";

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Equal("it is red", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Integration_EnumWithExplicitValues_Defined()
    {
        // Verify enum with explicit values can be defined and members accessed
        var script = @"
enum Priority
  low = 1
  medium = 5
  high = 10
end

p = Priority.high
puts p";

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Equal("High", stdout);
        Assert.Equal(0, exitCode);
    }
}
