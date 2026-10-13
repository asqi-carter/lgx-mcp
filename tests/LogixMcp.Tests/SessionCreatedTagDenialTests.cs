using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for the session-created tag denial feature.
///
/// <para>
/// Verifies that <see cref="WriteTagHandler.ExecuteAsync"/> denies writes to tags
/// that were recorded in the <see cref="TagSessionService"/> session-created registry
/// via <see cref="TagSessionService.RecordCreatedTags"/>, and that the registry is
/// cleared by <see cref="TagSessionService.Attach"/> (i.e. on a new
/// <c>logix_connect</c>).
/// </para>
///
/// <para>
/// These tests exercise the core-assembly guard path only — no L5X project or
/// <see cref="GatedTagService"/> is required, so they run without an SDK install.
/// </para>
/// </summary>
public class SessionCreatedTagDenialTests
{
    // ── TagSessionService.RecordCreatedTags / TryGetSessionCreatedTagEntry ────

    [Fact]
    public void RecordCreatedTags_SingleTag_IsFoundByTryGet()
    {
        var session = new TagSessionService();

        session.RecordCreatedTags(new[] { "Cmd_NewTag" }, "proposal-abc");

        Assert.True(session.TryGetSessionCreatedTagEntry("Cmd_NewTag", out var entry));
        Assert.NotNull(entry);
        Assert.Equal("Cmd_NewTag",    entry.TagName);
        Assert.Equal("proposal-abc",  entry.ProposalId);
    }

    [Fact]
    public void RecordCreatedTags_MultipleTags_AllFound()
    {
        var session = new TagSessionService();

        session.RecordCreatedTags(new[] { "Cmd_TagA", "Sim_TagB", "Cmd_TagC" }, "pid-1");

        Assert.True(session.TryGetSessionCreatedTagEntry("Cmd_TagA", out _));
        Assert.True(session.TryGetSessionCreatedTagEntry("Sim_TagB", out _));
        Assert.True(session.TryGetSessionCreatedTagEntry("Cmd_TagC", out _));
    }

