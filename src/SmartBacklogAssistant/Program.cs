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
            var collector = new RawDataCollector();
            var promptService = new PromptTemplateService();
            var orchestrator = new AiModelOrchestrator();
            var formatter = new JiraBacklogFormatter();
            var runtimeEnvService = new RuntimeEnvironmentService();
            var secretProvider = new GeminiSecretProvider();
            var workspaceService = new WorkspacePathService();
            var settingsService = new CliSettingsService();

            var workflow = new AsIsV1Workflow(
                collector,
                promptService,
                orchestrator,
                formatter,
                runtimeEnvService,
                secretProvider);

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
        var inputPath = GetArgValue(args, "--input") ?? "samples/raw-notes.txt";
        var promptPath = GetArgValue(args, "--prompt") ?? "prompts/as-is-v1-prompt.md";
        var provider = GetArgValue(args, "--provider");
        var apiKey = GetArgValue(args, "--api-key");
        var backlogType = GetArgValue(args, "--type");
        var outputDir = GetArgValue(args, "--output-dir") ?? "output";
        var modelName = GetArgValue(args, "--model");
        var workspaceRoot = GetArgValue(args, "--workspace");
        var goalTitle = GetArgValue(args, "--goal") ?? GetArgValue(args, "--title");
        var nonInteractive = args.Contains("--non-interactive", StringComparer.OrdinalIgnoreCase);
        var forceMenu = args.Contains("--menu", StringComparer.OrdinalIgnoreCase);
        var jsonFileOut = ParseJsonFileOutArg(args);

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
            JsonFileOut: jsonFileOut);
    }

    public static bool ShouldLaunchMenu(string[] args, WorkflowOptions options)
    {
        if (options.ForceMenu)
        {
            return true;
        }

        if (options.NonInteractive)
        {
            return false;
        }

        var hasDirectWorkflowFlag =
            HasFlag(args, "--input") ||
            HasFlag(args, "--output-dir") ||
            HasFlag(args, "--type") ||
            HasFlag(args, "--goal") ||
            HasFlag(args, "--title");

        return !hasDirectWorkflowFlag;
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

        if (string.IsNullOrWhiteSpace(options.WorkspaceRoot))
        {
            return options with
            {
                JsonFileOut = effectiveJsonOut
            };
        }

        workspaceService.EnsureDirectoriesExist(workspace);

        var inputPath = HasFlag(args, "--input")
            ? WorkspacePathService.NormalizePathForOs(options.InputFilePath, workspace.IsWindows)
            : workspace.InputDirectory;

        var outputDir = HasFlag(args, "--output-dir") && !string.IsNullOrWhiteSpace(options.OutputDirectory)
            ? WorkspacePathService.NormalizePathForOs(options.OutputDirectory, workspace.IsWindows)
            : workspace.OutputDirectory;

        return options with
        {
            InputFilePath = inputPath,
            OutputDirectory = outputDir,
            JsonFileOut = effectiveJsonOut
        };
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

    private static string? GetArgValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void PrintHelp(TextWriter output)
    {
        var color = new ConsoleColorWriter(output);
        color.WriteHeader("Smart Backlog Assistant — Interactive CLI Menu & AI-First Backlog Generator");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  dotnet run --project src/SmartBacklogAssistant -- [options]");
        output.WriteLine();
        output.WriteLine("Workspace Directories (Windows & WSL Compatible):");
        output.WriteLine(@"  Windows Default Root : c:\workspace\smart-backlog-assistant\{in,out}");
        output.WriteLine("  WSL Compatible Root  : /mnt/c/workspace/smart-backlog-assistant/{in,out}");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --menu                Launch interactive colored CLI menu (default when run interactively)");
        output.WriteLine(@"  --workspace <path>    Workspace root override (default: c:\workspace on Windows, /mnt/c/workspace on WSL)");
        output.WriteLine("  --goal <text>         Quick-add a Goal or Title directly into the output directory");
        output.WriteLine("  --input <path>        Path to raw discovery notes or input folder (default: samples/raw-notes.txt)");
        output.WriteLine("  --prompt <path>       Path to Markdown prompt template (default: prompts/as-is-v1-prompt.md)");
        output.WriteLine("  --provider <name>     AI provider: gemini (default), manual, azure, claude");
        output.WriteLine("  --model <name>        Gemini model name (default: gemini-2.5-flash, or set GEMINI_MODEL env var)");
        output.WriteLine("  --api-key <key>       Gemini API key (or set GEMINI_API_KEY / GOOGLE_API_KEY / AI_API_KEY in OS env)");
        output.WriteLine("                        Obtain a Gemini key from: https://aistudio.google.com/api-keys");
        output.WriteLine("  --type <01|02>        Jira backlog type: Type01/01 (Given/When/Then) or Type02/02 (Title+Description)");
        output.WriteLine("  --output-dir <path>   Directory for Jira Markdown & JSON output (default: output)");
        output.WriteLine("  --json-out [true|false] Enable writing .json backlog files alongside .md files (default: false / .md only)");
        output.WriteLine("  --no-json-out         Disable writing .json backlog files (generate .md files only)");
        output.WriteLine("  --non-interactive     Run without interactive console prompts");
        output.WriteLine("  -h, --help            Show help information");
    }
}
