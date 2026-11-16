using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;
using SmartBacklogAssistant.Workflow;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class AiProviderOrchestratorTests
{
    [Theory]
    [InlineData("4", AiProviderType.GitHubCopilot)]
    [InlineData("copilot-sdk", AiProviderType.GitHubCopilot)]
    [InlineData("github-copilot", AiProviderType.GitHubCopilot)]
    [InlineData("gh-copilot", AiProviderType.GitHubCopilot)]
    [InlineData("copilot", AiProviderType.ManualPrompt)]
    public void ResolveProvider_RoutesCopilotSdkAliasesWithoutBreakingLegacyCopilotToken(
        string input,
        AiProviderType expected)
    {
        var orchestrator = new AiModelOrchestrator();

        Assert.Equal(expected, orchestrator.ResolveProvider(input));
    }

    [Fact]
    public void GenerateDraft_WithGitHubCopilotLive_InvokesSdkGatewayWithZeroHallucinationSystemPrompt()
    {
        var fakeGateway = new FakeCopilotSdkGateway(
            diagnostics: new CopilotRuntimeDiagnostics(true, "stdio-path", "/usr/local/bin/copilot", true, false, "Copilot CLI on PATH"),
            responseFactory: _ => """
                {
                  "title": "Enforce RBAC on Export API",
                  "description": "Restrict CSV/JSON backlog exports to authorized roles.",
                  "userStory": "As a security admin, I want RBAC checks on exports so that sensitive notes are protected.",
                  "scenarios": [{ "given": "An unauthenticated caller", "when": "GET /export is called", "then": "HTTP 401 is returned" }]
                }
                """);
        var orchestrator = new AiProviderOrchestrator(copilotGateway: fakeGateway, environmentReader: _ => null);
        var modelOrchestrator = new AiModelOrchestrator();

        var genResult = orchestrator.GenerateDraft(new MultiProviderGenerationRequest(
            Provider: AiProviderType.GitHubCopilot,
            RenderedPrompt: "Analyze RBAC export requirements",
            BacklogType: BacklogType.Type01,
            ModelName: "gpt-5-mini",
            LiveEnabled: true));

        Assert.True(genResult.UsedLiveProvider);
        Assert.Equal("gpt-5-mini", genResult.ResolvedModel);
        Assert.Single(fakeGateway.CapturedRequests);
        Assert.Contains("Zero Hallucination", fakeGateway.CapturedRequests[0].SystemPrompt);
        Assert.Contains("Type 01 Complete User Story", fakeGateway.CapturedRequests[0].SystemPrompt);

        var outcome = modelOrchestrator.Process(
            "Title: Export RBAC",
            "Prompt",
            AiProviderType.GitHubCopilot,
            generatedContent: genResult.GeneratedContent,
            modelName: genResult.ResolvedModel,
            backlogType: BacklogType.Type01);

        Assert.Equal("Enforce RBAC on Export API", outcome.Title);
        Assert.Contains("GitHub Copilot SDK (gpt-5-mini)", outcome.GuidanceMessage);
        Assert.Single(outcome.Scenarios);
    }

    [Fact]
    public void GenerateDraft_WithGitHubCopilotOffline_SkipsSdkCallAndReturnsDeterministicFallback()
    {
        var fakeGateway = new FakeCopilotSdkGateway(
            diagnostics: new CopilotRuntimeDiagnostics(true, "stdio-path", "/usr/local/bin/copilot", true, false, "Ready"),
            responseFactory: _ => throw new InvalidOperationException("Should not call SDK in offline mode."));
        var orchestrator = new AiProviderOrchestrator(copilotGateway: fakeGateway);

        var result = orchestrator.GenerateDraft(new MultiProviderGenerationRequest(
            Provider: AiProviderType.GitHubCopilot,
            RenderedPrompt: "Prompt",
            BacklogType: BacklogType.Type01,
            LiveEnabled: false));

        Assert.False(result.UsedLiveProvider);
        Assert.Null(result.GeneratedContent);
        Assert.Empty(fakeGateway.CapturedRequests);
    }

    [Fact]
    public void GenerateDraft_WithAzureAndAnthropicByokConfigured_RoutesThroughCopilotSdkByok()
    {
        var fakeGateway = new FakeCopilotSdkGateway(
            diagnostics: new CopilotRuntimeDiagnostics(true, "stdio-path", "/usr/local/bin/copilot", false, true, "Ready BYOK"),
            responseFactory: _ => """{"title":"BYOK Schedule Item","description":"Generated via BYOK endpoint."}""");

        var env = new Dictionary<string, string?>
        {
            ["AZURE_OPENAI_ENDPOINT"] = "https://my-resource.openai.azure.com",
            ["AZURE_OPENAI_API_KEY"] = "az-live-key",
            ["AZURE_OPENAI_DEPLOYMENT"] = "gpt-4o-deploy",
            ["ANTHROPIC_API_KEY"] = "sk-ant-live-key"
        };
        var orchestrator = new AiProviderOrchestrator(
            copilotGateway: fakeGateway,
            environmentReader: key => env.TryGetValue(key, out var val) ? val : null);

        var azureResult = orchestrator.GenerateDraft(new MultiProviderGenerationRequest(
            Provider: AiProviderType.AzureOpenAi,
            RenderedPrompt: "Analyze latency alert",
            BacklogType: BacklogType.Type02,
            LiveEnabled: true));

        var claudeResult = orchestrator.GenerateDraft(new MultiProviderGenerationRequest(
            Provider: AiProviderType.AnthropicClaude,
            RenderedPrompt: "Analyze audit logging",
            BacklogType: BacklogType.Type02,
            LiveEnabled: true));

        Assert.True(azureResult.UsedLiveProvider);
        Assert.Equal("gpt-4o-deploy", azureResult.ResolvedModel);
        Assert.True(claudeResult.UsedLiveProvider);
        Assert.Equal("claude-sonnet-4.5", claudeResult.ResolvedModel);

        Assert.Equal(2, fakeGateway.CapturedRequests.Count);
        Assert.Equal("azure", fakeGateway.CapturedRequests[0].Byok?.Type);
        Assert.Equal("https://my-resource.openai.azure.com", fakeGateway.CapturedRequests[0].Byok?.BaseUrl);
        Assert.Equal("anthropic", fakeGateway.CapturedRequests[1].Byok?.Type);
    }

    [Fact]
    public void GenerateDraft_WhenCopilotRuntimeUnavailable_AzureAndClaudeFallbackToMockCleanly()
    {
        var fakeGateway = new FakeCopilotSdkGateway(
            diagnostics: new CopilotRuntimeDiagnostics(false, "unavailable", null, false, false, "Not installed"));
        var orchestrator = new AiProviderOrchestrator(
            copilotGateway: fakeGateway,
            environmentReader: key => key == "AZURE_OPENAI_ENDPOINT" ? "https://example.openai.azure.com" : null);

        var azureResult = orchestrator.GenerateDraft(new MultiProviderGenerationRequest(
            Provider: AiProviderType.AzureOpenAi,
            RenderedPrompt: "Prompt",
            BacklogType: BacklogType.Type02,
            ApiKey: "az-mock-1234",
            LiveEnabled: true));

        Assert.False(azureResult.UsedLiveProvider);
        Assert.Null(azureResult.GeneratedContent);
    }

    [Fact]
    public void InspectProviderMatrix_ReportsAllFiveProvidersWithReadinessAndActiveMode()
    {
        var fakeGateway = new FakeCopilotSdkGateway(
            diagnostics: new CopilotRuntimeDiagnostics(true, "remote-uri", "localhost:4321", false, false, "Remote Copilot CLI runtime at localhost:4321"));
        var orchestrator = new AiProviderOrchestrator(copilotGateway: fakeGateway, environmentReader: _ => null);
        var secret = new GeminiSecretResolution("AIzaTest1234", "****1234", SecretSource.EnvironmentVariable, "GEMINI_API_KEY", GeminiSecretProvider.GeminiApiKeysUrl);

        var matrix = orchestrator.InspectProviderMatrix(secret, AiProviderType.GitHubCopilot, liveEnabled: true);

        Assert.Equal(5, matrix.Count);
        Assert.Contains(matrix, p => p.Provider == AiProviderType.GoogleGemini && p.IsReady);
        Assert.Contains(matrix, p => p.Provider == AiProviderType.GitHubCopilot && p.IsReady && p.Mode == "SDK Ready");
        Assert.Contains(matrix, p => p.Provider == AiProviderType.ManualPrompt && p.IsReady);
    }

    [Fact]
    public void GitHubCopilotSdkGateway_InspectDiagnosticsAndByok_DetectsRemoteUrlAzureV1AndOllama()
    {
        var azureV1Env = new Dictionary<string, string?>
        {
            ["COPILOT_CLI_URL"] = "localhost:4321",
            ["COPILOT_GITHUB_TOKEN"] = "gho_token",
            ["AZURE_OPENAI_ENDPOINT"] = "https://foundry.services.ai.azure.com/openai/v1",
            ["AZURE_OPENAI_API_KEY"] = "key-123"
        };
        var gateway = new GitHubCopilotSdkGateway(k => azureV1Env.TryGetValue(k, out var v) ? v : null);

        var diag = gateway.InspectDiagnostics();
        var byok = gateway.ResolveEnvironmentByok();

        Assert.True(diag.RuntimeAvailable);
        Assert.Equal("remote-uri", diag.ConnectionMode);
        Assert.True(diag.HasGitHubToken);
        Assert.True(diag.ByokConfigured);
        Assert.NotNull(byok);
        Assert.Equal("openai", byok.Type);
        Assert.Equal("responses", byok.WireApi);

        var ollamaGateway = new GitHubCopilotSdkGateway(k => k == "OLLAMA_HOST" ? "http://localhost:11434/" : null);
        var ollamaByok = ollamaGateway.ResolveEnvironmentByok();
        Assert.NotNull(ollamaByok);
        Assert.Equal("openai", ollamaByok.Type);
        Assert.Equal("http://localhost:11434/v1", ollamaByok.BaseUrl);
    }

    [Fact]
    public void Workflow_ExecuteWithInjectedCopilotSdkProvider_GeneratesAndSavesTicket()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sba-copilot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var promptFile = Path.Combine(tempDir, "prompt.md");
            var outputDir = Path.Combine(tempDir, "out");
            File.WriteAllText(promptFile, "# Prompt\n{{RAW_DATA}}");

            var fakeGateway = new FakeCopilotSdkGateway(
                diagnostics: new CopilotRuntimeDiagnostics(true, "stdio-path", "/usr/local/bin/copilot", true, false, "Copilot CLI Ready"),
                responseFactory: _ => """
                    {
                      "title": "Copilot Generated SSO Story",
                      "description": "Add OIDC SSO support.",
                      "userStory": "As an enterprise admin, I want OIDC SSO so that users sign in securely.",
                      "scenarios": [{ "given": "IdP metadata is configured", "when": "User clicks Sign In with SSO", "then": "User enters authenticated session" }]
                    }
                    """);

            var workflow = new AsIsV1Workflow(
                new RawDataCollector(),
                new PromptTemplateService(),
                new AiModelOrchestrator(),
                new JiraBacklogFormatter(),
                new RuntimeEnvironmentService(),
                new GeminiSecretProvider(environmentReader: _ => null, environmentWriter: (_, _) => { }),
                new GeminiApiClient(),
                new AiProviderOrchestrator(copilotGateway: fakeGateway));

            using var output = new StringWriter();
            var ticket = workflow.ExecuteQuickGoal(
                "Add Enterprise SSO",
                "Type01",
                new WorkflowOptions(
                    InputFilePath: tempDir,
                    PromptFilePath: promptFile,
                    ProviderInput: "copilot-sdk",
                    OutputDirectory: outputDir,
                    NonInteractive: true,
                    GeminiLive: true),
                new StringReader(string.Empty),
                output);

            Assert.Equal("Copilot Generated SSO Story", ticket.Title);
            Assert.Contains("✓ Generated structured Type01 draft via Copilot SDK", output.ToString());
            Assert.True(File.Exists(Path.Combine(outputDir, "type01-copilot-generated-sso-story.md")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void SystemAndStructuredPrompts_IncludeGoalModeActionableGuidanceAlongsideZeroHallucination()
    {
        var type02System = AiProviderOrchestrator.BuildZeroHallucinationSystemPrompt(BacklogType.Type02);
        var type02Prompt = AiProviderOrchestrator.BuildStructuredJsonPrompt("Draft body", BacklogType.Type02);

        Assert.Contains("Zero Hallucination rule", type02System);
        Assert.Contains("do NOT write 'Not specified in discovery notes' boilerplate", type02System);
        Assert.Contains("Type 02 Schedule Item", type02System);
        Assert.Contains("Objective, Context/Key Details, and Verification Checklist", type02Prompt);
    }

    [Fact]
    public void GenerateGoalExpansion_WithGitHubCopilotLiveAndOffline_HandlesTwoStepExpansionCorrectly()
    {
        var fakeGateway = new FakeCopilotSdkGateway(
            diagnostics: new CopilotRuntimeDiagnostics(true, "stdio-path", "/usr/local/bin/copilot", true, false, "Copilot CLI Ready"),
            responseFactory: _ => """
                {
                  "enhancedTitle": "Verify & Document Official 2026 Theme",
                  "userStory": "As a stakeholder, I want official theme criteria verified so that plans align.",
                  "discoverySummary": "**Objective:** Verify official guidelines.",
                  "keyChecklist": ["Review rules"],
                  "alternatives": []
                }
                """);
        var orchestrator = new AiProviderOrchestrator(copilotGateway: fakeGateway, environmentReader: _ => null);

        var liveResult = orchestrator.GenerateGoalExpansion(new MultiProviderGenerationRequest(
            Provider: AiProviderType.GitHubCopilot,
            RenderedPrompt: "Goal: check 2026 theme",
            BacklogType: BacklogType.Type02,
            LiveEnabled: true));

        var offlineResult = orchestrator.GenerateGoalExpansion(new MultiProviderGenerationRequest(
            Provider: AiProviderType.GitHubCopilot,
            RenderedPrompt: "Goal: check 2026 theme",
            BacklogType: BacklogType.Type02,
            LiveEnabled: false));

        Assert.True(liveResult.UsedLiveProvider);
        Assert.Single(fakeGateway.CapturedRequests);
        Assert.Contains("INVEST", fakeGateway.CapturedRequests[0].SystemPrompt);
        Assert.Contains("enhancedTitle", fakeGateway.CapturedRequests[0].Prompt);
        Assert.False(offlineResult.UsedLiveProvider);
        Assert.Null(offlineResult.GeneratedContent);
    }

    private sealed class FakeCopilotSdkGateway(
        CopilotRuntimeDiagnostics diagnostics,
        Func<CopilotGenerationRequest, string>? responseFactory = null,
        IReadOnlyList<CopilotModelDescriptor>? models = null) : ICopilotSdkGateway
    {
        public List<CopilotGenerationRequest> CapturedRequests { get; } = [];

        public CopilotRuntimeDiagnostics InspectDiagnostics() => diagnostics;

        public Task<IReadOnlyList<CopilotModelDescriptor>> ListModelsAsync(
            string? gitHubToken = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(models ?? [new CopilotModelDescriptor("gpt-5-mini", "GPT-5 mini")]);

        public Task<string> GenerateContentAsync(
            CopilotGenerationRequest request,
            CancellationToken cancellationToken = default)
        {
            CapturedRequests.Add(request);
            return Task.FromResult(responseFactory is null
                ? """{"title":"Default Fake Title","description":"Default Fake Description"}"""
                : responseFactory(request));
        }
    }
}
