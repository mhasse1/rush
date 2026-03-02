using Rush;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for ScriptEngine.IsRushSyntax — the triage function that
/// decides whether input is Rush scripting language or a shell command.
/// </summary>
public class ScriptEngineTriageTests
{
    private readonly ScriptEngine _engine;

    public ScriptEngineTriageTests()
    {
        _engine = new ScriptEngine(new CommandTranslator());
    }

    // ── Block-Start Keywords ────────────────────────────────────────────

    [Theory]
    [InlineData("if x > 5")]
    [InlineData("unless done?")]
    [InlineData("for item in items")]
    [InlineData("while running")]
    [InlineData("until finished")]
    [InlineData("def greet(name)")]
    [InlineData("try")]
    [InlineData("case value")]
    [InlineData("begin")]
    [InlineData("match x")]
    public void BlockStartKeywords_AreRushSyntax(string input)
    {
        Assert.True(_engine.IsRushSyntax(input));
    }

    // ── End/Return/Control Keywords ─────────────────────────────────────

    [Theory]
    [InlineData("end")]
    [InlineData("return value")]
    [InlineData("next")]
    [InlineData("continue")]
    [InlineData("break")]
    public void ControlKeywords_AreRushSyntax(string input)
    {
        Assert.True(_engine.IsRushSyntax(input));
    }

    // ── Assignments ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("x = 5")]
    [InlineData("name = \"hello\"")]
    [InlineData("count = 0")]
    [InlineData("result = items.length")]
    public void Assignments_AreRushSyntax(string input)
    {
        Assert.True(_engine.IsRushSyntax(input));
    }

    [Theory]
    [InlineData("x += 1")]
    [InlineData("total -= 5")]
    public void CompoundAssignments_AreRushSyntax(string input)
    {
        Assert.True(_engine.IsRushSyntax(input));
    }

    // ── Built-in Functions ──────────────────────────────────────────────

    [Theory]
    [InlineData("puts \"hello\"")]
    [InlineData("warn \"error\"")]
    [InlineData("die \"fatal\"")]
    [InlineData("print \"output\"")]
    [InlineData("ask \"name?\"")]
    public void BuiltinFunctions_AreRushSyntax(string input)
    {
        Assert.True(_engine.IsRushSyntax(input));
    }

    // ── Shell Commands (NOT Rush Syntax) ────────────────────────────────

    [Theory]
    [InlineData("ls -la")]
    [InlineData("git status")]
    [InlineData("cd /tmp")]
    [InlineData("grep pattern file.txt")]
    [InlineData("echo hello")]
    [InlineData("cat file.txt | grep test")]
    [InlineData("export PATH=/usr/bin")]
    public void ShellCommands_AreNotRushSyntax(string input)
    {
        Assert.False(_engine.IsRushSyntax(input));
    }

    // ── Edge Cases ──────────────────────────────────────────────────────

    [Fact]
    public void EmptyInput_NotRushSyntax()
    {
        Assert.False(_engine.IsRushSyntax(""));
    }

    [Fact]
    public void WhitespaceOnly_NotRushSyntax()
    {
        Assert.False(_engine.IsRushSyntax("   "));
    }

    [Fact]
    public void NullInput_NotRushSyntax()
    {
        Assert.False(_engine.IsRushSyntax(null!));
    }

    // ── Shell Builtins with = should NOT be treated as assignments ────

    [Theory]
    [InlineData("cd = /tmp")]    // cd is a shell builtin, not assignment
    [InlineData("export = val")] // export is a shell builtin
    public void ShellBuiltinWithEquals_NotTreatedAsAssignment(string input)
    {
        Assert.False(_engine.IsRushSyntax(input));
    }

    // ── Comparison operators should NOT trigger assignment ─────────────

    [Fact]
    public void EqualityComparison_NotAssignment()
    {
        // == should not be treated as assignment
        Assert.False(_engine.IsRushSyntax("cd == foo"));
    }

    [Fact]
    public void NotEqualComparison_NotAssignment()
    {
        // != should not be treated as assignment
        Assert.False(_engine.IsRushSyntax("cd != foo"));
    }
}
