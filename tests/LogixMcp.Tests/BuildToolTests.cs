using System.Text.Json;
#if LOGIX_SDK
using RockwellAutomation.LogixDesigner;
using RequestedBuildTarget = RockwellAutomation.LogixDesigner.LogixProject.RequestedBuildTarget;

using Task = System.Threading.Tasks.Task;
#endif

namespace LogixMcp.Tests;

internal static class BuildTestFixtures
{
    /// <summary>A project service loaded from Demo.L5X (contains the existing UDT <c>MotorCmd</c>).</summary>
    internal static ProjectService DemoService()
    {
        var svc = new ProjectService();
        svc.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X"));
        return svc;
    }
}

#if LOGIX_SDK

/// <summary>
/// <c>logix_build</c> (SDK build check with located errors, capability-gated) and the datatype
/// import's build routed through the same capability check ("report what actually happened").
/// </summary>
public class BuildToolTests
{
    private const string UnsupportedMessage = "Operation not supported on Logix Designer version 36.5.";

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> OpenAsync(
        DesignerVersion? version = null)
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession((_p, _ct) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        if (version is not null) session.DesignerVersion = version;
        return (session, fake);
    }

    // ── passed / failed ───────────────────────────────────────────────────────

    [Fact]
    public async Task Build_Passed_ReturnsPassedWithNoErrors()
    {
        var (session, fake) = await OpenAsync(new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision));
        fake.BuildLogEvents.Add((false, "Build started"));          // chatter is not an issue

        var result = await BuildHandler.ExecuteAsync(null, session);

