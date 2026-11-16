using SmartBacklogAssistant.Services;
using SmartBacklogAssistant.Workflow;
using System.Net;
using System.Text;

namespace SmartBacklogAssistant.Tests.Integration;

public sealed class CliMenuRunnerTests : IDisposable
{
    private readonly string _tempWorkspaceRoot;
    private readonly string _promptFilePath;

    public CliMenuRunnerTests()
    {
        _tempWorkspaceRoot = Path.Combine(Path.GetTempPath(), $"sba-menu-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempWorkspaceRoot);

        _promptFilePath = Path.Combine(_tempWorkspaceRoot, "as-is-v1-prompt.md");
        File.WriteAllText(_promptFilePath, """
            # As-Is v1 Prompt
            Analyze the following raw notes:
            {{RAW_DATA}}
            """);
    }

    [Fact]
    public void Run_ProcessAllFiles_UsesInputDirectoryAndWritesToOutputDirectory()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);
        Directory.CreateDirectory(dirs.InputDirectory);
        Directory.CreateDirectory(dirs.OutputDirectory);

        File.WriteAllText(Path.Combine(dirs.InputDirectory, "discovery-01.txt"), """
            Discovery Note - Automated Failover Check
            Context: Regional database failover requires manual intervention today.
            Requirement: As an SRE, I want automated health-probe failover so that regional outages recover within 60 seconds.
            Precondition: Primary database region becomes unreachable.
            Action: Health monitor detects 3 consecutive probe failures.
            Expected Outcome: Traffic routes to standby region automatically.
            """);

        using var input = new StringReader("1\n1\n01\n0\n0\n");
        using var output = new StringWriter();

