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
                new JiraBacklogFormatter());

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
        var apiKey = GetArgValue(args, "--api-key") ?? Environment.GetEnvironmentVariable("AI_API_KEY");
        var backlogType = GetArgValue(args, "--type");
        var outputDir = GetArgValue(args, "--output-dir") ?? "output";
        var nonInteractive = args.Contains("--non-interactive", StringComparer.OrdinalIgnoreCase);

        return new WorkflowOptions(
            InputFilePath: inputPath,
            PromptFilePath: promptPath,
            ProviderInput: provider,
            ApiKey: apiKey,
            BacklogTypeInput: backlogType,
            OutputDirectory: outputDir,
            NonInteractive: nonInteractive);
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
        output.WriteLine("Smart Backlog Assistant — Use Case 1 (Practical Process As-Is v1)");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  dotnet run --project src/SmartBacklogAssistant -- [options]");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --input <path>        Path to raw discovery notes (default: samples/raw-notes.txt)");
        output.WriteLine("  --prompt <path>       Path to Markdown prompt template (default: prompts/as-is-v1-prompt.md)");
        output.WriteLine("  --provider <name>     AI provider: manual (default), azure, gemini, claude");
        output.WriteLine("  --api-key <key>       Optional API key for mocked provider (or set AI_API_KEY env var)");
        output.WriteLine("  --type <Type01|Type02> Jira backlog type: Type01 (Given/When/Then) or Type02 (Title+Description)");
        output.WriteLine("  --output-dir <path>   Directory for Jira Markdown & JSON output (default: output)");
        output.WriteLine("  --non-interactive     Run without interactive console prompts");
        output.WriteLine("  -h, --help            Show help information");
    }
}
