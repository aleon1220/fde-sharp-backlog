using Microsoft.Copilot;
using Microsoft.Extensions.Logging;
using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Workflow;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Captures the effective logging configuration and where each setting originated.
/// </summary>
public sealed record ResolvedLoggingConfig(
    LogLevel Level,
    string LevelSource,
    LogOutputFormat Format,
    string FormatSource);

/// <summary>
/// Parses, validates, formats, and resolves <see cref="LogLevel"/> and <see cref="LogOutputFormat"/>
/// across CLI flags, environment variables (<c>SBA_LOG_LEVEL</c>, <c>SBA_LOG_FORMAT</c>),
/// and persisted <c>settings.json</c> using deterministic precedence.
/// </summary>
public static class CliLogLevelResolver
{
    public const string LogLevelEnvVar = "SBA_LOG_LEVEL";
    public const string LogFormatEnvVar = "SBA_LOG_FORMAT";
    public const LogLevel DefaultLogLevel = LogLevel.Warning;
    public const LogOutputFormat DefaultLogFormat = LogOutputFormat.Text;

    public const string ValidLevelsDisplay = "Trace, Debug, Information, Warning, Error, Critical, None (alias: Quiet)";
    public const string ValidFormatsDisplay = "text, json";

    public static bool TryParseLogLevel(string? token, out LogLevel level)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            level = DefaultLogLevel;
            return false;
        }

        switch (token.Trim().ToLowerInvariant())
        {
            case "trace" or "trce" or "trc" or "0":
                level = LogLevel.Trace;
                return true;
            case "debug" or "dbug" or "dbg" or "verbose" or "1":
                level = LogLevel.Debug;
                return true;
            case "information" or "info" or "inf" or "2":
                level = LogLevel.Information;
                return true;
            case "warning" or "warn" or "wrn" or "3":
                level = LogLevel.Warning;
                return true;
            case "error" or "err" or "fail" or "4":
                level = LogLevel.Error;
                return true;
            case "critical" or "crit" or "crt" or "fatal" or "5":
                level = LogLevel.Critical;
                return true;
            case "none" or "quiet" or "off" or "silent" or "6":
                level = LogLevel.None;
                return true;
            default:
                level = DefaultLogLevel;
                return false;
        }
    }

    public static LogLevel ParseLogLevelOrThrow(string? token, string sourceName)
    {
        if (TryParseLogLevel(token, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException(
            $"Invalid log level '{token}' from {sourceName}. Valid values: {ValidLevelsDisplay}.");
    }

    public static bool TryParseLogFormat(string? token, out LogOutputFormat format)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            format = DefaultLogFormat;
            return false;
        }

        switch (token.Trim().ToLowerInvariant())
        {
            case "text" or "console" or "plain" or "human":
                format = LogOutputFormat.Text;
                return true;
            case "json" or "ndjson" or "jsonl" or "structured":
                format = LogOutputFormat.Json;
                return true;
            default:
                format = DefaultLogFormat;
                return false;
        }
    }

    public static LogOutputFormat ParseLogFormatOrThrow(string? token, string sourceName)
    {
        if (TryParseLogFormat(token, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException(
            $"Invalid log format '{token}' from {sourceName}. Valid values: {ValidFormatsDisplay}.");
    }

    /// <summary>
    /// Resolves effective logging configuration using strict precedence:
    /// <list type="number">
    /// <item><description>Explicit CLI flag (<c>--log-level</c> / <c>--log-format</c>)</description></item>
    /// <item><description>Shortcut CLI flags (<c>--verbose</c>/<c>-V</c> → <see cref="LogLevel.Debug"/>; <c>--quiet</c>/<c>-q</c> → <see cref="LogLevel.None"/>)</description></item>
    /// <item><description>OS Environment variable (<c>SBA_LOG_LEVEL</c> / <c>SBA_LOG_FORMAT</c>)</description></item>
    /// <item><description>Workspace <c>settings.json</c> (<c>logLevel</c> / <c>logFormat</c>)</description></item>
    /// <item><description>Default (<see cref="LogLevel.Warning"/> / <see cref="LogOutputFormat.Text"/>)</description></item>
    /// </list>
    /// </summary>
    public static ResolvedLoggingConfig ResolveEffectiveLogging(
        WorkflowOptions options,
        CliSettings? settings = null,
        Func<string, string?>? environmentReader = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var readEnv = environmentReader ?? Environment.GetEnvironmentVariable;

        LogLevel resolvedLevel;
        string levelSource;

        if (!string.IsNullOrWhiteSpace(options.LogLevelInput))
        {
            resolvedLevel = ParseLogLevelOrThrow(options.LogLevelInput, "--log-level");
            levelSource = "CLI --log-level";
        }
        else if (options.Verbose && options.Quiet)
        {
            throw new ArgumentException(
                "Cannot combine '--verbose' (-V) and '--quiet' (-q) without an explicit '--log-level <level>'.");
        }
        else if (options.Quiet)
        {
            resolvedLevel = LogLevel.None;
            levelSource = "CLI --quiet";
        }
        else if (options.Verbose)
        {
            resolvedLevel = LogLevel.Debug;
            levelSource = "CLI --verbose";
        }
        else
        {
            var envLevel = readEnv(LogLevelEnvVar);
            if (!string.IsNullOrWhiteSpace(envLevel))
            {
                resolvedLevel = ParseLogLevelOrThrow(envLevel, LogLevelEnvVar);
                levelSource = $"Env {LogLevelEnvVar}";
            }
            else if (settings?.LogLevel is { } persistedLevel && Enum.IsDefined(persistedLevel))
            {
                resolvedLevel = persistedLevel;
                levelSource = "settings.json";
            }
            else
            {
                resolvedLevel = DefaultLogLevel;
                levelSource = "default";
            }
        }

        LogOutputFormat resolvedFormat;
        string formatSource;

        if (!string.IsNullOrWhiteSpace(options.LogFormatInput))
        {
            resolvedFormat = ParseLogFormatOrThrow(options.LogFormatInput, "--log-format");
            formatSource = "CLI --log-format";
        }
        else
        {
            var envFormat = readEnv(LogFormatEnvVar);
            if (!string.IsNullOrWhiteSpace(envFormat))
            {
                resolvedFormat = ParseLogFormatOrThrow(envFormat, LogFormatEnvVar);
                formatSource = $"Env {LogFormatEnvVar}";
            }
            else if (settings?.LogFormat is { } persistedFormat && Enum.IsDefined(persistedFormat))
            {
                resolvedFormat = persistedFormat;
                formatSource = "settings.json";
            }
            else
            {
                resolvedFormat = DefaultLogFormat;
                formatSource = "default";
            }
        }

        return new ResolvedLoggingConfig(resolvedLevel, levelSource, resolvedFormat, formatSource);
    }

    public static string FormatLogLevelDisplay(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Information",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Critical",
        LogLevel.None => "None (Quiet)",
        _ => level.ToString()
    };

    public static string FormatLogLevelCode(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "information",
        LogLevel.Warning => "warning",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        LogLevel.None => "none",
        _ => level.ToString().ToLowerInvariant()
    };

    public static string FormatShortTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "NON"
    };

    public static string FormatLogFormatCode(LogOutputFormat format) => format switch
    {
        LogOutputFormat.Json => "json",
        _ => "text"
    };

    public static CopilotLogLevel MapToCopilotLogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => CopilotLogLevel.All,
        LogLevel.Debug => CopilotLogLevel.Debug,
        LogLevel.Information => CopilotLogLevel.Info,
        LogLevel.Warning => CopilotLogLevel.Warning,
        LogLevel.Error or LogLevel.Critical => CopilotLogLevel.Error,
        _ => CopilotLogLevel.None
    };

    public static ConsoleColor GetLogLevelColor(LogLevel level) => level switch
    {
        LogLevel.Trace => ConsoleColor.DarkGray,
        LogLevel.Debug => ConsoleColor.Cyan,
        LogLevel.Information => ConsoleColor.Green,
        LogLevel.Warning => ConsoleColor.Yellow,
        LogLevel.Error or LogLevel.Critical => ConsoleColor.Red,
        LogLevel.None => ConsoleColor.DarkGray,
        _ => ConsoleColor.Gray
    };
}
