#if LOGIX_SDK
// ── StudioLifecycleHandler — shared, verb-parameterised flow behind the Studio-GUI
//    pending-edit lifecycle tools ─────────────────────────
using System.Text.Json;
using Task = System.Threading.Tasks.Task;

/// <summary>
/// The wording and side-effect hooks that distinguish one Studio-lifecycle verb
/// (<c>cancel</c> | <c>accept</c>) from another: see <see cref="Cancel"/> and
/// <see cref="Accept"/>. Everything the shared flow in
/// <see cref="StudioLifecycleHandler.ExecuteAsync"/> says to the caller that is
/// verb-specific lives here, so the flow itself is written once.
/// </summary>
/// <param name="Verb">
/// The <c>bench/studio-edits.ps1 -Verb</c> value and the runner verb
/// (<c>"cancel"</c> or <c>"accept"</c>).
/// </param>
/// <param name="ToolName">The MCP tool name quoted in refusals.</param>
/// <param name="Status">The <c>status</c> value on success (<c>"cancelled"</c>, <c>"accepted"</c>).</param>
/// <param name="GestureMenu">
/// The Studio menu path the driver exercises, e.g.
/// <c>Logic -> Online Edits -> Cancel Accepted Program Edits</c>.
/// </param>
/// <param name="CapitalVerb">The verb as a sentence start (<c>Cancel</c>).</param>
/// <param name="Gerund">The verb's -ing form without capitals (<c>cancelling</c>).</param>
/// <param name="PastCapital">The past tense as a sentence start (<c>Cancelled</c>).</param>
/// <param name="Consequence">
/// Clause naming what the verb does to the pending edits, used in the
/// <c>confirm</c> refusal (e.g. <c>cancelling discards the controller's pending rung edits</c>).
/// </param>
/// <param name="RequireAfterNone">
/// When <c>true</c>, a gesture that reports success while the after-state is
/// still pending is an error naming both states — success is never claimed on
/// the gesture alone (used by <c>accept</c>, <c>assemble</c> and, since bench S14-p102,
/// <c>cancel</c>). The after-state is re-read until it settles (<see cref="StudioLifecycleHandler.AfterSettleAttempts"/>).
/// </param>
/// <param name="Audit">
/// Writes the audit-trail entry: <c>(program, routine, status, beforeState, afterState)</c>.
/// </param>
/// <param name="RequireAfterPending">
/// When <c>true</c>, a gesture that reports success while the after-state is
/// <c>none</c> is an error (used by <c>test</c> / <c>untest</c>, whose dialog closing
/// leaves the edit zone in place).
/// </param>
/// <param name="PassDialogInfo">
/// When <c>true</c>, the driver's <c>dialogText</c> and <c>dialogWarnings</c> are passed
/// through on the result and on post-launch errors. <c>false</c> keeps the
/// cancel/accept payloads unchanged.
/// </param>
/// <param name="SuccessLead">
/// Replaces the default <c>"{PastCapital} pending edits for"</c> opening of the success
/// message; <c>null</c> keeps the default.
/// </param>
/// <param name="SuccessNote">Sentence(s) appended to the success message; <c>null</c> for none.</param>
/// <param name="NotApplicableMessage">
/// When non-<c>null</c>, a driver failure with reason <c>command_not_applicable</c> is
/// returned with <c>kind=<paramref name="NotApplicableKind"/></c> and this message (used by
/// <c>assemble</c> and <c>cancel</c>) — a fallback for when the state precondition did not catch it.
/// </param>
/// <param name="NotApplicableKind">The <c>kind</c> of the <c>command_not_applicable</c> fallback; default <c>"NotTested"</c>.</param>
/// <param name="NotApplicableTestedMessage">
/// The <c>command_not_applicable</c> wording used when the before snapshot's <c>zoneState</c> is
/// <c>"tested"</c>; <paramref name="NotApplicableMessage"/> is used for every other state. Wording that
/// blames the tested state must live here only. <c>null</c> falls back to <paramref name="NotApplicableMessage"/>.
/// </param>
/// <param name="RefuseWhenZoneState">
/// When the routine's <c>zoneState</c> equals this value (<c>"tested"</c> or
/// <c>"untested"</c>) the verb is refused after the before-read and before the runner is
/// invoked, so no Studio command is sent. <c>null</c> for verbs with no state precondition.
/// </param>
/// <param name="RefusalKind">The <c>kind</c> of the state-precondition refusal.</param>
/// <param name="RefusalMessage">The message of the state-precondition refusal.</param>
/// <param name="RequireAfterZoneState">
/// When non-<c>null</c> (<c>"tested"</c> for test, <c>"untested"</c> for untest) the
/// after-read must show this <c>zoneState</c>; it is re-read on the settle loop until it does.
/// A wrong after-state is an error of kind <paramref name="StateNotConfirmedKind"/>.
/// </param>
/// <param name="StateNotConfirmedKind">The <c>kind</c> of the after-state-wrong error.</param>
/// <param name="AuditFailure">
/// Writes the audit line of a call that failed after the pre-launch checks:
/// <c>(verb, program, routine, kind, reason, beforeState, afterState)</c>, outcome <c>"error"</c>.
/// <c>null</c> (a verb built without an audit trail) writes no failure and no refusal line; the shipped
/// verbs use <see cref="AuditLogger.LogLifecycleFailure"/>. Pre-launch refusals go through
/// <see cref="AuditLogger.LogToolRefused"/> whenever this is set.
/// </param>
public sealed record StudioLifecycleVerb(
    string Verb,
    string ToolName,
    string Status,
    string GestureMenu,
    string CapitalVerb,
    string Gerund,
    string PastCapital,
    string Consequence,
    bool   RequireAfterNone,
    Action<string, string, string, string?, string?> Audit,
    bool    RequireAfterPending  = false,
    bool    PassDialogInfo       = false,
    string? SuccessLead          = null,
    string? SuccessNote          = null,
    string? NotApplicableMessage = null,
    string  NotApplicableKind    = "NotTested",
    string? NotApplicableTestedMessage = null,
    string? RefuseWhenZoneState  = null,
    string? RefusalKind          = null,
    string? RefusalMessage       = null,
    string? RequireAfterZoneState = null,
    string? StateNotConfirmedKind = null,
    Action<string, string, string, string, string?, string?, string?>? AuditFailure = null)
{
    /// <summary>The <c>cancel</c> verb — byte-identical to the pre-0098 handler's wording.</summary>
    public static StudioLifecycleVerb Cancel { get; } = new(
        Verb:             "cancel",
        ToolName:         "logix_cancel_pending_edits",
        Status:           "cancelled",
        GestureMenu:      "Logic -> Online Edits -> Cancel Accepted Program Edits",
        CapitalVerb:      "Cancel",
        Gerund:           "cancelling",
        PastCapital:      "Cancelled",
        Consequence:      "cancelling discards the controller's pending rung edits",
        RequireAfterNone: true,
        Audit:            AuditLogger.LogCancelPendingEdits,
        AuditFailure:     AuditLogger.LogLifecycleFailure,
        PassDialogInfo:   true,
        NotApplicableMessage:
            "Cancel Accepted is not applicable in Studio's current state: Studio showed no confirmation " +
            "dialog although the zone is not tested, so the tested state is not the cause. Look for an open " +
            "or late Studio dialog (see the screenshots), then retry logix_cancel_pending_edits",
        NotApplicableTestedMessage:
            "Cancel Accepted is not applicable in Studio's current state (it is disabled while the zone is tested). " +
            "Call logix_untest_pending_edits (the controller goes back to the original logic), then " +
            "logix_cancel_pending_edits",
        NotApplicableKind:   "CancelNotApplicable",
        RefuseWhenZoneState: "tested",
        RefusalKind:         "ZoneTested",
        RefusalMessage:
            "Cancel Accepted is disabled in Studio while the zone is tested. Call " +
            "logix_untest_pending_edits (the controller goes back to the original logic), then " +
            "logix_cancel_pending_edits.");

    /// <summary>
    /// The <c>accept</c> verb (<c>logix_accept_pending_edits</c>): drives
    /// Logic -> Online Edits -> Assemble Accepted Program Edits. Success requires the
    /// after-state to be <c>none</c> — never the gesture alone.
    /// </summary>
    public static StudioLifecycleVerb Accept { get; } = new(
        Verb:             "accept",
        ToolName:         "logix_accept_pending_edits",
        Status:           "accepted",
        GestureMenu:      "Logic -> Online Edits -> Assemble Accepted Program Edits",
        CapitalVerb:      "Accept",
        Gerund:           "accepting",
        PastCapital:      "Accepted",
        Consequence:      "accepting assembles the pending rung edits so they execute on a running controller",
        RequireAfterNone: true,
        Audit:            AuditLogger.LogAcceptPendingEdits,
        AuditFailure:     AuditLogger.LogLifecycleFailure,
        PassDialogInfo:   true,
        NotApplicableMessage:
            "Accept is not applicable in Studio's current state: Studio showed no confirmation dialog " +
            "although the zone has pending edits. Look for an open or late Studio dialog (see the screenshots), " +
            "then retry logix_accept_pending_edits",
        NotApplicableKind:   "AcceptNotApplicable");

    /// <summary>
    /// The <c>test</c> verb (<c>logix_test_pending_edits</c>): posts Studio's
    /// Test Accepted Program Edits command (32061), which makes the new logic execute on
    /// the running controller while the original is kept. Success means the dialog closed
    /// and the after-read shows <c>zoneState == "tested"</c> (program <c>TestEdits=true</c>).
    /// </summary>
    public static StudioLifecycleVerb Test { get; } = new(
        Verb:             "test",
        ToolName:         "logix_test_pending_edits",
        Status:           "test_confirmed",
        GestureMenu:      "Logic -> Online Edits -> Test Accepted Program Edits, posted command 32061",
        CapitalVerb:      "Test",
        Gerund:           "testing",
        PastCapital:      "Test confirmed",
        Consequence:      "testing makes the new logic execute on a running controller",
        RequireAfterNone: false,
        Audit:            AuditLogger.LogTestPendingEdits,
        AuditFailure:     AuditLogger.LogLifecycleFailure,
        RequireAfterPending: true,
        PassDialogInfo:   true,
        NotApplicableMessage:
            "Test is not applicable in Studio's current state: Studio showed no confirmation dialog " +
            "although the zone is not tested. Look for an open or late Studio dialog (see the screenshots), " +
            "then retry logix_test_pending_edits",
        NotApplicableKind:   "TestNotApplicable",
        RefuseWhenZoneState: "tested",
        RefusalKind:         "AlreadyTested",
        RefusalMessage:
            "the zone is already tested; read behaviour with logix_read, then " +
            "logix_assemble_pending_edits or logix_untest_pending_edits",
        RequireAfterZoneState: "tested",
        StateNotConfirmedKind: "TestStateNotConfirmed",
        SuccessLead:      "The Test command was posted and Studio's confirmation dialog closed for",
        SuccessNote:      CheckBehaviourNote + " Read the dialogWarnings: outputs in 'D' and 'R' " +
                          "zones stay in their last state in the Original View, and SFC routines are reset.");

    /// <summary>
    /// The <c>untest</c> verb (<c>logix_untest_pending_edits</c>): posts Studio's
    /// Untest Accepted Program Edits command (32062), which puts the original logic back.
    /// Same success contract as <see cref="Test"/>.
    /// </summary>
    public static StudioLifecycleVerb Untest { get; } = new(
        Verb:             "untest",
        ToolName:         "logix_untest_pending_edits",
        Status:           "untest_confirmed",
        GestureMenu:      "Logic -> Online Edits -> Untest Accepted Program Edits, posted command 32062",
        CapitalVerb:      "Untest",
        Gerund:           "untesting",
        PastCapital:      "Untest confirmed",
        Consequence:      "untesting reverts the tested logic on a running controller to the original",
        RequireAfterNone: false,
        Audit:            AuditLogger.LogUntestPendingEdits,
        AuditFailure:     AuditLogger.LogLifecycleFailure,
        RequireAfterPending: true,
        PassDialogInfo:   true,
        NotApplicableMessage:
            "Untest is not applicable: the zone is not tested, so there is nothing to untest",
        NotApplicableTestedMessage:
            "Untest is not applicable in Studio's current state: Studio showed no confirmation dialog " +
            "although the zone is tested. Look for an open or late Studio dialog (see the screenshots), " +
            "then retry logix_untest_pending_edits",
        NotApplicableKind:   "UntestNotApplicable",
        RefuseWhenZoneState: "untested",
        RefusalKind:         "NotTested",
        RefusalMessage:      "nothing to untest: the zone is not tested",
        RequireAfterZoneState: "untested",
        StateNotConfirmedKind: "UntestStateNotConfirmed",
        SuccessLead:      "The Untest command was posted and Studio's confirmation dialog closed for",
        SuccessNote:      CheckBehaviourNote + " Read the dialogWarnings about outputs in 'I' zones.");

    /// <summary>
    /// The <c>assemble</c> verb (<c>logix_assemble_pending_edits</c>): posts
    /// Studio's Assemble Accepted Program Edits command (32063), which is irreversible and
    /// is enabled only on a tested zone. Success requires the after-state to be <c>none</c>.
    /// </summary>
    public static StudioLifecycleVerb Assemble { get; } = new(
        Verb:             "assemble",
        ToolName:         "logix_assemble_pending_edits",
        Status:           "assembled",
        GestureMenu:      "Logic -> Online Edits -> Assemble Accepted Program Edits, posted command 32063",
        CapitalVerb:      "Assemble",
        Gerund:           "assembling",
        PastCapital:      "Assembled",
        Consequence:      "assembling is irreversible and finalizes the pending rung edits on a running controller",
        RequireAfterNone: true,
        Audit:            AuditLogger.LogAssemblePendingEdits,
        AuditFailure:     AuditLogger.LogLifecycleFailure,
        PassDialogInfo:   true,
        NotApplicableMessage: AssembleNeedsTestedMessage,
        NotApplicableTestedMessage:
            "Assemble is not applicable in Studio's current state: Studio showed no confirmation dialog " +
            "although the zone is tested. Look for an open or late Studio dialog (see the screenshots), " +
            "then retry logix_assemble_pending_edits",
        RefuseWhenZoneState:  "untested",
        RefusalKind:          "NotTested",
        RefusalMessage:       AssembleNeedsTestedMessage);

    private const string AssembleNeedsTestedMessage =
        "Assemble needs a tested zone: call logix_test_pending_edits first, or " +
        "logix_accept_pending_edits to finalize in one step";

    /// <summary>
    /// Said on every successful test / untest result (after "verified: program TestEdits=..."):
    /// one short suggestion to check behaviour.
    /// </summary>
    internal const string CheckBehaviourNote = "Check behaviour with logix_read.";
}

