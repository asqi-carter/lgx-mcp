// ── ControllerModeService ─────────────────────────────────────────────────────

/// <summary>
/// Singleton service that tracks the current and requested controller mode for
/// the active tag session.
///
/// <para>
/// Mode state is reset each time a session is established via
/// <see cref="ConnectHandler.ExecuteAsync"/>:
/// <list type="bullet">
///   <item>Virtual sessions start in <c>"Program"</c> mode.</item>
///   <item>Live CIP and SDK sessions start in <c>"Unknown"</c> mode (the actual
///   mode on the physical controller cannot be read without a separate CIP query
///   that is outside the scope of this service).</item>
/// </list>
/// For virtual sessions, <see cref="ControllerMode"/> mirrors the last
/// <see cref="RequestedMode"/> immediately — there is no execution lag.
/// For live sessions the service tracks what was requested but cannot verify
/// the controller's actual mode.
/// </para>
/// </summary>
public class ControllerModeService
{
    private string _controllerMode = "Unknown";
    private string _requestedMode  = "Unknown";

    /// <summary>
    /// Gets the most recently observed controller mode.
    /// For virtual sessions this always equals <see cref="RequestedMode"/>.
    /// For live sessions this reflects the last value set by
    /// <see cref="SetRequestedMode"/>; the actual PLC state is not queried.
    /// </summary>
    public string ControllerMode => _controllerMode;

    /// <summary>
    /// Gets the mode that was last requested via <see cref="SetRequestedMode"/>.
    /// Equals <see cref="ControllerMode"/> immediately after any successful set
    /// on a virtual session.
    /// </summary>
    public string RequestedMode => _requestedMode;

    /// <summary>
    /// Resets both <see cref="ControllerMode"/> and <see cref="RequestedMode"/>
    /// to <paramref name="initialMode"/>.  Called by <see cref="ConnectHandler"/>
    /// each time a new session is established.
    /// </summary>
    /// <param name="initialMode">
    /// Starting mode for the new session (<c>"Program"</c> for virtual sessions;
    /// <c>"Unknown"</c> for live CIP/SDK sessions).
    /// </param>
    public void Reset(string initialMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialMode);
        _controllerMode = initialMode;
        _requestedMode  = initialMode;
    }

    /// <summary>
    /// Records a mode change request and (for virtual sessions) immediately
    /// advances <see cref="ControllerMode"/> to the same value.
    /// </summary>
    /// <param name="mode">
    /// The requested mode string — one of <c>"Program"</c>, <c>"Run"</c>, or
    /// <c>"Test"</c>.  Validation is the caller's responsibility.
    /// </param>
    public void SetRequestedMode(string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        _requestedMode  = mode;
        _controllerMode = mode;   // virtual or in-memory sessions: mode is immediate
    }
}
