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
