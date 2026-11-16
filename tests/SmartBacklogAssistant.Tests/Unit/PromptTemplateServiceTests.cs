using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class PromptTemplateServiceTests
{
    private readonly PromptTemplateService _sut = new();

    [Fact]
    public void RenderPromptFromTemplate_WithPlaceholder_ReplacesPlaceholderWithRawData()
    {
        var template = "# Prompt Header\nInput:\n{{RAW_DATA}}\n# Footer";
        var rawData = "Requirement: Add SSO login";

        var rendered = _sut.RenderPromptFromTemplate(template, rawData);

        Assert.Contains("Requirement: Add SSO login", rendered);
        Assert.DoesNotContain("{{RAW_DATA}}", rendered);
    }

    [Fact]
    public void RenderPromptFromTemplate_WithoutPlaceholder_AppendsRawDataSection()
    {
        var template = "You are a Senior PM.";
        var rawData = "Requirement: Add audit log export";

        var rendered = _sut.RenderPromptFromTemplate(template, rawData);

        Assert.StartsWith("You are a Senior PM.", rendered);
        Assert.Contains("## Raw Input Data", rendered);
        Assert.Contains("Requirement: Add audit log export", rendered);
    }

    [Theory]
    [InlineData(BacklogType.Type01, PromptTemplateService.Type01PromptFilePath, "Complete User Story")]
    [InlineData(BacklogType.Type02, PromptTemplateService.Type02PromptFilePath, "Schedule Item")]
    public void ResolvePromptFilePath_DefaultPathSelectsPromptForBacklogType(
        BacklogType backlogType,
        string expectedPath,
        string expectedTitle)
    {
        var resolvedPath = _sut.ResolvePromptFilePath(PromptTemplateService.DefaultPromptFilePath, backlogType);
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

        Assert.Equal(expectedPath, resolvedPath);
        Assert.Contains(expectedTitle, File.ReadAllText(Path.Combine(repositoryRoot, resolvedPath)));
    }

    [Fact]
    public void ResolvePromptFilePath_CustomPathRemainsAnOverride()
    {
        var resolvedPath = _sut.ResolvePromptFilePath("custom/prompt.md", BacklogType.Type02);

        Assert.Equal("custom/prompt.md", resolvedPath);
    }

    [Theory]
    [InlineData("", "Valid raw data")]
    [InlineData("   ", "Valid raw data")]
    public void RenderPromptFromTemplate_WithEmptyTemplate_ThrowsInvalidOperationException(string template, string rawData)
    {
        Assert.Throws<InvalidOperationException>(() => _sut.RenderPromptFromTemplate(template, rawData));
    }

    [Theory]
    [InlineData("Valid template", "")]
    [InlineData("Valid template", "   ")]
    public void RenderPromptFromTemplate_WithEmptyRawData_ThrowsArgumentException(string template, string rawData)
    {
        Assert.Throws<ArgumentException>(() => _sut.RenderPromptFromTemplate(template, rawData));
    }

    [Theory]
    [InlineData(PromptTemplateService.Type01PromptFilePath)]
    [InlineData(PromptTemplateService.Type02PromptFilePath)]
    public void GoalPrompts_EnforceActionableGoalModeWithoutEmptyBoilerplate(string promptRelativePath)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var content = File.ReadAllText(Path.Combine(repositoryRoot, promptRelativePath));

        Assert.Contains("Goal / Brief Topic Mode", content);
        Assert.Contains("Discovery Notes Mode", content);
        Assert.Contains("Refinement Rule", content);
        Assert.Contains("Objective", content);
        Assert.Contains("Web Research Findings:", content);
    }

    [Fact]
    public void Type02GoalPrompt_ProhibitsAddingUserStoryAndBoilerplateDismissals()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var content = File.ReadAllText(Path.Combine(repositoryRoot, PromptTemplateService.Type02PromptFilePath));

        Assert.Contains("Do **NOT** dismiss the request with `Not specified in discovery notes` boilerplate", content);
        Assert.Contains("Do not add a user story", content);
    }

    [Fact]
    public void GoalExpandPrompt_ContainsInvestPrinciplesTitleEnhancementAndAlternatives()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var content = File.ReadAllText(Path.Combine(repositoryRoot, PromptTemplateService.GoalExpandPromptFilePath));

        Assert.Contains("Goal-to-Notes Expansion", content);
        Assert.Contains("User Story creation principles", content);
        Assert.Contains("INVEST", content);
        Assert.Contains("enhancedTitle", content);
        Assert.Contains("userStory", content);
        Assert.Contains("discoverySummary", content);
        Assert.Contains("keyChecklist", content);
        Assert.Contains("alternatives", content);
    }

    [Theory]
    [InlineData(PromptTemplateService.Type01PromptFilePath)]
    [InlineData(PromptTemplateService.Type02PromptFilePath)]
    public void GoalPrompts_ReferenceSynthesizedGoalBriefCreativityAndAlternatives(string promptRelativePath)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var content = File.ReadAllText(Path.Combine(repositoryRoot, promptRelativePath));

        Assert.Contains("Synthesized Goal Brief (Goal Expansion):", content);
        Assert.Contains("constructive creativity", content);
        Assert.Contains("alternatives", content);
    }

    [Theory]
    [InlineData("prompts/as-is-v1-prompt.md", true)]
    [InlineData("prompts/prompt-goal-type01.md", true)]
    [InlineData("prompts/prompt-goal-type02.md", true)]
    [InlineData("prompts/prompt-goal-expand.md", true)]
    [InlineData("/workspace/repo/prompts/as-is-v1-prompt.md", true)]
    [InlineData("/tmp/test-123/as-is-v1-prompt.md", false)]
    [InlineData("custom/prompt.md", false)]
    public void IsBuiltInPromptPath_DistinguishesBuiltInPromptsFromCustomTemplates(string path, bool expected)
    {
        Assert.Equal(expected, _sut.IsBuiltInPromptPath(path));
    }
}
