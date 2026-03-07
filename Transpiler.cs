using System.Text;

namespace Rush;

/// <summary>
/// Transpiles Rush AST nodes into PowerShell code.
/// This is the core of Rush's scripting language — every Rush construct
/// reduces to valid PowerShell 7 that executes on the shared Runspace.
/// </summary>
public class RushTranspiler
{
    private readonly CommandTranslator _translator;

    // Class context for super call transpilation — set during TranspileClassDef
    private string? _currentClassParent;

    // Registry of class definitions for named-arg resolution at .new() call sites
    private readonly Dictionary<string, ClassDefNode> _classDefinitions = new();

    /// <summary>
    /// ANSI color codes for string color methods.
    /// "text".green → ANSI-colored string that works in Write-Output and variables.
    /// </summary>
    private static readonly Dictionary<string, string> AnsiColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = "31",
        ["green"] = "32",
        ["yellow"] = "33",
        ["blue"] = "34",
        ["magenta"] = "35",
        ["cyan"] = "36",
        ["white"] = "37",
        ["gray"] = "90",
    };

    public RushTranspiler(CommandTranslator translator)
    {
        _translator = translator;
    }

    /// <summary>
    /// Transpile a list of AST nodes (a script body) to PowerShell.
    /// </summary>
    public string Transpile(List<RushNode> statements)
    {
        var sb = new StringBuilder();
        foreach (var stmt in statements)
        {
            var line = TranspileNode(stmt);
            if (!string.IsNullOrEmpty(line))
            {
                sb.AppendLine(line);
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Transpile a single AST node to PowerShell code.
    /// </summary>
    public string TranspileNode(RushNode node) => node switch
    {
        AssignmentNode a => TranspileAssignment(a),
        MultipleAssignmentNode ma => TranspileMultipleAssignment(ma),
        CompoundAssignmentNode ca => TranspileCompoundAssignment(ca),
        IfNode i => TranspileIf(i),
        PostfixIfNode p => TranspilePostfixIf(p),
        ForNode f => TranspileFor(f),
        WhileNode w => TranspileWhile(w),
        FunctionDefNode d => TranspileFunctionDef(d),
        ReturnNode r => TranspileReturn(r),
        TryNode t => TranspileTry(t),
        CaseNode c => TranspileCase(c),
        LoopControlNode lc => TranspileLoopControl(lc),
        ShellPassthroughNode s => TranspileShellPassthrough(s),
        ClassDefNode cls => TranspileClassDef(cls),
        EnumDefNode en => TranspileEnumDef(en),
        PropertyAssignmentNode pa => TranspilePropertyAssignment(pa),
        _ => TranspileExpression(node)
    };

    // ── Statements ──────────────────────────────────────────────────────

    private string TranspileAssignment(AssignmentNode node)
    {
        return $"${node.Name} = {TranspileExpression(node.Value)}";
    }

    private string TranspileMultipleAssignment(MultipleAssignmentNode node)
    {
        // a, b, c = 1, 2, 3 → $a = 1; $b = 2; $c = 3
        var parts = new List<string>();
        for (int i = 0; i < node.Names.Count; i++)
        {
            var value = i < node.Values.Count
                ? TranspileExpression(node.Values[i])
                : "$null";
            parts.Add($"${node.Names[i]} = {value}");
        }
        return string.Join("; ", parts);
    }

    private string TranspileCompoundAssignment(CompoundAssignmentNode node)
    {
        // += and -= pass through to PowerShell directly
        return $"${node.Name} {node.Op} {TranspileExpression(node.Value)}";
    }

    private string TranspileLoopControl(LoopControlNode node)
    {
        // next and continue both map to PowerShell's continue
        // break maps to PowerShell's break
        return node.Keyword.ToLower() switch
        {
            "next" or "continue" => "continue",
            "break" => "break",
            _ => node.Keyword
        };
    }

    private string TranspileIf(IfNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"if ({TranspileCondition(node.Condition)}) {{");
        sb.Append(TranspileBody(node.Body));
        sb.Append('}');

        foreach (var (condition, body) in node.Elsifs)
        {
            sb.AppendLine($" elseif ({TranspileCondition(condition)}) {{");
            sb.Append(TranspileBody(body));
            sb.Append('}');
        }

        if (node.ElseBody != null)
        {
            sb.AppendLine(" else {");
            sb.Append(TranspileBody(node.ElseBody));
            sb.Append('}');
        }

        return sb.ToString();
    }

    private string TranspilePostfixIf(PostfixIfNode node)
    {
        var stmt = TranspileNode(node.Statement);
        var cond = TranspileCondition(node.Condition);
        if (node.IsUnless)
            return $"if (-not ({cond})) {{ {stmt} }}";
        return $"if ({cond}) {{ {stmt} }}";
    }

    private string TranspileFor(ForNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"foreach (${node.Variable} in @({TranspileExpression(node.Collection)})) {{");
        sb.Append(TranspileBody(node.Body));
        sb.Append('}');
        return sb.ToString();
    }

    private string TranspileWhile(WhileNode node)
    {
        var sb = new StringBuilder();
        var cond = TranspileCondition(node.Condition);
        if (node.IsUntil)
            cond = $"-not ({cond})";
        sb.AppendLine($"while ({cond}) {{");
        sb.Append(TranspileBody(node.Body));
        sb.Append('}');
        return sb.ToString();
    }

    private string TranspileFunctionDef(FunctionDefNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"function {node.Name} {{");

        if (node.Params.Count > 0)
        {
            sb.AppendLine("  param(");
            for (int i = 0; i < node.Params.Count; i++)
            {
                var p = node.Params[i];
                var paramStr = $"    [Parameter(Position={i})] ${p.Name}";
                if (p.DefaultValue != null)
                    paramStr += $" = {TranspileExpression(p.DefaultValue)}";
                if (i < node.Params.Count - 1)
                    paramStr += ",";
                sb.AppendLine(paramStr);
            }
            sb.AppendLine("  )");
        }

        sb.Append(TranspileBody(node.Body));
        sb.Append('}');
        return sb.ToString();
    }

    private string TranspileClassDef(ClassDefNode node)
    {
        // Register class definition for named-arg resolution at .new() call sites
        _classDefinitions[node.Name] = node;

        // Set class context for super call transpilation
        var prevParent = _currentClassParent;
        _currentClassParent = node.ParentClassName;

        var sb = new StringBuilder();

        // Class declaration with optional inheritance
        if (node.ParentClassName != null)
            sb.AppendLine($"class {node.Name} : {node.ParentClassName} {{");
        else
            sb.AppendLine($"class {node.Name} {{");

        // Emit property declarations from attr (with optional type annotations)
        foreach (var attr in node.Attributes)
        {
            var psType = attr.TypeName != null ? MapRushType(attr.TypeName) : "object";
            sb.AppendLine($"  [{psType}]${CapitalizeProperty(attr.Name)}");
        }

        // Emit constructor (initialize → ClassName constructor)
        // PowerShell constructors don't support default parameter values like functions do.
        // When defaults exist, we generate constructor overloads + a hidden _Init method.
        if (node.Constructor != null)
        {
            var ctor = node.Constructor;
            var hasDefaults = ctor.Params.Any(p => p.DefaultValue != null);

            // Extract super(args) call from constructor body for : base(args) syntax
            var superCall = ExtractSuperCall(ctor.Body);
            var baseClause = "";
            if (superCall != null)
            {
                var baseArgs = string.Join(", ", superCall.Args.Select(TranspileExpression));
                baseClause = $" : base({baseArgs})";
            }

            // Body without the super call
            var bodyWithoutSuper = ctor.Body.Where(n => n is not SuperCallNode).ToList();

            if (hasDefaults)
            {
                // Hidden _Init method holds the actual constructor body (excluding super)
                var allParams = ctor.Params.Select(p => $"[object]${CapitalizeProperty(p.Name)}").ToList();
                sb.AppendLine();
                sb.AppendLine($"  hidden _Init({string.Join(", ", allParams)}) {{");
                sb.Append(TranspileBody(bodyWithoutSuper, "    "));
                sb.AppendLine("  }");

                // Generate constructor overloads
                var paramCount = ctor.Params.Count;
                int firstDefault = ctor.Params.FindIndex(p => p.DefaultValue != null);

                for (int argCount = firstDefault; argCount <= paramCount; argCount++)
                {
                    bool valid = true;
                    for (int k = argCount; k < paramCount; k++)
                    {
                        if (ctor.Params[k].DefaultValue == null) { valid = false; break; }
                    }
                    if (!valid) continue;

                    var overloadParams = ctor.Params.Take(argCount)
                        .Select(p => $"[object]${CapitalizeProperty(p.Name)}").ToList();
                    var callArgs = new List<string>();
                    for (int k = 0; k < paramCount; k++)
                    {
                        if (k < argCount)
                            callArgs.Add($"${CapitalizeProperty(ctor.Params[k].Name)}");
                        else
                            callArgs.Add(TranspileExpression(ctor.Params[k].DefaultValue!));
                    }

                    sb.AppendLine();
                    sb.AppendLine($"  {node.Name}({string.Join(", ", overloadParams)}){baseClause} {{");
                    sb.AppendLine($"    $this._Init({string.Join(", ", callArgs)})");
                    sb.AppendLine("  }");
                }
            }
            else
            {
                // No defaults — single constructor
                var paramList = string.Join(", ",
                    ctor.Params.Select(p => $"[object]${CapitalizeProperty(p.Name)}"));

                sb.AppendLine();
                sb.AppendLine($"  {node.Name}({paramList}){baseClause} {{");
                sb.Append(TranspileBody(bodyWithoutSuper, "    "));
                sb.AppendLine("  }");
            }
        }

        // Emit instance methods
        foreach (var method in node.Methods)
        {
            EmitMethod(sb, method, isStatic: false);
        }

        // Emit static methods
        foreach (var method in node.StaticMethods)
        {
            EmitMethod(sb, method, isStatic: true);
        }

        sb.Append('}');
        _currentClassParent = prevParent;
        return sb.ToString();
    }

    /// <summary>Emit a single method (instance or static) into a class body.</summary>
    private void EmitMethod(StringBuilder sb, FunctionDefNode method, bool isStatic)
    {
        var paramList = string.Join(", ",
            method.Params.Select(p =>
            {
                var ps = $"[object]${CapitalizeProperty(p.Name)}";
                if (p.DefaultValue != null)
                    ps += $" = {TranspileExpression(p.DefaultValue)}";
                return ps;
            }));

        var returnType = HasReturnValue(method.Body) ? "[object]" : "[void]";
        var staticKeyword = isStatic ? "static " : "";

        sb.AppendLine();
        sb.AppendLine($"  {staticKeyword}{returnType} {CapitalizeProperty(method.Name)}({paramList}) {{");
        sb.Append(TranspileBody(method.Body, "    "));
        sb.AppendLine("  }");
    }

    /// <summary>Extract the first SuperCallNode from a constructor body (for : base() syntax).</summary>
    private static SuperCallNode? ExtractSuperCall(List<RushNode> body)
    {
        return body.OfType<SuperCallNode>().FirstOrDefault(s => s.MethodName == null);
    }

    /// <summary>Transpile super.method(args) → ([ParentClass]$this).Method(args)</summary>
    private string TranspileSuperCall(SuperCallNode node)
    {
        if (_currentClassParent == null)
            return "<# super outside class hierarchy #>";

        var args = string.Join(", ", node.Args.Select(TranspileExpression));

        if (node.MethodName != null)
        {
            // super.method(args) → ([Parent]$this).Method(args)
            return $"([{_currentClassParent}]$this).{CapitalizeProperty(node.MethodName)}({args})";
        }

        // Bare super(args) in a method context (not constructor — constructor handled by extraction)
        return $"([{_currentClassParent}]$this)";
    }

    /// <summary>Transpile an enum definition to PowerShell.</summary>
    private string TranspileEnumDef(EnumDefNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"enum {node.Name} {{");
        foreach (var (name, value) in node.Members)
        {
            var capitalizedName = CapitalizeProperty(name);
            if (value != null)
                sb.AppendLine($"  {capitalizedName} = {TranspileExpression(value)}");
            else
                sb.AppendLine($"  {capitalizedName}");
        }
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// Check if a method body contains any return statement with a value.
    /// Used to decide between [void] and [object] return types in PS class methods.
    /// </summary>
    private static bool HasReturnValue(List<RushNode> body)
    {
        foreach (var node in body)
        {
            if (node is ReturnNode ret && ret.Value != null)
                return true;
            // Check nested bodies (if/else, loops, etc.)
            if (node is IfNode ifNode)
            {
                if (HasReturnValue(ifNode.Body)) return true;
                foreach (var elsif in ifNode.Elsifs)
                    if (HasReturnValue(elsif.Body)) return true;
                if (ifNode.ElseBody != null && HasReturnValue(ifNode.ElseBody)) return true;
            }
            if (node is ForNode forNode && HasReturnValue(forNode.Body)) return true;
            if (node is WhileNode whileNode && HasReturnValue(whileNode.Body)) return true;
            if (node is TryNode tryNode)
            {
                if (HasReturnValue(tryNode.Body)) return true;
                if (tryNode.RescueBody != null && HasReturnValue(tryNode.RescueBody)) return true;
            }
        }
        return false;
    }

    private string TranspilePropertyAssignment(PropertyAssignmentNode node)
    {
        var receiver = TranspileExpression(node.Receiver);
        var prop = CapitalizeProperty(node.Property);
        var value = TranspileExpression(node.Value);
        return $"{receiver}.{prop} = {value}";
    }

    private string TranspileReturn(ReturnNode node)
    {
        if (node.Value != null)
            return $"return {TranspileExpression(node.Value)}";
        return "return";
    }

    private string TranspileTry(TryNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine("try {");
        sb.Append(TranspileBody(node.Body));
        sb.Append('}');

        if (node.RescueBody != null)
        {
            sb.AppendLine(" catch {");
            if (node.RescueVariable != null)
                sb.AppendLine($"  ${node.RescueVariable} = $_.Exception");
            sb.Append(TranspileBody(node.RescueBody));
            sb.Append('}');
        }

        if (node.EnsureBody != null)
        {
            sb.AppendLine(" finally {");
            sb.Append(TranspileBody(node.EnsureBody));
            sb.Append('}');
        }

        return sb.ToString();
    }

    private string TranspileCase(CaseNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"switch ({TranspileExpression(node.Subject)}) {{");

        foreach (var (pattern, body) in node.Whens)
        {
            // Check if pattern is a regex match (uses ~ operator)
            if (pattern is BinaryOpNode bin && bin.Op == "~")
            {
                sb.AppendLine($"  {{ $_ -match {TranspileExpression(bin.Right)} }} {{");
            }
            else
            {
                sb.AppendLine($"  {TranspileExpression(pattern)} {{");
            }
            sb.Append(TranspileBody(body, indent: "    "));
            sb.AppendLine("  }");
        }

        if (node.ElseBody != null)
        {
            sb.AppendLine("  default {");
            sb.Append(TranspileBody(node.ElseBody, indent: "    "));
            sb.AppendLine("  }");
        }

        sb.Append('}');
        return sb.ToString();
    }

    private string TranspileShellPassthrough(ShellPassthroughNode node)
    {
        // Delegate to the existing CommandTranslator
        return _translator.Translate(node.RawCommand) ?? node.RawCommand;
    }

    // ── Expressions ─────────────────────────────────────────────────────

    /// <summary>
    /// Transpile an expression node to a PowerShell expression string.
    /// </summary>
    public string TranspileExpression(RushNode node) => node switch
    {
        LiteralNode lit => TranspileLiteral(lit),
        VariableRefNode v => TranspileVariableRef(v),
        BinaryOpNode b => TranspileBinary(b),
        UnaryOpNode u => TranspileUnary(u),
        MethodCallNode m => TranspileMethodCall(m),
        FunctionCallNode f => TranspileFunctionCall(f),
        PropertyAccessNode p => TranspilePropertyAccess(p),
        SafeNavNode sn => TranspileSafeNav(sn),
        InterpolatedStringNode s => TranspileInterpolatedString(s),
        RangeNode r => $"{TranspileExpression(r.Start)}..{TranspileExpression(r.End)}",
        SymbolNode sym => $"'{sym.Name[1..]}'", // :name → 'name'
        ArrayLiteralNode a => TranspileArrayLiteral(a),
        HashLiteralNode h => TranspileHashLiteral(h),
        CommandSubNode cmd => TranspileCommandSub(cmd),
        NamedArgNode na => $"-{CapitalizeProperty(na.Name)} {TranspileExpression(na.Value)}",
        AssignmentNode a => TranspileAssignment(a),
        CompoundAssignmentNode ca => TranspileCompoundAssignment(ca),
        LoopControlNode lc => TranspileLoopControl(lc),
        RegexLiteralNode rx => TranspileRegex(rx),
        SuperCallNode sc => TranspileSuperCall(sc),
        ShellPassthroughNode s => TranspileShellPassthrough(s),
        _ => $"<# unsupported: {node.GetType().Name} #>"
    };

    private string TranspileVariableRef(VariableRefNode node)
    {
        // Special variable $? → last exit status
        if (node.Name == "$?")
            return "$LASTEXITCODE";
        // self → $this (inside class methods)
        if (node.Name == "self")
            return "$this";
        return $"${node.Name}";
    }

    private string TranspileLiteral(LiteralNode node)
    {
        return node.Type switch
        {
            RushTokenType.True => "$true",
            RushTokenType.False => "$false",
            RushTokenType.Nil => "$null",
            _ => node.Value
        };
    }

    private string TranspileBinary(BinaryOpNode node)
    {
        var left = TranspileExpression(node.Left);
        var op = TranslateOperator(node.Op);

        // For match operators with regex literals, emit pattern as string (not [regex] cast)
        if (node.Op is "=~" or "!~" or "~" && node.Right is RegexLiteralNode rx)
        {
            var pattern = TranspileRegexAsPattern(rx);
            return $"({left} {op} {pattern})";
        }

        var right = TranspileExpression(node.Right);
        return $"({left} {op} {right})";
    }

    private string TranspileUnary(UnaryOpNode node)
    {
        var operand = TranspileExpression(node.Operand);
        return node.Op switch
        {
            "not" => $"(-not {operand})",
            "-" => $"(-{operand})",
            _ => $"({node.Op}{operand})"
        };
    }

    private string TranspileHashLiteral(HashLiteralNode node)
    {
        if (node.Entries.Count == 0)
            return "@{}";

        var entries = string.Join("; ", node.Entries.Select(e =>
        {
            var key = e.Key is SymbolNode sym ? sym.Name[1..] : TranspileExpression(e.Key);
            return $"{key} = {TranspileExpression(e.Value)}";
        }));
        return $"@{{ {entries} }}";
    }

    private string TranspileCommandSub(CommandSubNode node)
    {
        // Translate the captured command through CommandTranslator
        var translated = _translator.Translate(node.Command) ?? node.Command;
        return $"({translated})";
    }

    private string TranspileSafeNav(SafeNavNode node)
    {
        var receiver = TranspileExpression(node.Receiver);
        var member = CapitalizeProperty(node.Member);
        return $"$(if ($null -ne {receiver}) {{ {receiver}.{member} }})";
    }

    /// <summary>
    /// Transpile Rush method calls to PowerShell pipeline operators.
    /// This is where .each, .select, .map, etc. become PS cmdlets.
    /// Also handles string methods, numeric methods, color methods, and stdlib calls.
    /// </summary>
    private string TranspileMethodCall(MethodCallNode node)
    {
        // ── Stdlib: File.method() ──────────────────────────────────────
        if (IsStdlibReceiver(node.Receiver, "File"))
            return TranspileFileMethod(node);

        // ── Stdlib: Dir.method() ───────────────────────────────────────
        if (IsStdlibReceiver(node.Receiver, "Dir"))
            return TranspileDirMethod(node);

        // ── Stdlib: Time.method() ─────────────────────────────────────
        if (IsStdlibReceiver(node.Receiver, "Time"))
            return TranspileTimeMethod(node);

        // ── Class method calls: ClassName.new(args) or ClassName.method(args) ──
        // PascalCase receiver that's not a stdlib → [ClassName]::Method(args)
        // Excludes ALL_CAPS names (ARGV, PATH) which are variables, not classes
        if (node.Receiver is VariableRefNode cn
            && cn.Name.Length > 0 && char.IsUpper(cn.Name[0])
            && cn.Name.Any(char.IsLower))
        {
            var method = node.Method.Equals("new", StringComparison.OrdinalIgnoreCase)
                ? "new" : CapitalizeProperty(node.Method);
            var args = ResolveClassCallArgs(cn.Name, node.Method, node.Args);
            return $"[{cn.Name}]::{method}({args})";
        }

        // ── env["KEY"] or env[index] ───────────────────────────────────
        if (IsStdlibReceiver(node.Receiver, "env") && node.Method == "[]")
            return TranspileEnvAccess(node);

        var receiver = TranspileExpression(node.Receiver);

        return node.Method switch
        {
            // ── Collection/pipeline methods ────────────────────────────
            "each" => $"{receiver} | ForEach-Object {{ {TranspileBlockBody(node.Block!)} }}",
            "select" => $"{receiver} | Where-Object {{ {TranspileBlockCondition(node.Block!)} }}",
            "reject" => $"{receiver} | Where-Object {{ -not ({TranspileBlockCondition(node.Block!)}) }}",
            "map" => $"{receiver} | ForEach-Object {{ {TranspileBlockBody(node.Block!)} }}",
            "flat_map" => $"{receiver} | ForEach-Object {{ {TranspileBlockBody(node.Block!)} }}",
            "sort_by" => TranspileSortBy(receiver, node),
            "first" => TranspileFirst(receiver, node.Args),
            "last" => TranspileLast(receiver, node.Args),
            "count" => $"@({receiver}).Count",
            "any?" => TranspileAny(receiver, node.Block),
            "all?" => TranspileAll(receiver, node.Block),
            "group_by" => TranspileGroupBy(receiver, node.Args),
            "uniq" => $"{receiver} | Select-Object -Unique",
            "reverse" => $"@({receiver})[(@({receiver}).Count - 1)..0]",
            "join" => TranspileJoin(receiver, node.Args),
            "to_json" => $"{receiver} | ConvertTo-Json -Depth 5",
            "to_csv" => $"{receiver} | ConvertTo-Csv -NoTypeInformation",
            "include?" => TranspileInclude(receiver, node.Args),
            "sort" => $"{receiver} | Sort-Object",
            "skip" => TranspileSkip(receiver, node.Args),
            "skip_while" => TranspileSkipWhile(receiver, node.Block),
            "push" => $"[void]({receiver}).Add({TranspileExpression(node.Args[0])})",
            "compact" => $"{receiver} | Where-Object {{ $null -ne $_ }}",
            "flatten" => $"{receiver} | ForEach-Object {{ $_ }}",

            // ── String methods ─────────────────────────────────────────
            "strip" => $"({receiver}).Trim()",
            "lstrip" => $"({receiver}).TrimStart()",
            "rstrip" => $"({receiver}).TrimEnd()",
            "trim_end" => node.Args.Count > 0
                ? $"({receiver}).TrimEnd({TranspileExpression(node.Args[0])})"
                : $"({receiver}).TrimEnd()",
            "upcase" => $"({receiver}).ToUpper()",
            "downcase" => $"({receiver}).ToLower()",
            "split" => TranspileSplit(receiver, node.Args),
            "split_whitespace" => $"({receiver}).Trim() -split '\\s+'",
            "lines" => $"({receiver}) -split '\\r?\\n'",
            "start_with?" => $"({receiver}).StartsWith({TranspileExpression(node.Args[0])})",
            "end_with?" => $"({receiver}).EndsWith({TranspileExpression(node.Args[0])})",
            "empty?" => $"(({receiver}).Length -eq 0)",
            "nil?" => $"($null -eq {receiver})",
            "replace" => $"({receiver}).Replace({string.Join(", ", node.Args.Select(TranspileExpression))})",
            "ljust" => $"({receiver}).PadRight({TranspileExpression(node.Args[0])})",
            "rjust" => $"({receiver}).PadLeft({TranspileExpression(node.Args[0])})",
            "to_i" => $"[int]({receiver})",
            "to_f" => $"[double]({receiver})",
            "to_s" => $"[string]({receiver})",

            // ── Regex string methods ───────────────────────────────────
            "sub" => TranspileSub(receiver, node.Args),
            "gsub" => TranspileGsub(receiver, node.Args),
            "scan" => TranspileScan(receiver, node.Args),
            "match" => TranspileMatch(receiver, node.Args),

            // ── Numeric methods ────────────────────────────────────────
            "round" => TranspileRound(receiver, node.Args),
            "abs" => $"[Math]::Abs({receiver})",
            "times" => TranspileTimes(receiver, node.Block),
            "to_currency" => TranspileToCurrency(receiver, node.Args),
            "to_filesize" => TranspileToFilesize(receiver),
            "to_percent" => TranspileToPercent(receiver, node.Args),

            // ── Duration methods ──────────────────────────────────────
            "hours"   => $"[TimeSpan]::FromHours({receiver})",
            "minutes" => $"[TimeSpan]::FromMinutes({receiver})",
            "seconds" => $"[TimeSpan]::FromSeconds({receiver})",
            "days"    => $"[TimeSpan]::FromDays({receiver})",

            // ── Color methods ──────────────────────────────────────────
            "red" or "green" or "blue" or "cyan" or "yellow"
                or "magenta" or "white" or "gray"
                => TranspileColorMethod(receiver, node.Method),

            // ── Output methods ──────────────────────────────────────────
            "print" => receiver,
            "puts" => receiver,

            // ── Index access ───────────────────────────────────────────
            "[]" => $"{receiver}[{TranspileExpression(node.Args[0])}]",

            // ── Default: pass through as .NET method call ──────────────
            _ => TranspileDefaultMethod(receiver, node)
        };
    }

    /// <summary>
    /// Transpile function calls, including built-in functions (puts, warn, die, etc.)
    /// </summary>
    private string TranspileFunctionCall(FunctionCallNode node)
    {
        // ── Built-in functions ─────────────────────────────────────────
        switch (node.Name.ToLower())
        {
            case "puts":
                return TranspilePuts(node.Args);
            case "print":
                return TranspilePrint(node.Args);
            case "warn":
                if (node.Args.Count > 0)
                    return $"Write-Warning {TranspileExpression(node.Args[0])}";
                return "Write-Warning ''";
            case "die":
                if (node.Args.Count > 0)
                    return $"throw {TranspileExpression(node.Args[0])}";
                return "throw 'died'";
            case "ask":
                return TranspileAsk(node.Args);
            case "sleep":
                if (node.Args.Count > 0)
                    return $"Start-Sleep -Seconds {TranspileExpression(node.Args[0])}";
                return "Start-Sleep -Seconds 1";
            case "exit":
                if (node.Args.Count > 0)
                    return $"exit {TranspileExpression(node.Args[0])}";
                return "exit";
            case "ping":
                return TranspilePing(node.Args);
        }

        // ── Known commands → translate through CommandTranslator ───────
        if (_translator.IsKnownCommand(node.Name))
        {
            var argsStr = string.Join(" ", node.Args.Select(a =>
            {
                var expr = TranspileExpression(a);
                return expr;
            }));
            var cmdLine = string.IsNullOrEmpty(argsStr) ? node.Name : $"{node.Name} {argsStr}";
            return $"@({_translator.Translate(cmdLine) ?? cmdLine})";
        }

        // ── Regular function call ──────────────────────────────────────
        var args = string.Join(" ", node.Args.Select(TranspileExpression));
        if (string.IsNullOrEmpty(args))
            return node.Name;
        return $"{node.Name} {args}";
    }

    /// <summary>
    /// Transpile property access with special handling for env, $?, color methods, and common methods.
    /// </summary>
    private string TranspilePropertyAccess(PropertyAccessNode node)
    {
        var prop = node.Property;

        // ── env.HOME → $env:HOME ───────────────────────────────────────
        if (node.Receiver is VariableRefNode vr && vr.Name == "env")
            return $"$env:{prop}";

        // ── $?.ok? / $?.failed? / $?.code ──────────────────────────────
        if (node.Receiver is VariableRefNode vr2 && vr2.Name == "$?")
        {
            return prop switch
            {
                "ok?" => "($LASTEXITCODE -eq 0)",
                "failed?" => "($LASTEXITCODE -ne 0)",
                "code" => "$LASTEXITCODE",
                _ => "$LASTEXITCODE"
            };
        }

        // ── Time.now / Time.today / Time.utc_now ─────────────────────────
        if (node.Receiver is VariableRefNode tr && tr.Name.Equals("Time", StringComparison.OrdinalIgnoreCase))
        {
            return prop switch
            {
                "now"     => "[DateTime]::Now",
                "utc_now" => "[DateTime]::UtcNow",
                "today"   => "[DateTime]::Today",
                _ => $"[DateTime]::{CapitalizeProperty(prop)}"
            };
        }

        // ── File.xxx / Dir.xxx (property-style fallback) ───────────────
        // File/Dir methods normally require parens (→ MethodCallNode), but
        // if someone writes File.separator or Dir.current, produce a
        // reasonable .NET static property access instead of broken $File.xxx.
        if (node.Receiver is VariableRefNode fr && fr.Name.Equals("File", StringComparison.OrdinalIgnoreCase))
            return $"[System.IO.File]::{CapitalizeProperty(prop)}";

        if (node.Receiver is VariableRefNode dr && dr.Name.Equals("Dir", StringComparison.OrdinalIgnoreCase))
            return $"[System.IO.Directory]::{CapitalizeProperty(prop)}";

        // ── User class static property / enum value: ClassName.prop → [ClassName]::Prop ──
        // PascalCase only — excludes ALL_CAPS names (ARGV, PATH) which are variables
        if (node.Receiver is VariableRefNode ur
            && ur.Name.Length > 0 && char.IsUpper(ur.Name[0])
            && ur.Name.Any(char.IsLower))
        {
            return $"[{ur.Name}]::{CapitalizeProperty(prop)}";
        }

        var receiver = TranspileExpression(node.Receiver);

        // ── Duration methods (zero-arg form): 24.hours, 30.minutes ────
        if (prop is "hours" or "minutes" or "seconds" or "days")
        {
            return prop switch
            {
                "hours"   => $"[TimeSpan]::FromHours({receiver})",
                "minutes" => $"[TimeSpan]::FromMinutes({receiver})",
                "seconds" => $"[TimeSpan]::FromSeconds({receiver})",
                "days"    => $"[TimeSpan]::FromDays({receiver})",
                _ => throw new InvalidOperationException()
            };
        }

        // ── Color methods (zero-arg form) ──────────────────────────────
        if (AnsiColors.ContainsKey(prop))
            return TranspileColorMethod(receiver, prop);

        // ── Known zero-arg methods accessed as properties ──────────────
        return prop switch
        {
            "empty?" => $"(({receiver}).Length -eq 0)",
            "nil?" => $"($null -eq {receiver})",
            "strip" => $"({receiver}).Trim()",
            "lstrip" => $"({receiver}).TrimStart()",
            "rstrip" => $"({receiver}).TrimEnd()",
            "upcase" => $"({receiver}).ToUpper()",
            "downcase" => $"({receiver}).ToLower()",
            "to_i" => $"[int]({receiver})",
            "to_f" => $"[double]({receiver})",
            "to_s" => $"[string]({receiver})",
            "length" => $"({receiver}).Length",
            "size" => $"@({receiver}).Count",
            "lines" => $"({receiver}) -split '\\r?\\n'",
            "sort" => $"{receiver} | Sort-Object",
            "reverse" => $"@({receiver})[(@({receiver}).Count - 1)..0]",
            "uniq" => $"{receiver} | Select-Object -Unique",
            "count" => $"@({receiver}).Count",
            "first" => $"{receiver} | Select-Object -First 1",
            "last" => $"{receiver} | Select-Object -Last 1",
            "abs" => $"[Math]::Abs({receiver})",
            "to_currency" => $"('$' + [string]::Format('{{0:N2}}', {receiver}))",
            "to_filesize" => TranspileToFilesize(receiver),
            "to_percent" => $"([string]::Format('{{0:P1}}', {receiver}))",
            "print" => receiver,
            "puts" => receiver,
            "ok?" => $"({receiver} -eq 0)",
            "failed?" => $"({receiver} -ne 0)",
            "message" => $"{receiver}.Message",
            _ => $"{receiver}.{CapitalizeProperty(prop)}"
        };
    }

    private string TranspileInterpolatedString(InterpolatedStringNode node)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        foreach (var (isExpr, part) in node.Parts)
        {
            if (isExpr)
            {
                var expr = TranspileExpression(part);
                // Simple variable ref: #{name} → $name
                if (part is VariableRefNode)
                    sb.Append(expr);
                else
                    sb.Append($"$({expr})");
            }
            else
            {
                // Literal text — escape any $ signs that aren't ours
                var text = ((LiteralNode)part).Value;
                sb.Append(text);
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private string TranspileArrayLiteral(ArrayLiteralNode node)
    {
        var elements = string.Join(", ", node.Elements.Select(TranspileExpression));
        return $"@({elements})";
    }

    // ── Built-in Function Helpers ──────────────────────────────────────

    /// <summary>
    /// Transpile puts — detects color methods on the argument for Write-Host output.
    /// </summary>
    private string TranspilePuts(List<RushNode> args)
    {
        if (args.Count == 0) return "Write-Output ''";

        var arg = args[0];

        // puts "text".green → Write-Host "text" -ForegroundColor Green
        // Skip color detection when receiver is an uppercase class/enum name (Color.red ≠ color method)
        if (arg is PropertyAccessNode pa && AnsiColors.ContainsKey(pa.Property)
            && !(pa.Receiver is VariableRefNode pvr && pvr.Name.Length > 0 && char.IsUpper(pvr.Name[0])))
        {
            var inner = TranspileExpression(pa.Receiver);
            var color = char.ToUpper(pa.Property[0]) + pa.Property[1..];
            return $"Write-Host {inner} -ForegroundColor {color}";
        }

        // puts expr.method().color → Write-Host (transpiled_expr) -ForegroundColor Color
        if (arg is MethodCallNode mc && mc.Args.Count == 0 && mc.Block == null
            && AnsiColors.ContainsKey(mc.Method))
        {
            var inner = TranspileExpression(mc.Receiver);
            var color = char.ToUpper(mc.Method[0]) + mc.Method[1..];
            return $"Write-Host {inner} -ForegroundColor {color}";
        }

        var expr = TranspileExpression(arg);
        // Wrap in parens if expression contains PS operators that could be
        // misinterpreted as Write-Output parameters (e.g., -join, -split, -match)
        if (expr.Contains(" -") || expr.StartsWith("["))
            return $"Write-Output ({expr})";
        return $"Write-Output {expr}";
    }

    private string TranspilePrint(List<RushNode> args)
    {
        if (args.Count == 0) return "Write-Host '' -NoNewline";
        return $"Write-Host {TranspileExpression(args[0])} -NoNewline";
    }

    /// <summary>
    /// Transpile ask() — interactive prompt.
    /// ask("prompt") → Read-Host "prompt"
    /// ask("prompt", char: true) → [Console]::ReadKey() single-character input
    /// </summary>
    private string TranspileAsk(List<RushNode> args)
    {
        if (args.Count == 0) return "Read-Host";

        // Check for char: true named argument
        var hasChar = args.Any(a => a is NamedArgNode na
            && na.Name == "char"
            && na.Value is LiteralNode lit && lit.Value == "true");

        var prompt = TranspileExpression(args[0]);

        if (hasChar)
            return $"Write-Host {prompt} -NoNewline; [Console]::ReadKey($true).KeyChar";

        return $"Read-Host {prompt}";
    }

    /// <summary>
    /// Transpile ping() — cross-platform ping helper.
    /// </summary>
    private string TranspilePing(List<RushNode> args)
    {
        if (args.Count == 0) return "$false";
        var host = TranspileExpression(args[0]);

        // Check for named args
        var countArg = args.OfType<NamedArgNode>().FirstOrDefault(a => a.Name == "count");
        var count = countArg != null ? TranspileExpression(countArg.Value) : "1";

        var quietArg = args.OfType<NamedArgNode>().FirstOrDefault(a => a.Name == "quiet");
        var quiet = quietArg != null;

        if (quiet)
            return $"(Test-Connection {host} -Count {count} -Quiet)";
        return $"Test-Connection {host} -Count {count}";
    }

    // ── Stdlib Transpilation ────────────────────────────────────────────

    private string TranspileFileMethod(MethodCallNode node)
    {
        return node.Method switch
        {
            "read" => $"Get-Content {TranspileExpression(node.Args[0])} -Raw",
            "read_lines" => $"@(Get-Content {TranspileExpression(node.Args[0])})",
            "read_json" => $"(Get-Content {TranspileExpression(node.Args[0])} -Raw | ConvertFrom-Json)",
            "read_csv" => $"@(Import-Csv {TranspileExpression(node.Args[0])})",
            "write" => $"Set-Content -Path {TranspileExpression(node.Args[0])} -Value {TranspileExpression(node.Args[1])}",
            "append" => $"Add-Content -Path {TranspileExpression(node.Args[0])} -Value {TranspileExpression(node.Args[1])}",
            "exist?" or "exists" => $"(Test-Path {TranspileExpression(node.Args[0])})",
            "delete" => $"Remove-Item {TranspileExpression(node.Args[0])}",
            "size" => $"(Get-Item {TranspileExpression(node.Args[0])}).Length",
            _ => TranspileDefaultMethod(TranspileExpression(node.Receiver), node)
        };
    }

    private string TranspileDirMethod(MethodCallNode node)
    {
        return node.Method switch
        {
            "files" => TranspileDirFiles(node),
            "dirs" => node.Args.Count > 0
                ? $"Get-ChildItem {TranspileExpression(node.Args[0])} -Directory"
                : "Get-ChildItem -Directory",
            "exist?" or "exists" => $"(Test-Path {TranspileExpression(node.Args[0])} -PathType Container)",
            "mkdir" => $"$null = New-Item -ItemType Directory -Force -Path {TranspileExpression(node.Args[0])}",
            "rmdir" => $"Remove-Item -Recurse -Force {TranspileExpression(node.Args[0])}",
            _ => TranspileDefaultMethod(TranspileExpression(node.Receiver), node)
        };
    }

    private string TranspileDirFiles(MethodCallNode node)
    {
        var path = node.Args.Count > 0 ? TranspileExpression(node.Args[0]) : "'.'";

        // Check for recursive: true named arg
        var recursiveArg = node.Args.OfType<NamedArgNode>()
            .FirstOrDefault(a => a.Name == "recursive");
        var isRecursive = recursiveArg != null
            && recursiveArg.Value is LiteralNode lit
            && lit.Value == "true";

        return isRecursive
            ? $"Get-ChildItem {path} -File -Recurse"
            : $"Get-ChildItem {path} -File";
    }

    private string TranspileTimeMethod(MethodCallNode node)
    {
        return node.Method switch
        {
            "now"     => "[DateTime]::Now",
            "utc_now" => "[DateTime]::UtcNow",
            "today"   => "[DateTime]::Today",
            _ => $"[DateTime]::{CapitalizeProperty(node.Method)}({string.Join(", ", node.Args.Select(TranspileExpression))})"
        };
    }

    /// <summary>
    /// Transpile env["KEY"] → $env:KEY
    /// </summary>
    private string TranspileEnvAccess(MethodCallNode node)
    {
        var key = node.Args[0];
        if (key is LiteralNode lit)
        {
            var keyName = lit.Value.Trim('"', '\'');
            return $"$env:{keyName}";
        }
        // Dynamic key: env[variable]
        return $"[Environment]::GetEnvironmentVariable({TranspileExpression(key)})";
    }

    // ── Method Call Helpers ──────────────────────────────────────────────

    private string TranspileColorMethod(string receiver, string color)
    {
        if (!AnsiColors.TryGetValue(color, out var code))
            return $"{receiver}.{color}";

        // Use PowerShell 7's `e escape for ANSI codes
        // This makes colored strings work in variables, pipelines, and Write-Output
        return $"\"`e[{code}m$({receiver})`e[0m\"";
    }

    private string TranspileSortBy(string receiver, MethodCallNode node)
    {
        // sort_by with block: .sort_by { |x| x.name }
        if (node.Block != null)
        {
            var paramName = node.Block.Params.Count > 0 ? node.Block.Params[0] : "_";
            var body = TranspileBlockBody(node.Block);
            return $"{receiver} | Sort-Object {{ {body} }}";
        }

        // sort_by with symbol arg: .sort_by(:name)
        if (node.Args.Count > 0)
        {
            var prop = ExtractSymbolName(node.Args[0]);
            var desc = node.Args.Count > 1 && ExtractSymbolName(node.Args[1]) == "desc";
            var descFlag = desc ? " -Descending" : "";
            return $"{receiver} | Sort-Object -Property {prop}{descFlag}";
        }

        return $"{receiver} | Sort-Object";
    }

    private string TranspileFirst(string receiver, List<RushNode> args)
    {
        if (args.Count > 0)
            return $"{receiver} | Select-Object -First {TranspileExpression(args[0])}";
        return $"{receiver} | Select-Object -First 1";
    }

    private string TranspileLast(string receiver, List<RushNode> args)
    {
        if (args.Count > 0)
            return $"{receiver} | Select-Object -Last {TranspileExpression(args[0])}";
        return $"{receiver} | Select-Object -Last 1";
    }

    private string TranspileAny(string receiver, BlockLiteral? block)
    {
        if (block != null)
            return $"(@({receiver} | Where-Object {{ {TranspileBlockCondition(block)} }}).Count -gt 0)";
        return $"(@({receiver}).Count -gt 0)";
    }

    private string TranspileAll(string receiver, BlockLiteral? block)
    {
        if (block != null)
            return $"(@({receiver} | Where-Object {{ -not ({TranspileBlockCondition(block)}) }}).Count -eq 0)";
        return $"(@({receiver}).Count -gt 0)";
    }

    private string TranspileGroupBy(string receiver, List<RushNode> args)
    {
        if (args.Count > 0)
        {
            var prop = ExtractSymbolName(args[0]);
            return $"{receiver} | Group-Object -Property {prop}";
        }
        return $"{receiver} | Group-Object";
    }

    private string TranspileJoin(string receiver, List<RushNode> args)
    {
        if (args.Count > 0)
            return $"({receiver}) -join {TranspileExpression(args[0])}";
        return $"({receiver}) -join ' '";
    }

    private string TranspileInclude(string receiver, List<RushNode> args)
    {
        if (args.Count > 0)
            return $"({receiver} -contains {TranspileExpression(args[0])})";
        return "$false";
    }

    private string TranspileSplit(string receiver, List<RushNode> args)
    {
        if (args.Count > 0)
            return $"({receiver}) -split {TranspileExpression(args[0])}";
        return $"({receiver}) -split '\\s+'";
    }

    private string TranspileSkip(string receiver, List<RushNode> args)
    {
        if (args.Count > 0)
            return $"{receiver} | Select-Object -Skip {TranspileExpression(args[0])}";
        return $"{receiver} | Select-Object -Skip 1";
    }

    private string TranspileSkipWhile(string receiver, BlockLiteral? block)
    {
        if (block == null) return receiver;
        // PowerShell doesn't have skip_while natively — use a helper pattern
        var condition = TranspileBlockCondition(block);
        return $"& {{ $skipping = $true; {receiver} | ForEach-Object {{ if ($skipping -and ({condition})) {{ return }}; $skipping = $false; $_ }} }}";
    }

    // ── Regex String Methods ────────────────────────────────────────────

    private string TranspileSub(string receiver, List<RushNode> args)
    {
        if (args.Count < 2) return receiver;
        // .sub replaces first match only — needs [regex] object for .Replace(str, rep, count)
        var pattern = args[0] is RegexLiteralNode rx
            ? $"[regex]{TranspileRegexAsPattern(rx)}"
            : $"[regex]{TranspileExpression(args[0])}";
        var replacement = TranspileExpression(args[1]);
        return $"({pattern}).Replace({receiver}, {replacement}, 1)";
    }

    private string TranspileGsub(string receiver, List<RushNode> args)
    {
        if (args.Count < 2) return receiver;
        // .gsub replaces all matches — PowerShell -replace is global by default
        var pattern = args[0] is RegexLiteralNode rx
            ? TranspileRegexAsPattern(rx)
            : TranspileExpression(args[0]);
        return $"({receiver} -replace {pattern}, {TranspileExpression(args[1])})";
    }

    private string TranspileScan(string receiver, List<RushNode> args)
    {
        if (args.Count == 0) return receiver;
        var pattern = args[0] is RegexLiteralNode rx
            ? TranspileRegexAsPattern(rx)
            : TranspileExpression(args[0]);
        return $"[regex]::Matches({receiver}, {pattern}).Value";
    }

    private string TranspileMatch(string receiver, List<RushNode> args)
    {
        if (args.Count == 0) return receiver;
        var pattern = args[0] is RegexLiteralNode rx
            ? TranspileRegexAsPattern(rx)
            : TranspileExpression(args[0]);
        return $"[regex]::Match({receiver}, {pattern})";
    }

    // ── Regex Literal Transpilation ─────────────────────────────────────

    /// <summary>
    /// Transpile a regex literal as a [regex] cast expression.
    /// Used when the regex appears as a standalone expression or value.
    /// /^test/ → [regex]'^test'    /error/i → [regex]'(?i)error'
    /// </summary>
    private static string TranspileRegex(RegexLiteralNode node)
    {
        var pattern = EscapePsSingleQuote(node.Pattern);
        if (string.IsNullOrEmpty(node.Flags))
            return $"[regex]'{pattern}'";
        return $"[regex]'(?{node.Flags}){pattern}'";
    }

    /// <summary>
    /// Transpile a regex literal as a bare string pattern.
    /// Used with -match/-notmatch operators and [regex]:: static methods.
    /// /^test/ → '^test'    /error/i → '(?i)error'
    /// </summary>
    private static string TranspileRegexAsPattern(RegexLiteralNode node)
    {
        var pattern = EscapePsSingleQuote(node.Pattern);
        if (string.IsNullOrEmpty(node.Flags))
            return $"'{pattern}'";
        return $"'(?{node.Flags}){pattern}'";
    }

    /// <summary>Escape single quotes for PowerShell single-quoted strings (' → '').</summary>
    private static string EscapePsSingleQuote(string s)
    {
        return s.Replace("'", "''");
    }

    // ── Numeric Methods ─────────────────────────────────────────────────

    private string TranspileRound(string receiver, List<RushNode> args)
    {
        if (args.Count > 0)
            return $"[Math]::Round({receiver}, {TranspileExpression(args[0])})";
        return $"[Math]::Round({receiver})";
    }

    private string TranspileTimes(string receiver, BlockLiteral? block)
    {
        if (block == null) return receiver;
        var body = TranspileBlockBody(block);
        return $"0..({receiver} - 1) | ForEach-Object {{ {body} }}";
    }

    private string TranspileToCurrency(string receiver, List<RushNode> args)
    {
        // Check for pad: N named argument
        var padArg = args.OfType<NamedArgNode>().FirstOrDefault(a => a.Name == "pad");
        if (padArg != null)
        {
            var pad = TranspileExpression(padArg.Value);
            return $"('$' + [string]::Format('{{0:N2}}', {receiver})).PadLeft({pad})";
        }
        return $"('$' + [string]::Format('{{0:N2}}', {receiver}))";
    }

    private string TranspileToFilesize(string receiver)
    {
        // Human-readable file size using PowerShell logic
        return $"& {{ $s = {receiver}; if ($s -ge 1gb) {{ '{0:N1} GB' -f ($s/1gb) }} elseif ($s -ge 1mb) {{ '{0:N1} MB' -f ($s/1mb) }} elseif ($s -ge 1kb) {{ '{0:N1} KB' -f ($s/1kb) }} else {{ \"$s B\" }} }}";
    }

    private string TranspileToPercent(string receiver, List<RushNode> args)
    {
        var decimals = args.OfType<NamedArgNode>()
            .FirstOrDefault(a => a.Name == "decimals");
        var format = decimals != null ? $"N{TranspileExpression(decimals.Value)}" : "N1";
        return $"(({receiver} * 100).ToString('{format}') + '%')";
    }

    // ── Default Method Transpilation ────────────────────────────────────

    private string TranspileDefaultMethod(string receiver, MethodCallNode node)
    {
        var method = CapitalizeProperty(node.Method);
        var args = string.Join(", ", node.Args.Where(a => a is not NamedArgNode).Select(TranspileExpression));
        return $"{receiver}.{method}({args})";
    }

    // ── Block Transpilation ─────────────────────────────────────────────

    /// <summary>
    /// Transpile a block's body, replacing block params with $_ references.
    /// Used for .each, .map — where the block produces output.
    /// </summary>
    private string TranspileBlockBody(BlockLiteral block)
    {
        var paramName = block.Params.Count > 0 ? block.Params[0] : "_";
        var lines = new List<string>();
        foreach (var stmt in block.Body)
        {
            var line = TranspileNode(stmt);
            line = ReplaceBlockParam(line, paramName);
            lines.Add(line);
        }
        return string.Join("; ", lines);
    }

    /// <summary>
    /// Transpile a block's body as a condition expression.
    /// Used for .select, .reject, .any?, .all? — where the block returns bool.
    /// </summary>
    private string TranspileBlockCondition(BlockLiteral block)
    {
        var paramName = block.Params.Count > 0 ? block.Params[0] : "_";
        if (block.Body.Count == 1)
        {
            var expr = TranspileExpression(block.Body[0]);
            return ReplaceBlockParam(expr, paramName);
        }
        // Multi-statement block: last expression is the condition
        var lines = new List<string>();
        foreach (var stmt in block.Body)
        {
            var line = TranspileNode(stmt);
            line = ReplaceBlockParam(line, paramName);
            lines.Add(line);
        }
        return string.Join("; ", lines);
    }

    /// <summary>
    /// Replace block parameter variable ($paramName) with $_ in transpiled PowerShell.
    /// For example, in { |f| f.Length > 1000 }, we transpile f → $f, then replace $f → $_.
    /// </summary>
    private static string ReplaceBlockParam(string psCode, string paramName)
    {
        if (paramName == "_") return psCode;
        // Replace $paramName with $_ (word boundary aware)
        // NOTE: In .NET Regex.Replace, $_ is a special substitution token meaning
        // "entire input string". Use $$ to get a literal $ in the replacement.
        return System.Text.RegularExpressions.Regex.Replace(
            psCode,
            @"\$" + System.Text.RegularExpressions.Regex.Escape(paramName) + @"(?!\w)",
            "$$_");
    }

    // ── Operator Translation ────────────────────────────────────────────

    /// <summary>
    /// Translate Rush comparison operators to PowerShell operators.
    /// </summary>
    private static string TranslateOperator(string op) => op switch
    {
        ">" => "-gt",
        "<" => "-lt",
        ">=" => "-ge",
        "<=" => "-le",
        "==" => "-eq",
        "!=" => "-ne",
        "~" => "-match",
        "=~" => "-match",
        "!~" => "-notmatch",
        "and" => "-and",
        "or" => "-or",
        "not" => "-not",
        "&&" => "-and",
        "||" => "-or",
        "+" => "+",
        "-" => "-",
        "*" => "*",
        "/" => "/",
        "%" => "%",
        _ => op
    };

    /// <summary>
    /// Transpile a condition expression, ensuring proper PowerShell comparison syntax.
    /// </summary>
    private string TranspileCondition(RushNode node)
    {
        return TranspileExpression(node);
    }

    /// <summary>
    /// Transpile a body of statements with indentation.
    /// </summary>
    private string TranspileBody(List<RushNode> statements, string indent = "  ")
    {
        var sb = new StringBuilder();
        foreach (var stmt in statements)
        {
            var line = TranspileNode(stmt);
            if (!string.IsNullOrEmpty(line))
            {
                foreach (var subline in line.Split('\n'))
                    sb.AppendLine($"{indent}{subline}");
            }
        }
        return sb.ToString();
    }

    // ── Utility Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Extract a symbol name from a SymbolNode or string.
    /// :Name → "Name", "Name" → "Name"
    /// </summary>
    private string ExtractSymbolName(RushNode node)
    {
        if (node is SymbolNode sym)
            return sym.Name[1..]; // strip leading :
        if (node is LiteralNode lit)
            return lit.Value.Trim('"', '\'');
        return TranspileExpression(node);
    }

    /// <summary>
    /// Check if a receiver node is a stdlib class reference (File, Dir, etc.)
    /// </summary>
    private static bool IsStdlibReceiver(RushNode receiver, string name)
    {
        return receiver is VariableRefNode vr
            && vr.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Convert Rush snake_case property names to .NET PascalCase.
    /// Handles: snake_case → PascalCase, lowercase → Capitalized, UPPERCASE → unchanged.
    /// PowerShell is case-insensitive for property names, but PascalCase is conventional.
    /// </summary>
    /// <summary>
    /// Resolve arguments for class method calls, handling named args by matching
    /// them to constructor parameter positions. For .new() calls with named args,
    /// looks up the class definition and reorders args to match constructor params.
    /// </summary>
    private string ResolveClassCallArgs(string className, string method, List<RushNode> args)
    {
        var hasNamedArgs = args.Any(a => a is NamedArgNode);

        // No named args — simple positional pass-through
        if (!hasNamedArgs)
            return string.Join(", ", args.Select(TranspileExpression));

        // Named args on .new() — look up constructor params and reorder
        if (method.Equals("new", StringComparison.OrdinalIgnoreCase)
            && _classDefinitions.TryGetValue(className, out var classDef)
            && classDef.Constructor != null)
        {
            var ctorParams = classDef.Constructor.Params;
            var positionalArgs = args.Where(a => a is not NamedArgNode).ToList();
            var namedArgs = args.OfType<NamedArgNode>().ToDictionary(a => a.Name, a => a.Value);

            var result = new List<string>();
            for (int i = 0; i < ctorParams.Count; i++)
            {
                if (i < positionalArgs.Count)
                {
                    // Positional arg fills this slot
                    result.Add(TranspileExpression(positionalArgs[i]));
                }
                else if (namedArgs.TryGetValue(ctorParams[i].Name, out var namedVal))
                {
                    // Named arg matched to this parameter
                    result.Add(TranspileExpression(namedVal));
                }
                else if (ctorParams[i].DefaultValue != null)
                {
                    // Use the default value
                    result.Add(TranspileExpression(ctorParams[i].DefaultValue!));
                }
                // else: missing arg — PS7 will report the error
            }
            return string.Join(", ", result);
        }

        // Fallback: strip named args (non-.new() calls or class not found)
        return string.Join(", ", args.Where(a => a is not NamedArgNode).Select(TranspileExpression));
    }

    /// <summary>Map Rush type names to PowerShell type accelerators.</summary>
    private static string MapRushType(string rushType) => rushType.ToLower() switch
    {
        "string" => "string",
        "int" or "integer" => "int",
        "float" or "double" => "double",
        "bool" or "boolean" => "bool",
        "array" => "object[]",
        "hash" or "hashtable" => "hashtable",
        _ => rushType // PascalCase class name passes through as-is
    };

    private static string CapitalizeProperty(string name)
    {
        // Predicate methods: strip trailing ? for .NET mapping
        if (name.EndsWith('?'))
        {
            var baseName = name[..^1];
            return "Is" + CapitalizeProperty(baseName);
        }

        // snake_case → PascalCase
        if (name.Contains('_'))
        {
            return string.Join("", name.Split('_')
                .Where(s => s.Length > 0)
                .Select(s => char.ToUpper(s[0]) + s[1..]));
        }

        // Already starts with uppercase → leave it alone (e.g., IPAddress, Name)
        if (name.Length > 0 && char.IsUpper(name[0]))
            return name;

        // lowercase → capitalize first letter (name → Name)
        if (name.Length > 0)
            return char.ToUpper(name[0]) + name[1..];

        return name;
    }
}