        var menu = CreateMenuRunner(workspaceService);
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            ApiKey: "AIzaSyMenuTestKey1234",
            WorkspaceRoot: _tempWorkspaceRoot,
            GeminiLive: false);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "jira-backlog-item.md")));
        Assert.False(File.Exists(Path.Combine(dirs.OutputDirectory, "jira-backlog-item.json")));
        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "type01-automated-failover-check.md")));
        Assert.False(File.Exists(Path.Combine(dirs.OutputDirectory, "type01-automated-failover-check.json")));

        var consoleText = output.ToString();
        Assert.Contains("Smart Backlog Assistant", consoleText);
        Assert.Contains("SHARP BACKLOG ASSISTANT", consoleText);
        Assert.Contains("Process Input Files", consoleText);
        Assert.Contains("In: ./in (1 files)   │   Out: ./out   │   Export: .md (JSON: OFF)", consoleText);
        Assert.Contains("Automated Failover Check", consoleText);
    }

    [Fact]
    public void Run_DryRunPreviewsSelectedFileWithoutWritingArtifacts()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);
        Directory.CreateDirectory(dirs.InputDirectory);
        Directory.CreateDirectory(dirs.OutputDirectory);
        File.WriteAllText(Path.Combine(dirs.InputDirectory, "preview.txt"), "Title: Preview only\nContext: Validate dry-run behavior.");

        using var input = new StringReader("1\n3\n01\n0\n0\n");
        using var output = new StringWriter();
        var menu = CreateMenuRunner(workspaceService);
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            ApiKey: "AIzaSyDryRunKey1234",
            WorkspaceRoot: _tempWorkspaceRoot,
            GeminiLive: false);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);
        Assert.Empty(Directory.GetFiles(dirs.OutputDirectory));
        Assert.Contains("Dry Run (Preview Only)", output.ToString());
        Assert.Contains("No files written", output.ToString());
    }

    [Fact]
    public void Run_SettingsTogglePersistsAndUpdatesRootStatus()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var settingsService = new CliSettingsService();
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);

        using var input = new StringReader(
            "3\n1\n0\n" +
            "2\n1\nEnable JSON Export Setting\n0\n" +
            "0\n");
        using var output = new StringWriter();

        var menu = CreateMenuRunner(workspaceService, settingsService);
        var options = new WorkflowOptions(
            InputFilePath: "samples/raw-notes.txt",
            PromptFilePath: _promptFilePath,
            ApiKey: "AIzaSyMenuSettingsKey1234",
            WorkspaceRoot: _tempWorkspaceRoot,
            GeminiLive: false);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);

        var persistedSettings = settingsService.Load(dirs);
        Assert.True(persistedSettings.JsonFileOut);
        Assert.True(File.Exists(settingsService.GetSettingsFilePath(dirs)));

        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "jira-backlog-item.md")));
        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "jira-backlog-item.json")));
        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "type01-enable-json-export-setting.md")));
        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "type01-enable-json-export-setting.json")));

        var consoleText = output.ToString();
        Assert.Contains("Workspace & Settings", consoleText);
        Assert.Contains("JSON Export is now ON", consoleText);
        Assert.Contains("Export: .md (JSON: ON)", consoleText);
    }

    [Fact]
    public void Run_Option2_AddsGoalOrTitleWithType01AndType02ReadyInOutDirectory()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);

        using var input = new StringReader(
            "2\n1\nImplement OAuth2 PKCE Login\n" +
            "2\nMigrate Telemetry to OpenTelemetry\n" +
            "3\nGenerate Both OAuth2 Formats\n0\n" +
            "3\n3\n0\n0\n");
        using var output = new StringWriter();

        var menu = CreateMenuRunner(workspaceService);
        var options = new WorkflowOptions(
            InputFilePath: "samples/raw-notes.txt",
            PromptFilePath: _promptFilePath,
            ApiKey: "AIzaSyMenuKey7788",
            WorkspaceRoot: _tempWorkspaceRoot,
            GeminiLive: false);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);

        var type01MdPath = Path.Combine(dirs.OutputDirectory, "type01-implement-oauth2-pkce-login.md");
        var type02MdPath = Path.Combine(dirs.OutputDirectory, "type02-migrate-telemetry-to-opentelemetry.md");
        var combinedType01Path = Path.Combine(dirs.OutputDirectory, "type01-generate-both-oauth2-formats.md");
        var combinedType02Path = Path.Combine(dirs.OutputDirectory, "type02-generate-both-oauth2-formats.md");

        Assert.True(File.Exists(type01MdPath));
        Assert.True(File.Exists(type02MdPath));
        Assert.True(File.Exists(combinedType01Path));
        Assert.True(File.Exists(combinedType02Path));

        var type01Content = File.ReadAllText(type01MdPath);
        Assert.Contains("# [Jira - complete (Type01)] Implement OAuth2 PKCE Login", type01Content);
        Assert.Contains("## Acceptance Criteria (Given / When / Then)", type01Content);

        var type02Content = File.ReadAllText(type02MdPath);
        Assert.Contains("# [Jira - schedule (Type02)] Migrate Telemetry to OpenTelemetry", type02Content);
        Assert.Contains("## Description", type02Content);

        var consoleText = output.ToString();
        Assert.Contains(@"Windows: c:\workspace\smart-backlog-assistant\in", consoleText);
        Assert.Contains("WSL    : /mnt/c/workspace/smart-backlog-assistant/in", consoleText);
    }

    [Fact]
    public void Run_DraftGoal_CallsGeminiAndUsesGeneratedResponse()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);
        var requestMethods = new List<HttpMethod>();
        var requestBodies = new List<string>();
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestMethods.Add(request.Method);
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

            requestBodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""
                {
                  "candidates": [{
                    "content": {
                      "parts": [{
                        "text": "{\"title\":\"Account recovery\",\"description\":\"Users can recover account access.\",\"userStory\":\"As a user, I want to recover my account, so that I can regain access.\",\"scenarios\":[{\"given\":\"I cannot sign in\",\"when\":\"I request account recovery\",\"then\":\"I can regain access\"}]}"
                      }]
                    }
                  }]
                }
                """);
        }));
        var menu = CreateMenuRunner(workspaceService, geminiApiClient: new GeminiApiClient(httpClient));
        using var input = new StringReader("2\n1\nImplement account recovery\n\n0\n0\n");
        using var output = new StringWriter();
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            ApiKey: "test-key-not-a-real-credential",
            WorkspaceRoot: _tempWorkspaceRoot);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Post }, requestMethods);
        var generationRequest = Assert.Single(requestBodies);
        Assert.Contains("Goal: Implement account recovery", generationRequest);
        Assert.DoesNotContain("Context: Backlog goal added", generationRequest);
        Assert.Contains("[2/5 Local -> Process] Calling Google Gemini API", output.ToString());
        Assert.Contains("Account recovery", File.ReadAllText(Path.Combine(dirs.OutputDirectory, "type01-account-recovery.md")));
    }

    [Fact]
    public void Run_DraftPreview_DoesNotSaveArtifacts()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);
        var menu = CreateMenuRunner(workspaceService);
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            WorkspaceRoot: _tempWorkspaceRoot,
            GeminiLive: false);
        using var output = new StringWriter();

        var exitCode = menu.Run(options, new StringReader("2\n4\n1\nImplement preview\n2\ncheck Geocaching 2026 hiding movie theme\n3\nMigrate Auth Sessions\n0\n0\n0\n"), output);

        Assert.Equal(0, exitCode);
        Assert.Empty(Directory.GetFiles(dirs.OutputDirectory));
        var text = output.ToString();
        Assert.Contains("=== Preview Draft (No Files Saved) ===", text);
        Assert.Contains("Goal Tips for Better Results:", text);
        Assert.Contains("[Preview] Type01 item 'Implement preview' generated. No files written.", text);
        Assert.Contains("[Preview] Type02 item 'Check Geocaching 2026 hiding movie theme' generated. No files written.", text);
        Assert.Contains("[Preview] Type01 and Type02 items 'Migrate Auth Sessions' generated. No files written.", text);
        Assert.Contains("Alternatives", text);
        Assert.Contains("[Action & Delivery]", text);
        Assert.DoesNotContain("Not specified in discovery notes", text);
    }

    [Fact]
    public void Run_DisablingLiveGeminiPersistsAndSkipsApiForNextInteractiveDraft()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var settingsService = new CliSettingsService();
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);
        var apiRequestCount = 0;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            apiRequestCount++;
            return JsonResponse("{}");
        }));
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            WorkspaceRoot: _tempWorkspaceRoot);

        var settingsMenu = CreateMenuRunner(workspaceService, settingsService, new GeminiApiClient(httpClient));
        settingsMenu.Run(options, new StringReader("3\n5\n0\n0\n"), new StringWriter());

        var draftMenu = CreateMenuRunner(workspaceService, settingsService, new GeminiApiClient(httpClient));
        using var output = new StringWriter();
        draftMenu.Run(options, new StringReader("2\n1\nImplement account recovery\n0\n0\n"), output);

        Assert.False(settingsService.Load(dirs).GeminiLive);
        Assert.Equal(0, apiRequestCount);
        Assert.Contains("Live Gemini API is OFF", output.ToString());
        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "type01-implement-account-recovery.md")));
    }

    [Fact]
    public void Run_SettingsCanInspectDirectorySizesAndRuntimeDiagnostics()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);
        Directory.CreateDirectory(dirs.InputDirectory);
        Directory.CreateDirectory(dirs.OutputDirectory);
        File.WriteAllText(Path.Combine(dirs.InputDirectory, "queued.txt"), "queued");
        File.WriteAllText(Path.Combine(dirs.OutputDirectory, "existing.md"), "artifact");

        using var input = new StringReader("3\n2\n3\n0\n0\n");
        using var output = new StringWriter();
        var menu = CreateMenuRunner(workspaceService);
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            WorkspaceRoot: _tempWorkspaceRoot,
            GeminiLive: false);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);
        Assert.Contains("queued.txt (6 bytes)", output.ToString());
        Assert.Contains("existing.md (8 bytes)", output.ToString());
        Assert.Contains("GEMINI_API_KEY:", output.ToString());
        Assert.Contains("AI Model Enabled", output.ToString());
    }

    [Fact]
    public void Run_ThemeSelectorSubMenu_DefaultsToAgyAndPersistsUvAndKubectlThemes()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var settingsService = new CliSettingsService();
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);

        // 3 (Workspace & Settings) -> 4 (Select CLI Theme) -> 2 (UV Style) -> 4 (Select CLI Theme) -> 3 (Kubectl Style) -> 0 (Back) -> 0 (Exit)
        using var input = new StringReader("3\n4\n2\n4\n3\n0\n0\n");
        using var output = new StringWriter();

        var menu = CreateMenuRunner(workspaceService, settingsService);
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            WorkspaceRoot: _tempWorkspaceRoot,
            GeminiLive: false);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);
        var consoleText = output.ToString();
        Assert.Contains("Theme: agy", consoleText);
        Assert.Contains("=== CLI Theme Configuration ===", consoleText);
        Assert.Contains("CLI Theme is now uv (Astral uv Style)", consoleText);
        Assert.Contains("CLI Theme is now kubectl (Kubernetes CLI Style)", consoleText);
        Assert.Contains("Theme: kubectl", consoleText);

        var persistedSettings = settingsService.Load(dirs);
        Assert.Equal(SmartBacklogAssistant.Domain.CliThemeStyle.Kubectl, persistedSettings.Theme);
    }

    [Fact]
    public void Run_SlashCommandsInMainRepl_UpdateSettingsRenderStatusAndRunInlineDrafts()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var settingsService = new CliSettingsService();
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);
        Directory.CreateDirectory(dirs.InputDirectory);
        File.WriteAllText(Path.Combine(dirs.InputDirectory, "notes.txt"), "Title: Slash Note\nContext: Validate slash processing.");

        using var input = new StringReader(
            "/help\n" +
            "/status\n" +
            "/theme uv\n" +
            "/json on\n" +
            "/provider copilot-sdk\n" +
            "/live off\n" +
            "/process notes.txt --dry-run\n" +
            "/draft \"Inline Slash Draft\" -t 02 --preview\n" +
            "/exit\n");
        using var output = new StringWriter();

        var menu = CreateMenuRunner(workspaceService, settingsService);
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            WorkspaceRoot: _tempWorkspaceRoot,
            GeminiLive: false);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Interactive REPL Commands & Context Mentions", text);
        Assert.Contains("Smart Backlog Assistant — Workspace & AI Status", text);
        Assert.Contains("CLI Theme is now uv (Astral uv Style)", text);
        Assert.Contains("JSON Export is now ON", text);
        Assert.Contains("Active AI Provider set to 'copilot-sdk'", text);
        Assert.Contains("Export: .md (JSON: ON)", text);
        Assert.Contains("Provider: copilot-sdk", text);
        Assert.Contains("[Preview] Type02 item 'Inline Slash Draft' generated", text);
        Assert.Empty(Directory.GetFiles(dirs.OutputDirectory));

        var saved = settingsService.Load(dirs);
        Assert.Equal(SmartBacklogAssistant.Domain.CliThemeStyle.Uv, saved.Theme);
        Assert.Equal(SmartBacklogAssistant.Domain.AiProviderType.GitHubCopilot, saved.PreferredProvider);
        Assert.True(saved.JsonFileOut);
        Assert.False(saved.GeminiLive);
    }

    [Fact]
    public void Run_AtFileContextMentions_AttachContextForDraftsOrHandleMissingGracefully()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);
        Directory.CreateDirectory(dirs.InputDirectory);
        File.WriteAllText(Path.Combine(dirs.InputDirectory, "discovery.txt"), """
            Context: Existing checkout takes 4 clicks.
            Requirement: As a shopper, I want 1-click checkout so that conversion increases.
            Precondition: Cart contains items.
            Action: Shopper confirms order.
            Expected Outcome: Order completes in one step.
            """);

        using var input = new StringReader(
            "@missing-file.txt\n" +
            "@discovery.txt Optimize Checkout Flow --dry-run\n" +
            "@\n" +
            "/exit\n");
        using var output = new StringWriter();

        var menu = CreateMenuRunner(workspaceService);
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            WorkspaceRoot: _tempWorkspaceRoot,
            GeminiLive: false);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Could not resolve context reference(s): @missing-file.txt", text);
        Assert.Contains("✓ Attached context '@discovery.txt'", text);
        Assert.Contains("[Preview] 'Optimize Checkout Flow' generated from @context", text);
        Assert.Contains("Available @context files in", text);
        Assert.Empty(Directory.GetFiles(dirs.OutputDirectory));
    }

    [Fact]
    public void Run_SettingsOption7And8_DisplaysPersistedJsonAndSelectsCopilotSdkProvider()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var settingsService = new CliSettingsService();
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);

        // 3 (Settings) -> 1 (Toggle JSON ON so settings.json exists) -> 7 (View settings.json) -> 8 (Select Provider) -> 2 (copilot-sdk) -> 0 (Back) -> 0 (Exit)
        using var input = new StringReader("3\n1\n7\n8\n2\n0\n0\n");
        using var output = new StringWriter();

        var menu = CreateMenuRunner(workspaceService, settingsService);
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            WorkspaceRoot: _tempWorkspaceRoot,
            GeminiLive: false);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Settings JSON (", text);
        Assert.Contains("\"jsonFileOut\": true", text);
        Assert.Contains("=== AI Provider Orchestrator ===", text);
        Assert.Contains("Active AI Provider set to 'copilot-sdk'", text);
        Assert.Equal(SmartBacklogAssistant.Domain.AiProviderType.GitHubCopilot, settingsService.Load(dirs).PreferredProvider);
    }

    [Fact]
    public void Run_DraftType02WithInlineDetailsAndSearch_RunsGroundingAndSavesVerifiedDraftWithoutExtraPrompts()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);
        var requestMethods = new List<HttpMethod>();
        var postBodies = new List<string>();
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestMethods.Add(request.Method);
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
            postBodies.Add(body);
            if (body.Contains("google_search", StringComparison.Ordinal))
            {
                return JsonResponse("""
                    {
                      "candidates": [{
                        "content": { "parts": [{ "text": "- Official 2026 Geocaching movie hiding theme covers blockbuster genres." }] },
                        "groundingMetadata": {
                          "groundingChunks": [{ "web": { "title": "Geocaching Blog", "uri": "https://www.geocaching.com/blog/" } }]
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
                        "text": "{\"title\":\"Verify Geocaching 2026 Hiding Movie Theme Rules\",\"description\":\"Objective: Confirm 2026 hiding movie theme guidelines and promo window.\\nChecklist:\\n- Verify official blog post\"}"
                      }]
                    }
                  }]
                }
                """);
        }));

        var menu = CreateMenuRunner(workspaceService, geminiApiClient: new GeminiApiClient(httpClient));
        using var input = new StringReader(
            "2\n2\ncheck Geocaching 2026 hiding movie theme | Verify official HQ rules and submission dates --search\n\n0\n0\n");
        using var output = new StringWriter();
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            ApiKey: "test-key-inline-search",
            WorkspaceRoot: _tempWorkspaceRoot);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Post }, requestMethods);
        Assert.Contains("google_search", postBodies[0]);
        Assert.Contains("Context: Verify official HQ rules and submission dates", postBodies[1]);
        Assert.Contains("Web Research Findings:", postBodies[1]);
        Assert.Contains("blockbuster genres", postBodies[1]);

        var savedPath = Path.Combine(dirs.OutputDirectory, "type02-verify-geocaching-2026-hiding-movie-theme-rules.md");
        Assert.True(File.Exists(savedPath));
        Assert.Contains("Confirm 2026 hiding movie theme guidelines", File.ReadAllText(savedPath));
    }

    [Fact]
    public void Run_GuidedDraftWizard_SupportsSearchRefinementTypeSwitchAndAcceptSave()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);
        var postBodies = new List<string>();
        var generationCallCount = 0;

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
            postBodies.Add(body);

            if (body.Contains("google_search", StringComparison.Ordinal))
            {
                return JsonResponse("""
                    {
                      "candidates": [{
                        "content": { "parts": [{ "text": "- 2026 Geocaching hiding theme spotlights classic cinema tributes." }] },
                        "groundingMetadata": {
                          "groundingChunks": [{ "web": { "title": "Geocaching HQ", "uri": "https://www.geocaching.com/blog/" } }]
                        }
                      }]
                    }
                    """);
            }

            generationCallCount++;
            return generationCallCount switch
            {
                1 => JsonResponse("""
                    {
                      "candidates": [{
                        "content": { "parts": [{ "text": "{\"title\":\"Draft v1 Theme Check\",\"description\":\"Initial cinema theme verification.\"}" }] }
                      }]
                    }
                    """),
                2 => JsonResponse("""
                    {
                      "candidates": [{
                        "content": { "parts": [{ "text": "{\"title\":\"Refined 2026 Geocaching Movie Theme Check\",\"description\":\"Refined scope with souvenir deadline and HQ blog link.\"}" }] }
                      }]
                    }
                    """),
                _ => JsonResponse("""
                    {
                      "candidates": [{
                        "content": { "parts": [{ "text": "{\"title\":\"Refined 2026 Geocaching Movie Theme Check\",\"description\":\"Refined scope with souvenir deadline and HQ blog link.\",\"userStory\":\"As a player coordinator, I want to verify 2026 movie hiding rules so that our cache submission qualifies.\",\"scenarios\":[{\"given\":\"2026 campaign is active\",\"when\":\"A themed cache is submitted\",\"then\":\"Souvenir eligibility is confirmed\"}]}" }] }
                      }]
                    }
                    """)
            };
        }));

        var menu = CreateMenuRunner(workspaceService, geminiApiClient: new GeminiApiClient(httpClient));
        using var input = new StringReader(
            "2\n5\n" +
            "2\n" +
            "check Geocaching 2026 hiding movie theme\n" +
            "Verify 2026 movie hiding theme guidelines\n" +
            "y\n" +
            "\n" +
            "r\n" +
            "Add souvenir deadline and blog link\n" +
            "t\n1\n" +
            "t\n2\n" +
            "a\n" +
            "0\n0\n");
        using var output = new StringWriter();
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            ApiKey: "test-key-guided-wizard",
            WorkspaceRoot: _tempWorkspaceRoot);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, postBodies.Count); // 1 search + 3 generation passes (initial Type02, refine, switch Type02->Type01; switch back to Type02 & Accept reuse outcome with 0 extra HTTP calls)
        Assert.Contains("google_search", postBodies[0]);
        Assert.Contains("Previous Draft:", postBodies[2]);
        Assert.Contains("Draft v1 Theme Check", postBodies[2]);
        Assert.Contains("Refinement Instructions:", postBodies[2]);
        Assert.Contains("Add souvenir deadline and blog link", postBodies[2]);
        Assert.Contains("Web Research Findings:", postBodies[2]);

        var consoleText = output.ToString();
        Assert.Contains("[4/5 Review Decision]", consoleText);
        Assert.Contains("Ready-to-add Type02 item 'Refined 2026 Geocaching Movie Theme Check' saved", consoleText);

        var finalFile = Path.Combine(dirs.OutputDirectory, "type02-refined-2026-geocaching-movie-theme-check.md");
        Assert.True(File.Exists(finalFile));
        Assert.Contains("Refined scope with souvenir deadline and HQ blog link.", File.ReadAllText(finalFile));
        Assert.False(File.Exists(Path.Combine(dirs.OutputDirectory, "type01-draft-v1-theme-check.md")));
        Assert.False(File.Exists(Path.Combine(dirs.OutputDirectory, "type02-draft-v1-theme-check.md")));
    }

    [Fact]
    public void Run_GuidedDraftWizardCancelAndSlashDraftDetails_HandleNoWriteAndContextCleanly()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);
        var menu = CreateMenuRunner(workspaceService);

        using var input = new StringReader(
            "2\n5\n2\nDraft to Cancel\nInitial details\n" +
            "c\n" +
            "0\n" +
            "/draft \"Slash Draft With Details\" -t 02 -d \"Verify release date & reviewer rules\" --preview\n" +
            "/exit\n");
        using var output = new StringWriter();
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            WorkspaceRoot: _tempWorkspaceRoot,
            GeminiLive: false);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);
        Assert.Empty(Directory.GetFiles(dirs.OutputDirectory));
        var consoleText = output.ToString();
        Assert.Contains("Draft cancelled. No files written.", consoleText);
        Assert.Contains("Verify release date & reviewer rules", consoleText);
        Assert.Contains("[Preview] Type02 item 'Slash Draft With Details' generated. No files written.", consoleText);
    }

    [Fact]
    public void Run_GuidedDraftWizard_AdoptsAlternativeAndSavesUpdatedTicket()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);
        var menu = CreateMenuRunner(workspaceService);

        using var input = new StringReader(
            "2\n5\n" +
            "2\n" +
            "check Geocaching 2026 hiding movie theme\n" +
            "\n" +
            "2\n" +
            "a\n" +
            "0\n0\n");
        using var output = new StringWriter();
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            WorkspaceRoot: _tempWorkspaceRoot,
            GeminiLive: false);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);
        var consoleText = output.ToString();
        Assert.Contains("[1-3] Adopt Alternative Title & Description (#1..#3)", consoleText);
        Assert.Contains("✓ Adopted Alternative #2 (User Value / Story): 'Align Stakeholder Criteria for Geocaching 2026 Hiding Movie Theme'", consoleText);

        var savedFile = Path.Combine(dirs.OutputDirectory, "type02-align-stakeholder-criteria-for-geocaching-2026-hiding-movie-theme.md");
        Assert.True(File.Exists(savedFile));
        var content = File.ReadAllText(savedFile);
        Assert.Contains("# [Jira - schedule (Type02)] Align Stakeholder Criteria for Geocaching 2026 Hiding Movie Theme", content);
        Assert.Contains("**Objective (User Value / Story):**", content);
    }

    [Fact]
    public void Run_MenuOption4PreviewAndGuidedWizardCopy_ReadsMarkdownPreviewsTableAndCopiesBothFormats()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);
        var clipboard = new FakeClipboardService();
        var menu = CreateMenuRunner(workspaceService, clipboardService: clipboard);

        // 1) Guided wizard: draft Type02, copy Raw Text [W], copy Markdown [M], Accept & Save [A], Back [0]
        // 2) Main menu [4] Preview & Copy (.md): select #1, copy Raw Text [1], copy Markdown [2], Back [0]
        // 3) REPL /copy md, then /exit
        using var input = new StringReader(
            "2\n5\n" +
            "2\n" +
            "Check Geocaching 2026 hiding movie theme\n" +
            "\n" +
            "w\n" +
            "m\n" +
            "a\n" +
            "0\n" +
            "4\n" +
            "\n" +
            "1\n" +
            "2\n" +
            "0\n" +
            "/preview type02-check-geocaching-2026-hiding-movie-theme\n" +
            "/copy raw\n" +
            "/exit\n");
        using var output = new StringWriter();
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            WorkspaceRoot: _tempWorkspaceRoot,
            GeminiLive: false);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);
        var consoleText = output.ToString();
        Assert.Contains("=== Preview & Copy Outcome (Markdown) ===", consoleText);
        Assert.Contains("✓ Copied Raw Text outcome to clipboard", consoleText);
        Assert.Contains("✓ Copied Markdown outcome to clipboard", consoleText);

        Assert.Equal(5, clipboard.CopiedPayloads.Count);
        // [0] Raw Text from Guided Wizard
        Assert.Contains("┌", clipboard.CopiedPayloads[0]);
        Assert.Contains("* Gather official rules", clipboard.CopiedPayloads[0]);
        // [1] Markdown from Guided Wizard
        Assert.StartsWith("# [Jira - schedule (Type02)] Check Geocaching 2026 hiding movie theme", clipboard.CopiedPayloads[1]);
        Assert.DoesNotContain("┌", clipboard.CopiedPayloads[1]);
        // [2] Raw Text from Menu [4] reading saved .md
        Assert.Contains("┌", clipboard.CopiedPayloads[2]);
        Assert.Contains("* Gather official rules", clipboard.CopiedPayloads[2]);
        // [3] Markdown from Menu [4] reading saved .md
        Assert.StartsWith("# [Jira - schedule (Type02)] Check Geocaching 2026 hiding movie theme", clipboard.CopiedPayloads[3]);
        // [4] Raw Text from /preview + /copy raw
        Assert.Contains("┌", clipboard.CopiedPayloads[4]);
    }

    private static CliMenuRunner CreateMenuRunner(
        WorkspacePathService workspaceService,
        CliSettingsService? settingsService = null,
        GeminiApiClient? geminiApiClient = null,
        IClipboardService? clipboardService = null)
    {
        var secretProvider = new GeminiSecretProvider(
            environmentReader: _ => null,
            environmentWriter: (_, _) => { });
        var runtimeService = new RuntimeEnvironmentService();
        var orchestrator = new AiModelOrchestrator();

        var workflow = new AsIsV1Workflow(
            new RawDataCollector(),
            new PromptTemplateService(),
            orchestrator,
            new JiraBacklogFormatter(),
            runtimeService,
            secretProvider,
            geminiApiClient);

        return new CliMenuRunner(
            workflow,
            workspaceService,
            runtimeService,
            secretProvider,
            orchestrator,
            settingsService ?? new CliSettingsService(),
            clipboardService: clipboardService);
    }

    private sealed class FakeClipboardService : IClipboardService
    {
        public List<string> CopiedPayloads { get; } = [];

        public ClipboardCopyResult CopyText(string text)
        {
            CopiedPayloads.Add(text);
            return new ClipboardCopyResult(true, "fake-clipboard", "Copied via fake-clipboard.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempWorkspaceRoot))
        {
            Directory.Delete(_tempWorkspaceRoot, recursive: true);
        }
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
