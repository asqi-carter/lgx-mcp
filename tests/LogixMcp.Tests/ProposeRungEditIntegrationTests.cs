#if LOGIX_SDK
// ── ProposeRungEditIntegrationTests — integration tests against Echo controller ─
//
// Gated on LOGIX_ACD_PATH environment variable (established in an earlier change as the
// SDK-integration gate convention).  When the variable is absent every test is
// automatically skipped — no failures are reported and the pipeline gate
// (make test / unit tests only) is never broken.
//
// Run integration tests only:
//   LOGIX_ACD_PATH=/path/to/Project.ACD dotnet test --filter Category=Integration
//
// The Echo controller must be running in Test or Run mode with the project already
// loaded.  The project is expected to contain a MainProgram/MainRoutine — the same
// structure used by the Demo.L5X fixture that drives the policy check tests.
//
// Coverage:
//   1. Proposing a valid (NOP) rung edit against the live Echo controller returns
//      ProposeRungEditResult with status "proposed" and a message that explicitly
//      states the edit is pending and unaccepted — exactly the wording the MCP
//      model layer reads to avoid claiming the change is live.
//   2. A fragment that writes a previously-denied tag (Safety_OK, flagged as SafetyTag
//      by the Demo.L5X gate engine) now SUCCEEDS — the gate is advisory only.
//      The scan still runs but no denial is returned; the SDK is called.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using L5Sharp.Core;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;

using ControllerMode           = RockwellAutomation.LogixDesigner.LogixProject.ControllerMode;
using ConnectedState           = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState;
using DataType                 = RockwellAutomation.LogixDesigner.LogixProject.DataType;
using OperationMode            = RockwellAutomation.LogixDesigner.LogixProject.OperationMode;
using RequestedBuildTarget     = RockwellAutomation.LogixDesigner.LogixProject.RequestedBuildTarget;
using RequestedControllerMode  = RockwellAutomation.LogixDesigner.LogixProject.RequestedControllerMode;
using ImportCollisionOptions   = RockwellAutomation.LogixDesigner.LogixProject.ImportCollisionOptions;
using PartialImportOption      = RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption;
using RequestedLoadEvent       = RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadEvent;
using RequestedLoadMode        = RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadMode;
using AutomaticFirmwareUpdate  = RockwellAutomation.LogixDesigner.LogixProject.AutomaticFirmwareUpdate;

// Alias System.Threading.Tasks.Task to avoid ambiguity with L5Sharp.Core.Task
// when both namespaces are in scope.
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

// ── LogixAcdFixture ───────────────────────────────────────────────────────────

/// <summary>
/// Shared xunit collection fixture for Logix Designer SDK integration tests
/// that require a live ACD project open via an Echo controller or Logix
/// Designer installation.
/// </summary>
/// <remarks>
/// The fixture is gated on <c>LOGIX_ACD_PATH</c> — the same environment
/// variable convention established in an earlier change for SDK-backed integration
/// tests.  When the variable is absent the entire collection is skipped.
///
/// <list type="bullet">
///   <item><term>LOGIX_ACD_PATH</term><description>
///     Absolute path to the <c>.ACD</c> project file to open via the
///     Logix Designer SDK.  The file must be accessible on the host and the
///     controller must already be in Test or Run mode.  When this variable is
///     absent, all tests in the <c>LogixAcd</c> collection are skipped.
///   </description></item>
/// </list>
/// </remarks>
public sealed class LogixAcdFixture
{
    /// <summary>
    /// Absolute path to the ACD project, or <see langword="null"/> when
    /// <c>LOGIX_ACD_PATH</c> is not set.
    /// </summary>
    public string? AcdPath { get; }

    /// <summary>
    /// <see langword="true"/> when <c>LOGIX_ACD_PATH</c> is set and SDK
    /// integration tests should execute; <see langword="false"/> otherwise.
    /// </summary>
    public bool IsAvailable => AcdPath is not null;

