using System.Text;
using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Keeps backlog item descriptions simple, concise (at most 57 words by default),
/// and consistently formatted with <c>*</c> markdown bullet points for suggested actions.
/// </summary>
public static class DescriptionFormatter
{
    public const int MaxMinimalisticWords = 25;
    public const int MaxDescriptionWords = 57;
    public const int MaxDetailedWords = 95;

    public static int ResolveMaxWords(OutcomeVerbosity verbosity) => verbosity switch
    {
        OutcomeVerbosity.Minimalistic => MaxMinimalisticWords,
        OutcomeVerbosity.Detailed => MaxDetailedWords,
        _ => MaxDescriptionWords
    };

    public static string FormatVerbosityLabel(OutcomeVerbosity verbosity) => verbosity switch
    {
        OutcomeVerbosity.Minimalistic => $"Minimalistic (<={MaxMinimalisticWords}w)",
        OutcomeVerbosity.Detailed => $"Detailed (<={MaxDetailedWords}w)",
        _ => $"Balanced (<={MaxDescriptionWords}w)"
    };

    public static OutcomeVerbosity ResolveVerbosity(string? input, OutcomeVerbosity fallback = OutcomeVerbosity.Balanced)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return fallback;
        }

        return input.Trim().ToLowerInvariant() switch
        {
            "1" or "min" or "minimal" or "minimalistic" or "short" or "brief" => OutcomeVerbosity.Minimalistic,
            "2" or "bal" or "balanced" or "standard" or "default" or "normal" => OutcomeVerbosity.Balanced,
            "3" or "det" or "detailed" or "full" or "verbose" or "expanded" => OutcomeVerbosity.Detailed,
            _ => fallback
        };
    }

    public static string FormatWithVerbosity(string? description, OutcomeVerbosity verbosity)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        if (verbosity == OutcomeVerbosity.Minimalistic)
        {
            var lines = NormalizeBulletAndBlankLines(description);
            var compactLines = new List<string>();
            var bulletCount = 0;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || IsHeaderOnlyLine(line))
                {
                    continue;
                }

                if (line.TrimStart().StartsWith("* ", StringComparison.Ordinal))
                {
                    if (bulletCount >= 2)
                    {
                        continue;
                    }

                    bulletCount++;
                }

                compactLines.Add(line);
            }

            var joinedCompact = compactLines.Count > 0
                ? string.Join('\n', compactLines)
                : description;
            return Normalize(joinedCompact, MaxMinimalisticWords);
        }

        return Normalize(description, ResolveMaxWords(verbosity));
    }

    public static int CountWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    public static string FormatWordBudgetBadge(string? description, int maxWords = MaxDescriptionWords) =>
        $"{CountWords(description)}/{maxWords} words";

    public static string FormatAsBullet(string item)
    {
        var trimmed = item.Trim();
        if (trimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            return $"* {trimmed[2..].TrimStart()}";
        }

        if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
            trimmed.StartsWith("• ", StringComparison.Ordinal))
        {
            return $"* {trimmed[2..].TrimStart()}";
        }

        return $"* {trimmed}";
    }

    public static string Normalize(string? description, int maxWords = MaxDescriptionWords)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWords);

        var normalizedLines = NormalizeBulletAndBlankLines(description);
        var joined = string.Join('\n', normalizedLines).Trim();
        if (CountWords(joined) <= maxWords)
        {
            return joined;
        }

        return EnforceMaxWords(normalizedLines, maxWords);
    }

    private static List<string> NormalizeBulletAndBlankLines(string text)
    {
        var rawLines = text.Replace("\r\n", "\n").Trim().Split('\n');
        var result = new List<string>(rawLines.Length);
        var previousBlank = false;

        foreach (var rawLine in rawLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                if (result.Count > 0 && !previousBlank)
                {
                    result.Add(string.Empty);
                    previousBlank = true;
                }

                continue;
            }

            previousBlank = false;
            result.Add(NormalizeLineBullet(rawLine.TrimEnd()));
        }

        while (result.Count > 0 && string.IsNullOrEmpty(result[^1]))
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private static string NormalizeLineBullet(string line)
    {
        var indentLength = 0;
        while (indentLength < line.Length && line[indentLength] == ' ')
        {
            indentLength++;
        }

        var indent = indentLength > 0 ? line[..indentLength] : string.Empty;
        var trimmed = line[indentLength..];

        if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
            trimmed.StartsWith("• ", StringComparison.Ordinal) ||
            trimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            return $"{indent}{FormatAsBullet(trimmed)}";
        }

        return line;
    }

    private static string EnforceMaxWords(List<string> lines, int maxWords)
    {
        var kept = new List<string>();
        var usedWords = 0;
        var hasBullet = false;

        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                if (kept.Count > 0 && !string.IsNullOrEmpty(kept[^1]))
                {
                    kept.Add(string.Empty);
                }

                continue;
            }

            var lineWords = CountWords(line);
            if (usedWords + lineWords <= maxWords)
            {
                kept.Add(line);
                usedWords += lineWords;
                if (line.TrimStart().StartsWith("* ", StringComparison.Ordinal))
                {
                    hasBullet = true;
                }

                continue;
            }

            if (kept.Count == 0)
            {
                kept.Add(TruncateToMaxWords(line, maxWords));
                break;
            }

            var remaining = maxWords - usedWords;
            var isBulletLine = line.TrimStart().StartsWith("* ", StringComparison.Ordinal);
            if (!hasBullet && !isBulletLine && !IsHeaderOnlyLine(line) && remaining >= 6)
            {
                kept.Add(TruncateToMaxWords(line, remaining));
            }

            break;
        }

        while (kept.Count > 0 && (string.IsNullOrEmpty(kept[^1]) || IsHeaderOnlyLine(kept[^1])))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        if (kept.Count == 0 && lines.Count > 0)
        {
            kept.Add(TruncateToMaxWords(lines[0], maxWords));
        }

        var result = string.Join('\n', kept).Trim();
        return CountWords(result) <= maxWords
            ? result
            : TruncateToMaxWords(result, maxWords);
    }

    private static bool IsHeaderOnlyLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.EndsWith(":", StringComparison.Ordinal) ||
               trimmed.EndsWith(":**", StringComparison.Ordinal);
    }

    private static string TruncateToMaxWords(string text, int maxWords)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length <= maxWords)
        {
            return text.Trim();
        }

        var sentenceEndIndex = -1;
        var minSentenceBoundary = Math.Max(1, maxWords / 2);
        for (var i = maxWords - 1; i >= minSentenceBoundary; i--)
        {
            var token = tokens[i];
            if ((token.EndsWith('.') || token.EndsWith('!') || token.EndsWith('?')) &&
                !token.EndsWith("...", StringComparison.Ordinal))
            {
                sentenceEndIndex = i;
                break;
            }
        }

        if (sentenceEndIndex >= 0)
        {
            return CloseUnbalancedBold(string.Join(' ', tokens[..(sentenceEndIndex + 1)]));
        }

        var slice = tokens[..maxWords];
        var lastIndex = slice.Length - 1;
        while (lastIndex > 0 && IsHeaderOnlyLine(slice[lastIndex]))
        {
            lastIndex--;
        }

        var prefix = string.Join(' ', slice[..lastIndex]);
        var lastToken = slice[lastIndex].TrimEnd(',', ';', ':', '-', '—');
        if (string.IsNullOrEmpty(lastToken))
        {
            return CloseUnbalancedBold(prefix);
        }

        if (!lastToken.EndsWith('.') &&
            !lastToken.EndsWith('!') &&
            !lastToken.EndsWith('?') &&
            !lastToken.EndsWith("...", StringComparison.Ordinal))
        {
            lastToken += "...";
        }

        var combined = string.IsNullOrEmpty(prefix) ? lastToken : $"{prefix} {lastToken}";
        return CloseUnbalancedBold(combined);
    }

    private static string CloseUnbalancedBold(string text)
    {
        var markerCount = 0;
        var index = 0;
        while ((index = text.IndexOf("**", index, StringComparison.Ordinal)) >= 0)
        {
            markerCount++;
            index += 2;
        }

        if (markerCount % 2 == 0)
        {
            return text;
        }

        var lastMarker = text.LastIndexOf("**", StringComparison.Ordinal);
        return lastMarker >= 0 ? text.Remove(lastMarker, 2) : text;
    }
}
