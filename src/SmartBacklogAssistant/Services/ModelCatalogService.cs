using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

public sealed record ModelRecommendation(
    string ModelId,
    string DisplayName,
    string TierBadge,
    string Reason,
    bool IsRecommended = false);

public sealed record ModelSupportCheckResult(
    bool IsSupported,
    bool IsKnownWarning,
    string? Reason,
    string OfficialDocsUrl,
    IReadOnlyList<ModelRecommendation> Recommendations);

/// <summary>
/// Centralizes provider model recommendations, official documentation links,
/// and compatibility validation for unsupported, retired, wrong-modality,
/// or cross-provider model selections.
/// </summary>
public static class ModelCatalogService
{
    public const string GeminiModelsDocsUrl = "https://ai.google.dev/gemini-api/docs/models";
    public const string CopilotSupportedModelsDocsUrl = "https://docs.github.com/en/copilot/reference/ai-models/supported-models";
    public const string AzureOpenAiModelsDocsUrl = "https://learn.microsoft.com/azure/ai-services/openai/concepts/models";
    public const string AnthropicModelsDocsUrl = "https://docs.anthropic.com/en/docs/about-claude/models/overview";
    public const string ProjectProviderDocsUrl = "https://github.com/aleon1220/fde-sharp-backlog#multi-provider-ai-orchestration-gemini--microsoftcopilotsdk";

    private static readonly string[] NonTextModalityTokens =
    [
        "embedding",
        "imagen",
        "veo",
        "lyria",
        "whisper",
        "dall-e",
        "tts-",
        "moderation",
        "aqa"
    ];

