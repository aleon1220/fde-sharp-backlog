using System.Text.Json;
using System.Xml.Linq;

namespace SmartBacklogAssistant.Tests.Unit;

/// <summary>
/// Verifies repository-level NuGet restore hardening deliverables for Milestone #1 / Issue #57:
/// isolated nuget.config, lockfile determinism, caching proxy &amp; fallback feed MSBuild wiring,
/// container locked restore, pre-production restore scripts, CI workflow, and Markdown docs parity.
/// </summary>
public sealed class NuGetHardeningContractTests
{
    private static readonly string RepositoryRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void NuGetConfig_IsolatesPackageSourcesAndPinsOfficialNuGetV3Feed()
    {
        var nugetConfigPath = Path.Combine(RepositoryRoot, "nuget.config");
        Assert.True(File.Exists(nugetConfigPath), $"Expected nuget.config at '{nugetConfigPath}'.");

        var doc = XDocument.Load(nugetConfigPath);
        var root = doc.Root;
        Assert.NotNull(root);
        Assert.Equal("configuration", root.Name.LocalName);

        var packageSources = root.Element("packageSources");
        Assert.NotNull(packageSources);
        Assert.Contains(packageSources.Elements(), e => e.Name.LocalName == "clear");

        var nugetOrg = packageSources.Elements("add")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("key"), "nuget.org", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(nugetOrg);
        Assert.Equal("https://api.nuget.org/v3/index.json", (string?)nugetOrg.Attribute("value"));
        Assert.Equal("3", (string?)nugetOrg.Attribute("protocolVersion"));

        var disabledSources = root.Element("disabledPackageSources");
        Assert.NotNull(disabledSources);
        Assert.Contains(disabledSources.Elements(), e => e.Name.LocalName == "clear");
    }

    [Fact]
    public void DirectoryBuildProps_EnablesLockfilesAndConditionalProxyAndFallbackFeeds()
    {
        var propsPath = Path.Combine(RepositoryRoot, "Directory.Build.props");
        Assert.True(File.Exists(propsPath), $"Expected Directory.Build.props at '{propsPath}'.");

        var doc = XDocument.Load(propsPath);
        var content = File.ReadAllText(propsPath);

        var restoreWithLockFile = doc.Descendants("RestorePackagesWithLockFile").FirstOrDefault();
        Assert.NotNull(restoreWithLockFile);
        Assert.Equal("true", restoreWithLockFile.Value.Trim(), ignoreCase: true);

        var lockedMode = doc.Descendants("RestoreLockedMode").FirstOrDefault();
        Assert.NotNull(lockedMode);
        Assert.Contains("ContinuousIntegrationBuild", (string?)lockedMode.Attribute("Condition") ?? string.Empty);

        Assert.Contains("NUGET_PROXY_FEED_URL", content);
        Assert.Contains("NUGET_PROXY_ONLY", content);
        Assert.Contains("NUGET_FALLBACK_FEED_DIR", content);
        Assert.Contains("NUGET_FALLBACK_PACKAGES_DIR", content);
        Assert.Contains(".nuget/fallback", content);
    }

    [Theory]
    [InlineData("src/SmartBacklogAssistant/packages.lock.json", "Microsoft.Copilot.SDK")]
    [InlineData("tests/SmartBacklogAssistant.Tests/packages.lock.json", "xunit")]
    public void PackagesLockJson_ExistsAndContainsDeterministicNet10Dependencies(string relativePath, string expectedPackage)
    {
        var lockPath = Path.Combine(RepositoryRoot, relativePath);
        Assert.True(File.Exists(lockPath), $"Expected lockfile at '{lockPath}'.");

        using var json = JsonDocument.Parse(File.ReadAllText(lockPath));
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());

        var net10 = json.RootElement.GetProperty("dependencies").GetProperty("net10.0");
        Assert.True(net10.TryGetProperty(expectedPackage, out var pkg), $"Expected '{expectedPackage}' inside '{relativePath}'.");
        Assert.False(string.IsNullOrWhiteSpace(pkg.GetProperty("contentHash").GetString()));
    }

    [Fact]
    public void Containerfile_Scripts_Workflow_AndDocs_EnforceLockedRestoreRetryAndTokenScopedAuth()
    {
        var containerfile = File.ReadAllText(Path.Combine(RepositoryRoot, "Containerfile"));
        Assert.Contains("Directory.Build.props", containerfile);
        Assert.Contains("packages.lock.json", containerfile);
        Assert.Contains("NUGET_ENABLE_ENHANCED_HTTP_RETRY=true", containerfile);
        Assert.Contains("--locked-mode", containerfile);
        Assert.Contains("--no-restore", containerfile);

        var bashScript = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "restore-nuget.sh"));
        Assert.Contains("NUGET_ENABLE_ENHANCED_HTTP_RETRY", bashScript);
        Assert.Contains("NUGET_AUTH_TOKEN", bashScript);
        Assert.Contains("NuGetPackageSourceCredentials_upstream_proxy", bashScript);
        Assert.Contains("--locked-mode", bashScript);

        var psScript = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "restore-nuget.ps1"));
        Assert.Contains("NUGET_ENABLE_ENHANCED_HTTP_RETRY", psScript);
        Assert.Contains("NUGET_AUTH_TOKEN", psScript);
        Assert.Contains("NuGetPackageSourceCredentials_upstream_proxy", psScript);
        Assert.Contains("--locked-mode", psScript);

        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "preprod-nuget-uat.yml"));
        Assert.Contains("NUGET_ENABLE_ENHANCED_HTTP_RETRY: \"true\"", workflow);
        Assert.Contains("NUGET_AUTH_TOKEN: ${{ secrets.GITHUB_TOKEN }}", workflow);
        Assert.Contains("id-token: write", workflow);
        Assert.Contains("--locked-mode", workflow);

        var readme = File.ReadAllText(Path.Combine(RepositoryRoot, "README.md"));
        Assert.Contains("packages.lock.json", readme);
        Assert.Contains("NUGET_PROXY_FEED_URL", readme);
        Assert.Contains("NUGET_AUTH_TOKEN", readme);
        Assert.Contains("NUGET_ENABLE_ENHANCED_HTTP_RETRY", readme);
        Assert.Contains("scripts/restore-nuget.sh", readme);

        var backlog = File.ReadAllText(Path.Combine(RepositoryRoot, "BACKLOG.md"));
        Assert.Contains("SBA-109", backlog);
        Assert.Contains("packages.lock.json", backlog);
        Assert.Contains("Directory.Build.props", backlog);
    }
}
