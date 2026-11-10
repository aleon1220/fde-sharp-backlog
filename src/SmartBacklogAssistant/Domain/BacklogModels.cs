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

    [Description("Google Gemini API (Secret Key Injected at Runtime)")]
    GoogleGemini = 2,

    [Description("Mocked Anthropic Claude API")]
    AnthropicClaude = 3
}

/// <summary>
/// Indicates how the runtime AI secret key was supplied.
/// </summary>
public enum SecretSource
{
    None = 0,
    CliArgument = 1,
    EnvironmentVariable = 2,
    InteractiveCliPrompt = 3
}

/// <summary>
/// Represents the resolved secret API key state at runtime.
/// </summary>
public sealed record GeminiSecretResolution(
    string? ApiKey,
    string MaskedKey,
    SecretSource Source,
    string SourceDetail,
    string KeyPortalUrl)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Captures runtime OS, build metadata, and enabled AI model telemetry.
/// </summary>
public sealed record RuntimeEnvironmentInfo(
    string OsPlatform,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string FrameworkDescription,
    string BuildVersion,
    string BuildConfiguration,
    AiProviderType EnabledProvider,
    string EnabledAiModel,
    string KeyPortalUrl);

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

/// <summary>
/// Persistent CLI configuration settings for Smart Backlog Assistant.
/// </summary>
public sealed record CliSettings(
    bool JsonFileOut = false);