/// <summary>
/// Pure, verb-parameterised handler for the Studio-GUI pending-edit lifecycle
/// tools (<c>logix_cancel_pending_edits</c>, and <c>logix_accept_pending_edits</c>).
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
///
/// <para>
/// The flow, identical for every verb (see <see cref="StudioLifecycleVerb"/> for
/// what varies): input validation → Studio-installed gate → <c>confirm</c> gate →
/// read <c>logix_pending_edits</c> and refuse on <c>none</c> → run
/// <c>bench/studio-edits.ps1 -Verb &lt;verb&gt;</c> through the session-1 script
/// runner → re-read <c>logix_pending_edits</c> → audit → before/after result.
/// The flag gate (<c>LGX_ENABLE_STUDIO_AUTOMATION</c>) sits in the tool layer,
/// ahead of this handler.
/// </para>
///
/// <para>
/// Result and error payloads reuse <see cref="CancelPendingEditsResult"/> and
/// <see cref="CancelPendingEditsError"/> so their JSON shape is unchanged.
/// </para>
/// </summary>
public static class StudioLifecycleHandler
{
    /// <summary><c>kind</c> returned when no Logix Designer window is open (driver reason <c>studio_not_running</c>).</summary>
    public const string StudioNotRunningKind = StudioKinds.StudioNotRunningKind;

