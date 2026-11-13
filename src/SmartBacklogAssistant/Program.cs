using SmartBacklogAssistant.Services;
using SmartBacklogAssistant.Workflow;

namespace SmartBacklogAssistant;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) ||
                args.Contains("-h", StringComparer.OrdinalIgnoreCase))
            {
                PrintHelp(Console.Out);
                return 0;
            }

            var options = ParseArgs(args);
            var runtimeEnvService = new RuntimeEnvironmentService();
            var secretProvider = new GeminiSecretProvider();
            var geminiApiClient = new GeminiApiClient();
            var workspaceService = new WorkspacePathService();
            var settingsService = new CliSettingsService();
            var orchestrator = new AiModelOrchestrator();

            if (options.ShowVersion)
            {
                var info = runtimeEnvService.Inspect();
                Console.WriteLine($"SmartBacklogAssistant v{info.BuildVersion} ({info.BuildConfiguration}) | {info.FrameworkDescription} | {info.OsPlatform} ({info.ProcessArchitecture})");
                return 0;
            }

            if (options.ShowDoctorStatus)
            {
                return RunDoctorStatus(options, runtimeEnvService, secretProvider, workspaceService, settingsService, orchestrator, Console.Out);
            }

            if (options.ListGeminiModels)
            {
                var secret = secretProvider.ResolveOrPrompt(options.ApiKey, Console.In, Console.Out, options.NonInteractive);
                var models = geminiApiClient.ListGenerateContentModels(secret.ApiKey!);
                foreach (var model in models)
                {
                    Console.WriteLine($"{model.ModelId}\t{model.DisplayName}\t{model.Description}");
                }

                return 0;
            }

            var collector = new RawDataCollector();
            var promptService = new PromptTemplateService();
            var formatter = new JiraBacklogFormatter();

            var workflow = new AsIsV1Workflow(
                collector,
                promptService,
                orchestrator,
                formatter,
                runtimeEnvService,
                secretProvider,
                geminiApiClient);

            if (ShouldLaunchMenu(args, options))
            {
                var menuRunner = new CliMenuRunner(
                    workflow,
                    workspaceService,
                    runtimeEnvService,
                    secretProvider,
                    orchestrator,
                    settingsService);

                return menuRunner.Run(options, Console.In, Console.Out);
            }

            // Direct / headless execution: resolve workspace in/out directories and persisted settings
            var resolvedOptions = ResolveDirectExecutionOptions(args, options, workspaceService, settingsService);
            workflow.Execute(resolvedOptions, Console.In, Console.Out);
            return 0;
        }
        catch (Exception ex)
        {
            var errorWriter = new ConsoleColorWriter(Console.Error);
            errorWriter.WriteError($"Error: {ex.Message}");
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
            ?? (subcommand is "draft" or "add" ? subcommandArg : null);
        if (subcommand is "run" or "process" && !string.IsNullOrWhiteSpace(subcommandArg) && !HasAnyFlag(args, "--input", "-i"))
        {
            inputPath = subcommandArg;
        }

        var nonInteractive = args.Contains("--non-interactive", StringComparer.OrdinalIgnoreCase);
        var forceMenu = args.Contains("--menu", StringComparer.OrdinalIgnoreCase) || subcommand is "menu" or "tui";
        var (jsonFileOut, outputFormatCombined) = ParseOutputAndJsonFlags(args);
        var combinedOutput = outputFormatCombined ||
                             args.Contains("--combined", StringComparer.OrdinalIgnoreCase) ||
                             string.Equals(backlogType?.Trim(), "both", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(backlogType?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        bool? geminiLive = args.Contains("--offline", StringComparer.OrdinalIgnoreCase)
            ? false
            : args.Contains("--gemini-live", StringComparer.OrdinalIgnoreCase) ||
              args.Contains("--live", StringComparer.OrdinalIgnoreCase)
                ? true
                : null;
        var listGeminiModels = args.Contains("--list-models", StringComparer.OrdinalIgnoreCase) || subcommand is "models";
        var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        var showDoctorStatus = args.Contains("--doctor", StringComparer.OrdinalIgnoreCase) ||
                               args.Contains("--status", StringComparer.OrdinalIgnoreCase) ||
                               subcommand is "doctor" or "status";
        var showVersion = args.Contains("--version", StringComparer.OrdinalIgnoreCase) ||
                          args.Contains("-v", StringComparer.OrdinalIgnoreCase) ||
                          subcommand is "version";
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
            CliTheme: cliTheme);
    }

    public static bool ShouldLaunchMenu(string[] args, WorkflowOptions options)
    {
        if (options.ForceMenu)
        {
            return true;
        }

        if (options.NonInteractive || options.ShowDoctorStatus || options.ShowVersion || options.ListGeminiModels)
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
            HasAnyFlag(args, "--gemini-live", "--live", "--offline") ||
            HasAnyFlag(args, "--list-models", "--dry-run", "--combined");

        return !hasDirectWorkflowFlag;
    }

    public static int RunDoctorStatus(
        WorkflowOptions options,
        RuntimeEnvironmentService runtimeEnvService,
        GeminiSecretProvider secretProvider,
        WorkspacePathService workspaceService,
        CliSettingsService settingsService,
        AiModelOrchestrator orchestrator,
        TextWriter output)
    {
        var workspace = workspaceService.Resolve(options.WorkspaceRoot);
        var settings = settingsService.Load(workspace);
        var resolvedTheme = ConsoleColorWriter.ResolveTheme(options.CliTheme, settings.Theme);
        var color = new ConsoleColorWriter(output, theme: resolvedTheme);
        var provider = string.IsNullOrWhiteSpace(options.ProviderInput)
            ? Domain.AiProviderType.GoogleGemini
            : orchestrator.ResolveProvider(options.ProviderInput);
        var runtimeInfo = runtimeEnvService.Inspect(provider, options.ModelName);
        var secret = secretProvider.Resolve(options.ApiKey);

        color.WriteHeader("Smart Backlog Assistant — Environment & Workspace Readiness (doctor)");
        color.WriteMuted(runtimeEnvService.FormatRuntimeBanner(runtimeInfo, secret));
        color.WriteLine();

        var promptExists = File.Exists(options.PromptFilePath);
        var inputFiles = workspaceService.GetInputFiles(workspace.InputDirectory);
        var effectiveJsonOut = options.JsonFileOut ?? settings.JsonFileOut;

        color.WriteSuccess($"  [✓] Runtime         : {runtimeInfo.FrameworkDescription} on {runtimeInfo.OsPlatform} ({runtimeInfo.ProcessArchitecture})");
        color.WriteSuccess($"  [✓] CLI Theme       : {ConsoleColorWriter.FormatThemeDisplay(resolvedTheme)}");
        color.WriteSuccess($"  [✓] Active Model    : {runtimeInfo.EnabledAiModel}");
        if (secret.IsConfigured)
        {
            color.WriteSuccess($"  [✓] Gemini API Key  : {secret.SourceDetail} [{secret.MaskedKey}]");
        }
        else
        {
            color.WriteWarning($"  [!] Gemini API Key  : Not set in OS/CLI (obtain from {secret.KeyPortalUrl})");
        }

        if (promptExists)
        {
            color.WriteSuccess($"  [✓] Prompt Template : {options.PromptFilePath}");
        }
        else
        {
            color.WriteWarning($"  [!] Prompt Template : '{options.PromptFilePath}' not found (will use built-in fallback)");
        }

        color.WriteSuccess($"  [✓] Workspace Root  : {workspace.WorkspaceRoot}");
        color.WriteSuccess($"  [✓] Input Directory : {workspace.InputDirectory} ({inputFiles.Count} queued file(s))");
        color.WriteSuccess($"  [✓] Output Directory: {workspace.OutputDirectory} (Export: {(effectiveJsonOut ? ".md + .json" : ".md only")})");
        return 0;
    }

    private static (string? Subcommand, string? PositionalArg) ParseSubcommand(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            return (null, null);
        }

        var token = args[0].Trim().ToLowerInvariant();
        var canonical = token switch
        {
            "run" or "process" => "run",
            "draft" or "add" => "draft",
            "models" => "models",
            "doctor" or "status" => "doctor",
            "menu" or "tui" => "menu",
            "version" => "version",
            _ => null
        };

        if (canonical is null)
        {
            return (null, null);
        }

        string? positionalArg = null;
        if (args.Length > 1 && !args[1].StartsWith('-'))
        {
            positionalArg = args[1].Trim();
        }

        return (canonical, positionalArg);
    }

    private static WorkflowOptions ResolveDirectExecutionOptions(
        string[] args,
        WorkflowOptions options,
        WorkspacePathService workspaceService,
        CliSettingsService settingsService)
    {
        var workspace = workspaceService.Resolve(options.WorkspaceRoot);
        var persistedSettings = settingsService.Load(workspace);
        var effectiveJsonOut = options.JsonFileOut ?? persistedSettings.JsonFileOut;
        var effectiveTheme = options.CliTheme ?? ConsoleColorWriter.GetThemeCode(persistedSettings.Theme);
        workspaceService.EnsureDirectoriesExist(workspace);

        var hasExplicitInput = HasAnyFlag(args, "--input", "-i") ||
                               options.Subcommand == "run" && !string.IsNullOrWhiteSpace(GetSubcommandInput(args));
        var inputPath = hasExplicitInput
            ? WorkspacePathService.NormalizePathForOs(options.InputFilePath, workspace.IsWindows)
            : workspace.InputDirectory;
        var outputDir = HasFlag(args, "--output-dir") && !string.IsNullOrWhiteSpace(options.OutputDirectory)
            ? workspaceService.ResolveCustomOrRelativePath(options.OutputDirectory, workspace.IsWindows)
            : workspace.OutputDirectory;

        return options with
        {
            InputFilePath = inputPath,
            OutputDirectory = outputDir,
            JsonFileOut = effectiveJsonOut,
            CliTheme = effectiveTheme,
            ModelName = options.ModelName ?? persistedSettings.ModelName,
            GeminiLive = options.GeminiLive ?? persistedSettings.GeminiLive ?? true
        };
    }

    private static string? GetSubcommandInput(string[] args)
    {
        return args.Length > 1 && !args[1].StartsWith('-') ? args[1] : null;
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
            if (string.Equals(args[i], "--no-json-out", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--no-json-file-out", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(args[i], "--json-out", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--json-file-out", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    var nextVal = args[i + 1].Trim().ToLowerInvariant();
                    if (nextVal is "true" or "on" or "1" or "yes")
                    {
                        return true;
                    }

                    if (nextVal is "false" or "off" or "0" or "no")
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        return null;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        return args.Contains(flag, StringComparer.OrdinalIgnoreCase);
    }

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

    private static void PrintHelp(TextWriter output)
    {
        var color = new ConsoleColorWriter(output);
        color.WriteHeader("Smart Backlog Assistant — Ergonomic CLI & TUI for AI-First Backlog Generation");
        color.WriteMuted("Inspired by Gemini CLI (agy), Astral uv, and kubectl");
        output.WriteLine();
        output.WriteLine("USAGE:");
        output.WriteLine("  dotnet run --project src/SmartBacklogAssistant -- [command] [options]");
        output.WriteLine();
        output.WriteLine("COMMANDS (agy / uv / kubectl style):");
        output.WriteLine("  menu | tui            Launch interactive colored TUI menu (default when run with no args)");
        output.WriteLine("  run [file]            Process raw discovery notes from file or workspace './in'");
        output.WriteLine("  draft <goal>          Quick-draft a Jira item directly from a goal/title (alias: add)");
        output.WriteLine("  models                List Gemini models compatible with generateContent");
        output.WriteLine("  doctor | status       Run environment, workspace, and Gemini API key readiness check");
        output.WriteLine("  version               Print build, .NET runtime, and OS architecture version");
        output.WriteLine();
        output.WriteLine("WORKSPACE DIRECTORIES (Windows & WSL Compatible):");
        output.WriteLine(@"  Windows Default Root : c:\workspace\smart-backlog-assistant\{in,out}");
        output.WriteLine("  WSL Compatible Root  : /mnt/c/workspace/smart-backlog-assistant/{in,out}");
        output.WriteLine();
        output.WriteLine("OPTIONS:");
        output.WriteLine("  --menu                Launch interactive colored CLI menu (default when run interactively)");
        output.WriteLine("  --theme <agy|uv|kubectl> CLI color theme style (default: agy)");
        output.WriteLine(@"  -w, --workspace <path> Workspace root override (default: c:\workspace on Windows, /mnt/c/workspace on WSL)");
        output.WriteLine("  --goal <text>         Quick-add a Goal or Title directly into the output directory");
        output.WriteLine("  -i, --input <path>    Path to raw discovery notes or input folder (default: samples/raw-notes.txt)");
        output.WriteLine("  -p, --prompt <path>   Path to Markdown prompt template (default: prompts/as-is-v1-prompt.md)");
        output.WriteLine("  --provider <name>     AI provider: gemini (default), manual, azure, claude");
        output.WriteLine("  -m, --model <name>    Gemini model ID (default: best API-supported model or GEMINI_MODEL)");
        output.WriteLine("  --list-models         List models available to your API key that support generateContent");
        output.WriteLine("  --gemini-live         Generate the backlog draft through the Gemini API (default)");
        output.WriteLine("  --offline             Use the local deterministic draft without calling the Gemini API");
        output.WriteLine("  --api-key <key>       Gemini API key (or set GEMINI_API_KEY / GOOGLE_API_KEY / AI_API_KEY in OS env)");
        output.WriteLine("                        Obtain a Gemini key from: https://aistudio.google.com/api-keys");
        output.WriteLine("  -t, --type <01|02|both> Jira backlog type: Type01/01 (Given/When/Then), Type02/02 (Title+Description), or both");
        output.WriteLine("  --output-dir <path>   Directory for Jira Markdown & JSON output (default: output)");
        output.WriteLine("  -o, --output <md|json|both> Output artifact format selector (default: md)");
        output.WriteLine("  --json-out [true|false] Enable writing .json backlog files alongside .md files (default: false / .md only)");
        output.WriteLine("  --no-json-out         Disable writing .json backlog files (generate .md files only)");
        output.WriteLine("  --dry-run             Preview generated Jira item in terminal without writing files to disk");
        output.WriteLine("  --non-interactive     Run without interactive console prompts");
        output.WriteLine("  -v, --version         Print version information");
        output.WriteLine("  -h, --help            Show help information");
    }
}
