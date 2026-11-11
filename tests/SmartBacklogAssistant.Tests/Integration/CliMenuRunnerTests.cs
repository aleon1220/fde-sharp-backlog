using SmartBacklogAssistant.Services;
using SmartBacklogAssistant.Workflow;

namespace SmartBacklogAssistant.Tests.Integration;

public sealed class CliMenuRunnerTests : IDisposable
{
    private readonly string _tempWorkspaceRoot;
    private readonly string _promptFilePath;

    public CliMenuRunnerTests()
    {
        _tempWorkspaceRoot = Path.Combine(Path.GetTempPath(), $"sba-menu-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempWorkspaceRoot);

        _promptFilePath = Path.Combine(_tempWorkspaceRoot, "as-is-v1-prompt.md");
        File.WriteAllText(_promptFilePath, """
            # As-Is v1 Prompt
            Analyze the following raw notes:
            {{RAW_DATA}}
            """);
    }

    [Fact]
    public void Run_ProcessAllFiles_UsesInputDirectoryAndWritesToOutputDirectory()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);
        Directory.CreateDirectory(dirs.InputDirectory);
        Directory.CreateDirectory(dirs.OutputDirectory);

        File.WriteAllText(Path.Combine(dirs.InputDirectory, "discovery-01.txt"), """
            Discovery Note - Automated Failover Check
            Context: Regional database failover requires manual intervention today.
            Requirement: As an SRE, I want automated health-probe failover so that regional outages recover within 60 seconds.
            Precondition: Primary database region becomes unreachable.
            Action: Health monitor detects 3 consecutive probe failures.
            Expected Outcome: Traffic routes to standby region automatically.
            """);

        using var input = new StringReader("1\n1\n01\n0\n0\n");
        using var output = new StringWriter();

