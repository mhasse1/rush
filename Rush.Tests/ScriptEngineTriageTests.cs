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
    [InlineData("loop")]
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

    // ── New Shell Builtins Are Not Rush Syntax ───────────────────────

    [Theory]
    [InlineData("wait")]
    [InlineData("wait %1")]
    [InlineData("unalias ls")]
    [InlineData("printf '%s' hello")]
    [InlineData("read name")]
    [InlineData("read -p 'Enter: ' name")]
    [InlineData("exec /bin/bash")]
    [InlineData("trap 'echo bye' EXIT")]
    public void NewShellBuiltins_AreNotRushSyntax(string input)
    {
        Assert.False(_engine.IsRushSyntax(input));
    }

    // ── New Shell Builtins with = should NOT be assignments ──────────

    [Theory]
    [InlineData("wait = something")]
    [InlineData("printf = value")]
    [InlineData("read = value")]
    [InlineData("exec = value")]
    [InlineData("trap = value")]
    public void NewBuiltinsWithEquals_NotTreatedAsAssignment(string input)
    {
        Assert.False(_engine.IsRushSyntax(input));
    }

    // ── Method Chaining ──────────────────────────────────────────────

    [Theory]
    [InlineData("items.each { |x| puts x }")]
    [InlineData("list.map { |x| x * 2 }")]
    [InlineData("data.select { |r| r > 0 }")]
    [InlineData("names.sort")]
    [InlineData("str.upcase")]
    public void MethodChaining_IsRushSyntax(string input)
    {
        Assert.True(_engine.IsRushSyntax(input));
    }

    // ── Dot-Containing Shell Commands (NOT Rush) ──────────────────────

    [Theory]
    [InlineData("./script.sh")]              // path with dot-slash
    [InlineData("node.js")]                  // dotted command name (no parens)
    [InlineData("docker.compose")]           // dotted subcommand
    [InlineData("config.json")]              // standalone filename
    [InlineData("cat report.sort")]          // file extension collides with RushMethods
    [InlineData("ls data.join")]             // file extension collides with RushMethods
    [InlineData("grep foo input.lines")]     // file extension collides with RushMethods
    [InlineData("open archive.split")]       // file extension collides with RushMethods
    public void DotContainingShellCommands_AreNotRushSyntax(string input)
    {
        Assert.False(_engine.IsRushSyntax(input));
    }

    // ── Method Calls in Expression Position (ARE Rush) ──────────────

    [Theory]
    [InlineData("names.sort")]               // variable.rushMethod — first token
    [InlineData("data.join(\",\")")]          // variable.rushMethod with args
    [InlineData("x = items.reverse")]        // after assignment
    public void MethodCallsInExpressionPosition_AreRushSyntax(string input)
    {
        Assert.True(_engine.IsRushSyntax(input));
    }

    // ── Method Calls on Variables (class instances) ──────────────────

    [Theory]
    [InlineData("c.increment()")]
    [InlineData("person.greet()")]
    [InlineData("counter.get_value()")]
    public void MethodCallOnVariable_IsRushSyntax(string input)
    {
        Assert.True(_engine.IsRushSyntax(input));
    }

    // ── IsIncomplete (block depth) ───────────────────────────────────

    [Theory]
    [InlineData("if x > 5")]           // open if, no end
    [InlineData("for i in 1..5")]      // open for, no end
    [InlineData("def greet(name)")]    // open def, no end
    [InlineData("while true")]         // open while, no end
    [InlineData("loop")]               // open loop, no end
    public void IncompleteBlocks_AreDetected(string input)
    {
        Assert.True(_engine.IsIncomplete(input));
    }

    [Theory]
    [InlineData("if x > 5\nputs x\nend")]
    [InlineData("for i in 1..5\nputs i\nend")]
    [InlineData("def greet(name)\nputs name\nend")]
    [InlineData("loop\nbreak\nend")]
    public void CompleteBlocks_AreNotIncomplete(string input)
    {
        Assert.False(_engine.IsIncomplete(input));
    }

    // ── Duration & Time Triage ────────────────────────────────────────

    [Theory]
    [InlineData("t = Time.now")]
    [InlineData("d = 24.hours")]
    [InlineData("elapsed = Time.now - start")]
    [InlineData("target = ARGV[0]")]
    public void NewFeatures_AssignmentTriage(string input)
    {
        Assert.True(_engine.IsRushSyntax(input));
    }

    // ── File/Dir Stdlib Triage ──────────────────────────────────────

    [Theory]
    [InlineData("File.write(\"test.txt\", \"hello\")")]
    [InlineData("File.delete(\"test.txt\")")]
    [InlineData("File.append(\"log.txt\", \"entry\")")]
    [InlineData("File.read_json(\"config.json\")")]
    [InlineData("File.read_csv(\"data.csv\")")]
    [InlineData("Dir.mkdir(\"new_dir\")")]
    [InlineData("Dir.files(\".\")")]
    [InlineData("Dir.dirs(\"/tmp\")")]
    public void StdlibCalls_Standalone_AreRushSyntax(string input)
    {
        Assert.True(_engine.IsRushSyntax(input));
    }

    [Theory]
    [InlineData("content = File.read(\"test.txt\")")]
    [InlineData("exists = File.exist?(\"test.txt\")")]
    [InlineData("data = File.read_json(\"config.json\")")]
    [InlineData("files = Dir.files(\".\", recursive: true)")]
    public void StdlibCalls_Assignment_AreRushSyntax(string input)
    {
        Assert.True(_engine.IsRushSyntax(input));
    }

    [Theory]
    [InlineData("puts File.read(\"test.txt\")")]
    [InlineData("if File.exist?(\"test.txt\")")]
    public void StdlibCalls_InExpression_AreRushSyntax(string input)
    {
        Assert.True(_engine.IsRushSyntax(input));
    }

    // ── TranspileFile: Builtin Translation ──────────────────────────

    [Fact]
    public void TranspileFile_ExportTranslatesToPowerShell()
    {
        var result = _engine.TranspileFile("export FOO=bar");
        Assert.NotNull(result);
        Assert.Contains("$env:FOO", result);
        Assert.Contains("[Environment]::SetEnvironmentVariable", result);
    }

    [Fact]
    public void TranspileFile_ExportWithPathRef_ExpandsToEnvVar()
    {
        var result = _engine.TranspileFile("export PATH=\"/usr/local/bin:$PATH\"");
        Assert.NotNull(result);
        Assert.Contains("$env:PATH", result);
    }

    [Fact]
    public void TranspileFile_PathAdd_TranslatesToPathManipulation()
    {
        var result = _engine.TranspileFile("path add /opt/homebrew/bin");
        Assert.NotNull(result);
        Assert.Contains("$env:PATH", result);
        Assert.Contains("/opt/homebrew/bin", result);
    }

    [Fact]
    public void TranspileFile_PathAddFront_PrependsToPATH()
    {
        var result = _engine.TranspileFile("path add --front /opt/bin");
        Assert.NotNull(result);
        // Front: dir comes before $env:PATH
        Assert.Contains("/opt/bin:$env:PATH", result);
    }

    [Fact]
    public void TranspileFile_PathAddAppend_AppendsToPATH()
    {
        var result = _engine.TranspileFile("path add /opt/bin");
        Assert.NotNull(result);
        // Append: $env:PATH comes before dir
        Assert.Contains("$env:PATH:/opt/bin", result);
    }

    [Fact]
    public void TranspileFile_UnsetTranslatesToRemoveItem()
    {
        var result = _engine.TranspileFile("unset FOO");
        Assert.NotNull(result);
        Assert.Contains("Remove-Item Env:FOO", result);
    }

    [Fact]
    public void TranspileFile_PathAddWithTilde_ExpandsToDollarHOME()
    {
        var result = _engine.TranspileFile("path add ~/bin");
        Assert.NotNull(result);
        Assert.Contains("$HOME/bin", result);
    }
}
