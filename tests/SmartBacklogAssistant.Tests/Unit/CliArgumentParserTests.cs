namespace SmartBacklogAssistant.Tests.Unit;

public sealed class CliArgumentParserTests
{
    [Fact]
    public void ParseArgs_WithNoArguments_ReturnsDefaultOptions()
    {
        var options = Program.ParseArgs([]);

        Assert.Equal("samples/raw-notes.txt", options.InputFilePath);
        Assert.Equal("prompts/as-is-v1-prompt.md", options.PromptFilePath);
        Assert.Null(options.ProviderInput);
        Assert.Null(options.BacklogTypeInput);
        Assert.Equal("output", options.OutputDirectory);
        Assert.False(options.NonInteractive);
    }

    [Fact]
    public void ParseArgs_WithExplicitArguments_ParsesAllFlags()
    {
        string[] args =
        [
            "--input", "custom/notes.txt",
            "--prompt", "custom/prompt.md",
            "--provider", "gemini",
            "--api-key", "gem-12345",
            "--type", "Type02",
            "--output-dir", "artifacts",
            "--non-interactive"
        ];

        var options = Program.ParseArgs(args);

        Assert.Equal("custom/notes.txt", options.InputFilePath);
        Assert.Equal("custom/prompt.md", options.PromptFilePath);
        Assert.Equal("gemini", options.ProviderInput);
        Assert.Equal("gem-12345", options.ApiKey);
        Assert.Equal("Type02", options.BacklogTypeInput);
        Assert.Equal("artifacts", options.OutputDirectory);
        Assert.True(options.NonInteractive);
    }
}
