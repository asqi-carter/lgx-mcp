/// <summary>
/// Decorator that sends tag reads and writes of an SDK session over CIP or over the SDK, as
/// <c>LGX_TAG_ROUTE</c> says. It sits beneath <see cref="GatedTagService"/>, so the one
/// write-gate policy governs both routes, and types still come from the SDK session.
///
/// <para>
/// The route is chosen on the first read or write and kept for the session:
/// <c>sdk</c> uses the SDK; <c>cip</c> uses CIP or throws
/// <see cref="TagServiceErrorKind.TagRouteUnavailable"/> (nothing is cached, so a later call retries
/// the connect); <c>auto</c> uses CIP when <c>cipConnector</c> returns a service and otherwise the SDK,
/// recording why in <see cref="RouteReason"/>.
/// </para>
///
/// <para>
/// Fallback: in <c>auto</c>, a CIP <see cref="TagServiceErrorKind.ConnectionFailed"/> during
/// a read or write is retried exactly once over the SDK, the session stays on <c>sdk</c>, and the call's
/// <see cref="RouteNoteCapture"/> receives a note. In <c>cip</c> the typed error is returned, no retry.
/// </para>
/// </summary>
public sealed class RoutedTagService : ITagService, IAliasAware
{
    private readonly ITagService _sdk;
    private readonly TagRouteMode _mode;
    private readonly Func<CancellationToken, Task<ITagService>>? _cipConnector;
    private readonly string? _noCipReason;
    private readonly Func<string?>? _connectedNote;
    private readonly Func<string?>? _identityCheck;
    private readonly ISessionTypeSource? _aliasTypes;
    private readonly AliasResolver? _aliases;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ITagService? _chosen;
    private ITagService? _cip;

    /// <summary>Creates the router.</summary>
    /// <param name="sdk">The SDK-backed service: the source of truth for types, and the <c>sdk</c> route.</param>
    /// <param name="mode">The <c>LGX_TAG_ROUTE</c> setting.</param>
    /// <param name="cipConnector">
    /// Opens and returns a CIP service for the same controller, or throws when it cannot.
    /// <c>null</c> when no CIP target could be derived (see <paramref name="noCipReason"/>).
    /// </param>
    /// <param name="noCipReason">Why there is no <paramref name="cipConnector"/>.</param>
    /// <param name="connectedNote">Evidence (e.g. the identity check) appended to <see cref="RouteReason"/> once CIP is chosen.</param>
    /// <param name="identityCheck">How the CIP controller was verified, for <see cref="IdentityCheck"/>.</param>
    /// <param name="aliasTypes">
    /// The declarations used to follow <c>AliasFor</c> on reads of alias tags; defaults to
    /// <paramref name="sdk"/> when it is an <see cref="ISessionTypeSource"/>. Without one, aliases are not followed.
    /// </param>
    public RoutedTagService(
        ITagService sdk,
        TagRouteMode mode,
        Func<CancellationToken, Task<ITagService>>? cipConnector,
        string? noCipReason = null,
        Func<string?>? connectedNote = null,
        Func<string?>? identityCheck = null,
        ISessionTypeSource? aliasTypes = null)
    {
        ArgumentNullException.ThrowIfNull(sdk);
        _sdk          = sdk;
        _mode         = mode;
        _cipConnector = cipConnector;
        _noCipReason  = noCipReason;
        _connectedNote = connectedNote;
        _identityCheck = identityCheck;
        _aliasTypes    = aliasTypes ?? sdk as ISessionTypeSource;
        _aliases       = _aliasTypes is null ? null : new AliasResolver(_aliasTypes);
        if (mode == TagRouteMode.Sdk)
        {
            _chosen     = sdk;
            ActiveRoute = TagRoutes.Sdk;
            RouteReason = $"{TagRouteResolver.EnvVar}=sdk";
        }
    }

