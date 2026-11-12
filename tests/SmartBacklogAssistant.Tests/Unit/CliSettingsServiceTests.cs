using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class CliSettingsServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public CliSettingsServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"sba-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void Load_WhenSettingsFileDoesNotExist_ReturnsDefaultWithJsonFileOutFalse()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var workspace = workspaceService.Resolve(_tempRoot);
        var sut = new CliSettingsService();

        var settings = sut.Load(workspace);

        Assert.False(settings.JsonFileOut);
        Assert.Equal(CliThemeStyle.Agy, settings.Theme);
    }

    [Fact]
    public void SaveAndLoad_PersistsSettingsJsonToWorkspaceProjectRoot()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var workspace = workspaceService.Resolve(_tempRoot);
        var sut = new CliSettingsService();

        var savedPath = sut.Save(workspace, new CliSettings(JsonFileOut: true, Theme: CliThemeStyle.Uv));

        Assert.True(File.Exists(savedPath));
        Assert.EndsWith("settings.json", savedPath, StringComparison.OrdinalIgnoreCase);

        var loaded = sut.Load(workspace);
        Assert.True(loaded.JsonFileOut);
        Assert.Equal(CliThemeStyle.Uv, loaded.Theme);
    }

    [Fact]
    public void LoadFromPath_WhenFileContainsMalformedJson_ReturnsSafeDefaults()
    {
        var malformedPath = Path.Combine(_tempRoot, "settings.json");
        File.WriteAllText(malformedPath, "{ invalid-json ");
        var sut = new CliSettingsService();

        var settings = sut.LoadFromPath(malformedPath);

        Assert.False(settings.JsonFileOut);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}