    /// <summary>
    /// Human-readable message explaining why the test is being skipped, or an
    /// empty string when <see cref="IsAvailable"/> is <see langword="true"/>.
    /// </summary>
    public string SkipReason =>
        IsAvailable
            ? string.Empty
            : "LOGIX_ACD_PATH environment variable is not set. "
              + "Set LOGIX_ACD_PATH to the absolute path of the ACD project "
              + "file on a running Echo controller (or licensed Logix Designer "
              + "host) to run Logix SDK integration tests.";

    /// <summary>
    /// Initialises the fixture by reading <c>LOGIX_ACD_PATH</c> from the
    /// process environment.
    /// </summary>
    public LogixAcdFixture()
    {
        AcdPath = Environment.GetEnvironmentVariable("LOGIX_ACD_PATH");
    }
}

// ── LogixAcdTestCollection ────────────────────────────────────────────────────

/// <summary>
/// Defines the <c>LogixAcd</c> xunit collection and wires up
/// <see cref="LogixAcdFixture"/> as a shared collection-scoped fixture.
/// </summary>
/// <remarks>
/// All Logix SDK integration test classes should be decorated with
/// <c>[Collection("LogixAcd")]</c> so they share the fixture and receive
/// automatic environment-gating (SKIPPED when <c>LOGIX_ACD_PATH</c> is
/// absent).
/// </remarks>
[CollectionDefinition("LogixAcd")]
public sealed class LogixAcdTestCollection : ICollectionFixture<LogixAcdFixture>
{
    // This class is intentionally empty.  Its sole purpose is to serve as an
    // xunit [CollectionDefinition] anchor that associates LogixAcdFixture with
    // the "LogixAcd" collection.
}

// ── ProposeRungEditEchoTests ──────────────────────────────────────────────────

