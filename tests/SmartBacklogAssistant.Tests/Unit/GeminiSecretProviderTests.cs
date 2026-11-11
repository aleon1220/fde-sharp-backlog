using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class GeminiSecretProviderTests
{
    [Fact]
    public void Resolve_WithCliApiKey_ReturnsCliArgumentSourceAndMasksKey()
    {
        var injectedVars = new Dictionary<string, string>();
        var sut = new GeminiSecretProvider(
            environmentReader: _ => null,
            environmentWriter: (k, v) => injectedVars[k] = v);

        var result = sut.Resolve("AIzaSyDemoKey9876");

        Assert.True(result.IsConfigured);
        Assert.Equal("AIzaSyDemoKey9876", result.ApiKey);
        Assert.Equal("****9876", result.MaskedKey);
        Assert.Equal(SecretSource.CliArgument, result.Source);
        Assert.Contains("--api-key", result.SourceDetail);
        Assert.Equal(GeminiSecretProvider.GeminiApiKeysUrl, result.KeyPortalUrl);
        Assert.Equal("AIzaSyDemoKey9876", injectedVars[GeminiSecretProvider.PrimaryEnvVar]);
    }

    [Theory]
    [InlineData("GEMINI_API_KEY", "AIzaSyGeminiEnvKey1111", "****1111")]
    [InlineData("GOOGLE_API_KEY", "AIzaSyGoogleEnvKey2222", "****2222")]
    [InlineData("AI_API_KEY", "AIzaSyFallbackKey3333", "****3333")]
    public void Resolve_WithOsEnvironmentVariable_ResolvesAndInjectsSecret(
        string envVarName,
        string envVarValue,
        string expectedMask)
    {
        var env = new Dictionary<string, string?> { [envVarName] = envVarValue };
        var injected = new Dictionary<string, string>();
        var sut = new GeminiSecretProvider(
            environmentReader: key => env.GetValueOrDefault(key),
            environmentWriter: (k, v) => injected[k] = v);

        var result = sut.Resolve(cliApiKey: null);

        Assert.True(result.IsConfigured);
        Assert.Equal(envVarValue, result.ApiKey);
        Assert.Equal(expectedMask, result.MaskedKey);
        Assert.Equal(SecretSource.EnvironmentVariable, result.Source);
        Assert.Contains(envVarName, result.SourceDetail);
        Assert.Equal(envVarValue, injected[GeminiSecretProvider.PrimaryEnvVar]);
    }

    [Fact]
    public void Resolve_WhenNoKeyRegistered_ReturnsUnconfiguredResolution()
    {
        var sut = new GeminiSecretProvider(
            environmentReader: _ => null,
            environmentWriter: (_, _) => { });

        var result = sut.Resolve(cliApiKey: null);

        Assert.False(result.IsConfigured);
        Assert.Null(result.ApiKey);
        Assert.Equal("none", result.MaskedKey);
        Assert.Equal(SecretSource.None, result.Source);
        Assert.Equal("https://aistudio.google.com/api-keys", result.KeyPortalUrl);
    }

    [Fact]
    public void ResolveOrPrompt_WhenKeyMissingInInteractiveMode_PromptsUserInCliAndRepromptsOnEmptyInput()
    {
        var injected = new Dictionary<string, string>();
        var sut = new GeminiSecretProvider(
            environmentReader: _ => null,
            environmentWriter: (k, v) => injected[k] = v);

        // First line is empty (user presses Enter without a key), second line provides the Gemini key
        using var input = new StringReader("\nAIzaSyInteractiveKey4321\n");
        using var output = new StringWriter();

        var result = sut.ResolveOrPrompt(
            cliApiKey: null,
            input: input,
            output: output,
            nonInteractive: false);

        Assert.True(result.IsConfigured);
        Assert.Equal("AIzaSyInteractiveKey4321", result.ApiKey);
        Assert.Equal("****4321", result.MaskedKey);
        Assert.Equal(SecretSource.InteractiveCliPrompt, result.Source);
        Assert.Equal("AIzaSyInteractiveKey4321", injected[GeminiSecretProvider.PrimaryEnvVar]);

        var consoleText = output.ToString();
        Assert.Contains("https://aistudio.google.com/api-keys", consoleText);
        Assert.Contains("Gemini API key cannot be empty", consoleText);
        Assert.Contains("****4321", consoleText);
        Assert.DoesNotContain("AIzaSyInteractiveKey4321", consoleText);
    }

    [Fact]
    public void ResolveOrPrompt_WhenKeyMissingInNonInteractiveMode_ThrowsInvalidOperationExceptionWithPortalUrl()
    {
        var sut = new GeminiSecretProvider(
            environmentReader: _ => null,
            environmentWriter: (_, _) => { });

        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            sut.ResolveOrPrompt(
                cliApiKey: null,
                input: input,
                output: output,
                nonInteractive: true));

        Assert.Contains("https://aistudio.google.com/api-keys", ex.Message);
        Assert.Contains("GEMINI_API_KEY", ex.Message);
    }
}
