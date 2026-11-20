using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
        BacklogOrchestrator,
        CreateBacklog,
        ProcessFiles,
        DraftItem,
        Settings,
        ThemeSelector,
        ProviderSelector,
        ModelSelector,
        LogLevelSelector,
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
    private readonly StderrLoggerFactory? _loggerFactory;
    private readonly ILogger<CliMenuRunner> _logger;
    private readonly Func<string, string?> _environmentReader;
    private readonly RawDataCollector _collector;
    private IReadOnlyList<string> _lastMarkdownDocuments = [];
    private string? _lastSupplementalUserStory;
    private string? _lastSupplementalDescription;
    private MenuState _subMenuReturnState = MenuState.Main;
    private MenuState _createBacklogReturnState = MenuState.Main;
    private bool _previewReturnToOrchestrator;
    private string _lastSelectedMainKey = "1";
    private string _lastSelectedOrchestratorKey = "1";
    private string _lastSelectedCreateKey = "1";
    private string _lastSelectedProcessKey = "1";
    private string _lastSelectedSettingsKey = "1";

    public CliMenuRunner(
        AsIsV1Workflow workflow,
        WorkspacePathService? workspaceService = null,
        RuntimeEnvironmentService? runtimeEnvService = null,
        GeminiSecretProvider? secretProvider = null,
        AiModelOrchestrator? orchestrator = null,
        CliSettingsService? settingsService = null,
        WorkspaceDiagnosticService? diagnosticService = null,
        IClipboardService? clipboardService = null,
        StderrLoggerFactory? loggerFactory = null,
        Func<string, string?>? environmentReader = null)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _workspaceService = workspaceService ?? new WorkspacePathService();
        _runtimeEnvService = runtimeEnvService ?? new RuntimeEnvironmentService();
        _secretProvider = secretProvider ?? new GeminiSecretProvider();
        _orchestrator = orchestrator ?? new AiModelOrchestrator();
        _settingsService = settingsService ?? new CliSettingsService();
        _clipboardService = clipboardService ?? new OsClipboardService();
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<CliMenuRunner>() ?? NullLogger<CliMenuRunner>.Instance;
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
        _collector = new RawDataCollector(loggerFactory?.CreateLogger<RawDataCollector>());
        _diagnosticService = diagnosticService ?? new WorkspaceDiagnosticService(
            _workspaceService,
            _runtimeEnvService,
            _secretProvider,
            _settingsService,
            _orchestrator,
            _workflow.ProviderOrchestrator,
            _environmentReader);
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

        var persistedProvider = persistedSettings.PreferredProvider ?? AiProviderType.GoogleGemini;
        var switchedProviderWithoutModel = !string.IsNullOrWhiteSpace(baseOptions.ProviderInput) &&
                                           string.IsNullOrWhiteSpace(baseOptions.ModelName) &&
                                           resolvedProvider != persistedProvider;

        var currentSettings = persistedSettings with
        {
            JsonFileOut = baseOptions.JsonFileOut ?? persistedSettings.JsonFileOut,
            Theme = resolvedTheme,
            ModelName = !string.IsNullOrWhiteSpace(baseOptions.ModelName)
                ? baseOptions.ModelName
                : switchedProviderWithoutModel
                    ? null
                    : persistedSettings.ModelName,
            GeminiLive = baseOptions.GeminiLive ?? persistedSettings.GeminiLive ?? true,
            PreferredProvider = resolvedProvider,
            DefaultVerbosity = persistedSettings.DefaultVerbosity,
            LogLevel = persistedSettings.LogLevel,
            LogFormat = persistedSettings.LogFormat
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
            Verbosity = resolvedVerbosity,
            PersistedOpenAiBaseUrl = currentSettings.OpenAiBaseUrl,
            PersistedOpenAiWireApi = currentSettings.OpenAiWireApi
        };

        SyncActiveLogging(sessionOptions, currentSettings);

        var state = MenuState.Main;
        while (state != MenuState.Exit)
        {
            state = state switch
            {
                MenuState.Main => ShowMainMenu(baseWorkspace, ref workspace, ref sessionOptions, ref currentSettings, input, output, color),
                MenuState.BacklogOrchestrator => ShowBacklogOrchestratorMenu(baseWorkspace, ref workspace, ref sessionOptions, ref currentSettings, input, output, color),
                MenuState.CreateBacklog => ShowCreateGenerateBacklogMenu(baseWorkspace, workspace, ref sessionOptions, ref currentSettings, input, output, color, _createBacklogReturnState),
                MenuState.ProcessFiles => ShowProcessFilesMenu(workspace, ref sessionOptions, ref currentSettings, baseWorkspace, input, output, color, _subMenuReturnState),
                MenuState.DraftItem => RunGuidedDraftWizard(workspace, ref sessionOptions, ref currentSettings, baseWorkspace, input, output, color, _subMenuReturnState),
                MenuState.Settings => ShowSettingsMenu(baseWorkspace, ref workspace, ref sessionOptions, ref currentSettings, input, color, _subMenuReturnState),
                MenuState.ThemeSelector => ShowThemeMenu(baseWorkspace, ref sessionOptions, ref currentSettings, input, color),
                MenuState.ProviderSelector => ShowProviderMenu(baseWorkspace, ref sessionOptions, ref currentSettings, input, color),
                MenuState.ModelSelector => ShowModelMenu(baseWorkspace, ref sessionOptions, ref currentSettings, input, color),
                MenuState.LogLevelSelector => ShowLogLevelMenu(baseWorkspace, ref sessionOptions, ref currentSettings, input, color),
                MenuState.PreviewOutcome => ShowPreviewOutcomeMenu(workspace, ref sessionOptions, ref currentSettings, baseWorkspace, input, output, color, _subMenuReturnState, _previewReturnToOrchestrator),
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
        var previewArtifacts = CollectPreviewArtifacts(workspace, sessionOptions);
        var themeCode = ConsoleColorWriter.GetThemeCode(currentSettings.Theme);
        var activeProvider = _diagnosticService.ResolveEffectiveProvider(sessionOptions, currentSettings);
        var providerCode = RuntimeEnvironmentService.FormatProviderCode(activeProvider);
        var modelLabel = currentSettings.ModelName ?? ResolveDefaultModelLabel(activeProvider);

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
        color.WriteMuted("  Tip: ↑/↓ + Enter or [0-4] │ Shortcuts [w/g/d/p/v/s/c/?/q] │ /commands │ @filename");
        color.WriteLine();

        var (valueBadgeFg, valueBadgeBg) = color.GetValueBadgeColors();
        var previewBadge = _lastMarkdownDocuments.Count > 0
            ? "session ready"
            : previewArtifacts.Count > 0
                ? $"{previewArtifacts.Count} ready"
                : "empty";

        var options = new List<HighlightedMenuOption>
        {
            new("1", "Backlog Assisted Orchestrator", "recommended", ConsoleColor.Black, ConsoleColor.Yellow, "All-in-one: Draft, Process, Preview, Copy & Settings (/wizard)", IsSelectedValue: string.Equals(_lastSelectedMainKey, "1", StringComparison.Ordinal)),
            new("2", "Create / Generate Backlog", $"{files.Count} in ./in", valueBadgeFg, valueBadgeBg, "Draft from Goal/Title or process ./in files (/generate)", IsSelectedValue: string.Equals(_lastSelectedMainKey, "2", StringComparison.Ordinal)),
            new("3", "Preview", previewBadge, valueBadgeFg, valueBadgeBg, "Active draft, ./out/*.md, .json & ./in notes (/preview)", IsSelectedValue: string.Equals(_lastSelectedMainKey, "3", StringComparison.Ordinal)),
            new("4", "Workspace & Settings", $"{providerCode} • {modelLabel}", valueBadgeFg, valueBadgeBg, "Provider, model, live AI, verbosity, JSON & theme (/settings)", IsSelectedValue: string.Equals(_lastSelectedMainKey, "4", StringComparison.Ordinal)),
            new("0", "Exit", null, null, null, "Close CLI session (/exit)")
        };

        var initialIndex = InteractiveSelector.FindOptionIndexByKey(options, _lastSelectedMainKey);
        var choice = InteractiveSelector.SelectMenuOptionWithHighlight(
            options,
            initialIndex,
            "backlog > ",
            input,
            color);
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
            case "1" or "w" or "wizard" or "orchestrator" or "bao":
                _lastSelectedMainKey = "1";
                return MenuState.BacklogOrchestrator;
            case "2" or "g" or "generate" or "create":
                _lastSelectedMainKey = "2";
                _createBacklogReturnState = MenuState.Main;
                return MenuState.CreateBacklog;
            case "d" or "draft":
                _lastSelectedMainKey = "2";
                _subMenuReturnState = MenuState.Main;
                return MenuState.DraftItem;
            case "p" or "process":
                _lastSelectedMainKey = "2";
                _subMenuReturnState = MenuState.Main;
                return MenuState.ProcessFiles;
            case "3" or "v" or "preview" or "view":
                _lastSelectedMainKey = "3";
                _subMenuReturnState = MenuState.Main;
                _previewReturnToOrchestrator = false;
                return MenuState.PreviewOutcome;
            case "4" or "s" or "settings":
                _lastSelectedMainKey = "4";
                _subMenuReturnState = MenuState.Main;
                return MenuState.Settings;
            case "c" or "copy":
                return ExecuteInlineCopyCommand([], input, color, returnState: MenuState.Main);
            case "?" or "h" or "help":
                WriteReplHelp(color);
                return MenuState.Main;
            default:
                return WarnAndStay(color, trimmed, "0-4, [w/g/d/p/v/s/c/q], /command, or @filename", MenuState.Main);
        }
    }

    private MenuState ShowBacklogOrchestratorMenu(
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
        var modelLabel = currentSettings.ModelName ?? ResolveDefaultModelLabel(activeProvider);

        var activeLogging = ResolveActiveLogging(sessionOptions, currentSettings);
        color.WriteLine();
        color.WriteHeader("=== Backlog Assisted Orchestrator ===");
        color.WriteMuted($"  Workspace : In ./in ({inputFiles.Count} queued) │ Out ./out ({savedFiles.Count} saved) │ JSON: {(currentSettings.JsonFileOut ? "ON" : "OFF")}");
        color.WriteMuted($"  Pipeline  : {providerCode} ({modelLabel}) │ Live: {(currentSettings.GeminiLive == true ? "ON" : "OFF")} │ Verbosity: {DescriptionFormatter.FormatVerbosityLabel(sessionOptions.Verbosity)} │ Log: {CliLogLevelResolver.FormatLogLevelDisplay(activeLogging.Level)} │ Theme: {ConsoleColorWriter.GetThemeCode(currentSettings.Theme)}");
        if (_lastMarkdownDocuments.Count > 0)
        {
            var lastParsed = MarkdownOutcomeRenderer.ParseMarkdown(_lastMarkdownDocuments[0]);
            var lastTitle = !string.IsNullOrWhiteSpace(lastParsed.TitleLine)
                ? MarkdownOutcomeRenderer.StripMarkdownFormatting(lastParsed.TitleLine)
                : "Active outcome";
            color.WriteMuted($"  Session   : Active -> '{lastTitle}'");
        }

        color.WriteMuted("  Tip: ↑/↓ + Enter or [0-6] [d/p/v/c/s/?/q] │ '@filename' to review file │ '/command'");
        color.WriteLine();

        var (valueBadgeFg, valueBadgeBg) = color.GetValueBadgeColors();
        var options = new List<HighlightedMenuOption>
        {
            new("1", "Draft from Goal / Title", null, null, null, "Type 01 / 02 / Both -> Verbosity, Refine & Copy", IsSelectedValue: string.Equals(_lastSelectedOrchestratorKey, "1", StringComparison.Ordinal)),
            new("2", "Process Input Files (./in)", $"{inputFiles.Count} queued", valueBadgeFg, valueBadgeBg, "All files, Single (Review), or Dry Run", IsSelectedValue: string.Equals(_lastSelectedOrchestratorKey, "2", StringComparison.Ordinal)),
            new("3", "Preview Workspace & Outcomes", $"{savedFiles.Count} saved", valueBadgeFg, valueBadgeBg, "Active draft, ./out/*.md, .json & ./in notes", IsSelectedValue: string.Equals(_lastSelectedOrchestratorKey, "3", StringComparison.Ordinal)),
            new("4", "Quick Copy Last Outcome", _lastMarkdownDocuments.Count > 0 ? "ready" : null, ConsoleColor.Black, ConsoleColor.Green, "Simple [N], Markdown, Raw Table, Clean, or Field", IsSelectedValue: string.Equals(_lastSelectedOrchestratorKey, "4", StringComparison.Ordinal)),
            new("5", "Workspace & Settings", $"{providerCode} • {modelLabel}", valueBadgeFg, valueBadgeBg, "Provider, model, verbosity, JSON, theme & files", IsSelectedValue: string.Equals(_lastSelectedOrchestratorKey, "5", StringComparison.Ordinal)),
            new("6", "Status & AI Diagnostics", null, null, null, "WSL2/Windows paths & AI Provider Matrix (/status)", IsSelectedValue: string.Equals(_lastSelectedOrchestratorKey, "6", StringComparison.Ordinal)),
            new("0", "Back to Main Menu", null, null, null, "Return to root prompt (or [q]uit)")
        };

        var initialIndex = InteractiveSelector.FindOptionIndexByKey(options, _lastSelectedOrchestratorKey);
        var choice = InteractiveSelector.SelectMenuOptionWithHighlight(
            options,
            initialIndex,
            "orchestrator > ",
            input,
            color);
        if (choice is null)
        {
            return MenuState.Exit;
        }

        var trimmed = choice.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return MenuState.BacklogOrchestrator;
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
                returnState: MenuState.BacklogOrchestrator);
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
                        returnState: MenuState.BacklogOrchestrator);
                }

                WarnMissingContextFiles([tokens[0]], workspace, color);
                return MenuState.BacklogOrchestrator;
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
            return MenuState.BacklogOrchestrator;
        }

        switch (trimmed.ToLowerInvariant())
        {
            case "0" or "b" or "back":
                return MenuState.Main;
            case "q" or "quit" or "exit":
                return MenuState.Exit;
            case "1" or "d" or "draft":
                _lastSelectedOrchestratorKey = "1";
                _subMenuReturnState = MenuState.BacklogOrchestrator;
                return MenuState.DraftItem;
            case "2" or "p" or "process":
                _lastSelectedOrchestratorKey = "2";
                _subMenuReturnState = MenuState.BacklogOrchestrator;
                return MenuState.ProcessFiles;
            case "3" or "v" or "preview" or "view":
                _lastSelectedOrchestratorKey = "3";
                _subMenuReturnState = MenuState.BacklogOrchestrator;
                _previewReturnToOrchestrator = true;
                return MenuState.PreviewOutcome;
            case "4" or "c" or "copy":
                _lastSelectedOrchestratorKey = "4";
                if (_lastMarkdownDocuments.Count == 0 && savedFiles.Count > 0)
                {
                    PreviewMarkdownFilePath(savedFiles[0], color, includeAlternatives: sessionOptions.ShowAlternatives);
                }

                return ExecuteInlineCopyCommand([], input, color, returnState: MenuState.BacklogOrchestrator);
            case "5" or "s" or "settings":
                _lastSelectedOrchestratorKey = "5";
                _subMenuReturnState = MenuState.BacklogOrchestrator;
                return MenuState.Settings;
            case "6" or "status" or "doctor":
                _lastSelectedOrchestratorKey = "6";
                color.WriteLine();
                _diagnosticService.RenderStatusTable(sessionOptions, output, currentSettings);
                return MenuState.BacklogOrchestrator;
            case "?" or "h" or "help":
                WriteReplHelp(color);
                return MenuState.BacklogOrchestrator;
            default:
                return WarnAndStay(color, trimmed, "0-6, [d/p/v/c/s/b/q], /command, or @filename", MenuState.BacklogOrchestrator);
        }
    }

    private MenuState ShowCreateGenerateBacklogMenu(
        WorkspaceDirectories baseWorkspace,
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color,
        MenuState returnState = MenuState.Main)
    {
        _workspaceService.EnsureDirectoriesExist(workspace);
        var files = _workspaceService.GetInputFiles(workspace.InputDirectory);
        var activeProvider = _diagnosticService.ResolveEffectiveProvider(sessionOptions, currentSettings);
        var providerCode = RuntimeEnvironmentService.FormatProviderCode(activeProvider);
        var modelLabel = currentSettings.ModelName ?? ResolveDefaultModelLabel(activeProvider);

        color.WriteLine();
        color.WriteHeader("=== Create / Generate Backlog ===");
        color.WriteMuted($"  Source    : In ./in ({files.Count} file(s)) │ Out ./out │ Provider: {providerCode} ({modelLabel})");
        color.WriteMuted("  Tip: ↑/↓ + Enter or [1-3, d=draft, p=process, @filename, 0/b=back]");
        color.WriteLine();

        var (valueBadgeFg, valueBadgeBg) = color.GetValueBadgeColors();
        var options = new List<HighlightedMenuOption>
        {
            new("1", "Draft from Goal / Title", "goal", valueBadgeFg, valueBadgeBg, "Quick Type 01 Story, Type 02 Schedule, or Both (/draft)", IsSelectedValue: string.Equals(_lastSelectedCreateKey, "1", StringComparison.Ordinal)),
            new("2", "Process Input Files (./in)", $"{files.Count} queued", valueBadgeFg, valueBadgeBg, "Batch all, pick single file review, or Dry Run (/process)", IsSelectedValue: string.Equals(_lastSelectedCreateKey, "2", StringComparison.Ordinal)),
            new("3", "Dry Run / Scan Workspace", "preview only", valueBadgeFg, valueBadgeBg, "Scan & preview ./in items without writing files", IsSelectedValue: string.Equals(_lastSelectedCreateKey, "3", StringComparison.Ordinal)),
            new("0", BackToLabel(returnState))
        };

        var initialIndex = InteractiveSelector.FindOptionIndexByKey(options, _lastSelectedCreateKey);
        var choice = InteractiveSelector.SelectMenuOptionWithHighlight(
            options,
            initialIndex,
            "Select creation source (1-3, ↑/↓, b=back, q=quit, or @filename): ",
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
            return returnState;
        }

        if (normalized is "q" or "quit" or "/exit" or "/quit")
        {
            return MenuState.Exit;
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
                returnState: MenuState.CreateBacklog);
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
                        returnState: MenuState.CreateBacklog);
                }
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
            return MenuState.CreateBacklog;
        }

        switch (normalized)
        {
            case "1" or "d" or "draft" or "goal":
                _lastSelectedCreateKey = "1";
                _subMenuReturnState = MenuState.CreateBacklog;
                return MenuState.DraftItem;
            case "2" or "p" or "process" or "files" or "file":
                _lastSelectedCreateKey = "2";
                _subMenuReturnState = MenuState.CreateBacklog;
                return MenuState.ProcessFiles;
            case "3" or "dry" or "scan" or "dry-run":
                _lastSelectedCreateKey = "3";
                ProcessFiles(files, dryRun: true, baseWorkspace, ref sessionOptions, ref currentSettings, input, output, color);
                return MenuState.CreateBacklog;
            default:
                return WarnAndStay(color, choice, "0-3, d=draft, p=process, b=back, or @filename", MenuState.CreateBacklog);
        }
    }

    private static string BackToLabel(MenuState returnState) => returnState switch
    {
        MenuState.BacklogOrchestrator => "Back to Backlog Assisted Orchestrator",
        MenuState.CreateBacklog => "Back to Create / Generate Backlog",
        _ => "Back to Main Menu"
    };

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

                case "/generate" or "/create" or "/new":
                    _createBacklogReturnState = returnState;
                    return MenuState.CreateBacklog;

                case "/wizard" or "/guided" or "/orchestrator" or "/bao":
                    return MenuState.BacklogOrchestrator;

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

                    var previousProvider = currentSettings.PreferredProvider ?? AiProviderType.GoogleGemini;
                    var resolved = _orchestrator.ResolveProvider(args[0]);
                    currentSettings = currentSettings with
                    {
                        PreferredProvider = resolved,
                        ModelName = resolved != previousProvider ? null : currentSettings.ModelName
                    };
                    sessionOptions = sessionOptions with
                    {
                        ProviderInput = RuntimeEnvironmentService.FormatProviderCode(resolved),
                        ModelName = currentSettings.ModelName
                    };
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
                    var activeLogging = ResolveActiveLogging(sessionOptions, currentSettings);
                    var currentlyVerbose = activeLogging.Level <= LogLevel.Debug;
                    var nextVerbose = args.Length == 0
                        ? !currentlyVerbose
                        : ParseBooleanToggle(args[0], currentlyVerbose);
                    sessionOptions = nextVerbose
                        ? sessionOptions with { Verbose = true, Quiet = false, LogLevelInput = "debug" }
                        : sessionOptions with { Verbose = false, Quiet = false, LogLevelInput = null };
                    var resolvedAfter = SyncActiveLogging(sessionOptions, currentSettings);
                    color.WriteSuccess(
                        $"Verbose pipeline diagnostics are now {(nextVerbose ? "ON" : "OFF")} " +
                        $"(Log Level: {CliLogLevelResolver.FormatLogLevelDisplay(resolvedAfter.Level)} -> stderr).");
                    return returnState;
                }

                case "/log-level" or "/loglevel" or "/log":
                {
                    if (args.Length == 0)
                    {
                        _subMenuReturnState = returnState;
                        return MenuState.LogLevelSelector;
                    }

                    var token = args[0].Trim();
                    if (token.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                        token.Equals("reset", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSettings = currentSettings with { LogLevel = null };
                        sessionOptions = sessionOptions with { LogLevelInput = null, Verbose = false, Quiet = false };
                        var resolved = SyncActiveLogging(sessionOptions, currentSettings);
                        var savedResetPath = _settingsService.Save(baseWorkspace, currentSettings);
                        color.WriteSuccess(
                            $"Log Level reset to {CliLogLevelResolver.FormatLogLevelDisplay(resolved.Level)} ({resolved.LevelSource}, stderr). " +
                            $"Saved settings to '{savedResetPath}'.");
                        return returnState;
                    }

                    if (!CliLogLevelResolver.TryParseLogLevel(token, out var parsedLevel))
                    {
                        color.WriteWarning($"  [!] Unknown log level '{args[0]}'. Choose {CliLogLevelResolver.ValidLevelsDisplay}.");
                        return returnState;
                    }

                    currentSettings = currentSettings with { LogLevel = parsedLevel };
                    sessionOptions = sessionOptions with
                    {
                        LogLevelInput = CliLogLevelResolver.FormatLogLevelCode(parsedLevel),
                        Verbose = parsedLevel <= LogLevel.Debug,
                        Quiet = parsedLevel == LogLevel.None
                    };
                    SyncActiveLogging(sessionOptions, currentSettings);
                    var savedLevelPath = _settingsService.Save(baseWorkspace, currentSettings);
                    color.WriteSuccess(
                        $"Log Level is now {CliLogLevelResolver.FormatLogLevelDisplay(parsedLevel)} " +
                        $"({CliLogLevelResolver.FormatLogFormatCode(ResolveActiveLogging(sessionOptions, currentSettings).Format)} -> stderr). " +
                        $"Saved settings to '{savedLevelPath}'.");
                    return returnState;
                }

                case "/log-format" or "/logformat":
                {
                    var activeLogging = ResolveActiveLogging(sessionOptions, currentSettings);
                    LogOutputFormat nextFormat;
                    if (args.Length == 0)
                    {
                        nextFormat = activeLogging.Format == LogOutputFormat.Text
                            ? LogOutputFormat.Json
                            : LogOutputFormat.Text;
                    }
                    else if (args[0].Trim().Equals("default", StringComparison.OrdinalIgnoreCase) ||
                             args[0].Trim().Equals("reset", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSettings = currentSettings with { LogFormat = null };
                        sessionOptions = sessionOptions with { LogFormatInput = null };
                        var resetResolved = SyncActiveLogging(sessionOptions, currentSettings);
                        var savedResetFormatPath = _settingsService.Save(baseWorkspace, currentSettings);
                        color.WriteSuccess(
                            $"Log Format reset to {CliLogLevelResolver.FormatLogFormatCode(resetResolved.Format)} ({resetResolved.FormatSource}, stderr). " +
                            $"Saved settings to '{savedResetFormatPath}'.");
                        return returnState;
                    }
                    else if (!CliLogLevelResolver.TryParseLogFormat(args[0], out nextFormat))
                    {
                        color.WriteWarning($"  [!] Unknown log format '{args[0]}'. Choose {CliLogLevelResolver.ValidFormatsDisplay}.");
                        return returnState;
                    }

                    currentSettings = currentSettings with { LogFormat = nextFormat };
                    sessionOptions = sessionOptions with { LogFormatInput = CliLogLevelResolver.FormatLogFormatCode(nextFormat) };
                    SyncActiveLogging(sessionOptions, currentSettings);
                    var savedFormatPath = _settingsService.Save(baseWorkspace, currentSettings);
                    color.WriteSuccess(
                        $"Log Format is now {CliLogLevelResolver.FormatLogFormatCode(nextFormat)} (stderr). " +
                        $"Saved settings to '{savedFormatPath}'.");
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
                        _previewReturnToOrchestrator = returnState == MenuState.BacklogOrchestrator;
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
                var nextProvider = _orchestrator.ResolveProvider(providerArg);
                var previousProvider = updated.PreferredProvider ?? AiProviderType.GoogleGemini;
                var hasInlineModel = !string.IsNullOrWhiteSpace(GetInlineFlagValue(args, "--model", "-m"));
                updated = updated with
                {
                    PreferredProvider = nextProvider,
                    ModelName = nextProvider != previousProvider && !hasInlineModel ? null : updated.ModelName
                };
            }

            var modelArg = GetInlineFlagValue(args, "--model", "-m");
            if (!string.IsNullOrWhiteSpace(modelArg))
            {
                updated = updated with
                {
                    ModelName = string.Equals(modelArg, "auto", StringComparison.OrdinalIgnoreCase) ? null : modelArg.Trim()
                };
            }

            var gatewayUrlArg = GetInlineFlagValue(args, "--gateway-url", "--base-url", "--openai-base-url");
            if (!string.IsNullOrWhiteSpace(gatewayUrlArg))
            {
                if (gatewayUrlArg.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                    gatewayUrlArg.Equals("reset", StringComparison.OrdinalIgnoreCase))
                {
                    updated = updated with { OpenAiBaseUrl = null };
                }
                else
                {
                    if (!OpenAiGatewayClient.TryNormalizeBaseUrl(gatewayUrlArg, out var normalizedGatewayUrl, out var urlError))
                    {
                        throw new ArgumentException(urlError ?? $"Invalid OpenAI Gateway URL '{gatewayUrlArg}'.");
                    }

                    updated = updated with { OpenAiBaseUrl = normalizedGatewayUrl };
                }
            }

            var wireApiArg = GetInlineFlagValue(args, "--wire-api", "--gateway-api");
            if (!string.IsNullOrWhiteSpace(wireApiArg))
            {
                updated = updated with
                {
                    OpenAiWireApi = wireApiArg.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                                    wireApiArg.Equals("reset", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : OpenAiGatewayClient.FormatWireApiCode(OpenAiGatewayClient.ParseWireApiOrThrow(wireApiArg, "--wire-api"))
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

            var logLevelArg = GetInlineFlagValue(args, "--log-level");
            if (!string.IsNullOrWhiteSpace(logLevelArg))
            {
                if (logLevelArg.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                    logLevelArg.Equals("reset", StringComparison.OrdinalIgnoreCase))
                {
                    updated = updated with { LogLevel = null };
                }
                else
                {
                    updated = updated with
                    {
                        LogLevel = CliLogLevelResolver.ParseLogLevelOrThrow(logLevelArg, "--log-level")
                    };
                }
            }

            var logFormatArg = GetInlineFlagValue(args, "--log-format");
            if (!string.IsNullOrWhiteSpace(logFormatArg))
            {
                if (logFormatArg.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                    logFormatArg.Equals("reset", StringComparison.OrdinalIgnoreCase))
                {
                    updated = updated with { LogFormat = null };
                }
                else
                {
                    updated = updated with
                    {
                        LogFormat = CliLogLevelResolver.ParseLogFormatOrThrow(logFormatArg, "--log-format")
                    };
                }
            }
        }

        currentSettings = updated;
        workspace = _workspaceService.ApplySettings(baseWorkspace, currentSettings);
        Directory.CreateDirectory(workspace.InputDirectory);
        Directory.CreateDirectory(workspace.OutputDirectory);

        var resetRequested = args.Contains("--reset", StringComparer.OrdinalIgnoreCase);
        var updatedLogLevelArg = GetInlineFlagValue(args, "--log-level");
        var updatedLogFormatArg = GetInlineFlagValue(args, "--log-format");

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
            Verbosity = currentSettings.DefaultVerbosity ?? OutcomeVerbosity.Balanced,
            PersistedOpenAiBaseUrl = currentSettings.OpenAiBaseUrl,
            PersistedOpenAiWireApi = currentSettings.OpenAiWireApi,
            LogLevelInput = resetRequested
                ? null
                : !string.IsNullOrWhiteSpace(updatedLogLevelArg)
                    ? (currentSettings.LogLevel is { } persistedLvl ? CliLogLevelResolver.FormatLogLevelCode(persistedLvl) : null)
                    : sessionOptions.LogLevelInput,
            LogFormatInput = resetRequested
                ? null
                : !string.IsNullOrWhiteSpace(updatedLogFormatArg)
                    ? (currentSettings.LogFormat is { } persistedFmt ? CliLogLevelResolver.FormatLogFormatCode(persistedFmt) : null)
                    : sessionOptions.LogFormatInput,
            Verbose = resetRequested
                ? false
                : !string.IsNullOrWhiteSpace(updatedLogLevelArg)
                    ? currentSettings.LogLevel is { } lvl && lvl <= LogLevel.Debug
                    : sessionOptions.Verbose,
            Quiet = resetRequested
                ? false
                : !string.IsNullOrWhiteSpace(updatedLogLevelArg)
                    ? currentSettings.LogLevel == LogLevel.None
                    : sessionOptions.Quiet
        };

        SyncActiveLogging(sessionOptions, currentSettings);

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
        var (valueBadgeFg, valueBadgeBg) = color.GetValueBadgeColors();
        var options = new List<HighlightedMenuOption>
        {
            new("1", "Process All Files", $"{files.Count} queued", valueBadgeFg, valueBadgeBg, "Batch run all queued ./in files", IsSelectedValue: string.Equals(_lastSelectedProcessKey, "1", StringComparison.Ordinal)),
            new("2", "Select Single File (Orchestrator Review)", null, null, null, "Pick one file -> Preview, Verbosity, Refine & Copy", IsSelectedValue: string.Equals(_lastSelectedProcessKey, "2", StringComparison.Ordinal)),
            new("3", "Dry Run (Preview Only)", null, null, null, "Preview all files without writing", IsSelectedValue: string.Equals(_lastSelectedProcessKey, "3", StringComparison.Ordinal)),
            new("0", BackToLabel(returnState))
        };

        var initialIndex = InteractiveSelector.FindOptionIndexByKey(options, _lastSelectedProcessKey);
        var choice = InteractiveSelector.SelectMenuOptionWithHighlight(
            options,
            initialIndex,
            "Select an option (0-3, ↑/↓, b=back, q=quit, or @filename): ",
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
                _lastSelectedProcessKey = "1";
                ProcessFiles(files, false, baseWorkspace, ref sessionOptions, ref currentSettings, input, output, color);
                return MenuState.ProcessFiles;
            case "2" or "s" or "single":
                _lastSelectedProcessKey = "2";
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
                _lastSelectedProcessKey = "3";
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

        var options = new List<HighlightedMenuOption>(files.Count + 1);
        for (var index = 0; index < files.Count; index++)
        {
            var size = new FileInfo(files[index]).Length;
            options.Add(new HighlightedMenuOption((index + 1).ToString(), Path.GetFileName(files[index]), Description: $"{size:N0} bytes"));
        }

        options.Add(new HighlightedMenuOption("0", "Cancel"));

        var selection = InteractiveSelector.SelectMenuOptionWithHighlight(
            options,
            0,
            $"Select a file (1-{files.Count}, ↑/↓, or 0 to cancel): ",
            input,
            color)?.Trim();
        if (string.IsNullOrEmpty(selection) || selection is "0" or "b" or "back" or "q" or "quit")
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
        MenuState returnState = MenuState.BacklogOrchestrator)
    {
        color.WriteLine();
        color.WriteSubHeader("--- Backlog Assisted Orchestrator: Draft & Refine --- (Preview & Tweak)");
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
        var liveEnabled = currentSettings.GeminiLive ?? (activeProvider is AiProviderType.GitHubCopilot or AiProviderType.OpenAiGateway);

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
        color.WriteSubHeader($"--- Backlog Assisted Orchestrator: Reviewing '{Path.GetFileName(filePath)}' ---");
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

            var lastDecisionKey = "A";
            while (true)
            {
                color.WriteLine();
                color.WriteHeader($"[4/5 Review Decision] (Verbosity: {DescriptionFormatter.FormatVerbosityLabel(verbosity)})");
                var hasAlternatives = latestOutcome?.Alternatives is { Count: > 0 };
                var (valBadgeFg, valBadgeBg) = color.GetValueBadgeColors();
                var decisionOptions = new List<HighlightedMenuOption>();
                if (hasAlternatives)
                {
                    for (var altIdx = 0; altIdx < latestOutcome!.Alternatives!.Count; altIdx++)
                    {
                        var alt = latestOutcome.Alternatives[altIdx];
                        decisionOptions.Add(new HighlightedMenuOption(
                            (altIdx + 1).ToString(),
                            $"Adopt Alternative #{altIdx + 1} ({alt.Angle})",
                            Description: alt.Title));
                    }
                }

                decisionOptions.Add(new("A", $"Accept & Save to '{sessionOptions.OutputDirectory}'", "default", ConsoleColor.Black, ConsoleColor.Green, "Save artifacts to workspace", IsSelectedValue: true));
                decisionOptions.Add(new("N", "Copy Simple Text", null, null, null, "No format: Title / --- / Description"));
                decisionOptions.Add(new("M", "Copy Markdown to clipboard"));
                decisionOptions.Add(new("W", "Copy Raw Text (table) to clipboard"));
                decisionOptions.Add(new("S", "Select & Copy field / Clean Text", null, null, null, "Title, Story, Desc, AC"));
                decisionOptions.Add(new("V", "Outcome Verbosity", DescriptionFormatter.FormatVerbosityLabel(verbosity), valBadgeFg, valBadgeBg, "Minimalistic / Balanced / Detailed", IsSelectedValue: true));
                decisionOptions.Add(new("R", "Refine Title / Description with feedback"));
                decisionOptions.Add(new("T", "Switch format (Type01 / Type02 / Both)", combinedOutput ? "Both" : backlogType, valBadgeFg, valBadgeBg, IsSelectedValue: true));
                decisionOptions.Add(new("P", "Preview only", null, null, null, "Finish without writing files"));
                decisionOptions.Add(new("C", "Cancel / Back"));

                var initialDecisionIndex = InteractiveSelector.FindOptionIndexByKey(
                    decisionOptions,
                    lastDecisionKey,
                    fallbackIndex: hasAlternatives ? latestOutcome!.Alternatives!.Count : 0);
                var actionPrompt = hasAlternatives
                    ? $"Choose action (1-{latestOutcome!.Alternatives!.Count}/A/N/M/W/S/V/R/T/P/C, ↑/↓) [A]: "
                    : "Choose action (A/N/M/W/S/V/R/T/P/C, ↑/↓) [A]: ";

                var action = InteractiveSelector.SelectMenuOptionWithHighlight(
                    decisionOptions,
                    initialDecisionIndex,
                    actionPrompt,
                    input,
                    color);
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
                    lastDecisionKey = altChoice.ToString();
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
                    lastDecisionKey = "N";
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
                    lastDecisionKey = "M";
                    CopyCurrentOutcomeToClipboard(asMarkdown: true, color);
                    continue;
                }

                if (normalizedAction is "w" or "raw" or "text")
                {
                    lastDecisionKey = "W";
                    CopyCurrentOutcomeToClipboard(asMarkdown: false, color);
                    continue;
                }

                if (normalizedAction is "s" or "select")
                {
                    lastDecisionKey = "S";
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
                    lastDecisionKey = "V";
                    OutcomeVerbosity nextVerbosity;
                    if (normalizedAction is "v" or "verbosity")
                    {
                        color.WriteLine();
                        color.WriteSubHeader("Select Outcome Verbosity:");
                        var vChoice = InteractiveSelector.SelectMenuOptionWithHighlight(
                            BuildVerbosityOptions(verbosity),
                            ResolveVerbosityCursorIndex(verbosity),
                            "Choose verbosity (1-3, ↑/↓): ",
                            input,
                            color);
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
                    lastDecisionKey = "R";
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
                    lastDecisionKey = "T";
                    color.WriteLine();
                    color.WriteSubHeader("Switch Backlog Format:");
                    var formatOptions = new List<HighlightedMenuOption>
                    {
                        BuildSelectableValueOption("1", "Type01 Complete User Story", "User story + Given/When/Then scenarios", !combinedOutput && !backlogType.Equals("Type02", StringComparison.OrdinalIgnoreCase)),
                        BuildSelectableValueOption("2", "Type02 Schedule Item", "Concise Title + Objective/Checklist description", !combinedOutput && backlogType.Equals("Type02", StringComparison.OrdinalIgnoreCase)),
                        BuildSelectableValueOption("3", "Both (Type01 + Type02)", "Generate both story and schedule outputs", combinedOutput)
                    };
                    var currentFmtIdx = combinedOutput ? 2 : backlogType.Equals("Type02", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    var switchChoice = InteractiveSelector.SelectMenuOptionWithHighlight(
                        formatOptions,
                        currentFmtIdx,
                        "Switch format (1-3, ↑/↓): ",
                        input,
                        color);
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

        var activeLogging = ResolveActiveLogging(sessionOptions, currentSettings);
        var logLevelBadge = $"{CliLogLevelResolver.FormatLogLevelDisplay(activeLogging.Level)} • {CliLogLevelResolver.FormatLogFormatCode(activeLogging.Format)}";

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
            new("9", "Select Log Level", logLevelBadge, valueBadgeFg, valueBadgeBg, "Trace..None (Quiet) & stderr text/json (/log-level)", IsSelectedValue: true),
            new("0", BackToLabel(returnState))
        };

        var initialIndex = InteractiveSelector.FindOptionIndexByKey(options, _lastSelectedSettingsKey);

        var choice = InteractiveSelector.SelectMenuOptionWithHighlight(
            options,
            initialIndex,
            "Select option (0-9, ↑/↓, b=back, q=quit): ",
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

        if (normalized is "l" or "log" or "log-level" or "logging")
        {
            _lastSelectedSettingsKey = "9";
            return MenuState.LogLevelSelector;
        }

        if (normalized is "1" or "2" or "3" or "4" or "5" or "6" or "7" or "8" or "9")
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
                var vSelection = InteractiveSelector.SelectMenuOptionWithHighlight(
                    BuildVerbosityOptions(sessionOptions.Verbosity, includeCancel: false),
                    ResolveVerbosityCursorIndex(sessionOptions.Verbosity),
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
            case "9":
                return MenuState.LogLevelSelector;
            default:
                return WarnAndStay(color, choice, "0-9, b=back, or q=quit", MenuState.Settings);
        }
    }

    private MenuState ShowLogLevelMenu(
        WorkspaceDirectories baseWorkspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        TextReader input,
        ConsoleColorWriter color)
    {
        var active = ResolveActiveLogging(sessionOptions, currentSettings);
        var nextFormatLabel = active.Format == LogOutputFormat.Text ? "json" : "text";

        color.WriteLine();
        color.WriteHeader("=== Diagnostic Logging Configuration (stderr) ===");
        color.WriteMuted($"Active Level  : {CliLogLevelResolver.FormatLogLevelDisplay(active.Level)} ({active.LevelSource})");
        color.WriteMuted($"Active Format : {CliLogLevelResolver.FormatLogFormatCode(active.Format)} ({active.FormatSource}) — routed to stderr (stdout stays clean)");
        color.WriteLine();

        var options = new List<HighlightedMenuOption>
        {
            BuildSelectableValueOption("1", "Trace", "Deep diagnostic traces including payload/HTTP metrics", active.Level == LogLevel.Trace),
            BuildSelectableValueOption("2", "Debug", "Pipeline telemetry, runtime banner & provider diagnostics (--verbose)", active.Level == LogLevel.Debug),
            BuildSelectableValueOption("3", "Information", "Stage lifecycle milestones (collect, expand, generate, complete)", active.Level == LogLevel.Information),
            BuildSelectableValueOption("4", "Warning (Default)", "Degraded fallbacks & errors only — quiet normal runs", active.Level == LogLevel.Warning),
            BuildSelectableValueOption("5", "Error", "Failures & unhandled exceptions only", active.Level == LogLevel.Error),
            BuildSelectableValueOption("6", "Critical", "Fatal errors only", active.Level == LogLevel.Critical),
            BuildSelectableValueOption("7", "None (Quiet)", "Suppress all diagnostic log events (--quiet)", active.Level == LogLevel.None),
            new("8", $"Toggle stderr Log Format ({CliLogLevelResolver.FormatLogFormatCode(active.Format)} -> {nextFormatLabel})", CliLogLevelResolver.FormatLogFormatCode(active.Format), ConsoleColor.Black, ConsoleColor.Cyan, "Switch between human-readable text and structured NDJSON"),
            new("0", "Back to Workspace & Settings")
        };

        var activeIndex = active.Level switch
        {
            LogLevel.Trace => 0,
            LogLevel.Debug => 1,
            LogLevel.Information => 2,
            LogLevel.Warning => 3,
            LogLevel.Error => 4,
            LogLevel.Critical => 5,
            LogLevel.None => 6,
            _ => 3
        };

        var choice = InteractiveSelector.SelectMenuOptionWithHighlight(
            options,
            activeIndex,
            "Select log level (0-8, ↑/↓, b=back, q=quit): ",
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

        if (normalized is "8" or "format" or "json" or "text")
        {
            var nextFormat = normalized switch
            {
                "json" => LogOutputFormat.Json,
                "text" => LogOutputFormat.Text,
                _ => active.Format == LogOutputFormat.Text ? LogOutputFormat.Json : LogOutputFormat.Text
            };

            currentSettings = currentSettings with { LogFormat = nextFormat };
            sessionOptions = sessionOptions with { LogFormatInput = CliLogLevelResolver.FormatLogFormatCode(nextFormat) };
            SyncActiveLogging(sessionOptions, currentSettings);
            var savedFmtPath = _settingsService.Save(baseWorkspace, currentSettings);
            color.WriteSuccess($"Log Format is now {CliLogLevelResolver.FormatLogFormatCode(nextFormat)} (stderr). Saved settings to '{savedFmtPath}'.");
            return MenuState.LogLevelSelector;
        }

        LogLevel? selectedLevel = normalized switch
        {
            "1" or "trace" or "trc" => LogLevel.Trace,
            "2" or "debug" or "dbg" or "verbose" => LogLevel.Debug,
            "3" or "information" or "info" or "inf" => LogLevel.Information,
            "4" or "warning" or "warn" or "wrn" => LogLevel.Warning,
            "5" or "error" or "err" => LogLevel.Error,
            "6" or "critical" or "crit" => LogLevel.Critical,
            "7" or "none" or "quiet" or "off" => LogLevel.None,
            _ => null
        };

        if (selectedLevel is null)
        {
            return WarnAndStay(color, choice, "0-8, level name, b=back, or q=quit", MenuState.LogLevelSelector);
        }

        currentSettings = currentSettings with { LogLevel = selectedLevel.Value };
        sessionOptions = sessionOptions with
        {
            LogLevelInput = CliLogLevelResolver.FormatLogLevelCode(selectedLevel.Value),
            Verbose = selectedLevel.Value <= LogLevel.Debug,
            Quiet = selectedLevel.Value == LogLevel.None
        };
        SyncActiveLogging(sessionOptions, currentSettings);
        var savedPath = _settingsService.Save(baseWorkspace, currentSettings);
        color.WriteSuccess(
            $"Log Level is now {CliLogLevelResolver.FormatLogLevelDisplay(selectedLevel.Value)} " +
            $"({CliLogLevelResolver.FormatLogFormatCode(active.Format)} -> stderr). Saved settings to '{savedPath}'.");
        return MenuState.Settings;
    }

    private ResolvedLoggingConfig ResolveActiveLogging(WorkflowOptions sessionOptions, CliSettings currentSettings) =>
        CliLogLevelResolver.ResolveEffectiveLogging(sessionOptions, currentSettings, _environmentReader);

    private ResolvedLoggingConfig SyncActiveLogging(WorkflowOptions sessionOptions, CliSettings currentSettings)
    {
        var resolved = ResolveActiveLogging(sessionOptions, currentSettings);
        _loggerFactory?.Reconfigure(resolved.Level, resolved.Format);
        LogEvents.ReplLoggingReconfigured(_logger, resolved.Level, CliLogLevelResolver.FormatLogFormatCode(resolved.Format));
        return resolved;
    }

    private static List<HighlightedMenuOption> BuildVerbosityOptions(
        OutcomeVerbosity activeVerbosity,
        bool includeCancel = false)
    {
        var list = new List<HighlightedMenuOption>(4)
        {
            BuildSelectableValueOption("1", "Minimalistic (<= 25 words)", "1-line objective + at most 2 action bullets", activeVerbosity == OutcomeVerbosity.Minimalistic),
            BuildSelectableValueOption("2", "Balanced (<= 57 words)", "Standard Objective + 3 action bullets (default)", activeVerbosity == OutcomeVerbosity.Balanced),
            BuildSelectableValueOption("3", "Detailed (<= 95 words)", "Full Objective, Context/Web insights, bullets & 3 Alternatives", activeVerbosity == OutcomeVerbosity.Detailed)
        };
        if (includeCancel)
        {
            list.Add(new HighlightedMenuOption("0", "Cancel"));
        }

        return list;
    }

    private static int ResolveVerbosityCursorIndex(OutcomeVerbosity verbosity) => verbosity switch
    {
        OutcomeVerbosity.Minimalistic => 0,
        OutcomeVerbosity.Detailed => 2,
        _ => 1
    };

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
        var openAiConfig = _workflow.ProviderOrchestrator.ResolveOpenAiGatewayConfig(
            explicitApiKey: activeProvider == AiProviderType.OpenAiGateway ? sessionOptions.ApiKey : null,
            explicitBaseUrl: sessionOptions.OpenAiBaseUrl,
            persistedBaseUrl: currentSettings.OpenAiBaseUrl,
            explicitWireApi: sessionOptions.OpenAiWireApi,
            persistedWireApi: currentSettings.OpenAiWireApi);
        color.WriteLine();
        color.WriteHeader("=== AI Provider Orchestrator ===");
        color.WriteMuted($"  Active Provider : {activeProviderCode} │ Model: {currentSettings.ModelName ?? ResolveDefaultModelLabel(activeProvider)}");
        if (activeProvider == AiProviderType.OpenAiGateway || currentSettings.OpenAiBaseUrl is not null)
        {
            color.WriteMuted($"  OpenAI Gateway  : {openAiConfig.BaseUrl} ({openAiConfig.WireApiCode}) │ Key: {(openAiConfig.HasApiKey ? openAiConfig.MaskedApiKey : "not set")}");
        }

        color.WriteMuted("  Recommendation  : 'gemini' (AI Studio), 'copilot-sdk' (Copilot), or 'openai' (OpenAI Gateway API).");
        color.WriteLine();

        var options = new List<HighlightedMenuOption>
        {
            BuildProviderOption("1", "Google Gemini (gemini)", "Live REST generateContent or offline deterministic mode", activeProvider == AiProviderType.GoogleGemini, isRecommended: true),
            BuildProviderOption("2", "GitHub Copilot SDK (copilot-sdk)", "Official Microsoft.Copilot.SDK agent runtime & BYOK", activeProvider == AiProviderType.GitHubCopilot, isRecommended: true),
            BuildProviderOption("3", "OpenAI Gateway API (openai)", "Direct HTTP /v1/chat/completions or /v1/responses + custom URL", activeProvider == AiProviderType.OpenAiGateway, isRecommended: true),
            BuildProviderOption("4", "Azure OpenAI / Foundry (azure)", "Direct HTTP or Copilot SDK BYOK when configured, else mock fallback", activeProvider == AiProviderType.AzureOpenAi, isRecommended: false),
            BuildProviderOption("5", "Anthropic Claude (claude)", "Copilot SDK BYOK when configured, else mock fallback", activeProvider == AiProviderType.AnthropicClaude, isRecommended: false),
            BuildProviderOption("6", "Manual Prompt (manual)", "Render Markdown prompt for copy/paste into Copilot or any AI", activeProvider == AiProviderType.ManualPrompt, isRecommended: false),
            new("M", $"Select Model for '{activeProviderCode}'", null, null, null, "Configure model, view recommendations & official docs"),
            new("G", "Configure OpenAI Gateway URL / API", currentSettings.OpenAiBaseUrl ?? "default", ConsoleColor.Black, ConsoleColor.Cyan, "Set custom gateway base URL & chat/responses wire API"),
            new("0", "Back to Workspace & Settings")
        };

        var activeProviderIndex = activeProvider switch
        {
            AiProviderType.GitHubCopilot => 1,
            AiProviderType.OpenAiGateway => 2,
            AiProviderType.AzureOpenAi => 3,
            AiProviderType.AnthropicClaude => 4,
            AiProviderType.ManualPrompt => 5,
            _ => 0
        };

        var choice = InteractiveSelector.SelectMenuOptionWithHighlight(
            options,
            activeProviderIndex,
            "Select AI provider (0-6, M=model, G=gateway, ↑/↓, b=back, q=quit): ",
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

        if (normalized is "g" or "gateway")
        {
            return ConfigureOpenAiGatewayInteractive(baseWorkspace, ref sessionOptions, ref currentSettings, input, color);
        }

        AiProviderType? selected = normalized switch
        {
            "1" or "gemini" => AiProviderType.GoogleGemini,
            "2" or "copilot-sdk" or "github-copilot" => AiProviderType.GitHubCopilot,
            "3" or "openai" or "openai-gateway" or "gateway" => AiProviderType.OpenAiGateway,
            "4" or "azure" => AiProviderType.AzureOpenAi,
            "5" or "claude" or "anthropic" => AiProviderType.AnthropicClaude,
            "6" or "manual" => AiProviderType.ManualPrompt,
            _ => null
        };

        if (selected is null)
        {
            return WarnAndStay(color, choice, "0-6, M=model, G=gateway", MenuState.ProviderSelector);
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

    private MenuState ConfigureOpenAiGatewayInteractive(
        WorkspaceDirectories baseWorkspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        TextReader input,
        ConsoleColorWriter color)
    {
        color.WriteLine();
        color.WriteSubHeader("--- OpenAI Gateway Configuration ---");
        color.WriteMuted($"  Current URL     : {currentSettings.OpenAiBaseUrl ?? "(env OPENAI_BASE_URL or https://api.openai.com/v1)"}");
        color.WriteMuted($"  Current Wire API: {currentSettings.OpenAiWireApi ?? "chat (default: /v1/chat/completions)"}");
        color.WriteColored("Enter Gateway Base URL (or 'default' to clear, Enter to keep current): ", ConsoleColor.Yellow);
        var urlInput = input.ReadLine();
        if (urlInput is null)
        {
            return MenuState.Exit;
        }

        var updated = currentSettings;
        var trimmedUrl = urlInput.Trim();
        if (!string.IsNullOrEmpty(trimmedUrl))
        {
            if (trimmedUrl.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                trimmedUrl.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                updated = updated with { OpenAiBaseUrl = null };
            }
            else if (OpenAiGatewayClient.TryNormalizeBaseUrl(trimmedUrl, out var normalizedUrl, out var urlError))
            {
                updated = updated with { OpenAiBaseUrl = normalizedUrl };
            }
            else
            {
                color.WriteWarning($"  [!] {urlError}");
                return MenuState.ProviderSelector;
            }
        }

        color.WriteColored("Wire API ([1] chat/completions, [2] responses, 'default' to clear, Enter to keep): ", ConsoleColor.Yellow);
        var wireInput = input.ReadLine();
        if (wireInput is null)
        {
            return MenuState.Exit;
        }

        var trimmedWire = wireInput.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(trimmedWire))
        {
            if (trimmedWire is "default" or "reset")
            {
                updated = updated with { OpenAiWireApi = null };
            }
            else if (trimmedWire is "1" or "chat")
            {
                updated = updated with { OpenAiWireApi = "chat" };
            }
            else if (trimmedWire is "2" or "responses" or "response")
            {
                updated = updated with { OpenAiWireApi = "responses" };
            }
            else if (OpenAiGatewayClient.TryParseWireApi(trimmedWire, out var parsedWire))
            {
                updated = updated with { OpenAiWireApi = OpenAiGatewayClient.FormatWireApiCode(parsedWire) };
            }
            else
            {
                color.WriteWarning($"  [!] Unknown wire API '{wireInput.Trim()}'. Choose chat or responses.");
                return MenuState.ProviderSelector;
            }
        }

        currentSettings = updated;
        sessionOptions = sessionOptions with
        {
            PersistedOpenAiBaseUrl = currentSettings.OpenAiBaseUrl,
            PersistedOpenAiWireApi = currentSettings.OpenAiWireApi
        };
        var savedPath = _settingsService.Save(baseWorkspace, currentSettings);
        color.WriteSuccess($"OpenAI Gateway settings updated (URL: {currentSettings.OpenAiBaseUrl ?? "default"}, API: {currentSettings.OpenAiWireApi ?? "chat"}). Saved to '{savedPath}'.");
        return MenuState.ProviderSelector;
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
        else if (activeProvider == AiProviderType.OpenAiGateway)
        {
            var gatewayConfig = _workflow.ProviderOrchestrator.ResolveOpenAiGatewayConfig(
                explicitApiKey: sessionOptions.ApiKey,
                explicitBaseUrl: sessionOptions.OpenAiBaseUrl,
                persistedBaseUrl: currentSettings.OpenAiBaseUrl,
                explicitWireApi: sessionOptions.OpenAiWireApi,
                persistedWireApi: currentSettings.OpenAiWireApi);

            if (!gatewayConfig.IsValidBaseUrl)
            {
                discoveryNote = $"{gatewayConfig.ValidationError} Showing curated OpenAI recommendations.";
            }
            else if (gatewayConfig.HasApiKey)
            {
                try
                {
                    var explicitApiKey = sessionOptions.ApiKey;
                    var explicitBaseUrl = sessionOptions.OpenAiBaseUrl;
                    var persistedBaseUrl = currentSettings.OpenAiBaseUrl;
                    var catalog = TerminalUx.RunWithSpinner(
                        $"Discovering OpenAI Gateway models ({gatewayConfig.BaseUrl})...",
                        () => _workflow.ListOpenAiModelCatalog(
                            explicitApiKey,
                            explicitBaseUrl,
                            persistedBaseUrl),
                        color);
                    unsupportedHints = catalog.UnsupportedModelCapabilities;
                    (liveModelIds, discoveryNote) = MergeLiveDiscoveryEntries(
                        catalogEntries,
                        catalog.SupportedModels.Select(m => new ModelRecommendation(
                            m.ModelId,
                            m.ModelId,
                            "live",
                            string.IsNullOrWhiteSpace(m.OwnedBy)
                                ? "Available on configured OpenAI Gateway"
                                : $"Available via OpenAI Gateway ({m.OwnedBy})")),
                        providerCode);
                }
                catch (Exception ex)
                {
                    discoveryNote = $"Could not query live OpenAI Gateway models ({ex.Message}); showing curated recommendations.";
                }
            }
            else
            {
                discoveryNote = $"OPENAI_API_KEY not configured; showing recommended OpenAI models ({ModelCatalogService.OpenAiModelsDocsUrl}).";
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
            ?? (activeProvider switch
            {
                AiProviderType.GitHubCopilot => RuntimeEnvironmentService.DefaultCopilotModel,
                AiProviderType.OpenAiGateway => RuntimeEnvironmentService.DefaultOpenAiModel,
                _ => RuntimeEnvironmentService.DefaultGeminiModel
            });

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
        var activeLogging = ResolveActiveLogging(sessionOptions, currentSettings);

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
        color.WriteColored(" │ Log: ", ConsoleColor.DarkGray);
        color.WriteColored($"{CliLogLevelResolver.FormatLogLevelDisplay(activeLogging.Level)} ({CliLogLevelResolver.FormatLogFormatCode(activeLogging.Format)})", CliLogLevelResolver.GetLogLevelColor(activeLogging.Level));
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
            var isConfigured = !string.IsNullOrWhiteSpace(_environmentReader(variableName));
            color.WriteLine($"  {variableName}: {(isConfigured ? "configured" : "not set")}");
        }

        color.WriteLine();
        color.WriteSubHeader("--- OpenAI Gateway Environment Variables ---");
        foreach (var variableName in new[]
        {
            OpenAiGatewayClient.ApiKeyEnvVar,
            OpenAiGatewayClient.BaseUrlEnvVar,
            OpenAiGatewayClient.WireApiEnvVar,
            RuntimeEnvironmentService.OpenAiModelEnvVar
        })
        {
            var rawValue = _environmentReader(variableName);
            var status = string.IsNullOrWhiteSpace(rawValue)
                ? "not set"
                : string.Equals(variableName, OpenAiGatewayClient.ApiKeyEnvVar, StringComparison.Ordinal)
                    ? $"configured [{GeminiSecretProvider.MaskKey(rawValue)}]"
                    : rawValue.Trim();
            color.WriteLine($"  {variableName}: {status}");
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

        if (provider == AiProviderType.OpenAiGateway)
        {
            var gatewayConfig = _workflow.ProviderOrchestrator.ResolveOpenAiGatewayConfig(
                explicitApiKey: sessionOptions.ApiKey,
                explicitBaseUrl: sessionOptions.OpenAiBaseUrl,
                persistedBaseUrl: currentSettings.OpenAiBaseUrl,
                explicitWireApi: sessionOptions.OpenAiWireApi,
                persistedWireApi: currentSettings.OpenAiWireApi);

            if (!gatewayConfig.IsValidBaseUrl)
            {
                color.WriteWarning($"  [!] {gatewayConfig.ValidationError}");
            }
            else if (gatewayConfig.HasApiKey && sessionOptions.GeminiLive != false)
            {
                try
                {
                    var catalog = TerminalUx.RunWithSpinner(
                        $"Discovering OpenAI Gateway models ({gatewayConfig.BaseUrl})...",
                        () => _workflow.ListOpenAiModelCatalog(
                            sessionOptions.ApiKey,
                            sessionOptions.OpenAiBaseUrl,
                            currentSettings.OpenAiBaseUrl),
                        color);
                    if (catalog.SupportedModels.Count > 0)
                    {
                        color.WriteHeader($"Available OpenAI Gateway Models ({gatewayConfig.BaseUrl}):");
                        foreach (var model in catalog.SupportedModels)
                        {
                            var ownerSuffix = string.IsNullOrWhiteSpace(model.OwnedBy) ? string.Empty : $" ({model.OwnedBy})";
                            color.WriteLine($"  • {model.ModelId,-28} {model.ModelId}{ownerSuffix}");
                        }

                        return;
                    }

                    color.WriteWarning("  [i] Gateway returned no compatible chat/responses models; showing curated recommendations.");
                }
                catch (Exception ex)
                {
                    color.WriteWarning($"  [!] Could not query live OpenAI Gateway models ({ex.Message}); showing curated recommendations.");
                }
            }
            else if (sessionOptions.GeminiLive == false)
            {
                color.WriteMuted("  Offline mode (--offline) active; showing curated OpenAI recommendations.");
            }
            else
            {
                color.WriteWarning($"  [i] {OpenAiGatewayClient.ApiKeyEnvVar} not configured; showing curated OpenAI recommendations.");
            }

            color.WriteHeader("Recommended Models (openai):");
            WriteModelRecommendations(color, ModelCatalogService.GetRecommendations(provider));
            color.WriteMuted($"  Official Docs: {ModelCatalogService.GetOfficialDocsUrl(provider)}");
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

    private enum PreviewArtifactKind
    {
        ActiveSession,
        MarkdownOutcome,
        JsonExport,
        InputNote
    }

    private sealed record PreviewArtifact(
        PreviewArtifactKind Kind,
        string CategoryBadge,
        string DisplayName,
        string FullPath,
        long SizeBytes);

    private MenuState ShowPreviewOutcomeMenu(
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        WorkspaceDirectories baseWorkspace,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color,
        MenuState returnState = MenuState.Main,
        bool openInOrchestratorReview = false)
    {
        var allArtifacts = CollectPreviewArtifacts(workspace, sessionOptions);

        color.WriteLine();
        color.WriteHeader(openInOrchestratorReview
            ? "=== Preview, Refine & Copy Outcome (Backlog Assisted Orchestrator) ==="
            : "=== Preview Workspace & Outcomes ===");
        if (allArtifacts.Count == 0)
        {
            var outputDir = sessionOptions.OutputDirectory ?? workspace.OutputDirectory;
            color.WriteWarning($"  [!] No active draft, saved outcomes (.md/.json in '{outputDir}'), or notes (in '{workspace.InputDirectory}') found.");
            return returnState;
        }

        color.WriteMuted("  Includes: active session outcome, ./out/*.md, ./out/*.json & ./in input notes.");
        IReadOnlyList<PreviewArtifact> candidates = allArtifacts;
        var highlightedKey = "1";
        while (true)
        {
            var options = new List<HighlightedMenuOption>(candidates.Count + 1);
            for (var i = 0; i < candidates.Count; i++)
            {
                var artifact = candidates[i];
                var (badgeFg, badgeBg) = GetPreviewBadgeColors(artifact.Kind, color);
                var detail = artifact.Kind == PreviewArtifactKind.ActiveSession
                    ? "in-memory active draft / outcome"
                    : $"{artifact.SizeBytes:N0} bytes";
                options.Add(new HighlightedMenuOption(
                    (i + 1).ToString(),
                    artifact.DisplayName,
                    artifact.CategoryBadge,
                    badgeFg,
                    badgeBg,
                    detail,
                    IsSelectedValue: artifact.Kind == PreviewArtifactKind.ActiveSession));
            }

            options.Add(new HighlightedMenuOption("0", BackToLabel(returnState)));
            var initialIndex = InteractiveSelector.FindOptionIndexByKey(options, highlightedKey);

            var selection = InteractiveSelector.SelectMenuOptionWithHighlight(
                options,
                initialIndex,
                "Select item to preview [Enter=#1, ↑/↓, number, fuzzy name, b=back, q=quit]: ",
                input,
                color);
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

            PreviewArtifact? chosenArtifact = null;
            if (string.IsNullOrEmpty(trimmed))
            {
                chosenArtifact = candidates[0];
            }
            else if (int.TryParse(trimmed, out var chosen))
            {
                if (chosen < 1 || chosen > candidates.Count)
                {
                    color.WriteWarning($"[!] Invalid option '{trimmed}'. Choose 0-{candidates.Count}.");
                    continue;
                }

                chosenArtifact = candidates[chosen - 1];
            }
            else
            {
                var matches = FilterPreviewArtifacts(candidates, trimmed);
                if (matches.Count == 1)
                {
                    color.WriteSuccess($"  ✓ Matched '{matches[0].DisplayName}'");
                    chosenArtifact = matches[0];
                }
                else if (matches.Count > 1)
                {
                    candidates = matches;
                    highlightedKey = "1";
                    color.WriteMuted($"  Filtered to {matches.Count} matching item(s):");
                    continue;
                }
                else
                {
                    color.WriteWarning($"  [!] No preview items matched '{trimmed}'.");
                    candidates = allArtifacts;
                    highlightedKey = "1";
                    continue;
                }
            }

            if (chosenArtifact.Kind is PreviewArtifactKind.ActiveSession or PreviewArtifactKind.MarkdownOutcome)
            {
                string markdownContent;
                if (chosenArtifact.Kind == PreviewArtifactKind.ActiveSession)
                {
                    markdownContent = _lastMarkdownDocuments[0];
                    MarkdownOutcomeRenderer.WriteOutcomeTable(
                        color,
                        _lastMarkdownDocuments,
                        _lastSupplementalUserStory,
                        _lastSupplementalDescription,
                        includeAlternatives: openInOrchestratorReview || sessionOptions.ShowAlternatives);
                }
                else
                {
                    markdownContent = File.ReadAllText(chosenArtifact.FullPath);
                    PreviewMarkdownFilePath(chosenArtifact.FullPath, color, includeAlternatives: openInOrchestratorReview || sessionOptions.ShowAlternatives);
                }

                if (openInOrchestratorReview)
                {
                    return OpenMarkdownContentInOrchestratorReview(
                        markdownContent,
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
                if (copyOutcomeState == MenuState.BacklogOrchestrator)
                {
                    return OpenMarkdownContentInOrchestratorReview(
                        markdownContent,
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

            var rawActionState = PreviewRawArtifactWithActions(
                chosenArtifact,
                workspace,
                ref sessionOptions,
                ref currentSettings,
                baseWorkspace,
                input,
                output,
                color,
                returnState);
            if (rawActionState != MenuState.PreviewOutcome)
            {
                return rawActionState;
            }

            candidates = allArtifacts;
            highlightedKey = "1";
        }
    }

    private MenuState PreviewRawArtifactWithActions(
        PreviewArtifact artifact,
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        WorkspaceDirectories baseWorkspace,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color,
        MenuState returnState)
    {
        var content = File.ReadAllText(artifact.FullPath);
        color.WriteLine();
        color.WriteHeader($"=== Preview ({artifact.CategoryBadge.ToUpperInvariant()}): {artifact.DisplayName} ===");
        color.WriteMuted($"  Path: {artifact.FullPath} ({artifact.SizeBytes:N0} bytes)");
        color.WriteLine();
        color.WriteLine(content);
        color.WriteLine();

        var options = new List<HighlightedMenuOption>();
        if (artifact.Kind == PreviewArtifactKind.InputNote)
        {
            options.Add(new("1", "Process Note in Backlog Assisted Orchestrator", "workflow", ConsoleColor.Black, ConsoleColor.Yellow, "Review & format into Type01 / Type02 / Both"));
            options.Add(new("2", "Copy Raw Note to Clipboard"));
        }
        else
        {
            options.Add(new("1", $"Copy {artifact.CategoryBadge.ToUpperInvariant()} Content to Clipboard"));
        }

        options.Add(new("0", "Back to Preview List"));
        options.Add(new("B", BackToLabel(returnState)));

        var choice = InteractiveSelector.SelectMenuOptionWithHighlight(
            options,
            0,
            "Preview action (↑/↓, 0=list, b=back, q=quit): ",
            input,
            color);
        if (choice is null)
        {
            return MenuState.Exit;
        }

        var normalized = choice.Trim().ToLowerInvariant();
        if (normalized is "q" or "quit" or "/exit" or "/quit")
        {
            return MenuState.Exit;
        }

        if (normalized is "b" or "back" or "/back")
        {
            return returnState;
        }

        if (normalized is "" or "0" or "list")
        {
            return MenuState.PreviewOutcome;
        }

        if (artifact.Kind == PreviewArtifactKind.InputNote && normalized is "1" or "process")
        {
            return ProcessSingleFileInWizard(
                artifact.FullPath,
                workspace,
                ref sessionOptions,
                ref currentSettings,
                baseWorkspace,
                input,
                output,
                color,
                returnState: MenuState.PreviewOutcome);
        }

        if ((artifact.Kind == PreviewArtifactKind.InputNote && normalized is "2" or "c" or "copy") ||
            (artifact.Kind == PreviewArtifactKind.JsonExport && normalized is "1" or "c" or "copy"))
        {
            var copyResult = _clipboardService.CopyText(content);
            if (copyResult.Succeeded)
            {
                color.WriteSuccess($"  ✓ Copied '{artifact.DisplayName}' to clipboard ({copyResult.Method}).");
            }
            else
            {
                color.WriteWarning($"  [!] {copyResult.Message}");
            }

            return MenuState.PreviewOutcome;
        }

        return MenuState.PreviewOutcome;
    }

    private MenuState OpenMarkdownContentInOrchestratorReview(
        string markdown,
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        WorkspaceDirectories baseWorkspace,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color,
        MenuState returnState)
    {
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

        if (string.IsNullOrWhiteSpace(targetArg) && _lastMarkdownDocuments.Count > 0)
        {
            MarkdownOutcomeRenderer.WriteOutcomeTable(
                color,
                _lastMarkdownDocuments,
                _lastSupplementalUserStory,
                _lastSupplementalDescription,
                includeAlternatives: showAlts || sessionOptions.ShowAlternatives);
            color.WriteMuted("  Tip: Run '/copy simple', '/copy raw', '/copy md', '/copy clean', or '/copy <field>' to copy.");
            return;
        }

        var allArtifacts = CollectPreviewArtifacts(workspace, sessionOptions);
        if (!string.IsNullOrWhiteSpace(targetArg))
        {
            try
            {
                var mdPath = ResolveSavedMarkdownTarget(targetArg, outputDir);
                PreviewMarkdownFilePath(mdPath, color, includeAlternatives: showAlts || sessionOptions.ShowAlternatives);
                color.WriteMuted("  Tip: Run '/copy simple', '/copy raw', '/copy md', '/copy clean', or '/copy <field>' to copy.");
                return;
            }
            catch (FileNotFoundException)
            {
                var matches = FilterPreviewArtifacts(allArtifacts, targetArg);
                if (matches.Count > 0)
                {
                    var match = matches[0];
                    if (match.Kind == PreviewArtifactKind.MarkdownOutcome)
                    {
                        PreviewMarkdownFilePath(match.FullPath, color, includeAlternatives: showAlts || sessionOptions.ShowAlternatives);
                        color.WriteMuted("  Tip: Run '/copy simple', '/copy raw', '/copy md', '/copy clean', or '/copy <field>' to copy.");
                        return;
                    }

                    if (match.Kind is PreviewArtifactKind.JsonExport or PreviewArtifactKind.InputNote)
                    {
                        color.WriteLine();
                        color.WriteHeader($"=== Preview ({match.CategoryBadge.ToUpperInvariant()}): {match.DisplayName} ===");
                        color.WriteMuted($"  Path: {match.FullPath}");
                        color.WriteLine(File.ReadAllText(match.FullPath));
                        return;
                    }
                }

                throw;
            }
        }

        var fallbackPath = ResolveSavedMarkdownTarget(null, outputDir);
        PreviewMarkdownFilePath(fallbackPath, color, includeAlternatives: showAlts || sessionOptions.ShowAlternatives);
        color.WriteMuted("  Tip: Run '/copy simple', '/copy raw', '/copy md', '/copy clean', or '/copy <field>' to copy.");
    }

    private IReadOnlyList<PreviewArtifact> CollectPreviewArtifacts(
        WorkspaceDirectories workspace,
        WorkflowOptions sessionOptions)
    {
        var outputDir = sessionOptions.OutputDirectory ?? workspace.OutputDirectory;
        var list = new List<PreviewArtifact>();

        if (_lastMarkdownDocuments.Count > 0)
        {
            var parsed = MarkdownOutcomeRenderer.ParseMarkdown(_lastMarkdownDocuments[0]);
            var activeTitle = !string.IsNullOrWhiteSpace(parsed.TitleLine)
                ? MarkdownOutcomeRenderer.StripMarkdownFormatting(parsed.TitleLine)
                : "Current Session Draft / Outcome";
            list.Add(new PreviewArtifact(
                PreviewArtifactKind.ActiveSession,
                "session",
                $"Active: {activeTitle}",
                string.Empty,
                _lastMarkdownDocuments[0].Length));
        }

        foreach (var mdPath in GetSortedWorkspaceFiles(outputDir, "*.md"))
        {
            var info = new FileInfo(mdPath);
            list.Add(new PreviewArtifact(PreviewArtifactKind.MarkdownOutcome, "md", info.Name, mdPath, info.Length));
        }

        foreach (var jsonPath in GetSortedWorkspaceFiles(outputDir, "*.json"))
        {
            var info = new FileInfo(jsonPath);
            list.Add(new PreviewArtifact(PreviewArtifactKind.JsonExport, "json", info.Name, jsonPath, info.Length));
        }

        foreach (var inputPath in GetSortedWorkspaceFiles(workspace.InputDirectory, "*.txt", "*.md"))
        {
            var info = new FileInfo(inputPath);
            list.Add(new PreviewArtifact(PreviewArtifactKind.InputNote, "input", info.Name, inputPath, info.Length));
        }

        return list;
    }

    private static IReadOnlyList<PreviewArtifact> FilterPreviewArtifacts(
        IReadOnlyList<PreviewArtifact> items,
        string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return items;
        }

        var cleanQuery = query.Trim().TrimStart('@');
        return items
            .Select(item => (
                Item: item,
                Score: Math.Max(
                    InteractiveSelector.ScoreFuzzyMatch(item.DisplayName, cleanQuery),
                    InteractiveSelector.ScoreFuzzyMatch(item.CategoryBadge, cleanQuery))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Item)
            .ToArray();
    }

    private static (ConsoleColor Fg, ConsoleColor Bg) GetPreviewBadgeColors(
        PreviewArtifactKind kind,
        ConsoleColorWriter color) => kind switch
    {
        PreviewArtifactKind.ActiveSession => (ConsoleColor.Black, ConsoleColor.Green),
        PreviewArtifactKind.MarkdownOutcome => color.GetValueBadgeColors(),
        PreviewArtifactKind.JsonExport => (ConsoleColor.Black, ConsoleColor.Cyan),
        _ => (ConsoleColor.White, ConsoleColor.DarkGray)
    };

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
        var highlightedKey = "1";

        while (true)
        {
            color.WriteLine();
            color.WriteSubHeader("Selectable Output Formats & Jira Fields:");
            var copyOptions = new List<HighlightedMenuOption>(currentItems.Count + 2);
            foreach (var item in currentItems)
            {
                copyOptions.Add(new HighlightedMenuOption(
                    item.NumberKey,
                    item.Label,
                    Description: item.PreviewSummary));
            }

            if (allowWizardPromotion)
            {
                copyOptions.Add(new HighlightedMenuOption("E", "Refine / Verbosity in Backlog Assisted Orchestrator"));
            }

            copyOptions.Add(new HighlightedMenuOption("0", "Done / Back"));

            var initialIndex = InteractiveSelector.FindOptionIndexByKey(copyOptions, highlightedKey);
            var wizardOptionHint = allowWizardPromotion ? "  [E] Refine/Verbosity" : string.Empty;
            var copyChoice = InteractiveSelector.SelectMenuOptionWithHighlight(
                copyOptions,
                initialIndex,
                $"Copy [1..{allItems.Count}, 4/n=Simple, field{wizardOptionHint}, ↑/↓, 0/b=Back, q=Quit]: ",
                input,
                color);
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

            if (allowWizardPromotion && normalized is "e" or "edit" or "refine" or "wizard" or "orchestrator" or "verbosity")
            {
                return MenuState.BacklogOrchestrator;
            }

            if (normalized is "?" or "list" or "all")
            {
                currentItems = allItems;
                highlightedKey = "1";
                continue;
            }

            var matches = InteractiveSelector.FilterOutputItems(currentItems, copyChoice.Trim());
            if (matches.Count == 1)
            {
                MarkdownOutcomeRenderer.CopySelectableItemToClipboard(_clipboardService, matches[0], color);
                currentItems = allItems;
                highlightedKey = matches[0].NumberKey;
                continue;
            }

            if (matches.Count > 1)
            {
                currentItems = matches;
                highlightedKey = matches[0].NumberKey;
                continue;
            }

            color.WriteWarning($"  [!] No format or field matched '{copyChoice.Trim()}'. Choose 1-{allItems.Count}, field name, 0/b, or q.");
            currentItems = allItems;
            highlightedKey = "1";
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

    public static IReadOnlyList<string> GetSavedMarkdownFiles(string outputDirectory) =>
        GetSortedWorkspaceFiles(outputDirectory, "*.md");

    private static IReadOnlyList<string> GetSortedWorkspaceFiles(string directory, params string[] patterns)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        return patterns
            .SelectMany(pattern => Directory.EnumerateFiles(directory, pattern))
            .Distinct(StringComparer.OrdinalIgnoreCase)
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
        color.WriteLine("  Numbers        [1] Backlog Assisted Orchestrator  [2] Create / Generate Backlog  [3] Preview  [4] Settings  [0] Exit");
        color.WriteLine("  Shortcuts      [w] Orchestrator  [g] Generate  [d] Draft  [p] Process  [v] Preview  [s] Settings  [c] Copy  [?] Help  [q] Quit");
        color.WriteLine("  Navigation     Use ↑/↓ + Enter or enter [0/b/back] in any submenu to go back, or [q/quit] anytime");
        color.WriteLine("  /wizard        Open Backlog Assisted Orchestrator hub (/orchestrator, /bao) with all workflows & tools");
        color.WriteLine("  /generate      Open Create / Generate Backlog submenu (Draft from Goal or Process ./in files)");
        color.WriteLine("  /process       Open Process menu, or run inline: /process [all|<file>] [--dry-run] [--json]");
        color.WriteLine("  /draft         Open Guided Draft flow, or inline: /draft \"<goal>\" [-t 01|02|both] [--details \"...\"] [--search] [--preview]");
        color.WriteLine("  /preview       Preview active session, './out/*.md', './out/*.json', or './in' notes: /preview [filename]");
        color.WriteLine("  /copy          Copy outcome or field: /copy [simple|raw|md|clean|title|story|desc|ac|alt1..]");
        color.WriteLine("  /verbosity     Set persisted default verbosity: /verbosity [min|bal|det]");
        color.WriteLine("  /log-level     Set stderr log severity: /log-level [trace|debug|info|warning|error|critical|none|default]");
        color.WriteLine("  /log-format    Switch stderr log serialization: /log-format [text|json|default]");
        color.WriteLine("  /status        Show crisp WSL2/Windows paths, queued files, & AI provider readiness table");
        color.WriteLine("  /config        Show or update settings: /config [--json on|off] [--in <dir>] [--out <dir>] [--verbosity min|bal|det] [--log-level <lvl>] [--log-format text|json] [--gateway-url <url>] [--wire-api chat|responses]");
        color.WriteLine("  /provider      Switch AI orchestrator provider (gemini, copilot-sdk, openai, azure, claude, manual)");
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

    private static string ResolveDefaultModelLabel(AiProviderType activeProvider) => activeProvider switch
    {
        AiProviderType.GitHubCopilot => RuntimeEnvironmentService.DefaultCopilotModel,
        AiProviderType.OpenAiGateway => RuntimeEnvironmentService.DefaultOpenAiModel,
        _ => "auto"
    };

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
