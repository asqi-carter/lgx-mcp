using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for poll-delta reporting in <see cref="PollTagsHandler.ExecuteAsync"/>.
///
/// Scenario: two granted Demo.L5X tags are polled for two cycles.  Between the
/// baseline cycle (cycle 1) and the second cycle, exactly one tag changes value:
///   Cmd_MotorStart    — stays <c>false</c> in both cycles  (unchanged)
///   Cmd_SpeedSetpoint — changes from <c>0.0</c> to <c>75.0</c> (changed)
///
/// The delta in cycle 2 must contain only <c>Cmd_SpeedSetpoint</c> with its old
/// value (<c>0.0</c>) and new value (<c>75.0</c>).  <c>Cmd_MotorStart</c> must
/// not appear in cycle 2's delta at all.
///
/// Policy is derived from the real Demo.L5X fixture via <see cref="GateEngine"/>
/// so the test exercises the full grant-policy stack.
/// </summary>
public class PollTagsHandlerTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    // The two granted tags used in the delta scenario.
    private const string UnchangedTag = "Cmd_MotorStart";    // BOOL, stays false
    private const string ChangedTag   = "Cmd_SpeedSetpoint"; // REAL, 0.0 → 75.0

    private static readonly object OldSpeedValue = 0.0;
    private static readonly object NewSpeedValue = 75.0;

    // ── Private test-double ───────────────────────────────────────────────────

    /// <summary>
    /// A minimal <see cref="ITagService"/> test double that returns a
    /// pre-configured sequence of values for each named tag across successive
    /// <see cref="ReadAsync"/> calls.  The final value in each sequence is
    /// returned unchanged for all reads once the queue is exhausted.
    /// All other operations are no-ops.
    /// </summary>
    private sealed class SequencedReadService : ITagService
    {
        private readonly Dictionary<string, Queue<(object Value, string? DataType)>> _sequences;

        /// <summary>
        /// Initialises the service with one value-sequence per tag.
        /// </summary>
        /// <param name="sequences">
        /// Maps each tag name to an ordered array of values to return on
        /// successive reads and the data-type string to attach to each
        /// <see cref="TagValue"/>.
        /// </param>
        public SequencedReadService(
            Dictionary<string, (object[] Values, string? DataType)> sequences)
        {
            _sequences = new Dictionary<string, Queue<(object, string?)>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var kv in sequences)
            {
                var queue = new Queue<(object, string?)>();
                foreach (var v in kv.Value.Values)
                    queue.Enqueue((v, kv.Value.DataType));
                _sequences[kv.Key] = queue;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Returns the next value from the tag's sequence.  When only one item
        /// remains the same item is returned on every subsequent call (peek
        /// semantics so the tail value is stable).
        /// </remarks>
        public Task<TagValue> ReadAsync(string tagName, CancellationToken ct = default)
        {
            if (!_sequences.TryGetValue(tagName, out var queue))
                throw new TagServiceException(
                    new TagServiceError(
                        TagServiceErrorKind.TagNotFound,
                        $"Tag '{tagName}' not found in SequencedReadService.",
                        tagName));

            var (value, dataType) = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return Task.FromResult(new TagValue(tagName, value, dataType));
        }

        /// <inheritdoc/>
        public Task ConnectAsync(string gateway, string path,
            CancellationToken ct = default) => Task.CompletedTask;

        /// <inheritdoc/>
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        /// <inheritdoc/>
        public Task WriteAsync(string tagName, object value,
            CancellationToken ct = default) => Task.CompletedTask;

        /// <inheritdoc/>
        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TagInfo>>(new List<TagInfo>());
    }

    // ── Scenario setup ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="TagSessionService"/> backed by the Demo.L5X-derived
    /// write-gate policy and the sequenced read service, then executes two poll
    /// cycles (no inter-cycle delay) and returns the <see cref="PollResult"/>.
    /// </summary>
    private static async Task<PollResult> RunTwoCyclePollAsync()
    {
        // Derive the real policy from the Demo.L5X fixture.
        var project = L5X.Load(FixturePath);
        var policy  = GateEngine.DerivePolicy(project, GateConfig.Default);

        // Build the sequenced inner service:
        //   Cmd_MotorStart    → false  (cycle 1), false  (cycle 2)  — unchanged
        //   Cmd_SpeedSetpoint → 0.0   (cycle 1), 75.0   (cycle 2)  — changed
        var inner = new SequencedReadService(
            new Dictionary<string, (object[] Values, string? DataType)>
            {
                [UnchangedTag] = (new object[] { false, false        }, "BOOL"),
                [ChangedTag]   = (new object[] { OldSpeedValue, NewSpeedValue }, "REAL"),
            });

        // Wrap in the real GatedTagService so reads flow through the live policy.
        var gated = new GatedTagService(inner, policy);

        var session = new TagSessionService();
        session.Attach(gated, isVirtual: true);

        var outcome = await PollTagsHandler.ExecuteAsync(
            tags:       new[] { UnchangedTag, ChangedTag },
            count:      2,
            intervalMs: 100,
            session:    session);

        return Assert.IsType<PollResult>(outcome);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cycle 2 must contain exactly one change entry when only one granted tag
    /// changed value between the two cycles.
    /// </summary>
    [Fact]
    public async Task PollDelta_Cycle2_ContainsExactlyOneChange_WhenOnlyOneGrantedTagChanged()
    {
        var pollResult = await RunTwoCyclePollAsync();
        var cycle2 = pollResult.Cycles.Single(c => c.Cycle == 2);

        Assert.Single(cycle2.Changes);
    }

    /// <summary>
    /// The single change reported in cycle 2 must identify the tag whose value
    /// actually changed (<see cref="ChangedTag"/> = Cmd_SpeedSetpoint).
    /// </summary>
    [Fact]
    public async Task PollDelta_Cycle2_SingleChange_IsTheChangedGrantedTag_Cmd_SpeedSetpoint()
    {
        var pollResult = await RunTwoCyclePollAsync();
        var cycle2 = pollResult.Cycles.Single(c => c.Cycle == 2);
        var change = cycle2.Changes.Single();

        Assert.Equal(ChangedTag, change.Tag, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The change record for cycle 2 must carry the value observed in cycle 1
    /// as its <see cref="TagChange.Previous"/> field (old value = 0.0).
    /// </summary>
    [Fact]
    public async Task PollDelta_Cycle2_ChangedTag_PreviousField_IsOldValue()
    {
        var pollResult = await RunTwoCyclePollAsync();
        var cycle2 = pollResult.Cycles.Single(c => c.Cycle == 2);
        var change = cycle2.Changes.Single();

        Assert.Equal(OldSpeedValue, change.Previous);
    }

    /// <summary>
    /// The change record for cycle 2 must carry the value observed in cycle 2
    /// as its <see cref="TagChange.Current"/> field (new value = 75.0).
    /// </summary>
    [Fact]
    public async Task PollDelta_Cycle2_ChangedTag_CurrentField_IsNewValue()
    {
        var pollResult = await RunTwoCyclePollAsync();
        var cycle2 = pollResult.Cycles.Single(c => c.Cycle == 2);
        var change = cycle2.Changes.Single();

        Assert.Equal(NewSpeedValue, change.Current);
    }

    /// <summary>
    /// The tag whose value did NOT change between cycles (<see cref="UnchangedTag"/>
    /// = Cmd_MotorStart) must not appear anywhere in cycle 2's delta list.
    /// </summary>
    [Fact]
    public async Task PollDelta_Cycle2_UnchangedGrantedTag_Cmd_MotorStart_DoesNotAppearInDelta()
    {
        var pollResult = await RunTwoCyclePollAsync();
        var cycle2 = pollResult.Cycles.Single(c => c.Cycle == 2);

        Assert.DoesNotContain(cycle2.Changes, c =>
            string.Equals(c.Tag, UnchangedTag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>STRING values are wrapped as untrusted; numeric values are not.</summary>
    [Fact]
    public async Task Poll_OneBadPath_ReturnsItsErrorInItsSlot_AndValuesForTheOthers()
    {
        var inner = new SequencedReadService(
            new Dictionary<string, (object[] Values, string? DataType)>
            {
                [UnchangedTag] = (new object[] { false, false }, "BOOL"),
                [ChangedTag]   = (new object[] { OldSpeedValue, NewSpeedValue }, "REAL"),
            });
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: true);

        var outcome = await PollTagsHandler.ExecuteAsync(
            new[] { UnchangedTag, "Missing_Tag", ChangedTag }, count: 2, intervalMs: 100, session: session);

        var poll = Assert.IsType<PollResult>(outcome);
        foreach (var cycle in poll.Cycles)
        {
            var err = Assert.Single(cycle.Errors!);
            Assert.Equal("Missing_Tag", err.Tag);
            Assert.Equal("TagNotFound", err.Kind);
        }
        Assert.Equal(2, poll.Cycles[0].Changes.Count);
        Assert.Contains(poll.Cycles[0].Changes, c => c.Tag == ChangedTag && Equals(c.Current, OldSpeedValue));
        var change = Assert.Single(poll.Cycles[1].Changes);
        Assert.Equal(ChangedTag, change.Tag);
        Assert.Equal(NewSpeedValue, change.Current);
    }

    [Fact]
    public async Task StringValue_WrappedUntrusted()
    {
        var inner = new SequencedReadService(
            new Dictionary<string, (object[] Values, string? DataType)>
            {
                ["Name"]  = (new object[] { "a", "b" }, "STRING"),
                ["Speed"] = (new object[] { 1.0, 2.0 }, "REAL"),
            });
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: true);

        var result = Assert.IsType<PollResult>(await PollTagsHandler.ExecuteAsync(
            new[] { "Name", "Speed" }, 2, 100, session));

        Assert.Equal(new[] { "value" }, result.UntrustedContent);
        Assert.False(string.IsNullOrWhiteSpace(result.Notice));

        var c1 = result.Cycles[0].Changes.Single(c => c.Tag == "Name");
        Assert.Equal("""<untrusted source="tag-value">a</untrusted>""", c1.Current);
        Assert.Null(c1.Previous);

        var c2 = result.Cycles[1].Changes.Single(c => c.Tag == "Name");
        Assert.Equal("""<untrusted source="tag-value">a</untrusted>""", c2.Previous);
        Assert.Equal("""<untrusted source="tag-value">b</untrusted>""", c2.Current);

        Assert.Equal(2.0, result.Cycles[1].Changes.Single(c => c.Tag == "Speed").Current);
    }

    // ── Argument checks and declared-type refusals ─────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Poll_CountBelowOne_IsInvalidArguments_NamingCount(int count)
    {
        var (session, svc) = ReadTagHandlerTests.TypedSession();

        var err = Assert.IsType<PollError>(
            await PollTagsHandler.ExecuteAsync(["Plain"], count, 100, session));

        Assert.Equal("InvalidArguments", err.Kind);
        Assert.StartsWith("count", err.Error);
        Assert.Empty(svc.Reads);
    }

    [Fact]
    public async Task Poll_EmptyTagsList_IsInvalidArguments_NamingTags()
    {
        var (session, svc) = ReadTagHandlerTests.TypedSession();

        var err = Assert.IsType<PollError>(await PollTagsHandler.ExecuteAsync([], 1, 100, session));

        Assert.Equal("InvalidArguments", err.Kind);
        Assert.StartsWith("tags", err.Error);
        Assert.Empty(svc.Reads);
    }

    [Fact]
    public async Task Poll_WholeArrayAndBadIndex_ArePerPathTypedErrors_AndTheRestStillPolls()
    {
        var (session, svc) = ReadTagHandlerTests.TypedSession();

        var result = Assert.IsType<PollResult>(
            await PollTagsHandler.ExecuteAsync(["Arr", "Arr[9]", "Plain"], 1, 100, session));

        var errors = result.Cycles.Single().Errors!;
        Assert.Equal("StructureReadUnsupported", errors.Single(e => e.Tag == "Arr").Kind);
        Assert.Equal("IndexOutOfRange", errors.Single(e => e.Tag == "Arr[9]").Kind);
        Assert.Equal(["Plain"], svc.Reads);
    }
}
