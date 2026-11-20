using System.Net;
using System.Text;
using System.Text.Json;
using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class OpenAiGatewayClientTests
{
    [Fact]
    public void ResolveConfiguration_PrecedenceOrder_PrefersCliOverEnvAndSettings()
    {
        var env = new Dictionary<string, string?>
        {
            [OpenAiGatewayClient.ApiKeyEnvVar] = "sk-env-9999",
            [OpenAiGatewayClient.BaseUrlEnvVar] = "https://env.gateway.internal/v1/",
            [OpenAiGatewayClient.WireApiEnvVar] = "chat"
        };

        var fromCli = OpenAiGatewayClient.ResolveConfiguration(
            environmentReader: key => env.TryGetValue(key, out var val) ? val : null,
            explicitApiKey: "sk-cli-1234",
            explicitBaseUrl: "https://cli.gateway.internal/proxy/v1/",
            persistedBaseUrl: "https://settings.gateway.internal/v1",
            explicitWireApi: "responses",
            persistedWireApi: "chat");

        Assert.True(fromCli.IsValidBaseUrl);
        Assert.True(fromCli.IsConfiguredForLive);
        Assert.True(fromCli.IsCustomBaseUrl);
        Assert.Equal("https://cli.gateway.internal/proxy/v1", fromCli.BaseUrl);
        Assert.Equal("CLI --gateway-url", fromCli.BaseUrlSource);
        Assert.Equal("sk-cli-1234", fromCli.ApiKey);
        Assert.Equal("CLI Argument (--api-key)", fromCli.ApiKeySourceDetail);
        Assert.Equal(SecretSource.CliArgument, fromCli.ApiKeySource);
        Assert.Equal(OpenAiWireApi.Responses, fromCli.WireApi);
        Assert.Equal("responses", fromCli.WireApiCode);
        Assert.Equal("CLI --wire-api", fromCli.WireApiSource);
        Assert.False(fromCli.UseAzureApiKeyHeader);

        var fromEnv = OpenAiGatewayClient.ResolveConfiguration(
            environmentReader: key => env.TryGetValue(key, out var val) ? val : null,
            persistedBaseUrl: "https://settings.gateway.internal/v1",
            persistedWireApi: "responses");

        Assert.Equal("https://env.gateway.internal/v1", fromEnv.BaseUrl);
        Assert.Equal("Env OPENAI_BASE_URL", fromEnv.BaseUrlSource);
        Assert.Equal("sk-env-9999", fromEnv.ApiKey);
        Assert.Equal("Env OPENAI_WIRE_API", fromEnv.WireApiSource);
        Assert.Equal(OpenAiWireApi.ChatCompletions, fromEnv.WireApi);

        var fromSettings = OpenAiGatewayClient.ResolveConfiguration(
            environmentReader: _ => null,
            persistedBaseUrl: "https://settings.gateway.internal/v1",
            persistedWireApi: "responses");

        Assert.Equal("https://settings.gateway.internal/v1", fromSettings.BaseUrl);
        Assert.Equal("settings.json", fromSettings.BaseUrlSource);
        Assert.Equal(OpenAiWireApi.Responses, fromSettings.WireApi);
        Assert.False(fromSettings.IsConfiguredForLive);
    }

    [Fact]
    public void ResolveConfiguration_DetectsAzureEndpointAndResponsesSuffix()
    {
        var azureEnv = new Dictionary<string, string?>
        {
            [OpenAiGatewayClient.AzureEndpointEnvVar] = "https://my-resource.openai.azure.com",
            [OpenAiGatewayClient.AzureApiKeyEnvVar] = "az-secret-5678"
        };

        var azureConfig = OpenAiGatewayClient.ResolveConfiguration(
            environmentReader: key => azureEnv.TryGetValue(key, out var val) ? val : null);

        Assert.True(azureConfig.IsConfiguredForLive);
        Assert.True(azureConfig.UseAzureApiKeyHeader);
        Assert.Equal("https://my-resource.openai.azure.com", azureConfig.BaseUrl);
        Assert.Equal(OpenAiGatewayClient.DefaultAzureApiVersion, azureConfig.AzureApiVersion);
        Assert.Equal("****5678", azureConfig.MaskedApiKey);

        var inferredResponses = OpenAiGatewayClient.ResolveConfiguration(
            environmentReader: _ => null,
            explicitBaseUrl: "https://gateway.corp/openai/v1/responses");

        Assert.True(inferredResponses.IsValidBaseUrl);
        Assert.Equal(OpenAiWireApi.Responses, inferredResponses.WireApi);
        Assert.Equal("endpoint path", inferredResponses.WireApiSource);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/", true, "https://api.openai.com/v1")]
    [InlineData("http://localhost:11434", true, "http://localhost:11434")]
    [InlineData("https://proxy.internal/llm/v1/", true, "https://proxy.internal/llm/v1")]
    [InlineData("", false, "")]
    [InlineData("not-a-url", false, "")]
    [InlineData("ftp://gateway.internal/v1", false, "")]
    [InlineData("https://user:pass@gateway.internal/v1", false, "")]
    public void TryNormalizeBaseUrl_ValidatesAndNormalizesUrls(
        string rawUrl,
        bool expectedValid,
        string expectedNormalized)
    {
        var ok = OpenAiGatewayClient.TryNormalizeBaseUrl(rawUrl, out var normalized, out var error);

        Assert.Equal(expectedValid, ok);
        if (expectedValid)
        {
            Assert.Null(error);
            Assert.Equal(expectedNormalized, normalized);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Fact]
    public void BuildEndpointUris_HandlesStandardCustomAzureAndSwappedPaths()
    {
        Assert.Equal(
            "https://api.openai.com/v1/chat/completions",
            OpenAiGatewayClient.BuildGenerationEndpointUri("https://api.openai.com/v1", OpenAiWireApi.ChatCompletions, "gpt-4o").AbsoluteUri);

        Assert.Equal(
            "https://api.openai.com/v1/responses",
            OpenAiGatewayClient.BuildGenerationEndpointUri("https://api.openai.com/v1", OpenAiWireApi.Responses, "gpt-4o").AbsoluteUri);

        Assert.Equal(
            "https://gateway.internal/v1/chat/completions",
            OpenAiGatewayClient.BuildGenerationEndpointUri("https://gateway.internal", OpenAiWireApi.ChatCompletions, "gpt-4o").AbsoluteUri);

        Assert.Equal(
            "https://proxy.corp/llm/responses",
            OpenAiGatewayClient.BuildGenerationEndpointUri("https://proxy.corp/llm/chat/completions", OpenAiWireApi.Responses, "gpt-4o").AbsoluteUri);

        Assert.Equal(
            "https://contoso.openai.azure.com/openai/deployments/my-gpt4o/chat/completions?api-version=2024-10-21",
            OpenAiGatewayClient.BuildGenerationEndpointUri("https://contoso.openai.azure.com", OpenAiWireApi.ChatCompletions, "my-gpt4o").AbsoluteUri);

        Assert.Equal(
            "https://contoso.openai.azure.com/openai/v1/responses",
            OpenAiGatewayClient.BuildGenerationEndpointUri("https://contoso.openai.azure.com", OpenAiWireApi.Responses, "gpt-4o").AbsoluteUri);

        Assert.Equal(
            "https://gateway.internal/v1/models",
            OpenAiGatewayClient.BuildModelsEndpointUri("https://gateway.internal").AbsoluteUri);

        Assert.Equal(
            "https://proxy.corp/llm/models",
            OpenAiGatewayClient.BuildModelsEndpointUri("https://proxy.corp/llm/responses").AbsoluteUri);

        Assert.Equal(
            "https://contoso.openai.azure.com/openai/models?api-version=2024-10-21",
            OpenAiGatewayClient.BuildModelsEndpointUri("https://contoso.openai.azure.com").AbsoluteUri);
    }

    [Fact]
    public void GenerateContent_ChatCompletions_SendsBearerHeaderAndJsonResponseFormat()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            captured = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            const string responseJson = """
                {
                  "id": "chatcmpl-123",
                  "choices": [
                    {
                      "message": {
                        "role": "assistant",
                        "content": "{\"Title\":\"Gateway Ticket\",\"Description\":\"Live chat completion\"}"
                      }
                    }
                  ]
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        });

        var client = new OpenAiGatewayClient(new HttpClient(handler));
        var result = client.GenerateContent(new OpenAiGatewayGenerationRequest(
            ModelId: "gpt-4o",
            Prompt: "Respond strictly as JSON for this backlog goal.",
            SystemPrompt: "Senior FDE",
            ApiKey: "sk-live-secret-1234",
            BaseUrl: "https://gateway.internal/v1",
            WireApi: OpenAiWireApi.ChatCompletions));

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("https://gateway.internal/v1/chat/completions", captured.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("sk-live-secret-1234", captured.Headers.Authorization?.Parameter);

        using var payload = JsonDocument.Parse(capturedBody!);
        Assert.Equal("gpt-4o", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_object", payload.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Contains("Gateway Ticket", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateContent_ResponsesApiAndArrayParts_SendsResponsesPayloadAndExtractsOutputText()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            captured = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            const string responseJson = """
                {
                  "id": "resp-456",
                  "output": [
                    {
                      "type": "message",
                      "content": [
                        {
                          "type": "output_text",
                          "text": { "value": "{\"Title\":\"Responses Ticket\"," }
                        },
                        {
                          "type": "output_text",
                          "text": "\"Description\":\"From /v1/responses\"}"
                        }
                      ]
                    }
                  ]
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        });

        var azureConfig = OpenAiGatewayClient.ResolveConfiguration(
            environmentReader: key => key switch
            {
                OpenAiGatewayClient.AzureEndpointEnvVar => "https://acme.openai.azure.com",
                OpenAiGatewayClient.AzureApiKeyEnvVar => "az-live-8888",
                OpenAiGatewayClient.WireApiEnvVar => "responses",
                _ => null
            });

        var client = new OpenAiGatewayClient(new HttpClient(handler));
        var result = client.GenerateContent(
            new OpenAiGatewayGenerationRequest(
                ModelId: "gpt-4.1-mini",
                Prompt: "Generate JSON outcome"),
            azureConfig);

        Assert.NotNull(captured);
        Assert.Equal("https://acme.openai.azure.com/openai/v1/responses", captured!.RequestUri!.AbsoluteUri);
        Assert.Null(captured.Headers.Authorization);
        Assert.True(captured.Headers.TryGetValues("api-key", out var apiKeyValues));
        Assert.Equal("az-live-8888", apiKeyValues.Single());

        using var payload = JsonDocument.Parse(capturedBody!);
        Assert.Equal("gpt-4.1-mini", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("Generate JSON outcome", payload.RootElement.GetProperty("input").GetString());
        Assert.Equal("json_object", payload.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("{\"Title\":\"Responses Ticket\",\"Description\":\"From /v1/responses\"}", result);
    }

    [Fact]
    public void GenerateContent_HttpFailureOrMissingKey_ThrowsClearException()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            ReasonPhrase = "Unauthorized",
            Content = new StringContent("""{"error":{"message":"Incorrect API key provided"}}""", Encoding.UTF8, "application/json")
        });

        var client = new OpenAiGatewayClient(new HttpClient(handler));

        Assert.Throws<InvalidOperationException>(() =>
            client.GenerateContent(new OpenAiGatewayGenerationRequest("gpt-4o", "Test prompt", ApiKey: null)));

        var httpEx = Assert.Throws<InvalidOperationException>(() =>
            client.GenerateContent(new OpenAiGatewayGenerationRequest("gpt-4o", "Test prompt", ApiKey: "sk-bad")));
        Assert.Contains("401", httpEx.Message, StringComparison.Ordinal);
        Assert.Contains("Incorrect API key provided", httpEx.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ListModelCatalog_QueriesModelsEndpointAndFiltersNonChatModels()
    {
        Uri? capturedUri = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedUri = request.RequestUri;
            const string modelsJson = """
                {
                  "object": "list",
                  "data": [
                    { "id": "gpt-4.1-mini", "owned_by": "openai" },
                    { "id": "gpt-4o", "owned_by": "openai" },
                    { "id": "text-embedding-3-large", "owned_by": "openai" },
                    { "id": "whisper-1", "owned_by": "openai" }
                  ]
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(modelsJson, Encoding.UTF8, "application/json")
            };
        });

        var config = OpenAiGatewayClient.ResolveConfiguration(
            environmentReader: _ => null,
            explicitApiKey: "sk-catalog-1234",
            explicitBaseUrl: "https://gateway.internal/custom/v1");

        var client = new OpenAiGatewayClient(new HttpClient(handler));
        var catalog = client.ListModelCatalog(config);

        Assert.Equal("https://gateway.internal/custom/v1/models", capturedUri?.AbsoluteUri);
        Assert.Equal(2, catalog.SupportedModels.Count);
        Assert.Contains(catalog.SupportedModels, m => m.ModelId == "gpt-4o" && m.OwnedBy == "openai");
        Assert.Contains(catalog.SupportedModels, m => m.ModelId == "gpt-4.1-mini");
        Assert.True(catalog.UnsupportedModelCapabilities.ContainsKey("text-embedding-3-large"));
        Assert.True(catalog.UnsupportedModelCapabilities.ContainsKey("whisper-1"));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request, cancellationToken));
    }
}
