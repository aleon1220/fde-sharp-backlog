namespace SmartBacklogAssistant.Services;

/// <summary>
/// Provides safe, cross-platform basic CLI coloring using <see cref="ConsoleColor"/>
/// that works across Windows Console, Windows Terminal, and WSL/Linux terminals
/// while degrading gracefully when output is redirected or in unit tests.
/// </summary>
public sealed class ConsoleColorWriter
{
    private readonly TextWriter _output;
    private readonly bool _useConsoleColor;

    public ConsoleColorWriter(TextWriter output, bool? enableColor = null)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));

        var noColorEnv = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NO_COLOR"));
        _useConsoleColor = enableColor ?? (!noColorEnv && ReferenceEquals(output, Console.Out) && !Console.IsOutputRedirected);
    }

    public TextWriter InnerWriter => _output;

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

    public void WriteHeader(string title) => WriteLineColored(title, ConsoleColor.Cyan);

    public void WriteSubHeader(string title) => WriteLineColored(title, ConsoleColor.Blue);

    public void WriteSuccess(string message) => WriteLineColored(message, ConsoleColor.Green);

    public void WriteWarning(string message) => WriteLineColored(message, ConsoleColor.Yellow);

    public void WriteError(string message) => WriteLineColored(message, ConsoleColor.Red);

    public void WriteMuted(string message) => WriteLineColored(message, ConsoleColor.DarkGray);

    public void WriteMenuOption(string key, string label, string? description = null)
    {
        WriteColored($"  [{key}] ", ConsoleColor.Yellow);
        WriteColored(label, ConsoleColor.White);
        if (!string.IsNullOrWhiteSpace(description))
        {
            WriteColored($" — {description}", ConsoleColor.DarkGray);
        }

        _output.WriteLine();
    }
}
