namespace SmartBacklogAssistant.Services;

/// <summary>
/// Step 1 (Local -> Collect raw data): Reads and validates raw unstructured notes.
/// </summary>
public sealed class RawDataCollector
{
    public string CollectFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Input file path cannot be empty.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Raw input file not found at '{filePath}'.", filePath);
        }

        var content = File.ReadAllText(filePath);
        return CollectFromText(content);
    }

    public string CollectFromText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            throw new InvalidOperationException("Collected raw data cannot be empty or whitespace.");
        }

        return rawText.Trim().Replace("\r\n", "\n");
    }
}
