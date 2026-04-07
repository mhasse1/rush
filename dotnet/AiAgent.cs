using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Rush;

/// <summary>
/// Autonomous agent mode: takes a task, drives LlmMode via tool_use in a loop.
/// Entry point: AiAgent.RunAsync()
/// </summary>
internal static class AiAgent
{
    private const int MaxTurns = 25;
    private const int MaxTokens = 4096;

    // ── Agent Events ──────────────────────────────────────────────────

    internal enum AgentEventKind { TextDelta, ToolStart, ToolInputDelta, ToolEnd, TurnEnd }

    internal class AgentEvent
    {
        public AgentEventKind Kind { get; init; }
        public string? Text { get; init; }
        public string? ToolId { get; init; }
        public string? ToolName { get; init; }
        public string? PartialJson { get; init; }
        public string? StopReason { get; init; }
    }

    // ── Supported Providers ──────────────────────────────────────────

    private static readonly HashSet<string> SupportedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "anthropic", "gemini"
    };

    // ── Tool Definition (Anthropic format) ────────────────────────────

    private static readonly object AnthropicToolDef = new
    {
        name = "run_command",
        description = "Execute a command in the Rush shell. Returns JSON with status, stdout, stderr, exit_code, cwd. " +
                      "Supports Rush syntax, shell commands, and builtins (lcat for file reading, timeout N cmd). " +
                      "Output over 4KB is spooled — use 'spool' commands to retrieve.",
        input_schema = new
        {
            type = "object",
            properties = new
            {
                command = new
                {
                    type = "string",
                    description = "The command to execute (e.g., 'ls -la', 'git status', 'lcat src/main.rs')"
                },
                timeout_seconds = new
                {
                    type = "integer",
                    description = "Optional timeout in seconds. Default: no timeout."
                }
            },
            required = new[] { "command" }
        }
    };

    // ── Tool Definition (Gemini format — camelCase per REST API) ──────

    private static readonly object GeminiToolDef = new
    {
        functionDeclarations = new[]
        {
            new
            {
                name = "run_command",
                description = "Execute a command in the Rush shell. Returns JSON with status, stdout, stderr, exit_code, cwd. " +
                              "Supports Rush syntax, shell commands, and builtins (lcat for file reading, timeout N cmd). " +
                              "Output over 4KB is spooled — use 'spool' commands to retrieve. " +
                              "IMPORTANT: Always use this tool to run commands. Never suggest commands as text.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        command = new
                        {
                            type = "string",
                            description = "The command to execute (e.g., 'ls -la', 'git status', 'lcat src/main.rs')"
                        },
                        timeout_seconds = new
                        {
                            type = "integer",
                            description = "Optional timeout in seconds. Default: no timeout."
                        }
                    },
                    required = new[] { "command" }
                }
            }
        }
    };

    // ── Gemini tool_config — encourage function calling ─────────────

    private static readonly object GeminiToolConfig = new
    {
        functionCallingConfig = new { mode = "AUTO" }
    };

    // ── Main Entry ────────────────────────────────────────────────────

    internal static async Task<(bool success, string response)> RunAsync(
        string task,
        LlmMode llm,
        string? providerName,
        string? modelOverride,
        IReadOnlyList<string> history,
        RushConfig config,
        bool verbose,
        bool debug,
        CancellationToken ct)
    {
        // ── Validate provider
        var resolvedProvider = providerName ?? config.AiProvider ?? "anthropic";
        if (!SupportedProviders.Contains(resolvedProvider))
        {
            var valid = string.Join(", ", SupportedProviders);
            WriteAgentError($"agent mode supports: {valid} (got: {resolvedProvider})");
            return (false, "");
        }
        bool isGemini = resolvedProvider.Equals("gemini", StringComparison.OrdinalIgnoreCase);

        // ── Resolve model
        var configModel = config.AiModel;
        if (string.Equals(configModel, "auto", StringComparison.OrdinalIgnoreCase))
            configModel = "";
        var defaultModel = isGemini ? "gemini-2.0-flash" : "claude-sonnet-4-6";
        var model = !string.IsNullOrEmpty(modelOverride) ? modelOverride
            : !string.IsNullOrEmpty(configModel) ? configModel
            : defaultModel;

        // ── Resolve API key
        var keyEnvVar = isGemini ? "GEMINI_API_KEY" : "ANTHROPIC_API_KEY";
        var apiKey = Environment.GetEnvironmentVariable(keyEnvVar) ?? "";
        if (string.IsNullOrEmpty(apiKey))
        {
            WriteAgentError($"no API key. Run: set --secret {keyEnvVar} \"your-key\"");
            return (false, "");
        }

        // ── Build system prompt
        var systemPrompt = BuildAgentSystemPrompt(llm, history);

        // ── Debug log setup
        StreamWriter? debugLog = null;
        string? debugLogPath = null;
        if (debug)
        {
            debugLogPath = Path.Combine(Path.GetTempPath(), "rush-agent.log");
            debugLog = new StreamWriter(debugLogPath, append: false, Encoding.UTF8) { AutoFlush = true };
            debugLog.WriteLine($"═══ Rush Agent Debug Log ═══");
            debugLog.WriteLine($"Time:     {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            debugLog.WriteLine($"Task:     {task}");
            debugLog.WriteLine($"Provider: {resolvedProvider}");
            debugLog.WriteLine($"Model:    {model}");
            debugLog.WriteLine();
        }

        // ── Print header
        var truncatedTask = task.Length > 60 ? task[..57] + "..." : task;
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write("\U0001F916 ");
        Console.WriteLine($"Starting agent: {truncatedTask}");
        Console.ResetColor();
        if (debug && debugLogPath != null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  debug log: {debugLogPath}");
            Console.ResetColor();
        }
        Console.WriteLine();

        // ── Build initial messages (provider-specific format)
        var messages = new List<object>
        {
            isGemini
                ? new { role = "user", parts = new object[] { new { text = task } } }
                : (object)new { role = "user", content = task }
        };

        var totalSw = Stopwatch.StartNew();
        int commandCount = 0;
        int turnCount = 0;
        var summaryText = new StringBuilder();

        try
        {
            // ── Agent loop ────────────────────────────────────────────
            for (int turn = 0; turn < MaxTurns; turn++)
            {
                ct.ThrowIfCancellationRequested();
                turnCount++;

                // Debug: log turn start
                debugLog?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ═══ Turn {turnCount} ═══");
                debugLog?.WriteLine();

                // Call LLM with tools
                var turnBlocks = new List<object>(); // assistant content blocks for this turn
                var toolUses = new List<(string id, string name, string input)>();
                var currentToolId = "";
                var currentToolName = "";
                var inputJsonAccum = new StringBuilder();
                var thinkingBuffer = new StringBuilder();
                bool hadText = false;

                // Debug: log outgoing request body
                if (debugLog != null)
                {
                    var debugBody = isGemini ? "(gemini body)" : JsonSerializer.Serialize(new
                    {
                        model, max_tokens = MaxTokens, stream = true,
                        system = systemPrompt.Length > 200 ? systemPrompt[..200] + "..." : systemPrompt,
                        tools = new[] { AnthropicToolDef },
                        messages
                    }, new JsonSerializerOptions { WriteIndented = true });
                    debugLog.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ── request body ──");
                    debugLog.WriteLine(debugBody);
                    debugLog.WriteLine();
                }

                var eventStream = isGemini
                    ? StreamGeminiAgent(systemPrompt, messages, model, apiKey, ct)
                    : StreamAnthropicAgent(systemPrompt, messages, model, apiKey, ct);

                await foreach (var evt in eventStream)
                {
                    switch (evt.Kind)
                    {
                        case AgentEventKind.TextDelta:
                            // Print thinking text in dim
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.Write(evt.Text);
                            Console.ResetColor();
                            thinkingBuffer.Append(evt.Text);
                            hadText = true;
                            break;

                        case AgentEventKind.ToolStart:
                            currentToolId = evt.ToolId ?? "";
                            currentToolName = evt.ToolName ?? "";
                            inputJsonAccum.Clear();
                            break;

                        case AgentEventKind.ToolInputDelta:
                            inputJsonAccum.Append(evt.PartialJson);
                            break;

                        case AgentEventKind.ToolEnd:
                            toolUses.Add((currentToolId, currentToolName, inputJsonAccum.ToString()));
                            break;

                        case AgentEventKind.TurnEnd:
                            // Will handle after the stream ends
                            break;
                    }
                }

                // Ensure newline after thinking text
                if (hadText)
                {
                    Console.WriteLine();
                    summaryText.Append(thinkingBuffer);
                }

                // Debug: log thinking text
                if (debugLog != null && thinkingBuffer.Length > 0)
                {
                    debugLog.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ── thinking ──");
                    debugLog.WriteLine(thinkingBuffer.ToString().TrimEnd());
                    debugLog.WriteLine();
                }

                // ── Build assistant content blocks for message history ──
                if (isGemini)
                {
                    // Gemini: role "model", parts array with text and functionCall
                    var parts = new List<object>();
                    if (thinkingBuffer.Length > 0)
                        parts.Add(new { text = thinkingBuffer.ToString() });
                    foreach (var (_, name, input) in toolUses)
                    {
                        object argsObj;
                        try { argsObj = JsonDocument.Parse(input).RootElement.Clone(); }
                        catch { argsObj = new { command = input }; }
                        parts.Add(new { functionCall = new { name, args = argsObj } });
                    }
                    if (parts.Count > 0)
                        messages.Add(new { role = "model", parts = parts.ToArray() });
                }
                else
                {
                    // Anthropic: role "assistant", content array with type-tagged blocks
                    if (thinkingBuffer.Length > 0)
                        turnBlocks.Add(new { type = "text", text = thinkingBuffer.ToString() });
                    foreach (var (id, name, input) in toolUses)
                    {
                        object inputObj;
                        try { inputObj = JsonDocument.Parse(input).RootElement.Clone(); }
                        catch { inputObj = new { command = input }; }
                        turnBlocks.Add(new { type = "tool_use", id, name, input = inputObj });
                    }
                    if (turnBlocks.Count > 0)
                        messages.Add(new { role = "assistant", content = turnBlocks.ToArray() });
                }

                // ── No tool calls → agent is done ─────────────────────
                if (toolUses.Count == 0)
                    break;

                // ── Execute tool calls and build tool_result messages ──
                var toolResultParts = new List<object>(); // Gemini: functionResponse parts
                var toolResults = new List<object>();      // Anthropic: tool_result blocks

                foreach (var (id, name, input) in toolUses)
                {
                    ct.ThrowIfCancellationRequested();

                    if (name != "run_command")
                    {
                        if (isGemini)
                            toolResultParts.Add(new { functionResponse = new { name, response = new { content = $"Unknown tool: {name}" } } });
                        else
                            toolResults.Add(new { type = "tool_result", tool_use_id = id, content = $"Unknown tool: {name}", is_error = true });
                        continue;
                    }

                    // Parse command from input JSON
                    string command = "";
                    int? timeoutSeconds = null;
                    try
                    {
                        using var doc = JsonDocument.Parse(input);
                        if (doc.RootElement.TryGetProperty("command", out var cmdEl))
                            command = cmdEl.GetString() ?? "";
                        if (doc.RootElement.TryGetProperty("timeout_seconds", out var toEl))
                            timeoutSeconds = toEl.GetInt32();
                    }
                    catch
                    {
                        command = input.Trim('"');
                    }

                    if (string.IsNullOrWhiteSpace(command))
                    {
                        if (isGemini)
                            toolResultParts.Add(new { functionResponse = new { name, response = new { content = "Error: empty command" } } });
                        else
                            toolResults.Add(new { type = "tool_result", tool_use_id = id, content = "Error: empty command", is_error = true });
                        continue;
                    }

                    // Wrap command with timeout if specified
                    var execCommand = timeoutSeconds.HasValue
                        ? $"timeout {timeoutSeconds.Value} {command}"
                        : command;

                    // Print command
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write("  \u25b8 ");
                    Console.ResetColor();
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine(command);
                    Console.ResetColor();

                    // Verbose: show tool_use JSON inline
                    if (verbose)
                        PrintJsonBox("tool_use", input);

                    // Debug: log tool_use
                    if (debugLog != null)
                    {
                        debugLog.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ── tool_use: {name} ──");
                        debugLog.WriteLine(FormatJson(input));
                        debugLog.WriteLine();
                    }

                    // Execute via LlmMode
                    var result = llm.ExecuteCommand(execCommand);
                    commandCount++;

                    // Print result summary
                    PrintResultSummary(result);

                    // Serialize result as tool_result content
                    var resultJson = JsonSerializer.Serialize(result, new JsonSerializerOptions
                    {
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                    });

                    // Verbose: show tool_result JSON inline
                    if (verbose)
                        PrintJsonBox("tool_result", TruncateJson(resultJson, 500));

                    // Debug: log full tool_result
                    if (debugLog != null)
                    {
                        debugLog.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ── tool_result ({result.Status}) ──");
                        debugLog.WriteLine(FormatJson(resultJson));
                        debugLog.WriteLine();
                    }

                    if (isGemini)
                        toolResultParts.Add(new { functionResponse = new { name, response = new { content = resultJson } } });
                    else
                        toolResults.Add(new { type = "tool_result", tool_use_id = id, content = resultJson });
                }

                // Append tool results as user message
                if (isGemini)
                    messages.Add(new { role = "user", parts = toolResultParts.ToArray() });
                else
                    messages.Add(new { role = "user", content = toolResults.ToArray() });
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Agent cancelled.");
            Console.ResetColor();
            debugLog?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ── CANCELLED ──");
            debugLog?.Dispose();
            return (false, "");
        }
        catch (AiException ex)
        {
            WriteAgentError(ex.Message);
            debugLog?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ── ERROR: {ex.Message} ──");
            debugLog?.Dispose();
            return (false, "");
        }
        catch (HttpRequestException ex)
        {
            WriteAgentError($"network error: {ex.Message}");
            debugLog?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ── NETWORK ERROR: {ex.Message} ──");
            debugLog?.Dispose();
            return (false, "");
        }
        catch (Exception ex)
        {
            WriteAgentError($"error: {ex.Message}");
            debugLog?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ── ERROR: {ex.Message} ──");
            debugLog?.Dispose();
            return (false, "");
        }

        // ── Print summary ─────────────────────────────────────────────
        totalSw.Stop();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        var elapsed = totalSw.Elapsed.TotalSeconds;
        var summaryLine = $"Done. {commandCount} command{(commandCount != 1 ? "s" : "")}, " +
                          $"{turnCount} turn{(turnCount != 1 ? "s" : "")}, {elapsed:F1}s";
        Console.WriteLine($"  {summaryLine}");
        Console.ResetColor();
        Console.WriteLine();

        // Debug: finalize log
        if (debugLog != null)
        {
            debugLog.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ═══ {summaryLine} ═══");
            debugLog.Dispose();
        }

        return (true, summaryText.ToString());
    }

    // ── System Prompt ─────────────────────────────────────────────────

    private static string BuildAgentSystemPrompt(LlmMode llm, IReadOnlyList<string> history)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"You are an autonomous agent operating inside Rush shell (v{RushVersion.Full}).");
        sb.AppendLine($"Platform: {AiCommand.GetDetailedOS()}");
        sb.AppendLine($"Directory: {Environment.CurrentDirectory}");
        sb.AppendLine();
        sb.AppendLine("You have a run_command tool to execute commands and observe results.");
        sb.AppendLine("IMPORTANT: Always use the run_command tool to execute commands. Never write commands as text — call the tool.");
        sb.AppendLine("Work step by step. After each command, analyze the result before proceeding.");
        sb.AppendLine("When the task is complete, provide a brief summary of what you accomplished.");
        sb.AppendLine("Do not ask the user questions — make reasonable decisions and proceed.");
        sb.AppendLine();
        sb.AppendLine("Available commands include:");
        sb.AppendLine("- Standard Unix commands (ls, cat, grep, find, git, etc.)");
        sb.AppendLine("- Rush syntax (variables, functions, pipelines)");
        sb.AppendLine("- lcat <file> — read file contents (returns structured JSON with content)");
        sb.AppendLine("- spool <range> — retrieve spooled output when commands produce >4KB");
        sb.AppendLine("- timeout <seconds> <command> — run with timeout");
        sb.AppendLine();
        sb.AppendLine("Tips:");
        sb.AppendLine("- Use lcat instead of cat for reading files (it returns structured metadata).");
        sb.AppendLine("- If output is spooled, use spool commands to retrieve portions.");
        sb.AppendLine("- Prefer concise commands. Check results before proceeding.");
        sb.AppendLine("- If a command fails, try an alternative approach.");

        // Shell context
        var ctx = llm.GetContext();
        sb.AppendLine();
        sb.AppendLine($"Shell context: host={ctx.Host}, user={ctx.User}, cwd={ctx.Cwd}" +
                      (ctx.GitBranch != null ? $", git={ctx.GitBranch}{(ctx.GitDirty ? "*" : "")}" : ""));

        // Recent history
        if (history.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Recent commands:");
            var start = Math.Max(0, history.Count - 5);
            for (int i = start; i < history.Count; i++)
                sb.AppendLine($"  {history[i]}");
        }

        // Embed Rush language spec (concise reference for the agent)
        var spec = AiCommand.GetEmbeddedSpec();
        if (!string.IsNullOrEmpty(spec))
        {
            sb.AppendLine();
            sb.AppendLine("Rush Language Specification:");
            sb.AppendLine(spec);
        }

        return sb.ToString();
    }

    // ── Anthropic Streaming with Tool Use ─────────────────────────────

    private static async IAsyncEnumerable<AgentEvent> StreamAnthropicAgent(
        string systemPrompt,
        List<object> messages,
        string model,
        string apiKey,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var client = AiHttpClient.Create();

        var body = JsonSerializer.Serialize(new
        {
            model,
            max_tokens = MaxTokens,
            stream = true,
            system = systemPrompt,
            tools = new[] { AnthropicToolDef },
            messages
        });

        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        AiHttpClient.EnsureSuccess(resp);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (!line.StartsWith("data: ")) continue;

            var json = line[6..];
            if (json == "[DONE]") yield break;

            AgentEvent? evt = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString();

                switch (type)
                {
                    case "content_block_start":
                        if (root.TryGetProperty("content_block", out var block))
                        {
                            var blockType = block.GetProperty("type").GetString();
                            if (blockType == "tool_use")
                            {
                                evt = new AgentEvent
                                {
                                    Kind = AgentEventKind.ToolStart,
                                    ToolId = block.GetProperty("id").GetString(),
                                    ToolName = block.GetProperty("name").GetString()
                                };
                            }
                        }
                        break;

                    case "content_block_delta":
                        if (root.TryGetProperty("delta", out var delta))
                        {
                            var deltaType = delta.GetProperty("type").GetString();
                            if (deltaType == "text_delta" &&
                                delta.TryGetProperty("text", out var textEl))
                            {
                                var text = textEl.GetString();
                                if (!string.IsNullOrEmpty(text))
                                {
                                    evt = new AgentEvent
                                    {
                                        Kind = AgentEventKind.TextDelta,
                                        Text = text
                                    };
                                }
                            }
                            else if (deltaType == "input_json_delta" &&
                                     delta.TryGetProperty("partial_json", out var partialEl))
                            {
                                evt = new AgentEvent
                                {
                                    Kind = AgentEventKind.ToolInputDelta,
                                    PartialJson = partialEl.GetString()
                                };
                            }
                        }
                        break;

                    case "content_block_stop":
                        evt = new AgentEvent { Kind = AgentEventKind.ToolEnd };
                        break;

                    case "message_delta":
                        if (root.TryGetProperty("delta", out var msgDelta) &&
                            msgDelta.TryGetProperty("stop_reason", out var stopEl))
                        {
                            evt = new AgentEvent
                            {
                                Kind = AgentEventKind.TurnEnd,
                                StopReason = stopEl.GetString()
                            };
                        }
                        break;
                }
            }
            catch (JsonException) { }

            if (evt != null)
                yield return evt;
        }
    }

    // ── Gemini Streaming with Function Calling ──────────────────────────

    private static async IAsyncEnumerable<AgentEvent> StreamGeminiAgent(
        string systemPrompt,
        List<object> messages,
        string model,
        string apiKey,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var client = AiHttpClient.Create();
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse&key={apiKey}";

        var body = JsonSerializer.Serialize(new
        {
            systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = messages,
            tools = new[] { GeminiToolDef },
            toolConfig = GeminiToolConfig
        });

        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        AiHttpClient.EnsureSuccess(resp);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (!line.StartsWith("data: ")) continue;

            var json = line[6..];
            if (json == "[DONE]") yield break;

            // C# async iterators can't yield inside try-catch — extract events first
            var events = new List<AgentEvent>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("candidates", out var candidates) ||
                    candidates.GetArrayLength() == 0) continue;

                var candidate = candidates[0];

                if (candidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts))
                {
                    for (int i = 0; i < parts.GetArrayLength(); i++)
                    {
                        var part = parts[i];

                        if (part.TryGetProperty("text", out var textEl))
                        {
                            var text = textEl.GetString();
                            if (!string.IsNullOrEmpty(text))
                                events.Add(new AgentEvent { Kind = AgentEventKind.TextDelta, Text = text });
                        }

                        if (part.TryGetProperty("functionCall", out var fc))
                        {
                            var fnName = fc.GetProperty("name").GetString() ?? "";
                            var argsJson = fc.TryGetProperty("args", out var argsEl)
                                ? argsEl.GetRawText()
                                : "{}";

                            events.Add(new AgentEvent { Kind = AgentEventKind.ToolStart, ToolId = $"gemini_{fnName}_{i}", ToolName = fnName });
                            events.Add(new AgentEvent { Kind = AgentEventKind.ToolInputDelta, PartialJson = argsJson });
                            events.Add(new AgentEvent { Kind = AgentEventKind.ToolEnd });
                        }
                    }
                }

                if (candidate.TryGetProperty("finishReason", out var finishEl))
                {
                    var reason = finishEl.GetString();
                    if (reason == "STOP" || reason == "MAX_TOKENS")
                        events.Add(new AgentEvent { Kind = AgentEventKind.TurnEnd, StopReason = reason == "STOP" ? "end_turn" : "max_tokens" });
                }
            }
            catch (JsonException) { }

            foreach (var evt in events)
                yield return evt;
        }
    }

    // ── Terminal Output Helpers ────────────────────────────────────────

    private static void PrintResultSummary(LlmResult result)
    {
        Console.Write("  ");
        if (result.Status == "success" || result.Status == "output_limit")
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("\u2713 ");
            Console.ResetColor();

            if (result.Status == "output_limit")
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"{result.StdoutLines} lines (spooled) | {result.DurationMs}ms");
            }
            else if (result.StdoutType == "objects")
            {
                // Count objects in the array
                var count = CountObjects(result.Stdout);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"{count} object{(count != 1 ? "s" : "")} | {result.DurationMs}ms");
            }
            else
            {
                var lineCount = CountLines(result.Stdout);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                if (lineCount > 0)
                    Console.WriteLine($"{lineCount} line{(lineCount != 1 ? "s" : "")} | {result.DurationMs}ms");
                else
                    Console.WriteLine($"ok | {result.DurationMs}ms");
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("\u2717 ");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            var errMsg = result.Stderr?.Split('\n').FirstOrDefault() ?? $"exit {result.ExitCode}";
            if (errMsg.Length > 80) errMsg = errMsg[..77] + "...";
            Console.WriteLine($"exit {result.ExitCode}: {errMsg}");
        }
        Console.ResetColor();
    }

    private static int CountObjects(object? stdout)
    {
        if (stdout is JsonElement el && el.ValueKind == JsonValueKind.Array)
            return el.GetArrayLength();
        return 0;
    }

    private static int CountLines(object? stdout)
    {
        if (stdout is string s && !string.IsNullOrEmpty(s))
            return s.Split('\n').Length;
        return 0;
    }

    private static void WriteAgentError(string message)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"ai --agent: {message}");
        Console.ResetColor();
    }

    // ── Verbose / Debug Helpers ──────────────────────────────────────

    /// <summary>Print a JSON payload in a box-drawing frame (for --verbose)</summary>
    private static void PrintJsonBox(string label, string json)
    {
        var formatted = FormatJson(json);
        var lines = formatted.Split('\n');

        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"  \u256d\u2500 {label}");
        foreach (var line in lines)
        {
            Console.Write("  \u2502 ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(line);
            Console.ForegroundColor = ConsoleColor.DarkYellow;
        }
        Console.WriteLine("  \u2570\u2500");
        Console.ResetColor();
    }

    /// <summary>Pretty-print JSON, falling back to raw string on parse failure.</summary>
    private static string FormatJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch
        {
            return json;
        }
    }

    /// <summary>Truncate JSON string for inline display, preserving valid JSON structure hint.</summary>
    private static string TruncateJson(string json, int maxLen)
    {
        if (json.Length <= maxLen)
            return json;
        return json[..maxLen] + " ...(truncated)";
    }
}
