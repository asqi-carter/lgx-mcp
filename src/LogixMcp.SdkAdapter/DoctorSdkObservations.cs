#if LOGIX_SDK
// ── DoctorSdkObservations — SDK-only facts for logix_doctor ─────────
//
// logix_doctor itself lives in the core assembly. The three observations that need Rockwell
// types are made here and registered on DoctorEnvironment.SdkObservations when the adapter
// loads (SdkAdapterInitializer.Initialize). Read-only: nothing here connects, mutates,
// launches or writes.
using System.Runtime.CompilerServices;

/// <summary>Production <see cref="IDoctorSdkObservations"/> backed by the loaded Logix Designer SDK.</summary>
internal sealed class DoctorSdkObservations : IDoctorSdkObservations
{
    /// <inheritdoc />
    public string? AssemblyVersion => SdkVersionOrNull();

    /// <inheritdoc />
    public DoctorSessionInfo? GetSession(TagSessionService tags)
    {
        var svc = TagRouteResolver.Backend(tags.Service);
        if (svc is not TypedSdkTagService typed) return null;

        var s = typed.Session;
        var routed = tags.RoutedService;
        return new DoctorSessionInfo(
            "sdk",
            s.IsOnline ? "online-controller" : "offline-project",
            s.IsOnline,
            s.IsCorrelated ? "correlated" : "uncorrelated",
            DateTimeOffset.UtcNow - typed.TypeSnapshotAt,
            s.DesignerVersion is { } v ? $"{v} ({v.Source})" : null,
            routed?.ActiveRoute,
            routed?.RouteReason,
            routed?.Mode.ToString().ToLowerInvariant(),
            routed?.IdentityCheck,
            Faulted: s.IsFaulted);
    }

    /// <inheritdoc />
    public SdkCapabilitiesReport? GetCapabilities(TagSessionService tags)
    {
        var svc = TagRouteResolver.Backend(tags.Service);
        return svc is TypedSdkTagService typed ? typed.Session.Capabilities.Report() : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DoctorZone>> ReadPendingZonesAsync(TagSessionService? tags, CancellationToken ct)
    {
        var session = tags is null ? null : LogixSdkTools.GetActiveSession(tags);
        if (session is null) throw new InvalidOperationException("there is no active SDK session");

        var result = await PendingEditsHandler.ExecuteAsync(null, null, session, ct).ConfigureAwait(false);
        if (result is PendingEditsError err) throw new InvalidOperationException(err.Error);
        var ok = (PendingEditsResult)result;
        return ok.Routines.Select(r => new DoctorZone(r.Program, r.Routine, r.ZoneState, r.PendingRungs.Select(p => p.Text).ToList())).ToList();
    }

    // NoInlining: an unloadable assembly fails when the enclosing method is JIT-compiled,
    // before any try block is entered, so the SDK touch lives in its own method behind a
    // try/catch.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? SdkVersionOrNull()
    {
        try
        {
            var asm = typeof(RockwellAutomation.LogixDesigner.LogixProject).Assembly;
            return asm.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return null;
        }
    }
}
#endif
