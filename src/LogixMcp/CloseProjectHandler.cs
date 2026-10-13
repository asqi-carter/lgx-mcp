using System.Text.Json.Serialization;

/// <summary>
/// Pending-batch snapshot of the active file-backed SDK session, as reported
/// by <see cref="CloseProjectHandler.SdkSessionInspector"/>.  A non-<c>null</c>
/// instance means a file-backed SDK session (<c>LogixProjectSession</c>) is
/// currently active, regardless of whether it has any pending work.
/// </summary>
/// <param name="StagedTagCount">
/// Number of tag declarations held in the session's staged-tag buffer (the <c>StagedTagBuffer</c>)
/// — tags stripped from a workstation-target
/// rung-edit fragment and not yet published to the controller. Zero when none
/// are staged.
/// </param>
/// <param name="HasStagedRungEdit">
/// <c>true</c> when a rung edit has been staged to the workstation target
/// (<c>logix_propose_rung_edit target="workstation"</c>) and not yet published
/// to the controller — an unassembled ("e"-typed) edit living only in the
/// session's in-memory project image.
/// </param>
/// <param name="Faulted">
/// <c>true</c> when the SDK server faulted (<c>LgxSrv_E_FATAL_ERROR</c> /
/// <c>LgxSrv_E_SERVER_FAULTED</c>) and the session is dead. A faulted session
/// can only be disposed: its staged work is already lost, so the pending-batch
/// guard does not apply and a loaded project file is not required.
/// </param>
/// <param name="StagedRungCount">
/// Number of rungs held in the session's staged-rung buffer (zero when none). A connect that replaces the
/// session discards them and reports the count in a warning.
/// </param>
/// <param name="StagedRoutines">
/// Routines (<c>Program/Routine</c>) holding those staged rungs; <c>null</c> or empty when none.
/// </param>
public record SdkSessionCloseInfo(
    int  StagedTagCount,
    bool HasStagedRungEdit,
    bool Faulted = false,
    int  StagedRungCount = 0,
    IReadOnlyList<string>? StagedRoutines = null);

/// <summary>
/// Strongly-typed success response returned by <see cref="CloseProjectHandler.Execute"/>.
/// </summary>
/// <param name="ClosedPath">File-system path of the project that was closed.</param>
/// <param name="SdkSessionDisposed">
/// <c>true</c> when a file-backed SDK session was active and was disposed as
/// part of the close; <c>false</c> when no SDK session was active (core-only
/// build, or the active tag session — if any — was not SDK-backed).
/// </param>
public record CloseProjectResult(
    [property: JsonPropertyName("closedPath")]         string ClosedPath,
    [property: JsonPropertyName("sdkSessionDisposed")] bool   SdkSessionDisposed);

/// <summary>
/// Structured error returned by <see cref="CloseProjectHandler.Execute"/> when
/// <c>logix_close_project</c> is called with no project open.
/// </summary>
/// <param name="Error">Human-readable description of the failure.</param>
public record CloseProjectNoProjectError(
    [property: JsonPropertyName("error")] string Error);

/// <summary>
/// Structured error returned by <see cref="CloseProjectHandler.Execute"/> when a
/// staged batch is pending in the active SDK session — the staged-tag
/// buffer, an unpublished workstation-staged rung edit, or both — so closing
/// the project would silently discard it.  The project remains loaded when
/// this error is returned.
/// </summary>
/// <param name="Error">Human-readable description of the refusal.</param>
/// <param name="Pending">
/// Names the specific pending work that blocked the close (e.g. staged tag
/// count and/or the unpublished rung edit), so the caller does not have to
/// guess what would have been discarded.
/// </param>
/// <param name="Hint">
/// Actionable next step: publish the staged batch to the controller (or
/// otherwise discard it explicitly) before retrying the close. Never suggests
/// an automatic discard — the caller must make that choice explicitly.
/// </param>
public record CloseProjectPendingBatchError(
    [property: JsonPropertyName("error")]   string Error,
    [property: JsonPropertyName("pending")] string Pending,
    [property: JsonPropertyName("hint")]    string Hint);

/// <summary>
/// Pure handler for the <c>logix_close_project</c> MCP tool.  Unloads the open
/// project (<see cref="ProjectService.Clear"/>) and disposes any active
/// file-backed SDK session, refusing when a staged batch is pending.
///
/// <para>
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
/// Never issues any controller communication: it only inspects and disposes
/// local, in-process state (the SDK session's gRPC handle and private working
/// copy, and the in-memory <see cref="ProjectService"/>/<see cref="TagSessionService"/>
/// singletons). Never throws.
/// </para>
/// </summary>
public static class CloseProjectHandler
{
    // ── SDK session inspection/disposal seams ─────────────────────────────────