        var menu = CreateMenuRunner(workspaceService);
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            ApiKey: "AIzaSyMenuTestKey1234",
            WorkspaceRoot: _tempWorkspaceRoot);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "jira-backlog-item.md")));
        Assert.False(File.Exists(Path.Combine(dirs.OutputDirectory, "jira-backlog-item.json")));
        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "type01-automated-failover-check.md")));
        Assert.False(File.Exists(Path.Combine(dirs.OutputDirectory, "type01-automated-failover-check.json")));

        var consoleText = output.ToString();
        Assert.Contains("Smart Backlog Assistant", consoleText);
        Assert.Contains("Process Input Files", consoleText);
        Assert.Contains("In: ./in (1 files) | Out: ./out | Export: .md (JSON: OFF)", consoleText);
        Assert.Contains("Automated Failover Check", consoleText);
    }

    [Fact]
    public void Run_DryRunPreviewsSelectedFileWithoutWritingArtifacts()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);
        Directory.CreateDirectory(dirs.InputDirectory);
        Directory.CreateDirectory(dirs.OutputDirectory);
        File.WriteAllText(Path.Combine(dirs.InputDirectory, "preview.txt"), "Title: Preview only\nContext: Validate dry-run behavior.");

        using var input = new StringReader("1\n3\n01\n0\n0\n");
        using var output = new StringWriter();
        var menu = CreateMenuRunner(workspaceService);
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            ApiKey: "AIzaSyDryRunKey1234",
            WorkspaceRoot: _tempWorkspaceRoot);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);
        Assert.Empty(Directory.GetFiles(dirs.OutputDirectory));
        Assert.Contains("Dry Run (Preview Only)", output.ToString());
        Assert.Contains("No files written", output.ToString());
    }

    [Fact]
    public void Run_SettingsTogglePersistsAndUpdatesRootStatus()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var settingsService = new CliSettingsService();
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);

        using var input = new StringReader(
            "3\n1\n0\n" +
            "2\n1\nEnable JSON Export Setting\n0\n" +
            "0\n");
        using var output = new StringWriter();

        var menu = CreateMenuRunner(workspaceService, settingsService);
        var options = new WorkflowOptions(
            InputFilePath: "samples/raw-notes.txt",
            PromptFilePath: _promptFilePath,
            ApiKey: "AIzaSyMenuSettingsKey1234",
            WorkspaceRoot: _tempWorkspaceRoot);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);

        var persistedSettings = settingsService.Load(dirs);
        Assert.True(persistedSettings.JsonFileOut);
        Assert.True(File.Exists(settingsService.GetSettingsFilePath(dirs)));

        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "jira-backlog-item.md")));
        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "jira-backlog-item.json")));
        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "type01-enable-json-export-setting.md")));
        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "type01-enable-json-export-setting.json")));

        var consoleText = output.ToString();
        Assert.Contains("Workspace & Settings", consoleText);
        Assert.Contains("JSON Export is now ON", consoleText);
        Assert.Contains("Export: .md (JSON: ON)", consoleText);
    }

    [Fact]
    public void Run_Option2_AddsGoalOrTitleWithType01AndType02ReadyInOutDirectory()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);

        using var input = new StringReader(
            "2\n1\nImplement OAuth2 PKCE Login\n" +
            "2\nMigrate Telemetry to OpenTelemetry\n" +
            "3\nGenerate Both OAuth2 Formats\n0\n" +
            "3\n3\n0\n0\n");
        using var output = new StringWriter();

        var menu = CreateMenuRunner(workspaceService);
        var options = new WorkflowOptions(
            InputFilePath: "samples/raw-notes.txt",
            PromptFilePath: _promptFilePath,
            ApiKey: "AIzaSyMenuKey7788",
            WorkspaceRoot: _tempWorkspaceRoot);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);

        var type01MdPath = Path.Combine(dirs.OutputDirectory, "type01-implement-oauth2-pkce-login.md");
        var type02MdPath = Path.Combine(dirs.OutputDirectory, "type02-migrate-telemetry-to-opentelemetry.md");
        var combinedType01Path = Path.Combine(dirs.OutputDirectory, "type01-generate-both-oauth2-formats.md");
        var combinedType02Path = Path.Combine(dirs.OutputDirectory, "type02-generate-both-oauth2-formats.md");

        Assert.True(File.Exists(type01MdPath));
        Assert.True(File.Exists(type02MdPath));
        Assert.True(File.Exists(combinedType01Path));
        Assert.True(File.Exists(combinedType02Path));

        var type01Content = File.ReadAllText(type01MdPath);
        Assert.Contains("# [Jira - complete (Type01)] Implement OAuth2 PKCE Login", type01Content);
        Assert.Contains("## Acceptance Criteria (Given / When / Then)", type01Content);

        var type02Content = File.ReadAllText(type02MdPath);
        Assert.Contains("# [Jira - schedule (Type02)] Migrate Telemetry to OpenTelemetry", type02Content);
        Assert.Contains("## Description", type02Content);

        var consoleText = output.ToString();
        Assert.Contains(@"Windows: c:\workspace\smart-backlog-assistant\in", consoleText);
        Assert.Contains("WSL    : /mnt/c/workspace/smart-backlog-assistant/in", consoleText);
    }

    [Fact]
    public void Run_SettingsCanInspectDirectorySizesAndRuntimeDiagnostics()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);
        Directory.CreateDirectory(dirs.InputDirectory);
        Directory.CreateDirectory(dirs.OutputDirectory);
        File.WriteAllText(Path.Combine(dirs.InputDirectory, "queued.txt"), "queued");
        File.WriteAllText(Path.Combine(dirs.OutputDirectory, "existing.md"), "artifact");

        using var input = new StringReader("3\n2\n3\n0\n0\n");
        using var output = new StringWriter();
        var menu = CreateMenuRunner(workspaceService);
        var options = new WorkflowOptions(
            InputFilePath: dirs.InputDirectory,
            PromptFilePath: _promptFilePath,
            WorkspaceRoot: _tempWorkspaceRoot);

        var exitCode = menu.Run(options, input, output);

        Assert.Equal(0, exitCode);
        Assert.Contains("queued.txt (6 bytes)", output.ToString());
        Assert.Contains("existing.md (8 bytes)", output.ToString());
        Assert.Contains("GEMINI_API_KEY:", output.ToString());
        Assert.Contains("AI Model Enabled", output.ToString());
    }

    private static CliMenuRunner CreateMenuRunner(
        WorkspacePathService workspaceService,
        CliSettingsService? settingsService = null)
    {
        var secretProvider = new GeminiSecretProvider(
            environmentReader: _ => null,
            environmentWriter: (_, _) => { });
        var runtimeService = new RuntimeEnvironmentService();
        var orchestrator = new AiModelOrchestrator();

        var workflow = new AsIsV1Workflow(
            new RawDataCollector(),
            new PromptTemplateService(),
            orchestrator,
            new JiraBacklogFormatter(),
            runtimeService,
            secretProvider);

        return new CliMenuRunner(
            workflow,
            workspaceService,
            runtimeService,
            secretProvider,
            orchestrator,
            settingsService ?? new CliSettingsService());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempWorkspaceRoot))
        {
            Directory.Delete(_tempWorkspaceRoot, recursive: true);
        }
    }
}
