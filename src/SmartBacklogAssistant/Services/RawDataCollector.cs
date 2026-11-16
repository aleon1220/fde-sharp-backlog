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
        if (cleanedGoal.StartsWith("Goal:", StringComparison.OrdinalIgnoreCase) ||
            cleanedGoal.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
        {
            return CollectFromText(cleanedGoal);
        }

        return CollectFromText($"Goal: {cleanedGoal}");
    }

    public sealed record InlineGoalInput(
        string GoalExpression,
        string? InlineDetails,
        bool EnableWebSearch);

    /// <summary>
    /// Parses compact interactive draft inputs such as
    /// <c>Check Geocaching 2026 hiding movie theme | Include rules and dates --search</c>.
    /// Plain single-line goals without <c>|</c> or <c>--search</c>/<c>--web</c> pass through unchanged.
    /// </summary>
    public static InlineGoalInput ParseInlineGoalModifiers(string rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return new InlineGoalInput(string.Empty, null, false);
        }

        var enableSearch = false;
        var withoutSearchFlags = StripStandaloneSearchFlags(rawInput, ref enableSearch);

        var pipeIndex = FindUnquotedPipeIndex(withoutSearchFlags);
        if (pipeIndex < 0)
        {
            return new InlineGoalInput(withoutSearchFlags.Trim(), null, enableSearch);
        }

        var goalPart = withoutSearchFlags[..pipeIndex].Trim();
        var detailsPart = withoutSearchFlags[(pipeIndex + 1)..].Trim();

        return new InlineGoalInput(
            GoalExpression: goalPart,
            InlineDetails: string.IsNullOrWhiteSpace(detailsPart) ? null : detailsPart,
            EnableWebSearch: enableSearch);
    }

    public string CollectFromGoalWithContext(
        string goalOrTitle,
        string? details = null,
        string? webResearchFindings = null,
        string? previousDraftSummary = null,
        string? refinementInstructions = null)
    {
        var baseGoal = CollectFromGoal(goalOrTitle);
        var hasDetails = !string.IsNullOrWhiteSpace(details);
        var hasWeb = !string.IsNullOrWhiteSpace(webResearchFindings);
        var hasPrevious = !string.IsNullOrWhiteSpace(previousDraftSummary);
        var hasRefinement = !string.IsNullOrWhiteSpace(refinementInstructions);

        if (!hasDetails && !hasWeb && !hasPrevious && !hasRefinement)
        {
            return baseGoal;
        }

        var parts = new List<string> { baseGoal };

        if (hasDetails)
        {
            var normalizedDetails = details!.Trim().Replace("\r\n", "\n");
            if (normalizedDetails.StartsWith("Context:", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(normalizedDetails);
            }
            else if (normalizedDetails.Contains('\n'))
            {
                parts.Add($"Context:\n{normalizedDetails}");
            }
            else
            {
                parts.Add($"Context: {normalizedDetails}");
            }
        }

        if (hasWeb)
        {
            parts.Add($"Web Research Findings:\n{webResearchFindings!.Trim().Replace("\r\n", "\n")}");
        }

        if (hasPrevious)
        {
            parts.Add($"Previous Draft:\n{previousDraftSummary!.Trim().Replace("\r\n", "\n")}");
        }

        if (hasRefinement)
        {
            parts.Add($"Refinement Instructions:\n{refinementInstructions!.Trim().Replace("\r\n", "\n")}");
        }

        return CollectFromText(string.Join("\n\n", parts));
    }

    public IReadOnlyList<string> GetInputFilesFromDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(directoryPath)
            .Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string CollectFromText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            throw new InvalidOperationException("Collected raw data cannot be empty or whitespace.");
        }

        return rawText.Trim().Replace("\r\n", "\n");
    }

    private static string StripStandaloneSearchFlags(string text, ref bool enableSearch)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote.HasValue)
            {
                current.Append(ch);
                if (ch == quote.Value)
                {
                    quote = null;
                }
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
                current.Append(ch);
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    FlushToken(tokens, current, ref enableSearch);
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            FlushToken(tokens, current, ref enableSearch);
        }

        return string.Join(' ', tokens);
    }

    private static void FlushToken(List<string> tokens, System.Text.StringBuilder current, ref bool enableSearch)
    {
        var token = current.ToString();
        current.Clear();

        if (token.Equals("--search", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("--web", StringComparison.OrdinalIgnoreCase))
        {
            enableSearch = true;
            return;
        }

        tokens.Add(token);
    }

    private static int FindUnquotedPipeIndex(string text)
    {
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote.HasValue)
            {
                if (ch == quote.Value)
                {
                    quote = null;
                }
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (ch == '|')
            {
                return i;
            }
        }

        return -1;
    }
}