        var ok = Assert.IsType<BuildResult>(result);
        Assert.Equal("passed", ok.Status);
        Assert.Equal("default", ok.Target);
        Assert.Empty(ok.Errors);
        Assert.Empty(ok.Warnings);
        Assert.True(ok.ElapsedMs >= 0);
        Assert.Equal(1, fake.BuildCallCount);
        Assert.Equal(0, fake.GoOfflineCallCount);
        Assert.Empty(fake.Handlers);                                  // handler removed again
        var json = JsonSerializer.Serialize(ok);
        Assert.Contains("\"status\":\"passed\"", json);
        Assert.Contains("\"elapsedMs\":", json);
    }

    [Fact]
    public async Task Build_Failed_ReturnsLocatedErrorsAndWarnings()
    {
        var (session, fake) = await OpenAsync(new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision));
        fake.BuildLogEvents.Add((true,  "Error: MainProgram/MainRoutine, Rung 3: Tag 'Undefined_Tag' is not defined (error 1234)"));
        fake.BuildLogEvents.Add((true,  "Error: Program: Alarms, Routine: R_St, Line 12: Unexpected token ';'"));
        fake.BuildLogEvents.Add((true,  "Warning: MainProgram/MainRoutine, Rung 7: Unused output"));
        fake.ThrowOnBuild = new OperationFailedException("Build failed with 2 errors", "BUILD_FAILED");

        var result = await BuildHandler.ExecuteAsync("default", session);

        var failed = Assert.IsType<BuildResult>(result);
        Assert.Equal("failed", failed.Status);
        Assert.Equal(2, failed.Errors.Count);

        var rung = failed.Errors[0];
        Assert.Equal("MainProgram", rung.Program);
        Assert.Equal("MainRoutine", rung.Routine);
        Assert.Equal(3, rung.Rung);
        Assert.Equal("1234", rung.Code);
        Assert.Equal("MainProgram/MainRoutine rung 3", rung.Location);
        Assert.Contains("Undefined_Tag", rung.Message);

        var st = failed.Errors[1];
        Assert.Equal("Alarms", st.Program);
        Assert.Equal("R_St", st.Routine);
        Assert.Equal(12, st.Line);
        Assert.Null(st.Rung);

        var warn = Assert.Single(failed.Warnings);
        Assert.Equal(7, warn.Rung);
        Assert.Equal(0, fake.GoOfflineCallCount);                     // read-only toward the controller
    }

    [Fact]
    public async Task Build_FailedWithoutLogEvents_ReportsTheExceptionAsALocatedError()
    {
        var (session, fake) = await OpenAsync(new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision));
        fake.ThrowOnBuild = new InvalidOperationException("undefined tag reference XIC_Motor — Rung 0");

        var failed = Assert.IsType<BuildResult>(await BuildHandler.ExecuteAsync("default", session));

        Assert.Equal("failed", failed.Status);
        var error = Assert.Single(failed.Errors);
        Assert.Equal(0, error.Rung);
        Assert.Contains("XIC_Motor", error.Message);
        Assert.Equal(BuildHandler.NoLogEventsNote, failed.Note);
    }

    // ── v36 refusal ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Build_V36_RefusesWithSdkCapabilityUnavailableNamingStudioVerify()
    {
        // The session already knows the Designer is v36 (the SDK told it so): refuse without calling the SDK.
        var (session, fake) = await OpenAsync(new DesignerVersion(36, 5, DesignerVersionSource.SdkError));

        var result = await BuildHandler.ExecuteAsync("default", session);

        var refusal = Assert.IsType<SdkCapabilityUnavailableError>(result);
        Assert.Equal(ErrorKinds.SdkCapabilityUnavailable, refusal.Kind);
        Assert.Equal(SdkCapabilityNames.BuildCheck, refusal.Capability);
        Assert.Contains("Logic → Verify → Controller", refusal.WhatStillWorks);
        Assert.Equal(0, fake.BuildCallCount);
    }

    [Fact]
    public async Task Build_V36LearnedFromTheSdk_RefusesAndRemembersTheVersion()
    {
        // Only the project revision is known, so the first call goes to the SDK, which says v36.5.
        var (session, fake) = await OpenAsync(new DesignerVersion(36, 0, DesignerVersionSource.ProjectRevision));
        fake.ThrowOnBuild = new OperationFailedException(UnsupportedMessage, "OPERATION_NOT_SUPPORTED");

        var result = await BuildHandler.ExecuteAsync("default", session);

        var refusal = Assert.IsType<SdkCapabilityUnavailableError>(result);
        Assert.Contains("Logic → Verify → Controller", refusal.WhatStillWorks);
        Assert.Equal("36.5", refusal.Version);
        Assert.Equal("36.5", session.DesignerVersion?.ToString());

        // A second call is refused without asking the SDK again.
        Assert.IsType<SdkCapabilityUnavailableError>(await BuildHandler.ExecuteAsync("default", session));
        Assert.Equal(1, fake.BuildCallCount);
        Assert.Contains("\"kind\":\"SdkCapabilityUnavailable\"", JsonSerializer.Serialize(refusal, refusal.GetType()));
    }

    // ── targets ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null,       "default",  RequestedBuildTarget.DefaultTarget)]
    [InlineData("default",  "default",  RequestedBuildTarget.DefaultTarget)]
    [InlineData("physical", "physical", RequestedBuildTarget.PhysicalController)]
    [InlineData("echo",     "echo",     RequestedBuildTarget.EchoController)]
    [InlineData(" Echo ",   "echo",     RequestedBuildTarget.EchoController)]
    public async Task Build_TargetMapping_MapsToRequestedBuildTarget(string? target, string reported, RequestedBuildTarget expected)
    {
        var (session, fake) = await OpenAsync(new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision));

        var ok = Assert.IsType<BuildResult>(await BuildHandler.ExecuteAsync(target, session));

        Assert.Equal(reported, ok.Target);
        Assert.Equal(expected, Assert.Single(fake.CapturedBuildTargets));
        Assert.Equal(expected, BuildHandler.MapTarget(target));
    }

    [Fact]
    public async Task Build_UnknownTarget_IsRefusedBeforeTheSdkIsCalled()
    {
        var (session, fake) = await OpenAsync(new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision));

        var error = Assert.IsType<BuildError>(await BuildHandler.ExecuteAsync("simulator", session));

        Assert.Equal(ErrorKinds.InvalidValue, error.Kind);
        Assert.Equal(0, fake.BuildCallCount);
        Assert.Null(BuildHandler.MapTarget("simulator"));
    }

    [Fact]
    public async Task Build_NotPerformed_IsNotReportedAsAFailedBuild()
    {
        var (session, fake) = await OpenAsync(new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision));
        fake.ThrowOnBuild = new OperationNotPerformedException("server went away", "INTERNAL");

        var error = Assert.IsType<BuildError>(await BuildHandler.ExecuteAsync("default", session));

        Assert.Equal(ErrorKinds.BuildNotPerformed, error.Kind);
    }

    // ── tool endpoint ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Tool_NoSession_ReturnsNoActiveSession()
    {
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService());

        var json = await tools.GetBuildResponseAsync("default");

        Assert.Contains($"\"kind\":\"{ErrorKinds.NoActiveSession}\"", json);
    }

    [Fact]
    public async Task Tool_WithSession_SerializesTheResult()
    {
        var (session, fake) = await OpenAsync(new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision));
        fake.BuildLogEvents.Add((true, "Error: Program: Main, Routine: R1, Rung 2: Tag 'X' is not defined"));
        fake.ThrowOnBuild = new OperationFailedException("failed", "BUILD_FAILED");
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService());

        var json = await tools.GetBuildResponseAsync("echo", session);

        Assert.Contains("\"status\":\"failed\"", json);
        Assert.Contains("\"target\":\"echo\"", json);
        Assert.Contains("\"rung\":2", json);
        Assert.Contains("\"routine\":\"R1\"", json);
        Assert.Contains("logix_build", SdkToolNames.All);
    }

    // ── datatype import: build routed through the capability check ────────────

    [Fact]
    public async Task DatatypeImport_FakeV36Session_BuildCheckIsUnavailableNotFailed()
    {
        var (session, fake) = await OpenAsync(new DesignerVersion(36, 0, DesignerVersionSource.ProjectRevision));
        fake.ThrowOnBuild = new OperationFailedException(UnsupportedMessage, "OPERATION_NOT_SUPPORTED");

        // The runner's build (the same call SdkImportDatatypeRunner.Build makes) signals "unavailable".
        var ex = await Assert.ThrowsAsync<SdkBuildUnavailableException>(
            () => SdkImportDatatypeRunner.BuildSessionAsync(session, CancellationToken.None));
        Assert.Equal("36.5", ex.DesignerVersion);

        // And the handler, given that runner, reports the import as landed with buildVerify "unavailable".
        var save = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".L5X");
        try
        {
            var result = ImportDatatypeHandler.Execute(
                acdPath: "/tmp/project.ACD", datatypeName: "MotorCmd", l5xPath: null,
                members: [new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write")],
                saveAs: save, service: BuildTestFixtures.DemoService(), dryRun: false, confirm: true,
                buildRunner: path =>
                    SdkImportDatatypeRunner.BuildSessionAsync(session, CancellationToken.None).GetAwaiter().GetResult());

            var ok = Assert.IsType<ImportDatatypeResult>(result);
            Assert.Equal("unavailable", ok.BuildVerify);
            Assert.Equal("36.5", ok.DesignerVersion);
            Assert.StartsWith("unsupported on", ok.Build);
            Assert.Contains(DesignerVersionInfo.BuildUnavailableNote, ok.Note);
            Assert.NotNull(ok.SavedTo);                               // the import landed
            Assert.Contains("\"buildVerify\":\"unavailable\"", JsonSerializer.Serialize(ok));
        }
        finally { try { File.Delete(save); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task DatatypeImport_V38Session_BuildPassedAndFailedMapToCounts()
    {
        var (session, fake) = await OpenAsync(new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision));
        Assert.Equal(0, await SdkImportDatatypeRunner.BuildSessionAsync(session, CancellationToken.None));

        fake.BuildLogEvents.Add((true, "Error: Program: Main, Routine: R1, Rung 2: Tag 'X' is not defined"));
        fake.ThrowOnBuild = new OperationFailedException("failed", "BUILD_FAILED");
        Assert.Equal(1, await SdkImportDatatypeRunner.BuildSessionAsync(session, CancellationToken.None));
    }
}
#endif

    /// <summary>The build log parser and target names are SDK-free and run in every build.</summary>
    public class BuildLogParserTests
    {
        [Theory]
        [InlineData("MainProgram/MainRoutine, Rung 3: bad", "MainProgram", "MainRoutine", 3, null)]
        [InlineData("Program: P1, Routine: R2, Line 40: bad", "P1", "R2", null, 40)]
        [InlineData("Controller/Programs/Program[@Name='P9']/Routines/Routine[@Name='R9'] rung 5 bad", "P9", "R9", 5, null)]
        public void Parse_ReadsLocation(string text, string program, string routine, int? rung, int? line)
        {
            var issue = BuildLogParser.Parse(text);
            Assert.Equal(program, issue.Program);
            Assert.Equal(routine, issue.Routine);
            Assert.Equal(rung, issue.Rung);
            Assert.Equal(line, issue.Line);
            Assert.NotNull(issue.Location);
        }

        [Fact]
        public void Parse_NoLocation_StillYieldsTheMessage()
        {
            var issue = BuildLogParser.Parse("Error: something broke");
            Assert.Null(issue.Location);
            Assert.Equal("something broke", issue.Message);
        }

        [Fact]
        public void Classify_ErrorAndStatusChannels()
        {
            Assert.Equal(BuildLogParser.Severity.Error,   BuildLogParser.ClassifyErrorChannel("Tag missing"));
            Assert.Equal(BuildLogParser.Severity.Warning, BuildLogParser.ClassifyErrorChannel("Warning: unused"));
            Assert.Equal(BuildLogParser.Severity.None,    BuildLogParser.ClassifyStatusChannel("Build started"));
            Assert.Equal(BuildLogParser.Severity.Error,   BuildLogParser.ClassifyStatusChannel("Error: bad"));
        }

        [Theory]
        [InlineData(null, "default")]
        [InlineData("", "default")]
        [InlineData("PHYSICAL", "physical")]
        [InlineData("echo", "echo")]
        [InlineData("emulator", null)]
        public void Normalize_Targets(string? input, string? expected) =>
            Assert.Equal(expected, BuildTargetNames.Normalize(input));

        [Fact]
        public void ImportDatatype_BuildRunnerThrowsUnavailable_ReportsUnavailableNotFailed()
        {
            var save = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".L5X");
            try
            {
                var result = ImportDatatypeHandler.Execute(
                    acdPath: "/tmp/project.ACD", datatypeName: "MotorCmd", l5xPath: null,
                    members: [new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write")],
                    saveAs: save, service: BuildTestFixtures.DemoService(), dryRun: false, confirm: true,
                    buildRunner: _ => throw new SdkBuildUnavailableException("v36", "36.5"));

                var ok = Assert.IsType<ImportDatatypeResult>(result);
                Assert.Equal("unavailable", ok.BuildVerify);
                Assert.NotNull(ok.SavedTo);
            }
            finally { try { File.Delete(save); } catch { /* best effort */ } }
        }
    }
