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
        IfNode i => TranspileIf(i),
        PostfixIfNode p => TranspilePostfixIf(p),
        ForNode f => TranspileFor(f),
        WhileNode w => TranspileWhile(w),
        FunctionDefNode d => TranspileFunctionDef(d),
        ReturnNode r => TranspileReturn(r),
        TryNode t => TranspileTry(t),
        CaseNode c => TranspileCase(c),
        ShellPassthroughNode s => TranspileShellPassthrough(s),
        _ => TranspileExpression(node)
    };

    // ── Statements ──────────────────────────────────────────────────────

    private string TranspileAssignment(AssignmentNode node)
    {
        return $"${node.Name} = {TranspileExpression(node.Value)}";
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
        VariableRefNode v => $"${v.Name}",
        BinaryOpNode b => TranspileBinary(b),
        UnaryOpNode u => TranspileUnary(u),
        MethodCallNode m => TranspileMethodCall(m),
        FunctionCallNode f => TranspileFunctionCall(f),
        PropertyAccessNode p => TranspilePropertyAccess(p),
        InterpolatedStringNode s => TranspileInterpolatedString(s),
        RangeNode r => $"{TranspileExpression(r.Start)}..{TranspileExpression(r.End)}",
        SymbolNode sym => $"'{sym.Name[1..]}'", // :name → 'name'
        ArrayLiteralNode a => TranspileArrayLiteral(a),
        AssignmentNode a => TranspileAssignment(a),
        ShellPassthroughNode s => TranspileShellPassthrough(s),
        _ => $"<# unsupported: {node.GetType().Name} #>"
    };

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
        var right = TranspileExpression(node.Right);
        var op = TranslateOperator(node.Op);
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

    /// <summary>
    /// Transpile Rush method calls to PowerShell pipeline operators.
    /// This is where .each, .select, .map, etc. become PS cmdlets.
    /// </summary>
    private string TranspileMethodCall(MethodCallNode node)
    {
        var receiver = TranspileExpression(node.Receiver);

        return node.Method switch
        {
            "each" => $"{receiver} | ForEach-Object {{ {TranspileBlockBody(node.Block!)} }}",
            "select" => $"{receiver} | Where-Object {{ {TranspileBlockCondition(node.Block!)} }}",
            "reject" => $"{receiver} | Where-Object {{ -not ({TranspileBlockCondition(node.Block!)}) }}",
            "map" => $"{receiver} | ForEach-Object {{ {TranspileBlockBody(node.Block!)} }}",
            "flat_map" => $"{receiver} | ForEach-Object {{ {TranspileBlockBody(node.Block!)} }}",
            "sort_by" => TranspileSortBy(receiver, node.Args),
            "first" => TranspileFirst(receiver, node.Args),
            "last" => TranspileLast(receiver, node.Args),
            "count" => $"@({receiver}).Count",
            "any?" => TranspileAny(receiver, node.Block),
            "all?" => TranspileAll(receiver, node.Block),
            "group_by" => TranspileGroupBy(receiver, node.Args),
            "uniq" => $"{receiver} | Select-Object -Unique",
            "reverse" => $"@({receiver})[({@receiver}).Count..0]",
            "join" => TranspileJoin(receiver, node.Args),
            "to_json" => $"{receiver} | ConvertTo-Json -Depth 5",
            "to_csv" => $"{receiver} | ConvertTo-Csv -NoTypeInformation",
            "include?" => TranspileInclude(receiver, node.Args),
            "[]" => $"{receiver}[{TranspileExpression(node.Args[0])}]",
            _ => $"{receiver}.{node.Method}({string.Join(", ", node.Args.Select(TranspileExpression))})"
        };
    }

    private string TranspileFunctionCall(FunctionCallNode node)
    {
        // Check if it's a known command name — translate through CommandTranslator
        if (_translator.IsKnownCommand(node.Name))
        {
            var argsStr = string.Join(" ", node.Args.Select(a =>
            {
                var expr = TranspileExpression(a);
                // Strip $ prefix for command args that are string literals
                return expr;
            }));
            var cmdLine = string.IsNullOrEmpty(argsStr) ? node.Name : $"{node.Name} {argsStr}";
            return $"@({_translator.Translate(cmdLine) ?? cmdLine})";
        }

        // Regular function call
        var args = string.Join(" ", node.Args.Select(TranspileExpression));
        if (string.IsNullOrEmpty(args))
            return node.Name;
        return $"{node.Name} {args}";
    }

    private string TranspilePropertyAccess(PropertyAccessNode node)
    {
        return $"{TranspileExpression(node.Receiver)}.{node.Property}";
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

    // ── Method Call Helpers ──────────────────────────────────────────────

    private string TranspileSortBy(string receiver, List<RushNode> args)
    {
        if (args.Count == 0)
            return $"{receiver} | Sort-Object";

        var prop = ExtractSymbolName(args[0]);
        var desc = args.Count > 1 && ExtractSymbolName(args[1]) == "desc";
        var descFlag = desc ? " -Descending" : "";
        return $"{receiver} | Sort-Object -Property {prop}{descFlag}";
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
        return System.Text.RegularExpressions.Regex.Replace(
            psCode,
            @"\$" + System.Text.RegularExpressions.Regex.Escape(paramName) + @"(?!\w)",
            "$_");
    }

    // ── Operator Translation ────────────────────────────────────────────

    /// <summary>
    /// Translate Rush comparison operators to PowerShell operators.
    /// Reuses the same logic as CommandTranslator.TranslateWhereOperator().
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
        "!~" => "-notmatch",
        "and" => "-and",
        "or" => "-or",
        "not" => "-not",
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
}
