using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

/// <summary>
/// Verifies Issue #50 deliverables: GitHub Project V2 schema manifest (custom fields + 4 saved views),
/// deterministic C# issue/PR metadata classification, PR linked-issue extraction, lifecycle status transitions,
/// project-sync.yml workflow contracts, cross-platform setup scripts, and PROJECT_TOKEN secret redaction.
/// </summary>
public sealed class GitHubProjectSyncContractTests
{
    private static readonly string RepositoryRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void ProjectV2SchemaManifest_DefinesAllRequiredCustomFieldsOptionsAndFourSavedViews()
    {
        var schemaPath = Path.Combine(RepositoryRoot, ".github", "project-v2-schema.json");
        var manifest = GitHubProjectSyncService.LoadSchemaManifest(schemaPath);

        Assert.Equal("1.0.0", manifest.SchemaVersion);
        Assert.Equal("Status", manifest.StatusField.Name);
        Assert.Equal(
            ["Todo", "In Progress", "In Review", "Done"],
            manifest.StatusField.Options.Select(static o => o.Name).ToArray());

        var fieldsByName = manifest.CustomFields.ToDictionary(static f => f.Name, StringComparer.Ordinal);

        Assert.True(fieldsByName.TryGetValue("Priority", out var priorityField));
        Assert.Equal(["P0", "P1", "P2", "P3"], priorityField.Options.Select(static o => o.Name).ToArray());

        Assert.True(fieldsByName.TryGetValue("Backlog Type", out var backlogTypeField));
        Assert.Equal(
            ["Type01: Complete", "Type02: Schedule", "Baseline/Audit"],
            backlogTypeField.Options.Select(static o => o.Name).ToArray());

        Assert.True(fieldsByName.TryGetValue("Area", out var areaField));
        Assert.Equal(
            ["CLI/TUI", "AI Providers", "SRE/DevOps", "Prompt Library", "Docs/GitHub"],
            areaField.Options.Select(static o => o.Name).ToArray());

        Assert.True(fieldsByName.TryGetValue("Target Milestone", out var milestoneField));
        Assert.Equal(["M1", "M2", "M3"], milestoneField.Options.Select(static o => o.Name).ToArray());

        Assert.True(fieldsByName.TryGetValue("Effort", out var effortField));
        Assert.Equal(["XS", "S", "M", "L"], effortField.Options.Select(static o => o.Name).ToArray());

        Assert.Equal(
            ["Active Sprint Board", "M1 Release & UAT Gate", "M2 Priority Triage", "v2.0 Roadmap"],
            manifest.Views.Select(static v => v.Name).ToArray());

        var sprintBoard = manifest.Views.Single(static v => v.Name == "Active Sprint Board");
        Assert.Equal("Status", sprintBoard.GroupBy);

        var m1View = manifest.Views.Single(static v => v.Name == "M1 Release & UAT Gate");
        Assert.Contains("M1", m1View.Filter);

        var m2View = manifest.Views.Single(static v => v.Name == "M2 Priority Triage");
        Assert.Equal("Area", m2View.GroupBy);
        Assert.Contains("M2", m2View.Filter);

        var m3View = manifest.Views.Single(static v => v.Name == "v2.0 Roadmap");
        Assert.Contains("M3", m3View.Filter);
    }

