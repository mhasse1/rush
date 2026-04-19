use std::io::{BufRead, BufReader, Write};
use std::process::{Command, Stdio};

use futures_util::StreamExt;
use reqwest::Client;
use serde::{Deserialize, Serialize};

// ---------------------------------------------------------------------------
// LLM wire protocol types (mirrors rush-core::llm)
// ---------------------------------------------------------------------------

#[derive(Debug, Deserialize)]
struct LlmContext {
    #[allow(dead_code)]
    ready: bool,
    host: String,
    user: String,
    cwd: String,
    git_branch: Option<String>,
    #[allow(dead_code)]
    git_dirty: bool,
    #[allow(dead_code)]
    last_exit_code: i32,
    lang_spec: Option<String>,
}

#[derive(Debug, Deserialize)]
struct LlmResult {
    status: String,
    #[allow(dead_code)]
    exit_code: i32,
    stdout: Option<String>,
    stderr: Option<String>,
    #[allow(dead_code)]
    duration_ms: Option<u128>,
    hint: Option<String>,
    // lcat fields
    file: Option<String>,
    content: Option<String>,
    // errors
    errors: Option<Vec<String>>,
    error_type: Option<String>,
}

// ---------------------------------------------------------------------------
// Ollama API types
// ---------------------------------------------------------------------------

#[derive(Serialize)]
struct ChatRequest {
    model: String,
    messages: Vec<Message>,
    stream: bool,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
struct Message {
    role: String,
    content: String,
}

#[derive(Deserialize)]
struct ChatStreamChunk {
    message: Option<ChunkMessage>,
    done: bool,
}

#[derive(Deserialize)]
struct ChunkMessage {
    content: String,
}

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

fn ollama_url() -> String {
    std::env::var("OLLAMA_HOST").unwrap_or_else(|_| "http://localhost:11434".into())
}

fn ollama_model() -> String {
    std::env::var("RUSH_AGENT_MODEL").unwrap_or_else(|_| "qwen3:32b".into())
}

fn rush_binary() -> String {
    std::env::var("RUSH_BIN").unwrap_or_else(|_| "rush".into())
}

const MAX_TURNS: usize = 50;

// ---------------------------------------------------------------------------
// Rush subprocess
// ---------------------------------------------------------------------------

struct RushProcess {
    child: std::process::Child,
    stdin: std::process::ChildStdin,
    reader: BufReader<std::process::ChildStdout>,
}

impl RushProcess {
    fn spawn() -> Result<(Self, LlmContext), String> {
        let mut child = Command::new(rush_binary())
            .arg("--llm")
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .map_err(|e| format!("Failed to spawn rush --llm: {e}"))?;

        let stdin = child.stdin.take().unwrap();
        let stdout = child.stdout.take().unwrap();
        let mut reader = BufReader::new(stdout);

        // Read initial context (includes lang_spec)
        let mut line = String::new();
        reader
            .read_line(&mut line)
            .map_err(|e| format!("Failed to read initial context: {e}"))?;
        let ctx: LlmContext =
            serde_json::from_str(line.trim()).map_err(|e| format!("Bad initial context: {e}"))?;

        Ok((
            Self {
                child,
                stdin,
                reader,
            },
            ctx,
        ))
    }

