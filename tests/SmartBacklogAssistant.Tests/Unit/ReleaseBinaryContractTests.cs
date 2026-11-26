using System.Xml.Linq;
using SmartBacklogAssistant.Services;
using SmartBacklogAssistant.Workflow;

namespace SmartBacklogAssistant.Tests.Unit;

/// <summary>
/// Verifies release binary packaging, green-gated GitHub Actions workflow contracts,
/// bundled binary prompt diagnostics, and README/BACKLOG/PLAN documentation parity for Issue #59.
/// </summary>
public sealed class ReleaseBinaryContractTests
{
    private static readonly string RepositoryRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void Csproj_EmbedsBuiltInPromptsAndEnablesSelfExtractNativeLibraries()
    {
        var csprojPath = Path.Combine(RepositoryRoot, "src", "SmartBacklogAssistant", "SmartBacklogAssistant.csproj");
        var doc = XDocument.Load(csprojPath);

        var includeNative = doc.Descendants("IncludeNativeLibrariesForSelfExtract").FirstOrDefault();
        Assert.NotNull(includeNative);
        Assert.Equal("true", includeNative.Value.Trim(), ignoreCase: true);

        var embedded = doc.Descendants("EmbeddedResource")
            .FirstOrDefault(e => ((string?)e.Attribute("Include") ?? string.Empty).Contains("prompts", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(embedded);
    }

    [Fact]
    public void ReleaseWorkflow_AndPackagingScripts_EnforceGreenGateChecksumsAndStandaloneSmokeChecks()
    {
        var workflowPath = Path.Combine(RepositoryRoot, ".github", "workflows", "release-binaries.yml");
        Assert.True(File.Exists(workflowPath), $"Expected release workflow at '{workflowPath}'.");
        var workflow = File.ReadAllText(workflowPath);

        Assert.Contains("v[0-9]+.[0-9]+.[0-9]+*", workflow);
        Assert.Contains("workflow_dispatch:", workflow);
        Assert.Contains("needs: verify", workflow);
        Assert.Contains("needs: [verify, package]", workflow);
        Assert.Contains("linux-x64", workflow);
        Assert.Contains("win-x64", workflow);
        Assert.Contains("osx-arm64", workflow);
        Assert.Contains("SHA256SUMS.txt", workflow);
        Assert.Contains("actions/attest-build-provenance@v2", workflow);
        Assert.Contains("gh release create", workflow);

        var bashPackager = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "package-release.sh"));
        Assert.Contains("PublishSingleFile=true", bashPackager);
        Assert.Contains("--self-contained true", bashPackager);
        Assert.Contains("--no-restore", bashPackager);
        Assert.Contains(".sha256", bashPackager);
        Assert.Contains("(bundled in binary)", bashPackager);

        var psPackager = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "package-release.ps1"));
        Assert.Contains("PublishSingleFile=true", psPackager);
        Assert.Contains("--self-contained true", psPackager);
        Assert.Contains("--no-restore", psPackager);
        Assert.Contains("Get-FileHash", psPackager);
        Assert.Contains("(bundled in binary)", psPackager);
    }

    [Fact]
    public void Readme_Backlog_AndPlan_DocumentTestedGitHubBinaryInstallationAndReleaseGate()
    {
        var rootReadme = File.ReadAllText(Path.Combine(RepositoryRoot, "README.md"));
        Assert.Contains("docs/getting-started/install-binary.md", rootReadme);

        var installDoc = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "getting-started", "install-binary.md"));
        Assert.Contains("Install & Run from Tested GitHub Binary", installDoc);
        Assert.Contains("gh release list", installDoc);
        Assert.Contains("gh release download", installDoc);
        Assert.Contains("SHA256SUMS.txt", installDoc);
        Assert.Contains("gh attestation verify", installDoc);
        Assert.Contains("release-binaries.yml", installDoc);

        var backlog = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "project", "BACKLOG.md"));
        Assert.Contains("Issue #59", backlog);
        Assert.Contains("SBA-110", backlog);
        Assert.Contains("SHA256SUMS.txt", backlog);

        var plan = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "project", "PLAN.md"));
        Assert.Contains("Issue #59", plan);
        Assert.Contains("release-binaries.yml", plan);
    }

    [Fact]
    public void WorkspaceDiagnosticService_WhenPromptsAbsentOnDisk_ReportsBundledInBinary()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"sba-diag-bundled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        try
        {
            var isolatedPromptService = new PromptTemplateService(
                fileExists: _ => false,
                fileReader: _ => string.Empty,
                currentDirectoryProvider: () => tempWorkspace,
                baseDirectoryProvider: () => tempWorkspace);

            var diagnosticService = new WorkspaceDiagnosticService(
                workspaceService: new WorkspacePathService(_ => null),
                environmentReader: _ => null,
                promptService: isolatedPromptService);

            using var output = new StringWriter();
            var exitCode = diagnosticService.RenderStatusTable(
                new WorkflowOptions(
                    InputFilePath: tempWorkspace,
                    PromptFilePath: PromptTemplateService.DefaultPromptFilePath,
                    WorkspaceRoot: tempWorkspace),
                output);

            Assert.Equal(0, exitCode);
            Assert.Contains("prompts/as-is-v1-prompt.md (bundled in binary)", output.ToString());
        }
        finally
        {
            Directory.Delete(tempWorkspace, recursive: true);
        }
    }
}