    /// <summary>The declarations used to follow <c>AliasFor</c>, or <c>null</c> when aliases are not followed.</summary>
    public ISessionTypeSource? AliasTypes => _aliasTypes;

    /// <inheritdoc/>
    public AliasResolver? Aliases => _aliases;

    /// <summary>The SDK service this router wraps.</summary>
    public ITagService SdkService => _sdk;

    /// <summary>The setting this router applies.</summary>
    public TagRouteMode Mode => _mode;

    /// <summary><c>"cip"</c> or <c>"sdk"</c> once the route is chosen; <c>null</c> before the first operation.</summary>
    public string? ActiveRoute { get; private set; }

    /// <summary>How the CIP controller was verified (<c>serial</c>, <c>name</c> or <c>serial+name</c>); <c>null</c> unless the CIP route was chosen.</summary>
    public string? IdentityCheck => ActiveRoute == TagRoutes.Cip ? _identityCheck?.Invoke() : null;

    /// <summary>Why <see cref="ActiveRoute"/> is what it is (setting, or why CIP was not used); <c>null</c> before it is chosen.</summary>
    public string? RouteReason { get; private set; }

    /// <summary>
    /// Chooses the route now when it has not been chosen yet, and returns it.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the CIP connect.</param>
    /// <returns><c>"cip"</c> or <c>"sdk"</c>.</returns>
    /// <exception cref="TagServiceException">
    /// <see cref="TagServiceErrorKind.TagRouteUnavailable"/> in <c>cip</c> mode when CIP cannot connect.
    /// </exception>
    public async Task<string> ResolveRouteAsync(CancellationToken cancellationToken = default)
    {
        await SelectAsync(cancellationToken).ConfigureAwait(false);
        return ActiveRoute!;
    }

    private async Task<ITagService> SelectAsync(CancellationToken ct)
    {
        if (_chosen is { } done) return done;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_chosen is { } again) return again;

            string failure;
            if (_cipConnector is null)
            {
                failure = _noCipReason ?? "no CIP target could be derived for this session";
            }
            else
            {
                try
                {
                    _cip        = await _cipConnector(ct).ConfigureAwait(false);
                    _chosen     = _cip;
                    ActiveRoute = TagRoutes.Cip;
                    RouteReason = (_mode == TagRouteMode.Cip ? $"{TagRouteResolver.EnvVar}=cip" : "auto: CIP connected")
                                  + (_connectedNote?.Invoke() is { Length: > 0 } note ? $"; {note}" : "");
                    return _chosen;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failure = $"CIP connect failed: {ex.Message}";
                }
            }

