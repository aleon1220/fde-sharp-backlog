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
        Assert.Null(options.JsonFileOut);
    }

    [Theory]
    [InlineData(new[] { "--json-out" }, true)]
    [InlineData(new[] { "--json-out", "true" }, true)]
    [InlineData(new[] { "--json-out", "false" }, false)]
    [InlineData(new[] { "--no-json-out" }, false)]
    public void ParseArgs_WithJsonOutFlags_ParsesExpectedJsonFileOutSetting(string[] args, bool expected)
    {
        var options = Program.ParseArgs(args);

        Assert.Equal(expected, options.JsonFileOut);
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
            "--model", "gemini-3.8-flash",
            "--gemini-live",
            "--type", "Type02",
            "--output-dir", "artifacts",
            "--non-interactive"
        ];

        var options = Program.ParseArgs(args);

        Assert.Equal("custom/notes.txt", options.InputFilePath);
        Assert.Equal("custom/prompt.md", options.PromptFilePath);
        Assert.Equal("gemini", options.ProviderInput);
        Assert.Equal("gem-12345", options.ApiKey);
        Assert.Equal("gemini-3.8-flash", options.ModelName);
        Assert.True(options.GeminiLive);
        Assert.Equal("Type02", options.BacklogTypeInput);
        Assert.Equal("artifacts", options.OutputDirectory);
        Assert.True(options.NonInteractive);
    }

    [Fact]
    public void ParseArgs_WithWorkspaceAndGoalArguments_ParsesWorkspaceAndGoalAndMenuFlags()
    {
        string[] args =
        [
            "--workspace", "/mnt/c/workspace",
            "--goal", "Add Redis Distributed Cache",
            "--type", "02",
            "--menu"
        ];

        var options = Program.ParseArgs(args);

        Assert.Equal("/mnt/c/workspace", options.WorkspaceRoot);
        Assert.Equal("Add Redis Distributed Cache", options.GoalTitle);
        Assert.Equal("02", options.BacklogTypeInput);
        Assert.True(options.ForceMenu);
    }

    [Fact]
    public void ParseArgs_WithListModelsFlag_EnablesGeminiModelListing()
    {
        var options = Program.ParseArgs(["--list-models", "--non-interactive"]);

        Assert.True(options.ListGeminiModels);
        Assert.True(options.NonInteractive);
    }

    [Fact]
    public void ShouldLaunchMenu_WhenDefaultInteractiveRun_ReturnsTrue()
    {
        string[] args = [];
        var options = Program.ParseArgs(args);

        Assert.True(Program.ShouldLaunchMenu(args, options));
    }

    [Fact]
    public void ShouldLaunchMenu_WhenNonInteractiveOrDirectFlagsProvided_ReturnsFalse()
    {
        string[] args = ["--type", "Type01", "--non-interactive"];
        var options = Program.ParseArgs(args);

        Assert.False(Program.ShouldLaunchMenu(args, options));
    }
}
