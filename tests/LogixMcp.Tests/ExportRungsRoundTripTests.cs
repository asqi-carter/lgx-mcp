#if LOGIX_SDK
// ── ExportRungsRoundTripTests — end-to-end export → propose round trip ────────
//
// These tests document and verify that the l5x output from logix_export_rungs
// (ExportRungsHandler.ExecuteAsync) can be fed directly and unmodified into
// logix_propose_rung_edit (ProposeRungEditHandler.ExecuteAsync / ExecuteOfflineAsync)
// without triggering an envelope validation error or any other pre-SDK rejection.
//
// The round trip is tested in two complementary ways:
//
//   Offline path (Group 1):
//     Uses ProposeRungEditHandler.ExecuteOfflineAsync so that no live SDK session
//     or controller is required.  The exported L5X is applied directly to a temp
//     copy of the Demo.L5X fixture.  These tests always run — no environment
//     variable is needed.
//
//   Online path (Group 2):
//     Uses ProposeRungEditHandler.ExecuteAsync with a ProposeRungEditRecordingFake
//     session (IsOnline=true, IsCorrelated=true).  The session never contacts a
//     real controller; its purpose is to verify that the exported fragment passes
//     the pre-SDK validation layer (envelope check, optional gate check) and
//     reaches the PartialImportRungsFromXmlFileAsync call without producing a
//     ProposeRungEditError.  These tests always run.
//
// If the round trip does not work (i.e., the exported fragment is rejected by the
// import path), each test reports the failure explicitly with the rejection reason
// rather than skipping or masking broken behaviour.

using System.Text.Json;
using System.Xml.Linq;
using RockwellAutomation.LogixDesigner;

// Alias to avoid ambiguity with L5Sharp.Core.Task when both namespaces are in scope.
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

// ── ExportRungsRoundTripTests ─────────────────────────────────────────────────

