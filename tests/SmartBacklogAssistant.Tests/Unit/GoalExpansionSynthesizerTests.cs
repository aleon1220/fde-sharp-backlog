using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class GoalExpansionSynthesizerTests
{
    [Fact]
    public void ExpandOffline_WithVerifyGoal_SynthesizesInvestUserStoryStructuredDescriptionAndThreeAlternatives()
    {
        var expansion = GoalExpansionSynthesizer.ExpandOffline("check Geocaching 2026 hiding movie theme");

        Assert.Equal("Check Geocaching 2026 hiding movie theme", expansion.EnhancedTitle);
        Assert.StartsWith("As a ", expansion.UserStory);
        Assert.Contains("so that ", expansion.UserStory);
        Assert.Contains("**Objective:**", expansion.DiscoverySummary);
        Assert.Contains("**User Story Framing:**", expansion.DiscoverySummary);
        Assert.Contains("**Verification / Action Checklist:**", expansion.DiscoverySummary);
        Assert.DoesNotContain("Not specified in discovery notes", expansion.DiscoverySummary);

        Assert.Equal(3, expansion.KeyChecklist.Count);
        Assert.Equal(3, expansion.Alternatives.Count);
        Assert.Equal("Action & Delivery", expansion.Alternatives[0].Angle);
        Assert.Equal("User Value / Story", expansion.Alternatives[1].Angle);
        Assert.Equal("Verification & Scope", expansion.Alternatives[2].Angle);
        Assert.Contains("Verify & Document", expansion.Alternatives[0].Title);
    }

    [Fact]
    public void ExpandOffline_WithMigrateGoal_ProducesMigrationOrientedAlternatives()
    {
        var expansion = GoalExpansionSynthesizer.ExpandOffline("Migrate Telemetry to OpenTelemetry");

        Assert.Equal("Migrate Telemetry to OpenTelemetry", expansion.EnhancedTitle);
        Assert.Contains("platform engineer", expansion.UserStory);
        Assert.Contains("Migrate & Validate", expansion.Alternatives[0].Title);
        Assert.Contains("Transition", expansion.Alternatives[1].Title);
        Assert.Contains("Cutover, Rollback", expansion.Alternatives[2].Title);
    }

    [Fact]
    public void IsBareGoalWithoutDiscoveryNotes_ExcludesAttachedFileContextAndEmptyGoals()
    {
        Assert.True(GoalExpansionSynthesizer.IsBareGoalWithoutDiscoveryNotes("Check 2026 theme", "Goal: Check 2026 theme"));
        Assert.False(GoalExpansionSynthesizer.IsBareGoalWithoutDiscoveryNotes(
            "Optimize Checkout",
            "Goal: Optimize Checkout\n\n--- Context (@discovery.txt) ---\nExisting flow takes 4 clicks."));
        Assert.False(GoalExpansionSynthesizer.IsBareGoalWithoutDiscoveryNotes(null, "Discovery Note - Test"));
    }

    [Fact]
    public void MergeExpansionIntoRawData_AppendsSynthesizedBriefAndOptionalGwt()
    {
        var expansion = GoalExpansionSynthesizer.ExpandOffline("check Geocaching 2026 hiding movie theme");

        var withGwt = GoalExpansionSynthesizer.MergeExpansionIntoRawData("Goal: check Geocaching 2026 hiding movie theme", expansion, includeSyntheticGwt: true);
        var withoutGwt = GoalExpansionSynthesizer.MergeExpansionIntoRawData("Goal: check Geocaching 2026 hiding movie theme", expansion, includeSyntheticGwt: false);

        Assert.Contains("Synthesized Goal Brief (Goal Expansion):", withGwt);
        Assert.Contains("Enhanced Title: Check Geocaching 2026 hiding movie theme", withGwt);
        Assert.Contains("Expanded Description:", withGwt);
        Assert.Contains("Precondition:", withGwt);
        Assert.DoesNotContain("Precondition:", withoutGwt);
    }
}
