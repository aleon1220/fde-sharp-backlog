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

    [Fact]
    public void ReadMaskedLine_WhenNonInteractiveOrRedirected_FallsBackToReadLine()
    {
        using var input = new StringReader("secret-token-1234\n");
        using var output = new StringWriter();

        var result = GeminiSecretProvider.ReadMaskedLine(input, output, isInteractive: () => false);

        Assert.Equal("secret-token-1234", result);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void ReadMaskedLine_WhenInteractive_InterceptsKeystrokesAndMasksWithAsterisks()
    {
        var keys = new Queue<ConsoleKeyInfo>(
        [
            new('t', ConsoleKey.T, false, false, false),
            new('e', ConsoleKey.E, false, false, false),
            new('s', ConsoleKey.S, false, false, false),
            new('t', ConsoleKey.T, false, false, false),
            new('1', ConsoleKey.D1, false, false, false),
            new('\r', ConsoleKey.Enter, false, false, false)
        ]);

        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();

        var result = GeminiSecretProvider.ReadMaskedLine(
            input,
            output,
            keyReader: () => keys.Dequeue(),
            isInteractive: () => true);

        Assert.Equal("test1", result);
        var consoleOutput = output.ToString();
        Assert.StartsWith("*****" + Environment.NewLine, consoleOutput);
        Assert.DoesNotContain("test1", consoleOutput);
    }

    [Fact]
    public void ReadMaskedLine_WhenInteractive_HandlesBackspaceAndEscapeEditing()
    {
        var keys = new Queue<ConsoleKeyInfo>(
        [
            new('a', ConsoleKey.A, false, false, false),
            new('b', ConsoleKey.B, false, false, false),
            new('\b', ConsoleKey.Backspace, false, false, false), // removes 'b'
            new('c', ConsoleKey.C, false, false, false),          // now 'ac'
            new('\u001b', ConsoleKey.Escape, false, false, false), // clears buffer
            new('k', ConsoleKey.K, false, false, false),
            new('e', ConsoleKey.E, false, false, false),
            new('y', ConsoleKey.Y, false, false, false),
            new('\r', ConsoleKey.Enter, false, false, false)
        ]);

        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();

        var result = GeminiSecretProvider.ReadMaskedLine(
            input,
            output,
            keyReader: () => keys.Dequeue(),
            isInteractive: () => true);

        Assert.Equal("key", result);
        var consoleOutput = output.ToString();
        Assert.Contains("\b \b", consoleOutput);
        Assert.DoesNotContain("key", consoleOutput);
        Assert.EndsWith(Environment.NewLine, consoleOutput);
    }

    [Fact]
    public void ReadMaskedLine_WhenInteractive_IgnoresControlKeys()
    {
        var keys = new Queue<ConsoleKeyInfo>(
        [
            new('\0', ConsoleKey.UpArrow, false, false, false),
            new('\0', ConsoleKey.F1, false, false, false),
            new('\t', ConsoleKey.Tab, false, false, false),
            new('p', ConsoleKey.P, false, false, false),
            new('a', ConsoleKey.A, false, false, false),
            new('s', ConsoleKey.S, false, false, false),
            new('s', ConsoleKey.S, false, false, false),
            new('\n', ConsoleKey.Enter, false, false, false)
        ]);

        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();

        var result = GeminiSecretProvider.ReadMaskedLine(
            input,
            output,
            keyReader: () => keys.Dequeue(),
            isInteractive: () => true);

        Assert.Equal("pass", result);
        var consoleOutput = output.ToString();
        Assert.Equal("****" + Environment.NewLine, consoleOutput);
    }

    [Fact]
    public void ResolveOrPrompt_WhenInteractiveTty_MasksKeystrokesAndPrintsOnlyMaskedConfirmation()
    {
        var injected = new Dictionary<string, string>();
        var secret = "AIzaSySecretInteractiveKey9999";
        var keyQueue = new Queue<ConsoleKeyInfo>();
        foreach (var ch in secret)
        {
            keyQueue.Enqueue(new ConsoleKeyInfo(ch, ConsoleKey.NoName, false, false, false));
        }
        keyQueue.Enqueue(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        var sut = new GeminiSecretProvider(
            environmentReader: _ => null,
            environmentWriter: (k, v) => injected[k] = v,
            keyReader: () => keyQueue.Dequeue(),
            isInteractive: () => true);

        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();

        var result = sut.ResolveOrPrompt(
            cliApiKey: null,
            input: input,
            output: output,
            nonInteractive: false);

        Assert.True(result.IsConfigured);
        Assert.Equal(secret, result.ApiKey);
        Assert.Equal("****9999", result.MaskedKey);
        Assert.Equal(SecretSource.InteractiveCliPrompt, result.Source);
        Assert.Equal(secret, injected[GeminiSecretProvider.PrimaryEnvVar]);

        var consoleText = output.ToString();
        Assert.Contains(new string('*', secret.Length), consoleText);
        Assert.Contains("****9999", consoleText);
        Assert.DoesNotContain(secret, consoleText);
    }
}