    fn execute(&mut self, command: &str) -> Result<(LlmResult, LlmContext), String> {
        // Send command
        writeln!(self.stdin, "{command}").map_err(|e| format!("Write failed: {e}"))?;
        self.stdin.flush().map_err(|e| format!("Flush failed: {e}"))?;

        // Read result line
        let mut line = String::new();
        self.reader
            .read_line(&mut line)
            .map_err(|e| format!("Read result failed: {e}"))?;
        let result: LlmResult =
            serde_json::from_str(line.trim()).map_err(|e| format!("Bad result JSON: {e}"))?;

        // Read next context line
        line.clear();
        self.reader
            .read_line(&mut line)
            .map_err(|e| format!("Read context failed: {e}"))?;
        let ctx: LlmContext =
            serde_json::from_str(line.trim()).map_err(|e| format!("Bad context JSON: {e}"))?;

        Ok((result, ctx))
    }
}

impl Drop for RushProcess {
    fn drop(&mut self) {
        let _ = self.child.kill();
    }
}

// ---------------------------------------------------------------------------
// Ollama streaming chat
// ---------------------------------------------------------------------------

async fn chat(client: &Client, messages: &[Message], model: &str) -> Result<String, String> {
    let url = format!("{}/api/chat", ollama_url());
    let req = ChatRequest {
        model: model.to_string(),
        messages: messages.to_vec(),
        stream: true,
    };

    let resp = client
        .post(&url)
        .json(&req)
        .send()
        .await
        .map_err(|e| format!("Ollama request failed: {e}"))?;

    if !resp.status().is_success() {
        let status = resp.status();
        let body = resp.text().await.unwrap_or_default();
        return Err(format!("Ollama error {status}: {body}"));
    }

    let mut stream = resp.bytes_stream();
    let mut full_response = String::new();

    // Stream tokens to terminal
    while let Some(chunk) = stream.next().await {
        let bytes = chunk.map_err(|e| format!("Stream error: {e}"))?;
        let text = String::from_utf8_lossy(&bytes);

        // Ollama sends newline-delimited JSON chunks
        for line in text.lines() {
            if line.is_empty() {
                continue;
            }
            if let Ok(chunk) = serde_json::from_str::<ChatStreamChunk>(line) {
                if let Some(msg) = chunk.message {
                    print!("{}", msg.content);
                    std::io::stdout().flush().ok();
                    full_response.push_str(&msg.content);
                }
                if chunk.done {
                    println!();
                    return Ok(full_response);
                }
            }
        }
    }

    println!();
    Ok(full_response)
}

// ---------------------------------------------------------------------------
// Command extraction from LLM response
// ---------------------------------------------------------------------------

fn extract_command(response: &str) -> Option<String> {
    // Look for ```rush or ``` code blocks
    let response_trimmed = response.trim();

    // Try fenced code block first
    for fence_start in ["```rush", "```shell", "```bash", "```"] {
        if let Some(start) = response_trimmed.find(fence_start) {
            let after_fence = &response_trimmed[start + fence_start.len()..];
            if let Some(end) = after_fence.find("```") {
                let cmd = after_fence[..end].trim();
                if !cmd.is_empty() {
                    return Some(cmd.to_string());
                }
            }
        }
    }

    // Check for DONE signal
    let lower = response_trimmed.to_lowercase();
    if lower.contains("[done]") || lower.contains("task complete") || lower.ends_with("done.") {
        return None;
    }

    // If the entire response looks like a single command (no paragraphs), use it
    if !response_trimmed.contains("\n\n") && response_trimmed.lines().count() <= 3 {
        let line = response_trimmed
            .lines()
            .find(|l| !l.starts_with('#') && !l.is_empty())
            .unwrap_or("");
        if !line.is_empty() && !line.starts_with("I ") && !line.starts_with("The ") {
            return Some(line.to_string());
        }
    }

    None
}

// ---------------------------------------------------------------------------
// Format result for LLM context
// ---------------------------------------------------------------------------

fn format_result(result: &LlmResult) -> String {
    let mut parts = vec![format!("[{}]", result.status)];

    if let Some(ref stdout) = result.stdout
        && !stdout.is_empty() {
            parts.push(stdout.clone());
        }
    if let Some(ref stderr) = result.stderr
        && !stderr.is_empty() {
            parts.push(format!("STDERR: {stderr}"));
        }
    if let Some(ref hint) = result.hint {
        parts.push(format!("HINT: {hint}"));
    }
    if let Some(ref file) = result.file
        && let Some(ref content) = result.content {
            parts.push(format!("FILE {file}:\n{content}"));
        }
    if let Some(ref errors) = result.errors
        && !errors.is_empty() {
            parts.push(format!("ERRORS: {}", errors.join("; ")));
        }
    if let Some(ref et) = result.error_type {
        parts.push(format!("ERROR_TYPE: {et}"));
    }

    parts.join("\n")
}

// ---------------------------------------------------------------------------
// System prompt
// ---------------------------------------------------------------------------

fn build_system_prompt(ctx: &LlmContext) -> String {
    let mut prompt = format!(
        r#"You are a command-line agent running inside Rush shell on {host} as {user}.
Your working directory is {cwd}.{branch}

You complete tasks by executing Rush commands one at a time.
After each command, you'll see the result and can decide what to do next.

Rules:
- Respond with a SINGLE command in a ```rush code block.
- Use Rush syntax when it's clearer (File.read, Dir.glob, string interpolation).
- Standard Unix commands also work (ls, grep, find, curl, etc.).
- Use `lcat <file>` to read files, `spool` to paginate large output.
- When the task is complete, respond with [DONE] and a brief summary.
- If you need clarification, ask — don't guess.
- Be concise. Execute, observe, adapt.
"#,
        host = ctx.host,
        user = ctx.user,
        cwd = ctx.cwd,
        branch = ctx
            .git_branch
            .as_ref()
            .map(|b| format!("\nGit branch: {b}"))
            .unwrap_or_default(),
    );

    if let Some(ref spec) = ctx.lang_spec {
        prompt.push_str("\n## Rush Language Spec\n\n");
        prompt.push_str(spec);
    }

    prompt
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

#[tokio::main]
async fn main() {
    let args: Vec<String> = std::env::args().collect();

    if args.len() < 2 || args[1] == "--help" || args[1] == "-h" {
        eprintln!("Usage: rush-agent <task>");
        eprintln!("       rush-agent \"find large log files and compress them\"");
        eprintln!();
        eprintln!("Environment:");
        eprintln!("  OLLAMA_HOST         Ollama endpoint (default: http://localhost:11434)");
        eprintln!("  RUSH_AGENT_MODEL    Model name (default: qwen3:32b)");
        eprintln!("  RUSH_BIN            Path to rush binary (default: rush)");
        std::process::exit(1);
    }

    let task = args[1..].join(" ");
    let model = ollama_model();

    println!("rush-agent v0.1.0");
    println!("Model: {model} @ {}", ollama_url());
    println!("Task: {task}");
    println!("---");

    // Spawn Rush subprocess
    let (mut rush, initial_ctx) = match RushProcess::spawn() {
        Ok(r) => r,
        Err(e) => {
            eprintln!("Error: {e}");
            std::process::exit(1);
        }
    };

    // Build conversation
    let system_prompt = build_system_prompt(&initial_ctx);
    let mut messages = vec![
        Message {
            role: "system".into(),
            content: system_prompt,
        },
        Message {
            role: "user".into(),
            content: task,
        },
    ];

    let client = Client::new();

    for turn in 1..=MAX_TURNS {
        println!("\n[Turn {turn}/{MAX_TURNS}]");

        // Ask LLM
        let response = match chat(&client, &messages, &model).await {
            Ok(r) => r,
            Err(e) => {
                eprintln!("LLM error: {e}");
                break;
            }
        };

        // Extract command
        let command = match extract_command(&response) {
            Some(cmd) => cmd,
            None => {
                println!("\n[DONE] Agent finished.");
                messages.push(Message {
                    role: "assistant".into(),
                    content: response,
                });
                break;
            }
        };

        messages.push(Message {
            role: "assistant".into(),
            content: response,
        });

        println!("  > {command}");

        // Execute in Rush
        let (result, _ctx) = match rush.execute(&command) {
            Ok(r) => r,
            Err(e) => {
                eprintln!("Rush error: {e}");
                break;
            }
        };

        let formatted = format_result(&result);
        println!("  {}", formatted.replace('\n', "\n  "));

        messages.push(Message {
            role: "user".into(),
            content: format!("Command result:\n{formatted}"),
        });
    }
}