/// <summary>
/// Integration tests for <see cref="ProposeRungEditHandler"/> against a live
/// Echo controller running in Test or Run mode.
///
/// <para>Coverage:</para>
/// <list type="bullet">
///   <item>A valid NOP rung edit proposed via the live SDK session returns
///     <see cref="ProposeRungEditResult"/> with <c>status="proposed"</c> and a
///     message that explicitly states the edit is pending and unaccepted.</item>
///   <item>A fragment that writes a previously-denied tag (<c>Safety_OK</c>) now
///     succeeds — the gate is advisory only; the scan runs but does not block the
///     SDK call.</item>
/// </list>
///
/// <para>
/// All tests are skipped when <c>LOGIX_ACD_PATH</c> is not set.  Use
/// <see cref="SkippableFactAttribute"/> so the skip propagates correctly to the
/// xunit runner.
/// </para>
/// </summary>
[Collection("LogixAcd")]
[Trait("Category", "Integration")]
public class ProposeRungEditEchoTests
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private readonly LogixAcdFixture _fixture;

    /// <summary>
    /// Path to the Demo.L5X fixture, loaded to derive the <see cref="WriteGatePolicy"/>
    /// for gate-refusal tests without relying on the Echo controller's own project.
    /// </summary>
    private static readonly string DemoFixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    // ── L5X fragment constants ────────────────────────────────────────────────

    /// <summary>
    /// XPath targeting <c>MainRoutine</c> inside <c>MainProgram</c>.
    /// The Echo controller's project is expected to have this routine.
    /// </summary>
    private const string TargetRoutineXPath =
        "Controller/Programs/Program[@Name='MainProgram']" +
        "/Routines/Routine[@Name='MainRoutine']";

    /// <summary>
    /// A minimal, always-valid L5X fragment containing a single NOP rung.
    /// Used for the happy-path integration test — NOP compiles under any project.
    /// References the shared production constant so the test and the product surface
    /// stay in sync with the same L5X envelope shape.
    /// </summary>
    private const string ValidNopFragment = ProposeRungEditHandler.MinimalNopFragment;

    /// <summary>
    /// An L5X fragment containing a rung that writes <c>Safety_OK</c> via an
    /// <c>OTE</c> coil.  <c>Safety_OK</c> is denied by the Demo.L5X gate policy
    /// (classified <c>SafetyTag</c>), so this fragment must be refused by the
    /// write gate before any SDK call is made.
    /// </summary>
    private const string FragmentWritesSafetyOk =
        "<RSLogix5000Content>" +
          "<Controller>" +
            "<Programs>" +
              "<Program Name=\"MainProgram\">" +
                "<Routines>" +
                  "<Routine Name=\"MainRoutine\">" +
                    "<RLLContent>" +
                      "<Rung Number=\"0\" Type=\"N\">" +
                        "<Text><![CDATA[XIC(Cmd_MotorStart)OTE(Safety_OK);]]></Text>" +
                      "</Rung>" +
                    "</RLLContent>" +
                  "</Routine>" +
                "</Routines>" +
              "</Program>" +
            "</Programs>" +
          "</Controller>" +
        "</RSLogix5000Content>";

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the test class and skips the current test when
    /// <c>LOGIX_ACD_PATH</c> is not configured.
    /// </summary>
    /// <param name="fixture">
    /// The collection-scoped fixture injected by xunit.
    /// </param>
    public ProposeRungEditEchoTests(LogixAcdFixture fixture)
    {
        _fixture = fixture;
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Derives a <see cref="WriteGatePolicy"/> from the Demo.L5X fixture.
    /// Used for gate-refusal tests without loading the Echo controller project.
    /// <c>Safety_OK</c> is denied as <c>SafetyTag</c> in this policy.
    /// </summary>
    private static WriteGatePolicy LoadDemoPolicy() =>
        GateEngine.DerivePolicy(L5X.Load(DemoFixturePath), GateConfig.Default);

    /// <summary>
    /// Builds a fake-session-backed <see cref="LogixProjectSession"/> in the
    /// <c>Open</c> state backed by a fresh <see cref="ProposeRungEditRecordingFake"/>.
    /// Used for gate-refusal tests where we need to verify no SDK call was made —
    /// the gate fires before any SDK interaction so the session's real connectivity
    /// is irrelevant.
    /// </summary>
    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)>
        BuildFakeSessionAsync()
    {
        var fake = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        // Mark as online and correlated so the handler's pre-flight guard passes.
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    // ── Group 1 — valid rung edit against live Echo controller ────────────────

    /// <summary>
    /// (1a) Proposing a valid NOP rung edit via the live SDK session returns
    /// <see cref="ProposeRungEditResult"/> (not an error), confirming that the
    /// SDK accepted the import and the project compiled successfully after it.
    ///
    /// <para>
    /// This is the primary acceptance test: a rung edit proposed
    /// against a running Echo controller succeeds end-to-end, creating a pending
    /// edit that is observable (and acceptably declined) in Studio 5000.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task ProposeRungEdit_ValidNopRung_AgainstEchoController_ReturnsProposedResult()
    {
        // Arrange: open a real Logix Designer SDK session against the Echo controller.
        var session = new LogixProjectSession();
        try
        {
            await session.OpenAsync(_fixture.AcdPath!);

            // Act: propose the NOP rung insert at position 0 with no policy gate
            // (the gate is tested separately and does not need a live controller).
            var result = await ProposeRungEditHandler.ExecuteAsync(
                routineXPath:   TargetRoutineXPath,
                insertPosition: 0,
                replaceCount:   0,
                l5xFragment:    ValidNopFragment,
                session:        session);

            // Assert: must be a ProposeRungEditResult — not a ProposeRungEditError.
            Assert.IsType<ProposeRungEditResult>(result);
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// (1b) The returned result has <c>status="proposed"</c>, confirming the
    /// handler recognised the SDK call as successful and categorised the outcome
    /// correctly.
    /// </summary>
    [SkippableFact]
    public async Task ProposeRungEdit_ValidNopRung_AgainstEchoController_StatusIsProposed()
    {
        var session = new LogixProjectSession();
        try
        {
            await session.OpenAsync(_fixture.AcdPath!);

            var raw = await ProposeRungEditHandler.ExecuteAsync(
                TargetRoutineXPath, 0, 0, ValidNopFragment, session);

            var result = Assert.IsType<ProposeRungEditResult>(raw);
            Assert.Equal("proposed", result.Status);
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// (1c) The result message explicitly states the edit is pending and NOT live,
    /// preventing the MCP model layer from incorrectly claiming the change is
    /// active on the controller.
    /// </summary>
    [SkippableFact]
    public async Task ProposeRungEdit_ValidNopRung_AgainstEchoController_MessageStatesPendingNotLive()
    {
        var session = new LogixProjectSession();
        try
        {
            await session.OpenAsync(_fixture.AcdPath!);

            var raw = await ProposeRungEditHandler.ExecuteAsync(
                TargetRoutineXPath, 0, 0, ValidNopFragment, session);

            var result = Assert.IsType<ProposeRungEditResult>(raw);

            // The message must contain both "PENDING" and "NOT LIVE" so an
            // MCP model cannot misread the outcome as a live controller change.
            Assert.Contains("PENDING",  result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("NOT LIVE", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// (1d) The result message explicitly states the edit has NOT been accepted
    /// and is not applied to the controller — confirming that an operator must
    /// review and accept the pending edit in Logix Designer before it goes live.
    /// </summary>
    [SkippableFact]
    public async Task ProposeRungEdit_ValidNopRung_AgainstEchoController_MessageStatesEditNotAccepted()
    {
        var session = new LogixProjectSession();
        try
        {
            await session.OpenAsync(_fixture.AcdPath!);

            var raw = await ProposeRungEditHandler.ExecuteAsync(
                TargetRoutineXPath, 0, 0, ValidNopFragment, session);

            var result = Assert.IsType<ProposeRungEditResult>(raw);

            Assert.Contains("NOT",      result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("accepted", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            session.Dispose();
        }
    }

    // ── Group 2 — gate is now advisory; scan runs but no denial ─────────────

    /// <summary>
    /// (2a) A fragment that writes <c>Safety_OK</c> (previously denied as
    /// <c>SafetyTag</c> by the Demo.L5X gate policy) now succeeds — the gate
    /// is advisory.  The handler returns <see cref="ProposeRungEditResult"/>
    /// with status="proposed".
    /// </summary>
    [SkippableFact]
    public async Task ProposeRungEdit_PolicyDeniedTag_SafetyOk_NowSucceeds()
    {
        // Arrange: derive the Demo.L5X gate policy (Safety_OK → previously denied: SafetyTag).
        var policy = LoadDemoPolicy();

        // Use a fake session so the test does not require a live Echo controller.
        var (session, _) = await BuildFakeSessionAsync();

        // Act: propose a rung that writes Safety_OK.
        var result = await ProposeRungEditHandler.ExecuteAsync(
            routineXPath:   TargetRoutineXPath,
            insertPosition: 0,
            replaceCount:   0,
            l5xFragment:    FragmentWritesSafetyOk,
            session:        session,
            policy:         policy);

        // Assert: gate is advisory — result is a success, not an error.
        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("proposed", success.Status);
    }

    /// <summary>
    /// (2b) With the gate advisory, a fragment writing <c>Safety_OK</c> produces
    /// a <see cref="ProposeRungEditResult"/> — the scan still runs but does not
    /// generate an error message about the tag.
    /// </summary>
    [SkippableFact]
    public async Task ProposeRungEdit_PolicyDeniedTag_SafetyOk_ResultIsSuccess_NotError()
    {
        var policy = LoadDemoPolicy();
        var (session, _) = await BuildFakeSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            TargetRoutineXPath, 0, 0, FragmentWritesSafetyOk, session, policy);

        // Gate is advisory: result is ProposeRungEditResult, never ProposeRungEditError.
        Assert.IsNotType<ProposeRungEditError>(result);
        Assert.IsType<ProposeRungEditResult>(result);
    }

    /// <summary>
    /// (2c) The SDK import IS called when a policy is supplied and the fragment
    /// writes a previously-denied tag — the gate is advisory and does not
    /// block the SDK call.
    /// </summary>
    [SkippableFact]
    public async Task ProposeRungEdit_PolicyDeniedTag_SafetyOk_SdkCallIsMade()
    {
        var policy = LoadDemoPolicy();
        var (session, fake) = await BuildFakeSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            TargetRoutineXPath, 0, 0, FragmentWritesSafetyOk, session, policy);

        // The gate is advisory: the SDK import must have been called.
        Assert.NotEmpty(fake.CapturedXPaths);
    }
}
#endif
