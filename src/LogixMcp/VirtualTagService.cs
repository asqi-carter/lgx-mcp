/// <summary>
/// Fully in-memory implementation of <see cref="ITagService"/> for use in
/// unit and acceptance tests.  Requires no I/O, network, or PLC connection.
///
/// <para>
/// Seed the tag store at construction time by supplying a dictionary whose keys
/// are tag names and whose values are <c>(object Value, string? DataType)</c>
/// tuples.  The dictionary is copied into an internal concurrent store, so the
/// caller's dictionary is not mutated and repeated test runs stay independent.
/// </para>
///
/// <para>
/// <see cref="ConnectAsync"/> and <see cref="DisconnectAsync"/> are intentional
/// no-ops — they return <see cref="Task.CompletedTask"/> immediately and never
/// throw.  This matches the behaviour expected of a lightweight test double.
/// </para>
/// </summary>
public sealed class VirtualTagService : ITagService
{
    // Internal mutable store: tag name → (value, dataType).
    // Dictionary<> is used deliberately (single-threaded test context assumed).
    private readonly Dictionary<string, (object Value, string? DataType)> _store;

    /// <summary>
    /// Initialises a new <see cref="VirtualTagService"/> with the supplied seed data.
    /// </summary>
    /// <param name="seed">
    /// Initial tag store.  Keys are tag names (matched case-insensitively);
    /// values carry the boxed tag value and an optional data-type string.
    /// Pass an empty dictionary (or omit to use the parameterless constructor)
    /// for an empty store.
    /// </param>
    public VirtualTagService(IReadOnlyDictionary<string, (object Value, string? DataType)> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        // Copy into a case-insensitive dictionary so look-ups are PLC-name-safe.
        _store = new Dictionary<string, (object Value, string? DataType)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var kv in seed)
            _store[kv.Key] = kv.Value;
    }

    /// <summary>
    /// Initialises an empty <see cref="VirtualTagService"/> with no pre-seeded tags.
    /// </summary>
    public VirtualTagService()
        : this(new Dictionary<string, (object Value, string? DataType)>()) { }

    // ── ITagService ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>No-op in the virtual service; always completes successfully.</remarks>
    public Task ConnectAsync(string gateway, string path,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>No-op in the virtual service; always completes successfully.</remarks>
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc/>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.TagNotFound"/> when
    /// <paramref name="tagName"/> is not present in the in-memory store.
    /// </exception>
    public Task<TagValue> ReadAsync(string tagName,
        CancellationToken cancellationToken = default)
    {
        if (!_store.TryGetValue(tagName, out var entry))
            throw new TagServiceException(
                new TagServiceError(
                    TagServiceErrorKind.TagNotFound,
                    $"Tag '{tagName}' not found in the virtual tag store.",
                    tagName));

        return Task.FromResult(new TagValue(tagName, entry.Value, entry.DataType));
    }

    /// <inheritdoc/>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.TagNotFound"/> when
    /// <paramref name="tagName"/> is not present in the in-memory store.
    /// </exception>
    public Task WriteAsync(string tagName, object value,
        CancellationToken cancellationToken = default)
    {
        WriteTypeRule.RejectNonFinite(tagName, value, "VirtualTagService");

        if (!_store.TryGetValue(tagName, out var existing))
            throw new TagServiceException(
                new TagServiceError(
                    TagServiceErrorKind.TagNotFound,
                    $"Tag '{tagName}' not found in the virtual tag store.",
                    tagName));

        // Preserve the original DataType; update the stored value.
        _store[tagName] = (value, existing.DataType);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <returns>
    /// A read-only snapshot of all tag names and their data types currently in
    /// the in-memory store, in an unspecified order.
    /// </returns>
    public Task<IReadOnlyList<TagInfo>> ListTagsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TagInfo> list =
            _store.Select(kv => new TagInfo(kv.Key, kv.Value.DataType))
                  .ToList();

        return Task.FromResult(list);
    }
}
