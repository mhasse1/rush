using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Rush;

/// <summary>
/// Interface for AI providers. Each provider knows how to stream a chat response
/// from its API using HttpClient + SSE/NDJSON.
/// </summary>
public interface IAiProvider
{
    string Name { get; }
    string DefaultModel { get; }
    string ApiKeyEnvVar { get; }  // e.g. "ANTHROPIC_API_KEY", "" for Ollama
    IAsyncEnumerable<string> StreamAsync(
        string systemPrompt, string userMessage,
        string model, string apiKey, CancellationToken ct);
}

// ── Anthropic (Claude) ────────────────────────────────────────────────

public class AnthropicProvider : IAiProvider
{
    public string Name => "anthropic";
    public string DefaultModel => "claude-sonnet-4-20250514";
    public string ApiKeyEnvVar => "ANTHROPIC_API_KEY";

    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt, string userMessage,
        string model, string apiKey, [EnumeratorCancellation] CancellationToken ct)
    {
        using var client = AiHttpClient.Create();
        var body = JsonSerializer.Serialize(new
        {
            model = model,
            max_tokens = 4096,
            stream = true,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userMessage } }
        });

        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        AiHttpClient.EnsureSuccess(resp);

        await foreach (var token in SseParser.ParseAnthropicStream(resp, ct))
            yield return token;
    }
}

// ── OpenAI ────────────────────────────────────────────────────────────

public class OpenAiProvider : IAiProvider
{
    public string Name => "openai";
    public string DefaultModel => "gpt-4o";
    public string ApiKeyEnvVar => "OPENAI_API_KEY";

    // Allow subclasses (custom providers) to override the endpoint
    protected virtual string Endpoint => "https://api.openai.com/v1/chat/completions";

    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt, string userMessage,
        string model, string apiKey, [EnumeratorCancellation] CancellationToken ct)
    {
        using var client = AiHttpClient.Create();
        var body = JsonSerializer.Serialize(new
        {
            model = model,
            stream = true,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            }
        });

        var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        AiHttpClient.EnsureSuccess(resp);

        await foreach (var token in SseParser.ParseOpenAiStream(resp, ct))
            yield return token;
    }
}

// ── Google Gemini ─────────────────────────────────────────────────────

public class GeminiProvider : IAiProvider
{
    public string Name => "gemini";
    public string DefaultModel => "gemini-2.0-flash";
    public string ApiKeyEnvVar => "GEMINI_API_KEY";

    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt, string userMessage,
        string model, string apiKey, [EnumeratorCancellation] CancellationToken ct)
    {
        using var client = AiHttpClient.Create();
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse&key={apiKey}";

        var body = JsonSerializer.Serialize(new
        {
            system_instruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = new[] { new { parts = new[] { new { text = userMessage } } } }
        });

        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        AiHttpClient.EnsureSuccess(resp);

        await foreach (var token in SseParser.ParseGeminiStream(resp, ct))
            yield return token;
    }
}

// ── Ollama (local) ────────────────────────────────────────────────────

public class OllamaProvider : IAiProvider
{
    public string Name => "ollama";
    public string DefaultModel => "llama3.2";
    public string ApiKeyEnvVar => "";  // No key needed

    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt, string userMessage,
        string model, string apiKey, [EnumeratorCancellation] CancellationToken ct)
    {
        using var client = AiHttpClient.Create(TimeSpan.FromMinutes(5));
        var body = JsonSerializer.Serialize(new
        {
            model = model,
            stream = true,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            }
        });

        var req = new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/chat");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            throw new AiException("can't connect to Ollama at localhost:11434");
        }
        AiHttpClient.EnsureSuccess(resp);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;

            string? text = null;
            bool isDone = false;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var contentEl))
                {
                    text = contentEl.GetString();
                }

                if (root.TryGetProperty("done", out var done) && done.GetBoolean())
                    isDone = true;
            }
            catch (JsonException) { }

            if (!string.IsNullOrEmpty(text))
                yield return text;

            if (isDone)
                yield break;
        }
    }
}

// ── Custom Provider (loaded from JSON spec) ───────────────────────────

public class CustomProvider : IAiProvider
{
    public string Name { get; }
    public string DefaultModel { get; }
    public string ApiKeyEnvVar { get; }

    private readonly string _endpoint;
    private readonly string _format; // "openai", "anthropic", "gemini", "ollama"

