#if LOGIX_SDK
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// logix-mcp-r7k0.9 (N10, N11): publish/replace/cancel wording and the post-cancel pending_edits re-read.
/// </summary>
[Collection("StaticHandlerHooks")]
public class PublishAndCancelWordingTests
{
    private const string Program = "MainProgram";

    [Fact]
    public void TagsOnlyPublish_Message_Says_TagsSent_NoRungs()
    {
        var text = ProposeRungEditHandler.DescribePublishContents(rungs: 0, tags: 2, replaceCount: 0);

        Assert.Contains("tags sent; no rungs", text, StringComparison.Ordinal);
        Assert.DoesNotContain("replacing", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Plural_Strings_Are_Correct()
    {
        Assert.Equal("1 staged rung", ProposeRungEditHandler.Plural(1, "staged rung"));
        Assert.Equal("2 staged rungs", ProposeRungEditHandler.Plural(2, "staged rung"));
        Assert.Equal("0 staged rungs", ProposeRungEditHandler.Plural(0, "staged rung"));

        var one = ProposeRungEditHandler.DescribePublishContents(rungs: 1, tags: 1, replaceCount: 1);
        Assert.Contains("1 staged rung sent", one, StringComparison.Ordinal);
        Assert.Contains("replacing 1 rung", one, StringComparison.Ordinal);
        Assert.Contains("1 tag created", one, StringComparison.Ordinal);
        Assert.DoesNotContain("1 staged rungs", one, StringComparison.Ordinal);
        Assert.DoesNotContain("1 rungs", one, StringComparison.Ordinal);
        Assert.DoesNotContain("(s)", one, StringComparison.Ordinal);

        var many = ProposeRungEditHandler.DescribePublishContents(rungs: 3, tags: 2, replaceCount: 2);
        Assert.Contains("3 staged rungs sent", many, StringComparison.Ordinal);
        Assert.Contains("replacing 2 rungs", many, StringComparison.Ordinal);
        Assert.Contains("2 tags created", many, StringComparison.Ordinal);
    }

    private static string Xp(string routine) =>
        $"Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='{routine}']";

    [Fact]
    public void Publish_AcrossTwoRoutines_CountsEveryRoutine()
    {
        var buffer = new Dictionary<(string RoutineXPath, uint InsertPosition), List<string>>
        {
            [(Xp("BeaconRoutine"), 0u)] = new() { "XIC(A)OTE(B);", "XIC(C)OTE(D);" },
            [(Xp("MainRoutine"), 0u)] = new() { "XIC(E)OTE(F);" },
        };
        var text = ProposeRungEditHandler.DescribePublishContents(
            ProposeRungEditHandler.CountStagedRungsByRoutine(buffer), tags: 0, replaceCount: 0);

        Assert.Contains("3 staged rungs sent (BeaconRoutine 2, MainRoutine 1)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1 staged rung sent", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_SingleRoutine_StillReportsCorrectCount()
    {
        var buffer = new Dictionary<(string RoutineXPath, uint InsertPosition), List<string>>
        {
            [(Xp("MainRoutine"), 0u)] = new() { "XIC(A)OTE(B);", "XIC(C)OTE(D);" },
        };
        var text = ProposeRungEditHandler.DescribePublishContents(
            ProposeRungEditHandler.CountStagedRungsByRoutine(buffer), tags: 1, replaceCount: 1);

        Assert.Contains("2 staged rungs sent, replacing 1 rung", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(", text, StringComparison.Ordinal);

        var one = new Dictionary<string, int> { [Xp("MainRoutine")] = 1 };
        Assert.StartsWith("1 staged rung sent", ProposeRungEditHandler.DescribePublishContents(one, 0, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void Replace_Reports_ReplaceCount()
    {
        var text = ProposeRungEditHandler.DescribePublishContents(rungs: 3, tags: 0, replaceCount: 2);
        Assert.Contains("replacing 2 rungs", text, StringComparison.Ordinal);
        Assert.DoesNotContain("replacing 0", text, StringComparison.Ordinal);

        // The staged buckets' replace counts add up per routine and ignore other routines.
        var counts = new Dictionary<(string RoutineXPath, uint InsertPosition), uint>
        {
            [("R1", 0u)] = 2,
            [("R1", 5u)] = 1,
            [("R2", 0u)] = 9,
        };
        Assert.Equal(3u, ProposeRungEditHandler.SumStagedReplaceCountsForRoutine(counts, "R1"));
        Assert.Equal("replacing 3 rungs", ProposeRungEditHandler.DescribeReplacing(3));
        Assert.Equal("replacing no rungs", ProposeRungEditHandler.DescribeReplacing(0));
    }

    private static RoutinePendingEditState Routine(string name, string state, int pending) =>
        new(Program, name, pending + 1,
            Enumerable.Range(0, pending).Select(i => new PendingEditRungEntry(i, "I", "OTE(X);")).ToList(),
            state);

    private static PendingEditsResult Result(params RoutinePendingEditState[] routines) =>
        new(true, "Run", null, routines, new StagedEditsState(0, Array.Empty<string>()), DateTimeOffset.UtcNow);

    [Fact]
    public async Task Cancel_Reports_PerRoutine_And_PendingEdits_Rereads_AfterCancel()
    {
        var interval = StudioLifecycleHandler.AfterSettleInterval;
        StudioLifecycleHandler.AfterSettleInterval = TimeSpan.Zero;
        try
        {
            var gestureSent = false;
            var readsAfterGesture = 0;
            // The target routine (MainRoutine) holds nothing; the cancelled edits are OtherRoutine's.
            Task<object> Reader(string p, string r, CancellationToken ct)
            {
                if (gestureSent) readsAfterGesture++;
                var other = gestureSent ? Routine("OtherRoutine", "none", 0) : Routine("OtherRoutine", "pending", 2);
                var main  = Routine("MainRoutine", "none", 0);
                return Task.FromResult<object>(r.Length == 0 ? Result(main, other) : Result(main));
            }

            var result = await CancelPendingEditsHandler.ExecuteAsync(
                Program, "MainRoutine", confirm: true,
                pendingEditsReader: Reader,
                session1Runner: (verb, ct) =>
                {
                    gestureSent = true;
                    return Task.FromResult(new Session1ScriptResult(true, "cancel_gesture_sent", Array.Empty<string>()));
                },
                isStudioInstalled: () => true);

            var ok = Assert.IsType<CancelPendingEditsResult>(result);
            Assert.True(readsAfterGesture >= 2, "pending edits must be re-read after the gesture (routine and program)");
            Assert.DoesNotContain("state='none', now state='none'", ok.Message, StringComparison.Ordinal);
            Assert.Contains("'OtherRoutine'", ok.Message, StringComparison.Ordinal);
            Assert.Contains("2 pending rungs", ok.Message, StringComparison.Ordinal);
            var other = Assert.Single(ok.Cancelled!, c => c.Routine == "OtherRoutine");
            Assert.Equal("pending", other.BeforeState);
            Assert.Equal("none", other.AfterState);
            Assert.Equal(2, other.PendingRungs);
        }
        finally { StudioLifecycleHandler.AfterSettleInterval = interval; }
    }

    [Fact]
    public async Task Cancel_ListsLeftoverCreatedTags_WithNote()
    {
        var interval = StudioLifecycleHandler.AfterSettleInterval;
        StudioLifecycleHandler.AfterSettleInterval = TimeSpan.Zero;
        try
        {
            var gestureSent = false;
            Task<object> Reader(string p, string r, CancellationToken ct)
            {
                var main = gestureSent ? Routine("MainRoutine", "none", 0) : Routine("MainRoutine", "pending", 1);
                return Task.FromResult<object>(Result(main));
            }
            Task<Session1ScriptResult> Runner(string verb, CancellationToken ct)
            {
                gestureSent = true;
                return Task.FromResult(new Session1ScriptResult(true, "cancel_gesture_sent", Array.Empty<string>()));
            }

            var result = await CancelPendingEditsHandler.ExecuteAsync(
                Program, "MainRoutine", confirm: true, pendingEditsReader: Reader, session1Runner: Runner,
                isStudioInstalled: () => true,
                leftoverTagsProvider: () => new[] { "NewTagA", "NewTagB", "newtaga" });

            var ok = Assert.IsType<CancelPendingEditsResult>(result);
            Assert.Equal(new[] { "NewTagA", "NewTagB" }, ok.LeftoverTags);
            Assert.Equal("tags are not removed by cancel; delete them in Studio if unwanted", ok.LeftoverTagsNote);

            // No tags created: nothing is listed.
            gestureSent = false;
            var none = Assert.IsType<CancelPendingEditsResult>(await CancelPendingEditsHandler.ExecuteAsync(
                Program, "MainRoutine", confirm: true, pendingEditsReader: Reader, session1Runner: Runner,
                isStudioInstalled: () => true,
                leftoverTagsProvider: () => Array.Empty<string>()));
            Assert.Null(none.LeftoverTags);
            Assert.Null(none.LeftoverTagsNote);
        }
        finally { StudioLifecycleHandler.AfterSettleInterval = interval; }
    }

    [Fact]
    public async Task LeftoverTags_AfterCancel_ListsOnlyTagsCreatedByCancelledZonesPublishes()
    {
        var session = new TagSessionService();
        session.RecordCreatedTags(new[] { "MainTagA", "MainTagB" }, "p1", zone: TagSessionService.ZoneKey(Program, "MainRoutine"));
        session.RecordCreatedTags(new[] { "OtherTag" }, "p2", zone: TagSessionService.ZoneKey(Program, "OtherRoutine"));
        session.RecordCreatedTags(new[] { "UnzonedTag" }, "p3");

        Assert.Equal(new[] { "OtherTag" }, session.SessionCreatedTagNames(Program, "OtherRoutine"));
        Assert.Empty(session.SessionCreatedTagNames(Program, "Nowhere"));
        // The write-denial registry still holds every created tag.
        Assert.Equal(4, session.SessionCreatedTagNames().Count);

        var interval = StudioLifecycleHandler.AfterSettleInterval;
        StudioLifecycleHandler.AfterSettleInterval = TimeSpan.Zero;
        try
        {
            var gestureSent = false;
            Task<object> Reader(string p, string r, CancellationToken ct) =>
                Task.FromResult<object>(Result(gestureSent ? Routine("MainRoutine", "none", 0) : Routine("MainRoutine", "pending", 1)));
            Task<Session1ScriptResult> Runner(string verb, CancellationToken ct)
            {
                gestureSent = true;
                return Task.FromResult(new Session1ScriptResult(true, "cancel_gesture_sent", Array.Empty<string>()));
            }

            var result = await CancelPendingEditsHandler.ExecuteAsync(
                Program, "MainRoutine", confirm: true, pendingEditsReader: Reader, session1Runner: Runner,
                isStudioInstalled: () => true,
                leftoverTagsProvider: () => session.SessionCreatedTagNames(Program, "MainRoutine"));

            var ok = Assert.IsType<CancelPendingEditsResult>(result);
            Assert.Equal(new[] { "MainTagA", "MainTagB" }, ok.LeftoverTags);
        }
        finally { StudioLifecycleHandler.AfterSettleInterval = interval; }
    }

    [Fact]
    public void LeftoverTags_TwoCancelCyclesInOneRoutine_SecondListsOnlyItsOwnTags()
    {
        var session = new TagSessionService();
        var zone = TagSessionService.ZoneKey(Program, "MainRoutine");

        session.RecordCreatedTags(new[] { "FirstTagA", "FirstTagB" }, "p1", zone: zone);
        Assert.Equal(new[] { "FirstTagA", "FirstTagB" }, session.TakeZoneCreatedTagNames(Program, "MainRoutine"));

        session.RecordCreatedTags(new[] { "SecondTag" }, "p2", zone: zone);
        Assert.Equal(new[] { "SecondTag" }, session.TakeZoneCreatedTagNames(Program, "MainRoutine"));
        Assert.Empty(session.TakeZoneCreatedTagNames(Program, "MainRoutine"));

        // The write-denial registry is not pruned.
        Assert.Equal(3, session.SessionCreatedTagNames().Count);
        Assert.True(session.TryGetSessionCreatedTagEntry("FirstTagA", out _));
    }

    [Fact]
    public void DescribeCancelled_NamesEachRoutine_AndNeverSaysNoneToNone()
    {
        var target = Routine("MainRoutine", "pending", 1);
        var targetAfter = Routine("MainRoutine", "none", 0);
        var other = Routine("OtherRoutine", "pending", 3);
        var cancelled = StudioLifecycleHandler.BuildCancelledRoutines(
            target, targetAfter, new[] { target, other },
            new[] { targetAfter, Routine("OtherRoutine", "none", 0) });

        var text = StudioLifecycleHandler.DescribeCancelled("MainRoutine", target, targetAfter, cancelled);
        Assert.Contains("was state='pending', now state='none'", text, StringComparison.Ordinal);
        Assert.Contains("'OtherRoutine' (was state='pending', 3 pending rungs, now state='none')", text, StringComparison.Ordinal);
        Assert.DoesNotContain("state='none', now state='none'", text, StringComparison.Ordinal);

        // Target held nothing: the text says so and names what the program-wide cancel took.
        var idle = Routine("MainRoutine", "none", 0);
        var cancelledIdle = StudioLifecycleHandler.BuildCancelledRoutines(
            idle, idle, new[] { idle, other }, new[] { idle, Routine("OtherRoutine", "none", 0) });
        var idleText = StudioLifecycleHandler.DescribeCancelled("MainRoutine", idle, idle, cancelledIdle);
        Assert.Contains("routine 'MainRoutine' itself had no pending edits", idleText, StringComparison.Ordinal);
        Assert.Contains("'OtherRoutine'", idleText, StringComparison.Ordinal);
        Assert.DoesNotContain("state='none', now state='none'", idleText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_ProgramStillPendingAfterSettle_IsError()
    {
        var interval = StudioLifecycleHandler.AfterSettleInterval;
        StudioLifecycleHandler.AfterSettleInterval = TimeSpan.Zero;
        try
        {
            Task<object> Reader(string p, string r, CancellationToken ct) =>
                Task.FromResult<object>(r.Length == 0
                    ? Result(Routine("MainRoutine", "none", 0), Routine("OtherRoutine", "pending", 1))
                    : Result(Routine("MainRoutine", "none", 0)));

            var result = await CancelPendingEditsHandler.ExecuteAsync(
                Program, "MainRoutine", confirm: true,
                pendingEditsReader: Reader,
                session1Runner: (verb, ct) => Task.FromResult(new Session1ScriptResult(true, "cancel_gesture_sent", Array.Empty<string>())),
                isStudioInstalled: () => true);

            var error = Assert.IsType<CancelPendingEditsError>(result);
            Assert.Contains("'OtherRoutine'", error.Error, StringComparison.Ordinal);
        }
        finally { StudioLifecycleHandler.AfterSettleInterval = interval; }
    }
}
#endif
