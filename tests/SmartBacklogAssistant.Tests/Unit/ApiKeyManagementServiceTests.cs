using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class ApiKeyManagementServiceTests
{
    [Theory]
    [InlineData("gemini", AiProviderType.GoogleGemini)]
    [InlineData("1", AiProviderType.GoogleGemini)]
    [InlineData("openai", AiProviderType.OpenAiGateway)]
    [InlineData("openai-gateway", AiProviderType.OpenAiGateway)]
    [InlineData("2", AiProviderType.OpenAiGateway)]
    [InlineData("azure", AiProviderType.AzureOpenAi)]
    [InlineData("azure-openai", AiProviderType.AzureOpenAi)]
    [InlineData("foundry", AiProviderType.AzureOpenAi)]
    [InlineData("3", AiProviderType.AzureOpenAi)]
    [InlineData("claude", AiProviderType.AnthropicClaude)]
    [InlineData("anthropic", AiProviderType.AnthropicClaude)]
    [InlineData("4", AiProviderType.AnthropicClaude)]
    [InlineData("copilot-sdk", AiProviderType.GitHubCopilot)]
    [InlineData("copilot", AiProviderType.GitHubCopilot)]
    [InlineData("5", AiProviderType.GitHubCopilot)]
    public void TryResolveKeyProvider_SupportsAliasesAndNumericMenuIndices(string input, AiProviderType expected)
    {
        var resolved = ApiKeyManagementService.TryResolveKeyProvider(input, out var actual);

        Assert.True(resolved);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void TryResolveKeyProvider_RejectsUnsupportedProviders(string? input)
    {
        var resolved = ApiKeyManagementService.TryResolveKeyProvider(input, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void ValidateKeyFormat_AcceptsValidKeysForAllFiveProvidersAndMasksToLast4()
    {
        var store = new SessionEnvironmentStore(externalReader: _ => null);
        var service = new ApiKeyManagementService(store);

        var cases = new (AiProviderType Provider, string Key, string ExpectedMask)[]
        {
            (AiProviderType.GoogleGemini, "AIzaSyValidKey1234567890ABCD", "****ABCD"),
            (AiProviderType.OpenAiGateway, "sk-proj-ValidOpenAiKey987654WXYZ", "****WXYZ"),
            (AiProviderType.AzureOpenAi, "9f8a7b6c5d4e3f2a1b0c9d8eAZUREKEY", "****EKEY"),
            (AiProviderType.AnthropicClaude, "sk-ant-api03-ValidClaudeSecretKey7788", "****7788"),
            (AiProviderType.GitHubCopilot, "ghp_ValidGitHubCopilotToken12345690", "****5690"),
        };

        foreach (var (provider, key, expectedMask) in cases)
        {
            var result = service.ValidateKeyFormat(provider, key);
            Assert.True(result.IsValid, $"Expected {provider} key to be valid, got: {result.ErrorMessage}");
            Assert.Equal(expectedMask, result.MaskedKey);
            Assert.Equal(key, result.NormalizedKey);
            Assert.False(result.IsPlaceholder);
            Assert.False(result.IsCrossProviderMismatch);
        }
    }

    [Theory]
    [InlineData(AiProviderType.GoogleGemini, "YOUR_API_KEY", true)]
    [InlineData(AiProviderType.GoogleGemini, "****1234", true)]
    [InlineData(AiProviderType.GoogleGemini, "AIza...1234", true)]
    [InlineData(AiProviderType.OpenAiGateway, "Bearer sk-proj-ValidKey12345", false)]
    [InlineData(AiProviderType.OpenAiGateway, "OPENAI_API_KEY=sk-proj-ValidKey12345", false)]
    [InlineData(AiProviderType.AnthropicClaude, "<sk-ant-your-key-here>", true)]
    [InlineData(AiProviderType.AzureOpenAi, "https://contoso.openai.azure.com", false)]
    [InlineData(AiProviderType.GitHubCopilot, "ghp_aaaaaaaaaaaaaaaa", true)]
    [InlineData(AiProviderType.GoogleGemini, "not-a-gemini-key", false)]
    [InlineData(AiProviderType.GitHubCopilot, "plain_invalid_token_1234", false)]
    public void ValidateKeyFormat_RejectsPlaceholdersPrefixesUrlsAndMalformedSyntax(
        AiProviderType provider,
        string badKey,
        bool expectedPlaceholder)
    {
        var store = new SessionEnvironmentStore(externalReader: _ => null);
        var service = new ApiKeyManagementService(store);

        var result = service.ValidateKeyFormat(provider, badKey);

        Assert.False(result.IsValid);
        Assert.Equal(expectedPlaceholder, result.IsPlaceholder);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.False(string.IsNullOrWhiteSpace(result.ActionableHint));
    }

    [Theory]
    [InlineData(AiProviderType.OpenAiGateway, "AIzaSyGeminiKeyInstead123456", AiProviderType.GoogleGemini)]
    [InlineData(AiProviderType.OpenAiGateway, "sk-ant-ClaudeKeyInstead123456", AiProviderType.AnthropicClaude)]
    [InlineData(AiProviderType.GoogleGemini, "sk-proj-OpenAiKeyInstead123456", AiProviderType.OpenAiGateway)]
    [InlineData(AiProviderType.AnthropicClaude, "ghp_CopilotTokenInstead123456", AiProviderType.GitHubCopilot)]
    [InlineData(AiProviderType.AzureOpenAi, "github_pat_PatTokenInstead1234567890", AiProviderType.GitHubCopilot)]
    public void ValidateKeyFormat_DetectsCrossProviderKeyMismatchesBeforeSaving(
        AiProviderType targetProvider,
        string foreignKey,
        AiProviderType expectedMismatchedProvider)
    {
        var store = new SessionEnvironmentStore(externalReader: _ => null);
        var service = new ApiKeyManagementService(store);

        var result = service.SetKey(targetProvider, foreignKey, ApiKeyStorageScope.Session);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FormatValidation);
        Assert.True(result.FormatValidation.IsCrossProviderMismatch);
        Assert.Equal(expectedMismatchedProvider, result.FormatValidation.DetectedMismatchedProvider);
        Assert.Contains("Mismatched provider key", result.Message);
        Assert.DoesNotContain(foreignKey, result.Message);
        Assert.Null(store.Read(ApiKeyManagementService.GetMetadata(targetProvider).PrimaryEnvVar));
    }

    [Fact]
    public void ValidateKeyFormat_OpenAiCustomGatewayUrlAllowsNonSkGatewayToken()
    {
        var store = new SessionEnvironmentStore(externalReader: _ => null);
        var service = new ApiKeyManagementService(store);

        var standardResult = service.ValidateKeyFormat(AiProviderType.OpenAiGateway, "gw-token-1234567890");
        Assert.False(standardResult.IsValid);

        var customGatewayResult = service.ValidateKeyFormat(
            AiProviderType.OpenAiGateway,
            "gw-token-1234567890",
            explicitOpenAiBaseUrl: "https://gateway.internal/v1");
        Assert.True(customGatewayResult.IsValid);
        Assert.Equal("****7890", customGatewayResult.MaskedKey);
    }

    [Fact]
    public void VerifyKeyLive_AllFiveProvidersSucceedAndReportMaskedConfirmations()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [OpenAiGatewayClient.AzureEndpointEnvVar] = "https://foundry-test.openai.azure.com",
            ["ANTHROPIC_BASE_URL"] = "https://anthropic.stub.test"
        };
        var store = new SessionEnvironmentStore(externalReader: key => env.TryGetValue(key, out var val) ? val : null);
        var redactor = new SecretRedactor();

        var geminiHandler = new StubHttpMessageHandler(_ => JsonResponse("""
            {
              "models": [
                {
                  "name": "models/gemini-2.5-flash",
                  "baseModelId": "gemini-2.5-flash",
                  "displayName": "Gemini 2.5 Flash",
                  "supportedGenerationMethods": ["generateContent"]
                }
              ]
            }
            """));

        var openAiHandler = new StubHttpMessageHandler(req =>
        {
            if (req.RequestUri!.Host.Contains("azure", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(req.Headers.Contains("api-key"));
                return JsonResponse("""{"data":[{"id":"gpt-4o","owned_by":"azure"}]}""");
            }

            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            return JsonResponse("""{"data":[{"id":"gpt-4.1-mini","owned_by":"openai"}]}""");
        });

        var verificationHandler = new StubHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("/v1/models", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(req.Headers.Contains("x-api-key"));
                return JsonResponse("""{"data":[{"id":"claude-sonnet-4-5","display_name":"Claude Sonnet 4.5"}]}""");
            }

            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            return JsonResponse("""{"login":"fde-verifier"}""");
        });

        var service = new ApiKeyManagementService(
            store,
            geminiApiClient: new GeminiApiClient(new HttpClient(geminiHandler)),
            openAiGatewayClient: new OpenAiGatewayClient(new HttpClient(openAiHandler)),
            copilotGateway: new StubCopilotSdkGateway(
                runtimeAvailable: false,
                models: []),
            verificationHttpClient: new HttpClient(verificationHandler),
            secretRedactor: redactor);

        var gemini = service.VerifyKeyLive(AiProviderType.GoogleGemini, "AIzaSyGeminiLiveTestKey12345");
        var openai = service.VerifyKeyLive(AiProviderType.OpenAiGateway, "sk-proj-OpenAiLiveTestKey67890");
        var azure = service.VerifyKeyLive(AiProviderType.AzureOpenAi, "AzureLivePortalSecretKey99112233");
        var claude = service.VerifyKeyLive(AiProviderType.AnthropicClaude, "sk-ant-ClaudeLiveTestKey4455");
        var copilot = service.VerifyKeyLive(AiProviderType.GitHubCopilot, "gho_CopilotLiveTestToken7788");

        Assert.True(gemini.Succeeded);
        Assert.Equal("****2345", gemini.MaskedKey);
        Assert.Equal(1, gemini.AccessibleModelCount);

        Assert.True(openai.Succeeded);
        Assert.Equal("****7890", openai.MaskedKey);
        Assert.Equal(1, openai.AccessibleModelCount);

        Assert.True(azure.Succeeded);
        Assert.Equal("****2233", azure.MaskedKey);
        Assert.Contains("foundry-test.openai.azure.com", azure.Summary);

        Assert.True(claude.Succeeded);
        Assert.Equal("****4455", claude.MaskedKey);
        Assert.Equal(1, claude.AccessibleModelCount);

        Assert.True(copilot.Succeeded);
        Assert.Equal("****7788", copilot.MaskedKey);
        Assert.Contains("@fde-verifier", copilot.Summary);
    }

    [Fact]
    public void VerifyKeyLive_WhenProviderReturns401ReflectingSecret_RedactsSecretAndLogsOnlyMasked()
    {
        var rawSecret = "sk-ant-SecretEchoedIn401Error9999";
        using var errWriter = new StringWriter();
        using var loggerFactory = new StderrLoggerFactory(
            errWriter,
            minimumLevel: LogLevel.Information,
            outputFormat: LogOutputFormat.Text,
            enableColor: false);
        var redactor = loggerFactory.Redactor;
        var store = new SessionEnvironmentStore(externalReader: _ => null);

        var failingHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                $$"""{ "error": { "message": "Invalid key provided: {{rawSecret}}" } }""",
                Encoding.UTF8,
                "application/json")
        });

        var service = new ApiKeyManagementService(
            store,
            verificationHttpClient: new HttpClient(failingHandler),
            secretRedactor: redactor,
            loggerFactory: loggerFactory);

        var verifyResult = service.SetKey(
            AiProviderType.AnthropicClaude,
            rawSecret,
            ApiKeyStorageScope.Session,
            verifyLive: true);

        Assert.False(verifyResult.Succeeded);
        Assert.NotNull(verifyResult.LiveVerification);
        Assert.False(verifyResult.LiveVerification.Succeeded);
        Assert.Equal(401, verifyResult.LiveVerification.HttpStatusCode);
        Assert.DoesNotContain(rawSecret, verifyResult.Message);
        Assert.DoesNotContain(rawSecret, verifyResult.LiveVerification.ErrorMessage ?? string.Empty);
        Assert.Null(store.Read("ANTHROPIC_API_KEY"));

        var stderrLogs = errWriter.ToString();
        Assert.DoesNotContain(rawSecret, stderrLogs);
        Assert.Contains("****9999", stderrLogs);
    }

    [Fact]
    public void SetAndClearKey_RespectsSessionVsWindowsUserScopeAndProtectsGlobalGhTokenInUserScope()
    {
        var processEnv = new Dictionary<string, string?>(StringComparer.Ordinal);
        var userEnv = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [GitHubCopilotSdkGateway.GitHubTokenEnvVar] = "ghp_GlobalGhCliTokenDoNotTouch1234"
        };

        var store = new SessionEnvironmentStore(
            externalScopedWriter: (name, value, target) =>
            {
                var dict = target == EnvironmentVariableTarget.User ? userEnv : processEnv;
                if (value is null)
                {
                    dict.Remove(name);
                }
                else
                {
                    dict[name] = value;
                }
            },
            externalScopedReader: (name, target) =>
            {
                var dict = target == EnvironmentVariableTarget.User ? userEnv : processEnv;
                return dict.TryGetValue(name, out var val) ? val : null;
            },
            isWindowsOverride: true);

        var service = new ApiKeyManagementService(store);

        // Session-only set keeps User env untouched
        var sessionSet = service.SetKey(AiProviderType.OpenAiGateway, "sk-proj-SessionKeyOnly1122", ApiKeyStorageScope.Session);
        Assert.True(sessionSet.Succeeded);
        Assert.False(sessionSet.PersistedToOsUserEnvironment);
        Assert.Equal("sk-proj-SessionKeyOnly1122", store.Read("OPENAI_API_KEY"));
        Assert.False(userEnv.ContainsKey("OPENAI_API_KEY"));

        // User set persists both Session (Process) + User env
        var userSet = service.SetKey(AiProviderType.OpenAiGateway, "sk-proj-UserPersistedKey3344", ApiKeyStorageScope.User);
        Assert.True(userSet.Succeeded);
        Assert.True(userSet.PersistedToOsUserEnvironment);
        Assert.Equal("sk-proj-UserPersistedKey3344", userEnv["OPENAI_API_KEY"]);
        Assert.Equal("Session + OS User Env", store.DescribeVariableScope("OPENAI_API_KEY").ScopeSummary);

        // Session-only clear removes from active session while leaving User env intact
        var clearSessionOnly = service.ClearKey(AiProviderType.OpenAiGateway, ApiKeyStorageScope.Session);
        Assert.True(clearSessionOnly.Succeeded);
        Assert.False(clearSessionOnly.PersistedToOsUserEnvironment);
        Assert.Null(store.Read("OPENAI_API_KEY"));
        Assert.Equal("sk-proj-UserPersistedKey3344", userEnv["OPENAI_API_KEY"]);
        Assert.Contains("--scope all", clearSessionOnly.ScopeGuidanceNote ?? string.Empty);

        // All-scope clear wipes both Session and OS User env
        var clearAll = service.ClearKey(AiProviderType.OpenAiGateway, ApiKeyStorageScope.All);
        Assert.True(clearAll.Succeeded);
        Assert.True(clearAll.PersistedToOsUserEnvironment);
        Assert.False(userEnv.ContainsKey("OPENAI_API_KEY"));

        // Copilot token clear removes COPILOT_GITHUB_TOKEN from User env + masks session GITHUB_TOKEN,
        // without deleting User-level GITHUB_TOKEN used by 'gh' CLI.
        var setCopilot = service.SetKey(AiProviderType.GitHubCopilot, "gho_CopilotDedicatedToken5566", ApiKeyStorageScope.User);
        Assert.True(setCopilot.Succeeded);
        Assert.Equal("gho_CopilotDedicatedToken5566", userEnv[GitHubCopilotSdkGateway.CopilotTokenEnvVar]);

        var clearCopilotAll = service.ClearKey(AiProviderType.GitHubCopilot, ApiKeyStorageScope.All);
        Assert.True(clearCopilotAll.Succeeded);
        Assert.False(userEnv.ContainsKey(GitHubCopilotSdkGateway.CopilotTokenEnvVar));
        Assert.Equal("ghp_GlobalGhCliTokenDoNotTouch1234", userEnv[GitHubCopilotSdkGateway.GitHubTokenEnvVar]);
        Assert.Null(store.Read(GitHubCopilotSdkGateway.GitHubTokenEnvVar));
    }

    [Fact]
    public void SetKey_PosixUserScopeProvidesExportGuidanceAndNeverWritesSecretsToSettingsJson()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sba-keys-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var store = new SessionEnvironmentStore(externalReader: _ => null, isWindowsOverride: false);
            var service = new ApiKeyManagementService(store);

            var secret = "AIzaSyPosixKeyValue9876543210";
            var setResult = service.SetKey(AiProviderType.GoogleGemini, secret, ApiKeyStorageScope.User);

            Assert.True(setResult.Succeeded);
            Assert.False(setResult.PersistedToOsUserEnvironment);
            Assert.Contains("export GEMINI_API_KEY=", setResult.ScopeGuidanceNote ?? string.Empty);
            Assert.DoesNotContain(secret, setResult.ScopeGuidanceNote ?? string.Empty);
            Assert.Equal(secret, store.Read(GeminiSecretProvider.PrimaryEnvVar));

            var workspaceService = new WorkspacePathService();
            var dirs = workspaceService.Resolve(tempRoot);
            Directory.CreateDirectory(dirs.WorkspaceRoot);
            var settingsService = new CliSettingsService();
            var settingsPath = settingsService.Save(
                dirs,
                new CliSettings(PreferredProvider: AiProviderType.GoogleGemini, ModelName: "gemini-2.5-flash"));
            var rawJson = File.ReadAllText(settingsPath);
            Assert.DoesNotContain(secret, rawJson);
            Assert.DoesNotContain("apiKey", rawJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void SetAzureEndpoint_NormalizesHttpsUrlAndRejectsInvalidUrls()
    {
        var store = new SessionEnvironmentStore(externalReader: _ => null);
        var service = new ApiKeyManagementService(store);

        var invalid = service.SetAzureEndpoint("ftp://not-valid.local");
        Assert.False(invalid.Succeeded);

        var valid = service.SetAzureEndpoint("https://foundry-east.services.ai.azure.com/openai/v1/");
        Assert.True(valid.Succeeded);
        Assert.Equal("https://foundry-east.services.ai.azure.com/openai/v1", store.Read(OpenAiGatewayClient.AzureEndpointEnvVar));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class StubCopilotSdkGateway(
        bool runtimeAvailable,
        IReadOnlyList<CopilotModelDescriptor> models) : ICopilotSdkGateway
    {
        public CopilotRuntimeDiagnostics InspectDiagnostics() => new(
            RuntimeAvailable: runtimeAvailable,
            ConnectionMode: runtimeAvailable ? "stub-cli" : "unavailable",
            RuntimeLocation: null,
            HasGitHubToken: false,
            ByokConfigured: false,
            Summary: runtimeAvailable ? "Stub live Copilot gateway" : "Offline stub Copilot gateway");

        public Task<IReadOnlyList<CopilotModelDescriptor>> ListModelsAsync(
            string? gitHubToken = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(models);

        public Task<string> GenerateContentAsync(
            CopilotGenerationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Generation not used in unit tests.");
    }
}
