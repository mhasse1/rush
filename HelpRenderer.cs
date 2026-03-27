using Rush;
using System.Text.RegularExpressions;

/// <summary>
/// Renders help topics with color and formatting for the interactive REPL.
/// Parses the raw YAML topic content and writes directly to console.
/// LLM mode bypasses this entirely — it uses raw YAML from HelpCommand.Execute().
/// </summary>
static class HelpRenderer
{
    /// <summary>
    /// Render a help topic to the console with formatting and color.
    /// Falls back to plain text if topic is not found.
    /// </summary>
    internal static void Render(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            RenderTopicList();
            return;
        }

        topic = topic.Trim();
        var content = HelpCommand.GetTopic(topic);
        if (content == null)
        {
            // Try fuzzy match
            var match = HelpCommand.GetTopicNames()
                .FirstOrDefault(k => k.StartsWith(topic, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                topic = match;
                content = HelpCommand.GetTopic(match);
            }
        }

        if (content == null)
        {
            Console.ForegroundColor = Theme.Current.Error;
            Console.WriteLine($"Unknown topic: {topic}");
            Console.ResetColor();
            Console.WriteLine();
            RenderTopicList();
            return;
        }

        RenderYamlTopic(topic, content);
    }

    private static void RenderTopicList()
    {
        Console.ForegroundColor = Theme.Current.Banner;
        Console.WriteLine("Rush Help — available topics:");
        Console.ResetColor();
        Console.WriteLine();

        var names = HelpCommand.GetTopicNames();

        var groups = new (string Label, string[] Items)[]
        {
            ("Stdlib", new[] { "file", "dir", "time" }),
            ("Types", new[] { "strings", "arrays", "hashes", "classes", "enums" }),
            ("Flow", new[] { "functions", "loops", "control-flow", "errors" }),
            ("Data", new[] { "pipelines", "pipeline-ops", "regex", "objectify", "sql" }),
            ("Shell", new[] { "config", "platforms", "llm-mode", "mcp", "xref", "known-issues" }),
        };

        foreach (var (label, items) in groups)
        {
            var available = items.Where(i => names.Contains(i)).ToArray();
            if (available.Length == 0) continue;
            Console.ForegroundColor = Theme.Current.Accent;
            Console.Write($"  {label,-10}");
            Console.ResetColor();
            Console.WriteLine(string.Join("  ", available));
        }

        // Uncategorized
        var all = groups.SelectMany(g => g.Items).ToHashSet();
        var uncategorized = names.Where(n => !all.Contains(n)).ToArray();
        if (uncategorized.Length > 0)
        {
            Console.ForegroundColor = Theme.Current.Accent;
            Console.Write($"  {"Other",-10}");
            Console.ResetColor();
            Console.WriteLine(string.Join("  ", uncategorized));
        }

        Console.WriteLine();
        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine("  Usage: help <topic>  or  <keyword> --help");
        Console.ResetColor();
    }

    /// <summary>
    /// Parse and render a YAML topic block with colors and formatting.
    /// </summary>
    private static void RenderYamlTopic(string topicName, string content)
    {
        var lines = content.Split('\n');
        int i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimEnd();

            // Skip empty lines (but emit spacing)
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                Console.WriteLine();
                i++;
                continue;
            }

            // Top-level key (no leading whitespace or 2-space indent with colon)
            if (IsTopLevelKey(trimmed, out var key, out var inlineValue))
            {
                if (key == "summary")
                {
                    // Summary: show as topic header
                    Console.ForegroundColor = Theme.Current.Banner;
                    Console.Write($"  {topicName}");
                    Console.ForegroundColor = Theme.Current.Muted;
                    Console.WriteLine($" — {StripQuotes(inlineValue)}");
                    Console.ResetColor();
                    i++;
                    continue;
                }

                // Section header
                Console.WriteLine();
                Console.ForegroundColor = Theme.Current.Accent;
                Console.WriteLine($"  {FormatSectionName(key)}");
                Console.ResetColor();

                i++;

                // Determine what follows: list of dicts, list of strings, nested map
                if (i < lines.Length)
                {
                    var nextLine = lines[i].TrimEnd();

                    if (nextLine.TrimStart().StartsWith("- {") || nextLine.TrimStart().StartsWith("- { "))
                    {
                        // List of inline dicts — render as table
                        i = RenderInlineDictList(lines, i, key);
                    }
                    else if (nextLine.TrimStart().StartsWith("- name:"))
                    {
                        // List of method entries
                        i = RenderMethodList(lines, i);
                    }
                    else if (nextLine.TrimStart().StartsWith("- "))
                    {
                        // Simple string list
                        i = RenderStringList(lines, i);
                    }
                    else if (IsNestedMapKey(nextLine))
                    {
                        // Nested map (like config layers)
                        i = RenderNestedMap(lines, i);
                    }
                    else if (!string.IsNullOrWhiteSpace(inlineValue))
                    {
                        // Inline value already shown, skip
                    }
                    else
                    {
                        // Single inline value on next line
                        Console.ForegroundColor = Theme.Current.Muted;
                        Console.WriteLine($"    {nextLine.Trim()}");
                        Console.ResetColor();
                        i++;
                    }
                }
                continue;
            }

