namespace Rush;

// ═══════════════════════════════════════════════════════════════════════
// AST Node Types
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Base class for all Rush AST nodes.</summary>
public abstract class RushNode { }

/// <summary>A sequence of statements (script body, function body, etc.).</summary>
public class BlockBody : RushNode
{
    public List<RushNode> Statements { get; } = new();
}

/// <summary>Variable assignment: name = expr</summary>
public class AssignmentNode : RushNode
{
    public string Name { get; }
    public RushNode Value { get; }
    public AssignmentNode(string name, RushNode value) { Name = name; Value = value; }
}

/// <summary>Compound assignment: name += expr, name -= expr</summary>
public class CompoundAssignmentNode : RushNode
{
    public string Name { get; }
    public string Op { get; }  // "+=" or "-="
    public RushNode Value { get; }
    public CompoundAssignmentNode(string name, string op, RushNode value)
    {
        Name = name; Op = op; Value = value;
    }
}

/// <summary>if / elsif / else / end</summary>
public class IfNode : RushNode
{
    public RushNode Condition { get; }
    public List<RushNode> Body { get; }
    public List<(RushNode Condition, List<RushNode> Body)> Elsifs { get; } = new();
    public List<RushNode>? ElseBody { get; set; }
    public IfNode(RushNode condition, List<RushNode> body) { Condition = condition; Body = body; }
}

/// <summary>Postfix conditional: statement if/unless condition</summary>
public class PostfixIfNode : RushNode
{
    public RushNode Statement { get; }
    public RushNode Condition { get; }
    public bool IsUnless { get; }
    public PostfixIfNode(RushNode statement, RushNode condition, bool isUnless)
    {
        Statement = statement; Condition = condition; IsUnless = isUnless;
    }
}

/// <summary>for variable in collection ... end</summary>
public class ForNode : RushNode
{
    public string Variable { get; }
    public RushNode Collection { get; }
    public List<RushNode> Body { get; }
    public ForNode(string variable, RushNode collection, List<RushNode> body)
    {
        Variable = variable; Collection = collection; Body = body;
    }
}

/// <summary>while condition ... end</summary>
public class WhileNode : RushNode
{
    public RushNode Condition { get; }
    public List<RushNode> Body { get; }
    public bool IsUntil { get; }
    public WhileNode(RushNode condition, List<RushNode> body, bool isUntil = false)
    {
        Condition = condition; Body = body; IsUntil = isUntil;
    }
}

/// <summary>def name(params) ... end</summary>
public class FunctionDefNode : RushNode
{
    public string Name { get; }
    public List<ParamDef> Params { get; }
    public List<RushNode> Body { get; }
    public FunctionDefNode(string name, List<ParamDef> parameters, List<RushNode> body)
    {
        Name = name; Params = parameters; Body = body;
    }
}

/// <summary>A function parameter with optional default value.</summary>
public class ParamDef
{
    public string Name { get; }
    public RushNode? DefaultValue { get; }
    public bool IsNamed { get; }
    public ParamDef(string name, RushNode? defaultValue = null, bool isNamed = false)
    {
        Name = name; DefaultValue = defaultValue; IsNamed = isNamed;
    }
}

/// <summary>return [expr]</summary>
public class ReturnNode : RushNode
{
    public RushNode? Value { get; }
    public ReturnNode(RushNode? value) { Value = value; }
}

/// <summary>try/begin ... rescue ... ensure ... end</summary>
public class TryNode : RushNode
{
    public List<RushNode> Body { get; }
    public string? RescueVariable { get; set; }
    public List<RushNode>? RescueBody { get; set; }
    public List<RushNode>? EnsureBody { get; set; }
    public TryNode(List<RushNode> body) { Body = body; }
}

/// <summary>case/match expr / when val ... / else ... / end</summary>
public class CaseNode : RushNode
{
    public RushNode Subject { get; }
    public List<(RushNode Pattern, List<RushNode> Body)> Whens { get; } = new();
    public List<RushNode>? ElseBody { get; set; }
    public CaseNode(RushNode subject) { Subject = subject; }
}

/// <summary>Loop control: next, continue, break (with optional postfix if/unless)</summary>
public class LoopControlNode : RushNode
{
    public string Keyword { get; }  // "next", "continue", or "break"
    public LoopControlNode(string keyword) { Keyword = keyword; }
}

/// <summary>A variable reference (bare name).</summary>
public class VariableRefNode : RushNode
{
    public string Name { get; }
    public VariableRefNode(string name) { Name = name; }
}

