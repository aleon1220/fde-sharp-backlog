using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Represents the resolved workspace directories for Smart Backlog Assistant
/// across Windows ("c:\workspace") and WSL/Linux ("/mnt/c/workspace").
/// </summary>
public sealed record WorkspaceDirectories(
    string WorkspaceRoot,
    string ProjectRoot,
    string InputDirectory,
    string OutputDirectory,
    bool IsWindows);

/// <summary>
/// Resolves and manages cross-platform workspace paths for Windows ("c:\workspace")
/// and WSL/Linux ("/mnt/c/workspace"), ensuring "smart-backlog-assistant/in"
/// and "smart-backlog-assistant/out" are ready for reading and writing.
/// </summary>
public sealed partial class WorkspacePathService
{
    public const string DefaultWindowsWorkspaceRoot = @"c:\workspace";
    public const string DefaultWslWorkspaceRoot = "/mnt/c/workspace";
    public const string AppDirectoryName = "smart-backlog-assistant";
    public const string InputFolderName = "in";
    public const string OutputFolderName = "out";
    public const string WorkspaceRootEnvVar = "SBA_WORKSPACE_ROOT";

    private readonly Func<string, string?> _environmentReader;

    public WorkspacePathService(Func<string, string?>? environmentReader = null)
    {
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
    }

    public WorkspaceDirectories Resolve(string? customWorkspaceRoot = null, bool? isWindowsOverride = null)
    {
        var isWindows = isWindowsOverride ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        var rawRoot = !string.IsNullOrWhiteSpace(customWorkspaceRoot)
            ? customWorkspaceRoot.Trim()
            : _environmentReader(WorkspaceRootEnvVar) is { Length: > 0 } envRoot
                ? envRoot.Trim()
                : isWindows
                    ? DefaultWindowsWorkspaceRoot
                    : DefaultWslWorkspaceRoot;

        var normalizedRoot = NormalizePathForOs(rawRoot, isWindows);
        var separator = isWindows ? '\\' : '/';

        var projectRoot = JoinPath(normalizedRoot, AppDirectoryName, separator);
        var inputDir = JoinPath(projectRoot, InputFolderName, separator);
        var outputDir = JoinPath(projectRoot, OutputFolderName, separator);

        return new WorkspaceDirectories(
            WorkspaceRoot: normalizedRoot,
            ProjectRoot: projectRoot,
            InputDirectory: inputDir,
            OutputDirectory: outputDir,
            IsWindows: isWindows);
    }

    /// <summary>
    /// Translates between Windows paths (e.g. "c:\workspace\...") and WSL paths (e.g. "/mnt/c/workspace/...")
    /// based on the active OS platform.
    /// </summary>
    public static string NormalizePathForOs(string path, bool? isWindowsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Workspace path cannot be empty.", nameof(path));
        }

        var isWindows = isWindowsOverride ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var trimmed = path.Trim();

