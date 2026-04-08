//! AI provider integration: Anthropic, OpenAI, Gemini, Ollama.
//! Supports streaming responses for the `ai` builtin command.

use serde_json::json;
use std::io::Write;

/// AI provider configuration.
#[derive(Debug, Clone)]
pub struct AiProvider {
    pub name: String,
    pub default_model: String,
    pub api_key_env: String,
    pub endpoint: String,
    pub format: ProviderFormat,
}

#[derive(Debug, Clone, PartialEq)]
pub enum ProviderFormat {
    Anthropic,
    OpenAi,
    Gemini,
    Ollama,
}

/// Built-in providers.
pub fn builtin_providers() -> Vec<AiProvider> {
    vec![
        AiProvider {
            name: "anthropic".into(),
            default_model: "claude-sonnet-4-20250514".into(),
            api_key_env: "ANTHROPIC_API_KEY".into(),
            endpoint: "https://api.anthropic.com/v1/messages".into(),
            format: ProviderFormat::Anthropic,
        },
        AiProvider {
            name: "openai".into(),
            default_model: "gpt-4o".into(),
            api_key_env: "OPENAI_API_KEY".into(),
            endpoint: "https://api.openai.com/v1/chat/completions".into(),
            format: ProviderFormat::OpenAi,
        },
        AiProvider {
            name: "gemini".into(),
            default_model: "gemini-2.0-flash".into(),
            api_key_env: "GEMINI_API_KEY".into(),
            endpoint: "https://generativelanguage.googleapis.com/v1beta/models".into(),
            format: ProviderFormat::Gemini,
        },
        AiProvider {
            name: "ollama".into(),
            default_model: "llama3.2".into(),
            api_key_env: String::new(),
            endpoint: "http://localhost:11434/api/chat".into(),
            format: ProviderFormat::Ollama,
        },
    ]
}

/// Find a provider by name.
pub fn get_provider(name: &str) -> Option<AiProvider> {
    builtin_providers()
        .into_iter()
        .find(|p| p.name.eq_ignore_ascii_case(name))
}

/// Build the system prompt for AI requests.
pub fn build_system_prompt() -> String {
    let cwd = std::env::current_dir()
        .map(|p| p.to_string_lossy().to_string())
        .unwrap_or_default();
    let os = std::env::consts::OS;
    let arch = std::env::consts::ARCH;

    format!(
        "You are an AI assistant in a Rush shell session.\n\
         OS: {os}/{arch}\n\
         CWD: {cwd}\n\
         Shell: rush (Rust)\n\
         Be concise. When suggesting commands, use Rush syntax where appropriate.\n\
         For file operations use File.read/write, Dir.list, etc.\n\
         When showing code, use markdown fenced code blocks."
    )
}

/// Build the user message, optionally including piped input.
pub fn build_user_message(prompt: &str, piped_input: Option<&str>) -> String {
    if let Some(input) = piped_input {
        format!("{prompt}\n\n---\n\nInput:\n{input}")
    } else {
        prompt.to_string()
    }
}

/// Execute an AI request with streaming output to stdout.
/// Returns the full response text.
pub fn execute(
    provider_name: Option<&str>,
    model_override: Option<&str>,
    prompt: &str,
    piped_input: Option<&str>,
) -> Result<String, String> {
    let provider_name = provider_name.unwrap_or("anthropic");
    let provider = get_provider(provider_name)
        .ok_or_else(|| format!("Unknown AI provider: {provider_name}"))?;

    let model = model_override
        .map(String::from)
        .unwrap_or_else(|| provider.default_model.clone());

    let api_key = if !provider.api_key_env.is_empty() {
        std::env::var(&provider.api_key_env).map_err(|_| {
            format!(
                "No API key. Set {} environment variable.",
                provider.api_key_env
            )
        })?
    } else {
        String::new()
    };

    let system = build_system_prompt();
    let user_msg = build_user_message(prompt, piped_input);

    stream_request(&provider, &model, &api_key, &system, &user_msg)
}

