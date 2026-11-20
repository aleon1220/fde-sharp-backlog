using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

public enum OpenAiWireApi
{
    ChatCompletions = 0,
    Responses = 1
}

public sealed record OpenAiGatewayConfiguration(
    string BaseUrl,
    string BaseUrlSource,
    string? ApiKey,
    string MaskedApiKey,
    SecretSource ApiKeySource,
    string ApiKeySourceDetail,
    OpenAiWireApi WireApi,
    string WireApiSource,
    bool IsCustomBaseUrl,
    bool IsValidBaseUrl,
    string? ValidationError,
    bool UseAzureApiKeyHeader,
    string? AzureApiVersion)
{
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
    public bool IsConfiguredForLive => IsValidBaseUrl && HasApiKey;
    public string WireApiCode => OpenAiGatewayClient.FormatWireApiCode(WireApi);
}

public sealed record OpenAiGatewayModel(
    string ModelId,
    string? OwnedBy = null);

public sealed record OpenAiModelCatalogResult(
    IReadOnlyList<OpenAiGatewayModel> SupportedModels,
    IReadOnlyDictionary<string, IReadOnlyList<string>> UnsupportedModelCapabilities);

public sealed record OpenAiGatewayGenerationRequest(
    string ModelId,
    string Prompt,
    string? SystemPrompt = null,
    string? ApiKey = null,
    string? BaseUrl = null,
    OpenAiWireApi? WireApi = null,
    bool EnforceJsonOutput = true);