    /// <summary><c>kind</c> returned when Studio's window title shows it offline (driver reason <c>studio_offline</c>).</summary>
    public const string StudioOfflineKind = StudioKinds.StudioOfflineKind;

    /// <summary><c>kind</c> returned when Studio runs only in another Windows session (pre-launch reason <c>studio_not_in_this_session</c>).</summary>
    public const string StudioInOtherSessionKind = StudioKinds.StudioInOtherSessionKind;

    /// <summary><c>kind</c> returned when Studio is elevated and the driver is not (pre-launch reason <c>studio_elevated_driver_not</c>).</summary>
    public const string StudioElevatedKind = StudioKinds.StudioElevatedKind;

    /// <summary><c>kind</c> returned when no open Logix Designer window matches the session's controller and project (driver reason <c>studio_window_not_found</c>).</summary>
    public const string StudioWindowNotFoundKind = StudioKinds.StudioWindowNotFoundKind;

    /// <summary><c>kind</c> returned when more than one open Logix Designer window matches the session's controller and project (driver reason <c>studio_window_ambiguous</c>).</summary>
    public const string StudioWindowAmbiguousKind = StudioKinds.StudioWindowAmbiguousKind;

    /// <summary><c>kind</c> returned when a Studio dialog is already open, so nothing was posted (driver reason <c>studio_modal_open</c>).</summary>
    public const string StudioModalOpenKind = StudioKinds.StudioModalOpenKind;

    /// <summary>The hint <see cref="StudioModalOpenKind"/> carries.</summary>
    public const string StudioModalOpenHint = "Answer or close the open Studio dialog, then retry.";

    /// <summary>The message both Studio-state errors carry: lgx-mcp never launches Studio or takes it online.</summary>
    public const string StudioEngineerMessage = StudioKinds.StudioEngineerMessage;

    /// <summary>
    /// Maps a driver <c>reason</c> to the typed error kind for the Studio-state refusals,
    /// or <c>null</c> when the reason is not one of them.
    /// </summary>
    /// <param name="reason">The <c>reason</c> from the driver's RESULT_JSON.</param>
    /// <returns><see cref="StudioNotRunningKind"/>, <see cref="StudioOfflineKind"/> or <c>null</c>.</returns>
    internal static string? StudioStateKindOf(string? reason) => reason switch
    {
        "studio_not_running" => StudioNotRunningKind,
        "studio_offline"     => StudioOfflineKind,
        "studio_modal_open"  => StudioModalOpenKind,
        _                    => StudioPreLaunch.WindowKindOfReason(reason),
    };

    /// <summary>
    /// The driver's own refusal message for a window-targeting failure (it names the expected
    /// title), or a generic fallback when the RESULT_JSON carries none.
    /// </summary>
    private static string WindowRefusalMessage(Session1ScriptResult result)
    {
        if (result.ParsedResultJson is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
            root.TryGetProperty("detail", out var detail) &&
            detail.ValueKind == System.Text.Json.JsonValueKind.Object &&
            detail.TryGetProperty("message", out var msg) &&
            msg.ValueKind == System.Text.Json.JsonValueKind.String &&
            msg.GetString() is { Length: > 0 } text)
            return text.TrimEnd('.');
        return result.Reason;
    }

    /// <summary>The open dialog's title from a <c>studio_modal_open</c> RESULT_JSON (<c>detail.dialogTitle</c>), or empty.</summary>
    private static string ModalDialogTitle(Session1ScriptResult result) =>
        result.ParsedResultJson is { ValueKind: System.Text.Json.JsonValueKind.Object } root &&
        root.TryGetProperty("detail", out var detail) &&
        detail.ValueKind == System.Text.Json.JsonValueKind.Object &&
        detail.TryGetProperty("dialogTitle", out var title) &&
        title.ValueKind == System.Text.Json.JsonValueKind.String
            ? title.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Renders <c>programTestedEdits</c> as <c>true</c>, <c>false</c> or <c>null</c>.</summary>
    private static string FormatTestedEdits(bool? testedEdits) =>
        testedEdits is { } t ? (t ? "true" : "false") : "null";

    /// <summary>
    /// Picks the <c>command_not_applicable</c> wording from the observed zone state: the
    /// "disabled while the zone is tested" wording is used only when the zone is tested.
    /// </summary>
    internal static string NotApplicableText(StudioLifecycleVerb verb, RoutinePendingEditState before) =>
        string.Equals(before.ZoneState, "tested", StringComparison.Ordinal) &&
        verb.NotApplicableTestedMessage is { } tested
            ? tested
            : verb.NotApplicableMessage!;

    /// <summary>
    /// The extra cause every <c>NotApplicable</c> result names: Studio disables the Online Edits
    /// commands when no routine of the target program is open or selected.
    /// </summary>
    /// <param name="program">The target program name, substituted into the wording.</param>
    /// <returns>The wording, starting with <c>or Studio has no routine of program</c>.</returns>
    internal static string NoRoutineOpenWording(string program) =>
        $"or Studio has no routine of program '{program}' open; open one of its routines in Studio and retry";

    /// <summary>
    /// The second known cause of an empty-dialog <c>command_not_applicable</c>: the edits were already
    /// finalized or cancelled from Logix Designer, so the server's image is stale.
    /// </summary>
    internal const string AlreadyFinalizedWording =
        "or the edits were already finalized or cancelled from Logix Designer " +
        "(run logix_pending_edits, which reconciles the image)";

    /// <summary>
    /// The third known cause of an empty-dialog <c>command_not_applicable</c>: after a successful accept Logix
    /// Designer shows its Errors pane as an auto-hide flyout, which blocks the next confirmation dialog. The driver
    /// toggles the pane (View > Errors, WM_COMMAND 32027) and retries once before reporting; the wording names the
    /// manual fallback.
    /// </summary>
    internal const string SecondAcceptWording =
        "or after a successful accept Logix Designer shows its Errors pane as an auto-hide flyout, which blocks the " +
        "next accept's confirmation (the server toggles that pane via View > Errors and retries once; the pane is " +
        "then left docked under the ladder view, and its pin (Auto Hide) returns it to auto-hide)";

    /// <summary>
    /// The note a response carries when the driver left the Errors pane docked.
    /// </summary>
    internal const string PaneDockedNote =
        "Logix Designer's Errors pane was left docked under the ladder view; click its pin (Auto Hide) to return it to auto-hide.";

    /// <summary>
    /// The Errors pane layout to report: the driver's <c>errorsPaneLayout</c> only when the pane was toggled.
    /// </summary>
    /// <param name="r">The driver result.</param>
    /// <returns>The layout, or <c>null</c> when the pane was not toggled or the driver reported none.</returns>
    private static string? PaneLayoutOf(Session1ScriptResult r) => r.ErrorsPaneToggled ? r.ErrorsPaneLayout : null;

    /// <summary>
    /// The full <c>NotApplicable</c> hint: <see cref="NotApplicableText(StudioLifecycleVerb, RoutinePendingEditState)"/>
    /// followed by the three known causes in order (<see cref="NoRoutineOpenWording"/>,
    /// <see cref="AlreadyFinalizedWording"/>, <see cref="SecondAcceptWording"/>). Same wording for cancel, test,
    /// untest, assemble and accept.
    /// </summary>
    /// <param name="verb">The lifecycle verb that got <c>command_not_applicable</c>.</param>
    /// <param name="before">The pending-edit state read before the launch.</param>
    /// <param name="program">The target program name.</param>
    /// <returns>The hint text.</returns>
    internal static string NotApplicableHint(StudioLifecycleVerb verb, RoutinePendingEditState before, string program) =>
        NotApplicableText(verb, before) + ", " + NoRoutineOpenWording(program) + "; " +
        AlreadyFinalizedWording + "; " + SecondAcceptWording;

