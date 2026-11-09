using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class RuntimeEnvironmentServiceTests
{
    private readonly RuntimeEnvironmentService _sut = new();

    [Fact]
    public void Inspect_DefaultsToGoogleGeminiAndCapturesOsAndBuildMetadata()
    {
        var info = _sut.Inspect();

        Assert.NotEmpty(info.OsPlatform);
        Assert.NotEmpty(info.OsDescription);
        Assert.NotEmpty(info.OsArchitecture);
        Assert.NotEmpty(info.ProcessArchitecture);
        Assert.Contains(".NET", info.FrameworkDescription);
        Assert.NotEmpty(info.BuildVersion);
        Assert.NotEmpty(info.BuildConfiguration);
        Assert.Equal(AiProviderType.GoogleGemini, info.EnabledProvider);
        Assert.Contains("Google Gemini", info.EnabledAiModel);
        Assert.Contains(RuntimeEnvironmentService.DefaultGeminiModel, info.EnabledAiModel);
        Assert.Equal("https://aistudio.google.com/api-keys", info.KeyPortalUrl);
    }

    [Fact]
    public void FormatRuntimeBanner_IncludesOsRunningBuildInfoEnabledAiModelAndMaskedKey()
    {
        var info = _sut.Inspect(AiProviderType.GoogleGemini, "gemini-2.5-flash");
        var secret = new GeminiSecretResolution(
            ApiKey: "AIzaSySecretKey7788",
            MaskedKey: "****7788",
            Source: SecretSource.EnvironmentVariable,
            SourceDetail: "OS Environment Variable (GEMINI_API_KEY)",
            KeyPortalUrl: GeminiSecretProvider.GeminiApiKeysUrl);

        var banner = _sut.FormatRuntimeBanner(info, secret);

        Assert.Contains("OS Running", banner);
        Assert.Contains(info.OsPlatform, banner);
        Assert.Contains("Build Info", banner);
        Assert.Contains(info.BuildVersion, banner);
        Assert.Contains(info.BuildConfiguration, banner);
        Assert.Contains(info.FrameworkDescription, banner);
        Assert.Contains("AI Model Enabled : Google Gemini (gemini-2.5-flash)", banner);
        Assert.Contains("OS Environment Variable (GEMINI_API_KEY) [****7788]", banner);
        Assert.Contains("https://aistudio.google.com/api-keys", banner);
        Assert.DoesNotContain("AIzaSySecretKey7788", banner);
    }
}
