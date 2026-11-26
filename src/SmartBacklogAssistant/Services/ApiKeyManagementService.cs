using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

public sealed record ProviderKeyMetadata(
    AiProviderType Provider,
    string Code,
    string DisplayName,
    string PrimaryEnvVar,
    IReadOnlyList<string> AliasEnvVars,
    string KeyPortalUrl,
    string ExpectedFormatHint);

public sealed record ApiKeyFormatValidationResult(
    bool IsValid,
    AiProviderType Provider,
    string NormalizedKey,
    string MaskedKey,
    bool IsPlaceholder,
    bool IsCrossProviderMismatch,
    AiProviderType? DetectedMismatchedProvider,
    string? ErrorMessage,
    string? ActionableHint);

public sealed record ApiKeyLiveVerificationResult(
    bool Succeeded,
    AiProviderType Provider,
    string MaskedKey,
    string EndpointChecked,
    int? HttpStatusCode,
    int? AccessibleModelCount,
    string Summary,
    string? ErrorMessage,
    string? ActionableHint);

public sealed record ProviderKeyStatus(
    ProviderKeyMetadata Metadata,
    bool IsConfigured,
    string MaskedKey,
    string? ActiveEnvVar,
    SecretSource Source,
    string ScopeSummary,
    string Detail,
    bool HasCompanionConfigWarning = false,
    string? CompanionConfigNote = null);

public sealed record ApiKeyMutationResult(
    bool Succeeded,
    AiProviderType Provider,
    string PrimaryEnvVar,
    string MaskedKey,
    ApiKeyStorageScope AppliedScope,
    bool PersistedToOsUserEnvironment,
    string Message,
    string? ScopeGuidanceNote = null,
    ApiKeyFormatValidationResult? FormatValidation = null,
    ApiKeyLiveVerificationResult? LiveVerification = null);

public sealed record AnthropicModelDescriptor(
    string ModelId,
    string DisplayName);

public sealed class ApiKeyValidationException : InvalidOperationException
{
    public ApiKeyValidationException(string message, string? actionableHint = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ActionableHint = actionableHint;
    }

    public string? ActionableHint { get; }
}

/// <summary>
/// Manages multi-provider API key discovery, offline syntax &amp; cross-provider mismatch validation,
/// optional live endpoint authentication verification, session/OS-env scope injection, and clearing
/// without ever persisting plaintext secrets to <c>settings.json</c> or <c>stderr</c>.
/// </summary>
public sealed partial class ApiKeyManagementService
{
    public const string OpenAiApiKeysUrl = "https://platform.openai.com/api-keys";
    public const string AzureAiPortalUrl = "https://ai.azure.com";
    public const string AnthropicApiKeysUrl = "https://console.anthropic.com/settings/keys";
    public const string GitHubTokensUrl = "https://github.com/settings/tokens";
    public const string DefaultAnthropicBaseUrl = "https://api.anthropic.com";
    public const string AnthropicBaseUrlEnvVar = "ANTHROPIC_BASE_URL";
    public const string AnthropicApiVersion = "2023-06-01";
    public const string GitHubUserApiUrl = "https://api.github.com/user";

    private static readonly IReadOnlyList<AiProviderType> KeyManagedProviders =
    [
        AiProviderType.GoogleGemini,
        AiProviderType.OpenAiGateway,
        AiProviderType.AzureOpenAi,
        AiProviderType.AnthropicClaude,
        AiProviderType.GitHubCopilot
    ];

    private static readonly string[] PlaceholderTokens =
    [
        "your-api-key",
        "your_api_key",
        "your-key",
        "your_key",
        "your-gemini-key",
        "your-openai-key",
        "your-anthropic-key",
        "your-azure-key",
        "your-github-token",
        "api-key-here",
        "api_key_here",
        "key-here",
        "insert-key",
        "enter-key",
        "replace-me",
        "replace_me",
        "changeme",
        "change-me",
        "placeholder",
        "dummy-key",
        "dummy_key",
        "fake-key",
        "fake_key",
        "sample-key",
        "sample_key",
        "example-key",
        "example_key",
        "my-api-key",
        "secret-key-here",
        "redacted"
    ];

