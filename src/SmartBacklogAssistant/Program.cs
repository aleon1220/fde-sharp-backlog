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
            var workflow = new AsIsV1Workflow(
                new RawDataCollector(),
                new PromptTemplateService(),
                new AiModelOrchestrator(),
                new JiraBacklogFormatter(),
                new RuntimeEnvironmentService(),
                new GeminiSecretProvider());

            workflow.Execute(options, Console.In, Console.Out);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
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
        var nonInteractive = args.Contains("--non-interactive", StringComparer.OrdinalIgnoreCase);

        return new WorkflowOptions(
            InputFilePath: inputPath,
            PromptFilePath: promptPath,
            ProviderInput: provider,
            ApiKey: apiKey,
            BacklogTypeInput: backlogType,
            OutputDirectory: outputDir,
            NonInteractive: nonInteractive,
            ModelName: modelName);
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
        output.WriteLine("Smart Backlog Assistant — Use Case 1 (Practical Process As-Is v1 + Gemini Runtime Secret Injection)");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  dotnet run --project src/SmartBacklogAssistant -- [options]");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --input <path>        Path to raw discovery notes (default: samples/raw-notes.txt)");
        output.WriteLine("  --prompt <path>       Path to Markdown prompt template (default: prompts/as-is-v1-prompt.md)");
        output.WriteLine("  --provider <name>     AI provider: gemini (default), manual, azure, claude");
        output.WriteLine("  --model <name>        Gemini model name (default: gemini-2.5-flash, or set GEMINI_MODEL env var)");
        output.WriteLine("  --api-key <key>       Gemini API key (or set GEMINI_API_KEY / GOOGLE_API_KEY / AI_API_KEY in OS env)");
        output.WriteLine("                        Obtain a Gemini key from: https://aistudio.google.com/api-keys");
        output.WriteLine("  --type <Type01|Type02> Jira backlog type: Type01 (Given/When/Then) or Type02 (Title+Description)");
        output.WriteLine("  --output-dir <path>   Directory for Jira Markdown & JSON output (default: output)");
        output.WriteLine("  --non-interactive     Run without interactive console prompts");
        output.WriteLine("  -h, --help            Show help information");
    }
}
