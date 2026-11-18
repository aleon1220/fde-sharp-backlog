using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Copilot;

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
/// Wraps the official <c>Microsoft.Copilot.SDK</c> (<see cref="CopilotClient"/> / <see cref="CopilotSession"/>)
/// for agentic backlog generation and BYOK (Bring Your Own Key) provider routing.
/// </summary>
public sealed class GitHubCopilotSdkGateway : ICopilotSdkGateway
{
    public const string CliPathEnvVar = "COPILOT_CLI_PATH";
    public const string CliUrlEnvVar = "COPILOT_CLI_URL";
    public const string DefaultConnectionEnvVar = "COPILOT_SDK_DEFAULT_CONNECTION";
    public const string CopilotTokenEnvVar = "COPILOT_GITHUB_TOKEN";
    public const string GitHubTokenEnvVar = "GITHUB_TOKEN";
    public const string GhTokenEnvVar = "GH_TOKEN";

    private readonly Func<string, string?> _environmentReader;
    private readonly string _baseDirectory;
    private readonly Func<string?> _pathBinaryResolver;

    public GitHubCopilotSdkGateway(Func<string, string?>? environmentReader = null)
        : this(environmentReader, baseDirectory: null, pathBinaryResolver: null)
    {
    }

    internal GitHubCopilotSdkGateway(
        Func<string, string?>? environmentReader,
        string? baseDirectory,
        Func<string?>? pathBinaryResolver = null)
    {
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;
        _pathBinaryResolver = pathBinaryResolver ?? (() => FindCopilotOnPath(_environmentReader));
    }

