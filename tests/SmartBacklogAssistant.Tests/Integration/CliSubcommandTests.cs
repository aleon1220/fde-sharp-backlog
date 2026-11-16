using SmartBacklogAssistant.Domain;
using SmartBacklogAssistant.Services;

namespace SmartBacklogAssistant.Tests.Integration;

public sealed class CliSubcommandTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _promptFile;

    public CliSubcommandTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"sba-cli-subcmds-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        _promptFile = Path.Combine(_tempRoot, "prompt.md");
        File.WriteAllText(_promptFile, "# Test Prompt\nAnalyze the following notes:\n{{RAW_DATA}}");
    }

    [Theory]
    [InlineData(new[] { "--help" }, "SUBCOMMANDS (Astral uv style):")]
    [InlineData(new[] { "process", "--help" }, "Process Discovery Files")]
    [InlineData(new[] { "draft", "--help" }, "Draft Backlog Item from Goal or Title")]
    [InlineData(new[] { "config", "--help" }, "View or Persist Workspace Configuration")]
    [InlineData(new[] { "status", "--help" }, "Workspace & AI Diagnostics")]
    public void Execute_HelpCommands_RenderScannableSubcommandDocumentation(string[] args, string expectedHeading)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Execute(args, new StringReader(string.Empty), output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains(expectedHeading, output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void Execute_ConfigSubcommand_PersistsJsonInOutThemeAndProviderOverrides()
    {
        var customIn = Path.Combine(_tempRoot, "custom-in");
        var customOut = Path.Combine(_tempRoot, "custom-out");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Execute(
            [
                "-w", _tempRoot,
                "config",
                "--json", "on",
                "--in", customIn,
                "--out", customOut,
                "--theme", "uv",
                "--provider", "copilot-sdk",
                "--live", "off"
            ],
            new StringReader(string.Empty),
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.True(Directory.Exists(customIn));
        Assert.True(Directory.Exists(customOut));

        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var settingsService = new CliSettingsService();
        var saved = settingsService.Load(workspaceService.Resolve(_tempRoot));

        Assert.True(saved.JsonFileOut);
        Assert.Equal(customIn, saved.CustomInputDirectory);
        Assert.Equal(customOut, saved.CustomOutputDirectory);
        Assert.Equal(CliThemeStyle.Uv, saved.Theme);
        Assert.Equal(AiProviderType.GitHubCopilot, saved.PreferredProvider);
        Assert.False(saved.GeminiLive);

        var text = output.ToString();
        Assert.Contains("✓ Saved", text);
        Assert.Contains("Smart Backlog Assistant — Configuration", text);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void Execute_ConfigShowAndReset_DisplaysAndResetsSettings()
    {
        using var setOut = new StringWriter();
        using var showOut = new StringWriter();
        using var resetOut = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, Program.Execute(["-w", _tempRoot, "config", "--json", "on", "--theme", "kubectl"], new StringReader(string.Empty), setOut, error));
        Assert.Equal(0, Program.Execute(["-w", _tempRoot, "config", "--show"], new StringReader(string.Empty), showOut, error));
        Assert.Contains("ON (.md + .json)", showOut.ToString());

        Assert.Equal(0, Program.Execute(["-w", _tempRoot, "config", "--reset"], new StringReader(string.Empty), resetOut, error));

        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var settingsService = new CliSettingsService();
        var reloaded = settingsService.Load(workspaceService.Resolve(_tempRoot));
        Assert.False(reloaded.JsonFileOut);
        Assert.Equal(CliThemeStyle.Agy, reloaded.Theme);
        Assert.Null(reloaded.CustomInputDirectory);
    }

    [Fact]
    public void Execute_StatusSubcommand_RendersDiagnosticTableAndProviderMatrix()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempRoot);
        Directory.CreateDirectory(dirs.InputDirectory);
        Directory.CreateDirectory(dirs.OutputDirectory);
        File.WriteAllText(Path.Combine(dirs.InputDirectory, "note-a.txt"), "Title: Note A");
        File.WriteAllText(Path.Combine(dirs.InputDirectory, "note-b.md"), "Title: Note B");

        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Execute(["-w", _tempRoot, "status"], new StringReader(string.Empty), output, error);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Smart Backlog Assistant — Workspace & AI Status", text);
        Assert.Contains("(2 queued)", text);
        Assert.Contains("AI Provider Matrix:", text);
        Assert.Contains("Google Gemini REST", text);
        Assert.Contains("GitHub Copilot SDK", text);
        Assert.Contains("Windows Root", text);
        Assert.Contains("WSL2 Root", text);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void Execute_ProcessAllAndSingleFile_SupportsCustomConfiguredDirsDryRunAndJsonToggle()
    {
        var customIn = Path.Combine(_tempRoot, "configured-in");
        var customOut = Path.Combine(_tempRoot, "configured-out");
        Directory.CreateDirectory(customIn);
        File.WriteAllText(Path.Combine(customIn, "alpha.txt"), """
            Discovery Note - Alpha Feature
            Context: Automate customer onboarding checks.
            Requirement: As an operator, I want automated onboarding checks so that setup time drops.
            Precondition: Onboarding starts.
            Action: Check runs.
            Expected Outcome: Validation report is emitted.
            """);
        File.WriteAllText(Path.Combine(customIn, "beta.txt"), """
            Discovery Note - Beta Telemetry
            Context: Emit OpenTelemetry spans.
            Requirement: As an SRE, I want trace spans so that latency bottlenecks are visible.
            Precondition: Request enters API.
            Action: Span completes.
            Expected Outcome: Trace appears in collector.
            """);

        using var cfgOut = new StringWriter();
        using var dryOut = new StringWriter();
        using var singleOut = new StringWriter();
        using var err = new StringWriter();

        // Persist --in and --out via `backlog config`
        Assert.Equal(0, Program.Execute(
            ["-w", _tempRoot, "config", "--in", customIn, "--out", customOut],
            new StringReader(string.Empty),
            cfgOut,
            err));

        // 1. `process all --dry-run` processes both files without writing output artifacts
        var dryExit = Program.Execute(
            ["-w", _tempRoot, "process", "all", "--dry-run", "--offline", "-p", _promptFile],
            new StringReader(string.Empty),
            dryOut,
            err);

        Assert.Equal(0, dryExit);
        Assert.Empty(Directory.GetFiles(customOut));
        Assert.Contains("✓ Resolved 2 queued input files", dryOut.ToString());
        Assert.Contains("Alpha Feature", dryOut.ToString());
        Assert.Contains("Beta Telemetry", dryOut.ToString());
        Assert.Contains("✓ Processed 2 file(s)", dryOut.ToString());

        // 2. `process beta.txt --json` processes ONLY beta.txt with JSON dual export for the run
        var singleExit = Program.Execute(
            ["-w", _tempRoot, "process", "beta.txt", "--json", "--offline", "-p", _promptFile],
            new StringReader(string.Empty),
            singleOut,
            err);

        Assert.Equal(0, singleExit);
        Assert.True(File.Exists(Path.Combine(customOut, "type01-beta-telemetry.md")));
        Assert.True(File.Exists(Path.Combine(customOut, "type01-beta-telemetry.json")));
        Assert.False(File.Exists(Path.Combine(customOut, "type01-alpha-feature.md")));
        Assert.Empty(err.ToString());
    }

    [Fact]
    public void Execute_DraftSubcommandWithPreviewAndBothTypes_PrintsPreviewWithoutBlockingStdinOrWritingFiles()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempRoot);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Execute(
            ["-w", _tempRoot, "draft", "Distributed Cache Invalidation", "--type", "both", "--preview", "--offline", "-p", _promptFile],
            new StringReader(string.Empty),
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Empty(Directory.GetFiles(dirs.OutputDirectory));
        var text = output.ToString();
        Assert.Contains("Jira - complete (Type01)", text);
        Assert.Contains("Jira - schedule (Type02)", text);
        Assert.Contains("[Preview / Dry Run]", text);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void Execute_WithMissingInputOrMissingDraftGoal_EmitsActionableErrorAndHint()
    {
        using var outMissingFile = new StringWriter();
        using var errMissingFile = new StringWriter();
        var missingFileExit = Program.Execute(
            ["-w", _tempRoot, "process", "does-not-exist.txt"],
            new StringReader(string.Empty),
            outMissingFile,
            errMissingFile);

        Assert.Equal(1, missingFileExit);
        Assert.Contains("error:", errMissingFile.ToString());
        Assert.Contains("hint :", errMissingFile.ToString());
        Assert.Contains("backlog status", errMissingFile.ToString());

        using var outMissingGoal = new StringWriter();
        using var errMissingGoal = new StringWriter();
        var missingGoalExit = Program.Execute(
            ["-w", _tempRoot, "draft"],
            new StringReader(string.Empty),
            outMissingGoal,
            errMissingGoal);

        Assert.Equal(1, missingGoalExit);
        Assert.Contains("error: Missing goal or title for 'draft'", errMissingGoal.ToString());
    }

    [Fact]
    public void Execute_DraftType02WithDetailsAndInlineModifiers_PopulatesStructuredDescriptionAndWritesItem()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempRoot);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Execute(
            [
                "-w", _tempRoot,
                "draft",
                "Check Geocaching 2026 hiding movie theme",
                "-t", "02",
                "-d", "Verify official HQ rules, launch dates, and promotional requirements",
                "--offline",
                "-p", _promptFile
            ],
            new StringReader(string.Empty),
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());

        var expectedFile = Path.Combine(dirs.OutputDirectory, "type02-check-geocaching-2026-hiding-movie-theme.md");
        Assert.True(File.Exists(expectedFile));
        var markdown = File.ReadAllText(expectedFile);
        Assert.Contains("# [Jira - schedule (Type02)] Check Geocaching 2026 hiding movie theme", markdown);
        Assert.Contains("Verify official HQ rules, launch dates, and promotional requirements", markdown);
        Assert.DoesNotContain("Not specified in discovery notes", markdown);

        using var inlineOutput = new StringWriter();
        using var inlineError = new StringWriter();
        var inlineExit = Program.Execute(
            [
                "-w", _tempRoot,
                "draft",
                "Verify Geocaching 2026 Promo | Confirm souvenir criteria --search",
                "-t", "02",
                "--preview",
                "--offline",
                "-p", _promptFile
            ],
            new StringReader(string.Empty),
            inlineOutput,
            inlineError);

        Assert.Equal(0, inlineExit);
        Assert.Empty(inlineError.ToString());
        Assert.Contains("Confirm souvenir criteria", inlineOutput.ToString());
        Assert.Contains("[Preview / Dry Run]", inlineOutput.ToString());
    }

    [Fact]
    public void Execute_BareDraftType02WithoutDetails_SynthesizesUserStoryDescriptionAndThreeAlternatives()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempRoot);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Execute(
            [
                "-w", _tempRoot,
                "draft",
                "check Geocaching 2026 hiding movie theme",
                "-t", "02",
                "--preview",
                "--offline"
            ],
            new StringReader(string.Empty),
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Empty(Directory.GetFiles(dirs.OutputDirectory));

        var consoleText = output.ToString();
        Assert.Contains("**Objective:**", consoleText);
        Assert.Contains("User Story :", consoleText);
        Assert.Contains("Suggested Title & Description Alternatives", consoleText);
        Assert.Contains("[1] Action & Delivery", consoleText);
        Assert.Contains("[2] User Value / Story", consoleText);
        Assert.Contains("[3] Verification & Scope", consoleText);
        Assert.Contains("## Title & Description Alternatives", consoleText);
        Assert.DoesNotContain("Not specified in discovery notes", consoleText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
