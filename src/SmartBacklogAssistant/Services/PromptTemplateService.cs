namespace SmartBacklogAssistant.Services;

/// <summary>
/// Loads a pre-designed prompt from a Markdown file and populates it with collected raw data.
/// </summary>
public sealed class PromptTemplateService
{
    public const string RawDataPlaceholder = "{{RAW_DATA}}";

    public string LoadAndRenderPrompt(string promptFilePath, string rawData)
    {
        if (string.IsNullOrWhiteSpace(promptFilePath))
        {
            throw new ArgumentException("Prompt file path cannot be empty.", nameof(promptFilePath));
        }

        if (!File.Exists(promptFilePath))
        {
            throw new FileNotFoundException($"Prompt markdown file not found at '{promptFilePath}'.", promptFilePath);
        }

        var template = File.ReadAllText(promptFilePath);
        return RenderPromptFromTemplate(template, rawData);
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
}
