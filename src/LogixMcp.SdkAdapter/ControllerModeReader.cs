#if LOGIX_SDK
// ── ControllerModeReader — single source of truth for the live controller mode ─
using RockwellAutomation.LogixDesigner;

using ControllerMode = RockwellAutomation.LogixDesigner.LogixProject.ControllerMode;

/// <summary>
/// Reads and maps the live controller mode from a <see cref="LogixProjectSession"/>.
/// Shared by <c>logix_controller_mode</c> (via <see cref="TypedSdkTagService"/>)
/// and <c>logix_pending_edits</c> (<see cref="PendingEditsHandler"/>) so both
/// report the same value from the same SDK call.
/// </summary>
internal static class ControllerModeReader
{
    /// <summary>
    /// Maps the SDK enum to a display string.  Values that are not defined enum
    /// members (Echo controllers have been observed to send these) become
    /// <c>"Unknown"</c> rather than a bare integer.
    /// </summary>
    /// <param name="mode">The SDK-reported mode.</param>
    /// <returns>The mode name, or <c>"Unknown"</c>.</returns>
    internal static string MapMode(ControllerMode mode) =>
        Enum.IsDefined(mode) ? mode.ToString() : "Unknown";

    /// <summary>
    /// Reads the controller mode from <paramref name="session"/>.  SDK failures
    /// are converted to <c>"Unknown"</c> with a note naming the error; this
    /// method does not throw (other than for cancellation).
    /// </summary>
    /// <param name="session">The open SDK session.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mode reading, with a note when the mode is <c>"Unknown"</c>.</returns>
    internal static async Task<ControllerModeReading> ReadAsync(
        LogixProjectSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            var raw  = await session.ExecuteAsync(
                p => p.ReadControllerModeAsync(ct), ct).ConfigureAwait(false);
            var mode = MapMode(raw);
            return mode == "Unknown"
                ? new ControllerModeReading(mode,
                    $"SDK ReadControllerModeAsync returned an unrecognised mode value ({(int)raw})")
                : new ControllerModeReading(mode);
        }
        catch (TagServiceException ex)
        {
            return Unreadable($"{ex.Error.Kind}: {ex.Error.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unreadable($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ControllerModeReading Unreadable(string detail) =>
        new("Unknown", $"SDK ReadControllerModeAsync failed: {detail}");
}
#endif