    public CopilotRuntimeDiagnostics InspectDiagnostics()
    {
        var cliUrl = _environmentReader(CliUrlEnvVar)?.Trim();
        var explicitCliPath = _environmentReader(CliPathEnvVar)?.Trim();
        var defaultConnection = _environmentReader(DefaultConnectionEnvVar)?.Trim();

        var hasToken = !string.IsNullOrWhiteSpace(_environmentReader(CopilotTokenEnvVar)) ||
                       !string.IsNullOrWhiteSpace(_environmentReader(GitHubTokenEnvVar)) ||
                       !string.IsNullOrWhiteSpace(_environmentReader(GhTokenEnvVar));

        var byok = ResolveEnvironmentByok();
        var byokConfigured = byok is not null;

        if (!string.IsNullOrWhiteSpace(cliUrl))
        {
            if (TryParseTcpEndpoint(cliUrl, out var tcpHost, out var tcpPort))
            {
                var formattedEndpoint = FormatTcpEndpoint(tcpHost, tcpPort);
                return new CopilotRuntimeDiagnostics(
                    RuntimeAvailable: true,
                    ConnectionMode: "remote-tcp",
                    RuntimeLocation: formattedEndpoint,
                    HasGitHubToken: hasToken,
                    ByokConfigured: byokConfigured,
                    Summary: $"Remote Copilot runtime TCP at {formattedEndpoint}");
            }

            return new CopilotRuntimeDiagnostics(
                RuntimeAvailable: false,
                ConnectionMode: "unavailable",
                RuntimeLocation: cliUrl,
                HasGitHubToken: hasToken,
                ByokConfigured: byokConfigured,
                Summary: $"Invalid {CliUrlEnvVar} '{cliUrl}' (expected host:port, e.g. localhost:4321)");
        }

        var preferStdio = string.Equals(defaultConnection, "stdio", StringComparison.OrdinalIgnoreCase);
        var preferInProcessExplicit = string.Equals(defaultConnection, "inprocess", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(defaultConnection) && !preferStdio && !preferInProcessExplicit)
        {
            return new CopilotRuntimeDiagnostics(
                RuntimeAvailable: false,
                ConnectionMode: "unavailable",
                RuntimeLocation: null,
                HasGitHubToken: hasToken,
                ByokConfigured: byokConfigured,
                Summary: $"Invalid {DefaultConnectionEnvVar} '{defaultConnection}' (expected 'inprocess' or 'stdio')");
        }

        if (!string.IsNullOrWhiteSpace(explicitCliPath))
        {
            if (!File.Exists(explicitCliPath))
            {
                return new CopilotRuntimeDiagnostics(
                    RuntimeAvailable: false,
                    ConnectionMode: "unavailable",
                    RuntimeLocation: explicitCliPath,
                    HasGitHubToken: hasToken,
                    ByokConfigured: byokConfigured,
                    Summary: $"{CliPathEnvVar} not found: {explicitCliPath}");
            }

            var adjacentFfi = FindAdjacentFfiLibrary(explicitCliPath);
            if (!preferStdio && adjacentFfi is not null)
            {
                return new CopilotRuntimeDiagnostics(
                    RuntimeAvailable: true,
                    ConnectionMode: "inprocess-explicit",
                    RuntimeLocation: explicitCliPath,
                    HasGitHubToken: hasToken,
                    ByokConfigured: byokConfigured,
                    Summary: $"Copilot SDK in-process FFI runtime via {explicitCliPath}");
            }

            if (preferInProcessExplicit)
            {
                return new CopilotRuntimeDiagnostics(
                    RuntimeAvailable: false,
                    ConnectionMode: "unavailable",
                    RuntimeLocation: explicitCliPath,
                    HasGitHubToken: hasToken,
                    ByokConfigured: byokConfigured,
                    Summary: $"{DefaultConnectionEnvVar}=inprocess requires an FFI native library next to {explicitCliPath} (set {DefaultConnectionEnvVar}=stdio for standalone CLI binary)");
            }

            return new CopilotRuntimeDiagnostics(
                RuntimeAvailable: true,
                ConnectionMode: "stdio-explicit",
                RuntimeLocation: explicitCliPath,
                HasGitHubToken: hasToken,
                ByokConfigured: byokConfigured,
                Summary: $"Copilot CLI binary (stdio) at {explicitCliPath}");
        }

        var bundledFfi = FindBundledFfiLibrary(_baseDirectory);
        var bundledBinary = FindBundledRuntimeBinary(_baseDirectory);
        if (!preferStdio && bundledFfi is not null)
        {
            return new CopilotRuntimeDiagnostics(
                RuntimeAvailable: true,
                ConnectionMode: "inprocess-bundled",
                RuntimeLocation: bundledFfi,
                HasGitHubToken: hasToken,
                ByokConfigured: byokConfigured,
                Summary: $"Bundled Copilot SDK in-process FFI runtime ({Path.GetFileName(bundledFfi)})");
        }

        if (!string.IsNullOrWhiteSpace(bundledBinary))
        {
            if (preferInProcessExplicit)
            {
                return new CopilotRuntimeDiagnostics(
                    RuntimeAvailable: false,
                    ConnectionMode: "unavailable",
                    RuntimeLocation: bundledBinary,
                    HasGitHubToken: hasToken,
                    ByokConfigured: byokConfigured,
                    Summary: $"{DefaultConnectionEnvVar}=inprocess requires a bundled FFI native library ({GetFfiRuntimeLibraryFileName()})");
            }

            return new CopilotRuntimeDiagnostics(
                RuntimeAvailable: true,
                ConnectionMode: "stdio-bundled",
                RuntimeLocation: bundledBinary,
                HasGitHubToken: hasToken,
                ByokConfigured: byokConfigured,
                Summary: $"Bundled Copilot SDK runtime (stdio, {Path.GetFileName(bundledBinary)})");
        }

        var pathBinary = _pathBinaryResolver();
        if (!string.IsNullOrWhiteSpace(pathBinary))
        {
            if (preferInProcessExplicit)
            {
                return new CopilotRuntimeDiagnostics(
                    RuntimeAvailable: false,
                    ConnectionMode: "unavailable",
                    RuntimeLocation: pathBinary,
                    HasGitHubToken: hasToken,
                    ByokConfigured: byokConfigured,
                    Summary: $"{DefaultConnectionEnvVar}=inprocess cannot use standalone PATH binary '{pathBinary}' (set {DefaultConnectionEnvVar}=stdio or bundle the runtime)");
            }

            return new CopilotRuntimeDiagnostics(
                RuntimeAvailable: true,
                ConnectionMode: "stdio-path",
                RuntimeLocation: pathBinary,
                HasGitHubToken: hasToken,
                ByokConfigured: byokConfigured,
                Summary: $"Copilot CLI on PATH (stdio, {pathBinary})");
        }

        return new CopilotRuntimeDiagnostics(
            RuntimeAvailable: false,
            ConnectionMode: "unavailable",
            RuntimeLocation: null,
            HasGitHubToken: hasToken,
            ByokConfigured: byokConfigured,
            Summary: "Copilot runtime not found (set COPILOT_CLI_PATH, COPILOT_CLI_URL, install 'copilot', or build with -p:CopilotSkipCliDownload=false)");
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
            var sessionOptions = BuildSessionOptions(request, modelId, effectiveByok, diagnostics);

            await using var session = await client.CreateSessionAsync(sessionOptions, cancellationToken).ConfigureAwait(false);
            var response = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = request.Prompt },
                timeout: request.Timeout ?? TimeSpan.FromMinutes(2),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var content = response?.Data.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException("Microsoft Copilot SDK returned an empty response.");
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

