using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

public sealed record MultiProviderGenerationRequest(
    AiProviderType Provider,
    string RenderedPrompt,
    BacklogType BacklogType,
    string? ApiKey = null,
    string? ModelName = null,
    bool LiveEnabled = true,
    string? WorkingDirectory = null);

public sealed record MultiProviderGenerationResult(
    AiProviderType ProviderUsed,
    string? GeneratedContent,
    string? ResolvedModel,
    string? ResolvedApiKey,
    bool UsedLiveProvider);

/// <summary>
/// Coordinates multi-provider AI execution across Google Gemini, GitHub Copilot SDK
/// (native Copilot CLI runtime and BYOK providers), Azure OpenAI, Anthropic Claude,
/// and Manual Prompt fallback while preserving zero-regression deterministic behavior.
/// </summary>
public sealed class AiProviderOrchestrator
{
    private readonly GeminiApiClient _geminiApiClient;
    private readonly ICopilotSdkGateway _copilotGateway;
    private readonly Func<string, string?> _environmentReader;

    public AiProviderOrchestrator(
        GeminiApiClient? geminiApiClient = null,
        ICopilotSdkGateway? copilotGateway = null,
        Func<string, string?>? environmentReader = null)
    {
        _geminiApiClient = geminiApiClient ?? new GeminiApiClient();
        _copilotGateway = copilotGateway ?? new GitHubCopilotSdkGateway();
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
    }

    public ICopilotSdkGateway CopilotGateway => _copilotGateway;

    public static string BuildStructuredJsonPrompt(string renderedPrompt, BacklogType backlogType)
    {
        var responseContract = backlogType == BacklogType.Type02
            ? "Return only a JSON object with string properties `title` and `description` (Objective, Context/Key Details, and Verification Checklist; keep `description` simple, max 57 words, and use `* ` markdown bullets for suggested actions), plus optional `alternatives` array of 3 `{angle, title, description}` objects. Do not include a user story or acceptance criteria."
            : "Return only a JSON object with string properties `title`, `description` (keep simple, max 57 words, and use `* ` markdown bullets for suggested actions), `userStory`, a `scenarios` array containing `given`, `when`, and `then`, plus optional `alternatives` array of 3 `{angle, title, description}` objects.";

        return $"{renderedPrompt.Trim()}\n\n{responseContract} Do not include Markdown fences or commentary.";
    }

    public static string BuildZeroHallucinationSystemPrompt(BacklogType backlogType)
    {
        var scopeRule = backlogType == BacklogType.Type02
            ? "Produce a Type 02 Schedule Item containing only `title` and `description` (plus `alternatives` when drafting from a Goal)."
            : "Produce a Type 01 Complete User Story containing `title`, `description`, `userStory`, and Given/When/Then `scenarios` (plus `alternatives` when drafting from a Goal).";

        return
            "You are a Senior Technical Project Manager and Forward Deployed Engineer. " +
            "Zero Hallucination rule: never invent internal owners, sprint dates, ticket IDs, or unstated internal commitments. " +
            "For detailed raw discovery notes, mark absent internal facts as 'Not specified in discovery notes.'. " +
            "For brief Goal/Title inputs without detailed notes, allow constructive creativity: do NOT write 'Not specified in discovery notes' boilerplate; apply User Story creation principles to enhance the title, keep `description` simple and strictly max 57 words (using `* ` markdown bullet points for suggested actions), incorporate any Context, Web Research Findings, Synthesized Goal Brief, or Refinement Instructions, and suggest 3 concise Title + Description alternatives. " +
            scopeRule;
    }

    public static string BuildGoalExpansionJsonPrompt(string renderedPrompt) =>
        $"{renderedPrompt.Trim()}\n\nReturn only a valid JSON object with properties `enhancedTitle`, `userStory`, `discoverySummary` (simple, max 57 words, using `* ` markdown bullets for suggested actions), `keyChecklist` (array of concise strings), and `alternatives` (array of 3 objects with `angle`, `title`, and `description`). Do not include Markdown fences or commentary.";

