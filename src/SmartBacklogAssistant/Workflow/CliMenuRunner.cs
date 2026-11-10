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
    private readonly AsIsV1Workflow _workflow;
    private readonly WorkspacePathService _workspaceService;
    private readonly RuntimeEnvironmentService _runtimeEnvService;
    private readonly GeminiSecretProvider _secretProvider;
    private readonly AiModelOrchestrator _orchestrator;

    public CliMenuRunner(
        AsIsV1Workflow workflow,
        WorkspacePathService? workspaceService = null,
        RuntimeEnvironmentService? runtimeEnvService = null,
        GeminiSecretProvider? secretProvider = null,
        AiModelOrchestrator? orchestrator = null)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _workspaceService = workspaceService ?? new WorkspacePathService();
        _runtimeEnvService = runtimeEnvService ?? new RuntimeEnvironmentService();
        _secretProvider = secretProvider ?? new GeminiSecretProvider();
        _orchestrator = orchestrator ?? new AiModelOrchestrator();
    }

    public int Run(WorkflowOptions baseOptions, TextReader input, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(baseOptions);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var color = new ConsoleColorWriter(output);
        var workspace = _workspaceService.Resolve(baseOptions.WorkspaceRoot);
        _workspaceService.EnsureDirectoriesExist(workspace);

        // Ensure runtime options point to the resolved workspace out directory unless explicitly overridden
        var effectiveOutputDir = string.IsNullOrWhiteSpace(baseOptions.OutputDirectory) ||
                                 string.Equals(baseOptions.OutputDirectory, "output", StringComparison.OrdinalIgnoreCase)
            ? workspace.OutputDirectory
            : _workspaceService.ResolveCustomOrRelativePath(baseOptions.OutputDirectory, workspace.IsWindows);

        var sessionOptions = baseOptions with
        {
            OutputDirectory = effectiveOutputDir
        };

        WriteWelcomeBanner(color, workspace);

        while (true)
        {
            color.WriteLine();
            color.WriteHeader("┌──────────────────────────────────────────────────────────────────────┐");
            color.WriteHeader("│                 SMART BACKLOG ASSISTANT — CLI MENU                   │");
            color.WriteHeader("└──────────────────────────────────────────────────────────────────────┘");
            color.WriteMuted($"  Input Dir : {workspace.InputDirectory}");
            color.WriteMuted($"  Output Dir: {sessionOptions.OutputDirectory}");
            color.WriteLine();
            color.WriteMenuOption("1", "Process Input Directory", $"Read from '{WorkspacePathService.AppDirectoryName}/{WorkspacePathService.InputFolderName}' -> output to '{WorkspacePathService.AppDirectoryName}/{WorkspacePathService.OutputFolderName}'");
            color.WriteMenuOption("2", "Add Goal or Title", "Create a new Goal/Title and select Type 01 or Type 02 ready to be added");
            color.WriteMenuOption("3", "Workspace & Runtime Status", "Inspect WSL / Windows paths, AI model, and directory contents");
            color.WriteMenuOption("0", "Exit", "Close Smart Backlog Assistant");
            color.WriteLine();
            color.WriteColored("Select an option (1, 2, 3, or 0 to exit): ", ConsoleColor.Yellow);

            var choice = input.ReadLine();
            if (choice is null)
            {
                color.WriteLine();
                color.WriteMuted("Exiting Smart Backlog Assistant.");
                return 0;
            }

            var normalizedChoice = choice.Trim().ToLowerInvariant();
            switch (normalizedChoice)
            {
                case "0" or "q" or "quit" or "exit":
                    color.WriteSuccess("Goodbye! Backlog artifacts are ready in: " + sessionOptions.OutputDirectory);
                    return 0;

                case "1":
                    HandleProcessInputDirectory(workspace, ref sessionOptions, input, output, color);
                    break;

                case "2":
                    HandleAddGoalOrTitle(ref sessionOptions, input, output, color);
                    break;

                case "3":
                    HandleInspectStatus(workspace, sessionOptions, color);
                    break;

                default:
                    color.WriteWarning($"  [!] Unknown menu option '{choice.Trim()}'. Please choose 1, 2, 3, or 0.");
                    break;
            }
        }
    }

    private void HandleProcessInputDirectory(
        WorkspaceDirectories workspace,
        ref WorkflowOptions sessionOptions,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color)
    {
        _workspaceService.EnsureDirectoriesExist(workspace);
        var files = _workspaceService.GetInputFiles(workspace.InputDirectory);

        color.WriteLine();
        color.WriteHeader($"=== [Option 1] Process Files from '{workspace.InputDirectory}' ===");

        if (files.Count == 0)
        {
            color.WriteWarning($"  [!] No .txt or .md files found in '{workspace.InputDirectory}'.");
            color.WriteMuted("  Tip: Add a discovery file to the 'in' folder or use Option [2] to add a Goal/Title directly.");
            return;
        }

        for (var i = 0; i < files.Count; i++)
        {
            color.WriteMenuOption((i + 1).ToString(), Path.GetFileName(files[i]), files[i]);
        }

        if (files.Count > 1)
        {
            color.WriteMenuOption("A", "All Files", $"Process all {files.Count} files in '{workspace.InputDirectory}'");
        }

        color.WriteColored($"Select file [1-{files.Count}] (press Enter for 1{(files.Count > 1 ? ", or A for all" : "")}): ", ConsoleColor.Yellow);
        var selection = input.ReadLine()?.Trim();

        IReadOnlyList<string> selectedFiles;
        if (string.IsNullOrWhiteSpace(selection) || selection == "1")
        {
            selectedFiles = [files[0]];
        }
        else if (string.Equals(selection, "a", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(selection, "all", StringComparison.OrdinalIgnoreCase))
        {
            selectedFiles = files;
        }
        else if (int.TryParse(selection, out var index) && index >= 1 && index <= files.Count)
        {
            selectedFiles = [files[index - 1]];
        }
        else
        {
            color.WriteWarning($"  [!] Invalid file selection '{selection}'. Defaulting to '{Path.GetFileName(files[0])}'.");
            selectedFiles = [files[0]];
        }

        foreach (var filePath in selectedFiles)
        {
            color.WriteLine();
            color.WriteSubHeader($">>> Processing '{Path.GetFileName(filePath)}'...");
            var fileOptions = sessionOptions with
            {
                InputFilePath = filePath,
                GoalTitle = null
            };

            var ticket = _workflow.Execute(fileOptions, input, output);
            CacheResolvedApiKeyIfPresent(ref sessionOptions);
            color.WriteSuccess($"[✓] Completed '{ticket.Title}' ({ticket.BacklogType} -> {ticket.JiraStage}).");
        }
    }

    private void HandleAddGoalOrTitle(
        ref WorkflowOptions sessionOptions,
        TextReader input,
        TextWriter output,
        ConsoleColorWriter color)
    {
        color.WriteLine();
        color.WriteHeader("=== [Option 2] Add Goal or Title (Ready-to-Add Backlog Item) ===");
        color.WriteColored("Enter Goal or Title: ", ConsoleColor.Yellow);

        var goalOrTitle = input.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(goalOrTitle))
        {
            color.WriteWarning("  [!] Goal or Title cannot be empty. Returning to main menu.");
            return;
        }

        color.WriteLine();
        color.WriteSubHeader("Select Output Backlog Type:");
        color.WriteMenuOption("01", "Type 01 (complete)", "Given / When / Then acceptance criteria + User Story (default)");
        color.WriteMenuOption("02", "Type 02 (schedule)", "Title + Description ready for scheduling");
        color.WriteColored("Select output type (01 or 02) [default: 01]: ", ConsoleColor.Yellow);

        var typeInput = input.ReadLine()?.Trim();
        var resolvedTypeInput = string.IsNullOrWhiteSpace(typeInput) ? "Type01" : typeInput;

        var ticket = _workflow.ExecuteQuickGoal(
            goalOrTitle: goalOrTitle,
            backlogTypeInput: resolvedTypeInput,
            baseOptions: sessionOptions,
            input: input,
            output: output);

        CacheResolvedApiKeyIfPresent(ref sessionOptions);
        color.WriteSuccess($"[✓] Ready-to-add {ticket.BacklogType} item '{ticket.Title}' saved to '{sessionOptions.OutputDirectory}'.");
    }

    private void HandleInspectStatus(
        WorkspaceDirectories workspace,
        WorkflowOptions sessionOptions,
        ConsoleColorWriter color)
    {
        color.WriteLine();
        color.WriteHeader("=== [Option 3] Workspace & Runtime Status ===");

        var provider = string.IsNullOrWhiteSpace(sessionOptions.ProviderInput)
            ? AiProviderType.GoogleGemini
            : _orchestrator.ResolveProvider(sessionOptions.ProviderInput);

        var secret = _secretProvider.Resolve(sessionOptions.ApiKey);
        var runtimeInfo = _runtimeEnvService.Inspect(provider, sessionOptions.ModelName);
        color.WriteMuted(_runtimeEnvService.FormatRuntimeBanner(runtimeInfo, secret));

        color.WriteLine();
        color.WriteSubHeader("--- Cross-Platform Workspace Paths (Windows & WSL) ---");
        color.WriteLine($"  Windows Default Root : {WorkspacePathService.DefaultWindowsWorkspaceRoot}");
        color.WriteLine($"  WSL Compatible Root  : {WorkspacePathService.DefaultWslWorkspaceRoot}");
        color.WriteLine($"  Active Workspace Root: {workspace.WorkspaceRoot}");
        color.WriteLine($"  Input Directory (in) : {workspace.InputDirectory}");
        color.WriteLine($"  Output Directory(out): {sessionOptions.OutputDirectory}");

        var inFiles = _workspaceService.GetInputFiles(workspace.InputDirectory);
        color.WriteLine($"  Files in 'in/' ({inFiles.Count})   : {(inFiles.Count == 0 ? "none" : string.Join(", ", inFiles.Select(Path.GetFileName)))}");

        if (!string.IsNullOrWhiteSpace(sessionOptions.OutputDirectory) && Directory.Exists(sessionOptions.OutputDirectory))
        {
            var outFiles = Directory.EnumerateFiles(sessionOptions.OutputDirectory)
                .Select(Path.GetFileName)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            color.WriteLine($"  Files in 'out/' ({outFiles.Length})  : {(outFiles.Length == 0 ? "none" : string.Join(", ", outFiles))}");
        }
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

    private static void WriteWelcomeBanner(ConsoleColorWriter color, WorkspaceDirectories workspace)
    {
        color.WriteHeader("========================================================================");
        color.WriteHeader("  Smart Backlog Assistant — AI-First SDLC CLI (Windows & WSL Compatible)");
        color.WriteHeader("========================================================================");
        color.WriteMuted($"  Active Workspace : {workspace.WorkspaceRoot} (Windows: {WorkspacePathService.DefaultWindowsWorkspaceRoot} | WSL: {WorkspacePathService.DefaultWslWorkspaceRoot})");
        color.WriteMuted($"  Reading from     : {workspace.InputDirectory}");
        color.WriteMuted($"  Outputting to    : {workspace.OutputDirectory}");
    }
}
