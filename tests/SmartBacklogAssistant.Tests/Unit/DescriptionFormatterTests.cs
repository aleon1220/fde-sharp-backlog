using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class DescriptionFormatterTests
{
    [Fact]
    public void Normalize_ConvertsHyphenAndUnicodeBulletsToAsteriskBullets()
    {
        var input = """
            **Objective:** Verify release criteria.
            **Suggested Actions:**
            - Check official launch dates
            • Confirm reviewer sign-off
            * Document next steps
            """;

        var actual = DescriptionFormatter.Normalize(input);

        Assert.Contains("* Check official launch dates", actual);
        Assert.Contains("* Confirm reviewer sign-off", actual);
        Assert.Contains("* Document next steps", actual);
        Assert.DoesNotContain("- Check", actual);
        Assert.DoesNotContain("• Confirm", actual);
    }

    [Fact]
    public void Normalize_EnforcesMax57WordsOnLongProseDescription()
    {
        var words = Enumerable.Range(1, 80).Select(i => $"word{i}");
        var input = string.Join(' ', words);

        var actual = DescriptionFormatter.Normalize(input);

        Assert.True(DescriptionFormatter.CountWords(actual) <= DescriptionFormatter.MaxDescriptionWords);
        Assert.EndsWith("...", actual);
    }

    [Fact]
    public void Normalize_WhenMultiLineDescriptionExceeds57Words_DropsOverflowBulletsAndOrphanHeaders()
    {
        var longObjective = "**Objective:** " + string.Join(' ', Enumerable.Range(1, 49).Select(i => $"term{i}"));
        var input = $"""
            {longObjective}
            **Suggested Actions:**
            - First action item with several extra words
            - Second action item with more words
            """;

        var actual = DescriptionFormatter.Normalize(input);

        Assert.True(DescriptionFormatter.CountWords(actual) <= DescriptionFormatter.MaxDescriptionWords);
        Assert.DoesNotContain("**Suggested Actions:**", actual);
    }

    [Fact]
    public void Normalize_KeepsShortDescriptionUnchangedExceptBulletNormalization()
    {
        var input = "**Objective:** Keep things simple.\n* Verify flow";

        var actual = DescriptionFormatter.Normalize(input);

        Assert.Equal("**Objective:** Keep things simple.\n* Verify flow", actual);
        Assert.True(DescriptionFormatter.CountWords(actual) <= 57);
    }

    [Fact]
    public void FormatWithVerbosity_SupportsMinimalisticBalancedAndDetailedWordBudgets()
    {
        var input = """
            **Objective:** Verify official rules, timelines, and campaign criteria for the 2026 release.
            **Context:** Include regional submission windows and reviewer guidelines for participants across all tiers.
            **Suggested Actions:**
            * Gather official rules, references, and dates from the primary portal
            * Note open questions, edge cases, and cross-team dependencies
            * Summarize findings and next steps for stakeholder sign-off
            * Validate backwards compatibility and operational handoff checks
            """;

        var minimal = DescriptionFormatter.FormatWithVerbosity(input, SmartBacklogAssistant.Domain.OutcomeVerbosity.Minimalistic);
        var balanced = DescriptionFormatter.FormatWithVerbosity(input, SmartBacklogAssistant.Domain.OutcomeVerbosity.Balanced);
        var detailed = DescriptionFormatter.FormatWithVerbosity(input, SmartBacklogAssistant.Domain.OutcomeVerbosity.Detailed);

        Assert.True(DescriptionFormatter.CountWords(minimal) <= DescriptionFormatter.MaxMinimalisticWords);
        Assert.DoesNotContain("**Suggested Actions:**", minimal);
        Assert.True(DescriptionFormatter.CountWords(balanced) <= DescriptionFormatter.MaxDescriptionWords);
        Assert.True(DescriptionFormatter.CountWords(detailed) <= DescriptionFormatter.MaxDetailedWords);
        Assert.True(DescriptionFormatter.CountWords(detailed) >= DescriptionFormatter.CountWords(minimal));
    }
}