    /// <summary>
    /// Runs the shared Studio-lifecycle flow for <paramref name="verb"/>. Parameters
    /// other than <paramref name="verb"/> are documented on
    /// <see cref="CancelPendingEditsHandler.ExecuteAsync"/>. Never throws, except
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="verb">Which lifecycle verb to run (cancel | accept).</param>
    /// <param name="program">Program name containing the target routine.</param>
    /// <param name="routine">Routine name whose pending edits are targeted.</param>
    /// <param name="confirm">Must be <c>true</c> to proceed.</param>
    /// <param name="pendingEditsReader">Reads the routine's <c>logix_pending_edits</c> state.</param>
    /// <param name="session1Runner">Invokes the driver; called with <c>verb.Verb</c>.</param>
    /// <param name="isStudioInstalled">Optional Studio-installed probe override.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="planLaunch">Optional launch-plan override stamped on pre-launch refusals.</param>
    /// <param name="leftoverTagsProvider">Optional; cancel only. Names of tags the cancelled publish created that remain on the controller.</param>
    /// <param name="imageSession">
    /// Optional; accept, assemble and cancel only. The session whose controller image follows the result. The handler
    /// does not touch the image: the calling tool marks the program stale and refreshes the image from the
    /// controller export, so this is accepted for the call sites' sake only.
    /// </param>
    /// <param name="acceptForeign">
    /// Accept, assemble and test only. The live zone is compared with the <see cref="ZoneLedger"/>: a routine the ledger
    /// does not know was published by another session or by Studio, and the verb is refused with
    /// <see cref="ZoneLedger.ForeignPendingZoneKind"/> unless this is <c>true</c>, in which case it proceeds and the result
    /// carries <c>foreign: true</c>. Cancel is never refused on this ground.
    /// </param>
    /// <param name="ledgerSession">The controller session the <see cref="ZoneLedger"/> is keyed by; <c>null</c> for the default key.</param>
    /// <returns>A <see cref="CancelPendingEditsResult"/> or a <see cref="CancelPendingEditsError"/>.</returns>
    public static async Task<object> ExecuteAsync(
        StudioLifecycleVerb                                         verb,
        string                                                      program,
        string                                                      routine,
        bool                                                        confirm,
        Func<string, string, CancellationToken, Task<object>>       pendingEditsReader,
        Func<string, CancellationToken, Task<Session1ScriptResult>> session1Runner,
        Func<bool>?                                                 isStudioInstalled = null,
        CancellationToken                                           ct = default,
        Func<(string? LaunchMode, string? RunLevel)>?               planLaunch = null,
        Func<IReadOnlyList<string>>?                                leftoverTagsProvider = null,
        TagSessionService?                                          imageSession = null,
        bool                                                        acceptForeign = false,
        object?                                                     ledgerSession = null)
    {
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(pendingEditsReader);
        ArgumentNullException.ThrowIfNull(session1Runner);
        isStudioInstalled ??= CancelPendingEditsHandler.DefaultIsStudioInstalled;
        planLaunch ??= () => Session1ScriptRunner.PlanLaunch();

        var tool = verb.ToolName;

        // Every refusal that happens before the runner is invoked reports how the
        // runner WOULD launch (launchMode, plus runLevel in task mode) and
        // launched=false. PlanLaunch never throws; the guard is for injected plans.
        // Each one also writes its single audit line: a tool_refused event carrying the refusal kind.
        CancelPendingEditsError NotLaunched(
            string error, string auditKind, RoutinePendingEditState? before = null)
        {
            AuditOutcome(verb, "refused", auditKind, program, routine, null, before?.State, null);
            var (mode, level) = SafePlan(planLaunch);
            return new CancelPendingEditsError(
                error, Before: before, LaunchMode: mode, RunLevel: level, Launched: false);
        }

        // ── Input validation ────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(program))
            return NotLaunched(
                "program must not be empty. " + tool + " requires both " +
                "program and routine to identify a single routine's edit zone.",
                ErrorKinds.InvalidArguments);

        if (string.IsNullOrWhiteSpace(routine))
            return NotLaunched(
                "routine must not be empty. " + tool + " requires both " +
                "program and routine to identify a single routine's edit zone.",
                ErrorKinds.InvalidArguments);

        if (StudioPreLaunch.CheckNames(program, routine) is { } badName)
            return NotLaunched(badName, ErrorKinds.InvalidArguments);

        if (program.Contains('\'') || routine.Contains('\''))
            return NotLaunched(
                "program and routine must not contain a single-quote character — " +
                "it cannot be safely embedded in the SDK XPath used by logix_pending_edits.",
                ErrorKinds.InvalidArguments);

        // ── (1) Capability-honesty gate: refuse when Studio 5000 does not appear
        //     to be installed on this host. Checked before the confirm gate so the
        //     caller learns the tool is unavailable here regardless of confirm. ──
        if (!isStudioInstalled())
            return NotLaunched(
                $"{tool} is unavailable on this host: Studio 5000 " +
                $"Logix Designer was not found at '{CancelPendingEditsHandler.StudioInstallRoot}'. This tool drives " +
                $"the Studio 5000 GUI ({verb.GestureMenu}) " +
                "in the interactive desktop session via desktop automation — it has no SDK " +
                "equivalent and cannot function without a Studio 5000 install on the host " +
                "running this server.",
                HostWithoutStudioKind);

        // ── (2) Confirm gate — before any pending-edit read or session-1 call. ──
        if (!confirm)
            return NotLaunched(
                $"{tool} refused for '{program}'/'{routine}': {verb.Consequence} " +
                $"by driving Studio 5000's GUI ({verb.GestureMenu}) in the interactive " +
                "desktop session — this is desktop automation, not a reviewable SDK call. " +
                "Pass confirm=true to acknowledge this risk and proceed.",
                ConfirmRequiredKind);

        // ── (3) Read pending-edit state first; refuse when there is nothing to do ──
        var beforeResult = await pendingEditsReader(program, routine, ct).ConfigureAwait(false);

        if (beforeResult is PendingEditsError beforeError)
            // A read that failed because the SDK server faulted keeps its kind and hint (G45).
            return NotLaunched(
                $"Failed to read pending-edit state for '{program}'/'{routine}' before " +
                $"{verb.Gerund}: {beforeError.Error}",
                beforeError.Kind ?? PendingEditsReadFailedKind) with
            {
                Kind = beforeError.Kind == ErrorKinds.SdkServerFaulted ? beforeError.Kind : null,
                Hint = beforeError.Kind == ErrorKinds.SdkServerFaulted ? beforeError.Hint : null,
            };

        var beforePending = (PendingEditsResult)beforeResult;
        var beforeRoutine = beforePending.Routines.FirstOrDefault(r =>
            string.Equals(r.Program, program, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Routine, routine, StringComparison.OrdinalIgnoreCase));

        if (beforeRoutine is null)
            return NotLaunched(
                $"Routine '{routine}' in program '{program}' was not found while reading " +
                "pending-edit state. Check the program and routine names, and that the " +
                "routine's Type is RLL.",
                RoutineNotFoundKind);

        // Studio's cancel acts on the whole program's edit zone, so a routine with no
        // pending edits of its own does not make a cancel pointless while another routine of the
        // same program is pending. Check the program before refusing (cancel only).
        // N11: the program-scope read is kept (cancel only) so the result can say, per routine, what was cancelled.
        IReadOnlyList<RoutinePendingEditState>? programBefore = null;
        if (verb.Verb == "cancel")
            programBefore = await ReadProgramRoutinesAsync(pendingEditsReader, program, ct).ConfigureAwait(false);

        if (beforeRoutine.State == "none" && verb.Verb == "cancel" &&
            programBefore is not null && programBefore.Any(r => r.State != "none"))
        {
            // fall through: the program has pending edits, the cancel is allowed.
        }
        else if (beforeRoutine.State == "none")
            return NotLaunched(
                $"{tool} refused for '{program}'/'{routine}': " +
                "logix_pending_edits reports state='none' — there is nothing pending to " +
                $"{verb.Verb}.",
                ErrorKinds.NothingPending,
                before: beforeRoutine) with { Kind = ErrorKinds.NothingPending };