    public static string BuildGoalExpansionSystemPrompt() =>
        "You are a Senior Product Manager, Agile Coach, and Forward Deployed Engineer. " +
        "When given a brief Goal without detailed discovery notes, focus on simplicity and apply User Story creation principles (INVEST: clear Actor, Capability, Business Value, and action-oriented Title) to synthesize concise discovery context (max 57 words with `* ` bullet points for suggested actions) and 3 simple Title + Description alternatives. Never invent fake employee names or claim private sprint commitments already occurred.";

    public MultiProviderGenerationResult GenerateGoalExpansion(MultiProviderGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.LiveEnabled)
        {
            return new MultiProviderGenerationResult(request.Provider, null, request.ModelName, request.ApiKey, false);
        }

        return ExecuteProviderCall(
            request,
            promptText: BuildGoalExpansionJsonPrompt(request.RenderedPrompt),
            systemPrompt: BuildGoalExpansionSystemPrompt());
    }

    public MultiProviderGenerationResult GenerateDraft(MultiProviderGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ExecuteProviderCall(
            request,
            promptText: BuildStructuredJsonPrompt(request.RenderedPrompt, request.BacklogType),
            systemPrompt: BuildZeroHallucinationSystemPrompt(request.BacklogType));
    }

    private MultiProviderGenerationResult ExecuteProviderCall(
        MultiProviderGenerationRequest request,
        string promptText,
        string systemPrompt)
    {
        switch (request.Provider)
        {
            case AiProviderType.GitHubCopilot:
            {
                if (!request.LiveEnabled)
                {
                    return new MultiProviderGenerationResult(
                        ProviderUsed: AiProviderType.GitHubCopilot,
                        GeneratedContent: null,
                        ResolvedModel: request.ModelName ?? RuntimeEnvironmentService.DefaultCopilotModel,
                        ResolvedApiKey: request.ApiKey,
                        UsedLiveProvider: false);
                }

                var modelId = string.IsNullOrWhiteSpace(request.ModelName)
                    ? _environmentReader(RuntimeEnvironmentService.CopilotModelEnvVar)?.Trim()
                      ?? RuntimeEnvironmentService.DefaultCopilotModel
                    : request.ModelName.Trim();

                var content = _copilotGateway.GenerateContentAsync(
                    new CopilotGenerationRequest(
                        Prompt: promptText,
                        SystemPrompt: systemPrompt,
                        ModelId: modelId,
                        GitHubToken: request.ApiKey,
                        WorkingDirectory: request.WorkingDirectory))
                    .GetAwaiter()
                    .GetResult();

                return new MultiProviderGenerationResult(
                    ProviderUsed: AiProviderType.GitHubCopilot,
                    GeneratedContent: content,
                    ResolvedModel: modelId,
                    ResolvedApiKey: request.ApiKey,
                    UsedLiveProvider: true);
            }

            case AiProviderType.AzureOpenAi:
            {
                var azureEndpoint = _environmentReader("AZURE_OPENAI_ENDPOINT")?.Trim();
                var azureKey = !string.IsNullOrWhiteSpace(request.ApiKey)
                    ? request.ApiKey.Trim()
                    : _environmentReader("AZURE_OPENAI_API_KEY")?.Trim();
                var copilotDiag = _copilotGateway.InspectDiagnostics();

                if (request.LiveEnabled &&
                    !string.IsNullOrWhiteSpace(azureEndpoint) &&
                    !string.IsNullOrWhiteSpace(azureKey) &&
                    copilotDiag.RuntimeAvailable)
                {
                    var isOpenAiV1 = azureEndpoint.TrimEnd('/').EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase);
                    var modelId = string.IsNullOrWhiteSpace(request.ModelName)
                        ? _environmentReader("AZURE_OPENAI_DEPLOYMENT")?.Trim() ?? "gpt-4o"
                        : request.ModelName.Trim();

                    var content = _copilotGateway.GenerateContentAsync(
                        new CopilotGenerationRequest(
                            Prompt: promptText,
                            SystemPrompt: systemPrompt,
                            ModelId: modelId,
                            WorkingDirectory: request.WorkingDirectory,
                            Byok: new CopilotByokOptions(
                                Type: isOpenAiV1 ? "openai" : "azure",
                                BaseUrl: azureEndpoint,
                                ApiKey: azureKey,
                                WireApi: isOpenAiV1 ? "responses" : null,
                                AzureApiVersion: _environmentReader("AZURE_OPENAI_API_VERSION")?.Trim() ?? (isOpenAiV1 ? null : "2024-10-21"))))
                        .GetAwaiter()
                        .GetResult();

                    return new MultiProviderGenerationResult(
                        ProviderUsed: AiProviderType.AzureOpenAi,
                        GeneratedContent: content,
                        ResolvedModel: modelId,
                        ResolvedApiKey: azureKey,
                        UsedLiveProvider: true);
                }

                // Preserve mocked / deterministic fallback when Azure endpoint or runtime is not configured
                return new MultiProviderGenerationResult(
                    ProviderUsed: AiProviderType.AzureOpenAi,
                    GeneratedContent: null,
                    ResolvedModel: request.ModelName ?? "gpt-4o",
                    ResolvedApiKey: azureKey,
                    UsedLiveProvider: false);
            }

            case AiProviderType.AnthropicClaude:
            {
                var anthropicKey = !string.IsNullOrWhiteSpace(request.ApiKey)
                    ? request.ApiKey.Trim()
                    : _environmentReader("ANTHROPIC_API_KEY")?.Trim();
                var copilotDiag = _copilotGateway.InspectDiagnostics();

                if (request.LiveEnabled &&
                    !string.IsNullOrWhiteSpace(_environmentReader("ANTHROPIC_API_KEY")) &&
                    !string.IsNullOrWhiteSpace(anthropicKey) &&
                    copilotDiag.RuntimeAvailable)
                {
                    var modelId = string.IsNullOrWhiteSpace(request.ModelName)
                        ? "claude-sonnet-4.5"
                        : request.ModelName.Trim();

                    var content = _copilotGateway.GenerateContentAsync(
                        new CopilotGenerationRequest(
                            Prompt: promptText,
                            SystemPrompt: systemPrompt,
                            ModelId: modelId,
                            WorkingDirectory: request.WorkingDirectory,
                            Byok: new CopilotByokOptions(
                                Type: "anthropic",
                                BaseUrl: _environmentReader("ANTHROPIC_BASE_URL")?.Trim() ?? "https://api.anthropic.com",
                                ApiKey: anthropicKey)))
                        .GetAwaiter()
                        .GetResult();

                    return new MultiProviderGenerationResult(
                        ProviderUsed: AiProviderType.AnthropicClaude,
                        GeneratedContent: content,
                        ResolvedModel: modelId,
                        ResolvedApiKey: anthropicKey,
                        UsedLiveProvider: true);
                }

                return new MultiProviderGenerationResult(
                    ProviderUsed: AiProviderType.AnthropicClaude,
                    GeneratedContent: null,
                    ResolvedModel: request.ModelName ?? "claude-sonnet-4.5",
                    ResolvedApiKey: anthropicKey,
                    UsedLiveProvider: false);
            }

            case AiProviderType.GoogleGemini:
            {
                if (!request.LiveEnabled)
                {
                    return new MultiProviderGenerationResult(
                        ProviderUsed: AiProviderType.GoogleGemini,
                        GeneratedContent: null,
                        ResolvedModel: request.ModelName,
                        ResolvedApiKey: request.ApiKey,
                        UsedLiveProvider: false);
                }

                var modelId = string.IsNullOrWhiteSpace(request.ModelName)
                    ? RuntimeEnvironmentService.DefaultGeminiModel
                    : request.ModelName.Trim();
                var content = _geminiApiClient.GenerateContent(
                    request.ApiKey!,
                    modelId,
                    promptText);

                return new MultiProviderGenerationResult(
                    ProviderUsed: AiProviderType.GoogleGemini,
                    GeneratedContent: content,
                    ResolvedModel: modelId,
                    ResolvedApiKey: request.ApiKey,
                    UsedLiveProvider: true);
            }

            default:
                return new MultiProviderGenerationResult(
                    ProviderUsed: AiProviderType.ManualPrompt,
                    GeneratedContent: null,
                    ResolvedModel: request.ModelName,
                    ResolvedApiKey: request.ApiKey,
                    UsedLiveProvider: false);
        }
    }

    public IReadOnlyList<ProviderAvailability> InspectProviderMatrix(
        GeminiSecretResolution geminiSecret,
        AiProviderType activeProvider,
        bool liveEnabled)
    {
        ArgumentNullException.ThrowIfNull(geminiSecret);

        var copilotDiag = _copilotGateway.InspectDiagnostics();
        var azureEndpointConfigured = !string.IsNullOrWhiteSpace(_environmentReader("AZURE_OPENAI_ENDPOINT"));
        var azureKeyConfigured = !string.IsNullOrWhiteSpace(_environmentReader("AZURE_OPENAI_API_KEY"));
        var anthropicKeyConfigured = !string.IsNullOrWhiteSpace(_environmentReader("ANTHROPIC_API_KEY"));

        return
        [
            new ProviderAvailability(
                Provider: AiProviderType.GoogleGemini,
                Code: "gemini",
                DisplayName: "Google Gemini REST",
                IsReady: geminiSecret.IsConfigured || !liveEnabled,
                Mode: liveEnabled ? (geminiSecret.IsConfigured ? "Live API" : "Needs Key") : "Offline",
                Detail: geminiSecret.IsConfigured
                    ? $"{geminiSecret.SourceDetail} [{geminiSecret.MaskedKey}]"
                    : $"Set GEMINI_API_KEY (or pass --offline)"),

            new ProviderAvailability(
                Provider: AiProviderType.GitHubCopilot,
                Code: "copilot-sdk",
                DisplayName: "GitHub Copilot SDK",
                IsReady: copilotDiag.RuntimeAvailable,
                Mode: copilotDiag.RuntimeAvailable
                    ? (copilotDiag.ByokConfigured ? "SDK + BYOK" : "SDK Ready")
                    : "CLI Missing",
                Detail: copilotDiag.Summary),

            new ProviderAvailability(
                Provider: AiProviderType.AzureOpenAi,
                Code: "azure",
                DisplayName: "Azure OpenAI / Foundry",
                IsReady: azureEndpointConfigured && azureKeyConfigured && copilotDiag.RuntimeAvailable,
                Mode: azureEndpointConfigured && azureKeyConfigured && copilotDiag.RuntimeAvailable ? "BYOK Live" : "Mock Fallback",
                Detail: azureEndpointConfigured
                    ? "AZURE_OPENAI_ENDPOINT configured"
                    : "Mocked unless AZURE_OPENAI_ENDPOINT + Copilot CLI set"),

            new ProviderAvailability(
                Provider: AiProviderType.AnthropicClaude,
                Code: "claude",
                DisplayName: "Anthropic Claude",
                IsReady: anthropicKeyConfigured && copilotDiag.RuntimeAvailable,
                Mode: anthropicKeyConfigured && copilotDiag.RuntimeAvailable ? "BYOK Live" : "Mock Fallback",
                Detail: anthropicKeyConfigured
                    ? "ANTHROPIC_API_KEY configured"
                    : "Mocked unless ANTHROPIC_API_KEY + Copilot CLI set"),

            new ProviderAvailability(
                Provider: AiProviderType.ManualPrompt,
                Code: "manual",
                DisplayName: "Manual Prompt (Markdown)",
                IsReady: true,
                Mode: activeProvider == AiProviderType.ManualPrompt ? "Active" : "Always Ready",
                Detail: "Renders prompt Markdown + deterministic baseline draft")
        ];
    }
}
