using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class WorkspacePathServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspacePathServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"sba-workspace-{Guid.NewGuid():N}");
    }

    [Fact]
    public void Resolve_OnWindowsDefault_ReturnsCWorkspaceSmartBacklogAssistantInAndOut()
    {
        var sut = new WorkspacePathService(environmentReader: _ => null);

        var dirs = sut.Resolve(customWorkspaceRoot: null, isWindowsOverride: true);

        Assert.Equal(@"c:\workspace", dirs.WorkspaceRoot);
        Assert.Equal(@"c:\workspace\smart-backlog-assistant", dirs.ProjectRoot);
        Assert.Equal(@"c:\workspace\smart-backlog-assistant\in", dirs.InputDirectory);
        Assert.Equal(@"c:\workspace\smart-backlog-assistant\out", dirs.OutputDirectory);
        Assert.True(dirs.IsWindows);
    }

    [Fact]
    public void Resolve_OnWslOrLinuxDefault_ReturnsMntCWorkspaceSmartBacklogAssistantInAndOut()
    {
        var sut = new WorkspacePathService(environmentReader: _ => null);

        var dirs = sut.Resolve(customWorkspaceRoot: null, isWindowsOverride: false);

        Assert.Equal("/mnt/c/workspace", dirs.WorkspaceRoot);
        Assert.Equal("/mnt/c/workspace/smart-backlog-assistant", dirs.ProjectRoot);
        Assert.Equal("/mnt/c/workspace/smart-backlog-assistant/in", dirs.InputDirectory);
        Assert.Equal("/mnt/c/workspace/smart-backlog-assistant/out", dirs.OutputDirectory);
        Assert.False(dirs.IsWindows);
    }

    [Theory]
    [InlineData("/mnt/c/workspace", true, @"c:\workspace")]
    [InlineData("/mnt/c/workspace/smart-backlog-assistant/in", true, @"c:\workspace\smart-backlog-assistant\in")]
    [InlineData(@"c:\workspace", false, "/mnt/c/workspace")]
    [InlineData(@"C:\workspace\smart-backlog-assistant\out", false, "/mnt/c/workspace/smart-backlog-assistant/out")]
    public void NormalizePathForOs_TranslatesBetweenWindowsAndWslPaths(
        string inputPath,
        bool isWindows,
        string expected)
    {
        var actual = WorkspacePathService.NormalizePathForOs(inputPath, isWindows);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EnsureDirectoriesExist_CreatesInAndOutDirectoriesAndSeedsSampleWhenEmpty()
    {
        Directory.CreateDirectory(_tempRoot);
        var sampleFile = Path.Combine(_tempRoot, "sample-note.txt");
        File.WriteAllText(sampleFile, "Title: Sample Seeded Note\nContext: Seeded into in folder.");

        var sut = new WorkspacePathService(environmentReader: _ => null);
        var dirs = sut.Resolve(customWorkspaceRoot: _tempRoot);

        sut.EnsureDirectoriesExist(dirs, seedSampleFilePath: sampleFile);

        Assert.True(Directory.Exists(dirs.InputDirectory));
        Assert.True(Directory.Exists(dirs.OutputDirectory));

        var files = sut.GetInputFiles(dirs.InputDirectory);
        var seeded = Assert.Single(files);
        Assert.Equal("sample-note.txt", Path.GetFileName(seeded));
    }

    [Theory]
    [InlineData("Add SSO Login Flow!", "add-sso-login-flow")]
    [InlineData("  Webhook Retry Policy (v2)  ", "webhook-retry-policy-v2")]
    [InlineData("", "backlog-item")]
    public void CreateSlug_NormalizesTitleIntoSafeFilenameSlug(string input, string expected)
    {
        Assert.Equal(expected, WorkspacePathService.CreateSlug(input));
    }

    [Fact]
    public void ApplySettings_WithCustomDirectories_OverridesInputAndOutputPaths()
    {
        var sut = new WorkspacePathService(environmentReader: _ => null);
        var baseDirs = sut.Resolve(customWorkspaceRoot: _tempRoot);
        var customIn = Path.Combine(_tempRoot, "custom-in");
        var customOut = Path.Combine(_tempRoot, "custom-out");
        var settings = new SmartBacklogAssistant.Domain.CliSettings(
            CustomInputDirectory: customIn,
            CustomOutputDirectory: customOut);

        var applied = sut.ApplySettings(baseDirs, settings);

        Assert.Equal(WorkspacePathService.NormalizePathForOs(customIn, baseDirs.IsWindows), applied.InputDirectory);
        Assert.Equal(WorkspacePathService.NormalizePathForOs(customOut, baseDirs.IsWindows), applied.OutputDirectory);
    }

    [Fact]
    public void ResolveInputTarget_ResolvesAllSingleFileAndMention_AndThrowsOnMissing()
    {
        var sut = new WorkspacePathService(environmentReader: _ => null);
        var dirs = sut.Resolve(customWorkspaceRoot: _tempRoot);
        Directory.CreateDirectory(dirs.InputDirectory);
        var notePath = Path.Combine(dirs.InputDirectory, "Sprint-Notes.md");
        File.WriteAllText(notePath, "Title: Sprint Notes");

        Assert.Equal(dirs.InputDirectory, sut.ResolveInputTarget("all", dirs));
        Assert.Equal(notePath, sut.ResolveInputTarget("sprint-notes.md", dirs));
        Assert.Equal(notePath, sut.ResolveInputTarget("@Sprint-Notes.md", dirs));
        Assert.Equal(notePath, sut.ResolveInputTarget("./in/Sprint-Notes.md", dirs));
        Assert.Null(sut.TryResolveContextFile("@does-not-exist.md", dirs));
        Assert.Throws<FileNotFoundException>(() => sut.ResolveInputTarget("missing-note.txt", dirs));
    }

    [Fact]
    public void FormatCompactLabel_ReturnsDefaultWhenUnchangedAndCompactSuffixWhenOverridden()
    {
        Assert.Equal("./in", WorkspacePathService.FormatCompactLabel("/mnt/c/workspace/smart-backlog-assistant/in", "/mnt/c/workspace/smart-backlog-assistant/in", "./in"));
        Assert.Equal(".../project/custom-in", WorkspacePathService.FormatCompactLabel("/home/user/project/custom-in", "/mnt/c/workspace/smart-backlog-assistant/in", "./in"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
