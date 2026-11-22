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

        Assert.Equal(expectedPath, resolvedPath);
        Assert.Contains(expectedTitle, ReadRepoPrompt(resolvedPath));
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
    public void GoalPrompts_EnforceActionableModeGoalBriefCreativityAndAlternatives(string promptRelativePath)
    {
        var content = ReadRepoPrompt(promptRelativePath);

        Assert.Contains("Goal / Brief Topic Mode", content);
        Assert.Contains("Discovery Notes Mode", content);
        Assert.Contains("Refinement Rule", content);
        Assert.Contains("Objective", content);
        Assert.Contains("Web Research Findings:", content);
        Assert.Contains("Synthesized Goal Brief (Goal Expansion):", content);
        Assert.Contains("constructive creativity", content);
        Assert.Contains("alternatives", content);
    }

    [Fact]
    public void Type02GoalPrompt_ProhibitsAddingUserStoryAndBoilerplateDismissals()
    {
        var content = ReadRepoPrompt(PromptTemplateService.Type02PromptFilePath);

        Assert.Contains("Do **NOT** dismiss the request with `Not specified in discovery notes` boilerplate", content);
        Assert.Contains("Do not add a user story", content);
    }

    [Fact]
    public void GoalExpandPrompt_ContainsInvestPrinciplesTitleEnhancementAndAlternatives()
    {
        var content = ReadRepoPrompt(PromptTemplateService.GoalExpandPromptFilePath);

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

    [Theory]
    [InlineData(PromptTemplateService.DefaultPromptFilePath, "As-Is v1 Prompt")]
    [InlineData(PromptTemplateService.Type01PromptFilePath, "Complete User Story")]
    [InlineData(PromptTemplateService.Type02PromptFilePath, "Schedule Item")]
    [InlineData(PromptTemplateService.GoalExpandPromptFilePath, "Goal-to-Notes Expansion")]
    public void TryReadEmbeddedPromptTemplate_LoadsBundledMarkdownFromAssembly(string promptPath, string expectedText)
    {
        var embedded = _sut.TryReadEmbeddedPromptTemplate(promptPath);

        Assert.NotNull(embedded);
        Assert.Contains(expectedText, embedded);
    }

    [Fact]
    public void LoadAndRenderPrompt_WhenNoPromptsExistOnDisk_FallsBackToEmbeddedForBuiltInAndThrowsForMissingCustom()
    {
        var isolatedService = new PromptTemplateService(
            fileExists: _ => false,
            fileReader: _ => throw new InvalidOperationException("Disk reader should not be called when files do not exist."),
            currentDirectoryProvider: () => Path.GetTempPath(),
            baseDirectoryProvider: () => Path.GetTempPath());

        var rendered = isolatedService.LoadAndRenderPromptForType(
            PromptTemplateService.DefaultPromptFilePath,
            "Goal: Ship standalone release binaries",
            BacklogType.Type01);

        Assert.Contains("Complete User Story", rendered);
        Assert.Contains("Goal: Ship standalone release binaries", rendered);

        Assert.Throws<FileNotFoundException>(() =>
            isolatedService.LoadAndRenderPrompt("custom/missing-prompt.md", "Goal: Custom override"));
    }

    private static string ReadRepoPrompt(string relativePath)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        return File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
    }
}
