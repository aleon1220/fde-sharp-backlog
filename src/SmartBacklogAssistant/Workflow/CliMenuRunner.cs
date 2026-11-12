using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Workflow;

/// <summary>
/// Interactive CLI menu with basic ConsoleColor styling, WSL ("/mnt/c/workspace") and
/// Windows ("c:\workspace") workspace support, directory processing ("smart-backlog-assistant/in" -> "out"),
/// and a quick-add Goal/Title generator for Type01 and Type02 backlog items.
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
        Exit
    }

    private readonly AsIsV1Workflow _workflow;
    private readonly WorkspacePathService _workspaceService;
    private readonly RuntimeEnvironmentService _runtimeEnvService;
    private readonly GeminiSecretProvider _secretProvider;
    private readonly AiModelOrchestrator _orchestrator;
    private readonly CliSettingsService _settingsService;

    public CliMenuRunner(
        AsIsV1Workflow workflow,
        WorkspacePathService? workspaceService = null,
        RuntimeEnvironmentService? runtimeEnvService = null,
        GeminiSecretProvider? secretProvider = null,
        AiModelOrchestrator? orchestrator = null,
        CliSettingsService? settingsService = null)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _workspaceService = workspaceService ?? new WorkspacePathService();
        _runtimeEnvService = runtimeEnvService ?? new RuntimeEnvironmentService();
        _secretProvider = secretProvider ?? new GeminiSecretProvider();
        _orchestrator = orchestrator ?? new AiModelOrchestrator();
        _settingsService = settingsService ?? new CliSettingsService();
    }

    public int Run(WorkflowOptions baseOptions, TextReader input, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(baseOptions);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var workspace = _workspaceService.Resolve(baseOptions.WorkspaceRoot);
        _workspaceService.EnsureDirectoriesExist(workspace);

        var persistedSettings = _settingsService.Load(workspace);
        var resolvedTheme = !string.IsNullOrWhiteSpace(baseOptions.CliTheme)
            ? ConsoleColorWriter.ResolveTheme(baseOptions.CliTheme, persistedSettings.Theme)
            : persistedSettings.Theme;
        var currentSettings = persistedSettings with
        {
            JsonFileOut = baseOptions.JsonFileOut ?? persistedSettings.JsonFileOut,
            Theme = resolvedTheme
        };

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
            CliTheme = ConsoleColorWriter.GetThemeCode(currentSettings.Theme)
        };

        var state = MenuState.Main;
        while (state != MenuState.Exit)
        {
            state = state switch
            {
                MenuState.Main => ShowMainMenu(workspace, currentSettings, input, color),
                MenuState.ProcessFiles => ShowProcessFilesMenu(workspace, ref sessionOptions, input, output, color),
                MenuState.DraftItem => ShowDraftItemMenu(ref sessionOptions, input, output, color),
                MenuState.Settings => ShowSettingsMenu(workspace, ref sessionOptions, ref currentSettings, input, color),
                MenuState.ThemeSelector => ShowThemeMenu(workspace, ref sessionOptions, ref currentSettings, input, color),
                _ => MenuState.Exit
            };
        }

        color.WriteLine();
        color.WriteSuccess("Goodbye! Backlog artifacts are ready in: " + sessionOptions.OutputDirectory);
        return 0;
    }

    private MenuState ShowMainMenu(
        WorkspaceDirectories workspace,
        CliSettings currentSettings,
        TextReader input,
        ConsoleColorWriter color)
    {
        var files = _workspaceService.GetInputFiles(workspace.InputDirectory);
        var themeCode = ConsoleColorWriter.GetThemeCode(currentSettings.Theme);
        color.WriteLine();
        color.WriteHeader("Smart Backlog Assistant");
        color.WriteMuted($"In: ./in ({files.Count} files) | Out: ./out | Export: .md (JSON: {(currentSettings.JsonFileOut ? "ON" : "OFF")}) | Theme: {themeCode}");
        color.WriteLine();
        color.WriteMenuOption("1", "Process Input Files", "Extract Jira items from './in' to './out'");
        color.WriteMenuOption("2", "Draft Backlog Item", "Create a User Story (Type01) or Task (Type02)");
        color.WriteMenuOption("3", "Workspace & Settings", "Configure outputs, CLI themes (agy/uv/kubectl), & diagnostics");
        color.WriteMenuOption("0", "Exit");
        color.WriteColored("Select an option (0-3): ", ConsoleColor.Yellow);

        var choice = input.ReadLine();
        if (choice is null)
        {
            return MenuState.Exit;
        }

        return choice.Trim() switch
        {
            "0" or "q" or "Q" => MenuState.Exit,
            "1" => MenuState.ProcessFiles,
            "2" => MenuState.DraftItem,
            "3" => MenuState.Settings,
            _ => WarnAndStay(color, choice, "0, 1, 2, or 3", MenuState.Main)
        };
    }

    private MenuState ShowProcessFilesMenu(
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
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
            color.WriteLine($"  {Path.GetFileName(files[index])}");
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
        color.WriteColored("Select an option (0-3): ", ConsoleColor.Yellow);

        var choice = input.ReadLine();
        if (choice is null)
        {
            return MenuState.Exit;
        }

        switch (choice.Trim())
        {
            case "0":
                return MenuState.Main;
            case "1":
                ProcessFiles(files, false, ref sessionOptions, input, output, color);
                return MenuState.ProcessFiles;
            case "2":
                var selectedFile = SelectSingleFile(files, input, color);
                if (selectedFile is not null)
                {
                    ProcessFiles([selectedFile], false, ref sessionOptions, input, output, color);
                }

                return MenuState.ProcessFiles;
            case "3":
                ProcessFiles(files, true, ref sessionOptions, input, output, color);
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
        ref WorkflowOptions sessionOptions,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color)
    {
        if (files.Count == 0)
        {
            color.WriteWarning("  [!] No input files to process.");
            return;
        }

        foreach (var filePath in files)
        {
            color.WriteLine();
            color.WriteSubHeader($">>> {(dryRun ? "Previewing" : "Processing")} '{Path.GetFileName(filePath)}'...");
            var fileOptions = sessionOptions with
            {
                InputFilePath = filePath,
                GoalTitle = null,
                OutputDirectory = dryRun ? null : sessionOptions.OutputDirectory
            };

            var ticket = _workflow.Execute(fileOptions, input, output);
            CacheResolvedApiKeyIfPresent(ref sessionOptions);
            if (dryRun)
            {
                color.WriteSuccess($"[Preview] '{ticket.Title}' ({ticket.BacklogType} -> {ticket.JiraStage}). No files written.");
            }
            else
            {
                color.WriteSuccess($"[✓] Completed '{ticket.Title}' ({ticket.BacklogType} -> {ticket.JiraStage}).");
            }
        }
    }

    private MenuState ShowDraftItemMenu(
        ref WorkflowOptions sessionOptions,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color)
    {
        color.WriteLine();
        color.WriteHeader("=== Draft Backlog Item ===");
        color.WriteMenuOption("1", "Complete User Story (Type 01)", "Title, Description, User Story, and Given/When/Then criteria");
        color.WriteMenuOption("2", "Schedule Item (Type 02)", "Concise Title and Engineering Description");
        color.WriteMenuOption("3", "Combined Output (Both)", "Generate Type 01 and Type 02 from one draft");
        color.WriteMenuOption("0", "Back to Main Menu");
        color.WriteColored("Select an option (0-3): ", ConsoleColor.Yellow);

        var choice = input.ReadLine();
        if (choice is null)
        {
            return MenuState.Exit;
        }

        var normalizedChoice = choice.Trim();
        if (normalizedChoice == "0")
        {
            return MenuState.Main;
        }

        if (normalizedChoice is not ("1" or "2" or "3"))
        {
            return WarnAndStay(color, choice, "0, 1, 2, or 3", MenuState.DraftItem);
        }

        color.WriteColored("Enter Goal or Title: ", ConsoleColor.Yellow);
        var goalOrTitle = input.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(goalOrTitle))
        {
            color.WriteWarning("  [!] Goal or Title cannot be empty.");
            return MenuState.DraftItem;
        }

        var combinedOutput = normalizedChoice == "3";
        var backlogType = normalizedChoice == "2" ? "Type02" : "Type01";
        var ticket = _workflow.ExecuteQuickGoal(
            goalOrTitle: goalOrTitle,
            backlogTypeInput: backlogType,
            baseOptions: sessionOptions with { CombinedOutput = combinedOutput },
            input: input,
            output: output);

        CacheResolvedApiKeyIfPresent(ref sessionOptions);
        var resultDescription = combinedOutput ? "Type01 and Type02 items" : $"{ticket.BacklogType} item";
        color.WriteSuccess($"[✓] Ready-to-add {resultDescription} '{ticket.Title}' saved to '{sessionOptions.OutputDirectory}'.");
        return MenuState.DraftItem;
    }

    private MenuState ShowSettingsMenu(
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        ref CliSettings currentSettings,
        TextReader input,
        ConsoleColorWriter color)
    {
        WriteSettingsHeader(workspace, sessionOptions, currentSettings, color);
        color.WriteMenuOption("1", $"Toggle JSON Export [{(currentSettings.JsonFileOut ? "ON" : "OFF")}]");
        color.WriteMenuOption("2", "Inspect Directories");
        color.WriteMenuOption("3", "Runtime & AI Diagnostics");
        color.WriteMenuOption("4", $"Select CLI Theme [{ConsoleColorWriter.GetThemeCode(currentSettings.Theme)}]", "Switch between agy (default), uv, and kubectl styles");
        color.WriteMenuOption("0", "Back to Main Menu");
        color.WriteColored("Select an option (0-4): ", ConsoleColor.Yellow);

        var choice = input.ReadLine();
        if (choice is null)
        {
            return MenuState.Exit;
        }

        switch (choice.Trim())
        {
            case "0":
                return MenuState.Main;
            case "1":
                currentSettings = currentSettings with { JsonFileOut = !currentSettings.JsonFileOut };
                sessionOptions = sessionOptions with { JsonFileOut = currentSettings.JsonFileOut };
                var savedPath = _settingsService.Save(workspace, currentSettings);
                color.WriteSuccess($"JSON Export is now {(currentSettings.JsonFileOut ? "ON" : "OFF")}. Saved settings to '{savedPath}'.");
                return MenuState.Settings;
            case "2":
                InspectDirectoryContents("Input", workspace.InputDirectory, color);
                InspectDirectoryContents("Output", sessionOptions.OutputDirectory ?? workspace.OutputDirectory, color);
                return MenuState.Settings;
            case "3":
                WriteRuntimeDiagnostics(workspace, sessionOptions, color);
                return MenuState.Settings;
            case "4":
                return MenuState.ThemeSelector;
            default:
                return WarnAndStay(color, choice, "0, 1, 2, 3, or 4", MenuState.Settings);
        }
    }

    private MenuState ShowThemeMenu(
        WorkspaceDirectories workspace,
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
        if (normalized == "0")
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
        var savedPath = _settingsService.Save(workspace, currentSettings);
        color.WriteSuccess($"CLI Theme is now {ConsoleColorWriter.FormatThemeDisplay(selectedTheme.Value)}. Saved settings to '{savedPath}'.");
        return MenuState.Settings;
    }

    private void WriteSettingsHeader(
        WorkspaceDirectories workspace,
        WorkflowOptions sessionOptions,
        CliSettings currentSettings,
        ConsoleColorWriter color)
    {
        color.WriteLine();
        color.WriteHeader("=== Workspace & Settings ===");
        color.WriteLine($"Input Dir       : {workspace.InputDirectory}");
        color.WriteLine($"Output Dir      : {sessionOptions.OutputDirectory}");
        color.WriteLine($"Workspace Root  : {workspace.WorkspaceRoot}");
        color.WriteLine($"Windows Root    : {WorkspacePathService.DefaultWindowsWorkspaceRoot}");
        color.WriteLine($"WSL Root        : {WorkspacePathService.DefaultWslWorkspaceRoot}");
        color.WriteLine($"JSON Export     : {(currentSettings.JsonFileOut ? "ON (.md + .json)" : "OFF (.md only)")}");
        color.WriteLine($"CLI Theme       : {ConsoleColorWriter.FormatThemeDisplay(currentSettings.Theme)}");
        color.WriteLine($"Settings File   : {_settingsService.GetSettingsFilePath(workspace)}");

        var provider = string.IsNullOrWhiteSpace(sessionOptions.ProviderInput)
            ? AiProviderType.GoogleGemini
            : _orchestrator.ResolveProvider(sessionOptions.ProviderInput);
        var runtimeInfo = _runtimeEnvService.Inspect(provider, sessionOptions.ModelName);
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

        var provider = string.IsNullOrWhiteSpace(sessionOptions.ProviderInput)
            ? AiProviderType.GoogleGemini
            : _orchestrator.ResolveProvider(sessionOptions.ProviderInput);
        var runtimeInfo = _runtimeEnvService.Inspect(provider, sessionOptions.ModelName);
        var secret = _secretProvider.Resolve(sessionOptions.ApiKey);
        color.WriteMuted(_runtimeEnvService.FormatRuntimeBanner(runtimeInfo, secret).TrimEnd());
    }

    private void CacheResolvedApiKeyIfPresent(ref WorkflowOptions sessionOptions)
    {
        if (!string.IsNullOrWhiteSpace(sessionOptions.ApiKey))
        {
            return;
        }

        var resolved = _secretProvider.Resolve();
        if (resolved.IsConfigured)
        {
            sessionOptions = sessionOptions with { ApiKey = resolved.ApiKey };
        }
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
