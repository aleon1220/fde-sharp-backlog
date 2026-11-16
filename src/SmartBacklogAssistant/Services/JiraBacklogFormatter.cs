using System.Text;
using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Step 3 & 4 (Review -> decide{backlog type?} -> Jira):
/// Formats the reviewed outcome into a Jira backlog item:
/// - Type01 -> complete (use Given / When / Then)
/// - Type02 -> schedule (use Title + Description)
/// </summary>
public sealed class JiraBacklogFormatter
{
    public BacklogType ResolveBacklogType(string? userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return BacklogType.Type01;
        }

        return userInput.Trim().ToLowerInvariant() switch
        {
            "2" or "02" or "type02" or "type 02" or "type2" or "type 2" or "schedule" => BacklogType.Type02,
            _ => BacklogType.Type01
        };
    }

    public JiraBacklogTicket Format(
        ProcessedOutcome outcome,
        BacklogType backlogType,
        OutcomeVerbosity verbosity = OutcomeVerbosity.Balanced)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return backlogType switch
        {
            BacklogType.Type01 => FormatType01Complete(outcome, verbosity),
            BacklogType.Type02 => FormatType02Schedule(outcome, verbosity),
            _ => throw new ArgumentOutOfRangeException(nameof(backlogType), backlogType, "Unsupported backlog type.")
        };
    }

    private static JiraBacklogTicket FormatType01Complete(ProcessedOutcome outcome, OutcomeVerbosity verbosity)
    {
        var normalizedDescription = ResolveType01Description(outcome, verbosity);
        var sb = new StringBuilder();
        sb.AppendLine($"# [Jira - complete (Type01)] {outcome.Title}");
        sb.AppendLine();
        sb.AppendLine("## User Story");
        sb.AppendLine(outcome.UserStory);

        if (!string.IsNullOrWhiteSpace(normalizedDescription))
        {
            sb.AppendLine();
            sb.AppendLine("## Description");
            sb.AppendLine(normalizedDescription);
        }

        sb.AppendLine();
        sb.AppendLine("## Acceptance Criteria (Given / When / Then)");

        for (var i = 0; i < outcome.Scenarios.Count; i++)
        {
            var scenario = outcome.Scenarios[i];
            sb.AppendLine($"### Scenario {i + 1}");
            sb.AppendLine($"- **Given** {scenario.Given}");
            sb.AppendLine($"- **When** {scenario.When}");
            sb.AppendLine($"- **Then** {scenario.Then}");
        }

        AppendAlternativesSection(sb, outcome.Alternatives);

        return new JiraBacklogTicket(
            BacklogType: BacklogType.Type01,
            JiraStage: "complete",
            Title: outcome.Title,
            Description: normalizedDescription,
            UserStory: outcome.UserStory,
            AcceptanceCriteria: outcome.Scenarios,
            FormattedMarkdown: sb.ToString().TrimEnd().Replace("\r\n", "\n"),
            Alternatives: outcome.Alternatives);
    }

    private static string? ResolveType01Description(ProcessedOutcome outcome, OutcomeVerbosity verbosity)
    {
        if (string.IsNullOrWhiteSpace(outcome.Description))
        {
            return null;
        }

        var trimmed = outcome.Description.Trim();
        if (trimmed.Equals("Not specified in discovery notes.", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals(outcome.UserStory?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = DescriptionFormatter.FormatWithVerbosity(trimmed, verbosity);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static JiraBacklogTicket FormatType02Schedule(ProcessedOutcome outcome, OutcomeVerbosity verbosity)
    {
        var description = DescriptionFormatter.FormatWithVerbosity(outcome.Description, verbosity);
        var sb = new StringBuilder();
        sb.AppendLine($"# [Jira - schedule (Type02)] {outcome.Title}");
        sb.AppendLine();
        sb.AppendLine("## Description");
        sb.AppendLine(description);

        AppendAlternativesSection(sb, outcome.Alternatives);

        return new JiraBacklogTicket(
            BacklogType: BacklogType.Type02,
            JiraStage: "schedule",
            Title: outcome.Title,
            Description: description,
            UserStory: null,
            AcceptanceCriteria: [],
            FormattedMarkdown: sb.ToString().TrimEnd().Replace("\r\n", "\n"),
            Alternatives: outcome.Alternatives);
    }

    private static void AppendAlternativesSection(StringBuilder sb, IReadOnlyList<DraftAlternative>? alternatives)
    {
        if (alternatives is not { Count: > 0 })
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("## Title & Description Alternatives");
        for (var i = 0; i < alternatives.Count; i++)
        {
            var alt = alternatives[i];
            sb.AppendLine($"{i + 1}. **[{alt.Angle}] {alt.Title}** — {alt.Description}");
        }
    }
}
