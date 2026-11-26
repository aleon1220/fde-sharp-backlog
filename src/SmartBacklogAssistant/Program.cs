using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;
using SmartBacklogAssistant.Workflow;

namespace SmartBacklogAssistant;

public static class Program
{
    private static readonly HashSet<string> ValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--workspace", "-w",
        "--input", "-i",
        "--prompt", "-p",
        "--provider",
        "--api-key",
        "--type", "-t",
        "--output-dir",
        "--output", "-o",
        "--model", "-m",
        "--goal", "--title",
        "--details", "-d", "--context", "-c",
        "--theme",
        "--in",
        "--out",
        "--copy",
        "--verbosity",
        "--log-level",
        "--log-format",
        "--gateway-url", "--base-url", "--openai-base-url",
        "--wire-api", "--gateway-api",
        "--scope"
    };

    private static readonly HashSet<string> OptionalBoolValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--json",
        "--json-out",
        "--json-file-out",
        "--live"
    };

    public static int Main(string[] args) =>
        Execute(args, Console.In, Console.Out, Console.Error);

    public static int Execute(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error) =>
        Execute(args, input, output, error, clipboardService: null, environmentReader: null, openAiGatewayClient: null);

    internal static int Execute(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        IClipboardService? clipboardService,
        Func<string, string?>? environmentReader = null,
        OpenAiGatewayClient? openAiGatewayClient = null,
        GeminiApiClient? geminiApiClient = null,
        ICopilotSdkGateway? copilotGateway = null,
        Action<string, string?, EnvironmentVariableTarget>? environmentWriter = null,
        Func<string, EnvironmentVariableTarget, string?>? scopedEnvironmentReader = null,
        HttpClient? verificationHttpClient = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var environmentStore = new SessionEnvironmentStore(
            externalReader: environmentReader,
            externalScopedWriter: environmentWriter,
            externalScopedReader: scopedEnvironmentReader);
        Func<string, string?> effectiveEnvReader = environmentStore.Read;

        var secretRedactor = new SecretRedactor();
        secretRedactor.RegisterEnvironmentSecrets(effectiveEnvReader);
        StderrLoggerFactory? loggerFactory = null;
        ILogger? programLogger = null;

        try
        {
            var options = ParseArgs(args);
            secretRedactor.RegisterSecret(options.ApiKey);

            if (HasAnyFlag(args, "--help", "-h") || options.Subcommand == "help")
            {
                var helpTarget = options.Subcommand == "help"
                    ? ExtractSubcommandHelpTarget(args)
                    : options.Subcommand;
                PrintHelp(output, helpTarget);
                return 0;
            }

            var workspaceService = new WorkspacePathService(environmentReader: effectiveEnvReader);
            var settingsService = new CliSettingsService();
            var initialWorkspace = workspaceService.Resolve(options.WorkspaceRoot);
            var initialSettings = settingsService.Load(initialWorkspace);

            // Allow 'backlog config --log-level default|reset' and '--log-format default|reset' without rejecting as runtime override tokens.
            var runtimeLoggingOptions = options.Subcommand == "config"
                ? options with
                {
                    LogLevelInput = IsResetToken(options.LogLevelInput) ? null : options.LogLevelInput,
                    LogFormatInput = IsResetToken(options.LogFormatInput) ? null : options.LogFormatInput
                }
                : options;

            var resolvedLogging = CliLogLevelResolver.ResolveEffectiveLogging(
                runtimeLoggingOptions,
                initialSettings,
                effectiveEnvReader);

            loggerFactory = new StderrLoggerFactory(
                error,
                resolvedLogging.Level,
                resolvedLogging.Format,
                secretRedactor);
            programLogger = loggerFactory.CreateLogger("SmartBacklogAssistant.Program");

            LogEvents.CliInitialized(
                programLogger,
                options.Subcommand ?? (ShouldLaunchMenu(args, options) ? "menu" : "direct"),
                resolvedLogging.Level,
                resolvedLogging.LevelSource,
                CliLogLevelResolver.FormatLogFormatCode(resolvedLogging.Format),
                resolvedLogging.FormatSource,
                options.NonInteractive,
                options.DryRun);

            var runtimeEnvService = new RuntimeEnvironmentService();
            var secretProvider = new GeminiSecretProvider(
                environmentReader: effectiveEnvReader,
                environmentWriter: environmentStore.WriteProcess);
            var effectiveGeminiClient = geminiApiClient ?? new GeminiApiClient(logger: loggerFactory.CreateLogger<GeminiApiClient>());
            var effectiveOpenAiGateway = openAiGatewayClient ?? new OpenAiGatewayClient(logger: loggerFactory.CreateLogger<OpenAiGatewayClient>());
            var modelOrchestrator = new AiModelOrchestrator(loggerFactory.CreateLogger<AiModelOrchestrator>());
            var providerOrchestrator = new AiProviderOrchestrator(
                effectiveGeminiClient,
                copilotGateway: copilotGateway,
                environmentReader: effectiveEnvReader,
                loggerFactory: loggerFactory,
                openAiGatewayClient: effectiveOpenAiGateway);
            var apiKeyService = new ApiKeyManagementService(
                environmentStore: environmentStore,
                geminiApiClient: effectiveGeminiClient,
                openAiGatewayClient: effectiveOpenAiGateway,
                copilotGateway: providerOrchestrator.CopilotGateway,
                verificationHttpClient: verificationHttpClient,
                secretRedactor: secretRedactor,
                loggerFactory: loggerFactory);
            var diagnosticService = new WorkspaceDiagnosticService(
                workspaceService,
                runtimeEnvService,
                secretProvider,
                settingsService,
                modelOrchestrator,
                providerOrchestrator,
                effectiveEnvReader);

            if (options.ShowVersion)
            {
                LogEvents.DispatchingCommand(programLogger, "version", initialWorkspace.ProjectRoot);
                var info = runtimeEnvService.Inspect();
                output.WriteLine($"SmartBacklogAssistant v{info.BuildVersion} ({info.BuildConfiguration}) | {info.FrameworkDescription} | {info.OsPlatform} ({info.ProcessArchitecture})");
                return 0;
            }

            if (options.Subcommand == "config")
            {
                LogEvents.DispatchingCommand(programLogger, "config", initialWorkspace.ProjectRoot);
                return RunConfigCommand(
                    args,
                    options,
                    workspaceService,
                    settingsService,
                    diagnosticService,
                    modelOrchestrator,
                    output,
                    programLogger);
            }

            if (options.Subcommand == "keys")
            {
                LogEvents.DispatchingCommand(programLogger, "keys", initialWorkspace.ProjectRoot);
                return RunKeysCommand(
                    args,
                    options,
                    workspaceService,
                    settingsService,
                    diagnosticService,
                    secretProvider,
                    apiKeyService,
                    secretRedactor,
                    input,
                    output);
            }

            if (options.Subcommand == "preview")
            {
                LogEvents.DispatchingCommand(programLogger, "preview", initialWorkspace.ProjectRoot);
                return RunPreviewCommand(
                    args,
                    options,
                    workspaceService,
                    settingsService,
                    clipboardService ?? new OsClipboardService(),
                    input,
                    output,
                    error,
                    programLogger);
            }

            if (options.ShowDoctorStatus)
            {
                LogEvents.DispatchingCommand(programLogger, "status", initialWorkspace.ProjectRoot);
                return RunDoctorStatus(
                    options,
                    runtimeEnvService,
                    secretProvider,
                    workspaceService,
                    settingsService,
                    modelOrchestrator,
                    output,
                    providerOrchestrator,
                    effectiveEnvReader);
            }

            if (options.ListGeminiModels)
            {
                LogEvents.DispatchingCommand(programLogger, "models", initialWorkspace.ProjectRoot);
                return RunModelsCommand(
                    options,
                    workspaceService,
                    settingsService,
                    modelOrchestrator,
                    providerOrchestrator,
                    secretProvider,
                    effectiveGeminiClient,
                    apiKeyService,
                    diagnosticService,
                    environmentStore,
                    input,
                    output);
            }

            var collector = new RawDataCollector(loggerFactory.CreateLogger<RawDataCollector>());
            var promptService = new PromptTemplateService();
            var formatter = new JiraBacklogFormatter();

            var workflow = new AsIsV1Workflow(
                collector,
                promptService,
                modelOrchestrator,
                formatter,
                runtimeEnvService,
                secretProvider,
                effectiveGeminiClient,
                providerOrchestrator,
                loggerFactory,
                effectiveOpenAiGateway);

            if (ShouldLaunchMenu(args, options))
            {
                LogEvents.DispatchingCommand(programLogger, "menu", initialWorkspace.ProjectRoot);
                var menuRunner = new CliMenuRunner(
                    workflow,
                    workspaceService,
                    runtimeEnvService,
                    secretProvider,
                    modelOrchestrator,
                    settingsService,
                    diagnosticService,
                    clipboardService,
                    loggerFactory,
                    effectiveEnvReader,
                    apiKeyService,
                    secretRedactor);

                return menuRunner.Run(options, input, output);
            }

            // Direct / headless execution (uv-style one-shot subcommands or explicit flags)
            LogEvents.DispatchingCommand(programLogger, options.Subcommand ?? "direct", initialWorkspace.ProjectRoot);
            var resolvedOptions = ResolveDirectExecutionOptions(args, options, workspaceService, settingsService, collector);
            workflow.Execute(resolvedOptions, input, output);

            var copyMode = GetArgValue(args, "--copy");
            var selectInteractive = HasAnyFlag(args, "--select", "-s");
            if (selectInteractive && workflow.LastMarkdownDocuments.Count > 0)
            {
                RunInteractiveOutputSelection(
                    options,
                    resolvedOptions.CliTheme,
                    workflow.LastMarkdownDocuments,
                    workflow.LastSupplementalUserStory,
                    workflow.LastSupplementalDescription,
                    clipboardService ?? new OsClipboardService(),
                    input,
                    output,
                    error);
            }
            else if (!string.IsNullOrWhiteSpace(copyMode) && workflow.LastMarkdownDocuments.Count > 0)
            {
                var colorWriter = new ConsoleColorWriter(output, theme: ConsoleColorWriter.ResolveTheme(resolvedOptions.CliTheme));
                CopyOutcomeWithMode(
                    copyMode,
                    workflow.LastMarkdownDocuments,
                    workflow.LastSupplementalUserStory,
                    workflow.LastSupplementalDescription,
                    clipboardService ?? new OsClipboardService(),
                    colorWriter);
            }

            return 0;
        }
        catch (Exception ex)
        {
            if (programLogger is not null)
            {
                LogEvents.CommandExecutionFailed(programLogger, ex, ex.Message);
            }

            var theme = ConsoleColorWriter.ResolveTheme(GetArgValue(args, "--theme"));
            var errorWriter = new ConsoleColorWriter(error, theme: theme);
            TerminalUx.WriteActionableError(errorWriter, ex, secretRedactor);
            return 1;
        }
        finally
        {
            loggerFactory?.Dispose();
        }
    }

    public static WorkflowOptions ParseArgs(string[] args)
    {
        var (subcommand, subcommandArg) = ParseSubcommand(args);

        var inputPath = GetArgValue(args, "--input", "-i") ?? "samples/raw-notes.txt";
        var promptPath = GetArgValue(args, "--prompt", "-p") ?? "prompts/as-is-v1-prompt.md";
        var provider = GetArgValue(args, "--provider");
        var apiKey = GetArgValue(args, "--api-key");
        var backlogType = GetArgValue(args, "--type", "-t");
        var outputDir = GetArgValue(args, "--output-dir") ?? "output";
        var modelName = GetArgValue(args, "--model", "-m");
        var workspaceRoot = GetArgValue(args, "--workspace", "-w");
        var goalTitle = GetArgValue(args, "--goal")
            ?? GetArgValue(args, "--title")
            ?? (subcommand == "draft" ? subcommandArg : null);
        var goalContext = GetArgValue(args, "--details", "-d", "--context", "-c");
        var webSearch = HasAnyFlag(args, "--search", "--web");

        if (subcommand == "run" && !string.IsNullOrWhiteSpace(subcommandArg) && !HasAnyFlag(args, "--input", "-i"))
        {
            inputPath = subcommandArg;
        }

        var nonInteractive = HasFlag(args, "--non-interactive");
        var forceMenu = HasFlag(args, "--menu") || subcommand == "menu";
        var (jsonFileOut, outputFormatCombined) = ParseOutputAndJsonFlags(args);
        var combinedOutput = outputFormatCombined ||
                             HasFlag(args, "--combined") ||
                             string.Equals(backlogType?.Trim(), "both", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(backlogType?.Trim(), "all", StringComparison.OrdinalIgnoreCase);

        bool? geminiLive = HasFlag(args, "--offline")
            ? false
            : HasFlag(args, "--gemini-live")
                ? true
                : ParseOptionalBoolFlag(args, "--live");

        var listGeminiModels = HasFlag(args, "--list-models") || subcommand == "models";
        var dryRun = HasFlag(args, "--dry-run") || HasFlag(args, "--preview");
        var showDoctorStatus = HasFlag(args, "--doctor") ||
                               HasFlag(args, "--status") ||
                               subcommand == "doctor";
        var showVersion = HasFlag(args, "--version") ||
                          HasExactFlag(args, "-v") ||
                          subcommand == "version";
        var verbose = HasFlag(args, "--verbose") || HasExactFlag(args, "-V");
        var quiet = HasFlag(args, "--quiet") || HasExactFlag(args, "-q");
        var logLevelInput = TryGetRequiredFlagValue(args, out var parsedLogLevelToken, "--log-level")
            ? parsedLogLevelToken
            : null;
        var logFormatInput = TryGetRequiredFlagValue(args, out var parsedLogFormatToken, "--log-format")
            ? parsedLogFormatToken
            : null;
        var openAiBaseUrl = TryGetRequiredFlagValue(args, out var parsedGatewayUrl, "--gateway-url", "--base-url", "--openai-base-url")
            ? parsedGatewayUrl
            : null;
        var openAiWireApi = TryGetRequiredFlagValue(args, out var parsedWireApi, "--wire-api", "--gateway-api")
            ? parsedWireApi
            : null;
        var showAlternatives = HasAnyFlag(args, "--alternatives", "--alts");
        var verbosityArg = GetArgValue(args, "--verbosity");
        var verbosity = HasAnyFlag(args, "--minimal", "--minimalistic")
            ? OutcomeVerbosity.Minimalistic
            : HasFlag(args, "--detailed")
                ? OutcomeVerbosity.Detailed
                : DescriptionFormatter.ResolveVerbosity(verbosityArg);
        var cliTheme = GetArgValue(args, "--theme");

        return new WorkflowOptions(
            InputFilePath: inputPath,
            PromptFilePath: promptPath,
            ProviderInput: provider,
            ApiKey: apiKey,
            BacklogTypeInput: backlogType,
            OutputDirectory: outputDir,
            NonInteractive: nonInteractive,
            ModelName: modelName,
            WorkspaceRoot: workspaceRoot,
            GoalTitle: goalTitle,
            ForceMenu: forceMenu,
            JsonFileOut: jsonFileOut,
            GeminiLive: geminiLive,
            ListGeminiModels: listGeminiModels,
            CombinedOutput: combinedOutput,
            Subcommand: subcommand,
            DryRun: dryRun,
            ShowDoctorStatus: showDoctorStatus,
            ShowVersion: showVersion,
            CliTheme: cliTheme,
            GoalContext: goalContext,
            WebSearchGrounding: webSearch,
            Verbose: verbose,
            ShowAlternatives: showAlternatives,
            Verbosity: verbosity,
            LogLevelInput: logLevelInput,
            LogFormatInput: logFormatInput,
            Quiet: quiet,
            OpenAiBaseUrl: openAiBaseUrl,
            OpenAiWireApi: openAiWireApi);
    }

    public static bool ShouldLaunchMenu(string[] args, WorkflowOptions options)
    {
        if (options.ForceMenu)
        {
            return true;
        }

        if (options.NonInteractive ||
            options.ShowDoctorStatus ||
            options.ShowVersion ||
            options.ListGeminiModels ||
            options.Subcommand is "config" or "help" or "preview" or "keys")
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.Subcommand) && options.Subcommand is not ("menu" or "tui"))
        {
            return false;
        }

        var hasDirectWorkflowFlag =
            HasAnyFlag(args, "--input", "-i") ||
            HasAnyFlag(args, "--output-dir", "-o", "--output") ||
            HasAnyFlag(args, "--type", "-t") ||
            HasAnyFlag(args, "--goal", "--title") ||
            HasAnyFlag(args, "--details", "-d", "--context", "-c", "--search", "--web") ||
            HasAnyFlag(args, "--gemini-live", "--live", "--offline") ||
            HasAnyFlag(args, "--list-models", "--dry-run", "--preview", "--combined") ||
            HasAnyFlag(args, "--json", "--json-out", "--no-json", "--no-json-out");

        return !hasDirectWorkflowFlag;
    }

    public static int RunDoctorStatus(
        WorkflowOptions options,
        RuntimeEnvironmentService runtimeEnvService,
        GeminiSecretProvider secretProvider,
        WorkspacePathService workspaceService,
        CliSettingsService settingsService,
        AiModelOrchestrator orchestrator,
        TextWriter output,
        AiProviderOrchestrator? providerOrchestrator = null,
        Func<string, string?>? environmentReader = null)
    {
        var diagnosticService = new WorkspaceDiagnosticService(
            workspaceService,
            runtimeEnvService,
            secretProvider,
            settingsService,
            orchestrator,
            providerOrchestrator,
            environmentReader);

        return diagnosticService.RenderStatusTable(options, output);
    }

    public static int RunPreviewCommand(
        string[] args,
        WorkflowOptions options,
        WorkspacePathService workspaceService,
        CliSettingsService settingsService,
        IClipboardService clipboardService,
        TextWriter output) =>
        RunPreviewCommand(
            args,
            options,
            workspaceService,
            settingsService,
            clipboardService,
            Console.In,
            output,
            Console.Error,
            logger: null);

    public static int RunPreviewCommand(
        string[] args,
        WorkflowOptions options,
        WorkspacePathService workspaceService,
        CliSettingsService settingsService,
        IClipboardService clipboardService,
        TextReader input,
        TextWriter output,
        TextWriter error,
        ILogger? logger = null)
    {
        var baseWorkspace = workspaceService.Resolve(options.WorkspaceRoot);
        var settings = settingsService.Load(baseWorkspace);
        var activeWorkspace = workspaceService.ApplySettings(baseWorkspace, settings);
        var outputDir = HasFlag(args, "--output-dir") && !string.IsNullOrWhiteSpace(options.OutputDirectory)
            ? workspaceService.ResolveCustomOrRelativePath(options.OutputDirectory, activeWorkspace.IsWindows)
            : activeWorkspace.OutputDirectory;

        var selectInteractive = HasAnyFlag(args, "--select", "-s");
        var copyMode = GetArgValue(args, "--copy");
        if (selectInteractive && options.NonInteractive)
        {
            throw new InvalidOperationException("Interactive selection ('--select') requires an interactive terminal session.");
        }

        var theme = ConsoleColorWriter.ResolveTheme(options.CliTheme, settings.Theme);
        var (_, positionalTarget) = ParseSubcommand(args);
        var explicitTarget = GetArgValue(args, "--input", "-i") ?? positionalTarget;

        string resolvedPath;
        if (selectInteractive && string.IsNullOrWhiteSpace(explicitTarget))
        {
            var savedFiles = CliMenuRunner.GetSavedMarkdownFiles(outputDir);
            if (savedFiles.Count == 0)
            {
                throw new FileNotFoundException($"No saved Markdown outcome files (.md) found in '{outputDir}'.", outputDir);
            }

            if (savedFiles.Count == 1)
            {
                resolvedPath = savedFiles[0];
            }
            else
            {
                EnsureInteractiveInputAvailable(input);
                var promptColor = new ConsoleColorWriter(error, theme: theme);
                var chosenPath = InteractiveSelector.SelectFileInteractive(savedFiles, input, promptColor);
                if (chosenPath is null)
                {
                    promptColor.WriteMuted("  Selection cancelled.");
                    return 0;
                }

                resolvedPath = chosenPath;
            }
        }
        else
        {
            resolvedPath = CliMenuRunner.ResolveSavedMarkdownTarget(explicitTarget, outputDir);
        }

        if (logger is not null)
        {
            LogEvents.PreviewingMarkdown(logger, resolvedPath, selectInteractive, copyMode ?? "none");
        }

        var markdown = File.ReadAllText(resolvedPath);

        if (selectInteractive)
        {
            var promptColor = new ConsoleColorWriter(error, theme: theme);
            if (!string.IsNullOrWhiteSpace(copyMode))
            {
                var items = MarkdownOutcomeRenderer.ExtractSelectableItems([markdown]);
                var matches = InteractiveSelector.FilterOutputItems(items, copyMode.Trim());
                if (matches.Count == 0)
                {
                    var validTargets = string.Join(", ", items.Select(i => i.Id));
                    throw new ArgumentException($"Unsupported --copy target '{copyMode}'. Available targets: {validTargets}.");
                }

                MarkdownOutcomeRenderer.CopySelectableItemToClipboard(clipboardService, matches[0], promptColor);
                output.WriteLine(matches[0].Value);
                return 0;
            }

            return RunInteractiveOutputSelection(
                options,
                ConsoleColorWriter.GetThemeCode(theme),
                [markdown],
                null,
                null,
                clipboardService,
                input,
                output,
                error);
        }

        var color = new ConsoleColorWriter(output, theme: theme);
        color.WriteMuted($"  Reading Markdown: {resolvedPath}");
        MarkdownOutcomeRenderer.WriteOutcomeTable(
            color,
            [markdown],
            includeAlternatives: options.ShowAlternatives);

        if (!string.IsNullOrWhiteSpace(copyMode))
        {
            CopyOutcomeWithMode(copyMode, [markdown], null, null, clipboardService, color);
        }

        return 0;
    }

    private static int RunInteractiveOutputSelection(
        WorkflowOptions options,
        string? cliTheme,
        IReadOnlyList<string> markdownDocuments,
        string? supplementalUserStory,
        string? supplementalDescription,
        IClipboardService clipboardService,
        TextReader input,
        TextWriter output,
        TextWriter error)
    {
        if (options.NonInteractive)
        {
            throw new InvalidOperationException("Interactive selection ('--select') requires an interactive terminal session.");
        }

        EnsureInteractiveInputAvailable(input);

        var promptColor = new ConsoleColorWriter(error, theme: ConsoleColorWriter.ResolveTheme(cliTheme));
        var items = MarkdownOutcomeRenderer.ExtractSelectableItems(
            markdownDocuments,
            supplementalUserStory,
            supplementalDescription);

        var selected = InteractiveSelector.SelectOutputItemInteractive(items, input, promptColor);
        if (selected is null)
        {
            promptColor.WriteMuted("  Selection cancelled.");
            return 0;
        }

        MarkdownOutcomeRenderer.CopySelectableItemToClipboard(clipboardService, selected, promptColor);
        output.WriteLine(selected.Value);
        return 0;
    }

    private static void EnsureInteractiveInputAvailable(TextReader input)
    {
        if (ReferenceEquals(input, Console.In) && Console.IsInputRedirected && input.Peek() == -1)
        {
            throw new InvalidOperationException("Interactive selection ('--select') received EOF on stdin.");
        }

        if (!ReferenceEquals(input, Console.In) && input.Peek() == -1)
        {
            throw new InvalidOperationException("Interactive selection ('--select') received empty input.");
        }
    }

    private static void CopyOutcomeWithMode(
        string copyMode,
        IReadOnlyList<string> markdownDocuments,
        string? supplementalUserStory,
        string? supplementalDescription,
        IClipboardService clipboardService,
        ConsoleColorWriter color)
    {
        var items = MarkdownOutcomeRenderer.ExtractSelectableItems(
            markdownDocuments,
            supplementalUserStory,
            supplementalDescription);
        var matches = InteractiveSelector.FilterOutputItems(items, copyMode.Trim());
        if (matches.Count == 0)
        {
            var validTargets = string.Join(", ", items.Select(i => i.Id));
            throw new ArgumentException($"Unsupported --copy target '{copyMode}'. Available targets: {validTargets}.");
        }

        MarkdownOutcomeRenderer.CopySelectableItemToClipboard(clipboardService, matches[0], color);
    }

    public static int RunConfigCommand(
        string[] args,
        WorkflowOptions options,
        WorkspacePathService workspaceService,
        CliSettingsService settingsService,
        WorkspaceDiagnosticService diagnosticService,
        AiModelOrchestrator orchestrator,
        TextWriter output,
        ILogger? logger = null)
    {
        var sw = Stopwatch.StartNew();
        var baseWorkspace = workspaceService.Resolve(options.WorkspaceRoot);
        var currentSettings = settingsService.Load(baseWorkspace);
        var color = new ConsoleColorWriter(
            output,
            theme: ConsoleColorWriter.ResolveTheme(options.CliTheme, currentSettings.Theme));

        var hasResetFlag = HasFlag(args, "--reset");
        var updated = currentSettings;
        var changed = false;

        if (hasResetFlag)
        {
            updated = new CliSettings();
            changed = true;
        }
        else
        {
            if (TryParseStrictConfigBoolFlag(args, "--json", "--json-out", "--json-file-out", "--no-json", "--no-json-out", out var jsonSetting))
            {
                updated = updated with { JsonFileOut = jsonSetting };
                changed = true;
            }

            if (TryGetRequiredFlagValue(args, out var inValue, "--in"))
            {
                updated = updated with
                {
                    CustomInputDirectory = inValue.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                                           inValue.Equals("reset", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : workspaceService.ResolveCustomOrRelativePath(inValue, baseWorkspace.IsWindows)
                };
                changed = true;
            }

            if (TryGetRequiredFlagValue(args, out var outValue, "--out"))
            {
                updated = updated with
                {
                    CustomOutputDirectory = outValue.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                                            outValue.Equals("reset", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : workspaceService.ResolveCustomOrRelativePath(outValue, baseWorkspace.IsWindows)
                };
                changed = true;
            }

            if (TryGetRequiredFlagValue(args, out var themeValue, "--theme"))
            {
                if (!IsValidThemeToken(themeValue))
                {
                    throw new ArgumentException($"Invalid CLI theme '{themeValue}'. Valid values: agy, uv, kubectl.");
                }

                updated = updated with { Theme = ConsoleColorWriter.ResolveTheme(themeValue, updated.Theme) };
                color.Theme = updated.Theme;
                changed = true;
            }

            if (TryParseStrictConfigBoolFlag(args, "--live", "--gemini-live", null, "--offline", null, out var liveSetting))
            {
                updated = updated with { GeminiLive = liveSetting };
                changed = true;
            }

            if (TryGetRequiredFlagValue(args, out var providerValue, "--provider"))
            {
                var nextProvider = orchestrator.ResolveProvider(providerValue);
                var previousProvider = updated.PreferredProvider ?? AiProviderType.GoogleGemini;
                var modelProvidedOnCommandLine = TryGetRequiredFlagValue(args, out _, "--model", "-m");
                updated = updated with
                {
                    PreferredProvider = nextProvider,
                    ModelName = nextProvider != previousProvider && !modelProvidedOnCommandLine
                        ? null
                        : updated.ModelName
                };
                changed = true;
            }

            if (TryGetRequiredFlagValue(args, out var modelValue, "--model", "-m"))
            {
                updated = updated with
                {
                    ModelName = modelValue.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                                modelValue.Equals("default", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : modelValue.Trim()
                };
                changed = true;
            }

            if (TryGetRequiredFlagValue(args, out var gatewayUrlValue, "--gateway-url", "--base-url", "--openai-base-url"))
            {
                if (IsResetToken(gatewayUrlValue))
                {
                    updated = updated with { OpenAiBaseUrl = null };
                }
                else
                {
                    if (!OpenAiGatewayClient.TryNormalizeBaseUrl(gatewayUrlValue, out var normalizedGatewayUrl, out var urlError))
                    {
                        throw new ArgumentException(urlError ?? $"Invalid OpenAI Gateway URL '{gatewayUrlValue}'.");
                    }

                    updated = updated with { OpenAiBaseUrl = normalizedGatewayUrl };
                }

                changed = true;
            }

            if (TryGetRequiredFlagValue(args, out var wireApiValue, "--wire-api", "--gateway-api"))
            {
                updated = updated with
                {
                    OpenAiWireApi = IsResetToken(wireApiValue)
                        ? null
                        : OpenAiGatewayClient.FormatWireApiCode(OpenAiGatewayClient.ParseWireApiOrThrow(wireApiValue, "--wire-api"))
                };
                changed = true;
            }

            if (TryGetRequiredFlagValue(args, out var verbosityValue, "--verbosity"))
            {
                var parsedVerbosity = DescriptionFormatter.ResolveVerbosity(verbosityValue, (OutcomeVerbosity)(-1));
                if (!Enum.IsDefined(parsedVerbosity))
                {
                    throw new ArgumentException($"Invalid verbosity '{verbosityValue}'. Valid values: minimalistic (min), balanced (bal), detailed (det).");
                }

                updated = updated with { DefaultVerbosity = parsedVerbosity };
                changed = true;
            }

            if (TryGetRequiredFlagValue(args, out var logLevelValue, "--log-level"))
            {
                updated = updated with
                {
                    LogLevel = IsResetToken(logLevelValue)
                        ? null
                        : CliLogLevelResolver.ParseLogLevelOrThrow(logLevelValue, "--log-level")
                };
                changed = true;
            }

            if (TryGetRequiredFlagValue(args, out var logFormatValue, "--log-format"))
            {
                updated = updated with
                {
                    LogFormat = IsResetToken(logFormatValue)
                        ? null
                        : CliLogLevelResolver.ParseLogFormatOrThrow(logFormatValue, "--log-format")
                };
                changed = true;
            }
        }

        if (!changed)
        {
            var currentActiveWorkspace = workspaceService.ApplySettings(baseWorkspace, currentSettings);
            diagnosticService.RenderConfigSummary(baseWorkspace, currentActiveWorkspace, currentSettings, color);
            return 0;
        }

        var effectiveProvider = updated.PreferredProvider ?? AiProviderType.GoogleGemini;
        if (!string.IsNullOrWhiteSpace(updated.ModelName))
        {
            var modelCheck = ModelCatalogService.ValidateModelSelection(effectiveProvider, updated.ModelName);
            if (!modelCheck.IsSupported)
            {
                var recList = string.Join(", ", modelCheck.Recommendations.Where(r => r.IsRecommended).Select(r => r.ModelId));
                throw new ArgumentException(
                    $"{modelCheck.Reason} Official docs: {modelCheck.OfficialDocsUrl}" +
                    (string.IsNullOrWhiteSpace(recList) ? string.Empty : $" (Recommended: {recList})"));
            }

            if (modelCheck.IsKnownWarning)
            {
                if (logger is not null)
                {
                    LogEvents.ModelSelectionWarning(logger, effectiveProvider.ToString(), updated.ModelName, modelCheck.Reason ?? string.Empty);
                }

                color.WriteWarning($"  [!] {modelCheck.Reason}");
                color.WriteMuted($"      Docs: {modelCheck.OfficialDocsUrl}");
            }
        }

        var savedPath = settingsService.Save(baseWorkspace, updated);
        var activeWorkspace = workspaceService.ApplySettings(baseWorkspace, updated);
        Directory.CreateDirectory(activeWorkspace.InputDirectory);
        Directory.CreateDirectory(activeWorkspace.OutputDirectory);

        sw.Stop();
        if (logger is not null)
        {
            LogEvents.ConfigSaved(
                logger,
                savedPath,
                updated.LogLevel?.ToString() ?? "default",
                updated.LogFormat?.ToString() ?? "default");
        }

        TerminalUx.WriteStepDone(color, "Saved", $"workspace settings to '{savedPath}'", sw.Elapsed);
        diagnosticService.RenderConfigSummary(baseWorkspace, activeWorkspace, updated, color);
        return 0;
    }

    private static int RunKeysCommand(
        string[] args,
        WorkflowOptions options,
        WorkspacePathService workspaceService,
        CliSettingsService settingsService,
        WorkspaceDiagnosticService diagnosticService,
        GeminiSecretProvider secretProvider,
        ApiKeyManagementService apiKeyService,
        SecretRedactor secretRedactor,
        TextReader input,
        TextWriter output)
    {
        var workspace = workspaceService.Resolve(options.WorkspaceRoot);
        var settings = settingsService.Load(workspace);
        var color = new ConsoleColorWriter(output, theme: ConsoleColorWriter.ResolveTheme(options.CliTheme, settings.Theme));
        var positionals = ExtractSubcommandPositionals(args);

        // Register any positional secret argument immediately so stderr logs or errors never expose plaintext.
        if (positionals.Count >= 3 && positionals[0].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            secretRedactor.RegisterSecret(positionals[2]);
        }
        else if (positionals.Count >= 3 &&
                 (positionals[0].Equals("validate", StringComparison.OrdinalIgnoreCase) ||
                  positionals[0].Equals("verify", StringComparison.OrdinalIgnoreCase) ||
                  positionals[0].Equals("check", StringComparison.OrdinalIgnoreCase) ||
                  positionals[0].Equals("test", StringComparison.OrdinalIgnoreCase)))
        {
            secretRedactor.RegisterSecret(positionals[2]);
        }

        if (positionals.Count == 0 ||
            positionals[0].Equals("status", StringComparison.OrdinalIgnoreCase) ||
            positionals[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return RenderKeysStatusTable(
                apiKeyService,
                options,
                settings,
                color,
                output,
                singleProvider: null);
        }

        var action = positionals[0].ToLowerInvariant();
        if (ApiKeyManagementService.TryResolveKeyProvider(action, out var singleStatusProvider) && positionals.Count == 1)
        {
            return RenderKeysStatusTable(
                apiKeyService,
                options,
                settings,
                color,
                output,
                singleProvider: singleStatusProvider);
        }

        switch (action)
        {
            case "set" or "add" or "register":
                return RunKeysSetCommand(
                    args,
                    positionals,
                    options,
                    settings,
                    secretProvider,
                    apiKeyService,
                    secretRedactor,
                    color,
                    input,
                    output);

            case "validate" or "check":
                return RunKeysValidateCommand(
                    args,
                    positionals,
                    options,
                    settings,
                    diagnosticService,
                    apiKeyService,
                    color,
                    defaultLiveVerify: false);

            case "verify" or "test":
                return RunKeysValidateCommand(
                    args,
                    positionals,
                    options,
                    settings,
                    diagnosticService,
                    apiKeyService,
                    color,
                    defaultLiveVerify: true);

            case "clear" or "remove" or "unset" or "rm" or "delete":
                return RunKeysClearCommand(
                    args,
                    positionals,
                    apiKeyService,
                    color);

            default:
                throw new ApiKeyValidationException(
                    $"Unknown 'keys' subcommand '{positionals[0]}'. Expected status, set, validate, verify, or clear.",
                    "Run 'backlog keys --help' for usage and examples.");
        }
    }

    private static int RenderKeysStatusTable(
        ApiKeyManagementService apiKeyService,
        WorkflowOptions options,
        CliSettings settings,
        ConsoleColorWriter color,
        TextWriter output,
        AiProviderType? singleProvider)
    {
        var statuses = singleProvider is { } target
            ? [apiKeyService.GetProviderStatus(target, options.OpenAiBaseUrl, settings.OpenAiBaseUrl, options.OpenAiWireApi, settings.OpenAiWireApi)]
            : apiKeyService.GetAllProviderStatuses(options.OpenAiBaseUrl, settings.OpenAiBaseUrl, options.OpenAiWireApi, settings.OpenAiWireApi);

        if (options.JsonFileOut == true)
        {
            var payload = statuses.Select(static s => new
            {
                provider = s.Metadata.Code,
                displayName = s.Metadata.DisplayName,
                primaryEnvVar = s.Metadata.PrimaryEnvVar,
                activeEnvVar = s.ActiveEnvVar,
                isConfigured = s.IsConfigured,
                maskedKey = s.MaskedKey,
                scope = s.ScopeSummary,
                detail = s.Detail,
                companionWarning = s.HasCompanionConfigWarning ? s.CompanionConfigNote : null
            });
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var rows = new List<(string Section, string Setting, string Value, ConsoleColor ValueColor)>(statuses.Count);
        foreach (var status in statuses)
        {
            var valueText = status.IsConfigured
                ? $"{status.MaskedKey} ({status.ScopeSummary}) — {status.Detail}"
                : status.Detail;
            var valueColor = status.IsConfigured
                ? (status.HasCompanionConfigWarning ? ConsoleColor.Yellow : ConsoleColor.Green)
                : ConsoleColor.Yellow;
            rows.Add((status.Metadata.Code, status.Metadata.PrimaryEnvVar, valueText, valueColor));
        }

        TerminalUx.WriteDiagnosticTable(
            color,
            "Smart Backlog Assistant — AI Provider API Keys",
            rows);
        color.WriteMuted("  Secrets stay in memory/OS environment only and are NEVER written to settings.json.");
        return 0;
    }

    private static int RunKeysSetCommand(
        string[] args,
        IReadOnlyList<string> positionals,
        WorkflowOptions options,
        CliSettings settings,
        GeminiSecretProvider secretProvider,
        ApiKeyManagementService apiKeyService,
        SecretRedactor secretRedactor,
        ConsoleColorWriter color,
        TextReader input,
        TextWriter output)
    {
        if (positionals.Count < 2)
        {
            throw new ApiKeyValidationException(
                "Missing AI provider for 'backlog keys set'. Usage: backlog keys set <provider> [--api-key <key>] [--scope session|user] [--verify]",
                "Supported providers: gemini, openai, azure, claude, copilot-sdk.");
        }

        if (!ApiKeyManagementService.TryResolveKeyProvider(positionals[1], out var provider))
        {
            throw new ApiKeyValidationException(
                $"Unsupported provider '{positionals[1]}' for API key management.",
                "Supported providers: gemini, openai, azure, claude, copilot-sdk.");
        }

        var scope = ParseKeyScopeFlag(args, allowAll: false);
        var metadata = ApiKeyManagementService.GetMetadata(provider);

        if (provider == AiProviderType.AzureOpenAi && !string.IsNullOrWhiteSpace(options.OpenAiBaseUrl))
        {
            var endpointResult = apiKeyService.SetAzureEndpoint(options.OpenAiBaseUrl, scope);
            if (!endpointResult.Succeeded)
            {
                throw new ApiKeyValidationException(endpointResult.Message, endpointResult.ScopeGuidanceNote);
            }

            color.WriteMuted($"  ✓ {endpointResult.Message}");
        }

        var useStdin = HasFlag(args, "--stdin");
        var positionalKey = positionals.Count >= 3 ? positionals[2] : null;
        string? rawKey = !string.IsNullOrWhiteSpace(options.ApiKey)
            ? options.ApiKey
            : !string.IsNullOrWhiteSpace(positionalKey)
                ? positionalKey
                : null;

        if (rawKey is null)
        {
            if (useStdin)
            {
                rawKey = input.ReadLine();
                if (string.IsNullOrWhiteSpace(rawKey))
                {
                    throw new ApiKeyValidationException(
                        $"No API key received on stdin for '{metadata.Code}'.",
                        $"Pipe a non-empty {metadata.PrimaryEnvVar} value or pass '--api-key <key>'.");
                }
            }
            else if (options.NonInteractive)
            {
                throw new ApiKeyValidationException(
                    $"Missing API key for '{metadata.Code}' in --non-interactive mode.",
                    $"Pass '--api-key <key>' or '--stdin' (obtain key from {metadata.KeyPortalUrl}).");
            }
            else
            {
                color.WriteLine();
                color.WriteHeader($"=== Configure {metadata.DisplayName} API Key ===");
                color.WriteMuted($"  Variable : {metadata.PrimaryEnvVar} | Portal: {metadata.KeyPortalUrl}");
                color.WriteMuted($"  Format   : {metadata.ExpectedFormatHint}");
                color.WriteColored($"Enter {metadata.DisplayName} API key (masked with *): ", ConsoleColor.Yellow);
                rawKey = secretProvider.ReadMaskedInput(input, output);
                if (string.IsNullOrWhiteSpace(rawKey))
                {
                    throw new ApiKeyValidationException(
                        $"API key entry cancelled or empty for '{metadata.Code}'.",
                        $"Obtain a key from {metadata.KeyPortalUrl} and rerun 'backlog keys set {metadata.Code}'.");
                }
            }
        }

        secretRedactor.RegisterSecret(rawKey);
        var verifyLive = (HasFlag(args, "--verify") || options.GeminiLive == true) && options.GeminiLive != false;

        var sw = Stopwatch.StartNew();
        var result = verifyLive
            ? TerminalUx.RunWithSpinner(
                $"Validating & verifying {metadata.DisplayName} API key...",
                () => apiKeyService.SetKey(provider, rawKey, scope, verifyLive: true, options.OpenAiBaseUrl, settings.OpenAiBaseUrl),
                color)
            : apiKeyService.SetKey(provider, rawKey, scope, verifyLive: false, options.OpenAiBaseUrl, settings.OpenAiBaseUrl);
        sw.Stop();

        if (!result.Succeeded)
        {
            throw new ApiKeyValidationException(result.Message, result.ScopeGuidanceNote);
        }

        if (result.LiveVerification is { Succeeded: true } live)
        {
            color.WriteSuccess($"  ✓ {live.Summary}");
        }

        TerminalUx.WriteStepDone(color, "Saved", result.Message, sw.Elapsed);
        if (!string.IsNullOrWhiteSpace(result.ScopeGuidanceNote))
        {
            color.WriteMuted($"  [i] {result.ScopeGuidanceNote}");
        }

        return 0;
    }

    private static int RunKeysValidateCommand(
        string[] args,
        IReadOnlyList<string> positionals,
        WorkflowOptions options,
        CliSettings settings,
        WorkspaceDiagnosticService diagnosticService,
        ApiKeyManagementService apiKeyService,
        ConsoleColorWriter color,
        bool defaultLiveVerify)
    {
        var runLive = (defaultLiveVerify || HasFlag(args, "--verify") || options.GeminiLive == true) &&
                      options.GeminiLive != false;
        var targetToken = positionals.Count >= 2 ? positionals[1] : null;
        var positionalKey = positionals.Count >= 3 ? positionals[2] : null;
        var candidateExplicitKey = !string.IsNullOrWhiteSpace(options.ApiKey)
            ? options.ApiKey
            : positionalKey;

        if (!string.IsNullOrWhiteSpace(targetToken) &&
            !targetToken.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (!ApiKeyManagementService.TryResolveKeyProvider(targetToken, out var singleProvider))
            {
                throw new ApiKeyValidationException(
                    $"Unsupported provider '{targetToken}' for API key validation.",
                    "Supported providers: gemini, openai, azure, claude, copilot-sdk, or 'all'.");
            }

            return ValidateSingleProviderKeyCli(
                singleProvider,
                candidateExplicitKey,
                runLive,
                options,
                settings,
                apiKeyService,
                color);
        }

        if (!string.IsNullOrWhiteSpace(candidateExplicitKey) && string.IsNullOrWhiteSpace(targetToken))
        {
            var effectiveProvider = diagnosticService.ResolveEffectiveProvider(options, settings);
            if (effectiveProvider == AiProviderType.ManualPrompt)
            {
                effectiveProvider = AiProviderType.GoogleGemini;
            }

            return ValidateSingleProviderKeyCli(
                effectiveProvider,
                candidateExplicitKey,
                runLive,
                options,
                settings,
                apiKeyService,
                color);
        }

        // Validate all configured providers
        var configuredCount = 0;
        var failureCount = 0;
        color.WriteHeader(runLive
            ? "=== Validating & Live-Verifying Configured AI Provider Keys ==="
            : "=== Validating Configured AI Provider Key Formats ===");

        foreach (var provider in ApiKeyManagementService.SupportedProviders)
        {
            var status = apiKeyService.GetProviderStatus(provider, options.OpenAiBaseUrl, settings.OpenAiBaseUrl);
            if (!status.IsConfigured)
            {
                color.WriteMuted($"  [ ] {status.Metadata.Code,-12} not configured ({status.Metadata.PrimaryEnvVar})");
                continue;
            }

            configuredCount++;
            var formatResult = apiKeyService.ValidateKeyFormat(
                provider,
                apiKeyService.EnvironmentStore.Read(status.ActiveEnvVar!),
                options.OpenAiBaseUrl,
                settings.OpenAiBaseUrl);

            if (!formatResult.IsValid)
            {
                failureCount++;
                color.WriteError($"  [!] {status.Metadata.Code,-12} {formatResult.ErrorMessage}");
                if (!string.IsNullOrWhiteSpace(formatResult.ActionableHint))
                {
                    color.WriteMuted($"      hint: {formatResult.ActionableHint}");
                }

                continue;
            }

            if (!runLive)
            {
                color.WriteSuccess($"  [✓] {status.Metadata.Code,-12} format valid [{formatResult.MaskedKey}] ({status.ScopeSummary})");
                continue;
            }

            var liveResult = TerminalUx.RunWithSpinner(
                $"Verifying {status.Metadata.DisplayName} [{formatResult.MaskedKey}]...",
                () => apiKeyService.VerifyKeyLive(provider, null, options.OpenAiBaseUrl, settings.OpenAiBaseUrl),
                color);
            if (liveResult.Succeeded)
            {
                color.WriteSuccess($"  [✓] {status.Metadata.Code,-12} {liveResult.Summary}");
            }
            else
            {
                failureCount++;
                color.WriteError($"  [!] {status.Metadata.Code,-12} {liveResult.ErrorMessage}");
                if (!string.IsNullOrWhiteSpace(liveResult.ActionableHint))
                {
                    color.WriteMuted($"      hint: {liveResult.ActionableHint}");
                }
            }
        }

        if (configuredCount == 0)
        {
            throw new ApiKeyValidationException(
                "No AI provider API keys are currently configured.",
                "Run 'backlog keys set <provider>' or open Workspace & Settings -> [K] Manage AI Provider API Keys.");
        }

        if (failureCount > 0)
        {
            throw new ApiKeyValidationException(
                $"{failureCount} configured API key(s) failed validation.",
                "Update invalid keys via 'backlog keys set <provider>' or clear them with 'backlog keys clear <provider>'.");
        }

        return 0;
    }

    private static int ValidateSingleProviderKeyCli(
        AiProviderType provider,
        string? explicitKey,
        bool runLive,
        WorkflowOptions options,
        CliSettings settings,
        ApiKeyManagementService apiKeyService,
        ConsoleColorWriter color)
    {
        var metadata = ApiKeyManagementService.GetMetadata(provider);
        string? keyToCheck = explicitKey;
        if (string.IsNullOrWhiteSpace(keyToCheck))
        {
            var status = apiKeyService.GetProviderStatus(provider, options.OpenAiBaseUrl, settings.OpenAiBaseUrl);
            if (!status.IsConfigured)
            {
                throw new ApiKeyValidationException(
                    $"No API key is currently configured for '{metadata.Code}' ({metadata.PrimaryEnvVar}).",
                    $"Run 'backlog keys set {metadata.Code}' or pass '--api-key <key>' to validate a candidate key.");
            }

            keyToCheck = apiKeyService.EnvironmentStore.Read(status.ActiveEnvVar!);
        }

        var formatResult = apiKeyService.ValidateKeyFormat(
            provider,
            keyToCheck,
            options.OpenAiBaseUrl,
            settings.OpenAiBaseUrl);
        if (!formatResult.IsValid)
        {
            throw new ApiKeyValidationException(
                formatResult.ErrorMessage ?? "API key format validation failed.",
                formatResult.ActionableHint);
        }

        if (!runLive)
        {
            color.WriteSuccess($"  [✓] {metadata.DisplayName} key [{formatResult.MaskedKey}] format is valid.");
            color.WriteMuted($"      Pass '--verify' or run 'backlog keys verify {metadata.Code}' to test live authentication.");
            return 0;
        }

        var sw = Stopwatch.StartNew();
        var liveResult = TerminalUx.RunWithSpinner(
            $"Verifying {metadata.DisplayName} [{formatResult.MaskedKey}]...",
            () => apiKeyService.VerifyKeyLive(provider, keyToCheck, options.OpenAiBaseUrl, settings.OpenAiBaseUrl),
            color);
        sw.Stop();

        if (!liveResult.Succeeded)
        {
            throw new ApiKeyValidationException(
                liveResult.ErrorMessage ?? liveResult.Summary,
                liveResult.ActionableHint);
        }

        TerminalUx.WriteStepDone(color, "Verified", liveResult.Summary, sw.Elapsed);
        return 0;
    }

    private static int RunKeysClearCommand(
        string[] args,
        IReadOnlyList<string> positionals,
        ApiKeyManagementService apiKeyService,
        ConsoleColorWriter color)
    {
        if (positionals.Count < 2)
        {
            throw new ApiKeyValidationException(
                "Missing provider for 'backlog keys clear'. Usage: backlog keys clear <provider|all> [--scope session|user|all]",
                "Supported providers: gemini, openai, azure, claude, copilot-sdk, or 'all'.");
        }

        var scope = ParseKeyScopeFlag(args, allowAll: true);
        var targetToken = positionals[1];

        if (targetToken.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var provider in ApiKeyManagementService.SupportedProviders)
            {
                var clearResult = apiKeyService.ClearKey(provider, scope);
                color.WriteSuccess($"  ✓ {clearResult.Message}");
                if (!string.IsNullOrWhiteSpace(clearResult.ScopeGuidanceNote))
                {
                    color.WriteMuted($"    [i] {clearResult.ScopeGuidanceNote}");
                }
            }

            return 0;
        }

        if (!ApiKeyManagementService.TryResolveKeyProvider(targetToken, out var singleProvider))
        {
            throw new ApiKeyValidationException(
                $"Unsupported provider '{targetToken}' for 'backlog keys clear'.",
                "Supported providers: gemini, openai, azure, claude, copilot-sdk, or 'all'.");
        }

        var result = apiKeyService.ClearKey(singleProvider, scope);
        color.WriteSuccess($"  ✓ {result.Message}");
        if (!string.IsNullOrWhiteSpace(result.ScopeGuidanceNote))
        {
            color.WriteMuted($"    [i] {result.ScopeGuidanceNote}");
        }

        return 0;
    }

    private static ApiKeyStorageScope ParseKeyScopeFlag(string[] args, bool allowAll)
    {
        if (!TryGetRequiredFlagValue(args, out var rawScope, "--scope"))
        {
            return ApiKeyStorageScope.Session;
        }

        return rawScope.Trim().ToLowerInvariant() switch
        {
            "session" or "process" or "runtime" => ApiKeyStorageScope.Session,
            "user" or "os" or "persist" => ApiKeyStorageScope.User,
            "all" when allowAll => ApiKeyStorageScope.All,
            _ => throw new ApiKeyValidationException(
                $"Invalid --scope '{rawScope}'. Valid values: {(allowAll ? "session, user, all" : "session, user")}.",
                "Use '--scope session' for active runtime only or '--scope user' for OS User persistence.")
        };
    }

    private static List<string> ExtractSubcommandPositionals(string[] args)
    {
        var list = new List<string>();
        var skippedSubcommand = false;

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (ValueFlags.Contains(token))
            {
                i++;
                continue;
            }

            if (OptionalBoolValueFlags.Contains(token))
            {
                if (i + 1 < args.Length && IsBooleanToken(args[i + 1]))
                {
                    i++;
                }

                continue;
            }

            if (token.StartsWith('-'))
            {
                continue;
            }

            if (!skippedSubcommand)
            {
                skippedSubcommand = true;
                continue;
            }

            list.Add(token);
        }

        return list;
    }

    private static int RunModelsCommand(
        WorkflowOptions options,
        WorkspacePathService workspaceService,
        CliSettingsService settingsService,
        AiModelOrchestrator modelOrchestrator,
        AiProviderOrchestrator providerOrchestrator,
        GeminiSecretProvider secretProvider,
        GeminiApiClient geminiApiClient,
        ApiKeyManagementService apiKeyService,
        WorkspaceDiagnosticService diagnosticService,
        SessionEnvironmentStore environmentStore,
        TextReader input,
        TextWriter output)
    {
        var workspace = workspaceService.Resolve(options.WorkspaceRoot);
        var settings = settingsService.Load(workspace);
        var color = new ConsoleColorWriter(output, theme: ConsoleColorWriter.ResolveTheme(options.CliTheme, settings.Theme));
        var activeProvider = diagnosticService.ResolveEffectiveProvider(options, settings);

        if (activeProvider == AiProviderType.GitHubCopilot)
        {
            var sw = Stopwatch.StartNew();
            var copilotModels = TerminalUx.RunWithSpinner(
                "Discovering GitHub Copilot SDK models...",
                () => providerOrchestrator.CopilotGateway.ListModelsAsync(options.ApiKey).GetAwaiter().GetResult(),
                color);
            sw.Stop();
            TerminalUx.WriteStepDone(color, "Listed", $"{copilotModels.Count} GitHub Copilot SDK model(s)", sw.Elapsed);
            foreach (var model in copilotModels)
            {
                output.WriteLine($"{model.Id}\t{model.Name}");
            }

            return 0;
        }

        if (activeProvider == AiProviderType.OpenAiGateway)
        {
            var openAiConfig = providerOrchestrator.ResolveOpenAiGatewayConfig(
                explicitApiKey: options.ApiKey,
                explicitBaseUrl: options.OpenAiBaseUrl,
                persistedBaseUrl: settings.OpenAiBaseUrl,
                explicitWireApi: options.OpenAiWireApi,
                persistedWireApi: settings.OpenAiWireApi);

            if (options.GeminiLive == false || (!openAiConfig.HasApiKey && options.NonInteractive))
            {
                var recs = ModelCatalogService.GetRecommendations(AiProviderType.OpenAiGateway);
                color.WriteHeader($"Recommended OpenAI Gateway Models ({openAiConfig.BaseUrl}):");
                foreach (var rec in recs)
                {
                    output.WriteLine($"{rec.ModelId}\t{rec.DisplayName}\t{rec.Reason}");
                }

                return 0;
            }

            var oaSw = Stopwatch.StartNew();
            var openAiModels = TerminalUx.RunWithSpinner(
                $"Discovering OpenAI Gateway models ({openAiConfig.BaseUrl})...",
                () => providerOrchestrator.OpenAiGateway.ListModels(openAiConfig),
                color);
            oaSw.Stop();
            TerminalUx.WriteStepDone(color, "Listed", $"{openAiModels.Count} OpenAI Gateway model(s)", oaSw.Elapsed);
            foreach (var model in openAiModels)
            {
                output.WriteLine(string.IsNullOrWhiteSpace(model.OwnedBy)
                    ? model.ModelId
                    : $"{model.ModelId}\t{model.OwnedBy}");
            }

            return 0;
        }

        if (activeProvider == AiProviderType.AzureOpenAi)
        {
            var azureConfig = providerOrchestrator.ResolveOpenAiGatewayConfig(
                explicitApiKey: options.ApiKey,
                explicitBaseUrl: options.OpenAiBaseUrl,
                persistedBaseUrl: settings.OpenAiBaseUrl,
                explicitWireApi: options.OpenAiWireApi,
                persistedWireApi: settings.OpenAiWireApi,
                preferAzureEnvironment: true);

            if (options.GeminiLive != false && azureConfig.IsConfiguredForLive && azureConfig.IsCustomBaseUrl)
            {
                var azSw = Stopwatch.StartNew();
                var azureModels = TerminalUx.RunWithSpinner(
                    $"Discovering Azure OpenAI models ({azureConfig.BaseUrl})...",
                    () => providerOrchestrator.OpenAiGateway.ListModels(azureConfig),
                    color);
                azSw.Stop();
                TerminalUx.WriteStepDone(color, "Listed", $"{azureModels.Count} Azure OpenAI model(s)", azSw.Elapsed);
                foreach (var model in azureModels)
                {
                    output.WriteLine(string.IsNullOrWhiteSpace(model.OwnedBy)
                        ? model.ModelId
                        : $"{model.ModelId}\t{model.OwnedBy}");
                }

                return 0;
            }

            var azureRecs = ModelCatalogService.GetRecommendations(AiProviderType.AzureOpenAi);
            color.WriteHeader("Recommended Azure OpenAI Models:");
            foreach (var rec in azureRecs)
            {
                output.WriteLine($"{rec.ModelId}\t{rec.DisplayName}\t{rec.Reason}");
            }

            return 0;
        }

        if (activeProvider == AiProviderType.AnthropicClaude)
        {
            var anthropicKey = !string.IsNullOrWhiteSpace(options.ApiKey)
                ? options.ApiKey.Trim()
                : environmentStore.Read("ANTHROPIC_API_KEY")?.Trim();

            if (options.GeminiLive != false && !string.IsNullOrWhiteSpace(anthropicKey))
            {
                var claudeSw = Stopwatch.StartNew();
                var claudeModels = TerminalUx.RunWithSpinner(
                    "Discovering Anthropic Claude models...",
                    () => apiKeyService.ListAnthropicModels(anthropicKey, options.OpenAiBaseUrl),
                    color);
                claudeSw.Stop();
                TerminalUx.WriteStepDone(color, "Listed", $"{claudeModels.Count} Anthropic Claude model(s)", claudeSw.Elapsed);
                foreach (var model in claudeModels)
                {
                    output.WriteLine($"{model.ModelId}\t{model.DisplayName}");
                }

                return 0;
            }

            var claudeRecs = ModelCatalogService.GetRecommendations(AiProviderType.AnthropicClaude);
            color.WriteHeader("Recommended Anthropic Claude Models:");
            foreach (var rec in claudeRecs)
            {
                output.WriteLine($"{rec.ModelId}\t{rec.DisplayName}\t{rec.Reason}");
            }

            return 0;
        }

        if (activeProvider == AiProviderType.ManualPrompt)
        {
            color.WriteHeader("Manual Prompt Mode:");
            color.WriteMuted("  Manual Prompt mode renders Markdown prompts locally without calling a remote model API.");
            foreach (var rec in ModelCatalogService.GetRecommendations(AiProviderType.GoogleGemini))
            {
                output.WriteLine($"{rec.ModelId}\t{rec.DisplayName}\t{rec.Reason}");
            }

            return 0;
        }

        var gemSw = Stopwatch.StartNew();
        var secret = secretProvider.ResolveOrPrompt(options.ApiKey, input, output, options.NonInteractive);
        var models = TerminalUx.RunWithSpinner(
            "Discovering compatible Gemini models...",
            () => geminiApiClient.ListGenerateContentModels(secret.ApiKey!),
            color);
        gemSw.Stop();
        TerminalUx.WriteStepDone(color, "Listed", $"{models.Count} Gemini model(s)", gemSw.Elapsed);

        foreach (var model in models)
        {
            output.WriteLine($"{model.ModelId}\t{model.DisplayName}\t{model.Description}");
        }

        return 0;
    }

    public static void PrintHelp(TextWriter output, string? subcommand = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        var color = new ConsoleColorWriter(output);

        switch (subcommand?.ToLowerInvariant())
        {
            case "run" or "process":
                color.WriteHeader("Process Discovery Files (uv-style headless runner)");
                color.WriteMuted("USAGE: backlog process [all | <filename>] [OPTIONS]");
                output.WriteLine();
                output.WriteLine("ARGUMENTS:");
                output.WriteLine("  all | <filename>      Process all queued files in ./in (default), or a specific file / @mention");
                output.WriteLine();
                output.WriteLine("OPTIONS:");
                output.WriteLine("  -t, --type <01|02|both> Jira format: Type01 (Given/When/Then), Type02 (Title+Desc), or both (default: 01)");
                output.WriteLine("  --dry-run, --preview    Preview extracted Jira items without writing to disk");
                output.WriteLine("  --json [on|off]         Enable .json + .md dual export for this run");
                output.WriteLine("  --no-json               Write .md artifacts only for this run");
                output.WriteLine("  -i, --input <path>      Explicit input file or directory override");
                output.WriteLine("  --output-dir <path>     Explicit output directory override");
                output.WriteLine("  --provider <name>       AI provider: gemini, copilot-sdk, openai, azure, claude, manual");
                output.WriteLine("  -m, --model <name>      Target model ID override");
                output.WriteLine("  --gateway-url <url>     OpenAI-compatible gateway base URL override (env: OPENAI_BASE_URL)");
                output.WriteLine("  --wire-api <chat|responses> OpenAI Gateway wire API override (default: chat)");
                output.WriteLine("  --offline               Use local deterministic draft without live API calls");
                output.WriteLine("  -w, --workspace <path>  Workspace root override");
                output.WriteLine();
                output.WriteLine("EXAMPLES:");
                output.WriteLine("  backlog process all --dry-run");
                output.WriteLine("  backlog process notes.txt --type both --json");
                output.WriteLine("  backlog process all --provider openai --gateway-url https://api.openai.com/v1");
                output.WriteLine("  backlog process all --provider copilot-sdk --offline");
                return;

            case "draft":
                color.WriteHeader("Draft Backlog Item from Goal or Title");
                color.WriteMuted("USAGE: backlog draft \"<Goal or Title>\" [OPTIONS]");
                output.WriteLine();
                output.WriteLine("BEHAVIOR:");
                output.WriteLine("  When given only a Goal/Title (without @file notes), automatically expands via User Story");
                output.WriteLine("  principles (prompts/prompt-goal-expand.md) to enhance Title, synthesize Description/Story,");
                output.WriteLine("  and suggest 3 concise Title & Description alternatives.");
                output.WriteLine();
                output.WriteLine("OPTIONS:");
                output.WriteLine("  -t, --type <01|02|both>    Type01 (User Story + GWT), Type02 (Schedule Item), or both (default: 01)");
                output.WriteLine("  -d, --details <text>       Additional context, scope constraints, or information to include");
                output.WriteLine("  --search, --web            Run live Google Search grounding first (Gemini Live mode)");
                output.WriteLine("  --preview, --dry-run       Preview generated Jira draft in terminal without writing files");
                output.WriteLine("  --json [on|off]            Write .json alongside .md output");
                output.WriteLine("  --provider <name>          AI provider: gemini, copilot-sdk, openai, azure, claude, manual");
                output.WriteLine("  -m, --model <name>         Target model ID override");
                output.WriteLine("  --gateway-url <url>        OpenAI-compatible gateway base URL override (env: OPENAI_BASE_URL)");
                output.WriteLine("  --wire-api <chat|responses> OpenAI Gateway wire API override (default: chat)");
                output.WriteLine("  --offline                  Draft deterministically without live API calls");
                output.WriteLine();
                output.WriteLine("EXAMPLES:");
                output.WriteLine("  backlog draft \"Implement OAuth2 PKCE Login\" --type 01 --preview");
                output.WriteLine("  backlog draft \"Check Geocaching 2026 hiding movie theme\" -t 02 -d \"Include rules and dates\" --search");
                output.WriteLine("  backlog draft \"Implement API Rate Limiter\" --provider openai -m gpt-4.1-mini --preview");
                output.WriteLine("  backlog draft \"Migrate Redis Cache\" --type both --json");
                return;

            case "config":
                color.WriteHeader("View or Persist Workspace Configuration");
                color.WriteMuted("USAGE: backlog config [OPTIONS]");
                output.WriteLine();
                output.WriteLine("OPTIONS:");
                output.WriteLine("  --json <on|off>         Enable/disable persistent .json + .md dual export");
                output.WriteLine("  --in <path|default>     Set custom input directory (./in) or reset to default");
                output.WriteLine("  --out <path|default>    Set custom output directory (./out) or reset to default");
                output.WriteLine("  --provider <name>       Set default AI provider (gemini, copilot-sdk, openai, azure, claude, manual)");
                output.WriteLine("  -m, --model <name|auto> Pin a model ID or reset to automatic selection");
                output.WriteLine("  --gateway-url <url|default> Persist OpenAI-compatible gateway base URL (or reset to env/default)");
                output.WriteLine("  --wire-api <chat|responses|default> Persist OpenAI Gateway wire API (default: chat)");
                output.WriteLine("  --live <on|off>         Enable/disable live AI generation by default");
                output.WriteLine("  --theme <agy|uv|kubectl> Set interactive CLI theme palette");
                output.WriteLine("  --verbosity <min|bal|det> Set default outcome verbosity (Minimalistic, Balanced, Detailed)");
                output.WriteLine("  --log-level <level|default> Set stderr log severity (trace, debug, information, warning, error, critical, none)");
                output.WriteLine("  --log-format <text|json|default> Set stderr log serialization format (default: text)");
                output.WriteLine("  --show                  Display current settings summary");
                output.WriteLine("  --reset                 Reset settings.json back to defaults");
                output.WriteLine();
                output.WriteLine("EXAMPLES:");
                output.WriteLine("  backlog config --json on --in ./in --out ./out");
                output.WriteLine("  backlog config --provider openai --gateway-url https://gateway.internal/v1 --model gpt-4.1-mini");
                output.WriteLine("  backlog config --provider copilot-sdk --theme uv --log-level debug --log-format json");
                return;

            case "keys":
                color.WriteHeader("Manage AI Provider API Keys");
                color.WriteMuted("USAGE: backlog keys [status | set | validate | verify | clear] [ARGS] [OPTIONS]");
                output.WriteLine();
                output.WriteLine("ACTIONS:");
                output.WriteLine("  status | list            Display masked (****last4) readiness across all 5 AI providers");
                output.WriteLine("  set <provider>           Securely prompt (* masked) or inject key into runtime/OS env");
                output.WriteLine("  validate [<provider>|all] Check key syntax, placeholders, & cross-provider mismatch (offline)");
                output.WriteLine("  verify [<provider>|all]  Run format check + live authentication verification against provider");
                output.WriteLine("  clear <provider|all>     Remove provider API key from active session (and optional OS User env)");
                output.WriteLine();
                output.WriteLine("PROVIDERS:");
                output.WriteLine("  gemini | openai | azure | claude | copilot-sdk");
                output.WriteLine();
                output.WriteLine("OPTIONS:");
                output.WriteLine("  --api-key <key>          Supply key non-interactively (never written to settings.json)");
                output.WriteLine("  --stdin                  Read API key securely from standard input");
                output.WriteLine("  --scope <session|user|all> Storage scope (default: session; 'user' persists on Windows; 'all' for clear)");
                output.WriteLine("  --verify                 Execute live API authentication check before saving/confirming");
                output.WriteLine("  --offline                Skip live API verification (format validation only)");
                output.WriteLine("  --gateway-url <url>      Custom OpenAI Gateway or Azure OpenAI endpoint URL");
                output.WriteLine("  --json                   Emit masked key readiness as JSON (never includes plaintext)");
                output.WriteLine();
                output.WriteLine("EXAMPLES:");
                output.WriteLine("  backlog keys status");
                output.WriteLine("  backlog keys set gemini --verify");
                output.WriteLine("  backlog keys set openai --api-key sk-proj-... --scope user");
                output.WriteLine("  backlog keys validate all");
                output.WriteLine("  backlog keys verify claude");
                output.WriteLine("  backlog keys clear openai --scope all");
                return;

            case "doctor":
                color.WriteHeader("Workspace & AI Diagnostics");
                color.WriteMuted("USAGE: backlog status [OPTIONS]");
                output.WriteLine();
                output.WriteLine("Displays a diagnostic table of WSL2/Windows resolved paths, queued ./in file count,");
                output.WriteLine("active log level/format, export mode (.md / .json), active AI model, Gemini API key status, OpenAI Gateway, and Copilot SDK runtime readiness.");
                output.WriteLine();
                output.WriteLine("OPTIONS:");
                output.WriteLine("  -w, --workspace <path>  Inspect a custom workspace root");
                output.WriteLine("  --provider <name>       Inspect readiness with a specific provider selected");
                output.WriteLine("  --gateway-url <url>     Inspect readiness with a custom OpenAI Gateway base URL");
                output.WriteLine("  --wire-api <chat|responses> Inspect readiness with a specific OpenAI Gateway wire API");
                output.WriteLine("  --log-level <level>     Override effective log level for inspection");
                output.WriteLine("  --theme <agy|uv|kubectl> Override table color palette");
                return;

            case "models":
                color.WriteHeader("List Supported AI Models");
                color.WriteMuted("USAGE: backlog models [--provider <gemini|copilot-sdk|openai|azure|claude>]");
                output.WriteLine();
                output.WriteLine("Lists models available to your active provider (Google Gemini, GitHub Copilot SDK, OpenAI Gateway, Azure OpenAI, or Anthropic Claude).");
                return;

            case "preview":
                color.WriteHeader("Preview Saved Markdown Outcome");
                color.WriteMuted("USAGE: backlog preview [filename | latest] [OPTIONS]");
                output.WriteLine();
                output.WriteLine("Reads a saved .md outcome from ./out (or explicit path) and previews it in one colored table.");
                output.WriteLine();
                output.WriteLine("OPTIONS:");
                output.WriteLine("  -s, --select             Interactively fuzzy-select file & field (prompts on stderr, clean value on stdout)");
                output.WriteLine("  --copy <target>          Copy to clipboard: raw, md, clean, simple, title, story, desc, ac, alt1..alt3");
                output.WriteLine("  --output-dir <path>      Override ./out directory to inspect");
                output.WriteLine("  -w, --workspace <path>   Workspace root override");
                output.WriteLine("  --log-level <level>      Diagnostic stderr log severity (stdout remains clean)");
                output.WriteLine("  --log-format <text|json> Diagnostic stderr log format (text or json)");
                output.WriteLine("  --theme <agy|uv|kubectl> Table color palette");
                return;
        }

        color.WriteHeader("Smart Backlog Assistant — Hybrid Ergonomic CLI & Interactive REPL");
        color.WriteMuted("Astral uv subcommands for headless speed + Gemini CLI / Agy REPL for interactive workflows");
        output.WriteLine();
        output.WriteLine("USAGE:");
        output.WriteLine("  backlog [command] [options]");
        output.WriteLine("  dotnet run --project src/SmartBacklogAssistant -- [command] [options]");
        output.WriteLine();
        output.WriteLine("SUBCOMMANDS (Astral uv style):");
        output.WriteLine("  process [all | <file>]   Process queued ./in files or a specific file (alias: run)");
        output.WriteLine("  draft \"<Goal or Title>\"  Draft a Type01 User Story and/or Type02 Schedule Item (alias: add)");
        output.WriteLine("  preview [<file>|latest]  Preview a saved ./out/*.md file in one colored table (alias: view)");
        output.WriteLine("  status                   Show WSL2/Windows paths, queued files, & AI readiness table (alias: doctor)");
        output.WriteLine("  keys [status|set|verify|clear] Manage & validate AI provider API keys securely (masked, never in settings.json)");
        output.WriteLine("  config                   Configure persisted settings (--json, --in, --out, --provider, --gateway-url, --log-level, --theme)");
        output.WriteLine("  models                   List available models for the active AI provider");
        output.WriteLine("  menu | tui               Launch interactive Gemini Agy REPL/TUI (default when no args given)");
        output.WriteLine("  version                  Print build, .NET runtime, and architecture information");
        output.WriteLine();
        output.WriteLine("INTERACTIVE REPL (run with zero arguments):");
        output.WriteLine("  Starts from [1] Backlog Assisted Orchestrator (all-in-one: Draft, Process, Preview/Refine, Copy, Settings, & Status),");
        output.WriteLine("  supporting [0-4], quick shortcuts [w/g/d/p/v/s/c/?/q], /slash commands (/wizard, /generate, /draft, /process,");
        output.WriteLine("  /preview, /copy, /verbosity, /log-level, /log-format, /settings, /keys, /status, /config, /provider, /models, /theme, /json, /live, /help, /exit),");
        output.WriteLine("  universal submenu [b]ack/[q]uit navigation, theme-matched option highlights, and @filename context mentions.");
        output.WriteLine();
        output.WriteLine("AI PROVIDERS (--provider):");
        output.WriteLine("  gemini (default) | copilot-sdk (Microsoft.Copilot.SDK) | openai (OpenAI Gateway API) | azure | claude | manual");
        output.WriteLine();
        output.WriteLine("GLOBAL OPTIONS:");
        output.WriteLine("  -w, --workspace <path>       Workspace root override (c:\\workspace on Win, /mnt/c/workspace on WSL)");
        output.WriteLine("  -p, --prompt <path>          Markdown prompt template path override");
        output.WriteLine("  --api-key <key>              Provider API key or GitHub token");
        output.WriteLine("  -m, --model <name>           Model ID override");
        output.WriteLine("  --gateway-url <url>          OpenAI-compatible gateway base URL (env: OPENAI_BASE_URL)");
        output.WriteLine("  --wire-api <chat|responses>  OpenAI Gateway wire API: chat (default) or responses (env: OPENAI_WIRE_API)");
        output.WriteLine("  -d, --details <text>         Additional context/requirements for goal drafts");
        output.WriteLine("  --search, --web              Run Google Search grounding on goal drafts (Gemini Live)");
        output.WriteLine("  --offline / --live           Toggle live AI generation vs local deterministic drafting");
        output.WriteLine("  -s, --select                 Interactive fuzzy output picker (stderr prompts, clean stdout)");
        output.WriteLine("  --copy <target>              Copy outcome or field (raw, md, clean, title, story, desc, ac, alt1..)");
        output.WriteLine("  --log-level <level>          Stderr log level: trace, debug, information, warning (default), error, critical, none (env: SBA_LOG_LEVEL)");
        output.WriteLine("  --log-format <text|json>     Stderr log format: text (default) or json / NDJSON (env: SBA_LOG_FORMAT)");
        output.WriteLine("  -V, --verbose                Shortcut for --log-level debug (diagnostic logs routed to stderr)");
        output.WriteLine("  -q, --quiet                  Shortcut for --log-level none (suppress diagnostic logs)");
        output.WriteLine("  --non-interactive            Disable stdin prompts");
        output.WriteLine("  --theme <agy|uv|kubectl>     CLI color palette");
        output.WriteLine("  -h, --help                   Show help (run '<command> --help' for command options)");
        output.WriteLine();
        output.WriteLine("EXAMPLES:");
        output.WriteLine("  backlog                                              # Launch Gemini/Agy interactive REPL");
        output.WriteLine("  backlog keys set gemini --verify                     # Securely enter & verify Gemini API key");
        output.WriteLine("  backlog process all --dry-run --log-level debug      # Preview all ./in files with stderr diagnostics");
        output.WriteLine("  backlog draft \"OAuth2 PKCE Login\" -t 01 --preview    # Quick-draft without disk write");
        output.WriteLine("  backlog draft \"OAuth2 PKCE\" --provider openai --gateway-url https://api.openai.com/v1");
        output.WriteLine("  backlog config --json on --log-level info            # Persist workspace & logging overrides");
        output.WriteLine("  backlog status                                       # Crisp WSL2/Windows & AI table");
    }

    private static (string? Subcommand, string? PositionalArg) ParseSubcommand(string[] args)
    {
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (ValueFlags.Contains(token))
            {
                i++;
                continue;
            }

            if (OptionalBoolValueFlags.Contains(token))
            {
                if (i + 1 < args.Length && IsBooleanToken(args[i + 1]))
                {
                    i++;
                }

                continue;
            }

            if (token.StartsWith('-'))
            {
                continue;
            }

            positional.Add(token);
        }

        if (positional.Count == 0)
        {
            return (null, null);
        }

        var canonical = MapSubcommand(positional[0]);
        if (canonical is null)
        {
            return (null, null);
        }

        var remaining = positional.Skip(1).Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
        var positionalArg = remaining.Length == 0
            ? null
            : string.Join(" ", remaining);

        return (canonical, positionalArg);
    }

    private static string? MapSubcommand(string token) => token.Trim().ToLowerInvariant() switch
    {
        "run" or "process" => "run",
        "draft" or "add" => "draft",
        "preview" or "view" or "show" => "preview",
        "config" => "config",
        "keys" or "key" => "keys",
        "models" => "models",
        "doctor" or "status" => "doctor",
        "menu" or "tui" => "menu",
        "version" => "version",
        "help" => "help",
        _ => null
    };

    private static string? ExtractSubcommandHelpTarget(string[] args)
    {
        foreach (var token in args)
        {
            if (token.StartsWith('-') || string.Equals(token, "help", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var mapped = MapSubcommand(token);
            if (mapped is not null)
            {
                return mapped;
            }
        }

        return null;
    }

    private static WorkflowOptions ResolveDirectExecutionOptions(
        string[] args,
        WorkflowOptions options,
        WorkspacePathService workspaceService,
        CliSettingsService settingsService,
        RawDataCollector collector)
    {
        var baseWorkspace = workspaceService.Resolve(options.WorkspaceRoot);
        var persistedSettings = settingsService.Load(baseWorkspace);
        var workspace = workspaceService.ApplySettings(baseWorkspace, persistedSettings);

        if (string.Equals(
            workspace.InputDirectory.TrimEnd('\\', '/'),
            baseWorkspace.InputDirectory.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase))
        {
            workspaceService.EnsureDirectoriesExist(baseWorkspace);
        }
        else
        {
            Directory.CreateDirectory(workspace.InputDirectory);
            Directory.CreateDirectory(workspace.OutputDirectory);
        }

        var effectiveJsonOut = options.JsonFileOut ?? persistedSettings.JsonFileOut;
        var effectiveTheme = options.CliTheme ?? ConsoleColorWriter.GetThemeCode(persistedSettings.Theme);
        var effectiveProvider = options.ProviderInput ??
            (persistedSettings.PreferredProvider is { } preferred
                ? RuntimeEnvironmentService.FormatProviderCode(preferred)
                : null);

        string inputPath;
        if (HasAnyFlag(args, "--input", "-i"))
        {
            inputPath = workspaceService.TryResolveContextFile(options.InputFilePath, workspace)
                ?? WorkspacePathService.NormalizePathForOs(options.InputFilePath, workspace.IsWindows);
        }
        else if (options.Subcommand == "run")
        {
            var positionalTarget = GetPositionalTargetForSubcommand(args);
            inputPath = workspaceService.ResolveInputTarget(positionalTarget ?? "all", workspace);
        }
        else
        {
            inputPath = workspace.InputDirectory;
        }

        var outputDir = HasFlag(args, "--output-dir") && !string.IsNullOrWhiteSpace(options.OutputDirectory)
            ? workspaceService.ResolveCustomOrRelativePath(options.OutputDirectory, workspace.IsWindows)
            : workspace.OutputDirectory;

        var goalTitle = options.GoalTitle;
        var goalContext = options.GoalContext;
        var webSearch = options.WebSearchGrounding;

        if (!string.IsNullOrWhiteSpace(goalTitle))
        {
            var parsedGoal = RawDataCollector.ParseInlineGoalModifiers(goalTitle);
            goalTitle = parsedGoal.GoalExpression;
            if (!string.IsNullOrWhiteSpace(parsedGoal.InlineDetails))
            {
                goalContext = string.IsNullOrWhiteSpace(goalContext)
                    ? parsedGoal.InlineDetails
                    : $"{parsedGoal.InlineDetails}\n{goalContext.Trim()}";
            }

            webSearch = webSearch || parsedGoal.EnableWebSearch;
        }

        if (options.Subcommand == "draft" && string.IsNullOrWhiteSpace(goalTitle))
        {
            throw new ArgumentException("Missing goal or title for 'draft'. Usage: backlog draft \"<Goal or Title>\" [--type <01|02|both>] [--details <text>] [--search] [--preview]");
        }

        if (!string.IsNullOrWhiteSpace(goalTitle) && goalTitle.Contains('@'))
        {
            goalTitle = ResolveGoalMentions(goalTitle, workspace, workspaceService, collector);
        }

        // One-shot uv-style subcommands default to Type01 when -t/--type is omitted so they never block stdin.
        var effectiveBacklogType = options.BacklogTypeInput;
        if (string.IsNullOrWhiteSpace(effectiveBacklogType) &&
            options.Subcommand is "run" or "draft")
        {
            effectiveBacklogType = "Type01";
        }

        var hasExplicitVerbosity = HasAnyFlag(args, "--verbosity", "--minimal", "--minimalistic", "--detailed");
        var modelOrchestrator = new AiModelOrchestrator();
        var explicitProviderOverride = !string.IsNullOrWhiteSpace(options.ProviderInput)
            ? modelOrchestrator.ResolveProvider(options.ProviderInput)
            : (AiProviderType?)null;
        var persistedProvider = persistedSettings.PreferredProvider ?? AiProviderType.GoogleGemini;
        var switchedProviderWithoutModel = explicitProviderOverride is { } overrideProv &&
                                           overrideProv != persistedProvider;

        return options with
        {
            InputFilePath = inputPath,
            OutputDirectory = outputDir,
            GoalTitle = goalTitle,
            GoalContext = goalContext,
            WebSearchGrounding = webSearch,
            BacklogTypeInput = effectiveBacklogType,
            ProviderInput = effectiveProvider,
            JsonFileOut = effectiveJsonOut,
            CliTheme = effectiveTheme,
            ModelName = !string.IsNullOrWhiteSpace(options.ModelName)
                ? options.ModelName
                : switchedProviderWithoutModel
                    ? null
                    : persistedSettings.ModelName,
            GeminiLive = options.GeminiLive ?? persistedSettings.GeminiLive ?? true,
            Verbosity = hasExplicitVerbosity
                ? options.Verbosity
                : persistedSettings.DefaultVerbosity ?? options.Verbosity,
            PersistedOpenAiBaseUrl = persistedSettings.OpenAiBaseUrl,
            PersistedOpenAiWireApi = persistedSettings.OpenAiWireApi
        };
    }

    private static string ResolveGoalMentions(
        string goalTitle,
        WorkspaceDirectories workspace,
        WorkspacePathService workspaceService,
        RawDataCollector collector)
    {
        var tokens = goalTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var attached = new List<string>();
        var remaining = new List<string>();

        foreach (var token in tokens)
        {
            if (token.StartsWith('@') && token.Length > 1)
            {
                var file = workspaceService.TryResolveContextFile(token, workspace);
                if (file is not null)
                {
                    attached.Add(file);
                    continue;
                }
            }

            remaining.Add(token);
        }

        if (attached.Count == 0)
        {
            return goalTitle;
        }

        var headline = remaining.Count > 0
            ? string.Join(" ", remaining)
            : Path.GetFileNameWithoutExtension(attached[0]);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Goal: {headline}");
        foreach (var file in attached)
        {
            sb.AppendLine();
            sb.AppendLine($"--- Context (@{Path.GetFileName(file)}) ---");
            sb.AppendLine(collector.CollectFromFile(file));
        }

        return sb.ToString().TrimEnd();
    }

    private static string? GetPositionalTargetForSubcommand(string[] args)
    {
        var (_, arg) = ParseSubcommand(args);
        return arg;
    }

    private static (bool? JsonFileOut, bool CombinedTypes) ParseOutputAndJsonFlags(string[] args)
    {
        var jsonOut = ParseJsonFileOutArg(args);
        var combinedTypes = false;

        var outputFormat = GetArgValue(args, "--output", "-o")?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(outputFormat))
        {
            switch (outputFormat)
            {
                case "json":
                case "both":
                case "all":
                    jsonOut = true;
                    break;
                case "md":
                case "markdown":
                    jsonOut ??= false;
                    break;
            }
        }

        return (jsonOut, combinedTypes);
    }

    private static bool? ParseJsonFileOutArg(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--no-json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--no-json-out", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--no-json-file-out", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(args[i], "--json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--json-out", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--json-file-out", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && TryParseBooleanToken(args[i + 1], out var parsed))
                {
                    return parsed;
                }

                return true;
            }
        }

        return null;
    }

    private static bool? ParseOptionalBoolFlag(string[] args, string flag)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && TryParseBooleanToken(args[i + 1], out var parsed))
                {
                    return parsed;
                }

                return true;
            }
        }

        return null;
    }

    private static bool TryParseStrictConfigBoolFlag(
        string[] args,
        string primaryFlag,
        string? aliasFlag1,
        string? aliasFlag2,
        string? negativeFlag1,
        string? negativeFlag2,
        out bool result)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if ((negativeFlag1 is not null && token.Equals(negativeFlag1, StringComparison.OrdinalIgnoreCase)) ||
                (negativeFlag2 is not null && token.Equals(negativeFlag2, StringComparison.OrdinalIgnoreCase)))
            {
                result = false;
                return true;
            }

            if (token.Equals(primaryFlag, StringComparison.OrdinalIgnoreCase) ||
                (aliasFlag1 is not null && token.Equals(aliasFlag1, StringComparison.OrdinalIgnoreCase)) ||
                (aliasFlag2 is not null && token.Equals(aliasFlag2, StringComparison.OrdinalIgnoreCase)))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    if (!TryParseBooleanToken(args[i + 1], out result))
                    {
                        throw new ArgumentException($"Invalid boolean value '{args[i + 1]}' for '{token}'. Use 'on' or 'off'.");
                    }

                    return true;
                }

                result = true;
                return true;
            }
        }

        result = false;
        return false;
    }

    private static bool TryGetRequiredFlagValue(string[] args, out string value, params string[] flags)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            foreach (var flag in flags)
            {
                if (string.Equals(token, flag, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith('-'))
                    {
                        throw new ArgumentException($"Missing value for '{flag}'.");
                    }

                    value = args[i + 1];
                    return true;
                }

                if (token.Length > flag.Length &&
                    token[flag.Length] == '=' &&
                    token.StartsWith(flag, StringComparison.OrdinalIgnoreCase))
                {
                    var inlineValue = token[(flag.Length + 1)..];
                    if (string.IsNullOrWhiteSpace(inlineValue))
                    {
                        throw new ArgumentException($"Missing value for '{flag}'.");
                    }

                    value = inlineValue;
                    return true;
                }
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool IsResetToken(string? token) =>
        !string.IsNullOrWhiteSpace(token) &&
        (token.Trim().Equals("default", StringComparison.OrdinalIgnoreCase) ||
         token.Trim().Equals("reset", StringComparison.OrdinalIgnoreCase));

    private static bool IsBooleanToken(string token) =>
        TryParseBooleanToken(token, out _);

    private static bool TryParseBooleanToken(string token, out bool value)
    {
        switch (token.Trim().ToLowerInvariant())
        {
            case "on" or "true" or "1" or "yes":
                value = true;
                return true;
            case "off" or "false" or "0" or "no":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static bool IsValidThemeToken(string token) =>
        token.Trim().ToLowerInvariant() is "1" or "agy" or "gemini" or "gemini-agy" or "default"
            or "2" or "uv" or "astral" or "astral-uv"
            or "3" or "kubectl" or "k8s" or "kube";

    private static bool HasFlag(string[] args, string flag) =>
        args.Contains(flag, StringComparer.OrdinalIgnoreCase);

    private static bool HasExactFlag(string[] args, string flag) =>
        args.Contains(flag, StringComparer.Ordinal);

    private static bool HasAnyFlag(string[] args, params string[] flags)
    {
        foreach (var flag in flags)
        {
            if (args.Contains(flag, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetArgValue(string[] args, params string[] flags)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            foreach (var flag in flags)
            {
                if (string.Equals(token, flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    return args[i + 1];
                }

                if (token.Length > flag.Length &&
                    token[flag.Length] == '=' &&
                    token.StartsWith(flag, StringComparison.OrdinalIgnoreCase))
                {
                    var inlineValue = token[(flag.Length + 1)..];
                    return string.IsNullOrWhiteSpace(inlineValue) ? null : inlineValue;
                }
            }
        }

        return null;
    }
}
