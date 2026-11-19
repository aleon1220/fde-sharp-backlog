using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Step 2 (Local -> process &amp; validate):
/// AI model orchestrator that supports a live Google Gemini API path and mocked
/// Azure OpenAI/Anthropic providers, or defaults to selecting
/// a prompt from a Markdown file and suggesting the user run it in their desired AI model.
/// </summary>
public sealed class AiModelOrchestrator
{
    private readonly ILogger<AiModelOrchestrator> _logger;

    public AiModelOrchestrator(ILogger<AiModelOrchestrator>? logger = null)
    {
        _logger = logger ?? NullLogger<AiModelOrchestrator>.Instance;
    }

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
            "4" or "copilot-sdk" or "github-copilot" or "gh-copilot" or "ghcp" or "copilot_sdk" => AiProviderType.GitHubCopilot,
            _ => AiProviderType.ManualPrompt
        };
    }

    public ProcessedOutcome Process(
        string rawData,
        string renderedPrompt,
        AiProviderType provider = AiProviderType.ManualPrompt,
        string? apiKey = null,
        string? generatedContent = null,
        string? modelName = null,
        BacklogType backlogType = BacklogType.Type01,
        OutcomeVerbosity verbosity = OutcomeVerbosity.Balanced)
    {
        if (string.IsNullOrWhiteSpace(rawData))
        {
            throw new ArgumentException("Raw data is required for processing.", nameof(rawData));
        }

        if (string.IsNullOrWhiteSpace(renderedPrompt))
        {
            throw new ArgumentException("Rendered prompt is required for processing.", nameof(renderedPrompt));
        }

        var guidanceMessage = BuildGuidanceMessage(provider, apiKey, generatedContent is not null, modelName);
        var (title, description, userStory, scenarios, alternatives) = generatedContent is null
            ? ToDraft(ExtractStructuredDraft(rawData))
            : ParseGeneratedDraft(generatedContent);

        description = DescriptionFormatter.FormatWithVerbosity(description, verbosity);

        var outcome = new ProcessedOutcome(
            ProviderUsed: provider,
            RenderedPrompt: renderedPrompt,
            GuidanceMessage: guidanceMessage,
            Title: title,
            Description: description,
            UserStory: userStory,
            Scenarios: scenarios,
            Alternatives: alternatives);

        ValidateOutcome(outcome, backlogType);
        LogEvents.OutcomeOrchestrated(
            _logger,
            outcome.Title,
            provider.ToString(),
            backlogType.ToString(),
            outcome.Scenarios.Count,
            verbosity.ToString());
        return outcome;
    }

    public GoalExpansionResult ParseGoalExpansionResponse(string generatedContent, string fallbackGoalTitle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedContent);

        var sanitizedJson = ExtractJsonPayload(generatedContent);
        using var document = System.Text.Json.JsonDocument.Parse(sanitizedJson);
        var root = document.RootElement;
        if (root.ValueKind == System.Text.Json.JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            root = root[0];
        }

        var fallback = GoalExpansionSynthesizer.ExpandOffline(fallbackGoalTitle);
        var enhancedTitle = ReadFlexibleText(root, "enhancedTitle", "EnhancedTitle", "title", "Title", "headline", "summary");
        if (string.IsNullOrWhiteSpace(enhancedTitle))
        {
            enhancedTitle = fallback.EnhancedTitle;
        }

        var userStory = ReadFlexibleText(root, "userStory", "UserStory", "user_story");
        if (string.IsNullOrWhiteSpace(userStory))
        {
            userStory = fallback.UserStory;
        }

        var discoverySummary = ReadFlexibleText(root, "discoverySummary", "DiscoverySummary", "description", "Description", "details", "Details");
        var checklist = new List<string>();
        if (TryGetPropertyIgnoreCase(root, out var checklistProp, "keyChecklist", "KeyChecklist", "checklist", "Checklist") &&
            checklistProp.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in checklistProp.EnumerateArray())
            {
                var itemText = FormatJsonElementAsText(item);
                if (!string.IsNullOrWhiteSpace(itemText))
                {
                    checklist.Add(itemText);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(discoverySummary))
        {
            discoverySummary = checklist.Count > 0
                ? $"**Suggested Actions:**\n{string.Join('\n', checklist.Select(c => $"* {c}"))}"
                : fallback.DiscoverySummary;
        }

        discoverySummary = DescriptionFormatter.Normalize(discoverySummary);

        var alternatives = ParseAlternativesFromJson(root)
            ?? GoalExpansionSynthesizer.BuildFallbackAlternatives(enhancedTitle, discoverySummary, userStory);

        return new GoalExpansionResult(
            EnhancedTitle: enhancedTitle,
            UserStory: userStory,
            DiscoverySummary: discoverySummary,
            KeyChecklist: checklist.Count > 0 ? checklist : fallback.KeyChecklist,
            Alternatives: alternatives);
    }

    public void ValidateOutcome(ProcessedOutcome outcome) => ValidateOutcome(outcome, BacklogType.Type01);

    public void ValidateOutcome(ProcessedOutcome outcome, BacklogType backlogType)
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

        if (backlogType == BacklogType.Type02)
        {
            return;
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

    private static string BuildGuidanceMessage(AiProviderType provider, string? apiKey, bool liveGenerated, string? modelName)
    {
        var hasKey = !string.IsNullOrWhiteSpace(apiKey);
        var keyStatus = hasKey
            ? $"API key injected ({GeminiSecretProvider.MaskKey(apiKey)})"
            : provider == AiProviderType.GoogleGemini && !liveGenerated
                ? "API key not required (offline mode)"
                : provider == AiProviderType.GitHubCopilot
                    ? "Copilot CLI auth / BYOK"
                    : "no API key provided";

        return provider switch
        {
            AiProviderType.GitHubCopilot =>
                liveGenerated
                    ? $"[GitHub Copilot SDK ({modelName ?? RuntimeEnvironmentService.DefaultCopilotModel}) | {keyStatus}] Generated backlog draft via Copilot agent runtime."
                    : $"[Offline Copilot Draft ({modelName ?? RuntimeEnvironmentService.DefaultCopilotModel})] Processed prompt using local deterministic fallback.",
            AiProviderType.AzureOpenAi =>
                liveGenerated
                    ? $"[Azure OpenAI / Copilot BYOK ({modelName ?? "gpt-4o"}) | {keyStatus}] Generated backlog draft via live endpoint."
                    : $"[Mock Orchestrator -> Azure OpenAI ({keyStatus})] Processed prompt using mocked Azure OpenAI model.",
            AiProviderType.GoogleGemini =>
                liveGenerated
                    ? $"[Live Gemini API -> Google Gemini ({modelName ?? RuntimeEnvironmentService.DefaultGeminiModel}) | {keyStatus}] Generated backlog draft using the Gemini API."
                    : $"[Offline Gemini Draft -> Google Gemini ({modelName ?? RuntimeEnvironmentService.DefaultGeminiModel}) | {keyStatus} | Keys: {GeminiSecretProvider.GeminiApiKeysUrl}] Processed prompt using local deterministic fallback (enable Live Gemini API to call generateContent).",
            AiProviderType.AnthropicClaude =>
                liveGenerated
                    ? $"[Anthropic Claude / Copilot BYOK ({modelName ?? "claude-sonnet-4.5"}) | {keyStatus}] Generated backlog draft via live endpoint."
                    : $"[Mock Orchestrator -> Anthropic Claude ({keyStatus})] Processed prompt using mocked Claude model.",
            _ =>
                "[Default Mode -> Prompt from Markdown] Copy the rendered prompt above and run it in your desired AI model (e.g., Microsoft Copilot, Azure OpenAI, Gemini, or Claude). Using baseline draft outcome for review."
        };
    }

    private static (string Title, string Description, string UserStory, IReadOnlyList<GherkinScenario> Scenarios, IReadOnlyList<DraftAlternative>? Alternatives) ToDraft(
        (string Title, string Description, string UserStory, GherkinScenario Scenario) draft) =>
        (draft.Title, draft.Description, draft.UserStory, [draft.Scenario], null);

    private static (string Title, string Description, string UserStory, IReadOnlyList<GherkinScenario> Scenarios, IReadOnlyList<DraftAlternative>? Alternatives) ParseGeneratedDraft(
        string generatedContent)
    {
        var sanitizedJson = ExtractJsonPayload(generatedContent);
        using var document = System.Text.Json.JsonDocument.Parse(sanitizedJson);
        var root = document.RootElement;
        if (root.ValueKind == System.Text.Json.JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            root = root[0];
        }

        var title = ReadFlexibleText(root, "title", "Title", "summary", "Summary", "headline");
        var description = ReadFlexibleText(root, "description", "Description", "details", "Details", "body", "Body");
        var userStory = ReadFlexibleText(root, "userStory", "UserStory", "user_story");
        var scenarios = new List<GherkinScenario>();

        if (TryGetPropertyIgnoreCase(root, out var scenarioElements, "scenarios", "Scenarios", "acceptanceCriteria", "AcceptanceCriteria") &&
            scenarioElements.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var scenario in scenarioElements.EnumerateArray())
            {
                scenarios.Add(new GherkinScenario(
                    ReadFlexibleText(scenario, "given", "Given"),
                    ReadFlexibleText(scenario, "when", "When"),
                    ReadFlexibleText(scenario, "then", "Then")));
            }
        }

        if (scenarios.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(description) && !string.IsNullOrWhiteSpace(userStory))
            {
                description = userStory;
            }
            else if (string.IsNullOrWhiteSpace(userStory) && !string.IsNullOrWhiteSpace(description))
            {
                userStory = description;
            }
        }

        var alternatives = ParseAlternativesFromJson(root);
        return (title, description, userStory, scenarios, alternatives);
    }

    private static IReadOnlyList<DraftAlternative>? ParseAlternativesFromJson(System.Text.Json.JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(
                root,
                out var altElements,
                "alternatives",
                "Alternatives",
                "alternativeTitles",
                "titleAlternatives",
                "options") ||
            altElements.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        var alternatives = new List<DraftAlternative>();
        foreach (var item in altElements.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                continue;
            }

            var angle = ReadFlexibleText(item, "angle", "Angle", "focus", "Focus", "theme", "Theme");
            var title = ReadFlexibleText(item, "title", "Title", "headline", "Headline", "name");
            var description = ReadFlexibleText(item, "description", "Description", "summary", "Summary", "details", "Details");

            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(angle))
            {
                angle = $"Option {alternatives.Count + 1}";
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                description = $"Alternative focused on {angle.ToLowerInvariant()}.";
            }

            alternatives.Add(new DraftAlternative(angle, title, DescriptionFormatter.Normalize(description)));
            if (alternatives.Count == 3)
            {
                break;
            }
        }

        return alternatives.Count == 0 ? null : alternatives;
    }

    private static string ExtractJsonPayload(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            trimmed = firstNewline > 0
                ? trimmed[(firstNewline + 1)..]
                : trimmed.TrimStart('`');

            var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
            {
                trimmed = trimmed[..closingFence];
            }

            return trimmed.Trim();
        }

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return trimmed;
        }

        // Handle responses that include brief prose before a fenced JSON block or raw JSON object
        var fenceStart = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var afterFence = trimmed[(fenceStart + 3)..];
            var lineEnd = afterFence.IndexOf('\n');
            if (lineEnd >= 0)
            {
                var body = afterFence[(lineEnd + 1)..];
                var fenceEnd = body.IndexOf("```", StringComparison.Ordinal);
                if (fenceEnd >= 0)
                {
                    return body[..fenceEnd].Trim();
                }
            }
        }

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return trimmed[firstBrace..(lastBrace + 1)].Trim();
        }

        return trimmed;
    }

    private static bool TryGetPropertyIgnoreCase(
        System.Text.Json.JsonElement element,
        out System.Text.Json.JsonElement value,
        params string[] propertyNames)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var name in propertyNames)
            {
                if (element.TryGetProperty(name, out value))
                {
                    return true;
                }
            }

            foreach (var prop in element.EnumerateObject())
            {
                foreach (var name in propertyNames)
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }
        }

        value = default;
        return false;
    }

    private static string ReadFlexibleText(System.Text.Json.JsonElement element, params string[] propertyNames)
    {
        if (!TryGetPropertyIgnoreCase(element, out var value, propertyNames))
        {
            return string.Empty;
        }

        return FormatJsonElementAsText(value);
    }

    private static string FormatJsonElementAsText(System.Text.Json.JsonElement value)
    {
        switch (value.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String:
                return (value.GetString() ?? string.Empty).Trim();

            case System.Text.Json.JsonValueKind.Array:
            {
                var items = value.EnumerateArray()
                    .Select(FormatJsonElementAsText)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(DescriptionFormatter.FormatAsBullet);
                return string.Join("\n", items);
            }

            case System.Text.Json.JsonValueKind.Object:
            {
                var lines = new List<string>();
                foreach (var prop in value.EnumerateObject())
                {
                    var propText = FormatJsonElementAsText(prop.Value);
                    if (string.IsNullOrWhiteSpace(propText))
                    {
                        continue;
                    }

                    lines.Add(propText.Contains('\n')
                        ? $"**{prop.Name}:**\n{propText}"
                        : $"**{prop.Name}:** {propText}");
                }

                return string.Join("\n\n", lines);
            }

            default:
                return value.ValueKind is System.Text.Json.JsonValueKind.Number
                    or System.Text.Json.JsonValueKind.True
                    or System.Text.Json.JsonValueKind.False
                    ? value.ToString()
                    : string.Empty;
        }
    }

    private static (string Title, string Description, string UserStory, GherkinScenario Scenario) ExtractStructuredDraft(string rawData)
    {
        var lines = rawData
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var enhancedTitle = ExtractPrefixedValue(lines, "Enhanced Title:");
        string title;
        if (!string.IsNullOrWhiteSpace(enhancedTitle))
        {
            title = enhancedTitle;
        }
        else
        {
            title = ExtractPrefixedLine(lines, "Discovery Note", "Title", "Subject", "Goal")
                ?? lines.FirstOrDefault()
                ?? "Backlog Requirement";

            // Clean up leading prefixes if present in title
            var dashIndex = title.IndexOf('-', StringComparison.Ordinal);
            if (title.StartsWith("Discovery Note", StringComparison.OrdinalIgnoreCase) && dashIndex > 0 && dashIndex < title.Length - 1)
            {
                title = title[(dashIndex + 1)..].Trim();
            }
            else
            {
                foreach (var prefix in new[] { "Title:", "Subject:", "Goal:" })
                {
                    if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var stripped = title[prefix.Length..].Trim();
                        if (!string.IsNullOrWhiteSpace(stripped))
                        {
                            title = stripped;
                        }

                        break;
                    }
                }
            }
        }

        var expandedDescriptionSection = ExtractSectionText(lines, "Expanded Description:");
        var contextSections = ExtractAllSectionTexts(lines, "Context:");
        var attachedFileSections = ExtractAttachedContextBlocks(lines);
        var webSection = ExtractSectionText(lines, "Web Research Findings:");
        var refinementSection = ExtractSectionText(lines, "Refinement Instructions:");

        string description;
        if (lines.Length == 1 && lines[0].StartsWith("Goal:", StringComparison.OrdinalIgnoreCase))
        {
            description = "Not specified in discovery notes.";
        }
        else if (!string.IsNullOrWhiteSpace(expandedDescriptionSection))
        {
            description = expandedDescriptionSection;
        }
        else if (contextSections.Count > 0 ||
                 attachedFileSections.Count > 0 ||
                 !string.IsNullOrWhiteSpace(webSection) ||
                 !string.IsNullOrWhiteSpace(refinementSection))
        {
            var segments = new List<string>();
            foreach (var attached in attachedFileSections)
            {
                if (!contextSections.Any(c => attached.Contains(c, StringComparison.OrdinalIgnoreCase)))
                {
                    segments.Add(attached);
                }
            }

            foreach (var ctx in contextSections)
            {
                segments.Add(ctx);
            }

            if (!string.IsNullOrWhiteSpace(webSection))
            {
                segments.Add($"Web Research Findings:\n{webSection}");
            }

            if (!string.IsNullOrWhiteSpace(refinementSection))
            {
                segments.Add($"Refinement Notes: {refinementSection}");
            }

            description = string.Join("\n\n", segments);
        }
        else
        {
            description = string.Join(" ", lines);
        }

        var userStory = ExtractPrefixedValue(lines, "Requirement:", "User Story:")
            ?? ExtractLineContaining(lines, "As a ")
            ?? ExtractLineContaining(lines, "As an ")
            ?? "Not specified in discovery notes.";

        var given = ExtractPrefixedValue(lines, "Precondition:", "Given:")
            ?? "Not specified in discovery notes.";
        var when = ExtractPrefixedValue(lines, "Action:", "When:")
            ?? "Not specified in discovery notes.";
        var then = ExtractPrefixedValue(lines, "Expected Outcome:", "Then:")
            ?? "Not specified in discovery notes.";

        return (title, description.Trim(), userStory, new GherkinScenario(given, when, then));
    }

    private static List<string> ExtractAllSectionTexts(string[] lines, string headerPrefix)
    {
        var results = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith(headerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var collected = new List<string>();
            var inline = lines[i][headerPrefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(inline))
            {
                collected.Add(inline);
            }

            for (var j = i + 1; j < lines.Length; j++)
            {
                if (IsKnownSectionHeader(lines[j]))
                {
                    break;
                }

                collected.Add(lines[j]);
                i = j;
            }

            if (collected.Count > 0)
            {
                results.Add(string.Join("\n", collected));
            }
        }

        return results;
    }

    private static List<string> ExtractAttachedContextBlocks(string[] lines)
    {
        var results = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("--- Context", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var collected = new List<string>();
            for (var j = i + 1; j < lines.Length; j++)
            {
                var line = lines[j];
                if (line.StartsWith("--- Context", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Context:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Web Research Findings:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Previous Draft:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Refinement Instructions:", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                collected.Add(line);
                i = j;
            }

            if (collected.Count > 0)
            {
                results.Add(string.Join("\n", collected));
            }
        }

        return results;
    }

    private static string? ExtractSectionText(string[] lines, string headerPrefix) =>
        ExtractAllSectionTexts(lines, headerPrefix).FirstOrDefault();

    private static bool IsKnownSectionHeader(string line) =>
        line.StartsWith("Goal:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Title:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Discovery Note", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Synthesized Goal Brief", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Enhanced Title:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("User Story:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Expanded Description:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Expanded Checklist:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Context:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("--- Context", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Requirement:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Precondition:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Given:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Action:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("When:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Expected Outcome:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Then:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Web Research Findings:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Previous Draft:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Refinement Instructions:", StringComparison.OrdinalIgnoreCase);

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
