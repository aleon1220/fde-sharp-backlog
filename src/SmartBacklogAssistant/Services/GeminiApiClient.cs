using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SmartBacklogAssistant.Services;

public sealed record GeminiModel(
    string ModelId,
    string DisplayName,
    string Description);

public sealed record GeminiModelCatalogResult(
    IReadOnlyList<GeminiModel> SupportedModels,
    IReadOnlyDictionary<string, IReadOnlyList<string>> UnsupportedModelMethods);

public sealed record GeminiWebSource(
    string Title,
    string Uri);

public sealed record GeminiWebGroundingResult(
    string Summary,
    IReadOnlyList<GeminiWebSource> Sources,
    bool Succeeded,
    string? Warning = null);

public sealed class GeminiApiClient
{
    private const string ApiRoot = "https://generativelanguage.googleapis.com/v1beta/";
    private readonly HttpClient _httpClient;

    public GeminiApiClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public IReadOnlyList<GeminiModel> ListGenerateContentModels(string apiKey) =>
        ListModelCatalog(apiKey).SupportedModels;

    public GeminiModelCatalogResult ListModelCatalog(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var models = new List<GeminiModel>();
        var unsupportedMethods = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        string? pageToken = null;

        do
        {
            var path = "models?pageSize=1000";
            if (!string.IsNullOrWhiteSpace(pageToken))
            {
                path += $"&pageToken={Uri.EscapeDataString(pageToken)}";
            }

            using var request = CreateRequest(HttpMethod.Get, path, apiKey);
            var response = Send(request);
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;

            if (root.TryGetProperty("models", out var modelElements) && modelElements.ValueKind == JsonValueKind.Array)
            {
                foreach (var modelElement in modelElements.EnumerateArray())
                {
                    var modelName = GetString(modelElement, "baseModelId");
                    if (string.IsNullOrWhiteSpace(modelName))
                    {
                        modelName = GetString(modelElement, "name");
                        if (modelName.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                        {
                            modelName = modelName["models/".Length..];
                        }
                    }

                    if (string.IsNullOrWhiteSpace(modelName))
                    {
                        continue;
                    }

                    var methods = ExtractGenerationMethods(modelElement);
                    if (!methods.Any(m => string.Equals(m, "generateContent", StringComparison.OrdinalIgnoreCase)))
                    {
                        unsupportedMethods.TryAdd(modelName, methods);
                        continue;
                    }

                    models.Add(new GeminiModel(
                        ModelId: modelName,
                        DisplayName: GetString(modelElement, "displayName"),
                        Description: GetString(modelElement, "description")));
                }
            }

            pageToken = GetString(root, "nextPageToken");
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return new GeminiModelCatalogResult(
            SupportedModels: models
                .DistinctBy(model => model.ModelId, StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(model => model.ModelId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            UnsupportedModelMethods: unsupportedMethods);
    }

    public string GenerateContent(string apiKey, string modelId, string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var normalizedModelId = NormalizeAndValidateModelId(modelId);

        var requestBody = JsonSerializer.Serialize(new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = prompt } } }
            },
            generationConfig = new { responseMimeType = "application/json" }
        });

        using var request = CreateRequest(
            HttpMethod.Post,
            $"models/{Uri.EscapeDataString(normalizedModelId)}:generateContent",
            apiKey);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        var response = Send(request);
        using var document = JsonDocument.Parse(response);
        var text = ExtractCandidateText(document.RootElement);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        throw new InvalidOperationException("Gemini returned no text candidate. Check the prompt and model response feedback.");
    }

    /// <summary>
    /// Runs a dedicated Google Search grounded pass (without responseMimeType = application/json,
    /// avoiding known v1beta REST conflicts between structured output mode and Search grounding).
    /// </summary>
    public GeminiWebGroundingResult GatherWebGrounding(string apiKey, string modelId, string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        var normalizedModelId = NormalizeAndValidateModelId(modelId);
        var searchPrompt =
            "Use Google Search to gather factual, current, and accurate information for the following backlog topic or verification goal. " +
            "Summarize key facts, official naming/themes, dates, rules, constraints, and items needing verification concisely in clear bullet points:\n\n" +
            topic.Trim();

        var requestBody = JsonSerializer.Serialize(new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = searchPrompt } } }
            },
            tools = new[]
            {
                new { google_search = new { } }
            }
        });

        using var request = CreateRequest(
            HttpMethod.Post,
            $"models/{Uri.EscapeDataString(normalizedModelId)}:generateContent",
            apiKey);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var response = _httpClient.SendAsync(request).GetAwaiter().GetResult();
        var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is 401 or 403)
            {
                throw new InvalidOperationException(
                    $"Gemini API request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {responseBody}");
            }

            return new GeminiWebGroundingResult(
                Summary: string.Empty,
                Sources: [],
                Succeeded: false,
                Warning: $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})");
        }

        using var document = JsonDocument.Parse(responseBody);
        var summary = ExtractCandidateText(document.RootElement) ?? string.Empty;
        var sources = ExtractGroundingSources(document.RootElement);

        return new GeminiWebGroundingResult(
            Summary: summary.Trim(),
            Sources: sources,
            Succeeded: !string.IsNullOrWhiteSpace(summary),
            Warning: string.IsNullOrWhiteSpace(summary) ? "No grounded summary text returned by model." : null);
    }

    private static string NormalizeAndValidateModelId(string modelId)
    {
        var normalizedModelId = modelId.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? modelId["models/".Length..]
            : modelId;
        if (normalizedModelId.Contains('/') || normalizedModelId.Contains('?') || normalizedModelId.Contains('#'))
        {
            throw new ArgumentException("Gemini model must be a model ID, not a URL or resource path.", nameof(modelId));
        }

        return normalizedModelId;
    }

    private static string? ExtractCandidateText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var text = string.Join("", parts.EnumerateArray()
                .Select(part => GetString(part, "text"))
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static IReadOnlyList<GeminiWebSource> ExtractGroundingSources(JsonElement root)
    {
        var sources = new List<GeminiWebSource>();
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
        {
            return sources;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("groundingMetadata", out var metadata) ||
                !metadata.TryGetProperty("groundingChunks", out var chunks) ||
                chunks.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var chunk in chunks.EnumerateArray())
            {
                if (!chunk.TryGetProperty("web", out var web) || web.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var uri = GetString(web, "uri");
                if (string.IsNullOrWhiteSpace(uri))
                {
                    continue;
                }

                var title = GetString(web, "title");
                sources.Add(new GeminiWebSource(
                    Title: string.IsNullOrWhiteSpace(title) ? uri : title,
                    Uri: uri));
            }
        }

        return sources
            .DistinctBy(s => s.Uri, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractGenerationMethods(JsonElement model)
    {
        if (!model.TryGetProperty("supportedGenerationMethods", out var methods) ||
            methods.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return methods.EnumerateArray()
            .Where(m => m.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(m.GetString()))
            .Select(m => m.GetString()!)
            .ToArray();
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string apiKey)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(ApiRoot), path));
        request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private string Send(HttpRequestMessage request)
    {
        using var response = _httpClient.SendAsync(request).GetAwaiter().GetResult();
        var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Gemini API request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {responseBody}");
        }

        return responseBody;
    }
}