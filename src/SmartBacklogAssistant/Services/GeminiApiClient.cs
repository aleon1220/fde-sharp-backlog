using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SmartBacklogAssistant.Services;

public sealed record GeminiModel(
    string ModelId,
    string DisplayName,
    string Description);

public sealed class GeminiApiClient
{
    private const string ApiRoot = "https://generativelanguage.googleapis.com/v1beta/";
    private readonly HttpClient _httpClient;

    public GeminiApiClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public IReadOnlyList<GeminiModel> ListGenerateContentModels(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var models = new List<GeminiModel>();
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
                    if (!SupportsGenerateContent(modelElement))
                    {
                        continue;
                    }

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

                    models.Add(new GeminiModel(
                        ModelId: modelName,
                        DisplayName: GetString(modelElement, "displayName"),
                        Description: GetString(modelElement, "description")));
                }
            }

            pageToken = GetString(root, "nextPageToken");
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return models
            .DistinctBy(model => model.ModelId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string GenerateContent(string apiKey, string modelId, string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var normalizedModelId = modelId.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? modelId["models/".Length..]
            : modelId;
        if (normalizedModelId.Contains('/') || normalizedModelId.Contains('?') || normalizedModelId.Contains('#'))
        {
            throw new ArgumentException("Gemini model must be a model ID, not a URL or resource path.", nameof(modelId));
        }

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
        var candidates = document.RootElement.GetProperty("candidates");
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

        throw new InvalidOperationException("Gemini returned no text candidate. Check the prompt and model response feedback.");
    }

    private static bool SupportsGenerateContent(JsonElement model)
    {
        return model.TryGetProperty("supportedGenerationMethods", out var methods) &&
               methods.ValueKind == JsonValueKind.Array &&
               methods.EnumerateArray().Any(method =>
                   method.ValueKind == JsonValueKind.String &&
                   string.Equals(method.GetString(), "generateContent", StringComparison.OrdinalIgnoreCase));
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