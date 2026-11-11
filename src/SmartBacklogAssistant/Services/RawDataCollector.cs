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

        if (Directory.Exists(filePath))
        {
            var firstFile = Directory
                .EnumerateFiles(filePath)
                .Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (firstFile is null)
            {
                throw new FileNotFoundException($"No raw input files (.txt or .md) found in directory '{filePath}'.", filePath);
            }

            filePath = firstFile;
        }
        else if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Raw input file not found at '{filePath}'.", filePath);
        }

        var content = File.ReadAllText(filePath);
        return CollectFromText(content);
    }

    public string CollectFromGoal(string goalOrTitle)
    {
        if (string.IsNullOrWhiteSpace(goalOrTitle))
        {
            throw new ArgumentException("Goal or title cannot be empty.", nameof(goalOrTitle));
        }

        var cleanedGoal = goalOrTitle.Trim();
        var rawNotes = $"""
            Title: {cleanedGoal}
            Context: Backlog goal added via Smart Backlog Assistant CLI ({cleanedGoal}).
            Requirement: As a product stakeholder, I want to {cleanedGoal.ToLowerInvariant()}, so that the engineering backlog captures this deliverable clearly.
            Precondition: The requirement '{cleanedGoal}' is prioritized for implementation.
            Action: The engineering team executes and verifies '{cleanedGoal}'.
            Expected Outcome: '{cleanedGoal}' is completed, verified, and meets acceptance criteria.
            """;

        return CollectFromText(rawNotes);
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