    [Fact]
    public void TryGetSessionCreatedTagEntry_UnrecordedTag_ReturnsFalse()
    {
        var session = new TagSessionService();

        session.RecordCreatedTags(new[] { "Cmd_NewTag" }, "pid-1");

        Assert.False(session.TryGetSessionCreatedTagEntry("Cmd_OtherTag", out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void TryGetSessionCreatedTagEntry_CaseInsensitive_Matches()
    {
        // Tag lookup must be case-insensitive (Logix convention).
        var session = new TagSessionService();
        session.RecordCreatedTags(new[] { "Cmd_NewTag" }, "pid-1");

        Assert.True(session.TryGetSessionCreatedTagEntry("CMD_NEWTAG",  out _));
        Assert.True(session.TryGetSessionCreatedTagEntry("cmd_newtag",  out _));
        Assert.True(session.TryGetSessionCreatedTagEntry("Cmd_NewTag",  out _));
    }

    [Fact]
    public void RecordCreatedTags_SameTagTwice_LastProposalIdWins()
    {
        // If a tag is re-published in a later proposal, the newer record replaces the older.
        var session = new TagSessionService();

        session.RecordCreatedTags(new[] { "Cmd_NewTag" }, "pid-first");
        session.RecordCreatedTags(new[] { "Cmd_NewTag" }, "pid-second");

        session.TryGetSessionCreatedTagEntry("Cmd_NewTag", out var entry);
        Assert.Equal("pid-second", entry!.ProposalId);
    }

    [Fact]
    public void RecordCreatedTags_SetsCreatedAtToUtcNow()
    {
        var before  = DateTimeOffset.UtcNow;
        var session = new TagSessionService();
        session.RecordCreatedTags(new[] { "Cmd_NewTag" }, "pid-1");
        var after   = DateTimeOffset.UtcNow;

        session.TryGetSessionCreatedTagEntry("Cmd_NewTag", out var entry);

        Assert.NotNull(entry);
        Assert.True(entry.CreatedAt >= before);
        Assert.True(entry.CreatedAt <= after);
    }

    // ── Registry cleared by Attach ────────────────────────────────────────────

    [Fact]
    public void Attach_ClearsSessionCreatedTagRegistry()
    {
        // Simulates a new logix_connect: Attach must clear all recorded tags.
        var session = new TagSessionService();
        session.RecordCreatedTags(new[] { "Cmd_NewTag", "Sim_NewTag" }, "pid-1");

        // Create a stub service to satisfy Attach's non-null requirement.
        var stub = new NoOpTagService();
        session.Attach(stub, isVirtual: false);

        // Registry must be empty after Attach.
        Assert.False(session.TryGetSessionCreatedTagEntry("Cmd_NewTag",  out _));
        Assert.False(session.TryGetSessionCreatedTagEntry("Sim_NewTag",  out _));
    }

    [Fact]
    public void Attach_Twice_SecondAttachAlsoClearsRegistry()
    {
        var session = new TagSessionService();
        var stub    = new NoOpTagService();

        session.Attach(stub, isVirtual: false);
        session.RecordCreatedTags(new[] { "Cmd_TagX" }, "pid-2");

        // Second Attach (simulating a second logix_connect) must clear the registry.
        session.Attach(stub, isVirtual: false);

        Assert.False(session.TryGetSessionCreatedTagEntry("Cmd_TagX", out _));
    }

    // ── WriteTagHandler.ExecuteAsync — session-created tag denied ─────────────

    [Fact]
    public async Task ExecuteAsync_SessionCreatedTag_ReturnsWriteTagError_NotSuccessEnvelope()
    {
        // Arrange: connect a session and record a tag as session-created.
        var inner   = new VirtualTagService(
            new Dictionary<string, (object, string?)> { ["Cmd_NewTag"] = (0, "BOOL") });
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: false);
        session.RecordCreatedTags(new[] { "Cmd_NewTag" }, "pid-abc");

        // Act: attempt to write the session-created tag — must NOT throw.
        var result = await WriteTagHandler.ExecuteAsync("Cmd_NewTag", "1", session);

        // Assert (b): not a WriteTagResult success envelope.
        Assert.IsNotType<WriteTagResult>(result);
    }

    [Fact]
    public async Task ExecuteAsync_SessionCreatedTag_DoesNotThrow()
    {
        var inner   = new VirtualTagService(
            new Dictionary<string, (object, string?)> { ["Cmd_NewTag"] = (0, "BOOL") });
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: false);
        session.RecordCreatedTags(new[] { "Cmd_NewTag" }, "pid-abc");

        // Act: must NOT throw.
        var result = await WriteTagHandler.ExecuteAsync("Cmd_NewTag", "1", session);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task ExecuteAsync_SessionCreatedTag_ErrorKindIsWriteDenied()
    {
        var inner   = new VirtualTagService(
            new Dictionary<string, (object, string?)> { ["Cmd_NewTag"] = (0, "BOOL") });
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: false);
        session.RecordCreatedTags(new[] { "Cmd_NewTag" }, "pid-abc");

        var result = await WriteTagHandler.ExecuteAsync("Cmd_NewTag", "1", session);

        var error = Assert.IsType<WriteTagError>(result);
        Assert.Equal(nameof(TagServiceErrorKind.WriteDenied), error.Kind);
    }

    [Fact]
    public async Task ExecuteAsync_SessionCreatedTag_ErrorCarriesTagName()
    {
        var inner   = new VirtualTagService(
            new Dictionary<string, (object, string?)> { ["Cmd_NewTag"] = (0, "BOOL") });
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: false);
        session.RecordCreatedTags(new[] { "Cmd_NewTag" }, "pid-abc");

        var result = await WriteTagHandler.ExecuteAsync("Cmd_NewTag", "1", session);

        var error = Assert.IsType<WriteTagError>(result);
        Assert.Equal("Cmd_NewTag", error.Tag);
    }

    [Fact]
    public async Task ExecuteAsync_SessionCreatedTag_ErrorMessageContainsCreatedByThisSession()
    {
        var inner   = new VirtualTagService(
            new Dictionary<string, (object, string?)> { ["Cmd_NewTag"] = (0, "BOOL") });
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: false);
        session.RecordCreatedTags(new[] { "Cmd_NewTag" }, "pid-abc");

        var result = await WriteTagHandler.ExecuteAsync("Cmd_NewTag", "1", session);

