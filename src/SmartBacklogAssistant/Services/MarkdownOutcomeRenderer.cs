using System.Text;
using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Parses generated or saved Jira backlog Markdown documents and renders them
/// into one unified 2-column colored CLI table, plus raw-text and Markdown clipboard outputs.
/// </summary>
public static class MarkdownOutcomeRenderer
{
    public sealed record ParsedMarkdownOutcome(
        string? StageLabel,
        string? TitleLine,
        IReadOnlyList<(string Section, string Content)> Sections);

    public sealed record OutcomeTableRow(
        string Section,
        string Content,
        bool DividerBefore = false,
        ConsoleColor? OverrideTextColor = null);

    public static ParsedMarkdownOutcome ParseMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new ParsedMarkdownOutcome(null, null, []);
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        string? titleLine = null;
        string? stageLabel = null;
        var sections = new List<(string Section, string Content)>();
        var currentSection = string.Empty;
        var currentLines = new List<string>();
        var bodyLinesBeforeHeading = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (titleLine is null && line.StartsWith("# ", StringComparison.Ordinal))
            {
                titleLine = line;
                stageLabel = ExtractStageLabel(line);
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushSection(sections, currentSection, currentLines);
                currentSection = MapHeadingToSectionName(line[3..].Trim());
                currentLines.Clear();
                continue;
            }

