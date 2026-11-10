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
    public void Run_Option1_ReadsFromSmartBacklogAssistantInAndOutputsToOut()
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

        // Menu sequence:
        // "1" -> Option 1 (Process Input Directory)
        // ""  -> Select file 1 (default)
        // "01" -> Choose Type 01 in workflow review
        // "0" -> Exit menu
        using var input = new StringReader("1\n\n01\n0\n");
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
        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "jira-backlog-item.json")));
        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "type01-automated-failover-check.md")));
        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "type01-automated-failover-check.json")));

        var consoleText = output.ToString();
        Assert.Contains("SMART BACKLOG ASSISTANT — CLI MENU", consoleText);
        Assert.Contains("Automated Failover Check", consoleText);
    }

    [Fact]
    public void Run_Option2_AddsGoalOrTitleWithType01AndType02ReadyInOutDirectory()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempWorkspaceRoot);

        // Menu sequence:
        // "2" -> Option 2 (Add Goal or Title)
        // "Implement OAuth2 PKCE Login" -> Goal/Title
        // "01" -> Select Type 01 (Given/When/Then)
        // "2" -> Option 2 (Add Goal or Title again)
        // "Migrate Telemetry to OpenTelemetry" -> Goal/Title
        // "02" -> Select Type 02 (Title + Description)
        // "3" -> Inspect Workspace & Runtime Status
        // "0" -> Exit
        using var input = new StringReader(
            "2\nImplement OAuth2 PKCE Login\n01\n" +
            "2\nMigrate Telemetry to OpenTelemetry\n02\n" +
            "3\n" +
            "0\n");
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

        Assert.True(File.Exists(type01MdPath));
        Assert.True(File.Exists(type02MdPath));

        var type01Content = File.ReadAllText(type01MdPath);
        Assert.Contains("# [Jira - complete (Type01)] Implement OAuth2 PKCE Login", type01Content);
        Assert.Contains("## Acceptance Criteria (Given / When / Then)", type01Content);

        var type02Content = File.ReadAllText(type02MdPath);
        Assert.Contains("# [Jira - schedule (Type02)] Migrate Telemetry to OpenTelemetry", type02Content);
        Assert.Contains("## Description", type02Content);

        var consoleText = output.ToString();
        Assert.Contains(@"Windows Default Root : c:\workspace", consoleText);
        Assert.Contains("WSL Compatible Root  : /mnt/c/workspace", consoleText);
    }

    private static CliMenuRunner CreateMenuRunner(WorkspacePathService workspaceService)
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
            orchestrator);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempWorkspaceRoot))
        {
            Directory.Delete(_tempWorkspaceRoot, recursive: true);
        }
    }
}
