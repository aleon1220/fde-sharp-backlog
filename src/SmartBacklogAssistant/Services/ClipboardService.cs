using System.Diagnostics;
using System.Text;

namespace SmartBacklogAssistant.Services;

public sealed record ClipboardCopyResult(
    bool Succeeded,
    string Method,
    string Message);

public interface IClipboardService
{
    ClipboardCopyResult CopyText(string text);
}

/// <summary>
/// Cross-platform OS clipboard writer using native system utilities
/// (<c>Set-Clipboard</c>/<c>clip.exe</c> on Windows and WSL2, <c>pbcopy</c> on macOS,
/// <c>wl-copy</c>/<c>xclip</c>/<c>xsel</c> on Linux) with ANSI OSC 52 terminal fallback
/// and zero external NuGet dependencies.
/// </summary>
public sealed class OsClipboardService : IClipboardService
{
    private readonly Func<string, string?> _environmentReader;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string, string, bool> _commandRunner;
    private readonly TextWriter? _osc52Writer;
    private readonly Func<bool>? _osc52Allowed;

    public OsClipboardService(
        Func<string, string?>? environmentReader = null,
        Func<string, bool>? fileExists = null,
        Func<string, string, string, bool>? commandRunner = null,
        TextWriter? osc52Writer = null,
        Func<bool>? osc52Allowed = null)
    {
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
        _fileExists = fileExists ?? File.Exists;
        _commandRunner = commandRunner ?? ExecuteClipboardCommand;
        _osc52Writer = osc52Writer;
        _osc52Allowed = osc52Allowed;
    }

    public ClipboardCopyResult CopyText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new ClipboardCopyResult(false, "none", "Nothing to copy (outcome text is empty).");
        }

        foreach (var (executable, arguments, label) in ResolveCandidates())
        {
            if (_commandRunner(executable, arguments, text))
            {
                return new ClipboardCopyResult(true, label, $"Copied to clipboard via {label}.");
            }
        }

        if (CanUseOsc52Fallback())
        {
            var isTmux = !string.IsNullOrWhiteSpace(_environmentReader("TMUX"));
            var sequence = FormatOsc52Sequence(text, isTmux);
            try
            {
                var writer = _osc52Writer ?? Console.Error;
                writer.Write(sequence);
                writer.Flush();
                var method = isTmux ? "OSC 52 (tmux)" : "OSC 52";
                return new ClipboardCopyResult(true, method, $"Copied to clipboard via {method}.");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return new ClipboardCopyResult(false, "none", $"Failed to write OSC 52 clipboard sequence: {ex.Message}");
            }
        }

        return new ClipboardCopyResult(
            false,
            "none",
            "No active OS clipboard utility detected in this terminal session.");
    }

    public static string FormatOsc52Sequence(string text, bool isTmux = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        return isTmux
            ? $"\u001bPtmux;\u001b\u001b]52;c;{base64}\a\u001b\\"
            : $"\u001b]52;c;{base64}\a";
    }

    internal bool CanUseOsc52Fallback()
    {
        if (_osc52Allowed is not null)
        {
            return _osc52Allowed();
        }

        var overrideFlag = _environmentReader("SBA_OSC52")?.Trim().ToLowerInvariant();
        if (overrideFlag is "1" or "on" or "true")
        {
            return true;
        }

        if (overrideFlag is "0" or "off" or "false")
        {
            return false;
        }

        var term = _environmentReader("TERM");
        if (string.Equals(term?.Trim(), "dumb", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(term) ||
               !string.IsNullOrWhiteSpace(_environmentReader("WT_SESSION")) ||
               !string.IsNullOrWhiteSpace(_environmentReader("TERM_PROGRAM")) ||
               !string.IsNullOrWhiteSpace(_environmentReader("SSH_TTY")) ||
               !string.IsNullOrWhiteSpace(_environmentReader("TMUX"));
    }

    internal IReadOnlyList<(string Executable, string Arguments, string Label)> ResolveCandidates()
    {
        var candidates = new List<(string Executable, string Arguments, string Label)>();

        if (OperatingSystem.IsWindows())
        {
            candidates.Add(("powershell.exe", "-NoProfile -Sta -Command \"[Console]::InputEncoding = [System.Text.Encoding]::UTF8; Set-Clipboard -Value ([Console]::In.ReadToEnd())\"", "Windows PowerShell"));
            candidates.Add(("clip.exe", string.Empty, "Windows clip.exe"));
            return candidates;
        }

        if (OperatingSystem.IsMacOS())
        {
            candidates.Add(("pbcopy", string.Empty, "macOS pbcopy"));
            return candidates;
        }

        // Linux / WSL2: try Windows clipboard interop when available, plus Wayland/X11 tools
        const string wslPowerShell = "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe";
        const string wslClip = "/mnt/c/Windows/System32/clip.exe";
        if (_fileExists(wslPowerShell))
        {
            candidates.Add((wslPowerShell, "-NoProfile -Sta -Command \"[Console]::InputEncoding = [System.Text.Encoding]::UTF8; Set-Clipboard -Value ([Console]::In.ReadToEnd())\"", "WSL -> Windows PowerShell"));
        }
        else if (_fileExists(wslClip))
        {
            candidates.Add((wslClip, string.Empty, "WSL -> Windows clip.exe"));
        }

        if (!string.IsNullOrWhiteSpace(_environmentReader("WAYLAND_DISPLAY")))
        {
            candidates.Add(("wl-copy", string.Empty, "wl-copy"));
        }

        if (!string.IsNullOrWhiteSpace(_environmentReader("DISPLAY")))
        {
            candidates.Add(("sh", "-c \"xclip -selection clipboard >/dev/null 2>&1\"", "xclip"));
            candidates.Add(("sh", "-c \"xsel --clipboard --input >/dev/null 2>&1\"", "xsel"));
        }

        return candidates;
    }

    private static bool ExecuteClipboardCommand(string executable, string arguments, string text)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                return false;
            }

            process.StandardInput.Write(text);
            process.StandardInput.Flush();
            process.StandardInput.Close();

            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
