using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class MarkdownOutcomeRendererTests
{
    [Fact]
    public void WriteOutcomeTable_RendersSingleTwoColumnTableFromMarkdownWithAlignedBorders()
    {
        var markdown = """
            # [Jira - schedule (Type02)] Check Geocaching 2026 hiding movie theme

            ## Description
            **Objective:** Verify official rules and dates.
            **Suggested Actions:**
            * Gather official rules and dates
            * Check open questions

            ## Title & Description Alternatives
            1. **[Action & Delivery] Verify & Document Theme** — Audit official rules and dates.
            2. **[User Value / Story] Align Theme Criteria** — Frame user success criteria.
            """;

        using var writer = new StringWriter();
        var color = new ConsoleColorWriter(writer, enableColor: false);

        MarkdownOutcomeRenderer.WriteOutcomeTable(
            color,
            [markdown],
            supplementalUserStory: "As a stakeholder, I want theme rules verified so that plans align.");

        var rendered = writer.ToString().Replace("\r\n", "\n");
        var tableLines = rendered
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith('┌') || l.StartsWith('├') || l.StartsWith('│') || l.StartsWith('└'))
            .ToArray();

        Assert.True(tableLines.Length >= 8);
        Assert.StartsWith("┌", tableLines[0]);
        Assert.EndsWith("┐", tableLines[0]);
        Assert.StartsWith("└", tableLines[^1]);
        Assert.EndsWith("┘", tableLines[^1]);

        var width = tableLines[0].Length;
        Assert.All(tableLines, line => Assert.Equal(width, line.Length));
        Assert.Contains("Stage / Type", rendered);
        Assert.Contains("schedule (Type02)", rendered);
        Assert.Contains("User Story", rendered);
        Assert.Contains("Description", rendered);
        Assert.Contains("* Gather official rules and dates", rendered);
        Assert.Contains("Alternatives", rendered);
    }

    [Fact]
    public void RenderRawTableTextAndRenderMarkdownText_PreserveTableNewlinesBulletsAndMarkdown()
    {
        var markdown = """
            # [Jira - schedule (Type02)] Plan Canary Rollout

            ## Description
            **Objective:** Roll out service safely.
            **Suggested Actions:**
            * Enable canary routing
            * Monitor latency metrics
            """;

        var rawText = MarkdownOutcomeRenderer.RenderRawTableText([markdown]);
        var mdText = MarkdownOutcomeRenderer.RenderMarkdownText([markdown]);

        Assert.Contains("┌", rawText);
        Assert.Contains("\n", rawText);
        Assert.Contains("* Enable canary routing", rawText);
        Assert.Contains("* Monitor latency metrics", rawText);
        Assert.DoesNotContain("\u001b[", rawText, StringComparison.Ordinal);

        Assert.StartsWith("# [Jira - schedule (Type02)] Plan Canary Rollout", mdText);
        Assert.DoesNotContain("┌", mdText);
    }

    [Fact]
    public void BuildTableRows_WithCombinedType01AndType02_DeduplicatesAlternativesInOneTable()
    {
        var type01 = """
            # [Jira - complete (Type01)] Enable SSO Login

            ## User Story
            As an admin, I want SSO so that users sign in securely.

            ## Acceptance Criteria (Given / When / Then)
            ### Scenario 1
            - **Given** IdP is configured
            - **When** User logs in
            - **Then** Session opens

            ## Title & Description Alternatives
            1. **[Action & Delivery] Enable SSO** — Ship OIDC integration.
            """;

        var type02 = """
            # [Jira - schedule (Type02)] Enable SSO Login

            ## Description
            **Objective:** Deliver OIDC SSO integration.
            * Configure IdP metadata

            ## Title & Description Alternatives
            1. **[Action & Delivery] Enable SSO** — Ship OIDC integration.
            """;

        var rows = MarkdownOutcomeRenderer.BuildTableRows([type01, type02]);

        Assert.Single(rows, r => r.Section == "Alternatives");
        Assert.Contains(rows, r => r.Section == "Type01");
        Assert.Contains(rows, r => r.Section == "Type02");
        Assert.Contains(rows, r => r.Section == "Acceptance");
        Assert.Contains(rows, r => r.Section == "Description");
    }
}
