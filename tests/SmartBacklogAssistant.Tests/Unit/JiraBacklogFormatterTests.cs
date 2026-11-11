using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class JiraBacklogFormatterTests
{
    private readonly JiraBacklogFormatter _sut = new();

    private static ProcessedOutcome CreateSampleOutcome() =>
        new(
            ProviderUsed: AiProviderType.ManualPrompt,
            RenderedPrompt: "Prompt",
            GuidanceMessage: "Guidance",
            Title: "Export Audit Logs",
            Description: "Support async CSV export for over 5,000 rows.",
            UserStory: "As an admin, I want to export audit logs asynchronously, so that browser requests do not time out.",
            Scenarios:
            [
                new GherkinScenario(
                    Given: "An admin selects a monthly date range",
                    When: "The admin clicks Export CSV",
                    Then: "A download link is generated within 30 seconds")
            ]);

    [Theory]
    [InlineData(null, BacklogType.Type01)]
    [InlineData("", BacklogType.Type01)]
    [InlineData("1", BacklogType.Type01)]
    [InlineData("01", BacklogType.Type01)]
    [InlineData("Type01", BacklogType.Type01)]
    [InlineData("Type 01", BacklogType.Type01)]
    [InlineData("complete", BacklogType.Type01)]
    [InlineData("2", BacklogType.Type02)]
    [InlineData("02", BacklogType.Type02)]
    [InlineData("Type02", BacklogType.Type02)]
    [InlineData("Type 02", BacklogType.Type02)]
    [InlineData("schedule", BacklogType.Type02)]
    public void ResolveBacklogType_MapsInputToExpectedBacklogType(string? input, BacklogType expected)
    {
        var actual = _sut.ResolveBacklogType(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Format_WithType01_FormatsAsCompleteWithGivenWhenThen()
    {
        var outcome = CreateSampleOutcome();

        var ticket = _sut.Format(outcome, BacklogType.Type01);

        Assert.Equal(BacklogType.Type01, ticket.BacklogType);
        Assert.Equal("complete", ticket.JiraStage);
        Assert.Equal("Export Audit Logs", ticket.Title);
        Assert.Null(ticket.Description);
        Assert.NotNull(ticket.UserStory);
        Assert.Single(ticket.AcceptanceCriteria);
        Assert.Contains("**Given** An admin selects a monthly date range", ticket.FormattedMarkdown);
        Assert.Contains("**When** The admin clicks Export CSV", ticket.FormattedMarkdown);
        Assert.Contains("**Then** A download link is generated within 30 seconds", ticket.FormattedMarkdown);
    }

    [Fact]
    public void Format_WithType02_FormatsAsScheduleWithTitleAndDescription()
    {
        var outcome = CreateSampleOutcome();

        var ticket = _sut.Format(outcome, BacklogType.Type02);

        Assert.Equal(BacklogType.Type02, ticket.BacklogType);
        Assert.Equal("schedule", ticket.JiraStage);
        Assert.Equal("Export Audit Logs", ticket.Title);
        Assert.Equal("Support async CSV export for over 5,000 rows.", ticket.Description);
        Assert.Null(ticket.UserStory);
        Assert.Empty(ticket.AcceptanceCriteria);
        Assert.Contains("# [Jira - schedule (Type02)] Export Audit Logs", ticket.FormattedMarkdown);
        Assert.Contains("Support async CSV export for over 5,000 rows.", ticket.FormattedMarkdown);
    }
}