            // Fallback — just print the line
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine($"  {trimmed}");
            Console.ResetColor();
            i++;
        }

        Console.WriteLine();
    }

    // ── List Renderers ──────────────────────────────────────────────────

    private static int RenderStringList(string[] lines, int i)
    {
        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd();
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("- ")) break;

            var value = StripQuotes(trimmed[2..].Trim());

            // Check if it looks like "command — description" or "thing -- description"
            var dashIdx = value.IndexOf(" — ");
            if (dashIdx < 0) dashIdx = value.IndexOf(" -- ");

            if (dashIdx > 0)
            {
                var left = value[..dashIdx];
                var right = value[(dashIdx + 3)..].Trim();
                Console.Write("    ");
                Console.ForegroundColor = Theme.Current.Banner;
                Console.Write(left);
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine($"  {right}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = Theme.Current.Muted;
                Console.WriteLine($"    {value}");
                Console.ResetColor();
            }

            i++;
        }
        return i;
    }

    private static int RenderInlineDictList(string[] lines, int i, string sectionKey)
    {
        // Collect all entries
        var entries = new List<Dictionary<string, string>>();
        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("- {")) break;

            var dict = ParseInlineDict(trimmed);
            if (dict.Count > 0) entries.Add(dict);
            i++;
        }

        if (entries.Count == 0) return i;

        // Determine column keys
        var keys = entries[0].Keys.ToList();
        if (keys.Count < 2) return i;

        // Calculate column widths
        var col1Key = keys[0];
        var col2Key = keys[1];
        var col1Width = entries.Max(e => e.GetValueOrDefault(col1Key, "").Length);
        col1Width = Math.Max(col1Width, col1Key.Length);
        col1Width = Math.Min(col1Width, 45); // cap width

        // Render as table
        foreach (var entry in entries)
        {
            var left = entry.GetValueOrDefault(col1Key, "");
            var right = entry.GetValueOrDefault(col2Key, "");
            Console.Write("    ");
            Console.ForegroundColor = Theme.Current.Banner;
            Console.Write(left.PadRight(col1Width + 2));
            Console.ForegroundColor = Theme.Current.Muted;
            Console.WriteLine(right);
            Console.ResetColor();
        }
        return i;
    }

    private static int RenderMethodList(string[] lines, int i)
    {
        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("- name:") && !trimmed.StartsWith("name:"))
            {
                // Check if we're still inside a method entry (indented properties)
                if (trimmed.StartsWith("returns:") || trimmed.StartsWith("transpiles_to:") ||
                    trimmed.StartsWith("example:") || trimmed.StartsWith("note:"))
                {
                    // Skip — handled below
                }
                else if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    i++;
                    continue;
                }
                else if (!lines[i].StartsWith("    ") && !lines[i].StartsWith("\t"))
                {
                    break; // Left the method list
                }
            }

            if (trimmed.StartsWith("- name:"))
            {
                var name = StripQuotes(trimmed["- name:".Length..].Trim());
                Console.Write("    ");
                Console.ForegroundColor = Theme.Current.Banner;
                Console.Write(name);
                Console.ResetColor();

                // Look ahead for returns, example, note
                i++;
                string? returns = null, example = null, note = null;
                while (i < lines.Length)
                {
                    var inner = lines[i].TrimStart();
                    if (inner.StartsWith("returns:"))
                    {
                        returns = StripQuotes(inner["returns:".Length..].Trim());
                        i++;
                    }
                    else if (inner.StartsWith("transpiles_to:"))
                    {
                        i++; // Skip — internal detail
                    }
                    else if (inner.StartsWith("example:"))
                    {
                        var val = inner["example:".Length..].Trim();
                        if (val == "|")
                        {
                            // Multi-line example
                            i++;
                            var exLines = new List<string>();
                            while (i < lines.Length && (lines[i].StartsWith("        ") || lines[i].StartsWith("\t\t") || string.IsNullOrWhiteSpace(lines[i])))
                            {
                                if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }
                                exLines.Add(lines[i].TrimStart());
                                i++;
                            }
                            example = string.Join("\n", exLines);
                        }
                        else
                        {
                            example = StripQuotes(val);
                            i++;
                        }
                    }
                    else if (inner.StartsWith("note:"))
                    {
                        note = StripQuotes(inner["note:".Length..].Trim());
                        i++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (returns != null)
                {
                    Console.ForegroundColor = Theme.Current.Muted;
                    Console.Write($"  → {returns}");
                }
                Console.WriteLine();

                if (example != null)
                {
                    foreach (var exLine in example.Split('\n'))
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"      {exLine}");
                    }
                    Console.ResetColor();
                }

                if (note != null)
                {
                    Console.ForegroundColor = Theme.Current.Muted;
                    Console.WriteLine($"      ({note})");
                    Console.ResetColor();
                }

                continue;
            }

            i++;
        }
        return i;
    }

    private static int RenderNestedMap(string[] lines, int i)
    {
        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { Console.WriteLine(); i++; continue; }

            var trimmed = line.TrimStart();
            var indent = line.Length - line.TrimStart().Length;

            // Top-level key of the nested entry (like "config_json:")
            if (indent <= 4 && trimmed.EndsWith(':') && !trimmed.StartsWith("- "))
            {
                var mapKey = trimmed[..^1].Trim();
                Console.ForegroundColor = Theme.Current.Banner;
                Console.WriteLine($"    {FormatSectionName(mapKey)}");
                Console.ResetColor();
                i++;
                continue;
            }

            // Properties within the nested entry
            if (indent > 4 && trimmed.Contains(':'))
            {
                var colonIdx = trimmed.IndexOf(':');
                var propKey = trimmed[..colonIdx].Trim();
                var propVal = StripQuotes(trimmed[(colonIdx + 1)..].Trim());

                if (!string.IsNullOrEmpty(propVal))
                {
                    Console.ForegroundColor = Theme.Current.Muted;
                    Console.Write($"      {propKey}: ");
                    Console.ResetColor();
                    Console.WriteLine(propVal);
                }
                i++;
                continue;
            }

            // If we've un-indented back to a top-level key, stop
            if (indent <= 2 && !string.IsNullOrWhiteSpace(trimmed))
                break;

            i++;
        }
        return i;
    }

    // ── Parsing Helpers ─────────────────────────────────────────────────

    private static bool IsTopLevelKey(string trimmed, out string key, out string inlineValue)
    {
        key = ""; inlineValue = "";
        // 2-space indented keys (within a topic block)
        var stripped = trimmed.TrimStart();
        if (stripped.Length == 0 || stripped.StartsWith("- ") || stripped.StartsWith("#"))
            return false;

        var colonIdx = stripped.IndexOf(':');
        if (colonIdx <= 0) return false;

        // Must not be deeply indented (topic content is 2-space indented)
        var indent = trimmed.Length - stripped.Length;
        if (indent > 4) return false;

        key = stripped[..colonIdx].Trim();
        var rest = stripped[(colonIdx + 1)..].Trim();
        inlineValue = StripQuotes(rest);
        return true;
    }

    private static bool IsNestedMapKey(string line)
    {
        var trimmed = line.TrimStart();
        var indent = line.Length - trimmed.Length;
        return indent >= 4 && trimmed.EndsWith(':') && !trimmed.StartsWith("- ");
    }

    private static Dictionary<string, string> ParseInlineDict(string line)
    {
        var result = new Dictionary<string, string>();
        // Parse "- { key: 'value', key2: 'value2' }" or "- { key: \"value\", ... }"
        var match = Regex.Match(line, @"\{(.+)\}");
        if (!match.Success) return result;

        var inner = match.Groups[1].Value;
        // Split on comma, but respect quotes
        var pairs = SplitDictPairs(inner);
        foreach (var pair in pairs)
        {
            var colonIdx = pair.IndexOf(':');
            if (colonIdx <= 0) continue;
            var k = pair[..colonIdx].Trim();
            var v = StripQuotes(pair[(colonIdx + 1)..].Trim());
            result[k] = v;
        }
        return result;
    }

    private static List<string> SplitDictPairs(string s)
    {
        var pairs = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuote = false;
        char quoteChar = ' ';

        foreach (var c in s)
        {
            if (!inQuote && (c == '\'' || c == '"'))
            {
                inQuote = true;
                quoteChar = c;
                current.Append(c);
            }
            else if (inQuote && c == quoteChar)
            {
                inQuote = false;
                current.Append(c);
            }
            else if (!inQuote && c == ',')
            {
                pairs.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) pairs.Add(current.ToString().Trim());
        return pairs;
    }

    private static string StripQuotes(string s)
    {
        if (s.Length >= 2)
        {
            if ((s[0] == '\'' && s[^1] == '\'') || (s[0] == '"' && s[^1] == '"'))
                return s[1..^1];
        }
        return s;
    }

    private static string FormatSectionName(string key)
    {
        // Convert snake_case and kebab-case to Title Case
        return string.Join(" ", key.Split('_', '-')
            .Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));
    }
}
