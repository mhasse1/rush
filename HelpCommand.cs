using System.Reflection;
using System.Text;

/// <summary>
/// Topic-based help system backed by embedded rush-help.yaml.
/// Each topic is a self-contained block designed for minimal token consumption by LLMs.
/// Works in both interactive REPL and rush --llm mode.
/// </summary>
static class HelpCommand
{
    private static string? _cachedYaml;
    private static Dictionary<string, string>? _cachedTopics;

    /// <summary>
    /// Returns the raw embedded rush-help.yaml content, cached after first load.
    /// </summary>
    internal static string GetEmbeddedHelp()
    {
        if (_cachedYaml != null) return _cachedYaml;

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("rush-help.yaml", StringComparison.OrdinalIgnoreCase));

            if (resourceName != null)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    _cachedYaml = reader.ReadToEnd();
                    return _cachedYaml;
                }
            }
        }
        catch { }

        _cachedYaml = "";
        return _cachedYaml;
    }

    /// <summary>
    /// Parse topics lazily from the YAML. Each top-level key (no leading whitespace,
    /// ending with ':') is a topic name. The block runs until the next top-level key
    /// or EOF. Comment-only lines before the first topic are skipped.
    /// </summary>
    internal static Dictionary<string, string> GetTopics()
    {
        if (_cachedTopics != null) return _cachedTopics;

        _cachedTopics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var yaml = GetEmbeddedHelp();
        if (string.IsNullOrEmpty(yaml)) return _cachedTopics;

        var lines = yaml.Split('\n');
        string? currentTopic = null;
        var currentBlock = new StringBuilder();

        foreach (var line in lines)
        {
            // Top-level key: starts at column 0, is a word followed by ':'
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith("#"))
            {
                // Save previous topic
                if (currentTopic != null)
                {
                    _cachedTopics[currentTopic] = currentBlock.ToString().TrimEnd();
                }

                var colonIdx = line.IndexOf(':');
                if (colonIdx > 0)
                {
                    currentTopic = line[..colonIdx].Trim();
                    currentBlock.Clear();
                    // Include the header line itself
                    currentBlock.AppendLine(line);
                }
                else
                {
                    currentTopic = null;
                }
            }
            else if (currentTopic != null)
            {
                currentBlock.AppendLine(line);
            }
        }

        // Save last topic
        if (currentTopic != null)
        {
            _cachedTopics[currentTopic] = currentBlock.ToString().TrimEnd();
        }

        return _cachedTopics;
    }

    /// <summary>
    /// Get a list of available topic names.
    /// </summary>
    internal static IReadOnlyList<string> GetTopicNames()
    {
        return GetTopics().Keys.OrderBy(k => k).ToList();
    }

    /// <summary>
    /// Get a specific topic's content, or null if not found.
    /// </summary>
    internal static string? GetTopic(string name)
    {
        var topics = GetTopics();
        return topics.TryGetValue(name, out var content) ? content : null;
    }

    /// <summary>
    /// Execute the help command. Returns the output string.
    /// </summary>
    internal static string Execute(string? topic = null)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return FormatTopicList();
        }

        topic = topic.Trim();
        var content = GetTopic(topic);
        if (content != null)
        {
            return content;
        }

        // Try fuzzy match — check if any topic starts with the query
        var topics = GetTopics();
        var match = topics.Keys
            .FirstOrDefault(k => k.StartsWith(topic, StringComparison.OrdinalIgnoreCase));

        if (match != null)
        {
            return topics[match];
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Unknown topic: {topic}");
        sb.AppendLine();
        sb.Append(FormatTopicList());
        return sb.ToString();
    }

    private static string FormatTopicList()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Rush Help — available topics:");
        sb.AppendLine();

        var names = GetTopicNames();

        // Group into categories for readability
        var stdlib = new[] { "file", "dir", "time" };
        var types = new[] { "strings", "arrays", "hashes", "classes", "enums" };
        var flow = new[] { "functions", "loops", "control-flow", "errors" };
        var data = new[] { "pipelines", "pipeline-ops", "regex", "objectify", "sql" };
        var other = new[] { "platforms", "llm-mode", "xref" };

        void PrintGroup(string label, string[] items)
        {
            var available = items.Where(i => names.Contains(i)).ToArray();
            if (available.Length == 0) return;
            sb.AppendLine($"  {label,-14} {string.Join(", ", available)}");
        }

        PrintGroup("Stdlib:", stdlib);
        PrintGroup("Types:", types);
        PrintGroup("Flow:", flow);
        PrintGroup("Data:", data);
        PrintGroup("Other:", other);

        // Any topics not in our categories
        var all = stdlib.Concat(types).Concat(flow).Concat(data).Concat(other).ToHashSet();
        var uncategorized = names.Where(n => !all.Contains(n)).ToArray();
        if (uncategorized.Length > 0)
        {
            sb.AppendLine($"  {"Other:",-14} {string.Join(", ", uncategorized)}");
        }

        sb.AppendLine();
        sb.Append("Usage: help <topic>");
        return sb.ToString();
    }
}
