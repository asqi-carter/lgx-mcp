// ── StudioConfirmWait — how long the Studio driver waits for a confirmation dialog ──
//
// SDK-free so it compiles in both lanes and is unit-testable. A Studio lifecycle verb
// (cancel | accept | test | untest | assemble) that follows the server's own publish or
// lifecycle call on the same routine within 30 s runs while Studio is still busy with
// that earlier change, so its confirmation dialog is slow: the driver is told to wait
// 15 s instead of its 10 s default (bench/studio-edits.ps1 -ConfirmTimeoutSeconds).
using System.Collections.Concurrent;

/// <summary>
/// Tracks the last publish / lifecycle call per routine and decides the
/// <c>-ConfirmTimeoutSeconds</c> to hand to <c>bench/studio-edits.ps1</c>.
/// </summary>
public sealed class StudioConfirmWait
{
    /// <summary>The driver's own default (<c>-ConfirmTimeoutSeconds</c>), in seconds.</summary>
    public const int DefaultSeconds = 10;

    /// <summary>The wait passed when the call follows a recent publish/lifecycle call on the same routine, in seconds.</summary>
    public const int AfterRecentCallSeconds = 15;

    /// <summary>How recent "recent" is: the earlier call must have finished within this many seconds.</summary>
    public const int RecentWindowSeconds = 30;

    /// <summary>The process-wide instance production code uses; tests build their own instead of touching it.</summary>
    public static StudioConfirmWait Shared { get; } = new();

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCall =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<DateTimeOffset> _now;

    /// <summary>Creates an independent tracker.</summary>
    /// <param name="now">Clock used for all timing; defaults to <see cref="DateTimeOffset.UtcNow"/>. Tests inject their own.</param>
    public StudioConfirmWait(Func<DateTimeOffset>? now = null) =>
        _now = now ?? (() => DateTimeOffset.UtcNow);

    private static string Key(string? program, string? routine) => $"{program}/{routine}";

    /// <summary>
    /// Records that the server just published to, or ran a lifecycle verb on, the routine.
    /// A blank program or routine is ignored.
    /// </summary>
    /// <param name="program">Program name.</param>
    /// <param name="routine">Routine name.</param>
    public void Record(string? program, string? routine)
    {
        if (string.IsNullOrWhiteSpace(program) || string.IsNullOrWhiteSpace(routine)) return;
        _lastCall[Key(program, routine)] = _now();
    }

    /// <summary>
    /// The <c>-ConfirmTimeoutSeconds</c> for a lifecycle verb on the routine right now:
    /// <see cref="AfterRecentCallSeconds"/> when <see cref="Record"/> was last called for the same
    /// routine at most <see cref="RecentWindowSeconds"/> seconds ago, else <see cref="DefaultSeconds"/>.
    /// </summary>
    /// <param name="program">Program name.</param>
    /// <param name="routine">Routine name.</param>
    /// <returns>Seconds the driver should wait for the confirmation dialog.</returns>
    public int SecondsFor(string? program, string? routine)
    {
        if (string.IsNullOrWhiteSpace(program) || string.IsNullOrWhiteSpace(routine))
            return DefaultSeconds;
        return _lastCall.TryGetValue(Key(program, routine), out var at) &&
               _now() - at <= TimeSpan.FromSeconds(RecentWindowSeconds)
            ? AfterRecentCallSeconds
            : DefaultSeconds;
    }
}
