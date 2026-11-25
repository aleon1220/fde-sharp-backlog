using Microsoft.Extensions.Logging;
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
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
            request.Method == HttpMethod.Get
                ? JsonResponse("""
                    {
                      "models": [{
                        "name": "models/gemini-2.5-flash",
                        "baseModelId": "gemini-2.5-flash",
                        "displayName": "Gemini 2.5 Flash",
                        "supportedGenerationMethods": ["generateContent"]
                      }]
                    }
                    """)
                : JsonResponse("""
                    {
                      "candidates": [{
                        "content": {
                          "parts": [{
                            "text": "{\"title\":\"Webhook Retry Policy\",\"description\":\"Retry transient webhook failures.\",\"userStory\":\"As an integration developer, I want retries, so that transient failures do not drop events.\",\"scenarios\":[{\"given\":\"A webhook endpoint returns HTTP 503\",\"when\":\"The delivery attempt fails\",\"then\":\"The event is retried\"}]}"
                          }]
                        }
                      }]
                    }
                    """)));

        var isolatedSecretProvider = new GeminiSecretProvider(
            environmentReader: _ => null,
            environmentWriter: (_, _) => { });

        var workflow = CreateWorkflow(isolatedSecretProvider, new GeminiApiClient(httpClient));
        var options = new WorkflowOptions(
            InputFilePath: _inputFilePath,
            PromptFilePath: _promptFilePath,
            OutputDirectory: _outputDir,
            ModelName: "gemini-2.5-flash",
            GeminiLive: true,
            NonInteractive: false);

        var ticket = workflow.Execute(options, input, output);

        Assert.Equal(BacklogType.Type01, ticket.BacklogType);
        Assert.Equal("complete", ticket.JiraStage);
        Assert.Equal("Webhook Retry Policy", ticket.Title);
        Assert.Single(ticket.AcceptanceCriteria);

        var consoleOutput = output.ToString();
        Assert.Contains("✓ Collected", consoleOutput);
        Assert.Contains("✓ Generated structured Type01 draft via Google Gemini", consoleOutput);
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
        var requestBody = string.Empty;
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

                        requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""
                {
                  "candidates": [{
                                        "content": { "parts": [{ "text": "{\"title\":\"Gemini Draft\",\"description\":\"Created from Gemini\"}" }] }
                  }]
                }
                """);
        });
        using var httpClient = new HttpClient(handler);
        var workflow = CreateWorkflow(geminiApiClient: new GeminiApiClient(httpClient));
                var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var options = new WorkflowOptions(
            InputFilePath: _inputFilePath,
                        PromptFilePath: Path.Combine(repositoryRoot, PromptTemplateService.Type02PromptFilePath),
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
        Assert.Contains("Schedule Item (Type 02)", requestBody);
        Assert.Contains("Do not add a user story", requestBody);
    }

    [Fact]
    public void Execute_WithCombinedOutput_FormatsAndSavesBothTypesFromOneExecution()
    {
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();
        var workflow = CreateWorkflow();
        var options = new WorkflowOptions(
            InputFilePath: _inputFilePath,
            PromptFilePath: _promptFilePath,
            ProviderInput: "manual",
            BacklogTypeInput: "Type01",
            OutputDirectory: _outputDir,
            NonInteractive: true,
            CombinedOutput: true);

        var ticket = workflow.Execute(options, input, output);

        Assert.Equal(BacklogType.Type01, ticket.BacklogType);
        Assert.Contains("complete (Type01) + schedule (Type02)", output.ToString());
        Assert.DoesNotContain("# [Jira -", output.ToString());
        Assert.True(File.Exists(Path.Combine(_outputDir, "type01-webhook-retry-policy.md")));
        Assert.True(File.Exists(Path.Combine(_outputDir, "type02-webhook-retry-policy.md")));
    }

    [Fact]
    public void Execute_WithDryRunEnabled_PreviewsTicketWithoutWritingFiles()
    {
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();
        var workflow = CreateWorkflow();
        var options = new WorkflowOptions(
            InputFilePath: _inputFilePath,
            PromptFilePath: _promptFilePath,
            ProviderInput: "manual",
            BacklogTypeInput: "Type01",
            OutputDirectory: _outputDir,
            NonInteractive: true,
            DryRun: true);

        var ticket = workflow.Execute(options, input, output);

        Assert.Equal(BacklogType.Type01, ticket.BacklogType);
        Assert.Contains("[Preview / Dry Run]", output.ToString());
        Assert.False(Directory.Exists(_outputDir));
    }

    [Fact]
    public void Execute_WithWebSearchGrounding_InjectsSearchFindingsAndSourcesIntoGeminiDraft()
    {
        var capturedPosts = new List<string>();
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse("""
                    {
                      "models": [{
                        "name": "models/gemini-2.5-flash",
                        "baseModelId": "gemini-2.5-flash",
                        "displayName": "Gemini 2.5 Flash",
                        "supportedGenerationMethods": ["generateContent"]
                      }]
                    }
                    """);
            }

            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedPosts.Add(body);

            if (body.Contains("google_search", StringComparison.Ordinal))
            {
                return JsonResponse("""
                    {
                      "candidates": [{
                        "content": { "parts": [{ "text": "- Official Geocaching HQ 2026 hiding movie theme invites cinema-inspired caches." }] },
                        "groundingMetadata": {
                          "groundingChunks": [{ "web": { "title": "Geocaching HQ", "uri": "https://www.geocaching.com/blog/" } }]
                        }
                      }]
                    }
                    """);
            }

            if (body.Contains("Goal-to-Notes Expansion", StringComparison.Ordinal))
            {
                return JsonResponse("""
                    {
                      "candidates": [{
                        "content": {
                          "parts": [{
                            "text": "{\"enhancedTitle\":\"Verify & Document Geocaching 2026 Hiding Movie Theme Rules\",\"userStory\":\"As a cache organizer, I want to verify official 2026 movie hiding rules so that our submission qualifies for the campaign.\",\"discoverySummary\":\"**Objective:** Verify official 2026 cinema-inspired hide criteria and dates.\",\"keyChecklist\":[\"Verify Geocaching HQ blog dates\"],\"alternatives\":[{\"angle\":\"Action & Delivery\",\"title\":\"Verify & Document 2026 Movie Theme\",\"description\":\"Audit official campaign guidelines and dates.\"},{\"angle\":\"User Value / Story\",\"title\":\"Guide Hiders on 2026 Movie Theme\",\"description\":\"Help cache owners craft qualifying themed submissions.\"},{\"angle\":\"Verification & Scope\",\"title\":\"Confirm 2026 Movie Souvenir Criteria\",\"description\":\"Review eligibility dates and rule constraints.\"}]}"
                          }]
                        }
                      }]
                    }
                    """);
            }

            return JsonResponse("""
                {
                  "candidates": [{
                    "content": {
                      "parts": [{
                        "text": "{\"title\":\"Verify Geocaching 2026 Hiding Movie Theme Campaign\",\"description\":\"Objective: Confirm official 2026 cinema-inspired hide criteria.\\nChecklist:\\n- Verify Geocaching HQ blog dates\",\"alternatives\":[{\"angle\":\"Action & Delivery\",\"title\":\"Verify & Document 2026 Movie Theme\",\"description\":\"Audit official campaign guidelines and dates.\"},{\"angle\":\"User Value / Story\",\"title\":\"Guide Hiders on 2026 Movie Theme\",\"description\":\"Help cache owners craft qualifying themed submissions.\"},{\"angle\":\"Verification & Scope\",\"title\":\"Confirm 2026 Movie Souvenir Criteria\",\"description\":\"Review eligibility dates and rule constraints.\"}]}"
                      }]
                    }
                  }]
                }
                """);
        }));

        var workflow = CreateWorkflow(geminiApiClient: new GeminiApiClient(httpClient));
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        using var output = new StringWriter();

        var ticket = workflow.Execute(
            new WorkflowOptions(
                InputFilePath: _tempDir,
                PromptFilePath: Path.Combine(repositoryRoot, PromptTemplateService.DefaultPromptFilePath),
                ApiKey: "AIzaSyLiveSearchTest1234",
                ModelName: "gemini-2.5-flash",
                BacklogTypeInput: "Type02",
                GoalTitle: "check Geocaching 2026 hiding movie theme",
                GoalContext: "Verify hiding criteria and dates",
                WebSearchGrounding: true,
                OutputDirectory: _outputDir,
                NonInteractive: true,
                GeminiLive: true),
            new StringReader(string.Empty),
            output);

        Assert.Equal(3, capturedPosts.Count);
        Assert.Contains("google_search", capturedPosts[0]);
        Assert.Contains("Goal-to-Notes Expansion", capturedPosts[1]);
        Assert.Contains("Web Research Findings:", capturedPosts[1]);
        Assert.Contains("Synthesized Goal Brief (Goal Expansion):", capturedPosts[2]);
        Assert.Contains("Web Research Findings:", capturedPosts[2]);
        Assert.Contains("cinema-inspired caches", capturedPosts[2]);
        Assert.Contains("https://www.geocaching.com/blog/", capturedPosts[2]);
        Assert.Contains("Goal: check Geocaching 2026 hiding movie theme", capturedPosts[2]);
        Assert.Contains("Context: Verify hiding criteria and dates", capturedPosts[2]);

        Assert.Equal("Verify Geocaching 2026 Hiding Movie Theme Campaign", ticket.Title);
        Assert.Contains("cinema-inspired hide criteria", ticket.Description);
        Assert.Equal(3, ticket.Alternatives?.Count);
        Assert.DoesNotContain("│ Alternatives", output.ToString());
        Assert.DoesNotContain("│ User Story", output.ToString());
        Assert.Contains("web research from 1 source(s)", output.ToString());
    }

    [Fact]
    public void Execute_WhenWebSearchGroundingReturnsHttp400_LogsWarningAndCompletesDraft()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse("""
                    {
                      "models": [{
                        "name": "models/gemini-2.5-flash",
                        "baseModelId": "gemini-2.5-flash",
                        "displayName": "Gemini 2.5 Flash",
                        "supportedGenerationMethods": ["generateContent"]
                      }]
                    }
                    """);
            }

            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains("google_search", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
                {
                    ReasonPhrase = "Bad Request",
                    Content = new StringContent("""{"error":"Search grounding unavailable"}""", System.Text.Encoding.UTF8, "application/json")
                };
            }

            if (body.Contains("Goal-to-Notes Expansion", StringComparison.Ordinal))
            {
                return JsonResponse("""
                    {
                      "candidates": [{
                        "content": { "parts": [{ "text": "{\"enhancedTitle\":\"Verify Theme Draft\",\"userStory\":\"As a PM, I want to verify theme rules so that requirements are clear.\",\"discoverySummary\":\"**Objective:** Verify theme criteria.\",\"keyChecklist\":[\"Check announcement\"],\"alternatives\":[{\"angle\":\"Action & Delivery\",\"title\":\"Verify Theme Draft\",\"description\":\"Confirm rules.\"},{\"angle\":\"User Value / Story\",\"title\":\"Support Theme Participation\",\"description\":\"Clarify user criteria.\"},{\"angle\":\"Verification & Scope\",\"title\":\"Audit Theme Dates\",\"description\":\"Verify campaign timeline.\"}]}" }] }
                      }]
                    }
                    """);
            }

            return JsonResponse("""
                {
                  "candidates": [{
                    "content": { "parts": [{ "text": "{\"title\":\"Verify Theme Draft\",\"description\":\"Standard fallback description without web grounding.\"}" }] }
                  }]
                }
                """);
        }));

        var workflow = CreateWorkflow(geminiApiClient: new GeminiApiClient(httpClient));
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        using var output = new StringWriter();

        var ticket = workflow.Execute(
            new WorkflowOptions(
                InputFilePath: _tempDir,
                PromptFilePath: Path.Combine(repositoryRoot, PromptTemplateService.DefaultPromptFilePath),
                ApiKey: "AIzaSyLiveSearchWarn1234",
                ModelName: "gemini-2.5-flash",
                BacklogTypeInput: "Type02",
                GoalTitle: "check Geocaching 2026 hiding movie theme",
                WebSearchGrounding: true,
                OutputDirectory: _outputDir,
                NonInteractive: true,
                GeminiLive: true),
            new StringReader(string.Empty),
            output);

        Assert.Equal("Verify Theme Draft", ticket.Title);
        Assert.Contains("Google Search grounding skipped (HTTP 400 (Bad Request))", output.ToString());
    }

    [Fact]
    public void Execute_BareType02GoalOffline_CreativelySynthesizesUserStoryDescriptionAndThreeAlternatives()
    {
        var workflow = CreateWorkflow();
        using var output = new StringWriter();

        var ticket = workflow.Execute(
            new WorkflowOptions(
                InputFilePath: _tempDir,
                PromptFilePath: _promptFilePath,
                BacklogTypeInput: "Type02",
                GoalTitle: "check Geocaching 2026 hiding movie theme",
                DryRun: true,
                NonInteractive: true,
                GeminiLive: false),
            new StringReader(string.Empty),
            output);

        Assert.Equal(BacklogType.Type02, ticket.BacklogType);
        Assert.Equal("Check Geocaching 2026 hiding movie theme", ticket.Title);
        Assert.NotNull(ticket.Description);
        Assert.Contains("**Objective:**", ticket.Description);
        Assert.Contains("**Suggested Actions:**", ticket.Description);
        Assert.Contains("* Gather official rules", ticket.Description);
        Assert.True(DescriptionFormatter.CountWords(ticket.Description) <= DescriptionFormatter.MaxDescriptionWords);
        Assert.DoesNotContain("Not specified in discovery notes", ticket.Description);
        Assert.Equal(3, ticket.Alternatives?.Count);
        Assert.Contains("## Title & Description Alternatives", ticket.FormattedMarkdown);

        var consoleText = output.ToString();
        Assert.Contains("Smart Backlog Assistant — Markdown Outcome", consoleText);
        Assert.DoesNotContain("User Story", consoleText);
        Assert.DoesNotContain("Acceptance", consoleText);
        Assert.DoesNotContain("# [Jira -", consoleText);
        Assert.Contains("Objective:", consoleText);
        Assert.DoesNotContain("**Objective:**", consoleText);
        Assert.DoesNotContain("│ Alternatives", consoleText);
        Assert.Contains("--alternatives", consoleText);
        Assert.Contains("┌", consoleText);
        Assert.Contains("└", consoleText);
    }

    [Fact]
    public void Execute_WithVerboseFlagEnabled_RoutesRuntimeBannerAndWordBudgetTelemetryToStderrKeepingStdoutClean()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var loggerFactory = new StderrLoggerFactory(
            error,
            minimumLevel: Microsoft.Extensions.Logging.LogLevel.Debug,
            outputFormat: LogOutputFormat.Text,
            enableColor: false);
        var workflow = CreateWorkflow(loggerFactory: loggerFactory);

        workflow.Execute(
            new WorkflowOptions(
                InputFilePath: _inputFilePath,
                PromptFilePath: _promptFilePath,
                BacklogTypeInput: "Type01",
                DryRun: true,
                NonInteractive: true,
                GeminiLive: false,
                Verbose: true),
            new StringReader(string.Empty),
            output);

        var stdoutText = output.ToString();
        var stderrText = error.ToString();
        Assert.Contains("OS Running", stderrText);
        Assert.Contains("Build Info", stderrText);
        Assert.Contains("Description word budget:", stderrText);
        Assert.Contains("/57 words", stderrText);
        Assert.DoesNotContain("OS Running", stdoutText);
        Assert.DoesNotContain("Description word budget:", stdoutText);
    }

    [Fact]
    public void Execute_WithTraceLoggingAndGeminiLive_EmitsHttpAndPipelineTraceOnStderrWithoutLeakingApiKey()
    {
        const string secretKey = "AIzaSyWorkflowTraceSecret7788";
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var loggerFactory = new StderrLoggerFactory(
            error,
            minimumLevel: Microsoft.Extensions.Logging.LogLevel.Trace,
            outputFormat: LogOutputFormat.Text,
            enableColor: false);

        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
            request.Method == HttpMethod.Get
                ? JsonResponse("""
                    {
                      "models": [{
                        "name": "models/gemini-2.5-flash",
                        "baseModelId": "gemini-2.5-flash",
                        "displayName": "Gemini 2.5 Flash",
                        "supportedGenerationMethods": ["generateContent"]
                      }]
                    }
                    """)
                : JsonResponse("""
                    {
                      "candidates": [{
                        "content": {
                          "parts": [{
                            "text": "{\"title\":\"Trace Logged Ticket\",\"description\":\"Validate trace telemetry.\",\"userStory\":\"As an SRE, I want trace logs on stderr so that stdout stays clean.\",\"scenarios\":[{\"given\":\"Trace logging is enabled\",\"when\":\"Workflow runs\",\"then\":\"Secrets are masked\"}]}"
                          }]
                        }
                      }]
                    }
                    """)));

        var workflow = CreateWorkflow(
            geminiApiClient: new GeminiApiClient(httpClient, loggerFactory.CreateLogger<GeminiApiClient>()),
            loggerFactory: loggerFactory);

        workflow.Execute(
            new WorkflowOptions(
                InputFilePath: _inputFilePath,
                PromptFilePath: _promptFilePath,
                ApiKey: secretKey,
                ModelName: "gemini-2.5-flash",
                BacklogTypeInput: "Type01",
                DryRun: true,
                NonInteractive: true,
                GeminiLive: true),
            new StringReader(string.Empty),
            output);

        var stderrText = error.ToString();
        var stdoutText = output.ToString();
        Assert.Contains(" TRC]", stderrText);
        Assert.Contains("Gemini HTTP", stderrText);
        Assert.Contains("****7788", stderrText);
        Assert.DoesNotContain(secretKey, stderrText);
        Assert.DoesNotContain(secretKey, stdoutText);
        Assert.DoesNotContain(" TRC]", stdoutText);
    }

    [Fact]
    public void Execute_OpenAiGateway_LiveInvalidUrlAndInteractiveFallback_BehavesDeterministically()
    {
        var secretKey = "sk-workflow-live-secret-3141";
        var redactor = new SecretRedactor();
        redactor.RegisterSecret(secretKey);
        using var stderr = new StringWriter();
        using var loggerFactory = new StderrLoggerFactory(
            stderr,
            Microsoft.Extensions.Logging.LogLevel.Trace,
            LogOutputFormat.Text,
            secretRedactor: redactor,
            enableColor: false);

        var httpCalls = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            httpCalls++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                return JsonResponse("""{"object":"list","data":[{"id":"gpt-4o","owned_by":"openai"},{"id":"tts-1","owned_by":"openai"}]}""");
            }

            return JsonResponse("""
                {
                  "choices": [
                    {
                      "message": {
                        "content": "{\"title\":\"Workflow OpenAI Story\",\"description\":\"**Objective:** Live gateway generation succeeded.\",\"userStory\":\"As a user, I want live OpenAI Gateway synthesis.\",\"scenarios\":[{\"given\":\"Valid key\",\"when\":\"workflow runs\",\"then\":\"ticket is produced\"}]}"
                      }
                    }
                  ]
                }
                """);
        });

        var openAiClient = new OpenAiGatewayClient(new HttpClient(handler), loggerFactory.CreateLogger<OpenAiGatewayClient>());
        var env = new Dictionary<string, string?>
        {
            [OpenAiGatewayClient.ApiKeyEnvVar] = secretKey
        };
        var providerOrchestrator = new AiProviderOrchestrator(
            environmentReader: key => env.TryGetValue(key, out var val) ? val : null,
            loggerFactory: loggerFactory,
            openAiGatewayClient: openAiClient);

        var workflow = CreateWorkflow(
            providerOrchestrator: providerOrchestrator,
            loggerFactory: loggerFactory);

        // 1. Live Type01 execution with custom gateway URL
        using var liveOut = new StringWriter();
        var liveTicket = workflow.Execute(
            new WorkflowOptions(
                InputFilePath: _inputFilePath,
                PromptFilePath: _promptFilePath,
                ProviderInput: "openai",
                BacklogTypeInput: "Type01",
                DryRun: true,
                NonInteractive: true,
                GeminiLive: true,
                OpenAiBaseUrl: "https://gateway.internal/v1"),
            new StringReader(string.Empty),
            liveOut);

        Assert.Equal("Workflow OpenAI Story", liveTicket.Title);
        Assert.Equal(AiProviderType.OpenAiGateway, workflow.LastOutcome?.ProviderUsed);
        Assert.Contains("https://gateway.internal/v1/chat/completions", stderr.ToString());
        Assert.DoesNotContain(secretKey, stderr.ToString());
        Assert.DoesNotContain(secretKey, liveOut.ToString());

        // 2. Live /models catalog discovery via workflow
        var catalog = workflow.ListOpenAiModelCatalog(
            apiKey: secretKey,
            explicitBaseUrl: "https://gateway.internal/v1");
        Assert.Single(catalog.SupportedModels);
        Assert.Equal("gpt-4o", catalog.SupportedModels[0].ModelId);
        Assert.True(catalog.UnsupportedModelCapabilities.ContainsKey("tts-1"));

        // 3. Invalid gateway URL fails immediately without prompting for API key interactively
        var offlineEnvWorkflow = CreateWorkflow(
            providerOrchestrator: new AiProviderOrchestrator(
                environmentReader: _ => null,
                openAiGatewayClient: openAiClient));
        using var errOut = new StringWriter();
        Assert.Throws<InvalidOperationException>(() =>
            offlineEnvWorkflow.Execute(
                new WorkflowOptions(
                    InputFilePath: _inputFilePath,
                    PromptFilePath: _promptFilePath,
                    ProviderInput: "openai",
                    BacklogTypeInput: "Type02",
                    DryRun: true,
                    NonInteractive: false,
                    GeminiLive: true,
                    OpenAiBaseUrl: "not-a-valid-url"),
                new StringReader(string.Empty),
                errOut));

        // 4. Missing API key in interactive mode prompts for key -> Enter falls back deterministically
        var callsBeforePrompt = httpCalls;
        using var promptOut = new StringWriter();
        var fallbackTicket = offlineEnvWorkflow.Execute(
            new WorkflowOptions(
                InputFilePath: _inputFilePath,
                PromptFilePath: _promptFilePath,
                ProviderInput: "openai",
                BacklogTypeInput: "Type02",
                DryRun: true,
                NonInteractive: false,
                GeminiLive: true),
            new StringReader("\n"),
            promptOut);

        Assert.Equal(callsBeforePrompt, httpCalls);
        Assert.Contains("Enter OpenAI Gateway API key", promptOut.ToString());
        Assert.False(string.IsNullOrWhiteSpace(fallbackTicket.Title));
        Assert.Equal(AiProviderType.OpenAiGateway, offlineEnvWorkflow.LastOutcome?.ProviderUsed);
    }

    [Fact]
    public void Execute_WhenOpenAiGatewayInteractivePromptWithKey_MasksKeystrokesAndConfirmsWithMaskedKey()
    {
        var secret = "sk-testkey-8888";
        var keyQueue = new Queue<ConsoleKeyInfo>();
        foreach (var ch in secret)
        {
            keyQueue.Enqueue(new ConsoleKeyInfo(ch, ConsoleKey.NoName, false, false, false));
        }
        keyQueue.Enqueue(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        var secretProvider = new GeminiSecretProvider(
            environmentReader: _ => null,
            environmentWriter: (_, _) => { },
            keyReader: () => keyQueue.Dequeue(),
            isInteractive: () => true);

        var handler = new StubHttpMessageHandler(req =>
        {
            return JsonResponse("""
                {
                  "choices": [
                    {
                      "message": {
                        "content": "{\"title\":\"Workflow OpenAI Masked Story\",\"description\":\"**Objective:** Live gateway generation succeeded.\",\"userStory\":\"As a user, I want masked keystroke entry.\",\"scenarios\":[{\"given\":\"TTY entry\",\"when\":\"typing secret\",\"then\":\"masked with asterisks\"}]}"
                      }
                    }
                  ]
                }
                """);
        });

        var openAiClient = new OpenAiGatewayClient(new HttpClient(handler));
        var providerOrchestrator = new AiProviderOrchestrator(
            environmentReader: _ => null,
            openAiGatewayClient: openAiClient);

        var workflow = CreateWorkflow(
            secretProvider: secretProvider,
            providerOrchestrator: providerOrchestrator);

        using var promptOut = new StringWriter();
        var ticket = workflow.Execute(
            new WorkflowOptions(
                InputFilePath: _inputFilePath,
                PromptFilePath: _promptFilePath,
                ProviderInput: "openai",
                BacklogTypeInput: "Type02",
                DryRun: true,
                NonInteractive: false,
                GeminiLive: true),
            new StringReader(string.Empty),
            promptOut);

        Assert.Equal(secret, workflow.LastResolvedApiKey);
        var consoleOutput = promptOut.ToString();
        Assert.Contains("Enter OpenAI Gateway API key", consoleOutput);
        Assert.Contains(new string('*', secret.Length), consoleOutput);
        Assert.Contains("****8888", consoleOutput);
        Assert.DoesNotContain(secret, consoleOutput);
    }

    [Fact]
    public void Execute_WhenGenericProviderInteractivePromptWithKey_MasksKeystrokesAndConfirmsWithMaskedKey()
    {
        var secret = "azure-secret-4444";
        var keyQueue = new Queue<ConsoleKeyInfo>();
        foreach (var ch in secret)
        {
            keyQueue.Enqueue(new ConsoleKeyInfo(ch, ConsoleKey.NoName, false, false, false));
        }
        keyQueue.Enqueue(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        var secretProvider = new GeminiSecretProvider(
            environmentReader: _ => null,
            environmentWriter: (_, _) => { },
            keyReader: () => keyQueue.Dequeue(),
            isInteractive: () => true);

        var workflow = CreateWorkflow(secretProvider: secretProvider);

        using var promptOut = new StringWriter();
        var ticket = workflow.Execute(
            new WorkflowOptions(
                InputFilePath: _inputFilePath,
                PromptFilePath: _promptFilePath,
                ProviderInput: "azure",
                BacklogTypeInput: "Type02",
                DryRun: true,
                NonInteractive: false),
            new StringReader(string.Empty),
            promptOut);

        Assert.Equal(secret, workflow.LastResolvedApiKey);
        var consoleOutput = promptOut.ToString();
        Assert.Contains("Enter API key for AzureOpenAi", consoleOutput);
        Assert.Contains(new string('*', secret.Length), consoleOutput);
        Assert.Contains("****4444", consoleOutput);
        Assert.DoesNotContain(secret, consoleOutput);
    }

    private static AsIsV1Workflow CreateWorkflow(
        GeminiSecretProvider? secretProvider = null,
        GeminiApiClient? geminiApiClient = null,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null,
        AiProviderOrchestrator? providerOrchestrator = null) =>
        new(
            new RawDataCollector(loggerFactory?.CreateLogger<RawDataCollector>()),
            new PromptTemplateService(),
            new AiModelOrchestrator(loggerFactory?.CreateLogger<AiModelOrchestrator>()),
            new JiraBacklogFormatter(),
            new RuntimeEnvironmentService(),
            secretProvider ?? new GeminiSecretProvider(
                environmentReader: _ => null,
                environmentWriter: (_, _) => { }),
            geminiApiClient,
            providerOrchestrator: providerOrchestrator,
            loggerFactory: loggerFactory);

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
