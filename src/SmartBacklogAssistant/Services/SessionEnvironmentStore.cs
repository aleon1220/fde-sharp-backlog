using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Provides unified, thread-safe runtime environment variable resolution, session-scoped mutation,
/// and optional OS User environment persistence for AI provider credentials.
/// </summary>
public sealed class SessionEnvironmentStore
{
    private readonly object _lock = new();
    private readonly Func<string, string?>? _externalReader;
    private readonly Action<string, string?, EnvironmentVariableTarget>? _externalScopedWriter;
    private readonly Func<string, EnvironmentVariableTarget, string?>? _externalScopedReader;
    private readonly bool _isWindows;
    private readonly Dictionary<string, string> _sessionOverrides = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sessionCleared = new(StringComparer.Ordinal);

    public SessionEnvironmentStore(
        Func<string, string?>? externalReader = null,
        Action<string, string?, EnvironmentVariableTarget>? externalScopedWriter = null,
        Func<string, EnvironmentVariableTarget, string?>? externalScopedReader = null,
        bool? isWindowsOverride = null)
    {
        _externalReader = externalReader;
        _externalScopedWriter = externalScopedWriter;
        _externalScopedReader = externalScopedReader;
        _isWindows = isWindowsOverride ?? OperatingSystem.IsWindows();
    }

    public bool IsWindowsHost => _isWindows;

    /// <summary>
    /// Reads the effective runtime value of an environment variable, honoring active session
    /// overrides and explicit session clears ahead of OS or injected environment sources.
    /// </summary>
    public string? Read(string variableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);

        lock (_lock)
        {
            if (_sessionCleared.Contains(variableName))
            {
                return null;
            }

            if (_sessionOverrides.TryGetValue(variableName, out var sessionValue))
            {
                return sessionValue;
            }
        }

        if (_externalReader is not null)
        {
            return _externalReader(variableName);
        }

        if (_externalScopedReader is not null)
        {
            var proc = _externalScopedReader(variableName, EnvironmentVariableTarget.Process);
            if (!string.IsNullOrWhiteSpace(proc))
            {
                return proc;
            }

            if (_isWindows)
            {
                var usr = _externalScopedReader(variableName, EnvironmentVariableTarget.User);
                if (!string.IsNullOrWhiteSpace(usr))
                {
                    return usr;
                }

                var mach = _externalScopedReader(variableName, EnvironmentVariableTarget.Machine);
                if (!string.IsNullOrWhiteSpace(mach))
                {
                    return mach;
                }
            }

            return null;
        }

        var processValue = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Process);
        if (!string.IsNullOrWhiteSpace(processValue))
        {
            return processValue;
        }

        if (_isWindows)
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

    /// <summary>
    /// Reads a variable from a specific environment scope without falling back across scopes.
    /// </summary>
    public string? ReadFromScope(string variableName, EnvironmentVariableTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);

        if (target == EnvironmentVariableTarget.Process)
        {
            lock (_lock)
            {
                if (_sessionCleared.Contains(variableName))
                {
                    return null;
                }

                if (_sessionOverrides.TryGetValue(variableName, out var sessionValue))
                {
                    return sessionValue;
                }
            }

            if (_externalScopedReader is not null)
            {
                return _externalScopedReader(variableName, EnvironmentVariableTarget.Process);
            }

            if (_externalReader is not null)
            {
                return _externalReader(variableName);
            }

            return Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Process);
        }

        if (_externalScopedReader is not null)
        {
            return _externalScopedReader(variableName, target);
        }

        if (!_isWindows || _externalReader is not null)
        {
            return null;
        }

        return Environment.GetEnvironmentVariable(variableName, target);
    }

    /// <summary>
    /// Injects a variable into the active session (<see cref="EnvironmentVariableTarget.Process"/>)
    /// and optionally persists it to <see cref="EnvironmentVariableTarget.User"/> when supported.
    /// Returns <see langword="true"/> if OS User persistence succeeded.
    /// </summary>
    public bool Write(string variableName, string value, ApiKeyStorageScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);
        ArgumentNullException.ThrowIfNull(value);

        lock (_lock)
        {
            _sessionCleared.Remove(variableName);
            if (_externalScopedWriter is null && _externalReader is not null)
            {
                _sessionOverrides[variableName] = value;
            }
            else
            {
                _sessionOverrides.Remove(variableName);
            }
        }

        if (_externalScopedWriter is not null)
        {
            _externalScopedWriter(variableName, value, EnvironmentVariableTarget.Process);
            if (scope == ApiKeyStorageScope.User)
            {
                _externalScopedWriter(variableName, value, EnvironmentVariableTarget.User);
                return true;
            }

            return false;
        }

        if (_externalReader is null)
        {
            Environment.SetEnvironmentVariable(variableName, value, EnvironmentVariableTarget.Process);
            if (scope == ApiKeyStorageScope.User && _isWindows)
            {
                Environment.SetEnvironmentVariable(variableName, value, EnvironmentVariableTarget.User);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Convenience writer for single-target process updates (compatible with <see cref="GeminiSecretProvider"/>).
    /// </summary>
    public void WriteProcess(string variableName, string value) =>
        _ = Write(variableName, value, ApiKeyStorageScope.Session);

    /// <summary>
    /// Clears a variable from the active session and optionally from OS User scope.
    /// Returns <see langword="true"/> if OS User scope was also cleared.
    /// </summary>
    public bool Clear(string variableName, ApiKeyStorageScope scope, bool clearUserTarget = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);

        lock (_lock)
        {
            _sessionOverrides.Remove(variableName);
            _sessionCleared.Add(variableName);
        }

        if (_externalScopedWriter is not null)
        {
            _externalScopedWriter(variableName, null, EnvironmentVariableTarget.Process);
            if (clearUserTarget && scope is ApiKeyStorageScope.User or ApiKeyStorageScope.All)
            {
                _externalScopedWriter(variableName, null, EnvironmentVariableTarget.User);
                return true;
            }

            return false;
        }

        if (_externalReader is null)
        {
            Environment.SetEnvironmentVariable(variableName, null, EnvironmentVariableTarget.Process);
            if (clearUserTarget && scope is ApiKeyStorageScope.User or ApiKeyStorageScope.All && _isWindows)
            {
                Environment.SetEnvironmentVariable(variableName, null, EnvironmentVariableTarget.User);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Describes the active resolution scope for a configured variable.
    /// </summary>
    public (string ScopeSummary, bool IsSessionOverride, bool IsPersistedInUserEnv) DescribeVariableScope(string variableName)
    {
        lock (_lock)
        {
            if (_sessionCleared.Contains(variableName))
            {
                return ("Cleared in Session", false, false);
            }

            if (_sessionOverrides.ContainsKey(variableName))
            {
                return ("Session (In-Memory)", true, false);
            }
        }

        var hasProcess = !string.IsNullOrWhiteSpace(ReadFromScope(variableName, EnvironmentVariableTarget.Process));
        var hasUser = !string.IsNullOrWhiteSpace(ReadFromScope(variableName, EnvironmentVariableTarget.User));
        var hasMachine = !string.IsNullOrWhiteSpace(ReadFromScope(variableName, EnvironmentVariableTarget.Machine));

        if (hasProcess && hasUser)
        {
            return ("Session + OS User Env", false, true);
        }

        if (hasProcess)
        {
            return ("Session (Process Env)", false, false);
        }

        if (hasUser)
        {
            return ("OS User Env", false, true);
        }

        if (hasMachine)
        {
            return ("OS Machine Env", false, false);
        }

        return ("Not set", false, false);
    }
}
