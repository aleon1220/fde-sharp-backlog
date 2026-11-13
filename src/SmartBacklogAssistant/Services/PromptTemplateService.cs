using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Loads a pre-designed prompt from a Markdown file and populates it with collected raw data.
/// </summary>
public sealed class PromptTemplateService
{
    public const string RawDataPlaceholder = "{{RAW_DATA}}";
    public const string DefaultPromptFilePath = "prompts/as-is-v1-prompt.md";
    public const string Type01PromptFilePath = "prompts/prompt-goal-type01.md";
    public const string Type02PromptFilePath = "prompts/prompt-goal-type02.md";

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

        var resolvedPath = TryFindExistingPromptPath(promptFilePath) ?? promptFilePath;
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"Prompt markdown file not found at '{promptFilePath}'.", promptFilePath);
        }

        var template = File.ReadAllText(resolvedPath);
        return RenderPromptFromTemplate(template, rawData);
    }

    public string? TryFindExistingPromptPath(string? promptFilePath)
    {
        if (string.IsNullOrWhiteSpace(promptFilePath))
        {
            return null;
        }

        if (File.Exists(promptFilePath))
        {
            return promptFilePath;
        }

        if (Path.IsPathRooted(promptFilePath))
        {
            return null;
        }

        var normalizedRelative = promptFilePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        foreach (var startDir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(startDir))
            {
                continue;
            }

            var current = new DirectoryInfo(startDir);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, normalizedRelative);
                if (File.Exists(candidate))
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

    private static bool PathsMatch(string firstPath, string secondPath)
    {
        var normalizedFirst = firstPath.Replace('\\', '/').TrimStart('.', '/');
        var normalizedSecond = secondPath.Replace('\\', '/').TrimStart('.', '/');
        return string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }
}
