using System.Text.Json.Serialization;

// ── Result / error types ──────────────────────────────────────────────────────

/// <summary>
/// A single tag value change observed during one poll-and-diff cycle.
/// </summary>
/// <param name="Tag">Name of the tag whose value changed.</param>
/// <param name="Previous">
/// Value from the previous poll cycle, or <c>null</c> when this is the
/// first cycle and there is no prior baseline.
/// </param>
/// <param name="Current">Current value observed in this cycle.</param>
/// <param name="DataType">
/// Data-type string as reported by the driver, or <c>null</c> when unavailable.
/// </param>
public record TagChange(
    [property: JsonPropertyName("tag")]      string  Tag,
    [property: JsonPropertyName("previous")] object? Previous,
    [property: JsonPropertyName("current")]  object  Current,
    [property: JsonPropertyName("dataType")] string? DataType);

/// <summary>
/// Result of a single poll-and-diff cycle.
/// </summary>
/// <param name="Cycle">One-based cycle number (1 = first poll, 2 = second, …).</param>
/// <param name="Changes">
/// Tags whose values changed relative to the previous cycle.
/// On cycle 1 (the baseline poll), all successfully-read tags appear here with
/// <see cref="TagChange.Previous"/> = <c>null</c>.
/// On subsequent cycles, only tags whose value differs from the prior cycle
/// are included.
/// </param>
/// <param name="Errors">
/// Tags that could not be read in this cycle, each with its own error; omitted when every tag read.
/// A failing path never hides the values of the other paths.
/// </param>
public record PollCycle(
    [property: JsonPropertyName("cycle")]   int                      Cycle,
    [property: JsonPropertyName("changes")] IReadOnlyList<TagChange> Changes,
    [property: JsonPropertyName("errors"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<TagReadError>? Errors = null);

/// <summary>
/// The error for one path of a poll cycle that could not be read.
/// </summary>
/// <param name="Tag">The path that failed.</param>
/// <param name="Error">Human-readable error description.</param>
/// <param name="Kind">Machine-readable error kind (e.g. <c>"TypeUnknown"</c>).</param>
public record TagReadError(
    [property: JsonPropertyName("tag")]   string  Tag,
    [property: JsonPropertyName("error")] string  Error,
    [property: JsonPropertyName("kind")]  string? Kind);

/// <summary>
/// Successful response from <see cref="PollTagsHandler.ExecuteAsync"/>.
/// </summary>
/// <param name="Tags">The tag names that were polled.</param>
/// <param name="Count">Total number of poll cycles executed.</param>
/// <param name="IntervalMs">Milliseconds between consecutive cycles.</param>
/// <param name="Cycles">Per-cycle change reports.</param>
/// <param name="UntrustedContent">
/// <c>["value"]</c> when any previous/current STRING value was wrapped; omitted otherwise.
/// </param>
/// <param name="Notice">Untrusted-content notice; present exactly when <paramref name="UntrustedContent"/> is.</param>
/// <param name="Route"><c>"cip"</c> or <c>"sdk"</c>: the tag route this call used (<see cref="RoutedTagService.ActiveRoute"/>); omitted when the session is not routed.</param>
/// <param name="RouteNote">Present when CIP was lost during the poll and it continued over the SDK; says so. Omitted otherwise.</param>
public record PollResult(
    [property: JsonPropertyName("tags")]       string[]                 Tags,
    [property: JsonPropertyName("count")]      int                      Count,
    [property: JsonPropertyName("intervalMs")] int                      IntervalMs,
    [property: JsonPropertyName("cycles")]     IReadOnlyList<PollCycle> Cycles,
    [property: JsonPropertyName("untrustedContent"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<string>? UntrustedContent = null,
    [property: JsonPropertyName("notice"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Notice = null,
    [property: JsonPropertyName("route"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Route = null,
    [property: JsonPropertyName("routeNote"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? RouteNote = null);

/// <summary>
/// Error response from <see cref="PollTagsHandler.ExecuteAsync"/>.
/// </summary>
/// <param name="Error">Human-readable error description.</param>
/// <param name="Kind">
/// Machine-readable error kind string (e.g. <c>"TagNotFound"</c>), or <c>null</c>.
/// </param>
/// <param name="Tag">Tag name involved in the error, or <c>null</c>.</param>
/// <param name="Hint">
/// Optional imperative remediation text.  Present only when an unambiguous
/// corrective action is known; <c>null</c> otherwise.
/// </param>
/// <param name="DocsAnchor">
/// Optional stable slug referencing the relevant docs/ section.
/// <c>null</c> when no targeted documentation exists for this error.
/// </param>
public record PollError(
    [property: JsonPropertyName("error")]       string  Error,
    [property: JsonPropertyName("kind")]        string? Kind       = null,
    [property: JsonPropertyName("tag")]         string? Tag        = null,
    [property: JsonPropertyName("hint")]        string? Hint       = null,
    [property: JsonPropertyName("docs_anchor")] string? DocsAnchor = null);

// ── PollTagsHandler ───────────────────────────────────────────────────────────

/// <summary>
/// Pure handler for the <c>logix_poll</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
///
/// <para>
/// Performs <paramref name="count"/> explicit poll cycles against the active
/// <see cref="TagSessionService"/> session.  Between each consecutive pair of
/// cycles the handler waits <paramref name="intervalMs"/> milliseconds, then
/// reads all requested tags again and reports only the values that changed.
/// </para>
///
/// <para>
/// This is an explicit poll-and-diff operation.  The handler does NOT establish
/// any persistent subscription, push notification, or streaming channel — the
/// controller is queried on demand each cycle and the connection is idle between
/// <see cref="ExecuteAsync"/> calls.
/// </para>
/// </summary>
public static class PollTagsHandler
{
    /// <summary>Maximum number of poll cycles per call.</summary>
    public const int MaxCount = 1000;

    /// <summary>Maximum number of tags per call.</summary>
    public const int MaxTags = 64;

    /// <summary>Minimum milliseconds between consecutive cycles.</summary>
    public const int MinIntervalMs = 100;

    private static PollError LimitError(string message) =>
        new(message, Kind: ErrorKinds.InvalidValue);

    /// <summary>
    /// Runs <paramref name="count"/> poll-and-diff cycles against the active session.
    /// </summary>
    /// <param name="tags">
    /// Tag names to poll in each cycle.  Must be non-empty and at most <see cref="MaxTags"/> long.
    /// </param>
    /// <param name="count">
    /// Number of poll cycles to execute.  Minimum 1, maximum <see cref="MaxCount"/>; defaults to <c>3</c>.
    /// </param>
    /// <param name="intervalMs">
    /// Milliseconds to wait between consecutive cycles.  Minimum
    /// <see cref="MinIntervalMs"/>; defaults to <c>1000</c>.
    /// </param>
    /// <param name="session">The active tag session singleton.</param>
    /// <param name="ct">Cancellation token used both for delay waits and reads.</param>
    /// <returns>
    /// A <see cref="PollResult"/> containing one <see cref="PollCycle"/> per
    /// executed cycle, or a <see cref="PollError"/> on any failure.
    /// Never throws.
    /// </returns>
    public static async Task<object> ExecuteAsync(
        string[]          tags,
        int               count,
        int               intervalMs,
        TagSessionService session,
        CancellationToken ct = default)
    {
        if (!session.IsConnected)
            return new PollError("No active session. Call logix_connect first.", Kind: ErrorKinds.NoActiveSession);

        if (tags is null || tags.Length == 0)
            return new PollError("At least one tag name must be supplied.");

        // Enforce limits so a poll cannot hammer a live controller's comms.
        if (tags.Length > MaxTags)
            return LimitError($"Too many tags: {tags.Length} supplied; at most {MaxTags} tags per call.");
        if (count > MaxCount)
            return LimitError($"count {count} is too large; the maximum is {MaxCount}.");
        if (intervalMs < MinIntervalMs)
            return LimitError($"intervalMs {intervalMs} is too small; the minimum is {MinIntervalMs} ms.");

        // Clamp remaining input to a sensible bound.
        if (count < 1) count = 1;

        // Refuse malformed names up front, before any cycle touches the backend.
        foreach (var name in tags)
        {
            if (!TagNameValidator.IsValid(name))
            {
                try { TagNameValidator.ThrowIfInvalid(name, "Poll"); }
                catch (TagServiceException ex)
                {
                    return new PollError(
                        $"Read failed for tag '{name}': {ex.Error.Message}",
                        Kind: ex.Error.Kind.ToString(),
                        Tag:  name);
                }
            }
        }

        using var routeNote = RouteNoteCapture.Begin();
        var svc    = session.Service!;
        var cycles = new List<PollCycle>(count);
        bool anyWrapped = false;

        // previous: tag name → last observed value (used for diff).
        var previous = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i <= count; i++)
        {
            // Wait between cycles (no delay before the first cycle).
            if (i > 1 && intervalMs > 0)
            {
                try
                {
                    await Task.Delay(intervalMs, ct);
                }
                catch (OperationCanceledException)
                {
                    return new PollError("Poll cancelled during inter-cycle delay.");
                }
            }

            // Read all requested tags for this cycle.
            var changes = new List<TagChange>();
            var errors  = new List<TagReadError>();

            foreach (var tag in tags)
            {
                TagValue result;

                try
                {
                    result = await svc.ReadAsync(tag, ct);
                }
                catch (TagServiceException ex)
                {
                    // A lost connection or route fails the whole call; any other failure is this path's alone.
                    if (ex.Error.Kind is TagServiceErrorKind.ConnectionFailed or TagServiceErrorKind.TagRouteUnavailable)
                        return new PollError(
                            $"Read failed for tag '{tag}': {ex.Error.Message}",
                            Kind: ex.Error.Kind.ToString(),
                            Tag:  ex.Error.TagName ?? tag);
                    errors.Add(new TagReadError(
                        ex.Error.TagName ?? tag, $"Read failed for tag '{tag}': {ex.Error.Message}", ex.Error.Kind.ToString()));
                    continue;
                }
                catch (OperationCanceledException ex)
                {
                    return new PollError(
                        $"Read failed for tag '{tag}': {ex.Message}",
                        Kind: TagServiceErrorKind.Unknown.ToString(),
                        Tag:  tag);
                }
                catch (Exception ex)
                {
                    errors.Add(new TagReadError(
                        tag, $"Read failed for tag '{tag}': {ex.Message}", TagServiceErrorKind.Unknown.ToString()));
                    continue;
                }

                // Cycle 1 is always reported as a change (baseline with no previous).
                bool isFirstCycle = i == 1;
                bool valueChanged = !isFirstCycle &&
                    (!previous.TryGetValue(result.TagName, out var prev) ||
                     !Equals(prev, result.Value));

                if (isFirstCycle || valueChanged)
                {
                    previous.TryGetValue(result.TagName, out var oldValue);
                    bool wrap = result.Value is string && UntrustedWrapper.IsStringFamily(result.DataType);
                    anyWrapped |= wrap;
                    changes.Add(new TagChange(
                        Tag:      result.TagName,
                        Previous: isFirstCycle ? null : UntrustedWrapper.WrapIfString(oldValue, result.DataType),
                        Current:  UntrustedWrapper.WrapIfString(result.Value, result.DataType)!,
                        DataType: result.DataType));
                }

                // Update the baseline for the next cycle regardless of whether
                // we emitted a change entry.
                previous[result.TagName] = result.Value;
            }

            // Nothing readable on the baseline cycle: the call itself failed, not one path of it.
            if (i == 1 && errors.Count == tags.Length)
            {
                var first = errors[0];
                return new PollError(first.Error, Kind: first.Kind, Tag: first.Tag);
            }

            cycles.Add(new PollCycle(i, changes, errors.Count > 0 ? errors : null));
        }

        return anyWrapped
            ? new PollResult(tags, count, intervalMs, cycles,
                             UntrustedWrapper.ValueKinds, UntrustedWrapper.TagValueNotice, session.ActiveRoute, routeNote.Note)
            : new PollResult(tags, count, intervalMs, cycles, Route: session.ActiveRoute, RouteNote: routeNote.Note);
    }
}
