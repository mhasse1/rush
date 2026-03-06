using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

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
            var (prompt, providerName, modelOverride, systemOverride) = ParseArgs(arguments);

            if (string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(pipedInput))
            {
                WriteError("usage: ai \"your question\"");
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

            // ── Stream response ──────────────────────────────────────
            var responseBuilder = new StringBuilder();
            await foreach (var token in provider.StreamAsync(systemPrompt, userMessage, model, apiKey, ct))
            {
                Console.Write(token);
                responseBuilder.Append(token);
            }
            Console.WriteLine();

            return (true, responseBuilder.ToString());
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

    // ── Argument Parsing ──────────────────────────────────────────────

    /// <summary>
    /// Parse ai arguments: extract prompt, --provider, --model, --system flags.
    /// Supports: ai "prompt"  ai --provider ollama "prompt"  ai --model gpt-4o "prompt"
    /// </summary>
    internal static (string prompt, string? provider, string? model, string? system) ParseArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return ("", null, null, null);

        string? provider = null;
        string? model = null;
        string? system = null;
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
            else
            {
                promptParts.Add(t);
            }
        }

        return (string.Join(" ", promptParts), provider, model, system);
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
}