        if (isWindows)
        {
            // Convert WSL "/mnt/<drive>/..." to "<drive>:\..."
            var wslMatch = WslMountRegex().Match(trimmed);
            if (wslMatch.Success)
            {
                var driveLetter = wslMatch.Groups[1].Value.ToLowerInvariant();
                var remainder = wslMatch.Groups[2].Value.Replace('/', '\\');
                return string.IsNullOrEmpty(remainder)
                    ? $"{driveLetter}:\\"
                    : $"{driveLetter}:\\{remainder.TrimEnd('\\')}";
            }

            return trimmed.Replace('/', '\\').TrimEnd('\\');
        }
        else
        {
            // Convert Windows "<drive>:\..." or "<drive>:/..." to WSL "/mnt/<drive>/..."
            var winMatch = WindowsDriveRegex().Match(trimmed);
            if (winMatch.Success)
            {
                var driveLetter = winMatch.Groups[1].Value.ToLowerInvariant();
                var remainder = winMatch.Groups[2].Value.Replace('\\', '/').Trim('/');
                return string.IsNullOrEmpty(remainder)
                    ? $"/mnt/{driveLetter}"
                    : $"/mnt/{driveLetter}/{remainder}";
            }

            return trimmed.Replace('\\', '/').TrimEnd('/');
        }
    }

    public string ResolveCustomOrRelativePath(string path, bool? isWindowsOverride = null)
    {
        return NormalizePathForOs(path, isWindowsOverride);
    }

    /// <summary>
    /// Applies custom input/output directory overrides from persisted <see cref="Domain.CliSettings"/>.
    /// </summary>
    public WorkspaceDirectories ApplySettings(WorkspaceDirectories baseWorkspace, Domain.CliSettings settings)
    {
        ArgumentNullException.ThrowIfNull(baseWorkspace);
        ArgumentNullException.ThrowIfNull(settings);

        var inputDir = string.IsNullOrWhiteSpace(settings.CustomInputDirectory)
            ? baseWorkspace.InputDirectory
            : ResolveConfiguredDirectory(settings.CustomInputDirectory, baseWorkspace.IsWindows);

        var outputDir = string.IsNullOrWhiteSpace(settings.CustomOutputDirectory)
            ? baseWorkspace.OutputDirectory
            : ResolveConfiguredDirectory(settings.CustomOutputDirectory, baseWorkspace.IsWindows);

        return baseWorkspace with
        {
            InputDirectory = inputDir,
            OutputDirectory = outputDir
        };
    }

    /// <summary>
    /// Resolves a process target (<c>all</c>, <c>&lt;filename&gt;</c>, or <c>@filename</c>) against
    /// cwd, workspace <c>in</c> directory, project root, and <c>samples/</c>.
    /// </summary>
    public string ResolveInputTarget(string? target, WorkspaceDirectories workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (string.IsNullOrWhiteSpace(target) ||
            string.Equals(target.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            return workspace.InputDirectory;
        }

        var resolved = TryResolveContextFile(target, workspace);
        if (resolved is not null)
        {
            return resolved;
        }

        var normalized = NormalizePathForOs(target.Trim().TrimStart('@'), workspace.IsWindows);
        if (Directory.Exists(normalized))
        {
            return normalized;
        }

        throw new FileNotFoundException(
            $"Input file '{target.Trim()}' was not found in current directory or '{workspace.InputDirectory}'.",
            target.Trim());
    }

    /// <summary>
    /// Attempts to resolve a file path or <c>@filename</c> mention against cwd, workspace <c>./in</c>,
    /// workspace root, and <c>samples/</c>.
    /// </summary>
    public string? TryResolveContextFile(string? mentionOrPath, WorkspaceDirectories workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(mentionOrPath))
        {
            return null;
        }

        var cleaned = mentionOrPath.Trim();
        if (cleaned.StartsWith('@'))
        {
            cleaned = cleaned[1..].Trim();
        }

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        // Handle explicit "./in/<name>" or "in/<name>" shorthand
        var slashNormalized = cleaned.Replace('\\', '/');
        if (slashNormalized.StartsWith("./in/", StringComparison.OrdinalIgnoreCase))
        {
            var inCandidate = Path.Combine(workspace.InputDirectory, slashNormalized["./in/".Length..]);
            if (File.Exists(inCandidate))
            {
                return ResolveExactDiskCasing(inCandidate);
            }
        }
        else if (slashNormalized.StartsWith("in/", StringComparison.OrdinalIgnoreCase))
        {
            var inCandidate = Path.Combine(workspace.InputDirectory, slashNormalized["in/".Length..]);
            if (File.Exists(inCandidate))
            {
                return ResolveExactDiskCasing(inCandidate);
            }
        }

        var osNormalized = NormalizePathForOs(cleaned, workspace.IsWindows);
        if (File.Exists(osNormalized))
        {
            return ResolveExactDiskCasing(osNormalized);
        }

        if (Directory.Exists(osNormalized))
        {
            return osNormalized;
        }

        if (File.Exists(cleaned))
        {
            return ResolveExactDiskCasing(cleaned);
        }

        if (Directory.Exists(cleaned))
        {
            return cleaned;
        }

        var candidates = new[]
        {
            Path.Combine(workspace.InputDirectory, osNormalized),
            Path.Combine(workspace.ProjectRoot, osNormalized),
            Path.Combine("samples", Path.GetFileName(osNormalized))
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return ResolveExactDiskCasing(candidate);
            }
        }

        // Case-insensitive filename match inside workspace InputDirectory
        var requestedName = Path.GetFileName(osNormalized);
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            var match = GetInputFiles(workspace.InputDirectory)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), requestedName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static string ResolveExactDiskCasing(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName) || !Directory.Exists(directory))
        {
            return filePath;
        }

        var exactMatch = Directory
            .EnumerateFiles(directory)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));

        return exactMatch ?? filePath;
    }

    public static string FormatCompactLabel(string activePath, string defaultDirectory, string defaultLabel)
    {
        if (string.IsNullOrWhiteSpace(activePath))
        {
            return defaultLabel;
        }

        if (string.Equals(
            activePath.TrimEnd('\\', '/'),
            defaultDirectory.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase))
        {
            return defaultLabel;
        }

        var normalized = activePath.Replace('\\', '/').TrimEnd('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? $".../{parts[^2]}/{parts[^1]}"
            : normalized;
    }

    public static bool IsWslEnvironment()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_INTEROP")))
        {
            return true;
        }

        try
        {
            const string procVersion = "/proc/version";
            if (File.Exists(procVersion))
            {
                var content = File.ReadAllText(procVersion);
                return content.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                       content.Contains("WSL", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (IOException)
        {
            // Ignore restricted procfs
        }

        return false;
    }

    private string ResolveConfiguredDirectory(string path, bool isWindows)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith("./", StringComparison.Ordinal) ||
            trimmed.StartsWith("../", StringComparison.Ordinal) ||
            trimmed.StartsWith(".\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("..\\", StringComparison.Ordinal))
        {
            return NormalizePathForOs(Path.GetFullPath(trimmed), isWindows);
        }

        return NormalizePathForOs(trimmed, isWindows);
    }

    /// <summary>
    /// Ensures the "smart-backlog-assistant/in" and "smart-backlog-assistant/out" directories exist,
    /// optionally seeding a default discovery note if the "in" directory is empty.
    /// </summary>
    public void EnsureDirectoriesExist(WorkspaceDirectories directories, string? seedSampleFilePath = "samples/raw-notes.txt")
    {
        ArgumentNullException.ThrowIfNull(directories);

        Directory.CreateDirectory(directories.InputDirectory);
        Directory.CreateDirectory(directories.OutputDirectory);

        var existingFiles = GetInputFiles(directories.InputDirectory);
        if (existingFiles.Count == 0 && !string.IsNullOrWhiteSpace(seedSampleFilePath) && File.Exists(seedSampleFilePath))
        {
            var fileName = Path.GetFileName(seedSampleFilePath);
            var targetPath = Path.Combine(directories.InputDirectory, fileName);
            File.Copy(seedSampleFilePath, targetPath, overwrite: false);
        }
    }

    /// <summary>
    /// Lists all supported raw discovery files (.txt, .md) inside the input directory.
    /// </summary>
    public IReadOnlyList<string> GetInputFiles(string inputDirectory)
    {
        if (string.IsNullOrWhiteSpace(inputDirectory) || !Directory.Exists(inputDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(inputDirectory)
            .Where(file =>
                file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Generates a safe, lowercase file slug from a ticket title or goal.
    /// </summary>
    public static string CreateSlug(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "backlog-item";
        }

        var slug = NonAlphanumericRegex().Replace(title.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "backlog-item" : slug;
    }

    private static string JoinPath(string basePath, string segment, char separator)
    {
        return $"{basePath.TrimEnd('\\', '/')}{separator}{segment.Trim('\\', '/')}";
    }

    [GeneratedRegex(@"^/mnt/([a-zA-Z])(?:/(.*))?$")]
    private static partial Regex WslMountRegex();

    [GeneratedRegex(@"^([a-zA-Z]):[\\/](.*)$")]
    private static partial Regex WindowsDriveRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumericRegex();
}
