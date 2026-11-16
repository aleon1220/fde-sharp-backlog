using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Workflow;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Renders crisp, color-coded diagnostic tables and workspace configuration views
/// shared between headless <c>backlog status</c> / <c>backlog config</c> subcommands
/// and the interactive REPL (<c>/status</c>, <c>/config</c>).
/// </summary>
public sealed class WorkspaceDiagnosticService
{
    private readonly WorkspacePathService _workspaceService;
    private readonly RuntimeEnvironmentService _runtimeEnvService;
    private readonly GeminiSecretProvider _secretProvider;
    private readonly CliSettingsService _settingsService;
    private readonly AiModelOrchestrator _modelOrchestrator;
    private readonly AiProviderOrchestrator _providerOrchestrator;

    public WorkspaceDiagnosticService(
        WorkspacePathService? workspaceService = null,
        RuntimeEnvironmentService? runtimeEnvService = null,
        GeminiSecretProvider? secretProvider = null,
        CliSettingsService? settingsService = null,
        AiModelOrchestrator? modelOrchestrator = null,
        AiProviderOrchestrator? providerOrchestrator = null)
    {
        _workspaceService = workspaceService ?? new WorkspacePathService();
        _runtimeEnvService = runtimeEnvService ?? new RuntimeEnvironmentService();
        _secretProvider = secretProvider ?? new GeminiSecretProvider();
        _settingsService = settingsService ?? new CliSettingsService();
        _modelOrchestrator = modelOrchestrator ?? new AiModelOrchestrator();
        _providerOrchestrator = providerOrchestrator ?? new AiProviderOrchestrator();
    }

    public int RenderStatusTable(
        WorkflowOptions options,
        TextWriter output,
        CliSettings? settingsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        var baseWorkspace = _workspaceService.Resolve(options.WorkspaceRoot);
        var settings = settingsOverride ?? _settingsService.Load(baseWorkspace);
        var workspace = _workspaceService.ApplySettings(baseWorkspace, settings);
        var resolvedTheme = ConsoleColorWriter.ResolveTheme(options.CliTheme, settings.Theme);
        var color = new ConsoleColorWriter(output, theme: resolvedTheme);

        var activeProvider = ResolveEffectiveProvider(options, settings);
        var effectiveModel = options.ModelName ?? settings.ModelName;
        var effectiveLive = options.GeminiLive ?? settings.GeminiLive ?? true;
        var effectiveJson = options.JsonFileOut ?? settings.JsonFileOut;

        var runtimeInfo = _runtimeEnvService.Inspect(activeProvider, effectiveModel);
        var secret = _secretProvider.Resolve(options.ApiKey);
        var copilotDiag = _providerOrchestrator.CopilotGateway.InspectDiagnostics();
        var inputFiles = _workspaceService.GetInputFiles(workspace.InputDirectory);
        var outputFiles = Directory.Exists(workspace.OutputDirectory)
            ? Directory.EnumerateFiles(workspace.OutputDirectory).Count()
            : 0;
        var promptExists = File.Exists(options.PromptFilePath);
        var isWsl = WorkspacePathService.IsWslEnvironment();

        var platformSummary = isWsl
            ? $"{runtimeInfo.OsPlatform} (WSL2) | {runtimeInfo.FrameworkDescription}"
            : $"{runtimeInfo.OsPlatform} ({runtimeInfo.ProcessArchitecture}) | {runtimeInfo.FrameworkDescription}";

        var rows = new List<(string Section, string Setting, string Value, ConsoleColor ValueColor)>
        {
            ("Runtime", "OS & .NET", platformSummary, ConsoleColor.Green),
            ("Runtime", "Build Version", $"v{runtimeInfo.BuildVersion} ({runtimeInfo.BuildConfiguration})", ConsoleColor.Cyan),
            ("Runtime", "CLI Theme", ConsoleColorWriter.FormatThemeDisplay(resolvedTheme), ConsoleColor.Cyan),
            ("Paths", "Windows Root", WorkspacePathService.DefaultWindowsWorkspaceRoot, ConsoleColor.Gray),
            ("Paths", "WSL2 Root", WorkspacePathService.DefaultWslWorkspaceRoot, ConsoleColor.Gray),
            ("Paths", "Active In (./in)", $"{workspace.InputDirectory} ({inputFiles.Count} queued)", inputFiles.Count > 0 ? ConsoleColor.Green : ConsoleColor.Yellow),
            ("Paths", "Active Out (./out)", $"{workspace.OutputDirectory} ({outputFiles} file(s))", ConsoleColor.Green),
            ("Paths", "Settings File", _settingsService.GetSettingsFilePath(baseWorkspace), ConsoleColor.DarkGray),
            ("Export", "Artifact Format", effectiveJson ? "ON (.md + .json)" : "OFF (.md only)", effectiveJson ? ConsoleColor.Green : ConsoleColor.Yellow),
            ("Prompts", "Template Source", promptExists ? options.PromptFilePath : $"{options.PromptFilePath} (built-in fallback)", promptExists ? ConsoleColor.Green : ConsoleColor.Yellow),
            ("AI", "Active Provider", $"{RuntimeEnvironmentService.FormatProviderCode(activeProvider)} ({(effectiveLive ? "Live" : "Offline")})", ConsoleColor.Cyan),
            ("AI", "Active Model", runtimeInfo.EnabledAiModel, ConsoleColor.Green),
            ("AI", "Gemini API Key", secret.IsConfigured ? $"{secret.SourceDetail} [{secret.MaskedKey}]" : $"Not set ({secret.KeyPortalUrl})", secret.IsConfigured ? ConsoleColor.Green : ConsoleColor.Yellow),
            ("AI", "Copilot SDK", copilotDiag.Summary, copilotDiag.RuntimeAvailable ? ConsoleColor.Green : ConsoleColor.Yellow)
        };

        TerminalUx.WriteDiagnosticTable(
            color,
            "Smart Backlog Assistant — Workspace & AI Status",
            rows);

        color.WriteLine();
        color.WriteSubHeader("AI Provider Matrix:");
        var matrix = _providerOrchestrator.InspectProviderMatrix(secret, activeProvider, effectiveLive);
        foreach (var entry in matrix)
        {
            var marker = entry.IsReady ? "[✓]" : "[!]";
            var lineColor = entry.IsReady ? ConsoleColor.Green : ConsoleColor.Yellow;
            var activeTag = entry.Provider == activeProvider ? " (active)" : string.Empty;
            color.WriteColored($"  {marker} ", lineColor);
            color.WriteColored($"{entry.DisplayName}{activeTag}".PadRight(32), ConsoleColor.White);
            color.WriteColored($"[{entry.Mode}] ".PadRight(16), ConsoleColor.Cyan);
            color.WriteLineColored(entry.Detail, ConsoleColor.DarkGray);
        }

        return 0;
    }

