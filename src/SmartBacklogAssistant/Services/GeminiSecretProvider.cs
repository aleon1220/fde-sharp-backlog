using System.Text;
using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Resolves, masks, and injects the Google Gemini API secret at runtime from CLI flags,
/// OS environment variables (GEMINI_API_KEY, GOOGLE_API_KEY, AI_API_KEY), or an interactive CLI prompt.
/// </summary>
public sealed class GeminiSecretProvider
{
    public const string GeminiApiKeysUrl = "https://aistudio.google.com/api-keys";
    public const string PrimaryEnvVar = "GEMINI_API_KEY";
    public const string GoogleEnvVar = "GOOGLE_API_KEY";
    public const string FallbackEnvVar = "AI_API_KEY";

    private static readonly string[] SupportedEnvVars =
    [
        PrimaryEnvVar,
        GoogleEnvVar,
        FallbackEnvVar
    ];

    private readonly Func<string, string?> _environmentReader;
    private readonly Action<string, string> _environmentWriter;
    private readonly Func<ConsoleKeyInfo>? _keyReader;
    private readonly Func<bool>? _isInteractive;

    public GeminiSecretProvider(
        Func<string, string?>? environmentReader = null,
        Action<string, string>? environmentWriter = null,
        Func<ConsoleKeyInfo>? keyReader = null,
        Func<bool>? isInteractive = null)
    {
        _environmentReader = environmentReader ?? DefaultEnvironmentReader;
        _environmentWriter = environmentWriter ?? DefaultEnvironmentWriter;
        _keyReader = keyReader;
        _isInteractive = isInteractive;
    }

    /// <summary>
    /// Resolves the Gemini API key from CLI argument first, then OS environment variables.
    /// </summary>
    public GeminiSecretResolution Resolve(string? cliApiKey = null)
    {
        if (!string.IsNullOrWhiteSpace(cliApiKey))
        {
            var trimmedCliKey = cliApiKey.Trim();
            _environmentWriter(PrimaryEnvVar, trimmedCliKey);

            return new GeminiSecretResolution(
                ApiKey: trimmedCliKey,
                MaskedKey: MaskKey(trimmedCliKey),
                Source: SecretSource.CliArgument,
                SourceDetail: "CLI Argument (--api-key)",
                KeyPortalUrl: GeminiApiKeysUrl);
        }

        foreach (var envVar in SupportedEnvVars)
        {
            var value = _environmentReader(envVar);
            if (!string.IsNullOrWhiteSpace(value))
            {
                var trimmedEnvKey = value.Trim();
                _environmentWriter(PrimaryEnvVar, trimmedEnvKey);

                return new GeminiSecretResolution(
                    ApiKey: trimmedEnvKey,
                    MaskedKey: MaskKey(trimmedEnvKey),
                    Source: SecretSource.EnvironmentVariable,
                    SourceDetail: $"OS Environment Variable ({envVar})",
                    KeyPortalUrl: GeminiApiKeysUrl);
            }
        }

        return new GeminiSecretResolution(
            ApiKey: null,
            MaskedKey: "none",
            Source: SecretSource.None,
            SourceDetail: "Not registered in OS or CLI",
            KeyPortalUrl: GeminiApiKeysUrl);
    }

