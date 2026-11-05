using System.Text.Json;
using System.Text.Json.Serialization;
using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Workflow;

public sealed record WorkflowOptions(
    string InputFilePath,
    string PromptFilePath,
    string? ProviderInput = null,
    string? ApiKey = null,
    string? BacklogTypeInput = null,
    string? OutputDirectory = null,
    bool NonInteractive = false);

public sealed class AsIsV1Workflow
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly RawDataCollector _collector;
    private readonly PromptTemplateService _promptService;
    private readonly AiModelOrchestrator _orchestrator;
    private readonly JiraBacklogFormatter _formatter;

    public AsIsV1Workflow(
        RawDataCollector collector,
        PromptTemplateService promptService,
        AiModelOrchestrator orchestrator,
        JiraBacklogFormatter formatter)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _promptService = promptService ?? throw new ArgumentNullException(nameof(promptService));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
    }

    public JiraBacklogTicket Execute(WorkflowOptions options, TextReader input, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine("=== Smart Backlog Assistant: Use Case 1 (Practical Process As-Is v1) ===");

        // 1. Local: Collect raw data
        output.WriteLine($"[1/5 Local -> Collect] Reading raw data from '{options.InputFilePath}'...");
        var rawData = _collector.CollectFromFile(options.InputFilePath);

        // 2. Local: Process (Select AI model or default to prompt from markdown file)
        var renderedPrompt = _promptService.LoadAndRenderPrompt(options.PromptFilePath, rawData);
        var providerSelection = options.ProviderInput;

        if (string.IsNullOrWhiteSpace(providerSelection) && !options.NonInteractive)
        {
            output.WriteLine();
            output.WriteLine("[2/5 Local -> Process] Select AI Model Provider:");
            output.WriteLine("  [0 / Enter] Default: Select prompt from Markdown file & run in your desired AI model (Copilot, etc.)");
            output.WriteLine("  [1] Azure OpenAI (Mock Orchestrator)");
            output.WriteLine("  [2] Google Gemini (Mock Orchestrator)");
            output.WriteLine("  [3] Anthropic Claude (Mock Orchestrator)");
            output.Write("Your choice (default 0): ");
            providerSelection = input.ReadLine();
        }

        var provider = _orchestrator.ResolveProvider(providerSelection);
        var apiKey = options.ApiKey;

        if (provider != AiProviderType.ManualPrompt && string.IsNullOrWhiteSpace(apiKey) && !options.NonInteractive)
        {
            output.Write($"Enter API key for {provider} (or press Enter for mock key): ");
            apiKey = input.ReadLine();
        }

        if (provider == AiProviderType.ManualPrompt)
        {
            output.WriteLine();
            output.WriteLine("--- Pre-Designed Prompt (Loaded from Markdown) ---");
            output.WriteLine(renderedPrompt);
            output.WriteLine("--------------------------------------------------");
        }

        // 3. Local: Validate outcome
        var outcome = _orchestrator.Process(rawData, renderedPrompt, provider, apiKey);
        output.WriteLine(outcome.GuidanceMessage);
        output.WriteLine("[3/5 Local -> Validate] Outcome validated successfully.");

        // 4. Review: Review outcome & Decide backlog type
        output.WriteLine();
        output.WriteLine("[4/5 Review -> Review Outcome]");
        output.WriteLine($"  Title      : {outcome.Title}");
        output.WriteLine($"  Description: {outcome.Description}");
        output.WriteLine($"  User Story : {outcome.UserStory}");
        foreach (var scenario in outcome.Scenarios)
        {
            output.WriteLine($"  Scenario   : Given {scenario.Given} | When {scenario.When} | Then {scenario.Then}");
        }

        var backlogTypeSelection = options.BacklogTypeInput;
        if (string.IsNullOrWhiteSpace(backlogTypeSelection) && !options.NonInteractive)
        {
            output.WriteLine();
            output.WriteLine("[4/5 Review -> Decide Backlog Type]");
            output.WriteLine("  [1 / Type01] complete -> Use Given / When / Then (default)");
            output.WriteLine("  [2 / Type02] schedule -> Use Title + Description");
            output.Write("Select backlog type (1 or 2): ");
            backlogTypeSelection = input.ReadLine();
        }

        var backlogType = _formatter.ResolveBacklogType(backlogTypeSelection);

        // 5. Jira: Format output based on Type01 (complete) or Type02 (schedule)
        var ticket = _formatter.Format(outcome, backlogType);
        output.WriteLine();
        output.WriteLine($"[5/5 Jira -> {ticket.JiraStage} ({ticket.BacklogType})]");
        output.WriteLine(ticket.FormattedMarkdown);

        if (!string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            Directory.CreateDirectory(options.OutputDirectory);
            var mdPath = Path.Combine(options.OutputDirectory, "jira-backlog-item.md");
            var jsonPath = Path.Combine(options.OutputDirectory, "jira-backlog-item.json");

            File.WriteAllText(mdPath, ticket.FormattedMarkdown);
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(ticket, JsonOptions));

            output.WriteLine();
            output.WriteLine($"Saved Jira Markdown to: {mdPath}");
            output.WriteLine($"Saved Jira JSON to    : {jsonPath}");
        }

        return ticket;
    }
}
