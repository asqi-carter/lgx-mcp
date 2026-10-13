#if LOGIX_SDK
// ── UntestPendingEditsHandler — Studio-driver untest of a routine's pending edit zone ──
using Task = System.Threading.Tasks.Task;

/// <summary>
/// Pure handler for the <c>logix_untest_pending_edits</c> MCP tool — a thin <c>untest</c>
/// binding over the shared <see cref="StudioLifecycleHandler"/>.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
///
/// <para>
/// <b>This tool drives the Studio 5000 Logix Designer GUI with a posted command — it is
/// not an SDK call.</b> Gates and order are those of <see cref="AcceptPendingEditsHandler"/>:
/// Studio installed → <c>confirm</c> → read <c>logix_pending_edits</c> and refuse on
/// <c>none</c> → <c>bench/studio-edits.ps1 -Verb untest</c> via the session-1 runner →
/// re-read <c>logix_pending_edits</c>. See <see cref="StudioLifecycleVerb.Untest"/> for the
/// success contract. Results reuse <see cref="CancelPendingEditsResult"/> and
/// <see cref="CancelPendingEditsError"/>.
/// </para>
/// </summary>
public static class UntestPendingEditsHandler
{
    /// <summary>
    /// Runs the <c>untest</c> verb through <see cref="StudioLifecycleHandler.ExecuteAsync"/>.
    /// Parameters are documented on <see cref="CancelPendingEditsHandler.ExecuteAsync"/>; the
    /// runner is called with verb <c>"untest"</c>.
    /// </summary>
    /// <param name="program">Program name containing the target routine. Required.</param>
    /// <param name="routine">Routine name whose pending edits are targeted. Required.</param>
    /// <param name="confirm">Must be <c>true</c> to proceed.</param>
    /// <param name="pendingEditsReader">Reads the routine's <c>logix_pending_edits</c> state.</param>
    /// <param name="session1Runner">Invokes the driver with the given verb.</param>
    /// <param name="isStudioInstalled">Optional Studio-installed probe override.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="planLaunch">Optional launch-plan override stamped on pre-launch refusals.</param>
    /// <returns>A <see cref="CancelPendingEditsResult"/> or a <see cref="CancelPendingEditsError"/>.</returns>
    public static Task<object> ExecuteAsync(
        string                                                      program,
        string                                                      routine,
        bool                                                        confirm,
        Func<string, string, CancellationToken, Task<object>>       pendingEditsReader,
        Func<string, CancellationToken, Task<Session1ScriptResult>> session1Runner,
        Func<bool>?                                                 isStudioInstalled = null,
        CancellationToken                                           ct = default,
        Func<(string? LaunchMode, string? RunLevel)>?               planLaunch = null) =>
        StudioLifecycleHandler.ExecuteAsync(
            StudioLifecycleVerb.Untest, program, routine, confirm, pendingEditsReader,
            session1Runner, isStudioInstalled, ct, planLaunch);
}
#endif
