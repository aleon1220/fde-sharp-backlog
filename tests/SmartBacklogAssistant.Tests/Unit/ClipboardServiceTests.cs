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
}
