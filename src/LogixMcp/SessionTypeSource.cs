using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

/// <summary>
/// The session's declared-type lookup. The SDK service owns it; the SDK
/// route and the CIP route both resolve types through the same instance, so a tag the lookup learns
/// about (a refresh after a publish) is typed on both routes, and neither route keeps a private copy.
/// </summary>
/// <remarks>
/// Implementations serialise <see cref="Lookup{TResult}"/> against <see cref="RefreshAsync"/>. A lookup
/// reads the project's own tag and data-type collections; it never uses L5Sharp's project-wide index,
/// which tokenises every rung and throws when one is unparsable.
/// </remarks>
public interface ISessionTypeSource
{
    /// <summary>Runs <paramref name="lookup"/> over the type project while holding the lookup's lock.</summary>
    /// <typeparam name="TResult">Result type.</typeparam>
    /// <param name="lookup">A synchronous read; it must not retain the project after it returns.</param>
    /// <returns>Whatever <paramref name="lookup"/> returns.</returns>
    TResult Lookup<TResult>(Func<L5X, TResult> lookup);

    /// <summary>
    /// Makes the lookup know the root tag of <paramref name="tagPath"/> (e.g. one a publish just created):
    /// a partial re-export of that tag from the SDK session, added to the lookup. Does nothing when the
    /// tag is already known. Never throws except for cancellation.
    /// </summary>
    /// <param name="tagPath">A tag path; <c>Program:P.Tag.Member</c> and <c>Tag[2].Member</c> refresh <c>Tag</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the root tag is now in the lookup.</returns>
    Task<bool> RefreshAsync(string tagPath, CancellationToken cancellationToken = default);
}

/// <summary>An <see cref="ISessionTypeSource"/> over a fixed project that cannot refresh (no SDK session to ask).</summary>
public sealed class StaticTypeSource : ISessionTypeSource
{
    private readonly L5X _project;
    private readonly object _lock = new();

    /// <summary>Creates the source.</summary>
    /// <param name="project">The project holding the declared types.</param>
    public StaticTypeSource(L5X project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
    }

    /// <inheritdoc/>
    public TResult Lookup<TResult>(Func<L5X, TResult> lookup)
    {
        lock (_lock) return lookup(_project);
    }

    /// <inheritdoc/>
    public Task<bool> RefreshAsync(string tagPath, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

/// <summary>Helpers over <see cref="ISessionTypeSource"/>.</summary>
public static class SessionTypeSourceExtensions
{
    /// <summary>
    /// Refreshes the lookup for every name in <paramref name="tagNames"/>, one partial re-export each,
    /// e.g. for the tags a publish created.
    /// </summary>
    /// <param name="source">The lookup to refresh.</param>
    /// <param name="tagNames">Tag names (or paths) to make known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The names that are now in the lookup.</returns>
    public static async Task<IReadOnlyList<string>> RefreshTagsAsync(
        this ISessionTypeSource source, IEnumerable<string> tagNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tagNames);
        var known = new List<string>();
        foreach (var name in tagNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (await source.RefreshAsync(name, cancellationToken).ConfigureAwait(false))
                known.Add(name);
        return known;
    }
}