    internal CopilotClient CreateClient(
        string? gitHubToken,
        string? workingDirectory,
        CopilotRuntimeDiagnostics diagnostics)
    {
        var effectiveWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Directory.GetCurrentDirectory()
            : workingDirectory;

        var runtime = diagnostics.ConnectionMode switch
        {
            "remote-tcp" when TryParseTcpEndpoint(diagnostics.RuntimeLocation, out var host, out var port) =>
                (RuntimeConnection)RuntimeConnection.ForTcp(host, port),
            "inprocess-explicit" or "inprocess-bundled" =>
                RuntimeConnection.ForInProcess(),
            "stdio-explicit" or "stdio-path" or "stdio-bundled" when !string.IsNullOrWhiteSpace(diagnostics.RuntimeLocation) =>
                RuntimeConnection.ForSpawnedStdio(
                    path: diagnostics.RuntimeLocation,
                    workingDirectory: effectiveWorkingDirectory),
            _ => RuntimeConnection.ForDefault()
        };

        var options = new CopilotClientOptions
        {
            Mode = CopilotClientMode.CodingAgent,
            Runtime = runtime
        };

        // ForTcp connects to an external runtime that owns its process-wide GitHub identity;
        // Microsoft.Copilot.SDK throws if ClientGitHubOptions.Token or UseLoggedInUser is set on ForTcp.
        if (runtime is not TcpRuntimeConnection)
        {
            var resolvedToken = ResolveGitHubToken(gitHubToken);
            if (!string.IsNullOrWhiteSpace(resolvedToken))
            {
                options.GitHub = new ClientGitHubOptions
                {
                    Token = resolvedToken,
                    UseLoggedInUser = false
                };
            }
            else
            {
                options.GitHub = new ClientGitHubOptions
                {
                    UseLoggedInUser = true
                };
            }
        }

        return new CopilotClient(options);
    }

    internal CreateSessionOptions BuildSessionOptions(
        CopilotGenerationRequest request,
        string modelId,
        CopilotByokOptions? effectiveByok,
        CopilotRuntimeDiagnostics diagnostics)
    {
        var sessionOptions = new CreateSessionOptions
        {
            ClientName = "smart-backlog-assistant",
            Model = modelId,
            EnableConfigDiscovery = false,
            WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? Directory.GetCurrentDirectory()
                : request.WorkingDirectory,
            Permissions = new SessionPermissionOptions
            {
                OnRequest = PermissionHandler.ApproveAll
            },
            Tools = new SessionToolOptions
            {
                AvailableTools = []
            },
            Skills = new SessionSkillOptions
            {
                Enabled = false
            },
            Git = new SessionGitOptions
            {
                EnableHostGitOperations = false
            },
            Instructions = new CreateSessionInstructionOptions
            {
                SkipCustomInstructions = true
            },
            Hooks = new SessionHookOptions
            {
                EnableRepositoryHooks = false
            }
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            sessionOptions.SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = request.SystemPrompt
            };
        }

