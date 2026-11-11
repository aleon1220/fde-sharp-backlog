using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;
using SmartBacklogAssistant.Workflow;

namespace SmartBacklogAssistant.Tests.Integration;

public sealed class AsIsV1WorkflowTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputFilePath;
    private readonly string _promptFilePath;
    private readonly string _outputDir;

    public AsIsV1WorkflowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sba-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _inputFilePath = Path.Combine(_tempDir, "raw-notes.txt");
        _promptFilePath = Path.Combine(_tempDir, "as-is-v1-prompt.md");
        _outputDir = Path.Combine(_tempDir, "output");

        File.WriteAllText(_inputFilePath, """
            Discovery Note - Webhook Retry Policy
            Context: Outbound webhooks fail permanently on transient 503 errors.
            Requirement: As an integration developer, I want exponential backoff retries so that transient network glitches do not drop events.
            Precondition: A webhook endpoint returns HTTP 503.
            Action: The dispatcher handles the failed delivery attempt.
            Expected Outcome: The event is retried up to 5 times with exponential backoff.
            """);

        File.WriteAllText(_promptFilePath, """
            # As-Is v1 Prompt
            Analyze the following raw notes:
            {{RAW_DATA}}
            """);
    }

    [Fact]
    public void Execute_WhenNoGeminiKeyRegistered_PromptsForGeminiKeyInCliAndShowsRuntimeDiagnostics()
    {
        // User enters Gemini API key when prompted in CLI, then presses Enter for default Type01 (Given/When/Then)
        using var input = new StringReader("AIzaSyCliEnteredKey5566\n\n");
        using var output = new StringWriter();

        var isolatedSecretProvider = new GeminiSecretProvider(
            environmentReader: _ => null,
            environmentWriter: (_, _) => { });

        var workflow = CreateWorkflow(isolatedSecretProvider);
        var options = new WorkflowOptions(
            InputFilePath: _inputFilePath,
            PromptFilePath: _promptFilePath,
            OutputDirectory: _outputDir,
            NonInteractive: false);

        var ticket = workflow.Execute(options, input, output);

        Assert.Equal(BacklogType.Type01, ticket.BacklogType);
        Assert.Equal("complete", ticket.JiraStage);
        Assert.Equal("Webhook Retry Policy", ticket.Title);
        Assert.Single(ticket.AcceptanceCriteria);

        var consoleOutput = output.ToString();
        Assert.Contains("OS Running", consoleOutput);
        Assert.Contains("Build Info", consoleOutput);
        Assert.Contains("AI Model Enabled : Google Gemini", consoleOutput);
        Assert.Contains("https://aistudio.google.com/api-keys", consoleOutput);
        Assert.Contains("Enter your Gemini API key to proceed", consoleOutput);
        Assert.Contains("****5566", consoleOutput);
        Assert.DoesNotContain("AIzaSyCliEnteredKey5566", consoleOutput);

        Assert.True(File.Exists(Path.Combine(_outputDir, "jira-backlog-item.md")));
        Assert.False(File.Exists(Path.Combine(_outputDir, "jira-backlog-item.json")));
    }

    [Fact]
    public void Execute_WhenJsonFileOutIsEnabled_WritesBothMarkdownAndJsonArtifacts()
    {
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();

        var workflow = CreateWorkflow();
        var options = new WorkflowOptions(
            InputFilePath: _inputFilePath,
            PromptFilePath: _promptFilePath,
            ProviderInput: "gemini",
            ApiKey: "AIzaSyJsonEnabled9999",
            BacklogTypeInput: "Type01",
            OutputDirectory: _outputDir,
            NonInteractive: true,
            JsonFileOut: true);

        workflow.Execute(options, input, output);

        Assert.True(File.Exists(Path.Combine(_outputDir, "jira-backlog-item.md")));
        Assert.True(File.Exists(Path.Combine(_outputDir, "jira-backlog-item.json")));
        Assert.True(File.Exists(Path.Combine(_outputDir, "type01-webhook-retry-policy.md")));
        Assert.True(File.Exists(Path.Combine(_outputDir, "type01-webhook-retry-policy.json")));
    }

    [Fact]
    public void Execute_WhenGeminiKeyInjectedViaOsEnvironment_ProceedsWithoutKeyPrompt()
    {
        // Only one newline needed for BacklogType selection because GEMINI_API_KEY is already in OS environment
        using var input = new StringReader("1\n");
        using var output = new StringWriter();

        var envSecretProvider = new GeminiSecretProvider(
            environmentReader: name => name == GeminiSecretProvider.PrimaryEnvVar ? "AIzaSyOsEnvKey8899" : null,
            environmentWriter: (_, _) => { });

        var workflow = CreateWorkflow(envSecretProvider);
        var options = new WorkflowOptions(
            InputFilePath: _inputFilePath,
            PromptFilePath: _promptFilePath,
            OutputDirectory: _outputDir,
            NonInteractive: false);

        var ticket = workflow.Execute(options, input, output);

        Assert.Equal(BacklogType.Type01, ticket.BacklogType);
        var consoleOutput = output.ToString();
        Assert.Contains("OS Environment Variable (GEMINI_API_KEY) [****8899]", consoleOutput);
        Assert.DoesNotContain("Enter your Gemini API key to proceed", consoleOutput);
        Assert.DoesNotContain("AIzaSyOsEnvKey8899", consoleOutput);
    }

    [Fact]
    public void Execute_InteractiveManualProviderFlow_RendersPromptAndGeneratesType01Artifacts()
    {
        using var input = new StringReader("\n");
        using var output = new StringWriter();

        var workflow = CreateWorkflow();
        var options = new WorkflowOptions(
            InputFilePath: _inputFilePath,
            PromptFilePath: _promptFilePath,
            ProviderInput: "manual",
            OutputDirectory: _outputDir,
            NonInteractive: false);

        var ticket = workflow.Execute(options, input, output);

        Assert.Equal(BacklogType.Type01, ticket.BacklogType);
        Assert.Equal("complete", ticket.JiraStage);
        var consoleOutput = output.ToString();
        Assert.Contains("Pre-Designed Prompt (Loaded from Markdown)", consoleOutput);
        Assert.Contains("run it in your desired AI model", consoleOutput);
    }

    [Fact]
    public void Execute_WithMockedClaudeProviderAndType02_GeneratesScheduleTicket()
    {
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();

        var workflow = CreateWorkflow();
        var options = new WorkflowOptions(
            InputFilePath: _inputFilePath,
            PromptFilePath: _promptFilePath,
            ProviderInput: "claude",
            ApiKey: "sk-ant-test9999",
            BacklogTypeInput: "Type02",
            OutputDirectory: _outputDir,
            NonInteractive: true);

        var ticket = workflow.Execute(options, input, output);

        Assert.Equal(BacklogType.Type02, ticket.BacklogType);
        Assert.Equal("schedule", ticket.JiraStage);
        Assert.NotNull(ticket.Description);
        Assert.Empty(ticket.AcceptanceCriteria);

        var consoleOutput = output.ToString();
        Assert.Contains("Anthropic Claude", consoleOutput);
        Assert.Contains("****9999", consoleOutput);
    }

    [Fact]
    public void Execute_WithGeminiLive_UsesSelectedCompatibleModelAndGeneratedDraft()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse("""
                    {
                      "models": [{
                        "name": "models/gemini-3.8-flash",
                        "baseModelId": "gemini-3.8-flash",
                        "displayName": "Gemini 3.8 Flash",
                        "supportedGenerationMethods": ["generateContent"]
                      }]
                    }
                    """);
            }

            return JsonResponse("""
                {
                  "candidates": [{
                    "content": { "parts": [{ "text": "{\"title\":\"Gemini Draft\",\"description\":\"Created from Gemini\",\"userStory\":\"As a user, I want a generated draft, so that the team can review it.\",\"scenarios\":[{\"given\":\"the user has notes\",\"when\":\"Gemini drafts a backlog item\",\"then\":\"a structured item is returned\"}]}" }] }
                  }]
                }
                """);
        });
        using var httpClient = new HttpClient(handler);
        var workflow = CreateWorkflow(geminiApiClient: new GeminiApiClient(httpClient));
        var options = new WorkflowOptions(
            InputFilePath: _inputFilePath,
            PromptFilePath: _promptFilePath,
            ApiKey: "AIzaSyLiveTestKey1234",
            ModelName: "gemini-3.8-flash",
            BacklogTypeInput: "Type02",
            OutputDirectory: _outputDir,
            NonInteractive: true,
            GeminiLive: true);

        var ticket = workflow.Execute(options, new StringReader(string.Empty), new StringWriter());

        Assert.Equal(BacklogType.Type02, ticket.BacklogType);
        Assert.Equal("Gemini Draft", ticket.Title);
        Assert.Equal("Created from Gemini", ticket.Description);
    }

    private static AsIsV1Workflow CreateWorkflow(
        GeminiSecretProvider? secretProvider = null,
        GeminiApiClient? geminiApiClient = null) =>
        new(
            new RawDataCollector(),
            new PromptTemplateService(),
            new AiModelOrchestrator(),
            new JiraBacklogFormatter(),
            new RuntimeEnvironmentService(),
            secretProvider ?? new GeminiSecretProvider(
                environmentReader: _ => null,
                environmentWriter: (_, _) => { }),
            geminiApiClient);

    private static HttpResponseMessage JsonResponse(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
