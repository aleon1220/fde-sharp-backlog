using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    string? CliTheme = null,
    string? GoalContext = null,
    bool WebSearchGrounding = false,
    string? RefinementFeedback = null,
    string? PreviousDraftContext = null,
    bool? ExpandGoalContext = null,
    bool Verbose = false,
    bool ShowAlternatives = false,
    OutcomeVerbosity Verbosity = OutcomeVerbosity.Balanced,
    string? LogLevelInput = null,
    string? LogFormatInput = null,
    bool Quiet = false);

public sealed class AsIsV1Workflow
{
    private const string DefaultGoalExpandFallbackTemplate = """
        # As-Is v1 — Creative Goal-to-Notes Expansion (Step 1)
        Apply User Story creation principles (INVEST) and domain creativity to turn this Goal into structured discovery context, an enhanced title, a User Story, a checklist, and 3 intuitive Title & Description alternatives.
        {{RAW_DATA}}
        """;

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
    private readonly AiProviderOrchestrator _providerOrchestrator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AsIsV1Workflow> _logger;
    private readonly SecretRedactor? _secretRedactor;

    public AsIsV1Workflow(
        RawDataCollector collector,
        PromptTemplateService promptService,
        AiModelOrchestrator orchestrator,
        JiraBacklogFormatter formatter,
        RuntimeEnvironmentService? runtimeEnvService = null,
        GeminiSecretProvider? secretProvider = null,
        GeminiApiClient? geminiApiClient = null,
        AiProviderOrchestrator? providerOrchestrator = null,
        ILoggerFactory? loggerFactory = null)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _promptService = promptService ?? throw new ArgumentNullException(nameof(promptService));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        _runtimeEnvService = runtimeEnvService ?? new RuntimeEnvironmentService();
        _secretProvider = secretProvider ?? new GeminiSecretProvider();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<AsIsV1Workflow>();
        _secretRedactor = (_loggerFactory as StderrLoggerFactory)?.Redactor;
        _geminiApiClient = geminiApiClient ?? new GeminiApiClient(logger: _loggerFactory.CreateLogger<GeminiApiClient>());
        _providerOrchestrator = providerOrchestrator ?? new AiProviderOrchestrator(_geminiApiClient, loggerFactory: _loggerFactory);
    }

    public string? LastSelectedModel { get; private set; }

    public string? LastResolvedApiKey { get; private set; }

    public string? LastWebResearchFindings { get; private set; }

    public ProcessedOutcome? LastOutcome { get; private set; }

    public IReadOnlyList<JiraBacklogTicket> LastGeneratedTickets { get; private set; } = [];

    public IReadOnlyList<string> LastMarkdownDocuments { get; private set; } = [];

    public string? LastSupplementalUserStory { get; private set; }

    public string? LastSupplementalDescription { get; private set; }

    public AiProviderOrchestrator ProviderOrchestrator => _providerOrchestrator;

    public IReadOnlyList<GeminiModel> ListGeminiModels(string apiKey) =>
        _geminiApiClient.ListGenerateContentModels(apiKey);

    public GeminiModelCatalogResult ListGeminiModelCatalog(string apiKey) =>
        _geminiApiClient.ListModelCatalog(apiKey);

    public IReadOnlyList<CopilotModelDescriptor> ListCopilotModels(string? gitHubToken = null) =>
        _providerOrchestrator.CopilotGateway.ListModelsAsync(gitHubToken).GetAwaiter().GetResult();

    public JiraBacklogTicket Execute(WorkflowOptions options, TextReader input, TextWriter output)
    {
        var tickets = ExecuteCore(options, input, output, out _);
        return tickets[0];
    }

    public IReadOnlyList<JiraBacklogTicket> ExecuteDraftPreview(
        WorkflowOptions options,
        TextReader input,
        TextWriter output,
        out ProcessedOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ExecuteCore(
            options with { DryRun = true, OutputDirectory = null, ShowAlternatives = true },
            input,
            output,
            out outcome);
    }

    public IReadOnlyList<JiraBacklogTicket> ReformatOutcome(
        ProcessedOutcome outcome,
        string? backlogTypeInput,
        bool combinedOutput,
        OutcomeVerbosity verbosity = OutcomeVerbosity.Balanced)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var isCombinedType = combinedOutput ||
                             string.Equals(backlogTypeInput?.Trim(), "both", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(backlogTypeInput?.Trim(), "all", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(backlogTypeInput?.Trim(), "3", StringComparison.OrdinalIgnoreCase);

        if (isCombinedType)
        {
            return
            [
                _formatter.Format(outcome, BacklogType.Type01, verbosity),
                _formatter.Format(outcome, BacklogType.Type02, verbosity)
            ];
        }

        var resolvedType = _formatter.ResolveBacklogType(backlogTypeInput);
        return [_formatter.Format(outcome, resolvedType, verbosity)];
    }

    public IReadOnlyList<string> PersistTickets(
        IReadOnlyList<JiraBacklogTicket> tickets,
        string outputDirectory,
        bool jsonFileOut,
        TextWriter output,
        string? cliTheme = null)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(output);

        var colorWriter = new ConsoleColorWriter(output, theme: ConsoleColorWriter.ResolveTheme(cliTheme));
        var savedMdPaths = new List<string>(tickets.Count);
        foreach (var ticket in tickets)
        {
            savedMdPaths.Add(SaveTicketArtifacts(ticket, outputDirectory, jsonFileOut, colorWriter));
        }

        return savedMdPaths;
    }

    public static string FormatOutcomeContext(ProcessedOutcome outcome, string? webResearchFindings = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Title: {outcome.Title}");
        sb.AppendLine($"Description: {outcome.Description}");
        if (!string.IsNullOrWhiteSpace(outcome.UserStory) &&
            !outcome.UserStory.Equals("Not specified in discovery notes.", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"User Story: {outcome.UserStory}");
        }

        for (var i = 0; i < outcome.Scenarios.Count; i++)
        {
            var s = outcome.Scenarios[i];
            sb.AppendLine($"Scenario {i + 1}: Given {s.Given} | When {s.When} | Then {s.Then}");
        }

        if (!string.IsNullOrWhiteSpace(webResearchFindings))
        {
            sb.AppendLine();
            sb.AppendLine("Web Research Findings:");
            sb.AppendLine(webResearchFindings.Trim());
        }

        return sb.ToString().TrimEnd();
    }

    private IReadOnlyList<JiraBacklogTicket> ExecuteCore(
        WorkflowOptions options,
        TextReader input,
        TextWriter output,
        out ProcessedOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (string.IsNullOrWhiteSpace(options.GoalTitle) && Directory.Exists(options.InputFilePath))
        {
            var resolveSw = System.Diagnostics.Stopwatch.StartNew();
            var directoryFiles = _collector.GetInputFilesFromDirectory(options.InputFilePath);
            resolveSw.Stop();

            if (directoryFiles.Count == 0)
            {
                throw new FileNotFoundException($"No raw input files (.txt or .md) found in directory '{options.InputFilePath}'.", options.InputFilePath);
            }

            var batchColor = new ConsoleColorWriter(
                output,
                theme: ConsoleColorWriter.ResolveTheme(options.CliTheme));

            if (directoryFiles.Count == 1)
            {
                TerminalUx.WriteStepDone(batchColor, "Resolved", $"1 queued input file ({Path.GetFileName(directoryFiles[0])})", resolveSw.Elapsed);
            }
            else
            {
                TerminalUx.WriteStepDone(batchColor, "Resolved", $"{directoryFiles.Count} queued input files", resolveSw.Elapsed);
                var batchTicket = ExecuteDirectoryBatch(directoryFiles, options, input, output);
                outcome = LastOutcome!;
                return LastGeneratedTickets.Count > 0 ? LastGeneratedTickets : [batchTicket];
            }
        }

        var executionStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var colorWriter = new ConsoleColorWriter(
            output,
            theme: ConsoleColorWriter.ResolveTheme(options.CliTheme));

        // Runtime execution defaults to Google Gemini with a Gemini API Key unless another provider is explicitly requested.
        var provider = string.IsNullOrWhiteSpace(options.ProviderInput)
            ? AiProviderType.GoogleGemini
            : _orchestrator.ResolveProvider(options.ProviderInput);

        var sourceType = !string.IsNullOrWhiteSpace(options.GoalTitle) ? "goal" : "file";
        var modeLabel = options.GeminiLive == false ? "offline" : (options.GeminiLive == true ? "live" : "default");

        using var workflowScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["provider"] = RuntimeEnvironmentService.FormatProviderCode(provider),
            ["source"] = sourceType,
            ["dryRun"] = options.DryRun
        });

        LogEvents.WorkflowStarted(_logger, RuntimeEnvironmentService.FormatProviderCode(provider), modeLabel, sourceType, options.DryRun);

        // Inspect OS, Build Info, Enabled AI Model, and initial secret status
        var initialSecret = _secretProvider.Resolve(options.ApiKey);
        _secretRedactor?.RegisterSecret(initialSecret.ApiKey);
        _secretRedactor?.RegisterSecret(options.ApiKey);
        var runtimeInfo = _runtimeEnvService.Inspect(provider, options.ModelName);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            LogEvents.RuntimeBannerLogged(_logger, _runtimeEnvService.FormatRuntimeBanner(runtimeInfo, initialSecret).TrimEnd());
        }

        // 1. Local: Collect raw data (either from Goal/Title or from InputFilePath)
        var collectSw = System.Diagnostics.Stopwatch.StartNew();
        string rawData;
        if (!string.IsNullOrWhiteSpace(options.GoalTitle))
        {
            LogEvents.WorkflowCollectStage(_logger, "goal", options.GoalTitle);

            var hasExtendedGoalContext = !string.IsNullOrWhiteSpace(options.GoalContext) ||
                                         !string.IsNullOrWhiteSpace(options.PreviousDraftContext) ||
                                         !string.IsNullOrWhiteSpace(options.RefinementFeedback);
            rawData = hasExtendedGoalContext
                ? _collector.CollectFromGoalWithContext(
                    options.GoalTitle,
                    details: options.GoalContext,
                    previousDraftSummary: options.PreviousDraftContext,
                    refinementInstructions: options.RefinementFeedback)
                : _collector.CollectFromGoal(options.GoalTitle);
        }
        else
        {
            LogEvents.WorkflowCollectStage(_logger, "file", options.InputFilePath);
            rawData = _collector.CollectFromFile(options.InputFilePath);
        }

        collectSw.Stop();
        LogEvents.WorkflowCollected(_logger, rawData.Length, collectSw.ElapsedMilliseconds);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            LogEvents.WorkflowRawDataTrace(_logger, rawData.Length, BuildSingleLinePreview(rawData));
        }

        var collectDetail = !string.IsNullOrWhiteSpace(options.GoalTitle)
            ? $"goal '{options.GoalTitle}'"
            : $"raw discovery context ({Path.GetFileName(options.InputFilePath)})";
        TerminalUx.WriteStepDone(colorWriter, "Collected", collectDetail, collectSw.Elapsed);

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
            _secretRedactor?.RegisterSecret(apiKey);
            LogEvents.SecretResolved(_logger, resolvedSecret.SourceDetail, resolvedSecret.MaskedKey);
        }
        else if (provider == AiProviderType.GoogleGemini)
        {
            apiKey = initialSecret.ApiKey;
            LogEvents.SecretResolved(_logger, initialSecret.SourceDetail, initialSecret.MaskedKey);
        }
        else if (provider == AiProviderType.GitHubCopilot)
        {
            apiKey = options.ApiKey;
            _secretRedactor?.RegisterSecret(apiKey);
            LogEvents.SecretResolved(_logger, string.IsNullOrWhiteSpace(apiKey) ? "Copilot runtime / env" : "CLI Argument (--api-key)", GeminiSecretProvider.MaskKey(apiKey));
        }
        else if (provider != AiProviderType.ManualPrompt && string.IsNullOrWhiteSpace(apiKey) && !options.NonInteractive)
        {
            colorWriter.WriteColored($"Enter API key for {provider} (or press Enter for mock key): ", ConsoleColor.Yellow);
            apiKey = input.ReadLine();
            LastResolvedApiKey = apiKey;
            _secretRedactor?.RegisterSecret(apiKey);
            LogEvents.SecretResolved(_logger, "Interactive CLI Prompt", GeminiSecretProvider.MaskKey(apiKey));
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

        var modelName = options.ModelName;
        if (provider == AiProviderType.GoogleGemini && options.GeminiLive == true)
        {
            LogEvents.WorkflowProcessStage(_logger, "Calling Gemini API to list models supporting generateContent...");

            var availableModels = TerminalUx.RunWithSpinner(
                "Discovering compatible Gemini models...",
                () => _geminiApiClient.ListGenerateContentModels(apiKey!),
                colorWriter);
            modelName = SelectGeminiModel(availableModels, modelName, input, output, options.NonInteractive);
            LastSelectedModel = modelName;
        }
        else if (options.GeminiLive == true && provider != AiProviderType.GoogleGemini && provider != AiProviderType.GitHubCopilot && provider != AiProviderType.AzureOpenAi && provider != AiProviderType.AnthropicClaude)
        {
            throw new InvalidOperationException("--gemini-live can only be used with live AI providers.");
        }

        // Optional live Google Search grounding before structured prompt rendering
        LastWebResearchFindings = null;
        if (options.WebSearchGrounding)
        {
            var searchTopic = BuildWebSearchTopic(options.GoalTitle, options.GoalContext, rawData);
            if (provider == AiProviderType.GoogleGemini && options.GeminiLive == true)
            {
                LogEvents.WorkflowProcessStage(_logger, $"Running live Google Search grounding ({modelName})...");

                var searchSw = System.Diagnostics.Stopwatch.StartNew();
                var grounding = TerminalUx.RunWithSpinner(
                    $"Searching web context via Google Search ({modelName})...",
                    () => _geminiApiClient.GatherWebGrounding(apiKey!, modelName!, searchTopic),
                    colorWriter);
                searchSw.Stop();

                if (grounding.Succeeded && !string.IsNullOrWhiteSpace(grounding.Summary))
                {
                    var formattedFindings = FormatWebGroundingFindings(grounding);
                    LastWebResearchFindings = formattedFindings;
                    rawData = $"{rawData.TrimEnd()}\n\nWeb Research Findings:\n{formattedFindings}";
                    LogEvents.WebGroundingCompleted(_logger, grounding.Sources.Count, searchSw.ElapsedMilliseconds);
                    TerminalUx.WriteStepDone(
                        colorWriter,
                        "Grounded",
                        grounding.Sources.Count > 0
                            ? $"web research from {grounding.Sources.Count} source(s)"
                            : "web research summary",
                        searchSw.Elapsed);
                }
                else if (!string.IsNullOrWhiteSpace(grounding.Warning))
                {
                    LogEvents.WebGroundingSkipped(_logger, grounding.Warning);
                    colorWriter.WriteWarning($"  [!] Google Search grounding skipped ({grounding.Warning}); continuing with standard draft.");
                }
            }
            else if (options.GeminiLive == true)
            {
                colorWriter.WriteMuted($"  [i] Google Search grounding (--search) runs with Google Gemini Live mode; continuing with {provider}.");
            }
        }

        var liveEnabled = options.GeminiLive ?? (provider == AiProviderType.GitHubCopilot);
        var isProviderLiveForGeneration = provider switch
        {
            AiProviderType.GoogleGemini => options.GeminiLive == true,
            AiProviderType.GitHubCopilot => liveEnabled,
            AiProviderType.AzureOpenAi or AiProviderType.AnthropicClaude => liveEnabled,
            _ => false
        };

        GoalExpansionResult? capturedExpansion = null;
        var isBareGoalDraft = GoalExpansionSynthesizer.IsBareGoalWithoutDiscoveryNotes(options.GoalTitle, rawData);
        var hasNoPreviousDraft = string.IsNullOrWhiteSpace(options.PreviousDraftContext);
        var shouldExpandLive = isBareGoalDraft &&
                               hasNoPreviousDraft &&
                               isProviderLiveForGeneration &&
                               (options.ExpandGoalContext ?? _promptService.IsBuiltInPromptPath(options.PromptFilePath));

        if (shouldExpandLive)
        {
            var expandPromptPath = _promptService.TryFindExistingPromptPath(PromptTemplateService.GoalExpandPromptFilePath);
            var renderedExpandPrompt = expandPromptPath is not null
                ? _promptService.LoadAndRenderPrompt(expandPromptPath, rawData)
                : _promptService.RenderPromptFromTemplate(DefaultGoalExpandFallbackTemplate, rawData);

            var expandModelLabel = provider switch
            {
                AiProviderType.GoogleGemini => modelName ?? RuntimeEnvironmentService.DefaultGeminiModel,
                AiProviderType.GitHubCopilot => modelName ?? RuntimeEnvironmentService.DefaultCopilotModel,
                _ => modelName ?? provider.ToString()
            };

            if (provider is AiProviderType.GoogleGemini or AiProviderType.GitHubCopilot)
            {
                LogEvents.WorkflowProcessStage(_logger, $"Expanding goal with INVEST User Story principles ({expandModelLabel})...");
            }

            var expandSw = System.Diagnostics.Stopwatch.StartNew();
            var expansionGen = provider is AiProviderType.GoogleGemini or AiProviderType.GitHubCopilot
                ? TerminalUx.RunWithSpinner(
                    $"Synthesizing goal brief & 3 title alternatives ({expandModelLabel})...",
                    () => _providerOrchestrator.GenerateGoalExpansion(new MultiProviderGenerationRequest(
                        Provider: provider,
                        RenderedPrompt: renderedExpandPrompt,
                        BacklogType: promptType,
                        ApiKey: apiKey,
                        ModelName: modelName,
                        LiveEnabled: true,
                        WorkingDirectory: options.WorkspaceRoot)),
                    colorWriter)
                : _providerOrchestrator.GenerateGoalExpansion(new MultiProviderGenerationRequest(
                    Provider: provider,
                    RenderedPrompt: renderedExpandPrompt,
                    BacklogType: promptType,
                    ApiKey: apiKey,
                    ModelName: modelName,
                    LiveEnabled: liveEnabled,
                    WorkingDirectory: options.WorkspaceRoot));
            expandSw.Stop();

            if (expansionGen.UsedLiveProvider && !string.IsNullOrWhiteSpace(expansionGen.GeneratedContent))
            {
                try
                {
                    capturedExpansion = _orchestrator.ParseGoalExpansionResponse(expansionGen.GeneratedContent, options.GoalTitle!);
                    rawData = GoalExpansionSynthesizer.MergeExpansionIntoRawData(rawData, capturedExpansion, includeSyntheticGwt: false);
                    TerminalUx.WriteStepDone(
                        colorWriter,
                        "Expanded",
                        $"goal brief & {capturedExpansion.Alternatives.Count} alternatives ({expansionGen.ResolvedModel ?? expandModelLabel})",
                        expandSw.Elapsed);
                }
                catch (System.Text.Json.JsonException jsonEx)
                {
                    LogEvents.GoalExpansionJsonFallback(_logger, jsonEx);
                    colorWriter.WriteWarning("  [!] Goal expansion response was not valid JSON; using deterministic creative synthesis.");
                    capturedExpansion = GoalExpansionSynthesizer.ExpandOffline(options.GoalTitle!, options.GoalContext, LastWebResearchFindings);
                    rawData = GoalExpansionSynthesizer.MergeExpansionIntoRawData(rawData, capturedExpansion, includeSyntheticGwt: false);
                }
            }
            else if (!expansionGen.UsedLiveProvider)
            {
                capturedExpansion = GoalExpansionSynthesizer.ExpandOffline(options.GoalTitle!, options.GoalContext, LastWebResearchFindings);
                rawData = GoalExpansionSynthesizer.MergeExpansionIntoRawData(rawData, capturedExpansion, includeSyntheticGwt: true);
                TerminalUx.WriteStepDone(
                    colorWriter,
                    "Expanded",
                    $"goal brief & {capturedExpansion.Alternatives.Count} alternatives (offline)",
                    expandSw.Elapsed);
            }
        }
        else if (isBareGoalDraft &&
                 hasNoPreviousDraft &&
                 !isProviderLiveForGeneration &&
                 (options.ExpandGoalContext ?? true))
        {
            var expandOfflineSw = System.Diagnostics.Stopwatch.StartNew();
            capturedExpansion = GoalExpansionSynthesizer.ExpandOffline(options.GoalTitle!, options.GoalContext, LastWebResearchFindings);
            rawData = GoalExpansionSynthesizer.MergeExpansionIntoRawData(rawData, capturedExpansion, includeSyntheticGwt: true);
            expandOfflineSw.Stop();
            TerminalUx.WriteStepDone(
                colorWriter,
                "Expanded",
                $"goal brief & {capturedExpansion.Alternatives.Count} alternatives (offline)",
                expandOfflineSw.Elapsed);
        }

        var promptSourceName = existingSelectedPrompt ?? existingFallbackPrompt ?? "built-in-fallback";
        var renderedPrompt = existingSelectedPrompt is not null
            ? _promptService.LoadAndRenderPrompt(existingSelectedPrompt, rawData)
            : existingFallbackPrompt is not null
                ? _promptService.LoadAndRenderPrompt(existingFallbackPrompt, rawData)
                : _promptService.RenderPromptFromTemplate("# Practical Process As-Is v1\nAnalyze the following raw notes:\n{{RAW_DATA}}", rawData);
        LogEvents.PromptRendered(_logger, promptSourceName, promptType.ToString(), renderedPrompt.Length);

        if (provider == AiProviderType.ManualPrompt)
        {
            output.WriteLine();
            colorWriter.WriteMuted("--- Pre-Designed Prompt (Loaded from Markdown) ---");
            output.WriteLine(renderedPrompt);
            colorWriter.WriteMuted("--------------------------------------------------");
        }

        // 3. Local: Generate & Validate outcome
        string? generatedContent = null;
        var loggedLiveGenerationStep = false;
        var processSw = System.Diagnostics.Stopwatch.StartNew();

        if (provider == AiProviderType.GoogleGemini && options.GeminiLive == true)
        {
            LogEvents.WorkflowProcessStage(_logger, $"Calling Google Gemini API ({modelName}) with '{Path.GetFileName(existingSelectedPrompt ?? selectedPromptPath)}'...");

            var aiSw = System.Diagnostics.Stopwatch.StartNew();
            var generationResult = TerminalUx.RunWithSpinner(
                $"Generating {promptType} draft via Google Gemini ({modelName})...",
                () => _providerOrchestrator.GenerateDraft(new MultiProviderGenerationRequest(
                    Provider: provider,
                    RenderedPrompt: renderedPrompt,
                    BacklogType: promptType,
                    ApiKey: apiKey,
                    ModelName: modelName,
                    LiveEnabled: true,
                    WorkingDirectory: options.WorkspaceRoot)),
                colorWriter);
            aiSw.Stop();
            generatedContent = generationResult.GeneratedContent;
            modelName = generationResult.ResolvedModel ?? modelName;
            loggedLiveGenerationStep = true;
            TerminalUx.WriteStepDone(
                colorWriter,
                "Generated",
                $"structured {promptType} draft via Google Gemini ({modelName})",
                aiSw.Elapsed);
        }
        else if (provider == AiProviderType.GoogleGemini)
        {
            LogEvents.WorkflowProcessStage(_logger, "Live Gemini API is OFF; using the local deterministic draft.");
        }
        else if (provider == AiProviderType.GitHubCopilot)
        {
            var effectiveModel = modelName ?? RuntimeEnvironmentService.DefaultCopilotModel;
            if (liveEnabled)
            {
                LogEvents.WorkflowProcessStage(_logger, $"Calling GitHub Copilot SDK ({effectiveModel}) with '{Path.GetFileName(existingSelectedPrompt ?? selectedPromptPath)}'...");

                var aiSw = System.Diagnostics.Stopwatch.StartNew();
                var generationResult = TerminalUx.RunWithSpinner(
                    $"Generating {promptType} draft via GitHub Copilot SDK ({effectiveModel})...",
                    () => _providerOrchestrator.GenerateDraft(new MultiProviderGenerationRequest(
                        Provider: AiProviderType.GitHubCopilot,
                        RenderedPrompt: renderedPrompt,
                        BacklogType: promptType,
                        ApiKey: apiKey,
                        ModelName: effectiveModel,
                        LiveEnabled: true,
                        WorkingDirectory: options.WorkspaceRoot)),
                    colorWriter);
                aiSw.Stop();
                generatedContent = generationResult.GeneratedContent;
                modelName = generationResult.ResolvedModel ?? effectiveModel;
                LastSelectedModel = modelName;
                loggedLiveGenerationStep = true;
                TerminalUx.WriteStepDone(colorWriter, "Generated", $"structured {promptType} draft via Copilot SDK ({modelName})", aiSw.Elapsed);
            }
            else
            {
                LogEvents.WorkflowProcessStage(_logger, "Offline mode active; using local deterministic draft.");
            }
        }
        else if (provider is AiProviderType.AzureOpenAi or AiProviderType.AnthropicClaude)
        {
            var byokSw = System.Diagnostics.Stopwatch.StartNew();
            var generationResult = _providerOrchestrator.GenerateDraft(new MultiProviderGenerationRequest(
                Provider: provider,
                RenderedPrompt: renderedPrompt,
                BacklogType: promptType,
                ApiKey: apiKey,
                ModelName: modelName,
                LiveEnabled: liveEnabled,
                WorkingDirectory: options.WorkspaceRoot));
            byokSw.Stop();
            generatedContent = generationResult.GeneratedContent;
            modelName = generationResult.ResolvedModel ?? modelName;
            if (generationResult.UsedLiveProvider)
            {
                loggedLiveGenerationStep = true;
                TerminalUx.WriteStepDone(colorWriter, "Generated", $"structured {promptType} draft via {provider} ({modelName})", byokSw.Elapsed);
            }
        }

        if (generatedContent is null &&
            capturedExpansion is null &&
            isBareGoalDraft &&
            hasNoPreviousDraft &&
            (options.ExpandGoalContext ?? true))
        {
            capturedExpansion = GoalExpansionSynthesizer.ExpandOffline(options.GoalTitle!, options.GoalContext, LastWebResearchFindings);
            rawData = GoalExpansionSynthesizer.MergeExpansionIntoRawData(rawData, capturedExpansion, includeSyntheticGwt: true);
        }

        outcome = _orchestrator.Process(
            rawData,
            renderedPrompt,
            provider,
            apiKey,
            generatedContent,
            modelName,
            promptType,
            options.Verbosity);
        processSw.Stop();
        if (capturedExpansion is not null)
        {
            var resolvedAlternatives = outcome.Alternatives is { Count: > 0 }
                ? outcome.Alternatives
                : capturedExpansion.Alternatives;
            var resolvedUserStory = string.IsNullOrWhiteSpace(outcome.UserStory) ||
                                    outcome.UserStory.Equals("Not specified in discovery notes.", StringComparison.OrdinalIgnoreCase)
                ? capturedExpansion.UserStory
                : outcome.UserStory;

            outcome = outcome with
            {
                Alternatives = resolvedAlternatives,
                UserStory = resolvedUserStory
            };
        }
        else if (isBareGoalDraft && outcome.Alternatives is not { Count: > 0 })
        {
            outcome = outcome with
            {
                Alternatives = GoalExpansionSynthesizer.BuildFallbackAlternatives(outcome.Title, outcome.Description, outcome.UserStory)
            };
        }

        if (!loggedLiveGenerationStep)
        {
            var keyStatusDetail = initialSecret.IsConfigured
                ? $"{initialSecret.SourceDetail} [{initialSecret.MaskedKey}]"
                : !string.IsNullOrWhiteSpace(apiKey)
                    ? $"API key [{GeminiSecretProvider.MaskKey(apiKey)}]"
                    : "offline";
            TerminalUx.WriteStepDone(
                colorWriter,
                "Generated",
                $"deterministic {promptType} draft ({runtimeInfo.EnabledAiModel} · {keyStatusDetail})",
                processSw.Elapsed);
        }

        if (provider == AiProviderType.ManualPrompt)
        {
            colorWriter.WriteMuted(outcome.GuidanceMessage);
        }

        LogEvents.ProviderGuidance(_logger, outcome.GuidanceMessage);
        LogEvents.OutcomeValidationTelemetry(_logger, DescriptionFormatter.FormatWordBudgetBadge(outcome.Description));

        IReadOnlyList<JiraBacklogTicket> tickets = isCombinedType
            ? [
                _formatter.Format(outcome, BacklogType.Type01, options.Verbosity),
                _formatter.Format(outcome, BacklogType.Type02, options.Verbosity)
            ]
            : [_formatter.Format(outcome, backlogType, options.Verbosity)];

        IReadOnlyList<string> markdownDocuments;
        if (options.DryRun || string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            markdownDocuments = tickets.Select(t => t.FormattedMarkdown).ToArray();
        }
        else
        {
            var jsonFileOut = options.JsonFileOut ?? false;
            var savedPaths = new List<string>(tickets.Count);
            foreach (var ticketToSave in tickets)
            {
                var savedSlugPath = SaveTicketArtifacts(ticketToSave, options.OutputDirectory, jsonFileOut, colorWriter);
                LogEvents.TicketArtifactsSaved(_logger, savedSlugPath, jsonFileOut);
                savedPaths.Add(savedSlugPath);
            }

            // Read generated Markdown back from disk so the CLI table previews exact saved Markdown
            markdownDocuments = savedPaths.Select(File.ReadAllText).ToArray();
        }

        var supplementalUserStory = ResolveSupplementalUserStory(outcome, backlogType, isCombinedType);
        var supplementalDescription = ResolveSupplementalDescription(outcome, backlogType, isCombinedType);
        var effectiveShowAlternatives = options.Verbosity switch
        {
            OutcomeVerbosity.Minimalistic => false,
            OutcomeVerbosity.Detailed => true,
            _ => options.ShowAlternatives
        };

        MarkdownOutcomeRenderer.WriteOutcomeTable(
            colorWriter,
            markdownDocuments,
            supplementalUserStory,
            supplementalDescription,
            includeAlternatives: effectiveShowAlternatives);

        if (options.DryRun)
        {
            output.WriteLine();
            colorWriter.WriteSuccess($"[Preview / Dry Run] Validated '{tickets[0].Title}' ({tickets[0].BacklogType} -> {tickets[0].JiraStage}). No files written.");
        }

        LastOutcome = outcome;
        LastGeneratedTickets = tickets;
        LastMarkdownDocuments = markdownDocuments;
        LastSupplementalUserStory = supplementalUserStory;
        LastSupplementalDescription = supplementalDescription;

        executionStopwatch.Stop();
        LogEvents.WorkflowCompleted(_logger, tickets.Count, tickets[0].Title, options.DryRun, executionStopwatch.ElapsedMilliseconds);
        TerminalUx.WriteStepDone(
            colorWriter,
            options.DryRun ? "Previewed" : "Completed",
            $"{tickets.Count} Jira item(s) ('{tickets[0].Title}')",
            executionStopwatch.Elapsed);
        MarkdownOutcomeRenderer.WriteQuickCopyHint(
            colorWriter,
            markdownDocuments,
            includeAlternatives: effectiveShowAlternatives);

        return tickets;
    }

    private static string BuildSingleLinePreview(string text, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return singleLine.Length <= maxLength
            ? singleLine
            : singleLine[..maxLength] + "...";
    }

    public static string? ResolveSupplementalUserStory(
        ProcessedOutcome outcome,
        BacklogType backlogType,
        bool isCombinedType)
    {
        _ = outcome;
        _ = backlogType;
        _ = isCombinedType;
        return null;
    }

    public static string? ResolveSupplementalDescription(
        ProcessedOutcome outcome,
        BacklogType backlogType,
        bool isCombinedType)
    {
        _ = outcome;
        _ = backlogType;
        _ = isCombinedType;
        return null;
    }

    private static string BuildWebSearchTopic(string? goalTitle, string? goalContext, string rawData)
    {
        var baseLine = !string.IsNullOrWhiteSpace(goalTitle)
            ? goalTitle.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? goalTitle
            : rawData.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? rawData;

        if (baseLine.StartsWith("Goal:", StringComparison.OrdinalIgnoreCase))
        {
            baseLine = baseLine["Goal:".Length..].Trim();
        }
        else if (baseLine.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
        {
            baseLine = baseLine["Title:".Length..].Trim();
        }

        if (!string.IsNullOrWhiteSpace(goalContext))
        {
            return $"{baseLine}\nFocus / Scope: {goalContext.Trim()}";
        }

        return baseLine;
    }

    private static string FormatWebGroundingFindings(GeminiWebGroundingResult grounding)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(grounding.Summary.Trim());
        if (grounding.Sources.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Verified Web Sources:");
            foreach (var source in grounding.Sources.Take(5))
            {
                sb.AppendLine($"- {source.Title}: {source.Uri}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private JiraBacklogTicket ExecuteDirectoryBatch(
        IReadOnlyList<string> files,
        WorkflowOptions baseOptions,
        TextReader input,
        TextWriter output)
    {
        var currentOptions = baseOptions;
        JiraBacklogTicket? lastTicket = null;
        var batchStopwatch = System.Diagnostics.Stopwatch.StartNew();

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

        batchStopwatch.Stop();
        var colorWriter = new ConsoleColorWriter(output, theme: ConsoleColorWriter.ResolveTheme(baseOptions.CliTheme));
        TerminalUx.WriteStepDone(colorWriter, "Processed", $"{files.Count} file(s)", batchStopwatch.Elapsed);

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
            var recommendations = string.Join(
                ", ",
                ModelCatalogService.GetRecommendations(AiProviderType.GoogleGemini)
                    .Where(r => r.IsRecommended)
                    .Select(r => r.ModelId));
            throw new InvalidOperationException(
                $"Gemini model '{selectedModel}' is not available for generateContent with this API key. " +
                $"Models may be retired, embedding/media-only, or restricted to another tier/region. " +
                $"Recommended: {recommendations}. Official docs: {ModelCatalogService.GeminiModelsDocsUrl} (or run --list-models).");
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

    private static string SaveTicketArtifacts(
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

        return slugMdPath;
    }
}
