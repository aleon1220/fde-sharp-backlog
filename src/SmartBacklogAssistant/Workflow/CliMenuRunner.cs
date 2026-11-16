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
        ProcessFiles,
        DraftItem,
        Settings,
        ThemeSelector,
        ProviderSelector,
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
            PreferredProvider = resolvedProvider
        };

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
            ProviderInput = RuntimeEnvironmentService.FormatProviderCode(resolvedProvider)
        };

        var state = MenuState.Main;
        while (state != MenuState.Exit)
        {
            state = state switch
            {
                MenuState.Main => ShowMainMenu(baseWorkspace, ref workspace, ref sessionOptions, ref currentSettings, input, output, color),
                MenuState.ProcessFiles => ShowProcessFilesMenu(workspace, ref sessionOptions, ref currentSettings, baseWorkspace, input, output, color),
                MenuState.DraftItem => ShowDraftItemMenu(workspace, ref sessionOptions, ref currentSettings, baseWorkspace, input, output, color),
                MenuState.Settings => ShowSettingsMenu(baseWorkspace, ref workspace, ref sessionOptions, ref currentSettings, input, color),
                MenuState.ThemeSelector => ShowThemeMenu(baseWorkspace, ref sessionOptions, ref currentSettings, input, color),
                MenuState.ProviderSelector => ShowProviderMenu(baseWorkspace, ref sessionOptions, ref currentSettings, input, color),
                MenuState.PreviewOutcome => ShowPreviewOutcomeMenu(workspace, sessionOptions, input, color),
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

        color.WriteMuted($"  Provider: {providerCode} ({modelLabel}) │ Live: {(currentSettings.GeminiLive == true ? "ON" : "OFF")} │ Theme: {themeCode}");
        color.WriteLine();

        color.WriteMenuOptionWithSlash("1", "Process Input Files", "Extract Jira items from './in' to './out'", "/process");
        color.WriteMenuOptionWithSlash("2", "Draft Backlog Item", "Create a User Story (Type01) or Task (Type02)", "/draft");
        color.WriteMenuOptionWithSlash("3", "Workspace & Settings", "Configure outputs, inspect files, & status", "/settings");
        color.WriteMenuOptionWithSlash("4", "Preview & Copy (.md)", "Preview saved './out/*.md' table & copy", "/preview");
        color.WriteMenuOptionWithSlash("0", "Exit", null, "/exit");
        color.WriteLine();
        color.WriteMuted("  Tip: Type a number [0-4], a slash command (/process, /draft, /preview, /copy), or @filename");
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
                color);
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

        return trimmed switch
        {
            "0" or "q" or "Q" => MenuState.Exit,
            "1" => MenuState.ProcessFiles,
            "2" => MenuState.DraftItem,
            "3" => MenuState.Settings,
            "4" => MenuState.PreviewOutcome,
            _ => WarnAndStay(color, trimmed, "0-4, /command, or @filename", MenuState.Main)
        };
    }

    private MenuState ExecuteSlashCommand(
        string commandLine,
        WorkspaceDirectories baseWorkspace,
        ref WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color)
    {
        var tokens = TokenizeCommandLine(commandLine);
        if (tokens.Count == 0)
        {
            return MenuState.Main;
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
                    return MenuState.Main;

                case "/clear" or "/cls":
                    if (ReferenceEquals(output, Console.Out) && !Console.IsOutputRedirected)
                    {
                        Console.Clear();
                    }

                    return MenuState.Main;

                case "/settings":
                    return MenuState.Settings;

                case "/status" or "/doctor":
                    color.WriteLine();
                    _diagnosticService.RenderStatusTable(sessionOptions, output, currentSettings);
                    return MenuState.Main;

                case "/process" or "/run":
                {
                    if (args.Length == 0)
                    {
                        return MenuState.ProcessFiles;
                    }

                    ExecuteInlineProcessCommand(args, workspace, ref sessionOptions, ref currentSettings, baseWorkspace, input, output, color);
                    return MenuState.Main;
                }

                case "/draft" or "/add":
                {
                    if (args.Length == 0)
                    {
                        return MenuState.DraftItem;
                    }

                    ExecuteInlineDraftCommand(args, workspace, ref sessionOptions, ref currentSettings, baseWorkspace, input, output, color);
                    return MenuState.Main;
                }

                case "/theme":
                {
                    if (args.Length == 0)
                    {
                        return MenuState.ThemeSelector;
                    }

                    var targetTheme = ConsoleColorWriter.ResolveTheme(args[0], currentSettings.Theme);
                    if (!IsValidThemeToken(args[0]))
                    {
                        color.WriteWarning($"  [!] Unknown theme '{args[0]}'. Choose agy, uv, or kubectl.");
                        return MenuState.Main;
                    }

                    currentSettings = currentSettings with { Theme = targetTheme };
                    color.Theme = targetTheme;
                    sessionOptions = sessionOptions with { CliTheme = ConsoleColorWriter.GetThemeCode(targetTheme) };
                    var savedPath = _settingsService.Save(baseWorkspace, currentSettings);
                    color.WriteSuccess($"CLI Theme is now {ConsoleColorWriter.FormatThemeDisplay(targetTheme)}. Saved settings to '{savedPath}'.");
                    return MenuState.Main;
                }

                case "/provider":
                {
                    if (args.Length == 0)
                    {
                        return MenuState.ProviderSelector;
                    }

                    var resolved = _orchestrator.ResolveProvider(args[0]);
                    currentSettings = currentSettings with { PreferredProvider = resolved };
                    sessionOptions = sessionOptions with { ProviderInput = RuntimeEnvironmentService.FormatProviderCode(resolved) };
                    var savedPath = _settingsService.Save(baseWorkspace, currentSettings);
                    color.WriteSuccess($"Active AI Provider set to '{RuntimeEnvironmentService.FormatProviderCode(resolved)}'. Saved settings to '{savedPath}'.");
                    return MenuState.Main;
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
                    return MenuState.Main;
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
                    return MenuState.Main;
                }

                case "/models":
                    ListActiveProviderModels(sessionOptions, currentSettings, input, color);
                    return MenuState.Main;

                case "/config":
                    ExecuteInlineConfigCommand(args, baseWorkspace, ref workspace, ref sessionOptions, ref currentSettings, color);
                    return MenuState.Main;

                case "/preview" or "/view" or "/show":
                {
                    if (args.Length == 0)
                    {
                        return MenuState.PreviewOutcome;
                    }

                    ExecuteInlinePreviewCommand(args, workspace, sessionOptions, color);
                    return MenuState.Main;
                }

                case "/copy":
                    ExecuteInlineCopyCommand(args, input, color);
                    return MenuState.Main;

                default:
                    color.WriteWarning($"  [!] Unknown slash command '{command}'. Type /help for available commands.");
                    return MenuState.Main;
            }
        }
        catch (Exception ex)
        {
            TerminalUx.WriteActionableError(color, ex);
            return MenuState.Main;
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
                WebSearchGrounding = effectiveSearch
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
                currentSettings.PreferredProvider ?? AiProviderType.GoogleGemini)
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
        ConsoleColorWriter color)
    {
        _workspaceService.EnsureDirectoriesExist(workspace);
        var files = _workspaceService.GetInputFiles(workspace.InputDirectory);

        color.WriteLine();
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
        color.WriteMenuOption("1", "Process All Files");
        color.WriteMenuOption("2", "Select Single File");
        color.WriteMenuOption("3", "Dry Run (Preview Only)");
        color.WriteMenuOption("0", "Back to Main Menu");
        color.WriteColored("Select an option (0-3, or @filename): ", ConsoleColor.Yellow);

        var choice = input.ReadLine();
        if (choice is null)
        {
            return MenuState.Exit;
        }

        var trimmed = choice.Trim();
        if (trimmed.Equals("/back", StringComparison.OrdinalIgnoreCase))
        {
            return MenuState.Main;
        }

        if (trimmed.Equals("/exit", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        {
            return MenuState.Exit;
        }

        if (trimmed.StartsWith('@'))
        {
            var resolved = _workspaceService.TryResolveContextFile(trimmed, workspace);
            if (resolved is not null)
            {
                ProcessFiles([resolved], false, baseWorkspace, ref sessionOptions, ref currentSettings, input, output, color);
            }
            else
            {
                WarnMissingContextFiles([trimmed], workspace, color);
            }

            return MenuState.ProcessFiles;
        }

        switch (trimmed)
        {
            case "0":
                return MenuState.Main;
            case "1":
                ProcessFiles(files, false, baseWorkspace, ref sessionOptions, ref currentSettings, input, output, color);
                return MenuState.ProcessFiles;
            case "2":
                var selectedFile = SelectSingleFile(files, input, color);
                if (selectedFile is not null)
                {
                    ProcessFiles([selectedFile], false, baseWorkspace, ref sessionOptions, ref currentSettings, input, output, color);
                }

                return MenuState.ProcessFiles;
            case "3":
                ProcessFiles(files, true, baseWorkspace, ref sessionOptions, ref currentSettings, input, output, color);
                return MenuState.ProcessFiles;
            default:
                return WarnAndStay(color, choice, "0, 1, 2, or 3", MenuState.ProcessFiles);
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

    private MenuState ShowDraftItemMenu(
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        WorkspaceDirectories baseWorkspace,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color)
    {
        color.WriteLine();
        color.WriteHeader("=== Draft Backlog Item ===");
        color.WriteMenuOption("1", "Complete User Story (Type 01)", "Title, Description, User Story, and Given/When/Then criteria");
        color.WriteMenuOption("2", "Schedule Item (Type 02)", "Concise Title and Engineering Description (with Goal -> User Story synthesis)");
        color.WriteMenuOption("3", "Combined Output (Both)", "Generate Type 01 and Type 02 from one draft");
        color.WriteMenuOption("4", "Preview Draft (No Files Saved)", "Select Type 01, Type 02, or Both without saving files");
        color.WriteMenuOption("5", "Guided Draft & Refine (Wizard)", "Add details/scope, optional Google Search, & refine before saving");
        color.WriteMenuOption("0", "Back to Main Menu");
        color.WriteMuted("  Tips: Enter just a goal (\"<Verb> <Topic>\") — bare goals auto-expand via User Story principles + 3 Alternatives.");
        color.WriteMuted("        Append '| <scope/dates>' for extra details, '--search' for live web lookup, or '@filename' to attach notes.");
        color.WriteColored("Select an option (0-5): ", ConsoleColor.Yellow);

        var choice = input.ReadLine();
        if (choice is null)
        {
            return MenuState.Exit;
        }

        var normalizedChoice = choice.Trim();
        if (normalizedChoice == "0" || normalizedChoice.Equals("/back", StringComparison.OrdinalIgnoreCase))
        {
            return MenuState.Main;
        }

        if (normalizedChoice.Equals("/exit", StringComparison.OrdinalIgnoreCase) || normalizedChoice.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        {
            return MenuState.Exit;
        }

        if (normalizedChoice == "4")
        {
            return ShowPreviewDraftSubMenu(workspace, ref sessionOptions, ref currentSettings, baseWorkspace, input, output, color);
        }

        if (normalizedChoice == "5")
        {
            return RunGuidedDraftWizard(workspace, ref sessionOptions, ref currentSettings, baseWorkspace, input, output, color);
        }

        if (normalizedChoice is not ("1" or "2" or "3"))
        {
            return WarnAndStay(color, choice, "0, 1, 2, 3, 4, or 5", MenuState.DraftItem);
        }

        var combinedOutput = normalizedChoice == "3";
        var backlogType = normalizedChoice == "2" ? "Type02" : "Type01";
        var previewOnly = sessionOptions.DryRun;

        var resultState = ExecuteGoalPromptDraft(
            backlogType: backlogType,
            combinedOutput: combinedOutput,
            previewOnly: previewOnly,
            workspace: workspace,
            sessionOptions: ref sessionOptions,
            currentSettings: ref currentSettings,
            baseWorkspace: baseWorkspace,
            input: input,
            output: output,
            color: color);

        return resultState == MenuState.Exit ? MenuState.Exit : MenuState.DraftItem;
    }

    private MenuState ShowPreviewDraftSubMenu(
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        WorkspaceDirectories baseWorkspace,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color)
    {
        while (true)
        {
            color.WriteLine();
            color.WriteHeader("=== Preview Draft (No Files Saved) ===");
            color.WriteMenuOption("1", "Complete User Story (Type 01)", "Preview Title, User Story, & Given/When/Then without saving");
            color.WriteMenuOption("2", "Schedule Item (Type 02)", "Preview enhanced Title, Description, User Story framing & 3 Alternatives");
            color.WriteMenuOption("3", "Combined Output (Both)", "Preview Type 01 and Type 02 side-by-side without saving");
            color.WriteMenuOption("0", "Back to Draft Menu");
            color.WriteMuted("  Goal Tips for Better Results:");
            color.WriteMuted("    • Bare Goals  : Write <Verb> <Topic> for <Outcome> (auto-expands with INVEST User Story + 3 Alternatives)");
            color.WriteMuted("    • Add Context : \"check Geocaching 2026 hiding movie theme | Verify official rules and dates\"");
            color.WriteMuted("    • Live Lookup : Add \"--search\" for web grounding or \"@file.txt\" to attach discovery notes");
            color.WriteColored("Select preview type (0-3): ", ConsoleColor.Yellow);

            var choice = input.ReadLine();
            if (choice is null)
            {
                return MenuState.Exit;
            }

            var normalized = choice.Trim().ToLowerInvariant();
            if (normalized is "0" or "/back" or "back")
            {
                return MenuState.DraftItem;
            }

            if (normalized is "/exit" or "/quit" or "q")
            {
                return MenuState.Exit;
            }

            var selection = normalized switch
            {
                "1" or "01" or "type01" or "type 01" or "complete" => (BacklogType: "Type01", CombinedOutput: false, Valid: true),
                "2" or "02" or "type02" or "type 02" or "schedule" => (BacklogType: "Type02", CombinedOutput: false, Valid: true),
                "3" or "both" or "all" => (BacklogType: "Type01", CombinedOutput: true, Valid: true),
                _ => (BacklogType: "Type01", CombinedOutput: false, Valid: false)
            };

            if (!selection.Valid)
            {
                color.WriteWarning($"[!] Invalid option '{choice.Trim()}'. Choose 0, 1, 2, or 3.");
                continue;
            }

            var runState = ExecuteGoalPromptDraft(
                backlogType: selection.BacklogType,
                combinedOutput: selection.CombinedOutput,
                previewOnly: true,
                workspace: workspace,
                sessionOptions: ref sessionOptions,
                currentSettings: ref currentSettings,
                baseWorkspace: baseWorkspace,
                input: input,
                output: output,
                color: color);

            if (runState == MenuState.Exit)
            {
                return MenuState.Exit;
            }
        }
    }

    private MenuState ExecuteGoalPromptDraft(
        string backlogType,
        bool combinedOutput,
        bool previewOnly,
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        WorkspaceDirectories baseWorkspace,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color)
    {
        WriteGoalTypingHint(color, backlogType, combinedOutput);
        color.WriteColored("Enter Goal or Title (or @filename, '| details', '--search'): ", ConsoleColor.Yellow);
        var rawInputLine = input.ReadLine();
        if (rawInputLine is null)
        {
            return MenuState.Exit;
        }

        var rawLine = rawInputLine.Trim();
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            color.WriteWarning("  [!] Goal or Title cannot be empty.");
            return MenuState.DraftItem;
        }

        var parsedInline = RawDataCollector.ParseInlineGoalModifiers(rawLine);
        if (string.IsNullOrWhiteSpace(parsedInline.GoalExpression))
        {
            color.WriteWarning("  [!] Goal or Title cannot be empty.");
            return MenuState.DraftItem;
        }

        var enrichedGoal = ResolveInlineContextMentions(parsedInline.GoalExpression, workspace, color, out var missingMentions);
        if (missingMentions.Count > 0)
        {
            WarnMissingContextFiles(missingMentions, workspace, color);
            return MenuState.DraftItem;
        }

        var ticket = _workflow.ExecuteQuickGoal(
            goalOrTitle: enrichedGoal,
            backlogTypeInput: backlogType,
            baseOptions: sessionOptions with
            {
                CombinedOutput = combinedOutput,
                DryRun = previewOnly,
                OutputDirectory = previewOnly ? null : sessionOptions.OutputDirectory,
                GoalContext = parsedInline.InlineDetails,
                WebSearchGrounding = parsedInline.EnableWebSearch
            },
            input: input,
            output: output);

        CacheResolvedWorkflowOptions(ref sessionOptions, ref currentSettings, baseWorkspace);
        var resultDescription = combinedOutput ? "Type01 and Type02 items" : $"{ticket.BacklogType} item";
        color.WriteSuccess(previewOnly
            ? $"[Preview] {resultDescription} '{ticket.Title}' generated. No files written."
            : $"[✓] Ready-to-add {resultDescription} '{ticket.Title}' saved to '{sessionOptions.OutputDirectory}'.");

        return MenuState.DraftItem;
    }

    private static void WriteGoalTypingHint(ConsoleColorWriter color, string backlogType, bool combinedOutput)
    {
        if (combinedOutput)
        {
            color.WriteMuted("  Tip [Both]: Example \"Migrate Session Store to Redis | Zero-downtime cutover\" (generates Type 01 + Type 02 + 3 Alternatives).");
        }
        else if (backlogType.Equals("Type02", StringComparison.OrdinalIgnoreCase))
        {
            color.WriteMuted("  Tip [Type 02]: Bare goal OK (e.g. \"check Geocaching 2026 hiding movie theme\") — expands User Story framing, Description & 3 Alternatives.");
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
        ConsoleColorWriter color)
    {
        color.WriteLine();
        color.WriteSubHeader("--- Guided Draft & Refine Wizard ---");
        color.WriteColored("Format ([1] Type01 Story, [2] Type02 Schedule, [3] Both) [default: 1]: ", ConsoleColor.Yellow);
        var formatChoice = input.ReadLine();
        if (formatChoice is null)
        {
            return MenuState.Exit;
        }

        var (backlogType, combinedOutput) = ParseGuidedFormatChoice(formatChoice);

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
            return MenuState.DraftItem;
        }

        var enrichedGoal = ResolveInlineContextMentions(parsedGoal.GoalExpression, workspace, color, out var missingMentions);
        if (missingMentions.Count > 0)
        {
            WarnMissingContextFiles(missingMentions, workspace, color);
            return MenuState.DraftItem;
        }

        color.WriteColored("Additional details / required info to include (Enter to skip): ", ConsoleColor.Yellow);
        var extraDetailsLine = input.ReadLine();
        if (extraDetailsLine is null)
        {
            return MenuState.Exit;
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

        string? previousDraft = null;
        string? refinementFeedback = null;
        string? preservedWebFindings = null;
        var runWebSearchThisPass = webSearch;
        IReadOnlyList<JiraBacklogTicket>? latestTickets = null;
        ProcessedOutcome? latestOutcome = null;

        while (true)
        {
            var draftOptions = sessionOptions with
            {
                BacklogTypeInput = backlogType,
                CombinedOutput = combinedOutput,
                GoalTitle = enrichedGoal,
                GoalContext = details,
                WebSearchGrounding = runWebSearchThisPass,
                PreviousDraftContext = previousDraft,
                RefinementFeedback = refinementFeedback
            };

            latestTickets = _workflow.ExecuteDraftPreview(draftOptions, input, output, out var outcome);
            latestOutcome = outcome;
            if (!string.IsNullOrWhiteSpace(_workflow.LastWebResearchFindings))
            {
                preservedWebFindings = _workflow.LastWebResearchFindings;
            }

            CacheResolvedWorkflowOptions(ref sessionOptions, ref currentSettings, baseWorkspace);
            runWebSearchThisPass = false;

            while (true)
            {
                color.WriteLine();
                color.WriteHeader("[4/5 Review Decision]");
                var hasAlternatives = latestOutcome?.Alternatives is { Count: > 0 };
                if (hasAlternatives)
                {
                    color.WriteLine($"  [1-{latestOutcome!.Alternatives!.Count}] Adopt Alternative Title & Description (#1..#{latestOutcome.Alternatives.Count})");
                }

                color.WriteLine($"  [A] Accept & Save to '{sessionOptions.OutputDirectory}' (default)");
                color.WriteLine("  [M] Copy Markdown to clipboard");
                color.WriteLine("  [W] Copy Raw Text (table) to clipboard");
                color.WriteLine("  [R] Refine Title / Description with feedback");
                color.WriteLine("  [T] Switch format (Type01 / Type02 / Both)");
                color.WriteLine("  [P] Preview only (finish without writing files)");
                color.WriteLine("  [C] Cancel draft");
                var actionPrompt = hasAlternatives
                    ? $"Choose action (1-{latestOutcome!.Alternatives!.Count}/A/M/W/R/T/P/C) [A]: "
                    : "Choose action (A/M/W/R/T/P/C) [A]: ";
                color.WriteColored(actionPrompt, ConsoleColor.Yellow);

                var action = input.ReadLine();
                if (action is null)
                {
                    return MenuState.Exit;
                }

                var normalizedAction = action.Trim().ToLowerInvariant();
                if (int.TryParse(normalizedAction, out var altChoice) &&
                    latestOutcome?.Alternatives is { Count: > 0 } availableAlternatives &&
                    altChoice >= 1 &&
                    altChoice <= availableAlternatives.Count)
                {
                    var chosenAlt = availableAlternatives[altChoice - 1];
                    latestOutcome = ApplyAlternativeToOutcome(latestOutcome, chosenAlt);
                    latestTickets = _workflow.ReformatOutcome(latestOutcome, backlogType, combinedOutput);
                    color.WriteSuccess($"  ✓ Adopted Alternative #{altChoice} ({chosenAlt.Angle}): '{chosenAlt.Title}'");
                    RenderAndCacheWizardTickets(latestOutcome, latestTickets, combinedOutput, color);

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

                if (normalizedAction is "" or "a" or "accept" or "save" or "s")
                {
                    if (!string.IsNullOrWhiteSpace(sessionOptions.OutputDirectory))
                    {
                        var savedPaths = _workflow.PersistTickets(
                            latestTickets,
                            sessionOptions.OutputDirectory,
                            currentSettings.JsonFileOut,
                            output,
                            sessionOptions.CliTheme);
                        if (savedPaths.Count > 0)
                        {
                            _lastMarkdownDocuments = savedPaths.Select(File.ReadAllText).ToArray();
                        }
                    }

                    var savedSummary = combinedOutput ? "Type01 and Type02 items" : $"{latestTickets[0].BacklogType} item";
                    color.WriteSuccess($"[✓] Ready-to-add {savedSummary} '{latestTickets[0].Title}' saved to '{sessionOptions.OutputDirectory}'.");
                    return MenuState.DraftItem;
                }

                if (normalizedAction is "p" or "preview")
                {
                    var previewSummary = combinedOutput ? "Type01 and Type02 items" : $"{latestTickets[0].BacklogType} item";
                    color.WriteSuccess($"[Preview] {previewSummary} '{latestTickets[0].Title}' generated. No files written.");
                    return MenuState.DraftItem;
                }

                if (normalizedAction is "c" or "cancel" or "/back")
                {
                    color.WriteMuted("  Draft cancelled. No files written.");
                    return MenuState.DraftItem;
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

                    latestTickets = _workflow.ReformatOutcome(latestOutcome!, backlogType, combinedOutput);
                    RenderAndCacheWizardTickets(latestOutcome!, latestTickets, combinedOutput, color);

                    continue;
                }

                color.WriteWarning($"  [!] Unknown decision '{action.Trim()}'. Choose 1-3, A, M, W, R, T, P, or C.");
            }
        }
    }

    private static ProcessedOutcome ApplyAlternativeToOutcome(ProcessedOutcome outcome, DraftAlternative chosen)
    {
        var currentDesc = outcome.Description?.Trim() ?? string.Empty;
        string updatedDescription;
        if (string.IsNullOrWhiteSpace(currentDesc) ||
            currentDesc.Equals("Not specified in discovery notes.", StringComparison.OrdinalIgnoreCase))
        {
            updatedDescription = chosen.Description;
        }
        else if (currentDesc.StartsWith("**Objective:**", StringComparison.OrdinalIgnoreCase))
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
            Description = DescriptionFormatter.Normalize(updatedDescription)
        };
    }

    private MenuState ShowSettingsMenu(
        WorkspaceDirectories baseWorkspace,
        ref WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        TextReader input,
        ConsoleColorWriter color)
    {
        WriteSettingsHeader(baseWorkspace, workspace, sessionOptions, currentSettings, color);
        var activeProvider = _diagnosticService.ResolveEffectiveProvider(sessionOptions, currentSettings);
        color.WriteMenuOption("1", $"Toggle JSON Export [{(currentSettings.JsonFileOut ? "ON" : "OFF")}]");
        color.WriteMenuOption("2", "Inspect Directories");
        color.WriteMenuOption("3", "Runtime & AI Diagnostics");
        color.WriteMenuOption("4", $"Select CLI Theme [{ConsoleColorWriter.GetThemeCode(currentSettings.Theme)}]", "Switch between agy (default), uv, and kubectl styles");
        color.WriteMenuOption("5", $"Toggle Live AI API [{(currentSettings.GeminiLive == true ? "ON" : "OFF")}]", "ON calls configured AI provider; OFF uses deterministic local draft");
        color.WriteMenuOption("6", $"Select AI Model [{currentSettings.ModelName ?? "automatic"}]", "Fetch models supported by your active provider");
        color.WriteMenuOption("7", "View settings.json", "Display persisted JSON workspace configuration");
        color.WriteMenuOption("8", $"Select AI Provider [{RuntimeEnvironmentService.FormatProviderCode(activeProvider)}]", "Switch between Gemini, GitHub Copilot SDK, Azure, Claude, & Manual");
        color.WriteMenuOption("0", "Back to Main Menu");
        color.WriteColored("Select an option (0-8): ", ConsoleColor.Yellow);

        var choice = input.ReadLine();
        if (choice is null)
        {
            return MenuState.Exit;
        }

        var trimmed = choice.Trim();
        if (trimmed.Equals("/back", StringComparison.OrdinalIgnoreCase))
        {
            return MenuState.Main;
        }

        if (trimmed.Equals("/exit", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        {
            return MenuState.Exit;
        }

        switch (trimmed)
        {
            case "0":
                return MenuState.Main;
            case "1":
                currentSettings = currentSettings with { JsonFileOut = !currentSettings.JsonFileOut };
                sessionOptions = sessionOptions with { JsonFileOut = currentSettings.JsonFileOut };
                var savedPath = _settingsService.Save(baseWorkspace, currentSettings);
                color.WriteSuccess($"JSON Export is now {(currentSettings.JsonFileOut ? "ON" : "OFF")}. Saved settings to '{savedPath}'.");
                return MenuState.Settings;
            case "2":
                InspectDirectoryContents("Input", workspace.InputDirectory, color);
                InspectDirectoryContents("Output", sessionOptions.OutputDirectory ?? workspace.OutputDirectory, color);
                return MenuState.Settings;
            case "3":
                WriteRuntimeDiagnostics(workspace, sessionOptions, currentSettings, color);
                return MenuState.Settings;
            case "4":
                return MenuState.ThemeSelector;
            case "5":
                currentSettings = currentSettings with { GeminiLive = currentSettings.GeminiLive != true };
                sessionOptions = sessionOptions with { GeminiLive = currentSettings.GeminiLive };
                var liveSettingsPath = _settingsService.Save(baseWorkspace, currentSettings);
                color.WriteSuccess($"Live Gemini API is now {(currentSettings.GeminiLive == true ? "ON" : "OFF")}. Saved settings to '{liveSettingsPath}'.");
                return MenuState.Settings;
            case "6":
                SelectProviderModel(baseWorkspace, ref sessionOptions, ref currentSettings, input, color);
                return MenuState.Settings;
            case "7":
                DisplayPersistedSettingsJson(baseWorkspace, color);
                return MenuState.Settings;
            case "8":
                return MenuState.ProviderSelector;
            default:
                return WarnAndStay(color, choice, "0-8", MenuState.Settings);
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
        color.WriteMenuOption("1", "Agy Style (Default)", "Gemini Agy palette (Cyan headers, Blue steps, Yellow prompts)");
        color.WriteMenuOption("2", "UV Style", "Astral uv palette (Magenta headers, Cyan steps, Green prompts)");
        color.WriteMenuOption("3", "Kubectl Style", "Kubernetes kubectl palette (Blue headers, DarkCyan steps, Cyan prompts)");
        color.WriteMenuOption("0", "Back to Workspace & Settings");
        color.WriteColored("Select a CLI theme (0-3): ", ConsoleColor.Yellow);

        var choice = input.ReadLine();
        if (choice is null)
        {
            return MenuState.Exit;
        }

        var normalized = choice.Trim();
        if (normalized == "0" || normalized.Equals("/back", StringComparison.OrdinalIgnoreCase))
        {
            return MenuState.Settings;
        }

        CliThemeStyle? selectedTheme = normalized.ToLowerInvariant() switch
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
        color.WriteLine();
        color.WriteHeader("=== AI Provider Orchestrator ===");
        color.WriteMuted($"Active Provider: {RuntimeEnvironmentService.FormatProviderCode(activeProvider)}");
        color.WriteLine();
        color.WriteMenuOption("1", "Google Gemini (gemini)", "Live REST generateContent or offline deterministic mode");
        color.WriteMenuOption("2", "GitHub Copilot SDK (copilot-sdk)", "Official GitHub.Copilot.SDK agent runtime & BYOK");
        color.WriteMenuOption("3", "Azure OpenAI / Foundry (azure)", "Copilot SDK BYOK when configured, else mock fallback");
        color.WriteMenuOption("4", "Anthropic Claude (claude)", "Copilot SDK BYOK when configured, else mock fallback");
        color.WriteMenuOption("5", "Manual Prompt (manual)", "Render Markdown prompt for copy/paste into Copilot or any AI");
        color.WriteMenuOption("0", "Back to Workspace & Settings");
        color.WriteColored("Select an AI provider (0-5): ", ConsoleColor.Yellow);

        var choice = input.ReadLine();
        if (choice is null)
        {
            return MenuState.Exit;
        }

        var normalized = choice.Trim();
        if (normalized == "0" || normalized.Equals("/back", StringComparison.OrdinalIgnoreCase))
        {
            return MenuState.Settings;
        }

        AiProviderType? selected = normalized.ToLowerInvariant() switch
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
            return WarnAndStay(color, choice, "0-5", MenuState.ProviderSelector);
        }

        var providerCode = RuntimeEnvironmentService.FormatProviderCode(selected.Value);
        currentSettings = currentSettings with { PreferredProvider = selected.Value };
        sessionOptions = sessionOptions with { ProviderInput = providerCode };
        var savedPath = _settingsService.Save(baseWorkspace, currentSettings);
        color.WriteSuccess($"Active AI Provider set to '{providerCode}'. Saved settings to '{savedPath}'.");
        return MenuState.Settings;
    }

    private void WriteSettingsHeader(
        WorkspaceDirectories baseWorkspace,
        WorkspaceDirectories workspace,
        WorkflowOptions sessionOptions,
        CliSettings currentSettings,
        ConsoleColorWriter color)
    {
        var activeProvider = _diagnosticService.ResolveEffectiveProvider(sessionOptions, currentSettings);
        color.WriteLine();
        color.WriteHeader("=== Workspace & Settings ===");
        color.WriteLine($"Input Dir       : {workspace.InputDirectory}");
        color.WriteLine($"Output Dir      : {sessionOptions.OutputDirectory}");
        color.WriteLine($"Workspace Root  : {workspace.WorkspaceRoot}");
        color.WriteLine($"Windows Root    : {WorkspacePathService.DefaultWindowsWorkspaceRoot}");
        color.WriteLine($"WSL Root        : {WorkspacePathService.DefaultWslWorkspaceRoot}");
        color.WriteLine($"JSON Export     : {(currentSettings.JsonFileOut ? "ON (.md + .json)" : "OFF (.md only)")}");
        color.WriteLine($"AI Provider     : {RuntimeEnvironmentService.FormatProviderCode(activeProvider)}");
        color.WriteLine($"Gemini Live API : {(currentSettings.GeminiLive == true ? "ON" : "OFF")}");
        color.WriteLine($"Gemini Model    : {currentSettings.ModelName ?? "automatic (best available)"}");
        color.WriteLine($"CLI Theme       : {ConsoleColorWriter.FormatThemeDisplay(currentSettings.Theme)}");
        color.WriteLine($"Settings File   : {_settingsService.GetSettingsFilePath(baseWorkspace)}");

        var runtimeInfo = _runtimeEnvService.Inspect(activeProvider, sessionOptions.ModelName);
        var secret = _secretProvider.Resolve(sessionOptions.ApiKey);
        color.WriteMuted(_runtimeEnvService.FormatRuntimeBanner(runtimeInfo, secret).TrimEnd());
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

    private void SelectProviderModel(
        WorkspaceDirectories baseWorkspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        TextReader input,
        ConsoleColorWriter color)
    {
        var provider = _diagnosticService.ResolveEffectiveProvider(sessionOptions, currentSettings);
        if (provider == AiProviderType.GitHubCopilot)
        {
            var copilotModels = _workflow.ListCopilotModels(sessionOptions.ApiKey);
            if (copilotModels.Count == 0)
            {
                color.WriteWarning("  [!] No Copilot SDK models returned.");
                return;
            }

            color.WriteLine();
            color.WriteLine("Copilot SDK models available:");
            for (var i = 0; i < copilotModels.Count; i++)
            {
                color.WriteLine($"  [{i + 1}] {copilotModels[i].Name} ({copilotModels[i].Id})");
            }

            color.Write($"Select a model [default: {RuntimeEnvironmentService.DefaultCopilotModel}]: ");
            var selection = input.ReadLine()?.Trim();
            var modelName = int.TryParse(selection, out var idx) && idx >= 1 && idx <= copilotModels.Count
                ? copilotModels[idx - 1].Id
                : string.IsNullOrWhiteSpace(selection)
                    ? RuntimeEnvironmentService.DefaultCopilotModel
                    : selection;

            sessionOptions = sessionOptions with { ModelName = modelName };
            currentSettings = currentSettings with { ModelName = modelName };
            var savedCopilotPath = _settingsService.Save(baseWorkspace, currentSettings);
            color.WriteSuccess($"Copilot SDK model set to '{modelName}'. Saved settings to '{savedCopilotPath}'.");
            return;
        }

        var secret = _secretProvider.ResolveOrPrompt(sessionOptions.ApiKey, input, color.InnerWriter);
        var models = _workflow.ListGeminiModels(secret.ApiKey!);
        var selectedGeminiModel = AsIsV1Workflow.SelectGeminiModel(
            models,
            requestedModel: null,
            input,
            color.InnerWriter,
            nonInteractive: false);
        sessionOptions = sessionOptions with { ApiKey = secret.ApiKey, ModelName = selectedGeminiModel };
        currentSettings = currentSettings with { ModelName = selectedGeminiModel };
        var savedPath = _settingsService.Save(baseWorkspace, currentSettings);
        color.WriteSuccess($"Gemini model set to '{selectedGeminiModel}'. Saved settings to '{savedPath}'.");
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
        WorkflowOptions sessionOptions,
        TextReader input,
        ConsoleColorWriter color)
    {
        var outputDir = sessionOptions.OutputDirectory ?? workspace.OutputDirectory;
        var mdFiles = GetSavedMarkdownFiles(outputDir);

        color.WriteLine();
        color.WriteHeader("=== Preview & Copy Outcome (Markdown) ===");
        if (mdFiles.Count == 0)
        {
            color.WriteWarning($"  [!] No saved Markdown outcomes found in '{outputDir}'. Run [1] Process or [2] Draft first.");
            return MenuState.Main;
        }

        for (var i = 0; i < mdFiles.Count; i++)
        {
            var info = new FileInfo(mdFiles[i]);
            color.WriteMenuOption((i + 1).ToString(), info.Name, $"{info.Length:N0} bytes");
        }

        color.WriteMenuOption("0", "Back to Main Menu");
        color.WriteColored($"Select a Markdown file [Enter for #1, or 0 to back]: ", ConsoleColor.Yellow);
        var selection = input.ReadLine();
        if (selection is null)
        {
            return MenuState.Exit;
        }

        var trimmed = selection.Trim();
        if (trimmed is "0" or "/back" or "q")
        {
            return MenuState.Main;
        }

        var selectedIndex = 0;
        if (!string.IsNullOrEmpty(trimmed))
        {
            if (!int.TryParse(trimmed, out var chosen) || chosen < 1 || chosen > mdFiles.Count)
            {
                return WarnAndStay(color, trimmed, $"0-{mdFiles.Count}", MenuState.PreviewOutcome);
            }

            selectedIndex = chosen - 1;
        }

        PreviewMarkdownFilePath(mdFiles[selectedIndex], color);
        return PromptCopyFormatLoop(input, color);
    }

    private void ExecuteInlinePreviewCommand(
        string[] args,
        WorkspaceDirectories workspace,
        WorkflowOptions sessionOptions,
        ConsoleColorWriter color)
    {
        var outputDir = sessionOptions.OutputDirectory ?? workspace.OutputDirectory;
        var targetPath = ResolveSavedMarkdownTarget(args[0], outputDir);
        PreviewMarkdownFilePath(targetPath, color);
        color.WriteMuted("  Tip: Run '/copy raw' or '/copy md' to copy this preview to your clipboard.");
    }

    private void ExecuteInlineCopyCommand(string[] args, TextReader input, ConsoleColorWriter color)
    {
        if (_lastMarkdownDocuments.Count == 0)
        {
            color.WriteWarning("  [!] No outcome available yet. Run /process, /draft, or /preview first.");
            return;
        }

        if (args.Length > 0)
        {
            var mode = args[0].Trim().ToLowerInvariant();
            if (mode is "md" or "markdown")
            {
                CopyCurrentOutcomeToClipboard(asMarkdown: true, color);
                return;
            }

            if (mode is "raw" or "text" or "table")
            {
                CopyCurrentOutcomeToClipboard(asMarkdown: false, color);
                return;
            }

            color.WriteWarning($"  [!] Unknown copy format '{args[0]}'. Use '/copy raw' or '/copy md'.");
            return;
        }

        PromptCopyFormatLoop(input, color);
    }

    private MenuState PromptCopyFormatLoop(TextReader input, ConsoleColorWriter color)
    {
        while (true)
        {
            color.WriteLine();
            color.WriteColored("Copy outcome? [1] Raw Text (table)  [2] Markdown  [0] Back: ", ConsoleColor.Yellow);
            var copyChoice = input.ReadLine();
            if (copyChoice is null)
            {
                return MenuState.Exit;
            }

            var normalized = copyChoice.Trim().ToLowerInvariant();
            if (normalized is "" or "0" or "back" or "/back" or "done")
            {
                return MenuState.Main;
            }

            if (normalized is "1" or "raw" or "text" or "w")
            {
                CopyCurrentOutcomeToClipboard(asMarkdown: false, color);
                continue;
            }

            if (normalized is "2" or "md" or "markdown" or "m")
            {
                CopyCurrentOutcomeToClipboard(asMarkdown: true, color);
                continue;
            }

            color.WriteWarning($"  [!] Invalid option '{copyChoice.Trim()}'. Choose 1, 2, or 0.");
        }
    }

    private void PreviewMarkdownFilePath(string filePath, ConsoleColorWriter color)
    {
        var markdown = File.ReadAllText(filePath);
        _lastMarkdownDocuments = [markdown];
        _lastSupplementalUserStory = null;
        _lastSupplementalDescription = null;

        color.WriteMuted($"  Reading Markdown: {filePath}");
        MarkdownOutcomeRenderer.WriteOutcomeTable(color, _lastMarkdownDocuments);
    }

    private void RenderAndCacheWizardTickets(
        ProcessedOutcome outcome,
        IReadOnlyList<JiraBacklogTicket> tickets,
        bool combinedOutput,
        ConsoleColorWriter color)
    {
        var resolvedType = tickets[0].BacklogType;
        var docs = tickets.Select(t => t.FormattedMarkdown).ToArray();
        var suppStory = AsIsV1Workflow.ResolveSupplementalUserStory(outcome, resolvedType, combinedOutput);
        var suppDesc = AsIsV1Workflow.ResolveSupplementalDescription(outcome, resolvedType, combinedOutput);

        _lastMarkdownDocuments = docs;
        _lastSupplementalUserStory = suppStory;
        _lastSupplementalDescription = suppDesc;

        MarkdownOutcomeRenderer.WriteOutcomeTable(color, docs, suppStory, suppDesc);
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
        color.WriteLine("  Numbers        [1] Process Files   [2] Draft Item   [3] Settings   [4] Preview (.md)   [0] Exit");
        color.WriteLine("  /process       Open Process menu, or run inline: /process [all|<file>] [--dry-run] [--json]");
        color.WriteLine("  /draft         Open Draft menu (incl. [5] Guided Wizard), or inline: /draft \"<goal>\" [-t 01|02|both] [--details \"...\"] [--search] [--preview]");
        color.WriteLine("  /preview       Preview saved './out/*.md' outcome table & copy: /preview [filename]");
        color.WriteLine("  /copy          Copy last outcome to clipboard: /copy [raw|md]");
        color.WriteLine("  /status        Show crisp WSL2/Windows paths, queued files, & AI provider readiness table");
        color.WriteLine("  /config        Show or update settings: /config [--json on|off] [--in <dir>] [--out <dir>]");
        color.WriteLine("  /provider      Switch AI orchestrator provider (gemini, copilot-sdk, azure, claude, manual)");
        color.WriteLine("  /models        List available models for the active provider");
        color.WriteLine("  /theme         Switch CLI theme (agy, uv, kubectl)");
        color.WriteLine("  /json [on|off] Toggle dual .md + .json artifact export");
        color.WriteLine("  /live [on|off] Toggle live AI calls vs offline deterministic fallback");
        color.WriteLine("  @filename      Attach file context directly, e.g. '@raw-notes.txt' or '@raw-notes.txt Add auth story'");
        color.WriteLine("  /clear, /exit  Clear screen or exit session");
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
