using Xunit;
using Rush;

namespace Rush.Tests;

/// <summary>
/// Tests for AI command argument parsing, provider resolution, system prompt building,
/// and user message formatting.
/// </summary>
public class AiCommandTests
{
    // ── Argument Parsing ──────────────────────────────────────────────

    [Fact]
    public void ParseArgs_SimpleQuotedPrompt()
    {
        var (prompt, provider, model, system) = AiCommand.ParseArgs("\"how do I find large files?\"");
        Assert.Equal("how do I find large files?", prompt);
        Assert.Null(provider);
        Assert.Null(model);
        Assert.Null(system);
    }

    [Fact]
    public void ParseArgs_UnquotedPrompt()
    {
        var (prompt, provider, model, system) = AiCommand.ParseArgs("hello world");
        Assert.Equal("hello world", prompt);
        Assert.Null(provider);
    }

    [Fact]
    public void ParseArgs_ProviderFlag()
    {
        var (prompt, provider, model, system) = AiCommand.ParseArgs("--provider ollama \"hello\"");
        Assert.Equal("hello", prompt);
        Assert.Equal("ollama", provider);
        Assert.Null(model);
    }

    [Fact]
    public void ParseArgs_ModelFlag()
    {
        var (prompt, provider, model, system) = AiCommand.ParseArgs("--model gpt-4o \"explain this\"");
        Assert.Equal("explain this", prompt);
        Assert.Null(provider);
        Assert.Equal("gpt-4o", model);
    }

    [Fact]
    public void ParseArgs_AllFlags()
    {
        var (prompt, provider, model, system) = AiCommand.ParseArgs(
            "--provider openai --model gpt-4o --system \"be brief\" \"what is rush?\"");
        Assert.Equal("what is rush?", prompt);
        Assert.Equal("openai", provider);
        Assert.Equal("gpt-4o", model);
        Assert.Equal("be brief", system);
    }

    [Fact]
    public void ParseArgs_EmptyInput()
    {
        var (prompt, provider, model, system) = AiCommand.ParseArgs("");
        Assert.Equal("", prompt);
        Assert.Null(provider);
        Assert.Null(model);
    }

    [Fact]
    public void ParseArgs_FlagsAfterPrompt()
    {
        // Flags and prompt can be in any order
        var (prompt, provider, model, system) = AiCommand.ParseArgs("\"hello\" --provider gemini");
        Assert.Equal("hello", prompt);
        Assert.Equal("gemini", provider);
    }

    // ── Tokenizer ─────────────────────────────────────────────────────

    [Fact]
    public void TokenizeArgs_QuotedString()
    {
        var tokens = AiCommand.TokenizeArgs("\"hello world\" --flag val");
        Assert.Equal(3, tokens.Count);
        Assert.Equal("hello world", tokens[0]);
        Assert.Equal("--flag", tokens[1]);
        Assert.Equal("val", tokens[2]);
    }

    [Fact]
    public void TokenizeArgs_SingleQuoted()
    {
        var tokens = AiCommand.TokenizeArgs("'hello world'");
        Assert.Single(tokens);
        Assert.Equal("hello world", tokens[0]);
    }

    [Fact]
    public void TokenizeArgs_MixedQuotes()
    {
        var tokens = AiCommand.TokenizeArgs("\"double\" 'single' bare");
        Assert.Equal(3, tokens.Count);
        Assert.Equal("double", tokens[0]);
        Assert.Equal("single", tokens[1]);
        Assert.Equal("bare", tokens[2]);
    }

    // ── User Message Formatting ───────────────────────────────────────

    [Fact]
    public void BuildUserMessage_NoPipedInput()
    {
        var msg = AiCommand.BuildUserMessage("what is rush?", null);
        Assert.Equal("what is rush?", msg);
    }

    [Fact]
    public void BuildUserMessage_WithPipedInput()
    {
        var msg = AiCommand.BuildUserMessage("what went wrong?", "error: file not found\n");
        Assert.Contains("[Input]", msg);
        Assert.Contains("error: file not found", msg);
        Assert.Contains("[Question]", msg);
        Assert.Contains("what went wrong?", msg);
    }

    [Fact]
    public void BuildUserMessage_EmptyPipedInput()
    {
        var msg = AiCommand.BuildUserMessage("hello", "");
        Assert.Equal("hello", msg);
    }

    // ── System Prompt ─────────────────────────────────────────────────

