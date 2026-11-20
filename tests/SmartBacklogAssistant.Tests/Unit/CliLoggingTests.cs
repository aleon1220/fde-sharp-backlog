using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;
using SmartBacklogAssistant.Workflow;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class CliLoggingTests
{
    [Theory]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("TRC", LogLevel.Trace)]
    [InlineData("0", LogLevel.Trace)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("verbose", LogLevel.Debug)]
    [InlineData("dbg", LogLevel.Debug)]
    [InlineData("information", LogLevel.Information)]
    [InlineData("info", LogLevel.Information)]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("err", LogLevel.Error)]
    [InlineData("critical", LogLevel.Critical)]
    [InlineData("fatal", LogLevel.Critical)]
    [InlineData("none", LogLevel.None)]
    [InlineData("quiet", LogLevel.None)]
    [InlineData("off", LogLevel.None)]
    [InlineData("6", LogLevel.None)]
    public void TryParseLogLevel_SupportsAllStandardLevelsAndAliases(string token, LogLevel expected)
    {
        var ok = CliLogLevelResolver.TryParseLogLevel(token, out var parsed);

        Assert.True(ok);
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("noisy")]
    [InlineData("all")]
    [InlineData("")]
    public void ParseLogLevelOrThrow_RejectsInvalidValuesWithActionableMessage(string invalid)
    {
        var ex = Assert.Throws<ArgumentException>(() => CliLogLevelResolver.ParseLogLevelOrThrow(invalid, "--log-level"));
        Assert.Contains("Invalid log level", ex.Message);
        Assert.Contains("Trace", ex.Message);
        Assert.Contains("None", ex.Message);
    }

    [Theory]
    [InlineData("text", LogOutputFormat.Text)]
    [InlineData("plain", LogOutputFormat.Text)]
    [InlineData("json", LogOutputFormat.Json)]
    [InlineData("ndjson", LogOutputFormat.Json)]
    [InlineData("jsonl", LogOutputFormat.Json)]
    public void TryParseLogFormat_SupportsTextAndJsonAliases(string token, LogOutputFormat expected)
    {
        var ok = CliLogLevelResolver.TryParseLogFormat(token, out var parsed);

        Assert.True(ok);
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void ResolveEffectiveLogging_EnforcesStrictPrecedenceChain()
    {
        var baseOptions = new WorkflowOptions("in.txt", "prompt.md");
        var persisted = new CliSettings(LogLevel: LogLevel.Error, LogFormat: LogOutputFormat.Json);
        var env = new Dictionary<string, string?>
        {
            [CliLogLevelResolver.LogLevelEnvVar] = "information",
            [CliLogLevelResolver.LogFormatEnvVar] = "text"
        };
        Func<string, string?> readEnv = k => env.TryGetValue(k, out var v) ? v : null;

        // 1. Default when nothing set
        var def = CliLogLevelResolver.ResolveEffectiveLogging(baseOptions, new CliSettings(), _ => null);
        Assert.Equal(LogLevel.Warning, def.Level);
        Assert.Equal("default", def.LevelSource);
        Assert.Equal(LogOutputFormat.Text, def.Format);
        Assert.Equal("default", def.FormatSource);

        // 2. settings.json beats default
        var fromSettings = CliLogLevelResolver.ResolveEffectiveLogging(baseOptions, persisted, _ => null);
        Assert.Equal(LogLevel.Error, fromSettings.Level);
        Assert.Equal("settings.json", fromSettings.LevelSource);
        Assert.Equal(LogOutputFormat.Json, fromSettings.Format);
        Assert.Equal("settings.json", fromSettings.FormatSource);

        // 3. Environment variable beats settings.json
        var fromEnv = CliLogLevelResolver.ResolveEffectiveLogging(baseOptions, persisted, readEnv);
        Assert.Equal(LogLevel.Information, fromEnv.Level);
        Assert.Equal("Env SBA_LOG_LEVEL", fromEnv.LevelSource);
        Assert.Equal(LogOutputFormat.Text, fromEnv.Format);
        Assert.Equal("Env SBA_LOG_FORMAT", fromEnv.FormatSource);

        // 4. --verbose / --quiet beats environment variable
        var fromVerbose = CliLogLevelResolver.ResolveEffectiveLogging(baseOptions with { Verbose = true }, persisted, readEnv);
        Assert.Equal(LogLevel.Debug, fromVerbose.Level);
        Assert.Equal("CLI --verbose", fromVerbose.LevelSource);

        var fromQuiet = CliLogLevelResolver.ResolveEffectiveLogging(baseOptions with { Quiet = true }, persisted, readEnv);
        Assert.Equal(LogLevel.None, fromQuiet.Level);
        Assert.Equal("CLI --quiet", fromQuiet.LevelSource);

        // 5. Explicit --log-level / --log-format beats everything (even --verbose and --quiet)
        var fromExplicit = CliLogLevelResolver.ResolveEffectiveLogging(
            baseOptions with { Verbose = true, Quiet = true, LogLevelInput = "trace", LogFormatInput = "json" },
            persisted,
            readEnv);
        Assert.Equal(LogLevel.Trace, fromExplicit.Level);
        Assert.Equal("CLI --log-level", fromExplicit.LevelSource);
        Assert.Equal(LogOutputFormat.Json, fromExplicit.Format);
        Assert.Equal("CLI --log-format", fromExplicit.FormatSource);

        // 6. Conflicting --verbose and --quiet without --log-level throws
        Assert.Throws<ArgumentException>(() =>
            CliLogLevelResolver.ResolveEffectiveLogging(baseOptions with { Verbose = true, Quiet = true }, persisted, readEnv));
    }

    [Fact]
    public void SecretRedactor_MasksGeminiGitHubAnthropicBearerHeadersAndRegisteredSecrets()
    {
        var redactor = new SecretRedactor();
        redactor.RegisterSecret("custom-super-secret-value-9876");

        var raw =
            "Gemini=AIzaSySecretGeminiKey123456 " +
            "GH=ghp_1234567890abcdef1234 " +
            "PAT=github_pat_11ABCDEF12345678901234 " +
            "Claude=sk-ant-api03-secretvalue7788 " +
            "Authorization: Bearer tok_live_abcde4321 " +
            "x-goog-api-key: rawHeaderSecret5566 " +
            "https://example.test/v1?key=querySecret9900 " +
            "Custom=custom-super-secret-value-9876";

        var redacted = redactor.Redact(raw);

        Assert.DoesNotContain("AIzaSySecretGeminiKey123456", redacted);
        Assert.DoesNotContain("ghp_1234567890abcdef1234", redacted);
        Assert.DoesNotContain("github_pat_11ABCDEF12345678901234", redacted);
        Assert.DoesNotContain("sk-ant-api03-secretvalue7788", redacted);
        Assert.DoesNotContain("tok_live_abcde4321", redacted);
        Assert.DoesNotContain("rawHeaderSecret5566", redacted);
        Assert.DoesNotContain("querySecret9900", redacted);
        Assert.DoesNotContain("custom-super-secret-value-9876", redacted);

        Assert.Contains("****3456", redacted);
        Assert.Contains("****1234", redacted);
        Assert.Contains("****7788", redacted);
        Assert.Contains("****4321", redacted);
        Assert.Contains("****5566", redacted);
        Assert.Contains("****9900", redacted);
        Assert.Contains("****9876", redacted);
    }

    [Theory]
    [InlineData(LogLevel.Trace, 6)]
    [InlineData(LogLevel.Debug, 5)]
    [InlineData(LogLevel.Information, 4)]
    [InlineData(LogLevel.Warning, 3)]
    [InlineData(LogLevel.Error, 2)]
    [InlineData(LogLevel.Critical, 1)]
    [InlineData(LogLevel.None, 0)]
    public void StderrLoggerFactory_FiltersEventsStrictlyAtOrAboveConfiguredSeverity(
        LogLevel configuredLevel,
        int expectedEmittedCount)
    {
        using var stderr = new StringWriter();
        using var factory = new StderrLoggerFactory(
            stderr,
            minimumLevel: configuredLevel,
            outputFormat: LogOutputFormat.Text,
            timeProvider: new FixedTimeProvider(new DateTimeOffset(2026, 11, 19, 14, 30, 15, 123, TimeSpan.Zero)),
            enableColor: false);

        var logger = factory.CreateLogger("SmartBacklogAssistant.Tests.FilterProbe");
        logger.LogTrace("msg-trace");
        logger.LogDebug("msg-debug");
        logger.LogInformation("msg-info");
        logger.LogWarning("msg-warn");
        logger.LogError("msg-error");
        logger.LogCritical("msg-critical");

        var lines = stderr.ToString()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(expectedEmittedCount, lines.Length);
        if (expectedEmittedCount > 0)
        {
            Assert.All(lines, line => Assert.StartsWith("[14:30:15.123 ", line));
            Assert.All(lines, line => Assert.Contains("[FilterProbe]", line));
        }
    }

    [Fact]
    public void StderrLoggerFactory_JsonFormat_EmitsValidNdjsonWithScopesStateExceptionAndSecretRedaction()
    {
        using var stderr = new StringWriter();
        var redactor = new SecretRedactor();
        redactor.RegisterSecret("AIzaSyJsonSecretKey4242");
        using var factory = new StderrLoggerFactory(
            stderr,
            minimumLevel: LogLevel.Debug,
            outputFormat: LogOutputFormat.Json,
            secretRedactor: redactor,
            timeProvider: new FixedTimeProvider(new DateTimeOffset(2026, 11, 19, 14, 30, 15, 500, TimeSpan.Zero)),
            enableColor: false);

        var logger = factory.CreateLogger("SmartBacklogAssistant.Services.Probe");
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["provider"] = "gemini",
            ["token"] = "AIzaSyJsonSecretKey4242"
        }))
        {
            logger.LogError(
                new EventId(4005, "GeminiHttpError"),
                new InvalidOperationException("Failed with key AIzaSyJsonSecretKey4242"),
                "Provider {Provider} rejected {ApiKey}",
                "gemini",
                "AIzaSyJsonSecretKey4242");
        }

        var rawOutput = stderr.ToString().Trim();
        Assert.DoesNotContain("AIzaSyJsonSecretKey4242", rawOutput);

        using var doc = JsonDocument.Parse(rawOutput);
        var root = doc.RootElement;
        Assert.Equal("2026-11-19T14:30:15.5000000+00:00", root.GetProperty("timestamp").GetString());
        Assert.Equal("Error", root.GetProperty("level").GetString());
        Assert.Equal("SmartBacklogAssistant.Services.Probe", root.GetProperty("category").GetString());
        Assert.Equal(4005, root.GetProperty("eventId").GetInt32());
        Assert.Equal("GeminiHttpError", root.GetProperty("eventName").GetString());
        Assert.Contains("****4242", root.GetProperty("message").GetString());
        Assert.Equal("****4242", root.GetProperty("state").GetProperty("ApiKey").GetString());
        Assert.Equal("****4242", root.GetProperty("scopes")[0].GetProperty("token").GetString());
        Assert.Contains("****4242", root.GetProperty("exception").GetProperty("message").GetString());
    }

    [Fact]
    public void StderrLoggerFactory_LiveReconfigureAndTemporaryOverride_UpdateExistingLoggersImmediately()
    {
        using var stderr = new StringWriter();
        using var factory = new StderrLoggerFactory(stderr, LogLevel.Warning, LogOutputFormat.Text, enableColor: false);
        var logger = factory.CreateLogger("SmartBacklogAssistant.Dynamic");

        logger.LogDebug("suppressed-at-warning");
        Assert.Empty(stderr.ToString());

        factory.Reconfigure(LogLevel.Debug, LogOutputFormat.Text);
        logger.LogDebug("visible-at-debug");
        Assert.Contains("visible-at-debug", stderr.ToString());

        using (factory.PushTemporaryOverride(LogLevel.None))
        {
            logger.LogCritical("suppressed-during-override");
            Assert.DoesNotContain("suppressed-during-override", stderr.ToString());
        }

        Assert.Equal(LogLevel.Debug, factory.MinimumLevel);
        logger.LogInformation("restored-after-override");
        Assert.Contains("restored-after-override", stderr.ToString());
    }

    [Fact]
    public void CopilotGateway_CreateClientWithStderrLoggerFactory_SucceedsAcrossTcpStdioAndInProcess()
    {
        using var stderr = new StringWriter();
        using var factory = new StderrLoggerFactory(stderr, LogLevel.Debug, LogOutputFormat.Text, enableColor: false);
        var gateway = new GitHubCopilotSdkGateway(environmentReader: _ => null, loggerFactory: factory);

        using var tcpClient = gateway.CreateClient(
            gitHubToken: "gho_testtoken1234",
            workingDirectory: null,
            new CopilotRuntimeDiagnostics(true, "remote-tcp", "localhost:4321", true, false, "TCP"));
        using var stdioClient = gateway.CreateClient(
            gitHubToken: "gho_testtoken1234",
            workingDirectory: null,
            new CopilotRuntimeDiagnostics(true, "stdio-explicit", "/usr/local/bin/copilot", true, false, "stdio"));
        using var inProcessClient = gateway.CreateClient(
            gitHubToken: null,
            workingDirectory: null,
            new CopilotRuntimeDiagnostics(true, "inprocess-bundled", "/tmp/libcopilot_runtime.so", false, false, "inprocess"));

        Assert.NotNull(tcpClient);
        Assert.NotNull(stdioClient);
        Assert.NotNull(inProcessClient);
        Assert.Equal("all", CliLogLevelResolver.MapToCopilotLogLevel(LogLevel.Trace).ToString());
        Assert.Equal("debug", CliLogLevelResolver.MapToCopilotLogLevel(LogLevel.Debug).ToString());
        Assert.Equal("info", CliLogLevelResolver.MapToCopilotLogLevel(LogLevel.Information).ToString());
        Assert.Equal("warning", CliLogLevelResolver.MapToCopilotLogLevel(LogLevel.Warning).ToString());
        Assert.Equal("error", CliLogLevelResolver.MapToCopilotLogLevel(LogLevel.Error).ToString());
        Assert.Equal("none", CliLogLevelResolver.MapToCopilotLogLevel(LogLevel.None).ToString());
    }

    [Fact]
    public void SecretRedactor_AndOpenAiGatewayClient_MaskOpenAiApiKeyAndLogHttpLifecycle()
    {
        var env = new Dictionary<string, string?>
        {
            [OpenAiGatewayClient.ApiKeyEnvVar] = "sk-openai-live-secret-9988"
        };
        var redactor = new SecretRedactor();
        redactor.RegisterEnvironmentSecrets(k => env.TryGetValue(k, out var val) ? val : null);

        var redacted = redactor.Redact("Authorization failed for key sk-openai-live-secret-9988");
        Assert.DoesNotContain("sk-openai-live-secret-9988", redacted);
        Assert.Contains("****9988", redacted);

        using var stderr = new StringWriter();
        using var factory = new StderrLoggerFactory(stderr, LogLevel.Debug, LogOutputFormat.Text, enableColor: false, secretRedactor: redactor);
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"content":"{\"title\":\"Logged\",\"description\":\"Safe\"}"}}]}""", System.Text.Encoding.UTF8, "application/json")
        });

        var client = new OpenAiGatewayClient(new HttpClient(handler), factory.CreateLogger<OpenAiGatewayClient>());
        _ = client.GenerateContent(new OpenAiGatewayGenerationRequest(
            ModelId: "gpt-4o",
            Prompt: "Return JSON",
            ApiKey: "sk-openai-live-secret-9988",
            BaseUrl: "https://gateway.internal/v1"));

        var logOutput = stderr.ToString();
        Assert.Contains("POST", logOutput);
        Assert.Contains("https://gateway.internal/v1/chat/completions", logOutput);
        Assert.DoesNotContain("sk-openai-live-secret-9988", logOutput);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request, cancellationToken));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
