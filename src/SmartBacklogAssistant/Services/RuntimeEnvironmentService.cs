using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Inspects and formats runtime diagnostics including OS platform, build metadata,
/// .NET runtime version, and the enabled AI model (defaulting to Google Gemini).
/// </summary>
public sealed class RuntimeEnvironmentService
{
    public const string DefaultGeminiModel = "gemini-3.8-flash";
    public const string GeminiModelEnvVar = "GEMINI_MODEL";

    public RuntimeEnvironmentInfo Inspect(
        AiProviderType provider = AiProviderType.GoogleGemini,
        string? customModelName = null)
    {
        var osPlatform = ResolveOsPlatform();
        var osDescription = RuntimeInformation.OSDescription.Trim();
        var osArch = RuntimeInformation.OSArchitecture.ToString();
        var processArch = RuntimeInformation.ProcessArchitecture.ToString();
        var frameworkDescription = RuntimeInformation.FrameworkDescription.Trim();

        var assembly = typeof(RuntimeEnvironmentService).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var buildVersion = !string.IsNullOrWhiteSpace(informationalVersion)
            ? informationalVersion.Split('+')[0]
            : assembly.GetName().Version?.ToString() ?? "1.1.0";

        var buildConfiguration = ResolveBuildConfiguration(assembly);
        var enabledAiModel = ResolveEnabledModelDisplay(provider, customModelName);

        return new RuntimeEnvironmentInfo(
            OsPlatform: osPlatform,
            OsDescription: osDescription,
            OsArchitecture: osArch,
            ProcessArchitecture: processArch,
            FrameworkDescription: frameworkDescription,
            BuildVersion: buildVersion,
            BuildConfiguration: buildConfiguration,
            EnabledProvider: provider,
            EnabledAiModel: enabledAiModel,
            KeyPortalUrl: GeminiSecretProvider.GeminiApiKeysUrl);
    }

    public string FormatRuntimeBanner(RuntimeEnvironmentInfo info, GeminiSecretResolution secretResolution)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(secretResolution);

        var keyStatus = secretResolution.IsConfigured
            ? $"{secretResolution.SourceDetail} [{secretResolution.MaskedKey}]"
            : $"Not registered in OS or CLI — required for live Gemini generation (get key: {info.KeyPortalUrl})";

        var sb = new StringBuilder();
        sb.AppendLine("--- Runtime Environment & Build Info ---");
        sb.AppendLine($"  OS Running       : {info.OsPlatform} ({info.OsDescription} | OS Arch: {info.OsArchitecture}, Process Arch: {info.ProcessArchitecture})");
        sb.AppendLine($"  Build Info       : SmartBacklogAssistant v{info.BuildVersion} ({info.BuildConfiguration}) | Runtime: {info.FrameworkDescription}");
        sb.AppendLine($"  AI Model Enabled : {info.EnabledAiModel}");
        sb.AppendLine($"  Gemini Key Status: {keyStatus}");
        sb.AppendLine($"  API Key Portal   : {info.KeyPortalUrl}");
        sb.Append("----------------------------------------");

        return sb.ToString().Replace("\r\n", "\n");
    }

    public void WriteRuntimeBanner(TextWriter output, RuntimeEnvironmentInfo info, GeminiSecretResolution secretResolution)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine(FormatRuntimeBanner(info, secretResolution));
    }

    private static string ResolveOsPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "Windows";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "Linux";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "macOS";
        }

        return "Other";
    }

    private static string ResolveBuildConfiguration(Assembly assembly)
    {
        var attribute = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>();
        if (!string.IsNullOrWhiteSpace(attribute?.Configuration))
        {
            return attribute.Configuration;
        }

#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }

    private static string ResolveEnabledModelDisplay(AiProviderType provider, string? customModelName)
    {
        var configuredGeminiModel = !string.IsNullOrWhiteSpace(customModelName)
            ? customModelName.Trim()
            : Environment.GetEnvironmentVariable(GeminiModelEnvVar) is { Length: > 0 } envModel
                ? envModel.Trim()
                : DefaultGeminiModel;

        return provider switch
        {
            AiProviderType.GoogleGemini => $"Google Gemini ({configuredGeminiModel})",
            AiProviderType.AzureOpenAi => "Azure OpenAI (gpt-4o)",
            AiProviderType.AnthropicClaude => "Anthropic Claude (claude-3-5-sonnet)",
            _ => $"Manual Prompt Mode (Google Gemini {configuredGeminiModel} / Copilot Ready)"
        };
    }
}