/// Make the streaming HTTP request and print tokens as they arrive.
fn stream_request(
    provider: &AiProvider,
    model: &str,
    api_key: &str,
    system: &str,
    user_msg: &str,
) -> Result<String, String> {
    let client = reqwest::blocking::Client::builder()
        .timeout(std::time::Duration::from_secs(120))
        .build()
        .map_err(|e| format!("HTTP client error: {e}"))?;

    let (url, body, headers) = build_request(provider, model, api_key, system, user_msg);

    let mut req = client.post(&url).header("Content-Type", "application/json");
    for (key, val) in &headers {
        req = req.header(key.as_str(), val.as_str());
    }

    let resp = req
        .body(body)
        .send()
        .map_err(|e| {
            if provider.format == ProviderFormat::Ollama {
                format!("Can't connect to Ollama at {}. Is it running?", provider.endpoint)
            } else {
                format!("HTTP error: {e}")
            }
        })?;

    if !resp.status().is_success() {
        let status = resp.status();
        let body = resp.text().unwrap_or_default();
        return Err(format!("API error {status}: {body}"));
    }

    let text = resp.text().map_err(|e| format!("Read error: {e}"))?;
    let mut full_response = String::new();

    // Parse SSE/NDJSON response and extract tokens
    for line in text.lines() {
        if let Some(token) = extract_token(line, &provider.format) {
            print!("{token}");
            std::io::stdout().flush().ok();
            full_response.push_str(&token);
        }
    }
    println!();

    Ok(full_response)
}

fn build_request(
    provider: &AiProvider,
    model: &str,
    api_key: &str,
    system: &str,
    user_msg: &str,
) -> (String, String, Vec<(String, String)>) {
    let mut headers = Vec::new();

    match provider.format {
        ProviderFormat::Anthropic => {
            headers.push(("x-api-key".into(), api_key.to_string()));
            headers.push(("anthropic-version".into(), "2023-06-01".into()));
            let body = json!({
                "model": model,
                "max_tokens": 4096,
                "stream": true,
                "system": system,
                "messages": [{"role": "user", "content": user_msg}]
            });
            (provider.endpoint.clone(), body.to_string(), headers)
        }
        ProviderFormat::OpenAi => {
            headers.push(("Authorization".into(), format!("Bearer {api_key}")));
            let body = json!({
                "model": model,
                "stream": true,
                "messages": [
                    {"role": "system", "content": system},
                    {"role": "user", "content": user_msg}
                ]
            });
            (provider.endpoint.clone(), body.to_string(), headers)
        }
        ProviderFormat::Gemini => {
            let url = format!(
                "{}/{}:streamGenerateContent?alt=sse&key={}",
                provider.endpoint, model, api_key
            );
            let body = json!({
                "system_instruction": {"parts": [{"text": system}]},
                "contents": [{"parts": [{"text": user_msg}]}]
            });
            (url, body.to_string(), headers)
        }
        ProviderFormat::Ollama => {
            let body = json!({
                "model": model,
                "stream": true,
                "messages": [
                    {"role": "system", "content": system},
                    {"role": "user", "content": user_msg}
                ]
            });
            (provider.endpoint.clone(), body.to_string(), headers)
        }
    }
}

/// Extract a token from an SSE or NDJSON line.
fn extract_token(line: &str, format: &ProviderFormat) -> Option<String> {
    let data = if let Some(d) = line.strip_prefix("data: ") {
        d.trim()
    } else if line.starts_with('{') {
        line.trim()
    } else {
        return None;
    };

    if data == "[DONE]" || data.is_empty() {
        return None;
    }

    let parsed: serde_json::Value = serde_json::from_str(data).ok()?;

    match format {
        ProviderFormat::Anthropic => {
            // {"type":"content_block_delta","delta":{"type":"text_delta","text":"..."}}
            parsed
                .get("delta")
                .and_then(|d| d.get("text"))
                .and_then(|t| t.as_str())
                .map(String::from)
        }
        ProviderFormat::OpenAi => {
            // {"choices":[{"delta":{"content":"..."}}]}
            parsed
                .get("choices")
                .and_then(|c| c.get(0))
                .and_then(|c| c.get("delta"))
                .and_then(|d| d.get("content"))
                .and_then(|t| t.as_str())
                .map(String::from)
        }
        ProviderFormat::Gemini => {
            // {"candidates":[{"content":{"parts":[{"text":"..."}]}}]}
            parsed
                .get("candidates")
                .and_then(|c| c.get(0))
                .and_then(|c| c.get("content"))
                .and_then(|c| c.get("parts"))
                .and_then(|p| p.get(0))
                .and_then(|p| p.get("text"))
                .and_then(|t| t.as_str())
                .map(String::from)
        }
        ProviderFormat::Ollama => {
            // {"message":{"content":"..."},"done":false}
            if parsed.get("done").and_then(|d| d.as_bool()).unwrap_or(false) {
                return None;
            }
            parsed
                .get("message")
                .and_then(|m| m.get("content"))
                .and_then(|t| t.as_str())
                .map(String::from)
        }
    }
}

