using System.Text;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Provides fuzzy matching, live keyboard navigation (<c>Up</c>/<c>Down</c>/type-to-filter)
/// in interactive TTY sessions, and deterministic line-based selection fallback for
/// redirected or test streams.
/// </summary>
public static class InteractiveSelector
{
    public static bool IsFuzzyMatch(string candidate, string query) =>
        ScoreFuzzyMatch(candidate, query) > 0;

    public static int ScoreFuzzyMatch(string candidate, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 100;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return 0;
        }

        var normalizedQuery = query.Trim();
        var normalizedCandidate = candidate.Trim();

        if (string.Equals(normalizedCandidate, normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 1000;
        }

        if (normalizedCandidate.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(700, 850 - normalizedCandidate.Length);
        }

        var substringIdx = normalizedCandidate.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase);
        if (substringIdx >= 0)
        {
            return Math.Max(450, 650 - (substringIdx * 2) - normalizedCandidate.Length);
        }

        var compactQuery = new string(normalizedQuery.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (compactQuery.Length == 0)
        {
            return 0;
        }

        var qIdx = 0;
        var consecutive = 0;
        var score = 150;
        var lastMatchIndex = -2;

        for (var cIdx = 0; cIdx < normalizedCandidate.Length && qIdx < compactQuery.Length; cIdx++)
        {
            if (char.ToLowerInvariant(normalizedCandidate[cIdx]) == char.ToLowerInvariant(compactQuery[qIdx]))
            {
                if (cIdx == lastMatchIndex + 1)
                {
                    consecutive++;
                    score += 18 * consecutive;
                }
                else
                {
                    consecutive = 0;
                    score += 10;
                }

                if (cIdx == 0 || !char.IsLetterOrDigit(normalizedCandidate[cIdx - 1]))
                {
                    score += 15;
                }

                lastMatchIndex = cIdx;
                qIdx++;
            }
        }

        return qIdx == compactQuery.Length ? Math.Max(1, score) : 0;
    }

    public static IReadOnlyList<MarkdownOutcomeRenderer.SelectableOutputItem> FilterOutputItems(
        IReadOnlyList<MarkdownOutcomeRenderer.SelectableOutputItem> items,
        string query)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (string.IsNullOrWhiteSpace(query))
        {
            return items;
        }

        var trimmed = query.Trim();
        var exact = items.FirstOrDefault(item =>
            string.Equals(item.NumberKey, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Id, trimmed, StringComparison.OrdinalIgnoreCase) ||
            item.Aliases.Any(alias => string.Equals(alias, trimmed, StringComparison.OrdinalIgnoreCase)));

        if (exact is not null)
        {
            return [exact];
        }

        return items
            .Select((item, index) => (
                Item: item,
                Index: index,
                Score: ScoreOutputItem(item, trimmed)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => x.Item)
            .ToArray();
    }

    public static IReadOnlyList<string> FilterFilePaths(
        IReadOnlyList<string> filePaths,
        string query)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (string.IsNullOrWhiteSpace(query))
        {
            return filePaths;
        }

        var trimmed = query.Trim();
        var exact = filePaths.FirstOrDefault(path =>
            string.Equals(Path.GetFileName(path), trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(path), trimmed, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            return [exact];
        }

        return filePaths
            .Select((path, index) => (
                Path: path,
                Index: index,
                Score: ScoreFuzzyMatch(Path.GetFileName(path), trimmed)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => x.Path)
            .ToArray();
    }

    public static MarkdownOutcomeRenderer.SelectableOutputItem? SelectOutputItemInteractive(
        IReadOnlyList<MarkdownOutcomeRenderer.SelectableOutputItem> items,
        TextReader input,
        ConsoleColorWriter promptColor,
        bool allowInteractiveKeys = true)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(promptColor);

        if (items.Count == 0)
        {
            return null;
        }

        return RunSelector(
            items,
            input,
            promptColor,
            heading: "Select output format or Jira field to copy:",
            formatRow: item => ($"{item.NumberKey}", item.Label, item.PreviewSummary),
            filterFunc: (candidates, query) => FilterOutputItems(candidates, query),
            allowInteractiveKeys: allowInteractiveKeys);
    }