        var error = Assert.IsType<WriteTagError>(result);
        Assert.Contains("CreatedByThisSession", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_SessionCreatedTag_ErrorMessageContainsProposalId()
    {
        // The denial message must include the proposal ID so the agent can trace
        // the denied write back to the originating rung-edit operation.
        var inner   = new VirtualTagService(
            new Dictionary<string, (object, string?)> { ["Cmd_NewTag"] = (0, "BOOL") });
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: false);
        session.RecordCreatedTags(new[] { "Cmd_NewTag" }, "traceable-proposal-id");

        var result = await WriteTagHandler.ExecuteAsync("Cmd_NewTag", "1", session);

        var error = Assert.IsType<WriteTagError>(result);
        Assert.Contains("traceable-proposal-id", error.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_SessionCreatedTag_HintIsNonEmpty()
    {
        var inner   = new VirtualTagService(
            new Dictionary<string, (object, string?)> { ["Cmd_NewTag"] = (0, "BOOL") });
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: false);
        session.RecordCreatedTags(new[] { "Cmd_NewTag" }, "pid-abc");

        var result = await WriteTagHandler.ExecuteAsync("Cmd_NewTag", "1", session);

        var error = Assert.IsType<WriteTagError>(result);
        Assert.NotNull(error.Hint);
        Assert.NotEmpty(error.Hint);
    }

    [Fact]
    public async Task ExecuteAsync_SessionCreatedTag_InnerServiceNotWritten()
    {
        // The inner service must NOT be written when the session-created guard fires.
        var inner   = new VirtualTagService(
            new Dictionary<string, (object, string?)> { ["Cmd_NewTag"] = (0, "BOOL") });
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: false);
        session.RecordCreatedTags(new[] { "Cmd_NewTag" }, "pid-abc");

        var before = await inner.ReadAsync("Cmd_NewTag");
        try { await WriteTagHandler.ExecuteAsync("Cmd_NewTag", "1", session); } catch { /* expected */ }
        var after = await inner.ReadAsync("Cmd_NewTag");

        Assert.Equal(before.Value, after.Value);
    }

    // ── Member-path writes to session-created tags are also denied ────────────

    [Fact]
    public async Task ExecuteAsync_SessionCreatedTag_MemberPath_IsDenied()
    {
        // Writing "Cmd_NewTag.Speed" when "Cmd_NewTag" is session-created must also
        // be denied — the base tag lookup must strip the member suffix.
        var inner   = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Cmd_NewTag"]       = (0,   "MotorCmd"),
            ["Cmd_NewTag.Speed"] = (0.0, "REAL"),
        });
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: false);
        session.RecordCreatedTags(new[] { "Cmd_NewTag" }, "pid-abc");

        var result = await WriteTagHandler.ExecuteAsync("Cmd_NewTag.Speed", "50", session);

        var error = Assert.IsType<WriteTagError>(result);
        Assert.Equal(nameof(TagServiceErrorKind.WriteDenied), error.Kind);
        Assert.Contains("CreatedByThisSession", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── Non-session-created tags are unaffected ───────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NonSessionCreatedTag_NotDeniedByRegistryCheck()
    {
        // Tags not in the registry must pass through to the inner service normally.
        var inner   = new VirtualTagService(
            new Dictionary<string, (object, string?)> { ["Cmd_Normal"] = (false, "BOOL") });
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: false);
        session.RecordCreatedTags(new[] { "Cmd_Other" }, "pid-abc");   // different tag

        // Act: write Cmd_Normal — no registry guard should fire.
        var result = await WriteTagHandler.ExecuteAsync("Cmd_Normal", "true", session);

        // Assert: write succeeded (WriteTagResult, not error).
        Assert.IsType<WriteTagResult>(result);
    }

    // ── After Attach (new logix_connect), previously-created tags are writable ─

    [Fact]
    public async Task ExecuteAsync_AfterReconnect_PreviouslyCreatedTag_IsNoLongerDenied()
    {
        // After a new logix_connect (Attach), tags that were recorded in the old
        // session must no longer be blocked by the session-created guard.
        var inner   = new VirtualTagService(
            new Dictionary<string, (object, string?)> { ["Cmd_NewTag"] = (0, "BOOL") });
        var session = new TagSessionService();

        // First session: record tag.
        session.Attach(inner, isVirtual: false);
        session.RecordCreatedTags(new[] { "Cmd_NewTag" }, "pid-abc");

        // Second session (logix_connect): Attach clears the registry.
        session.Attach(inner, isVirtual: false);

        // Act: write Cmd_NewTag in the new session — registry is clear, so no denial.
        var result = await WriteTagHandler.ExecuteAsync("Cmd_NewTag", "1", session);

        // Assert: write succeeded (no session-created denial).
        Assert.IsType<WriteTagResult>(result);
    }

    // ── Private test doubles ──────────────────────────────────────────────────

    /// <summary>
    /// No-op tag service used to satisfy <see cref="TagSessionService.Attach"/>
    /// in tests that only need a connected session without tag-level functionality.
    /// </summary>
    private sealed class NoOpTagService : ITagService
    {
        public Task ConnectAsync(string gateway, string path,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task WriteAsync(string tagName, object value,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<TagValue> ReadAsync(string tagName,
            CancellationToken ct = default) =>
            Task.FromResult(new TagValue(tagName, 0, "BOOL"));

        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TagInfo>>(Array.Empty<TagInfo>());
    }
}
