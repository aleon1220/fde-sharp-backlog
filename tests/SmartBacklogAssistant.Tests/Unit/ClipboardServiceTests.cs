using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class ClipboardServiceTests
{
    [Fact]
    public void CopyText_WithEmptyText_ReturnsFailureWithoutInvokingProcess()
    {
        var invoked = false;
        var service = new OsClipboardService(
            commandRunner: (_, _, _) =>
            {
                invoked = true;
                return true;
            });

        var result = service.CopyText(string.Empty);

        Assert.False(result.Succeeded);
        Assert.False(invoked);
    }

    [Fact]
    public void CopyText_WhenClipboardToolAvailable_WritesTextAndReturnsSuccess()
    {
        string? capturedText = null;
        var service = new OsClipboardService(
            environmentReader: key => key == "DISPLAY" ? ":0" : null,
            fileExists: path => path.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase),
            commandRunner: (_, _, text) =>
            {
                capturedText = text;
                return true;
            });

        var result = service.CopyText("hello\n* bullet");

        Assert.True(result.Succeeded);
        Assert.Equal("hello\n* bullet", capturedText);
    }

    [Fact]
    public void CopyText_WhenNoClipboardToolWorks_ReturnsGracefulFailure()
    {
        var service = new OsClipboardService(
            environmentReader: _ => null,
            fileExists: _ => false,
            commandRunner: (_, _, _) => false);

        var result = service.CopyText("payload");

        Assert.False(result.Succeeded);
        Assert.Equal("none", result.Method);
    }

    [Fact]
    public void FormatOsc52Sequence_EncodesBase64ForStandardAndTmuxTerminals()
    {
        var standard = OsClipboardService.FormatOsc52Sequence("hello", isTmux: false);
        var tmux = OsClipboardService.FormatOsc52Sequence("hello", isTmux: true);

        Assert.Equal("\u001b]52;c;aGVsbG8=\a", standard);
        Assert.Equal("\u001bPtmux;\u001b\u001b]52;c;aGVsbG8=\a\u001b\\", tmux);
    }

    [Fact]
    public void CopyText_WhenNativeToolsFailAndTerminalSupportsOsc52_EmitsOsc52Sequence()
    {
        using var standardWriter = new StringWriter();
        var standardService = new OsClipboardService(
            environmentReader: key => key == "TERM" ? "xterm-256color" : null,
            fileExists: _ => false,
            commandRunner: (_, _, _) => false,
            osc52Writer: standardWriter);

        var standardResult = standardService.CopyText("Clean Title");

        Assert.True(standardResult.Succeeded);
        Assert.Equal("OSC 52", standardResult.Method);
        Assert.Equal(OsClipboardService.FormatOsc52Sequence("Clean Title", isTmux: false), standardWriter.ToString());

        using var tmuxWriter = new StringWriter();
        var tmuxService = new OsClipboardService(
            environmentReader: key => key is "TERM" or "TMUX" ? "tmux-256color" : null,
            fileExists: _ => false,
            commandRunner: (_, _, _) => false,
            osc52Writer: tmuxWriter);

        var tmuxResult = tmuxService.CopyText("Clean Title");

        Assert.True(tmuxResult.Succeeded);
        Assert.Equal("OSC 52 (tmux)", tmuxResult.Method);
        Assert.Equal(OsClipboardService.FormatOsc52Sequence("Clean Title", isTmux: true), tmuxWriter.ToString());
    }
}
