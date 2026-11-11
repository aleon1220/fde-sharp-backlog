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