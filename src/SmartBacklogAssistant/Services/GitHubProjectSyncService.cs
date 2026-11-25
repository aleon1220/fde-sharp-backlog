using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Represents the canonical GitHub Project V2 field values inferred for a repository issue or pull request.
/// </summary>
public sealed record GitHubProjectItemClassification(
    string Status,
    string Priority,
    string BacklogType,
    string Area,
    string TargetMilestone,
    string Effort);

/// <summary>
/// Represents a single-select option in the declarative GitHub Project V2 schema (<c>.github/project-v2-schema.json</c>).
/// </summary>
public sealed record GitHubProjectFieldOptionSchema(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("description")] string Description);

/// <summary>
/// Represents a single-select field definition in the declarative GitHub Project V2 schema.
/// </summary>
public sealed record GitHubProjectFieldSchema(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dataType")] string DataType,
    [property: JsonPropertyName("defaultOption")] string DefaultOption,
    [property: JsonPropertyName("options")] IReadOnlyList<GitHubProjectFieldOptionSchema> Options);

/// <summary>
/// Represents a saved view definition in the declarative GitHub Project V2 schema.
/// </summary>
public sealed record GitHubProjectViewSchema(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("layout")] string Layout,
    [property: JsonPropertyName("groupBy")] string GroupBy,
    [property: JsonPropertyName("filter")] string Filter,
    [property: JsonPropertyName("visibleFields")] IReadOnlyList<string> VisibleFields,
    [property: JsonPropertyName("description")] string Description);

/// <summary>
/// Root contract for <c>.github/project-v2-schema.json</c> (Issue #50).
/// </summary>
public sealed record GitHubProjectV2SchemaManifest(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("statusField")] GitHubProjectFieldSchema StatusField,
    [property: JsonPropertyName("customFields")] IReadOnlyList<GitHubProjectFieldSchema> CustomFields,
    [property: JsonPropertyName("views")] IReadOnlyList<GitHubProjectViewSchema> Views);

/// <summary>
/// Deterministic classifier and lifecycle state machine for synchronizing GitHub Issues and Pull Requests
/// into the repository's GitHub Project V2 board (Issue #50).
/// </summary>
public sealed partial class GitHubProjectSyncService
{
    public const string StatusTodo = "Todo";
    public const string StatusInProgress = "In Progress";
    public const string StatusInReview = "In Review";
    public const string StatusDone = "Done";

    public const string BacklogTypeComplete = "Type01: Complete";
    public const string BacklogTypeSchedule = "Type02: Schedule";
    public const string BacklogTypeBaseline = "Baseline/Audit";

    public const string AreaCliTui = "CLI/TUI";
    public const string AreaAiProviders = "AI Providers";
    public const string AreaSreDevOps = "SRE/DevOps";
    public const string AreaPromptLibrary = "Prompt Library";
    public const string AreaDocsGitHub = "Docs/GitHub";

    private static readonly JsonSerializerOptions SchemaJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Loads and validates the declarative GitHub Project V2 schema manifest from disk.
    /// </summary>
    public static GitHubProjectV2SchemaManifest LoadSchemaManifest(string schemaFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaFilePath);

        if (!File.Exists(schemaFilePath))
        {
            throw new FileNotFoundException($"GitHub Project V2 schema manifest not found at '{schemaFilePath}'.", schemaFilePath);
        }

        var json = File.ReadAllText(schemaFilePath);
        var manifest = JsonSerializer.Deserialize<GitHubProjectV2SchemaManifest>(json, SchemaJsonOptions);
        if (manifest is null)
        {
            throw new InvalidOperationException($"Failed to deserialize GitHub Project V2 schema manifest at '{schemaFilePath}'.");
        }