/// Parse `ai` command arguments.
/// Returns (prompt, provider_override, model_override).
pub fn parse_ai_args(input: &str) -> (String, Option<String>, Option<String>) {
    let args: Vec<&str> = input.split_whitespace().collect();
    let mut prompt_parts = Vec::new();
    let mut provider = None;
    let mut model = None;
    let mut i = 0;

    while i < args.len() {
        match args[i] {
            "--provider" | "-p" if i + 1 < args.len() => {
                provider = Some(args[i + 1].to_string());
                i += 2;
            }
            "--model" | "-m" if i + 1 < args.len() => {
                model = Some(args[i + 1].to_string());
                i += 2;
            }
            _ => {
                prompt_parts.push(args[i]);
                i += 1;
            }
        }
    }

    let prompt = prompt_parts.join(" ");
    // Strip surrounding quotes if present
    let prompt = prompt
        .strip_prefix('"')
        .and_then(|s| s.strip_suffix('"'))
        .unwrap_or(&prompt)
        .to_string();

    (prompt, provider, model)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builtin_providers_exist() {
        let providers = builtin_providers();
        assert_eq!(providers.len(), 4);
        assert!(get_provider("anthropic").is_some());
        assert!(get_provider("openai").is_some());
        assert!(get_provider("gemini").is_some());
        assert!(get_provider("ollama").is_some());
    }

    #[test]
    fn parse_args_simple() {
        let (prompt, provider, model) = parse_ai_args("\"what is rust?\"");
        assert_eq!(prompt, "what is rust?");
        assert!(provider.is_none());
        assert!(model.is_none());
    }

    #[test]
    fn parse_args_with_provider() {
        let (prompt, provider, model) = parse_ai_args("--provider openai what is rust");
        assert_eq!(prompt, "what is rust");
        assert_eq!(provider, Some("openai".to_string()));
        assert!(model.is_none());
    }

    #[test]
    fn parse_args_with_model() {
        let (prompt, provider, model) = parse_ai_args("-m gpt-4 -p openai explain this");
        assert_eq!(prompt, "explain this");
        assert_eq!(provider, Some("openai".to_string()));
        assert_eq!(model, Some("gpt-4".to_string()));
    }

    #[test]
    fn extract_anthropic_token() {
        let line = r#"data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"Hello"}}"#;
        assert_eq!(
            extract_token(line, &ProviderFormat::Anthropic),
            Some("Hello".to_string())
        );
    }

    #[test]
    fn extract_openai_token() {
        let line = r#"data: {"choices":[{"delta":{"content":"world"}}]}"#;
        assert_eq!(
            extract_token(line, &ProviderFormat::OpenAi),
            Some("world".to_string())
        );
    }

    #[test]
    fn extract_gemini_token() {
        let line = r#"data: {"candidates":[{"content":{"parts":[{"text":"hi"}]}}]}"#;
        assert_eq!(
            extract_token(line, &ProviderFormat::Gemini),
            Some("hi".to_string())
        );
    }

    #[test]
    fn extract_ollama_token() {
        let line = r#"{"message":{"content":"test"},"done":false}"#;
        assert_eq!(
            extract_token(line, &ProviderFormat::Ollama),
            Some("test".to_string())
        );
    }

    #[test]
    fn extract_ollama_done() {
        let line = r#"{"message":{"content":""},"done":true}"#;
        assert_eq!(extract_token(line, &ProviderFormat::Ollama), None);
    }

    #[test]
    fn system_prompt_has_context() {
        let prompt = build_system_prompt();
        assert!(prompt.contains("Rush shell"));
        assert!(prompt.contains("CWD:"));
    }
}