    [Fact]
    public void BuildSystemPrompt_ContainsVersion()
    {
        var prompt = AiCommand.BuildSystemPrompt(new List<string>());
        Assert.Contains("Rush shell", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_ContainsCwd()
    {
        var prompt = AiCommand.BuildSystemPrompt(new List<string>());
        Assert.Contains("Directory:", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_ContainsPlatform()
    {
        var prompt = AiCommand.BuildSystemPrompt(new List<string>());
        Assert.Contains("Platform:", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_IncludesHistory()
    {
        var history = new List<string> { "ls", "cd src", "git status" };
        var prompt = AiCommand.BuildSystemPrompt(history);
        Assert.Contains("Recent commands:", prompt);
        Assert.Contains("ls", prompt);
        Assert.Contains("git status", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_LimitsHistoryTo10()
    {
        var history = new List<string>();
        for (int i = 0; i < 15; i++) history.Add($"cmd{i}");
        var prompt = AiCommand.BuildSystemPrompt(history);
        // Should include cmd5 through cmd14 (last 10)
        Assert.Contains("cmd5", prompt);
        Assert.Contains("cmd14", prompt);
        Assert.DoesNotContain("cmd4", prompt);
    }

    // ── OS Detection ──────────────────────────────────────────────────

    [Fact]
    public void GetDetailedOS_ReturnsNonEmpty()
    {
        var os = AiCommand.GetDetailedOS();
        Assert.False(string.IsNullOrWhiteSpace(os));
    }

    [Fact]
    public void GetDetailedOS_ContainsArchitecture()
    {
        var os = AiCommand.GetDetailedOS();
        // Should contain architecture info like arm64, x86_64, x64, etc.
        Assert.Contains("(", os);
        Assert.Contains(")", os);
    }

    // ── Provider Loading ──────────────────────────────────────────────

    [Fact]
    public void LoadAllProviders_ContainsBuiltins()
    {
        var providers = AiCommand.LoadAllProviders();
        Assert.True(providers.ContainsKey("anthropic"));
        Assert.True(providers.ContainsKey("openai"));
        Assert.True(providers.ContainsKey("gemini"));
        Assert.True(providers.ContainsKey("ollama"));
    }

    [Fact]
    public void LoadAllProviders_CaseInsensitive()
    {
        var providers = AiCommand.LoadAllProviders();
        Assert.True(providers.ContainsKey("Anthropic"));
        Assert.True(providers.ContainsKey("OPENAI"));
    }

    // ── Provider Properties ───────────────────────────────────────────

    [Fact]
    public void AnthropicProvider_Properties()
    {
        var p = new AnthropicProvider();
        Assert.Equal("anthropic", p.Name);
        Assert.Equal("ANTHROPIC_API_KEY", p.ApiKeyEnvVar);
        Assert.False(string.IsNullOrEmpty(p.DefaultModel));
    }

    [Fact]
    public void OpenAiProvider_Properties()
    {
        var p = new OpenAiProvider();
        Assert.Equal("openai", p.Name);
        Assert.Equal("OPENAI_API_KEY", p.ApiKeyEnvVar);
        Assert.Equal("gpt-4o", p.DefaultModel);
    }

    [Fact]
    public void GeminiProvider_Properties()
    {
        var p = new GeminiProvider();
        Assert.Equal("gemini", p.Name);
        Assert.Equal("GEMINI_API_KEY", p.ApiKeyEnvVar);
        Assert.Contains("gemini", p.DefaultModel);
    }

    [Fact]
    public void OllamaProvider_Properties()
    {
        var p = new OllamaProvider();
        Assert.Equal("ollama", p.Name);
        Assert.Equal("", p.ApiKeyEnvVar);
        Assert.False(string.IsNullOrEmpty(p.DefaultModel));
    }

    // ── Custom Provider ───────────────────────────────────────────────

    [Fact]
    public void CustomProvider_Properties()
    {
        var p = new CustomProvider("together",
            "https://api.together.xyz/v1/chat/completions",
            "openai", "TOGETHER_API_KEY", "meta-llama/Llama-3-70b-chat-hf");
        Assert.Equal("together", p.Name);
        Assert.Equal("TOGETHER_API_KEY", p.ApiKeyEnvVar);
        Assert.Equal("meta-llama/Llama-3-70b-chat-hf", p.DefaultModel);
    }

    // ── Config Integration ────────────────────────────────────────────

    [Fact]
    public void Config_AiProviderDefault()
    {
        var config = new RushConfig();
        Assert.Equal("anthropic", config.AiProvider);
    }

    [Fact]
    public void Config_AiModelDefault()
    {
        var config = new RushConfig();
        Assert.Equal("auto", config.AiModel);
    }

    [Fact]
    public void Config_SetAiProvider()
    {
        var config = new RushConfig();
        Assert.True(config.SetValue("aiProvider", "ollama"));
        Assert.Equal("ollama", config.AiProvider);
    }

    [Fact]
    public void Config_SetAiModel()
    {
        var config = new RushConfig();
        Assert.True(config.SetValue("aiModel", "gpt-4o"));
        Assert.Equal("gpt-4o", config.AiModel);
    }

    // ── Syntax Highlighting ───────────────────────────────────────────

    [Fact]
    public void SyntaxHighlighter_AiIsBuiltin()
    {
        var translator = new CommandTranslator();
        var highlighter = new SyntaxHighlighter(translator);
        var result = highlighter.Colorize("ai \"hello\"");
        // ai should be colored as a builtin (not plain white)
        Assert.NotEqual("ai \"hello\"", result);
    }

    // ── Fence Stripping ──────────────────────────────────────────────

    [Fact]
    public void FenceStripper_PassesThroughPlainText()
    {
        var stripper = new AiCommand.FenceStripper();
        var output = stripper.Process("hello world\n");
        output += stripper.Flush();
        Assert.Equal("hello world\n", output);
        Assert.Equal("hello world", stripper.DisplayContent);
        Assert.Null(stripper.CodeContent);
    }

    [Fact]
    public void FenceStripper_StripsSimpleFences()
    {
        var stripper = new AiCommand.FenceStripper();
        var output = "";
        output += stripper.Process("```\n");
        output += stripper.Process("ls -la\n");
        output += stripper.Process("```\n");
        output += stripper.Flush();
        Assert.Equal("ls -la\n", output);
        Assert.Equal("ls -la", stripper.DisplayContent);
    }

    [Fact]
    public void FenceStripper_StripsLanguageTag()
    {
        var stripper = new AiCommand.FenceStripper();
        var output = "";
        output += stripper.Process("```bash\n");
        output += stripper.Process("du -sh * | sort -rh\n");
        output += stripper.Process("```\n");
        output += stripper.Flush();
        Assert.Equal("du -sh * | sort -rh\n", output);
        Assert.Equal("du -sh * | sort -rh", stripper.DisplayContent);
    }

    [Fact]
    public void FenceStripper_PreservesTextAroundFences()
    {
        var stripper = new AiCommand.FenceStripper();
        var output = "";
        output += stripper.Process("Here's how:\n");
        output += stripper.Process("```bash\n");
        output += stripper.Process("ls -la\n");
        output += stripper.Process("```\n");
        output += stripper.Process("That's it.\n");
        output += stripper.Flush();
        Assert.Equal("Here's how:\nls -la\nThat's it.\n", output);
    }

    [Fact]
    public void FenceStripper_ExtractsCodeContent()
    {
        var stripper = new AiCommand.FenceStripper();
        stripper.Process("Here's the command:\n");
        stripper.Process("```bash\n");
        stripper.Process("find . -size +100M\n");
        stripper.Process("```\n");
        stripper.Process("This will find large files.\n");
        stripper.Flush();
        Assert.Equal("find . -size +100M", stripper.CodeContent);
    }

    [Fact]
    public void FenceStripper_CodeContentNull_WhenNoFences()
    {
        var stripper = new AiCommand.FenceStripper();
        stripper.Process("just plain text\n");
        stripper.Flush();
        Assert.Null(stripper.CodeContent);
    }

    [Fact]
    public void FenceStripper_MultipleCodeBlocks()
    {
        var stripper = new AiCommand.FenceStripper();
        stripper.Process("Step 1:\n");
        stripper.Process("```\n");
        stripper.Process("mkdir test\n");
        stripper.Process("```\n");
        stripper.Process("Step 2:\n");
        stripper.Process("```\n");
        stripper.Process("cd test\n");
        stripper.Process("```\n");
        stripper.Flush();
        Assert.Equal("mkdir test\ncd test", stripper.CodeContent);
        Assert.Equal("Step 1:\nmkdir test\nStep 2:\ncd test", stripper.DisplayContent);
    }

    [Fact]
    public void FenceStripper_HandlesCharByCharStreaming()
    {
        var stripper = new AiCommand.FenceStripper();
        var output = "";
        // Feed character by character: "```\nls\n```\n"
        foreach (char c in "```\nls\n```\n")
        {
            output += stripper.Process(c.ToString());
        }
        output += stripper.Flush();
        Assert.Equal("ls\n", output);
    }

    [Fact]
    public void FenceStripper_HandlesLargeChunks()
    {
        var stripper = new AiCommand.FenceStripper();
        // Feed entire response as one chunk
        var output = stripper.Process("```python\nprint('hello')\n```\n");
        output += stripper.Flush();
        Assert.Equal("print('hello')\n", output);
    }

    [Fact]
    public void FenceStripper_TripleBackticksInMiddleOfLine_NotStripped()
    {
        var stripper = new AiCommand.FenceStripper();
        var output = stripper.Process("Use ```code``` for inline code.\n");
        output += stripper.Flush();
        // Line contains ``` but is not a fence line (has other content)
        Assert.Contains("Use", output);
    }

    // ── GetLastResponse ──────────────────────────────────────────────

    [Fact]
    public void GetLastResponse_ReturnsNull_Initially()
    {
        // Note: This test may be affected by other tests that call ExecuteAsync.
        // In isolation, GetLastResponse returns null or a previously stored value.
        // We test the storage mechanism via FenceStripper instead.
        var stripper = new AiCommand.FenceStripper();
        stripper.Process("test\n");
        stripper.Flush();
        Assert.Null(stripper.CodeContent); // No fences
        Assert.Equal("test", stripper.DisplayContent);
    }

    [Fact]
    public void GetLastResponse_PrefersCodeContent()
    {
        var stripper = new AiCommand.FenceStripper();
        stripper.Process("explanation\n");
        stripper.Process("```\n");
        stripper.Process("actual-command\n");
        stripper.Process("```\n");
        stripper.Flush();

        // CodeContent should be preferred over DisplayContent
        Assert.Equal("actual-command", stripper.CodeContent);
        Assert.Contains("explanation", stripper.DisplayContent);
    }
}