    private static readonly HashSet<string> RetiredModelIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "gemini-1.0-pro",
        "gemini-1.0-pro-001",
        "gemini-pro",
        "gemini-pro-vision",
        "gemini-1.5-pro-001",
        "gemini-1.5-flash-001",
        "claude-2",
        "claude-2.0",
        "claude-2.1",
        "claude-instant",
        "claude-instant-1.2",
        "claude-3-sonnet-20240229",
        "claude-3-opus-20240229",
        "gpt-3.5-turbo-0301",
        "gpt-3.5-turbo-0613",
        "davinci",
        "davinci-002",
        "babbage-002",
        "curie",
        "ada",
        "code-davinci-002"
    };

    public static string GetOfficialDocsUrl(AiProviderType provider) => provider switch
    {
        AiProviderType.GoogleGemini => GeminiModelsDocsUrl,
        AiProviderType.GitHubCopilot => CopilotSupportedModelsDocsUrl,
        AiProviderType.AzureOpenAi => AzureOpenAiModelsDocsUrl,
        AiProviderType.AnthropicClaude => AnthropicModelsDocsUrl,
        _ => ProjectProviderDocsUrl
    };

    public static IReadOnlyList<ModelRecommendation> GetRecommendations(AiProviderType provider) => provider switch
    {
        AiProviderType.GoogleGemini =>
        [
            new("gemini-2.5-flash", "Gemini 2.5 Flash", "recommended", "Best balance of speed, cost, and structured JSON output", IsRecommended: true),
            new("gemini-2.5-pro", "Gemini 2.5 Pro", "recommended", "Strongest reasoning for complex specs and multi-file notes", IsRecommended: true),
            new("gemini-2.5-flash-lite", "Gemini 2.5 Flash-Lite", "fast", "Lowest latency and high throughput for quick backlog drafts"),
            new("gemini-3.8-flash", "Gemini 3.8 Flash", "default", "Configured default flash target when enabled on your API key")
        ],
        AiProviderType.GitHubCopilot =>
        [
            new("gpt-5-mini", "GPT-5 mini", "recommended", "Default fast Copilot model with reliable structured JSON output", IsRecommended: true),
            new("gpt-4.1", "GPT-4.1", "recommended", "General-purpose reliable coding and backlog decomposition", IsRecommended: true),
            new("claude-sonnet-4.5", "Claude Sonnet 4.5", "reasoning", "Strong INVEST user story framing and nuanced requirements"),
            new("gpt-5", "GPT-5", "pro", "Deep analysis for large technical specifications")
        ],
        AiProviderType.AzureOpenAi =>
        [
            new("gpt-4o", "GPT-4o (Deployment)", "recommended", "Broad Azure region availability and dependable JSON schema output", IsRecommended: true),
            new("gpt-4o-mini", "GPT-4o mini (Deployment)", "recommended", "Fast, low-cost deployment for routine backlog items", IsRecommended: true),
            new("gpt-4.1", "GPT-4.1 (Deployment)", "reasoning", "High-precision extraction and engineering checklists")
        ],
        AiProviderType.AnthropicClaude =>
        [
            new("claude-sonnet-4.5", "Claude Sonnet 4.5", "recommended", "Best balance of speed, INVEST framing, and structured JSON", IsRecommended: true),
            new("claude-haiku-4.5", "Claude Haiku 4.5", "recommended", "Fastest Claude response with lowest token cost", IsRecommended: true),
            new("claude-opus-4.1", "Claude Opus 4.1", "pro", "Deep architectural reasoning for complex discovery transcripts")
        ],
        _ => []
    };

    public static string GetProviderRecommendationSummary(AiProviderType provider)
    {
        var recs = GetRecommendations(provider);
        if (recs.Count == 0)
        {
            return "Manual mode renders Markdown prompts locally; switch to 'gemini' or 'copilot-sdk' for live AI drafting.";
        }

        var top = recs.Where(r => r.IsRecommended).ToArray();
        var picks = top.Length > 0 ? top : [recs[0]];
        return $"Recommended: {string.Join(", ", picks.Select(p => $"{p.ModelId} ({p.Reason})"))}";
    }

    public static ModelSupportCheckResult ValidateModelSelection(
        AiProviderType provider,
        string? rawModelId,
        IReadOnlyList<string>? liveAvailableModelIds = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? unsupportedCapabilityHints = null)
    {
        var docsUrl = GetOfficialDocsUrl(provider);
        var recommendations = GetRecommendations(provider);

        if (provider == AiProviderType.ManualPrompt)
        {
            return new ModelSupportCheckResult(
                IsSupported: false,
                IsKnownWarning: false,
                Reason: "Manual Prompt mode renders Markdown prompts for copy/paste and does not invoke an AI model API directly. Switch to 'gemini', 'copilot-sdk', 'azure', or 'claude' to select a model.",
                OfficialDocsUrl: docsUrl,
                Recommendations: GetRecommendations(AiProviderType.GoogleGemini));
        }

        if (string.IsNullOrWhiteSpace(rawModelId) ||
            rawModelId.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            rawModelId.Trim().Equals("automatic", StringComparison.OrdinalIgnoreCase) ||
            rawModelId.Trim().Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return new ModelSupportCheckResult(
                IsSupported: true,
                IsKnownWarning: false,
                Reason: null,
                OfficialDocsUrl: docsUrl,
                Recommendations: recommendations);
        }

        var normalized = rawModelId.Trim();
        if (provider == AiProviderType.GoogleGemini &&
            normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["models/".Length..].Trim();
        }

        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Contains('/') ||
            normalized.Contains('?') ||
            normalized.Contains('#') ||
            normalized.Any(char.IsWhiteSpace))
        {
            return new ModelSupportCheckResult(
                IsSupported: false,
                IsKnownWarning: false,
                Reason: $"Model ID '{rawModelId.Trim()}' is malformed. Enter a plain model identifier (no spaces, URLs, or extra path segments).",
                OfficialDocsUrl: docsUrl,
                Recommendations: recommendations);
        }

        if (unsupportedCapabilityHints is not null &&
            TryFindIgnoreCase(unsupportedCapabilityHints, normalized, out var actualMethods))
        {
            var methodsDisplay = actualMethods.Count > 0 ? string.Join(", ", actualMethods) : "none";
            return new ModelSupportCheckResult(
                IsSupported: false,
                IsKnownWarning: false,
                Reason: $"Model '{normalized}' only supports [{methodsDisplay}], not text/JSON 'generateContent' required for backlog extraction.",
                OfficialDocsUrl: docsUrl,
                Recommendations: recommendations);
        }

        if (NonTextModalityTokens.Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return new ModelSupportCheckResult(
                IsSupported: false,
                IsKnownWarning: false,
                Reason: $"Model '{normalized}' targets an unsupported modality (embeddings, image, audio, or moderation) rather than text/chat JSON generation.",
                OfficialDocsUrl: docsUrl,
                Recommendations: recommendations);
        }

        if (RetiredModelIds.Contains(normalized))
        {
            return new ModelSupportCheckResult(
                IsSupported: false,
                IsKnownWarning: false,
                Reason: $"Model '{normalized}' is deprecated or retired by {RuntimeEnvironmentService.FormatProviderCode(provider)} and is no longer available for new requests.",
                OfficialDocsUrl: docsUrl,
                Recommendations: recommendations);
        }

        var crossProviderError = CheckCrossProviderMismatch(provider, normalized, liveAvailableModelIds);
        if (crossProviderError is not null)
        {
            return new ModelSupportCheckResult(
                IsSupported: false,
                IsKnownWarning: false,
                Reason: crossProviderError,
                OfficialDocsUrl: docsUrl,
                Recommendations: recommendations);
        }

        if (liveAvailableModelIds is { Count: > 0 })
        {
            var matchedLive = liveAvailableModelIds.Any(id => string.Equals(id, normalized, StringComparison.OrdinalIgnoreCase));
            if (matchedLive)
            {
                return new ModelSupportCheckResult(
                    IsSupported: true,
                    IsKnownWarning: false,
                    Reason: null,
                    OfficialDocsUrl: docsUrl,
                    Recommendations: recommendations);
            }

            return new ModelSupportCheckResult(
                IsSupported: false,
                IsKnownWarning: false,
                Reason: $"Model '{normalized}' is not enabled or available for your '{RuntimeEnvironmentService.FormatProviderCode(provider)}' account, plan/tier, organization policy, or region.",
                OfficialDocsUrl: docsUrl,
                Recommendations: recommendations);
        }

        var isKnownCurated = recommendations.Any(r => string.Equals(r.ModelId, normalized, StringComparison.OrdinalIgnoreCase));
        if (isKnownCurated)
        {
            return new ModelSupportCheckResult(
                IsSupported: true,
                IsKnownWarning: false,
                Reason: null,
                OfficialDocsUrl: docsUrl,
                Recommendations: recommendations);
        }

        return new ModelSupportCheckResult(
            IsSupported: true,
            IsKnownWarning: true,
            Reason: $"Model '{normalized}' could not be verified offline against live '{RuntimeEnvironmentService.FormatProviderCode(provider)}' models. Unprovisioned deployments, preview models, or plan-restricted IDs may fail at runtime.",
            OfficialDocsUrl: docsUrl,
            Recommendations: recommendations);
    }

    private static string? CheckCrossProviderMismatch(
        AiProviderType provider,
        string modelId,
        IReadOnlyList<string>? liveAvailableModelIds)
    {
        var lower = modelId.ToLowerInvariant();
        var isGeminiId = lower.StartsWith("gemini-", StringComparison.Ordinal);
        var isClaudeId = lower.StartsWith("claude-", StringComparison.Ordinal);
        var isOpenAiId = lower.StartsWith("gpt-", StringComparison.Ordinal) ||
                         lower.StartsWith("o1", StringComparison.Ordinal) ||
                         lower.StartsWith("o3", StringComparison.Ordinal) ||
                         lower.StartsWith("o4", StringComparison.Ordinal);

        return provider switch
        {
            AiProviderType.GoogleGemini when isClaudeId || isOpenAiId =>
                $"Model '{modelId}' belongs to OpenAI/Anthropic and is not served by the Google Gemini API.",
            AiProviderType.AnthropicClaude when isGeminiId || isOpenAiId =>
                $"Model '{modelId}' belongs to Google/OpenAI and is not served by the Anthropic Claude API.",
            AiProviderType.AzureOpenAi when isGeminiId || isClaudeId =>
                $"Model '{modelId}' belongs to Google/Anthropic rather than an Azure OpenAI / Foundry deployment.",
            AiProviderType.GitHubCopilot when liveAvailableModelIds is null or { Count: 0 } && isGeminiId &&
                !lower.Contains("pro", StringComparison.Ordinal) && !lower.Contains("flash", StringComparison.Ordinal) =>
                $"Model '{modelId}' may not be enabled in GitHub Copilot without BYOK or an eligible Copilot plan.",
            _ => null
        };
    }

    private static bool TryFindIgnoreCase(
        IReadOnlyDictionary<string, IReadOnlyList<string>> dictionary,
        string key,
        out IReadOnlyList<string> value)
    {
        if (dictionary.TryGetValue(key, out var direct))
        {
            value = direct;
            return true;
        }

        foreach (var entry in dictionary)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = [];
        return false;
    }
}
