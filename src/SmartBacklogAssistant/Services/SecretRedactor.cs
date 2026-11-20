using System.Text.RegularExpressions;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Centralized, thread-safe secret redaction engine that masks API keys, bearer tokens,
/// provider credentials, and sensitive HTTP headers via <see cref="GeminiSecretProvider.MaskKey"/>
/// before any diagnostic or exception text is written to <c>stderr</c>.
/// </summary>
public sealed partial class SecretRedactor
{
    private static readonly string[] SecretEnvVars =
    [
        GeminiSecretProvider.PrimaryEnvVar,
        GeminiSecretProvider.GoogleEnvVar,
        GeminiSecretProvider.FallbackEnvVar,
        GitHubCopilotSdkGateway.CopilotTokenEnvVar,
        GitHubCopilotSdkGateway.GitHubTokenEnvVar,
        GitHubCopilotSdkGateway.GhTokenEnvVar,
        "AZURE_OPENAI_API_KEY",
        "ANTHROPIC_API_KEY",
        "COPILOT_BYOK_API_KEY",
        OpenAiGatewayClient.ApiKeyEnvVar,
        "NUGET_AUTH_TOKEN",
        "VSS_NUGET_ACCESSTOKEN"
    ];

    private readonly object _lock = new();
    private readonly HashSet<string> _registeredSecrets = new(StringComparer.Ordinal);
    private string[] _orderedSecrets = [];

    /// <summary>
    /// Registers an explicit runtime secret value so all exact occurrences are masked via
    /// <see cref="GeminiSecretProvider.MaskKey"/>.
    /// </summary>
    public void RegisterSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return;
        }

        var trimmed = secret.Trim();
        if (trimmed.Length < 4)
        {
            return;
        }

        lock (_lock)
        {
            if (_registeredSecrets.Add(trimmed))
            {
                _orderedSecrets = _registeredSecrets
                    .OrderByDescending(static s => s.Length)
                    .ToArray();
            }
        }
    }

    /// <summary>
    /// Scans known provider credential environment variables and registers any configured values for masking.
    /// </summary>
    public void RegisterEnvironmentSecrets(Func<string, string?>? environmentReader = null)
    {
        var reader = environmentReader ?? Environment.GetEnvironmentVariable;
        foreach (var variableName in SecretEnvVars)
        {
            RegisterSecret(reader(variableName));
        }
    }

    /// <summary>
    /// Redacts all registered runtime secrets and recognized credential/header patterns from <paramref name="text"/>.
    /// </summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;

        string[] snapshot;
        lock (_lock)
        {
            snapshot = _orderedSecrets;
        }

        foreach (var secret in snapshot)
        {
            if (result.Contains(secret, StringComparison.Ordinal))
            {
                result = result.Replace(secret, GeminiSecretProvider.MaskKey(secret), StringComparison.Ordinal);
            }
        }

        return RedactPatterns(result);
    }

    /// <summary>
    /// Redacts known credential patterns without requiring an instance with registered runtime secrets.
    /// </summary>
    public static string RedactPatterns(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = GeminiKeyRegex().Replace(text, static match => GeminiSecretProvider.MaskKey(match.Value));
        result = GitHubTokenRegex().Replace(result, static match => GeminiSecretProvider.MaskKey(match.Value));
        result = SkPrefixedKeyRegex().Replace(result, static match => GeminiSecretProvider.MaskKey(match.Value));
        result = BearerHeaderRegex().Replace(
            result,
            static match => match.Groups[1].Value + GeminiSecretProvider.MaskKey(match.Groups[2].Value));
        result = SensitiveHeaderOrFieldRegex().Replace(
            result,
            static match => match.Groups[1].Value + GeminiSecretProvider.MaskKey(match.Groups[2].Value));
        result = SensitiveQueryParamRegex().Replace(
            result,
            static match => match.Groups[1].Value + GeminiSecretProvider.MaskKey(match.Groups[2].Value));

        return result;
    }

    // Google AI / Gemini API keys (AIza...)
    [GeneratedRegex(@"\bAIza[0-9A-Za-z\-_]{12,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GeminiKeyRegex();

    // GitHub classic & fine-grained PATs / OAuth / Copilot tokens
    [GeneratedRegex(@"\b(?:(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9_]{12,}|github_pat_[A-Za-z0-9_]{16,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenRegex();

    // Anthropic / OpenAI sk-* keys
    [GeneratedRegex(@"\bsk-(?:ant-|proj-)?[A-Za-z0-9\-_]{12,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SkPrefixedKeyRegex();

    // Authorization: Bearer <token>
    [GeneratedRegex(@"(?i)(Authorization\s*[:=]\s*Bearer\s+)([^\s""',;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex BearerHeaderRegex();

    // Header or key=value pairs for API keys, access tokens, and NuGet feed passwords (skip already-masked **** tokens)
    [GeneratedRegex(@"(?i)((?:x-goog-api-key|x-api-key|api[-_]?key|access[-_]?token|ClearTextPassword|Password)\s*[:=]\s*[""']?)(?!\*{4})([^\s""',;&}]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveHeaderOrFieldRegex();

    // Sensitive query string parameters (?key=..., &api_key=..., &token=...)
    [GeneratedRegex(@"(?i)([?&](?:key|api_key|apikey|token)=)(?!\*{4})([^&\s""']+)", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveQueryParamRegex();
}
