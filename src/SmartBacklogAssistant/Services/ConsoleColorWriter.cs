using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Represents a selectable menu row with an optional colourful value/status badge.
/// </summary>
public sealed record HighlightedMenuOption(
    string Key,
    string Label,
    string? BadgeText = null,
    ConsoleColor? BadgeForeground = null,
    ConsoleColor? BadgeBackground = null,
    string? Description = null,
    bool IsSelectedValue = false)
{
    internal int EstimateLength()
    {
        var len = 2 + Key.Length + 3 + Label.Length;
        if (!string.IsNullOrWhiteSpace(BadgeText))
        {
            len += BadgeText.Length + 3;
        }

        if (!string.IsNullOrWhiteSpace(Description))
        {
            len += Description.Length + 3;
        }

        return len;
    }
}

/// <summary>
/// Provides safe, cross-platform basic CLI coloring using <see cref="ConsoleColor"/>
/// with selectable CLI theme palettes (<c>agy</c> default, <c>uv</c>, <c>kubectl</c>)
/// that work across Windows Console, Windows Terminal, and WSL/Linux terminals
/// while degrading gracefully when output is redirected or in unit tests.
/// </summary>
public sealed class ConsoleColorWriter
{
    private readonly TextWriter _output;
    private readonly bool _useConsoleColor;

    public ConsoleColorWriter(
        TextWriter output,
        bool? enableColor = null,
        CliThemeStyle theme = CliThemeStyle.Agy)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        Theme = theme;

