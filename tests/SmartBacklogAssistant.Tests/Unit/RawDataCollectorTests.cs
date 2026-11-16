using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class RawDataCollectorTests
{
    private readonly RawDataCollector _sut = new();

    [Fact]
    public void CollectFromText_WithValidRawNotes_TrimsAndNormalizesNewlines()
    {
        var input = "  Title: Export CSV\r\nContext: Large dataset  \r\n";

        var result = _sut.CollectFromText(input);

        Assert.Equal("Title: Export CSV\nContext: Large dataset", result);
    }

    [Fact]
    public void CollectFromGoal_ProducesOnlyTheSuppliedGoalWithoutSyntheticRequirements()
    {
        var result = _sut.CollectFromGoal("Implement account recovery");

        Assert.Equal("Goal: Implement account recovery", result);
        Assert.DoesNotContain("Context:", result);
        Assert.DoesNotContain("Requirement:", result);
        Assert.DoesNotContain("Precondition:", result);
        Assert.DoesNotContain("Expected Outcome:", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void CollectFromText_WithEmptyOrWhitespace_ThrowsInvalidOperationException(string invalidInput)
    {
        Assert.Throws<InvalidOperationException>(() => _sut.CollectFromText(invalidInput));
    }

    [Fact]
    public void CollectFromFile_WhenFileDoesNotExist_ThrowsFileNotFoundException()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.txt");

        Assert.Throws<FileNotFoundException>(() => _sut.CollectFromFile(missingPath));
    }

    [Fact]
    public void ParseInlineGoalModifiers_ExtractsPipeDetailsAndSearchFlagOutsideQuotes()
    {
        var parsed = RawDataCollector.ParseInlineGoalModifiers(
            "\"Check | verify\" Geocaching 2026 hiding movie theme | Include official dates and rules --search");

        Assert.Equal("\"Check | verify\" Geocaching 2026 hiding movie theme", parsed.GoalExpression);
        Assert.Equal("Include official dates and rules", parsed.InlineDetails);
        Assert.True(parsed.EnableWebSearch);
    }

    [Fact]
    public void ParseInlineGoalModifiers_WithPlainGoal_ReturnsUnchangedGoalWithoutSearch()
    {
        var parsed = RawDataCollector.ParseInlineGoalModifiers("Implement account recovery");

        Assert.Equal("Implement account recovery", parsed.GoalExpression);
        Assert.Null(parsed.InlineDetails);
        Assert.False(parsed.EnableWebSearch);
    }

    [Fact]
    public void CollectFromGoalWithContext_WithDetailsWebAndRefinement_FormatsAllSections()
    {
        var collected = _sut.CollectFromGoalWithContext(
            goalOrTitle: "Check Geocaching 2026 hiding movie theme",
            details: "Verify souvenir window\nConfirm HQ blog post rules",
            webResearchFindings: "- HQ announced 2026 hiding campaign",
            previousDraftSummary: "Title: Draft 1",
            refinementInstructions: "Add timeline checklist");

        Assert.Contains("Goal: Check Geocaching 2026 hiding movie theme", collected);
        Assert.Contains("Context:\nVerify souvenir window\nConfirm HQ blog post rules", collected);
        Assert.Contains("Web Research Findings:\n- HQ announced 2026 hiding campaign", collected);
        Assert.Contains("Previous Draft:\nTitle: Draft 1", collected);
        Assert.Contains("Refinement Instructions:\nAdd timeline checklist", collected);
    }
}
