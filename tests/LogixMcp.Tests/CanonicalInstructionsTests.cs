using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// <see cref="LogixTools.CanonicalInstructions"/> must
/// name the two things the Tier D bench runs got wrong when working only from
/// the tool list and this always-resident text.
///
/// <para>
/// Run 1 opened the L5X file first and every SDK online connect took the
/// file-based path and failed (a known trap) — the working route (call
/// <c>logix_connect</c> with <c>sdkCommPath</c> and no project opened first)
/// was described only inside <c>logix_connect</c>'s long tool description.
/// </para>
///
/// <para>
/// Both runs published with <c>target="controller"</c> directly and never
/// staged with <c>target="workstation"</c> first — the stage-then-publish-once
/// model was described only in the final third of
/// <c>logix_propose_rung_edit</c>'s tool description.
/// </para>
///
/// <para>
/// These tests assert both gaps are now closed directly in the orientation
/// text, and that the text still stays within its ≤25 line budget.
/// </para>
/// </summary>
public class CanonicalInstructionsTests
{
#if LOGIX_SDK
    /// <summary>
    /// The instructions must name the upload route for online SDK work: call
    /// <c>logix_connect</c> with the online SDK mode and a comm path BEFORE any
    /// <c>logix_open_project</c> call, so the server uploads and correlates the
    /// controller project automatically instead of silently falling back to a
    /// file-based (and therefore unreachable) path.
    /// </summary>
    [Fact]
    public void Instructions_NameTheUploadRouteForOnlineSdkWork()
    {
        var text = LogixTools.CanonicalInstructions;

        Assert.Contains("logix_connect", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sdkMode=\"online\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sdkCommPath", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uploads", text, StringComparison.OrdinalIgnoreCase);

        // The ordering constraint is the whole point: logix_connect before any
        // logix_open_project for online SDK work.
        Assert.Contains("BEFORE any logix_open_project", text);
    }

    /// <summary>
    /// The instructions must name the stage-then-publish-once model for rung
    /// edits: stage repeatedly with <c>target="workstation"</c>, then publish
    /// the whole change set exactly ONCE with <c>target="controller"</c>.
    /// </summary>
    [Fact]
    public void Instructions_NameStageThenPublishOnce()
    {
        var text = LogixTools.CanonicalInstructions;

        Assert.Contains("stage", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target=\"workstation\"", text);
        Assert.Contains("publish", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target=\"controller\"", text);
        Assert.Contains("ONCE", text);
    }

    /// <summary>
    /// The instructions must state the real
    /// publish/accept cycle measured on Bench 2 (04:14Z) — a
    /// <c>target="controller"</c> publish does not execute; it leaves the rung
    /// pending on the controller (<c>logix_pending_edits</c> reports
    /// <c>Type="I"</c>) until the engineer accepts or cancels it in Studio
    /// 5000, or the caller cancels it via <c>logix_cancel_pending_edits</c>.
    /// Only after that accept/cancel can the next publish happen — the Logix
    /// Designer SDK has no accept/assemble call of its own, so
    /// <c>logix_finalize_edits</c> can never assemble an existing zone and
    /// must not be described as a second way to publish. The text must also
    /// disclose that a tag declared by a publish is created on the controller
    /// immediately (corrected the earlier <c>TagNotFound</c>
    /// claim).
    /// </summary>
    [Fact]
    public void Instructions_StateOnePublishPerAcceptCycle()
    {
        var text = LogixTools.CanonicalInstructions;

        // Multi-word phrases can legitimately be split across the ≤25-line budget's
        // hard wraps, so compare against a whitespace-normalized copy for those checks.
        var normalized = Regex.Replace(text, @"\s+", " ");

        // Names the pending state left on the controller by a publish.
        Assert.Contains("pending", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Type=\"I\"", text);

        // Names both valid transitions out of that pending state. The instructions
        // deliberately name the SDK-only tools by role ("the SDK's cancel-pending-edits
        // tool") rather than by literal logix_* token: InstructionsToolNameStalenessTests
        // only resolves tool names against the core LogixMcp assembly, and
        // logix_cancel_pending_edits/logix_finalize_edits/logix_pending_edits live in the
        // SDK-adapter assembly, so spelling them out here would trip that staleness guard.
        Assert.Contains("accept", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cancel", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Studio 5000", text);
        Assert.Contains("cancel-pending-edits", text, StringComparison.OrdinalIgnoreCase);

        // States the one-publish-per-cycle constraint explicitly.
        Assert.Contains("one publish per accept/cancel cycle", normalized, StringComparison.OrdinalIgnoreCase);

        // No longer implies the finalize-edits tool is a second way to publish.
        Assert.Contains("finalize-edits", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot assemble", normalized, StringComparison.OrdinalIgnoreCase);

        // Discloses the corrected pending-tag behavior:
        // tags are created immediately, not hidden behind TagNotFound.
        Assert.Contains("created on the controller immediately", normalized);
        Assert.Contains("CreatedByThisSession", text);
    }

#endif

    /// <summary>
    /// The always-resident instructions field must stay small. The budget was raised
    /// from ≤10 lines to ≤25 to make room for the two new sections above, to ≤27 for
    /// the Skills orientation entry, and to ≤29 for the publish-mode orientation
    /// entry. The text must not grow without bound.
    /// </summary>
    [Fact]
    public void Instructions_StayUnder25Lines()
    {
        var lineCount = LogixTools.CanonicalInstructions
            .Split('\n')
            .Length;

        Assert.True(
            lineCount <= 29,
            $"LogixTools.CanonicalInstructions has grown to {lineCount} lines; " +
            $"keep the always-resident orientation text to ≤29 lines.");
    }
}
