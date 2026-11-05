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
}
