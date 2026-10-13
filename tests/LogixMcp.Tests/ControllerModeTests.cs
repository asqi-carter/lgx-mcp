#if LOGIX_SDK
using System.Text.Json;
using RockwellAutomation.LogixDesigner;

using ControllerMode  = RockwellAutomation.LogixDesigner.LogixProject.ControllerMode;
using ConnectedState  = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for the Unknown controller-mode annotation that
/// <see cref="PendingEditsHandler"/> injects when
/// <c>ReadConnectedStateAsync</c> returns <see cref="ConnectedState.Online"/>
/// and <c>ReadControllerModeAsync</c> returns <c>Unknown</c>.
///
/// <para>
/// Echo controllers have been observed to return <c>Unknown</c> for the
/// controller mode even while the session is confirmed ONLINE.  The response
/// must carry the <c>mode:'Unknown'</c> value unchanged (so callers can detect
/// the anomaly) together with a <c>note</c> field that names the known cause.
/// </para>
/// </summary>
public partial class ControllerModeTests
{
    // ── Minimal one-rung L5X required by PendingEditsHandler ─────────────────

    private static readonly string OneRungL5X =
        "<RSLogix5000Content><Controller><Programs>" +
        "<Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent>" +
        "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
        "</RLLContent></Routine>" +
        "</Routines></Program>" +
        "</Programs></Controller></RSLogix5000Content>";

    // ── UnknownWhileOnline_IsAnnotated (contractual) ──────────────────────────

    /// <summary>
    /// When the SDK returns <c>Unknown</c> for the controller mode and
    /// <c>ReadConnectedStateAsync</c> returns <c>Online</c>, the
    /// <see cref="PendingEditsResult"/> must include:
    /// <list type="bullet">
    ///   <item><c>mode:'Unknown'</c> — not suppressed.</item>
    ///   <item><c>note:'SDK returned Unknown while online; Echo controllers have shown this'</c>
    ///     — the annotation.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task UnknownWhileOnline_IsAnnotated()
    {
        var fake = new FakePendingEditsProject
        {
            ExportContent          = OneRungL5X,
            ControllerModeToReturn = (ControllerMode)99,  // unrecognised value: Echo controllers have sent this
            ConnectedStateToReturn = ConnectedState.Online,
        };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var raw = await PendingEditsHandler.ExecuteAsync("MainProgram", "MainRoutine", session);

        var result = Assert.IsType<PendingEditsResult>(raw);

        // mode:'Unknown' must be present, not suppressed.
        Assert.Equal("Unknown", result.ControllerMode);

        // note must be present with the exact annotation text.
        Assert.Equal(
            "SDK returned Unknown while online; Echo controllers have shown this",
            result.Note);
    }

    // ── UnknownWhileOnline_NoteAppearsInJson ─────────────────────────────────

    /// <summary>
    /// The serialised JSON must carry both <c>"controllerMode":"Unknown"</c>
    /// and <c>"note":"SDK returned Unknown while online; Echo controllers have
    /// shown this"</c> side by side.
    /// </summary>
    [Fact]
    public async Task UnknownWhileOnline_NoteAppearsInJson()
    {
        var fake = new FakePendingEditsProject
        {
            ExportContent          = OneRungL5X,
            ControllerModeToReturn = (ControllerMode)99,  // unrecognised value: Echo controllers have sent this
            ConnectedStateToReturn = ConnectedState.Online,
        };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var raw  = await PendingEditsHandler.ExecuteAsync("MainProgram", "MainRoutine", session);
        var json = JsonSerializer.Serialize(raw);
        var doc  = JsonDocument.Parse(json);

        Assert.Equal("Unknown",
            doc.RootElement.GetProperty("controllerMode").GetString());

        Assert.Equal(
            "SDK returned Unknown while online; Echo controllers have shown this",
            doc.RootElement.GetProperty("note").GetString());
    }

    // ── UnknownWhileOffline_NoAnnotation ─────────────────────────────────────

    /// <summary>
    /// When the SDK returns <c>Unknown</c> for the controller mode but the
    /// connected state is NOT <c>Online</c> (here: <c>Offline</c>), no
    /// annotation must be added — <c>Note</c> must be <c>null</c> and the
    /// <c>"note"</c> key must be absent from the serialised JSON.
    /// </summary>
    [Fact]
    public async Task UnknownWhileOffline_NoAnnotation()
    {
        var fake = new FakePendingEditsProject
        {
            ExportContent          = OneRungL5X,
            ControllerModeToReturn = (ControllerMode)99,  // unrecognised value: Echo controllers have sent this
            ConnectedStateToReturn = ConnectedState.Offline,
        };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var raw = await PendingEditsHandler.ExecuteAsync("MainProgram", "MainRoutine", session);

        var result = Assert.IsType<PendingEditsResult>(raw);

        // mode:'Unknown' must still be surfaced.
        Assert.Equal("Unknown", result.ControllerMode);

        // No annotation when not online.
        Assert.Null(result.Note);
    }