/// <summary>
/// Documents and verifies the <c>logix_export_rungs</c> →
/// <c>logix_propose_rung_edit</c> round trip.
///
/// <para>
/// The SDK's <see cref="ILogixProject.PartialExportToXmlFileAsync"/> produces an
/// L5X fragment that already has the <c>RSLogix5000Content</c> root element and
/// the required <c>RLLContent</c> inner element.
/// <see cref="ProposeRungEditHandler.ExecuteAsync"/> and
/// <see cref="ProposeRungEditHandler.ExecuteOfflineAsync"/> both perform an
/// envelope validation that requires exactly these two elements.  This means the
/// exported fragment is directly usable as the <c>l5xFragment</c> input — no
/// reformatting or re-wrapping is needed.
/// </para>
///
/// <para>Coverage areas:</para>
/// <list type="bullet">
///   <item>Exported L5X root element is <c>RSLogix5000Content</c>.</item>
///   <item>Exported L5X contains an <c>RLLContent</c> descendant.</item>
///   <item>Full offline round trip: exported L5X → offline rung edit — succeeds.</item>
///   <item>Full offline round trip: result has <c>status="saved-to-file"</c>.</item>
///   <item>Full online round trip: exported L5X passes pre-SDK validation and
///     reaches the SDK import call without a <see cref="ProposeRungEditError"/>.</item>
///   <item>Full online round trip: the SDK import XPath matches the routine XPath.</item>
/// </list>
/// </summary>
public class ExportRungsRoundTripTests
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>
    /// SDK XPath targeting <c>MainRoutine</c> inside <c>MainProgram</c>.
    /// Both the export and the import use this XPath.
    /// </summary>
    private const string TargetRoutineXPath =
        "Controller/Programs/Program[@Name='MainProgram']" +
        "/Routines/Routine[@Name='MainRoutine']";

    /// <summary>
    /// Path to the Demo.L5X fixture, used as the project file for offline
    /// round-trip tests.  A temp copy is created and mutated; the original is
    /// never modified.
    /// </summary>
    private static readonly string DemoFixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    /// <summary>
    /// A realistic L5X fragment shaped like a real
    /// <see cref="ILogixProject.PartialExportToXmlFileAsync"/> output for
    /// <c>MainProgram/MainRoutine</c>.
    ///
    /// <para>
    /// The Logix Designer SDK wraps exported rung content in the full
    /// <c>RSLogix5000Content &gt; Controller &gt; Programs &gt; Program &gt;
    /// Routines &gt; Routine &gt; RLLContent</c> envelope.  This constant
    /// captures that shape so the round-trip tests can verify that the exported
    /// fragment is accepted without modification by
    /// <see cref="ProposeRungEditHandler"/>.
    /// </para>
    ///
    /// <para>
    /// The <c>Program Name</c> and <c>Routine Name</c> attributes match
    /// <see cref="TargetRoutineXPath"/> so that both the export XPath and the
    /// import routineXPath refer to the same routine.
    /// </para>
    /// </summary>
    private const string SimulatedExportedL5X =
        "<RSLogix5000Content SchemaRevision=\"1.0\" TargetName=\"DemoController\" TargetType=\"Controller\">" +
          "<Controller Name=\"DemoController\">" +
            "<Programs>" +
              "<Program Name=\"MainProgram\">" +
                "<Routines>" +
                  "<Routine Name=\"MainRoutine\" Type=\"RLL\">" +
                    "<RLLContent>" +
                      "<Rung Number=\"0\" Type=\"N\">" +
                        "<Text><![CDATA[XIC(Cmd_MotorStart)XIC(Safety_OK)OTE(Motor_Run);]]></Text>" +
                      "</Rung>" +
                      "<Rung Number=\"1\" Type=\"N\">" +
                        "<Text><![CDATA[NOP();]]></Text>" +
                      "</Rung>" +
                    "</RLLContent>" +
                  "</Routine>" +
                "</Routines>" +
              "</Program>" +
            "</Programs>" +
          "</Controller>" +
        "</RSLogix5000Content>";

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the export step with a <see cref="FakeExportProject"/> configured
    /// to emit <see cref="SimulatedExportedL5X"/> and returns the
    /// <see cref="ExportRungsResult"/>.
    /// </summary>
    private static async Task<ExportRungsResult> RunExportStepAsync()
    {
        var fake = new FakeExportProject { ExportContent = SimulatedExportedL5X };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        var raw = await ExportRungsHandler.ExecuteAsync(TargetRoutineXPath, session);

        return Assert.IsType<ExportRungsResult>(raw);
    }

    /// <summary>
    /// Builds a <see cref="LogixProjectSession"/> backed by a fresh
    /// <see cref="ProposeRungEditRecordingFake"/> with <c>IsOnline</c> and
    /// <c>IsCorrelated</c> set to <c>true</c>, so the handler's pre-flight guard
    /// passes without a live controller.
    /// </summary>
    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)>
        BuildOnlineImportSessionAsync()
    {
        var fake = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    // ── Group 0 — Exported L5X envelope shape ────────────────────────────────

    /// <summary>
    /// (0a) The L5X fragment emitted by <see cref="ExportRungsHandler.ExecuteAsync"/>
    /// has <c>RSLogix5000Content</c> as its root element — the first requirement of
    /// <see cref="ProposeRungEditHandler.ValidateFragmentEnvelope"/>.
    ///
    /// <para>
    /// This test documents the contract: any fragment exported by the SDK through
    /// <c>PartialExportToXmlFileAsync</c> uses the full <c>RSLogix5000Content</c>
    /// wrapper, making it structurally compatible with the import path.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ExportedL5X_HasRSLogix5000ContentRootElement()
    {
        var result = await RunExportStepAsync();

        var doc = XDocument.Parse(result.L5X);
        Assert.Equal("RSLogix5000Content", doc.Root?.Name.LocalName);
    }

    /// <summary>
    /// (0b) The exported L5X contains an <c>RLLContent</c> descendant — the
    /// second requirement of the envelope validator.
    ///
    /// <para>
    /// Both conditions together ensure the fragment will pass
    /// <see cref="ProposeRungEditHandler.ValidateFragmentEnvelope"/> without
    /// triggering the "allows only to import RLLContent" rejection.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ExportedL5X_HasRLLContentDescendant()
    {
        var result = await RunExportStepAsync();

        var doc = XDocument.Parse(result.L5X);
        Assert.True(doc.Descendants("RLLContent").Any(),
            "Exported L5X must contain an <RLLContent> element for the round trip to succeed.");
    }

    // ── Group 1 — Offline round trip (Export → ProposeOffline) ───────────────

    /// <summary>
    /// (1a) The exported L5X, fed unmodified to
    /// <see cref="ProposeRungEditHandler.ExecuteOfflineAsync"/>, returns a
    /// <see cref="ProposeRungEditOfflineResult"/> — not an error.
    ///
    /// <para>
    /// This is the primary round-trip acceptance test: the output of the export
    /// step satisfies all validation requirements of the import step and the
    /// offline file write succeeds.
    /// </para>
    ///
    /// <para>
    /// If this test fails, the round trip is broken.  The failure message
    /// includes the <see cref="ProposeRungEditError.Error"/> text so the
    /// specific rejection reason is visible without inspecting test runner logs.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RoundTrip_Offline_ExportedL5XUnmodified_ReturnsOfflineResult()
    {
        // Step 1: Export
        var exportResult = await RunExportStepAsync();
        var exportedL5X = exportResult.L5X;   // ← unmodified export output

        // Step 2: Create a temp copy of Demo.L5X to apply the edit to.
        //         The original fixture must not be mutated.
        var tempFile = Path.GetTempFileName();
        File.Copy(DemoFixturePath, tempFile, overwrite: true);

        try
        {
            // Step 3: Propose using the unmodified exported fragment (offline mode).
            var importRaw = await ProposeRungEditHandler.ExecuteOfflineAsync(
                routineXPath:    TargetRoutineXPath,
                insertPosition:  0,
                replaceCount:    0,
                l5xFragment:     exportedL5X,
                projectFilePath: tempFile);

            // Step 4: Assert — must succeed.
            if (importRaw is ProposeRungEditError err)
            {
                Assert.Fail(
                    $"Round trip BROKEN — exported L5X was rejected by ExecuteOfflineAsync. " +
                    $"Error: {err.Error}" +
                    (err.Hint is not null ? $"  Hint: {err.Hint}" : string.Empty));
            }

            Assert.IsType<ProposeRungEditOfflineResult>(importRaw);
        }
        finally
        {
            if (File.Exists(tempFile))
                try { File.Delete(tempFile); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// (1b) The offline round-trip result has <c>status="saved-to-file"</c>,
    /// confirming that the handler recognised the operation as a successful
    /// direct-to-file write.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Offline_ExportedL5XUnmodified_StatusIsSavedToFile()
    {
        var exportResult = await RunExportStepAsync();

        var tempFile = Path.GetTempFileName();
        File.Copy(DemoFixturePath, tempFile, overwrite: true);

        try
        {
            var importRaw = await ProposeRungEditHandler.ExecuteOfflineAsync(
                routineXPath:    TargetRoutineXPath,
                insertPosition:  0,
                replaceCount:    0,
                l5xFragment:     exportResult.L5X,
                projectFilePath: tempFile);

            if (importRaw is ProposeRungEditError err)
            {
                Assert.Fail(
                    $"Round trip BROKEN — exported L5X was rejected. Error: {err.Error}");
            }

            var offlineResult = Assert.IsType<ProposeRungEditOfflineResult>(importRaw);
            Assert.Equal("saved-to-file", offlineResult.Status);
        }
        finally
        {
            if (File.Exists(tempFile))
                try { File.Delete(tempFile); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// (1c) The offline round-trip result records the correct routine XPath.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Offline_ExportedL5XUnmodified_RoutineXPathPreserved()
    {
        var exportResult = await RunExportStepAsync();

        var tempFile = Path.GetTempFileName();
        File.Copy(DemoFixturePath, tempFile, overwrite: true);

        try
        {
            var importRaw = await ProposeRungEditHandler.ExecuteOfflineAsync(
                routineXPath:    TargetRoutineXPath,
                insertPosition:  0,
                replaceCount:    0,
                l5xFragment:     exportResult.L5X,
                projectFilePath: tempFile);

            if (importRaw is ProposeRungEditError err)
            {
                Assert.Fail(
                    $"Round trip BROKEN — exported L5X was rejected. Error: {err.Error}");
            }

            var offlineResult = Assert.IsType<ProposeRungEditOfflineResult>(importRaw);
            Assert.Equal(TargetRoutineXPath, offlineResult.RoutineXPath);
        }
        finally
        {
            if (File.Exists(tempFile))
                try { File.Delete(tempFile); } catch { /* best-effort */ }
        }
    }

    // ── Group 2 — Online round trip (Export → ProposeAsync with recording fake) ─

    /// <summary>
    /// (2a) The exported L5X, fed unmodified to
    /// <see cref="ProposeRungEditHandler.ExecuteAsync"/>, does not produce a
    /// <see cref="ProposeRungEditError"/> — the fragment passes all pre-SDK
    /// validation (envelope check, gate) and reaches the SDK import call.
    ///
    /// <para>
    /// A <see cref="ProposeRungEditRecordingFake"/> session (IsOnline=true,
    /// IsCorrelated=true) is used so that no live controller is needed.  The
    /// fake records the <c>PartialImportRungsFromXmlFileAsync</c> call, confirming
    /// the export output cleared every validation layer.
    /// </para>
    ///
    /// <para>
    /// If this test fails with a <see cref="ProposeRungEditError"/>, the round
    /// trip is broken and the test reports the specific rejection reason so
    /// nothing is masked.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RoundTrip_Online_ExportedL5XUnmodified_DoesNotReturnError()
    {
        // Step 1: Export
        var exportResult = await RunExportStepAsync();
        var exportedL5X = exportResult.L5X;   // ← unmodified export output

        // Step 2: Build an online fake session for the import step.
        var (importSession, _) = await BuildOnlineImportSessionAsync();

        // Step 3: Propose with the unmodified exported fragment.
        var importRaw = await ProposeRungEditHandler.ExecuteAsync(
            routineXPath:   TargetRoutineXPath,
            insertPosition: 0,
            replaceCount:   0,
            l5xFragment:    exportedL5X,
            session:        importSession);

        // Step 4: Assert — must not be an error.
        if (importRaw is ProposeRungEditError err)
        {
            Assert.Fail(
                $"Round trip BROKEN — exported L5X was rejected by ExecuteAsync. " +
                $"Error: {err.Error}" +
                (err.Hint is not null ? $"  Hint: {err.Hint}" : string.Empty));
        }
    }

    /// <summary>
    /// (2b) The exported L5X reaches the SDK import call — verified by asserting
    /// that the recording fake's <c>CapturedXPaths</c> is non-empty after the
    /// handler returns.
    ///
    /// <para>
    /// A non-empty <c>CapturedXPaths</c> proves that
    /// <c>PartialImportRungsFromXmlFileAsync</c> was invoked, i.e., the fragment
    /// cleared the envelope validator and any gate check and made it all the way
    /// to the SDK boundary.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RoundTrip_Online_ExportedL5XUnmodified_ReachesSdkImportCall()
    {
        var exportResult = await RunExportStepAsync();
        var (importSession, importFake) = await BuildOnlineImportSessionAsync();

        var importRaw = await ProposeRungEditHandler.ExecuteAsync(
            routineXPath:   TargetRoutineXPath,
            insertPosition: 0,
            replaceCount:   0,
            l5xFragment:    exportResult.L5X,
            session:        importSession);

        if (importRaw is ProposeRungEditError err)
        {
            Assert.Fail(
                $"Round trip BROKEN — fragment was rejected before the SDK call. " +
                $"Error: {err.Error}" +
                (err.Hint is not null ? $"  Hint: {err.Hint}" : string.Empty));
        }

        // Confirm the import call was recorded (fragment cleared all validation layers).
        Assert.NotEmpty(importFake.CapturedXPaths);
    }

    /// <summary>
    /// (2c) The SDK import call receives the same XPath that was supplied as
    /// <c>routineXPath</c> — confirming the handler forwards the parameter
    /// verbatim rather than deriving a different path from the fragment.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Online_ExportedL5XUnmodified_SdkCallReceivesCorrectXPath()
    {
        var exportResult = await RunExportStepAsync();
        var (importSession, importFake) = await BuildOnlineImportSessionAsync();

        var importRaw = await ProposeRungEditHandler.ExecuteAsync(
            routineXPath:   TargetRoutineXPath,
            insertPosition: 0,
            replaceCount:   0,
            l5xFragment:    exportResult.L5X,
            session:        importSession);

        if (importRaw is ProposeRungEditError err)
        {
            Assert.Fail(
                $"Round trip BROKEN — fragment was rejected before the SDK call. " +
                $"Error: {err.Error}");
        }

        Assert.Single(importFake.CapturedXPaths);
        Assert.Equal(TargetRoutineXPath, importFake.CapturedXPaths[0]);
    }

    // ── Group 3 — Tool-layer (LogixSdkTools) round trip description contract ──

    /// <summary>
    /// (3a) The <c>logix_export_rungs</c> tool description mentions that the
    /// <c>l5x</c> output is directly usable as the <c>l5xFragment</c> input to
    /// <c>logix_propose_rung_edit</c>.
    ///
    /// <para>
    /// This test locks the documentation contract so future edits to the
    /// description cannot silently remove the round-trip discoverability text
    /// without a test failure.
    /// </para>
    /// </summary>
    [Fact]
    public void ExportRungs_ToolDescription_MentionsRoundTripToPropose()
    {
        var method = typeof(LogixSdkTools).GetMethod(nameof(LogixSdkTools.ExportRungs));
        Assert.NotNull(method);

        var descAttr = method!.GetCustomAttributes(inherit: false)
            .FirstOrDefault(a => a.GetType().Name == "DescriptionAttribute");
        Assert.NotNull(descAttr);

        var description = descAttr!.GetType().GetProperty("Description")?.GetValue(descAttr) as string;
        Assert.NotNull(description);

        // The description must tell callers that the l5x output is directly usable
        // as l5xFragment input to logix_propose_rung_edit.
        Assert.True(
            description!.Contains("logix_propose_rung_edit", StringComparison.OrdinalIgnoreCase) &&
            description.Contains("l5x", StringComparison.OrdinalIgnoreCase),
            $"logix_export_rungs description must mention that its l5x output is usable " +
            $"by logix_propose_rung_edit. Actual description: {description}");
    }

    /// <summary>
    /// (3b) The <c>logix_propose_rung_edit</c> tool description mentions that the
    /// <c>l5x</c> field from <c>logix_export_rungs</c> is directly usable as
    /// <c>l5xFragment</c>.
    ///
    /// <para>
    /// Mirrors (3a) for the import tool: the round-trip discoverability hint must
    /// be present on both ends of the workflow so an MCP model can discover it
    /// regardless of which tool description it reads first.
    /// </para>
    /// </summary>
    [Fact]
    public void ProposeRungEdit_ToolDescription_MentionsExportRoundTrip()
    {
        var method = typeof(LogixSdkTools).GetMethod(nameof(LogixSdkTools.ProposeRungEdit));
        Assert.NotNull(method);

        var descAttr = method!.GetCustomAttributes(inherit: false)
            .FirstOrDefault(a => a.GetType().Name == "DescriptionAttribute");
        Assert.NotNull(descAttr);

        var description = descAttr!.GetType().GetProperty("Description")?.GetValue(descAttr) as string;
        Assert.NotNull(description);

        // The description must tell callers that l5x from logix_export_rungs is
        // directly usable as l5xFragment input.
        Assert.True(
            description!.Contains("logix_export_rungs", StringComparison.OrdinalIgnoreCase) &&
            description.Contains("l5xFragment", StringComparison.OrdinalIgnoreCase),
            $"logix_propose_rung_edit description must mention logix_export_rungs and l5xFragment. " +
            $"Actual description: {description}");
    }

    /// <summary>
    /// (3c) The <c>l5xFragment</c> parameter of <c>logix_propose_rung_edit</c>
    /// has a description that explicitly mentions <c>logix_export_rungs</c>, so
    /// parameter-level help surfaces the round-trip discoverability hint.
    /// </summary>
    [Fact]
    public void ProposeRungEdit_L5xFragmentParameter_DescriptionMentionsExportRungs()
    {
        var method = typeof(LogixSdkTools).GetMethod(nameof(LogixSdkTools.ProposeRungEdit));
        Assert.NotNull(method);

        // l5xFragment is the 4th parameter (index 3).
        var param = method!.GetParameters()
            .FirstOrDefault(p => p.Name == "l5xFragment");
        Assert.NotNull(param);

        var descAttr = param!.GetCustomAttributes(inherit: false)
            .FirstOrDefault(a => a.GetType().Name == "DescriptionAttribute");
        Assert.NotNull(descAttr);

        var description = descAttr!.GetType().GetProperty("Description")?.GetValue(descAttr) as string;
        Assert.NotNull(description);

        Assert.Contains("logix_export_rungs", description!, StringComparison.OrdinalIgnoreCase);
    }
}
#endif