    public void RenderConfigSummary(
        WorkspaceDirectories baseWorkspace,
        WorkspaceDirectories activeWorkspace,
        CliSettings settings,
        ConsoleColorWriter color)
    {
        var activeProvider = settings.PreferredProvider ?? AiProviderType.GoogleGemini;
        var rows = new List<(string Section, string Setting, string Value, ConsoleColor ValueColor)>
        {
            ("Export", "JSON Export (--json)", settings.JsonFileOut ? "ON (.md + .json)" : "OFF (.md only)", settings.JsonFileOut ? ConsoleColor.Green : ConsoleColor.Yellow),
            ("Paths", "Input Dir (--in)", activeWorkspace.InputDirectory, ConsoleColor.Cyan),
            ("Paths", "Output Dir (--out)", activeWorkspace.OutputDirectory, ConsoleColor.Cyan),
            ("UI", "CLI Theme (--theme)", ConsoleColorWriter.FormatThemeDisplay(settings.Theme), ConsoleColor.Cyan),
            ("AI", "Provider (--provider)", RuntimeEnvironmentService.FormatProviderCode(activeProvider), ConsoleColor.Green),
            ("AI", "Live AI (--live)", (settings.GeminiLive ?? true) ? "ON" : "OFF", (settings.GeminiLive ?? true) ? ConsoleColor.Green : ConsoleColor.Yellow),
            ("AI", "Model (--model)", settings.ModelName ?? "automatic", ConsoleColor.Gray),
            ("Storage", "settings.json", _settingsService.GetSettingsFilePath(baseWorkspace), ConsoleColor.DarkGray)
        };

        TerminalUx.WriteDiagnosticTable(color, "Smart Backlog Assistant — Configuration", rows);
    }

    public AiProviderType ResolveEffectiveProvider(WorkflowOptions options, CliSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(options.ProviderInput))
        {
            return _modelOrchestrator.ResolveProvider(options.ProviderInput);
        }

        return settings.PreferredProvider ?? AiProviderType.GoogleGemini;
    }
}
