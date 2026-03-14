using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rush;

/// <summary>
/// MCP SSH gateway — JSON-RPC 2.0 over stdio.
/// Server name: rush-ssh. Dynamic multi-host: tools take a `host` parameter.
///
/// If Rush is installed on the remote host, uses a persistent `rush --llm`
/// session (structured JSON output, cwd/variable persistence across calls).
/// Falls back to stateless raw shell commands if Rush is not available.
///
/// Usage:
///   rush --mcp-ssh                                    # start SSH gateway
///   claude mcp add rush-ssh -- rush --mcp-ssh         # register with Claude Code
/// </summary>
public class McpSshMode
{
    private readonly string _version;
    private readonly Dictionary<string, SshLlmSession> _sessions = new();
    private readonly HashSet<string> _rawShellHosts = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public McpSshMode(string version)
    {
        _version = version;
    }

    public void Run()
    {
        Console.Error.WriteLine($"[rush-ssh] Rush SSH gateway v{_version} starting");

        while (true)
        {
            var line = Console.ReadLine();
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? msg;
            try { msg = JsonNode.Parse(line); }
            catch { McpJsonRpc.WriteError(null, -32700, "Parse error"); continue; }

            if (msg == null) { McpJsonRpc.WriteError(null, -32700, "Parse error"); continue; }

            var id = msg["id"];
            var method = msg["method"]?.GetValue<string>();

            if (id == null) continue; // Swallow notifications

            try
            {
                switch (method)
                {
                    case "initialize":
                        HandleInitialize(id);
                        break;
                    case "tools/list":
                        HandleToolsList(id);
                        break;
                    case "tools/call":
                        HandleToolsCall(id, msg["params"]);
                        break;
                    case "resources/list":
                        McpResources.HandleResourcesList(id);
                        break;
                    case "resources/read":
                        McpResources.HandleResourcesRead(id, msg["params"]);
                        break;
                    default:
                        McpJsonRpc.WriteError(id, -32601, $"Method not found: {method}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[rush-ssh] Error handling {method}: {ex.Message}");
                McpJsonRpc.WriteError(id, -32603, $"Internal error: {ex.Message}");
            }
        }

        // Dispose persistent sessions before tearing down SSH sockets
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();

        SshPool.Cleanup();
        Console.Error.WriteLine("[rush-ssh] Server shutting down (EOF)");
    }

    // ── initialize ─────────────────────────────────────────────────────

    private void HandleInitialize(JsonNode id)
    {
        var result = new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject(),
                ["resources"] = new JsonObject()
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "rush-ssh",
                ["version"] = _version
            },
            ["instructions"] = "Rush SSH gateway. Execute commands on remote hosts via SSH. " +
                "All tools require a 'host' parameter (hostname or SSH alias). " +
                "If Rush is installed on the remote host, commands run in a persistent session " +
                "(variables, cwd, and environment survive across calls). Falls back to stateless " +
                "raw shell for hosts without Rush. Multiple hosts can be targeted in parallel. " +
                McpResources.Instructions
        };

