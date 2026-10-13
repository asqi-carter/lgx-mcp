using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogixMcp.Tests;

/// <summary>
/// Verifies that structured error payloads for error kinds outside the four
/// targeted by the hint change (<c>WriteDenied</c>, <c>ImagesIncompatible</c>,
/// <c>ConnectSdkNotAvailable</c>, and build failure) do not include a
/// <c>hint</c> field at all — not an empty string, not a null value, but
/// completely absent from the serialized JSON output.
///
/// <para>
/// This prevents the anti-pattern of generic or vacuous hints that would train
/// readers to ignore the field entirely.  An absent key is unambiguous: there
/// is no actionable remediation known for this error.  An empty string or a
/// <c>null</c> that slips past the serializer would silently pollute every
/// consumer that checks for the key.
/// </para>
/// </summary>
public class NonTargetKindHintAbsenceTests
{
    // ── Serializer options (matches LogixTagTools._jsonOptions) ───────────────

    /// <summary>
    /// Production-identical serialiser settings: nulls are omitted so that a
    /// null <c>Hint</c> is indistinguishable from an absent one at the wire
    /// level — unless someone accidentally sets it to an empty string.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    // ── ITagService stubs ─────────────────────────────────────────────────────

    /// <summary>
    /// Throws a <see cref="TagServiceException"/> with a caller-specified
    /// <see cref="TagServiceErrorKind"/> from every operation, exercising the
    /// typed-exception handler branch in each handler.
    /// </summary>
    private sealed class KindedExceptionTagService : ITagService
    {
        private readonly TagServiceErrorKind _kind;
        private readonly string _message;

        internal KindedExceptionTagService(TagServiceErrorKind kind, string message = "simulated error")
        {
            _kind    = kind;
            _message = message;
        }

        public Task ConnectAsync(string gateway, string path, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<TagValue> ReadAsync(string tagName, CancellationToken ct = default)
            => throw new TagServiceException(new TagServiceError(_kind, _message, tagName));

        public Task WriteAsync(string tagName, object value, CancellationToken ct = default)
            => throw new TagServiceException(new TagServiceError(_kind, _message, tagName));

        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken ct = default)
            => throw new TagServiceException(new TagServiceError(_kind, _message));
    }

    /// <summary>
    /// Throws a plain <see cref="InvalidOperationException"/> from every
    /// operation, exercising the catch-all <c>catch (Exception)</c> branch
    /// that maps to <c>Kind = "Unknown"</c>.
    /// </summary>
    private sealed class GenericExceptionTagService : ITagService
    {
        private readonly string _message;

        internal GenericExceptionTagService(string message = "unexpected failure")
            => _message = message;

        public Task ConnectAsync(string gateway, string path, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<TagValue> ReadAsync(string tagName, CancellationToken ct = default)
            => throw new InvalidOperationException(_message);

        public Task WriteAsync(string tagName, object value, CancellationToken ct = default)
            => throw new InvalidOperationException(_message);

        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken ct = default)
            => throw new InvalidOperationException(_message);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TagSessionService AttachedSession(ITagService svc)
    {
        var session = new TagSessionService();
        session.Attach(svc, isVirtual: false);
        return session;
    }

    /// <summary>
    /// Serialises <paramref name="obj"/> with the production-identical options
    /// (WhenWritingNull) and returns a <see cref="JsonDocument"/> for inspection.
    /// </summary>
    private static JsonDocument Serialize(object obj)
        => JsonDocument.Parse(JsonSerializer.Serialize(obj, JsonOptions));

    /// <summary>
    /// Returns <c>true</c> when the top-level JSON object contains a key named
    /// <c>"hint"</c>, regardless of its value.  Both <c>"hint": null</c> and
    /// <c>"hint": ""</c> are failures.
    /// </summary>
    private static bool HasHintKey(JsonDocument doc)
        => doc.RootElement.ValueKind == JsonValueKind.Object
           && doc.RootElement.TryGetProperty("hint", out _);

    // ══════════════════════════════════════════════════════════════════════════
    // ReadTagError — non-target kinds
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadTagError_NoSession_HintAbsentFromJson()
    {
        // No-session path: Kind=null.  Not a target kind → hint must be absent.
        var session = new TagSessionService();   // disconnected, no Attach call

        var result = await ReadTagHandler.ExecuteAsync("SomeTag", session);

        using var doc = Serialize(result);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for ReadTagError with Kind=null (no-session path).");
    }

    [Fact]
    public async Task ReadTagError_TagNotFound_HintAbsentFromJson()
    {
        var session = AttachedSession(
            new KindedExceptionTagService(TagServiceErrorKind.TagNotFound));

        var result = await ReadTagHandler.ExecuteAsync("SomeTag", session);

        using var doc = Serialize(result);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for ReadTagError with Kind=TagNotFound.");
    }

    [Fact]
    public async Task ReadTagError_ConnectionFailed_HintAbsentFromJson()
    {
        var session = AttachedSession(
            new KindedExceptionTagService(TagServiceErrorKind.ConnectionFailed));

        var result = await ReadTagHandler.ExecuteAsync("SomeTag", session);

        using var doc = Serialize(result);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for ReadTagError with Kind=ConnectionFailed.");
    }

    [Fact]
    public async Task ReadTagError_TypeMismatch_HintAbsentFromJson()
    {
        var session = AttachedSession(
            new KindedExceptionTagService(TagServiceErrorKind.TypeMismatch));

        var result = await ReadTagHandler.ExecuteAsync("SomeTag", session);

        using var doc = Serialize(result);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for ReadTagError with Kind=TypeMismatch.");
    }

