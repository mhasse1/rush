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
        Assert.Equal(new[] { "name", "age" }, cls.Attributes.Select(a => a.Name).ToArray());
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

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
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

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
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

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
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

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
        Assert.Contains("alpha", stdout);
        Assert.Contains("beta", stdout);
        Assert.Equal(0, exitCode);
    }

    // ── Inheritance: Lexer ───────────────────────────────────────────────

    [Fact]
    public void Lexer_SuperKeyword()
    {
        var tokens = new Lexer("super").Tokenize();
        Assert.Equal(RushTokenType.Super, tokens[0].Type);
    }

    // ── Inheritance: Parser ─────────────────────────────────────────────

    [Fact]
    public void Parse_ClassWithInheritance()
    {
        var code = "class Dog < Animal\n  attr breed\nend";
        var cls = Assert.IsType<ClassDefNode>(ParseSingle(code));
        Assert.Equal("Dog", cls.Name);
        Assert.Equal("Animal", cls.ParentClassName);
        Assert.Single(cls.Attributes);
    }

    [Fact]
    public void Parse_ClassWithoutInheritance_ParentIsNull()
    {
        var code = "class Person\n  attr name\nend";
        var cls = Assert.IsType<ClassDefNode>(ParseSingle(code));
        Assert.Null(cls.ParentClassName);
    }

    [Fact]
    public void Parse_SuperCallInConstructor()
    {
        var code = "class Dog < Animal\n  def initialize(name)\n    super(name)\n  end\nend";
        var cls = Assert.IsType<ClassDefNode>(ParseSingle(code));
        Assert.NotNull(cls.Constructor);
        var superNode = cls.Constructor!.Body.OfType<SuperCallNode>().FirstOrDefault();
        Assert.NotNull(superNode);
        Assert.Null(superNode!.MethodName); // constructor super
        Assert.Single(superNode.Args);
    }

    [Fact]
    public void Parse_SuperMethodCall()
    {
        var code = "class Dog < Animal\n  def speak\n    super.speak\n  end\nend";
        var cls = Assert.IsType<ClassDefNode>(ParseSingle(code));
        var method = cls.Methods.First();
        var superNode = method.Body.OfType<SuperCallNode>().FirstOrDefault();
        Assert.NotNull(superNode);
        Assert.Equal("speak", superNode!.MethodName);
    }

    // ── Inheritance: Transpiler ──────────────────────────────────────────

    [Fact]
    public void Transpile_ClassInheritance()
    {
        var ps = Transpile("class Dog < Animal\n  attr breed\nend");
        Assert.Contains("class Dog : Animal {", ps);
    }

    [Fact]
    public void Transpile_ConstructorWithSuper()
    {
        var code = "class Dog < Animal\n  attr breed\n  def initialize(name, breed)\n    super(name)\n    self.breed = breed\n  end\nend";
        var ps = Transpile(code);
        Assert.Contains(": base($name)", ps);
        Assert.Contains("$this.Breed = $breed", ps);
        // super(name) should NOT appear in the body — extracted to : base()
        Assert.DoesNotContain("([Animal]$this)", ps);
    }

    [Fact]
    public void Transpile_SuperMethodCall()
    {
        var code = "class Dog < Animal\n  def speak\n    super.speak\n  end\nend";
        var ps = Transpile(code);
        Assert.Contains("([Animal]$this).Speak()", ps);
    }

    // ── Inheritance: Triage ─────────────────────────────────────────────

    [Fact]
    public void Triage_ClassWithInheritance_IsRushSyntax()
    {
        Assert.True(_engine.IsRushSyntax("class Dog < Animal"));
    }

    // ── Inheritance: Integration ─────────────────────────────────────────

    [Fact]
    public void Integration_InheritanceBasic()
    {
        var script = @"
class Animal
  attr name
  def initialize(name)
    self.name = name
  end
  def speak
    return ""...""
  end
end

class Dog < Animal
  def initialize(name)
    super(name)
  end
  def speak
    return self.name + "" says Woof!""
  end
end

d = Dog.new(""Rex"")
puts d.speak()";

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Equal("Rex says Woof!", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Integration_InheritanceWithSuper()
    {
        var script = @"
class Animal
  attr name
  def initialize(name)
    self.name = name
  end
end

class Dog < Animal
  attr breed
  def initialize(name, breed)
    super(name)
    self.breed = breed
  end
  def describe
    return self.name + "" the "" + self.breed
  end
end

d = Dog.new(""Rex"", ""Labrador"")
puts d.describe()";

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Equal("Rex the Labrador", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Integration_SuperMethodCall()
    {
        var script = @"
class Base
  def value
    return 10
  end
end

class Child < Base
  def value
    return super.value() + 5
  end
end

c = Child.new()
puts c.value()";

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Equal("15", stdout);
        Assert.Equal(0, exitCode);
    }

    // ── Static Methods: Parser ──────────────────────────────────────────

    [Fact]
    public void Parse_StaticMethod()
    {
        var code = "class MathHelper\n  def self.add(a, b)\n    return a + b\n  end\nend";
        var cls = Assert.IsType<ClassDefNode>(ParseSingle(code));
        Assert.Empty(cls.Methods);
        Assert.Single(cls.StaticMethods);
        Assert.Equal("add", cls.StaticMethods[0].Name);
        Assert.True(cls.StaticMethods[0].IsStatic);
    }

    [Fact]
    public void Parse_MixedInstanceAndStaticMethods()
    {
        var code = "class Util\n  def instance_method\n    return 1\n  end\n  def self.class_method\n    return 2\n  end\nend";
        var cls = Assert.IsType<ClassDefNode>(ParseSingle(code));
        Assert.Single(cls.Methods);
        Assert.Single(cls.StaticMethods);
        Assert.Equal("instance_method", cls.Methods[0].Name);
        Assert.Equal("class_method", cls.StaticMethods[0].Name);
    }

    // ── Static Methods: Transpiler ──────────────────────────────────────

    [Fact]
    public void Transpile_StaticMethod()
    {
        var code = "class MathHelper\n  def self.add(a, b)\n    return a + b\n  end\nend";
        var ps = Transpile(code);
        Assert.Contains("static [object] Add(", ps);
    }

    [Fact]
    public void Transpile_StaticMethodCall()
    {
        var ps = Transpile("MathHelper.add(2, 3)");
        Assert.Contains("[MathHelper]::Add(2, 3)", ps);
    }

    [Fact]
    public void Transpile_StaticPropertyAccess()
    {
        var ps = Transpile("Color.red");
        Assert.Contains("[Color]::Red", ps);
    }

    // ── Static Methods: Triage ──────────────────────────────────────────

    [Fact]
    public void Triage_StaticMethodCall_IsRushSyntax()
    {
        Assert.True(_engine.IsRushSyntax("MathHelper.add(2, 3)"));
    }

    [Fact]
    public void Triage_ClassPropertyAccess_IsRushSyntax()
    {
        Assert.True(_engine.IsRushSyntax("Color.red"));
    }

    // ── Static Methods: Integration ─────────────────────────────────────

    [Fact]
    public void Integration_StaticMethod()
    {
        var script = @"
class MathHelper
  def self.add(a, b)
    return a + b
  end
end

puts MathHelper.add(2, 3)";

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Equal("5", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Integration_StaticAndInstanceMethods()
    {
        var script = @"
class Counter
  attr value
  def initialize(start: 0)
    self.value = start
  end
  def self.create_at(n)
    return Counter.new(n)
  end
  def get_value
    return self.value
  end
end

c = Counter.create_at(42)
puts c.get_value()";

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Equal("42", stdout);
        Assert.Equal(0, exitCode);
    }

    // ── Typed Attrs: Parser ──────────────────────────────────────────────

    [Fact]
    public void Parse_TypedAttr()
    {
        var code = "class Person\n  attr name: String\nend";
        var cls = Assert.IsType<ClassDefNode>(ParseSingle(code));
        Assert.Single(cls.Attributes);
        Assert.Equal("name", cls.Attributes[0].Name);
        Assert.Equal("String", cls.Attributes[0].TypeName);
    }

    [Fact]
    public void Parse_TypedAttrMultiple()
    {
        var code = "class Point\n  attr x: Int, y: Int\nend";
        var cls = Assert.IsType<ClassDefNode>(ParseSingle(code));
        Assert.Equal(2, cls.Attributes.Count);
        Assert.Equal("x", cls.Attributes[0].Name);
        Assert.Equal("Int", cls.Attributes[0].TypeName);
        Assert.Equal("y", cls.Attributes[1].Name);
        Assert.Equal("Int", cls.Attributes[1].TypeName);
    }

    [Fact]
    public void Parse_MixedTypedAndUntypedAttrs()
    {
        var code = "class Record\n  attr name: String, age, active: Bool\nend";
        var cls = Assert.IsType<ClassDefNode>(ParseSingle(code));
        Assert.Equal(3, cls.Attributes.Count);
        Assert.Equal("String", cls.Attributes[0].TypeName);
        Assert.Null(cls.Attributes[1].TypeName);
        Assert.Equal("Bool", cls.Attributes[2].TypeName);
    }

    // ── Typed Attrs: Transpiler ──────────────────────────────────────────

    [Fact]
    public void Transpile_TypedAttr()
    {
        var ps = Transpile("class Person\n  attr name: String\nend");
        Assert.Contains("[string]$Name", ps);
    }

    [Fact]
    public void Transpile_TypedAttrInt()
    {
        var ps = Transpile("class Point\n  attr x: Int, y: Int\nend");
        Assert.Contains("[int]$X", ps);
        Assert.Contains("[int]$Y", ps);
    }

    [Fact]
    public void Transpile_UntypedAttrStaysObject()
    {
        var ps = Transpile("class Box\n  attr label\nend");
        Assert.Contains("[object]$Label", ps);
    }

    [Fact]
    public void Transpile_TypedAttrCustomClass()
    {
        var ps = Transpile("class Node\n  attr child: Node\nend");
        Assert.Contains("[Node]$Child", ps);
    }

    // ── Typed Attrs: Integration ──────────────────────────────────────────

    [Fact]
    public void Integration_TypedAttr()
    {
        var script = @"
class Person
  attr name: String, age: Int

  def initialize(name, age)
    self.name = name
    self.age = age
  end

  def describe
    return self.name + "" is "" + self.age
  end
end

p = Person.new(""Alice"", 30)
puts p.describe()";

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Equal("Alice is 30", stdout);
        Assert.Equal(0, exitCode);
    }

    // ── Named Args at .new(): Transpiler ─────────────────────────────────

    [Fact]
    public void Transpile_NamedArgsAtNew()
    {
        // Class must be defined first so transpiler can resolve named args
        var code = "class Dog\n  attr name, breed\n  def initialize(name, breed)\n    self.name = name\n    self.breed = breed\n  end\nend\nDog.new(breed: \"Lab\", name: \"Rex\")";
        var ps = Transpile(code);
        // Named args should be reordered to match constructor params: name, breed
        Assert.Contains("[Dog]::new(\"Rex\", \"Lab\")", ps);
    }

    // ── Named Args at .new(): Integration ────────────────────────────────

    [Fact]
    public void Integration_NamedArgsAtNew()
    {
        var script = @"
class Dog
  attr name, breed

  def initialize(name, breed)
    self.name = name
    self.breed = breed
  end

  def describe
    return self.name + "" the "" + self.breed
  end
end

d = Dog.new(breed: ""Labrador"", name: ""Rex"")
puts d.describe()";

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Equal("Rex the Labrador", stdout);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Integration_NamedArgsAtNewWithDefaults()
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

c1 = Counter.new()
c2 = Counter.new(start: 42)
puts c1.get_value()
puts c2.get_value()";

        var (stdout, stderr, exitCode) = TestHelper.RunRush(script);
        Assert.True(string.IsNullOrEmpty(stderr), $"stderr: {stderr}");
        Assert.Contains("0", stdout);
        Assert.Contains("42", stdout);
        Assert.Equal(0, exitCode);
    }

    // ── Puts in class methods (#27) ─────────────────────────────────

    [Fact]
    public void ClassMethod_Puts_ProducesOutput()
    {
        var code = "class Dog\n  def speak\n    puts \"Woof\"\n  end\nend\nd = Dog.new()\nd.speak()";
        var (stdout, stderr, exitCode) = TestHelper.RunRush(code);
        Assert.Equal(0, exitCode);
        Assert.Contains("Woof", stdout);
    }

    [Fact]
    public void ClassMethod_Puts_UsesConsoleWriteLine()
    {
        // Verify transpiler generates [Console]::WriteLine inside class methods
        var ps = Transpile("class Dog\n  def speak\n    puts \"Woof\"\n  end\nend");
        Assert.Contains("[Console]::WriteLine", ps);
        Assert.DoesNotContain("Write-Output", ps);
    }

    [Fact]
    public void ClassMethod_Print_ProducesOutput()
    {
        var code = "class Cat\n  def speak\n    print \"Meow\"\n  end\nend\nc = Cat.new()\nc.speak()";
        var (stdout, stderr, exitCode) = TestHelper.RunRush(code);
        Assert.Equal(0, exitCode);
        Assert.Contains("Meow", stdout);
    }
}
