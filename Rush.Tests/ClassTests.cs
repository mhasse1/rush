using System.Diagnostics;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for Rush class support: parsing, transpilation, triage, and end-to-end execution.
/// </summary>
public class ClassTests
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

    private static (string stdout, string stderr, int exitCode) RunRush(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = RushBinary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return (stdout.Trim(), stderr.Trim(), proc.ExitCode);
    }

    // ── Lexer Tests ─────────────────────────────────────────────────────

    [Fact]
    public void Lexer_ClassKeyword()
    {
        var tokens = new Lexer("class").Tokenize();
        Assert.Equal(RushTokenType.Class, tokens[0].Type);
    }

    [Fact]
    public void Lexer_AttrKeyword()
    {
        var tokens = new Lexer("attr").Tokenize();
        Assert.Equal(RushTokenType.Attr, tokens[0].Type);
    }

    [Fact]
    public void Lexer_SelfKeyword()
    {
        var tokens = new Lexer("self").Tokenize();
        Assert.Equal(RushTokenType.Self, tokens[0].Type);
    }

    // ── Triage Tests ────────────────────────────────────────────────────

    [Fact]
    public void Triage_ClassIsRushSyntax()
    {
        Assert.True(_engine.IsRushSyntax("class Person"));
    }

    [Fact]
    public void Triage_ClassIsIncomplete()
    {
        Assert.True(_engine.IsIncomplete("class Person\n  attr name"));
    }

    [Fact]
    public void Triage_ClassIsComplete()
    {
        Assert.False(_engine.IsIncomplete("class Person\n  attr name\nend"));
    }

    // ── Parser Tests ────────────────────────────────────────────────────

    [Fact]
    public void Parse_ClassWithAttrsOnly()
    {
        var node = ParseSingle("class Person\n  attr name, age\nend");
        var cls = Assert.IsType<ClassDefNode>(node);
        Assert.Equal("Person", cls.Name);
        Assert.Equal(new[] { "name", "age" }, cls.Attributes);
        Assert.Null(cls.Constructor);
        Assert.Empty(cls.Methods);
    }

    [Fact]
    public void Parse_ClassWithConstructor()
    {
        var code = "class Dog\n  attr name\n  def initialize(name)\n    self.name = name\n  end\nend";
        var cls = Assert.IsType<ClassDefNode>(ParseSingle(code));
        Assert.Equal("Dog", cls.Name);
        Assert.Single(cls.Attributes);
        Assert.NotNull(cls.Constructor);
        Assert.Equal("initialize", cls.Constructor!.Name);
        Assert.Single(cls.Constructor.Params);
        Assert.Empty(cls.Methods);
    }

    [Fact]
    public void Parse_ClassWithMethods()
    {
        var code = "class Calc\n  def add(a, b)\n    return a + b\n  end\n  def sub(a, b)\n    return a - b\n  end\nend";
        var cls = Assert.IsType<ClassDefNode>(ParseSingle(code));
        Assert.Equal("Calc", cls.Name);
        Assert.Empty(cls.Attributes);
        Assert.Null(cls.Constructor);
        Assert.Equal(2, cls.Methods.Count);
        Assert.Equal("add", cls.Methods[0].Name);
        Assert.Equal("sub", cls.Methods[1].Name);
    }

    [Fact]
    public void Parse_PropertyAssignment()
    {
        var node = ParseSingle("self.name = value");
        var pa = Assert.IsType<PropertyAssignmentNode>(node);
        Assert.Equal("name", pa.Property);
        var receiver = Assert.IsType<VariableRefNode>(pa.Receiver);
        Assert.Equal("self", receiver.Name);
    }

    [Fact]
    public void Parse_ClassWithDefaultParams()
    {
        var code = "class Counter\n  attr value\n  def initialize(start: 0)\n    self.value = start\n  end\nend";
        var cls = Assert.IsType<ClassDefNode>(ParseSingle(code));
        Assert.NotNull(cls.Constructor);
        Assert.Single(cls.Constructor!.Params);
        Assert.True(cls.Constructor.Params[0].IsNamed);
        Assert.NotNull(cls.Constructor.Params[0].DefaultValue);
    }

    // ── Transpiler Tests ────────────────────────────────────────────────

    [Fact]
    public void Transpile_SelfToThis()
    {
        var ps = Transpile("self.name");
        Assert.Contains("$this.Name", ps);
    }

    [Fact]
    public void Transpile_PropertyAssignment()
    {
        var ps = Transpile("self.name = value");
        Assert.Contains("$this.Name = $value", ps);
    }

    [Fact]
    public void Transpile_ClassNew()
    {
        var ps = Transpile("Person.new(\"Mark\")");
        Assert.Contains("[Person]::new(\"Mark\")", ps);
    }

    [Fact]
    public void Transpile_ClassDef_AttrsBecomeProperties()
    {
        var ps = Transpile("class Person\n  attr name, age\nend");
        Assert.Contains("[object]$Name", ps);
        Assert.Contains("[object]$Age", ps);
        Assert.Contains("class Person {", ps);
    }

    [Fact]
    public void Transpile_ClassDef_ConstructorUsesClassName()
    {
        var ps = Transpile("class Dog\n  attr name\n  def initialize(name)\n    self.name = name\n  end\nend");
        // Constructor should use class name, not "initialize"
        Assert.Contains("Dog(", ps);
        Assert.DoesNotContain("Initialize(", ps);
        // self.name transpiles to $this.Name, bare name transpiles to $name
        // (PowerShell is case-insensitive for variables, so $name == $Name)
        Assert.Contains("$this.Name = $name", ps);
    }

    [Fact]
    public void Transpile_ClassDef_MethodsGetReturnType()
    {
        var ps = Transpile("class Calc\n  def add(a, b)\n    return a + b\n  end\nend");
        Assert.Contains("[object] Add(", ps);
    }

    // ── Integration Tests (rush -c) ─────────────────────────────────────

    [Fact]
    public void Integration_ClassInstantiateAndCallMethod()
    {
        var script = @"
class Greeter
  attr name

  def initialize(name)
    self.name = name
  end

  def greet
    return ""Hello, "" + self.name
  end
end

g = Greeter.new(""World"")
puts g.greet()";

        var (stdout, stderr, exitCode) = RunRush(script);
        Assert.Equal("Hello, World", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Integration_ClassWithDefaultParams()
    {
        var script = @"
class Counter
  attr value

  def initialize(start: 0)
    self.value = start
  end

  def get_value
    return self.value
  end
end

c = Counter.new()
puts c.get_value()";

        var (stdout, stderr, exitCode) = RunRush(script);
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Equal("0", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Integration_ClassStateMutation()
    {
        var script = @"
class Counter
  attr value

  def initialize(start: 0)
    self.value = start
  end

  def increment
    self.value = self.value + 1
  end

  def get_value
    return self.value
  end
end

c = Counter.new(10)
c.increment()
c.increment()
puts c.get_value()";

        var (stdout, stderr, exitCode) = RunRush(script);
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Equal("12", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Integration_ClassMultipleInstances()
    {
        var script = @"
class Box
  attr label

  def initialize(label)
    self.label = label
  end

  def get_label
    return self.label
  end
end

a = Box.new(""alpha"")
b = Box.new(""beta"")
puts a.get_label()
puts b.get_label()";

        var (stdout, stderr, exitCode) = RunRush(script);
        Assert.Contains("alpha", stdout);
        Assert.Contains("beta", stdout);
        Assert.Equal(0, exitCode);
    }
}
