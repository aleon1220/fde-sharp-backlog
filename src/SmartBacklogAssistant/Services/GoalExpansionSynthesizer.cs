using System.Text;
using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

public sealed record GoalExpansionResult(
    string EnhancedTitle,
    string UserStory,
    string DiscoverySummary,
    IReadOnlyList<string> KeyChecklist,
    IReadOnlyList<DraftAlternative> Alternatives);

/// <summary>
/// Synthesizes structured User Story framing, enriched Description context, Gherkin checks,
/// and 3 intuitive Title + Description alternatives when only a brief Goal is supplied
/// (both for creative deterministic offline drafting and fallback alternatives).
/// </summary>
public static class GoalExpansionSynthesizer
{
    public static bool IsBareGoalWithoutDiscoveryNotes(string? goalTitle, string rawData) =>
        !string.IsNullOrWhiteSpace(goalTitle) &&
        !rawData.Contains("--- Context (@", StringComparison.OrdinalIgnoreCase);

    public static GoalExpansionResult ExpandOffline(
        string goalTitle,
        string? userContext = null,
        string? webResearchFindings = null)
    {
        var cleanTitle = ExtractCleanHeadline(goalTitle);
        var (verb, topicPhrase) = SplitLeadingVerb(cleanTitle);
        var intentKind = ClassifyVerb(verb);

        var (role, actionClause, benefit) = intentKind switch
        {
            IntentKind.Verify => (
                "product stakeholder or engineer",
                $"verify and document {LowerFirst(topicPhrase)}",
                "official criteria, timelines, and actionable requirements are confirmed before execution"),
            IntentKind.Migrate => (
                "platform engineer",
                $"migrate and validate {LowerFirst(topicPhrase)}",
                "systems transition smoothly without regressions or service disruption"),
            _ => (
                "delivery engineer or user",
                $"{verb.ToLowerInvariant()} {topicPhrase}",
                "the target capability works reliably and delivers measurable stakeholder value")
        };

        var userStory = $"As a {role}, I want to {actionClause}, so that {benefit}.";
        var summaryBuilder = new StringBuilder();

        summaryBuilder.AppendLine(intentKind switch
        {
            IntentKind.Verify => $"**Objective:** Investigate, verify, and document all relevant rules, dates, constraints, and requirements for {topicPhrase}.",
            IntentKind.Migrate => $"**Objective:** Plan, execute, and validate the transition for {topicPhrase} with full compatibility and rollback safety.",
            _ => $"**Objective:** Design, deliver, and validate {topicPhrase} with clear scope, quality gates, and observable outcomes."
        });

        summaryBuilder.AppendLine($"**User Story Framing:** {userStory}");

        if (!string.IsNullOrWhiteSpace(userContext))
        {
            summaryBuilder.AppendLine($"**Context & Key Details:** {userContext.Trim()}");
        }
        else
        {
            summaryBuilder.AppendLine($"**Context & Key Details:** Initiated from brief goal \"{cleanTitle}\". Explore functional scope, edge cases, external/timing dependencies, and acceptance criteria.");
        }

        if (!string.IsNullOrWhiteSpace(webResearchFindings))
        {
            summaryBuilder.AppendLine("**Web Research Insights:**");
            summaryBuilder.AppendLine(webResearchFindings.Trim());
        }

        IReadOnlyList<string> checklist = intentKind switch
        {
            IntentKind.Verify =>
            [
                $"Gather authoritative references, rules, and dates for {topicPhrase}",
                "Identify open questions, edge cases, and stakeholder dependencies",
                "Summarize verified findings and define follow-up delivery items"
            ],
            IntentKind.Migrate =>
            [
                $"Inventory current components and dependencies impacted by {topicPhrase}",
                "Define cutover steps, compatibility checks, and rollback criteria",
                "Validate behavior in staging before production rollout"
            ],
            _ =>
            [
                $"Confirm functional requirements and acceptance scope for {topicPhrase}",
                "Cover error handling, edge cases, and security/reliability boundaries",
                "Verify end-to-end outcome with automated checks or demo"
            ]
        };

        summaryBuilder.AppendLine("**Verification / Action Checklist:**");
        foreach (var item in checklist)
        {
            summaryBuilder.AppendLine($"- {item}");
        }

        var alternatives = BuildAlternatives(cleanTitle, topicPhrase, intentKind, userContext);

        return new GoalExpansionResult(
            EnhancedTitle: cleanTitle,
            UserStory: userStory,
            DiscoverySummary: summaryBuilder.ToString().TrimEnd(),
            KeyChecklist: checklist,
            Alternatives: alternatives);
    }

