using System.Diagnostics;
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
        "--verbosity"
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
        Execute(args, input, output, error, clipboardService: null);

    internal static int Execute(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        IClipboardService? clipboardService)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            var options = ParseArgs(args);

            if (HasAnyFlag(args, "--help", "-h") || options.Subcommand == "help")
            {
                var helpTarget = options.Subcommand == "help"
                    ? ExtractSubcommandHelpTarget(args)
                    : options.Subcommand;
                PrintHelp(output, helpTarget);
                return 0;
            }

            var runtimeEnvService = new RuntimeEnvironmentService();
            var secretProvider = new GeminiSecretProvider();
            var geminiApiClient = new GeminiApiClient();
            var workspaceService = new WorkspacePathService();
            var settingsService = new CliSettingsService();
            var modelOrchestrator = new AiModelOrchestrator();
            var providerOrchestrator = new AiProviderOrchestrator(geminiApiClient);
            var diagnosticService = new WorkspaceDiagnosticService(
                workspaceService,
                runtimeEnvService,
                secretProvider,
                settingsService,
                modelOrchestrator,
                providerOrchestrator);

            if (options.ShowVersion)
            {
                var info = runtimeEnvService.Inspect();
                output.WriteLine($"SmartBacklogAssistant v{info.BuildVersion} ({info.BuildConfiguration}) | {info.FrameworkDescription} | {info.OsPlatform} ({info.ProcessArchitecture})");
                return 0;
            }

            if (options.Subcommand == "config")
            {
                return RunConfigCommand(
                    args,
                    options,
                    workspaceService,
                    settingsService,
                    diagnosticService,
                    modelOrchestrator,
                    output);
            }

            if (options.Subcommand == "preview")
            {
                return RunPreviewCommand(
                    args,
                    options,
                    workspaceService,
                    settingsService,
                    clipboardService ?? new OsClipboardService(),
                    input,
                    output,
                    error);
            }

            if (options.ShowDoctorStatus)
            {
                return RunDoctorStatus(
                    options,
                    runtimeEnvService,
                    secretProvider,
                    workspaceService,
                    settingsService,
                    modelOrchestrator,
                    output,
                    providerOrchestrator);
            }

            if (options.ListGeminiModels)
            {
                return RunModelsCommand(
                    options,
                    workspaceService,
                    settingsService,
                    modelOrchestrator,
                    providerOrchestrator,
                    secretProvider,
                    geminiApiClient,
                    diagnosticService,
                    input,
                    output);
            }

            var collector = new RawDataCollector();
            var promptService = new PromptTemplateService();
            var formatter = new JiraBacklogFormatter();

            var workflow = new AsIsV1Workflow(
                collector,
                promptService,
                modelOrchestrator,
                formatter,
                runtimeEnvService,
                secretProvider,
                geminiApiClient,
                providerOrchestrator);

            if (ShouldLaunchMenu(args, options))
            {
                var menuRunner = new CliMenuRunner(
                    workflow,
                    workspaceService,
                    runtimeEnvService,
                    secretProvider,
                    modelOrchestrator,
                    settingsService,
                    diagnosticService,
                    clipboardService);

                return menuRunner.Run(options, input, output);
            }

            // Direct / headless execution (uv-style one-shot subcommands or explicit flags)
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
            var theme = ConsoleColorWriter.ResolveTheme(GetArgValue(args, "--theme"));
            var errorWriter = new ConsoleColorWriter(error, theme: theme);
            TerminalUx.WriteActionableError(errorWriter, ex);
            return 1;
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
                          HasFlag(args, "-v") ||
                          subcommand == "version";
        var verbose = HasFlag(args, "--verbose") || HasFlag(args, "-V");
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
            Verbosity: verbosity);
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
            options.Subcommand is "config" or "help" or "preview")
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
        AiProviderOrchestrator? providerOrchestrator = null)
    {
        var diagnosticService = new WorkspaceDiagnosticService(
            workspaceService,
            runtimeEnvService,
            secretProvider,
            settingsService,
            orchestrator,
            providerOrchestrator);

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
            Console.Error);

    public static int RunPreviewCommand(
        string[] args,
        WorkflowOptions options,
        WorkspacePathService workspaceService,
        CliSettingsService settingsService,
        IClipboardService clipboardService,
        TextReader input,
        TextWriter output,
        TextWriter error)
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
        TextWriter output)
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
                updated = updated with { PreferredProvider = orchestrator.ResolveProvider(providerValue) };
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
        }

        if (!changed)
        {
            var currentActiveWorkspace = workspaceService.ApplySettings(baseWorkspace, currentSettings);
            diagnosticService.RenderConfigSummary(baseWorkspace, currentActiveWorkspace, currentSettings, color);
            return 0;
        }

        var savedPath = settingsService.Save(baseWorkspace, updated);
        var activeWorkspace = workspaceService.ApplySettings(baseWorkspace, updated);
        Directory.CreateDirectory(activeWorkspace.InputDirectory);
        Directory.CreateDirectory(activeWorkspace.OutputDirectory);

        sw.Stop();
        TerminalUx.WriteStepDone(color, "Saved", $"workspace settings to '{savedPath}'", sw.Elapsed);
        diagnosticService.RenderConfigSummary(baseWorkspace, activeWorkspace, updated, color);
        return 0;
    }

    private static int RunModelsCommand(
        WorkflowOptions options,
        WorkspacePathService workspaceService,
        CliSettingsService settingsService,
        AiModelOrchestrator modelOrchestrator,
        AiProviderOrchestrator providerOrchestrator,
        GeminiSecretProvider secretProvider,
        GeminiApiClient geminiApiClient,
        WorkspaceDiagnosticService diagnosticService,
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
                output.WriteLine("  --provider <name>       AI provider: gemini, copilot-sdk, azure, claude, manual");
                output.WriteLine("  -m, --model <name>      Target model ID override");
                output.WriteLine("  --offline               Use local deterministic draft without live API calls");
                output.WriteLine("  -w, --workspace <path>  Workspace root override");
                output.WriteLine();
                output.WriteLine("EXAMPLES:");
                output.WriteLine("  backlog process all --dry-run");
                output.WriteLine("  backlog process notes.txt --type both --json");
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
                output.WriteLine("  --provider <name>          AI provider: gemini, copilot-sdk, azure, claude, manual");
                output.WriteLine("  -m, --model <name>         Target model ID override");
                output.WriteLine("  --offline                  Draft deterministically without live API calls");
                output.WriteLine();
                output.WriteLine("EXAMPLES:");
                output.WriteLine("  backlog draft \"Implement OAuth2 PKCE Login\" --type 01 --preview");
                output.WriteLine("  backlog draft \"Check Geocaching 2026 hiding movie theme\" -t 02 -d \"Include rules and dates\" --search");
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
                output.WriteLine("  --provider <name>       Set default AI provider (gemini, copilot-sdk, azure, claude, manual)");
                output.WriteLine("  -m, --model <name|auto> Pin a model ID or reset to automatic selection");
                output.WriteLine("  --live <on|off>         Enable/disable live AI generation by default");
                output.WriteLine("  --theme <agy|uv|kubectl> Set interactive CLI theme palette");
                output.WriteLine("  --verbosity <min|bal|det> Set default outcome verbosity (Minimalistic, Balanced, Detailed)");
                output.WriteLine("  --show                  Display current settings summary");
                output.WriteLine("  --reset                 Reset settings.json back to defaults");
                output.WriteLine();
                output.WriteLine("EXAMPLES:");
                output.WriteLine("  backlog config --json on --in ./in --out ./out");
                output.WriteLine("  backlog config --provider copilot-sdk --theme uv");
                return;

            case "doctor":
                color.WriteHeader("Workspace & AI Diagnostics");
                color.WriteMuted("USAGE: backlog status [OPTIONS]");
                output.WriteLine();
                output.WriteLine("Displays a diagnostic table of WSL2/Windows resolved paths, queued ./in file count,");
                output.WriteLine("export mode (.md / .json), active AI model, Gemini API key status, and Copilot SDK runtime readiness.");
                output.WriteLine();
                output.WriteLine("OPTIONS:");
                output.WriteLine("  -w, --workspace <path>  Inspect a custom workspace root");
                output.WriteLine("  --provider <name>       Inspect readiness with a specific provider selected");
                output.WriteLine("  --theme <agy|uv|kubectl> Override table color palette");
                return;

            case "models":
                color.WriteHeader("List Supported AI Models");
                color.WriteMuted("USAGE: backlog models [--provider <gemini|copilot-sdk>]");
                output.WriteLine();
                output.WriteLine("Lists models available to your active provider (Google Gemini or GitHub Copilot SDK).");
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
        output.WriteLine("  config                   Configure persisted settings (--json, --in, --out, --provider, --theme)");
        output.WriteLine("  models                   List available models for the active AI provider");
        output.WriteLine("  menu | tui               Launch interactive Gemini Agy REPL/TUI (default when no args given)");
        output.WriteLine("  version                  Print build, .NET runtime, and architecture information");
        output.WriteLine();
        output.WriteLine("INTERACTIVE REPL (run with zero arguments):");
        output.WriteLine("  Starts from [1] Wizard Orchestrator (all-in-one: Draft, Process, Preview/Refine, Copy, Settings, & Status),");
        output.WriteLine("  supporting [0-5], quick shortcuts [w/d/p/v/s/c/?/q], /slash commands (/wizard, /draft, /process, /preview,");
        output.WriteLine("  /copy, /verbosity, /settings, /status, /config, /provider, /models, /theme, /json, /live, /help, /exit),");
        output.WriteLine("  universal submenu [b]ack/[q]uit navigation, and @filename context mentions.");
        output.WriteLine();
        output.WriteLine("AI PROVIDERS (--provider):");
        output.WriteLine("  gemini (default) | copilot-sdk (Microsoft.Copilot.SDK) | azure | claude | manual");
        output.WriteLine();
        output.WriteLine("GLOBAL OPTIONS:");
        output.WriteLine("  -w, --workspace <path>       Workspace root override (c:\\workspace on Win, /mnt/c/workspace on WSL)");
        output.WriteLine("  -p, --prompt <path>          Markdown prompt template path override");
        output.WriteLine("  --api-key <key>              Provider API key or GitHub token");
        output.WriteLine("  -m, --model <name>           Model ID override");
        output.WriteLine("  -d, --details <text>         Additional context/requirements for goal drafts");
        output.WriteLine("  --search, --web              Run Google Search grounding on goal drafts (Gemini Live)");
        output.WriteLine("  --offline / --live           Toggle live AI generation vs local deterministic drafting");
        output.WriteLine("  -s, --select                 Interactive fuzzy output picker (stderr prompts, clean stdout)");
        output.WriteLine("  --copy <target>              Copy outcome or field (raw, md, clean, title, story, desc, ac, alt1..)");
        output.WriteLine("  --non-interactive            Disable stdin prompts");
        output.WriteLine("  --theme <agy|uv|kubectl>     CLI color palette");
        output.WriteLine("  -h, --help                   Show help (run '<command> --help' for command options)");
        output.WriteLine();
        output.WriteLine("EXAMPLES:");
        output.WriteLine("  backlog                                              # Launch Gemini/Agy interactive REPL");
        output.WriteLine("  backlog process all --dry-run                        # Preview all queued ./in files");
        output.WriteLine("  backlog draft \"OAuth2 PKCE Login\" -t 01 --preview    # Quick-draft without disk write");
        output.WriteLine("  backlog config --json on --in ./in --out ./out       # Persist workspace overrides");
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
            ModelName = options.ModelName ?? persistedSettings.ModelName,
            GeminiLive = options.GeminiLive ?? persistedSettings.GeminiLive ?? true,
            Verbosity = hasExplicitVerbosity
                ? options.Verbosity
                : persistedSettings.DefaultVerbosity ?? options.Verbosity
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
            foreach (var flag in flags)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith('-'))
                    {
                        throw new ArgumentException($"Missing value for '{flag}'.");
                    }

                    value = args[i + 1];
                    return true;
                }
            }
        }

        value = string.Empty;
        return false;
    }

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
        for (var i = 0; i < args.Length - 1; i++)
        {
            foreach (var flag in flags)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
        }

        return null;
    }
}