    /// <summary>
    /// SDK-provided delegate that inspects the active <see cref="TagSessionService"/>
    /// session for a file-backed SDK session and reports its pending-batch state.
    ///
    /// <para>
    /// Set to a non-<c>null</c> value by <c>SdkAdapterInitializer.Initialize</c>
    /// when the <c>LogixMcp.SdkAdapter</c> assembly is loaded. <c>null</c> in
    /// core-only builds or when the adapter has not been loaded — in which case
    /// no SDK session is possible and the pending-batch guard trivially passes.
    /// </para>
    ///
    /// <para>
    /// Returns <c>null</c> when no file-backed SDK session is active (e.g. the
    /// active tag session is CIP-backed, virtual, or there is no active tag
    /// session at all); returns a populated <see cref="SdkSessionCloseInfo"/>
    /// when one is active, whether or not it has pending work.
    /// </para>
    /// </summary>
    public static Func<TagSessionService, SdkSessionCloseInfo?>? SdkSessionInspector { get; set; }

    /// <summary>
    /// SDK-provided delegate that disposes the active file-backed SDK session
    /// (releasing its gRPC handle and deleting its private working copy) and
    /// detaches it from <see cref="TagSessionService"/>.
    ///
    /// <para>
    /// Set to a non-<c>null</c> value by <c>SdkAdapterInitializer.Initialize</c>
    /// when the <c>LogixMcp.SdkAdapter</c> assembly is loaded. <c>null</c> in
    /// core-only builds. Only invoked by <see cref="Execute"/> after
    /// <see cref="SdkSessionInspector"/> confirms an SDK session is active, and
    /// only after the pending-batch guard has passed. Never issues any
    /// controller communication.
    /// </para>
    /// </summary>
    public static Action<TagSessionService>? SdkSessionDisposer { get; set; }

    // ── Execute ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Unloads the currently-open project and disposes any active file-backed
    /// SDK session, refusing when a staged batch is pending.
    /// </summary>
    /// <param name="projectService">The project singleton holding the loaded project.</param>
    /// <param name="tagSessionService">
    /// The tag-session singleton holding the active <see cref="ITagService"/>, if any.
    /// </param>
    /// <returns>
    /// A <see cref="CloseProjectResult"/> naming the closed path on success;
    /// a <see cref="CloseProjectNoProjectError"/> when no project is open;
    /// or a <see cref="CloseProjectPendingBatchError"/> — with the project left
    /// loaded — when a staged batch would be silently discarded.
    /// </returns>
    public static object Execute(ProjectService projectService, TagSessionService tagSessionService)
    {
        ArgumentNullException.ThrowIfNull(projectService);
        ArgumentNullException.ThrowIfNull(tagSessionService);

        var sessionInfo = SdkSessionInspector?.Invoke(tagSessionService);
        bool faulted = sessionInfo is { Faulted: true };

        // A faulted SDK session is closable even when no project file is loaded (an upload session).
        if (!projectService.IsLoaded && !faulted)
            return new CloseProjectNoProjectError(
                "No project is open. There is nothing for logix_close_project to close.");

        string closedPath = projectService.IsLoaded
            ? projectService.FilePath!
            : "(no project file; faulted SDK session only)";

        // ── Pending-batch guard ───────────────────────
        // Skipped for a faulted session: the SDK server is dead, so the staged work cannot be published.
        if (!faulted && sessionInfo is { } pending && (pending.StagedTagCount > 0 || pending.HasStagedRungEdit))
        {
            var pendingParts = new List<string>();
            if (pending.StagedTagCount > 0)
                pendingParts.Add(
                    $"{pending.StagedTagCount} staged tag declaration(s) in the workstation buffer");
            if (pending.HasStagedRungEdit)
                pendingParts.Add(
                    "an unpublished workstation-staged (\"e\"-typed) rung edit");
            string pendingDescription = string.Join(" and ", pendingParts);

            return new CloseProjectPendingBatchError(
                Error:
                    $"Cannot close the project: {pendingDescription} would be silently discarded.",
                Pending: pendingDescription,
                Hint:
                    "Publish the staged batch to the controller first " +
                    "(logix_propose_rung_edit with target=\"controller\"), or discard it " +
                    "explicitly (logix_connect to establish a fresh session), then retry " +
                    "logix_close_project. The project remains open and nothing was lost.");
        }

        // ── Dispose the SDK session (if any) and unload the project ────────────
        // sessionInfo being non-null means an active file-backed SDK session was
        // found (whether or not it had pending work, already ruled out above).
        bool sdkSessionDisposed = sessionInfo is not null;
        if (sdkSessionDisposed)
            SdkSessionDisposer?.Invoke(tagSessionService);

        projectService.Clear();
        tagSessionService.RefreshNoGateReason(projectService);
        AuditLogger.LogProjectClosed(closedPath, sdkSessionDisposed);

        return new CloseProjectResult(closedPath, sdkSessionDisposed);
    }
}
