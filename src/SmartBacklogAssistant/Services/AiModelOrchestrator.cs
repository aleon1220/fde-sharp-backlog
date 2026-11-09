using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Step 2 (Local -> process & validate):
/// Mocked AI model orchestrator that selects an AI model from user input
/// (e.g., Azure OpenAI, Google Gemini, Anthropic Claude) or defaults to selecting
/// a prompt from a Markdown file and suggesting the user run it in their desired AI model.
/// </summary>
public sealed class AiModelOrchestrator
{
    public AiProviderType ResolveProvider(string? userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return AiProviderType.ManualPrompt;
        }

        return userInput.Trim().ToLowerInvariant() switch
        {
            "1" or "azure" or "azure-openai" or "azureopenai" => AiProviderType.AzureOpenAi,
            "2" or "gemini" or "google" or "google-gemini" or "googlegemini" => AiProviderType.GoogleGemini,
            "3" or "claude" or "anthropic" or "anthropic-claude" or "anthropicclaude" => AiProviderType.AnthropicClaude,
            _ => AiProviderType.ManualPrompt
        };
    }

    public ProcessedOutcome Process(
        string rawData,
        string renderedPrompt,
        AiProviderType provider = AiProviderType.ManualPrompt,
        string? apiKey = null)
    {
        if (string.IsNullOrWhiteSpace(rawData))
        {
            throw new ArgumentException("Raw data is required for processing.", nameof(rawData));
        }

        if (string.IsNullOrWhiteSpace(renderedPrompt))
        {
            throw new ArgumentException("Rendered prompt is required for processing.", nameof(renderedPrompt));
        }

        var guidanceMessage = BuildGuidanceMessage(provider, apiKey);
        var (title, description, userStory, scenario) = ExtractStructuredDraft(rawData);

        var outcome = new ProcessedOutcome(
            ProviderUsed: provider,
            RenderedPrompt: renderedPrompt,
            GuidanceMessage: guidanceMessage,
            Title: title,
            Description: description,
            UserStory: userStory,
            Scenarios: [scenario]);

        ValidateOutcome(outcome);
        return outcome;
    }

    public void ValidateOutcome(ProcessedOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (string.IsNullOrWhiteSpace(outcome.Title))
        {
            throw new InvalidOperationException("Validation failed: Outcome Title is missing.");
        }

        if (string.IsNullOrWhiteSpace(outcome.Description))
        {
            throw new InvalidOperationException("Validation failed: Outcome Description is missing.");
        }

        if (outcome.Scenarios is null || outcome.Scenarios.Count == 0)
        {
            throw new InvalidOperationException("Validation failed: At least one Given/When/Then scenario is required.");
        }

        foreach (var scenario in outcome.Scenarios)
        {
            if (string.IsNullOrWhiteSpace(scenario.Given) ||
                string.IsNullOrWhiteSpace(scenario.When) ||
                string.IsNullOrWhiteSpace(scenario.Then))
            {
                throw new InvalidOperationException("Validation failed: Given, When, and Then fields must all be non-empty.");
            }
        }
    }

    private static string BuildGuidanceMessage(AiProviderType provider, string? apiKey)
    {
        var hasKey = !string.IsNullOrWhiteSpace(apiKey);
        var keyStatus = hasKey ? $"API key injected ({GeminiSecretProvider.MaskKey(apiKey)})" : "no API key provided (running mock simulation)";

        return provider switch
        {
            AiProviderType.AzureOpenAi =>
                $"[Mock Orchestrator -> Azure OpenAI ({keyStatus})] Processed prompt using mocked Azure OpenAI model.",
            AiProviderType.GoogleGemini =>
                $"[Gemini Orchestrator -> Google Gemini ({RuntimeEnvironmentService.DefaultGeminiModel}) | {keyStatus} | Keys: {GeminiSecretProvider.GeminiApiKeysUrl}] Processed prompt using Google Gemini model.",
            AiProviderType.AnthropicClaude =>
                $"[Mock Orchestrator -> Anthropic Claude ({keyStatus})] Processed prompt using mocked Claude model.",
            _ =>
                "[Default Mode -> Prompt from Markdown] Copy the rendered prompt above and run it in your desired AI model (e.g., Microsoft Copilot, Azure OpenAI, Gemini, or Claude). Using baseline draft outcome for review."
        };
    }

    private static (string Title, string Description, string UserStory, GherkinScenario Scenario) ExtractStructuredDraft(string rawData)
    {
        var lines = rawData
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var title = ExtractPrefixedLine(lines, "Discovery Note", "Title", "Subject")
            ?? lines.FirstOrDefault()
            ?? "Backlog Requirement";

        // Clean up leading prefixes if present in title
        var dashIndex = title.IndexOf('-', StringComparison.Ordinal);
        if (title.StartsWith("Discovery Note", StringComparison.OrdinalIgnoreCase) && dashIndex > 0 && dashIndex < title.Length - 1)
        {
            title = title[(dashIndex + 1)..].Trim();
        }

        var context = ExtractPrefixedValue(lines, "Context:")
            ?? string.Join(" ", lines);

        var userStory = ExtractPrefixedValue(lines, "Requirement:")
            ?? ExtractLineContaining(lines, "As a ")
            ?? ExtractLineContaining(lines, "As an ")
            ?? $"As a user, I want {title.ToLowerInvariant()}, so that the team delivers the requested capability.";

        var given = ExtractPrefixedValue(lines, "Precondition:", "Given:")
            ?? "the system is in a valid initial state for the requirement";
        var when = ExtractPrefixedValue(lines, "Action:", "When:")
            ?? "the user triggers the requested workflow action";
        var then = ExtractPrefixedValue(lines, "Expected Outcome:", "Then:")
            ?? "the system completes the action and meets acceptance criteria";

        var description = $"{context} {userStory}".Trim();

        return (title, description, userStory, new GherkinScenario(given, when, then));
    }

    private static string? ExtractPrefixedLine(string[] lines, params string[] prefixes)
    {
        foreach (var line in lines)
        {
            foreach (var prefix in prefixes)
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return line;
                }
            }
        }

        return null;
    }

    private static string? ExtractPrefixedValue(string[] lines, params string[] prefixes)
    {
        foreach (var line in lines)
        {
            foreach (var prefix in prefixes)
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var value = line[prefix.Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }

        return null;
    }

    private static string? ExtractLineContaining(string[] lines, string substring)
    {
        return lines.FirstOrDefault(l => l.Contains(substring, StringComparison.OrdinalIgnoreCase));
    }
}