        if (effectiveByok is not null)
        {
            sessionOptions.GitHub = new SessionGitHubOptions
            {
                Capi = new CapiSessionOptions { Enabled = false }
            };

#pragma warning disable MCPSDK001 // NamedProviderConfig and SessionInferenceOptions.Providers are experimental in Microsoft.Copilot.SDK 2.0.1.
            sessionOptions.Inference = new SessionInferenceOptions
            {
                DefaultProvider = "byok",
                Providers =
                [
                    new NamedProviderConfig
                    {
                        Name = "byok",
                        Type = effectiveByok.Type,
                        BaseUrl = effectiveByok.BaseUrl,
                        ApiKey = effectiveByok.ApiKey,
                        WireApi = effectiveByok.WireApi,
                        Azure = string.IsNullOrWhiteSpace(effectiveByok.AzureApiVersion)
                            ? null
                            : new AzureOptions { ApiVersion = effectiveByok.AzureApiVersion }
                    }
                ]
            };
#pragma warning restore MCPSDK001
        }
        else if (diagnostics.ConnectionMode == "remote-tcp")
        {
            var resolvedToken = ResolveGitHubToken(request.GitHubToken);
            if (!string.IsNullOrWhiteSpace(resolvedToken))
            {
                sessionOptions.GitHub = new SessionGitHubOptions
                {
                    Token = resolvedToken
                };
            }
        }

