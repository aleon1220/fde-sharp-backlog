using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class ConsoleColorWriterTests
{
    [Theory]
    [InlineData(CliThemeStyle.Agy, ConsoleColor.Black, ConsoleColor.Cyan)]
    [InlineData(CliThemeStyle.Uv, ConsoleColor.Black, ConsoleColor.Green)]
    [InlineData(CliThemeStyle.Kubectl, ConsoleColor.White, ConsoleColor.DarkCyan)]
    public void GetHighlightColors_ReturnsThemeAppropriateHighContrastPalette(
        CliThemeStyle theme,
        ConsoleColor expectedFg,
        ConsoleColor expectedBg)
    {
        using var writer = new StringWriter();
        var color = new ConsoleColorWriter(writer, enableColor: false, theme: theme);

        var (fg, bg) = color.GetHighlightColors();

        Assert.Equal(expectedFg, fg);
        Assert.Equal(expectedBg, bg);
    }

    [Fact]
    public void GetToggleBadgeColors_ReturnsDistinctGreenAndRedBadges()
    {
        var (onFg, onBg) = ConsoleColorWriter.GetToggleBadgeColors(isEnabled: true);
        var (offFg, offBg) = ConsoleColorWriter.GetToggleBadgeColors(isEnabled: false);

        Assert.Equal(ConsoleColor.Black, onFg);
        Assert.Equal(ConsoleColor.Green, onBg);
        Assert.Equal(ConsoleColor.White, offFg);
        Assert.Equal(ConsoleColor.DarkRed, offBg);
    }

    [Fact]
    public void WriteSelectableMenuRow_RendersCursorMarkerBadgeAndPaddingWhenHighlighted()
    {
        using var writer = new StringWriter();
        var color = new ConsoleColorWriter(writer, enableColor: false);
        var option = new HighlightedMenuOption(
            "4",
            "Select CLI Theme",
            BadgeText: "uv",
            Description: "Switch agy, uv, or kubectl palette",
            IsSelectedValue: true);

        color.WriteSelectableMenuRow(option, isCursorHighlighted: true, minRowWidth: 70, maxRowWidth: 90);
        color.WriteSelectableMenuRow(option, isCursorHighlighted: false);

        var lines = writer.ToString().Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("❯ [4] Select CLI Theme [uv] — Switch agy, uv, or kubectl palette", lines[0]);
        Assert.Equal(70, lines[0].Length);
        Assert.Equal("  [4] Select CLI Theme [uv] — Switch agy, uv, or kubectl palette", lines[1]);
    }
}
