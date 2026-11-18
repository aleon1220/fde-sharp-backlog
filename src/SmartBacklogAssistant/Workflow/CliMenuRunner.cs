using System.Diagnostics;
using System.Text;
using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Workflow;

/// <summary>
/// Hybrid Gemini CLI / Agy interactive REPL &amp; 3-option TUI state machine supporting
/// numeric navigation, <c>/slash</c> commands, <c>@filename</c> context mentions,
/// Windows/WSL workspace management, and multi-provider AI orchestration
/// (Google Gemini, GitHub Copilot SDK, Azure OpenAI, Anthropic Claude, and Manual Prompt).
/// </summary>
public sealed class CliMenuRunner
{
    private enum MenuState
    {
        Main,
        WizardOrchestrator,
        ProcessFiles,
        DraftItem,
        Settings,
        ThemeSelector,
        ProviderSelector,
        ModelSelector,
        PreviewOutcome,
        Exit
    }

    private readonly AsIsV1Workflow _workflow;
    private readonly WorkspacePathService _workspaceService;
    private readonly RuntimeEnvironmentService _runtimeEnvService;
    private readonly GeminiSecretProvider _secretProvider;
    private readonly AiModelOrchestrator _orchestrator;
    private readonly CliSettingsService _settingsService;
    private readonly WorkspaceDiagnosticService _diagnosticService;
    private readonly IClipboardService _clipboardService;
    private readonly RawDataCollector _collector = new();
    private IReadOnlyList<string> _lastMarkdownDocuments = [];
    private string? _lastSupplementalUserStory;
    private string? _lastSupplementalDescription;
    private MenuState _subMenuReturnState = MenuState.Main;
    private bool _wizardPreviewReturnToWizard;
    private string _lastSelectedSettingsKey = "1";