        return sessionOptions;
    }

    internal static bool TryParseTcpEndpoint(string? rawEndpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        if (string.IsNullOrWhiteSpace(rawEndpoint))
        {
            return false;
        }

        var trimmed = rawEndpoint.Trim();
        if (trimmed.Any(char.IsWhiteSpace) || trimmed.Contains('?') || trimmed.Contains('#'))
        {
            return false;
        }

        string candidateHost;
        int candidatePort;

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo) ||
                (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/"))
            {
                return false;
            }

            var schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal) + 3;
            var slashIdx = trimmed.IndexOf('/', schemeEnd);
            var rawAuthority = slashIdx >= 0 ? trimmed[schemeEnd..slashIdx] : trimmed[schemeEnd..];
            if (!rawAuthority.EndsWith($":{uri.Port}", StringComparison.Ordinal))
            {
                return false;
            }

            candidateHost = uri.Host.Trim('[', ']');
            candidatePort = uri.Port;
        }
        else if (trimmed.StartsWith('['))
        {
            var closeBracket = trimmed.IndexOf(']');
            if (closeBracket <= 1 ||
                closeBracket + 2 >= trimmed.Length ||
                trimmed[closeBracket + 1] != ':')
            {
                return false;
            }

            candidateHost = trimmed[1..closeBracket];
            if (!int.TryParse(trimmed[(closeBracket + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out candidatePort) ||
                Uri.CheckHostName(candidateHost) != UriHostNameType.IPv6)
            {
                return false;
            }
        }
        else
        {
            var firstColon = trimmed.IndexOf(':');
            var lastColon = trimmed.LastIndexOf(':');
            if (firstColon < 0 || firstColon != lastColon || firstColon == trimmed.Length - 1)
            {
                return false;
            }

            candidateHost = firstColon == 0 ? "localhost" : trimmed[..firstColon];
            if (!int.TryParse(trimmed[(firstColon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out candidatePort))
            {
                return false;
            }
        }

        if (candidatePort is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(candidateHost) ||
            candidateHost[0] == '[' ||
            Uri.CheckHostName(candidateHost) == UriHostNameType.Unknown)
        {
            return false;
        }

        host = candidateHost;
        port = candidatePort;
        return true;
    }

    private static string FormatTcpEndpoint(string host, int port) =>
        host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";

    private string? ResolveGitHubToken(string? explicitToken) =>
        !string.IsNullOrWhiteSpace(explicitToken)
            ? explicitToken.Trim()
            : _environmentReader(CopilotTokenEnvVar)?.Trim()
              ?? _environmentReader(GitHubTokenEnvVar)?.Trim()
              ?? _environmentReader(GhTokenEnvVar)?.Trim();

    private static InvalidOperationException CreateUnavailableRuntimeException(CopilotRuntimeDiagnostics diagnostics) =>
        new(
            $"GitHub Copilot SDK runtime is unavailable ({diagnostics.Summary}). " +
            "Install Copilot CLI and run 'copilot login', set COPILOT_CLI_PATH or COPILOT_CLI_URL, " +
            "or build with '-p:CopilotSkipCliDownload=false' to bundle the runtime.");

    private static string? FindCopilotOnPath(Func<string, string?> environmentReader)
    {
        var pathEnv = environmentReader("PATH");
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

    private static string? FindAdjacentFfiLibrary(string explicitCliPath)
    {
        var fullEntrypoint = Path.GetFullPath(explicitCliPath);
        var directory = Path.GetDirectoryName(fullEntrypoint);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var flatLibraryPath = Path.Combine(directory, GetFfiRuntimeLibraryFileName());
        if (File.Exists(flatLibraryPath))
        {
            return flatLibraryPath;
        }

        var adjacentRuntimeNode = Path.Combine(directory, "runtime.node");
        if (File.Exists(adjacentRuntimeNode))
        {
            return adjacentRuntimeNode;
        }

        var napiFolder = GetNapiPrebuildsFolder();
        if (!string.IsNullOrWhiteSpace(napiFolder))
        {
            var prebuildNode = Path.Combine(directory, "prebuilds", napiFolder, "runtime.node");
            if (File.Exists(prebuildNode))
            {
                return prebuildNode;
            }
        }

        return null;
    }

    private static string? FindBundledFfiLibrary(string baseDirectory)
    {
        var runtimesDir = Path.Combine(baseDirectory, "runtimes");
        if (!Directory.Exists(runtimesDir))
        {
            return null;
        }

        var targetNames = new[] { GetFfiRuntimeLibraryFileName(), "runtime.node" };
        var portableRid = GetPortableRid();
        if (!string.IsNullOrWhiteSpace(portableRid))
        {
            var nativeDir = Path.Combine(runtimesDir, portableRid, "native");
            if (Directory.Exists(nativeDir))
            {
                foreach (var targetName in targetNames)
                {
                    var direct = Path.Combine(nativeDir, targetName);
                    if (File.Exists(direct))
                    {
                        return direct;
                    }
                }
            }
        }

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

    private static string? FindBundledRuntimeBinary(string baseDirectory)
    {
        var runtimesDir = Path.Combine(baseDirectory, "runtimes");
        if (!Directory.Exists(runtimesDir))
        {
            return null;
        }

        var targetNames = OperatingSystem.IsWindows()
            ? new[] { "copilot-runtime.exe", "copilot.exe" }
            : new[] { "copilot-runtime", "copilot" };

        var portableRid = GetPortableRid();
        if (!string.IsNullOrWhiteSpace(portableRid))
        {
            var nativeDir = Path.Combine(runtimesDir, portableRid, "native");
            if (Directory.Exists(nativeDir))
            {
                foreach (var targetName in targetNames)
                {
                    var direct = Path.Combine(nativeDir, targetName);
                    if (File.Exists(direct))
                    {
                        return direct;
                    }
                }
            }
        }

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

    private static string GetFfiRuntimeLibraryFileName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "copilot_runtime.dll";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "libcopilot_runtime.dylib";
        }

        return "libcopilot_runtime.so";
    }

    private static string? GetPortableRid()
    {
        string os;
        if (OperatingSystem.IsWindows())
        {
            os = "win";
        }
        else if (OperatingSystem.IsLinux())
        {
            os = RuntimeInformation.RuntimeIdentifier.StartsWith("linux-musl-", StringComparison.Ordinal)
                ? "linux-musl"
                : "linux";
        }
        else if (OperatingSystem.IsMacOS())
        {
            os = "osx";
        }
        else
        {
            return null;
        }

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null
        };

        return arch is not null ? $"{os}-{arch}" : null;
    }

    private static string? GetNapiPrebuildsFolder()
    {
        string platform;
        if (OperatingSystem.IsWindows())
        {
            platform = "win32";
        }
        else if (OperatingSystem.IsLinux())
        {
            platform = RuntimeInformation.RuntimeIdentifier.StartsWith("linux-musl-", StringComparison.Ordinal)
                ? "linuxmusl"
                : "linux";
        }
        else if (OperatingSystem.IsMacOS())
        {
            platform = "darwin";
        }
        else
        {
            return null;
        }

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null
        };

        return arch is not null ? $"{platform}-{arch}" : null;
    }
}
