using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class ModelCatalogServiceTests
{
    [Theory]
    [InlineData(AiProviderType.GoogleGemini, "https://ai.google.dev/gemini-api/docs/models")]
    [InlineData(AiProviderType.GitHubCopilot, "https://docs.github.com/en/copilot/reference/ai-models/supported-models")]
    [InlineData(AiProviderType.AzureOpenAi, "https://learn.microsoft.com/azure/ai-services/openai/concepts/models")]
    [InlineData(AiProviderType.AnthropicClaude, "https://docs.anthropic.com/en/docs/about-claude/models/overview")]
    public void GetOfficialDocsUrl_AndGetRecommendations_ReturnCuratedGuidancePerProvider(
        AiProviderType provider,
        string expectedDocsUrl)
    {
        Assert.Equal(expectedDocsUrl, ModelCatalogService.GetOfficialDocsUrl(provider));
        var recommendations = ModelCatalogService.GetRecommendations(provider);
        Assert.NotEmpty(recommendations);
        Assert.Contains(recommendations, r => r.IsRecommended);
    }

    [Theory]
    [InlineData(AiProviderType.GoogleGemini, "text-embedding-004", "unsupported modality")]
    [InlineData(AiProviderType.GoogleGemini, "imagen-3.0-generate-001", "unsupported modality")]
    [InlineData(AiProviderType.GoogleGemini, "gemini-1.0-pro", "deprecated or retired")]
    [InlineData(AiProviderType.GoogleGemini, "gpt-4o", "OpenAI/Anthropic")]
    [InlineData(AiProviderType.AnthropicClaude, "claude-2.1", "deprecated or retired")]
    [InlineData(AiProviderType.AnthropicClaude, "gemini-2.5-flash", "Google/OpenAI")]
    [InlineData(AiProviderType.AzureOpenAi, "dall-e-3", "unsupported modality")]
    [InlineData(AiProviderType.AzureOpenAi, "claude-sonnet-4.5", "Google/Anthropic")]
    [InlineData(AiProviderType.GoogleGemini, "models/invalid/path", "malformed")]
    [InlineData(AiProviderType.ManualPrompt, "gemini-2.5-flash", "Manual Prompt mode")]
    public void ValidateModelSelection_RejectsUnsupportedModelsWithReasonOfficialDocsAndRecommendations(
        AiProviderType provider,
        string modelId,
        string expectedReasonSnippet)
    {
        var result = ModelCatalogService.ValidateModelSelection(provider, modelId);

        Assert.False(result.IsSupported);
        Assert.Contains(expectedReasonSnippet, result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ModelCatalogService.GetOfficialDocsUrl(provider), result.OfficialDocsUrl);
        Assert.NotEmpty(result.Recommendations);
    }

    [Fact]
    public void ValidateModelSelection_WithLiveCatalogAndUnsupportedMethodHint_ExplainsActualCapabilitiesAndDocs()
    {
        var liveIds = new[] { "gemini-2.5-flash", "gemini-2.5-pro" };
        var unsupportedHints = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini-embedding-001"] = ["embedContent"]
        };

        var embedCheck = ModelCatalogService.ValidateModelSelection(
            AiProviderType.GoogleGemini,
            "gemini-embedding-001",
            liveIds,
            unsupportedHints);
        Assert.False(embedCheck.IsSupported);
        Assert.Contains("embedContent", embedCheck.Reason);
        Assert.Equal(ModelCatalogService.GeminiModelsDocsUrl, embedCheck.OfficialDocsUrl);

        var unavailableCheck = ModelCatalogService.ValidateModelSelection(
            AiProviderType.GitHubCopilot,
            "gpt-custom-locked",
            ["gpt-5-mini", "gpt-4.1"]);
        Assert.False(unavailableCheck.IsSupported);
        Assert.Contains("account, plan/tier, organization policy, or region", unavailableCheck.Reason);
        Assert.Equal(ModelCatalogService.CopilotSupportedModelsDocsUrl, unavailableCheck.OfficialDocsUrl);

        var supportedCheck = ModelCatalogService.ValidateModelSelection(
            AiProviderType.GoogleGemini,
            "gemini-2.5-flash",
            liveIds,
            unsupportedHints);
        Assert.True(supportedCheck.IsSupported);
        Assert.False(supportedCheck.IsKnownWarning);
    }

    [Fact]
    public void ValidateModelSelection_WhenLiveCatalogIsEmpty_FallsBackToCuratedRecommendations()
    {
        var result = ModelCatalogService.ValidateModelSelection(
            AiProviderType.GitHubCopilot,
            "gpt-5-mini",
            Array.Empty<string>());

        Assert.True(result.IsSupported);
        Assert.False(result.IsKnownWarning);
    }

    [Fact]
    public void TerminalUx_ResolveActionableHint_PointsUnsupportedModelErrorsToOfficialDocs()
    {
        var hint = TerminalUx.ResolveActionableHint(
            new InvalidOperationException("GitHub Copilot SDK model 'o9-secret' is not supported."));

        Assert.NotNull(hint);
        Assert.Contains(ModelCatalogService.CopilotSupportedModelsDocsUrl, hint);
    }

    [Fact]
    public void OpenAiGateway_CatalogAndValidation_SupportVendorPrefixedModelsAndActionableHints()
    {
        var docs = ModelCatalogService.GetOfficialDocsUrl(AiProviderType.OpenAiGateway);
        Assert.Equal(ModelCatalogService.OpenAiModelsDocsUrl, docs);

        var recs = ModelCatalogService.GetRecommendations(AiProviderType.OpenAiGateway);
        Assert.Contains(recs, r => r.ModelId == "gpt-4o" && r.IsRecommended);
        Assert.Contains(recs, r => r.ModelId == "gpt-4.1-mini" && r.IsRecommended);

        // Single vendor/model slug allowed on OpenAI-compatible gateways (OpenRouter/LiteLLM)
        var gatewaySlug = ModelCatalogService.ValidateModelSelection(
            AiProviderType.OpenAiGateway,
            "openai/gpt-4o",
            Array.Empty<string>());
        Assert.True(gatewaySlug.IsSupported);

        // Multi-segment slash slug still rejected even on OpenAiGateway
        var badSlug = ModelCatalogService.ValidateModelSelection(
            AiProviderType.OpenAiGateway,
            "org/vendor/gpt-4o",
            Array.Empty<string>());
        Assert.False(badSlug.IsSupported);

        // Vendor slug rejected on Gemini (only single bare ID or models/ prefix allowed)
        var geminiSlug = ModelCatalogService.ValidateModelSelection(
            AiProviderType.GoogleGemini,
            "openai/gpt-4o",
            Array.Empty<string>());
        Assert.False(geminiSlug.IsSupported);

        var wireHint = TerminalUx.ResolveActionableHint(
            new InvalidOperationException("Invalid wire API 'xml' for '--wire-api'."));
        Assert.NotNull(wireHint);
        Assert.Contains("--wire-api", wireHint);

        var urlHint = TerminalUx.ResolveActionableHint(
            new InvalidOperationException("Invalid Gateway URL 'not-a-url'; check OPENAI_BASE_URL."));
        Assert.NotNull(urlHint);
        Assert.Contains("OPENAI_BASE_URL", urlHint);

        var keyHint = TerminalUx.ResolveActionableHint(
            new InvalidOperationException("OpenAI Gateway API key is missing (401 Unauthorized)."));
        Assert.NotNull(keyHint);
        Assert.Contains("OPENAI_API_KEY", keyHint);

        var modelHint = TerminalUx.ResolveActionableHint(
            new InvalidOperationException("OpenAI Gateway model 'o9-secret' is not supported."));
        Assert.NotNull(modelHint);
        Assert.Contains(ModelCatalogService.OpenAiModelsDocsUrl, modelHint);
    }
}
