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
    bool? JsonFileOut = null,
    bool? GeminiLive = null,
    bool ListGeminiModels = false,
    bool CombinedOutput = false,
    string? Subcommand = null,
    bool DryRun = false,
    bool ShowDoctorStatus = false,
    bool ShowVersion = false,
    string? CliTheme = null);

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
    private readonly GeminiApiClient _geminiApiClient;

    public AsIsV1Workflow(
        RawDataCollector collector,
        PromptTemplateService promptService,
        AiModelOrchestrator orchestrator,
        JiraBacklogFormatter formatter,
        RuntimeEnvironmentService? runtimeEnvService = null,
        GeminiSecretProvider? secretProvider = null,
        GeminiApiClient? geminiApiClient = null)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _promptService = promptService ?? throw new ArgumentNullException(nameof(promptService));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        _runtimeEnvService = runtimeEnvService ?? new RuntimeEnvironmentService();
        _secretProvider = secretProvider ?? new GeminiSecretProvider();
        _geminiApiClient = geminiApiClient ?? new GeminiApiClient();
    }

    public string? LastSelectedModel { get; private set; }

    public string? LastResolvedApiKey { get; private set; }

    public IReadOnlyList<GeminiModel> ListGeminiModels(string apiKey) =>
        _geminiApiClient.ListGenerateContentModels(apiKey);

    public JiraBacklogTicket Execute(WorkflowOptions options, TextReader input, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (string.IsNullOrWhiteSpace(options.GoalTitle) && Directory.Exists(options.InputFilePath))
        {
            var directoryFiles = _collector.GetInputFilesFromDirectory(options.InputFilePath);
            if (directoryFiles.Count > 1)
            {
                return ExecuteDirectoryBatch(directoryFiles, options, input, output);
            }
        }

        var colorWriter = new ConsoleColorWriter(
            output,
            theme: ConsoleColorWriter.ResolveTheme(options.CliTheme));
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
            colorWriter.WriteSubHeader($"[1/5 Local -> Collect] Preparing backlog goal '{options.GoalTitle}'...");
            rawData = _collector.CollectFromGoal(options.GoalTitle);
        }
        else
        {
            colorWriter.WriteSubHeader($"[1/5 Local -> Collect] Reading raw data from '{options.InputFilePath}'...");
            rawData = _collector.CollectFromFile(options.InputFilePath);
        }

        // 2. Local: Process & Inject Secret API Key at Runtime
        var apiKey = initialSecret.ApiKey;

        if (provider == AiProviderType.GoogleGemini && options.GeminiLive == true)
        {
            var resolvedSecret = _secretProvider.ResolveOrPrompt(
                options.ApiKey,
                input,
                output,
                options.NonInteractive);
            apiKey = resolvedSecret.ApiKey;
            LastResolvedApiKey = apiKey;
        }
        else if (provider == AiProviderType.GoogleGemini)
        {
            apiKey = initialSecret.ApiKey;
        }
        else if (provider != AiProviderType.ManualPrompt && string.IsNullOrWhiteSpace(apiKey) && !options.NonInteractive)
        {
            colorWriter.WriteColored($"Enter API key for {provider} (or press Enter for mock key): ", ConsoleColor.Yellow);
            apiKey = input.ReadLine();
            LastResolvedApiKey = apiKey;
        }

        var backlogTypeSelection = options.BacklogTypeInput;
        if (options.CombinedOutput && string.IsNullOrWhiteSpace(backlogTypeSelection))
        {
            backlogTypeSelection = "both";
        }
        else if (string.IsNullOrWhiteSpace(backlogTypeSelection) && !options.NonInteractive)
        {
            output.WriteLine();
            colorWriter.WriteHeader("[4/5 Review -> Decide Backlog Type]");
            colorWriter.WriteMenuOption("1 / Type01", "complete", "Use Given / When / Then (default)");
            colorWriter.WriteMenuOption("2 / Type02", "schedule", "Use Title + Description");
            colorWriter.WriteMenuOption("3 / Both", "combined", "Generate both formats");
            colorWriter.WriteColored("Select backlog type (01, 02, or 3): ", ConsoleColor.Yellow);
            backlogTypeSelection = input.ReadLine();
        }

        var isCombinedType = options.CombinedOutput ||
                             string.Equals(backlogTypeSelection?.Trim(), "both", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(backlogTypeSelection?.Trim(), "all", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(backlogTypeSelection?.Trim(), "3", StringComparison.OrdinalIgnoreCase);
        var backlogType = _formatter.ResolveBacklogType(backlogTypeSelection);
        var promptType = isCombinedType ? BacklogType.Type01 : backlogType;
        var selectedPromptPath = _promptService.ResolvePromptFilePath(options.PromptFilePath, promptType);
        var existingSelectedPrompt = _promptService.TryFindExistingPromptPath(selectedPromptPath);
        var existingFallbackPrompt = _promptService.TryFindExistingPromptPath(options.PromptFilePath);
        var renderedPrompt = existingSelectedPrompt is not null
            ? _promptService.LoadAndRenderPrompt(existingSelectedPrompt, rawData)
            : existingFallbackPrompt is not null
                ? _promptService.LoadAndRenderPrompt(existingFallbackPrompt, rawData)
                : _promptService.RenderPromptFromTemplate("# Practical Process As-Is v1\nAnalyze the following raw notes:\n{{RAW_DATA}}", rawData);

        var modelName = options.ModelName;
        if (options.GeminiLive == true)
        {
            if (provider != AiProviderType.GoogleGemini)
            {
                throw new InvalidOperationException("--gemini-live can only be used with the Gemini provider.");
            }

            colorWriter.WriteSubHeader("[2/5 Local -> Process] Calling Gemini API to list models supporting generateContent...");
            var availableModels = _geminiApiClient.ListGenerateContentModels(apiKey!);
            modelName = SelectGeminiModel(availableModels, modelName, input, output, options.NonInteractive);
            LastSelectedModel = modelName;
        }

        if (provider == AiProviderType.ManualPrompt)
        {
            output.WriteLine();
            colorWriter.WriteMuted("--- Pre-Designed Prompt (Loaded from Markdown) ---");
            output.WriteLine(renderedPrompt);
            colorWriter.WriteMuted("--------------------------------------------------");
        }

        // 3. Local: Generate & Validate outcome
        string? generatedContent = null;
        if (options.GeminiLive == true)
        {
            colorWriter.WriteSubHeader($"[2/5 Local -> Process] Calling Google Gemini API ({modelName}) with '{Path.GetFileName(existingSelectedPrompt ?? selectedPromptPath)}'...");
            generatedContent = _geminiApiClient.GenerateContent(apiKey!, modelName!, BuildGeminiPrompt(renderedPrompt, promptType));
        }
        else if (provider == AiProviderType.GoogleGemini)
        {
            colorWriter.WriteMuted("[2/5 Local -> Process] Live Gemini API is OFF; using the local deterministic draft.");
        }

        var outcome = _orchestrator.Process(rawData, renderedPrompt, provider, apiKey, generatedContent, modelName, promptType);
        colorWriter.WriteMuted(outcome.GuidanceMessage);
        colorWriter.WriteSuccess("[3/5 Local -> Validate] Outcome validated successfully.");

        // 4. Review: Review outcome & Decide backlog type
        output.WriteLine();
        colorWriter.WriteHeader("[4/5 Review -> Review Outcome]");
        output.WriteLine($"  Title      : {outcome.Title}");
        output.WriteLine($"  Description: {outcome.Description}");
        if (!string.IsNullOrWhiteSpace(outcome.UserStory))
        {
            output.WriteLine($"  User Story : {outcome.UserStory}");
        }

        foreach (var scenario in outcome.Scenarios)
        {
            output.WriteLine($"  Scenario   : Given {scenario.Given} | When {scenario.When} | Then {scenario.Then}");
        }

        IReadOnlyList<JiraBacklogTicket> tickets = isCombinedType
            ? [
                _formatter.Format(outcome, BacklogType.Type01),
                _formatter.Format(outcome, BacklogType.Type02)
            ]
            : [_formatter.Format(outcome, backlogType)];

        foreach (var ticketToOutput in tickets)
        {
            output.WriteLine();
            colorWriter.WriteSuccess($"[5/5 Jira -> {ticketToOutput.JiraStage} ({ticketToOutput.BacklogType})]");
            output.WriteLine(ticketToOutput.FormattedMarkdown);
        }

        if (options.DryRun)
        {
            output.WriteLine();
            colorWriter.WriteSuccess($"[Preview / Dry Run] Validated '{tickets[0].Title}' ({tickets[0].BacklogType} -> {tickets[0].JiraStage}). No files written.");
        }
        else if (!string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            var jsonFileOut = options.JsonFileOut ?? false;
            foreach (var ticketToSave in tickets)
            {
                SaveTicketArtifacts(ticketToSave, options.OutputDirectory, jsonFileOut, colorWriter);
            }
        }

        return tickets[0];
    }

    private JiraBacklogTicket ExecuteDirectoryBatch(
        IReadOnlyList<string> files,
        WorkflowOptions baseOptions,
        TextReader input,
        TextWriter output)
    {
        var currentOptions = baseOptions;
        JiraBacklogTicket? lastTicket = null;

        foreach (var filePath in files)
        {
            var fileOptions = currentOptions with { InputFilePath = filePath };
            lastTicket = Execute(fileOptions, input, output);
            currentOptions = currentOptions with
            {
                ApiKey = LastResolvedApiKey ?? currentOptions.ApiKey,
                ModelName = LastSelectedModel ?? currentOptions.ModelName,
                BacklogTypeInput = currentOptions.BacklogTypeInput ?? (lastTicket.BacklogType == BacklogType.Type02 ? "Type02" : "Type01")
            };
        }

        return lastTicket!;
    }

    public static string SelectGeminiModel(
        IReadOnlyList<GeminiModel> availableModels,
        string? requestedModel,
        TextReader input,
        TextWriter output,
        bool nonInteractive)
    {
        if (availableModels.Count == 0)
        {
            throw new InvalidOperationException("The Gemini API key has no models that support generateContent.");
        }

        var defaultModelId = ResolveDefaultModelId(availableModels);
        var selectedModel = requestedModel;
        if (string.IsNullOrWhiteSpace(selectedModel) && !nonInteractive)
        {
            output.WriteLine();
            output.WriteLine("Gemini models available to this API key:");
            for (var index = 0; index < availableModels.Count; index++)
            {
                var model = availableModels[index];
                output.WriteLine($"  [{index + 1}] {model.DisplayName} ({model.ModelId})");
            }

            output.Write($"Select a model [default: {defaultModelId}]: ");
            var selection = input.ReadLine()?.Trim();
            if (int.TryParse(selection, out var modelIndex) && modelIndex >= 1 && modelIndex <= availableModels.Count)
            {
                selectedModel = availableModels[modelIndex - 1].ModelId;
            }
            else if (!string.IsNullOrWhiteSpace(selection))
            {
                selectedModel = selection;
            }
        }

        selectedModel = string.IsNullOrWhiteSpace(selectedModel)
            ? defaultModelId
            : selectedModel.Trim();
        if (selectedModel.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            selectedModel = selectedModel["models/".Length..];
        }

        var match = availableModels.FirstOrDefault(model =>
            string.Equals(model.ModelId, selectedModel, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new InvalidOperationException(
                $"Gemini model '{selectedModel}' is not available for generateContent with this API key. Run --list-models to see compatible models.");
        }

        return match.ModelId;
    }

    private static string ResolveDefaultModelId(IReadOnlyList<GeminiModel> availableModels)
    {
        var envModel = Environment.GetEnvironmentVariable(RuntimeEnvironmentService.GeminiModelEnvVar)?.Trim();
        if (!string.IsNullOrWhiteSpace(envModel))
        {
            var normalizedEnv = envModel.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? envModel["models/".Length..]
                : envModel;
            var envMatch = availableModels.FirstOrDefault(m =>
                string.Equals(m.ModelId, normalizedEnv, StringComparison.OrdinalIgnoreCase));
            if (envMatch is not null)
            {
                return envMatch.ModelId;
            }
        }

        string[] preferredModels =
        [
            RuntimeEnvironmentService.DefaultGeminiModel,
            "gemini-2.5-flash",
            "gemini-2.0-flash",
            "gemini-1.5-flash"
        ];

        foreach (var candidate in preferredModels)
        {
            var match = availableModels.FirstOrDefault(m =>
                string.Equals(m.ModelId, candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.ModelId;
            }
        }

        var flashModel = availableModels.FirstOrDefault(m =>
            m.ModelId.Contains("flash", StringComparison.OrdinalIgnoreCase));
        return (flashModel ?? availableModels[0]).ModelId;
    }

    private static string BuildGeminiPrompt(string renderedPrompt, BacklogType backlogType)
    {
        var responseContract = backlogType == BacklogType.Type02
            ? "Return only a JSON object with string properties `title` and `description`. Do not include a user story or acceptance criteria."
            : "Return only a JSON object with string properties `title`, `description`, `userStory`, and a `scenarios` array containing `given`, `when`, and `then`.";

        return $"{renderedPrompt.Trim()}\n\n{responseContract} Do not include Markdown fences or commentary.";
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