        return manifest;
    }

    /// <summary>
    /// Classifies an issue or pull request into the repository's GitHub Project V2 custom field taxonomy.
    /// </summary>
    public static GitHubProjectItemClassification ClassifyItem(
        string? title,
        string? body,
        string? state,
        int? milestoneNumber = null,
        string? milestoneTitle = null,
        IReadOnlyList<string>? labels = null,
        bool isPullRequest = false,
        bool isDraftPullRequest = false,
        bool isMergedPullRequest = false)
    {
        var safeTitle = title ?? string.Empty;
        var safeBody = body ?? string.Empty;
        var safeLabels = labels ?? [];

        var status = ResolveLifecycleStatus(
            state: state,
            isPullRequest: isPullRequest,
            isDraftPullRequest: isDraftPullRequest,
            isMergedPullRequest: isMergedPullRequest);

        var backlogType = InferBacklogType(safeTitle, safeBody, safeLabels);
        var priority = InferPriority(safeTitle, safeBody, safeLabels, backlogType, milestoneNumber);
        var area = InferArea(safeTitle, safeBody, safeLabels);
        var targetMilestone = InferTargetMilestone(milestoneNumber, milestoneTitle, safeTitle, safeBody);
        var effort = InferEffort(safeTitle, safeBody, safeLabels, backlogType, targetMilestone);

        return new GitHubProjectItemClassification(
            Status: status,
            Priority: priority,
            BacklogType: backlogType,
            Area: area,
            TargetMilestone: targetMilestone,
            Effort: effort);
    }

    /// <summary>
    /// Resolves the target Project V2 <c>Status</c> value for an issue or pull request lifecycle transition.
    /// </summary>
    public static string ResolveLifecycleStatus(
        string? state,
        bool isPullRequest = false,
        bool isDraftPullRequest = false,
        bool isMergedPullRequest = false,
        string? eventAction = null)
    {
        var normalizedState = state?.Trim().ToUpperInvariant() ?? "OPEN";
        var normalizedAction = eventAction?.Trim().ToLowerInvariant() ?? string.Empty;

        if (isPullRequest)
        {
            if (isMergedPullRequest || normalizedState == "MERGED")
            {
                return StatusDone;
            }

            if (normalizedState == "CLOSED")
            {
                return StatusDone;
            }

            if (isDraftPullRequest || normalizedAction == "converted_to_draft")
            {
                return StatusInProgress;
            }

            return StatusInReview;
        }

        if (normalizedState == "CLOSED" || normalizedAction == "closed")
        {
            return StatusDone;
        }

        return StatusTodo;
    }

    /// <summary>
    /// Resolves the target Project V2 <c>Status</c> for an issue linked to a pull request via <c>Closes #N</c>.
    /// </summary>
    public static string ResolveLinkedIssueStatusForPullRequest(
        string? prState,
        bool isDraftPullRequest,
        bool isMergedPullRequest,
        string? eventAction = null)
    {
        var normalizedState = prState?.Trim().ToUpperInvariant() ?? "OPEN";
        var normalizedAction = eventAction?.Trim().ToLowerInvariant() ?? string.Empty;

        if (isMergedPullRequest || normalizedState == "MERGED")
        {
            return StatusDone;
        }

        if (normalizedState == "CLOSED" && !isMergedPullRequest)
        {
            return StatusTodo;
        }

        if (isDraftPullRequest || normalizedAction == "converted_to_draft")
        {
            return StatusInProgress;
        }

        return StatusInReview;
    }

    /// <summary>
    /// Extracts referenced issue numbers from a pull request title and body (e.g., <c>Closes #50</c>, <c>Fixes #57</c>, or title <c>(#50)</c>).
    /// </summary>
    public static IReadOnlyList<int> ExtractLinkedIssueNumbers(string? prTitle, string? prBody)
    {
        var numbers = new SortedSet<int>();

        if (!string.IsNullOrWhiteSpace(prBody))
        {
            foreach (Match match in ClosingKeywordIssueRegex().Matches(prBody))
            {
                if (int.TryParse(match.Groups[1].Value, out var num) && num > 0)
                {
                    numbers.Add(num);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(prTitle))
        {
            foreach (Match match in ClosingKeywordIssueRegex().Matches(prTitle))
            {
                if (int.TryParse(match.Groups[1].Value, out var num) && num > 0)
                {
                    numbers.Add(num);
                }
            }

            foreach (Match match in TitleIssueReferenceRegex().Matches(prTitle))
            {
                if (int.TryParse(match.Groups[1].Value, out var num) && num > 0)
                {
                    numbers.Add(num);
                }
            }
        }

        return [.. numbers];
    }

    private static string InferBacklogType(string title, string body, IReadOnlyList<string> labels)
    {
        if (labels.Any(static l => l.Contains("baseline", StringComparison.OrdinalIgnoreCase)) ||
            title.Contains("[Baseline", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("Baseline/Audit", StringComparison.OrdinalIgnoreCase))
        {
            return BacklogTypeBaseline;
        }

        if (labels.Any(static l => l.Contains("type02", StringComparison.OrdinalIgnoreCase)) ||
            title.Contains("(Type02)", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("(Type02)", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("Jira - schedule", StringComparison.OrdinalIgnoreCase))
        {
            return BacklogTypeSchedule;
        }

        if (labels.Any(static l => l.Contains("type01", StringComparison.OrdinalIgnoreCase)) ||
            title.Contains("(Type01)", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("(Type01)", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("Jira - complete", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("## Acceptance Criteria", StringComparison.OrdinalIgnoreCase))
        {
            return BacklogTypeComplete;
        }

        return BacklogTypeSchedule;
    }

    private static string InferPriority(
        string title,
        string body,
        IReadOnlyList<string> labels,
        string backlogType,
        int? milestoneNumber)
    {
        foreach (var label in labels)
        {
            var match = PriorityTokenRegex().Match(label);
            if (match.Success)
            {
                return match.Groups[1].Value.ToUpperInvariant();
            }
        }

        var bodyMatch = ExplicitPriorityFieldRegex().Match(body);
        if (bodyMatch.Success)
        {
            return bodyMatch.Groups[1].Value.ToUpperInvariant();
        }

        var titleMatch = PriorityTokenRegex().Match(title);
        if (titleMatch.Success)
        {
            return titleMatch.Groups[1].Value.ToUpperInvariant();
        }

        if (backlogType == BacklogTypeBaseline)
        {
            return "P2";
        }

        if (title.Contains("UAT", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Security", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Mask", StringComparison.OrdinalIgnoreCase))
        {
            return "P0";
        }

        return milestoneNumber switch
        {
            1 => "P1",
            2 => "P1",
            3 => "P2",
            _ => "P2"
        };
    }

    private static string InferArea(string title, string body, IReadOnlyList<string> labels)
    {
        foreach (var label in labels)
        {
            if (label.Contains("area:cli", StringComparison.OrdinalIgnoreCase))
            {
                return AreaCliTui;
            }

            if (label.Contains("area:ai", StringComparison.OrdinalIgnoreCase))
            {
                return AreaAiProviders;
            }

            if (label.Contains("area:sre", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("area:devops", StringComparison.OrdinalIgnoreCase))
            {
                return AreaSreDevOps;
            }

            if (label.Contains("area:prompt", StringComparison.OrdinalIgnoreCase))
            {
                return AreaPromptLibrary;
            }

            if (label.Contains("area:docs", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("area:github", StringComparison.OrdinalIgnoreCase))
            {
                return AreaDocsGitHub;
            }
        }

        var combined = $"{title}\n{body}";

        if (title.Contains("[GitHub Project", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Issue Templates", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("README", StringComparison.OrdinalIgnoreCase))
        {
            return AreaDocsGitHub;
        }

        if (title.Contains("fde-AI-prompts", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Prompt Scanner", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Prompt Generator", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Prompt Template", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Goal Expansion", StringComparison.OrdinalIgnoreCase))
        {
            return AreaPromptLibrary;
        }

        if (title.Contains("NuGet", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Binary", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Logging", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Podman", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Container", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Kubernetes", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Dependabot", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Branch Protection", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("CI/CD", StringComparison.OrdinalIgnoreCase))
        {
            return AreaSreDevOps;
        }

        if (title.Contains("Gemini", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Copilot", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Provider", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("BYOK", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Grounding", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("MCP", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Evaluator", StringComparison.OrdinalIgnoreCase))
        {
            return AreaAiProviders;
        }

        if (combined.Contains("CLI", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("TUI", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("REPL", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Wizard", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Clipboard", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("OSC 52", StringComparison.OrdinalIgnoreCase))
        {
            return AreaCliTui;
        }

        return AreaDocsGitHub;
    }

    private static string InferTargetMilestone(
        int? milestoneNumber,
        string? milestoneTitle,
        string title,
        string body)
    {
        if (milestoneNumber == 1 ||
            (milestoneTitle?.Contains("release-fde-version", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return "M1";
        }

        if (milestoneNumber == 2 ||
            (milestoneTitle?.Contains("v1.1.0", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return "M2";
        }

        if (milestoneNumber == 3 ||
            (milestoneTitle?.Contains("v2.0.0", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return "M3";
        }

        if (title.Contains("v2.0", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("Milestone #3", StringComparison.OrdinalIgnoreCase))
        {
            return "M3";
        }

        if (body.Contains("Milestone #1", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("release-fde-version", StringComparison.OrdinalIgnoreCase))
        {
            return "M1";
        }

        return "M2";
    }

    private static string InferEffort(
        string title,
        string body,
        IReadOnlyList<string> labels,
        string backlogType,
        string targetMilestone)
    {
        foreach (var label in labels)
        {
            if (label.Equals("effort:XS", StringComparison.OrdinalIgnoreCase)) return "XS";
            if (label.Equals("effort:S", StringComparison.OrdinalIgnoreCase)) return "S";
            if (label.Equals("effort:M", StringComparison.OrdinalIgnoreCase)) return "M";
            if (label.Equals("effort:L", StringComparison.OrdinalIgnoreCase)) return "L";
        }

        if (backlogType == BacklogTypeBaseline)
        {
            return "XS";
        }

        if (targetMilestone == "M3" ||
            title.Contains("Epic", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("v2.0", StringComparison.OrdinalIgnoreCase))
        {
            return "L";
        }

        if (backlogType == BacklogTypeComplete || body.Length > 1400)
        {
            return "M";
        }

        return "S";
    }

    [GeneratedRegex(@"(?i)\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+#(\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ClosingKeywordIssueRegex();

    [GeneratedRegex(@"\(#(\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex TitleIssueReferenceRegex();

    [GeneratedRegex(@"(?i)(?:Priority\s*\**:\**\s*|\[|\b)(P[0-3])\b", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitPriorityFieldRegex();

    [GeneratedRegex(@"(?i)\b(P[0-3])\b", RegexOptions.CultureInvariant)]
    private static partial Regex PriorityTokenRegex();
}