            if (string.IsNullOrEmpty(currentSection))
            {
                if (!string.IsNullOrWhiteSpace(line) || bodyLinesBeforeHeading.Count > 0)
                {
                    bodyLinesBeforeHeading.Add(line);
                }
            }
            else
            {
                currentLines.Add(line);
            }
        }

        FlushSection(sections, currentSection, currentLines);

        if (sections.Count == 0 && bodyLinesBeforeHeading.Count > 0)
        {
            var body = TrimBlankLines(bodyLinesBeforeHeading);
            if (!string.IsNullOrWhiteSpace(body))
            {
                sections.Add(("Content", body));
            }
        }

        return new ParsedMarkdownOutcome(stageLabel, titleLine, sections);
    }

    public static IReadOnlyList<OutcomeTableRow> BuildTableRows(
        IReadOnlyList<string> markdownDocuments,
        string? supplementalUserStory = null,
        string? supplementalDescription = null)
    {
        ArgumentNullException.ThrowIfNull(markdownDocuments);

        var rows = new List<OutcomeTableRow>();
        string? sharedAlternatives = null;
        var addedSupplementalStory = false;
        var addedSupplementalDescription = false;

        for (var docIndex = 0; docIndex < markdownDocuments.Count; docIndex++)
        {
            var parsed = ParseMarkdown(markdownDocuments[docIndex]);
            var isFirstDoc = docIndex == 0;

            if (markdownDocuments.Count == 1 && !string.IsNullOrWhiteSpace(parsed.StageLabel))
            {
                rows.Add(new OutcomeTableRow("Stage / Type", parsed.StageLabel, OverrideTextColor: ConsoleColor.Green));
            }

            if (!string.IsNullOrWhiteSpace(parsed.TitleLine))
            {
                var titleSection = markdownDocuments.Count > 1
                    ? ResolveMultiDocTitleSection(parsed.StageLabel, docIndex)
                    : "Title";
                rows.Add(new OutcomeTableRow(titleSection, parsed.TitleLine, DividerBefore: !isFirstDoc));
            }

            var hasUserStorySection = parsed.Sections.Any(s => s.Section.Equals("User Story", StringComparison.OrdinalIgnoreCase));
            var hasDescriptionSection = parsed.Sections.Any(s => s.Section.Equals("Description", StringComparison.OrdinalIgnoreCase));

            if (!addedSupplementalStory &&
                !hasUserStorySection &&
                IsMeaningfulSupplemental(supplementalUserStory))
            {
                rows.Add(new OutcomeTableRow("User Story", supplementalUserStory!.Trim(), DividerBefore: true));
                addedSupplementalStory = true;
            }

            if (!addedSupplementalDescription &&
                markdownDocuments.Count == 1 &&
                !hasDescriptionSection &&
                IsMeaningfulSupplemental(supplementalDescription))
            {
                rows.Add(new OutcomeTableRow("Description", supplementalDescription!.Trim(), DividerBefore: true));
                addedSupplementalDescription = true;
            }

            foreach (var (section, content) in parsed.Sections)
            {
                if (section.Equals("Alternatives", StringComparison.OrdinalIgnoreCase))
                {
                    sharedAlternatives ??= content;
                    continue;
                }

                if (section.Equals("User Story", StringComparison.OrdinalIgnoreCase))
                {
                    addedSupplementalStory = true;
                }

                if (section.Equals("Description", StringComparison.OrdinalIgnoreCase))
                {
                    addedSupplementalDescription = true;
                }

                rows.Add(new OutcomeTableRow(section, content, DividerBefore: rows.Count > 0));
            }
        }

        if (!string.IsNullOrWhiteSpace(sharedAlternatives))
        {
            rows.Add(new OutcomeTableRow("Alternatives", sharedAlternatives, DividerBefore: rows.Count > 0));
        }

        return rows;
    }

    public static void WriteOutcomeTable(
        ConsoleColorWriter color,
        IReadOnlyList<string> markdownDocuments,
        string? supplementalUserStory = null,
        string? supplementalDescription = null,
        string tableTitle = "Smart Backlog Assistant — Markdown Outcome")
    {
        ArgumentNullException.ThrowIfNull(color);
        ArgumentNullException.ThrowIfNull(markdownDocuments);

        var rows = BuildTableRows(markdownDocuments, supplementalUserStory, supplementalDescription);
        if (rows.Count == 0)
        {
            return;
        }

        var col1Width = Math.Clamp(
            Math.Max("Section".Length, rows.Max(r => r.Section.Length)),
            12,
            16);
        var wrapCol2Width = ResolveColumn2Width(color.InnerWriter, col1Width);
        var preparedRows = new List<(OutcomeTableRow Row, IReadOnlyList<string> WrappedLines)>(rows.Count);
        var maxCol2LineLength = "Markdown Content".Length;

        foreach (var row in rows)
        {
            var wrapped = WrapCellLines(row.Content, wrapCol2Width);
            foreach (var line in wrapped)
            {
                if (line.Length > maxCol2LineLength)
                {
                    maxCol2LineLength = line.Length;
                }
            }

            preparedRows.Add((row, wrapped));
        }

        var col2Width = Math.Max(wrapCol2Width, maxCol2LineLength);
        var borderColor = color.GetSubHeaderColor();
        var sectionColor = color.GetOptionKeyColor();

        var topBorder = $"┌{new string('─', col1Width + 2)}┬{new string('─', col2Width + 2)}┐";
        var midBorder = $"├{new string('─', col1Width + 2)}┼{new string('─', col2Width + 2)}┤";
        var botBorder = $"└{new string('─', col1Width + 2)}┴{new string('─', col2Width + 2)}┘";

        color.WriteLine();
        color.WriteHeader(tableTitle);
        color.WriteLineColored(topBorder, borderColor);

        color.WriteColored("│ ", borderColor);
        color.WriteColored("Section".PadRight(col1Width), ConsoleColor.White);
        color.WriteColored(" │ ", borderColor);
        color.WriteColored("Markdown Content".PadRight(col2Width), ConsoleColor.White);
        color.WriteLineColored(" │", borderColor);
        color.WriteLineColored(midBorder, borderColor);

        var firstRow = true;
        foreach (var (row, lines) in preparedRows)
        {
            if (!firstRow && row.DividerBefore)
            {
                color.WriteLineColored(midBorder, borderColor);
            }

            firstRow = false;
            var sectionLabel = TruncatePad(row.Section, col1Width);

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var col1Text = i == 0 ? sectionLabel : new string(' ', col1Width);
                var padding = new string(' ', Math.Max(0, col2Width - line.Length));

                color.WriteColored("│ ", borderColor);
                color.WriteColored(col1Text, sectionColor);
                color.WriteColored(" │ ", borderColor);
                WriteStyledMarkdownCellLine(color, line, row.OverrideTextColor);
                color.Write(padding);
                color.WriteLineColored(" │", borderColor);
            }
        }

        color.WriteLineColored(botBorder, borderColor);
    }

    public static string RenderRawTableText(
        IReadOnlyList<string> markdownDocuments,
        string? supplementalUserStory = null,
        string? supplementalDescription = null)
    {
        using var writer = new StringWriter();
        var plainColor = new ConsoleColorWriter(writer, enableColor: false);
        WriteOutcomeTable(plainColor, markdownDocuments, supplementalUserStory, supplementalDescription);
        return writer.ToString().Replace("\r\n", "\n").Trim();
    }

    public static string RenderMarkdownText(IReadOnlyList<string> markdownDocuments)
    {
        ArgumentNullException.ThrowIfNull(markdownDocuments);

        var nonEmpty = markdownDocuments
            .Where(doc => !string.IsNullOrWhiteSpace(doc))
            .Select(doc => doc.Replace("\r\n", "\n").Trim())
            .ToArray();

        return nonEmpty.Length switch
        {
            0 => string.Empty,
            1 => nonEmpty[0],
            _ => string.Join("\n\n---\n\n", nonEmpty)
        };
    }

    public static ClipboardCopyResult CopyOutcomeToClipboard(
        IClipboardService clipboardService,
        bool asMarkdown,
        IReadOnlyList<string> markdownDocuments,
        string? supplementalUserStory,
        string? supplementalDescription,
        ConsoleColorWriter color)
    {
        ArgumentNullException.ThrowIfNull(clipboardService);
        ArgumentNullException.ThrowIfNull(markdownDocuments);
        ArgumentNullException.ThrowIfNull(color);

        if (markdownDocuments.Count == 0)
        {
            var emptyResult = new ClipboardCopyResult(false, "none", "No outcome available to copy.");
            color.WriteWarning($"  [!] {emptyResult.Message}");
            return emptyResult;
        }

        var payload = asMarkdown
            ? RenderMarkdownText(markdownDocuments)
            : RenderRawTableText(markdownDocuments, supplementalUserStory, supplementalDescription);

        var result = clipboardService.CopyText(payload);
        var label = asMarkdown ? "Markdown" : "Raw Text";
        if (result.Succeeded)
        {
            color.WriteSuccess($"  ✓ Copied {label} outcome to clipboard ({result.Method}).");
        }
        else
        {
            color.WriteWarning($"  [!] {result.Message}");
        }

        return result;
    }

    private static void WriteStyledMarkdownCellLine(
        ConsoleColorWriter color,
        string line,
        ConsoleColor? overrideColor)
    {
        if (overrideColor.HasValue)
        {
            color.WriteColored(line, overrideColor.Value);
            return;
        }

        if (line.StartsWith("# ", StringComparison.Ordinal))
        {
            color.WriteColored(line, color.GetHeaderColor());
            return;
        }

        if (line.StartsWith("### ", StringComparison.Ordinal) ||
            line.StartsWith("## ", StringComparison.Ordinal))
        {
            color.WriteColored(line, color.GetOptionKeyColor());
            return;
        }

        if (line.StartsWith("* ", StringComparison.Ordinal) ||
            line.StartsWith("- ", StringComparison.Ordinal))
        {
            color.WriteColored(line[..2], color.GetOptionKeyColor());
            color.WriteColored(line[2..], ConsoleColor.White);
            return;
        }

        var numberedPrefixLen = GetNumberedListPrefixLength(line);
        if (numberedPrefixLen > 0)
        {
            color.WriteColored(line[..numberedPrefixLen], color.GetOptionKeyColor());
            color.WriteColored(line[numberedPrefixLen..], ConsoleColor.White);
            return;
        }

        color.WriteColored(line, ConsoleColor.White);
    }

    private static string? ExtractStageLabel(string h1Line)
    {
        const string prefix = "# [Jira - ";
        if (!h1Line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var closeBracket = h1Line.IndexOf(']', prefix.Length);
        return closeBracket > prefix.Length
            ? h1Line[prefix.Length..closeBracket].Trim()
            : null;
    }

    private static string ResolveMultiDocTitleSection(string? stageLabel, int index)
    {
        if (!string.IsNullOrWhiteSpace(stageLabel))
        {
            if (stageLabel.Contains("Type01", StringComparison.OrdinalIgnoreCase))
            {
                return "Type01";
            }

            if (stageLabel.Contains("Type02", StringComparison.OrdinalIgnoreCase))
            {
                return "Type02";
            }
        }

        return $"Item {index + 1}";
    }

    private static string MapHeadingToSectionName(string heading)
    {
        if (heading.StartsWith("Acceptance Criteria", StringComparison.OrdinalIgnoreCase))
        {
            return "Acceptance";
        }

        if (heading.Equals("Title & Description Alternatives", StringComparison.OrdinalIgnoreCase))
        {
            return "Alternatives";
        }

        return heading;
    }

    private static void FlushSection(
        List<(string Section, string Content)> sections,
        string sectionName,
        List<string> lines)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            return;
        }

        var content = TrimBlankLines(lines);
        if (!string.IsNullOrWhiteSpace(content))
        {
            sections.Add((sectionName, content));
        }
    }

    private static string TrimBlankLines(List<string> lines)
    {
        var start = 0;
        while (start < lines.Count && string.IsNullOrWhiteSpace(lines[start]))
        {
            start++;
        }

        var end = lines.Count - 1;
        while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
        {
            end--;
        }

        if (start > end)
        {
            return string.Empty;
        }

        var cleaned = new List<string>(end - start + 1);
        var previousBlank = false;
        for (var i = start; i <= end; i++)
        {
            var isBlank = string.IsNullOrWhiteSpace(lines[i]);
            if (isBlank)
            {
                if (!previousBlank)
                {
                    cleaned.Add(string.Empty);
                }

                previousBlank = true;
            }
            else
            {
                previousBlank = false;
                cleaned.Add(lines[i]);
            }
        }

        return string.Join('\n', cleaned);
    }

    private static bool IsMeaningfulSupplemental(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Trim().Equals("Not specified in discovery notes.", StringComparison.OrdinalIgnoreCase);

    private static int ResolveColumn2Width(TextWriter writer, int col1Width)
    {
        if (ReferenceEquals(writer, Console.Out) && !Console.IsOutputRedirected)
        {
            try
            {
                if (Console.WindowWidth > 0)
                {
                    return Math.Clamp(Console.WindowWidth - col1Width - 8, 52, 92);
                }
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or ArgumentOutOfRangeException)
            {
            }
        }

        return 74;
    }

    private static IReadOnlyList<string> WrapCellLines(string content, int maxWidth)
    {
        var rawLines = content.Replace("\r\n", "\n").Split('\n');
        var wrapped = new List<string>();

        foreach (var rawLine in rawLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                if (wrapped.Count > 0 && !string.IsNullOrEmpty(wrapped[^1]))
                {
                    wrapped.Add(string.Empty);
                }

                continue;
            }

            wrapped.AddRange(WrapSingleLine(rawLine, maxWidth));
        }

        while (wrapped.Count > 0 && string.IsNullOrEmpty(wrapped[^1]))
        {
            wrapped.RemoveAt(wrapped.Count - 1);
        }

        return wrapped.Count > 0 ? wrapped : [string.Empty];
    }

    private static IReadOnlyList<string> WrapSingleLine(string line, int maxWidth)
    {
        if (line.Length <= maxWidth)
        {
            return [line];
        }

        var continuationIndent = ResolveContinuationIndent(line, maxWidth);
        var leadingSpaces = line.Length - line.TrimStart(' ').Length;
        var current = new StringBuilder(leadingSpaces > 0 ? line[..leadingSpaces] : string.Empty);
        var words = line.TrimStart(' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wrapped = new List<string>();

        foreach (var word in words)
        {
            var isLineStart = current.Length == 0 ||
                              (wrapped.Count == 0 && current.Length == leadingSpaces) ||
                              (wrapped.Count > 0 && current.Length == continuationIndent.Length);
            var candidateLength = isLineStart ? current.Length + word.Length : current.Length + 1 + word.Length;

            if (candidateLength > maxWidth && !isLineStart)
            {
                wrapped.Add(current.ToString());
                current.Clear();
                current.Append(continuationIndent);
                current.Append(word);
            }
            else
            {
                if (!isLineStart)
                {
                    current.Append(' ');
                }

                current.Append(word);
            }
        }

        if (current.Length > 0)
        {
            wrapped.Add(current.ToString());
        }

        return wrapped.Count > 0 ? wrapped : [line];
    }

    private static string ResolveContinuationIndent(string line, int maxWidth)
    {
        var indentLen = 0;
        while (indentLen < line.Length && line[indentLen] == ' ')
        {
            indentLen++;
        }

        var trimmed = line[indentLen..];
        var extraIndent = 0;

        if (trimmed.StartsWith("* ", StringComparison.Ordinal) ||
            trimmed.StartsWith("- ", StringComparison.Ordinal))
        {
            extraIndent = 2;
        }
        else
        {
            var numberedLen = GetNumberedListPrefixLength(trimmed);
            if (numberedLen > 0)
            {
                extraIndent = numberedLen;
            }
        }

        var totalIndent = indentLen + extraIndent;
        return totalIndent < maxWidth - 12 ? new string(' ', totalIndent) : string.Empty;
    }

    private static int GetNumberedListPrefixLength(string line)
    {
        var i = 0;
        while (i < line.Length && char.IsDigit(line[i]))
        {
            i++;
        }

        return i > 0 && i + 1 < line.Length && line[i] == '.' && line[i + 1] == ' '
            ? i + 2
            : 0;
    }

    private static string TruncatePad(string text, int width) =>
        text.Length > width ? text[..(width - 1)] + "…" : text.PadRight(width);
}
