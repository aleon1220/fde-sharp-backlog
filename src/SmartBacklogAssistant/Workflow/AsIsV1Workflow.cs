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
    bool NonInteractive = false,
    string? ModelName = null,
    string? WorkspaceRoot = null,
    string? GoalTitle = null,
    bool ForceMenu = false,
    bool? JsonFileOut = null);

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
    private readonly RuntimeEnvironmentService _runtimeEnvService;
    private readonly GeminiSecretProvider _secretProvider;

    public AsIsV1Workflow(
        RawDataCollector collector,
        PromptTemplateService promptService,
        AiModelOrchestrator orchestrator,
        JiraBacklogFormatter formatter,
        RuntimeEnvironmentService? runtimeEnvService = null,
        GeminiSecretProvider? secretProvider = null)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _promptService = promptService ?? throw new ArgumentNullException(nameof(promptService));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        _runtimeEnvService = runtimeEnvService ?? new RuntimeEnvironmentService();
        _secretProvider = secretProvider ?? new GeminiSecretProvider();
    }

    public JiraBacklogTicket Execute(WorkflowOptions options, TextReader input, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var colorWriter = new ConsoleColorWriter(output);
        colorWriter.WriteHeader("=== Smart Backlog Assistant: Use Case 1 (Practical Process As-Is v1) ===");

        // Runtime execution defaults to Google Gemini with a Gemini API Key unless another provider is explicitly requested.
        var provider = string.IsNullOrWhiteSpace(options.ProviderInput)
            ? AiProviderType.GoogleGemini
            : _orchestrator.ResolveProvider(options.ProviderInput);

        // Inspect OS, Build Info, Enabled AI Model, and initial secret status
        var initialSecret = _secretProvider.Resolve(options.ApiKey);
        var runtimeInfo = _runtimeEnvService.Inspect(provider, options.ModelName);
        colorWriter.WriteMuted(_runtimeEnvService.FormatRuntimeBanner(runtimeInfo, initialSecret));

        // 1. Local: Collect raw data (either from Goal/Title or from InputFilePath)
        string rawData;
        if (!string.IsNullOrWhiteSpace(options.GoalTitle))
        {
            colorWriter.WriteSubHeader($"[1/5 Local -> Collect] Generating raw discovery notes from goal/title '{options.GoalTitle}'...");
            rawData = _collector.CollectFromGoal(options.GoalTitle);
        }
        else
        {
            colorWriter.WriteSubHeader($"[1/5 Local -> Collect] Reading raw data from '{options.InputFilePath}'...");
            rawData = _collector.CollectFromFile(options.InputFilePath);
        }

        // 2. Local: Process & Inject Secret API Key at Runtime
        var renderedPrompt = File.Exists(options.PromptFilePath)
            ? _promptService.LoadAndRenderPrompt(options.PromptFilePath, rawData)
            : _promptService.RenderPromptFromTemplate("# Practical Process As-Is v1\nAnalyze the following raw notes:\n{{RAW_DATA}}", rawData);

        var apiKey = initialSecret.ApiKey;

        if (provider == AiProviderType.GoogleGemini)
        {
            var resolvedSecret = _secretProvider.ResolveOrPrompt(
                options.ApiKey,
                input,
                output,
                options.NonInteractive);
            apiKey = resolvedSecret.ApiKey;
        }
        else if (provider != AiProviderType.ManualPrompt && string.IsNullOrWhiteSpace(apiKey) && !options.NonInteractive)
        {
            colorWriter.WriteColored($"Enter API key for {provider} (or press Enter for mock key): ", ConsoleColor.Yellow);
            apiKey = input.ReadLine();
        }

        if (provider == AiProviderType.ManualPrompt)
        {
            output.WriteLine();
            colorWriter.WriteMuted("--- Pre-Designed Prompt (Loaded from Markdown) ---");
            output.WriteLine(renderedPrompt);
            colorWriter.WriteMuted("--------------------------------------------------");
        }

        // 3. Local: Validate outcome
        var outcome = _orchestrator.Process(rawData, renderedPrompt, provider, apiKey);
        colorWriter.WriteMuted(outcome.GuidanceMessage);
        colorWriter.WriteSuccess("[3/5 Local -> Validate] Outcome validated successfully.");

        // 4. Review: Review outcome & Decide backlog type
        output.WriteLine();
        colorWriter.WriteHeader("[4/5 Review -> Review Outcome]");
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
            colorWriter.WriteHeader("[4/5 Review -> Decide Backlog Type]");
            colorWriter.WriteMenuOption("1 / Type01", "complete", "Use Given / When / Then (default)");
            colorWriter.WriteMenuOption("2 / Type02", "schedule", "Use Title + Description");
            colorWriter.WriteColored("Select backlog type (01 or 02): ", ConsoleColor.Yellow);
            backlogTypeSelection = input.ReadLine();
        }

        var backlogType = _formatter.ResolveBacklogType(backlogTypeSelection);

        // 5. Jira: Format output based on Type01 (complete) or Type02 (schedule)
        var ticket = _formatter.Format(outcome, backlogType);
        output.WriteLine();
        colorWriter.WriteSuccess($"[5/5 Jira -> {ticket.JiraStage} ({ticket.BacklogType})]");
        output.WriteLine(ticket.FormattedMarkdown);

        if (!string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            var jsonFileOut = options.JsonFileOut ?? false;
            SaveTicketArtifacts(ticket, options.OutputDirectory, jsonFileOut, colorWriter);
        }

        return ticket;
    }

    public JiraBacklogTicket ExecuteQuickGoal(
        string goalOrTitle,
        string? backlogTypeInput,
        WorkflowOptions baseOptions,
        TextReader input,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(baseOptions);

        var goalOptions = baseOptions with
        {
            GoalTitle = goalOrTitle,
            BacklogTypeInput = backlogTypeInput
        };

        return Execute(goalOptions, input, output);
    }

    private static void SaveTicketArtifacts(
        JiraBacklogTicket ticket,
        string outputDirectory,
        bool jsonFileOut,
        ConsoleColorWriter colorWriter)
    {
        Directory.CreateDirectory(outputDirectory);

        // Canonical output filename (backward-compatible)
        var mdPath = Path.Combine(outputDirectory, "jira-backlog-item.md");
        File.WriteAllText(mdPath, ticket.FormattedMarkdown);

        // Slugged ready-to-add filename per item in smart-backlog-assistant/out
        var typePrefix = ticket.BacklogType.ToString().ToLowerInvariant();
        var slug = WorkspacePathService.CreateSlug(ticket.Title);
        var slugMdPath = Path.Combine(outputDirectory, $"{typePrefix}-{slug}.md");
        File.WriteAllText(slugMdPath, ticket.FormattedMarkdown);

        colorWriter.WriteLine();
        colorWriter.WriteSuccess($"Saved Jira Markdown to: {mdPath}");
        colorWriter.WriteMuted($"Ready-to-add item     : {slugMdPath}");

        if (jsonFileOut)
        {
            var serializedJson = JsonSerializer.Serialize(ticket, JsonOptions);
            var jsonPath = Path.Combine(outputDirectory, "jira-backlog-item.json");
            var slugJsonPath = Path.Combine(outputDirectory, $"{typePrefix}-{slug}.json");

            File.WriteAllText(jsonPath, serializedJson);
            File.WriteAllText(slugJsonPath, serializedJson);

            colorWriter.WriteSuccess($"Saved Jira JSON to    : {jsonPath}");
            colorWriter.WriteMuted($"Ready-to-add JSON     : {slugJsonPath}");
        }
    }
}
