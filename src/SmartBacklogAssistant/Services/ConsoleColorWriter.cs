using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

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

    private ConsoleColor GetHeaderColor() => Theme switch
    {
        CliThemeStyle.Uv => ConsoleColor.Magenta,
        CliThemeStyle.Kubectl => ConsoleColor.Blue,
        _ => ConsoleColor.Cyan
    };

    private ConsoleColor GetSubHeaderColor() => Theme switch
    {
        CliThemeStyle.Uv => ConsoleColor.Cyan,
        CliThemeStyle.Kubectl => ConsoleColor.DarkCyan,
        _ => ConsoleColor.Blue
    };

    private ConsoleColor GetOptionKeyColor() => Theme switch
    {
        CliThemeStyle.Uv => ConsoleColor.Green,
        CliThemeStyle.Kubectl => ConsoleColor.Cyan,
        _ => ConsoleColor.Yellow
    };
}
