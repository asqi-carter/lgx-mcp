#if LOGIX_SDK
/// <summary>
/// Bridges <c>CloseProjectHandler</c>'s SDK-session seams
/// (<c>SdkSessionInspector</c>/<c>SdkSessionDisposer</c>) to the adapter's
/// <see cref="LogixProjectSession"/>/<see cref="LogixSdkTools"/> internals.
///
/// <para>
/// Registered by <c>SdkAdapterInitializer.Initialize</c> when the
/// <c>LogixMcp.SdkAdapter</c> assembly is loaded. Kept as a small, dedicated
/// static class (rather than folding this logic into <see cref="LogixSdkTools"/>)
/// so the seam wiring is easy to find alongside the other adapter registrations
/// in <c>SdkConnectFactory.cs</c>.
/// </para>
/// </summary>
internal static class CloseProjectSdkBridge
{
    /// <summary>
    /// Implements <c>CloseProjectHandler.SdkSessionInspector</c>: reports the
    /// pending-batch state of the active file-backed SDK session, or
    /// <c>null</c> when no such session is active.
    /// </summary>
    /// <param name="tagSessionService">The tag-session singleton to inspect.</param>
    internal static SdkSessionCloseInfo? Inspect(TagSessionService tagSessionService)
    {
        var session = LogixSdkTools.GetActiveSession(tagSessionService);
        if (session is null)
            return null;

        var bufferedRungs = session.StagedRungBuffer.Values.Sum(b => b.Count);
        var routines = session.StagedRungBuffer
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => DiffAgainstControllerHandler.RoutineKeyFromXPath(kv.Key.RoutineXPath) ?? kv.Key.RoutineXPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SdkSessionCloseInfo(
            StagedTagCount:    session.StagedTagBuffer.Count,
            HasStagedRungEdit: session.HasStagedRungEdit,
            Faulted:           session.IsFaulted,
            StagedRungCount:   bufferedRungs > 0 ? bufferedRungs : session.StagedRungCount,
            StagedRoutines:    routines);
    }

    /// <summary>
    /// Implements <c>CloseProjectHandler.SdkSessionDisposer</c>: disposes the
    /// active file-backed SDK session (releasing its gRPC handle and deleting
    /// its private working copy) and detaches it from
    /// <paramref name="tagSessionService"/>. Never issues any controller
    /// communication. A no-op when no SDK session is active.
    /// </summary>
    /// <param name="tagSessionService">The tag-session singleton to update.</param>
    internal static void Dispose(TagSessionService tagSessionService)
    {
        var session = LogixSdkTools.GetActiveSession(tagSessionService);
        session?.Dispose();
        tagSessionService.Detach();
    }
}
#endif