        // ── (3a) State precondition: the zone's tested state is decided from the
        //     program export BEFORE any Studio command is sent. A refusal reports
        //     zoneState, programTestedEdits and the pending rungs through Before. ──
        if (verb.RefuseWhenZoneState is not null &&
            string.Equals(beforeRoutine.ZoneState, verb.RefuseWhenZoneState, StringComparison.Ordinal))
        {
            var refused = NotLaunched(
                $"{verb.RefusalMessage} ({tool} refused for '{program}'/'{routine}': " +
                $"zoneState='{beforeRoutine.ZoneState}', programTestedEdits=" +
                $"{FormatTestedEdits(beforeRoutine.ProgramTestedEdits)}; " +
                "no Studio command was sent.)",
                verb.RefusalKind ?? ErrorKinds.InvalidArguments,
                before: beforeRoutine);
            return refused with { Kind = verb.RefusalKind };
        }

        // ── (3b) Foreign-zone gate: the live zone is the program's pending routines; any routine the
        //     ZoneLedger holds no publish for was published by another session or by Studio. accept / assemble / test
        //     refuse it before any Studio command unless acceptForeign; cancel is never refused, it only reports it. ──
        var foreign = false;
        var ledger = ZoneLedger.For(ledgerSession);
        if (verb.Verb is "accept" or "assemble" or "test" or "cancel")
        {
            // The before-read names the target routine; when the ledger knows all of what it shows, read the whole
            // program too (the routine-scoped read cannot see a sibling routine of the same zone).
            var pendingRoutines = beforePending.Routines
                .Where(r => r.State != "none" && string.Equals(r.Program, program, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Routine)
                .ToList();
            if (beforeRoutine.State != "none") pendingRoutines.Add(beforeRoutine.Routine);
            var unknown = ledger.Unknown(program, pendingRoutines.Distinct(StringComparer.OrdinalIgnoreCase));
            if (unknown.Count == 0)
            {
                var liveZone = programBefore ?? await ReadProgramRoutinesAsync(pendingEditsReader, program, ct).ConfigureAwait(false);
                if (liveZone is not null)
                {
                    pendingRoutines.AddRange(liveZone.Where(r => r.State != "none").Select(r => r.Routine));
                    unknown = ledger.Unknown(program, pendingRoutines.Distinct(StringComparer.OrdinalIgnoreCase));
                }
            }
            foreign = unknown.Count > 0;
            if (foreign && verb.Verb != "cancel" && !acceptForeign)
            {
                var refusal = NotLaunched(
                    $"{tool} refused for '{program}'/'{routine}': the pending zone in program '{program}' " +
                    $"(routines {string.Join(", ", unknown.Select(u => $"'{u}'"))}) was not published by this server. " +
                    "No Studio command was sent.",
                    ZoneLedger.ForeignPendingZoneKind,
                    before: beforeRoutine);
                return refusal with
                {
                    Kind = ZoneLedger.ForeignPendingZoneKind,
                    Hint = ForeignPendingZoneHint,
                    Program = program,
                    Routines = unknown,
                    PublishedByThisServer = false,
                };
            }
        }

        // ── (4) Invoke bench/studio-edits.ps1 -Verb <verb> through the runner ──
        Session1ScriptResult scriptResult;
        try
        {
            scriptResult = await session1Runner(verb.Verb, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var (planMode, planLevel) = SafePlan(planLaunch);
            AuditOutcome(verb, "error", ScriptInvocationFailedKind, program, routine, ex.Message, beforeRoutine.State, null);
            return new CancelPendingEditsError(
                $"session-1 script invocation failed for '{program}'/'{routine}': " +
                $"{ex.Message}",
                Before: beforeRoutine,
                LaunchMode: planMode,
                RunLevel: planLevel,
                Launched: true);
        }

        // The driver's RESULT_JSON is surfaced verbatim on every response from
        // this point on — success or failure — so a driver defect is never
        // silently swallowed and misread by the caller as a bench fact.
        var driver = new CancelPendingEditsDriverInfo(
            ResultJsonLine: scriptResult.RawResultJsonLine,
            Parsed:         scriptResult.ParsedResultJson,
            Malformed:      scriptResult.Malformed);

        // Error carrying the runner's full outcome; used for every post-launch failure.
        // It also writes the call's single audit line: outcome "error" with the response kind (or auditKind
        // when the response carries none) and the driver reason. afterState is the re-read state, once there is one.
        CancelPendingEditsError Failed(
            string error, string? kind = null, string? hint = null,
            string? auditKind = null, string? reason = null, string? afterState = null)
        {
            AuditOutcome(
                verb, "error", kind ?? auditKind ?? UnclassifiedKind, program, routine, reason,
                beforeRoutine.State, afterState, scriptResult.ErrorsPaneToggled, PaneLayoutOf(scriptResult));
            return Build(error, kind, hint);
        }

        CancelPendingEditsError Build(string error, string? kind, string? hint) => new(
            error,
            Before:                beforeRoutine,
            Screenshots:           scriptResult.Screenshots,
            Driver:                driver,
            TaskName:              scriptResult.TaskName,
            LastTaskResultHex:     scriptResult.LastTaskResultHex,
            LastTaskResultDecimal: scriptResult.LastTaskResultDecimal,
            LaunchMode:            scriptResult.LaunchMode,
            RunLevel:              scriptResult.RunLevel,
            Launched:              true,
            GestureTier:           scriptResult.GestureTier,
            DialogText:            verb.PassDialogInfo ? scriptResult.DialogText : null,
            DialogWarnings:        verb.PassDialogInfo ? DialogWarningsOf(scriptResult) : null,
            Kind:                  kind,
            Hint:                  hint,
            ErrorsPaneToggled:     scriptResult.ErrorsPaneToggled,
            ErrorsPaneLayout:      PaneLayoutOf(scriptResult));

        // ── (4a) A RESULT_JSON line that could not be parsed is a driver defect —
        //     refuse regardless of what Ok says. ──────────────────────────────
        if (scriptResult.Malformed)
            return Failed(
                $"bench/studio-edits.ps1 -Verb {verb.Verb} produced a RESULT_JSON line for " +
                $"'{program}'/'{routine}' that could not be parsed: {scriptResult.Reason}",
                auditKind: DriverResultMalformedKind, reason: scriptResult.Reason);

        // ── (4b) The runner refused before starting the driver: say so -- the
        //     script never ran. (No LastTaskResult fields; Launched=false.) ──
        if (!scriptResult.Ok && !scriptResult.Launched)
        {
            var notLaunchedKind = scriptResult.Kind ?? Session1ScriptRunner.PreLaunchKindOf(scriptResult.Reason);
            AuditOutcome(
                verb, "refused", notLaunchedKind ?? NotLaunchedKind, program, routine,
                scriptResult.Reason, beforeRoutine.State, null);
            return new CancelPendingEditsError(
                $"Not launched: bench/studio-edits.ps1 -Verb {verb.Verb} was not started for " +
                $"'{program}'/'{routine}' because the pre-launch check refused: {scriptResult.Reason}",
                Before:                beforeRoutine,
                Screenshots:           scriptResult.Screenshots,
                Driver:                driver,
                TaskName:              scriptResult.TaskName,
                LaunchMode:            scriptResult.LaunchMode,
                RunLevel:              scriptResult.RunLevel,
                Launched:              false,
                GestureTier:           scriptResult.GestureTier,
                DialogText:            verb.PassDialogInfo ? scriptResult.DialogText : null,
                DialogWarnings:        verb.PassDialogInfo ? DialogWarningsOf(scriptResult) : null,
                Kind:                  notLaunchedKind);
        }

        // ── (4c0) A Studio dialog is already open: the driver posted nothing. ──
        if (!scriptResult.Ok &&
            string.Equals(scriptResult.Reason, "studio_modal_open", StringComparison.Ordinal))
            return Failed(
                $"A Studio dialog is already open for '{program}'/'{routine}'; nothing was posted " +
                $"(title: '{ModalDialogTitle(scriptResult)}', text: '{scriptResult.DialogText}'). " +
                StudioModalOpenHint +
                $" (driver reason: studio_modal_open; the zone is unchanged, state='{beforeRoutine.State}').",
                kind: StudioModalOpenKind,
                hint: StudioModalOpenHint,
                reason: scriptResult.Reason);

        // ── (4c) Studio is not running / is offline: typed errors with the before-state.
        //     The driver never launches Studio or goes online through it. ──
        if (!scriptResult.Ok &&
            StudioStateKindOf(scriptResult.Reason) is { } studioKind)
            return Failed(
                (studioKind switch
                {
                    StudioNotRunningKind => $"Studio is not running for '{program}'/'{routine}': ",
                    StudioWindowNotFoundKind or StudioWindowAmbiguousKind =>
                        $"{WindowRefusalMessage(scriptResult)} for '{program}'/'{routine}'; nothing was clicked: ",
                    _ => $"Studio is offline for '{program}'/'{routine}': ",
                }) +
                StudioEngineerMessage +
                $" (driver reason: {scriptResult.Reason}; the zone is unchanged, state='{beforeRoutine.State}').",
                kind: studioKind,
                reason: scriptResult.Reason);

        // G10: Studio had nothing to confirm because the controller holds nothing to cancel (the before-read was
        // stale or came from the session image). Say NothingPending, with no Studio-dialog hint.
        if (!scriptResult.Ok && verb.Verb == "cancel" &&
            string.Equals(scriptResult.Reason, "command_not_applicable", StringComparison.Ordinal) &&
            await NothingPendingOnControllerAsync(pendingEditsReader, program, routine, ct).ConfigureAwait(false))
            return Failed(
                $"{tool} found nothing pending for '{program}'/'{routine}': a fresh logix_pending_edits read " +
                $"reports state='none' for the program, so there is nothing pending to {verb.Verb}. " +
                "The Studio command changed nothing.",
                kind: ErrorKinds.NothingPending,
                reason: scriptResult.Reason);

        if (!scriptResult.Ok &&
            verb.NotApplicableMessage is not null &&
            string.Equals(scriptResult.Reason, "command_not_applicable", StringComparison.Ordinal))
            return Failed(
                NotApplicableHint(verb, beforeRoutine, program) + $" (driver reason: command_not_applicable for " +
                $"'{program}'/'{routine}'; observed before: zoneState='{beforeRoutine.ZoneState}', " +
                $"programTestedEdits={FormatTestedEdits(beforeRoutine.ProgramTestedEdits)}; " +
                $"the zone is unchanged, state='{beforeRoutine.State}').",
                kind: verb.NotApplicableKind,
                hint: NotApplicableHint(verb, beforeRoutine, program),
                reason: scriptResult.Reason);

        if (!scriptResult.Ok)
            return Failed(
                $"bench/studio-edits.ps1 -Verb {verb.Verb} reported failure for " +
                $"'{program}'/'{routine}': {scriptResult.Reason}",
                auditKind: DriverFailedKind, reason: scriptResult.Reason);

        // ── (5) Re-read pending-edit state and return before/after ──────────
        // Studio's finalize / cancel / assemble can still be landing on the controller
        // when the driver returns (the dialog closes before the controller-side work ends),
        // so a single immediate read can be stale. For verbs that expect the zone to be
        // gone, re-read on a short interval until it is, or the settle budget runs out
        // (bench S20-p102 / S14-p102, 2026-10-01: oracle all N while the one read said 'I').
        var afterResult = await pendingEditsReader(program, routine, ct).ConfigureAwait(false);
        if (verb.RequireAfterNone)
        {
            for (var attempt = 1;
                 attempt < AfterSettleAttempts &&
                 afterResult is PendingEditsResult settling &&
                 RoutineStateOf(settling, program, routine) is { } st && st != "none";
                 attempt++)
            {
                await Task.Delay(AfterSettleInterval, ct).ConfigureAwait(false);
                afterResult = await pendingEditsReader(program, routine, ct).ConfigureAwait(false);
            }
        }

        if (verb.RequireAfterZoneState is not null)
        {
            // Wait for the program's TestEdits to flip, unless the zone is gone
            // (a vanished zone is reported separately below).
            for (var attempt = 1;
                 attempt < AfterSettleAttempts &&
                 afterResult is PendingEditsResult settling &&
                 RoutineOf(settling, program, routine) is { } st &&
                 st.State != "none" &&
                 !string.Equals(st.ZoneState, verb.RequireAfterZoneState, StringComparison.Ordinal);
                 attempt++)
            {
                await Task.Delay(AfterSettleInterval, ct).ConfigureAwait(false);
                afterResult = await pendingEditsReader(program, routine, ct).ConfigureAwait(false);
            }
        }

        // N11: a cancel acts on the whole program's edit zone, so the target routine alone says nothing when
        // the edits cancelled were another routine's. Re-read the program (fresh, never reusing the before
        // read) and settle until no routine of it is pending.
        IReadOnlyList<RoutinePendingEditState>? programAfter = null;
        if (verb.Verb == "cancel" && afterResult is PendingEditsResult)
        {
            programAfter = await ReadProgramRoutinesAsync(pendingEditsReader, program, ct).ConfigureAwait(false);
            for (var attempt = 1;
                 attempt < AfterSettleAttempts && programAfter is not null && programAfter.Any(r => r.State != "none");
                 attempt++)
            {
                await Task.Delay(AfterSettleInterval, ct).ConfigureAwait(false);
                programAfter = await ReadProgramRoutinesAsync(pendingEditsReader, program, ct).ConfigureAwait(false);
            }
        }

        if (afterResult is PendingEditsError afterError)
            return Failed(
                $"{verb.CapitalVerb} gesture for '{program}'/'{routine}' was sent, but reading " +
                $"pending-edit state afterward failed: {afterError.Error}",
                kind: afterError.Kind == ErrorKinds.SdkServerFaulted ? afterError.Kind : null,
                hint: afterError.Kind == ErrorKinds.SdkServerFaulted ? afterError.Hint : null,
                auditKind: afterError.Kind ?? PendingEditsReadFailedKind);

        var afterPending = (PendingEditsResult)afterResult;
        var afterRoutine = afterPending.Routines.FirstOrDefault(r =>
            string.Equals(r.Program, program, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Routine, routine, StringComparison.OrdinalIgnoreCase));

        if (afterRoutine is null)
            return Failed(
                $"{verb.CapitalVerb} gesture for '{program}'/'{routine}' was sent, but the routine could " +
                "not be found while re-reading pending-edit state afterward.",
                auditKind: RoutineNotFoundKind);

        // Never claim success on the gesture alone: when the verb requires the
        // zone to be gone, a still-pending after-state is an error naming both states.
        if (verb.RequireAfterNone && afterRoutine.State != "none")
            return Failed(
                $"{verb.CapitalVerb} gesture for '{program}'/'{routine}' reported success, but " +
                $"logix_pending_edits afterward still reports state='{afterRoutine.State}' " +
                $"(was state='{beforeRoutine.State}') — the edits were not {verb.Status}.",
                auditKind: StillPendingKind, afterState: afterRoutine.State);

        if (verb.Verb == "cancel" && programAfter is not null && programAfter.Any(r => r.State != "none"))
        {
            var left = string.Join(", ", programAfter.Where(r => r.State != "none")
                .Select(r => $"'{r.Routine}' (state='{r.State}')"));
            return Failed(
                $"{verb.CapitalVerb} gesture for '{program}'/'{routine}' reported success, but " +
                $"logix_pending_edits afterward still reports pending edits in program '{program}': {left} " +
                "— the edits were not cancelled.",
                auditKind: StillPendingKind, afterState: afterRoutine.State);
        }

        // test / untest close the dialog and leave the zone in place: a vanished zone
        // means something else happened, so it is not reported as a test/untest success.
        if (verb.RequireAfterPending && afterRoutine.State == "none")
            return Failed(
                $"{verb.CapitalVerb} gesture for '{program}'/'{routine}' reported success, but " +
                $"logix_pending_edits afterward reports state='none' " +
                $"(was state='{beforeRoutine.State}') — {verb.Verb} leaves the edit zone in place, " +
                "so the zone was resolved by something else. Verify with logix_pending_edits and logix_read.",
                auditKind: ZoneResolvedElsewhereKind, afterState: afterRoutine.State);

        // Success is the program's TestEdits state, never the gesture alone.
        if (verb.RequireAfterZoneState is not null &&
            !string.Equals(afterRoutine.ZoneState, verb.RequireAfterZoneState, StringComparison.Ordinal))
            return Failed(
                $"{verb.CapitalVerb} gesture for '{program}'/'{routine}' reported success, but the program " +
                $"export afterward shows zoneState='{afterRoutine.ZoneState}' " +
                $"(programTestedEdits={TestEditsText(afterRoutine.ProgramTestedEdits)}), expected " +
                $"'{verb.RequireAfterZoneState}' (was zoneState='{beforeRoutine.ZoneState}', " +
                $"programTestedEdits={TestEditsText(beforeRoutine.ProgramTestedEdits)}, " +
                $"state='{beforeRoutine.State}'; now state='{afterRoutine.State}').",
                kind: verb.StateNotConfirmedKind,
                auditKind: WrongAfterStateKind, afterState: afterRoutine.State);

        var cancelled = verb.Verb == "cancel"
            ? BuildCancelledRoutines(beforeRoutine, afterRoutine, programBefore, programAfter)
            : null;

        // Cancel discards rungs but never removes tags the publish created: list what is left.
        IReadOnlyList<string>? leftoverTags = null;
        if (verb.Verb == "cancel" && leftoverTagsProvider is not null)
        {
            var names = leftoverTagsProvider()
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (names.Count > 0) leftoverTags = names;
        }

        // The session image is not followed here: the calling tool marks the program stale and refreshes it.

        // The zone is resolved: accept, assemble and cancel forget the program's ledger entry.
        if (verb.Verb is "accept" or "assemble" or "cancel")
            ledger.ClearProgram(program);

        // ── (6) Audit ─────────────────────────────────────────────────────
        AuditOutcome(
            verb, verb.Status, null, program, routine, null, beforeRoutine.State, afterRoutine.State,
            scriptResult.ErrorsPaneToggled, PaneLayoutOf(scriptResult));

        return new CancelPendingEditsResult(
            Status:      verb.Status,
            Program:     program,
            Routine:     routine,
            Message:
                $"{verb.SuccessLead ?? $"{verb.PastCapital} pending edits for"} '{program}'/'{routine}' by driving the Studio " +
                $"5000 GUI ({verb.GestureMenu}) in the " +
                "interactive desktop session via the session-1 script runner " +
                (cancelled is null
                    ? $"(was state='{beforeRoutine.State}', now state='{afterRoutine.State}')."
                    : DescribeCancelled(routine, beforeRoutine, afterRoutine, cancelled)) +
                (verb.RequireAfterZoneState is null
                    ? ""
                    : $" verified: program TestEdits={TestEditsText(afterRoutine.ProgramTestedEdits)}.") +
                (verb.SuccessNote is null ? "" : " " + verb.SuccessNote),
            Before:                beforeRoutine,
            After:                 afterRoutine,
            Screenshots:           scriptResult.Screenshots,
            Driver:                driver,
            TaskName:              scriptResult.TaskName,
            LastTaskResultHex:     scriptResult.LastTaskResultHex,
            LastTaskResultDecimal: scriptResult.LastTaskResultDecimal,
            LaunchMode:            scriptResult.LaunchMode,
            RunLevel:              scriptResult.RunLevel,
            Launched:              true,
            GestureTier:           scriptResult.GestureTier,
            DialogText:            verb.PassDialogInfo ? scriptResult.DialogText : null,
            DialogWarnings:        verb.PassDialogInfo ? DialogWarningsOf(scriptResult) : null,
            Cancelled:             cancelled,
            LeftoverTags:          leftoverTags,
            LeftoverTagsNote:      leftoverTags is null ? null : LeftoverTagsNoteText,
            ErrorsPaneToggled:     scriptResult.ErrorsPaneToggled,
            ErrorsPaneLayout:      PaneLayoutOf(scriptResult),
            Note:                  string.Equals(PaneLayoutOf(scriptResult), "docked", StringComparison.Ordinal) ? PaneDockedNote : null,
            Foreign:               foreign);
    }

    /// <summary><c>kind</c> audited when Studio 5000 is not installed on the host.</summary>
    internal const string HostWithoutStudioKind = "StudioNotInstalled";

    /// <summary>The <c>hint</c> of the <see cref="ZoneLedger.ForeignPendingZoneKind"/> refusal (lives here: it names Studio, the no-SDK build carries no Studio text).</summary>
    internal const string ForeignPendingZoneHint =
        "another session or Studio published these edits; review them in logix_pending_edits and pass " +
        "acceptForeign: true to finalize them anyway, or cancel them";

    /// <summary><c>kind</c> audited when the <c>confirm</c> gate refuses the call.</summary>
    internal const string ConfirmRequiredKind = "ConfirmRequired";
    /// <summary><c>kind</c> audited when reading <c>logix_pending_edits</c> failed.</summary>
    internal const string PendingEditsReadFailedKind = "PendingEditsReadFailed";
    /// <summary><c>kind</c> audited when the target routine is not in the pending-edit read.</summary>
    internal const string RoutineNotFoundKind = "RoutineNotFound";
    /// <summary><c>kind</c> audited when the session-1 runner threw.</summary>
    internal const string ScriptInvocationFailedKind = "ScriptInvocationFailed";
    /// <summary><c>kind</c> audited when the driver's RESULT_JSON line could not be parsed.</summary>
    internal const string DriverResultMalformedKind = "DriverResultMalformed";
    /// <summary><c>kind</c> audited when the runner refused before starting the driver and named no kind.</summary>
    internal const string NotLaunchedKind = "NotLaunched";
    /// <summary><c>kind</c> audited for a driver failure that has no typed response kind.</summary>
    internal const string DriverFailedKind = "DriverFailed";
    /// <summary><c>kind</c> audited when the zone is still pending after the settle attempts.</summary>
    internal const string StillPendingKind = "StillPending";
    /// <summary><c>kind</c> audited when a test / untest gesture left no pending zone behind.</summary>
    internal const string ZoneResolvedElsewhereKind = "ZoneResolvedElsewhere";
    /// <summary><c>kind</c> audited when a failure carries no kind at all.</summary>
    internal const string UnclassifiedKind = "Unclassified";
    private const string WrongAfterStateKind = "StateNotConfirmed";

    /// <summary>
    /// Writes the one audit line of a lifecycle call; every return site of <see cref="ExecuteAsync"/> goes through here.
    /// <c>outcome="refused"</c> is a pre-launch refusal (<see cref="AuditLogger.LogToolRefused"/>, kind required);
    /// <c>outcome="error"</c> is a post-launch failure (<see cref="StudioLifecycleVerb.AuditFailure"/>, with the driver
    /// <paramref name="reason"/>); any other outcome is the verb's success status, written through
    /// <see cref="StudioLifecycleVerb.Audit"/> exactly as before. A failure line never carries a success outcome.
    /// A verb with no <see cref="StudioLifecycleVerb.AuditFailure"/> writes no refusal or failure line.
    /// </summary>
    /// <param name="verb">The lifecycle verb being run.</param>
    /// <param name="outcome"><c>"refused"</c>, <c>"error"</c>, or the verb's success status.</param>
    /// <param name="kind">Refusal / failure kind; ignored for a success.</param>
    /// <param name="program">Program the call targeted.</param>
    /// <param name="routine">Routine the call targeted.</param>
    /// <param name="reason">The driver's reason, when there is one.</param>
    /// <param name="beforeState">Pending-edit state read before the gesture, if any.</param>
    /// <param name="afterState">Pending-edit state re-read after the gesture, if any.</param>
    /// <param name="errorsPaneToggled">
    /// The driver toggled the Errors pane. A post-launch line of a shipped verb (one with an
    /// <see cref="StudioLifecycleVerb.AuditFailure"/>) then also carries <c>errors_pane_toggled:true</c>; the field is
    /// omitted when false.
    /// </param>
    /// <param name="errorsPaneLayout">
    /// The layout the driver reported after the toggle; written as <c>errors_pane_layout</c> only when non-null.
    /// </param>
    private static void AuditOutcome(
        StudioLifecycleVerb verb, string outcome, string? kind, string program, string routine,
        string? reason, string? beforeState, string? afterState, bool errorsPaneToggled = false,
        string? errorsPaneLayout = null)
    {
        if (errorsPaneToggled && outcome != "refused" && verb.AuditFailure is not null)
        {
            AuditLogger.LogLifecycleWithPaneToggle(
                verb.Verb, program, routine, outcome,
                outcome == "error" ? kind ?? UnclassifiedKind : null, reason, beforeState, afterState,
                errorsPaneLayout);
            return;
        }

        switch (outcome)
        {
            case "refused":
                if (verb.AuditFailure is not null)
                    AuditLogger.LogToolRefused(verb.ToolName, kind ?? UnclassifiedKind, program: program, routine: routine);
                break;
            case "error":
                verb.AuditFailure?.Invoke(
                    verb.Verb, program, routine, kind ?? UnclassifiedKind, reason, beforeState, afterState);
                break;
            default:
                verb.Audit(program, routine, outcome, beforeState, afterState);
                break;
        }
    }

    /// <summary>The note returned with <c>leftoverTags</c> on a successful cancel.</summary>
    public const string LeftoverTagsNoteText =
        "tags are not removed by cancel; delete them in Studio if unwanted";

    /// <summary>
    /// <c>true</c> when a fresh pending-edits read shows no pending edit in the target routine or anywhere in its
    /// program; <c>false</c> when something is pending or either read failed.
    /// </summary>
    private static async Task<bool> NothingPendingOnControllerAsync(
        Func<string, string, CancellationToken, Task<object>> pendingEditsReader,
        string program, string routine, CancellationToken ct)
    {
        try
        {
            if (await pendingEditsReader(program, routine, ct).ConfigureAwait(false) is not PendingEditsResult own ||
                own.Routines.Any(r => r.State != "none"))
                return false;
            var all = await ReadProgramRoutinesAsync(pendingEditsReader, program, ct).ConfigureAwait(false);
            return all is not null && all.All(r => r.State == "none");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads every ladder routine of <paramref name="program"/> (program scope, empty routine) with a fresh
    /// pending-edits read; <c>null</c> when the read failed.
    /// </summary>
    private static async Task<IReadOnlyList<RoutinePendingEditState>?> ReadProgramRoutinesAsync(
        Func<string, string, CancellationToken, Task<object>> pendingEditsReader,
        string program, CancellationToken ct)
    {
        var result = await pendingEditsReader(program, string.Empty, ct).ConfigureAwait(false);
        return result is PendingEditsResult ok
            ? ok.Routines.Where(r => string.Equals(r.Program, program, StringComparison.OrdinalIgnoreCase)).ToList()
            : null;
    }

    /// <summary>
    /// Builds the per-routine cancel report: every routine of the program that held pending edits before
    /// the cancel (the target routine first), with its state before and after. The target routine is
    /// always included, even when it held nothing itself.
    /// </summary>
    internal static IReadOnlyList<CancelledRoutine> BuildCancelledRoutines(
        RoutinePendingEditState target, RoutinePendingEditState targetAfter,
        IReadOnlyList<RoutinePendingEditState>? programBefore,
        IReadOnlyList<RoutinePendingEditState>? programAfter)
    {
        var list = new List<CancelledRoutine>
        {
            new(target.Routine, target.State, targetAfter.State, target.PendingRungs.Count),
        };
        foreach (var r in programBefore ?? Array.Empty<RoutinePendingEditState>())
        {
            if (r.State == "none" || string.Equals(r.Routine, target.Routine, StringComparison.OrdinalIgnoreCase))
                continue;
            var after = programAfter?.FirstOrDefault(a =>
                string.Equals(a.Routine, r.Routine, StringComparison.OrdinalIgnoreCase))?.State ?? "none";
            list.Add(new CancelledRoutine(r.Routine, r.State, after, r.PendingRungs.Count));
        }
        return list;
    }

    /// <summary>
    /// The cancel success wording: states, per routine, what was cancelled. When the target routine itself
    /// held nothing, says so instead of reporting a misleading <c>state 'none' to 'none'</c>.
    /// </summary>
    internal static string DescribeCancelled(
        string routine, RoutinePendingEditState before, RoutinePendingEditState after,
        IReadOnlyList<CancelledRoutine> cancelled)
    {
        var others = cancelled.Skip(1).ToList();
        var othersText = string.Join("; ", others.Select(c =>
            $"'{c.Routine}' (was state='{c.BeforeState}', {PluralRungs(c.PendingRungs)}, now state='{c.AfterState}')"));

        if (before.State == "none")
            return $"(routine '{routine}' itself had no pending edits; Studio's cancel acts on the whole program " +
                   (others.Count > 0 ? $"and cancelled the edits of: {othersText})." : "and found nothing else pending).");

        return $"(was state='{before.State}', now state='{after.State}')." +
               (others.Count > 0 ? $" Also cancelled in the same program: {othersText}." : string.Empty);
    }

    private static string PluralRungs(int n) => n == 1 ? "1 pending rung" : $"{n} pending rungs";

    /// <summary>
    /// How many times the after-state is read, at most, for verbs that require it to be
    /// <c>none</c> (the first read plus re-reads). Settable so tests can run without waiting.
    /// </summary>
    public static int AfterSettleAttempts { get; set; } = 8;

    /// <summary>Delay between after-state re-reads (see <see cref="AfterSettleAttempts"/>).</summary>
    public static TimeSpan AfterSettleInterval { get; set; } = TimeSpan.FromSeconds(1.5);

    private static RoutinePendingEditState? RoutineOf(PendingEditsResult result, string program, string routine) =>
        result.Routines.FirstOrDefault(r =>
            string.Equals(r.Program, program, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Routine, routine, StringComparison.OrdinalIgnoreCase));

    private static string TestEditsText(bool? value) => value is { } v ? (v ? "true" : "false") : "null";

    private static string? RoutineStateOf(PendingEditsResult result, string program, string routine) =>
        result.Routines.FirstOrDefault(r =>
            string.Equals(r.Program, program, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Routine, routine, StringComparison.OrdinalIgnoreCase))?.State;

    /// <summary>
    /// The driver's <c>dialogWarnings</c>, taken from the parsed runner result and, when
    /// that is absent, from the raw RESULT_JSON payload; an empty list when it reported none
    /// (so test / untest / assemble always carry the field).
    /// </summary>
    private static IReadOnlyList<string> DialogWarningsOf(Session1ScriptResult result)
    {
        if (result.DialogWarnings is not null)
            return result.DialogWarnings;

        if (result.ParsedResultJson is { ValueKind: JsonValueKind.Object } root &&
            root.TryGetProperty("dialogWarnings", out var prop) &&
            prop.ValueKind == JsonValueKind.Array)
            return prop.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(e.GetString()))
                .Select(e => e.GetString()!)
                .ToList();

        return Array.Empty<string>();
    }

    private static (string? LaunchMode, string? RunLevel) SafePlan(
        Func<(string? LaunchMode, string? RunLevel)> planLaunch)
    {
        try { return planLaunch(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (null, null); }
    }
}
#endif
