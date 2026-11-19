using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Synchronous, zero-dependency <see cref="ILoggerFactory"/> and <see cref="ILoggerProvider"/>
/// aligned with <c>Microsoft.Extensions.Logging</c> semantics. Routes all diagnostic logs strictly
/// to the configured <c>stderr</c> <see cref="TextWriter"/>, supports live runtime level/format
/// switching (for REPL <c>/log-level</c> and Settings), structured NDJSON output, logical scopes,
/// and mandatory secret redaction via <see cref="SecretRedactor"/>.
/// </summary>
public sealed class StderrLoggerFactory : ILoggerFactory, ILoggerProvider, ISupportExternalScope
{
    private readonly TextWriter _stderrWriter;
    private readonly SecretRedactor _secretRedactor;
    private readonly TimeProvider _timeProvider;
    private readonly bool _useColor;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, StderrLogger> _loggers = new(StringComparer.Ordinal);
    private readonly List<ILoggerProvider> _additionalProviders = [];
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private volatile int _minimumLevel;
    private volatile int _outputFormat;
    private bool _disposed;

    public StderrLoggerFactory(
        TextWriter stderrWriter,
        LogLevel minimumLevel = CliLogLevelResolver.DefaultLogLevel,
        LogOutputFormat outputFormat = CliLogLevelResolver.DefaultLogFormat,
        SecretRedactor? secretRedactor = null,
        TimeProvider? timeProvider = null,
        bool? enableColor = null)
    {
        _stderrWriter = stderrWriter ?? throw new ArgumentNullException(nameof(stderrWriter));
        _minimumLevel = (int)minimumLevel;
        _outputFormat = (int)outputFormat;
        _secretRedactor = secretRedactor ?? new SecretRedactor();
        _timeProvider = timeProvider ?? TimeProvider.System;

        var noColor = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NO_COLOR"));
        _useColor = enableColor ?? (!noColor && ReferenceEquals(stderrWriter, Console.Error) && !Console.IsErrorRedirected);
    }

    public LogLevel MinimumLevel
    {
        get => (LogLevel)_minimumLevel;
        set => _minimumLevel = (int)value;
    }

    public LogOutputFormat OutputFormat
    {
        get => (LogOutputFormat)_outputFormat;
        set => _outputFormat = (int)value;
    }

    public SecretRedactor Redactor => _secretRedactor;

    public void Reconfigure(LogLevel minimumLevel, LogOutputFormat outputFormat)
    {
        _minimumLevel = (int)minimumLevel;
        _outputFormat = (int)outputFormat;
    }

    public IDisposable PushTemporaryOverride(LogLevel? minimumLevel = null, LogOutputFormat? outputFormat = null)
    {
        var previousLevel = MinimumLevel;
        var previousFormat = OutputFormat;

        if (minimumLevel.HasValue)
        {
            MinimumLevel = minimumLevel.Value;
        }

        if (outputFormat.HasValue)
        {
            OutputFormat = outputFormat.Value;
        }

        return new RestoreScope(this, previousLevel, previousFormat);
    }

