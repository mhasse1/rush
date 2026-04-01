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
                "(variables, cwd, and environment survive across calls) using a JSON envelope protocol " +
                "that preserves shell metacharacters ($_, semicolons, quotes, etc.). " +
                "Falls back to stateless raw shell for hosts without Rush. " +
                "Use rush_exec_script for complex multi-line scripts — it pushes the script to a temp file " +
                "and executes it, avoiding all escaping issues. " +
                "Use rush_write_file to upload files to remote hosts. " +
                "Multiple hosts can be targeted in parallel. " +
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
                "Falls back to stateless raw shell if Rush is not available. " +
                "Uses a JSON envelope protocol that preserves $_ and other metacharacters.",
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
                        },
                        ["cwd"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Working directory for this command (optional)"
                        },
                        ["timeout"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Timeout in seconds (optional)"
                        },
                        ["env"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["description"] = "Environment variables to set before execution (optional)",
                            ["additionalProperties"] = new JsonObject { ["type"] = "string" }
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
                }),

            McpJsonRpc.MakeTool(
                "rush_write_file",
                "Write content to a file on a remote host via SSH. Content is sent via JSON envelope " +
                "protocol (no shell escaping issues). Requires Rush on the remote host.",
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
                            ["description"] = "Remote file path to write"
                        },
                        ["content"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "File content (text). Will be base64-encoded for transport."
                        },
                        ["mode"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Unix file permissions, e.g. \"0644\" (optional)"
                        },
                        ["append"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Append to file instead of overwriting (default: false)"
                        }
                    },
                    ["required"] = new JsonArray { "host", "path", "content" }
                }),

            McpJsonRpc.MakeTool(
                "rush_exec_script",
                "Push a script to a remote host, execute it, and return results — all in one call. " +
                "The script is transferred via JSON (no shell escaping issues) and written to a temp file. " +
                "Shell is auto-detected from filename extension (.ps1→PowerShell, .sh→bash, .py→python, .rush→rush). " +
                "Requires Rush on the remote host. For hosts without Rush, falls back to SSH stdin piping.",
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
                        ["filename"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Script filename with extension (e.g. 'audit.ps1', 'setup.sh')"
                        },
                        ["content"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Script content (text). Will be base64-encoded for transport."
                        },
                        ["shell"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Override shell: powershell, bash, rush, python, node, ruby (optional, inferred from extension)"
                        },
                        ["args"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["description"] = "Command-line arguments to pass to the script (optional)"
                        },
                        ["timeout"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Timeout in seconds (optional)"
                        },
                        ["cleanup"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Delete temp file after execution (default: true)"
                        }
                    },
                    ["required"] = new JsonArray { "host", "filename", "content" }
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

                // Optional envelope parameters
                var cwd = arguments?["cwd"]?.GetValue<string>();
                int? timeout = arguments?["timeout"] != null ? arguments["timeout"]!.GetValue<int>() : null;
                Dictionary<string, string>? env = null;
                var envNode = arguments?["env"]?.AsObject();
                if (envNode != null)
                {
                    env = new Dictionary<string, string>();
                    foreach (var kvp in envNode)
                        env[kvp.Key] = kvp.Value?.GetValue<string>() ?? "";
                }

                var session = GetOrCreateSession(host);
                if (session != null)
                {
                    try
                    {
                        var result = session.Execute(command, cwd, timeout, env);
                        EmitToolResult(id, host, "rush", result);
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
                        var result = session.GetFile(path);
                        EmitToolResult(id, host, "rush", result);
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
            case "rush_write_file":
            {
                var host = arguments?["host"]?.GetValue<string>();
                var path = arguments?["path"]?.GetValue<string>();
                var content = arguments?["content"]?.GetValue<string>();
                if (string.IsNullOrEmpty(host)) { McpJsonRpc.WriteError(id, -32602, "Missing required parameter: host"); return; }
                if (string.IsNullOrEmpty(path)) { McpJsonRpc.WriteError(id, -32602, "Missing required parameter: path"); return; }
                if (content == null) { McpJsonRpc.WriteError(id, -32602, "Missing required parameter: content"); return; }

                var mode = arguments?["mode"]?.GetValue<string>();
                var append = arguments?["append"]?.GetValue<bool>() ?? false;
                var bytes = System.Text.Encoding.UTF8.GetBytes(content);

                var session = GetOrCreateSession(host);
                if (session != null)
                {
                    try
                    {
                        var result = session.PutFile(path, bytes, mode, append);
                        EmitToolResult(id, host, "rush", result);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[rush-ssh] Session error on {host}: {ex.Message}");
                        RemoveSession(host);
                        ExecuteRawWriteFile(id, host, path, content, append);
                    }
                }
                else
                {
                    ExecuteRawWriteFile(id, host, path, content, append);
                }
                break;
            }
            case "rush_exec_script":
            {
                var host = arguments?["host"]?.GetValue<string>();
                var filename = arguments?["filename"]?.GetValue<string>();
                var content = arguments?["content"]?.GetValue<string>();
                if (string.IsNullOrEmpty(host)) { McpJsonRpc.WriteError(id, -32602, "Missing required parameter: host"); return; }
                if (string.IsNullOrEmpty(filename)) { McpJsonRpc.WriteError(id, -32602, "Missing required parameter: filename"); return; }
                if (content == null) { McpJsonRpc.WriteError(id, -32602, "Missing required parameter: content"); return; }

                var shell = arguments?["shell"]?.GetValue<string>();
                var timeout = arguments?["timeout"] != null ? (int?)arguments["timeout"]!.GetValue<int>() : null;
                var cleanup = arguments?["cleanup"]?.GetValue<bool>() ?? true;
                string[]? args = null;
                var argsNode = arguments?["args"]?.AsArray();
                if (argsNode != null)
                    args = argsNode.Select(a => a?.GetValue<string>() ?? "").ToArray();

                var bytes = System.Text.Encoding.UTF8.GetBytes(content);

                var session = GetOrCreateSession(host);
                if (session != null)
                {
                    try
                    {
                        var result = session.ExecScript(filename, bytes, shell, args, timeout, cleanup);
                        EmitToolResult(id, host, "rush", result);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[rush-ssh] Session error on {host}: {ex.Message}");
                        RemoveSession(host);
                        ExecuteRawExecScript(id, host, filename, content, shell, args);
                    }
                }
                else
                {
                    ExecuteRawExecScript(id, host, filename, content, shell, args);
                }
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

    // ── Tool result helper ─────────────────────────────────────────────

    private void EmitToolResult(JsonNode id, string host, string shell, LlmResult result)
    {
        var resultJson = JsonSerializer.Serialize(result, JsonOpts);
        var resultObj = JsonNode.Parse(resultJson)!.AsObject();
        resultObj["host"] = host;
        resultObj["shell"] = shell;

        var isError = result.Status != "success" && result.Status != "output_limit";
        WriteToolResult(id, resultObj.ToJsonString(), isError);
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

    private void ExecuteRawWriteFile(JsonNode id, string host, string path, string content, bool append)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Pipe content via stdin to avoid shell escaping issues
            var op = append ? ">>" : ">";
            var psi = new ProcessStartInfo("ssh")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ServerAliveInterval=15");
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ServerAliveCountMax=3");
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
            SshPool.Apply(psi);
            psi.ArgumentList.Add(host);
            psi.ArgumentList.Add($"cat {op} {ShellEscape(path)}");

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                WriteToolResult(id, JsonSerializer.Serialize(new JsonObject
                {
                    ["status"] = "error", ["host"] = host, ["shell"] = "raw",
                    ["stderr"] = "Failed to start ssh"
                }, JsonOpts), true);
                return;
            }
            SshPool.Track(host);

            proc.StandardInput.Write(content);
            proc.StandardInput.Close();

            var stderr = proc.StandardError.ReadToEnd().TrimEnd('\n', '\r');
            proc.WaitForExit(30_000);

            var resultObj = new JsonObject
            {
                ["status"] = proc.ExitCode == 0 ? "success" : "error",
                ["exit_code"] = proc.ExitCode,
                ["host"] = host,
                ["shell"] = "raw",
                ["file"] = path,
                ["duration_ms"] = sw.ElapsedMilliseconds
            };
            if (!string.IsNullOrEmpty(stderr)) resultObj["stderr"] = stderr;

            WriteToolResult(id, JsonSerializer.Serialize(resultObj, JsonOpts), proc.ExitCode != 0);
        }
        catch (Exception ex)
        {
            WriteToolResult(id, JsonSerializer.Serialize(new JsonObject
            {
                ["status"] = "error", ["host"] = host, ["shell"] = "raw",
                ["stderr"] = $"SSH error: {ex.Message}", ["duration_ms"] = sw.ElapsedMilliseconds
            }, JsonOpts), true);
        }
    }

    private void ExecuteRawExecScript(JsonNode id, string host, string filename, string content,
        string? shell, string[]? args)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var ext = Path.GetExtension(filename);
            var remoteTmp = $"/tmp/rush_exec_{Guid.NewGuid():N}{ext}";

            // Step 1: Upload script via stdin
            var uploadPsi = new ProcessStartInfo("ssh")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            uploadPsi.ArgumentList.Add("-o"); uploadPsi.ArgumentList.Add("BatchMode=yes");
            SshPool.Apply(uploadPsi);
            uploadPsi.ArgumentList.Add(host);
            uploadPsi.ArgumentList.Add($"cat > {ShellEscape(remoteTmp)}");

            using (var uploadProc = Process.Start(uploadPsi))
            {
                if (uploadProc == null)
                {
                    WriteToolResult(id, JsonSerializer.Serialize(new JsonObject
                    {
                        ["status"] = "error", ["host"] = host, ["shell"] = "raw",
                        ["stderr"] = "Failed to start ssh for upload"
                    }, JsonOpts), true);
                    return;
                }
                SshPool.Track(host);
                uploadProc.StandardInput.Write(content);
                uploadProc.StandardInput.Close();
                uploadProc.WaitForExit(30_000);
                if (uploadProc.ExitCode != 0)
                {
                    var uploadErr = uploadProc.StandardError.ReadToEnd().TrimEnd();
                    WriteToolResult(id, JsonSerializer.Serialize(new JsonObject
                    {
                        ["status"] = "error", ["host"] = host, ["shell"] = "raw",
                        ["stderr"] = $"Upload failed: {uploadErr}"
                    }, JsonOpts), true);
                    return;
                }
            }

            // Step 2: Execute + cleanup
            var detectedShell = shell ?? ext.ToLowerInvariant() switch
            {
                ".ps1" => "powershell",
                ".rush" => "rush",
                ".sh" or ".bash" => "bash",
                ".py" => "python",
                _ => "bash"
            };

            string execCmd;
            var argsStr = args != null && args.Length > 0
                ? " " + string.Join(" ", args.Select(a => ShellEscape(a)))
                : "";

            switch (detectedShell)
            {
                case "powershell":
                    execCmd = $"powershell -NoProfile -ExecutionPolicy Bypass -File {ShellEscape(remoteTmp)}{argsStr}; rm -f {ShellEscape(remoteTmp)}";
                    break;
                default:
                    execCmd = $"{detectedShell} {ShellEscape(remoteTmp)}{argsStr}; rm -f {ShellEscape(remoteTmp)}";
                    break;
            }

            var (stdout, stderr, exitCode, durationMs) = RunSsh(host, execCmd);
            var resultObj = new JsonObject
            {
                ["status"] = exitCode == 0 ? "success" : "error",
                ["exit_code"] = exitCode,
                ["host"] = host,
                ["shell"] = "raw",
                ["duration_ms"] = sw.ElapsedMilliseconds
            };
            if (!string.IsNullOrEmpty(stdout)) resultObj["stdout"] = stdout;
            if (!string.IsNullOrEmpty(stderr)) resultObj["stderr"] = stderr;

            WriteToolResult(id, JsonSerializer.Serialize(resultObj, JsonOpts), exitCode != 0);
        }
        catch (Exception ex)
        {
            WriteToolResult(id, JsonSerializer.Serialize(new JsonObject
            {
                ["status"] = "error", ["host"] = host, ["shell"] = "raw",
                ["stderr"] = $"SSH error: {ex.Message}", ["duration_ms"] = sw.ElapsedMilliseconds
            }, JsonOpts), true);
        }
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
