using System.Net;
using System.Text;
using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class GeminiApiClientTests
{
    [Fact]
    public void ListGenerateContentModels_FiltersModelsBySupportedMethod()
    {
        using var httpClient = CreateHttpClient(_ => JsonResponse("""
            {
              "models": [
                {
                  "name": "models/gemini-text",
                  "baseModelId": "gemini-text",
                  "displayName": "Gemini Text",
                  "description": "Text model",
                  "supportedGenerationMethods": ["generateContent"]
                },
                {
                  "name": "models/gemini-embed",
                  "baseModelId": "gemini-embed",
                  "displayName": "Gemini Embed",
                  "supportedGenerationMethods": ["embedContent"]
                }
              ]
            }
            """));

        var models = new GeminiApiClient(httpClient).ListGenerateContentModels("test-key");

        var model = Assert.Single(models);
        Assert.Equal("gemini-text", model.ModelId);
        Assert.Equal("Gemini Text", model.DisplayName);
    }

    [Fact]
    public void ListModelCatalog_CapturesSupportedAndUnsupportedGenerationMethods()
    {
        using var httpClient = CreateHttpClient(_ => JsonResponse("""
            {
              "models": [
                {
                  "name": "models/gemini-2.5-flash",
                  "baseModelId": "gemini-2.5-flash",
                  "displayName": "Gemini 2.5 Flash",
                  "supportedGenerationMethods": ["generateContent"]
                },
                {
                  "name": "models/gemini-embedding-001",
                  "baseModelId": "gemini-embedding-001",
                  "displayName": "Gemini Embedding",
                  "supportedGenerationMethods": ["embedContent"]
                }
              ]
            }
            """));

        var catalog = new GeminiApiClient(httpClient).ListModelCatalog("test-key");

        var supported = Assert.Single(catalog.SupportedModels);
        Assert.Equal("gemini-2.5-flash", supported.ModelId);
        Assert.True(catalog.UnsupportedModelMethods.TryGetValue("gemini-embedding-001", out var methods));
        Assert.Equal(["embedContent"], methods);
    }

    [Fact]
    public void GenerateContent_SendsSelectedModelAndReturnsCandidateText()
    {
        HttpMethod? capturedMethod = null;
        Uri? capturedUri = null;
        string? capturedApiKey = null;
        string? capturedRequestBody = null;
        using var httpClient = CreateHttpClient(request =>
        {
            capturedMethod = request.Method;
            capturedUri = request.RequestUri;
            capturedApiKey = request.Headers.GetValues("x-goog-api-key").Single();
            capturedRequestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""
                {
                  "candidates": [
                    { "content": { "parts": [{ "text": "{\"title\":\"Generated\"}" }] } }
                  ]
                }
                """);
        });
        var generated = new GeminiApiClient(httpClient).GenerateContent("test-key", "gemini-text", "Draft this backlog item");

        Assert.Contains("Generated", generated);
        Assert.Equal(HttpMethod.Post, capturedMethod);
        Assert.EndsWith("/v1beta/models/gemini-text:generateContent", capturedUri!.AbsolutePath);
        Assert.Equal("test-key", capturedApiKey);
        Assert.Contains("Draft this backlog item", capturedRequestBody);
        Assert.Contains("application/json", capturedRequestBody);
    }

    [Fact]
    public void GatherWebGrounding_UsesGoogleSearchToolWithoutJsonMimeTypeAndDeduplicatesSources()
    {
        string? capturedRequestBody = null;
        using var httpClient = CreateHttpClient(request =>
        {
            capturedRequestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""
                {
                  "candidates": [{
                    "content": {
                      "parts": [{ "text": "- Official 2026 Geocaching theme covers cinema genres." }]
                    },
                    "groundingMetadata": {
                      "groundingChunks": [
                        { "web": { "title": "Geocaching Blog", "uri": "https://www.geocaching.com/blog/" } },
                        { "web": { "title": "Duplicate", "uri": "https://www.geocaching.com/blog/" } },
                        { "web": { "title": "HQ Rules", "uri": "https://www.geocaching.com/help/" } }
                      ]
                    }
                  }]
                }
                """);
        });

        var result = new GeminiApiClient(httpClient).GatherWebGrounding(
            "test-key",
            "gemini-2.5-flash",
            "Check Geocaching 2026 hiding movie theme");

        Assert.True(result.Succeeded);
        Assert.Contains("cinema genres", result.Summary);
        Assert.Equal(2, result.Sources.Count);
        Assert.Equal("Geocaching Blog", result.Sources[0].Title);
        Assert.Contains("google_search", capturedRequestBody);
        Assert.DoesNotContain("responseMimeType", capturedRequestBody);
    }

    [Fact]
    public void GatherWebGrounding_OnToolBadRequestReturnsGracefulFailure_WhileUnauthorizedThrows()
    {
        using var badRequestClient = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            ReasonPhrase = "Bad Request",
            Content = new StringContent("""{"error":"tool unsupported"}""", Encoding.UTF8, "application/json")
        });

        var skipped = new GeminiApiClient(badRequestClient).GatherWebGrounding("test-key", "gemini-2.5-flash", "topic");
        Assert.False(skipped.Succeeded);
        Assert.Empty(skipped.Sources);
        Assert.Contains("HTTP 400", skipped.Warning);

        using var unauthorizedClient = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            ReasonPhrase = "Unauthorized",
            Content = new StringContent("""{"error":"invalid api key"}""", Encoding.UTF8, "application/json")
        });

        Assert.Throws<InvalidOperationException>(() =>
            new GeminiApiClient(unauthorizedClient).GatherWebGrounding("bad-key", "gemini-2.5-flash", "topic"));
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var handler = new StubHttpMessageHandler(responseFactory);
        return new HttpClient(handler);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}