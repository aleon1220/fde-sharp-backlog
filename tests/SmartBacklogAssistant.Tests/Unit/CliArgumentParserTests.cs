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
        Assert.Null(options.GeminiLive);
    }

    [Fact]
    public void ParseArgs_WithLiveAndOfflineFlags_ParsesGeminiLiveSetting()
    {
        Assert.True(Program.ParseArgs(["--gemini-live"]).GeminiLive);
        Assert.False(Program.ParseArgs(["--offline"]).GeminiLive);
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

    [Fact]
    public void ParseArgs_WithDraftSubcommandAndShortFlags_ParsesGoalTypeOutputAndDryRun()
    {
        string[] args = ["draft", "Implement Ergonomic CLI", "-t", "both", "-o", "json", "--dry-run"];

        var options = Program.ParseArgs(args);

        Assert.Equal("draft", options.Subcommand);
        Assert.Equal("Implement Ergonomic CLI", options.GoalTitle);
        Assert.Equal("both", options.BacklogTypeInput);
        Assert.True(options.CombinedOutput);
        Assert.True(options.JsonFileOut);
        Assert.True(options.DryRun);
        Assert.False(Program.ShouldLaunchMenu(args, options));
    }

    [Theory]
    [InlineData("doctor")]
    [InlineData("status")]
    [InlineData("--doctor")]
    public void ParseArgs_WithDoctorOrStatus_EnablesShowDoctorStatusAndSkipsMenu(string arg)
    {
        string[] args = [arg];

        var options = Program.ParseArgs(args);

        Assert.True(options.ShowDoctorStatus);
        Assert.False(Program.ShouldLaunchMenu(args, options));
    }

    [Fact]
    public void ParseArgs_WithRunSubcommandAndPositionalFile_UsesPositionalInputFilePath()
    {
        string[] args = ["run", "samples/custom-notes.md", "-t", "02"];

        var options = Program.ParseArgs(args);

        Assert.Equal("run", options.Subcommand);
        Assert.Equal("samples/custom-notes.md", options.InputFilePath);
        Assert.Equal("02", options.BacklogTypeInput);
        Assert.False(Program.ShouldLaunchMenu(args, options));
    }

    [Fact]
    public void ParseArgs_WithProcessSubcommandAndFlagsBeforeOrAfterPositional_ParsesTargetDryRunAndJson()
    {
        string[] args = ["process", "--dry-run", "all", "--json"];

        var options = Program.ParseArgs(args);

        Assert.Equal("run", options.Subcommand);
        Assert.Equal("all", options.InputFilePath);
        Assert.True(options.DryRun);
        Assert.True(options.JsonFileOut);
        Assert.False(Program.ShouldLaunchMenu(args, options));
    }

    [Fact]
    public void ParseArgs_WithDraftFlagsBeforeGoalAndPreviewFlag_ParsesGoalAndPreview()
    {
        string[] args = ["draft", "--type", "02", "Add Redis Cache", "--preview"];

        var options = Program.ParseArgs(args);

        Assert.Equal("draft", options.Subcommand);
        Assert.Equal("Add Redis Cache", options.GoalTitle);
        Assert.Equal("02", options.BacklogTypeInput);
        Assert.True(options.DryRun);
        Assert.False(Program.ShouldLaunchMenu(args, options));
    }

    [Fact]
    public void ParseArgs_WithConfigSubcommand_SkipsInteractiveMenu()
    {
        string[] args = ["config", "--json", "on", "--in", "./in", "--out", "./out"];

        var options = Program.ParseArgs(args);

        Assert.Equal("config", options.Subcommand);
        Assert.True(options.JsonFileOut);
        Assert.False(Program.ShouldLaunchMenu(args, options));
    }

    [Fact]
    public void ParseArgs_WithGlobalFlagsBeforeSubcommand_ParsesSubcommandCorrectly()
    {
        string[] args = ["--workspace", "/tmp/ws", "--theme", "uv", "status"];

        var options = Program.ParseArgs(args);

        Assert.Equal("doctor", options.Subcommand);
        Assert.Equal("/tmp/ws", options.WorkspaceRoot);
        Assert.Equal("uv", options.CliTheme);
        Assert.True(options.ShowDoctorStatus);
        Assert.False(Program.ShouldLaunchMenu(args, options));
    }

    [Fact]
    public void ParseArgs_WithModelsSubcommandAndCopilotProvider_EnablesModelListing()
    {
        string[] args = ["models", "--provider", "copilot-sdk"];

        var options = Program.ParseArgs(args);

        Assert.Equal("models", options.Subcommand);
        Assert.True(options.ListGeminiModels);
        Assert.Equal("copilot-sdk", options.ProviderInput);
        Assert.False(Program.ShouldLaunchMenu(args, options));
    }

    [Fact]
    public void ParseArgs_WithDraftDetailsAndSearchFlags_ParsesContextAndWebSearchGrounding()
    {
        string[] args =
        [
            "draft",
            "Check Geocaching 2026 hiding movie theme",
            "-t", "02",
            "-d", "Include hiding rules and dates",
            "--search",
            "--preview"
        ];

        var options = Program.ParseArgs(args);

        Assert.Equal("draft", options.Subcommand);
        Assert.Equal("Check Geocaching 2026 hiding movie theme", options.GoalTitle);
        Assert.Equal("02", options.BacklogTypeInput);
        Assert.Equal("Include hiding rules and dates", options.GoalContext);
        Assert.True(options.WebSearchGrounding);
        Assert.True(options.DryRun);
        Assert.False(Program.ShouldLaunchMenu(args, options));
    }

    [Fact]
    public void ParseArgs_WithContextAndWebAliases_ParsesGoalContextAndSearch()
    {
        string[] args = ["draft", "Verify release dates", "-c", "Focus on Q3 deadlines", "--web"];

        var options = Program.ParseArgs(args);

        Assert.Equal("Verify release dates", options.GoalTitle);
        Assert.Equal("Focus on Q3 deadlines", options.GoalContext);
        Assert.True(options.WebSearchGrounding);
        Assert.False(Program.ShouldLaunchMenu(args, options));
    }

    [Fact]
    public void ParseArgs_WithLogLevelLogFormatVerboseAndQuietFlags_ParsesExpectedOptions()
    {
        var separated = Program.ParseArgs(["draft", "Goal", "--log-level", "debug", "--log-format", "json"]);
        Assert.Equal("debug", separated.LogLevelInput);
        Assert.Equal("json", separated.LogFormatInput);
        Assert.False(separated.Verbose);
        Assert.False(separated.Quiet);

        var inlineEquals = Program.ParseArgs(["status", "--log-level=trace", "--log-format=text"]);
        Assert.Equal("trace", inlineEquals.LogLevelInput);
        Assert.Equal("text", inlineEquals.LogFormatInput);
        Assert.True(inlineEquals.ShowDoctorStatus);

        var verboseShort = Program.ParseArgs(["-V", "status"]);
        Assert.True(verboseShort.Verbose);
        Assert.False(verboseShort.ShowVersion);

        var versionShort = Program.ParseArgs(["-v"]);
        Assert.True(versionShort.ShowVersion);
        Assert.False(versionShort.Verbose);

        var quietShort = Program.ParseArgs(["-q", "draft", "Goal"]);
        Assert.True(quietShort.Quiet);
        Assert.False(quietShort.Verbose);
    }

    [Theory]
    [InlineData("status", "--log-level")]
    [InlineData("status", "--log-level=")]
    [InlineData("status", "--log-format")]
    [InlineData("status", "--log-format=")]
    public void ParseArgs_WithMissingLoggingFlagValue_ThrowsArgumentException(string subcommand, string flag)
    {
        Assert.Throws<ArgumentException>(() => Program.ParseArgs([subcommand, flag]));
    }
}
