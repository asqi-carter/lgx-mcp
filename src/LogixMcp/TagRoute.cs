/// <summary>
/// Which channel carries tag reads and writes in an SDK session (<c>LGX_TAG_ROUTE</c>).
/// </summary>
public enum TagRouteMode
{
    /// <summary>CIP when a CIP connection to the same controller succeeds, otherwise the SDK.</summary>
    Auto,

    /// <summary>CIP only; <see cref="TagServiceErrorKind.TagRouteUnavailable"/> when CIP cannot connect.</summary>
    Cip,

    /// <summary>The SDK session only (the behaviour).</summary>
    Sdk,
}

/// <summary>Names of the two tag routes, as reported in results (<c>route</c>).</summary>
public static class TagRoutes
{
    /// <summary>Tag operations went over a CIP (EtherNet/IP) connection.</summary>
    public const string Cip = "cip";

    /// <summary>Tag operations went through the Logix Designer SDK session.</summary>
    public const string Sdk = "sdk";
}

/// <summary>Thrown by <see cref="TagRouteResolver.Current"/> when <c>LGX_TAG_ROUTE</c> holds an unknown value.</summary>
public sealed class TagRouteSettingInvalidException : Exception
{
    /// <summary>Creates the exception for the offending raw value.</summary>
    /// <param name="value">The value that was set, as written.</param>
    public TagRouteSettingInvalidException(string value)
        : base($"{TagRouteResolver.EnvVar} has the invalid value '{value}'. " +
               "Allowed values: auto (default), cip, sdk. Fix or unset it, then reconnect.") { }
}

/// <summary>
/// The single resolver of the <c>LGX_TAG_ROUTE</c> setting: <c>auto</c> (default, also
/// when unset or empty), <c>cip</c> or <c>sdk</c>, case-insensitive. An unknown value is an error,
/// never silently the default.
/// </summary>
public static class TagRouteResolver
{
    /// <summary>Name of the environment variable.</summary>
    public const string EnvVar = "LGX_TAG_ROUTE";

    // Per-async-flow replacement for the environment value, so tests can set the setting
    // without racing other tests that run in parallel in the same process.
    private static readonly AsyncLocal<Override?> TestOverride = new();

    private sealed record Override(string? Value);

    private sealed class Scope(Override? previous) : IDisposable
    {
        public void Dispose() => TestOverride.Value = previous;
    }

    /// <summary>
    /// Makes the resolver read <paramref name="value"/> instead of the environment in the current
    /// async flow until the returned scope is disposed. For tests.
    /// </summary>
    internal static IDisposable UseValueForTesting(string? value)
    {
        var previous = TestOverride.Value;
        TestOverride.Value = new Override(value);
        return new Scope(previous);
    }

    /// <summary>The raw <c>LGX_TAG_ROUTE</c> value, or <c>null</c> when unset.</summary>
    public static string? Raw() =>
        TestOverride.Value is { } o ? o.Value : Environment.GetEnvironmentVariable(EnvVar);

    /// <summary>The setting in force for this process.</summary>
    /// <exception cref="TagRouteSettingInvalidException">The variable holds an unknown value.</exception>
    public static TagRouteMode Current =>
        TryParse(Raw(), out var mode) ? mode : throw new TagRouteSettingInvalidException(Raw()!.Trim());

    /// <summary>Parses a raw setting value.</summary>
    /// <param name="raw">The raw value; <c>null</c>, empty or whitespace means <see cref="TagRouteMode.Auto"/>.</param>
    /// <param name="mode">The parsed mode when the method returns <c>true</c>.</param>
    /// <returns><c>true</c> when the value is empty or one of auto, cip, sdk.</returns>
    public static bool TryParse(string? raw, out TagRouteMode mode)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null or "" or "auto": mode = TagRouteMode.Auto; return true;
            case "cip":                mode = TagRouteMode.Cip;  return true;
            case "sdk":                mode = TagRouteMode.Sdk;  return true;
            default:                   mode = TagRouteMode.Auto; return false;
        }
    }

    /// <summary>
    /// Returns the concrete backend beneath the decorators a session adds
    /// (<see cref="GatedTagService"/>, <see cref="RoutedTagService"/>): the SDK service of a routed
    /// session, so callers that need <c>TypedSdkTagService</c> or the SDK-only interfaces still find it.
    /// </summary>
    /// <param name="service">The session's service, or <c>null</c>.</param>
    public static ITagService? Backend(ITagService? service)
    {
        if (service is GatedTagService gated) service = gated.InnerService;
        if (service is RoutedTagService routed) service = routed.SdkService;
        return service;
    }
}

/// <summary>
/// Collects the note a tag call leaves when its route changed mid-call (CIP lost, retried over the SDK).
/// A handler opens one with <see cref="Begin"/> before it calls the session and reads <see cref="Note"/>
/// when it builds its result; <see cref="RoutedTagService"/> reports into the one in the current async flow.
/// </summary>
public sealed class RouteNoteCapture : IDisposable
{
    private static readonly AsyncLocal<RouteNoteCapture?> Current = new();

    private readonly RouteNoteCapture? _previous;
    private readonly object _lock = new();
    private string? _note;

    private RouteNoteCapture(RouteNoteCapture? previous) => _previous = previous;

    /// <summary>The note reported during this call, or <c>null</c> when the route did not change.</summary>
    public string? Note { get { lock (_lock) return _note; } }

    /// <summary>Starts capturing for the current async flow; dispose to restore the outer capture.</summary>
    public static RouteNoteCapture Begin()
    {
        var capture = new RouteNoteCapture(Current.Value);
        Current.Value = capture;
        return capture;
    }

    /// <summary>Records <paramref name="note"/> on the current capture; a no-op when none is open.</summary>
    internal static void Report(string note)
    {
        if (Current.Value is not { } capture) return;
        lock (capture._lock) capture._note = capture._note is null ? note : capture._note + " " + note;
    }

    /// <inheritdoc/>
    public void Dispose() => Current.Value = _previous;
}
