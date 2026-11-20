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

    [Fact]
    public void SaveAndLoad_PersistsLogLevelAndLogFormatAsCamelCaseStringsAndSupportsLegacyJson()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var workspace = workspaceService.Resolve(_tempRoot);
        var sut = new CliSettingsService();

        var savedPath = sut.Save(
            workspace,
            new CliSettings(
                JsonFileOut: false,
                Theme: CliThemeStyle.Kubectl,
                LogLevel: Microsoft.Extensions.Logging.LogLevel.Debug,
                LogFormat: LogOutputFormat.Json));

        var rawJson = File.ReadAllText(savedPath);
        Assert.Contains("\"logLevel\": \"debug\"", rawJson);
        Assert.Contains("\"logFormat\": \"json\"", rawJson);

        var loaded = sut.Load(workspace);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, loaded.LogLevel);
        Assert.Equal(LogOutputFormat.Json, loaded.LogFormat);

        // Legacy settings.json without logLevel/logFormat continues to deserialize cleanly
        File.WriteAllText(savedPath, """{"jsonFileOut":true,"theme":"uv"}""");
        var legacy = sut.Load(workspace);
        Assert.True(legacy.JsonFileOut);
        Assert.Equal(CliThemeStyle.Uv, legacy.Theme);
        Assert.Null(legacy.LogLevel);
        Assert.Null(legacy.LogFormat);
        Assert.Null(legacy.OpenAiBaseUrl);
        Assert.Null(legacy.OpenAiWireApi);
    }

    [Fact]
    public void SaveAndLoad_PersistsOpenAiGatewaySettingsWithoutSecrets()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var workspace = workspaceService.Resolve(_tempRoot);
        var sut = new CliSettingsService();

        var savedPath = sut.Save(
            workspace,
            new CliSettings(
                PreferredProvider: AiProviderType.OpenAiGateway,
                ModelName: "gpt-4.1-mini",
                OpenAiBaseUrl: "https://gateway.internal/v1",
                OpenAiWireApi: "responses"));

        var rawJson = File.ReadAllText(savedPath);
        Assert.Contains("\"preferredProvider\": \"openAiGateway\"", rawJson);
        Assert.Contains("\"openAiBaseUrl\": \"https://gateway.internal/v1\"", rawJson);
        Assert.Contains("\"openAiWireApi\": \"responses\"", rawJson);
        Assert.DoesNotContain("apiKey", rawJson, StringComparison.OrdinalIgnoreCase);

        var loaded = sut.Load(workspace);
        Assert.Equal(AiProviderType.OpenAiGateway, loaded.PreferredProvider);
        Assert.Equal("gpt-4.1-mini", loaded.ModelName);
        Assert.Equal("https://gateway.internal/v1", loaded.OpenAiBaseUrl);
        Assert.Equal("responses", loaded.OpenAiWireApi);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}