/// <summary>
/// Direct HTTP client for OpenAI and OpenAI-compatible enterprise gateways
/// supporting <c>/v1/chat/completions</c>, <c>/v1/responses</c>, <c>/v1/models</c>,
/// custom gateway URLs (<c>OPENAI_BASE_URL</c> / <c>AZURE_OPENAI_ENDPOINT</c>),
/// and Bearer or Azure <c>api-key</c> authentication.
/// </summary>
public sealed class OpenAiGatewayClient
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";
    public const string ApiKeyEnvVar = "OPENAI_API_KEY";
    public const string BaseUrlEnvVar = "OPENAI_BASE_URL";
    public const string WireApiEnvVar = "OPENAI_WIRE_API";
    public const string ApiModeEnvVar = "OPENAI_API_MODE";
    public const string AzureEndpointEnvVar = "AZURE_OPENAI_ENDPOINT";
    public const string AzureApiKeyEnvVar = "AZURE_OPENAI_API_KEY";
    public const string AzureDeploymentEnvVar = "AZURE_OPENAI_DEPLOYMENT";
    public const string AzureApiVersionEnvVar = "AZURE_OPENAI_API_VERSION";
    public const string DefaultAzureApiVersion = "2024-10-21";
    public const string ValidWireApisDisplay = "chat, responses";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string[] NonChatModelTokens =
    [
        "embedding",
        "whisper",
        "dall-e",
        "tts-",
        "moderation",
        "realtime",
        "transcribe",
        "sora",
        "gpt-image"
    ];

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiGatewayClient> _logger;

    public OpenAiGatewayClient(HttpClient? httpClient = null, ILogger<OpenAiGatewayClient>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger ?? NullLogger<OpenAiGatewayClient>.Instance;
    }

    public static OpenAiGatewayConfiguration ResolveConfiguration(
        Func<string, string?>? environmentReader = null,
        string? explicitApiKey = null,
        string? explicitBaseUrl = null,
        string? persistedBaseUrl = null,
        string? explicitWireApi = null,
        string? persistedWireApi = null,
        bool preferAzureEnvironment = false)
    {
        var readEnv = environmentReader ?? Environment.GetEnvironmentVariable;

        string? rawBaseUrl = null;
        var baseUrlSource = "default";
        var cameFromAzureEndpoint = false;

        if (preferAzureEnvironment)
        {
            if (!string.IsNullOrWhiteSpace(explicitBaseUrl))
            {
                rawBaseUrl = explicitBaseUrl.Trim();
                baseUrlSource = "CLI --gateway-url";
            }
            else if (!string.IsNullOrWhiteSpace(readEnv(AzureEndpointEnvVar)))
            {
                rawBaseUrl = readEnv(AzureEndpointEnvVar)!.Trim();
                baseUrlSource = $"Env {AzureEndpointEnvVar}";
                cameFromAzureEndpoint = true;
            }
            else if (!string.IsNullOrWhiteSpace(readEnv(BaseUrlEnvVar)))
            {
                rawBaseUrl = readEnv(BaseUrlEnvVar)!.Trim();
                baseUrlSource = $"Env {BaseUrlEnvVar}";
            }
            else if (!string.IsNullOrWhiteSpace(persistedBaseUrl))
            {
                rawBaseUrl = persistedBaseUrl.Trim();
                baseUrlSource = "settings.json";
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(explicitBaseUrl))
            {
                rawBaseUrl = explicitBaseUrl.Trim();
                baseUrlSource = "CLI --gateway-url";
            }
            else if (!string.IsNullOrWhiteSpace(readEnv(BaseUrlEnvVar)))
            {
                rawBaseUrl = readEnv(BaseUrlEnvVar)!.Trim();
                baseUrlSource = $"Env {BaseUrlEnvVar}";
            }
            else if (!string.IsNullOrWhiteSpace(persistedBaseUrl))
            {
                rawBaseUrl = persistedBaseUrl.Trim();
                baseUrlSource = "settings.json";
            }
            else if (!string.IsNullOrWhiteSpace(readEnv(AzureEndpointEnvVar)))
            {
                rawBaseUrl = readEnv(AzureEndpointEnvVar)!.Trim();
                baseUrlSource = $"Env {AzureEndpointEnvVar}";
                cameFromAzureEndpoint = true;
            }
        }

        var isCustomBaseUrl = !string.IsNullOrWhiteSpace(rawBaseUrl);
        var candidateBaseUrl = isCustomBaseUrl ? rawBaseUrl! : DefaultBaseUrl;
        var isValidBaseUrl = TryNormalizeBaseUrl(candidateBaseUrl, out var normalizedBaseUrl, out var urlError);
        var effectiveBaseUrl = isValidBaseUrl ? normalizedBaseUrl : candidateBaseUrl;

        string? apiKey = null;
        var apiKeySource = SecretSource.None;
        var apiKeySourceDetail = "Not registered in OS or CLI";
        var keyCameFromAzureEnv = false;

        if (!string.IsNullOrWhiteSpace(explicitApiKey))
        {
            apiKey = explicitApiKey.Trim();
            apiKeySource = SecretSource.CliArgument;
            apiKeySourceDetail = "CLI Argument (--api-key)";
        }
        else if (preferAzureEnvironment)
        {
            var azureKey = readEnv(AzureApiKeyEnvVar);
            var openAiKey = readEnv(ApiKeyEnvVar);
            if (!string.IsNullOrWhiteSpace(azureKey))
            {
                apiKey = azureKey.Trim();
                apiKeySource = SecretSource.EnvironmentVariable;
                apiKeySourceDetail = $"OS Environment Variable ({AzureApiKeyEnvVar})";
                keyCameFromAzureEnv = true;
            }
            else if (!string.IsNullOrWhiteSpace(openAiKey))
            {
                apiKey = openAiKey.Trim();
                apiKeySource = SecretSource.EnvironmentVariable;
                apiKeySourceDetail = $"OS Environment Variable ({ApiKeyEnvVar})";
            }
        }
        else
        {
            var openAiKey = readEnv(ApiKeyEnvVar);
            if (!string.IsNullOrWhiteSpace(openAiKey))
            {
                apiKey = openAiKey.Trim();
                apiKeySource = SecretSource.EnvironmentVariable;
                apiKeySourceDetail = $"OS Environment Variable ({ApiKeyEnvVar})";
            }
            else if (cameFromAzureEndpoint && !string.IsNullOrWhiteSpace(readEnv(AzureApiKeyEnvVar)))
            {
                apiKey = readEnv(AzureApiKeyEnvVar)!.Trim();
                apiKeySource = SecretSource.EnvironmentVariable;
                apiKeySourceDetail = $"OS Environment Variable ({AzureApiKeyEnvVar})";
                keyCameFromAzureEnv = true;
            }
        }

        var wireApi = OpenAiWireApi.ChatCompletions;
        var wireApiSource = "default";
        string? wireApiError = null;

        if (!string.IsNullOrWhiteSpace(explicitWireApi))
        {
            wireApiSource = "CLI --wire-api";
            if (!TryParseWireApi(explicitWireApi, out wireApi))
            {
                wireApiError = $"Invalid OpenAI wire API '{explicitWireApi}'. Valid values: {ValidWireApisDisplay}.";
            }
        }
        else if (!string.IsNullOrWhiteSpace(readEnv(WireApiEnvVar)))
        {
            var envWire = readEnv(WireApiEnvVar)!;
            wireApiSource = $"Env {WireApiEnvVar}";
            if (!TryParseWireApi(envWire, out wireApi))
            {
                wireApiError = $"Invalid {WireApiEnvVar} '{envWire}'. Valid values: {ValidWireApisDisplay}.";
            }
        }
        else if (!string.IsNullOrWhiteSpace(readEnv(ApiModeEnvVar)))
        {
            var envMode = readEnv(ApiModeEnvVar)!;
            wireApiSource = $"Env {ApiModeEnvVar}";
            if (!TryParseWireApi(envMode, out wireApi))
            {
                wireApiError = $"Invalid {ApiModeEnvVar} '{envMode}'. Valid values: {ValidWireApisDisplay}.";
            }
        }
        else if (!string.IsNullOrWhiteSpace(persistedWireApi))
        {
            wireApiSource = "settings.json";
            if (!TryParseWireApi(persistedWireApi, out wireApi))
            {
                wireApiError = $"Invalid persisted openAiWireApi '{persistedWireApi}'. Valid values: {ValidWireApisDisplay}.";
            }
        }
        else if (isValidBaseUrl && IsResponsesEndpointUrl(effectiveBaseUrl))
        {
            wireApi = OpenAiWireApi.Responses;
            wireApiSource = "endpoint path";
        }

        var validationError = urlError ?? wireApiError;
        var isOverallValid = isValidBaseUrl && wireApiError is null;

        var isAzureHost = isValidBaseUrl && IsAzureOpenAiHost(effectiveBaseUrl);
        var isAzureClassic = isValidBaseUrl && IsAzureClassicDeploymentRoute(effectiveBaseUrl);
        var useAzureApiKeyHeader = keyCameFromAzureEnv ||
                                   (isAzureHost && (preferAzureEnvironment || isAzureClassic));
        var azureApiVersion = readEnv(AzureApiVersionEnvVar)?.Trim();
        if (string.IsNullOrWhiteSpace(azureApiVersion) && isAzureClassic)
        {
            azureApiVersion = DefaultAzureApiVersion;
        }

        return new OpenAiGatewayConfiguration(
            BaseUrl: effectiveBaseUrl,
            BaseUrlSource: baseUrlSource,
            ApiKey: apiKey,
            MaskedApiKey: GeminiSecretProvider.MaskKey(apiKey),
            ApiKeySource: apiKeySource,
            ApiKeySourceDetail: apiKeySourceDetail,
            WireApi: wireApi,
            WireApiSource: wireApiSource,
            IsCustomBaseUrl: isCustomBaseUrl,
            IsValidBaseUrl: isOverallValid,
            ValidationError: validationError,
            UseAzureApiKeyHeader: useAzureApiKeyHeader,
            AzureApiVersion: azureApiVersion);
    }

    public static bool TryNormalizeBaseUrl(string? rawBaseUrl, out string normalizedBaseUrl, out string? validationError)
    {
        normalizedBaseUrl = string.Empty;
        validationError = null;

        if (string.IsNullOrWhiteSpace(rawBaseUrl))
        {
            validationError = "OpenAI Gateway base URL cannot be empty.";
            return false;
        }

        var trimmed = rawBaseUrl.Trim();
        if (trimmed.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            validationError = $"Invalid OpenAI Gateway URL '{trimmed}' (expected absolute http:// or https:// URL, e.g. https://api.openai.com/v1).";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            validationError = "OpenAI Gateway URL must not embed username/password credentials; supply API keys via OPENAI_API_KEY or --api-key.";
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty
        };

        var trimmedPath = builder.Path.TrimEnd('/');
        builder.Path = string.IsNullOrEmpty(trimmedPath) ? "/" : trimmedPath;
        normalizedBaseUrl = builder.Uri.AbsoluteUri;
        if (builder.Path == "/" && string.IsNullOrEmpty(builder.Query))
        {
            normalizedBaseUrl = normalizedBaseUrl.TrimEnd('/');
        }

        return true;
    }

    public static bool TryParseWireApi(string? rawValue, out OpenAiWireApi wireApi)
    {
        wireApi = OpenAiWireApi.ChatCompletions;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        switch (rawValue.Trim().ToLowerInvariant())
        {
            case "chat" or "chat-completions" or "chat_completions" or "chat/completions" or "completions":
                wireApi = OpenAiWireApi.ChatCompletions;
                return true;
            case "responses" or "response":
                wireApi = OpenAiWireApi.Responses;
                return true;
            default:
                return false;
        }
    }

    public static OpenAiWireApi ParseWireApiOrThrow(string rawValue, string flagName = "--wire-api")
    {
        if (TryParseWireApi(rawValue, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException($"Invalid OpenAI wire API '{rawValue}' for '{flagName}'. Valid values: {ValidWireApisDisplay}.");
    }

    public static string FormatWireApiCode(OpenAiWireApi wireApi) =>
        wireApi == OpenAiWireApi.Responses ? "responses" : "chat";

    public static Uri BuildGenerationEndpointUri(
        string baseUrl,
        OpenAiWireApi wireApi,
        string modelId,
        string? azureApiVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        if (!TryNormalizeBaseUrl(baseUrl, out var normalized, out var error))
        {
            throw new ArgumentException(error ?? "Invalid OpenAI Gateway base URL.", nameof(baseUrl));
        }

        var baseUri = new Uri(normalized);
        var path = baseUri.AbsolutePath.TrimEnd('/');

        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            if (wireApi == OpenAiWireApi.ChatCompletions)
            {
                return baseUri;
            }

            var swapped = path[..^"/chat/completions".Length] + "/responses";
            return RebuildUriWithPath(baseUri, swapped);
        }

        if (path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
        {
            if (wireApi == OpenAiWireApi.Responses)
            {
                return baseUri;
            }

            var swapped = path[..^"/responses".Length] + "/chat/completions";
            return RebuildUriWithPath(baseUri, swapped);
        }

        if (IsAzureClassicDeploymentRoute(normalized))
        {
            var apiVersion = string.IsNullOrWhiteSpace(azureApiVersion)
                ? DefaultAzureApiVersion
                : azureApiVersion.Trim();

            if (wireApi == OpenAiWireApi.Responses)
            {
                var responsesPath = $"{path}/openai/v1/responses";
                return RebuildUriWithPath(baseUri, responsesPath);
            }

            var deploymentPath = $"{path}/openai/deployments/{Uri.EscapeDataString(modelId.Trim())}/chat/completions";
            return RebuildUriWithPathAndQueryParam(baseUri, deploymentPath, "api-version", apiVersion);
        }

        var targetSuffix = wireApi == OpenAiWireApi.Responses ? "/responses" : "/chat/completions";
        var effectivePath = string.IsNullOrEmpty(path)
            ? $"/v1{targetSuffix}"
            : $"{path}{targetSuffix}";

        return RebuildUriWithPath(baseUri, effectivePath);
    }

    public static Uri BuildModelsEndpointUri(string baseUrl, string? azureApiVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        if (!TryNormalizeBaseUrl(baseUrl, out var normalized, out var error))
        {
            throw new ArgumentException(error ?? "Invalid OpenAI Gateway base URL.", nameof(baseUrl));
        }

        var baseUri = new Uri(normalized);
        var path = baseUri.AbsolutePath.TrimEnd('/');

        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return RebuildUriWithPath(baseUri, path[..^"/chat/completions".Length] + "/models");
        }

        if (path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
        {
            return RebuildUriWithPath(baseUri, path[..^"/responses".Length] + "/models");
        }

        if (IsAzureClassicDeploymentRoute(normalized))
        {
            var apiVersion = string.IsNullOrWhiteSpace(azureApiVersion)
                ? DefaultAzureApiVersion
                : azureApiVersion.Trim();
            return RebuildUriWithPathAndQueryParam(baseUri, $"{path}/openai/models", "api-version", apiVersion);
        }

        var effectivePath = string.IsNullOrEmpty(path)
            ? "/v1/models"
            : $"{path}/models";

        return RebuildUriWithPath(baseUri, effectivePath);
    }

    public string GenerateContent(
        OpenAiGatewayGenerationRequest request,
        OpenAiGatewayConfiguration? resolvedConfig = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var baseUrl = !string.IsNullOrWhiteSpace(request.BaseUrl)
            ? request.BaseUrl.Trim()
            : resolvedConfig?.BaseUrl ?? DefaultBaseUrl;
        var apiKey = !string.IsNullOrWhiteSpace(request.ApiKey)
            ? request.ApiKey.Trim()
            : resolvedConfig?.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "OpenAI Gateway API key is required for live requests. Set OPENAI_API_KEY, pass --api-key <key>, or run with --offline.");
        }

        var wireApi = request.WireApi ?? resolvedConfig?.WireApi ?? OpenAiWireApi.ChatCompletions;
        var endpointUri = BuildGenerationEndpointUri(
            baseUrl,
            wireApi,
            request.ModelId,
            resolvedConfig?.AzureApiVersion);
        var useAzureHeader = resolvedConfig?.UseAzureApiKeyHeader
            ?? (IsAzureOpenAiHost(baseUrl) && IsAzureClassicDeploymentRoute(baseUrl));

        var containsJsonCue =
            request.Prompt.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(request.SystemPrompt) &&
             request.SystemPrompt.Contains("json", StringComparison.OrdinalIgnoreCase));
        var enforceJson = request.EnforceJsonOutput && containsJsonCue;

        var payload = BuildRequestJson(request, wireApi, enforceJson);

        using var httpRequest = CreateRequest(HttpMethod.Post, endpointUri, apiKey, useAzureHeader);
        httpRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var responseBody = Send(httpRequest, FormatWireApiCode(wireApi), request.ModelId.Trim(), cancellationToken);
        using var document = JsonDocument.Parse(responseBody);
        ThrowIfApiErrorObject(document.RootElement);

        var text = wireApi == OpenAiWireApi.Responses
            ? ExtractResponsesText(document.RootElement) ?? ExtractChatCompletionsText(document.RootElement)
            : ExtractChatCompletionsText(document.RootElement) ?? ExtractResponsesText(document.RootElement);

        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        throw new InvalidOperationException(
            "OpenAI Gateway returned an empty text completion. Check the configured model ID and gateway response payload.");
    }

    public IReadOnlyList<OpenAiGatewayModel> ListModels(
        string apiKey,
        string? baseUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var effectiveBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
        var config = ResolveConfiguration(
            environmentReader: _ => null,
            explicitApiKey: apiKey,
            explicitBaseUrl: effectiveBaseUrl);
        return ListModels(config, cancellationToken);
    }

    public IReadOnlyList<OpenAiGatewayModel> ListModels(
        OpenAiGatewayConfiguration config,
        CancellationToken cancellationToken = default) =>
        ListModelCatalog(config, cancellationToken).SupportedModels;

    public OpenAiModelCatalogResult ListModelCatalog(
        OpenAiGatewayConfiguration config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.IsValidBaseUrl)
        {
            throw new InvalidOperationException(config.ValidationError ?? "Invalid OpenAI Gateway base URL.");
        }

        if (!config.HasApiKey)
        {
            throw new InvalidOperationException(
                "OpenAI Gateway API key is required to list gateway models. Set OPENAI_API_KEY or pass --api-key <key>.");
        }

        var endpointUri = BuildModelsEndpointUri(config.BaseUrl, config.AzureApiVersion);
        using var request = CreateRequest(HttpMethod.Get, endpointUri, config.ApiKey!, config.UseAzureApiKeyHeader);
        var responseBody = Send(request, "models", "catalog", cancellationToken);

        using var document = JsonDocument.Parse(responseBody);
        ThrowIfApiErrorObject(document.RootElement);

        var supported = new List<OpenAiGatewayModel>();
        var unsupported = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        if (document.RootElement.TryGetProperty("data", out var dataElements) &&
            dataElements.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataElements.EnumerateArray())
            {
                var id = GetString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var ownedBy = GetString(item, "owned_by");
                if (IsNonChatModelId(id))
                {
                    unsupported.TryAdd(id, ["non-chat"]);
                    continue;
                }

                supported.Add(new OpenAiGatewayModel(
                    ModelId: id,
                    OwnedBy: string.IsNullOrWhiteSpace(ownedBy) ? null : ownedBy));
            }
        }

        return new OpenAiModelCatalogResult(
            SupportedModels: supported
                .DistinctBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            UnsupportedModelCapabilities: unsupported);
    }

    private static string BuildRequestJson(
        OpenAiGatewayGenerationRequest request,
        OpenAiWireApi wireApi,
        bool enforceJson)
    {
        if (wireApi == OpenAiWireApi.Responses)
        {
            var responsesBody = new Dictionary<string, object?>
            {
                ["model"] = request.ModelId.Trim(),
                ["input"] = request.Prompt
            };

            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                responsesBody["instructions"] = request.SystemPrompt.Trim();
            }

            if (enforceJson)
            {
                responsesBody["text"] = new { format = new { type = "json_object" } };
            }

            return JsonSerializer.Serialize(responsesBody, JsonOptions);
        }

        var messages = new List<object>(2);
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new { role = "system", content = request.SystemPrompt.Trim() });
        }

        messages.Add(new { role = "user", content = request.Prompt });

        var chatBody = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId.Trim(),
            ["messages"] = messages
        };

        if (enforceJson)
        {
            chatBody["response_format"] = new { type = "json_object" };
        }

        return JsonSerializer.Serialize(chatBody, JsonOptions);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri endpointUri,
        string apiKey,
        bool useAzureApiKeyHeader)
    {
        var request = new HttpRequestMessage(method, endpointUri);
        if (useAzureApiKeyHeader)
        {
            request.Headers.TryAddWithoutValidation("api-key", apiKey.Trim());
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private string Send(
        HttpRequestMessage request,
        string wireApiLabel,
        string modelId,
        CancellationToken cancellationToken)
    {
        var sanitizedEndpoint = request.RequestUri?.GetLeftPart(UriPartial.Path) ?? "openai-gateway";
        LogEvents.OpenAiHttpRequest(_logger, request.Method.Method, sanitizedEndpoint, wireApiLabel, modelId);

        var sw = Stopwatch.StartNew();
        using var response = _httpClient.SendAsync(request, cancellationToken).GetAwaiter().GetResult();
        var responseBody = response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        sw.Stop();

        LogEvents.OpenAiHttpResponse(
            _logger,
            request.Method.Method,
            sanitizedEndpoint,
            (int)response.StatusCode,
            responseBody.Length,
            sw.ElapsedMilliseconds);

        if (!response.IsSuccessStatusCode)
        {
            var errorMessage = TryExtractErrorMessage(responseBody) ?? responseBody;
            LogEvents.OpenAiHttpError(
                _logger,
                request.Method.Method,
                sanitizedEndpoint,
                (int)response.StatusCode,
                response.ReasonPhrase ?? string.Empty,
                TrimPreview(errorMessage));

            throw new InvalidOperationException(
                $"OpenAI Gateway API request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {errorMessage}");
        }

        return responseBody;
    }

    internal static string? ExtractChatCompletionsText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
            {
                if (message.TryGetProperty("content", out var content))
                {
                    if (content.ValueKind == JsonValueKind.String)
                    {
                        var text = content.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text.Trim();
                        }
                    }
                    else if (content.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.ValueKind == JsonValueKind.String)
                            {
                                sb.Append(part.GetString());
                            }
                            else if (part.ValueKind == JsonValueKind.Object)
                            {
                                var partText = GetString(part, "text");
                                if (string.IsNullOrWhiteSpace(partText))
                                {
                                    partText = GetString(part, "content");
                                }

                                if (!string.IsNullOrWhiteSpace(partText))
                                {
                                    sb.Append(partText);
                                }
                            }
                        }

                        var combined = sb.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(combined))
                        {
                            return combined;
                        }
                    }
                }
            }

            var fallbackText = GetString(choice, "text");
            if (!string.IsNullOrWhiteSpace(fallbackText))
            {
                return fallbackText.Trim();
            }
        }

        return null;
    }

    internal static string? ExtractResponsesText(JsonElement root)
    {
        var topLevelOutputText = GetString(root, "output_text");
        if (!string.IsNullOrWhiteSpace(topLevelOutputText))
        {
            return topLevelOutputText.Trim();
        }

        if (!root.TryGetProperty("output", out var outputArray) || outputArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var item in outputArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("content", out var contentArray) && contentArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var contentItem in contentArray.EnumerateArray())
                {
                    if (contentItem.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (contentItem.TryGetProperty("text", out var textElement))
                    {
                        if (textElement.ValueKind == JsonValueKind.String)
                        {
                            var textVal = textElement.GetString();
                            if (!string.IsNullOrWhiteSpace(textVal))
                            {
                                parts.Add(textVal);
                            }
                        }
                        else if (textElement.ValueKind == JsonValueKind.Object)
                        {
                            var nestedVal = GetString(textElement, "value");
                            if (!string.IsNullOrWhiteSpace(nestedVal))
                            {
                                parts.Add(nestedVal);
                            }
                        }
                    }
                    else
                    {
                        var outputText = GetString(contentItem, "output_text");
                        if (!string.IsNullOrWhiteSpace(outputText))
                        {
                            parts.Add(outputText);
                        }
                    }
                }
            }
        }

        var combined = string.Join(string.Empty, parts).Trim();
        return string.IsNullOrWhiteSpace(combined) ? null : combined;
    }

    private static void ThrowIfApiErrorObject(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var errorElement) ||
            errorElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        var message = errorElement.ValueKind == JsonValueKind.Object
            ? GetString(errorElement, "message")
            : errorElement.ToString();

        if (string.IsNullOrWhiteSpace(message))
        {
            message = errorElement.GetRawText();
        }

        throw new InvalidOperationException($"OpenAI Gateway API error: {message}");
    }

    private static string? TryExtractErrorMessage(string responseBody)
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
                if (err.ValueKind == JsonValueKind.Object)
                {
                    var msg = GetString(err, "message");
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        return msg;
                    }
                }
                else if (err.ValueKind == JsonValueKind.String)
                {
                    return err.GetString();
                }
            }

            var detail = GetString(doc.RootElement, "message");
            return string.IsNullOrWhiteSpace(detail) ? null : detail;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsAzureOpenAiHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        return host.EndsWith(".openai.azure.com", StringComparison.Ordinal) ||
               host.EndsWith(".cognitiveservices.azure.com", StringComparison.Ordinal);
    }

    private static bool IsAzureClassicDeploymentRoute(string url)
    {
        if (!IsAzureOpenAiHost(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return !path.Contains("/openai/v1", StringComparison.OrdinalIgnoreCase) &&
               !path.Contains("/deployments/", StringComparison.OrdinalIgnoreCase) &&
               !path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) &&
               !path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsResponsesEndpointUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.AbsolutePath.TrimEnd('/').EndsWith("/responses", StringComparison.OrdinalIgnoreCase);

    private static Uri RebuildUriWithPath(Uri baseUri, string newPath)
    {
        var builder = new UriBuilder(baseUri)
        {
            Path = newPath
        };
        return builder.Uri;
    }

    private static Uri RebuildUriWithPathAndQueryParam(
        Uri baseUri,
        string newPath,
        string queryParamName,
        string queryParamValue)
    {
        var builder = new UriBuilder(baseUri)
        {
            Path = newPath
        };

        var existingQuery = builder.Query.TrimStart('?');
        var hasParam = existingQuery
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Any(pair => pair.StartsWith(queryParamName + "=", StringComparison.OrdinalIgnoreCase));

        if (!hasParam)
        {
            var addition = $"{Uri.EscapeDataString(queryParamName)}={Uri.EscapeDataString(queryParamValue)}";
            builder.Query = string.IsNullOrWhiteSpace(existingQuery)
                ? addition
                : $"{existingQuery}&{addition}";
        }

        return builder.Uri;
    }

    private static bool IsNonChatModelId(string modelId) =>
        NonChatModelTokens.Any(token => modelId.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string TrimPreview(string text, int maxLength = 240)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return singleLine.Length <= maxLength
            ? singleLine
            : singleLine[..maxLength] + "...";
    }
}
