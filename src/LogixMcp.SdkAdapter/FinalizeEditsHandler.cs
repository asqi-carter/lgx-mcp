#if LOGIX_SDK
// ── FinalizeEditsHandler — honest refusal for an existing pending
//    edit zone ────────────────────────────────────────
using System.Text.Json.Serialization;

// Alias System.Threading.Tasks.Task to avoid ambiguity with L5Sharp.Core.Task
// when both namespaces are in scope.
using Task = System.Threading.Tasks.Task;

// ── Result / error types ──────────────────────────────────────────────────────

/// <summary>
/// Error response from <see cref="FinalizeEditsHandler.ExecuteAsync"/>.
/// </summary>
/// <param name="Error">Human-readable description of the failure or refusal.</param>
/// <param name="Before">
/// The routine's <c>logix_pending_edits</c> snapshot observed before the refusal
/// or failure, when one was successfully captured; <c>null</c> when the error
/// occurred before that read (e.g. missing <c>confirm</c>, bad input, no session).
/// </param>
/// <param name="Kind">
/// Optional machine-readable error category. <c>"PendingZoneStudioOnly"</c>
/// When the target routine's <c>logix_pending_edits</c>
/// state is not <c>"none"</c>: the Logix Designer SDK has no accept, assemble, or
/// cancel call for an existing pending edit zone, so this tool refuses rather than
/// calling <c>PartialImportOption.FinalizeEdits</c> against it. <c>null</c> for
/// every other error on this handler (including the <c>state='none'</c> "nothing
/// pending" refusal, and input-validation/session-guard failures).
/// </param>
public record FinalizeEditsError(
    [property: JsonPropertyName("error")]  string                    Error,
    [property: JsonPropertyName("before")] RoutinePendingEditState?  Before = null,
    [property: JsonPropertyName("kind"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string?                    Kind = null,
    [property: JsonPropertyName("hint"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string?                    Hint = null,
    [property: JsonPropertyName("session"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string?                    Session = null);

// ── FinalizeEditsHandler ──────────────────────────────────────────────────────

/// <summary>
/// Pure handler for the <c>logix_finalize_edits</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
///
/// <para>
/// <b>This tool assembles pending edits into the running program on a live
/// controller.</b> The Logix Designer SDK has <em>no</em> accept, assemble, or
/// cancel call for an existing pending edit zone (bench-confirmed):
/// <c>PartialImportOption.FinalizeEdits</c> is an import option that finalizes the
/// rungs of the import that carries it — it cannot touch a zone created by a prior
/// import. Once a routine has a pending edit zone, the SDK refuses every further
/// import into that program until an operator resolves it in Studio 5000 (accept
/// or cancel) — one publish per Studio accept/cancel cycle. This handler is gated
/// accordingly:
/// <list type="number">
///   <item>Refuses unless <c>confirm=true</c> is passed (same shape as
///     <c>logix_controller_mode</c>'s write-path gate).</item>
///   <item>Reads <c>logix_pending_edits</c> for the target routine and refuses
///     when its <c>state</c> is <c>"none"</c> — there is either nothing pending,
///     or (per the known an earlier change limitation) an accepted-but-unassembled edit
///     this reader cannot see. Either way this tool cannot safely proceed.</item>
///   <item>Refuses with kind <c>"PendingZoneStudioOnly"</c> when the state is
///     anything other than <c>"none"</c> — naming the routine, every pending rung
///     (number, type, text), and the valid transitions: with
///     <c>LGX_ENABLE_STUDIO_AUTOMATION=1</c>, <c>logix_accept_pending_edits</c> or
///     <c>logix_cancel_pending_edits</c>; otherwise accept or cancel the zone in
///     Logix Designer (Logic -> Online Edits). The kind name is retained for
///     compatibility. This handler never calls the SDK with
///     <c>PartialImportOption.FinalizeEdits</c> against an existing zone — there is
///     no code path left in this handler that does so.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Never called automatically.</b> This handler is invoked only by the
/// <c>logix_finalize_edits</c> MCP tool. No other code path — in particular
/// neither <c>logix_connect</c>/<c>SdkConnectFactory</c> nor
/// <c>logix_propose_rung_edit</c>/<c>ProposeRungEditHandler</c> — may call it:
/// finalizing an edit zone is a decision this server never makes
/// silently, and this handler itself now never reaches an SDK import call.
/// </para>
/// </summary>
public static class FinalizeEditsHandler
{
    /// <summary>Pointer appended to every refusal: the one-step path when nothing is pending.</summary>
    private const string LiveHint =
        "To go live in one step when nothing is pending, use " +
        "`logix_propose_rung_edit target=\"live\"`.";

    /// <summary>
    /// Attempts to finalize (assemble) the pending edit zone of a single routine on
    /// the active online SDK session.
    ///
    /// <para>
    /// As, this never succeeds: the Logix Designer SDK
    /// has no call that can assemble an existing pending edit zone, so any routine
    /// whose <c>logix_pending_edits</c> state is not <c>"none"</c> is refused with
    /// kind <c>"PendingZoneStudioOnly"</c> rather than attempting an SDK import
    /// that is known to fail. Resolving the zone is a Studio 5000 operation
    /// (accept or cancel in Logic -> Online Edits), or
    /// <c>logix_accept_pending_edits</c> / <c>logix_cancel_pending_edits</c> when the
    /// Studio-automation flag is on — not something this tool can do.
    /// </para>
    /// </summary>
    /// <param name="program">Program name containing the target routine. Required.</param>
    /// <param name="routine">Routine name to finalize. Required.</param>
    /// <param name="confirm">
    /// Must be <c>true</c> to proceed. When <c>false</c> (the default), the call is
    /// refused before any session or controller interaction.
    /// </param>
    /// <param name="session">
    /// Open <see cref="LogixProjectSession"/> to use. Must already be in the
    /// <c>Open</c> state, online, and correlated — this method does not open,
    /// correlate, or close it.
    /// </param>
    /// <param name="ct">Cancellation token forwarded to all async operations.</param>
    /// <param name="studioAutomationEnabled">
    /// Whether <c>LGX_ENABLE_STUDIO_AUTOMATION</c> is on. When <c>true</c> the
    /// <c>PendingZoneStudioOnly</c> refusal names <c>logix_accept_pending_edits</c> and
    /// <c>logix_cancel_pending_edits</c>; otherwise it names Logix Designer and the flag.
    /// </param>
    /// <returns>
    /// Always a <see cref="FinalizeEditsError"/> refusal in the current SDK:
    /// either the <c>state='none'</c> "nothing pending" refusal, or
    /// (when the state is not <c>"none"</c>) the <c>"PendingZoneStudioOnly"</c>
    /// refusal. Never throws (except <see cref="OperationCanceledException"/> for
    /// cancellation).
    /// </returns>
    public static async Task<object> ExecuteAsync(
        string               program,
        string               routine,
        bool                 confirm,
        LogixProjectSession  session,
        CancellationToken    ct = default,
        bool                 studioAutomationEnabled = false)
    {
        // ── Input validation ────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(program))
            return new FinalizeEditsError(
                "program must not be empty. logix_finalize_edits requires both " +
                "program and routine to identify a single routine's edit zone.");

        if (string.IsNullOrWhiteSpace(routine))
            return new FinalizeEditsError(
                "routine must not be empty. logix_finalize_edits requires both " +
                "program and routine to identify a single routine's edit zone.");

        if (program.Contains('\'') || routine.Contains('\''))
            return new FinalizeEditsError(
                "program and routine must not contain a single-quote character — " +
                "it cannot be safely embedded in the SDK XPath.");

        // ── Confirm gate — checked before any session or controller interaction ──
        // Even though this handler can never actually assemble an edit zone (the
        // SDK has no call that can), the caller does not know that until this call
        // returns, and reading pending-edit state still touches the live session.
        // The confirm gate stays so the same acknowledgement shape is required
        // regardless of outcome.
        if (!confirm)
            return new FinalizeEditsError(
                $"logix_finalize_edits refused for '{program}'/'{routine}': finalizing " +
                "assembles the controller's pending rung edits into the running program " +
                "on a live controller. Pass confirm=true to acknowledge this risk and " +
                "proceed.");

        // ── Session guards ────────────────────────────────────────────────────────
        if (!session.IsOnline)
            return new FinalizeEditsError(
                "Session is not online. Finalizing edits requires an online " +
                "correlated session. Ensure the session has been connected to a " +
                "live controller (sdkMode=\"online\") before calling logix_finalize_edits.");

        if (!session.IsCorrelated)
            return new FinalizeEditsError(
                "Session is not correlated. Finalizing edits requires an online " +
                "correlated session. Ensure the controller and project images are " +
                "aligned before calling logix_finalize_edits.");

        // ── Read pending-edit state first; refuse when there is nothing this
        //     tool can safely finalize ──────────────────────────────────────────
        var beforeResult = await PendingEditsHandler.ExecuteAsync(program, routine, session, ct)
            .ConfigureAwait(false);

        if (beforeResult is PendingEditsError beforeError)
            // A read that failed because the SDK server faulted keeps its kind, hint and session marker (G45).
            return new FinalizeEditsError(
                $"Failed to read pending-edit state for '{program}'/'{routine}' before " +
                $"finalizing: {beforeError.Error}",
                Kind: beforeError.Kind == ErrorKinds.SdkServerFaulted ? beforeError.Kind : null,
                Hint: beforeError.Kind == ErrorKinds.SdkServerFaulted ? beforeError.Hint : null,
                Session: beforeError.Kind == ErrorKinds.SdkServerFaulted ? beforeError.Session : null);

        var beforePending = (PendingEditsResult)beforeResult;
        var beforeRoutine = beforePending.Routines.FirstOrDefault(r =>
            string.Equals(r.Program, program, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Routine, routine, StringComparison.OrdinalIgnoreCase));

        if (beforeRoutine is null)
            return new FinalizeEditsError(
                $"Routine '{routine}' in program '{program}' was not found while reading " +
                "pending-edit state. Check the program and routine names, and that the " +
                "routine's Type is RLL.");

        if (beforeRoutine.State == "none")
            return new FinalizeEditsError(
                $"logix_finalize_edits refused for '{program}'/'{routine}': " +
                "logix_pending_edits reports state='none' — there is nothing pending to " +
                "finalize. Note: a rung accepted via AcceptEdits has been " +
                "observed to export as plain 'N' with no distinguishing marker, so " +
                "'none' is not proof there is no accepted-but-unassembled edit — it does " +
                "mean this tool cannot safely proceed. " + LiveHint,
                Before: beforeRoutine);

        // ── PendingZoneStudioOnly ──────────────────────
        // The only remaining state is non-'none', i.e. an existing pending edit
        // zone. The Logix Designer SDK has no accept, assemble, or cancel call for
        // it: PartialImportOption.FinalizeEdits is an import option that finalizes
        // the rungs of the import that carries it and cannot touch a zone created
        // by a prior import — while this zone exists, the SDK refuses every
        // further import into this program (one publish per Studio accept/cancel
        // cycle, bench-confirmed 2026-09-07). This handler never calls the SDK
        // against an existing zone; resolving it is a Studio 5000 operation or, with
        // the Studio-automation flag on, logix_accept_pending_edits /
        // logix_cancel_pending_edits. The kind name is kept for compatibility.
        var pendingRungsSummary = beforeRoutine.PendingRungs.Count == 0
            ? "(none listed)"
            : string.Join("; ", beforeRoutine.PendingRungs.Select(p =>
                $"rung {p.Number} (type={p.Type}): \"{p.Text}\""));

        return new FinalizeEditsError(
            $"logix_finalize_edits refused for '{program}'/'{routine}': logix_pending_edits " +
            $"reports state='{beforeRoutine.State}' — an existing pending edit zone. The " +
            "Logix Designer SDK has no accept, assemble, or cancel call for an existing " +
            "pending edit zone: PartialImportOption.FinalizeEdits is an import " +
            "option that finalizes the rungs of the import that carries it and cannot touch " +
            "a zone created by a prior import — while this zone exists, the SDK refuses " +
            $"every further import into '{program}' (one publish per Studio accept/cancel " +
            $"cycle). Pending rungs: {pendingRungsSummary}. This tool will never call the " +
            "SDK against this existing zone. Resolve it first — finalize cannot do it: " +
            (studioAutomationEnabled
                ? "call logix_accept_pending_edits to accept it (this makes the rungs " +
                  "execute on a running controller — only with the engineer's go-ahead) or " +
                  "logix_cancel_pending_edits to cancel it, each with confirm=true; verify " +
                  "afterwards with logix_pending_edits. The edits can also be accepted or " +
                  "cancelled in Logix Designer (Logic -> Online Edits)."
                : "accept or cancel the edits in Logix Designer (Logic -> Online Edits), " +
                  "or set LGX_ENABLE_STUDIO_AUTOMATION=1 on this host to use " +
                  "logix_accept_pending_edits or logix_cancel_pending_edits.") +
            " " + LiveHint,
            Before: beforeRoutine,
            Kind:   "PendingZoneStudioOnly");
    }
}
#endif
