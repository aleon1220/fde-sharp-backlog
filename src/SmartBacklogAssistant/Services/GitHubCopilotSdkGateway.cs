using System.Runtime.InteropServices;
using GitHub.Copilot;

namespace SmartBacklogAssistant.Services;

public sealed record CopilotByokOptions(
    string Type,
    string BaseUrl,
    string? ApiKey = null,
    string? WireApi = null,
    string? AzureApiVersion = null);

public sealed record CopilotGenerationRequest(
    string Prompt,
    string SystemPrompt,
    string? ModelId = null,
    string? GitHubToken = null,
    string? WorkingDirectory = null,
    CopilotByokOptions? Byok = null,
    TimeSpan? Timeout = null);

public sealed record CopilotModelDescriptor(
    string Id,
    string Name);

public sealed record CopilotRuntimeDiagnostics(
    bool RuntimeAvailable,
    string ConnectionMode,
    string? RuntimeLocation,
    bool HasGitHubToken,
    bool ByokConfigured,
    string Summary);

public interface ICopilotSdkGateway
{
    CopilotRuntimeDiagnostics InspectDiagnostics();

    Task<IReadOnlyList<CopilotModelDescriptor>> ListModelsAsync(
        string? gitHubToken = null,
        CancellationToken cancellationToken = default);

    Task<string> GenerateContentAsync(
        CopilotGenerationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Wraps the official <c>GitHub.Copilot.SDK</c> (<see cref="CopilotClient"/> / <see cref="CopilotSession"/>)
/// for agentic backlog generation and BYOK (Bring Your Own Key) provider routing.
/// </summary>
public sealed class GitHubCopilotSdkGateway : ICopilotSdkGateway
{
    public const string CliPathEnvVar = "COPILOT_CLI_PATH";
    public const string CliUrlEnvVar = "COPILOT_CLI_URL";
    public const string CopilotTokenEnvVar = "COPILOT_GITHUB_TOKEN";
    public const string GitHubTokenEnvVar = "GITHUB_TOKEN";
    public const string GhTokenEnvVar = "GH_TOKEN";

    private readonly Func<string, string?> _environmentReader;

    public GitHubCopilotSdkGateway(Func<string, string?>? environmentReader = null)
    {
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
    }

    public CopilotRuntimeDiagnostics InspectDiagnostics()
    {
        var cliUrl = _environmentReader(CliUrlEnvVar)?.Trim();
        var explicitCliPath = _environmentReader(CliPathEnvVar)?.Trim();
        var pathBinary = FindCopilotOnPath();
        var bundledRuntime = FindBundledRuntimeBinary();

        var hasToken = !string.IsNullOrWhiteSpace(_environmentReader(CopilotTokenEnvVar)) ||
                       !string.IsNullOrWhiteSpace(_environmentReader(GitHubTokenEnvVar)) ||
                       !string.IsNullOrWhiteSpace(_environmentReader(GhTokenEnvVar));

        var byok = ResolveEnvironmentByok();
        var byokConfigured = byok is not null;

        if (!string.IsNullOrWhiteSpace(cliUrl))
        {
            return new CopilotRuntimeDiagnostics(
                RuntimeAvailable: true,
                ConnectionMode: "remote-uri",
                RuntimeLocation: cliUrl,
                HasGitHubToken: hasToken,
                ByokConfigured: byokConfigured,
                Summary: $"Remote Copilot CLI runtime at {cliUrl}");
        }

        if (!string.IsNullOrWhiteSpace(explicitCliPath))
        {
            var exists = File.Exists(explicitCliPath);
            return new CopilotRuntimeDiagnostics(
                RuntimeAvailable: exists,
                ConnectionMode: "stdio-explicit",
                RuntimeLocation: explicitCliPath,
                HasGitHubToken: hasToken,
                ByokConfigured: byokConfigured,
                Summary: exists
                    ? $"Copilot CLI binary at {explicitCliPath}"
                    : $"COPILOT_CLI_PATH not found: {explicitCliPath}");
        }

        if (!string.IsNullOrWhiteSpace(pathBinary))
        {
            return new CopilotRuntimeDiagnostics(
                RuntimeAvailable: true,
                ConnectionMode: "stdio-path",
                RuntimeLocation: pathBinary,
                HasGitHubToken: hasToken,
                ByokConfigured: byokConfigured,
                Summary: $"Copilot CLI on PATH ({pathBinary})");
        }

        if (!string.IsNullOrWhiteSpace(bundledRuntime))
        {
            return new CopilotRuntimeDiagnostics(
                RuntimeAvailable: true,
                ConnectionMode: "stdio-bundled",
                RuntimeLocation: bundledRuntime,
                HasGitHubToken: hasToken,
                ByokConfigured: byokConfigured,
                Summary: $"Bundled Copilot SDK runtime ({Path.GetFileName(bundledRuntime)})");
        }

        return new CopilotRuntimeDiagnostics(
            RuntimeAvailable: false,
            ConnectionMode: "unavailable",
            RuntimeLocation: null,
            HasGitHubToken: hasToken,
            ByokConfigured: byokConfigured,
            Summary: "Copilot CLI not found (set COPILOT_CLI_PATH or install 'copilot')");
    }

    public async Task<IReadOnlyList<CopilotModelDescriptor>> ListModelsAsync(
        string? gitHubToken = null,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = InspectDiagnostics();
        if (!diagnostics.RuntimeAvailable)
        {
            throw CreateUnavailableRuntimeException(diagnostics);
        }

        await using var client = CreateClient(gitHubToken, workingDirectory: null, diagnostics);
        await client.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            return models
                .Select(m => new CopilotModelDescriptor(
                    Id: m.Id,
                    Name: string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name))
                .DistinctBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            await client.StopAsync().ConfigureAwait(false);
        }
    }

    public async Task<string> GenerateContentAsync(
        CopilotGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var diagnostics = InspectDiagnostics();
        if (!diagnostics.RuntimeAvailable)
        {
            throw CreateUnavailableRuntimeException(diagnostics);
        }

        var effectiveByok = request.Byok ?? ResolveEnvironmentByok();
        var modelId = string.IsNullOrWhiteSpace(request.ModelId)
            ? RuntimeEnvironmentService.DefaultCopilotModel
            : request.ModelId.Trim();

        await using var client = CreateClient(request.GitHubToken, request.WorkingDirectory, diagnostics);
        await client.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var sessionConfig = new SessionConfig
            {
                ClientName = "smart-backlog-assistant",
                Model = modelId,
                OnPermissionRequest = PermissionHandler.ApproveAll,
                AvailableTools = [],
                EnableConfigDiscovery = false,
                EnableSkills = false,
                EnableHostGitOperations = false,
                WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                    ? Directory.GetCurrentDirectory()
                    : request.WorkingDirectory
            };

            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                sessionConfig.SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Replace,
                    Content = request.SystemPrompt
                };
            }

