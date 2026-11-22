using System.Reflection;
using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Loads a pre-designed prompt from a Markdown file (or embedded binary resource fallback)
/// and populates it with collected raw data.
/// </summary>
public sealed class PromptTemplateService
{
    public const string RawDataPlaceholder = "{{RAW_DATA}}";
    public const string DefaultPromptFilePath = "prompts/as-is-v1-prompt.md";
    public const string Type01PromptFilePath = "prompts/prompt-goal-type01.md";
    public const string Type02PromptFilePath = "prompts/prompt-goal-type02.md";
    public const string GoalExpandPromptFilePath = "prompts/prompt-goal-expand.md";
    private const string EmbeddedPromptPrefix = "SmartBacklogAssistant.prompts.";

    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string> _fileReader;
    private readonly Func<string> _currentDirectoryProvider;
    private readonly Func<string?> _baseDirectoryProvider;

    public PromptTemplateService()
        : this(null, null, null, null)
    {
    }

    internal PromptTemplateService(
        Func<string, bool>? fileExists,
        Func<string, string>? fileReader,
        Func<string>? currentDirectoryProvider,
        Func<string?>? baseDirectoryProvider)
    {
        _fileExists = fileExists ?? File.Exists;
        _fileReader = fileReader ?? File.ReadAllText;
        _currentDirectoryProvider = currentDirectoryProvider ?? Directory.GetCurrentDirectory;
        _baseDirectoryProvider = baseDirectoryProvider ?? (() => AppContext.BaseDirectory);
    }

    public bool IsBuiltInPromptPath(string? promptFilePath) =>
        !string.IsNullOrWhiteSpace(promptFilePath) &&
        (MatchesBuiltInPath(promptFilePath, DefaultPromptFilePath) ||
         MatchesBuiltInPath(promptFilePath, Type01PromptFilePath) ||
         MatchesBuiltInPath(promptFilePath, Type02PromptFilePath) ||
         MatchesBuiltInPath(promptFilePath, GoalExpandPromptFilePath));

    public string ResolvePromptFilePath(string promptFilePath, BacklogType backlogType)
    {
        if (string.IsNullOrWhiteSpace(promptFilePath))
        {
            throw new ArgumentException("Prompt file path cannot be empty.", nameof(promptFilePath));
        }

        if (!PathsMatch(promptFilePath, DefaultPromptFilePath))
        {
            return promptFilePath;
        }

        return backlogType == BacklogType.Type02
            ? Type02PromptFilePath
            : Type01PromptFilePath;
    }

    public string LoadAndRenderPromptForType(string promptFilePath, string rawData, BacklogType backlogType)
    {
        var resolvedPath = ResolvePromptFilePath(promptFilePath, backlogType);
        return LoadAndRenderPrompt(resolvedPath, rawData);
    }

    public string LoadAndRenderPrompt(string promptFilePath, string rawData)
    {
        if (string.IsNullOrWhiteSpace(promptFilePath))
        {
            throw new ArgumentException("Prompt file path cannot be empty.", nameof(promptFilePath));
        }

        if (TryLoadTemplate(promptFilePath, out var template, out _))
        {
            return RenderPromptFromTemplate(template, rawData);
        }

        throw new FileNotFoundException($"Prompt markdown file not found at '{promptFilePath}'.", promptFilePath);
    }

    public bool TryLoadTemplate(string? promptFilePath, out string templateContent, out string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(promptFilePath))
        {
            templateContent = string.Empty;
            sourceLabel = string.Empty;
            return false;
        }

        var resolvedPath = TryFindExistingPromptPath(promptFilePath);
        if (resolvedPath is not null && _fileExists(resolvedPath))
        {
            templateContent = _fileReader(resolvedPath);
            sourceLabel = resolvedPath;
            return true;
        }

        var embedded = TryReadEmbeddedPromptTemplate(promptFilePath);
        if (embedded is not null)
        {
            templateContent = embedded;
            sourceLabel = $"embedded:{GetBuiltInFileName(promptFilePath) ?? Path.GetFileName(promptFilePath)}";
            return true;
        }

        templateContent = string.Empty;
        sourceLabel = string.Empty;
        return false;
    }

    public string? TryReadEmbeddedPromptTemplate(string? promptFilePath)
    {
        if (!IsBuiltInPromptPath(promptFilePath))
        {
            return null;
        }

        var fileName = GetBuiltInFileName(promptFilePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var assembly = typeof(PromptTemplateService).Assembly;
        var expectedName = EmbeddedPromptPrefix + fileName;
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public string? TryFindExistingPromptPath(string? promptFilePath)
    {
        if (string.IsNullOrWhiteSpace(promptFilePath))
        {
            return null;
        }

        if (_fileExists(promptFilePath))
        {
            return promptFilePath;
        }

        if (Path.IsPathRooted(promptFilePath))
        {
            return null;
        }

        var normalizedRelative = promptFilePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        foreach (var startDir in new[] { _currentDirectoryProvider(), _baseDirectoryProvider() })
        {
            if (string.IsNullOrWhiteSpace(startDir))
            {
                continue;
            }

            DirectoryInfo? current;
            try
            {
                current = new DirectoryInfo(startDir);
            }
            catch (Exception)
            {
                continue;
            }

            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, normalizedRelative);
                if (_fileExists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }
        }

        return null;
    }

    public string RenderPromptFromTemplate(string templateContent, string rawData)
    {
        if (string.IsNullOrWhiteSpace(templateContent))
        {
            throw new InvalidOperationException("Prompt template content cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(rawData))
        {
            throw new ArgumentException("Raw data cannot be empty when rendering prompt.", nameof(rawData));
        }

        var normalizedTemplate = templateContent.Trim().Replace("\r\n", "\n");
        if (normalizedTemplate.Contains(RawDataPlaceholder, StringComparison.Ordinal))
        {
            return normalizedTemplate.Replace(RawDataPlaceholder, rawData.Trim(), StringComparison.Ordinal);
        }

        return $"{normalizedTemplate}\n\n## Raw Input Data\n```text\n{rawData.Trim()}\n```";
    }

    private static string? GetBuiltInFileName(string? promptFilePath)
    {
        if (string.IsNullOrWhiteSpace(promptFilePath))
        {
            return null;
        }

        if (MatchesBuiltInPath(promptFilePath, DefaultPromptFilePath))
        {
            return "as-is-v1-prompt.md";
        }

        if (MatchesBuiltInPath(promptFilePath, Type01PromptFilePath))
        {
            return "prompt-goal-type01.md";
        }

        if (MatchesBuiltInPath(promptFilePath, Type02PromptFilePath))
        {
            return "prompt-goal-type02.md";
        }

        if (MatchesBuiltInPath(promptFilePath, GoalExpandPromptFilePath))
        {
            return "prompt-goal-expand.md";
        }

        return null;
    }

    private static bool PathsMatch(string firstPath, string secondPath)
    {
        var normalizedFirst = firstPath.Replace('\\', '/').TrimStart('.', '/');
        var normalizedSecond = secondPath.Replace('\\', '/').TrimStart('.', '/');
        return string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesBuiltInPath(string candidatePath, string builtInRelativePath)
    {
        if (PathsMatch(candidatePath, builtInRelativePath))
        {
            return true;
        }

        var normalizedCandidate = candidatePath.Replace('\\', '/');
        var normalizedBuiltIn = builtInRelativePath.Replace('\\', '/').TrimStart('.', '/');
        return normalizedCandidate.EndsWith("/" + normalizedBuiltIn, StringComparison.OrdinalIgnoreCase);
    }
}