    /// <summary>
    /// Ensures a Gemini API key is available at runtime. If not registered via CLI or OS environment variable,
    /// prompts the user in the CLI before proceeding.
    /// </summary>
    public GeminiSecretResolution ResolveOrPrompt(
        string? cliApiKey,
        TextReader input,
        TextWriter output,
        bool nonInteractive = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var existing = Resolve(cliApiKey);
        if (existing.IsConfigured)
        {
            return existing;
        }

        output.WriteLine();
        output.WriteLine("[2/5 Local -> Gemini Secret Injection]");
        output.WriteLine($"  No Gemini API key detected in OS environment ({PrimaryEnvVar} / {FallbackEnvVar}) or CLI arguments.");
        output.WriteLine($"  Obtain your Gemini API key from Google AI Studio: {GeminiApiKeysUrl}");
        output.WriteLine($"  Tip: You can register it in your OS environment as {PrimaryEnvVar}=\"<your-key>\".");

        if (nonInteractive)
        {
            throw new InvalidOperationException(
                $"Gemini API key is required before proceeding. Set the {PrimaryEnvVar} environment variable in your OS, " +
                $"pass --api-key <key>, or run interactively to enter a key obtained from {GeminiApiKeysUrl}.");
        }

        while (true)
        {
            output.Write($"Enter your Gemini API key to proceed (from {GeminiApiKeysUrl}): ");
            var line = ReadMaskedInput(input, output);

            if (line is null)
            {
                throw new InvalidOperationException(
                    $"A Gemini API key is required before proceeding. Obtain one from {GeminiApiKeysUrl} " +
                    $"or set {PrimaryEnvVar} in your OS environment.");
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                var trimmedKey = line.Trim();
                _environmentWriter(PrimaryEnvVar, trimmedKey);

                var resolved = new GeminiSecretResolution(
                    ApiKey: trimmedKey,
                    MaskedKey: MaskKey(trimmedKey),
                    Source: SecretSource.InteractiveCliPrompt,
                    SourceDetail: "Interactive CLI Prompt (injected at runtime)",
                    KeyPortalUrl: GeminiApiKeysUrl);

                output.WriteLine($"  [Gemini Secret Injected] Key registered for runtime session ({resolved.MaskedKey}).");
                return resolved;
            }

            output.WriteLine($"  [!] Gemini API key cannot be empty. Please obtain a key from {GeminiApiKeysUrl} and enter it below.");
        }
    }

    /// <summary>
    /// Reads secret input from the user. When running interactively on a console (TTY),
    /// keystrokes are intercepted with <see cref="Console.ReadKey(bool)"/> and masked with '*',
    /// supporting Backspace and Escape editing. When redirected or non-interactive, falls back to <see cref="TextReader.ReadLine"/>.
    /// </summary>
    public string? ReadMaskedInput(TextReader input, TextWriter output) =>
        ReadMaskedLine(input, output, _keyReader, _isInteractive);

    /// <summary>
    /// Reads secret input from the user with masked keystrokes ('*').
    /// Intercepts TTY keystrokes via <see cref="Console.ReadKey(bool)"/> when interactive,
    /// with Backspace and Escape editing. Seamlessly falls back to <see cref="TextReader.ReadLine"/>
    /// when standard input is redirected or in test/non-TTY execution.
    /// </summary>
    public static string? ReadMaskedLine(
        TextReader input,
        TextWriter output,
        Func<ConsoleKeyInfo>? keyReader = null,
        Func<bool>? isInteractive = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var canUseKeyboard = isInteractive?.Invoke() ??
                             (ReferenceEquals(input, Console.In) &&
                              !Console.IsInputRedirected &&
                              !Console.IsOutputRedirected);

        if (!canUseKeyboard)
        {
            return input.ReadLine();
        }

        try
        {
            var buffer = new StringBuilder();
            var readKeyFunc = keyReader ?? (() => Console.ReadKey(intercept: true));

            while (true)
            {
                var keyInfo = readKeyFunc();
                if (keyInfo.Key == ConsoleKey.Enter || keyInfo.KeyChar is '\r' or '\n')
                {
                    output.WriteLine();
                    output.Flush();
                    return buffer.ToString();
                }

                if (keyInfo.Key == ConsoleKey.Backspace || keyInfo.KeyChar == '\b')
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        output.Write("\b \b");
                        output.Flush();
                    }
                    continue;
                }

                if (keyInfo.Key == ConsoleKey.Escape)
                {
                    while (buffer.Length > 0)
                    {
                        buffer.Length--;
                        output.Write("\b \b");
                    }
                    output.Flush();
                    continue;
                }

                if (!char.IsControl(keyInfo.KeyChar))
                {
                    buffer.Append(keyInfo.KeyChar);
                    output.Write('*');
                    output.Flush();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return input.ReadLine();
        }
    }

    public static string MaskKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "none";
        }

        var trimmed = key.Trim();
        if (trimmed.Length <= 4)
        {
            return "****";
        }

        return $"****{trimmed[^4..]}";
    }

    private static string? DefaultEnvironmentReader(string variableName)
    {
        var processValue = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Process);
        if (!string.IsNullOrWhiteSpace(processValue))
        {
            return processValue;
        }

        if (OperatingSystem.IsWindows())
        {
            var userValue = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(userValue))
            {
                return userValue;
            }

            var machineValue = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(machineValue))
            {
                return machineValue;
            }
        }

        return null;
    }

    private static void DefaultEnvironmentWriter(string variableName, string value)
    {
        Environment.SetEnvironmentVariable(variableName, value, EnvironmentVariableTarget.Process);
    }
}
