using L5Sharp.Core;

/// <summary>
/// What a read of an alias tag resolves to: the alias's declared <c>AliasFor</c>, the base path that is read in its
/// place, and the base's real type.
/// </summary>
/// <param name="AliasFor">The alias's own <c>AliasFor</c> text, as declared (reported as <c>aliasFor</c>).</param>
/// <param name="ResolvedFrom">The base path the alias resolves to (reported as <c>resolvedFrom</c>).</param>
/// <param name="DataType">The base's Logix type (for a bit, <c>BOOL</c>); <c>null</c> when the project cannot type it.</param>
/// <param name="IsModuleIo">True when the base is module I/O, typed from the module connection structures.</param>
public sealed record AliasResolution(string AliasFor, string ResolvedFrom, string? DataType, bool IsModuleIo = false);

/// <summary>
/// The one alias resolver every tag backend uses (CIP, SDK online via <see cref="RoutedTagService"/>, SDK offline and
/// L5X): it follows <c>AliasFor</c> in the open project, reports <c>resolvedFrom</c>, <c>aliasFor</c> and the base's
/// real type, and refuses a cycle, a missing target, a chain deeper than <see cref="AliasReadResolver.MaxDepth"/> or
/// module I/O the project cannot type with <see cref="TagServiceErrorKind.AliasUnresolved"/>.
/// </summary>
/// <remarks>
/// The declarations come from an <see cref="ISessionTypeSource"/>: the SDK session's own lookup, or a
/// <see cref="StaticTypeSource"/> over the L5X / export the gate was derived from. Module I/O aliases
/// (<c>Alias_ModIn -&gt; Local:1:I.Data</c>) resolve through <see cref="ModuleIoTypes"/> on every backend.
/// </remarks>
public sealed class AliasResolver
{
    private readonly ISessionTypeSource _source;

    /// <summary>Creates a resolver over <paramref name="source"/>.</summary>
    /// <param name="source">The declarations to follow <c>AliasFor</c> in.</param>
    public AliasResolver(ISessionTypeSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <summary>Creates a resolver over a fixed <paramref name="project"/>.</summary>
    /// <param name="project">The project holding the declarations.</param>
    public AliasResolver(L5X project) : this(new StaticTypeSource(project)) { }

    /// <summary>
    /// The low-level chain walk for <paramref name="tagPath"/> (no typing, never throws for an unresolved alias):
    /// <c>null</c> when it is not an alias. Used by the write gate, which reports its own wording.
    /// </summary>
    /// <param name="tagPath">The tag path.</param>
    /// <returns>The chain resolution, or <c>null</c> when the path is not an alias.</returns>
    public AliasReadResolver.Resolution? Walk(string tagPath)
        => _source.Lookup(project => AliasReadResolver.Resolve(project, tagPath));

    /// <summary>Resolves <paramref name="tagPath"/> when it names an alias; <c>null</c> when it does not.</summary>
    /// <param name="tagPath">The tag path being read.</param>
    /// <returns>The resolution, or <c>null</c> for a path that is not an alias.</returns>
    /// <exception cref="TagServiceException"><see cref="TagServiceErrorKind.AliasUnresolved"/> when the alias cannot be followed.</exception>
    public AliasResolution? Resolve(string tagPath)
    {
        var failure = (string?)null;
        var result = _source.Lookup(project =>
        {
            var walk = AliasReadResolver.Resolve(project, tagPath);
            if (walk is null) return null;

            if (walk.ModuleIoTarget is { } io)
            {
                if (CipTypeResolver.TryParsePath(io, out var ioSegments, out _)
                    && ModuleIoTypes.Resolve(project, ioSegments) is { IsResolved: true } typed)
                    return new AliasResolution(walk.AliasFor, io, typed.TypeName, IsModuleIo: true);
                failure = walk.Failure;
                return null;
            }

            if (!walk.IsResolved)
            {
                failure = walk.Failure;
                return null;
            }

            string? type = null;
            if (CipTypeResolver.TryParsePath(walk.Target!, out var segments, out _)
                && CipTypeResolver.ResolveFromProject(project, segments) is { IsResolved: true } resolved)
                type = resolved.TypeName;
            return new AliasResolution(walk.AliasFor, walk.Target!, type);
        });

        if (result is null && failure is not null)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.AliasUnresolved,
                $"Cannot read '{tagPath}': {failure}.",
                tagPath,
                Hint: "fix the alias's AliasFor in the project, or read its target tag directly"));
        return result;
    }

    /// <summary>
    /// Reads <paramref name="tagName"/>, following it to its base when it is an alias, and stamps the result with
    /// <c>aliasFor</c>, <c>resolvedFrom</c> and the real type.
    /// </summary>
    /// <param name="tagName">The tag path being read.</param>
    /// <param name="read">The backend's plain read of a path.</param>
    /// <param name="aliasNameFirst">
    /// True for CIP: the controller resolves aliases by name, so the alias name is read first and the base path only
    /// when that read fails (other than by a lost connection, which the caller's route fallback handles).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The value; for an alias its <see cref="TagValue.TagName"/> stays the alias name.</returns>
    public async Task<TagValue> ReadAsync(
        string tagName, Func<string, CancellationToken, Task<TagValue>> read, bool aliasNameFirst,
        CancellationToken cancellationToken = default)
        => await ReadAsync(Resolve(tagName), tagName, read, aliasNameFirst, cancellationToken).ConfigureAwait(false);

    /// <summary>As <see cref="ReadAsync(string, Func{string, CancellationToken, Task{TagValue}}, bool, CancellationToken)"/>, for an already resolved alias.</summary>
    /// <param name="alias">The resolution of <paramref name="tagName"/>, or <c>null</c> when it is not an alias.</param>
    /// <param name="tagName">The tag path being read.</param>
    /// <param name="read">The backend's plain read of a path.</param>
    /// <param name="aliasNameFirst">Whether to try the alias name before the base path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The value.</returns>
    public static async Task<TagValue> ReadAsync(
        AliasResolution? alias, string tagName, Func<string, CancellationToken, Task<TagValue>> read,
        bool aliasNameFirst, CancellationToken cancellationToken = default)
    {
        if (alias is null)
            return await read(tagName, cancellationToken).ConfigureAwait(false);

        TagValue? value = null;
        if (aliasNameFirst)
        {
            try { value = await read(tagName, cancellationToken).ConfigureAwait(false); }
            catch (TagServiceException ex) when (ex.Error.Kind != TagServiceErrorKind.ConnectionFailed)
            {
                // fall through to the resolved base path
            }
        }

        value ??= await read(alias.ResolvedFrom, cancellationToken).ConfigureAwait(false);
        return value with
        {
            TagName      = tagName,
            DataType     = value.DataType is null or "NULL" ? alias.DataType : value.DataType,
            AliasFor     = alias.AliasFor,
            ResolvedFrom = alias.ResolvedFrom,
        };
    }
}

/// <summary>A tag service that follows aliases through a shared <see cref="AliasResolver"/>.</summary>
public interface IAliasAware
{
    /// <summary>The resolver this service reads and gates aliases with; <c>null</c> when it has no declarations to follow.</summary>
    AliasResolver? Aliases { get; }
}