    [Fact]
    public void ClassifyItem_AccuratelyClassifiesMilestone1And2And3IssuesAcrossAllAreasAndTypes()
    {
        // Issue #50: Type01 Complete, P1, Docs/GitHub, M1
        var issue50 = GitHubProjectSyncService.ClassifyItem(
            title: "[Jira - complete (Type01)] [GitHub Project 1/3] Configure GitHub Project V2 Custom Fields, Views, and Automated Workflow Sync",
            body: "**Priority:** P1 | **Persona:** Senior PM / DevOps\n## Acceptance Criteria (Given / When / Then)",
            state: "OPEN",
            milestoneNumber: 1,
            milestoneTitle: "release-fde-version",
            labels: ["enhancement"]);

        Assert.Equal(GitHubProjectSyncService.StatusTodo, issue50.Status);
        Assert.Equal("P1", issue50.Priority);
        Assert.Equal(GitHubProjectSyncService.BacklogTypeComplete, issue50.BacklogType);
        Assert.Equal(GitHubProjectSyncService.AreaDocsGitHub, issue50.Area);
        Assert.Equal("M1", issue50.TargetMilestone);
        Assert.Equal("M", issue50.Effort);

        // Baseline audit issue (#14): Baseline/Audit, P2, CLI/TUI, M2, XS
        var baseline14 = GitHubProjectSyncService.ClassifyItem(
            title: "[Baseline 01/11] Headless CLI Subcommands, Argument Parsing, and Terminal UX",
            body: "Baseline inventory check for CLI subcommands.",
            state: "OPEN",
            milestoneNumber: 2,
            milestoneTitle: "v1.1.0-core-capabilities-and-polish",
            labels: ["documentation"]);

        Assert.Equal(GitHubProjectSyncService.BacklogTypeBaseline, baseline14.BacklogType);
        Assert.Equal("P2", baseline14.Priority);
        Assert.Equal(GitHubProjectSyncService.AreaCliTui, baseline14.Area);
        Assert.Equal("M2", baseline14.TargetMilestone);
        Assert.Equal("XS", baseline14.Effort);

        // Prompt Library issue (#54): M3, Prompt Library, L
        var prompt54 = GitHubProjectSyncService.ClassifyItem(
            title: "[Feature] SDLC & TDLC Prompt Scanner and Action Prompt Assistant via fde-AI-prompts",
            body: "**Priority:** P2\nIntegrate with fde-AI-prompts repository.",
            state: "OPEN",
            milestoneNumber: 3,
            milestoneTitle: "v2.0.0-managed-fde-backlog");

        Assert.Equal(GitHubProjectSyncService.AreaPromptLibrary, prompt54.Area);
        Assert.Equal("M3", prompt54.TargetMilestone);
        Assert.Equal("L", prompt54.Effort);

        // SRE/DevOps closed issue (#57): Done, SRE/DevOps, M1
        var nuget57 = GitHubProjectSyncService.ClassifyItem(
            title: "Harden Pre-Production NuGet Restores to Prevent UAT and Dogfooding Outages",
            body: "**Priority:** P1 | (Type02)",
            state: "CLOSED",
            milestoneNumber: 1,
            milestoneTitle: "release-fde-version");

        Assert.Equal(GitHubProjectSyncService.StatusDone, nuget57.Status);
        Assert.Equal(GitHubProjectSyncService.BacklogTypeSchedule, nuget57.BacklogType);
        Assert.Equal(GitHubProjectSyncService.AreaSreDevOps, nuget57.Area);
        Assert.Equal("M1", nuget57.TargetMilestone);
    }

    [Fact]
    public void ExtractLinkedIssueNumbers_AndLifecycleTransitions_FollowPullRequestStateAndMergeEvents()
    {
        var linked = GitHubProjectSyncService.ExtractLinkedIssueNumbers(
            prTitle: "feat(project): configure Project V2 custom fields and workflow sync (#50)",
            prBody: "Implements Project V2 automation.\n\nCloses #50\nAlso fixes #51 and resolves #52.");

        Assert.Equal([50, 51, 52], linked);

        // Draft PR -> In Progress
        Assert.Equal(
            GitHubProjectSyncService.StatusInProgress,
            GitHubProjectSyncService.ResolveLinkedIssueStatusForPullRequest("open", isDraftPullRequest: true, isMergedPullRequest: false));

        // Ready for review PR -> In Review
        Assert.Equal(
            GitHubProjectSyncService.StatusInReview,
            GitHubProjectSyncService.ResolveLinkedIssueStatusForPullRequest("open", isDraftPullRequest: false, isMergedPullRequest: false));

        // Merged PR -> Done
        Assert.Equal(
            GitHubProjectSyncService.StatusDone,
            GitHubProjectSyncService.ResolveLinkedIssueStatusForPullRequest("closed", isDraftPullRequest: false, isMergedPullRequest: true));

        // Closed unmerged PR -> Todo
        Assert.Equal(
            GitHubProjectSyncService.StatusTodo,
            GitHubProjectSyncService.ResolveLinkedIssueStatusForPullRequest("closed", isDraftPullRequest: false, isMergedPullRequest: false));
    }