            if (_mode == TagRouteMode.Cip)
                throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.TagRouteUnavailable,
                    $"{TagRouteResolver.EnvVar}=cip but the CIP route is unavailable: {failure}",
                    Hint: $"Fix the CIP connection to the controller, or set {TagRouteResolver.EnvVar}=auto or sdk and reconnect."));

            _chosen     = _sdk;
            ActiveRoute = TagRoutes.Sdk;
            RouteReason = $"auto: {failure}";
            return _sdk;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>Forwarded to the SDK service; the CIP service is opened by the connector.</remarks>
    public Task ConnectAsync(string gateway, string path, CancellationToken cancellationToken = default)
        => _sdk.ConnectAsync(gateway, path, cancellationToken);

    /// <inheritdoc/>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_cip is { } cip)
        {
            try { await cip.DisconnectAsync(cancellationToken).ConfigureAwait(false); }
            catch { /* best effort: the SDK session is closed regardless */ }
        }
        await _sdk.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// In <c>auto</c> mode a CIP connection loss is retried once over the SDK (see <see cref="FallbackToSdkAsync"/>);
    /// in <c>cip</c> mode the typed CIP error is returned unchanged.
    /// </remarks>
    public async Task<TagValue> ReadAsync(string tagName, CancellationToken cancellationToken = default)
    {
        // An alias declares no type of its own: follow AliasFor here, once, so the SDK and
        // CIP routes report the same type, value, aliasFor and resolvedFrom.
        var alias = _aliases?.Resolve(tagName);

        var target = await SelectAsync(cancellationToken).ConfigureAwait(false);
        TagValue result;
        try
        {
            result = await ReadVia(target, tagName, alias, cancellationToken).ConfigureAwait(false);
        }
        catch (TagServiceException ex) when (ShouldFallBack(target, ex))
        {
            await FallbackToSdkAsync("read", tagName, ex, target).ConfigureAwait(false);
            result = await ReadVia(_sdk, tagName, alias, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Reads <paramref name="tagName"/> from <paramref name="service"/> through the shared <see cref="AliasResolver"/>.
    /// Over CIP an alias is read by its own name first (the controller, and Echo, resolve aliases by name); if that
    /// read fails the resolved base path is read instead. A lost connection is not retried here: it goes to the route
    /// fallback. The SDK route reads the base path directly.
    /// </summary>
    private Task<TagValue> ReadVia(
        ITagService service, string tagName, AliasResolution? alias, CancellationToken ct)
        => AliasResolver.ReadAsync(alias, tagName, service.ReadAsync, ReferenceEquals(service, _cip), ct);

    /// <inheritdoc/>
    /// <remarks>
    /// A write retried over the SDK is never silent: the session route becomes <c>sdk</c> and the call's
    /// <see cref="RouteNoteCapture"/> carries a note saying the CIP write failed and was retried.
    /// </remarks>
    public async Task WriteAsync(string tagName, object value, CancellationToken cancellationToken = default)
    {
        var target = await SelectAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await target.WriteAsync(tagName, value, cancellationToken).ConfigureAwait(false);
        }
        catch (TagServiceException ex) when (ShouldFallBack(target, ex))
        {
            await FallbackToSdkAsync("write", tagName, ex, target).ConfigureAwait(false);
            await _sdk.WriteAsync(tagName, value, cancellationToken).ConfigureAwait(false);
        }
    }

    // A CIP connection loss in auto mode, on the CIP route, is the only failure that falls back.
    private bool ShouldFallBack(ITagService target, TagServiceException ex) =>
        _mode == TagRouteMode.Auto
        && ReferenceEquals(target, _cip)
        && ex.Error.Kind == TagServiceErrorKind.ConnectionFailed;

    /// <summary>
    /// Switches the session to the SDK route after a CIP connection loss and records a note on the
    /// current call. Called at most once per session: afterwards the route is <c>sdk</c> and a second
    /// failure is the SDK's own error, never retried. A new connect starts over.
    /// </summary>
    private async Task FallbackToSdkAsync(string operation, string tagName, TagServiceException ex, ITagService failedCip)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_chosen, failedCip))
            {
                _chosen     = _sdk;
                ActiveRoute = TagRoutes.Sdk;
                RouteReason = $"auto: CIP connection lost during {operation} of '{tagName}' ({ex.Error.Message}); fell back to the SDK";
            }
        }
        finally
        {
            _gate.Release();
        }

        var note = operation == "write"
            ? $"CIP connection lost during the write of '{tagName}' ({ex.Error.Message}); the write was retried once over the SDK (route \"sdk\"). " +
              "The CIP attempt did not complete, so check the observed value rather than assuming a single write."
            : $"CIP connection lost during the read of '{tagName}' ({ex.Error.Message}); the read was retried once over the SDK (route \"sdk\").";
        RouteNoteCapture.Report(note);

        try { await failedCip.DisconnectAsync().ConfigureAwait(false); }
        catch { /* best effort: the CIP connection is already lost */ }
    }

    /// <inheritdoc/>
    /// <remarks>Always the SDK service: it holds the project's tag declarations.</remarks>
    public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken cancellationToken = default)
        => _sdk.ListTagsAsync(cancellationToken);
}
