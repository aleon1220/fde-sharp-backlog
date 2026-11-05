using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;
using SmartBacklogAssistant.Workflow;

namespace SmartBacklogAssistant.Tests.Integration;

public sealed class AsIsV1WorkflowTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputFilePath;
    private readonly string _promptFilePath;
    private readonly string _outputDir;

    public AsIsV1WorkflowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sba-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _inputFilePath = Path.Combine(_tempDir, "raw-notes.txt");
        _promptFilePath = Path.Combine(_tempDir, "as-is-v1-prompt.md");
        _outputDir = Path.Combine(_tempDir, "output");

        File.WriteAllText(_inputFilePath, """
            Discovery Note - Webhook Retry Policy
            Context: Outbound webhooks fail permanently on transient 503 errors.
            Requirement: As an integration developer, I want exponential backoff retries so that transient network glitches do not drop events.
            Precondition: A webhook endpoint returns HTTP 503.
            Action: The dispatcher handles the failed delivery attempt.
            Expected Outcome: The event is retried up to 5 times with exponential backoff.
            """);

        File.WriteAllText(_promptFilePath, """
            # As-Is v1 Prompt
            Analyze the following raw notes:
            {{RAW_DATA}}
            """);
    }

    [Fact]
    public void Execute_InteractiveDefaultFlow_PromptsUserAndGeneratesType01Artifacts()
    {
        // User presses Enter for default AI provider (ManualPrompt) and Enter for default Type01 (Given/When/Then)
        using var input = new StringReader("\n\n");
        using var output = new StringWriter();

        var workflow = CreateWorkflow();
        var options = new WorkflowOptions(
            InputFilePath: _inputFilePath,
            PromptFilePath: _promptFilePath,
            OutputDirectory: _outputDir,
            NonInteractive: false);

        var ticket = workflow.Execute(options, input, output);

        Assert.Equal(BacklogType.Type01, ticket.BacklogType);
        Assert.Equal("complete", ticket.JiraStage);
        Assert.Equal("Webhook Retry Policy", ticket.Title);
        Assert.Single(ticket.AcceptanceCriteria);

        var consoleOutput = output.ToString();
        Assert.Contains("Pre-Designed Prompt (Loaded from Markdown)", consoleOutput);
        Assert.Contains("run it in your desired AI model", consoleOutput);

        Assert.True(File.Exists(Path.Combine(_outputDir, "jira-backlog-item.md")));
        Assert.True(File.Exists(Path.Combine(_outputDir, "jira-backlog-item.json")));
    }

    [Fact]
    public void Execute_WithMockedClaudeProviderAndType02_GeneratesScheduleTicket()
    {
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();

        var workflow = CreateWorkflow();
        var options = new WorkflowOptions(
            InputFilePath: _inputFilePath,
            PromptFilePath: _promptFilePath,
            ProviderInput: "claude",
            ApiKey: "sk-ant-test9999",
            BacklogTypeInput: "Type02",
            OutputDirectory: _outputDir,
            NonInteractive: true);

        var ticket = workflow.Execute(options, input, output);

        Assert.Equal(BacklogType.Type02, ticket.BacklogType);
        Assert.Equal("schedule", ticket.JiraStage);
        Assert.NotNull(ticket.Description);
        Assert.Empty(ticket.AcceptanceCriteria);

        var consoleOutput = output.ToString();
        Assert.Contains("Anthropic Claude", consoleOutput);
        Assert.Contains("****9999", consoleOutput);
    }

    private static AsIsV1Workflow CreateWorkflow() =>
        new(
            new RawDataCollector(),
            new PromptTemplateService(),
            new AiModelOrchestrator(),
            new JiraBacklogFormatter());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