    private static readonly HashSet<string> ExactPlaceholderValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "none",
        "null",
        "undefined",
        "n/a",
        "na",
        "todo",
        "tbd",
        "dummy",
        "fake",
        "sample",
        "example",
        "apikey",
        "api-key",
        "api_key",
        "secret",
        "token"
    };

    private readonly SessionEnvironmentStore _environmentStore;
    private readonly GeminiApiClient _geminiApiClient;
    private readonly OpenAiGatewayClient _openAiGatewayClient;
    private readonly ICopilotSdkGateway _copilotGateway;
    private readonly HttpClient _verificationHttpClient;
    private readonly SecretRedactor? _secretRedactor;
    private readonly ILogger<ApiKeyManagementService> _logger;

    public ApiKeyManagementService(
        SessionEnvironmentStore? environmentStore = null,
        GeminiApiClient? geminiApiClient = null,
        OpenAiGatewayClient? openAiGatewayClient = null,
        ICopilotSdkGateway? copilotGateway = null,
        HttpClient? verificationHttpClient = null,
        SecretRedactor? secretRedactor = null,
        ILoggerFactory? loggerFactory = null)
    {
        var effectiveFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _environmentStore = environmentStore ?? new SessionEnvironmentStore();
        _geminiApiClient = geminiApiClient ?? new GeminiApiClient(logger: effectiveFactory.CreateLogger<GeminiApiClient>());
        _openAiGatewayClient = openAiGatewayClient ?? new OpenAiGatewayClient(logger: effectiveFactory.CreateLogger<OpenAiGatewayClient>());
        _copilotGateway = copilotGateway ?? new GitHubCopilotSdkGateway(_environmentStore.Read, loggerFactory: effectiveFactory);
        _verificationHttpClient = verificationHttpClient ?? new HttpClient();
        _secretRedactor = secretRedactor;
        _logger = effectiveFactory.CreateLogger<ApiKeyManagementService>();
    }

    public static IReadOnlyList<AiProviderType> SupportedProviders => KeyManagedProviders;

    public SessionEnvironmentStore EnvironmentStore => _environmentStore;

    public static bool TryResolveKeyProvider(string? rawInput, out AiProviderType provider)
    {
        provider = AiProviderType.ManualPrompt;
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return false;
        }

        switch (rawInput.Trim().ToLowerInvariant())
        {
            case "1" or "gemini" or "google" or "google-gemini" or "googlegemini":
                provider = AiProviderType.GoogleGemini;
                return true;
            case "2" or "openai" or "openai-gateway" or "openaigateway" or "openai_gateway" or "openai-api" or "gateway":
                provider = AiProviderType.OpenAiGateway;
                return true;
            case "3" or "azure" or "azure-openai" or "azureopenai" or "foundry":
                provider = AiProviderType.AzureOpenAi;
                return true;
            case "4" or "claude" or "anthropic" or "anthropic-claude" or "anthropicclaude":
                provider = AiProviderType.AnthropicClaude;
                return true;
            case "5" or "copilot-sdk" or "copilot" or "github-copilot" or "gh-copilot" or "ghcp" or "copilot_sdk" or "github":
                provider = AiProviderType.GitHubCopilot;
                return true;
            default:
                return false;
        }
    }

    public static ProviderKeyMetadata GetMetadata(AiProviderType provider) => provider switch
    {
        AiProviderType.GoogleGemini => new ProviderKeyMetadata(
            Provider: AiProviderType.GoogleGemini,
            Code: "gemini",
            DisplayName: "Google Gemini REST",
            PrimaryEnvVar: GeminiSecretProvider.PrimaryEnvVar,
            AliasEnvVars: [GeminiSecretProvider.GoogleEnvVar, GeminiSecretProvider.FallbackEnvVar],
            KeyPortalUrl: GeminiSecretProvider.GeminiApiKeysUrl,
            ExpectedFormatHint: "Starts with 'AIza' (at least 14 characters, no spaces)."),
        AiProviderType.OpenAiGateway => new ProviderKeyMetadata(
            Provider: AiProviderType.OpenAiGateway,
            Code: "openai",
            DisplayName: "OpenAI Gateway API",
            PrimaryEnvVar: OpenAiGatewayClient.ApiKeyEnvVar,
            AliasEnvVars: [],
            KeyPortalUrl: OpenAiApiKeysUrl,
            ExpectedFormatHint: "Starts with 'sk-' (e.g. sk-proj-...), or a >=12-char gateway token when --gateway-url / OPENAI_BASE_URL is configured."),
        AiProviderType.AzureOpenAi => new ProviderKeyMetadata(
            Provider: AiProviderType.AzureOpenAi,
            Code: "azure",
            DisplayName: "Azure OpenAI / Foundry",
            PrimaryEnvVar: OpenAiGatewayClient.AzureApiKeyEnvVar,
            AliasEnvVars: [],
            KeyPortalUrl: AzureAiPortalUrl,
            ExpectedFormatHint: ">=16-character Azure Portal / Foundry key (without 'AIza', 'sk-', or 'ghp_' prefixes)."),
        AiProviderType.AnthropicClaude => new ProviderKeyMetadata(
            Provider: AiProviderType.AnthropicClaude,
            Code: "claude",
            DisplayName: "Anthropic Claude",
            PrimaryEnvVar: "ANTHROPIC_API_KEY",
            AliasEnvVars: [],
            KeyPortalUrl: AnthropicApiKeysUrl,
            ExpectedFormatHint: "Starts with 'sk-ant-' (at least 15 characters, no spaces)."),
        AiProviderType.GitHubCopilot => new ProviderKeyMetadata(
            Provider: AiProviderType.GitHubCopilot,
            Code: "copilot-sdk",
            DisplayName: "GitHub Copilot SDK",
            PrimaryEnvVar: GitHubCopilotSdkGateway.CopilotTokenEnvVar,
            AliasEnvVars: [GitHubCopilotSdkGateway.GitHubTokenEnvVar, GitHubCopilotSdkGateway.GhTokenEnvVar],
            KeyPortalUrl: GitHubTokensUrl,
            ExpectedFormatHint: "Starts with 'gho_', 'ghp_', 'ghu_', 'ghs_', 'ghr_', or 'github_pat_'."),
        _ => throw new ArgumentException(
            $"Provider '{RuntimeEnvironmentService.FormatProviderCode(provider)}' does not use an API key. Supported providers: gemini, openai, azure, claude, copilot-sdk.",
            nameof(provider))
    };

    public IReadOnlyList<ProviderKeyStatus> GetAllProviderStatuses(
        string? explicitOpenAiBaseUrl = null,
        string? persistedOpenAiBaseUrl = null,
        string? explicitOpenAiWireApi = null,
        string? persistedOpenAiWireApi = null)
    {
        var list = new List<ProviderKeyStatus>(KeyManagedProviders.Count);
        foreach (var provider in KeyManagedProviders)
        {
            list.Add(GetProviderStatus(
                provider,
                explicitOpenAiBaseUrl,
                persistedOpenAiBaseUrl,
                explicitOpenAiWireApi,
                persistedOpenAiWireApi));
        }

        return list;
    }

    public ProviderKeyStatus GetProviderStatus(
        AiProviderType provider,
        string? explicitOpenAiBaseUrl = null,
        string? persistedOpenAiBaseUrl = null,
        string? explicitOpenAiWireApi = null,
        string? persistedOpenAiWireApi = null)
    {
        var metadata = GetMetadata(provider);
        var (activeEnvVar, activeValue) = ResolveActiveEnvVarAndValue(metadata);
        var isConfigured = !string.IsNullOrWhiteSpace(activeValue);
        var maskedKey = isConfigured ? GeminiSecretProvider.MaskKey(activeValue) : "not set";
        var source = isConfigured ? SecretSource.EnvironmentVariable : SecretSource.None;
        var scopeSummary = activeEnvVar is not null
            ? _environmentStore.DescribeVariableScope(activeEnvVar).ScopeSummary
            : "Not set";

        var companionWarning = false;
        string? companionNote = null;
        string detail;

        switch (provider)
        {
            case AiProviderType.OpenAiGateway:
            {
                var openAiConfig = OpenAiGatewayClient.ResolveConfiguration(
                    environmentReader: _environmentStore.Read,
                    explicitBaseUrl: explicitOpenAiBaseUrl,
                    persistedBaseUrl: persistedOpenAiBaseUrl,
                    explicitWireApi: explicitOpenAiWireApi,
                    persistedWireApi: persistedOpenAiWireApi);
                detail = !openAiConfig.IsValidBaseUrl
                    ? openAiConfig.ValidationError!
                    : isConfigured
                        ? $"{activeEnvVar} [{maskedKey}] @ {openAiConfig.BaseUrl} ({openAiConfig.WireApiCode})"
                        : $"Set {metadata.PrimaryEnvVar} @ {openAiConfig.BaseUrl} ({openAiConfig.WireApiCode})";
                if (!openAiConfig.IsValidBaseUrl)
                {
                    companionWarning = true;
                    companionNote = openAiConfig.ValidationError;
                }

                break;
            }

            case AiProviderType.AzureOpenAi:
            {
                var azureEndpoint = _environmentStore.Read(OpenAiGatewayClient.AzureEndpointEnvVar)?.Trim();
                var hasEndpoint = !string.IsNullOrWhiteSpace(azureEndpoint);
                if (isConfigured && !hasEndpoint)
                {
                    companionWarning = true;
                    companionNote = $"Set {OpenAiGatewayClient.AzureEndpointEnvVar} for live Azure HTTP / BYOK calls.";
                }

                detail = isConfigured
                    ? hasEndpoint
                        ? $"{activeEnvVar} [{maskedKey}] -> {azureEndpoint}"
                        : $"{activeEnvVar} [{maskedKey}] (needs {OpenAiGatewayClient.AzureEndpointEnvVar})"
                    : hasEndpoint
                        ? $"Endpoint {azureEndpoint} set; needs {metadata.PrimaryEnvVar}"
                        : $"Not set (needs {metadata.PrimaryEnvVar} + {OpenAiGatewayClient.AzureEndpointEnvVar})";
                break;
            }

            case AiProviderType.GitHubCopilot:
            {
                var diag = _copilotGateway.InspectDiagnostics();
                if (isConfigured && !diag.RuntimeAvailable)
                {
                    companionWarning = true;
                    companionNote = "Token configured; install 'copilot' CLI or set COPILOT_CLI_URL for live SDK generation.";
                }

                detail = isConfigured
                    ? $"{activeEnvVar} [{maskedKey}] | {diag.Summary}"
                    : $"Not set (uses {metadata.PrimaryEnvVar}/GITHUB_TOKEN or 'copilot login') | {diag.Summary}";
                break;
            }

            default:
                detail = isConfigured
                    ? $"{activeEnvVar} [{maskedKey}] ({scopeSummary})"
                    : $"Not set ({metadata.PrimaryEnvVar} | {metadata.KeyPortalUrl})";
                break;
        }

        return new ProviderKeyStatus(
            Metadata: metadata,
            IsConfigured: isConfigured,
            MaskedKey: maskedKey,
            ActiveEnvVar: activeEnvVar,
            Source: source,
            ScopeSummary: scopeSummary,
            Detail: detail,
            HasCompanionConfigWarning: companionWarning,
            CompanionConfigNote: companionNote);
    }

    public ApiKeyFormatValidationResult ValidateKeyFormat(
        AiProviderType provider,
        string? rawKey,
        string? explicitOpenAiBaseUrl = null,
        string? persistedOpenAiBaseUrl = null)
    {
        if (provider == AiProviderType.ManualPrompt)
        {
            return new ApiKeyFormatValidationResult(
                IsValid: false,
                Provider: provider,
                NormalizedKey: string.Empty,
                MaskedKey: "none",
                IsPlaceholder: false,
                IsCrossProviderMismatch: false,
                DetectedMismatchedProvider: null,
                ErrorMessage: "Manual Prompt mode ('manual') does not use an API key.",
                ActionableHint: "Select one of: gemini, openai, azure, claude, or copilot-sdk.");
        }

        var metadata = GetMetadata(provider);

        if (string.IsNullOrWhiteSpace(rawKey))
        {
            LogEvents.ApiKeyValidated(_logger, metadata.Code, "none", false);
            return new ApiKeyFormatValidationResult(
                IsValid: false,
                Provider: provider,
                NormalizedKey: string.Empty,
                MaskedKey: "none",
                IsPlaceholder: false,
                IsCrossProviderMismatch: false,
                DetectedMismatchedProvider: null,
                ErrorMessage: $"{metadata.DisplayName} API key cannot be empty.",
                ActionableHint: $"Obtain a valid key from {metadata.KeyPortalUrl}. {metadata.ExpectedFormatHint}");
        }

        var trimmed = rawKey.Trim();
        _secretRedactor?.RegisterSecret(trimmed);
        var maskedKey = GeminiSecretProvider.MaskKey(trimmed);

        // Reject raw control chars, newlines, tabs, or embedded whitespace inside key
        if (trimmed.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                LogEvents.ApiKeyValidated(_logger, metadata.Code, maskedKey, false);
                return InvalidFormat(
                    provider,
                    maskedKey,
                    isPlaceholder: false,
                    errorMessage: $"Invalid {metadata.DisplayName} key format: remove the 'Bearer ' / 'Basic ' authorization prefix.",
                    hint: $"Paste only the raw secret token. {metadata.ExpectedFormatHint}");
            }

            LogEvents.ApiKeyValidated(_logger, metadata.Code, maskedKey, false);
            return InvalidFormat(
                provider,
                maskedKey,
                isPlaceholder: false,
                errorMessage: $"Malformed {metadata.DisplayName} API key [{maskedKey}]: key must be a single continuous token without spaces or newlines.",
                hint: metadata.ExpectedFormatHint);
        }

        // Reject KEY=VALUE assignments or header wrappers
        if (EnvAssignmentRegex().IsMatch(trimmed) ||
            trimmed.StartsWith("api-key:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("x-api-key:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("x-goog-api-key:", StringComparison.OrdinalIgnoreCase))
        {
            LogEvents.ApiKeyValidated(_logger, metadata.Code, maskedKey, false);
            return InvalidFormat(
                provider,
                maskedKey,
                isPlaceholder: false,
                errorMessage: $"Malformed {metadata.DisplayName} API key [{maskedKey}]: enter only the key value without 'ENV_VAR=' or HTTP header names.",
                hint: metadata.ExpectedFormatHint);
        }

        // Reject URLs, quotes, or bracketed templates
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains('<') ||
            trimmed.Contains('>') ||
            trimmed.Contains('"') ||
            trimmed.Contains('\'') ||
            trimmed.Contains('[') ||
            trimmed.Contains(']'))
        {
            LogEvents.ApiKeyValidated(_logger, metadata.Code, maskedKey, false);
            return InvalidFormat(
                provider,
                maskedKey,
                isPlaceholder: IsTemplateBracket(trimmed),
                errorMessage: $"Malformed {metadata.DisplayName} API key [{maskedKey}]: value contains a URL, quotes, or template brackets.",
                hint: $"Obtain a valid key from {metadata.KeyPortalUrl}. {metadata.ExpectedFormatHint}");
        }

        // Reject already-masked keys or ellipsis-truncated placeholders
        if (trimmed.StartsWith("****", StringComparison.Ordinal) ||
            trimmed.Contains("...", StringComparison.Ordinal) ||
            trimmed.Contains('…'))
        {
            LogEvents.ApiKeyValidated(_logger, metadata.Code, maskedKey, false);
            return InvalidFormat(
                provider,
                maskedKey,
                isPlaceholder: true,
                errorMessage: $"Rejected placeholder/masked value [{maskedKey}] for {metadata.DisplayName}.",
                hint: $"Paste the complete unmasked API key from {metadata.KeyPortalUrl}.");
        }

        // Reject placeholder / dummy tokens
        if (IsPlaceholderSecret(trimmed))
        {
            LogEvents.ApiKeyValidated(_logger, metadata.Code, maskedKey, false);
            return InvalidFormat(
                provider,
                maskedKey,
                isPlaceholder: true,
                errorMessage: $"Rejected placeholder API key [{maskedKey}] for {metadata.DisplayName}.",
                hint: $"Replace placeholder text with a real key from {metadata.KeyPortalUrl}.");
        }

        // Cross-provider prefix detection
        var detectedPrefixProvider = DetectPrefixedProvider(trimmed);
        if (detectedPrefixProvider is { } mismatched && mismatched != provider)
        {
            var mismatchedMeta = GetMetadata(mismatched);
            LogEvents.ApiKeyValidated(_logger, metadata.Code, maskedKey, false);
            return new ApiKeyFormatValidationResult(
                IsValid: false,
                Provider: provider,
                NormalizedKey: string.Empty,
                MaskedKey: maskedKey,
                IsPlaceholder: false,
                IsCrossProviderMismatch: true,
                DetectedMismatchedProvider: mismatched,
                ErrorMessage:
                    $"Mismatched provider key [{maskedKey}]: key format matches {mismatchedMeta.DisplayName} ({mismatchedMeta.Code}), not {metadata.DisplayName} ({metadata.Code}).",
                ActionableHint:
                    $"Switch to '{mismatchedMeta.Code}' to save this key, or provide a {metadata.DisplayName} key ({metadata.ExpectedFormatHint}).");
        }

        // Provider-specific syntax & length validation
        var syntaxError = ValidateProviderSyntax(
            provider,
            trimmed,
            maskedKey,
            explicitOpenAiBaseUrl,
            persistedOpenAiBaseUrl);
        if (syntaxError is not null)
        {
            LogEvents.ApiKeyValidated(_logger, metadata.Code, maskedKey, false);
            return InvalidFormat(
                provider,
                maskedKey,
                isPlaceholder: false,
                errorMessage: syntaxError,
                hint: $"Get a key at {metadata.KeyPortalUrl}. {metadata.ExpectedFormatHint}");
        }

        LogEvents.ApiKeyValidated(_logger, metadata.Code, maskedKey, true);
        return new ApiKeyFormatValidationResult(
            IsValid: true,
            Provider: provider,
            NormalizedKey: trimmed,
            MaskedKey: maskedKey,
            IsPlaceholder: false,
            IsCrossProviderMismatch: false,
            DetectedMismatchedProvider: null,
            ErrorMessage: null,
            ActionableHint: null);
    }

    public ApiKeyLiveVerificationResult VerifyKeyLive(
        AiProviderType provider,
        string? rawKey = null,
        string? explicitOpenAiBaseUrl = null,
        string? persistedOpenAiBaseUrl = null,
        CancellationToken cancellationToken = default) =>
        VerifyKeyLiveAsync(
            provider,
            rawKey,
            explicitOpenAiBaseUrl,
            persistedOpenAiBaseUrl,
            cancellationToken)
            .GetAwaiter()
            .GetResult();

    public async Task<ApiKeyLiveVerificationResult> VerifyKeyLiveAsync(
        AiProviderType provider,
        string? rawKey = null,
        string? explicitOpenAiBaseUrl = null,
        string? persistedOpenAiBaseUrl = null,
        CancellationToken cancellationToken = default)
    {
        var metadata = GetMetadata(provider);
        var candidateKey = rawKey;
        if (string.IsNullOrWhiteSpace(candidateKey))
        {
            (_, candidateKey) = ResolveActiveEnvVarAndValue(metadata);
            if (string.IsNullOrWhiteSpace(candidateKey))
            {
                return new ApiKeyLiveVerificationResult(
                    Succeeded: false,
                    Provider: provider,
                    MaskedKey: "not set",
                    EndpointChecked: metadata.KeyPortalUrl,
                    HttpStatusCode: null,
                    AccessibleModelCount: null,
                    Summary: $"No {metadata.DisplayName} API key is currently configured.",
                    ErrorMessage: $"No API key configured for '{metadata.Code}' ({metadata.PrimaryEnvVar}).",
                    ActionableHint: $"Run 'backlog keys set {metadata.Code}' or Workspace & Settings -> [K] Manage AI Provider API Keys.");
            }
        }

        var trimmed = candidateKey.Trim();
        _secretRedactor?.RegisterSecret(trimmed);
        var maskedKey = GeminiSecretProvider.MaskKey(trimmed);

        var formatCheck = ValidateKeyFormat(provider, trimmed, explicitOpenAiBaseUrl, persistedOpenAiBaseUrl);
        if (!formatCheck.IsValid)
        {
            return new ApiKeyLiveVerificationResult(
                Succeeded: false,
                Provider: provider,
                MaskedKey: maskedKey,
                EndpointChecked: "offline-format-check",
                HttpStatusCode: null,
                AccessibleModelCount: null,
                Summary: formatCheck.ErrorMessage ?? "API key format validation failed.",
                ErrorMessage: formatCheck.ErrorMessage,
                ActionableHint: formatCheck.ActionableHint);
        }

        var sw = Stopwatch.StartNew();
        var endpointChecked = metadata.KeyPortalUrl;

        try
        {
            switch (provider)
            {
                case AiProviderType.GoogleGemini:
                {
                    endpointChecked = "https://generativelanguage.googleapis.com/v1beta/models";
                    var catalog = _geminiApiClient.ListModelCatalog(trimmed);
                    sw.Stop();
                    LogEvents.ApiKeyLiveVerificationSucceeded(_logger, metadata.Code, maskedKey, endpointChecked, sw.ElapsedMilliseconds);
                    return new ApiKeyLiveVerificationResult(
                        Succeeded: true,
                        Provider: provider,
                        MaskedKey: maskedKey,
                        EndpointChecked: endpointChecked,
                        HttpStatusCode: 200,
                        AccessibleModelCount: catalog.SupportedModels.Count,
                        Summary: $"Verified {metadata.DisplayName} key [{maskedKey}] ({catalog.SupportedModels.Count} generateContent model(s) accessible).",
                        ErrorMessage: null,
                        ActionableHint: null);
                }

                case AiProviderType.OpenAiGateway:
                {
                    var config = OpenAiGatewayClient.ResolveConfiguration(
                        environmentReader: _environmentStore.Read,
                        explicitApiKey: trimmed,
                        explicitBaseUrl: explicitOpenAiBaseUrl,
                        persistedBaseUrl: persistedOpenAiBaseUrl);
                    endpointChecked = config.IsValidBaseUrl
                        ? OpenAiGatewayClient.BuildModelsEndpointUri(config.BaseUrl, config.AzureApiVersion).GetLeftPart(UriPartial.Path)
                        : config.BaseUrl;
                    if (!config.IsValidBaseUrl)
                    {
                        throw new ApiKeyValidationException(
                            config.ValidationError ?? "Invalid OpenAI Gateway URL.",
                            $"Pass a valid http:// or https:// base URL via --gateway-url or {OpenAiGatewayClient.BaseUrlEnvVar}.");
                    }

                    var catalog = _openAiGatewayClient.ListModelCatalog(config);
                    sw.Stop();
                    LogEvents.ApiKeyLiveVerificationSucceeded(_logger, metadata.Code, maskedKey, endpointChecked, sw.ElapsedMilliseconds);
                    return new ApiKeyLiveVerificationResult(
                        Succeeded: true,
                        Provider: provider,
                        MaskedKey: maskedKey,
                        EndpointChecked: endpointChecked,
                        HttpStatusCode: 200,
                        AccessibleModelCount: catalog.SupportedModels.Count,
                        Summary: $"Verified {metadata.DisplayName} key [{maskedKey}] at {config.BaseUrl} ({catalog.SupportedModels.Count} model(s) accessible).",
                        ErrorMessage: null,
                        ActionableHint: null);
                }

                case AiProviderType.AzureOpenAi:
                {
                    var azureEndpoint = !string.IsNullOrWhiteSpace(explicitOpenAiBaseUrl)
                        ? explicitOpenAiBaseUrl.Trim()
                        : _environmentStore.Read(OpenAiGatewayClient.AzureEndpointEnvVar)?.Trim();
                    if (string.IsNullOrWhiteSpace(azureEndpoint))
                    {
                        throw new ApiKeyValidationException(
                            $"Live Azure OpenAI verification requires {OpenAiGatewayClient.AzureEndpointEnvVar} (or --gateway-url).",
                            $"Set {OpenAiGatewayClient.AzureEndpointEnvVar} (e.g. https://<resource>.openai.azure.com) or skip live check to store format-validated key.");
                    }

                    var azureConfig = OpenAiGatewayClient.ResolveConfiguration(
                        environmentReader: _environmentStore.Read,
                        explicitApiKey: trimmed,
                        explicitBaseUrl: explicitOpenAiBaseUrl,
                        persistedBaseUrl: persistedOpenAiBaseUrl,
                        preferAzureEnvironment: true);
                    endpointChecked = azureConfig.IsValidBaseUrl
                        ? OpenAiGatewayClient.BuildModelsEndpointUri(azureConfig.BaseUrl, azureConfig.AzureApiVersion).GetLeftPart(UriPartial.Path)
                        : azureEndpoint;
                    if (!azureConfig.IsValidBaseUrl)
                    {
                        throw new ApiKeyValidationException(
                            azureConfig.ValidationError ?? "Invalid Azure OpenAI endpoint URL.",
                            $"Verify {OpenAiGatewayClient.AzureEndpointEnvVar} starts with https://.");
                    }

                    var catalog = _openAiGatewayClient.ListModelCatalog(azureConfig);
                    sw.Stop();
                    LogEvents.ApiKeyLiveVerificationSucceeded(_logger, metadata.Code, maskedKey, endpointChecked, sw.ElapsedMilliseconds);
                    return new ApiKeyLiveVerificationResult(
                        Succeeded: true,
                        Provider: provider,
                        MaskedKey: maskedKey,
                        EndpointChecked: endpointChecked,
                        HttpStatusCode: 200,
                        AccessibleModelCount: catalog.SupportedModels.Count,
                        Summary: $"Verified {metadata.DisplayName} key [{maskedKey}] at {azureConfig.BaseUrl} ({catalog.SupportedModels.Count} model(s) accessible).",
                        ErrorMessage: null,
                        ActionableHint: null);
                }

                case AiProviderType.AnthropicClaude:
                {
                    var baseUrl = ResolveAnthropicBaseUrl(explicitOpenAiBaseUrl);
                    endpointChecked = BuildAnthropicModelsUri(baseUrl).GetLeftPart(UriPartial.Path);
                    var models = await ListAnthropicModelsAsync(trimmed, baseUrl, cancellationToken).ConfigureAwait(false);
                    sw.Stop();
                    LogEvents.ApiKeyLiveVerificationSucceeded(_logger, metadata.Code, maskedKey, endpointChecked, sw.ElapsedMilliseconds);
                    return new ApiKeyLiveVerificationResult(
                        Succeeded: true,
                        Provider: provider,
                        MaskedKey: maskedKey,
                        EndpointChecked: endpointChecked,
                        HttpStatusCode: 200,
                        AccessibleModelCount: models.Count,
                        Summary: $"Verified {metadata.DisplayName} key [{maskedKey}] at {baseUrl} ({models.Count} model(s) accessible).",
                        ErrorMessage: null,
                        ActionableHint: null);
                }

                case AiProviderType.GitHubCopilot:
                {
                    var diag = _copilotGateway.InspectDiagnostics();
                    if (diag.RuntimeAvailable &&
                        !string.Equals(diag.ConnectionMode, "remote-tcp", StringComparison.OrdinalIgnoreCase))
                    {
                        endpointChecked = $"copilot-sdk:{diag.ConnectionMode}";
                        var models = await _copilotGateway.ListModelsAsync(trimmed, cancellationToken).ConfigureAwait(false);
                        sw.Stop();
                        LogEvents.ApiKeyLiveVerificationSucceeded(_logger, metadata.Code, maskedKey, endpointChecked, sw.ElapsedMilliseconds);
                        return new ApiKeyLiveVerificationResult(
                            Succeeded: true,
                            Provider: provider,
                            MaskedKey: maskedKey,
                            EndpointChecked: endpointChecked,
                            HttpStatusCode: 200,
                            AccessibleModelCount: models.Count,
                            Summary: $"Verified {metadata.DisplayName} token [{maskedKey}] via {diag.ConnectionMode} ({models.Count} model(s) accessible).",
                            ErrorMessage: null,
                            ActionableHint: null);
                    }

                    endpointChecked = GitHubUserApiUrl;
                    var login = await VerifyGitHubUserTokenAsync(trimmed, cancellationToken).ConfigureAwait(false);
                    sw.Stop();
                    LogEvents.ApiKeyLiveVerificationSucceeded(_logger, metadata.Code, maskedKey, endpointChecked, sw.ElapsedMilliseconds);
                    var accountNote = string.IsNullOrWhiteSpace(login) ? "GitHub API" : $"@{login}";
                    var runtimeHint = diag.RuntimeAvailable
                        ? string.Empty
                        : " (install 'copilot' CLI or set COPILOT_CLI_URL for live Copilot SDK sessions)";
                    return new ApiKeyLiveVerificationResult(
                        Succeeded: true,
                        Provider: provider,
                        MaskedKey: maskedKey,
                        EndpointChecked: endpointChecked,
                        HttpStatusCode: 200,
                        AccessibleModelCount: null,
                        Summary: $"Verified GitHub token [{maskedKey}] for {accountNote} via api.github.com{runtimeHint}.",
                        ErrorMessage: null,
                        ActionableHint: null);
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            var redactedError = RedactSecretText(trimmed, ex.Message);
            var statusCode = TryExtractHttpStatusCode(redactedError);
            var hint = ex is ApiKeyValidationException customEx && !string.IsNullOrWhiteSpace(customEx.ActionableHint)
                ? customEx.ActionableHint
                : BuildLiveVerificationHint(provider, statusCode);

            LogEvents.ApiKeyLiveVerificationFailed(_logger, metadata.Code, maskedKey, endpointChecked, redactedError);
            return new ApiKeyLiveVerificationResult(
                Succeeded: false,
                Provider: provider,
                MaskedKey: maskedKey,
                EndpointChecked: endpointChecked,
                HttpStatusCode: statusCode,
                AccessibleModelCount: null,
                Summary: $"{metadata.DisplayName} live verification failed [{maskedKey}].",
                ErrorMessage: redactedError,
                ActionableHint: hint);
        }
    }

    public ApiKeyMutationResult SetKey(
        AiProviderType provider,
        string? rawKey,
        ApiKeyStorageScope scope = ApiKeyStorageScope.Session,
        bool verifyLive = false,
        string? explicitOpenAiBaseUrl = null,
        string? persistedOpenAiBaseUrl = null)
    {
        if (scope == ApiKeyStorageScope.All)
        {
            scope = ApiKeyStorageScope.User;
        }

        var metadata = GetMetadata(provider);
        var formatResult = ValidateKeyFormat(provider, rawKey, explicitOpenAiBaseUrl, persistedOpenAiBaseUrl);
        if (!formatResult.IsValid)
        {
            return new ApiKeyMutationResult(
                Succeeded: false,
                Provider: provider,
                PrimaryEnvVar: metadata.PrimaryEnvVar,
                MaskedKey: formatResult.MaskedKey,
                AppliedScope: scope,
                PersistedToOsUserEnvironment: false,
                Message: formatResult.ErrorMessage ?? "API key validation failed.",
                ScopeGuidanceNote: formatResult.ActionableHint,
                FormatValidation: formatResult);
        }

        ApiKeyLiveVerificationResult? liveResult = null;
        if (verifyLive)
        {
            liveResult = VerifyKeyLive(
                provider,
                formatResult.NormalizedKey,
                explicitOpenAiBaseUrl,
                persistedOpenAiBaseUrl);
            if (!liveResult.Succeeded)
            {
                return new ApiKeyMutationResult(
                    Succeeded: false,
                    Provider: provider,
                    PrimaryEnvVar: metadata.PrimaryEnvVar,
                    MaskedKey: formatResult.MaskedKey,
                    AppliedScope: scope,
                    PersistedToOsUserEnvironment: false,
                    Message: liveResult.ErrorMessage ?? liveResult.Summary,
                    ScopeGuidanceNote: liveResult.ActionableHint,
                    FormatValidation: formatResult,
                    LiveVerification: liveResult);
            }
        }

        var persistedToUser = _environmentStore.Write(metadata.PrimaryEnvVar, formatResult.NormalizedKey, scope);
        _secretRedactor?.RegisterSecret(formatResult.NormalizedKey);

        string? guidanceNote = null;
        if (scope == ApiKeyStorageScope.User && !persistedToUser)
        {
            guidanceNote = $"Active session updated. OS User registry persistence is Windows-only; persist across POSIX shells via: export {metadata.PrimaryEnvVar}=\"<your-key>\"";
        }

        var scopeLabel = persistedToUser
            ? "Active Session + OS User Env"
            : "Active Runtime Session";
        LogEvents.ApiKeySaved(_logger, metadata.Code, metadata.PrimaryEnvVar, formatResult.MaskedKey, scopeLabel, persistedToUser);

        return new ApiKeyMutationResult(
            Succeeded: true,
            Provider: provider,
            PrimaryEnvVar: metadata.PrimaryEnvVar,
            MaskedKey: formatResult.MaskedKey,
            AppliedScope: scope,
            PersistedToOsUserEnvironment: persistedToUser,
            Message: $"Registered {metadata.DisplayName} key [{formatResult.MaskedKey}] in {scopeLabel} ({metadata.PrimaryEnvVar}).",
            ScopeGuidanceNote: guidanceNote,
            FormatValidation: formatResult,
            LiveVerification: liveResult);
    }

    public ApiKeyMutationResult ClearKey(
        AiProviderType provider,
        ApiKeyStorageScope scope = ApiKeyStorageScope.Session)
    {
        var metadata = GetMetadata(provider);
        var (_, existingValue) = ResolveActiveEnvVarAndValue(metadata);
        var maskedExisting = string.IsNullOrWhiteSpace(existingValue)
            ? "not set"
            : GeminiSecretProvider.MaskKey(existingValue);

        var clearedUserTargets = 0;
        var clearUser = scope is ApiKeyStorageScope.User or ApiKeyStorageScope.All;

        if (_environmentStore.Clear(metadata.PrimaryEnvVar, scope, clearUserTarget: clearUser))
        {
            clearedUserTargets++;
        }

        foreach (var alias in metadata.AliasEnvVars)
        {
            // Keep global GITHUB_TOKEN / GH_TOKEN intact in OS User registry unless only process session is cleared,
            // so other CLI tools (e.g. 'gh') are not unexpectedly logged out on Windows.
            var allowAliasUserClear = provider != AiProviderType.GitHubCopilot;
            if (_environmentStore.Clear(alias, scope, clearUserTarget: clearUser && allowAliasUserClear))
            {
                clearedUserTargets++;
            }
        }

        var remainingUserValue = _environmentStore.ReadFromScope(metadata.PrimaryEnvVar, EnvironmentVariableTarget.User);
        string? guidanceNote = null;
        if (scope == ApiKeyStorageScope.Session && !string.IsNullOrWhiteSpace(remainingUserValue))
        {
            guidanceNote = $"{metadata.PrimaryEnvVar} is cleared from the active session; OS User env persistence remains (clear with '--scope all').";
        }
        else if (provider == AiProviderType.GitHubCopilot &&
                 clearUser &&
                 (!string.IsNullOrWhiteSpace(_environmentStore.ReadFromScope(GitHubCopilotSdkGateway.GitHubTokenEnvVar, EnvironmentVariableTarget.User)) ||
                  !string.IsNullOrWhiteSpace(_environmentStore.ReadFromScope(GitHubCopilotSdkGateway.GhTokenEnvVar, EnvironmentVariableTarget.User))))
        {
            guidanceNote = "Cleared COPILOT_GITHUB_TOKEN and active session tokens; OS User GITHUB_TOKEN/GH_TOKEN was left untouched for 'gh' CLI compatibility.";
        }

        var scopeLabel = clearedUserTargets > 0 ? "Active Session + OS User Env" : "Active Runtime Session";
        LogEvents.ApiKeyCleared(_logger, metadata.Code, maskedExisting, scopeLabel);

        return new ApiKeyMutationResult(
            Succeeded: true,
            Provider: provider,
            PrimaryEnvVar: metadata.PrimaryEnvVar,
            MaskedKey: maskedExisting,
            AppliedScope: scope,
            PersistedToOsUserEnvironment: clearedUserTargets > 0,
            Message: string.IsNullOrWhiteSpace(existingValue)
                ? $"Cleared {metadata.DisplayName} ({metadata.PrimaryEnvVar}) from {scopeLabel} (was not set)."
                : $"Cleared {metadata.DisplayName} key [{maskedExisting}] from {scopeLabel}.",
            ScopeGuidanceNote: guidanceNote);
    }

    public ApiKeyMutationResult SetAzureEndpoint(string? rawEndpoint, ApiKeyStorageScope scope = ApiKeyStorageScope.Session)
    {
        if (scope == ApiKeyStorageScope.All)
        {
            scope = ApiKeyStorageScope.User;
        }

        if (!OpenAiGatewayClient.TryNormalizeBaseUrl(rawEndpoint, out var normalizedUrl, out var error))
        {
            return new ApiKeyMutationResult(
                Succeeded: false,
                Provider: AiProviderType.AzureOpenAi,
                PrimaryEnvVar: OpenAiGatewayClient.AzureEndpointEnvVar,
                MaskedKey: "none",
                AppliedScope: scope,
                PersistedToOsUserEnvironment: false,
                Message: error ?? "Invalid Azure OpenAI endpoint URL.",
                ScopeGuidanceNote: "Expected https://<resource>.openai.azure.com or https://<foundry>.services.ai.azure.com/openai/v1.");
        }

        var persisted = _environmentStore.Write(OpenAiGatewayClient.AzureEndpointEnvVar, normalizedUrl, scope);
        var scopeLabel = persisted ? "Active Session + OS User Env" : "Active Runtime Session";
        return new ApiKeyMutationResult(
            Succeeded: true,
            Provider: AiProviderType.AzureOpenAi,
            PrimaryEnvVar: OpenAiGatewayClient.AzureEndpointEnvVar,
            MaskedKey: normalizedUrl,
            AppliedScope: scope,
            PersistedToOsUserEnvironment: persisted,
            Message: $"Set {OpenAiGatewayClient.AzureEndpointEnvVar} to '{normalizedUrl}' in {scopeLabel}.");
    }

    public IReadOnlyList<AnthropicModelDescriptor> ListAnthropicModels(
        string apiKey,
        string? baseUrlOverride = null,
        CancellationToken cancellationToken = default) =>
        ListAnthropicModelsAsync(apiKey, baseUrlOverride, cancellationToken).GetAwaiter().GetResult();

    public async Task<IReadOnlyList<AnthropicModelDescriptor>> ListAnthropicModelsAsync(
        string apiKey,
        string? baseUrlOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var trimmedKey = apiKey.Trim();
        _secretRedactor?.RegisterSecret(trimmedKey);
        var baseUrl = ResolveAnthropicBaseUrl(baseUrlOverride);
        var endpointUri = BuildAnthropicModelsUri(baseUrl);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpointUri);
        request.Headers.TryAddWithoutValidation("x-api-key", trimmedKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicApiVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _verificationHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var safeDetail = RedactSecretText(trimmedKey, TryExtractApiErrorMessage(responseBody) ?? responseBody);
            throw new ApiKeyValidationException(
                $"Anthropic API returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {safeDetail}",
                $"Verify ANTHROPIC_API_KEY at {AnthropicApiKeysUrl}.");
        }

        var models = new List<AnthropicModelDescriptor>();
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var id = item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                        ? idProp.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var displayName = item.TryGetProperty("display_name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                        ? nameProp.GetString()
                        : null;
                    models.Add(new AnthropicModelDescriptor(id.Trim(), string.IsNullOrWhiteSpace(displayName) ? id.Trim() : displayName.Trim()));
                }
            }
        }

        return models
            .DistinctBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<string?> VerifyGitHubUserTokenAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubUserApiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("SmartBacklogAssistant/1.1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _verificationHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var safeDetail = RedactSecretText(token, TryExtractApiErrorMessage(responseBody) ?? responseBody);
            throw new ApiKeyValidationException(
                $"GitHub API returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {safeDetail}",
                $"Verify your GitHub token at {GitHubTokensUrl}.");
        }

        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("login", out var loginProp) && loginProp.ValueKind == JsonValueKind.String)
            {
                return loginProp.GetString();
            }
        }

        return null;
    }

    private (string? ActiveEnvVar, string? Value) ResolveActiveEnvVarAndValue(ProviderKeyMetadata metadata)
    {
        var primary = _environmentStore.Read(metadata.PrimaryEnvVar);
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return (metadata.PrimaryEnvVar, primary.Trim());
        }

        foreach (var alias in metadata.AliasEnvVars)
        {
            var aliasVal = _environmentStore.Read(alias);
            if (!string.IsNullOrWhiteSpace(aliasVal))
            {
                return (alias, aliasVal.Trim());
            }
        }

        return (null, null);
    }

    private string? ValidateProviderSyntax(
        AiProviderType provider,
        string trimmed,
        string maskedKey,
        string? explicitOpenAiBaseUrl,
        string? persistedOpenAiBaseUrl)
    {
        switch (provider)
        {
            case AiProviderType.GoogleGemini:
                if (!trimmed.StartsWith("AIza", StringComparison.Ordinal))
                {
                    return $"Invalid Google Gemini API key [{maskedKey}]: Google AI Studio keys start with 'AIza' (case-sensitive).";
                }

                if (trimmed.Length < 14 || !GeminiTokenRegex().IsMatch(trimmed))
                {
                    return $"Malformed Google Gemini API key [{maskedKey}]: expected 'AIza' followed by at least 10 alphanumeric, '-', or '_' characters.";
                }

                return null;

            case AiProviderType.AnthropicClaude:
                if (!trimmed.StartsWith("sk-ant-", StringComparison.Ordinal))
                {
                    return $"Invalid Anthropic Claude API key [{maskedKey}]: Anthropic keys must start with 'sk-ant-'.";
                }

                if (trimmed.Length < 15 || !AnthropicTokenRegex().IsMatch(trimmed))
                {
                    return $"Malformed Anthropic Claude API key [{maskedKey}]: expected 'sk-ant-' followed by at least 8 alphanumeric, '-', or '_' characters.";
                }

                return null;

            case AiProviderType.GitHubCopilot:
                if (!GitHubTokenValidationRegex().IsMatch(trimmed))
                {
                    return $"Invalid GitHub Copilot token [{maskedKey}]: expected 'gho_', 'ghp_', 'ghu_', 'ghs_', 'ghr_' (>=14 chars), or 'github_pat_' (>=27 chars).";
                }

                return null;

            case AiProviderType.OpenAiGateway:
            {
                if (trimmed.Length < 12 || !OpenAiTokenRegex().IsMatch(trimmed))
                {
                    return $"Malformed OpenAI Gateway API key [{maskedKey}]: expected at least 12 valid token characters without whitespace.";
                }

                var gatewayConfig = OpenAiGatewayClient.ResolveConfiguration(
                    environmentReader: _environmentStore.Read,
                    explicitBaseUrl: explicitOpenAiBaseUrl,
                    persistedBaseUrl: persistedOpenAiBaseUrl);
                if (!gatewayConfig.IsCustomBaseUrl && !trimmed.StartsWith("sk-", StringComparison.Ordinal))
                {
                    return $"Invalid OpenAI API key [{maskedKey}]: standard OpenAI keys start with 'sk-' (e.g. 'sk-proj-...'). Configure --gateway-url / OPENAI_BASE_URL if using a non-OpenAI proxy key.";
                }

                return null;
            }

            case AiProviderType.AzureOpenAi:
                if (trimmed.Length < 16 || !AzureTokenRegex().IsMatch(trimmed))
                {
                    return $"Malformed Azure OpenAI API key [{maskedKey}]: expected at least 16 valid Azure Portal / Foundry key characters.";
                }

                return null;

            default:
                return null;
        }
    }

    private static AiProviderType? DetectPrefixedProvider(string trimmed)
    {
        if (trimmed.StartsWith("AIza", StringComparison.Ordinal))
        {
            return AiProviderType.GoogleGemini;
        }

        if (trimmed.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase))
        {
            return AiProviderType.AnthropicClaude;
        }

        if (trimmed.StartsWith("ghp_", StringComparison.Ordinal) ||
            trimmed.StartsWith("gho_", StringComparison.Ordinal) ||
            trimmed.StartsWith("ghu_", StringComparison.Ordinal) ||
            trimmed.StartsWith("ghs_", StringComparison.Ordinal) ||
            trimmed.StartsWith("ghr_", StringComparison.Ordinal) ||
            trimmed.StartsWith("github_pat_", StringComparison.Ordinal))
        {
            return AiProviderType.GitHubCopilot;
        }

        if (trimmed.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
        {
            return AiProviderType.OpenAiGateway;
        }

        return null;
    }

    private static bool IsPlaceholderSecret(string trimmed)
    {
        if (ExactPlaceholderValues.Contains(trimmed))
        {
            return true;
        }

        var lower = trimmed.ToLowerInvariant();
        foreach (var token in PlaceholderTokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var body = StripKnownPrefix(trimmed);
        if (body.Length >= 8)
        {
            var distinct = new HashSet<char>();
            foreach (var ch in body)
            {
                distinct.Add(char.ToLowerInvariant(ch));
                if (distinct.Count >= 3)
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private static string StripKnownPrefix(string key)
    {
        string[] prefixes =
        [
            "github_pat_",
            "sk-ant-api03-",
            "sk-ant-",
            "sk-svcacct-",
            "sk-proj-",
            "sk-or-v1-",
            "sk-live-",
            "sk-",
            "AIzaSy",
            "AIza",
            "ghp_",
            "gho_",
            "ghu_",
            "ghs_",
            "ghr_"
        ];

        foreach (var prefix in prefixes)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return key[prefix.Length..];
            }
        }

        return key;
    }

    private static bool IsTemplateBracket(string value) =>
        value.Contains('<') || value.Contains('>') || value.Contains('[');

    private static ApiKeyFormatValidationResult InvalidFormat(
        AiProviderType provider,
        string maskedKey,
        bool isPlaceholder,
        string errorMessage,
        string hint) =>
        new(
            IsValid: false,
            Provider: provider,
            NormalizedKey: string.Empty,
            MaskedKey: maskedKey,
            IsPlaceholder: isPlaceholder,
            IsCrossProviderMismatch: false,
            DetectedMismatchedProvider: null,
            ErrorMessage: errorMessage,
            ActionableHint: hint);

    private string ResolveAnthropicBaseUrl(string? baseUrlOverride)
    {
        var raw = !string.IsNullOrWhiteSpace(baseUrlOverride)
            ? baseUrlOverride.Trim()
            : _environmentStore.Read(AnthropicBaseUrlEnvVar)?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultAnthropicBaseUrl;
        }

        if (!OpenAiGatewayClient.TryNormalizeBaseUrl(raw, out var normalized, out var error))
        {
            throw new ApiKeyValidationException(
                error ?? $"Invalid Anthropic base URL '{raw}'.",
                $"Set {AnthropicBaseUrlEnvVar} to a valid https:// base URL (default: {DefaultAnthropicBaseUrl}).");
        }

        return normalized;
    }

    private static Uri BuildAnthropicModelsUri(string baseUrl)
    {
        var uri = new Uri(baseUrl.TrimEnd('/'));
        var path = uri.AbsolutePath.TrimEnd('/');
        var effectivePath = path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{path}/models"
            : string.IsNullOrEmpty(path)
                ? "/v1/models"
                : $"{path}/v1/models";
        var builder = new UriBuilder(uri) { Path = effectivePath };
        return builder.Uri;
    }

    private string RedactSecretText(string? rawSecret, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;
        if (!string.IsNullOrWhiteSpace(rawSecret))
        {
            var trimmed = rawSecret.Trim();
            if (trimmed.Length >= 4 && result.Contains(trimmed, StringComparison.Ordinal))
            {
                result = result.Replace(trimmed, GeminiSecretProvider.MaskKey(trimmed), StringComparison.Ordinal);
            }
        }

        return _secretRedactor is not null
            ? _secretRedactor.Redact(result)
            : SecretRedactor.RedactPatterns(result);
    }

    private static string? TryExtractApiErrorMessage(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String)
                {
                    return err.GetString();
                }

                if (err.ValueKind == JsonValueKind.Object &&
                    err.TryGetProperty("message", out var msg) &&
                    msg.ValueKind == JsonValueKind.String)
                {
                    return msg.GetString();
                }
            }

            if (doc.RootElement.TryGetProperty("message", out var topMsg) &&
                topMsg.ValueKind == JsonValueKind.String)
            {
                return topMsg.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static int? TryExtractHttpStatusCode(string message)
    {
        var match = HttpStatusRegex().Match(message);
        return match.Success && int.TryParse(match.Groups[1].Value, out var code)
            ? code
            : null;
    }

    private static string BuildLiveVerificationHint(AiProviderType provider, int? statusCode)
    {
        var meta = GetMetadata(provider);
        if (statusCode == 429)
        {
            return $"Provider rate limit reached for '{meta.Code}'. Wait briefly and re-verify.";
        }

        return provider switch
        {
            AiProviderType.GoogleGemini => $"Verify your Gemini key at {meta.KeyPortalUrl} and confirm Generative Language API access.",
            AiProviderType.OpenAiGateway => $"Verify {meta.PrimaryEnvVar} and gateway URL (--gateway-url / {OpenAiGatewayClient.BaseUrlEnvVar}).",
            AiProviderType.AzureOpenAi => $"Verify {meta.PrimaryEnvVar} and {OpenAiGatewayClient.AzureEndpointEnvVar} in Azure Portal / Foundry ({meta.KeyPortalUrl}).",
            AiProviderType.AnthropicClaude => $"Verify {meta.PrimaryEnvVar} at {meta.KeyPortalUrl}.",
            AiProviderType.GitHubCopilot => $"Verify your GitHub token scopes/expiration at {meta.KeyPortalUrl} or run 'copilot login'.",
            _ => $"Verify your key at {meta.KeyPortalUrl}."
        };
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{2,}=", RegexOptions.CultureInvariant)]
    private static partial Regex EnvAssignmentRegex();

    [GeneratedRegex(@"^AIza[0-9A-Za-z\-_]{10,}$", RegexOptions.CultureInvariant)]
    private static partial Regex GeminiTokenRegex();

    [GeneratedRegex(@"^sk-ant-[A-Za-z0-9\-_]{8,}$", RegexOptions.CultureInvariant)]
    private static partial Regex AnthropicTokenRegex();

    [GeneratedRegex(@"^(?:(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9_]{10,}|github_pat_[A-Za-z0-9_]{16,})$", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenValidationRegex();

    [GeneratedRegex(@"^[A-Za-z0-9\-_.+=:/]{12,}$", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiTokenRegex();

    [GeneratedRegex(@"^[A-Za-z0-9\-_+/=.]{16,}$", RegexOptions.CultureInvariant)]
    private static partial Regex AzureTokenRegex();

    [GeneratedRegex(@"\bHTTP\s+(\d{3})\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HttpStatusRegex();
}
