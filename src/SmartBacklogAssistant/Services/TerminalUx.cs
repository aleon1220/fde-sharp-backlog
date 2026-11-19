using System.Diagnostics;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Provides Astral <c>uv</c>-inspired terminal ergonomics: elapsed timers, non-intrusive
/// Braille progress spinners on interactive TTYs, crisp Unicode diagnostic tables,
/// and actionable error diagnostics.
/// </summary>
public static class TerminalUx
{
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalMilliseconds < 1000)
        {
            return $"{Math.Max(1, (int)Math.Round(elapsed.TotalMilliseconds))}ms";
        }

        return $"{elapsed.TotalSeconds:0.00}s";
    }

    public static void WriteStepDone(
        ConsoleColorWriter color,
        string actionVerb,
        string detail,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(color);
        color.WriteColored("  ✓ ", ConsoleColor.Green);
        color.WriteColored(actionVerb, ConsoleColor.White);
        color.WriteColored($" {detail}", ConsoleColor.Gray);
        color.WriteColored($" in {FormatElapsed(elapsed)}", ConsoleColor.DarkGray);
        color.WriteLine();
    }

    public static T RunWithSpinner<T>(
        string message,
        Func<T> work,
        ConsoleColorWriter color,
        bool allowSpinner = true)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(color);

        var useLiveSpinner = allowSpinner &&
                             ReferenceEquals(color.InnerWriter, Console.Out) &&
                             !Console.IsOutputRedirected;

        var stopwatch = Stopwatch.StartNew();

        if (!useLiveSpinner)
        {
            color.WriteMuted($"  ⋯ {message}");
            var directResult = work();
            stopwatch.Stop();
            return directResult;
        }

        var task = Task.Run(work);
        var frameIndex = 0;
        var lastLength = 0;

        while (!task.IsCompleted)
        {
            var frame = SpinnerFrames[frameIndex % SpinnerFrames.Length];
            var line = $"  {frame} {message} ({FormatElapsed(stopwatch.Elapsed)})";
            lastLength = Math.Max(lastLength, line.Length);
            color.Write($"\r{line.PadRight(lastLength)}");
            frameIndex++;
            Thread.Sleep(85);
        }

        color.Write($"\r{new string(' ', lastLength)}\r");
        stopwatch.Stop();
        return task.GetAwaiter().GetResult();
    }

    public static void WriteDiagnosticTable(
        ConsoleColorWriter color,
        string title,
        IReadOnlyList<(string Section, string Setting, string Value, ConsoleColor ValueColor)> rows)
    {
        ArgumentNullException.ThrowIfNull(color);
        ArgumentNullException.ThrowIfNull(rows);

        const int col1Width = 14;
        const int col2Width = 20;
        var col3Width = Math.Max(34, rows.Select(r => r.Value.Length).DefaultIfEmpty(34).Max());
        col3Width = Math.Min(col3Width, 82);

        var topBorder = $"┌{new string('─', col1Width + 2)}┬{new string('─', col2Width + 2)}┬{new string('─', col3Width + 2)}┐";
        var midBorder = $"├{new string('─', col1Width + 2)}┼{new string('─', col2Width + 2)}┼{new string('─', col3Width + 2)}┤";
        var botBorder = $"└{new string('─', col1Width + 2)}┴{new string('─', col2Width + 2)}┴{new string('─', col3Width + 2)}┘";

        color.WriteHeader(title);
        color.WriteLineColored(topBorder, color.GetSubHeaderColor());

        color.WriteColored("│ ", color.GetSubHeaderColor());
        color.WriteColored("Area".PadRight(col1Width), ConsoleColor.White);
        color.WriteColored(" │ ", color.GetSubHeaderColor());
        color.WriteColored("Setting".PadRight(col2Width), ConsoleColor.White);
        color.WriteColored(" │ ", color.GetSubHeaderColor());
        color.WriteColored("Status / Value".PadRight(col3Width), ConsoleColor.White);
        color.WriteLineColored(" │", color.GetSubHeaderColor());
        color.WriteLineColored(midBorder, color.GetSubHeaderColor());

        foreach (var (section, setting, value, valueColor) in rows)
        {
            var truncatedValue = TruncateMiddle(value, col3Width);

            color.WriteColored("│ ", color.GetSubHeaderColor());
            color.WriteColored(TruncatePad(section, col1Width), ConsoleColor.DarkGray);
            color.WriteColored(" │ ", color.GetSubHeaderColor());
            color.WriteColored(TruncatePad(setting, col2Width), ConsoleColor.White);
            color.WriteColored(" │ ", color.GetSubHeaderColor());
            color.WriteColored(truncatedValue.PadRight(col3Width), valueColor);
            color.WriteLineColored(" │", color.GetSubHeaderColor());
        }

        color.WriteLineColored(botBorder, color.GetSubHeaderColor());
    }

    private static string TruncateMiddle(string text, int maxWidth)
    {
        if (text.Length <= maxWidth)
        {
            return text;
        }

        if (maxWidth <= 6)
        {
            return text[..(maxWidth - 1)] + "…";
        }

        var tailLength = Math.Min(text.Length / 2, (maxWidth - 1) * 2 / 3);
        var headLength = maxWidth - 1 - tailLength;
        return $"{text[..headLength]}…{text[^tailLength..]}";
    }

    public static void WriteActionableError(ConsoleColorWriter errorWriter, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(errorWriter);
        ArgumentNullException.ThrowIfNull(ex);

        var root = ex is AggregateException agg && agg.InnerException is not null
            ? agg.InnerException
            : ex;

        errorWriter.WriteColored("error: ", ConsoleColor.Red);
        errorWriter.WriteLine(root.Message);

        var hint = ResolveActionableHint(root);
        if (!string.IsNullOrWhiteSpace(hint))
        {
            errorWriter.WriteColored("hint : ", ConsoleColor.Yellow);
            errorWriter.WriteLine(hint);
        }
    }

    public static string? ResolveActionableHint(Exception ex)
    {
        var msg = ex.Message;

        if (msg.Contains("Interactive selection", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("--select", StringComparison.OrdinalIgnoreCase))
        {
            return "Pass an explicit target with '--copy <clean|title|desc|story|ac|raw|md>' when running with '--non-interactive' or in a headless pipeline.";
        }

        if (ex is FileNotFoundException)
        {
            return "Run 'backlog status' to inspect ./in paths, or pass an explicit file with '-i <path>'.";
        }

        if (ex is HttpRequestException || ex is TimeoutException || ex is TaskCanceledException)
        {
            return "Check network/proxy connectivity or rerun with '--offline' for deterministic local drafting.";
        }

        if (msg.Contains("API key", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("403", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase))
        {
            return $"Set GEMINI_API_KEY (from {GeminiSecretProvider.GeminiApiKeysUrl}), pass '--api-key <key>', or use '--offline' / '--provider copilot-sdk'.";
        }

        if (msg.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            return "Provider rate limit reached. Wait briefly, switch models with '-m <model>', or use '--offline'.";
        }

        if (msg.Contains("Copilot", StringComparison.OrdinalIgnoreCase) &&
            (msg.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
             msg.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
             msg.Contains("login", StringComparison.OrdinalIgnoreCase)))
        {
            return "Run 'copilot login', set COPILOT_GITHUB_TOKEN / GITHUB_TOKEN + COPILOT_CLI_PATH / COPILOT_CLI_URL, build with '-p:CopilotSkipCliDownload=false', or pass '--offline'.";
        }

        if (msg.Contains("model", StringComparison.OrdinalIgnoreCase) &&
            (msg.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
             msg.Contains("not available", StringComparison.OrdinalIgnoreCase) ||
             msg.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
             msg.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
             msg.Contains("deprecated", StringComparison.OrdinalIgnoreCase) ||
             msg.Contains("retired", StringComparison.OrdinalIgnoreCase) ||
             msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase)))
        {
            var docs = msg.Contains("Copilot", StringComparison.OrdinalIgnoreCase)
                ? ModelCatalogService.CopilotSupportedModelsDocsUrl
                : msg.Contains("Azure", StringComparison.OrdinalIgnoreCase)
                    ? ModelCatalogService.AzureOpenAiModelsDocsUrl
                    : msg.Contains("Claude", StringComparison.OrdinalIgnoreCase) || msg.Contains("Anthropic", StringComparison.OrdinalIgnoreCase)
                        ? ModelCatalogService.AnthropicModelsDocsUrl
                        : ModelCatalogService.GeminiModelsDocsUrl;

            return $"Selected model may be retired, embedding/media-only, or restricted by your plan/region/policy. Official docs: {docs} — or run 'backlog models' / Workspace & Settings -> [1] Select AI Provider.";
        }

        return null;
    }

    private static string TruncatePad(string text, int width)
    {
        if (text.Length > width)
        {
            return text[..(width - 1)] + "…";
        }

        return text.PadRight(width);
    }
}
