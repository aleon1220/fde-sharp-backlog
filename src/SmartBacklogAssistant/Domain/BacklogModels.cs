using System.ComponentModel;

namespace SmartBacklogAssistant.Domain;

/// <summary>
/// Supported AI model providers for the Use Case 1 orchestrator.
/// </summary>
public enum AiProviderType
{
    [Description("Default: Load prompt from Markdown file and suggest running it in your desired AI model (e.g. Copilot)")]
    ManualPrompt = 0,

    [Description("Mocked Azure OpenAI Service")]
    AzureOpenAi = 1,

    [Description("Mocked Google Gemini API")]
    GoogleGemini = 2,

    [Description("Mocked Anthropic Claude API")]
    AnthropicClaude = 3
}

/// <summary>
/// Backlog routing decision in practical process as-is v1.
/// </summary>
public enum BacklogType
{
    /// <summary>
    /// Routes to 'complete': formats backlog item using Given / When / Then acceptance criteria.
    /// </summary>
    Type01 = 1,

    /// <summary>
    /// Routes to 'schedule': formats backlog item using Title + Description.
    /// </summary>
    Type02 = 2
}

/// <summary>
/// Represents a single Given / When / Then acceptance criterion.
/// </summary>
public sealed record GherkinScenario(
    string Given,
    string When,
    string Then);

/// <summary>
/// Outcome produced after collecting raw data, applying the prompt, and validating the result.
/// </summary>
public sealed record ProcessedOutcome(
    AiProviderType ProviderUsed,
    string RenderedPrompt,
    string GuidanceMessage,
    string Title,
    string Description,
    string UserStory,
    IReadOnlyList<GherkinScenario> Scenarios);

/// <summary>
/// Final Jira-ready artifact after human review and backlog type decision.
/// </summary>
public sealed record JiraBacklogTicket(
    BacklogType BacklogType,
    string JiraStage,
    string Title,
    string? Description,
    string? UserStory,
    IReadOnlyList<GherkinScenario> AcceptanceCriteria,
    string FormattedMarkdown);
