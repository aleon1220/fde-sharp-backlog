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
    [InlineData(new[] { "preview", "--help" }, "Preview Saved Markdown Outcome")]
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
                "--live", "off",
                "--verbosity", "minimalistic"
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
        Assert.Equal(OutcomeVerbosity.Minimalistic, saved.DefaultVerbosity);

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
        Assert.Contains("complete (Type01) + schedule (Type02)", text);
        Assert.DoesNotContain("# [Jira -", text);
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
        Assert.Contains("Smart Backlog Assistant — Markdown Outcome", consoleText);
        Assert.Contains("Objective:", consoleText);
        Assert.DoesNotContain("**Objective:**", consoleText);
        Assert.DoesNotContain("# [Jira -", consoleText);
        Assert.DoesNotContain("User Story", consoleText);
        Assert.DoesNotContain("Acceptance", consoleText);
        Assert.DoesNotContain("│ Alternatives", consoleText);
        Assert.Contains("--alternatives", consoleText);
        Assert.DoesNotContain("Not specified in discovery notes", consoleText);

        using var altOutput = new StringWriter();
        using var altError = new StringWriter();
        var altExit = Program.Execute(
            [
                "-w", _tempRoot,
                "draft",
                "check Geocaching 2026 hiding movie theme",
                "-t", "02",
                "--alternatives",
                "--preview",
                "--offline"
            ],
            new StringReader(string.Empty),
            altOutput,
            altError);
        Assert.Equal(0, altExit);
        Assert.Contains("Alternatives", altOutput.ToString());
        Assert.Contains("[Action & Delivery]", altOutput.ToString());

        var tableLines = consoleText
            .Replace("\r\n", "\n")
            .Split('\n')
            .SkipWhile(line => !line.StartsWith('┌'))
            .TakeWhile(line => !line.Contains("[Preview / Dry Run]", StringComparison.Ordinal))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        Assert.True(tableLines.Length >= 8);
        Assert.StartsWith("┌", tableLines[0]);
        Assert.EndsWith("┐", tableLines[0]);
        Assert.StartsWith("└", tableLines[^1]);
        Assert.EndsWith("┘", tableLines[^1]);
        var expectedWidth = tableLines[0].Length;
        Assert.All(tableLines, line => Assert.Equal(expectedWidth, line.Length));
        Assert.All(tableLines[1..^1], line => Assert.True(
            (line.StartsWith("│ ", StringComparison.Ordinal) && line.EndsWith(" │", StringComparison.Ordinal)) ||
            (line.StartsWith("├", StringComparison.Ordinal) && line.EndsWith("┤", StringComparison.Ordinal))));
    }

    [Fact]
    public void Execute_PreviewSubcommand_ReadsSavedMarkdownAndCopiesRawAndMarkdownFormats()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempRoot);
        Directory.CreateDirectory(dirs.OutputDirectory);
        var mdPath = Path.Combine(dirs.OutputDirectory, "type02-verify-theme.md");
        File.WriteAllText(mdPath, """
            # [Jira - schedule (Type02)] Verify Theme Rules

            ## Description
            **Objective:** Confirm official rules and dates.
            **Suggested Actions:**
            * Check announcement blog
            * Verify timeline dates
            """);

        var clipboard = new RecordingClipboardService();
        using var outRaw = new StringWriter();
        using var errRaw = new StringWriter();
        var rawExit = Program.Execute(
            ["-w", _tempRoot, "preview", "type02-verify-theme", "--copy", "raw"],
            new StringReader(string.Empty),
            outRaw,
            errRaw,
            clipboard);

        Assert.Equal(0, rawExit);
        Assert.Empty(errRaw.ToString());
        Assert.Contains("Reading Markdown:", outRaw.ToString());
        Assert.Contains("Smart Backlog Assistant — Markdown Outcome", outRaw.ToString());
        Assert.Contains("✓ Copied Raw Text outcome to clipboard", outRaw.ToString());

        Assert.Single(clipboard.CopiedTexts);
        Assert.Contains("┌", clipboard.CopiedTexts[0]);
        Assert.Contains("\n", clipboard.CopiedTexts[0]);
        Assert.Contains("* Check announcement blog", clipboard.CopiedTexts[0]);

        using var outMd = new StringWriter();
        using var errMd = new StringWriter();
        var mdExit = Program.Execute(
            ["-w", _tempRoot, "preview", "--copy", "md"],
            new StringReader(string.Empty),
            outMd,
            errMd,
            clipboard);

        Assert.Equal(0, mdExit);
        Assert.Empty(errMd.ToString());
        Assert.Equal(2, clipboard.CopiedTexts.Count);
        Assert.StartsWith("# [Jira - schedule (Type02)] Verify Theme Rules", clipboard.CopiedTexts[1]);
        Assert.Contains("* Verify timeline dates", clipboard.CopiedTexts[1]);
        Assert.DoesNotContain("┌", clipboard.CopiedTexts[1]);
    }

    [Fact]
    public void Execute_PreviewCopyCleanAndTitle_CopiesCleanTextAndFieldsWithoutBorders()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempRoot);
        Directory.CreateDirectory(dirs.OutputDirectory);
        File.WriteAllText(Path.Combine(dirs.OutputDirectory, "type02-verify-theme.md"), """
            # [Jira - schedule (Type02)] Verify Theme Rules

            ## Description
            **Objective:** Confirm official rules and dates.
            * Check announcement blog
            """);

        var clipboard = new RecordingClipboardService();
        using var outClean = new StringWriter();
        using var errClean = new StringWriter();
        var cleanExit = Program.Execute(
            ["-w", _tempRoot, "preview", "type02-verify-theme", "--copy", "clean"],
            new StringReader(string.Empty),
            outClean,
            errClean,
            clipboard);

        Assert.Equal(0, cleanExit);
        Assert.Empty(errClean.ToString());
        Assert.Contains("✓ Copied Clean Plain Text outcome to clipboard", outClean.ToString());

        using var outTitle = new StringWriter();
        using var errTitle = new StringWriter();
        var titleExit = Program.Execute(
            ["-w", _tempRoot, "preview", "type02-verify-theme", "--copy", "title"],
            new StringReader(string.Empty),
            outTitle,
            errTitle,
            clipboard);

        Assert.Equal(0, titleExit);
        Assert.Empty(errTitle.ToString());
        Assert.Contains("✓ Copied Title to clipboard", outTitle.ToString());

        Assert.Equal(2, clipboard.CopiedTexts.Count);
        Assert.DoesNotContain("┌", clipboard.CopiedTexts[0]);
        Assert.DoesNotContain("**", clipboard.CopiedTexts[0]);
        Assert.Contains("Objective: Confirm official rules and dates.", clipboard.CopiedTexts[0]);
        Assert.Equal("Verify Theme Rules", clipboard.CopiedTexts[1]);
    }

    [Fact]
    public void Execute_PreviewSelect_RoutesPromptsToStderrAndEmitsCleanValueToStdout()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempRoot);
        Directory.CreateDirectory(dirs.OutputDirectory);

        var alphaPath = Path.Combine(dirs.OutputDirectory, "alpha-story.md");
        var betaPath = Path.Combine(dirs.OutputDirectory, "beta-schedule.md");
        File.WriteAllText(alphaPath, "# [Jira - complete (Type01)] Alpha Story\n\n## User Story\nAs a user, I want alpha.");
        File.WriteAllText(betaPath, "# [Jira - schedule (Type02)] Verify Beta Theme\n\n## Description\n**Objective:** Confirm beta scope.");

        var clipboard = new RecordingClipboardService();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Execute(
            ["-w", _tempRoot, "preview", "--select"],
            new StringReader("beta\ntitle\n"),
            output,
            error,
            clipboard);

        Assert.Equal(0, exitCode);
        Assert.Equal("Verify Beta Theme", output.ToString().Trim());
        Assert.Equal("Verify Beta Theme", Assert.Single(clipboard.CopiedTexts));

        var stderr = error.ToString();
        Assert.Contains("Select a saved Markdown file:", stderr);
        Assert.Contains("Select output format or Jira field to copy:", stderr);
        Assert.Contains("✓ Copied Title to clipboard", stderr);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Execute_PreviewSelectInNonInteractiveOrEmptyStream_ExitsGracefullyWithActionableHint(bool nonInteractiveFlag)
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempRoot);
        Directory.CreateDirectory(dirs.OutputDirectory);
        File.WriteAllText(Path.Combine(dirs.OutputDirectory, "type02-sample.md"), "# [Jira - schedule (Type02)] Sample");

        var args = nonInteractiveFlag
            ? new[] { "-w", _tempRoot, "preview", "--select", "--non-interactive" }
            : new[] { "-w", _tempRoot, "preview", "--select" };

        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Execute(args, new StringReader(string.Empty), output, error);

        Assert.Equal(1, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("error:", error.ToString());
        Assert.Contains("hint :", error.ToString());
        Assert.Contains("--copy", error.ToString());
    }

    [Fact]
    public void Execute_ConfigWithUnsupportedModel_FailsWithReasonOfficialDocsAndRecommendations()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Execute(
            ["-w", _tempRoot, "config", "--provider", "gemini", "--model", "text-embedding-004"],
            new StringReader(string.Empty),
            output,
            error);

        Assert.Equal(1, exitCode);
        var errText = error.ToString();
        Assert.Contains("error:", errText);
        Assert.Contains("unsupported modality", errText);
        Assert.Contains(ModelCatalogService.GeminiModelsDocsUrl, errText);
        Assert.Contains("gemini-2.5-flash", errText);
        Assert.Contains("hint :", errText);
    }

    [Fact]
    public void Execute_LogLevelCliFlagEnvVarAndConfig_ControlsStderrVerbosityAndMasksSecrets()
    {
        const string secretKey = "AIzaSyCliSubcmdSecret9012";

        // 1. --log-level debug emits DBG/INF logs to stderr while keeping stdout clean and masking secrets
        using var debugOut = new StringWriter();
        using var debugErr = new StringWriter();
        var debugExit = Program.Execute(
            [
                "-w", _tempRoot,
                "draft", "Enable Structured Logging",
                "-t", "01",
                "--preview",
                "--offline",
                "--api-key", secretKey,
                "--log-level", "debug",
                "-p", _promptFile
            ],
            new StringReader(string.Empty),
            debugOut,
            debugErr,
            clipboardService: null,
            environmentReader: _ => null);

        Assert.Equal(0, debugExit);
        var debugStdout = debugOut.ToString();
        var debugStderr = debugErr.ToString();
        Assert.Contains("Smart Backlog Assistant — Markdown Outcome", debugStdout);
        Assert.DoesNotContain(" DBG]", debugStdout);
        Assert.DoesNotContain(" INF]", debugStdout);
        Assert.DoesNotContain(secretKey, debugStdout);

        Assert.Contains(" DBG]", debugStderr);
        Assert.Contains(" INF]", debugStderr);
        Assert.DoesNotContain(" TRC]", debugStderr);
        Assert.Contains("****9012", debugStderr);
        Assert.DoesNotContain(secretKey, debugStderr);

        // 2. --log-level warning suppresses DBG/INF/TRC logs on stderr
        using var warnOut = new StringWriter();
        using var warnErr = new StringWriter();
        var warnExit = Program.Execute(
            ["-w", _tempRoot, "draft", "Enable Structured Logging", "-t", "01", "--preview", "--offline", "--log-level", "warning", "-p", _promptFile],
            new StringReader(string.Empty),
            warnOut,
            warnErr,
            clipboardService: null,
            environmentReader: _ => null);

        Assert.Equal(0, warnExit);
        Assert.Empty(warnErr.ToString());

        // 3. SBA_LOG_LEVEL=trace and SBA_LOG_FORMAT=json emit valid NDJSON on stderr with secret masking
        var env = new Dictionary<string, string?>
        {
            [CliLogLevelResolver.LogLevelEnvVar] = "trace",
            [CliLogLevelResolver.LogFormatEnvVar] = "json",
            [GeminiSecretProvider.PrimaryEnvVar] = secretKey
        };
        using var envOut = new StringWriter();
        using var envErr = new StringWriter();
        var envExit = Program.Execute(
            ["-w", _tempRoot, "draft", "Enable Structured Logging", "-t", "01", "--preview", "--offline", "-p", _promptFile],
            new StringReader(string.Empty),
            envOut,
            envErr,
            clipboardService: null,
            environmentReader: key => env.TryGetValue(key, out var val) ? val : null);

        Assert.Equal(0, envExit);
        var ndjsonLines = envErr.ToString()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(ndjsonLines);
        Assert.Contains(ndjsonLines, l => l.Contains("\"level\":\"Trace\""));
        Assert.All(ndjsonLines, line =>
        {
            Assert.DoesNotContain(secretKey, line);
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            Assert.True(doc.RootElement.TryGetProperty("timestamp", out _));
            Assert.True(doc.RootElement.TryGetProperty("level", out _));
            Assert.True(doc.RootElement.TryGetProperty("category", out _));
            Assert.True(doc.RootElement.TryGetProperty("message", out _));
        });
    }

    [Fact]
    public void Execute_PreviewSelectWithDebugAndTraceJsonLogging_KeepsStdoutStrictlyCleanForPipelines()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempRoot);
        Directory.CreateDirectory(dirs.OutputDirectory);
        File.WriteAllText(
            Path.Combine(dirs.OutputDirectory, "type02-logging-pipeline.md"),
            "# [Jira - schedule (Type02)] Clean Pipeline Title\n\n## Description\n**Objective:** Verify clean stdout piping.");

        var clipboard = new RecordingClipboardService();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = Program.Execute(
            ["-w", _tempRoot, "preview", "--select", "--log-level", "trace", "--log-format", "json"],
            new StringReader("title\n"),
            stdout,
            stderr,
            clipboard,
            environmentReader: _ => null);

        Assert.Equal(0, exitCode);
        Assert.Equal("Clean Pipeline Title", stdout.ToString().Trim());
        Assert.Equal("Clean Pipeline Title", Assert.Single(clipboard.CopiedTexts));

        var stderrText = stderr.ToString();
        Assert.Contains("\"level\":\"Debug\"", stderrText);
        Assert.Contains("Previewing saved Markdown", stderrText);
        Assert.Contains("Select output format or Jira field to copy:", stderrText);
    }

    [Fact]
    public void Execute_ConfigAndStatus_PersistDisplayAndResetLogLevelAndFormat()
    {
        using var cfgOut = new StringWriter();
        using var cfgErr = new StringWriter();
        Assert.Equal(0, Program.Execute(
            ["-w", _tempRoot, "config", "--log-level", "information", "--log-format", "json"],
            new StringReader(string.Empty),
            cfgOut,
            cfgErr,
            clipboardService: null,
            environmentReader: _ => null));

        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var settingsService = new CliSettingsService();
        var persisted = settingsService.Load(workspaceService.Resolve(_tempRoot));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, persisted.LogLevel);
        Assert.Equal(LogOutputFormat.Json, persisted.LogFormat);
        Assert.Contains("Information", cfgOut.ToString());
        Assert.Contains("json (stderr)", cfgOut.ToString());

        // `status` reflects persisted settings.json log level and format
        using var statusOut = new StringWriter();
        using var statusErr = new StringWriter();
        Assert.Equal(0, Program.Execute(
            ["-w", _tempRoot, "status"],
            new StringReader(string.Empty),
            statusOut,
            statusErr,
            clipboardService: null,
            environmentReader: _ => null));

        var statusText = statusOut.ToString();
        Assert.Contains("Information (settings.json)", statusText);
        Assert.Contains("json -> stderr (settings.json)", statusText);

        // `config --log-level default --log-format default` clears overrides cleanly
        using var resetOut = new StringWriter();
        using var resetErr = new StringWriter();
        Assert.Equal(0, Program.Execute(
            ["-w", _tempRoot, "config", "--log-level", "default", "--log-format", "default"],
            new StringReader(string.Empty),
            resetOut,
            resetErr,
            clipboardService: null,
            environmentReader: _ => null));

        var afterReset = settingsService.Load(workspaceService.Resolve(_tempRoot));
        Assert.Null(afterReset.LogLevel);
        Assert.Null(afterReset.LogFormat);
    }

    [Fact]
    public void Execute_WithInvalidLogLevelOrQuietSuppression_BehavesDeterministically()
    {
        using var invalidOut = new StringWriter();
        using var invalidErr = new StringWriter();
        var invalidExit = Program.Execute(
            ["-w", _tempRoot, "status", "--log-level", "super-noisy"],
            new StringReader(string.Empty),
            invalidOut,
            invalidErr,
            clipboardService: null,
            environmentReader: _ => null);

        Assert.Equal(1, invalidExit);
        Assert.Contains("error: Invalid log level 'super-noisy'", invalidErr.ToString());
        Assert.Contains("hint :", invalidErr.ToString());
        Assert.Contains("SBA_LOG_LEVEL", invalidErr.ToString());

        // --quiet suppresses diagnostic ERR log entry while still rendering concise actionable error
        using var quietOut = new StringWriter();
        using var quietErr = new StringWriter();
        var quietExit = Program.Execute(
            ["-w", _tempRoot, "draft", "--quiet"],
            new StringReader(string.Empty),
            quietOut,
            quietErr,
            clipboardService: null,
            environmentReader: _ => null);

        Assert.Equal(1, quietExit);
        Assert.Contains("error: Missing goal or title for 'draft'", quietErr.ToString());
        Assert.DoesNotContain(" ERR]", quietErr.ToString());
    }

    [Fact]
    public void Execute_ConfigAndStatus_WithOpenAiGateway_PersistsGatewaySettingsAndRendersProviderMatrix()
    {
        var env = new Dictionary<string, string?>
        {
            [OpenAiGatewayClient.ApiKeyEnvVar] = "sk-live-status-4242"
        };

        using var cfgOut = new StringWriter();
        using var cfgErr = new StringWriter();
        var cfgExit = Program.Execute(
            [
                "-w", _tempRoot,
                "config",
                "--provider", "openai",
                "--gateway-url", "https://gateway.internal/v1/",
                "--wire-api", "responses",
                "-m", "gpt-4.1-mini"
            ],
            new StringReader(string.Empty),
            cfgOut,
            cfgErr,
            clipboardService: null,
            environmentReader: key => env.TryGetValue(key, out var val) ? val : null);

        Assert.Equal(0, cfgExit);
        Assert.Empty(cfgErr.ToString());

        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var settingsService = new CliSettingsService();
        var persisted = settingsService.Load(workspaceService.Resolve(_tempRoot));
        Assert.Equal(AiProviderType.OpenAiGateway, persisted.PreferredProvider);
        Assert.Equal("https://gateway.internal/v1", persisted.OpenAiBaseUrl);
        Assert.Equal("responses", persisted.OpenAiWireApi);
        Assert.Equal("gpt-4.1-mini", persisted.ModelName);

        using var statusOut = new StringWriter();
        using var statusErr = new StringWriter();
        var statusExit = Program.Execute(
            ["-w", _tempRoot, "status"],
            new StringReader(string.Empty),
            statusOut,
            statusErr,
            clipboardService: null,
            environmentReader: key => env.TryGetValue(key, out var val) ? val : null);

        Assert.Equal(0, statusExit);
        var statusText = statusOut.ToString();
        Assert.Contains("OpenAI Gateway", statusText);
        Assert.Contains("https://gateway.internal/v1 (responses)", statusText);
        Assert.Contains("****4242", statusText);
        Assert.DoesNotContain("sk-live-status-4242", statusText);
    }

    [Fact]
    public void Execute_ModelsDraftAndProcess_WithOpenAiGateway_ExecutesLiveHttpAndOfflineFallback()
    {
        var workspaceService = new WorkspacePathService(environmentReader: _ => null);
        var dirs = workspaceService.Resolve(_tempRoot);
        Directory.CreateDirectory(dirs.InputDirectory);
        Directory.CreateDirectory(dirs.OutputDirectory);
        var notesFile = Path.Combine(dirs.InputDirectory, "notes.txt");
        File.WriteAllText(notesFile, "User story notes for OpenAI Gateway offline fallback.");

        var calls = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            calls++;
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                return JsonResponse("""{"object":"list","data":[{"id":"gpt-4o","owned_by":"openai"},{"id":"gpt-4.1-mini","owned_by":"openai"}]}""");
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
            {
                return JsonResponse("""
                    {
                      "output_text": "{\"title\":\"Gateway Schedule Check\",\"description\":\"**Objective:** Validate responses gateway integration.\\n* Verify auth header\\n* Confirm JSON parsing\"}"
                    }
                    """);
            }

            return JsonResponse("""
                {
                  "choices": [
                    {
                      "message": {
                        "content": "{\"title\":\"Ship OAuth Gateway\",\"description\":\"**Objective:** Deliver OpenAI Gateway support.\\n* Send bearer auth\\n* Parse structured JSON\",\"userStory\":\"As a senior FDE, I want OpenAI Gateway integration so teams can use enterprise endpoints.\",\"scenarios\":[{\"given\":\"OPENAI_API_KEY is set\",\"when\":\"draft executes\",\"then\":\"Type01 backlog item is generated\"}]}"
                      }
                    }
                  ]
                }
                """);
        });

        var stubClient = new OpenAiGatewayClient(new HttpClient(handler));
        var secretKey = "sk-live-subcmd-9988";
        var env = new Dictionary<string, string?>
        {
            [OpenAiGatewayClient.ApiKeyEnvVar] = secretKey
        };
        string? ReadEnv(string key) => env.TryGetValue(key, out var val) ? val : null;

        // 1. models
        using var modelsOut = new StringWriter();
        using var modelsErr = new StringWriter();
        var modelsExit = Program.Execute(
            ["-w", _tempRoot, "models", "--provider", "openai", "--gateway-url", "https://gateway.internal/v1"],
            new StringReader(string.Empty),
            modelsOut,
            modelsErr,
            clipboardService: null,
            environmentReader: ReadEnv,
            openAiGatewayClient: stubClient);
        Assert.Equal(0, modelsExit);
        Assert.Contains("gpt-4o", modelsOut.ToString());
        Assert.Contains("gpt-4.1-mini", modelsOut.ToString());

        // 2. draft Type01 via /chat/completions + --json + stderr secret redaction check
        using var draft1Out = new StringWriter();
        using var draft1Err = new StringWriter();
        var draft1Exit = Program.Execute(
            ["-w", _tempRoot, "-p", _promptFile, "draft", "Ship OAuth Gateway", "--provider", "openai", "-t", "01", "--json", "--log-level", "debug"],
            new StringReader(string.Empty),
            draft1Out,
            draft1Err,
            clipboardService: null,
            environmentReader: ReadEnv,
            openAiGatewayClient: stubClient);
        Assert.Equal(0, draft1Exit);
        Assert.Contains("Ship OAuth Gateway", draft1Out.ToString());
        Assert.True(File.Exists(Path.Combine(dirs.OutputDirectory, "jira-backlog-item.json")));
        Assert.Contains("OpenAI", draft1Err.ToString());
        Assert.DoesNotContain(secretKey, draft1Err.ToString());
        Assert.DoesNotContain(secretKey, draft1Out.ToString());

        // 3. draft Type02 via /responses
        using var draft2Out = new StringWriter();
        using var draft2Err = new StringWriter();
        var draft2Exit = Program.Execute(
            ["-w", _tempRoot, "-p", _promptFile, "draft", "Gateway Schedule Check", "--provider", "openai", "--gateway-url", "https://gateway.internal/v1", "--wire-api", "responses", "-t", "02", "--preview"],
            new StringReader(string.Empty),
            draft2Out,
            draft2Err,
            clipboardService: null,
            environmentReader: ReadEnv,
            openAiGatewayClient: stubClient);
        Assert.Equal(0, draft2Exit);
        Assert.Contains("Gateway Schedule Check", draft2Out.ToString());

        var callsBeforeOffline = calls;

        // 4. process --offline with no env key -> deterministic fallback with zero HTTP requests
        using var procOut = new StringWriter();
        using var procErr = new StringWriter();
        var procExit = Program.Execute(
            ["-w", _tempRoot, "-p", _promptFile, "process", "notes.txt", "--provider", "openai", "--offline", "--preview"],
            new StringReader(string.Empty),
            procOut,
            procErr,
            clipboardService: null,
            environmentReader: _ => null,
            openAiGatewayClient: stubClient);
        Assert.Equal(0, procExit);
        Assert.Equal(callsBeforeOffline, calls);
        Assert.Contains("User story notes for OpenAI Gateway offline fallback", procOut.ToString());
    }

    private static HttpResponseMessage JsonResponse(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request, cancellationToken));
    }

    private sealed class RecordingClipboardService : IClipboardService
    {
        public List<string> CopiedTexts { get; } = [];

        public ClipboardCopyResult CopyText(string text)
        {
            CopiedTexts.Add(text);
            return new ClipboardCopyResult(true, "fake-clipboard", "Copied via fake-clipboard.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