    public void AddProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_writeLock)
        {
            _additionalProviders.Add(provider);
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);
        return _loggers.GetOrAdd(categoryName, name => new StderrLogger(this, name));
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider ?? new LoggerExternalScopeProvider();
    }

    internal bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel == LogLevel.None)
        {
            return false;
        }

        var currentMinimum = MinimumLevel;
        return currentMinimum != LogLevel.None && logLevel >= currentMinimum;
    }

    internal IDisposable BeginScope<TState>(TState state) where TState : notnull =>
        _scopeProvider.Push(state);

    internal void WriteLog<TState>(
        string categoryName,
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (_disposed || !IsEnabled(logLevel))
        {
            return;
        }

        var rawMessage = formatter(state, exception);
        if (string.IsNullOrEmpty(rawMessage) && exception is null)
        {
            return;
        }

        var timestamp = _timeProvider.GetUtcNow();
        var redactedMessage = _secretRedactor.Redact(rawMessage);
        var scopes = CollectScopes();

        var outputFormat = OutputFormat;
        if (outputFormat == LogOutputFormat.Json)
        {
            var jsonLine = FormatJsonLine(
                timestamp,
                logLevel,
                categoryName,
                eventId,
                redactedMessage,
                state,
                scopes,
                exception);

            lock (_writeLock)
            {
                _stderrWriter.WriteLine(jsonLine);
            }

            return;
        }

        WriteTextEntry(timestamp, logLevel, categoryName, eventId, redactedMessage, scopes, exception);
    }

    private void WriteTextEntry(
        DateTimeOffset timestamp,
        LogLevel logLevel,
        string categoryName,
        EventId eventId,
        string redactedMessage,
        IReadOnlyList<object?> scopes,
        Exception? exception)
    {
        var shortCategory = ShortenCategoryName(categoryName);
        var levelTag = CliLogLevelResolver.FormatShortTag(logLevel);
        var ts = timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

        var scopeSuffix = FormatTextScopes(scopes);
        var eventSuffix = eventId.Id != 0
            ? string.IsNullOrWhiteSpace(eventId.Name)
                ? $"#{eventId.Id}"
                : $"#{eventId.Id}:{eventId.Name}"
            : string.Empty;

        var prefix = $"[{ts} {levelTag}] [{shortCategory}{eventSuffix}]";
        var body = string.IsNullOrEmpty(scopeSuffix)
            ? $" {redactedMessage}"
            : $" {scopeSuffix} {redactedMessage}";

        string? exceptionLine = null;
        string? stackTrace = null;
        if (exception is not null)
        {
            var redactedExMessage = _secretRedactor.Redact(exception.Message);
            exceptionLine = $"  Exception ({exception.GetType().Name}): {redactedExMessage}";
            if (MinimumLevel <= LogLevel.Debug && !string.IsNullOrWhiteSpace(exception.StackTrace))
            {
                stackTrace = _secretRedactor.Redact(exception.StackTrace);
            }
        }

        lock (_writeLock)
        {
            if (_useColor)
            {
                WriteColoredSegment(prefix, GetTextColor(logLevel));
                _stderrWriter.WriteLine(body);
                if (exceptionLine is not null)
                {
                    WriteColoredSegment(exceptionLine, ConsoleColor.Red);
                    _stderrWriter.WriteLine();
                }

                if (stackTrace is not null)
                {
                    WriteColoredSegment(stackTrace, ConsoleColor.DarkGray);
                    _stderrWriter.WriteLine();
                }
            }
            else
            {
                _stderrWriter.Write(prefix);
                _stderrWriter.WriteLine(body);
                if (exceptionLine is not null)
                {
                    _stderrWriter.WriteLine(exceptionLine);
                }

                if (stackTrace is not null)
                {
                    _stderrWriter.WriteLine(stackTrace);
                }
            }
        }
    }

    private string FormatJsonLine<TState>(
        DateTimeOffset timestamp,
        LogLevel logLevel,
        string categoryName,
        EventId eventId,
        string redactedMessage,
        TState state,
        IReadOnlyList<object?> scopes,
        Exception? exception)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp", timestamp.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("level", logLevel.ToString());
            writer.WriteString("category", categoryName);
            writer.WriteNumber("eventId", eventId.Id);
            if (!string.IsNullOrWhiteSpace(eventId.Name))
            {
                writer.WriteString("eventName", eventId.Name);
            }

            writer.WriteString("message", redactedMessage);

            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                var hasStateProps = false;
                foreach (var kvp in properties)
                {
                    if (string.Equals(kvp.Key, "{OriginalFormat}", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!hasStateProps)
                    {
                        writer.WriteStartObject("state");
                        hasStateProps = true;
                    }

                    WriteJsonValue(writer, kvp.Key, kvp.Value);
                }

                if (hasStateProps)
                {
                    writer.WriteEndObject();
                }
            }

            if (scopes.Count > 0)
            {
                writer.WriteStartArray("scopes");
                foreach (var scope in scopes)
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> scopePairs)
                    {
                        writer.WriteStartObject();
                        foreach (var kvp in scopePairs)
                        {
                            if (string.Equals(kvp.Key, "{OriginalFormat}", StringComparison.Ordinal))
                            {
                                continue;
                            }

                            WriteJsonValue(writer, kvp.Key, kvp.Value);
                        }

                        writer.WriteEndObject();
                    }
                    else
                    {
                        writer.WriteStringValue(_secretRedactor.Redact(scope?.ToString()));
                    }
                }

                writer.WriteEndArray();
            }

            if (exception is not null)
            {
                writer.WriteStartObject("exception");
                writer.WriteString("type", exception.GetType().FullName ?? exception.GetType().Name);
                writer.WriteString("message", _secretRedactor.Redact(exception.Message));
                if (MinimumLevel <= LogLevel.Debug && !string.IsNullOrWhiteSpace(exception.StackTrace))
                {
                    writer.WriteString("stackTrace", _secretRedactor.Redact(exception.StackTrace));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private void WriteJsonValue(Utf8JsonWriter writer, string propertyName, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(propertyName);
                break;
            case bool b:
                writer.WriteBoolean(propertyName, b);
                break;
            case int i:
                writer.WriteNumber(propertyName, i);
                break;
            case long l:
                writer.WriteNumber(propertyName, l);
                break;
            case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                writer.WriteNumber(propertyName, d);
                break;
            case decimal dec:
                writer.WriteNumber(propertyName, dec);
                break;
            default:
                writer.WriteString(propertyName, _secretRedactor.Redact(Convert.ToString(value, CultureInfo.InvariantCulture)));
                break;
        }
    }

    private IReadOnlyList<object?> CollectScopes()
    {
        List<object?>? collected = null;
        _scopeProvider.ForEachScope(static (scope, state) =>
        {
            state.Add(scope);
        }, collected ??= []);

        return collected;
    }

    private string FormatTextScopes(IReadOnlyList<object?> scopes)
    {
        if (scopes.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(scopes.Count);
        foreach (var scope in scopes)
        {
            if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                var kvParts = new List<string>();
                foreach (var kvp in pairs)
                {
                    if (string.Equals(kvp.Key, "{OriginalFormat}", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var rawVal = Convert.ToString(kvp.Value, CultureInfo.InvariantCulture);
                    kvParts.Add($"{kvp.Key}={_secretRedactor.Redact(rawVal)}");
                }

                if (kvParts.Count > 0)
                {
                    parts.Add(string.Join(", ", kvParts));
                }
            }
            else if (scope is not null)
            {
                var redacted = _secretRedactor.Redact(scope.ToString());
                if (!string.IsNullOrWhiteSpace(redacted))
                {
                    parts.Add(redacted);
                }
            }
        }

        return parts.Count == 0 ? string.Empty : $"({string.Join(" | ", parts)})";
    }

    private void WriteColoredSegment(string text, ConsoleColor color)
    {
        try
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            try
            {
                _stderrWriter.Write(text);
            }
            finally
            {
                Console.ForegroundColor = previous;
            }
        }
        catch (IOException)
        {
            _stderrWriter.Write(text);
        }
    }

    private static ConsoleColor GetTextColor(LogLevel level) => level switch
    {
        LogLevel.Trace => ConsoleColor.DarkGray,
        LogLevel.Debug => ConsoleColor.Gray,
        LogLevel.Information => ConsoleColor.Cyan,
        LogLevel.Warning => ConsoleColor.Yellow,
        LogLevel.Error => ConsoleColor.Red,
        LogLevel.Critical => ConsoleColor.DarkRed,
        _ => ConsoleColor.Gray
    };

    internal static string ShortenCategoryName(string categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
        {
            return "App";
        }

        var lastDot = categoryName.LastIndexOf('.');
        return lastDot >= 0 && lastDot < categoryName.Length - 1
            ? categoryName[(lastDot + 1)..]
            : categoryName;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_writeLock)
        {
            foreach (var provider in _additionalProviders)
            {
                provider.Dispose();
            }

            _additionalProviders.Clear();
        }
    }

    private sealed class StderrLogger(StderrLoggerFactory owner, string categoryName) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            owner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) =>
            owner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            owner.WriteLog(categoryName, logLevel, eventId, state, exception, formatter);
        }
    }

    private sealed class RestoreScope(
        StderrLoggerFactory factory,
        LogLevel previousLevel,
        LogOutputFormat previousFormat) : IDisposable
    {
        private bool _restored;

        public void Dispose()
        {
            if (_restored)
            {
                return;
            }

            _restored = true;
            factory.Reconfigure(previousLevel, previousFormat);
        }
    }
}
