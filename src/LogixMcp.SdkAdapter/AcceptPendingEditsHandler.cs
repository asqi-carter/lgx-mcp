#if LOGIX_SDK
// ── AcceptPendingEditsHandler — Studio-GUI-driven accept (assemble) of a routine's
//    pending edit zone ──────────────────────────────────
using Task = System.Threading.Tasks.Task;

/// <summary>
/// Pure handler for the <c>logix_accept_pending_edits</c> MCP tool — a thin
/// <c>accept</c> binding over the shared <see cref="StudioLifecycleHandler"/>.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
///
/// <para>
/// <b>This tool drives the Studio 5000 Logix Designer GUI in the interactive
/// desktop session — it is not an SDK call.</b> The SDK cannot assemble an
/// existing pending edit zone. Accepting makes the pending rungs execute on a
/// running controller, which is why it is gated by
/// <c>LGX_ENABLE_STUDIO_AUTOMATION=1</c> (tool layer) and <c>confirm=true</c>.
/// </para>
///
/// <para>
/// Flow (same gates and order as <see cref="CancelPendingEditsHandler"/>):
/// Studio installed → <c>confirm</c> → read <c>logix_pending_edits</c> and refuse on
/// <c>none</c> → <c>bench/studio-edits.ps1 -Verb accept</c> via the session-1 runner →
/// re-read <c>logix_pending_edits</c>. Success is returned only when the after-state
/// is <c>none</c>; a gesture that reports success while the zone is still pending is
/// an error naming both states.
/// </para>
///
/// <para>
/// Results reuse <see cref="CancelPendingEditsResult"/> (status <c>"accepted"</c>) and
/// <see cref="CancelPendingEditsError"/>, including <c>launched</c>, <c>launchMode</c>
/// and <c>runLevel</c>.
/// </para>
/// </summary>
public static class AcceptPendingEditsHandler
{
    /// <summary>
    /// Accepts (assembles) all pending online edits for a single routine by driving
    /// the Studio 5000 GUI through the session-1 script runner. Parameters are
    /// documented on <see cref="CancelPendingEditsHandler.ExecuteAsync"/>; the runner
    /// is called with verb <c>"accept"</c>.
    /// </summary>
    /// <param name="program">Program name containing the target routine. Required.</param>
    /// <param name="routine">Routine name to accept edits for. Required.</param>
    /// <param name="confirm">Must be <c>true</c> to proceed.</param>
    /// <param name="pendingEditsReader">Reads the routine's <c>logix_pending_edits</c> state.</param>
    /// <param name="session1Runner">Invokes the driver with the given verb.</param>
    /// <param name="isStudioInstalled">Optional Studio-installed probe override.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="planLaunch">Optional launch-plan override stamped on pre-launch refusals.</param>
    /// <returns>
    /// A <see cref="CancelPendingEditsResult"/> on success, or a
    /// <see cref="CancelPendingEditsError"/> on refusal or failure.
    /// </returns>
    public static Task<object> ExecuteAsync(
        string                                                      program,
        string                                                      routine,
        bool                                                        confirm,
        Func<string, string, CancellationToken, Task<object>>       pendingEditsReader,
        Func<string, CancellationToken, Task<Session1ScriptResult>> session1Runner,
        Func<bool>?                                                 isStudioInstalled = null,
        CancellationToken                                           ct = default,
        Func<(string? LaunchMode, string? RunLevel)>?               planLaunch = null) =>
        ExecuteAsync(
            program, routine, confirm, pendingEditsReader, session1Runner, isStudioInstalled, ct, planLaunch,
            imageSession: null);

    /// <summary>
    /// As the overload above, with the session whose controller image follows the result.
    /// Parameters are documented there.
    /// </summary>
    /// <param name="program">Program name containing the target routine. Required.</param>
    /// <param name="routine">Routine name. Required.</param>
    /// <param name="confirm">Must be <c>true</c> to proceed.</param>
    /// <param name="pendingEditsReader">Reads the routine's <c>logix_pending_edits</c> state.</param>
    /// <param name="session1Runner">Invokes the driver with the given verb.</param>
    /// <param name="isStudioInstalled">Studio-installed probe override, or <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="planLaunch">Launch-plan override stamped on pre-launch refusals, or <c>null</c>.</param>
    /// <param name="imageSession">
    /// Session whose controller image follows the result: a zone this session published is applied to the image, any
    /// other zone marks it stale (see <see cref="SessionImageState"/>). <c>null</c> leaves the image alone.
    /// </param>
    /// <returns>A <see cref="CancelPendingEditsResult"/> or a <see cref="CancelPendingEditsError"/>.</returns>
    public static Task<object> ExecuteAsync(
        string                                                      program,
        string                                                      routine,
        bool                                                        confirm,
        Func<string, string, CancellationToken, Task<object>>       pendingEditsReader,
        Func<string, CancellationToken, Task<Session1ScriptResult>> session1Runner,
        Func<bool>?                                                 isStudioInstalled,
        CancellationToken                                           ct,
        Func<(string? LaunchMode, string? RunLevel)>?               planLaunch,
        TagSessionService?                                          imageSession) =>
        StudioLifecycleHandler.ExecuteAsync(
            StudioLifecycleVerb.Accept, program, routine, confirm, pendingEditsReader,
            session1Runner, isStudioInstalled, ct, planLaunch, imageSession: imageSession);
}
#endif