        var noColorEnv = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NO_COLOR"));
        _useConsoleColor = enableColor ?? (!noColorEnv && ReferenceEquals(output, Console.Out) && !Console.IsOutputRedirected);
    }

    public TextWriter InnerWriter => _output;

    public bool IsColorEnabled => _useConsoleColor;

    public CliThemeStyle Theme { get; set; }

    public static CliThemeStyle ResolveTheme(string? themeInput, CliThemeStyle fallback = CliThemeStyle.Agy)
    {
        if (string.IsNullOrWhiteSpace(themeInput))
        {
            return fallback;
        }

        return themeInput.Trim().ToLowerInvariant() switch
        {
            "1" or "agy" or "gemini" or "gemini-agy" or "default" => CliThemeStyle.Agy,
            "2" or "uv" or "astral" or "astral-uv" => CliThemeStyle.Uv,
            "3" or "kubectl" or "k8s" or "kube" => CliThemeStyle.Kubectl,
            _ => fallback
        };
    }

    public static string GetThemeCode(CliThemeStyle theme) => theme switch
    {
        CliThemeStyle.Uv => "uv",
        CliThemeStyle.Kubectl => "kubectl",
        _ => "agy"
    };

    public static string FormatThemeDisplay(CliThemeStyle theme) => theme switch
    {
        CliThemeStyle.Uv => "uv (Astral uv Style)",
        CliThemeStyle.Kubectl => "kubectl (Kubernetes CLI Style)",
        _ => "agy (Gemini Agy Style — Default)"
    };

    public void WriteLine() => _output.WriteLine();

    public void WriteLine(string message) => _output.WriteLine(message);

    public void Write(string message) => _output.Write(message);

    public void WriteColored(string message, ConsoleColor color)
    {
        if (!_useConsoleColor)
        {
            _output.Write(message);
            return;
        }

        try
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            try
            {
                _output.Write(message);
            }
            finally
            {
                Console.ForegroundColor = previous;
            }
        }
        catch (IOException)
        {
            _output.Write(message);
        }
    }

    public void WriteLineColored(string message, ConsoleColor color)
    {
        WriteColored(message, color);
        _output.WriteLine();
    }

    public void WriteHighlighted(string message, ConsoleColor foreground, ConsoleColor background)
    {
        if (!_useConsoleColor)
        {
            _output.Write(message);
            return;
        }

        try
        {
            var previousFg = Console.ForegroundColor;
            var previousBg = Console.BackgroundColor;
            Console.ForegroundColor = foreground;
            Console.BackgroundColor = background;
            try
            {
                _output.Write(message);
            }
            finally
            {
                Console.ForegroundColor = previousFg;
                Console.BackgroundColor = previousBg;
            }
        }
        catch (IOException)
        {
            _output.Write(message);
        }
    }

    public void WriteSelectableMenuRow(
        HighlightedMenuOption option,
        bool isCursorHighlighted,
        int minRowWidth = 0,
        int maxRowWidth = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(option);

        var prefix = isCursorHighlighted ? "❯ " : "  ";
        var keyPart = $"[{option.Key}] ";
        var labelPart = option.Label;
        var hasBadge = !string.IsNullOrWhiteSpace(option.BadgeText);
        var badgeBody = hasBadge ? $"[{option.BadgeText}]" : string.Empty;
        var badgePart = hasBadge ? $" {badgeBody}" : string.Empty;
        var descPart = string.IsNullOrWhiteSpace(option.Description) ? string.Empty : $" — {option.Description}";

        var baseLen = prefix.Length + keyPart.Length + labelPart.Length + badgePart.Length;
        if (maxRowWidth < int.MaxValue && baseLen > maxRowWidth)
        {
            var availableLabel = Math.Max(4, maxRowWidth - prefix.Length - keyPart.Length - badgePart.Length - 1);
            if (labelPart.Length > availableLabel)
            {
                labelPart = labelPart[..availableLabel] + "…";
            }

            baseLen = prefix.Length + keyPart.Length + labelPart.Length + badgePart.Length;
        }

        if (maxRowWidth < int.MaxValue && !string.IsNullOrEmpty(descPart) && baseLen + descPart.Length > maxRowWidth)
        {
            var availableDesc = maxRowWidth - baseLen;
            descPart = availableDesc >= 8
                ? descPart[..(availableDesc - 1)] + "…"
                : string.Empty;
        }

        var renderedLen = baseLen + descPart.Length;
        var trailingPad = minRowWidth > renderedLen
            ? new string(' ', minRowWidth - renderedLen)
            : string.Empty;

        var (defaultBadgeFg, defaultBadgeBg) = GetValueBadgeColors();
        var badgeFg = option.BadgeForeground ?? defaultBadgeFg;
        var badgeBg = option.BadgeBackground ?? defaultBadgeBg;

        if (isCursorHighlighted)
        {
            var (hlFg, hlBg) = GetHighlightColors();
            WriteHighlighted($"{prefix}{keyPart}{labelPart}", hlFg, hlBg);
            if (hasBadge)
            {
                WriteHighlighted(" ", hlFg, hlBg);
                WriteHighlighted(badgeBody, badgeFg, badgeBg);
            }

            if (!string.IsNullOrEmpty(descPart) || !string.IsNullOrEmpty(trailingPad))
            {
                WriteHighlighted($"{descPart}{trailingPad}", hlFg, hlBg);
            }

            _output.WriteLine();
            return;
        }

        WriteColored(prefix, ConsoleColor.DarkGray);
        WriteColored(keyPart, GetOptionKeyColor());
        WriteColored(labelPart, option.IsSelectedValue ? GetHeaderColor() : ConsoleColor.White);
        if (hasBadge)
        {
            _output.Write(' ');
            WriteHighlighted(badgeBody, badgeFg, badgeBg);
        }

        if (!string.IsNullOrEmpty(descPart))
        {
            WriteColored(descPart, option.IsSelectedValue ? GetSubHeaderColor() : ConsoleColor.DarkGray);
        }

        if (!string.IsNullOrEmpty(trailingPad))
        {
            _output.Write(trailingPad);
        }

        _output.WriteLine();
    }

    public void WriteHeader(string title) => WriteLineColored(title, GetHeaderColor());

    public void WriteSubHeader(string title) => WriteLineColored(title, GetSubHeaderColor());

    public void WriteSuccess(string message) => WriteLineColored(message, ConsoleColor.Green);

    public void WriteWarning(string message) => WriteLineColored(message, ConsoleColor.Yellow);

    public void WriteError(string message) => WriteLineColored(message, ConsoleColor.Red);

    public void WriteMuted(string message) => WriteLineColored(message, ConsoleColor.DarkGray);

    public void WriteMenuOption(string key, string label, string? description = null)
    {
        WriteColored($"  [{key}] ", GetOptionKeyColor());
        WriteColored(label, ConsoleColor.White);
        if (!string.IsNullOrWhiteSpace(description))
        {
            WriteColored($" — {description}", ConsoleColor.DarkGray);
        }

        _output.WriteLine();
    }

    public void WriteMenuOptionWithSlash(
        string key,
        string label,
        string? description,
        string slashCommand,
        int labelWidth = 22,
        int descWidth = 36)
    {
        WriteColored($"  [{key}] ", GetOptionKeyColor());
        WriteColored(label.PadRight(labelWidth), ConsoleColor.White);
        if (!string.IsNullOrWhiteSpace(description))
        {
            WriteColored($"— {description}".PadRight(descWidth), ConsoleColor.DarkGray);
        }
        else
        {
            WriteColored(string.Empty.PadRight(descWidth), ConsoleColor.DarkGray);
        }

        WriteColored($" ({slashCommand})", GetSubHeaderColor());
        _output.WriteLine();
    }

    public ConsoleColor GetHeaderColor() => Theme switch
    {
        CliThemeStyle.Uv => ConsoleColor.Magenta,
        CliThemeStyle.Kubectl => ConsoleColor.Blue,
        _ => ConsoleColor.Cyan
    };

    public ConsoleColor GetSubHeaderColor() => Theme switch
    {
        CliThemeStyle.Uv => ConsoleColor.Cyan,
        CliThemeStyle.Kubectl => ConsoleColor.DarkCyan,
        _ => ConsoleColor.Blue
    };

    public ConsoleColor GetOptionKeyColor() => Theme switch
    {
        CliThemeStyle.Uv => ConsoleColor.Green,
        CliThemeStyle.Kubectl => ConsoleColor.Cyan,
        _ => ConsoleColor.Yellow
    };

    public (ConsoleColor Foreground, ConsoleColor Background) GetHighlightColors() => Theme switch
    {
        CliThemeStyle.Uv => (ConsoleColor.Black, ConsoleColor.Green),
        CliThemeStyle.Kubectl => (ConsoleColor.White, ConsoleColor.DarkCyan),
        _ => (ConsoleColor.Black, ConsoleColor.Cyan)
    };

    public (ConsoleColor Foreground, ConsoleColor Background) GetValueBadgeColors() => Theme switch
    {
        CliThemeStyle.Uv => (ConsoleColor.Black, ConsoleColor.Cyan),
        _ => (ConsoleColor.Black, ConsoleColor.Yellow)
    };

    public static (ConsoleColor Foreground, ConsoleColor Background) GetToggleBadgeColors(bool isEnabled) =>
        isEnabled
            ? (ConsoleColor.Black, ConsoleColor.Green)
            : (ConsoleColor.White, ConsoleColor.DarkRed);
}
