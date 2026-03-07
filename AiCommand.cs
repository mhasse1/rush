using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rush;

/// <summary>
/// Orchestrates AI requests: parses arguments, builds context, streams responses.
/// Entry point for the `ai` builtin command.
/// </summary>
public static class AiCommand
{
    // ── Built-in providers ────────────────────────────────────────────
    private static readonly Dictionary<string, IAiProvider> BuiltinProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] = new AnthropicProvider(),
        ["openai"] = new OpenAiProvider(),
        ["gemini"] = new GeminiProvider(),
        ["ollama"] = new OllamaProvider(),
    };

    // ── Last response storage (for ai --exec) ────────────────────────
    private static string? _lastResponse;
    private static string? _lastCodeContent;

    /// <summary>
    /// Get the last AI response for execution.
    /// Prefers extracted code block content over the full response.
    /// </summary>
    internal static string? GetLastResponse()
        => _lastCodeContent ?? _lastResponse;

    /// <summary>
    /// Execute an AI prompt. Streams tokens to stdout as they arrive.
    /// Returns (success, fullResponse) for pipeline integration.
    /// </summary>
    public static async Task<(bool success, string response)> ExecuteAsync(
        string arguments, string? pipedInput, RushConfig config,
        IReadOnlyList<string> history, CancellationToken ct)
    {
        try
        {
            // ── Parse arguments ──────────────────────────────────────
            var (prompt, providerName, modelOverride, systemOverride, isAgent) = ParseArgs(arguments);

            if (string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(pipedInput))
            {
                WriteError(isAgent ? "usage: ai --agent \"your task\"" : "usage: ai \"your question\"");
                return (false, "");
            }

            // ── Resolve provider ─────────────────────────────────────
            var resolvedName = providerName ?? config.AiProvider ?? "anthropic";
            var providers = LoadAllProviders();

            if (!providers.TryGetValue(resolvedName, out var provider))
            {
                var validNames = string.Join(", ", providers.Keys.OrderBy(k => k));
                WriteError($"unknown provider \"{resolvedName}\". Valid: {validNames}");
                return (false, "");
            }

            // ── Resolve model ────────────────────────────────────────
            var configModel = config.AiModel;
            if (string.Equals(configModel, "auto", StringComparison.OrdinalIgnoreCase))
                configModel = "";
            var model = !string.IsNullOrEmpty(modelOverride) ? modelOverride
                : !string.IsNullOrEmpty(configModel) ? configModel
                : provider.DefaultModel;

            // ── Resolve API key ──────────────────────────────────────
            var apiKey = "";
            if (!string.IsNullOrEmpty(provider.ApiKeyEnvVar))
            {
                apiKey = Environment.GetEnvironmentVariable(provider.ApiKeyEnvVar) ?? "";
                if (string.IsNullOrEmpty(apiKey))
                {
                    WriteError($"no API key. Run: set --secret {provider.ApiKeyEnvVar} \"your-key\"");
                    return (false, "");
                }
            }

            // ── Build prompts ────────────────────────────────────────
            var systemPrompt = systemOverride ?? BuildSystemPrompt(history);
            var userMessage = BuildUserMessage(prompt, pipedInput);

            // ── Stream response (with fence stripping) ────────────────
            var stripper = new FenceStripper();
            await foreach (var token in provider.StreamAsync(systemPrompt, userMessage, model, apiKey, ct))
            {
                var output = stripper.Process(token);
                if (output.Length > 0) Console.Write(output);
            }
            var trailing = stripper.Flush();
            if (trailing.Length > 0) Console.Write(trailing);
            Console.WriteLine();

            // Store for ai --exec
            var fullResponse = stripper.DisplayContent;
            _lastResponse = fullResponse;
            _lastCodeContent = stripper.CodeContent;

            return (true, fullResponse);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C — graceful cancel
            Console.WriteLine();
            return (false, "");
        }
        catch (AiException ex)
        {
            WriteError(ex.Message);
            return (false, "");
        }
        catch (HttpRequestException ex)
        {
            WriteError($"network error: {ex.Message}");
            return (false, "");
        }
        catch (Exception ex)
        {
            WriteError($"error: {ex.Message}");
            return (false, "");
        }
    }

    // ── Agent Mode Entry Point ───────────────────────────────────────

    /// <summary>
    /// Execute AI agent mode: autonomous multi-turn task execution.
    /// Called from Program.cs with a LlmMode instance for command execution.
    /// </summary>
    public static async Task<(bool success, string response)> ExecuteAgentAsync(
        string arguments, LlmMode llm, RushConfig config,
        IReadOnlyList<string> history, CancellationToken ct)
    {
        var (prompt, providerName, modelOverride, _, _) = ParseArgs(arguments);

        // Agent-only flags: --verbose (inline JSON) and --debug (log file)
        var tokens = TokenizeArgs(arguments);
        bool verbose = tokens.Any(t => t.Equals("--verbose", StringComparison.OrdinalIgnoreCase));
        bool debug = tokens.Any(t => t.Equals("--debug", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(prompt))
        {
            WriteError("usage: ai --agent [--verbose] [--debug] \"your task\"");
            return (false, "");
        }

        return await AiAgent.RunAsync(prompt, llm, providerName, modelOverride,
            history, config, verbose, debug, ct);
    }

    // ── Argument Parsing ──────────────────────────────────────────────

    /// <summary>
    /// Parse ai arguments: extract prompt, --provider, --model, --system, --agent flags.
    /// Supports: ai "prompt"  ai --agent "task"  ai --provider ollama "prompt"
    /// </summary>
    internal static (string prompt, string? provider, string? model, string? system, bool agent) ParseArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return ("", null, null, null, false);

        string? provider = null;
        string? model = null;
        string? system = null;
        bool agent = false;
        var promptParts = new List<string>();

        var tokens = TokenizeArgs(args);
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Equals("--provider", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
            {
                provider = tokens[++i];
            }
            else if (t.Equals("--model", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
            {
                model = tokens[++i];
            }
            else if (t.Equals("--system", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
            {
                system = tokens[++i];
            }
            else if (t.Equals("--agent", StringComparison.OrdinalIgnoreCase))
            {
                agent = true;
            }
            else
            {
                promptParts.Add(t);
            }
        }

        return (string.Join(" ", promptParts), provider, model, system, agent);
    }

    /// <summary>
    /// Tokenize args respecting quoted strings.
    /// "hello world" stays as one token, --flag value as two tokens.
    /// </summary>
    internal static List<string> TokenizeArgs(string input)
    {
        var tokens = new List<string>();
        int i = 0;

        while (i < input.Length)
        {
            // Skip whitespace
            while (i < input.Length && char.IsWhiteSpace(input[i])) i++;
            if (i >= input.Length) break;

            if (input[i] is '"' or '\'')
            {
                // Quoted string — extract content without quotes
                char quote = input[i];
                i++;
                int start = i;
                while (i < input.Length && input[i] != quote) i++;
                tokens.Add(input[start..i]);
                if (i < input.Length) i++; // skip closing quote
            }
            else
            {
                // Unquoted word
                int start = i;
                while (i < input.Length && !char.IsWhiteSpace(input[i])) i++;
                tokens.Add(input[start..i]);
            }
        }

        return tokens;
    }

    // ── System Prompt ─────────────────────────────────────────────────

    /// <summary>
    /// Build the system prompt with context: OS, cwd, history, Rush language spec.
    /// Designed as a separate method so it can be swapped for an embedding-based
    /// approach in v2.
    /// </summary>
    internal static string BuildSystemPrompt(IReadOnlyList<string> history)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are an AI assistant embedded in Rush shell (v{RushVersion.Full}).");
        sb.AppendLine($"Platform: {GetDetailedOS()}");
        sb.AppendLine($"Directory: {Environment.CurrentDirectory}");

        // Last 10 commands from history
        if (history.Count > 0)
        {
            sb.AppendLine("Recent commands:");
            var start = Math.Max(0, history.Count - 10);
            for (int i = start; i < history.Count; i++)
            {
                sb.AppendLine($"  {i - start + 1}. {history[i]}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Respond concisely. Use Unix-style commands compatible with Rush when suggesting commands.");
        sb.AppendLine("Do not use markdown code fences — output goes directly to a terminal.");
        sb.AppendLine("Prefer platform-appropriate commands (e.g. brew on macOS, apt on Ubuntu).");

        // Embed the Rush language spec
        var spec = GetEmbeddedSpec();
        if (!string.IsNullOrEmpty(spec))
        {
            sb.AppendLine();
            sb.AppendLine("Rush Language Specification:");
            sb.AppendLine(spec);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Build user message, prepending piped input if present.
    /// </summary>
    internal static string BuildUserMessage(string prompt, string? pipedInput)
    {
        if (string.IsNullOrEmpty(pipedInput))
            return prompt;

        var sb = new StringBuilder();
        sb.AppendLine("[Input]");
        sb.AppendLine(pipedInput.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("[Question]");
        sb.Append(prompt);
        return sb.ToString();
    }

    // ── OS Detection ──────────────────────────────────────────────────

    /// <summary>
    /// Get detailed OS string: "macOS 15.2 (arm64)", "Ubuntu 24.04 (x86_64)", etc.
    /// </summary>
    internal static string GetDetailedOS()
    {
        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Darwin kernel version → macOS version mapping
            var desc = RuntimeInformation.OSDescription; // e.g. "Darwin 24.2.0"
            var macVersion = GetMacOSVersion();
            return $"macOS {macVersion} ({arch})";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var distro = GetLinuxDistro();
            return $"{distro} ({arch})";
        }

        // Windows
        return $"{RuntimeInformation.OSDescription} ({arch})";
    }

    private static string GetMacOSVersion()
    {
        try
        {
            // Try sw_vers for exact macOS version
            var psi = new System.Diagnostics.ProcessStartInfo("sw_vers", "-productVersion")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (!string.IsNullOrEmpty(output))
                    return output;
            }
        }
        catch { }

        // Fallback: extract from OSDescription
        return RuntimeInformation.OSDescription;
    }

    private static string GetLinuxDistro()
    {
        try
        {
            if (File.Exists("/etc/os-release"))
            {
                var lines = File.ReadAllLines("/etc/os-release");
                var prettyName = lines
                    .FirstOrDefault(l => l.StartsWith("PRETTY_NAME="))
                    ?.Split('=', 2)[1].Trim('"');
                if (!string.IsNullOrEmpty(prettyName))
                    return prettyName;
            }
        }
        catch { }

        return $"Linux {RuntimeInformation.OSDescription}";
    }

    // ── Embedded Spec ─────────────────────────────────────────────────

    private static string? _cachedSpec;

    /// <summary>
    /// Read the embedded rush-lang-spec.yaml from the assembly resources.
    /// Cached after first read.
    /// </summary>
    internal static string GetEmbeddedSpec()
    {
        if (_cachedSpec != null) return _cachedSpec;

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            // Try both possible resource names
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("rush-lang-spec.yaml", StringComparison.OrdinalIgnoreCase));

            if (resourceName != null)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    _cachedSpec = reader.ReadToEnd();
                    return _cachedSpec;
                }
            }
        }
        catch { }

        _cachedSpec = "";
        return _cachedSpec;
    }

    // ── Custom Provider Loading ───────────────────────────────────────

    /// <summary>
    /// Load all providers: built-ins + custom JSON specs from ~/.config/rush/ai-providers/
    /// Custom providers override built-ins if names collide.
    /// </summary>
    internal static Dictionary<string, IAiProvider> LoadAllProviders()
    {
        var providers = new Dictionary<string, IAiProvider>(BuiltinProviders, StringComparer.OrdinalIgnoreCase);

        try
        {
            var customDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "rush", "ai-providers");

            if (Directory.Exists(customDir))
            {
                foreach (var file in Directory.GetFiles(customDir, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        var name = root.GetProperty("name").GetString() ?? "";
                        var endpoint = root.GetProperty("endpoint").GetString() ?? "";
                        var format = root.TryGetProperty("format", out var fmt) ? fmt.GetString() ?? "openai" : "openai";
                        var keyVar = root.TryGetProperty("apiKeyEnvVar", out var kv) ? kv.GetString() ?? "" : "";
                        var defModel = root.TryGetProperty("defaultModel", out var dm) ? dm.GetString() ?? "" : "";

                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(endpoint))
                        {
                            providers[name] = new CustomProvider(name, endpoint, format, keyVar, defModel);
                        }
                    }
                    catch { }  // Skip malformed provider files
                }
            }
        }
        catch { }

        return providers;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static void WriteError(string message)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"ai: {message}");
        Console.ResetColor();
    }

    // ── Fence Stripping ──────────────────────────────────────────────

    /// <summary>
    /// Line-buffered streaming filter that strips markdown code fences.
    /// Accumulates tokens into a line buffer; when a newline arrives,
    /// evaluates the complete line and suppresses fence markers (```).
    /// Also tracks content inside fences for command extraction.
    /// </summary>
    internal class FenceStripper
    {
        private readonly StringBuilder _lineBuffer = new();
        private readonly StringBuilder _displayContent = new();
        private readonly StringBuilder _codeContent = new();
        private bool _insideFence;
        private bool _hadFences;

        /// <summary>
        /// Process a streaming token. Returns text to display (may be empty
        /// if the token is buffered or part of a fence line).
        /// </summary>
        public string Process(string token)
        {
            var output = new StringBuilder();

            foreach (char c in token)
            {
                if (c == '\n')
                {
                    var line = _lineBuffer.ToString();
                    if (IsFenceLine(line))
                    {
                        // Toggle fence state, suppress the line
                        if (_insideFence)
                        {
                            // Closing fence
                            _insideFence = false;
                        }
                        else
                        {
                            // Opening fence
                            _insideFence = true;
                            _hadFences = true;
                        }
                    }
                    else
                    {
                        // Regular line — output it
                        output.Append(line);
                        output.Append('\n');
                        _displayContent.Append(line);
                        _displayContent.Append('\n');
                        if (_insideFence)
                        {
                            _codeContent.Append(line);
                            _codeContent.Append('\n');
                        }
                    }
                    _lineBuffer.Clear();
                }
                else
                {
                    _lineBuffer.Append(c);
                }
            }

            return output.ToString();
        }

        /// <summary>
        /// Flush any remaining buffered content at end of stream.
        /// </summary>
        public string Flush()
        {
            var line = _lineBuffer.ToString();
            _lineBuffer.Clear();

            if (IsFenceLine(line))
                return "";

            if (line.Length > 0)
            {
                _displayContent.Append(line);
                if (_insideFence)
                    _codeContent.Append(line);
                return line;
            }

            return "";
        }

        /// <summary>Full response with fences stripped.</summary>
        public string DisplayContent => _displayContent.ToString().TrimEnd();

        /// <summary>
        /// Only content from inside code fences (for --exec).
        /// Null if no fences were present in the response.
        /// </summary>
        public string? CodeContent => _hadFences ? _codeContent.ToString().TrimEnd() : null;

        private static bool IsFenceLine(string line)
            => Regex.IsMatch(line.Trim(), @"^```\w*$");
    }
}
