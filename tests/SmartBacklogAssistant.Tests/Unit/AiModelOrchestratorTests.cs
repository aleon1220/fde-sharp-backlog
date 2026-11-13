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
}