    // ── UnknownWhileOffline_NoteAbsentFromJson ────────────────────────────────

    /// <summary>
    /// JSON for the non-online Unknown case must not carry the <c>"note"</c> key
    /// (the property is null and decorated with
    /// <c>JsonIgnore(Condition = WhenWritingNull)</c>).
    /// </summary>
    [Fact]
    public async Task UnknownWhileOffline_NoteAbsentFromJson()
    {
        var fake = new FakePendingEditsProject
        {
            ExportContent          = OneRungL5X,
            ControllerModeToReturn = (ControllerMode)99,  // unrecognised value: Echo controllers have sent this
            ConnectedStateToReturn = ConnectedState.Offline,
        };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var raw  = await PendingEditsHandler.ExecuteAsync("MainProgram", "MainRoutine", session);
        var json = JsonSerializer.Serialize(raw);
        var doc  = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("note", out _),
            "\"note\" key must not appear in JSON when the mode is Unknown but the " +
            "connected state is not Online.");
    }

    // ── KnownMode_OnlineOrOffline_NoAnnotation ────────────────────────────────

    /// <summary>
    /// For any mode other than <c>Unknown</c> (here: <c>Run</c> while online),
    /// <c>Note</c> must remain <c>null</c> — the annotation is exclusive to the
    /// Unknown+Online combination.
    /// </summary>
    [Theory]
    [InlineData(ControllerMode.Run,     ConnectedState.Online)]
    [InlineData(ControllerMode.Program, ConnectedState.Online)]
    [InlineData(ControllerMode.Test,    ConnectedState.Online)]
    [InlineData(ControllerMode.Faulted, ConnectedState.Online)]
    [InlineData(ControllerMode.Run,     ConnectedState.Offline)]
    public async Task KnownMode_OnlineOrOffline_NoAnnotation(
        ControllerMode mode, ConnectedState state)
    {
        var fake = new FakePendingEditsProject
        {
            ExportContent          = OneRungL5X,
            ControllerModeToReturn = mode,
            ConnectedStateToReturn = state,
        };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var raw    = await PendingEditsHandler.ExecuteAsync("MainProgram", "MainRoutine", session);
        var result = Assert.IsType<PendingEditsResult>(raw);

        Assert.Null(result.Note);
    }

    // ── logix_controller_mode live read ────────────────────────────

    private const string EmptyL5X =
        "<RSLogix5000Content><Controller Name=\"C\"/></RSLogix5000Content>";

    private static async Task<(TagSessionService Tags, ControllerModeService Modes, LogixProjectSession Session)>
        LiveToolSessionAsync(FakePendingEditsProject fake)
    {
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        var svc  = new TypedSdkTagService(session, L5Sharp.Core.L5X.Parse(EmptyL5X),
            LogixProject.OperationMode.Online);
        var tags = new TagSessionService();
        tags.Attach(
            new GatedTagService(svc, new WriteGatePolicy(
                Array.Empty<GrantEntry>(), Array.Empty<DenyEntry>())),
            isVirtual: false, isLiveController: true);
        var modes = new ControllerModeService();
        modes.Reset("Unknown");   // what logix_connect leaves for live sessions
        return (tags, modes, session);
    }

    /// <summary>
    /// On a live SDK session the <c>logix_controller_mode</c> read must report
    /// the same mode as <c>logix_pending_edits</c> (not the "Unknown" placeholder).
    /// </summary>
    [Fact]
    public async Task LiveSession_Read_UsesSameSourceAsPendingEdits()
    {
        var fake = new FakePendingEditsProject
        {
            ExportContent          = OneRungL5X,
            ControllerModeToReturn = ControllerMode.Run,
            ConnectedStateToReturn = ConnectedState.Online,
        };
        var (tags, modes, session) = await LiveToolSessionAsync(fake);

        var pending = Assert.IsType<PendingEditsResult>(
            await PendingEditsHandler.ExecuteAsync("MainProgram", "MainRoutine", session));
        var tool = Assert.IsType<ControllerModeResult>(
            await ControllerModeHandler.ExecuteAsync(null, false, tags, modes));

        Assert.Equal("Run", pending.ControllerMode);
        Assert.Equal(pending.ControllerMode, tool.ControllerMode);
        Assert.Null(tool.Note);
    }

    /// <summary>An unreadable mode yields Unknown plus a note naming the SDK error.</summary>
    [Fact]
    public async Task LiveSession_UnreadableMode_ReturnsUnknownWithNoteNamingSdkError()
    {
        var fake = new FakePendingEditsProject
        {
            ThrowOnReadControllerMode = true,
        };
        var (tags, modes, _) = await LiveToolSessionAsync(fake);

        var tool = Assert.IsType<ControllerModeResult>(
            await ControllerModeHandler.ExecuteAsync(null, false, tags, modes));

        Assert.Equal("Unknown", tool.ControllerMode);
        Assert.NotNull(tool.Note);
        Assert.Contains("ReadControllerModeAsync", tool.Note);
        Assert.Contains("Simulated controller-mode read failure.", tool.Note);
    }
}
#endif