    [Fact]
    public async Task ReadTagError_Unknown_HintAbsentFromJson()
    {
        // Generic exception → handler maps to Kind=Unknown.  Not a target kind.
        var session = AttachedSession(new GenericExceptionTagService());

        var result = await ReadTagHandler.ExecuteAsync("SomeTag", session);

        using var doc = Serialize(result);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for ReadTagError with Kind=Unknown.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // WriteTagError — non-target kinds (WriteDenied is the only target kind)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WriteTagError_NoSession_HintAbsentFromJson()
    {
        var session = new TagSessionService();   // disconnected

        var result = await WriteTagHandler.ExecuteAsync("SomeTag", "1", session);

        using var doc = Serialize(result);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for WriteTagError with Kind=null (no-session path).");
    }

    [Fact]
    public async Task WriteTagError_TagNotFound_HintAbsentFromJson()
    {
        var session = AttachedSession(
            new KindedExceptionTagService(TagServiceErrorKind.TagNotFound));

        var result = await WriteTagHandler.ExecuteAsync("SomeTag", "1", session);

        using var doc = Serialize(result);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for WriteTagError with Kind=TagNotFound.");
    }

    [Fact]
    public async Task WriteTagError_ConnectionFailed_HintAbsentFromJson()
    {
        var session = AttachedSession(
            new KindedExceptionTagService(TagServiceErrorKind.ConnectionFailed));

        var result = await WriteTagHandler.ExecuteAsync("SomeTag", "1", session);

        using var doc = Serialize(result);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for WriteTagError with Kind=ConnectionFailed.");
    }

    [Fact]
    public async Task WriteTagError_TypeMismatch_HintAbsentFromJson()
    {
        var session = AttachedSession(
            new KindedExceptionTagService(TagServiceErrorKind.TypeMismatch));

        var result = await WriteTagHandler.ExecuteAsync("SomeTag", "1", session);

        using var doc = Serialize(result);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for WriteTagError with Kind=TypeMismatch.");
    }

    [Fact]
    public async Task WriteTagError_Unknown_HintAbsentFromJson()
    {
        var session = AttachedSession(new GenericExceptionTagService());

        var result = await WriteTagHandler.ExecuteAsync("SomeTag", "1", session);

        using var doc = Serialize(result);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for WriteTagError with Kind=Unknown.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PollError — non-target kinds
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PollError_NoSession_HintAbsentFromJson()
    {
        var session = new TagSessionService();   // disconnected

        var result = await PollTagsHandler.ExecuteAsync(
            ["SomeTag"], count: 1, intervalMs: 100, session);

        using var doc = Serialize(result);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for PollError with Kind=null (no-session path).");
    }

    [Fact]
    public async Task PollError_TagNotFound_HintAbsentFromJson()
    {
        var session = AttachedSession(
            new KindedExceptionTagService(TagServiceErrorKind.TagNotFound));

        var result = await PollTagsHandler.ExecuteAsync(
            ["SomeTag"], count: 1, intervalMs: 100, session);

        using var doc = Serialize(result);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for PollError with Kind=TagNotFound.");
    }

    [Fact]
    public async Task PollError_ConnectionFailed_HintAbsentFromJson()
    {
        var session = AttachedSession(
            new KindedExceptionTagService(TagServiceErrorKind.ConnectionFailed));

        var result = await PollTagsHandler.ExecuteAsync(
            ["SomeTag"], count: 1, intervalMs: 100, session);

        using var doc = Serialize(result);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for PollError with Kind=ConnectionFailed.");
    }

    [Fact]
    public async Task PollError_Unknown_HintAbsentFromJson()
    {
        var session = AttachedSession(new GenericExceptionTagService());

        var result = await PollTagsHandler.ExecuteAsync(
            ["SomeTag"], count: 1, intervalMs: 100, session);

        using var doc = Serialize(result);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for PollError with Kind=Unknown.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ConnectError / ConnectBadBackendError / ConnectUnknownParameterError
    // — these record types carry no Hint parameter at all; verify they remain
    //   absent as the records evolve.
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConnectError_NoProject_HintAbsentFromJson()
    {
        // useVirtual=true without a loaded project → ConnectError, no hint.
        var result = await ConnectHandler.ExecuteAsync(
            gateway:        "192.0.2.1",
            path:           "1,0",
            useVirtual:     true,
            projectService: new ProjectService(),
            sessionService: new TagSessionService());

        using var doc = Serialize(result);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for ConnectError (no-project path).");
    }

    [Fact]
    public void ConnectBadBackendError_HintAbsentFromJson()
    {
        var err = new ConnectBadBackendError(
            Error:       "Unknown backend 'grpc'.",
            Backend:     "grpc",
            ValidValues: ConnectHandler.ValidBackends);

        using var doc = Serialize(err);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for ConnectBadBackendError.");
    }

    [Fact]
    public void ConnectUnknownParameterError_HintAbsentFromJson()
    {
        var err = ConnectHandler.CheckForUnknownParameters(["gateway", "mode"]);

        Assert.NotNull(err);   // "mode" is not a valid parameter

        using var doc = Serialize(err);
        Assert.False(HasHintKey(doc),
            "hint must not appear in JSON for ConnectUnknownParameterError.");
    }
}