    public CliMenuRunner(
        AsIsV1Workflow workflow,
        WorkspacePathService? workspaceService = null,
        RuntimeEnvironmentService? runtimeEnvService = null,
        GeminiSecretProvider? secretProvider = null,
        AiModelOrchestrator? orchestrator = null,
        CliSettingsService? settingsService = null,
        WorkspaceDiagnosticService? diagnosticService = null,
        IClipboardService? clipboardService = null)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _workspaceService = workspaceService ?? new WorkspacePathService();
        _runtimeEnvService = runtimeEnvService ?? new RuntimeEnvironmentService();
        _secretProvider = secretProvider ?? new GeminiSecretProvider();
        _orchestrator = orchestrator ?? new AiModelOrchestrator();
        _settingsService = settingsService ?? new CliSettingsService();
        _clipboardService = clipboardService ?? new OsClipboardService();
        _diagnosticService = diagnosticService ?? new WorkspaceDiagnosticService(
            _workspaceService,
            _runtimeEnvService,
            _secretProvider,
            _settingsService,
            _orchestrator,
            _workflow.ProviderOrchestrator);
    }

    public int Run(WorkflowOptions baseOptions, TextReader input, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(baseOptions);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var baseWorkspace = _workspaceService.Resolve(baseOptions.WorkspaceRoot);
        _workspaceService.EnsureDirectoriesExist(baseWorkspace);

        var persistedSettings = _settingsService.Load(baseWorkspace);
        var resolvedTheme = !string.IsNullOrWhiteSpace(baseOptions.CliTheme)
            ? ConsoleColorWriter.ResolveTheme(baseOptions.CliTheme, persistedSettings.Theme)
            : persistedSettings.Theme;
        var resolvedProvider = !string.IsNullOrWhiteSpace(baseOptions.ProviderInput)
            ? _orchestrator.ResolveProvider(baseOptions.ProviderInput)
            : persistedSettings.PreferredProvider ?? AiProviderType.GoogleGemini;

        var currentSettings = persistedSettings with
        {
            JsonFileOut = baseOptions.JsonFileOut ?? persistedSettings.JsonFileOut,
            Theme = resolvedTheme,
            ModelName = baseOptions.ModelName ?? persistedSettings.ModelName,
            GeminiLive = baseOptions.GeminiLive ?? persistedSettings.GeminiLive ?? true,
            PreferredProvider = resolvedProvider,
            DefaultVerbosity = persistedSettings.DefaultVerbosity
        };
        var resolvedVerbosity = baseOptions.Verbosity != OutcomeVerbosity.Balanced
            ? baseOptions.Verbosity
            : currentSettings.DefaultVerbosity ?? OutcomeVerbosity.Balanced;

        var workspace = _workspaceService.ApplySettings(baseWorkspace, currentSettings);
        Directory.CreateDirectory(workspace.InputDirectory);
        Directory.CreateDirectory(workspace.OutputDirectory);

        var color = new ConsoleColorWriter(output, theme: currentSettings.Theme);

        // Ensure runtime options point to the resolved workspace out directory unless explicitly overridden
        var effectiveOutputDir = string.IsNullOrWhiteSpace(baseOptions.OutputDirectory) ||
                                 string.Equals(baseOptions.OutputDirectory, "output", StringComparison.OrdinalIgnoreCase)
            ? workspace.OutputDirectory
            : _workspaceService.ResolveCustomOrRelativePath(baseOptions.OutputDirectory, workspace.IsWindows);

        var sessionOptions = baseOptions with
        {
            OutputDirectory = effectiveOutputDir,
            JsonFileOut = currentSettings.JsonFileOut,
            CliTheme = ConsoleColorWriter.GetThemeCode(currentSettings.Theme),
            ModelName = currentSettings.ModelName,
            GeminiLive = currentSettings.GeminiLive,
            ProviderInput = RuntimeEnvironmentService.FormatProviderCode(resolvedProvider),
            Verbosity = resolvedVerbosity
        };

        var state = MenuState.Main;
        while (state != MenuState.Exit)
        {
            state = state switch
            {
                MenuState.Main => ShowMainMenu(baseWorkspace, ref workspace, ref sessionOptions, ref currentSettings, input, output, color),
                MenuState.WizardOrchestrator => ShowWizardOrchestratorMenu(baseWorkspace, ref workspace, ref sessionOptions, ref currentSettings, input, output, color),
                MenuState.ProcessFiles => ShowProcessFilesMenu(workspace, ref sessionOptions, ref currentSettings, baseWorkspace, input, output, color, _subMenuReturnState),
                MenuState.DraftItem => RunGuidedDraftWizard(workspace, ref sessionOptions, ref currentSettings, baseWorkspace, input, output, color, _subMenuReturnState),
                MenuState.Settings => ShowSettingsMenu(baseWorkspace, ref workspace, ref sessionOptions, ref currentSettings, input, color, _subMenuReturnState),
                MenuState.ThemeSelector => ShowThemeMenu(baseWorkspace, ref sessionOptions, ref currentSettings, input, color),
                MenuState.ProviderSelector => ShowProviderMenu(baseWorkspace, ref sessionOptions, ref currentSettings, input, color),
                MenuState.ModelSelector => ShowModelMenu(baseWorkspace, ref sessionOptions, ref currentSettings, input, color),
                MenuState.PreviewOutcome => ShowPreviewOutcomeMenu(workspace, ref sessionOptions, ref currentSettings, baseWorkspace, input, output, color, _subMenuReturnState, _wizardPreviewReturnToWizard),
                _ => MenuState.Exit
            };
        }

        color.WriteLine();
        color.WriteSuccess("Goodbye! Backlog artifacts are ready in: " + sessionOptions.OutputDirectory);
        return 0;
    }

    private MenuState ShowMainMenu(
        WorkspaceDirectories baseWorkspace,
        ref WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color)
    {
        var files = _workspaceService.GetInputFiles(workspace.InputDirectory);
        var themeCode = ConsoleColorWriter.GetThemeCode(currentSettings.Theme);
        var activeProvider = _diagnosticService.ResolveEffectiveProvider(sessionOptions, currentSettings);
        var providerCode = RuntimeEnvironmentService.FormatProviderCode(activeProvider);
        var modelLabel = currentSettings.ModelName ?? (activeProvider == AiProviderType.GitHubCopilot
            ? RuntimeEnvironmentService.DefaultCopilotModel
            : "auto");

        var inDisplay = WorkspacePathService.FormatCompactLabel(workspace.InputDirectory, baseWorkspace.InputDirectory, "./in");
        var outDisplay = WorkspacePathService.FormatCompactLabel(
            sessionOptions.OutputDirectory ?? workspace.OutputDirectory,
            baseWorkspace.OutputDirectory,
            "./out");
        var jsonStatus = currentSettings.JsonFileOut ? "ON" : "OFF";
        var statusLine = $"  In: {inDisplay} ({files.Count} files)   │   Out: {outDisplay}   │   Export: .md (JSON: {jsonStatus})";
        var boxInnerWidth = Math.Max(70, statusLine.Length + 2);

        const string titleText = "SHARP BACKLOG ASSISTANT";
        var leftPad = Math.Max(0, (boxInnerWidth - titleText.Length) / 2);
        var rightPad = Math.Max(0, boxInnerWidth - titleText.Length - leftPad);
        var titleLine = new string(' ', leftPad) + titleText + new string(' ', rightPad);

        var headerColor = color.GetHeaderColor();
        var borderColor = color.GetSubHeaderColor();

        color.WriteLine();
        color.WriteLineColored($"┌{new string('─', boxInnerWidth)}┐", borderColor);
        color.WriteColored("│", borderColor);
        color.WriteColored(titleLine, headerColor);
        color.WriteLineColored("│", borderColor);
        color.WriteLineColored($"├{new string('─', boxInnerWidth)}┤", borderColor);
        color.WriteColored("│", borderColor);
        color.WriteColored(statusLine.PadRight(boxInnerWidth), ConsoleColor.White);
        color.WriteLineColored("│", borderColor);
        color.WriteLineColored($"└{new string('─', boxInnerWidth)}┘", borderColor);

        color.WriteMuted($"  Provider: {providerCode} ({modelLabel}) │ Live: {(currentSettings.GeminiLive == true ? "ON" : "OFF")} │ Verbosity: {DescriptionFormatter.FormatVerbosityLabel(sessionOptions.Verbosity)} │ Theme: {themeCode}");
        color.WriteLine();

        color.WriteMenuOptionWithSlash("1", "Wizard Orchestrator", "All-in-one: Draft, Process, Preview, Copy & Settings", "/wizard");
        color.WriteMenuOptionWithSlash("2", "Draft Goal / Title", "Quick Type01 / Type02 / Both via Wizard", "/draft");
        color.WriteMenuOptionWithSlash("3", "Process Input Files", "Batch or single ./in file via Wizard", "/process");
        color.WriteMenuOptionWithSlash("4", "Preview & Copy (.md)", "View ./out/*.md, tweak verbosity & copy", "/preview");
        color.WriteMenuOptionWithSlash("5", "Workspace & Settings", "Provider, model, verbosity, JSON & status", "/settings");
        color.WriteMenuOptionWithSlash("0", "Exit", null, "/exit");
        color.WriteLine();
        color.WriteMuted("  Tip: [1] or [w] starts Wizard Orchestrator │ [0-5] [d/p/v/s/c/?/q] │ /commands │ @filename");
        color.WriteColored("backlog > ", color.GetOptionKeyColor());

        var choice = input.ReadLine();
        if (choice is null)
        {
            return MenuState.Exit;
        }

        var trimmed = choice.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return MenuState.Main;
        }

        if (trimmed.StartsWith('/'))
        {
            return ExecuteSlashCommand(
                trimmed,
                baseWorkspace,
                ref workspace,
                ref sessionOptions,
                ref currentSettings,
                input,
                output,
                color,
                returnState: MenuState.Main);
        }

        if (trimmed.Contains('@'))
        {
            ExecuteContextMentionInput(
                trimmed,
                workspace,
                ref sessionOptions,
                ref currentSettings,
                baseWorkspace,
                input,
                output,
                color);
            return MenuState.Main;
        }

        switch (trimmed.ToLowerInvariant())
        {
            case "0" or "q" or "quit" or "exit":
                return MenuState.Exit;
            case "1" or "w" or "wizard":
                return MenuState.WizardOrchestrator;
            case "2" or "d" or "draft":
                _subMenuReturnState = MenuState.Main;
                return MenuState.DraftItem;
            case "3" or "p" or "process":
                _subMenuReturnState = MenuState.Main;
                return MenuState.ProcessFiles;
            case "4" or "v" or "preview" or "view":
                _subMenuReturnState = MenuState.Main;
                _wizardPreviewReturnToWizard = false;
                return MenuState.PreviewOutcome;
            case "5" or "s" or "settings":
                _subMenuReturnState = MenuState.Main;
                return MenuState.Settings;
            case "c" or "copy":
                return ExecuteInlineCopyCommand([], input, color, returnState: MenuState.Main);
            case "?" or "h" or "help":
                WriteReplHelp(color);
                return MenuState.Main;
            default:
                return WarnAndStay(color, trimmed, "0-5, [w/d/p/v/s/c/q], /command, or @filename", MenuState.Main);
        }
    }

    private MenuState ShowWizardOrchestratorMenu(
        WorkspaceDirectories baseWorkspace,
        ref WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color)
    {
        var inputFiles = _workspaceService.GetInputFiles(workspace.InputDirectory);
        var savedFiles = GetSavedMarkdownFiles(sessionOptions.OutputDirectory ?? workspace.OutputDirectory);
        var activeProvider = _diagnosticService.ResolveEffectiveProvider(sessionOptions, currentSettings);
        var providerCode = RuntimeEnvironmentService.FormatProviderCode(activeProvider);
        var modelLabel = currentSettings.ModelName ?? (activeProvider == AiProviderType.GitHubCopilot
            ? RuntimeEnvironmentService.DefaultCopilotModel
            : "auto");

        color.WriteLine();
        color.WriteHeader("=== Wizard Orchestrator ===");
        color.WriteMuted($"  Workspace : In ./in ({inputFiles.Count} queued) │ Out ./out ({savedFiles.Count} saved) │ JSON: {(currentSettings.JsonFileOut ? "ON" : "OFF")}");
        color.WriteMuted($"  Pipeline  : {providerCode} ({modelLabel}) │ Live: {(currentSettings.GeminiLive == true ? "ON" : "OFF")} │ Verbosity: {DescriptionFormatter.FormatVerbosityLabel(sessionOptions.Verbosity)} │ Theme: {ConsoleColorWriter.GetThemeCode(currentSettings.Theme)}");
        if (_lastMarkdownDocuments.Count > 0)
        {
            var lastParsed = MarkdownOutcomeRenderer.ParseMarkdown(_lastMarkdownDocuments[0]);
            var lastTitle = !string.IsNullOrWhiteSpace(lastParsed.TitleLine)
                ? MarkdownOutcomeRenderer.StripMarkdownFormatting(lastParsed.TitleLine)
                : "Active outcome";
            color.WriteMuted($"  Session   : Active -> '{lastTitle}'");
        }

        color.WriteLine();
        color.WriteMenuOption("1", "Draft from Goal / Title", "Type 01 / 02 / Both -> Verbosity, Refine & Copy");
        color.WriteMenuOption("2", "Process Input Files (./in)", "All files, Single (Wizard Review), or Dry Run");
        color.WriteMenuOption("3", "Preview, Refine & Copy (.md)", "Open ./out/*.md -> Verbosity, Refine, Type & Copy");
        color.WriteMenuOption("4", "Quick Copy Last Outcome", "Simple [N], Markdown, Raw Table, Clean, or Field");
        color.WriteMenuOption("5", "Workspace & Settings", "Provider, model, verbosity, JSON, theme & files");
        color.WriteMenuOption("6", "Status & AI Diagnostics", "WSL2/Windows paths & AI Provider Matrix (/status)");
        color.WriteMenuOption("0", "Back to Main Menu", "Return to root prompt (or [q]uit)");
        color.WriteLine();
        color.WriteMuted("  Tip: [0-6] or [d/p/v/c/s/?/q] │ '@filename' to review file │ '/command' (/verbosity, /copy, /status)");
        color.WriteColored("wizard > ", color.GetOptionKeyColor());

        var choice = input.ReadLine();
        if (choice is null)
        {
            return MenuState.Exit;
        }

        var trimmed = choice.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return MenuState.WizardOrchestrator;
        }

        if (trimmed.StartsWith('/'))
        {
            return ExecuteSlashCommand(
                trimmed,
                baseWorkspace,
                ref workspace,
                ref sessionOptions,
                ref currentSettings,
                input,
                output,
                color,
                returnState: MenuState.WizardOrchestrator);
        }

        if (trimmed.Contains('@'))
        {
            var tokens = TokenizeCommandLine(trimmed);
            if (tokens.Count == 1 && tokens[0].StartsWith('@') && tokens[0].Length > 1)
            {
                var resolved = _workspaceService.TryResolveContextFile(tokens[0], workspace);
                if (resolved is not null)
                {
                    return ProcessSingleFileInWizard(
                        resolved,
                        workspace,
                        ref sessionOptions,
                        ref currentSettings,
                        baseWorkspace,
                        input,
                        output,
                        color,
                        returnState: MenuState.WizardOrchestrator);
                }

                WarnMissingContextFiles([tokens[0]], workspace, color);
                return MenuState.WizardOrchestrator;
            }

            ExecuteContextMentionInput(
                trimmed,
                workspace,
                ref sessionOptions,
                ref currentSettings,
                baseWorkspace,
                input,
                output,
                color);
            return MenuState.WizardOrchestrator;
        }

        switch (trimmed.ToLowerInvariant())
        {
            case "0" or "b" or "back":
                return MenuState.Main;
            case "q" or "quit" or "exit":
                return MenuState.Exit;
            case "1" or "d" or "draft":
                _subMenuReturnState = MenuState.WizardOrchestrator;
                return MenuState.DraftItem;
            case "2" or "p" or "process":
                _subMenuReturnState = MenuState.WizardOrchestrator;
                return MenuState.ProcessFiles;
            case "3" or "v" or "preview" or "view":
                _subMenuReturnState = MenuState.WizardOrchestrator;
                _wizardPreviewReturnToWizard = true;
                return MenuState.PreviewOutcome;
            case "4" or "c" or "copy":
                if (_lastMarkdownDocuments.Count == 0 && savedFiles.Count > 0)
                {
                    PreviewMarkdownFilePath(savedFiles[0], color, includeAlternatives: sessionOptions.ShowAlternatives);
                }

                return ExecuteInlineCopyCommand([], input, color, returnState: MenuState.WizardOrchestrator);
            case "5" or "s" or "settings":
                _subMenuReturnState = MenuState.WizardOrchestrator;
                return MenuState.Settings;
            case "6" or "status" or "doctor":
                color.WriteLine();
                _diagnosticService.RenderStatusTable(sessionOptions, output, currentSettings);
                return MenuState.WizardOrchestrator;
            case "?" or "h" or "help":
                WriteReplHelp(color);
                return MenuState.WizardOrchestrator;
            default:
                return WarnAndStay(color, trimmed, "0-6, [d/p/v/c/s/b/q], /command, or @filename", MenuState.WizardOrchestrator);
        }
    }

    private MenuState ExecuteSlashCommand(
        string commandLine,
        WorkspaceDirectories baseWorkspace,
        ref WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color,
        MenuState returnState = MenuState.Main)
    {
        var tokens = TokenizeCommandLine(commandLine);
        if (tokens.Count == 0)
        {
            return returnState;
        }

        var command = tokens[0].ToLowerInvariant();
        var args = tokens.Skip(1).ToArray();

        try
        {
            switch (command)
            {
                case "/exit" or "/quit" or "/q":
                    return MenuState.Exit;

                case "/help" or "/?":
                    WriteReplHelp(color);
                    return returnState;

                case "/clear" or "/cls":
                    if (ReferenceEquals(output, Console.Out) && !Console.IsOutputRedirected)
                    {
                        Console.Clear();
                    }

                    return returnState;

                case "/settings":
                    _subMenuReturnState = returnState;
                    return MenuState.Settings;

                case "/status" or "/doctor":
                    color.WriteLine();
                    _diagnosticService.RenderStatusTable(sessionOptions, output, currentSettings);
                    return returnState;

                case "/process" or "/run":
                {
                    if (args.Length == 0)
                    {
                        _subMenuReturnState = returnState;
                        return MenuState.ProcessFiles;
                    }

                    ExecuteInlineProcessCommand(args, workspace, ref sessionOptions, ref currentSettings, baseWorkspace, input, output, color);
                    return returnState;
                }

                case "/draft" or "/add":
                {
                    if (args.Length == 0)
                    {
                        _subMenuReturnState = returnState;
                        return MenuState.DraftItem;
                    }

                    ExecuteInlineDraftCommand(args, workspace, ref sessionOptions, ref currentSettings, baseWorkspace, input, output, color);
                    return returnState;
                }

                case "/wizard" or "/guided":
                    return MenuState.WizardOrchestrator;

                case "/theme":
                {
                    if (args.Length == 0)
                    {
                        _subMenuReturnState = returnState;
                        return MenuState.ThemeSelector;
                    }

                    var targetTheme = ConsoleColorWriter.ResolveTheme(args[0], currentSettings.Theme);
                    if (!IsValidThemeToken(args[0]))
                    {
                        color.WriteWarning($"  [!] Unknown theme '{args[0]}'. Choose agy, uv, or kubectl.");
                        return returnState;
                    }

                    currentSettings = currentSettings with { Theme = targetTheme };
                    color.Theme = targetTheme;
                    sessionOptions = sessionOptions with { CliTheme = ConsoleColorWriter.GetThemeCode(targetTheme) };
                    var savedPath = _settingsService.Save(baseWorkspace, currentSettings);
                    color.WriteSuccess($"CLI Theme is now {ConsoleColorWriter.FormatThemeDisplay(targetTheme)}. Saved settings to '{savedPath}'.");
                    return returnState;
                }

                case "/provider":
                {
                    if (args.Length == 0)
                    {
                        _subMenuReturnState = returnState;
                        return MenuState.ProviderSelector;
                    }

                    var resolved = _orchestrator.ResolveProvider(args[0]);
                    currentSettings = currentSettings with { PreferredProvider = resolved };
                    sessionOptions = sessionOptions with { ProviderInput = RuntimeEnvironmentService.FormatProviderCode(resolved) };
                    var savedPath = _settingsService.Save(baseWorkspace, currentSettings);
                    color.WriteSuccess($"Active AI Provider set to '{RuntimeEnvironmentService.FormatProviderCode(resolved)}'. Saved settings to '{savedPath}'.");
                    return returnState;
                }

                case "/json":
                {
                    var nextJson = args.Length == 0
                        ? !currentSettings.JsonFileOut
                        : ParseBooleanToggle(args[0], currentSettings.JsonFileOut);
                    currentSettings = currentSettings with { JsonFileOut = nextJson };
                    sessionOptions = sessionOptions with { JsonFileOut = nextJson };
                    var savedPath = _settingsService.Save(baseWorkspace, currentSettings);
                    color.WriteSuccess($"JSON Export is now {(nextJson ? "ON" : "OFF")}. Saved settings to '{savedPath}'.");
                    return returnState;
                }

                case "/live":
                {
                    var currentLive = currentSettings.GeminiLive ?? true;
                    var nextLive = args.Length == 0
                        ? !currentLive
                        : ParseBooleanToggle(args[0], currentLive);
                    currentSettings = currentSettings with { GeminiLive = nextLive };
                    sessionOptions = sessionOptions with { GeminiLive = nextLive };
                    var savedPath = _settingsService.Save(baseWorkspace, currentSettings);
                    color.WriteSuccess($"Live AI generation is now {(nextLive ? "ON" : "OFF")}. Saved settings to '{savedPath}'.");
                    return returnState;
                }

                case "/verbose":
                {
                    var nextVerbose = args.Length == 0
                        ? !sessionOptions.Verbose
                        : ParseBooleanToggle(args[0], sessionOptions.Verbose);
                    sessionOptions = sessionOptions with { Verbose = nextVerbose };
                    color.WriteSuccess($"Verbose pipeline diagnostics are now {(nextVerbose ? "ON" : "OFF")}.");
                    return returnState;
                }

                case "/verbosity":
                {
                    OutcomeVerbosity nextVerbosity;
                    if (args.Length == 0)
                    {
                        nextVerbosity = sessionOptions.Verbosity switch
                        {
                            OutcomeVerbosity.Minimalistic => OutcomeVerbosity.Balanced,
                            OutcomeVerbosity.Balanced => OutcomeVerbosity.Detailed,
                            _ => OutcomeVerbosity.Minimalistic
                        };
                    }
                    else
                    {
                        var parsed = DescriptionFormatter.ResolveVerbosity(args[0], (OutcomeVerbosity)(-1));
                        if (!Enum.IsDefined(parsed))
                        {
                            color.WriteWarning($"  [!] Unknown verbosity '{args[0]}'. Choose minimalistic (min), balanced (bal), or detailed (det).");
                            return returnState;
                        }

                        nextVerbosity = parsed;
                    }

                    currentSettings = currentSettings with { DefaultVerbosity = nextVerbosity };
                    sessionOptions = sessionOptions with { Verbosity = nextVerbosity };
                    var savedPath = _settingsService.Save(baseWorkspace, currentSettings);
                    color.WriteSuccess($"Default Outcome Verbosity is now {DescriptionFormatter.FormatVerbosityLabel(nextVerbosity)}. Saved settings to '{savedPath}'.");
                    return returnState;
                }

                case "/models":
                    ListActiveProviderModels(sessionOptions, currentSettings, input, color);
                    return returnState;

                case "/config":
                    ExecuteInlineConfigCommand(args, baseWorkspace, ref workspace, ref sessionOptions, ref currentSettings, color);
                    return returnState;

                case "/preview" or "/view" or "/show":
                {
                    if (args.Length == 0)
                    {
                        _subMenuReturnState = returnState;
                        _wizardPreviewReturnToWizard = returnState == MenuState.WizardOrchestrator;
                        return MenuState.PreviewOutcome;
                    }

                    ExecuteInlinePreviewCommand(args, workspace, sessionOptions, color);
                    return returnState;
                }

                case "/copy":
                    return ExecuteInlineCopyCommand(args, input, color, returnState: returnState);

                default:
                    color.WriteWarning($"  [!] Unknown slash command '{command}'. Type /help for available commands.");
                    return returnState;
            }
        }
        catch (Exception ex)
        {
            TerminalUx.WriteActionableError(color, ex);
            return returnState;
        }
    }

    private void ExecuteInlineProcessCommand(
        string[] args,
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        WorkspaceDirectories baseWorkspace,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color)
    {
        var dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase) || a.Equals("--preview", StringComparison.OrdinalIgnoreCase));
        var jsonFlag = ParseOptionalJsonFlag(args);
        var typeInput = GetInlineFlagValue(args, "--type", "-t") ?? sessionOptions.BacklogTypeInput ?? "Type01";
        var positional = ExtractPositionalTokens(args, "--type", "-t", "--json");
        var target = positional.Count > 0 ? positional[0] : "all";

        var resolvedTarget = _workspaceService.ResolveInputTarget(target, workspace);
        IReadOnlyList<string> filesToProcess = Directory.Exists(resolvedTarget)
            ? _workspaceService.GetInputFiles(resolvedTarget)
            : [resolvedTarget];

        if (filesToProcess.Count == 0)
        {
            color.WriteWarning($"  [!] No .txt or .md files found in '{resolvedTarget}'.");
            return;
        }

        var effectiveOptions = sessionOptions with
        {
            JsonFileOut = jsonFlag ?? sessionOptions.JsonFileOut,
            BacklogTypeInput = typeInput,
            CombinedOutput = string.Equals(typeInput, "both", StringComparison.OrdinalIgnoreCase)
        };

        ProcessFiles(filesToProcess, dryRun, baseWorkspace, ref effectiveOptions, ref currentSettings, input, output, color);
        sessionOptions = effectiveOptions with
        {
            BacklogTypeInput = sessionOptions.BacklogTypeInput,
            JsonFileOut = sessionOptions.JsonFileOut
        };
    }

    private void ExecuteInlineDraftCommand(
        string[] args,
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        WorkspaceDirectories baseWorkspace,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color)
    {
        var preview = args.Any(a => a.Equals("--preview", StringComparison.OrdinalIgnoreCase) || a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
        var webSearch = args.Any(a => a.Equals("--search", StringComparison.OrdinalIgnoreCase) || a.Equals("--web", StringComparison.OrdinalIgnoreCase));
        var showAlternatives = args.Any(a => a.Equals("--alternatives", StringComparison.OrdinalIgnoreCase) || a.Equals("--alts", StringComparison.OrdinalIgnoreCase));
        var jsonFlag = ParseOptionalJsonFlag(args);
        var typeInput = GetInlineFlagValue(args, "--type", "-t") ?? "Type01";
        var explicitDetails = GetInlineFlagValue(args, "--details", "-d", "--context", "-c");
        var positional = ExtractPositionalTokens(args, "--type", "-t", "--json", "--details", "-d", "--context", "-c");

        if (positional.Count == 0)
        {
            color.WriteWarning("  [!] Provide a goal or title, e.g. /draft \"Implement OAuth2 PKCE\" --details \"Include token rotation\" --type 01");
            return;
        }

        var rawGoalExpression = string.Join(" ", positional);
        var parsedInline = RawDataCollector.ParseInlineGoalModifiers(rawGoalExpression);
        if (string.IsNullOrWhiteSpace(parsedInline.GoalExpression))
        {
            color.WriteWarning("  [!] Goal or Title cannot be empty.");
            return;
        }

        var effectiveDetails = MergeDetails(parsedInline.InlineDetails, explicitDetails);
        var effectiveSearch = webSearch || parsedInline.EnableWebSearch;

        var enrichedGoal = ResolveInlineContextMentions(parsedInline.GoalExpression, workspace, color, out var missingMentions);
        if (missingMentions.Count > 0)
        {
            WarnMissingContextFiles(missingMentions, workspace, color);
            return;
        }

        var combinedOutput = string.Equals(typeInput, "both", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(typeInput, "all", StringComparison.OrdinalIgnoreCase);

        var ticket = _workflow.ExecuteQuickGoal(
            goalOrTitle: enrichedGoal,
            backlogTypeInput: typeInput,
            baseOptions: sessionOptions with
            {
                CombinedOutput = combinedOutput,
                DryRun = preview || sessionOptions.DryRun,
                JsonFileOut = jsonFlag ?? sessionOptions.JsonFileOut,
                OutputDirectory = preview ? null : sessionOptions.OutputDirectory,
                GoalContext = effectiveDetails,
                WebSearchGrounding = effectiveSearch,
                ShowAlternatives = showAlternatives || sessionOptions.ShowAlternatives
            },
            input: input,
            output: output);

        CacheResolvedWorkflowOptions(ref sessionOptions, ref currentSettings, baseWorkspace);
        var label = combinedOutput ? "Type01 and Type02 items" : $"{ticket.BacklogType} item";
        color.WriteSuccess(preview
            ? $"[Preview] {label} '{ticket.Title}' generated. No files written."
            : $"[✓] Ready-to-add {label} '{ticket.Title}' saved to '{sessionOptions.OutputDirectory}'.");
    }

    private void ExecuteContextMentionInput(
        string rawInput,
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        WorkspaceDirectories baseWorkspace,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color)
    {
        var parsedInline = RawDataCollector.ParseInlineGoalModifiers(rawInput);
        var tokens = TokenizeCommandLine(parsedInline.GoalExpression);
        var mentionTokens = tokens.Where(t => t.StartsWith('@')).ToList();
        if (mentionTokens.Count == 1 && (mentionTokens[0] == "@" || mentionTokens[0] == "@?"))
        {
            ListAvailableContextFiles(workspace, color);
            return;
        }

        var dryRun = tokens.Any(t => t.Equals("--dry-run", StringComparison.OrdinalIgnoreCase) || t.Equals("--preview", StringComparison.OrdinalIgnoreCase));
        var typeInput = GetInlineFlagValue([.. tokens], "--type", "-t");
        var explicitDetails = GetInlineFlagValue([.. tokens], "--details", "-d", "--context", "-c");
        var effectiveDetails = MergeDetails(parsedInline.InlineDetails, explicitDetails);
        var nonFlagTextTokens = new List<string>();
        var resolvedFiles = new List<string>();
        var missingFiles = new List<string>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token is "--type" or "-t" or "--details" or "-d" or "--context" or "-c")
            {
                i++;
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (token.StartsWith('@'))
            {
                var filePath = _workspaceService.TryResolveContextFile(token, workspace);
                if (filePath is not null)
                {
                    resolvedFiles.Add(filePath);
                }
                else
                {
                    missingFiles.Add(token);
                }
            }
            else
            {
                nonFlagTextTokens.Add(token);
            }
        }

        if (missingFiles.Count > 0)
        {
            WarnMissingContextFiles(missingFiles, workspace, color);
            return;
        }

        if (resolvedFiles.Count == 0)
        {
            ListAvailableContextFiles(workspace, color);
            return;
        }

        try
        {
            foreach (var file in resolvedFiles)
            {
                var size = new FileInfo(file).Length;
                color.WriteSuccess($"  ✓ Attached context '@{Path.GetFileName(file)}' ({size:N0} bytes)");
            }

            if (nonFlagTextTokens.Count == 0)
            {
                var runOptions = sessionOptions with
                {
                    BacklogTypeInput = typeInput ?? sessionOptions.BacklogTypeInput ?? "Type01",
                    CombinedOutput = string.Equals(typeInput, "both", StringComparison.OrdinalIgnoreCase)
                };
                ProcessFiles(resolvedFiles, dryRun, baseWorkspace, ref runOptions, ref currentSettings, input, output, color);
                return;
            }

            var goalInstruction = string.Join(" ", nonFlagTextTokens);
            var combinedRaw = BuildGoalWithAttachedFiles(goalInstruction, resolvedFiles);
            var effectiveType = typeInput ?? "Type01";
            var combinedOutput = string.Equals(effectiveType, "both", StringComparison.OrdinalIgnoreCase);

            var ticket = _workflow.ExecuteQuickGoal(
                goalOrTitle: combinedRaw,
                backlogTypeInput: effectiveType,
                baseOptions: sessionOptions with
                {
                    CombinedOutput = combinedOutput,
                    DryRun = dryRun || sessionOptions.DryRun,
                    OutputDirectory = dryRun ? null : sessionOptions.OutputDirectory,
                    GoalContext = effectiveDetails,
                    WebSearchGrounding = parsedInline.EnableWebSearch
                },
                input: input,
                output: output);

            CacheResolvedWorkflowOptions(ref sessionOptions, ref currentSettings, baseWorkspace);
            color.WriteSuccess(dryRun
                ? $"[Preview] '{ticket.Title}' generated from @context. No files written."
                : $"[✓] Completed '{ticket.Title}' from @context in '{sessionOptions.OutputDirectory}'.");
        }
        catch (Exception ex)
        {
            TerminalUx.WriteActionableError(color, ex);
        }
    }

    private void ExecuteInlineConfigCommand(
        string[] args,
        WorkspaceDirectories baseWorkspace,
        ref WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        ConsoleColorWriter color)
    {
        if (args.Length == 0 || args.Contains("--show", StringComparer.OrdinalIgnoreCase))
        {
            _diagnosticService.RenderConfigSummary(baseWorkspace, workspace, currentSettings, color);
            return;
        }

        var updated = currentSettings;
        if (args.Contains("--reset", StringComparer.OrdinalIgnoreCase))
        {
            updated = new CliSettings();
        }
        else
        {
            var jsonArg = GetInlineFlagValue(args, "--json");
            if (!string.IsNullOrWhiteSpace(jsonArg))
            {
                updated = updated with { JsonFileOut = ParseBooleanToggle(jsonArg, updated.JsonFileOut) };
            }

            var inArg = GetInlineFlagValue(args, "--in");
            if (!string.IsNullOrWhiteSpace(inArg))
            {
                updated = updated with
                {
                    CustomInputDirectory = string.Equals(inArg, "default", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : _workspaceService.ResolveCustomOrRelativePath(inArg, baseWorkspace.IsWindows)
                };
            }

            var outArg = GetInlineFlagValue(args, "--out");
            if (!string.IsNullOrWhiteSpace(outArg))
            {
                updated = updated with
                {
                    CustomOutputDirectory = string.Equals(outArg, "default", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : _workspaceService.ResolveCustomOrRelativePath(outArg, baseWorkspace.IsWindows)
                };
            }

            var themeArg = GetInlineFlagValue(args, "--theme");
            if (!string.IsNullOrWhiteSpace(themeArg))
            {
                updated = updated with { Theme = ConsoleColorWriter.ResolveTheme(themeArg, updated.Theme) };
            }

            var liveArg = GetInlineFlagValue(args, "--live");
            if (!string.IsNullOrWhiteSpace(liveArg))
            {
                updated = updated with { GeminiLive = ParseBooleanToggle(liveArg, updated.GeminiLive ?? true) };
            }

            var providerArg = GetInlineFlagValue(args, "--provider");
            if (!string.IsNullOrWhiteSpace(providerArg))
            {
                updated = updated with { PreferredProvider = _orchestrator.ResolveProvider(providerArg) };
            }

            var modelArg = GetInlineFlagValue(args, "--model", "-m");
            if (!string.IsNullOrWhiteSpace(modelArg))
            {
                updated = updated with
                {
                    ModelName = string.Equals(modelArg, "auto", StringComparison.OrdinalIgnoreCase) ? null : modelArg.Trim()
                };
            }

            var verbosityArg = GetInlineFlagValue(args, "--verbosity");
            if (!string.IsNullOrWhiteSpace(verbosityArg))
            {
                updated = updated with
                {
                    DefaultVerbosity = DescriptionFormatter.ResolveVerbosity(verbosityArg, updated.DefaultVerbosity ?? OutcomeVerbosity.Balanced)
                };
            }
        }

        currentSettings = updated;
        workspace = _workspaceService.ApplySettings(baseWorkspace, currentSettings);
        Directory.CreateDirectory(workspace.InputDirectory);
        Directory.CreateDirectory(workspace.OutputDirectory);

        color.Theme = currentSettings.Theme;
        sessionOptions = sessionOptions with
        {
            JsonFileOut = currentSettings.JsonFileOut,
            OutputDirectory = workspace.OutputDirectory,
            CliTheme = ConsoleColorWriter.GetThemeCode(currentSettings.Theme),
            GeminiLive = currentSettings.GeminiLive,
            ModelName = currentSettings.ModelName,
            ProviderInput = RuntimeEnvironmentService.FormatProviderCode(
                currentSettings.PreferredProvider ?? AiProviderType.GoogleGemini),
            Verbosity = currentSettings.DefaultVerbosity ?? OutcomeVerbosity.Balanced
        };

        var savedPath = _settingsService.Save(baseWorkspace, currentSettings);
        color.WriteSuccess($"✓ Configuration updated and saved to '{savedPath}'.");
        _diagnosticService.RenderConfigSummary(baseWorkspace, workspace, currentSettings, color);
    }

    private MenuState ShowProcessFilesMenu(
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        WorkspaceDirectories baseWorkspace,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color,
        MenuState returnState = MenuState.Main)
    {
        _workspaceService.EnsureDirectoriesExist(workspace);
        var files = _workspaceService.GetInputFiles(workspace.InputDirectory);

        color.WriteLine();
        color.WriteHeader("=== Process Input Files ===");
        color.WriteMuted($"Queued files in {workspace.InputDirectory}: {files.Count}");
        for (var index = 0; index < files.Count; index++)
        {
            var size = new FileInfo(files[index]).Length;
            color.WriteLine($"  {Path.GetFileName(files[index])} ({size:N0} bytes)");
        }

        if (files.Count == 0)
        {
            color.WriteMuted("  No .txt or .md files are queued.");
        }

        color.WriteLine();
        color.WriteMenuOption("1", "Process All Files", "Batch run all queued ./in files");
        color.WriteMenuOption("2", "Select Single File (Wizard Review)", "Pick one file -> Preview, Verbosity, Refine & Copy");
        color.WriteMenuOption("3", "Dry Run (Preview Only)", "Preview all files without writing");
        color.WriteMenuOption("0", returnState == MenuState.WizardOrchestrator ? "Back to Wizard Orchestrator" : "Back to Main Menu");
        color.WriteColored("Select an option (0-3, b=back, q=quit, or @filename): ", ConsoleColor.Yellow);

        var choice = input.ReadLine();
        if (choice is null)
        {
            return MenuState.Exit;
        }

        var trimmed = choice.Trim();
        var normalized = trimmed.ToLowerInvariant();
        if (normalized is "0" or "b" or "back" or "/back")
        {
            return returnState;
        }

        if (normalized is "q" or "quit" or "/exit" or "/quit")
        {
            return MenuState.Exit;
        }

        if (trimmed.StartsWith('@'))
        {
            var resolved = _workspaceService.TryResolveContextFile(trimmed, workspace);
            if (resolved is not null)
            {
                return ProcessSingleFileInWizard(
                    resolved,
                    workspace,
                    ref sessionOptions,
                    ref currentSettings,
                    baseWorkspace,
                    input,
                    output,
                    color,
                    returnState: MenuState.ProcessFiles);
            }

            WarnMissingContextFiles([trimmed], workspace, color);
            return MenuState.ProcessFiles;
        }

        switch (normalized)
        {
            case "1" or "a" or "all":
                ProcessFiles(files, false, baseWorkspace, ref sessionOptions, ref currentSettings, input, output, color);
                return MenuState.ProcessFiles;
            case "2" or "s" or "single":
                var selectedFile = SelectSingleFile(files, input, color);
                if (selectedFile is not null)
                {
                    return ProcessSingleFileInWizard(
                        selectedFile,
                        workspace,
                        ref sessionOptions,
                        ref currentSettings,
                        baseWorkspace,
                        input,
                        output,
                        color,
                        returnState: MenuState.ProcessFiles);
                }

                return MenuState.ProcessFiles;
            case "3" or "p" or "preview" or "dry":
                ProcessFiles(files, true, baseWorkspace, ref sessionOptions, ref currentSettings, input, output, color);
                return MenuState.ProcessFiles;
            default:
                return WarnAndStay(color, choice, "0-3, b=back, q=quit, or @filename", MenuState.ProcessFiles);
        }
    }

    private string? SelectSingleFile(IReadOnlyList<string> files, TextReader input, ConsoleColorWriter color)
    {
        if (files.Count == 0)
        {
            color.WriteWarning("  [!] There are no files to select.");
            return null;
        }

        for (var index = 0; index < files.Count; index++)
        {
            color.WriteMenuOption((index + 1).ToString(), Path.GetFileName(files[index]));
        }

        color.WriteMenuOption("0", "Cancel");
        color.WriteColored($"Select a file (1-{files.Count}, or 0 to cancel): ", ConsoleColor.Yellow);
        var selection = input.ReadLine()?.Trim();
        if (selection == "0")
        {
            return null;
        }

        if (int.TryParse(selection, out var fileIndex) && fileIndex >= 1 && fileIndex <= files.Count)
        {
            return files[fileIndex - 1];
        }

        color.WriteWarning($"  [!] Invalid file selection '{selection}'.");
        return null;
    }

    private void ProcessFiles(
        IReadOnlyList<string> files,
        bool dryRun,
        WorkspaceDirectories baseWorkspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color)
    {
        if (files.Count == 0)
        {
            color.WriteWarning("  [!] No input files to process.");
            return;
        }

        var batchStopwatch = Stopwatch.StartNew();
        foreach (var filePath in files)
        {
            var previewOnly = dryRun || sessionOptions.DryRun;
            color.WriteLine();
            color.WriteSubHeader($">>> {(previewOnly ? "Previewing" : "Processing")} '{Path.GetFileName(filePath)}'...");
            var fileOptions = sessionOptions with
            {
                InputFilePath = filePath,
                GoalTitle = null,
                OutputDirectory = previewOnly ? null : sessionOptions.OutputDirectory,
                DryRun = previewOnly
            };

            var ticket = _workflow.Execute(fileOptions, input, output);
            CacheResolvedWorkflowOptions(ref sessionOptions, ref currentSettings, baseWorkspace);
            if (previewOnly)
            {
                color.WriteSuccess($"[Preview] '{ticket.Title}' ({ticket.BacklogType} -> {ticket.JiraStage}). No files written.");
            }
            else
            {
                color.WriteSuccess($"[✓] Completed '{ticket.Title}' ({ticket.BacklogType} -> {ticket.JiraStage}).");
            }
        }

        batchStopwatch.Stop();
        if (files.Count > 1)
        {
            TerminalUx.WriteStepDone(color, "Completed", $"batch of {files.Count} files", batchStopwatch.Elapsed);
        }
    }

    private static void WriteGoalTypingHint(ConsoleColorWriter color, string backlogType, bool combinedOutput)
    {
        if (combinedOutput)
        {
            color.WriteMuted("  Tip [Both]: Example \"Migrate Session Store to Redis | Zero-downtime cutover\" (generates Type 01 + Type 02 + 3 Alternatives).");
        }
        else if (backlogType.Equals("Type02", StringComparison.OrdinalIgnoreCase))
        {
            color.WriteMuted("  Tip [Type 02]: Bare goal OK (e.g. \"check Geocaching 2026 hiding movie theme\") — expands Title, concise Description & 3 Alternatives.");
        }
        else
        {
            color.WriteMuted("  Tip [Type 01]: Focus on <Verb> <Capability> for <User Benefit> (e.g. \"Implement OAuth2 PKCE Login | mobile users\").");
        }
    }

    private MenuState RunGuidedDraftWizard(
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        WorkspaceDirectories baseWorkspace,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color,
        MenuState returnState = MenuState.WizardOrchestrator)
    {
        color.WriteLine();
        color.WriteSubHeader("--- Guided Draft & Refine Wizard --- (Preview & Tweak)");
        color.WriteMuted("  Outcome Verbosity: [1/min] Minimalistic (<=25w)  │  [2/bal] Balanced (<=57w, default)  │  [3/det] Detailed (<=95w)");
        color.WriteColored("Format ([1] Type01 Story, [2] Type02 Schedule, [3] Both; optional 'min|bal|det') [default: 1]: ", ConsoleColor.Yellow);
        var formatChoice = input.ReadLine();
        if (formatChoice is null)
        {
            return MenuState.Exit;
        }

        var normalizedFormatChoice = formatChoice.Trim().ToLowerInvariant();
        if (normalizedFormatChoice is "0" or "b" or "back" or "/back")
        {
            return returnState;
        }

        if (normalizedFormatChoice is "q" or "quit" or "/exit" or "/quit")
        {
            return MenuState.Exit;
        }

        var (backlogType, combinedOutput, verbosity) = ParseGuidedFormatAndVerbosity(formatChoice, sessionOptions.Verbosity);

        WriteGoalTypingHint(color, backlogType, combinedOutput);
        color.WriteColored("Enter Goal or Title (or @filename context): ", ConsoleColor.Yellow);
        var goalLine = input.ReadLine();
        if (goalLine is null)
        {
            return MenuState.Exit;
        }

        var parsedGoal = RawDataCollector.ParseInlineGoalModifiers(goalLine.Trim());
        if (string.IsNullOrWhiteSpace(parsedGoal.GoalExpression))
        {
            color.WriteWarning("  [!] Goal or Title cannot be empty.");
            return returnState;
        }

        var enrichedGoal = ResolveInlineContextMentions(parsedGoal.GoalExpression, workspace, color, out var missingMentions);
        if (missingMentions.Count > 0)
        {
            WarnMissingContextFiles(missingMentions, workspace, color);
            return returnState;
        }

        string? extraDetailsLine = null;
        if (string.IsNullOrWhiteSpace(parsedGoal.InlineDetails))
        {
            color.WriteColored("Additional details / required info to include (or 'min'/'bal'/'det' verbosity; Enter to skip): ", ConsoleColor.Yellow);
            extraDetailsLine = input.ReadLine();
            if (extraDetailsLine is null)
            {
                return MenuState.Exit;
            }

            if (TryExtractStandaloneVerbosityToken(extraDetailsLine, out var extractedVerbosity))
            {
                verbosity = extractedVerbosity;
                extraDetailsLine = string.Empty;
            }
        }

        var details = MergeDetails(parsedGoal.InlineDetails, extraDetailsLine);
        var webSearch = parsedGoal.EnableWebSearch;
        var activeProvider = _diagnosticService.ResolveEffectiveProvider(sessionOptions, currentSettings);
        var liveEnabled = currentSettings.GeminiLive ?? (activeProvider == AiProviderType.GitHubCopilot);

        if (!webSearch && activeProvider == AiProviderType.GoogleGemini && liveEnabled)
        {
            color.WriteColored("Run live Google Search grounding first? [y/N]: ", ConsoleColor.Yellow);
            var searchChoice = input.ReadLine();
            if (searchChoice is null)
            {
                return MenuState.Exit;
            }

            webSearch = searchChoice.Trim().ToLowerInvariant() is "y" or "yes" or "1" or "true";
        }

        return RunReviewDecisionLoop(
            enrichedGoal: enrichedGoal,
            initialFilePath: null,
            initialDetails: details,
            initialBacklogType: backlogType,
            initialCombinedOutput: combinedOutput,
            initialVerbosity: verbosity,
            initialWebSearch: webSearch,
            initialOutcome: null,
            initialTickets: null,
            workspace: workspace,
            sessionOptions: ref sessionOptions,
            currentSettings: ref currentSettings,
            baseWorkspace: baseWorkspace,
            input: input,
            output: output,
            color: color,
            returnState: returnState);
    }

    private MenuState ProcessSingleFileInWizard(
        string filePath,
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        WorkspaceDirectories baseWorkspace,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color,
        MenuState returnState)
    {
        color.WriteLine();
        color.WriteSubHeader($"--- Wizard Orchestrator: Reviewing '{Path.GetFileName(filePath)}' ---");
        color.WriteMuted("  Outcome Verbosity: [1/min] Minimalistic (<=25w)  │  [2/bal] Balanced (<=57w, default)  │  [3/det] Detailed (<=95w)");
        color.WriteColored("Format ([1] Type01 Story, [2] Type02 Schedule, [3] Both; optional 'min|bal|det') [default: 1]: ", ConsoleColor.Yellow);
        var formatChoice = input.ReadLine();
        if (formatChoice is null)
        {
            return MenuState.Exit;
        }

        var normalizedFormatChoice = formatChoice.Trim().ToLowerInvariant();
        if (normalizedFormatChoice is "0" or "b" or "back" or "/back")
        {
            return returnState;
        }

        if (normalizedFormatChoice is "q" or "quit" or "/exit" or "/quit")
        {
            return MenuState.Exit;
        }

        var (backlogType, combinedOutput, verbosity) = ParseGuidedFormatAndVerbosity(formatChoice, sessionOptions.Verbosity);
        var fileContent = _collector.CollectFromFile(filePath);

        return RunReviewDecisionLoop(
            enrichedGoal: null,
            initialFilePath: filePath,
            initialDetails: fileContent,
            initialBacklogType: backlogType,
            initialCombinedOutput: combinedOutput,
            initialVerbosity: verbosity,
            initialWebSearch: false,
            initialOutcome: null,
            initialTickets: null,
            workspace: workspace,
            sessionOptions: ref sessionOptions,
            currentSettings: ref currentSettings,
            baseWorkspace: baseWorkspace,
            input: input,
            output: output,
            color: color,
            returnState: returnState);
    }

    private MenuState RunReviewDecisionLoop(
        string? enrichedGoal,
        string? initialFilePath,
        string? initialDetails,
        string initialBacklogType,
        bool initialCombinedOutput,
        OutcomeVerbosity initialVerbosity,
        bool initialWebSearch,
        ProcessedOutcome? initialOutcome,
        IReadOnlyList<JiraBacklogTicket>? initialTickets,
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        WorkspaceDirectories baseWorkspace,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color,
        MenuState returnState)
    {
        var backlogType = initialBacklogType;
        var combinedOutput = initialCombinedOutput;
        var verbosity = initialVerbosity;
        string? previousDraft = null;
        string? refinementFeedback = null;
        string? preservedWebFindings = null;
        string? baseFullDescription = initialOutcome?.Description;
        var runWebSearchThisPass = initialWebSearch;
        IReadOnlyList<JiraBacklogTicket>? latestTickets = initialTickets;
        ProcessedOutcome? latestOutcome = initialOutcome;
        var usePreloadedOutcome = initialOutcome is not null && initialTickets is not null;

        while (true)
        {
            if (usePreloadedOutcome)
            {
                usePreloadedOutcome = false;
                CacheWizardTickets(latestOutcome!, latestTickets!, combinedOutput);
            }
            else
            {
                var isRefinementOfFileOrSaved = !string.IsNullOrWhiteSpace(previousDraft) && string.IsNullOrWhiteSpace(enrichedGoal);
                var effectiveGoalTitle = !string.IsNullOrWhiteSpace(enrichedGoal)
                    ? enrichedGoal
                    : isRefinementOfFileOrSaved
                        ? latestOutcome!.Title
                        : null;

                var draftOptions = sessionOptions with
                {
                    InputFilePath = !string.IsNullOrWhiteSpace(initialFilePath) ? initialFilePath : sessionOptions.InputFilePath,
                    BacklogTypeInput = backlogType,
                    CombinedOutput = combinedOutput,
                    GoalTitle = effectiveGoalTitle,
                    GoalContext = effectiveGoalTitle is not null ? initialDetails : null,
                    WebSearchGrounding = runWebSearchThisPass,
                    PreviousDraftContext = previousDraft,
                    RefinementFeedback = refinementFeedback,
                    Verbosity = verbosity,
                    ShowAlternatives = verbosity != OutcomeVerbosity.Minimalistic
                };

                _workflow.ExecuteDraftPreview(draftOptions, input, output, out var outcome);
                baseFullDescription = outcome.Description;
                latestOutcome = outcome with
                {
                    Description = DescriptionFormatter.FormatWithVerbosity(baseFullDescription, verbosity)
                };
                latestTickets = _workflow.ReformatOutcome(latestOutcome, backlogType, combinedOutput, verbosity);
                if (!string.IsNullOrWhiteSpace(_workflow.LastWebResearchFindings))
                {
                    preservedWebFindings = _workflow.LastWebResearchFindings;
                }

                CacheResolvedWorkflowOptions(ref sessionOptions, ref currentSettings, baseWorkspace);
                CacheWizardTickets(latestOutcome, latestTickets, combinedOutput);
                runWebSearchThisPass = false;
            }

            while (true)
            {
                color.WriteLine();
                color.WriteHeader($"[4/5 Review Decision] (Verbosity: {DescriptionFormatter.FormatVerbosityLabel(verbosity)})");
                var hasAlternatives = latestOutcome?.Alternatives is { Count: > 0 };
                if (hasAlternatives)
                {
                    color.WriteMenuOption($"1-{latestOutcome!.Alternatives!.Count}", $"Adopt Alternative Title & Description (#1..#{latestOutcome.Alternatives.Count})");
                }

                color.WriteMenuOption("A", $"Accept & Save to '{sessionOptions.OutputDirectory}' (default)");
                color.WriteMenuOption("N", "Copy Simple Text (no format: Title / --- / Description)");
                color.WriteMenuOption("M", "Copy Markdown to clipboard");
                color.WriteMenuOption("W", "Copy Raw Text (table) to clipboard");
                color.WriteMenuOption("S", "Select & Copy field / Clean Text (Title, Story, Desc, AC)");
                color.WriteMenuOption("V", $"Outcome Verbosity [{DescriptionFormatter.FormatVerbosityLabel(verbosity)}: Minimalistic / Balanced / Detailed]");
                color.WriteMenuOption("R", "Refine Title / Description with feedback");
                color.WriteMenuOption("T", "Switch format (Type01 / Type02 / Both)");
                color.WriteMenuOption("P", "Preview only (finish without writing files)");
                color.WriteMenuOption("C", "Cancel / Back");
                var actionPrompt = hasAlternatives
                    ? $"Choose action (1-{latestOutcome!.Alternatives!.Count}/A/N/M/W/S/V/R/T/P/C) [A]: "
                    : "Choose action (A/N/M/W/S/V/R/T/P/C) [A]: ";
                color.WriteColored(actionPrompt, ConsoleColor.Yellow);

                var action = input.ReadLine();
                if (action is null)
                {
                    return MenuState.Exit;
                }

                var normalizedAction = action.Trim().ToLowerInvariant();
                if (normalizedAction is "q" or "quit" or "/exit" or "/quit")
                {
                    return MenuState.Exit;
                }

                if (int.TryParse(normalizedAction, out var altChoice) &&
                    latestOutcome?.Alternatives is { Count: > 0 } availableAlternatives &&
                    altChoice >= 1 &&
                    altChoice <= availableAlternatives.Count)
                {
                    var chosenAlt = availableAlternatives[altChoice - 1];
                    latestOutcome = ApplyAlternativeToOutcome(latestOutcome, chosenAlt, verbosity);
                    baseFullDescription = latestOutcome.Description;
                    latestTickets = _workflow.ReformatOutcome(latestOutcome, backlogType, combinedOutput, verbosity);
                    color.WriteSuccess($"  ✓ Adopted Alternative #{altChoice} ({chosenAlt.Angle}): '{chosenAlt.Title}'");
                    RenderAndCacheWizardTickets(latestOutcome, latestTickets, combinedOutput, color, verbosity != OutcomeVerbosity.Minimalistic);

                    continue;
                }

                if (normalizedAction is "n" or "simple" or "noformat" or "no-format")
                {
                    CacheWizardTickets(latestOutcome!, latestTickets!, combinedOutput);
                    MarkdownOutcomeRenderer.CopySimpleUnformattedToClipboard(
                        _clipboardService,
                        _lastMarkdownDocuments,
                        _lastSupplementalUserStory,
                        _lastSupplementalDescription,
                        color);
                    continue;
                }

                if (normalizedAction is "m" or "md" or "markdown")
                {
                    CopyCurrentOutcomeToClipboard(asMarkdown: true, color);
                    continue;
                }

                if (normalizedAction is "w" or "raw" or "text")
                {
                    CopyCurrentOutcomeToClipboard(asMarkdown: false, color);
                    continue;
                }

                if (normalizedAction is "s" or "select")
                {
                    CacheWizardTickets(latestOutcome!, latestTickets!, combinedOutput);
                    var selectResult = PromptCopyFormatLoop(input, color, returnState: MenuState.Main, allowWizardPromotion: false);
                    if (selectResult == MenuState.Exit)
                    {
                        return MenuState.Exit;
                    }

                    continue;
                }

                if (normalizedAction is "v" or "verbosity" or "min" or "minimal" or "minimalistic" or "bal" or "balanced" or "det" or "detailed")
                {
                    OutcomeVerbosity nextVerbosity;
                    if (normalizedAction is "v" or "verbosity")
                    {
                        color.WriteLine();
                        color.WriteSubHeader("Select Outcome Verbosity:");
                        color.WriteMenuOption("1", "Minimalistic (<= 25 words)", "1-line objective + at most 2 action bullets (no extras)");
                        color.WriteMenuOption("2", "Balanced (<= 57 words)", "Standard Objective + 3 action bullets (default)");
                        color.WriteMenuOption("3", "Detailed (<= 95 words)", "Full Objective, Context/Web insights, bullets & 3 Alternatives");
                        color.WriteColored("Choose verbosity (1=Minimalistic, 2=Balanced, 3=Detailed) [2]: ", ConsoleColor.Yellow);
                        var vChoice = input.ReadLine();
                        if (vChoice is null)
                        {
                            return MenuState.Exit;
                        }

                        nextVerbosity = DescriptionFormatter.ResolveVerbosity(vChoice, verbosity);
                    }
                    else
                    {
                        nextVerbosity = DescriptionFormatter.ResolveVerbosity(normalizedAction, verbosity);
                    }

                    verbosity = nextVerbosity;
                    sessionOptions = sessionOptions with { Verbosity = verbosity };
                    var sourceDesc = baseFullDescription ?? latestOutcome!.Description;
                    latestOutcome = latestOutcome! with
                    {
                        Description = DescriptionFormatter.FormatWithVerbosity(sourceDesc, verbosity)
                    };
                    latestTickets = _workflow.ReformatOutcome(latestOutcome, backlogType, combinedOutput, verbosity);
                    color.WriteSuccess($"  ✓ Outcome verbosity set to {DescriptionFormatter.FormatVerbosityLabel(verbosity)}.");
                    RenderAndCacheWizardTickets(
                        latestOutcome,
                        latestTickets,
                        combinedOutput,
                        color,
                        includeAlternatives: verbosity != OutcomeVerbosity.Minimalistic);
                    continue;
                }

                if (normalizedAction is "" or "a" or "accept" or "save")
                {
                    if (!string.IsNullOrWhiteSpace(sessionOptions.OutputDirectory))
                    {
                        var savedPaths = _workflow.PersistTickets(
                            latestTickets!,
                            sessionOptions.OutputDirectory,
                            currentSettings.JsonFileOut,
                            output,
                            sessionOptions.CliTheme);
                        if (savedPaths.Count > 0)
                        {
                            _lastMarkdownDocuments = savedPaths.Select(File.ReadAllText).ToArray();
                        }
                    }

                    var savedSummary = combinedOutput ? "Type01 and Type02 items" : $"{latestTickets![0].BacklogType} item";
                    color.WriteSuccess($"[✓] Ready-to-add {savedSummary} '{latestTickets![0].Title}' saved to '{sessionOptions.OutputDirectory}'.");
                    return returnState;
                }

                if (normalizedAction is "p" or "preview")
                {
                    var previewSummary = combinedOutput ? "Type01 and Type02 items" : $"{latestTickets![0].BacklogType} item";
                    color.WriteSuccess($"[Preview] {previewSummary} '{latestTickets![0].Title}' generated. No files written.");
                    return returnState;
                }

                if (normalizedAction is "0" or "c" or "cancel" or "b" or "back" or "/back")
                {
                    color.WriteMuted("  Draft cancelled. No files written.");
                    return returnState;
                }

                if (normalizedAction is "r" or "refine")
                {
                    color.WriteColored("What info should be added, changed, or emphasized? ", ConsoleColor.Yellow);
                    var feedback = input.ReadLine();
                    if (feedback is null)
                    {
                        return MenuState.Exit;
                    }

                    if (string.IsNullOrWhiteSpace(feedback))
                    {
                        color.WriteWarning("  [!] Refinement instructions cannot be empty.");
                        continue;
                    }

                    previousDraft = AsIsV1Workflow.FormatOutcomeContext(latestOutcome!, preservedWebFindings);
                    refinementFeedback = feedback.Trim();
                    break;
                }

                if (normalizedAction is "t" or "type")
                {
                    color.WriteColored("Switch format ([1] Type01 Story, [2] Type02 Schedule, [3] Both): ", ConsoleColor.Yellow);
                    var switchChoice = input.ReadLine();
                    if (switchChoice is null)
                    {
                        return MenuState.Exit;
                    }

                    (backlogType, combinedOutput) = ParseGuidedFormatChoice(switchChoice);
                    var needsScenarioGeneration = !backlogType.Equals("Type02", StringComparison.OrdinalIgnoreCase) &&
                                                  latestOutcome!.Scenarios.Count == 0;

                    if (needsScenarioGeneration)
                    {
                        previousDraft = AsIsV1Workflow.FormatOutcomeContext(latestOutcome!, preservedWebFindings);
                        refinementFeedback = "Convert this draft into Type 01 format with a clear User Story and observable Given/When/Then scenarios while keeping the verified Title and Description.";
                        break;
                    }

                    latestTickets = _workflow.ReformatOutcome(latestOutcome!, backlogType, combinedOutput, verbosity);
                    RenderAndCacheWizardTickets(latestOutcome!, latestTickets, combinedOutput, color, verbosity != OutcomeVerbosity.Minimalistic);

                    continue;
                }

                color.WriteWarning($"  [!] Unknown decision '{action.Trim()}'. Choose 1-3, A, N, M, W, S, V, R, T, P, or C.");
            }
        }
    }

    private static ProcessedOutcome ApplyAlternativeToOutcome(
        ProcessedOutcome outcome,
        DraftAlternative chosen,
        OutcomeVerbosity verbosity = OutcomeVerbosity.Balanced)
    {
        var currentDesc = outcome.Description?.Trim() ?? string.Empty;
        string updatedDescription;
        if (string.IsNullOrWhiteSpace(currentDesc) ||
            currentDesc.Equals("Not specified in discovery notes.", StringComparison.OrdinalIgnoreCase))
        {
            updatedDescription = chosen.Description;
        }
        else if (currentDesc.StartsWith("**Objective:**", StringComparison.OrdinalIgnoreCase) ||
                 currentDesc.StartsWith("Objective:", StringComparison.OrdinalIgnoreCase))
        {
            var newlineIndex = currentDesc.IndexOf('\n');
            var rest = newlineIndex >= 0 ? currentDesc[(newlineIndex + 1)..] : string.Empty;
            updatedDescription = string.IsNullOrWhiteSpace(rest)
                ? $"**Objective ({chosen.Angle}):** {chosen.Description}"
                : $"**Objective ({chosen.Angle}):** {chosen.Description}\n{rest}";
        }
        else
        {
            updatedDescription = $"{chosen.Description}\n\n{currentDesc}";
        }

        return outcome with
        {
            Title = chosen.Title,
            Description = DescriptionFormatter.FormatWithVerbosity(updatedDescription, verbosity)
        };
    }

    private MenuState ShowSettingsMenu(
        WorkspaceDirectories baseWorkspace,
        ref WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        TextReader input,
        ConsoleColorWriter color,
        MenuState returnState = MenuState.Main)
    {
        WriteSettingsHeader(baseWorkspace, workspace, sessionOptions, currentSettings, color);
        var activeProvider = _diagnosticService.ResolveEffectiveProvider(sessionOptions, currentSettings);
        var (jsonBadgeFg, jsonBadgeBg) = ConsoleColorWriter.GetToggleBadgeColors(currentSettings.JsonFileOut);
        var liveEnabled = currentSettings.GeminiLive == true;
        var (liveBadgeFg, liveBadgeBg) = ConsoleColorWriter.GetToggleBadgeColors(liveEnabled);
        var (valueBadgeFg, valueBadgeBg) = color.GetValueBadgeColors();
        var providerModelBadge = $"{RuntimeEnvironmentService.FormatProviderCode(activeProvider)} • {currentSettings.ModelName ?? "automatic"}";

        var options = new List<HighlightedMenuOption>
        {
            new("1", "Select AI Provider", providerModelBadge, ConsoleColor.Black, ConsoleColor.Cyan, "Choose AI provider & model submenu (with recommendations)", IsSelectedValue: true),
            new("2", "Toggle Live AI API", liveEnabled ? "ON" : "OFF", liveBadgeFg, liveBadgeBg, "Live provider calls vs offline draft", IsSelectedValue: liveEnabled),
            new("3", "Default Outcome Verbosity", DescriptionFormatter.FormatVerbosityLabel(sessionOptions.Verbosity), valueBadgeFg, valueBadgeBg, "Minimalistic (<=25w) / Balanced / Detailed", IsSelectedValue: true),
            new("4", "Toggle JSON Export", currentSettings.JsonFileOut ? "ON" : "OFF", jsonBadgeFg, jsonBadgeBg, "Write .md + .json vs .md only", IsSelectedValue: currentSettings.JsonFileOut),
            new("5", "Select CLI Theme", ConsoleColorWriter.GetThemeCode(currentSettings.Theme), valueBadgeFg, valueBadgeBg, "Switch agy, uv, or kubectl palette", IsSelectedValue: true),
            new("6", "Inspect Directories", null, null, null, "List files & byte sizes in ./in and ./out"),
            new("7", "Runtime & AI Diagnostics", null, null, null, "Windows/WSL paths, env vars, & status table"),
            new("8", "View settings.json", null, null, null, "Show persisted JSON configuration"),
            new("0", returnState == MenuState.WizardOrchestrator ? "Back to Wizard Orchestrator" : "Back to Main Menu")
        };

        var initialIndex = options.FindIndex(o => string.Equals(o.Key, _lastSelectedSettingsKey, StringComparison.OrdinalIgnoreCase));
        if (initialIndex < 0)
        {
            initialIndex = 0;
        }

        var choice = InteractiveSelector.SelectMenuOptionWithHighlight(
            options,
            initialIndex,
            "Select option (0-8, ↑/↓, b=back, q=quit): ",
            input,
            color);
        if (choice is null)
        {
            return MenuState.Exit;
        }

        var normalized = choice.Trim().ToLowerInvariant();
        if (normalized is "0" or "b" or "back" or "/back")
        {
            return returnState;
        }

        if (normalized is "q" or "quit" or "/exit" or "/quit")
        {
            return MenuState.Exit;
        }

        if (normalized is "m" or "model" or "models")
        {
            _lastSelectedSettingsKey = "1";
            return MenuState.ModelSelector;
        }

        if (normalized is "1" or "2" or "3" or "4" or "5" or "6" or "7" or "8")
        {
            _lastSelectedSettingsKey = normalized;
        }

        switch (normalized)
        {
            case "1":
                return MenuState.ProviderSelector;
            case "2":
                currentSettings = currentSettings with { GeminiLive = currentSettings.GeminiLive != true };
                sessionOptions = sessionOptions with { GeminiLive = currentSettings.GeminiLive };
                var liveSettingsPath = _settingsService.Save(baseWorkspace, currentSettings);
                color.WriteSuccess($"Live Gemini API is now {(currentSettings.GeminiLive == true ? "ON" : "OFF")}. Saved settings to '{liveSettingsPath}'.");
                return MenuState.Settings;
            case "3":
                color.WriteLine();
                color.WriteSubHeader("Select Default Outcome Verbosity:");
                var verbosityOptions = new List<HighlightedMenuOption>
                {
                    BuildSelectableValueOption("1", "Minimalistic (<= 25 words)", "1-line objective + at most 2 action bullets", sessionOptions.Verbosity == OutcomeVerbosity.Minimalistic),
                    BuildSelectableValueOption("2", "Balanced (<= 57 words)", "Standard Objective + 3 action bullets (default)", sessionOptions.Verbosity == OutcomeVerbosity.Balanced),
                    BuildSelectableValueOption("3", "Detailed (<= 95 words)", "Full context, bullets & 3 Alternatives", sessionOptions.Verbosity == OutcomeVerbosity.Detailed)
                };
                var activeVerbosityIndex = sessionOptions.Verbosity switch
                {
                    OutcomeVerbosity.Minimalistic => 0,
                    OutcomeVerbosity.Detailed => 2,
                    _ => 1
                };
                var vSelection = InteractiveSelector.SelectMenuOptionWithHighlight(
                    verbosityOptions,
                    activeVerbosityIndex,
                    "Choose verbosity (1-3, ↑/↓, 0=cancel): ",
                    input,
                    color);
                if (vSelection is null || vSelection.Trim().ToLowerInvariant() is "q" or "quit" or "/exit" or "/quit")
                {
                    return MenuState.Exit;
                }

                if (vSelection.Trim().ToLowerInvariant() is not ("0" or "b" or "back" or "/back"))
                {
                    var nextDefaultVerbosity = DescriptionFormatter.ResolveVerbosity(vSelection, sessionOptions.Verbosity);
                    currentSettings = currentSettings with { DefaultVerbosity = nextDefaultVerbosity };
                    sessionOptions = sessionOptions with { Verbosity = nextDefaultVerbosity };
                    var savedVerbosityPath = _settingsService.Save(baseWorkspace, currentSettings);
                    color.WriteSuccess($"Default Outcome Verbosity set to {DescriptionFormatter.FormatVerbosityLabel(nextDefaultVerbosity)}. Saved settings to '{savedVerbosityPath}'.");
                }

                return MenuState.Settings;
            case "4":
                currentSettings = currentSettings with { JsonFileOut = !currentSettings.JsonFileOut };
                sessionOptions = sessionOptions with { JsonFileOut = currentSettings.JsonFileOut };
                var savedPath = _settingsService.Save(baseWorkspace, currentSettings);
                color.WriteSuccess($"JSON Export is now {(currentSettings.JsonFileOut ? "ON" : "OFF")}. Saved settings to '{savedPath}'.");
                return MenuState.Settings;
            case "5":
                return MenuState.ThemeSelector;
            case "6":
                InspectDirectoryContents("Input", workspace.InputDirectory, color);
                InspectDirectoryContents("Output", sessionOptions.OutputDirectory ?? workspace.OutputDirectory, color);
                return MenuState.Settings;
            case "7":
                WriteRuntimeDiagnostics(workspace, sessionOptions, currentSettings, color);
                return MenuState.Settings;
            case "8":
                DisplayPersistedSettingsJson(baseWorkspace, color);
                return MenuState.Settings;
            default:
                return WarnAndStay(color, choice, "0-8, b=back, or q=quit", MenuState.Settings);
        }
    }

    private MenuState ShowThemeMenu(
        WorkspaceDirectories baseWorkspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        TextReader input,
        ConsoleColorWriter color)
    {
        color.WriteLine();
        color.WriteHeader("=== CLI Theme Configuration ===");
        color.WriteMuted($"Active Theme: {ConsoleColorWriter.FormatThemeDisplay(currentSettings.Theme)}");
        color.WriteLine();

        var options = new List<HighlightedMenuOption>
        {
            BuildSelectableValueOption("1", "Agy Style (Default)", "Gemini Agy palette (Cyan headers, Blue steps, Yellow prompts)", currentSettings.Theme == CliThemeStyle.Agy),
            BuildSelectableValueOption("2", "UV Style", "Astral uv palette (Magenta headers, Cyan steps, Green prompts)", currentSettings.Theme == CliThemeStyle.Uv),
            BuildSelectableValueOption("3", "Kubectl Style", "Kubernetes kubectl palette (Blue headers, DarkCyan steps, Cyan prompts)", currentSettings.Theme == CliThemeStyle.Kubectl),
            new("0", "Back to Workspace & Settings")
        };

        var activeThemeIndex = currentSettings.Theme switch
        {
            CliThemeStyle.Uv => 1,
            CliThemeStyle.Kubectl => 2,
            _ => 0
        };

        var choice = InteractiveSelector.SelectMenuOptionWithHighlight(
            options,
            activeThemeIndex,
            "Select a CLI theme (0-3, ↑/↓, b=back, q=quit): ",
            input,
            color);
        if (choice is null)
        {
            return MenuState.Exit;
        }

        var normalized = choice.Trim().ToLowerInvariant();
        if (normalized is "0" or "b" or "back" or "/back")
        {
            return MenuState.Settings;
        }

        if (normalized is "q" or "quit" or "/exit" or "/quit")
        {
            return MenuState.Exit;
        }

        CliThemeStyle? selectedTheme = normalized switch
        {
            "1" or "agy" => CliThemeStyle.Agy,
            "2" or "uv" => CliThemeStyle.Uv,
            "3" or "kubectl" => CliThemeStyle.Kubectl,
            _ => null
        };

        if (selectedTheme is null)
        {
            return WarnAndStay(color, choice, "0, 1, 2, or 3", MenuState.ThemeSelector);
        }

        currentSettings = currentSettings with { Theme = selectedTheme.Value };
        color.Theme = selectedTheme.Value;
        var themeCode = ConsoleColorWriter.GetThemeCode(selectedTheme.Value);
        sessionOptions = sessionOptions with { CliTheme = themeCode };
        var savedPath = _settingsService.Save(baseWorkspace, currentSettings);
        color.WriteSuccess($"CLI Theme is now {ConsoleColorWriter.FormatThemeDisplay(selectedTheme.Value)}. Saved settings to '{savedPath}'.");
        return MenuState.Settings;
    }

    private MenuState ShowProviderMenu(
        WorkspaceDirectories baseWorkspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        TextReader input,
        ConsoleColorWriter color)
    {
        var activeProvider = _diagnosticService.ResolveEffectiveProvider(sessionOptions, currentSettings);
        var activeProviderCode = RuntimeEnvironmentService.FormatProviderCode(activeProvider);
        color.WriteLine();
        color.WriteHeader("=== AI Provider Orchestrator ===");
        color.WriteMuted($"  Active Provider : {activeProviderCode} │ Model: {currentSettings.ModelName ?? "automatic"}");
        color.WriteMuted("  Recommendation  : 'gemini' (fast AI Studio tier) or 'copilot-sdk' (GitHub Copilot runtime).");
        color.WriteLine();

        var options = new List<HighlightedMenuOption>
        {
            BuildProviderOption("1", "Google Gemini (gemini)", "Live REST generateContent or offline deterministic mode", activeProvider == AiProviderType.GoogleGemini, isRecommended: true),
            BuildProviderOption("2", "GitHub Copilot SDK (copilot-sdk)", "Official Microsoft.Copilot.SDK agent runtime & BYOK", activeProvider == AiProviderType.GitHubCopilot, isRecommended: true),
            BuildProviderOption("3", "Azure OpenAI / Foundry (azure)", "Copilot SDK BYOK when configured, else mock fallback", activeProvider == AiProviderType.AzureOpenAi, isRecommended: false),
            BuildProviderOption("4", "Anthropic Claude (claude)", "Copilot SDK BYOK when configured, else mock fallback", activeProvider == AiProviderType.AnthropicClaude, isRecommended: false),
            BuildProviderOption("5", "Manual Prompt (manual)", "Render Markdown prompt for copy/paste into Copilot or any AI", activeProvider == AiProviderType.ManualPrompt, isRecommended: false),
            new("M", $"Select Model for '{activeProviderCode}'", null, null, null, "Configure model, view recommendations & official docs"),
            new("0", "Back to Workspace & Settings")
        };

        var activeProviderIndex = activeProvider switch
        {
            AiProviderType.GitHubCopilot => 1,
            AiProviderType.AzureOpenAi => 2,
            AiProviderType.AnthropicClaude => 3,
            AiProviderType.ManualPrompt => 4,
            _ => 0
        };

        var choice = InteractiveSelector.SelectMenuOptionWithHighlight(
            options,
            activeProviderIndex,
            "Select AI provider (0-5, M=model, ↑/↓, b=back, q=quit): ",
            input,
            color);
        if (choice is null)
        {
            return MenuState.Exit;
        }

        var normalized = choice.Trim().ToLowerInvariant();
        if (normalized is "0" or "b" or "back" or "/back")
        {
            return MenuState.Settings;
        }

        if (normalized is "q" or "quit" or "/exit" or "/quit")
        {
            return MenuState.Exit;
        }

        if (normalized is "m" or "model" or "models")
        {
            return MenuState.ModelSelector;
        }

        AiProviderType? selected = normalized switch
        {
            "1" or "gemini" => AiProviderType.GoogleGemini,
            "2" or "copilot-sdk" or "github-copilot" => AiProviderType.GitHubCopilot,
            "3" or "azure" => AiProviderType.AzureOpenAi,
            "4" or "claude" or "anthropic" => AiProviderType.AnthropicClaude,
            "5" or "manual" => AiProviderType.ManualPrompt,
            _ => null
        };

        if (selected is null)
        {
            return WarnAndStay(color, choice, "0-5, M=model", MenuState.ProviderSelector);
        }

        var providerCode = RuntimeEnvironmentService.FormatProviderCode(selected.Value);
        var clearPinnedModel = selected.Value != activeProvider;
        currentSettings = currentSettings with
        {
            PreferredProvider = selected.Value,
            ModelName = clearPinnedModel ? null : currentSettings.ModelName
        };
        sessionOptions = sessionOptions with
        {
            ProviderInput = providerCode,
            ModelName = currentSettings.ModelName
        };
        var savedPath = _settingsService.Save(baseWorkspace, currentSettings);
        color.WriteSuccess($"Active AI Provider set to '{providerCode}'. Saved settings to '{savedPath}'.");

        if (selected.Value == AiProviderType.ManualPrompt)
        {
            color.WriteMuted($"  [i] {ModelCatalogService.GetProviderRecommendationSummary(AiProviderType.ManualPrompt)}");
            return MenuState.Settings;
        }

        return MenuState.ModelSelector;
    }

    private MenuState ShowModelMenu(
        WorkspaceDirectories baseWorkspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        TextReader input,
        ConsoleColorWriter color)
    {
        var activeProvider = _diagnosticService.ResolveEffectiveProvider(sessionOptions, currentSettings);
        var providerCode = RuntimeEnvironmentService.FormatProviderCode(activeProvider);
        var docsUrl = ModelCatalogService.GetOfficialDocsUrl(activeProvider);

        if (activeProvider == AiProviderType.ManualPrompt)
        {
            color.WriteLine();
            color.WriteHeader($"=== AI Model Selection ({providerCode}) ===");
            color.WriteWarning("  [!] Manual Prompt mode renders Markdown prompts locally and does not invoke a remote model API.");
            color.WriteMuted($"  Recommendation : {ModelCatalogService.GetProviderRecommendationSummary(activeProvider)}");
            color.WriteMuted($"  Official Docs  : {docsUrl}");
            return MenuState.Settings;
        }

        var catalogEntries = new List<ModelRecommendation>(ModelCatalogService.GetRecommendations(activeProvider));
        IReadOnlyList<string>? liveModelIds = null;
        IReadOnlyDictionary<string, IReadOnlyList<string>>? unsupportedHints = null;
        string? discoveryNote = null;

        if (activeProvider == AiProviderType.GoogleGemini)
        {
            var secret = _secretProvider.Resolve(sessionOptions.ApiKey);
            if (secret.IsConfigured)
            {
                try
                {
                    var catalog = TerminalUx.RunWithSpinner(
                        "Discovering compatible Gemini models...",
                        () => _workflow.ListGeminiModelCatalog(secret.ApiKey!),
                        color);
                    unsupportedHints = catalog.UnsupportedModelMethods;
                    (liveModelIds, discoveryNote) = MergeLiveDiscoveryEntries(
                        catalogEntries,
                        catalog.SupportedModels.Select(m => new ModelRecommendation(
                            m.ModelId,
                            string.IsNullOrWhiteSpace(m.DisplayName) ? m.ModelId : m.DisplayName,
                            "live",
                            string.IsNullOrWhiteSpace(m.Description) ? m.DisplayName : m.Description)),
                        providerCode);
                }
                catch (Exception ex)
                {
                    discoveryNote = $"Could not query live Gemini models ({ex.Message}); showing curated recommendations.";
                }
            }
            else
            {
                discoveryNote = $"GEMINI_API_KEY not configured; showing recommended Gemini models ({GeminiSecretProvider.GeminiApiKeysUrl}).";
            }
        }
        else if (activeProvider == AiProviderType.GitHubCopilot)
        {
            var copilotDiag = _workflow.ProviderOrchestrator.CopilotGateway.InspectDiagnostics();
            if (copilotDiag.RuntimeAvailable)
            {
                try
                {
                    var explicitApiKey = sessionOptions.ApiKey;
                    var copilotModels = TerminalUx.RunWithSpinner(
                        "Querying GitHub Copilot SDK models...",
                        () => _workflow.ListCopilotModels(explicitApiKey),
                        color);
                    (liveModelIds, discoveryNote) = MergeLiveDiscoveryEntries(
                        catalogEntries,
                        copilotModels.Select(m => new ModelRecommendation(
                            m.Id,
                            m.Name,
                            "live",
                            "Available on your GitHub Copilot subscription")),
                        providerCode);
                }
                catch (Exception ex)
                {
                    discoveryNote = $"Could not query live Copilot models ({ex.Message}); showing curated recommendations.";
                }
            }
            else
            {
                discoveryNote = "Copilot runtime unavailable offline; showing recommended Copilot models.";
            }
        }

        // When live catalog discovery succeeded, filter out curated entries that are absent on this key/account
        // unless they are currently pinned, so users see actionable choices and clear reasons.
        var pinnedModelName = currentSettings.ModelName;
        var displayEntries = liveModelIds is { Count: > 0 }
            ? catalogEntries
                .Where(e =>
                    liveModelIds.Any(id => string.Equals(id, e.ModelId, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(e.ModelId, pinnedModelName, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : catalogEntries;

        var defaultRecommendedId = displayEntries.FirstOrDefault(e => e.IsRecommended)?.ModelId
            ?? displayEntries.FirstOrDefault()?.ModelId
            ?? (activeProvider == AiProviderType.GitHubCopilot
                ? RuntimeEnvironmentService.DefaultCopilotModel
                : RuntimeEnvironmentService.DefaultGeminiModel);

        while (true)
        {
            color.WriteLine();
            color.WriteHeader($"=== AI Model Selection ({providerCode}) ===");
            color.WriteMuted($"  Active Model   : {currentSettings.ModelName ?? $"automatic ({defaultRecommendedId})"}");
            color.WriteMuted($"  Recommendation : {ModelCatalogService.GetProviderRecommendationSummary(activeProvider)}");
            color.WriteMuted($"  Official Docs  : {docsUrl}");
            if (!string.IsNullOrWhiteSpace(discoveryNote))
            {
                color.WriteWarning($"  [i] {discoveryNote}");
                discoveryNote = null;
            }

            color.WriteLine();

            var options = new List<HighlightedMenuOption>(displayEntries.Count + 3);
            var initialIndex = displayEntries.Count; // defaults to [A] Automatic when nothing pinned
            for (var i = 0; i < displayEntries.Count; i++)
            {
                var entry = displayEntries[i];
                var isSelected = string.Equals(entry.ModelId, currentSettings.ModelName, StringComparison.OrdinalIgnoreCase);
                if (isSelected)
                {
                    initialIndex = i;
                }

                string? badgeText = isSelected
                    ? "selected"
                    : entry.IsRecommended
                        ? "recommended"
                        : null;
                ConsoleColor? badgeFg = isSelected
                    ? ConsoleColor.Black
                    : entry.IsRecommended
                        ? ConsoleColor.Black
                        : null;
                ConsoleColor? badgeBg = isSelected
                    ? ConsoleColor.Green
                    : entry.IsRecommended
                        ? ConsoleColor.Yellow
                        : null;

                var desc = string.Equals(entry.DisplayName, entry.ModelId, StringComparison.OrdinalIgnoreCase)
                    ? entry.Reason
                    : $"{entry.ModelId} — {entry.Reason}";

                options.Add(new HighlightedMenuOption(
                    (i + 1).ToString(),
                    entry.DisplayName,
                    BadgeText: badgeText,
                    BadgeForeground: badgeFg,
                    BadgeBackground: badgeBg,
                    Description: desc,
                    IsSelectedValue: isSelected));
            }

            var isAuto = string.IsNullOrWhiteSpace(currentSettings.ModelName);
            options.Add(BuildSelectableValueOption(
                "A",
                $"Automatic (Recommended: {defaultRecommendedId})",
                "Let Smart Backlog Assistant pick best compatible model",
                isAuto));
            options.Add(new("C", "Enter Custom Model ID", null, null, null, "Validate any model ID against compatibility rules & docs"));
            options.Add(new("P", "Switch AI Provider", null, null, null, "Return to AI Provider Orchestrator"));
            options.Add(new("0", "Back to Workspace & Settings"));

            var choice = InteractiveSelector.SelectMenuOptionWithHighlight(
                options,
                initialIndex,
                $"Select model (1-{displayEntries.Count}, A=auto, C=custom, P=provider, 0=back): ",
                input,
                color);
            if (choice is null)
            {
                return MenuState.Exit;
            }

            var trimmed = choice.Trim();
            var normalized = trimmed.ToLowerInvariant();
            if (normalized is "0" or "b" or "back" or "/back")
            {
                return MenuState.Settings;
            }

            if (normalized is "q" or "quit" or "/exit" or "/quit")
            {
                return MenuState.Exit;
            }

            if (normalized is "p" or "provider")
            {
                return MenuState.ProviderSelector;
            }

            if (normalized is "a" or "auto" or "automatic" or "default")
            {
                currentSettings = currentSettings with { ModelName = null };
                sessionOptions = sessionOptions with { ModelName = null };
                var savedAutoPath = _settingsService.Save(baseWorkspace, currentSettings);
                color.WriteSuccess($"{providerCode} model reset to automatic (recommended: {defaultRecommendedId}). Saved settings to '{savedAutoPath}'.");
                return MenuState.Settings;
            }

            string candidateModelId;
            if (normalized is "c" or "custom")
            {
                color.WriteColored($"Enter custom {providerCode} model ID (or 0 to cancel): ", ConsoleColor.Yellow);
                var customInput = input.ReadLine();
                if (customInput is null)
                {
                    return MenuState.Exit;
                }

                var customTrimmed = customInput.Trim();
                if (string.IsNullOrEmpty(customTrimmed) ||
                    customTrimmed.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                    customTrimmed.Equals("b", StringComparison.OrdinalIgnoreCase) ||
                    customTrimmed.Equals("back", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidateModelId = customTrimmed;
            }
            else if (int.TryParse(trimmed, out var idx))
            {
                if (idx < 1 || idx > displayEntries.Count)
                {
                    color.WriteWarning($"  [!] Invalid option '{trimmed}'. Choose 1-{displayEntries.Count}, A, C, P, or 0.");
                    continue;
                }

                candidateModelId = displayEntries[idx - 1].ModelId;
            }
            else
            {
                candidateModelId = trimmed;
            }

            var check = ModelCatalogService.ValidateModelSelection(
                activeProvider,
                candidateModelId,
                liveModelIds,
                unsupportedHints);

            if (!check.IsSupported)
            {
                WriteUnsupportedModelDiagnostics(color, providerCode, candidateModelId, check);
                continue;
            }

            if (check.IsKnownWarning)
            {
                color.WriteWarning($"  [!] {check.Reason}");
                color.WriteColored($"  Official Docs  : ", ConsoleColor.DarkGray);
                color.WriteLineColored(check.OfficialDocsUrl, ConsoleColor.Cyan);
                WriteModelRecommendations(color, check.Recommendations);
                color.WriteColored($"Use unverified model '{candidateModelId}' anyway? [y/N]: ", ConsoleColor.Yellow);
                var confirm = input.ReadLine();
                if (confirm is null)
                {
                    return MenuState.Exit;
                }

                if (confirm.Trim().ToLowerInvariant() is not ("y" or "yes"))
                {
                    continue;
                }
            }

            var normalizedId = activeProvider == AiProviderType.GoogleGemini &&
                               candidateModelId.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? candidateModelId["models/".Length..].Trim()
                : candidateModelId.Trim();

            currentSettings = currentSettings with { ModelName = normalizedId };
            sessionOptions = sessionOptions with { ModelName = normalizedId };
            var savedModelPath = _settingsService.Save(baseWorkspace, currentSettings);
            color.WriteSuccess($"{providerCode} model set to '{normalizedId}'. Saved settings to '{savedModelPath}'.");
            return MenuState.Settings;
        }
    }

    private static void WriteUnsupportedModelDiagnostics(
        ConsoleColorWriter color,
        string providerCode,
        string candidateModelId,
        ModelSupportCheckResult check)
    {
        color.WriteLine();
        color.WriteError($"  [!] Model '{candidateModelId}' is not supported for '{providerCode}'.");
        color.WriteColored("  Reason         : ", ConsoleColor.Yellow);
        color.WriteLine(check.Reason ?? "Incompatible or unavailable model.");
        color.WriteColored("  Official Docs  : ", ConsoleColor.Cyan);
        color.WriteLineColored(check.OfficialDocsUrl, ConsoleColor.White);
        WriteModelRecommendations(color, check.Recommendations);
    }

    private static void WriteModelRecommendations(
        ConsoleColorWriter color,
        IReadOnlyList<ModelRecommendation> recommendations)
    {
        if (recommendations.Count == 0)
        {
            return;
        }

        color.WriteSubHeader("  Recommended Models:");
        foreach (var rec in recommendations.Take(3))
        {
            var tag = rec.IsRecommended ? "★" : "•";
            color.WriteColored($"    {tag} {rec.ModelId}", ConsoleColor.Green);
            color.WriteLineColored($" — {rec.Reason}", ConsoleColor.DarkGray);
        }
    }

    private static (IReadOnlyList<string>? LiveModelIds, string? DiscoveryNote) MergeLiveDiscoveryEntries(
        List<ModelRecommendation> catalogEntries,
        IEnumerable<ModelRecommendation> discoveredEntries,
        string providerCode)
    {
        var liveIds = new List<string>();
        foreach (var liveModel in discoveredEntries)
        {
            liveIds.Add(liveModel.ModelId);
            if (!catalogEntries.Any(e => string.Equals(e.ModelId, liveModel.ModelId, StringComparison.OrdinalIgnoreCase)))
            {
                catalogEntries.Add(liveModel);
            }
        }

        if (liveIds.Count == 0)
        {
            return (null, $"Live '{providerCode}' catalog returned 0 models (unauthenticated or empty subscription list); showing curated recommendations.");
        }

        return (liveIds, null);
    }

    private static HighlightedMenuOption BuildProviderOption(
        string key,
        string label,
        string? description,
        bool isSelected,
        bool isRecommended) =>
        new(
            key,
            label,
            BadgeText: isSelected ? "selected" : isRecommended ? "recommended" : null,
            BadgeForeground: isSelected || isRecommended ? ConsoleColor.Black : null,
            BadgeBackground: isSelected ? ConsoleColor.Green : isRecommended ? ConsoleColor.Yellow : null,
            Description: description,
            IsSelectedValue: isSelected);

    private static HighlightedMenuOption BuildSelectableValueOption(
        string key,
        string label,
        string? description,
        bool isSelected) =>
        new(
            key,
            label,
            BadgeText: isSelected ? "selected" : null,
            BadgeForeground: isSelected ? ConsoleColor.Black : null,
            BadgeBackground: isSelected ? ConsoleColor.Green : null,
            Description: description,
            IsSelectedValue: isSelected);

    private void WriteSettingsHeader(
        WorkspaceDirectories baseWorkspace,
        WorkspaceDirectories workspace,
        WorkflowOptions sessionOptions,
        CliSettings currentSettings,
        ConsoleColorWriter color)
    {
        var activeProvider = _diagnosticService.ResolveEffectiveProvider(sessionOptions, currentSettings);
        var providerCode = RuntimeEnvironmentService.FormatProviderCode(activeProvider);
        var modelLabel = currentSettings.ModelName ?? "automatic";
        var themeCode = ConsoleColorWriter.GetThemeCode(currentSettings.Theme);
        var liveOn = currentSettings.GeminiLive == true;
        var jsonOn = currentSettings.JsonFileOut;

        color.WriteLine();
        color.WriteHeader("=== Workspace & Settings ===");
        color.WriteMuted($"  Paths  : In: {workspace.InputDirectory} │ Out: {sessionOptions.OutputDirectory}");

        color.WriteColored("  Active : Provider: ", ConsoleColor.DarkGray);
        color.WriteColored(providerCode, ConsoleColor.Cyan);
        color.WriteColored(" (", ConsoleColor.DarkGray);
        color.WriteColored(modelLabel, ConsoleColor.White);
        color.WriteColored(") │ Live: ", ConsoleColor.DarkGray);
        color.WriteColored(liveOn ? "ON" : "OFF", liveOn ? ConsoleColor.Green : ConsoleColor.Red);
        color.WriteColored(" │ JSON: ", ConsoleColor.DarkGray);
        color.WriteColored(jsonOn ? "ON" : "OFF", jsonOn ? ConsoleColor.Green : ConsoleColor.DarkYellow);
        color.WriteColored(" │ Verbosity: ", ConsoleColor.DarkGray);
        color.WriteColored(DescriptionFormatter.FormatVerbosityLabel(sessionOptions.Verbosity), ConsoleColor.Yellow);
        color.WriteColored(" │ Theme: ", ConsoleColor.DarkGray);
        color.WriteLineColored(themeCode, color.GetOptionKeyColor());

        color.WriteMuted($"  Config : {_settingsService.GetSettingsFilePath(baseWorkspace)}");
        color.WriteLine();
    }

    private void InspectDirectoryContents(string label, string directoryPath, ConsoleColorWriter color)
    {
        color.WriteLine();
        color.WriteSubHeader($"{label} Directory: {directoryPath}");
        if (!Directory.Exists(directoryPath))
        {
            color.WriteMuted("  Directory does not exist.");
            return;
        }

        try
        {
            var files = Directory.EnumerateFiles(directoryPath)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length == 0)
            {
                color.WriteMuted("  No files.");
                return;
            }

            foreach (var filePath in files)
            {
                var file = new FileInfo(filePath);
                color.WriteLine($"  {file.Name} ({file.Length:N0} bytes)");
            }
        }
        catch (IOException exception)
        {
            color.WriteWarning($"  [!] Could not inspect directory: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            color.WriteWarning($"  [!] Could not inspect directory: {exception.Message}");
        }
    }

    private void WriteRuntimeDiagnostics(
        WorkspaceDirectories workspace,
        WorkflowOptions sessionOptions,
        CliSettings currentSettings,
        ConsoleColorWriter color)
    {
        color.WriteLine();
        color.WriteSubHeader("--- Windows / WSL Path Mappings ---");
        color.WriteLine($@"  Windows: {WorkspacePathService.DefaultWindowsWorkspaceRoot}\smart-backlog-assistant\in -> {WorkspacePathService.DefaultWindowsWorkspaceRoot}\smart-backlog-assistant\out");
        color.WriteLine($"  WSL    : {WorkspacePathService.DefaultWslWorkspaceRoot}/smart-backlog-assistant/in -> {WorkspacePathService.DefaultWslWorkspaceRoot}/smart-backlog-assistant/out");
        color.WriteLine($"  Active : {workspace.InputDirectory} -> {sessionOptions.OutputDirectory}");

        color.WriteLine();
        color.WriteSubHeader("--- Gemini Environment Variables ---");
        foreach (var variableName in new[]
        {
            GeminiSecretProvider.PrimaryEnvVar,
            GeminiSecretProvider.GoogleEnvVar,
            GeminiSecretProvider.FallbackEnvVar
        })
        {
            var isConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableName));
            color.WriteLine($"  {variableName}: {(isConfigured ? "configured" : "not set")}");
        }

        var provider = _diagnosticService.ResolveEffectiveProvider(sessionOptions, currentSettings);
        var runtimeInfo = _runtimeEnvService.Inspect(provider, sessionOptions.ModelName);
        var secret = _secretProvider.Resolve(sessionOptions.ApiKey);
        color.WriteMuted(_runtimeEnvService.FormatRuntimeBanner(runtimeInfo, secret).TrimEnd());
        color.WriteLine();
        _diagnosticService.RenderStatusTable(sessionOptions, color.InnerWriter, currentSettings);
    }

    private void DisplayPersistedSettingsJson(WorkspaceDirectories baseWorkspace, ConsoleColorWriter color)
    {
        var path = _settingsService.GetSettingsFilePath(baseWorkspace);
        color.WriteLine();
        color.WriteSubHeader($"Settings JSON ({path}):");
        if (!File.Exists(path))
        {
            color.WriteMuted("  settings.json has not been written yet (using defaults).");
            return;
        }

        color.WriteLine(File.ReadAllText(path));
    }

    private void ListActiveProviderModels(
        WorkflowOptions sessionOptions,
        CliSettings currentSettings,
        TextReader input,
        ConsoleColorWriter color)
    {
        var provider = _diagnosticService.ResolveEffectiveProvider(sessionOptions, currentSettings);
        if (provider == AiProviderType.GitHubCopilot)
        {
            var copilotModels = TerminalUx.RunWithSpinner(
                "Querying GitHub Copilot SDK models...",
                () => _workflow.ListCopilotModels(sessionOptions.ApiKey),
                color);
            color.WriteHeader("Available GitHub Copilot SDK Models:");
            foreach (var model in copilotModels)
            {
                color.WriteLine($"  • {model.Id,-28} {model.Name}");
            }

            return;
        }

        if (provider is AiProviderType.AzureOpenAi or AiProviderType.AnthropicClaude or AiProviderType.ManualPrompt)
        {
            var providerCode = RuntimeEnvironmentService.FormatProviderCode(provider);
            color.WriteHeader($"Recommended Models ({providerCode}):");
            WriteModelRecommendations(color, ModelCatalogService.GetRecommendations(provider));
            color.WriteMuted($"  Official Docs: {ModelCatalogService.GetOfficialDocsUrl(provider)}");
            return;
        }

        var secret = _secretProvider.ResolveOrPrompt(sessionOptions.ApiKey, input, color.InnerWriter);
        var models = TerminalUx.RunWithSpinner(
            "Querying Gemini models...",
            () => _workflow.ListGeminiModels(secret.ApiKey!),
            color);
        color.WriteHeader("Available Gemini Models (generateContent):");
        foreach (var model in models)
        {
            color.WriteLine($"  • {model.ModelId,-28} {model.DisplayName}");
        }
    }

    private void CacheResolvedWorkflowOptions(
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        WorkspaceDirectories? baseWorkspace)
    {
        var updatedOptions = sessionOptions;
        if (string.IsNullOrWhiteSpace(updatedOptions.ApiKey) &&
            !string.IsNullOrWhiteSpace(_workflow.LastResolvedApiKey))
        {
            updatedOptions = updatedOptions with { ApiKey = _workflow.LastResolvedApiKey };
        }

        if (!string.IsNullOrWhiteSpace(_workflow.LastSelectedModel))
        {
            updatedOptions = updatedOptions with { ModelName = _workflow.LastSelectedModel };
            currentSettings = currentSettings with { ModelName = _workflow.LastSelectedModel };
            if (baseWorkspace is not null)
            {
                _settingsService.Save(baseWorkspace, currentSettings);
            }
        }

        sessionOptions = updatedOptions;

        if (_workflow.LastMarkdownDocuments.Count > 0)
        {
            _lastMarkdownDocuments = _workflow.LastMarkdownDocuments;
            _lastSupplementalUserStory = _workflow.LastSupplementalUserStory;
            _lastSupplementalDescription = _workflow.LastSupplementalDescription;
        }
    }

    private MenuState ShowPreviewOutcomeMenu(
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        WorkspaceDirectories baseWorkspace,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color,
        MenuState returnState = MenuState.Main,
        bool openInWizardReview = false)
    {
        var outputDir = sessionOptions.OutputDirectory ?? workspace.OutputDirectory;
        var mdFiles = GetSavedMarkdownFiles(outputDir);

        color.WriteLine();
        color.WriteHeader(openInWizardReview
            ? "=== Preview, Refine & Copy Outcome (Wizard) ==="
            : "=== Preview & Copy Outcome (Markdown) ===");
        if (mdFiles.Count == 0)
        {
            color.WriteWarning($"  [!] No saved Markdown outcomes found in '{outputDir}'. Run Draft or Process first.");
            return returnState;
        }

        IReadOnlyList<string> candidates = mdFiles;
        while (true)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var info = new FileInfo(candidates[i]);
                color.WriteMenuOption((i + 1).ToString(), info.Name, $"{info.Length:N0} bytes");
            }

            color.WriteMenuOption("0", returnState == MenuState.WizardOrchestrator ? "Back to Wizard Orchestrator" : "Back to Main Menu");
            color.WriteColored("Select file [Enter=#1, number, fuzzy name, b=back, q=quit]: ", ConsoleColor.Yellow);
            var selection = input.ReadLine();
            if (selection is null)
            {
                return MenuState.Exit;
            }

            var trimmed = selection.Trim();
            var normalized = trimmed.ToLowerInvariant();
            if (normalized is "0" or "b" or "back" or "/back")
            {
                return returnState;
            }

            if (normalized is "q" or "quit" or "/exit" or "/quit")
            {
                return MenuState.Exit;
            }

            string? chosenPath = null;
            if (string.IsNullOrEmpty(trimmed))
            {
                chosenPath = candidates[0];
            }
            else if (int.TryParse(trimmed, out var chosen))
            {
                if (chosen < 1 || chosen > candidates.Count)
                {
                    color.WriteWarning($"[!] Invalid option '{trimmed}'. Choose 0-{candidates.Count}.");
                    continue;
                }

                chosenPath = candidates[chosen - 1];
            }
            else
            {
                var matches = InteractiveSelector.FilterFilePaths(candidates, trimmed);
                if (matches.Count == 1)
                {
                    color.WriteSuccess($"  ✓ Matched '{Path.GetFileName(matches[0])}'");
                    chosenPath = matches[0];
                }
                else if (matches.Count > 1)
                {
                    candidates = matches;
                    color.WriteMuted($"  Filtered to {matches.Count} matching file(s):");
                    continue;
                }
                else
                {
                    color.WriteWarning($"  [!] No saved Markdown files matched '{trimmed}'.");
                    candidates = mdFiles;
                    continue;
                }
            }

            PreviewMarkdownFilePath(chosenPath, color, includeAlternatives: openInWizardReview || sessionOptions.ShowAlternatives);
            if (openInWizardReview)
            {
                return OpenSavedMarkdownInWizardReview(
                    chosenPath,
                    workspace,
                    ref sessionOptions,
                    ref currentSettings,
                    baseWorkspace,
                    input,
                    output,
                    color,
                    returnState);
            }

            var copyOutcomeState = PromptCopyFormatLoop(input, color, returnState: returnState, allowWizardPromotion: true);
            if (copyOutcomeState == MenuState.WizardOrchestrator)
            {
                return OpenSavedMarkdownInWizardReview(
                    chosenPath,
                    workspace,
                    ref sessionOptions,
                    ref currentSettings,
                    baseWorkspace,
                    input,
                    output,
                    color,
                    returnState);
            }

            return copyOutcomeState;
        }
    }

    private MenuState OpenSavedMarkdownInWizardReview(
        string filePath,
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        WorkspaceDirectories baseWorkspace,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color,
        MenuState returnState)
    {
        var markdown = File.ReadAllText(filePath);
        var activeProvider = _diagnosticService.ResolveEffectiveProvider(sessionOptions, currentSettings);
        var (detectedType, reconstructed) = MarkdownOutcomeRenderer.ReconstructOutcomeFromMarkdown(markdown, activeProvider);
        var typeCode = detectedType == BacklogType.Type02 ? "Type02" : "Type01";
        var tickets = _workflow.ReformatOutcome(reconstructed, typeCode, combinedOutput: false, verbosity: sessionOptions.Verbosity);

        return RunReviewDecisionLoop(
            enrichedGoal: null,
            initialFilePath: null,
            initialDetails: reconstructed.Description,
            initialBacklogType: typeCode,
            initialCombinedOutput: false,
            initialVerbosity: sessionOptions.Verbosity,
            initialWebSearch: false,
            initialOutcome: reconstructed,
            initialTickets: tickets,
            workspace: workspace,
            sessionOptions: ref sessionOptions,
            currentSettings: ref currentSettings,
            baseWorkspace: baseWorkspace,
            input: input,
            output: output,
            color: color,
            returnState: returnState);
    }

    private void ExecuteInlinePreviewCommand(
        string[] args,
        WorkspaceDirectories workspace,
        WorkflowOptions sessionOptions,
        ConsoleColorWriter color)
    {
        var outputDir = sessionOptions.OutputDirectory ?? workspace.OutputDirectory;
        var showAlts = args.Any(a => a.Equals("--alternatives", StringComparison.OrdinalIgnoreCase) || a.Equals("--alts", StringComparison.OrdinalIgnoreCase));
        var targetArg = args.FirstOrDefault(a => !a.StartsWith('-'));
        var targetPath = ResolveSavedMarkdownTarget(targetArg, outputDir);
        PreviewMarkdownFilePath(targetPath, color, includeAlternatives: showAlts || sessionOptions.ShowAlternatives);
        color.WriteMuted("  Tip: Run '/copy simple', '/copy raw', '/copy md', '/copy clean', or '/copy <field>' to copy.");
    }

    private MenuState ExecuteInlineCopyCommand(
        string[] args,
        TextReader input,
        ConsoleColorWriter color,
        MenuState returnState = MenuState.Main)
    {
        if (_lastMarkdownDocuments.Count == 0)
        {
            color.WriteWarning("  [!] No outcome available yet. Run /wizard, /process, /draft, or /preview first.");
            return returnState;
        }

        var items = MarkdownOutcomeRenderer.ExtractSelectableItems(
            _lastMarkdownDocuments,
            _lastSupplementalUserStory,
            _lastSupplementalDescription);

        if (args.Length > 0)
        {
            var query = string.Join(" ", args);
            var matches = InteractiveSelector.FilterOutputItems(items, query);
            if (matches.Count == 1)
            {
                MarkdownOutcomeRenderer.CopySelectableItemToClipboard(_clipboardService, matches[0], color);
                return returnState;
            }

            if (matches.Count > 1)
            {
                color.WriteMuted($"  Multiple items matched '{query}': {string.Join(", ", matches.Select(m => m.Id))}");
                var picked = InteractiveSelector.SelectOutputItemInteractive(matches, input, color, allowInteractiveKeys: false);
                if (picked is not null)
                {
                    MarkdownOutcomeRenderer.CopySelectableItemToClipboard(_clipboardService, picked, color);
                }

                return returnState;
            }

            color.WriteWarning($"  [!] Unknown copy target '{query}'. Try: {string.Join(", ", items.Select(i => i.Id))}.");
            return returnState;
        }

        return PromptCopyFormatLoop(input, color, returnState: returnState, allowWizardPromotion: false);
    }

    private MenuState PromptCopyFormatLoop(
        TextReader input,
        ConsoleColorWriter color,
        MenuState returnState = MenuState.Main,
        bool allowWizardPromotion = false)
    {
        var allItems = MarkdownOutcomeRenderer.ExtractSelectableItems(
            _lastMarkdownDocuments,
            _lastSupplementalUserStory,
            _lastSupplementalDescription);
        var currentItems = allItems;
        var showList = true;

        while (true)
        {
            if (showList)
            {
                color.WriteLine();
                color.WriteSubHeader("Selectable Output Formats & Jira Fields:");
                foreach (var item in currentItems)
                {
                    color.WriteMenuOption(item.NumberKey, item.Label, item.PreviewSummary);
                }

                showList = false;
            }

            color.WriteLine();
            var wizardOptionHint = allowWizardPromotion ? "  [E] Refine/Verbosity in Wizard" : string.Empty;
            color.WriteColored($"Copy [1] Raw  [2] Markdown  [3] Clean Text  [4/n] Simple (Title/---/Desc)  [5..N/name] Field{wizardOptionHint}  [0/b] Back  [q] Quit: ", ConsoleColor.Yellow);
            var copyChoice = input.ReadLine();
            if (copyChoice is null)
            {
                return MenuState.Exit;
            }

            var normalized = copyChoice.Trim().ToLowerInvariant();
            if (normalized is "" or "0" or "b" or "back" or "/back" or "done")
            {
                return returnState;
            }

            if (normalized is "q" or "quit" or "/exit" or "/quit")
            {
                return MenuState.Exit;
            }

            if (allowWizardPromotion && normalized is "e" or "edit" or "refine" or "wizard" or "verbosity")
            {
                return MenuState.WizardOrchestrator;
            }

            if (normalized is "?" or "list" or "all")
            {
                currentItems = allItems;
                showList = true;
                continue;
            }

            var matches = InteractiveSelector.FilterOutputItems(currentItems, copyChoice.Trim());
            if (matches.Count == 1)
            {
                MarkdownOutcomeRenderer.CopySelectableItemToClipboard(_clipboardService, matches[0], color);
                currentItems = allItems;
                continue;
            }

            if (matches.Count > 1)
            {
                currentItems = matches;
                showList = true;
                continue;
            }

            color.WriteWarning($"  [!] No format or field matched '{copyChoice.Trim()}'. Choose 1-{allItems.Count}, field name, 0/b, or q.");
            currentItems = allItems;
        }
    }

    private void PreviewMarkdownFilePath(
        string filePath,
        ConsoleColorWriter color,
        bool includeAlternatives = false)
    {
        var markdown = File.ReadAllText(filePath);
        _lastMarkdownDocuments = [markdown];
        _lastSupplementalUserStory = null;
        _lastSupplementalDescription = null;

        color.WriteMuted($"  Reading Markdown: {filePath}");
        MarkdownOutcomeRenderer.WriteOutcomeTable(
            color,
            _lastMarkdownDocuments,
            includeAlternatives: includeAlternatives);
    }

    private void CacheWizardTickets(
        ProcessedOutcome outcome,
        IReadOnlyList<JiraBacklogTicket> tickets,
        bool combinedOutput)
    {
        var resolvedType = tickets[0].BacklogType;
        _lastMarkdownDocuments = tickets.Select(t => t.FormattedMarkdown).ToArray();
        _lastSupplementalUserStory = AsIsV1Workflow.ResolveSupplementalUserStory(outcome, resolvedType, combinedOutput);
        _lastSupplementalDescription = AsIsV1Workflow.ResolveSupplementalDescription(outcome, resolvedType, combinedOutput);
    }

    private void RenderAndCacheWizardTickets(
        ProcessedOutcome outcome,
        IReadOnlyList<JiraBacklogTicket> tickets,
        bool combinedOutput,
        ConsoleColorWriter color,
        bool includeAlternatives = true)
    {
        CacheWizardTickets(outcome, tickets, combinedOutput);
        MarkdownOutcomeRenderer.WriteOutcomeTable(
            color,
            _lastMarkdownDocuments,
            _lastSupplementalUserStory,
            _lastSupplementalDescription,
            includeAlternatives: includeAlternatives);
    }

    private void CopyCurrentOutcomeToClipboard(bool asMarkdown, ConsoleColorWriter color) =>
        MarkdownOutcomeRenderer.CopyOutcomeToClipboard(
            _clipboardService,
            asMarkdown,
            _lastMarkdownDocuments,
            _lastSupplementalUserStory,
            _lastSupplementalDescription,
            color);

    public static IReadOnlyList<string> GetSavedMarkdownFiles(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(outputDirectory, "*.md")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string ResolveSavedMarkdownTarget(string? target, string outputDirectory)
    {
        var savedFiles = GetSavedMarkdownFiles(outputDirectory);
        if (string.IsNullOrWhiteSpace(target) ||
            target.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            var canonical = Path.Combine(outputDirectory, "jira-backlog-item.md");
            if (savedFiles.Count > 0)
            {
                return savedFiles[0];
            }

            if (File.Exists(canonical))
            {
                return canonical;
            }

            throw new FileNotFoundException(
                $"No saved Markdown outcome files (.md) found in '{outputDirectory}'.",
                outputDirectory);
        }

        var cleanTarget = target.Trim().TrimStart('@');
        if (File.Exists(cleanTarget))
        {
            return Path.GetFullPath(cleanTarget);
        }

        var candidate = Path.Combine(outputDirectory, cleanTarget);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        if (!cleanTarget.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var withExt = Path.Combine(outputDirectory, cleanTarget + ".md");
            if (File.Exists(withExt))
            {
                return withExt;
            }
        }

        var partial = savedFiles.FirstOrDefault(f =>
            Path.GetFileName(f).Contains(cleanTarget, StringComparison.OrdinalIgnoreCase));
        if (partial is not null)
        {
            return partial;
        }

        throw new FileNotFoundException($"Markdown outcome file '{target}' not found in '{outputDirectory}'.", cleanTarget);
    }

    private string ResolveInlineContextMentions(
        string text,
        WorkspaceDirectories workspace,
        ConsoleColorWriter color,
        out List<string> missingMentions)
    {
        missingMentions = [];
        if (!text.Contains('@'))
        {
            return text;
        }

        var tokens = TokenizeCommandLine(text);
        var attachedFiles = new List<string>();
        var remainingWords = new List<string>();

        foreach (var token in tokens)
        {
            if (token.StartsWith('@') && token.Length > 1)
            {
                var resolved = _workspaceService.TryResolveContextFile(token, workspace);
                if (resolved is not null)
                {
                    attachedFiles.Add(resolved);
                    color.WriteSuccess($"  ✓ Attached context '@{Path.GetFileName(resolved)}' ({new FileInfo(resolved).Length:N0} bytes)");
                }
                else
                {
                    missingMentions.Add(token);
                }
            }
            else
            {
                remainingWords.Add(token);
            }
        }

        if (attachedFiles.Count == 0)
        {
            return text;
        }

        var goalHeadline = remainingWords.Count > 0
            ? string.Join(" ", remainingWords)
            : Path.GetFileNameWithoutExtension(attachedFiles[0]);

        return BuildGoalWithAttachedFiles(goalHeadline, attachedFiles);
    }

    private string BuildGoalWithAttachedFiles(string goalHeadline, IReadOnlyList<string> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Goal: {goalHeadline.Trim()}");
        foreach (var file in files)
        {
            var content = _collector.CollectFromFile(file);
            sb.AppendLine();
            sb.AppendLine($"--- Context (@{Path.GetFileName(file)}) ---");
            sb.AppendLine(content);
        }

        return sb.ToString().TrimEnd();
    }

    private void ListAvailableContextFiles(WorkspaceDirectories workspace, ConsoleColorWriter color)
    {
        var files = _workspaceService.GetInputFiles(workspace.InputDirectory);
        color.WriteLine();
        color.WriteSubHeader($"Available @context files in {workspace.InputDirectory}:");
        if (files.Count == 0)
        {
            color.WriteMuted("  No .txt or .md files in ./in. You can also reference @samples/raw-notes.txt.");
            return;
        }

        foreach (var file in files)
        {
            color.WriteLine($"  @{Path.GetFileName(file)} ({new FileInfo(file).Length:N0} bytes)");
        }
    }

    private void WarnMissingContextFiles(IReadOnlyList<string> missing, WorkspaceDirectories workspace, ConsoleColorWriter color)
    {
        color.WriteWarning($"  [!] Could not resolve context reference(s): {string.Join(", ", missing)}");
        ListAvailableContextFiles(workspace, color);
    }

    private static void WriteReplHelp(ConsoleColorWriter color)
    {
        color.WriteLine();
        color.WriteHeader("Interactive REPL Commands & Context Mentions");
        color.WriteLine("  Numbers        [1] Wizard Orchestrator  [2] Draft Goal  [3] Process Files  [4] Preview (.md)  [5] Settings  [0] Exit");
        color.WriteLine("  Shortcuts      [w] Wizard   [d] Draft   [p] Process   [v] Preview   [s] Settings   [c] Copy   [?] Help   [q] Quit");
        color.WriteLine("  Navigation     Enter [0/b/back] in any submenu to go back, or [q/quit] to exit anytime");
        color.WriteLine("  /wizard        Open the unified Wizard Orchestrator hub offering ALL workflow, preview, copy & settings options");
        color.WriteLine("  /process       Open Process menu, or run inline: /process [all|<file>] [--dry-run] [--json]");
        color.WriteLine("  /draft         Open Guided Draft Wizard, or inline: /draft \"<goal>\" [-t 01|02|both] [--details \"...\"] [--search] [--preview]");
        color.WriteLine("  /preview       Fuzzy-pick or preview saved './out/*.md' outcome table & copy/refine: /preview [filename]");
        color.WriteLine("  /copy          Copy outcome or field: /copy [simple|raw|md|clean|title|story|desc|ac|alt1..]");
        color.WriteLine("  /verbosity     Set persisted default verbosity: /verbosity [min|bal|det]");
        color.WriteLine("  /status        Show crisp WSL2/Windows paths, queued files, & AI provider readiness table");
        color.WriteLine("  /config        Show or update settings: /config [--json on|off] [--in <dir>] [--out <dir>] [--verbosity min|bal|det]");
        color.WriteLine("  /provider      Switch AI orchestrator provider (gemini, copilot-sdk, azure, claude, manual)");
        color.WriteLine("  /models        List available models for the active provider");
        color.WriteLine("  /theme         Switch CLI theme (agy, uv, kubectl)");
        color.WriteLine("  /json [on|off] Toggle dual .md + .json artifact export");
        color.WriteLine("  /live [on|off] Toggle live AI calls vs offline deterministic fallback");
        color.WriteLine("  @filename      Attach file context directly, e.g. '@raw-notes.txt' or '@raw-notes.txt Add auth story'");
        color.WriteLine("  /clear, /exit  Clear screen or exit session");
    }

    private static (string BacklogType, bool CombinedOutput, OutcomeVerbosity Verbosity) ParseGuidedFormatAndVerbosity(
        string? rawChoice,
        OutcomeVerbosity defaultVerbosity)
    {
        if (string.IsNullOrWhiteSpace(rawChoice))
        {
            return ("Type01", false, defaultVerbosity);
        }

        var parts = rawChoice.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (backlogType, combinedOutput) = ParseGuidedFormatChoice(parts[0]);
        var verbosity = defaultVerbosity;

        for (var i = 1; i < parts.Length; i++)
        {
            if (TryExtractStandaloneVerbosityToken(parts[i], out var parsedVerbosity))
            {
                verbosity = parsedVerbosity;
            }
        }

        if (parts.Length == 1 && TryExtractStandaloneVerbosityToken(parts[0], out var directVerbosity))
        {
            verbosity = directVerbosity;
        }

        return (backlogType, combinedOutput, verbosity);
    }

    private static bool TryExtractStandaloneVerbosityToken(string? input, out OutcomeVerbosity verbosity)
    {
        verbosity = OutcomeVerbosity.Balanced;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        switch (input.Trim().ToLowerInvariant())
        {
            case "min" or "minimal" or "minimalistic" or "--minimal" or "--minimalistic":
                verbosity = OutcomeVerbosity.Minimalistic;
                return true;
            case "bal" or "balanced" or "--balanced":
                verbosity = OutcomeVerbosity.Balanced;
                return true;
            case "det" or "detailed" or "--detailed":
                verbosity = OutcomeVerbosity.Detailed;
                return true;
            default:
                return false;
        }
    }

    private static (string BacklogType, bool CombinedOutput) ParseGuidedFormatChoice(string? rawChoice) =>
        rawChoice?.Trim().ToLowerInvariant() switch
        {
            "2" or "02" or "type02" or "type 02" or "schedule" => ("Type02", false),
            "3" or "both" or "all" => ("Type01", true),
            _ => ("Type01", false)
        };

    private static string? MergeDetails(string? first, string? second)
    {
        var hasFirst = !string.IsNullOrWhiteSpace(first);
        var hasSecond = !string.IsNullOrWhiteSpace(second);
        if (!hasFirst && !hasSecond)
        {
            return null;
        }

        if (hasFirst && hasSecond)
        {
            return $"{first!.Trim()}\n{second!.Trim()}";
        }

        return (hasFirst ? first : second)!.Trim();
    }

    internal static List<string> TokenizeCommandLine(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;

        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (quote.HasValue)
            {
                if (ch == quote.Value)
                {
                    quote = null;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static bool IsValidThemeToken(string token) =>
        token.Trim().ToLowerInvariant() is "1" or "agy" or "gemini" or "gemini-agy" or "default"
            or "2" or "uv" or "astral" or "astral-uv"
            or "3" or "kubectl" or "k8s" or "kube";

    private static bool ParseBooleanToggle(string token, bool fallback) =>
        token.Trim().ToLowerInvariant() switch
        {
            "on" or "true" or "1" or "yes" => true,
            "off" or "false" or "0" or "no" => false,
            _ => fallback
        };

    private static bool? ParseOptionalJsonFlag(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--no-json", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("--no-json-out", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (args[i].Equals("--json", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("--json-out", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    var val = args[i + 1].Trim().ToLowerInvariant();
                    if (val is "on" or "true" or "1" or "yes")
                    {
                        return true;
                    }

                    if (val is "off" or "false" or "0" or "no")
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        return null;
    }

    private static string? GetInlineFlagValue(string[] args, params string[] flags)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            foreach (var flag in flags)
            {
                if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
        }

        return null;
    }

    private static List<string> ExtractPositionalTokens(string[] args, params string[] flagsWithValues)
    {
        var positional = new List<string>();
        var valueFlags = new HashSet<string>(flagsWithValues, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (valueFlags.Contains(token))
            {
                if (token.Equals("--json", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && args[i + 1].ToLowerInvariant() is "on" or "off" or "true" or "false" or "1" or "0" or "yes" or "no")
                    {
                        i++;
                    }
                }
                else
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

        return positional;
    }

    private static MenuState WarnAndStay(
        ConsoleColorWriter color,
        string choice,
        string expected,
        MenuState state)
    {
        color.WriteWarning($"[!] Invalid option '{choice.Trim()}'. Choose {expected}.");
        return state;
    }
}