    public static string MergeExpansionIntoRawData(
        string rawData,
        GoalExpansionResult expansion,
        bool includeSyntheticGwt = true)
    {
        ArgumentNullException.ThrowIfNull(expansion);

        var sb = new StringBuilder();
        sb.AppendLine(rawData.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("Synthesized Goal Brief (Goal Expansion):");
        sb.AppendLine($"Enhanced Title: {expansion.EnhancedTitle}");
        sb.AppendLine($"User Story: {expansion.UserStory}");
        sb.AppendLine();
        sb.AppendLine("Expanded Description:");
        sb.AppendLine(expansion.DiscoverySummary.Trim());

        if (expansion.KeyChecklist.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Expanded Checklist:");
            foreach (var item in expansion.KeyChecklist)
            {
                sb.AppendLine($"- {item}");
            }
        }

        if (includeSyntheticGwt)
        {
            var topic = ExtractTopicPhrase(expansion.EnhancedTitle);
            sb.AppendLine();
            sb.AppendLine($"Precondition: Stakeholder prepares scope and context for {topic}.");
            sb.AppendLine($"Action: The workflow executes or verifies {topic}.");
            sb.AppendLine($"Expected Outcome: Validated outcomes and criteria for {topic} are documented and verified.");
        }

        return sb.ToString().TrimEnd();
    }

    public static IReadOnlyList<DraftAlternative> BuildFallbackAlternatives(
        string title,
        string? description = null,
        string? userStory = null)
    {
        var cleanTitle = ExtractCleanHeadline(title);
        var (verb, topicPhrase) = SplitLeadingVerb(cleanTitle);
        var intentKind = ClassifyVerb(verb);
        return BuildAlternatives(cleanTitle, topicPhrase, intentKind, description ?? userStory);
    }

    private static IReadOnlyList<DraftAlternative> BuildAlternatives(
        string cleanTitle,
        string topicPhrase,
        IntentKind intentKind,
        string? contextHint)
    {
        var conciseHint = string.IsNullOrWhiteSpace(contextHint)
            ? string.Empty
            : $" Incorporating: {TruncateOneLine(contextHint, 70)}";

        var alt1Title = intentKind switch
        {
            IntentKind.Verify => $"Verify & Document {CapitalizeWords(topicPhrase)}",
            IntentKind.Migrate => $"Migrate & Validate {CapitalizeWords(topicPhrase)}",
            _ => $"Deliver {CapitalizeWords(topicPhrase)}"
        };
        var alt1Desc = intentKind switch
        {
            IntentKind.Verify => $"Audit official requirements, rules, and dates for {topicPhrase}, producing a ready-to-act engineering brief.{conciseHint}",
            IntentKind.Migrate => $"Execute phased migration for {topicPhrase} with compatibility checks and cutover runbook.{conciseHint}",
            _ => $"Implement {topicPhrase} end-to-end with concrete validation steps and clear operational handoff.{conciseHint}"
        };

        var alt2Title = intentKind switch
        {
            IntentKind.Verify => $"Align Stakeholder Criteria for {CapitalizeWords(topicPhrase)}",
            IntentKind.Migrate => $"Transition {CapitalizeWords(topicPhrase)} Without Disruption",
            _ => $"Enable {CapitalizeWords(topicPhrase)} for Target Users"
        };
        var alt2Desc = intentKind == IntentKind.Migrate
            ? $"Ensure zero-downtime experience and backwards compatibility while shifting {topicPhrase}."
            : $"Frame {topicPhrase} around actor value, expected outcomes, and user-facing success criteria.";

        var alt3Title = intentKind == IntentKind.Migrate
            ? $"Confirm Cutover, Rollback & Checks for {CapitalizeWords(topicPhrase)}"
            : $"Confirm Scope, Timeline & Checks for {CapitalizeWords(topicPhrase)}";
        var alt3Desc = intentKind == IntentKind.Migrate
            ? $"Define staging verification gates, observability metrics, and rollback triggers for {topicPhrase}."
            : $"Establish concrete verification checklist, open questions, and readiness gates for {topicPhrase}.";

        if (string.Equals(alt1Title, cleanTitle, StringComparison.OrdinalIgnoreCase))
        {
            alt1Title = $"Execute Action Plan: {CapitalizeWords(topicPhrase)}";
        }

        return
        [
            new DraftAlternative("Action & Delivery", alt1Title, alt1Desc),
            new DraftAlternative("User Value / Story", alt2Title, alt2Desc),
            new DraftAlternative("Verification & Scope", alt3Title, alt3Desc)
        ];
    }

    private static string ExtractCleanHeadline(string rawGoal)
    {
        var firstLine = rawGoal
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "Backlog Goal";

        foreach (var prefix in new[] { "Goal:", "Title:", "Subject:" })
        {
            if (firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = firstLine[prefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(stripped))
                {
                    firstLine = stripped;
                }

                break;
            }
        }

        return char.ToUpperInvariant(firstLine[0]) + firstLine[1..];
    }

    private static (string Verb, string Topic) SplitLeadingVerb(string headline)
    {
        var parts = headline.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return ("Deliver", headline);
        }

        var firstWord = parts[0];
        if (ClassifyVerb(firstWord) != IntentKind.GeneralAction || IsCommonActionVerb(firstWord))
        {
            return (firstWord, parts[1]);
        }

        return ("Implement", headline);
    }

    private static string ExtractTopicPhrase(string headline) =>
        SplitLeadingVerb(headline).Topic;

    private static IntentKind ClassifyVerb(string verb) => verb.ToLowerInvariant() switch
    {
        "check" or "verify" or "audit" or "review" or "investigate" or "evaluate" or "assess" or "research" or "confirm" or "validate" => IntentKind.Verify,
        "migrate" or "upgrade" or "transition" or "move" or "port" => IntentKind.Migrate,
        _ => IntentKind.GeneralAction
    };

    private static bool IsCommonActionVerb(string word) => word.ToLowerInvariant() switch
    {
        "implement" or "add" or "build" or "create" or "configure" or "enable" or "support"
            or "fix" or "optimize" or "refactor" or "draft" or "update" or "launch" or "design" => true,
        _ => false
    };

    private static string LowerFirst(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToLowerInvariant(text[0]) + text[1..];

    private static string CapitalizeWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            if (i == 0 || words[i].Length > 3)
            {
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
            }
        }

        return string.Join(' ', words);
    }

    private static string TruncateOneLine(string text, int maxLength)
    {
        var single = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return single.Length <= maxLength ? single : single[..(maxLength - 3)] + "...";
    }

    private enum IntentKind
    {
        GeneralAction,
        Verify,
        Migrate
    }
}