            if (effectiveByok is not null)
            {
                sessionConfig.Provider = new ProviderConfig
                {
                    Type = effectiveByok.Type,
                    BaseUrl = effectiveByok.BaseUrl,
                    ApiKey = effectiveByok.ApiKey,
                    WireApi = effectiveByok.WireApi,
                    ModelId = modelId,
                    WireModel = modelId,
                    Azure = string.IsNullOrWhiteSpace(effectiveByok.AzureApiVersion)
                        ? null
                        : new AzureOptions { ApiVersion = effectiveByok.AzureApiVersion }
                };
            }

            await using var session = await client.CreateSessionAsync(sessionConfig, cancellationToken).ConfigureAwait(false);
            var response = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = request.Prompt },
                timeout: request.Timeout ?? TimeSpan.FromMinutes(2),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var content = response?.Data?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException("GitHub Copilot SDK returned an empty response.");
            }

            return content;
        }
        finally
        {
            await client.StopAsync().ConfigureAwait(false);
        }
    }

    public CopilotByokOptions? ResolveEnvironmentByok()
    {
        var explicitBaseUrl = _environmentReader("COPILOT_BYOK_BASE_URL")?.Trim();
        if (!string.IsNullOrWhiteSpace(explicitBaseUrl))
        {
            var type = _environmentReader("COPILOT_BYOK_TYPE")?.Trim();
            if (string.IsNullOrWhiteSpace(type))
            {
                type = "openai";
            }

            return new CopilotByokOptions(
                Type: type,
                BaseUrl: explicitBaseUrl,
                ApiKey: _environmentReader("COPILOT_BYOK_API_KEY")?.Trim(),
                WireApi: _environmentReader("COPILOT_BYOK_WIRE_API")?.Trim(),
                AzureApiVersion: _environmentReader("AZURE_OPENAI_API_VERSION")?.Trim());
        }

        var azureEndpoint = _environmentReader("AZURE_OPENAI_ENDPOINT")?.Trim();
        var azureKey = _environmentReader("AZURE_OPENAI_API_KEY")?.Trim();
        if (!string.IsNullOrWhiteSpace(azureEndpoint) && !string.IsNullOrWhiteSpace(azureKey))
        {
            var isOpenAiV1 = azureEndpoint.TrimEnd('/').EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase);
            return new CopilotByokOptions(
                Type: isOpenAiV1 ? "openai" : "azure",
                BaseUrl: azureEndpoint,
                ApiKey: azureKey,
                WireApi: isOpenAiV1 ? "responses" : null,
                AzureApiVersion: _environmentReader("AZURE_OPENAI_API_VERSION")?.Trim() ?? (isOpenAiV1 ? null : "2024-10-21"));
        }

        var ollamaHost = _environmentReader("OLLAMA_HOST")?.Trim();
        if (!string.IsNullOrWhiteSpace(ollamaHost))
        {
            var baseUrl = ollamaHost.TrimEnd('/') + "/v1";
            return new CopilotByokOptions(Type: "openai", BaseUrl: baseUrl);
        }

        return null;
    }

    private CopilotClient CreateClient(
        string? gitHubToken,
        string? workingDirectory,
        CopilotRuntimeDiagnostics diagnostics)
    {
        var options = new CopilotClientOptions
        {
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Directory.GetCurrentDirectory()
                : workingDirectory
        };

        options.Connection = diagnostics.ConnectionMode switch
        {
            "remote-uri" when !string.IsNullOrWhiteSpace(diagnostics.RuntimeLocation) =>
                RuntimeConnection.ForUri(diagnostics.RuntimeLocation),
            ("stdio-explicit" or "stdio-path") when !string.IsNullOrWhiteSpace(diagnostics.RuntimeLocation) =>
                RuntimeConnection.ForStdio(diagnostics.RuntimeLocation),
            _ => null
        };

        var resolvedToken = !string.IsNullOrWhiteSpace(gitHubToken)
            ? gitHubToken.Trim()
            : _environmentReader(CopilotTokenEnvVar)?.Trim()
              ?? _environmentReader(GitHubTokenEnvVar)?.Trim()
              ?? _environmentReader(GhTokenEnvVar)?.Trim();

        if (!string.IsNullOrWhiteSpace(resolvedToken))
        {
            options.GitHubToken = resolvedToken;
            options.UseLoggedInUser = false;
        }
        else
        {
            options.UseLoggedInUser = true;
        }

        return new CopilotClient(options);
    }

    private static InvalidOperationException CreateUnavailableRuntimeException(CopilotRuntimeDiagnostics diagnostics) =>
        new(
            $"GitHub Copilot SDK runtime is unavailable ({diagnostics.Summary}). " +
            "Install the GitHub Copilot CLI and run 'copilot login', set COPILOT_CLI_PATH or COPILOT_CLI_URL, " +
            "or build with '-p:CopilotSkipCliDownload=false' to bundle the runtime.");

    private static string? FindCopilotOnPath()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        var binaryNames = OperatingSystem.IsWindows()
            ? new[] { "copilot.exe", "copilot.cmd", "copilot" }
            : new[] { "copilot" };

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var binary in binaryNames)
            {
                var candidate = Path.Combine(dir, binary);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? FindBundledRuntimeBinary()
    {
        var runtimesDir = Path.Combine(AppContext.BaseDirectory, "runtimes");
        if (!Directory.Exists(runtimesDir))
        {
            return null;
        }

        var targetNames = OperatingSystem.IsWindows()
            ? new[] { "copilot-runtime.exe", "copilot.exe" }
            : new[] { "copilot-runtime", "copilot" };

        try
        {
            foreach (var targetName in targetNames)
            {
                var found = Directory.EnumerateFiles(runtimesDir, targetName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }
        catch (IOException)
        {
            // Ignore directory enumeration issues
        }

        return null;
    }
}