        McpJsonRpc.WriteResult(id, result);
    }

    // ── tools/list ─────────────────────────────────────────────────────

    private void HandleToolsList(JsonNode id)
    {
        var tools = new JsonArray
        {
            McpJsonRpc.MakeTool(
                "rush_execute",
                "Execute a command on a remote host via SSH. If Rush is installed on the remote, " +
                "commands run in a persistent session (variables, cwd, env persist across calls). " +
                "Falls back to stateless raw shell if Rush is not available.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["host"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "SSH host (hostname, IP, or SSH config alias)"
                        },
                        ["command"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The command to execute on the remote host"
                        }
                    },
                    ["required"] = new JsonArray { "host", "command" }
                }),

            McpJsonRpc.MakeTool(
                "rush_read_file",
                "Read a file from a remote host via SSH. If Rush is on the remote, returns " +
                "structured result with MIME type, encoding, and content. Falls back to cat.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["host"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "SSH host (hostname, IP, or SSH config alias)"
                        },
                        ["path"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Absolute or relative path to the file on the remote host"
                        }
                    },
                    ["required"] = new JsonArray { "host", "path" }
                }),

            McpJsonRpc.MakeTool(
                "rush_context",
                "Get current shell context from a remote host: hostname, cwd, git branch/dirty status, " +
                "last exit code. If Rush is on the remote, returns cached context (0ms latency).",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["host"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "SSH host (hostname, IP, or SSH config alias)"
                        }
                    },
                    ["required"] = new JsonArray { "host" }
                })
        };

        McpJsonRpc.WriteResult(id, new JsonObject { ["tools"] = tools });
    }

    // ── tools/call ─────────────────────────────────────────────────────

    private void HandleToolsCall(JsonNode id, JsonNode? parameters)
    {
        var toolName = parameters?["name"]?.GetValue<string>();
        var arguments = parameters?["arguments"];

        if (string.IsNullOrEmpty(toolName))
        {
            McpJsonRpc.WriteError(id, -32602, "Missing tool name");
            return;
        }

        switch (toolName)
        {
            case "rush_execute":
            {
                var host = arguments?["host"]?.GetValue<string>();
                var command = arguments?["command"]?.GetValue<string>();
                if (string.IsNullOrEmpty(host)) { McpJsonRpc.WriteError(id, -32602, "Missing required parameter: host"); return; }
                if (string.IsNullOrEmpty(command)) { McpJsonRpc.WriteError(id, -32602, "Missing required parameter: command"); return; }

                var session = GetOrCreateSession(host);
                if (session != null)
                {
                    try
                    {
                        var result = session.Execute(command);
                        var resultJson = JsonSerializer.Serialize(result, JsonOpts);
                        var resultObj = JsonNode.Parse(resultJson)!.AsObject();
                        resultObj["host"] = host;
                        resultObj["shell"] = "rush";

                        var isError = result.Status != "success" && result.Status != "output_limit";
                        WriteToolResult(id, resultObj.ToJsonString(), isError);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[rush-ssh] Session error on {host}: {ex.Message}");
                        RemoveSession(host);
                        ExecuteRawSsh(id, host, command);
                    }
                }
                else
                {
                    ExecuteRawSsh(id, host, command);
                }
                break;
            }
            case "rush_read_file":
            {
                var host = arguments?["host"]?.GetValue<string>();
                var path = arguments?["path"]?.GetValue<string>();
                if (string.IsNullOrEmpty(host)) { McpJsonRpc.WriteError(id, -32602, "Missing required parameter: host"); return; }
                if (string.IsNullOrEmpty(path)) { McpJsonRpc.WriteError(id, -32602, "Missing required parameter: path"); return; }

                var session = GetOrCreateSession(host);
                if (session != null)
                {
                    try
                    {
                        var result = session.ReadFile(path);
                        var resultJson = JsonSerializer.Serialize(result, JsonOpts);
                        var resultObj = JsonNode.Parse(resultJson)!.AsObject();
                        resultObj["host"] = host;
                        resultObj["shell"] = "rush";

                        WriteToolResult(id, resultObj.ToJsonString(), result.Status != "success");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[rush-ssh] Session error on {host}: {ex.Message}");
                        RemoveSession(host);
                        ExecuteRawReadFile(id, host, path);
                    }
                }
                else
                {
                    ExecuteRawReadFile(id, host, path);
                }
                break;
            }
            case "rush_context":
            {
                var host = arguments?["host"]?.GetValue<string>();
                if (string.IsNullOrEmpty(host)) { McpJsonRpc.WriteError(id, -32602, "Missing required parameter: host"); return; }

                var session = GetOrCreateSession(host);
                if (session != null)
                {
                    var ctx = session.GetCachedContext();
                    if (ctx != null)
                    {
                        var resultObj = new JsonObject
                        {
                            ["status"] = "success",
                            ["host"] = host,
                            ["hostname"] = ctx.Host,
                            ["cwd"] = ctx.Cwd,
                            ["user"] = ctx.User,
                            ["shell"] = "rush",
                            ["version"] = ctx.Version,
                            ["last_exit_code"] = ctx.LastExitCode,
                            ["git_dirty"] = ctx.GitDirty,
                            ["duration_ms"] = 0
                        };
                        if (ctx.GitBranch != null) resultObj["git_branch"] = ctx.GitBranch;

                        WriteToolResult(id, JsonSerializer.Serialize(resultObj, JsonOpts), false);
                        break;
                    }
                }

                // Fall back to raw shell context gathering
                ExecuteRawContext(id, host);
                break;
            }
            default:
                McpJsonRpc.WriteError(id, -32602, $"Unknown tool: {toolName}");
                break;
        }
    }

    // ── SSH execution ──────────────────────────────────────────────────

    private static (string stdout, string stderr, int exitCode, long durationMs) RunSsh(string host, string command)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo("ssh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            // Args: keepalive + host + command
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("ServerAliveInterval=15");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("ServerAliveCountMax=3");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("BatchMode=yes");
            SshPool.Apply(psi);
            psi.ArgumentList.Add(host);
            psi.ArgumentList.Add(command);

            using var proc = Process.Start(psi);
            if (proc == null)
                return ("", $"Failed to start ssh to {host}", 1, sw.ElapsedMilliseconds);
            SshPool.Track(host);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();

            var stdout = stdoutTask.GetAwaiter().GetResult().TrimEnd('\n', '\r');
            var stderr = stderrTask.GetAwaiter().GetResult().TrimEnd('\n', '\r');

            return (stdout, stderr, proc.ExitCode, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return ("", $"SSH error: {ex.Message}", 1, sw.ElapsedMilliseconds);
        }
    }

    // ── Session management ─────────────────────────────────────────────

    /// <summary>
    /// Get an existing persistent Rush session or create one.
    /// Returns null if Rush is not available on the host (use raw shell fallback).
    /// </summary>
    private SshLlmSession? GetOrCreateSession(string host)
    {
        // Already known to not have Rush
        if (_rawShellHosts.Contains(host))
            return null;

        // Existing live session
        if (_sessions.TryGetValue(host, out var existing))
        {
            if (existing.IsAlive)
                return existing;

            // Session died — try to reconnect once
            Console.Error.WriteLine($"[rush-ssh] Session to {host} died, reconnecting...");
            try
            {
                if (existing.Reconnect())
                    return existing;
            }
            catch { }

            // Reconnect failed — remove and try fresh
            existing.Dispose();
            _sessions.Remove(host);
        }

        // Try to create new session
        var session = SshLlmSession.TryCreate(host);
        if (session == null)
        {
            _rawShellHosts.Add(host);
            return null;
        }

        _sessions[host] = session;
        return session;
    }

    private void RemoveSession(string host)
    {
        if (_sessions.TryGetValue(host, out var session))
        {
            session.Dispose();
            _sessions.Remove(host);
        }
    }

    // ── Raw shell fallbacks ─────────────────────────────────────────────

    private void ExecuteRawSsh(JsonNode id, string host, string command)
    {
        var (stdout, stderr, exitCode, durationMs) = RunSsh(host, command);
        var resultObj = new JsonObject
        {
            ["status"] = exitCode == 0 ? "success" : "error",
            ["exit_code"] = exitCode,
            ["host"] = host,
            ["shell"] = "raw",
            ["duration_ms"] = durationMs
        };
        if (!string.IsNullOrEmpty(stdout)) resultObj["stdout"] = stdout;
        if (!string.IsNullOrEmpty(stderr)) resultObj["stderr"] = stderr;

        WriteToolResult(id, JsonSerializer.Serialize(resultObj, JsonOpts), exitCode != 0);
    }

    private void ExecuteRawReadFile(JsonNode id, string host, string path)
    {
        var (stdout, stderr, exitCode, durationMs) = RunSsh(host, $"cat {ShellEscape(path)}");
        var resultObj = new JsonObject
        {
            ["status"] = exitCode == 0 ? "success" : "error",
            ["exit_code"] = exitCode,
            ["host"] = host,
            ["file"] = path,
            ["shell"] = "raw",
            ["duration_ms"] = durationMs
        };
        if (exitCode == 0)
            resultObj["content"] = stdout;
        else if (!string.IsNullOrEmpty(stderr))
            resultObj["stderr"] = stderr;

        WriteToolResult(id, JsonSerializer.Serialize(resultObj, JsonOpts), exitCode != 0);
    }

    private void ExecuteRawContext(JsonNode id, string host)
    {
        // Gather context in a single SSH call (bash syntax for Linux/macOS)
        var contextCmd = "echo \"__HOST__$(hostname)\" && echo \"__CWD__$(pwd)\" && " +
            "echo \"__BRANCH__$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo '')\" && " +
            "echo \"__DIRTY__$(test -n \"$(git status --porcelain 2>/dev/null)\" && echo true || echo false)\"";
        var (stdout, stderr, exitCode, durationMs) = RunSsh(host, contextCmd);

        // Fallback: PowerShell 5.1 on Windows doesn't support && or test
        if (exitCode != 0 && stderr.Contains("is not a valid statement separator"))
        {
            var psCmd = "echo \"__HOST__$(hostname)\"; " +
                "echo \"__CWD__$(Get-Location)\"; " +
                "try { $b = git rev-parse --abbrev-ref HEAD 2>$null; " +
                "if ($b) { echo \"__BRANCH__$b\" } } catch {}; " +
                "try { $d = git status --porcelain 2>$null; " +
                "if ($d) { echo \"__DIRTY__true\" } else { echo \"__DIRTY__false\" } } " +
                "catch { echo \"__DIRTY__false\" }";
            (stdout, stderr, exitCode, durationMs) = RunSsh(host, psCmd);
        }

        var resultObj = new JsonObject
        {
            ["status"] = exitCode == 0 ? "success" : "error",
            ["host"] = host,
            ["shell"] = "raw",
            ["duration_ms"] = durationMs
        };

        if (exitCode == 0 && !string.IsNullOrEmpty(stdout))
        {
            // Parse the tagged output
            foreach (var line in stdout.Split('\n'))
            {
                if (line.StartsWith("__HOST__")) resultObj["hostname"] = line[8..];
                else if (line.StartsWith("__CWD__")) resultObj["cwd"] = line[7..];
                else if (line.StartsWith("__BRANCH__")) { var b = line[10..]; if (!string.IsNullOrEmpty(b)) resultObj["git_branch"] = b; }
                else if (line.StartsWith("__DIRTY__")) resultObj["git_dirty"] = line[9..] == "true";
            }
        }
        else if (!string.IsNullOrEmpty(stderr))
        {
            resultObj["stderr"] = stderr;
        }

        WriteToolResult(id, JsonSerializer.Serialize(resultObj, JsonOpts), exitCode != 0);
    }

    /// <summary>
    /// Shell-escape a single argument for use in a remote command string.
    /// Wraps in single quotes with proper escaping of embedded single quotes.
    /// </summary>
    private static string ShellEscape(string arg)
    {
        // Replace ' with '\'' (end quote, escaped quote, start quote)
        return "'" + arg.Replace("'", "'\\''") + "'";
    }

    private static void WriteToolResult(JsonNode id, string resultJson, bool isError)
    {
        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = resultJson
            }
        };

        McpJsonRpc.WriteResult(id, new JsonObject
        {
            ["content"] = content,
            ["isError"] = isError
        });
    }
}
