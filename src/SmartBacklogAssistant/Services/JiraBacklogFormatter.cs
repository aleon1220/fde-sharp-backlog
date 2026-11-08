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
            "2" or "type02" or "type2" or "schedule" => BacklogType.Type02,
            _ => BacklogType.Type01
        };
    }

    public JiraBacklogTicket Format(ProcessedOutcome outcome, BacklogType backlogType)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return backlogType switch
        {
            BacklogType.Type01 => FormatType01Complete(outcome),
            BacklogType.Type02 => FormatType02Schedule(outcome),
            _ => throw new ArgumentOutOfRangeException(nameof(backlogType), backlogType, "Unsupported backlog type.")
        };
    }

    private static JiraBacklogTicket FormatType01Complete(ProcessedOutcome outcome)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# [Jira - complete (Type01)] {outcome.Title}");
        sb.AppendLine();
        sb.AppendLine("## User Story");
        sb.AppendLine(outcome.UserStory);
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

        return new JiraBacklogTicket(
            BacklogType: BacklogType.Type01,
            JiraStage: "complete",
            Title: outcome.Title,
            Description: null,
            UserStory: outcome.UserStory,
            AcceptanceCriteria: outcome.Scenarios,
            FormattedMarkdown: sb.ToString().TrimEnd().Replace("\r\n", "\n"));
    }

    private static JiraBacklogTicket FormatType02Schedule(ProcessedOutcome outcome)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# [Jira - schedule (Type02)] {outcome.Title}");
        sb.AppendLine();
        sb.AppendLine("## Description");
        sb.AppendLine(outcome.Description);

        return new JiraBacklogTicket(
            BacklogType: BacklogType.Type02,
            JiraStage: "schedule",
            Title: outcome.Title,
            Description: outcome.Description,
            UserStory: null,
            AcceptanceCriteria: [],
            FormattedMarkdown: sb.ToString().TrimEnd().Replace("\r\n", "\n"));
    }
}