    [Fact]
    public void ProjectSyncWorkflow_Scripts_Docs_AndSecretRedactor_MeetIssue50Contracts()
    {
        // Milestone #2 issue mentioning Milestone #1 in body must still resolve to M2
        var milestone2WithBodyRef = GitHubProjectSyncService.ClassifyItem(
            title: "Add unit test coverage for workflow sync",
            body: "Follow-up task that builds on Milestone #1 scaffolding.",
            state: "OPEN",
            milestoneNumber: 2,
            milestoneTitle: "v1.1.0-core-capabilities-and-polish");

        Assert.Equal("M2", milestone2WithBodyRef.TargetMilestone);

        var workflowPath = Path.Combine(RepositoryRoot, ".github", "workflows", "project-sync.yml");
        Assert.True(File.Exists(workflowPath), $"Expected workflow at '{workflowPath}'.");
        var workflow = File.ReadAllText(workflowPath);

        Assert.Contains("issues:", workflow);
        Assert.Contains("pull_request_target:", workflow);
        Assert.Contains("workflow_dispatch:", workflow);
        Assert.Contains("actions/add-to-project@v1.0.2", workflow);
        Assert.Contains("PROJECT_TOKEN", workflow);
        Assert.Contains("scripts/setup-github-project.sh", workflow);
        Assert.Contains("--sync-event", workflow);

        var bashScript = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "setup-github-project.sh"));
        Assert.Contains("addProjectV2ItemById", bashScript);
        Assert.Contains("updateProjectV2ItemFieldValue", bashScript);
        Assert.Contains("createProjectV2Field", bashScript);
        Assert.Contains("updateProjectV2Field", bashScript);
        Assert.DoesNotContain("updateProjectV2SingleSelectField", bashScript);
        Assert.Contains("singleSelectOptions", bashScript);
        Assert.Contains("dataType: ITERATION", bashScript);
        Assert.Contains("createProjectV2View", bashScript);
        Assert.Contains("updateProjectV2View", bashScript);
        Assert.Contains("gh api graphql --input -", bashScript);
        Assert.Contains("Target Milestone", bashScript);
        Assert.Contains("Backlog Type", bashScript);

        var psScript = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "setup-github-project.ps1"));
        Assert.Contains("project-v2-schema.json", psScript);
        Assert.Contains("Get-ItemClassification", psScript);
        Assert.Contains("area:cli", psScript);

        var readme = File.ReadAllText(Path.Combine(RepositoryRoot, "README.md"));
        Assert.Contains("project-sync.yml", readme);
        Assert.Contains("project-v2-schema.json", readme);
        Assert.Contains("PROJECT_TOKEN", readme);

        var backlog = File.ReadAllText(Path.Combine(RepositoryRoot, "BACKLOG.md"));
        Assert.Contains("Issue #50", backlog);
        Assert.Contains("SBA-111", backlog);

        var plan = File.ReadAllText(Path.Combine(RepositoryRoot, "PLAN.md"));
        Assert.Contains("Issue #50", plan);
        Assert.Contains("project-sync.yml", plan);

        var redactor = new SecretRedactor();
        redactor.RegisterEnvironmentSecrets(name => name == "PROJECT_TOKEN" ? "MyCustomProjectSecretToken9876" : null);
        var redacted = redactor.Redact("Sync failed with token MyCustomProjectSecretToken9876");
        Assert.DoesNotContain("MyCustomProjectSecretToken9876", redacted);
        Assert.Contains("****9876", redacted);
    }
}
