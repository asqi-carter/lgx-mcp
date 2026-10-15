#if LOGIX_SDK
// ── CancelPendingEditsHandler — Studio-GUI-driven cancel of a routine's pending
//    edit zone ────────────────────────────────────────
using System.Text.Json;
using System.Text.Json.Serialization;

// Alias System.Threading.Tasks.Task to avoid ambiguity with L5Sharp.Core.Task
// when both namespaces are in scope.
using Task = System.Threading.Tasks.Task;

// ── Result / error types ──────────────────────────────────────────────────────

/// <summary>
/// Outcome of one <c>bench/studio-edits.ps1</c> invocation, as reported by its
/// single <c>RESULT_JSON:</c> stdout line (see
/// <see cref="Session1ScriptRunner.ParseResultJson"/>).
/// </summary>
/// <param name="Ok">
/// <c>true</c> when the script reported success for the requested verb.
/// </param>
/// <param name="Reason">
/// Short machine-readable reason code the script emitted (e.g.
/// <c>"cancel_gesture_sent"</c>, <c>"session_0_refused"</c>,
/// <c>"cancel_confirmation_dialog_not_found"</c>).
/// </param>
/// <param name="Screenshots">
/// Every screenshot path the script collected during the run, in the order
/// captured — always present (possibly empty) per the script's RESULT_JSON
/// contract.
/// </param>
/// <param name="RawResultJsonLine">
/// The verbatim <c>RESULT_JSON: {...}</c> line as printed by the script
/// (including the <c>RESULT_JSON: </c> prefix), or <c>null</c> when no such
/// line was found in the captured output at all (as opposed to one being
/// found but failing to parse — see <see cref="Malformed"/>).
/// </param>
/// <param name="ParsedResultJson">
/// The parsed JSON document of the <c>RESULT_JSON:</c> line's payload,
/// preserved verbatim (including any keys this type does not otherwise
/// surface) so callers can inspect exactly what the driver emitted.
/// <c>null</c> when no line was found or the line was not valid JSON.
/// </param>
/// <param name="Malformed">
/// <c>true</c> when a <c>RESULT_JSON:</c> line was found but could not be
/// parsed as valid JSON with the expected shape — a driver defect distinct
/// from the driver legitimately reporting <c>ok=false</c>. Always implies
/// <see cref="Ok"/> is <c>false</c>.
/// </param>
/// <param name="TaskName">
/// The <c>logix-mcp-s1-&lt;verb&gt;</c> scheduled task name this run used
/// (see <see cref="Session1ScriptRunner.BuildTaskName"/>) — always set,
/// regardless of whether the run ultimately succeeded or failed, so a
/// failure payload can be traced back to the exact scheduled task an
/// operator should inspect with <c>Get-ScheduledTaskInfo</c> (an earlier change
/// requirement 4).
/// </param>
/// <param name="LastTaskResultHex">
/// The scheduled task's <c>LastTaskResult</c> (the Win32 exit/status code
/// Task Scheduler recorded for the run), formatted as
/// <c>0xXXXXXXXX</c> — e.g. <c>0xFFFD0000</c> for the "path format not
/// supported" quoting bug this runner guards against (see
/// <see cref="Session1ScriptRunner.BuildActionArgument"/>'s remarks).
/// <c>null</c> when the task never ran or the value could not be queried.
/// </param>
/// <param name="LastTaskResultDecimal">
/// The same <c>LastTaskResult</c> value as <see cref="LastTaskResultHex"/>,
/// as an unsigned decimal number. <c>null</c> under the same conditions as
/// <see cref="LastTaskResultHex"/>.
/// </param>
/// <param name="LaunchMode">
/// How the session-1 runner reached the desktop: <c>"direct"</c> or
/// <c>"scheduled_task"</c>. When the runner was never
/// invoked (<c>Launched</c> false) this is the planned mode; <c>null</c> only if the
/// environment probe itself failed.
/// </param>
/// <param name="RunLevel">
/// The scheduled task's run level (<c>"Highest"</c> or <c>"Limited"</c>);
/// <c>null</c> in direct mode, which registers no task.
/// </param>
/// <param name="GestureTier">
/// The gesture tier the driver reported using (always <c>"posted"</c>: the driver only
/// posts menu commands), passed through from the runner result's
/// <c>gestureTier</c>; <c>null</c> (omitted from the JSON) when the driver did not
/// report one.
/// </param>
/// <param name="DialogText">
/// The confirmation dialog's text as the driver reported it (<c>dialogText</c>);
/// <c>null</c> when the driver reported none.
/// </param>
/// <param name="DialogWarnings">
/// The dialog's Static texts other than the verb phrase and the question, in control
/// order (<c>dialogWarnings</c>), verbatim; <c>null</c> when the driver reported none.
/// </param>
/// <param name="ErrorsPaneToggled">
/// <c>errorsPaneToggled</c>: the driver posted the Errors-pane toggle (WM_COMMAND 32027) at least once;
/// <c>false</c> when absent (older scripts).
/// </param>
/// <param name="RetriedAfterPaneToggle">
/// <c>retriedAfterPaneToggle</c>: the verb's second post, after the toggle, produced the confirmation dialog;
/// <c>false</c> when absent.
/// </param>
/// <param name="PaneToggleRestored">
/// <c>paneToggleRestored</c>: the driver posted the toggle a second time to restore the pane after the retry also
/// failed; <c>false</c> when absent.
/// </param>
/// <param name="ErrorsPaneLayout">
/// <c>errorsPaneLayout</c>: where the driver left the Errors pane after a toggle (<c>auto-hide</c>, <c>docked</c>,
/// <c>hidden</c> or <c>unknown</c>); <c>null</c> when absent or malformed.
/// </param>
/// <param name="Kind">
/// Typed error kind for a pre-launch refusal (<c>StudioNotRunning</c>,
/// <c>StudioInOtherSession</c>, <c>StudioElevated</c>); <c>null</c> otherwise.
/// </param>
public record Session1ScriptResult(
    [property: JsonPropertyName("ok")]                    bool                  Ok,
    [property: JsonPropertyName("reason")]                string                Reason,
    [property: JsonPropertyName("screenshots")]           IReadOnlyList<string> Screenshots,
    [property: JsonPropertyName("rawResultJsonLine")]     string?               RawResultJsonLine     = null,
    [property: JsonPropertyName("parsedResultJson")]      JsonElement?          ParsedResultJson      = null,
    [property: JsonPropertyName("malformed")]             bool                  Malformed             = false,
    [property: JsonPropertyName("taskName")]              string?               TaskName              = null,
    [property: JsonPropertyName("lastTaskResultHex")]     string?               LastTaskResultHex     = null,
    [property: JsonPropertyName("lastTaskResultDecimal")] long?                 LastTaskResultDecimal = null,
    [property: JsonPropertyName("launchMode")]            string?               LaunchMode            = null,
    [property: JsonPropertyName("runLevel")]              string?               RunLevel              = null,
    [property: JsonPropertyName("launched")]              bool                  Launched              = true,
    [property: JsonPropertyName("gestureTier")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GestureTier = null,
    [property: JsonPropertyName("dialogText")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DialogText = null,
    [property: JsonPropertyName("dialogWarnings")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? DialogWarnings = null,
    [property: JsonPropertyName("kind")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null,
    [property: JsonPropertyName("errorsPaneToggled")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ErrorsPaneToggled = false,
    [property: JsonPropertyName("retriedAfterPaneToggle")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool RetriedAfterPaneToggle = false,
    [property: JsonPropertyName("paneToggleRestored")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool PaneToggleRestored = false,
    [property: JsonPropertyName("errorsPaneLayout")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorsPaneLayout = null);

/// <summary>
/// Verbatim capture of the <c>bench/studio-edits.ps1</c> driver's
/// <c>RESULT_JSON:</c> output, surfaced on every
/// <see cref="CancelPendingEditsHandler"/> response (success or failure) so a
/// driver defect (malformed JSON, unexpected/missing keys) is visible to the
/// caller rather than being silently swallowed and misread as a bench fact.
/// </summary>
/// <param name="ResultJsonLine">
/// The verbatim <c>RESULT_JSON: {...}</c> line the driver printed, or
/// <c>null</c> when no such line was found in its output.
/// </param>
/// <param name="Parsed">
/// The parsed JSON document of that line's payload, or <c>null</c> when no
/// line was found or it was not valid JSON.
/// </param>
/// <param name="Malformed">
/// <c>true</c> when a <c>RESULT_JSON:</c> line was present but could not be
/// parsed as valid JSON — a driver defect.
/// </param>
public record CancelPendingEditsDriverInfo(
    [property: JsonPropertyName("resultJsonLine")] string?      ResultJsonLine,
    [property: JsonPropertyName("parsed")]          JsonElement? Parsed,
    [property: JsonPropertyName("malformed")]       bool         Malformed);

/// <summary>
/// Successful response from <see cref="CancelPendingEditsHandler.ExecuteAsync"/>.
/// </summary>
/// <param name="Status">Always <c>"cancelled"</c> on success.</param>
/// <param name="Program">Program name that was targeted.</param>
/// <param name="Routine">Routine name that was targeted.</param>
/// <param name="Message">Human-readable summary of the operation outcome.</param>
/// <param name="Before">
/// The routine's <c>logix_pending_edits</c> snapshot observed immediately before
/// the cancel gesture was sent.
/// </param>
/// <param name="After">
/// The routine's <c>logix_pending_edits</c> snapshot observed immediately after
/// the cancel gesture completed.
/// </param>
/// <param name="Screenshots">
/// Every screenshot path collected by <c>bench/studio-edits.ps1</c> during this
/// run, in capture order.
/// </param>
/// <param name="Driver">
/// The verbatim <c>RESULT_JSON:</c> line and its parsed document, as reported
/// by the <c>bench/studio-edits.ps1</c> driver invocation that produced this
/// result — see <see cref="CancelPendingEditsDriverInfo"/>.
/// </param>
/// <param name="TaskName">
/// The <c>logix-mcp-s1-edits</c> scheduled task name the session-1 runner
/// used for this invocation (see
/// <see cref="Session1ScriptRunner.BuildTaskName"/>), surfaced alongside
/// <see cref="Driver"/>.
/// </param>
/// <param name="LastTaskResultHex">
/// The scheduled task's <c>LastTaskResult</c>, formatted as
/// <c>0xXXXXXXXX</c> (see
/// <see cref="Session1ScriptResult.LastTaskResultHex"/>). <c>null</c> when it
/// could not be queried.
/// </param>
/// <param name="LastTaskResultDecimal">
/// The same <c>LastTaskResult</c> value as <see cref="LastTaskResultHex"/>,
/// as an unsigned decimal number.
/// </param>
/// <param name="LaunchMode">
/// How the session-1 runner reached the desktop: <c>"direct"</c> or
/// <c>"scheduled_task"</c>. When the runner was never
/// invoked (<c>Launched</c> false) this is the planned mode; <c>null</c> only if the
/// environment probe itself failed.
/// </param>
/// <param name="RunLevel">
/// The scheduled task's run level (<c>"Highest"</c> or <c>"Limited"</c>);
/// <c>null</c> in direct mode, which registers no task.
/// </param>
/// <param name="Launched">
/// <c>true</c> when the session-1 runner was invoked for this call; <c>false</c>
/// when the call returned before the runner was ever invoked (in which case
/// <c>LaunchMode</c>/<c>RunLevel</c> report how it would have been launched, via
/// <see cref="Session1ScriptRunner.PlanLaunch"/>).
/// </param>
/// <param name="GestureTier">
/// The gesture tier the driver reported using (always <c>"posted"</c>: the driver only
/// posts menu commands), passed through from the runner result's
/// <c>gestureTier</c>; <c>null</c> (omitted from the JSON) when the driver did not
/// report one.
/// </param>
/// <param name="DialogText">
/// The confirmation dialog's text as the driver reported it; <c>null</c> (omitted from the
/// JSON) for the cancel and accept verbs and when the driver reported none.
/// </param>
/// <param name="DialogWarnings">
/// The dialog's warning texts verbatim, in control order; <c>null</c> (omitted
/// from the JSON) for the cancel and accept verbs.
/// </param>
/// <param name="ErrorsPaneLayout">
/// The Errors pane layout the driver reported after toggling the pane; <c>null</c> (omitted) when the pane was not toggled.
/// </param>
/// <param name="Note">
/// <see cref="StudioLifecycleHandler.PaneDockedNote"/> when the pane was left docked; <c>null</c> (omitted) otherwise.
/// </param>
/// <param name="Foreign">
/// <c>true</c> (serialised as <c>foreign</c>) when the zone acted on was not published by this server (the
/// <see cref="ZoneLedger"/> did not know it): an accept / assemble / test that passed <c>acceptForeign</c>, or a cancel.
/// Omitted otherwise.
/// </param>
/// <param name="Cancelled">Per-routine record of what the program-wide cancel took; cancel only.</param>
/// <param name="LeftoverTags">
/// Cancel only: tags the cancelled publish created that remain on the controller (cancel does not
/// remove tags); <c>null</c> (omitted from the JSON) when there are none.
/// </param>
/// <param name="LeftoverTagsNote">
/// Present with <paramref name="LeftoverTags"/>: <see cref="StudioLifecycleHandler.LeftoverTagsNoteText"/>.
/// </param>
/// <param name="CommentsRestored">
/// Cancel only: the <c>routine#rung</c> keys whose original comment, overwritten by an explicit-comment
/// workstation stage that was then published, was written back after the cancel; <c>null</c> (omitted) when none.
/// </param>
/// <param name="CommentRestoreFailure">
/// Cancel only: why an original comment could not be written back after the cancel; <c>null</c> (omitted) otherwise.
/// </param>
public record CancelPendingEditsResult(
    [property: JsonPropertyName("status")]                string                        Status,
    [property: JsonPropertyName("program")]               string                        Program,
    [property: JsonPropertyName("routine")]               string                        Routine,
    [property: JsonPropertyName("message")]               string                        Message,
    [property: JsonPropertyName("before")]                RoutinePendingEditState       Before,
    [property: JsonPropertyName("after")]                 RoutinePendingEditState       After,
    [property: JsonPropertyName("screenshots")]           IReadOnlyList<string>         Screenshots,
    [property: JsonPropertyName("driver")]                CancelPendingEditsDriverInfo? Driver                = null,
    [property: JsonPropertyName("taskName")]              string?                       TaskName              = null,
    [property: JsonPropertyName("lastTaskResultHex")]     string?                       LastTaskResultHex     = null,
    [property: JsonPropertyName("lastTaskResultDecimal")] long?                         LastTaskResultDecimal = null,
    [property: JsonPropertyName("launchMode")]            string?                       LaunchMode            = null,
    [property: JsonPropertyName("runLevel")]              string?                       RunLevel              = null,
    [property: JsonPropertyName("launched")]              bool                          Launched              = true,
    [property: JsonPropertyName("gestureTier")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GestureTier = null,
    [property: JsonPropertyName("dialogText")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DialogText = null,
    [property: JsonPropertyName("dialogWarnings")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? DialogWarnings = null,
    [property: JsonPropertyName("cancelled")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CancelledRoutine>? Cancelled = null,
    [property: JsonPropertyName("leftoverTags")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? LeftoverTags = null,
    [property: JsonPropertyName("leftoverTagsNote")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LeftoverTagsNote = null,
    [property: JsonPropertyName("commentsRestored")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? CommentsRestored = null,
    [property: JsonPropertyName("commentRestoreFailure")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CommentRestoreFailure = null,
    [property: JsonPropertyName("errorsPaneToggled")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ErrorsPaneToggled = false,
    [property: JsonPropertyName("errorsPaneLayout")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorsPaneLayout = null,
    [property: JsonPropertyName("note")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null,
    [property: JsonPropertyName("foreign")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Foreign = false);

/// <summary>
/// One routine's part in a cancel: what it held before and after Studio's program-wide cancel.
/// </summary>
/// <param name="Routine">Routine name.</param>
/// <param name="BeforeState">The routine's <c>logix_pending_edits</c> state before the cancel.</param>
/// <param name="AfterState">The routine's state on the fresh re-read after the cancel.</param>
/// <param name="PendingRungs">Pending rungs the routine held before the cancel.</param>
public record CancelledRoutine(
    [property: JsonPropertyName("routine")]      string Routine,
    [property: JsonPropertyName("beforeState")]  string BeforeState,
    [property: JsonPropertyName("afterState")]   string AfterState,
    [property: JsonPropertyName("pendingRungs")] int    PendingRungs);

/// <summary>
/// Error response from <see cref="CancelPendingEditsHandler.ExecuteAsync"/>.
/// </summary>
/// <param name="Error">Human-readable description of the failure or refusal.</param>
/// <param name="Before">
/// The routine's <c>logix_pending_edits</c> snapshot observed before the refusal
/// or failure, when one was successfully captured; <c>null</c> when the error
/// occurred before that read (e.g. Studio not installed, missing
/// <c>confirm</c>, bad input).
/// </param>
/// <param name="Screenshots">
/// Screenshot paths collected by <c>bench/studio-edits.ps1</c> before the
/// failure, when the script ran at all; <c>null</c> when the script was never
/// invoked.
/// </param>
/// <param name="Driver">
/// The verbatim <c>RESULT_JSON:</c> line and its parsed document from the
/// <c>bench/studio-edits.ps1</c> driver invocation, when one ran; <c>null</c>
/// when the failure occurred before the driver was ever invoked. See
/// <see cref="CancelPendingEditsDriverInfo"/>.
/// </param>
/// <param name="TaskName">
/// The <c>logix-mcp-s1-edits</c> scheduled task name the session-1 runner
/// used, when it ran at all; <c>null</c> when the failure occurred before the
/// session-1 runner was ever invoked.
/// </param>
/// <param name="LastTaskResultHex">
/// The scheduled task's <c>LastTaskResult</c>, formatted as
/// <c>0xXXXXXXXX</c> (see
/// <see cref="Session1ScriptResult.LastTaskResultHex"/>). <c>null</c> when it
/// could not be queried or the task never ran.
/// </param>
/// <param name="LastTaskResultDecimal">
/// The same <c>LastTaskResult</c> value as <see cref="LastTaskResultHex"/>,
/// as an unsigned decimal number.
/// </param>
/// <param name="LaunchMode">
/// How the session-1 runner reached the desktop: <c>"direct"</c> or
/// <c>"scheduled_task"</c>. When the runner was never
/// invoked (<c>Launched</c> false) this is the planned mode; <c>null</c> only if the
/// environment probe itself failed.
/// </param>
/// <param name="RunLevel">
/// The scheduled task's run level (<c>"Highest"</c> or <c>"Limited"</c>);
/// <c>null</c> in direct mode, which registers no task.
/// </param>
/// <param name="Launched">
/// <c>true</c> when the session-1 runner was invoked for this call; <c>false</c>
/// when the call returned before the runner was ever invoked (in which case
/// <c>LaunchMode</c>/<c>RunLevel</c> report how it would have been launched, via
/// <see cref="Session1ScriptRunner.PlanLaunch"/>).
/// </param>
/// <param name="GestureTier">
/// The gesture tier the driver reported using (always <c>"posted"</c>: the driver only
/// posts menu commands), passed through from the runner result's
/// <c>gestureTier</c>; <c>null</c> (omitted from the JSON) when the driver did not
/// report one.
/// </param>
/// <param name="DialogText">
/// The confirmation dialog's text as the driver reported it; <c>null</c> (omitted from the
/// JSON) for the cancel and accept verbs and when the driver reported none.
/// </param>
/// <param name="DialogWarnings">
/// The dialog's warning texts verbatim, in control order; <c>null</c> (omitted
/// from the JSON) for the cancel and accept verbs.
/// </param>
/// <param name="Kind">
/// Machine-readable refusal kind (e.g. <c>"NotTested"</c>); <c>null</c> (omitted from the
/// JSON) for every error without one.
/// </param>
/// <param name="Program">The program of a <c>ForeignPendingZone</c> refusal; <c>null</c> (omitted) otherwise.</param>
/// <param name="Routines">The live-zone routines the <see cref="ZoneLedger"/> does not know (<c>ForeignPendingZone</c> only).</param>
/// <param name="PublishedByThisServer">Always <c>false</c> on a <c>ForeignPendingZone</c> refusal; <c>null</c> (omitted) otherwise.</param>
public record CancelPendingEditsError(
    [property: JsonPropertyName("error")]                 string                        Error,
    [property: JsonPropertyName("before")]                RoutinePendingEditState?      Before                = null,
    [property: JsonPropertyName("screenshots")]           IReadOnlyList<string>?        Screenshots           = null,
    [property: JsonPropertyName("driver")]                CancelPendingEditsDriverInfo? Driver                = null,
    [property: JsonPropertyName("taskName")]              string?                       TaskName              = null,
    [property: JsonPropertyName("lastTaskResultHex")]     string?                       LastTaskResultHex     = null,
    [property: JsonPropertyName("lastTaskResultDecimal")] long?                         LastTaskResultDecimal = null,
    [property: JsonPropertyName("launchMode")]            string?                       LaunchMode            = null,
    [property: JsonPropertyName("runLevel")]              string?                       RunLevel              = null,
    [property: JsonPropertyName("launched")]              bool                          Launched              = false,
    [property: JsonPropertyName("gestureTier")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GestureTier = null,
    [property: JsonPropertyName("dialogText")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DialogText = null,
    [property: JsonPropertyName("dialogWarnings")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? DialogWarnings = null,
    [property: JsonPropertyName("kind")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null,
    [property: JsonPropertyName("hint")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint = null,
    [property: JsonPropertyName("errorsPaneToggled")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ErrorsPaneToggled = false,
    [property: JsonPropertyName("errorsPaneLayout")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorsPaneLayout = null,
    [property: JsonPropertyName("program")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Program = null,
    [property: JsonPropertyName("routines")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Routines = null,
    [property: JsonPropertyName("publishedByThisServer")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PublishedByThisServer = null);

// ── CancelPendingEditsHandler ──────────────────────────────────────────────────

/// <summary>
/// Pure handler for the <c>logix_cancel_pending_edits</c> MCP tool — a thin
/// <c>cancel</c> binding over the shared <see cref="StudioLifecycleHandler"/>,
/// which holds the flow described below.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
///
/// <para>
/// <b>This tool drives the Studio 5000 Logix Designer GUI in the interactive
/// desktop session — it is not an SDK call.</b> The Logix Designer SDK has no
/// API to cancel pending or accepted edits (verified against the SDK 2.2.1109
/// nupkg XML docs); only Studio 5000's
/// <c>Logic → Online Edits → Cancel Accepted Program Edits</c> menu gesture
/// can. This handler reaches that gesture indirectly, by asking
/// <c>bench/studio-edits.ps1</c> to run on the
/// interactive desktop (see <see cref="Session1ScriptRunner"/>): launched
/// directly as a child <c>powershell.exe</c> when this server is already in
/// the user's interactive session (the normal stdio-child case), or
/// through a <c>logix-mcp-s1-edits</c> scheduled task with an
/// <c>Interactive</c> principal when the server is in session 0 (SSH or a
/// service). It parses the single <c>RESULT_JSON:</c> line
/// it prints.
/// </para>
///
/// <para>
/// Gated accordingly:
/// <list type="number">
///   <item>Refuses when Studio 5000 does not appear to be installed on this
///     host — capability honesty: a server with no Studio 5000 install can
///     never drive its GUI, so this tool must say so up front rather than
///     attempt (and fail) the session-1 script invocation.</item>
///   <item>Refuses unless <c>confirm=true</c> is passed — cancelling discards
///     the controller's pending rung edits, and does so via desktop
///     automation (keyboard accelerators / coordinate clicks) rather than a
///     reviewable SDK call.</item>
///   <item>Reads <c>logix_pending_edits</c> for the target routine first and
///     refuses when its <c>state</c> is <c>"none"</c> — there is nothing this
///     tool can safely cancel.</item>
///   <item>Runs <c>bench/studio-edits.ps1 -Verb cancel</c> through the
///     session-1 script runner and refuses when it reports failure.</item>
///   <item>Re-reads <c>logix_pending_edits</c> for the routine and returns
///     both the before and after snapshots, plus every screenshot path the
///     script collected.</item>
/// </list>
/// </para>
/// </summary>
public static class CancelPendingEditsHandler
{
    /// <summary>
    /// The standard Studio 5000 Logix Designer (the GUI application, not the
    /// SDK) install root on Windows — the same path
    /// <c>bootstrap/cold-install-gate.ps1</c> uses to resolve the installed
    /// Logix Designer version. Its presence is treated as evidence that
    /// Studio 5000 is installed on this host and this tool's GUI-automation
    /// path can, in principle, be attempted.
    /// </summary>
    internal const string StudioInstallRoot =
        @"C:\Program Files (x86)\Rockwell Software\Studio 5000\Logix Designer";

    /// <summary>
    /// Default Studio-installed probe: returns <c>true</c> when
    /// <see cref="StudioInstallRoot"/> exists. Used when no override is
    /// supplied to <see cref="ExecuteAsync"/>; tests supply their own probe to
    /// exercise both branches without depending on the host's filesystem.
    /// </summary>
    internal static bool DefaultIsStudioInstalled() => Directory.Exists(StudioInstallRoot);

    /// <summary>
    /// Attempts to cancel all pending online edits for a single routine by
    /// driving the Studio 5000 GUI through the session-1 script runner.
    /// </summary>
    /// <param name="program">Program name containing the target routine. Required.</param>
    /// <param name="routine">Routine name to cancel edits for. Required.</param>
    /// <param name="confirm">
    /// Must be <c>true</c> to proceed. When <c>false</c> (the default), the
    /// call is refused before any Studio-installed probe result is acted on,
    /// pending-edit read, or session-1 script invocation happens.
    /// </param>
    /// <param name="pendingEditsReader">
    /// Reads the current <c>logix_pending_edits</c> state for
    /// <paramref name="program"/>/<paramref name="routine"/>. Production
    /// callers bind this to <see cref="PendingEditsHandler.ExecuteAsync"/>
    /// against the active <see cref="LogixProjectSession"/>; tests supply a
    /// fake reader.
    /// </param>
    /// <param name="session1Runner">
    /// Invokes <c>bench/studio-edits.ps1</c> with the given verb on the
    /// signed-in user's interactive desktop and returns its parsed
    /// <see cref="Session1ScriptResult"/>. It has two launch modes: when this
    /// server is in a non-zero session it runs the driver directly as a child
    /// <c>powershell.exe</c> (no scheduled task); when the server is in
    /// session 0 it registers and starts a <c>logix-mcp-s1-&lt;verb&gt;</c>
    /// scheduled task with an Interactive principal. The result's
    /// <c>LaunchMode</c> reports which was used. Production callers bind this to
    /// <see cref="Session1ScriptRunner.RunAsync"/>; tests supply a fake
    /// runner. Called with verb <c>"cancel"</c>.
    /// </param>
    /// <param name="isStudioInstalled">
    /// Optional override for the Studio-installed probe. Defaults to
    /// <see cref="DefaultIsStudioInstalled"/> when <c>null</c>.
    /// </param>
    /// <param name="ct">Cancellation token forwarded to all async operations.</param>
    /// <param name="planLaunch">
    /// Optional override reporting the <c>(launchMode, runLevel)</c> the runner would
    /// use, stamped on every refusal that occurs before the runner is invoked (those
    /// also carry <c>launched=false</c>). Defaults to
    /// <see cref="Session1ScriptRunner.PlanLaunch"/> on the real environment probe.
    /// </param>
    /// <param name="leftoverTagsProvider">
    /// Optional. Returns the names of tags the cancelled publish created that remain on the controller;
    /// a successful result lists them in <c>leftoverTags</c> with the note that cancel does not remove tags.
    /// </param>
    /// <param name="imageSession">
    /// Optional. The session whose controller image follows the cancel: the image is restored
    /// to its state before this session's publish, or marked stale when it cannot be. <c>null</c> leaves it alone.
    /// </param>
    /// <returns>
    /// A <see cref="CancelPendingEditsResult"/> on success, or a
    /// <see cref="CancelPendingEditsError"/> on refusal or failure. Never
    /// throws (except <see cref="OperationCanceledException"/> for
    /// cancellation).
    /// </returns>
    public static Task<object> ExecuteAsync(
        string                                                     program,
        string                                                     routine,
        bool                                                       confirm,
        Func<string, string, CancellationToken, Task<object>>      pendingEditsReader,
        Func<string, CancellationToken, Task<Session1ScriptResult>> session1Runner,
        Func<bool>?                                                isStudioInstalled = null,
        CancellationToken                                          ct = default,
        Func<(string? LaunchMode, string? RunLevel)>?              planLaunch = null,
        Func<IReadOnlyList<string>>?                               leftoverTagsProvider = null,
        TagSessionService?                                         imageSession = null,
        object?                                                    ledgerSession = null) =>
        StudioLifecycleHandler.ExecuteAsync(
            StudioLifecycleVerb.Cancel, program, routine, confirm, pendingEditsReader,
            session1Runner, isStudioInstalled, ct, planLaunch, leftoverTagsProvider, imageSession,
            ledgerSession: ledgerSession);
}
#endif