    public static string? SelectFileInteractive(
        IReadOnlyList<string> filePaths,
        TextReader input,
        ConsoleColorWriter promptColor,
        bool allowInteractiveKeys = true)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(promptColor);

        if (filePaths.Count == 0)
        {
            return null;
        }

        return RunSelector(
            filePaths,
            input,
            promptColor,
            heading: "Select a saved Markdown file:",
            formatRow: path =>
            {
                var info = new FileInfo(path);
                return (string.Empty, info.Name, info.Exists ? $"{info.Length:N0} bytes" : string.Empty);
            },
            filterFunc: (candidates, query) => FilterFilePaths(candidates, query),
            allowInteractiveKeys: allowInteractiveKeys);
    }

    public static int FindOptionIndexByKey(
        IReadOnlyList<HighlightedMenuOption> options,
        string? key,
        int fallbackIndex = 0)
    {
        if (options.Count == 0)
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(key))
        {
            for (var i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return Math.Clamp(fallbackIndex, 0, options.Count - 1);
    }

    public static string? SelectMenuOptionWithHighlight(
        IReadOnlyList<HighlightedMenuOption> options,
        int initialIndex,
        string promptText,
        TextReader input,
        ConsoleColorWriter color,
        int minBarWidth = 64,
        bool allowInteractiveKeys = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(color);

        if (options.Count == 0)
        {
            return null;
        }

        var selectedIndex = Math.Clamp(initialIndex, 0, options.Count - 1);
        var canUseKeyboard = allowInteractiveKeys &&
                             ReferenceEquals(input, Console.In) &&
                             ReferenceEquals(color.InnerWriter, Console.Out) &&
                             !Console.IsInputRedirected &&
                             !Console.IsOutputRedirected;

        if (!canUseKeyboard)
        {
            for (var i = 0; i < options.Count; i++)
            {
                color.WriteSelectableMenuRow(options[i], isCursorHighlighted: i == selectedIndex);
            }

            color.WriteColored(promptText, color.GetOptionKeyColor());
            return input.ReadLine();
        }

        var maxWidth = 100;
        try
        {
            if (Console.WindowWidth > 20)
            {
                maxWidth = Console.WindowWidth - 1;
            }
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or ArgumentOutOfRangeException)
        {
        }

        var barWidth = Math.Min(maxWidth, Math.Max(minBarWidth, options.Max(o => o.EstimateLength()) + 2));

        for (var i = 0; i < options.Count; i++)
        {
            color.WriteSelectableMenuRow(options[i], i == selectedIndex, barWidth, maxWidth);
        }

        color.WriteColored(promptText, color.GetOptionKeyColor());

        var topRow = -1;
        try
        {
            topRow = Math.Max(0, Console.CursorTop - options.Count);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or ArgumentOutOfRangeException)
        {
            return input.ReadLine();
        }

        var buffer = new StringBuilder();
        var previousPromptLength = promptText.Length;

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                color.WriteLine();
                return buffer.Length > 0 ? buffer.ToString() : options[selectedIndex].Key;
            }

            if (key.Key == ConsoleKey.Escape)
            {
                if (buffer.Length > 0)
                {
                    buffer.Clear();
                }
                else
                {
                    color.WriteLine();
                    return "0";
                }
            }
            else if (key.Key == ConsoleKey.UpArrow)
            {
                selectedIndex = (selectedIndex - 1 + options.Count) % options.Count;
                buffer.Clear();
            }
            else if (key.Key == ConsoleKey.DownArrow)
            {
                selectedIndex = (selectedIndex + 1) % options.Count;
                buffer.Clear();
            }
            else if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                    var matchedIdx = FindMatchingOptionIndex(options, buffer.ToString());
                    if (matchedIdx >= 0)
                    {
                        selectedIndex = matchedIdx;
                    }
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
                var matchedIdx = FindMatchingOptionIndex(options, buffer.ToString());
                if (matchedIdx >= 0)
                {
                    selectedIndex = matchedIdx;
                }
            }

            try
            {
                Console.SetCursorPosition(0, topRow);
                for (var i = 0; i < options.Count; i++)
                {
                    color.WriteSelectableMenuRow(options[i], i == selectedIndex, barWidth, maxWidth);
                }

                var currentPromptText = $"{promptText}{buffer}";
                color.WriteColored(promptText, color.GetOptionKeyColor());
                color.Write(buffer.ToString());
                var (endLeft, endTop) = Console.GetCursorPosition();
                var clearTrailing = Math.Max(0, previousPromptLength - currentPromptText.Length);
                if (clearTrailing > 0)
                {
                    color.Write(new string(' ', clearTrailing));
                    Console.SetCursorPosition(endLeft, endTop);
                }

                previousPromptLength = currentPromptText.Length;
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or ArgumentOutOfRangeException)
            {
            }
        }
    }

    private static int FindMatchingOptionIndex(IReadOnlyList<HighlightedMenuOption> options, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return -1;
        }

        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].Key, text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static T? RunSelector<T>(
        IReadOnlyList<T> items,
        TextReader input,
        ConsoleColorWriter promptColor,
        string heading,
        Func<T, (string KeyHint, string Label, string Detail)> formatRow,
        Func<IReadOnlyList<T>, string, IReadOnlyList<T>> filterFunc,
        bool allowInteractiveKeys) where T : class
    {
        var canUseKeyboard = allowInteractiveKeys &&
                             ReferenceEquals(input, Console.In) &&
                             !Console.IsInputRedirected &&
                             !Console.IsErrorRedirected;

        if (canUseKeyboard)
        {
            return RunKeyboardSelector(items, promptColor, heading, formatRow, filterFunc);
        }

        return RunLineSelector(items, input, promptColor, heading, formatRow, filterFunc);
    }

    private static T? RunKeyboardSelector<T>(
        IReadOnlyList<T> allItems,
        ConsoleColorWriter promptColor,
        string heading,
        Func<T, (string KeyHint, string Label, string Detail)> formatRow,
        Func<IReadOnlyList<T>, string, IReadOnlyList<T>> filterFunc) where T : class
    {
        var query = string.Empty;
        var selectedIndex = 0;

        while (true)
        {
            var filtered = filterFunc(allItems, query);
            if (selectedIndex >= filtered.Count)
            {
                selectedIndex = Math.Max(0, filtered.Count - 1);
            }

            if (!Console.IsOutputRedirected && ReferenceEquals(promptColor.InnerWriter, Console.Out))
            {
                try
                {
                    Console.Clear();
                }
                catch (IOException)
                {
                }
            }
            else if (promptColor.IsColorEnabled)
            {
                promptColor.Write("\u001b[2J\u001b[H");
            }

            promptColor.WriteHeader(heading);
            promptColor.WriteMuted($"  Filter: {(string.IsNullOrEmpty(query) ? "(type to fuzzy filter)" : query)}   │   ↑/↓ Move  Enter Select  Esc Cancel");
            promptColor.WriteLine();

            if (filtered.Count == 0)
            {
                promptColor.WriteWarning("  [!] No matching items.");
            }
            else
            {
                for (var i = 0; i < filtered.Count; i++)
                {
                    var (keyHint, label, detail) = formatRow(filtered[i]);
                    var displayKey = string.IsNullOrEmpty(keyHint) ? (i + 1).ToString() : keyHint;
                    promptColor.WriteSelectableMenuRow(
                        new HighlightedMenuOption(
                            displayKey,
                            label,
                            Description: string.IsNullOrWhiteSpace(detail) ? null : detail,
                            IsSelectedValue: i == selectedIndex),
                        isCursorHighlighted: i == selectedIndex,
                        minRowWidth: 64);
                }
            }

            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape)
            {
                return null;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                return filtered.Count > 0 ? filtered[selectedIndex] : null;
            }

            if (key.Key == ConsoleKey.UpArrow)
            {
                if (filtered.Count > 0)
                {
                    selectedIndex = (selectedIndex - 1 + filtered.Count) % filtered.Count;
                }

                continue;
            }

            if (key.Key == ConsoleKey.DownArrow)
            {
                if (filtered.Count > 0)
                {
                    selectedIndex = (selectedIndex + 1) % filtered.Count;
                }

                continue;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (query.Length > 0)
                {
                    query = query[..^1];
                    selectedIndex = 0;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                query += key.KeyChar;
                selectedIndex = 0;
            }
        }
    }

    private static T? RunLineSelector<T>(
        IReadOnlyList<T> allItems,
        TextReader input,
        ConsoleColorWriter promptColor,
        string heading,
        Func<T, (string KeyHint, string Label, string Detail)> formatRow,
        Func<IReadOnlyList<T>, string, IReadOnlyList<T>> filterFunc) where T : class
    {
        var currentItems = allItems;
        while (true)
        {
            promptColor.WriteLine();
            promptColor.WriteHeader(heading);
            for (var i = 0; i < currentItems.Count; i++)
            {
                var (keyHint, label, detail) = formatRow(currentItems[i]);
                var displayKey = string.IsNullOrEmpty(keyHint) ? (i + 1).ToString() : keyHint;
                promptColor.WriteSelectableMenuRow(
                    new HighlightedMenuOption(
                        displayKey,
                        label,
                        Description: string.IsNullOrWhiteSpace(detail) ? null : detail,
                        IsSelectedValue: i == 0),
                    isCursorHighlighted: i == 0,
                    minRowWidth: 64);
            }

            promptColor.WriteSelectableMenuRow(new HighlightedMenuOption("0", "Cancel"), isCursorHighlighted: false, minRowWidth: 64);
            promptColor.WriteColored($"Enter number (1-{currentItems.Count}), name, or fuzzy filter [0 to cancel]: ", promptColor.GetOptionKeyColor());

            var line = input.ReadLine();
            if (line is null)
            {
                return null;
            }

            var trimmed = line.Trim();
            var lower = trimmed.ToLowerInvariant();
            if (lower is "" or "0" or "b" or "back" or "/back" or "q" or "quit" or "/exit" or "/quit")
            {
                return null;
            }

            if (int.TryParse(trimmed, out var idx) && idx >= 1 && idx <= currentItems.Count)
            {
                return currentItems[idx - 1];
            }

            var matches = filterFunc(currentItems, trimmed);
            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1)
            {
                currentItems = matches;
                continue;
            }

            promptColor.WriteWarning($"  [!] No item matched '{trimmed}'. Try again or enter 0 to cancel.");
        }
    }

    private static int ScoreOutputItem(MarkdownOutcomeRenderer.SelectableOutputItem item, string query)
    {
        var best = Math.Max(
            ScoreFuzzyMatch(item.Id, query),
            ScoreFuzzyMatch(item.Label, query));

        foreach (var alias in item.Aliases)
        {
            var aliasScore = ScoreFuzzyMatch(alias, query);
            if (aliasScore > best)
            {
                best = aliasScore;
            }
        }

        if (!string.IsNullOrWhiteSpace(item.PreviewSummary))
        {
            var previewScore = ScoreFuzzyMatch(item.PreviewSummary, query) / 2;
            if (previewScore > best)
            {
                best = previewScore;
            }
        }

        return best;
    }
}