    public CustomProvider(string name, string endpoint, string format,
        string apiKeyEnvVar, string defaultModel)
    {
        Name = name;
        _endpoint = endpoint;
        _format = format.ToLowerInvariant();
        ApiKeyEnvVar = apiKeyEnvVar;
        DefaultModel = defaultModel;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt, string userMessage,
        string model, string apiKey, [EnumeratorCancellation] CancellationToken ct)
    {
        // Most custom providers use the OpenAI-compatible format
        using var client = AiHttpClient.Create();

        HttpRequestMessage req;
        switch (_format)
        {
            case "anthropic":
            {
                var body = JsonSerializer.Serialize(new
                {
                    model = model,
                    max_tokens = 4096,
                    stream = true,
                    system = systemPrompt,
                    messages = new[] { new { role = "user", content = userMessage } }
                });
                req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                req.Headers.Add("x-api-key", apiKey);
                req.Headers.Add("anthropic-version", "2023-06-01");
                break;
            }
            case "gemini":
            {
                var url = _endpoint.Contains("?") ? $"{_endpoint}&alt=sse&key={apiKey}"
                    : $"{_endpoint}?alt=sse&key={apiKey}";
                var body = JsonSerializer.Serialize(new
                {
                    system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                    contents = new[] { new { parts = new[] { new { text = userMessage } } } }
                });
                req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                break;
            }
            case "ollama":
            {
                var body = JsonSerializer.Serialize(new
                {
                    model = model,
                    stream = true,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userMessage }
                    }
                });
                req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                break;
            }
            default: // "openai" and anything else
            {
                var body = JsonSerializer.Serialize(new
                {
                    model = model,
                    stream = true,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userMessage }
                    }
                });
                req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                break;
            }
        }

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        AiHttpClient.EnsureSuccess(resp);

        var parser = _format switch
        {
            "anthropic" => SseParser.ParseAnthropicStream(resp, ct),
            "gemini" => SseParser.ParseGeminiStream(resp, ct),
            // For ollama, we'd need NDJSON parsing here — but custom ollama providers
            // are unusual. Fall back to OpenAI SSE.
            _ => SseParser.ParseOpenAiStream(resp, ct)
        };

        await foreach (var token in parser)
            yield return token;
    }
}

// ── Shared Utilities ──────────────────────────────────────────────────

/// <summary>
/// Shared HttpClient factory.
/// </summary>
internal static class AiHttpClient
{
    public static HttpClient Create(TimeSpan? timeout = null)
    {
        var client = new HttpClient();
        client.Timeout = timeout ?? TimeSpan.FromMinutes(2);
        return client;
    }

    public static void EnsureSuccess(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;

        var status = (int)resp.StatusCode;
        throw status switch
        {
            401 or 403 => new AiException("auth failed. Check API key in secrets.rush"),
            429 => new AiException("rate limited. Try again shortly."),
            _ => new AiException($"HTTP {status}: {resp.ReasonPhrase}")
        };
    }
}

/// <summary>
/// SSE stream parser for AI provider responses.
/// Reads "data: {json}" lines and extracts text tokens.
/// </summary>
internal static class SseParser
{
    /// <summary>
    /// Anthropic SSE: content_block_delta events contain text deltas.
    /// </summary>
    public static async IAsyncEnumerable<string> ParseAnthropicStream(
        HttpResponseMessage resp, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line == null || !line.StartsWith("data: ")) continue;

            var json = line[6..];
            if (json == "[DONE]") yield break;

            string? text = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var type) &&
                    type.GetString() == "content_block_delta" &&
                    root.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("text", out var textEl))
                {
                    text = textEl.GetString();
                }
            }
            catch (JsonException) { }

            if (!string.IsNullOrEmpty(text))
                yield return text;
        }
    }

    /// <summary>
    /// OpenAI SSE: choices[0].delta.content in each data line.
    /// </summary>
    public static async IAsyncEnumerable<string> ParseOpenAiStream(
        HttpResponseMessage resp, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line == null || !line.StartsWith("data: ")) continue;

            var json = line[6..];
            if (json == "[DONE]") yield break;

            string? text = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() > 0)
                {
                    var delta = choices[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var content))
                        text = content.GetString();
                }
            }
            catch (JsonException) { }

            if (!string.IsNullOrEmpty(text))
                yield return text;
        }
    }

    /// <summary>
    /// Gemini SSE: candidates[0].content.parts[0].text in each data line.
    /// </summary>
    public static async IAsyncEnumerable<string> ParseGeminiStream(
        HttpResponseMessage resp, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line == null || !line.StartsWith("data: ")) continue;

            var json = line[6..];
            if (json == "[DONE]") yield break;

            string? text = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("candidates", out var candidates) &&
                    candidates.GetArrayLength() > 0 &&
                    candidates[0].TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0 &&
                    parts[0].TryGetProperty("text", out var textEl))
                {
                    text = textEl.GetString();
                }
            }
            catch (JsonException) { }

            if (!string.IsNullOrEmpty(text))
                yield return text;
        }
    }
}

/// <summary>
/// AI-specific exception for user-friendly error messages.
/// </summary>
public class AiException : Exception
{
    public AiException(string message) : base(message) { }
}
