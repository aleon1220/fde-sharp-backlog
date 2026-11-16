using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class AiModelOrchestratorTests
{
    private readonly AiModelOrchestrator _sut = new();

    [Theory]
    [InlineData(null, AiProviderType.ManualPrompt)]
    [InlineData("", AiProviderType.ManualPrompt)]
    [InlineData("0", AiProviderType.ManualPrompt)]
    [InlineData("manual", AiProviderType.ManualPrompt)]
    [InlineData("copilot", AiProviderType.ManualPrompt)]
    [InlineData("1", AiProviderType.AzureOpenAi)]
    [InlineData("azure", AiProviderType.AzureOpenAi)]
    [InlineData("azure-openai", AiProviderType.AzureOpenAi)]
    [InlineData("2", AiProviderType.GoogleGemini)]
    [InlineData("gemini", AiProviderType.GoogleGemini)]
    [InlineData("google-gemini", AiProviderType.GoogleGemini)]
    [InlineData("3", AiProviderType.AnthropicClaude)]
    [InlineData("claude", AiProviderType.AnthropicClaude)]
    [InlineData("anthropic", AiProviderType.AnthropicClaude)]
    public void ResolveProvider_MapsUserInputToExpectedProvider(string? input, AiProviderType expected)
    {
        var actual = _sut.ResolveProvider(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Process_WithDefaultManualProvider_SuggestsRunningMarkdownPromptInDesiredAiModel()
    {
        var rawData = """
            Discovery Note - Bulk User Invite
            Context: Admins spend too long inviting users one by one.
            Requirement: As an admin, I want to upload a CSV of emails so that I can invite teams in bulk.
            Precondition: Admin is on the Users page.
            Action: Admin uploads a valid CSV file.
            Expected Outcome: Invitations are queued for all rows.
            """;

        var outcome = _sut.Process(rawData, "Rendered Prompt Content");

        Assert.Equal(AiProviderType.ManualPrompt, outcome.ProviderUsed);
        Assert.Contains("run it in your desired AI model", outcome.GuidanceMessage);
        Assert.Equal("Bulk User Invite", outcome.Title);
        Assert.Contains("As an admin, I want to upload a CSV", outcome.UserStory);
        var scenario = Assert.Single(outcome.Scenarios);
        Assert.Equal("Admin is on the Users page.", scenario.Given);
        Assert.Equal("Admin uploads a valid CSV file.", scenario.When);
        Assert.Equal("Invitations are queued for all rows.", scenario.Then);
    }

    [Fact]
    public void Process_WithMockedAzureOpenAiAndApiKey_MasksKeyInGuidanceMessage()
    {
        var rawData = "Title: Rate Limiting\nContext: Protect public endpoints.";

        var outcome = _sut.Process(
            rawData,
            "Rendered Prompt",
            AiProviderType.AzureOpenAi,
            apiKey: "az-secret-key-9876");

        Assert.Equal(AiProviderType.AzureOpenAi, outcome.ProviderUsed);
        Assert.Contains("Azure OpenAI", outcome.GuidanceMessage);
        Assert.Contains("****9876", outcome.GuidanceMessage);
        Assert.DoesNotContain("az-secret-key-9876", outcome.GuidanceMessage);
    }

    [Fact]
    public void Process_WithGeneratedType02Response_RequiresOnlyTitleAndDescription()
    {
        var outcome = _sut.Process(
            rawData: "Goal: Improve onboarding",
            renderedPrompt: "Type02 prompt",
            provider: AiProviderType.GoogleGemini,
            generatedContent: """
                { "title": "Improve onboarding", "description": "Clarify the onboarding workflow." }
                """,
            backlogType: BacklogType.Type02);

        Assert.Equal("Improve onboarding", outcome.Title);
        Assert.Equal("Clarify the onboarding workflow.", outcome.Description);
        Assert.Empty(outcome.UserStory);
        Assert.Empty(outcome.Scenarios);
    }

    [Fact]
    public void Process_OfflineGoalDraft_DoesNotInventContextOrRepeatGoalInEveryField()
    {
        var outcome = _sut.Process(
            rawData: "Goal: Implement account recovery",
            renderedPrompt: "Rendered prompt",
            provider: AiProviderType.GoogleGemini,
            backlogType: BacklogType.Type01);

        Assert.Equal("Implement account recovery", outcome.Title);
        Assert.Equal("Not specified in discovery notes.", outcome.Description);
        Assert.Equal("Not specified in discovery notes.", outcome.UserStory);
        var scenario = Assert.Single(outcome.Scenarios);
        Assert.Equal("Not specified in discovery notes.", scenario.Given);
        Assert.Equal("Not specified in discovery notes.", scenario.When);
        Assert.Equal("Not specified in discovery notes.", scenario.Then);
    }

    [Fact]
    public void ValidateOutcome_WithMissingScenarioFields_ThrowsInvalidOperationException()
    {
        var invalidOutcome = new ProcessedOutcome(
            ProviderUsed: AiProviderType.ManualPrompt,
            RenderedPrompt: "Prompt",
            GuidanceMessage: "Guidance",
            Title: "Valid Title",
            Description: "Valid Description",
            UserStory: "As a user...",
            Scenarios: [new GherkinScenario(Given: " state ", When: "", Then: "result")]);

        Assert.Throws<InvalidOperationException>(() => _sut.ValidateOutcome(invalidOutcome));
    }

    [Fact]
    public void Process_WithProseWrappedJsonAndObjectDescription_FormatsCleanMarkdownDescription()
    {
        var generated = """
            Here is your structured Jira Schedule Item:

            ```json
            {
              "summary": "Verify Geocaching 2026 Hiding Movie Theme",
              "description": {
                "Objective": "Confirm theme rules and submission dates.",
                "Checklist": [
                  "Review official Geocaching HQ announcement",
                  "Validate souvenir eligibility dates"
                ]
              }
            }
            ```
            """;

        var outcome = _sut.Process(
            rawData: "Goal: check Geocaching 2026 hiding movie theme",
            renderedPrompt: "Type02 prompt",
            provider: AiProviderType.GoogleGemini,
            generatedContent: generated,
            backlogType: BacklogType.Type02);

        Assert.Equal("Verify Geocaching 2026 Hiding Movie Theme", outcome.Title);
        Assert.Contains("**Objective:** Confirm theme rules and submission dates.", outcome.Description);
        Assert.Contains("- Review official Geocaching HQ announcement", outcome.Description);
        Assert.Contains("- Validate souvenir eligibility dates", outcome.Description);
    }

    [Fact]
    public void Process_OfflineGoalWithAttachedContextWebFindingsAndRefinement_PreservesAllDetailsInDescription()
    {
        var rawData = """
            Goal: Check Geocaching 2026 hiding movie theme

            --- Context (@notes.txt) ---
            Include official HQ campaign rules.

            Context: Confirm July 2026 souvenir window

            Web Research Findings:
            - Geocaching HQ 2026 Spotlight theme focuses on hidden-in-plain-sight cinematic tributes.

            Refinement Instructions:
            Emphasize reviewer checklist items
            """;

        var outcome = _sut.Process(
            rawData: rawData,
            renderedPrompt: "Rendered prompt",
            provider: AiProviderType.GoogleGemini,
            backlogType: BacklogType.Type02);

        Assert.Equal("Check Geocaching 2026 hiding movie theme", outcome.Title);
        Assert.Contains("Include official HQ campaign rules.", outcome.Description);
        Assert.Contains("Confirm July 2026 souvenir window", outcome.Description);
        Assert.Contains("Web Research Findings:", outcome.Description);
        Assert.Contains("Refinement Notes: Emphasize reviewer checklist items", outcome.Description);
        Assert.DoesNotContain("Not specified in discovery notes.", outcome.Description);
    }

    [Fact]
    public void ParseGoalExpansionResponse_ParsesEnhancedTitleUserStoryChecklistAndAlternatives()
    {
        var json = """
            ```json
            {
              "enhancedTitle": "Verify & Document Official Geocaching 2026 Movie Theme",
              "userStory": "As a player coordinator, I want to verify 2026 movie hiding rules so that our cache submission qualifies.",
              "discoverySummary": "**Objective:** Confirm theme rules and dates.\n**Verification / Action Checklist:**\n- Check HQ announcement",
              "keyChecklist": ["Check HQ announcement", "Confirm souvenir dates"],
              "alternatives": [
                { "angle": "Action & Delivery", "title": "Publish 2026 Theme Guide", "description": "Deliver actionable hiding checklist." },
                { "angle": "User Value / Story", "title": "Enable Movie Cache Submissions", "description": "Help hiders meet criteria easily." },
                { "angle": "Verification & Scope", "title": "Audit 2026 Movie Rules", "description": "Confirm eligibility dates and restrictions." }
              ]
            }
            ```
            """;

        var result = _sut.ParseGoalExpansionResponse(json, "check Geocaching 2026 hiding movie theme");

        Assert.Equal("Verify & Document Official Geocaching 2026 Movie Theme", result.EnhancedTitle);
        Assert.Contains("As a player coordinator", result.UserStory);
        Assert.Contains("Confirm theme rules and dates", result.DiscoverySummary);
        Assert.Equal(2, result.KeyChecklist.Count);
        Assert.Equal(3, result.Alternatives.Count);
        Assert.Equal("Publish 2026 Theme Guide", result.Alternatives[0].Title);
    }

    [Fact]
    public void Process_WithSynthesizedGoalBrief_ExtractsExpandedDescriptionCleanlyWithoutAbsorbingChecklistHeader()
    {
        var expansion = GoalExpansionSynthesizer.ExpandOffline(
            "check Geocaching 2026 hiding movie theme",
            webResearchFindings: "- Cinema theme announced for 2026.");
        var merged = GoalExpansionSynthesizer.MergeExpansionIntoRawData(
            "Goal: check Geocaching 2026 hiding movie theme",
            expansion,
            includeSyntheticGwt: true);

        var outcome = _sut.Process(
            rawData: merged,
            renderedPrompt: "Type02 prompt",
            provider: AiProviderType.GoogleGemini,
            backlogType: BacklogType.Type02);

        Assert.Equal("Check Geocaching 2026 hiding movie theme", outcome.Title);
        Assert.Contains("**Objective:**", outcome.Description);
        Assert.Contains("**Web Research Insights:**", outcome.Description);
        Assert.Contains("**Verification / Action Checklist:**", outcome.Description);
        Assert.DoesNotContain("Expanded Checklist:", outcome.Description);
        Assert.DoesNotContain("Precondition:", outcome.Description);
        Assert.StartsWith("As a ", outcome.UserStory);
    }

    [Fact]
    public void Process_WithGeneratedJsonIncludingAlternatives_AttachesAlternativesToOutcome()
    {
        var json = """
            {
              "title": "Verify Geocaching 2026 Theme",
              "description": "**Objective:** Verify official guidelines.",
              "alternatives": [
                { "angle": "Action & Delivery", "title": "Alt Title 1", "description": "Alt Desc 1." },
                { "angle": "User Value / Story", "title": "Alt Title 2", "description": "Alt Desc 2." },
                { "angle": "Verification & Scope", "title": "Alt Title 3", "description": "Alt Desc 3." }
              ]
            }
            """;

        var outcome = _sut.Process(
            rawData: "Goal: check Geocaching 2026 hiding movie theme",
            renderedPrompt: "Type02 prompt",
            provider: AiProviderType.GoogleGemini,
            generatedContent: json,
            backlogType: BacklogType.Type02);

        Assert.NotNull(outcome.Alternatives);
        Assert.Equal(3, outcome.Alternatives.Count);
        Assert.Equal("Alt Title 2", outcome.Alternatives[1].Title);
        Assert.Equal("Alt Desc 2.", outcome.Alternatives[1].Description);
    }
}