/// <summary>A literal value (integer, float, string, bool, nil).</summary>
public class LiteralNode : RushNode
{
    public string Value { get; }
    public RushTokenType Type { get; }
    public LiteralNode(string value, RushTokenType type) { Value = value; Type = type; }
}

/// <summary>Binary operation: left op right</summary>
public class BinaryOpNode : RushNode
{
    public RushNode Left { get; }
    public string Op { get; }
    public RushNode Right { get; }
    public BinaryOpNode(RushNode left, string op, RushNode right)
    {
        Left = left; Op = op; Right = right;
    }
}

/// <summary>Unary operation: not expr, -expr</summary>
public class UnaryOpNode : RushNode
{
    public string Op { get; }
    public RushNode Operand { get; }
    public UnaryOpNode(string op, RushNode operand) { Op = op; Operand = operand; }
}

/// <summary>Method call on receiver: receiver.method(args) { block }</summary>
public class MethodCallNode : RushNode
{
    public RushNode Receiver { get; }
    public string Method { get; }
    public List<RushNode> Args { get; }
    public BlockLiteral? Block { get; }
    public MethodCallNode(RushNode receiver, string method, List<RushNode> args, BlockLiteral? block = null)
    {
        Receiver = receiver; Method = method; Args = args; Block = block;
    }
}

/// <summary>Function call: name(args)</summary>
public class FunctionCallNode : RushNode
{
    public string Name { get; }
    public List<RushNode> Args { get; }
    public FunctionCallNode(string name, List<RushNode> args) { Name = name; Args = args; }
}

/// <summary>Property access: receiver.property</summary>
public class PropertyAccessNode : RushNode
{
    public RushNode Receiver { get; }
    public string Property { get; }
    public PropertyAccessNode(RushNode receiver, string property)
    {
        Receiver = receiver; Property = property;
    }
}

/// <summary>Safe navigation: receiver&.property (returns nil if receiver is nil)</summary>
public class SafeNavNode : RushNode
{
    public RushNode Receiver { get; }
    public string Member { get; }
    public SafeNavNode(RushNode receiver, string member)
    {
        Receiver = receiver; Member = member;
    }
}

/// <summary>A block literal: { |params| body } or do |params| ... end</summary>
public class BlockLiteral : RushNode
{
    public List<string> Params { get; }
    public List<RushNode> Body { get; }
    public BlockLiteral(List<string> parameters, List<RushNode> body)
    {
        Params = parameters; Body = body;
    }
}

/// <summary>An interpolated string: "hello #{name}, count is #{1+2}"</summary>
public class InterpolatedStringNode : RushNode
{
    public List<(bool IsExpr, RushNode Node)> Parts { get; } = new();
}

/// <summary>A range expression: start..end</summary>
public class RangeNode : RushNode
{
    public RushNode Start { get; }
    public RushNode End { get; }
    public RangeNode(RushNode start, RushNode end) { Start = start; End = end; }
}

/// <summary>A symbol literal: :name</summary>
public class SymbolNode : RushNode
{
    public string Name { get; }
    public SymbolNode(string name) { Name = name; }
}

/// <summary>Array literal: [1, 2, 3]</summary>
public class ArrayLiteralNode : RushNode
{
    public List<RushNode> Elements { get; }
    public ArrayLiteralNode(List<RushNode> elements) { Elements = elements; }
}

/// <summary>Hash literal: { key: val, key2: val2 }</summary>
public class HashLiteralNode : RushNode
{
    public List<(RushNode Key, RushNode Value)> Entries { get; }
    public HashLiteralNode(List<(RushNode Key, RushNode Value)> entries) { Entries = entries; }
}

/// <summary>Named argument in function call: name: value</summary>
public class NamedArgNode : RushNode
{
    public string Name { get; }
    public RushNode Value { get; }
    public NamedArgNode(string name, RushNode value) { Name = name; Value = value; }
}

/// <summary>Command substitution: $(command)</summary>
public class CommandSubNode : RushNode
{
    public string Command { get; }
    public CommandSubNode(string command) { Command = command; }
}

/// <summary>A shell command passed through to the existing pipeline.</summary>
public class ShellPassthroughNode : RushNode
{
    public string RawCommand { get; }
    public ShellPassthroughNode(string rawCommand) { RawCommand = rawCommand; }
}

