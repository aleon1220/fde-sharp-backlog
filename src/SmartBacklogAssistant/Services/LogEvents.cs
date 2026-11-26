using Microsoft.Extensions.Logging;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Compile-time source-generated structured log events (<see cref="LoggerMessageAttribute"/>)
/// providing zero-boxing, high-performance diagnostics across CLI, ingestion, workflow, and AI providers.
/// </summary>
internal static partial class LogEvents
{
    // 1000-1099: CLI & Configuration lifecycle
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Debug,
        Message = "CLI initialized: subcommand={Subcommand}, logLevel={LogLevel} ({LevelSource}), logFormat={LogFormat} ({FormatSource}), nonInteractive={NonInteractive}, dryRun={DryRun}")]
    public static partial void CliInitialized(
        ILogger logger,
        string subcommand,
        LogLevel logLevel,
        string levelSource,
        string logFormat,
        string formatSource,
        bool nonInteractive,
        bool dryRun);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Dispatching CLI command '{Command}' (workspace='{Workspace}')")]
    public static partial void DispatchingCommand(
        ILogger logger,
        string command,
        string workspace);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Saved workspace settings to '{SettingsPath}' (logLevel={LogLevel}, logFormat={LogFormat})")]
    public static partial void ConfigSaved(
        ILogger logger,
        string settingsPath,
        string logLevel,
        string logFormat);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Debug,
        Message = "Previewing saved Markdown '{MarkdownPath}' (select={SelectInteractive}, copyTarget={CopyTarget})")]
    public static partial void PreviewingMarkdown(
        ILogger logger,
        string markdownPath,
        bool selectInteractive,
        string copyTarget);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Warning,
        Message = "Model selection warning for {Provider}/{Model}: {Reason}")]
    public static partial void ModelSelectionWarning(
        ILogger logger,
        string provider,
        string model,
        string reason);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Error,
        Message = "Command execution failed: {ErrorMessage}")]
    public static partial void CommandExecutionFailed(
        ILogger logger,
        Exception exception,
        string errorMessage);

    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Debug,
        Message = "Interactive REPL reconfigured logging: logLevel={LogLevel}, logFormat={LogFormat}")]
    public static partial void ReplLoggingReconfigured(
        ILogger logger,
        LogLevel logLevel,
        string logFormat);

    // 2000-2099: Raw data ingestion & prompt resolution
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Debug,
        Message = "Collecting raw discovery input from file '{FilePath}'")]
    public static partial void CollectingFromFile(
        ILogger logger,
        string filePath);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Debug,
        Message = "Enumerated {FileCount} queued input file(s) in '{DirectoryPath}'")]
    public static partial void EnumeratedInputFiles(
        ILogger logger,
        int fileCount,
        string directoryPath);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Trace,
        Message = "Collected normalized raw input ({CharCount} chars)")]
    public static partial void RawTextCollected(
        ILogger logger,
        int charCount);

    // 3000-3099: Workflow orchestration, formatting & validation
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "Workflow started: provider={Provider}, mode={Mode}, source={SourceType}, dryRun={DryRun}")]
    public static partial void WorkflowStarted(
        ILogger logger,
        string provider,
        string mode,
        string sourceType,
        bool dryRun);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Debug,
        Message = "Runtime environment banner:\n{RuntimeBanner}")]
    public static partial void RuntimeBannerLogged(
        ILogger logger,
        string runtimeBanner);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Debug,
        Message = "[1/5 Local -> Collect] Source={SourceType} Target='{Target}'")]
    public static partial void WorkflowCollectStage(
        ILogger logger,
        string sourceType,
        string target);

    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Information,
        Message = "Collected raw context ({CharCount} chars) in {ElapsedMs}ms")]
    public static partial void WorkflowCollected(
        ILogger logger,
        int charCount,
        long elapsedMs);

    [LoggerMessage(
        EventId = 3005,
        Level = LogLevel.Trace,
        Message = "Raw data preview ({CharCount} chars total): {Preview}")]
    public static partial void WorkflowRawDataTrace(
        ILogger logger,
        int charCount,
        string preview);

    [LoggerMessage(
        EventId = 3006,
        Level = LogLevel.Debug,
        Message = "Resolved provider secret: source={SecretSource}, maskedKey={MaskedKey}")]
    public static partial void SecretResolved(
        ILogger logger,
        string secretSource,
        string maskedKey);

    [LoggerMessage(
        EventId = 3007,
        Level = LogLevel.Debug,
        Message = "Rendered prompt template '{PromptSource}' for {PromptType} ({CharCount} chars)")]
    public static partial void PromptRendered(
        ILogger logger,
        string promptSource,
        string promptType,
        int charCount);

    [LoggerMessage(
        EventId = 3008,
        Level = LogLevel.Debug,
        Message = "[2/5 Local -> Process] {Detail}")]
    public static partial void WorkflowProcessStage(
        ILogger logger,
        string detail);

    [LoggerMessage(
        EventId = 3009,
        Level = LogLevel.Warning,
        Message = "Google Search grounding skipped ({Warning}); continuing with standard draft")]
    public static partial void WebGroundingSkipped(
        ILogger logger,
        string warning);

    [LoggerMessage(
        EventId = 3010,
        Level = LogLevel.Information,
        Message = "Web grounding completed with {SourceCount} verified source(s) in {ElapsedMs}ms")]
    public static partial void WebGroundingCompleted(
        ILogger logger,
        int sourceCount,
        long elapsedMs);

    [LoggerMessage(
        EventId = 3011,
        Level = LogLevel.Warning,
        Message = "Live goal expansion returned non-JSON payload; falling back to offline creative synthesis")]
    public static partial void GoalExpansionJsonFallback(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 3012,
        Level = LogLevel.Debug,
        Message = "Provider guidance: {GuidanceMessage}")]
    public static partial void ProviderGuidance(
        ILogger logger,
        string guidanceMessage);

    [LoggerMessage(
        EventId = 3013,
        Level = LogLevel.Debug,
        Message = "[Telemetry] Description word budget: {WordBudgetBadge} | [3/5 Local -> Validate] Outcome validated successfully.")]
    public static partial void OutcomeValidationTelemetry(
        ILogger logger,
        string wordBudgetBadge);

    [LoggerMessage(
        EventId = 3014,
        Level = LogLevel.Debug,
        Message = "Saved ticket artifact '{SlugMarkdownPath}' (jsonExport={JsonFileOut})")]
    public static partial void TicketArtifactsSaved(
        ILogger logger,
        string slugMarkdownPath,
        bool jsonFileOut);

    [LoggerMessage(
        EventId = 3015,
        Level = LogLevel.Information,
        Message = "Workflow completed: tickets={TicketCount}, primaryTitle='{Title}', dryRun={DryRun}, elapsedMs={ElapsedMs}")]
    public static partial void WorkflowCompleted(
        ILogger logger,
        int ticketCount,
        string title,
        bool dryRun,
        long elapsedMs);

    [LoggerMessage(
        EventId = 3016,
        Level = LogLevel.Debug,
        Message = "Model orchestrator parsed outcome '{Title}' (provider={Provider}, backlogType={BacklogType}, scenarios={ScenarioCount}, verbosity={Verbosity})")]
    public static partial void OutcomeOrchestrated(
        ILogger logger,
        string title,
        string provider,
        string backlogType,
        int scenarioCount,
        string verbosity);

    // 4000-4099: AI Providers, Gemini HTTP & Copilot SDK
    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Information,
        Message = "Dispatching AI provider call: provider={Provider}, model={Model}, backlogType={BacklogType}, live={LiveEnabled}")]
    public static partial void ProviderCallDispatching(
        ILogger logger,
        string provider,
        string model,
        string backlogType,
        bool liveEnabled);

    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Debug,
        Message = "AI provider call completed: provider={Provider}, resolvedModel={ResolvedModel}, usedLive={UsedLive}, contentLength={ContentLength}, elapsedMs={ElapsedMs}")]
    public static partial void ProviderCallCompleted(
        ILogger logger,
        string provider,
        string resolvedModel,
        bool usedLive,
        int contentLength,
        long elapsedMs);

    [LoggerMessage(
        EventId = 4003,
        Level = LogLevel.Debug,
        Message = "Gemini HTTP {Method} {EndpointPath}")]
    public static partial void GeminiHttpRequest(
        ILogger logger,
        string method,
        string endpointPath);

    [LoggerMessage(
        EventId = 4004,
        Level = LogLevel.Trace,
        Message = "Gemini HTTP {Method} {EndpointPath} responded HTTP {StatusCode} ({ResponseLength} chars) in {ElapsedMs}ms")]
    public static partial void GeminiHttpResponse(
        ILogger logger,
        string method,
        string endpointPath,
        int statusCode,
        int responseLength,
        long elapsedMs);

    [LoggerMessage(
        EventId = 4005,
        Level = LogLevel.Error,
        Message = "Gemini HTTP {Method} {EndpointPath} failed with HTTP {StatusCode} ({ReasonPhrase}): {ResponsePreview}")]
    public static partial void GeminiHttpError(
        ILogger logger,
        string method,
        string endpointPath,
        int statusCode,
        string reasonPhrase,
        string responsePreview);

    [LoggerMessage(
        EventId = 4006,
        Level = LogLevel.Warning,
        Message = "Gemini web grounding HTTP {StatusCode} ({ReasonPhrase}); returning degraded non-grounded result")]
    public static partial void GeminiGroundingWarning(
        ILogger logger,
        int statusCode,
        string reasonPhrase);

    [LoggerMessage(
        EventId = 4007,
        Level = LogLevel.Debug,
        Message = "Copilot SDK runtime diagnostics: mode={ConnectionMode}, available={RuntimeAvailable}, hasToken={HasToken}, byok={ByokConfigured}, location='{Location}'")]
    public static partial void CopilotDiagnosticsInspected(
        ILogger logger,
        string connectionMode,
        bool runtimeAvailable,
        bool hasToken,
        bool byokConfigured,
        string location);

    [LoggerMessage(
        EventId = 4008,
        Level = LogLevel.Information,
        Message = "Invoking Copilot SDK generation: mode={ConnectionMode}, model={ModelId}, byokType={ByokType}")]
    public static partial void CopilotGenerationStarting(
        ILogger logger,
        string connectionMode,
        string modelId,
        string byokType);

    [LoggerMessage(
        EventId = 4009,
        Level = LogLevel.Trace,
        Message = "Copilot SDK generation completed: model={ModelId}, promptChars={PromptChars}, responseChars={ResponseChars}, elapsedMs={ElapsedMs}")]
    public static partial void CopilotGenerationCompleted(
        ILogger logger,
        string modelId,
        int promptChars,
        int responseChars,
        long elapsedMs);

    [LoggerMessage(
        EventId = 4010,
        Level = LogLevel.Debug,
        Message = "OpenAI Gateway HTTP {Method} {EndpointUri} (wireApi={WireApi}, model={ModelId})")]
    public static partial void OpenAiHttpRequest(
        ILogger logger,
        string method,
        string endpointUri,
        string wireApi,
        string modelId);

    [LoggerMessage(
        EventId = 4011,
        Level = LogLevel.Trace,
        Message = "OpenAI Gateway HTTP {Method} {EndpointUri} responded HTTP {StatusCode} ({ResponseLength} chars) in {ElapsedMs}ms")]
    public static partial void OpenAiHttpResponse(
        ILogger logger,
        string method,
        string endpointUri,
        int statusCode,
        int responseLength,
        long elapsedMs);

    [LoggerMessage(
        EventId = 4012,
        Level = LogLevel.Error,
        Message = "OpenAI Gateway HTTP {Method} {EndpointUri} failed with HTTP {StatusCode} ({ReasonPhrase}): {ResponsePreview}")]
    public static partial void OpenAiHttpError(
        ILogger logger,
        string method,
        string endpointUri,
        int statusCode,
        string reasonPhrase,
        string responsePreview);

    [LoggerMessage(
        EventId = 4013,
        Level = LogLevel.Debug,
        Message = "OpenAI Gateway offline/unconfigured fallback: {Reason}")]
    public static partial void OpenAiGatewayFallback(
        ILogger logger,
        string reason);

    [LoggerMessage(
        EventId = 4014,
        Level = LogLevel.Debug,
        Message = "API key format check: provider={Provider}, maskedKey={MaskedKey}, valid={IsValid}")]
    public static partial void ApiKeyValidated(
        ILogger logger,
        string provider,
        string maskedKey,
        bool isValid);

    [LoggerMessage(
        EventId = 4015,
        Level = LogLevel.Information,
        Message = "Registered API key: provider={Provider}, envVar={EnvVar}, maskedKey={MaskedKey}, scope={Scope}, persistedToUserEnv={PersistedToUserEnv}")]
    public static partial void ApiKeySaved(
        ILogger logger,
        string provider,
        string envVar,
        string maskedKey,
        string scope,
        bool persistedToUserEnv);

    [LoggerMessage(
        EventId = 4016,
        Level = LogLevel.Information,
        Message = "Cleared API key: provider={Provider}, maskedKey={MaskedKey}, scope={Scope}")]
    public static partial void ApiKeyCleared(
        ILogger logger,
        string provider,
        string maskedKey,
        string scope);

    [LoggerMessage(
        EventId = 4017,
        Level = LogLevel.Information,
        Message = "Live API key verification succeeded: provider={Provider}, maskedKey={MaskedKey}, endpoint={Endpoint}, elapsedMs={ElapsedMs}")]
    public static partial void ApiKeyLiveVerificationSucceeded(
        ILogger logger,
        string provider,
        string maskedKey,
        string endpoint,
        long elapsedMs);

    [LoggerMessage(
        EventId = 4018,
        Level = LogLevel.Warning,
        Message = "Live API key verification failed: provider={Provider}, maskedKey={MaskedKey}, endpoint={Endpoint}, reason={Reason}")]
    public static partial void ApiKeyLiveVerificationFailed(
        ILogger logger,
        string provider,
        string maskedKey,
        string endpoint,
        string reason);
}
