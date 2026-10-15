// ── ZoneLedger — which pending edit zones this server published (task 2) ──

/// <summary>
/// Record of the pending edit zones this server published to one controller session. The controller publish path
/// records <c>(program, routines, time)</c> for every publish that left a pending zone; accept, assemble and cancel
/// clear the program's entry; a connect clears every ledger (<see cref="ClearAll"/>). <c>accept</c>, <c>assemble</c>
/// and <c>test</c> compare the live zone with it and refuse a zone the ledger does not know
/// (<see cref="ForeignPendingZoneKind"/>): another session or Studio published those edits.
/// <para>
/// There is one ledger per controller session, process-wide: <see cref="For"/> returns it.
/// </para>
/// </summary>
public sealed class ZoneLedger
{
    /// <summary>The <c>kind</c> of the refusal for a pending zone this server did not publish.</summary>
    public const string ForeignPendingZoneKind = "ForeignPendingZone";

    /// <summary>One recorded zone: the routines the server published into a program and when it last did.</summary>
    /// <param name="Program">Program name.</param>
    /// <param name="Routines">Routine names the server published pending edits into (case-insensitive set).</param>
    /// <param name="At">When the latest publish into the program was recorded.</param>
    public sealed record ZoneEntry(string Program, IReadOnlySet<string> Routines, DateTimeOffset At);

    private static readonly object RegistryGate = new();

    /// <summary>The ledger used when a caller names no session (direct handler use).</summary>
    private static readonly ZoneLedger DefaultLedger = new();

    private static readonly Dictionary<object, ZoneLedger> Registry = new(ReferenceEqualityComparer.Instance);

    /// <summary>Test hook: a ledger every <see cref="For"/> call in this async flow returns, isolating a test from other tests' connects.</summary>
    private static readonly AsyncLocal<ZoneLedger?> Override = new();

    /// <summary>Test hook: routes <see cref="For"/> to <c>value</c> for the current async flow; <c>null</c> removes the override.</summary>
    internal static ZoneLedger? TestOverride
    {
        set => Override.Value = value;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, ZoneEntry> _programs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The process-wide ledger of one controller session.</summary>
    /// <param name="session">The controller session (compared by reference); <c>null</c> for the default ledger.</param>
    /// <returns>The session's ledger, created on first use.</returns>
    public static ZoneLedger For(object? session)
    {
        if (Override.Value is { } overridden) return overridden;
        if (session is null) return DefaultLedger;
        lock (RegistryGate)
        {
            if (!Registry.TryGetValue(session, out var ledger))
                Registry[session] = ledger = new ZoneLedger();
            return ledger;
        }
    }

    /// <summary>Empties every session's ledger (a connect starts from an empty record).</summary>
    public static void ClearAll()
    {
        lock (RegistryGate)
        {
            foreach (var ledger in Registry.Values) ledger.Clear();
            Registry.Clear();
        }
        DefaultLedger.Clear();
    }

    /// <summary>Number of programs with a recorded zone in this ledger.</summary>
    public int Count
    {
        get { lock (_gate) return _programs.Count; }
    }

    /// <summary>
    /// Records that the server published pending edits into <paramref name="routines"/> of <paramref name="program"/>.
    /// Routines of an earlier publish into the same program stay recorded.
    /// </summary>
    /// <param name="program">Program name.</param>
    /// <param name="routines">Routine names that hold the published edits.</param>
    /// <param name="at">Publish time; <c>null</c> for now.</param>
    public void Record(string program, IEnumerable<string> routines, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(program)) return;
        var names = new HashSet<string>(
            routines.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (_programs.TryGetValue(program.Trim(), out var existing))
                names.UnionWith(existing.Routines);
            _programs[program.Trim()] = new ZoneEntry(program.Trim(), names, at ?? DateTimeOffset.UtcNow);
        }
    }

    /// <summary>The routines of <paramref name="pendingRoutines"/> this ledger holds no publish for in <paramref name="program"/>.</summary>
    /// <param name="program">Program name.</param>
    /// <param name="pendingRoutines">Routines of the live zone (those with pending edits).</param>
    /// <returns>The unknown routines in their given order; empty when the ledger knows the whole zone.</returns>
    public IReadOnlyList<string> Unknown(string program, IEnumerable<string> pendingRoutines)
    {
        var entry = Get(program);
        return pendingRoutines.Where(r => entry is null || !entry.Routines.Contains(r.Trim())).ToList();
    }

    /// <summary>The recorded entry for <paramref name="program"/>, or <c>null</c> when the ledger holds none.</summary>
    /// <param name="program">Program name.</param>
    public ZoneEntry? Get(string program)
    {
        lock (_gate)
            return _programs.TryGetValue((program ?? string.Empty).Trim(), out var entry) ? entry : null;
    }

    /// <summary>Forgets the program's entry (accept, assemble and cancel resolved the zone).</summary>
    /// <param name="program">Program name.</param>
    public void ClearProgram(string program)
    {
        lock (_gate) _programs.Remove((program ?? string.Empty).Trim());
    }

    /// <summary>Forgets every entry of this ledger.</summary>
    public void Clear()
    {
        lock (_gate) _programs.Clear();
    }
}