// ═══════════════════════════════════════════════════════════════════════
// Parser
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Recursive descent parser for Rush scripting language.
/// Produces an AST from a token stream.
/// </summary>
public class Parser
{
    /// <summary>
    /// Known builtin function names that can be called without parentheses.
    /// Example: puts "hello" instead of puts("hello")
    /// </summary>
    private static readonly HashSet<string> BuiltinFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "puts", "print", "warn", "die", "ask", "sleep", "exit"
    };

    private readonly List<RushToken> _tokens;
    private int _pos;

    public Parser(List<RushToken> tokens)
    {
        _tokens = tokens;
        _pos = 0;
    }

    private RushToken Current => _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1]; // EOF sentinel
    private RushToken Peek(int offset = 0) =>
        _pos + offset < _tokens.Count ? _tokens[_pos + offset] : _tokens[^1];

    private RushToken Advance()
    {
        var token = Current;
        _pos++;
        return token;
    }

    private bool Check(RushTokenType type) => Current.Type == type;
    private bool Check(params RushTokenType[] types) => types.Contains(Current.Type);

    private RushToken Expect(RushTokenType type)
    {
        if (Current.Type != type)
            throw new RushParseException($"Expected {type}, got {Current.Type} ('{Current.Value}') at position {Current.Position}");
        return Advance();
    }

    private bool Match(RushTokenType type)
    {
        if (Current.Type == type)
        {
            _pos++;
            return true;
        }
        return false;
    }

    private void SkipNewlines()
    {
        while (Current.Type == RushTokenType.Newline)
            _pos++;
    }

    /// <summary>
    /// Parse the entire token stream into a list of statements.
    /// </summary>
    public List<RushNode> Parse()
    {
        var statements = new List<RushNode>();
        SkipNewlines();

        while (Current.Type != RushTokenType.EOF)
        {
            var stmt = ParseStatement();
            if (stmt != null)
                statements.Add(stmt);
            SkipNewlines();
        }

        return statements;
    }

    /// <summary>
    /// Parse a single statement.
    /// </summary>
    private RushNode? ParseStatement()
    {
        SkipNewlines();
        if (Current.Type == RushTokenType.EOF) return null;

        return Current.Type switch
        {
            RushTokenType.If => ParseIf(),
            RushTokenType.Unless => ParseUnless(),
            RushTokenType.For => ParseFor(),
            RushTokenType.While => ParseWhile(),
            RushTokenType.Until => ParseUntil(),
            RushTokenType.Def => ParseFunctionDef(),
            RushTokenType.Return => ParseReturn(),
            RushTokenType.Try => ParseTry(),
            RushTokenType.Begin => ParseTry(),        // begin is identical to try
            RushTokenType.Case => ParseCase(),
            RushTokenType.Next => ParseLoopControl(),
            RushTokenType.Continue => ParseLoopControl(),
            RushTokenType.Break => ParseLoopControl(),
            _ => ParseExpressionStatement()
        };
    }

    /// <summary>
    /// Parse loop control: next [if cond], continue [if cond], break [if cond]
    /// </summary>
    private RushNode ParseLoopControl()
    {
        var keyword = Advance().Value; // "next", "continue", or "break"
        var node = new LoopControlNode(keyword);
        return WrapPostfix(node);
    }

    /// <summary>
    /// Parse an expression that may be an assignment or have a postfix if/unless.
    /// </summary>
    private RushNode ParseExpressionStatement()
    {
        // Check for compound assignment: identifier += expr or identifier -= expr
        if (Current.Type == RushTokenType.Identifier &&
            Peek(1).Type is RushTokenType.PlusAssign or RushTokenType.MinusAssign)
        {
            var name = Advance().Value;
            var op = Advance().Value; // += or -=
            var value = ParseExpression();
            return WrapPostfix(new CompoundAssignmentNode(name, op, value));
        }

        // Check for assignment: identifier = expr
        if (Current.Type == RushTokenType.Identifier && Peek(1).Type == RushTokenType.Assign
            && Peek(2).Type != RushTokenType.Assign) // not ==
        {
            var name = Advance().Value;
            Advance(); // skip =
            var value = ParseExpression();

            // Check for postfix if/unless
            value = WrapPostfix(new AssignmentNode(name, value));
            return value;
        }

        var expr = ParseExpression();

        // Check for postfix if/unless
        return WrapPostfix(expr);
    }

    /// <summary>
    /// If the current token is `if` or `unless` after an expression, wrap in PostfixIfNode.
    /// </summary>
    private RushNode WrapPostfix(RushNode statement)
    {
        if (Current.Type == RushTokenType.If)
        {
            Advance(); // skip 'if'
            var condition = ParseExpression();
            return new PostfixIfNode(statement, condition, isUnless: false);
        }
        if (Current.Type == RushTokenType.Unless)
        {
            Advance(); // skip 'unless'
            var condition = ParseExpression();
            return new PostfixIfNode(statement, condition, isUnless: true);
        }
        return statement;
    }

    // ── Control Flow ────────────────────────────────────────────────────

    private IfNode ParseIf()
    {
        Advance(); // skip 'if'
        var condition = ParseExpression();
        SkipNewlines();

        var body = ParseBody(RushTokenType.Elsif, RushTokenType.Else, RushTokenType.End);

        var node = new IfNode(condition, body);

        while (Current.Type == RushTokenType.Elsif)
        {
            Advance(); // skip 'elsif'
            var elsifCondition = ParseExpression();
            SkipNewlines();
            var elsifBody = ParseBody(RushTokenType.Elsif, RushTokenType.Else, RushTokenType.End);
            node.Elsifs.Add((elsifCondition, elsifBody));
        }

        if (Current.Type == RushTokenType.Else)
        {
            Advance(); // skip 'else'
            SkipNewlines();
            node.ElseBody = ParseBody(RushTokenType.End);
        }

        Expect(RushTokenType.End);
        return node;
    }

    private IfNode ParseUnless()
    {
        Advance(); // skip 'unless'
        var condition = ParseExpression();
        SkipNewlines();
        var body = ParseBody(RushTokenType.Else, RushTokenType.End);

        // unless condition → if (not condition)
        var negated = new UnaryOpNode("not", condition);
        var node = new IfNode(negated, body);

        if (Current.Type == RushTokenType.Else)
        {
            Advance();
            SkipNewlines();
            node.ElseBody = ParseBody(RushTokenType.End);
        }

        Expect(RushTokenType.End);
        return node;
    }

    private ForNode ParseFor()
    {
        Advance(); // skip 'for'
        var variable = Expect(RushTokenType.Identifier).Value;
        Expect(RushTokenType.In);
        var collection = ParseExpression();
        SkipNewlines();
        var body = ParseBody(RushTokenType.End);
        Expect(RushTokenType.End);
        return new ForNode(variable, collection, body);
    }

    private WhileNode ParseWhile()
    {
        Advance(); // skip 'while'
        var condition = ParseExpression();
        SkipNewlines();
        var body = ParseBody(RushTokenType.End);
        Expect(RushTokenType.End);
        return new WhileNode(condition, body);
    }

    private WhileNode ParseUntil()
    {
        Advance(); // skip 'until'
        var condition = ParseExpression();
        SkipNewlines();
        var body = ParseBody(RushTokenType.End);
        Expect(RushTokenType.End);
        return new WhileNode(condition, body, isUntil: true);
    }

    private FunctionDefNode ParseFunctionDef()
    {
        Advance(); // skip 'def'
        var name = Expect(RushTokenType.Identifier).Value;

        var parameters = new List<ParamDef>();
        if (Match(RushTokenType.LParen))
        {
            if (!Check(RushTokenType.RParen))
            {
                do
                {
                    var paramName = Expect(RushTokenType.Identifier).Value;
                    RushNode? defaultVal = null;
                    bool isNamed = false;

                    // Check for named parameter with colon: "name:"
                    // In def, name: default means a named parameter (keyword arg)
                    if (Match(RushTokenType.Colon))
                    {
                        isNamed = true;
                        defaultVal = ParseExpression();
                    }
                    else if (Match(RushTokenType.Assign))
                    {
                        defaultVal = ParseExpression();
                    }

                    parameters.Add(new ParamDef(paramName, defaultVal, isNamed));
                } while (Match(RushTokenType.Comma));
            }
            Expect(RushTokenType.RParen);
        }

        SkipNewlines();
        var body = ParseBody(RushTokenType.End);
        Expect(RushTokenType.End);
        return new FunctionDefNode(name, parameters, body);
    }

    private ReturnNode ParseReturn()
    {
        Advance(); // skip 'return'
        RushNode? value = null;
        if (Current.Type != RushTokenType.Newline && Current.Type != RushTokenType.EOF
            && Current.Type != RushTokenType.End)
        {
            value = ParseExpression();
        }
        return new ReturnNode(value);
    }

    private TryNode ParseTry()
    {
        Advance(); // skip 'try' or 'begin'
        SkipNewlines();
        var body = ParseBody(RushTokenType.Rescue, RushTokenType.Ensure, RushTokenType.End);

        var node = new TryNode(body);

        if (Current.Type == RushTokenType.Rescue)
        {
            Advance(); // skip 'rescue'

            // Handle: rescue => e, rescue SomeType => e, rescue e (legacy)
            if (Current.Type == RushTokenType.Assign && Peek(1).Type == RushTokenType.GreaterThan)
            {
                // rescue => e
                Advance(); // skip =
                Advance(); // skip >
                if (Current.Type == RushTokenType.Identifier)
                    node.RescueVariable = Advance().Value;
            }
            else if (Current.Type == RushTokenType.Identifier)
            {
                var firstIdent = Advance().Value;
                if (Current.Type == RushTokenType.Assign && Peek(1).Type == RushTokenType.GreaterThan)
                {
                    // rescue IOError => e
                    Advance(); // skip =
                    Advance(); // skip >
                    if (Current.Type == RushTokenType.Identifier)
                        node.RescueVariable = Advance().Value;
                    // firstIdent is exception type — stored in rescue variable comment for now
                }
                else
                {
                    // rescue e (legacy: just variable name)
                    node.RescueVariable = firstIdent;
                }
            }

            SkipNewlines();
            node.RescueBody = ParseBody(RushTokenType.Ensure, RushTokenType.End);
        }

        if (Current.Type == RushTokenType.Ensure)
        {
            Advance(); // skip 'ensure'
            SkipNewlines();
            node.EnsureBody = ParseBody(RushTokenType.End);
        }

        Expect(RushTokenType.End);
        return node;
    }

    private CaseNode ParseCase()
    {
        Advance(); // skip 'case' or 'match'
        var subject = ParseExpression();
        SkipNewlines();

        var node = new CaseNode(subject);

        while (Current.Type == RushTokenType.When)
        {
            Advance(); // skip 'when'
            var pattern = ParseExpression();
            SkipNewlines();
            var whenBody = ParseBody(RushTokenType.When, RushTokenType.Else, RushTokenType.End);
            node.Whens.Add((pattern, whenBody));
        }

        if (Current.Type == RushTokenType.Else)
        {
            Advance();
            SkipNewlines();
            node.ElseBody = ParseBody(RushTokenType.End);
        }

        Expect(RushTokenType.End);
        return node;
    }

    // ── Expression Parsing (Precedence Climbing) ─────────────────────────

    /// <summary>
    /// Public entry point for expression parsing (used by string interpolation sub-parser).
    /// </summary>
    public RushNode ParseExpression()
    {
        return ParsePipePipe();
    }

    private RushNode ParsePipePipe()
    {
        var left = ParseAmpAmp();
        while (Current.Type == RushTokenType.PipePipe)
        {
            Advance();
            var right = ParseAmpAmp();
            left = new BinaryOpNode(left, "||", right);
        }
        return left;
    }

    private RushNode ParseAmpAmp()
    {
        var left = ParseOr();
        while (Current.Type == RushTokenType.AmpAmp)
        {
            Advance();
            var right = ParseOr();
            left = new BinaryOpNode(left, "&&", right);
        }
        return left;
    }

    private RushNode ParseOr()
    {
        var left = ParseAnd();
        while (Current.Type == RushTokenType.Or)
        {
            Advance();
            var right = ParseAnd();
            left = new BinaryOpNode(left, "or", right);
        }
        return left;
    }

    private RushNode ParseAnd()
    {
        var left = ParseNot();
        while (Current.Type == RushTokenType.And)
        {
            Advance();
            var right = ParseNot();
            left = new BinaryOpNode(left, "and", right);
        }
        return left;
    }

    private RushNode ParseNot()
    {
        if (Current.Type == RushTokenType.Not)
        {
            Advance();
            var operand = ParseNot();
            return new UnaryOpNode("not", operand);
        }
        return ParseComparison();
    }

    private RushNode ParseComparison()
    {
        var left = ParseRange();
        if (Current.Type is RushTokenType.Equals or RushTokenType.NotEquals
            or RushTokenType.LessThan or RushTokenType.GreaterThan
            or RushTokenType.LessEqual or RushTokenType.GreaterEqual
            or RushTokenType.Match or RushTokenType.MatchOp or RushTokenType.NotMatch)
        {
            var opToken = Advance();
            var opStr = opToken.Type == RushTokenType.MatchOp ? "=~" : opToken.Value;
            var right = ParseRange();
            return new BinaryOpNode(left, opStr, right);
        }
        return left;
    }

    private RushNode ParseRange()
    {
        var left = ParseAdditive();
        if (Current.Type == RushTokenType.DotDot)
        {
            Advance();
            var right = ParseAdditive();
            return new RangeNode(left, right);
        }
        return left;
    }

    private RushNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Current.Type is RushTokenType.Plus or RushTokenType.Minus)
        {
            var op = Advance().Value;
            var right = ParseMultiplicative();
            left = new BinaryOpNode(left, op, right);
        }
        return left;
    }

    private RushNode ParseMultiplicative()
    {
        var left = ParseUnaryMinus();
        while (Current.Type is RushTokenType.Star or RushTokenType.Slash or RushTokenType.Percent)
        {
            var op = Advance().Value;
            var right = ParseUnaryMinus();
            left = new BinaryOpNode(left, op, right);
        }
        return left;
    }

    private RushNode ParseUnaryMinus()
    {
        if (Current.Type == RushTokenType.Minus)
        {
            Advance();
            var operand = ParsePostfix();
            return new UnaryOpNode("-", operand);
        }
        return ParsePostfix();
    }

    /// <summary>
    /// Parse postfix operations: .method, .property, &.method, (args), [index]
    /// </summary>
    private RushNode ParsePostfix()
    {
        var node = ParsePrimary();

        while (true)
        {
            if (Current.Type == RushTokenType.Dot)
            {
                Advance(); // skip .
                var member = Expect(RushTokenType.Identifier).Value;

                // Check for method call with block: .each { |x| ... } or .select { |x| ... }
                if (Current.Type == RushTokenType.LBrace && IsBlockStart())
                {
                    var block = ParseBlock();
                    node = new MethodCallNode(node, member, new List<RushNode>(), block);
                }
                // Check for method call with do block: .each do |x| ... end
                else if (Current.Type == RushTokenType.Do)
                {
                    var block = ParseDoBlock();
                    node = new MethodCallNode(node, member, new List<RushNode>(), block);
                }
                // Check for method call with parens: .first(5), .sort_by(:Name)
                else if (Current.Type == RushTokenType.LParen)
                {
                    var args = ParseArgList();
                    // After args, check for block
                    BlockLiteral? block = null;
                    if (Current.Type == RushTokenType.LBrace && IsBlockStart())
                        block = ParseBlock();
                    else if (Current.Type == RushTokenType.Do)
                        block = ParseDoBlock();
                    node = new MethodCallNode(node, member, args, block);
                }
                // Property access
                else
                {
                    node = new PropertyAccessNode(node, member);
                }
            }
            else if (Current.Type == RushTokenType.SafeNav) // &.
            {
                Advance(); // skip &.
                var member = Expect(RushTokenType.Identifier).Value;

                // Safe navigation supports property access and method calls
                if (Current.Type == RushTokenType.LParen)
                {
                    // For now, treat as property — method call through safe nav is complex
                    var args = ParseArgList();
                    // Wrap in SafeNavNode with the method call result
                    node = new SafeNavNode(node, member);
                }
                else
                {
                    node = new SafeNavNode(node, member);
                }
            }
            else if (Current.Type == RushTokenType.LBracket)
            {
                Advance(); // skip [
                var index = ParseExpression();
                Expect(RushTokenType.RBracket);
                // Index access transpiles to property access in PS
                node = new MethodCallNode(node, "[]", new List<RushNode> { index });
            }
            else
            {
                break;
            }
        }

        return node;
    }

    /// <summary>
    /// Determine if a '{' starts a block (has |params|) or is something else.
    /// Look ahead for { |identifier| or { |identifier, ...| pattern.
    /// </summary>
    private bool IsBlockStart()
    {
        if (Current.Type != RushTokenType.LBrace) return false;

        // Save position and look ahead
        var saved = _pos;
        _pos++; // skip {

        // Skip whitespace/newlines
        while (_pos < _tokens.Count && _tokens[_pos].Type == RushTokenType.Newline)
            _pos++;

        // Check for |params| pattern
        bool isBlock = false;
        if (_pos < _tokens.Count && _tokens[_pos].Type == RushTokenType.Pipe)
        {
            isBlock = true;
        }

        _pos = saved; // restore
        return isBlock;
    }

    /// <summary>Parse { |params| body }</summary>
    private BlockLiteral ParseBlock()
    {
        Expect(RushTokenType.LBrace);
        var parameters = new List<string>();

        // Parse |param1, param2| if present
        if (Match(RushTokenType.Pipe))
        {
            do
            {
                parameters.Add(Expect(RushTokenType.Identifier).Value);
            } while (Match(RushTokenType.Comma));
            Expect(RushTokenType.Pipe);
        }

        SkipNewlines();
        var body = new List<RushNode>();
        while (Current.Type != RushTokenType.RBrace && Current.Type != RushTokenType.EOF)
        {
            var stmt = ParseStatement();
            if (stmt != null) body.Add(stmt);
            SkipNewlines();
        }
        Expect(RushTokenType.RBrace);

        return new BlockLiteral(parameters, body);
    }

    /// <summary>Parse do |params| ... end</summary>
    private BlockLiteral ParseDoBlock()
    {
        Expect(RushTokenType.Do);
        var parameters = new List<string>();

        // Parse |param1, param2| if present
        if (Match(RushTokenType.Pipe))
        {
            do
            {
                parameters.Add(Expect(RushTokenType.Identifier).Value);
            } while (Match(RushTokenType.Comma));
            Expect(RushTokenType.Pipe);
        }

        SkipNewlines();
        var body = ParseBody(RushTokenType.End);
        Expect(RushTokenType.End);

        return new BlockLiteral(parameters, body);
    }

    /// <summary>Parse parenthesized argument list: (arg1, arg2, name: val, ...)</summary>
    private List<RushNode> ParseArgList()
    {
        Expect(RushTokenType.LParen);
        var args = new List<RushNode>();
        if (!Check(RushTokenType.RParen))
        {
            do
            {
                SkipNewlines();
                // Check for named argument: identifier: value
                if (Current.Type == RushTokenType.Identifier && Peek(1).Type == RushTokenType.Colon)
                {
                    var name = Advance().Value;
                    Advance(); // skip :
                    var value = ParseExpression();
                    args.Add(new NamedArgNode(name, value));
                }
                else
                {
                    args.Add(ParseExpression());
                }
                SkipNewlines();
            } while (Match(RushTokenType.Comma));
        }
        Expect(RushTokenType.RParen);
        return args;
    }

    /// <summary>Parse a primary expression (atoms).</summary>
    private RushNode ParsePrimary()
    {
        switch (Current.Type)
        {
            case RushTokenType.Integer:
            case RushTokenType.Float:
            {
                var tok = Advance();
                return new LiteralNode(tok.Value, tok.Type);
            }

            case RushTokenType.StringLiteral:
                return ParseStringLiteral();

            case RushTokenType.True:
                Advance();
                return new LiteralNode("true", RushTokenType.True);

            case RushTokenType.False:
                Advance();
                return new LiteralNode("false", RushTokenType.False);

            case RushTokenType.Nil:
                Advance();
                return new LiteralNode("nil", RushTokenType.Nil);

            case RushTokenType.Symbol:
                return new SymbolNode(Advance().Value);

            case RushTokenType.Identifier:
                return ParseIdentifierExpr();

            case RushTokenType.LParen:
                Advance(); // skip (
                var expr = ParseExpression();
                Expect(RushTokenType.RParen);
                return expr;

            case RushTokenType.LBracket:
                return ParseArrayLiteral();

            case RushTokenType.LBrace:
                return ParseHashLiteral();

            case RushTokenType.DollarParen:
                return new CommandSubNode(Advance().Value);

            case RushTokenType.DollarQuestion:
                Advance();
                return new VariableRefNode("$?");

            default:
                throw new RushParseException($"Unexpected token {Current.Type} ('{Current.Value}') at position {Current.Position}");
        }
    }

    /// <summary>
    /// Parse an identifier which could be: variable ref, function call, builtin call, or command call.
    /// </summary>
    private RushNode ParseIdentifierExpr()
    {
        var name = Advance().Value;

        // Function call with parens: name(args)
        if (Current.Type == RushTokenType.LParen)
        {
            var args = ParseArgList();
            // Check for block after function call
            BlockLiteral? block = null;
            if (Current.Type == RushTokenType.LBrace && IsBlockStart())
                block = ParseBlock();
            else if (Current.Type == RushTokenType.Do)
                block = ParseDoBlock();
            if (block != null)
                return new MethodCallNode(new FunctionCallNode(name, args), "block", new List<RushNode>(), block);
            return new FunctionCallNode(name, args);
        }

        // Builtin function call without parens: puts "hello", warn "error", die "msg"
        if (BuiltinFunctions.Contains(name) && IsExpressionStart())
        {
            var arg = ParseExpression();
            return new FunctionCallNode(name, new List<RushNode> { arg });
        }

        return new VariableRefNode(name);
    }

    /// <summary>
    /// Check if the current token could start an expression (for detecting builtin args).
    /// </summary>
    private bool IsExpressionStart()
    {
        return Current.Type is RushTokenType.StringLiteral or RushTokenType.Integer
            or RushTokenType.Float or RushTokenType.Identifier or RushTokenType.LParen
            or RushTokenType.LBracket or RushTokenType.Not or RushTokenType.True
            or RushTokenType.False or RushTokenType.Nil or RushTokenType.Minus
            or RushTokenType.Symbol or RushTokenType.DollarParen or RushTokenType.DollarQuestion;
    }

    /// <summary>
    /// Parse a string literal, detecting interpolation in double-quoted strings.
    /// </summary>
    private RushNode ParseStringLiteral()
    {
        var token = Advance();
        var raw = token.Value;

        // Single-quoted strings have no interpolation
        if (raw.StartsWith('\''))
            return new LiteralNode(raw, RushTokenType.StringLiteral);

        // Double-quoted: check for #{...} interpolation
        if (!raw.Contains("#{"))
            return new LiteralNode(raw, RushTokenType.StringLiteral);

        // Parse interpolated string
        var node = new InterpolatedStringNode();
        var content = raw[1..^1]; // strip surrounding quotes
        int i = 0;
        var textBuf = new System.Text.StringBuilder();

        while (i < content.Length)
        {
            if (i + 1 < content.Length && content[i] == '#' && content[i + 1] == '{')
            {
                // Flush text buffer
                if (textBuf.Length > 0)
                {
                    node.Parts.Add((false, new LiteralNode(textBuf.ToString(), RushTokenType.StringLiteral)));
                    textBuf.Clear();
                }

                // Find matching }
                i += 2; // skip #{
                int depth = 1;
                var exprBuf = new System.Text.StringBuilder();
                while (i < content.Length && depth > 0)
                {
                    if (content[i] == '{') depth++;
                    else if (content[i] == '}') depth--;
                    if (depth > 0) exprBuf.Append(content[i]);
                    i++;
                }

                // Parse the expression inside #{}
                var exprLexer = new Lexer(exprBuf.ToString());
                var exprTokens = exprLexer.Tokenize();
                var exprParser = new Parser(exprTokens);
                var exprNode = exprParser.ParseExpression();
                node.Parts.Add((true, exprNode));
            }
            else if (content[i] == '\\' && i + 1 < content.Length)
            {
                textBuf.Append(content[i]);
                textBuf.Append(content[i + 1]);
                i += 2;
            }
            else
            {
                textBuf.Append(content[i]);
                i++;
            }
        }

        // Flush remaining text
        if (textBuf.Length > 0)
            node.Parts.Add((false, new LiteralNode(textBuf.ToString(), RushTokenType.StringLiteral)));

        return node.Parts.Count == 1 && !node.Parts[0].IsExpr
            ? new LiteralNode(raw, RushTokenType.StringLiteral)
            : node;
    }

    private ArrayLiteralNode ParseArrayLiteral()
    {
        Expect(RushTokenType.LBracket);
        var elements = new List<RushNode>();
        if (!Check(RushTokenType.RBracket))
        {
            do
            {
                SkipNewlines();
                elements.Add(ParseExpression());
                SkipNewlines();
            } while (Match(RushTokenType.Comma));
        }
        Expect(RushTokenType.RBracket);
        return new ArrayLiteralNode(elements);
    }

    /// <summary>
    /// Parse a hash literal: { key: val, key2: val2 }
    /// Distinguished from blocks by the absence of |params|.
    /// </summary>
    private HashLiteralNode ParseHashLiteral()
    {
        Expect(RushTokenType.LBrace);
        SkipNewlines();

        var entries = new List<(RushNode Key, RushNode Value)>();

        if (!Check(RushTokenType.RBrace))
        {
            do
            {
                SkipNewlines();

                // Key: identifier followed by colon
                if (Current.Type == RushTokenType.Identifier && Peek(1).Type == RushTokenType.Colon)
                {
                    var keyName = Advance().Value;
                    Advance(); // skip colon
                    SkipNewlines();
                    var value = ParseExpression();
                    entries.Add((new SymbolNode(":" + keyName), value));
                }
                else
                {
                    // Arbitrary key expression (for future use)
                    var key = ParseExpression();
                    Expect(RushTokenType.Colon);
                    SkipNewlines();
                    var value = ParseExpression();
                    entries.Add((key, value));
                }

                SkipNewlines();
            } while (Match(RushTokenType.Comma));
        }

        SkipNewlines();
        Expect(RushTokenType.RBrace);
        return new HashLiteralNode(entries);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Parse a body of statements until one of the stop tokens is reached.
    /// </summary>
    private List<RushNode> ParseBody(params RushTokenType[] stopTokens)
    {
        var statements = new List<RushNode>();
        SkipNewlines();

        while (Current.Type != RushTokenType.EOF && !stopTokens.Contains(Current.Type))
        {
            var stmt = ParseStatement();
            if (stmt != null) statements.Add(stmt);
            SkipNewlines();
        }

        return statements;
    }
}

/// <summary>
/// Exception thrown when the Rush parser encounters invalid syntax.
/// </summary>
public class RushParseException : Exception
{
    public RushParseException(string message) : base(message) { }
}
