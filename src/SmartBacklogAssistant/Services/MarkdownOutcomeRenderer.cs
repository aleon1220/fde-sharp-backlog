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

    public sealed record SelectableOutputItem(
        string NumberKey,
        string Id,
        string Label,
        string CopyBadgeLabel,
        string Value,
        string PreviewSummary,
        IReadOnlyList<string> Aliases);

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
        var label = asMarkdown ? "Markdown outcome" : "Raw Text outcome";
        return CopyTextAndReport(clipboardService, payload, label, color);
    }

    public static ClipboardCopyResult CopySelectableItemToClipboard(
        IClipboardService clipboardService,
        SelectableOutputItem item,
        ConsoleColorWriter color)
    {
        ArgumentNullException.ThrowIfNull(clipboardService);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(color);

        if (string.IsNullOrWhiteSpace(item.Value))
        {
            var emptyResult = new ClipboardCopyResult(false, "none", $"Selected item '{item.Label}' has no content to copy.");
            color.WriteWarning($"  [!] {emptyResult.Message}");
            return emptyResult;
        }

        return CopyTextAndReport(clipboardService, item.Value, item.CopyBadgeLabel, color);
    }

    private static ClipboardCopyResult CopyTextAndReport(
        IClipboardService clipboardService,
        string payload,
        string badgeLabel,
        ConsoleColorWriter color)
    {
        var result = clipboardService.CopyText(payload);
        if (result.Succeeded)
        {
            color.WriteSuccess($"  ✓ Copied {badgeLabel} to clipboard ({result.Method}).");
        }
        else
        {
            color.WriteWarning($"  [!] {result.Message}");
        }

        return result;
    }

    public static string StripMarkdownFormatting(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var cleaned = new List<string>(lines.Length);

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("# [Jira - ", StringComparison.OrdinalIgnoreCase))
            {
                cleaned.Add(ExtractCleanTitle(line));
                continue;
            }

            if (line.StartsWith('#'))
            {
                var idx = 0;
                while (idx < line.Length && line[idx] == '#')
                {
                    idx++;
                }

                line = line[idx..].TrimStart();
            }

            line = line.Replace("**", string.Empty, StringComparison.Ordinal)
                       .Replace("`", string.Empty, StringComparison.Ordinal);

            cleaned.Add(line);
        }

        return TrimBlankLines(cleaned);
    }

    public static string RenderCleanPlainText(
        IReadOnlyList<string> markdownDocuments,
        string? supplementalUserStory = null,
        string? supplementalDescription = null)
    {
        ArgumentNullException.ThrowIfNull(markdownDocuments);

        var rows = BuildTableRows(markdownDocuments, supplementalUserStory, supplementalDescription);
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var blocks = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Section.Equals("Stage / Type", StringComparison.OrdinalIgnoreCase))
            {
                blocks.Add($"Stage / Type: {row.Content.Trim()}");
                continue;
            }

            if (row.Section is "Title" or "Type01" or "Type02")
            {
                blocks.Add($"{row.Section}: {ExtractCleanTitle(row.Content)}");
                continue;
            }

            var header = row.Section.Equals("Acceptance", StringComparison.OrdinalIgnoreCase)
                ? "Acceptance Criteria"
                : row.Section;
            var cleanBody = StripMarkdownFormatting(row.Content);
            if (!string.IsNullOrWhiteSpace(cleanBody))
            {
                blocks.Add($"{header}:\n{cleanBody}");
            }
        }

        return string.Join("\n\n", blocks).Trim();
    }

    public static IReadOnlyList<SelectableOutputItem> ExtractSelectableItems(
        IReadOnlyList<string> markdownDocuments,
        string? supplementalUserStory = null,
        string? supplementalDescription = null)
    {
        ArgumentNullException.ThrowIfNull(markdownDocuments);
        if (markdownDocuments.Count == 0)
        {
            return [];
        }

        var items = new List<SelectableOutputItem>();
        var rawTable = RenderRawTableText(markdownDocuments, supplementalUserStory, supplementalDescription);
        var fullMarkdown = RenderMarkdownText(markdownDocuments);
        var cleanText = RenderCleanPlainText(markdownDocuments, supplementalUserStory, supplementalDescription);

        items.Add(new SelectableOutputItem(
            "1",
            "raw",
            "Raw Text (table)",
            "Raw Text outcome",
            rawTable,
            SummarizePreview(rawTable),
            ["raw", "table", "w"]));

        items.Add(new SelectableOutputItem(
            "2",
            "md",
            "Markdown",
            "Markdown outcome",
            fullMarkdown,
            SummarizePreview(fullMarkdown),
            ["md", "markdown", "m"]));

        items.Add(new SelectableOutputItem(
            "3",
            "clean",
            "Clean Plain Text (no borders/markdown)",
            "Clean Plain Text outcome",
            cleanText,
            SummarizePreview(cleanText),
            ["clean", "plain", "text", "c"]));

        var rows = BuildTableRows(markdownDocuments, supplementalUserStory, supplementalDescription);
        var nextNumber = 4;

        var titleRow = rows.FirstOrDefault(r => r.Section is "Title" or "Type01" or "Type02");
        if (titleRow is not null)
        {
            var cleanTitle = ExtractCleanTitle(titleRow.Content);
            if (!string.IsNullOrWhiteSpace(cleanTitle))
            {
                items.Add(new SelectableOutputItem(
                    (nextNumber++).ToString(),
                    "title",
                    "Title (Clean)",
                    "Title",
                    cleanTitle,
                    SummarizePreview(cleanTitle),
                    ["title", "t"]));
            }
        }

        var storyRow = rows.FirstOrDefault(r => r.Section.Equals("User Story", StringComparison.OrdinalIgnoreCase));
        if (storyRow is not null && IsMeaningfulSupplemental(storyRow.Content))
        {
            var cleanStory = StripMarkdownFormatting(storyRow.Content);
            items.Add(new SelectableOutputItem(
                (nextNumber++).ToString(),
                "story",
                "User Story (Clean)",
                "User Story",
                cleanStory,
                SummarizePreview(cleanStory),
                ["story", "user-story", "us"]));
        }

        var descRow = rows.FirstOrDefault(r => r.Section.Equals("Description", StringComparison.OrdinalIgnoreCase));
        if (descRow is not null && IsMeaningfulSupplemental(descRow.Content))
        {
            var cleanDesc = StripMarkdownFormatting(descRow.Content);
            items.Add(new SelectableOutputItem(
                (nextNumber++).ToString(),
                "desc",
                "Description (Clean)",
                "Description",
                cleanDesc,
                SummarizePreview(cleanDesc),
                ["desc", "description", "d"]));
        }

        var acRow = rows.FirstOrDefault(r => r.Section.Equals("Acceptance", StringComparison.OrdinalIgnoreCase));
        if (acRow is not null)
        {
            var cleanAc = StripMarkdownFormatting(acRow.Content);
            if (!string.IsNullOrWhiteSpace(cleanAc))
            {
                items.Add(new SelectableOutputItem(
                    (nextNumber++).ToString(),
                    "ac",
                    "Acceptance Criteria (Clean)",
                    "Acceptance Criteria",
                    cleanAc,
                    SummarizePreview(cleanAc),
                    ["ac", "acceptance", "gwt", "criteria"]));
            }
        }

        var altRow = rows.FirstOrDefault(r => r.Section.Equals("Alternatives", StringComparison.OrdinalIgnoreCase));
        if (altRow is not null)
        {
            var alternatives = ParseAlternatives(altRow.Content);
            for (var i = 0; i < alternatives.Count; i++)
            {
                var (angle, title, description) = alternatives[i];
                var altNum = i + 1;
                var value = string.IsNullOrWhiteSpace(description)
                    ? title
                    : $"{title}\n{description}";
                var label = string.IsNullOrWhiteSpace(angle)
                    ? $"Alternative #{altNum}"
                    : $"Alternative #{altNum} ({angle})";

                items.Add(new SelectableOutputItem(
                    (nextNumber++).ToString(),
                    $"alt{altNum}",
                    label,
                    $"Alternative #{altNum}",
                    value,
                    SummarizePreview(title),
                    [$"alt{altNum}", $"alternative{altNum}", $"alt-{altNum}"]));
            }
        }

        return items;
    }

    private static string ExtractCleanTitle(string rawTitleLine)
    {
        var trimmed = rawTitleLine.Trim();
        if (trimmed.StartsWith("# ", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..].Trim();
        }

        const string jiraPrefix = "[Jira - ";
        if (trimmed.StartsWith(jiraPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var closeIdx = trimmed.IndexOf(']', jiraPrefix.Length);
            if (closeIdx >= 0 && closeIdx + 1 < trimmed.Length)
            {
                trimmed = trimmed[(closeIdx + 1)..].Trim();
            }
        }

        return trimmed.Replace("**", string.Empty, StringComparison.Ordinal)
                      .Replace("`", string.Empty, StringComparison.Ordinal);
    }

    private static IReadOnlyList<(string Angle, string Title, string Description)> ParseAlternatives(string rawAlternatives)
    {
        var parsed = new List<(string Angle, string Title, string Description)>();
        var lines = rawAlternatives.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var prefixLen = GetNumberedListPrefixLength(line);
            if (prefixLen == 0)
            {
                continue;
            }

            var body = line[prefixLen..].Replace("**", string.Empty, StringComparison.Ordinal).Trim();
            var angle = string.Empty;
            if (body.StartsWith('[') && body.IndexOf(']') is > 1 and var closeBracket)
            {
                angle = body[1..closeBracket].Trim();
                body = body[(closeBracket + 1)..].Trim();
            }

            string title;
            string description;
            var emDashIdx = body.IndexOf(" — ", StringComparison.Ordinal);
            if (emDashIdx >= 0)
            {
                title = body[..emDashIdx].Trim();
                description = body[(emDashIdx + 3)..].Trim();
            }
            else
            {
                var dashIdx = body.IndexOf(" - ", StringComparison.Ordinal);
                if (dashIdx >= 0)
                {
                    title = body[..dashIdx].Trim();
                    description = body[(dashIdx + 3)..].Trim();
                }
                else
                {
                    title = body;
                    description = string.Empty;
                }
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                parsed.Add((angle, title, description));
            }
        }

        return parsed;
    }

    private static string SummarizePreview(string value, int maxLength = 52)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var singleLine = string.Join(
            ' ',
            value.Replace("\r\n", "\n")
                 .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return singleLine.Length <= maxLength
            ? singleLine
            : singleLine[..(maxLength - 1)] + "…";
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
