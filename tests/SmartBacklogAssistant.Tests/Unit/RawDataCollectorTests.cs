using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Unit;

public sealed class RawDataCollectorTests
{
    private readonly RawDataCollector _sut = new();

    [Fact]
    public void CollectFromText_WithValidRawNotes_TrimsAndNormalizesNewlines()
    {
        var input = "  Title: Export CSV\r\nContext: Large dataset  \r\n";

        var result = _sut.CollectFromText(input);

        Assert.Equal("Title: Export CSV\nContext: Large dataset", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void CollectFromText_WithEmptyOrWhitespace_ThrowsInvalidOperationException(string invalidInput)
    {
        Assert.Throws<InvalidOperationException>(() => _sut.CollectFromText(invalidInput));
    }

    [Fact]
    public void CollectFromFile_WhenFileDoesNotExist_ThrowsFileNotFoundException()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.txt");

        Assert.Throws<FileNotFoundException>(() => _sut.CollectFromFile(missingPath));
    }
}
