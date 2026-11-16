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
        Assert.DoesNotContain("## Title & Description Alternatives", ticket.FormattedMarkdown);
    }

    [Fact]
    public void Format_WhenOutcomeHasAlternatives_RendersAlternativesSectionInBothTypes()
    {
        var outcome = CreateSampleOutcome() with
        {
            Alternatives =
            [
                new DraftAlternative("Action & Delivery", "Deliver Async Audit Export", "Implement queued CSV export."),
                new DraftAlternative("User Value / Story", "Enable Self-Service Audit Downloads", "Allow admins to download large logs cleanly."),
                new DraftAlternative("Verification & Scope", "Validate Export SLAs & Access Rules", "Verify 30s SLA and RBAC enforcement.")
            ]
        };

        var type01 = _sut.Format(outcome, BacklogType.Type01);
        var type02 = _sut.Format(outcome, BacklogType.Type02);

        Assert.Equal(3, type01.Alternatives?.Count);
        Assert.Equal(3, type02.Alternatives?.Count);
        Assert.Contains("## Title & Description Alternatives", type01.FormattedMarkdown);
        Assert.Contains("**[Action & Delivery] Deliver Async Audit Export** — Implement queued CSV export.", type01.FormattedMarkdown);
        Assert.Contains("## Title & Description Alternatives", type02.FormattedMarkdown);
        Assert.Contains("**[Verification & Scope] Validate Export SLAs & Access Rules** — Verify 30s SLA and RBAC enforcement.", type02.FormattedMarkdown);
    }

    [Fact]
    public void Format_WithType02VerboseDescription_LimitsTo57WordsAndUsesAsteriskBullets()
    {
        var verboseOutcome = CreateSampleOutcome() with
        {
            Description = "**Objective:** " + string.Join(' ', Enumerable.Range(1, 35).Select(i => $"word{i}")) +
                          "\n**Suggested Actions:**\n- Check item one\n- Check item two\n- Check item three"
        };

        var ticket = _sut.Format(verboseOutcome, BacklogType.Type02);

        Assert.NotNull(ticket.Description);
        Assert.True(DescriptionFormatter.CountWords(ticket.Description) <= DescriptionFormatter.MaxDescriptionWords);
        Assert.Contains("* Check item one", ticket.Description);
        Assert.DoesNotContain("- Check item", ticket.Description);
    }
}
