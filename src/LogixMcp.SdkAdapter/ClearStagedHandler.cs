#if LOGIX_SDK
// ── ClearStagedHandler — discards the session's staged rungs and tags (logix_clear_staged) ─────
using System.Text.Json.Serialization;
using System.Xml.Linq;
using ConnectedState = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState;
using PartialImportOption = RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption;
using Task = System.Threading.Tasks.Task;

/// <summary>One routine whose staged rungs were cleared.</summary>
/// <param name="RoutineXPath">XPath of the routine, as keyed in the session's staged buffer.</param>
/// <param name="Buckets">Number of staged buckets (insert positions) cleared in that routine.</param>
/// <param name="Rungs">Number of staged rungs cleared in that routine.</param>
public record ClearedRoutine(
    [property: JsonPropertyName("routineXPath")] string RoutineXPath,
    [property: JsonPropertyName("buckets")]      int    Buckets,
    [property: JsonPropertyName("rungs")]        int    Rungs);

/// <summary>Successful <c>logix_clear_staged</c> result: what was cleared and what became of the session image.</summary>
/// <param name="Status"><c>"cleared"</c>, or <c>"nothing_staged"</c> when there was nothing in scope to clear.</param>
/// <param name="Scope"><c>"all"</c>, or the routine the caller named.</param>
/// <param name="ClearedRoutines">Each routine whose staged rungs were cleared.</param>
/// <param name="ClearedRungCount">Total staged rungs cleared.</param>
/// <param name="ClearedTagNames">Names of the staged tag declarations cleared.</param>
/// <param name="TagsRetained">Staged tag declarations left in place because another routine still holds staged rungs.</param>
/// <param name="ControllerTouched"><c>false</c>: nothing was sent to or removed from the controller; <c>true</c> only when an original rung comment was written back (<c>commentsRestored</c>).</param>
/// <param name="Note">Plain-language statement of what was removed from where.</param>
/// <param name="ImageRestored">
/// <c>true</c> when the session image was rebuilt and a re-export of every cleared routine matched the image recorded before it
/// was first staged (rung count, types and text), or, for a routine with no such record, showed no staged rung; the note says which.
/// Omitted when nothing in the image needed restoring.
/// </param>
/// <param name="ReplayedBuckets">Staged buckets of other routines replayed into the rebuilt image; omitted when none.</param>
/// <param name="CommentsRestored">
/// Original rung comments written back because a staged replace had overwritten them, as
/// <c>routine#rung</c>; omitted when none.
/// </param>
public record ClearStagedResult(
    [property: JsonPropertyName("status")]            string                    Status,
    [property: JsonPropertyName("scope")]             string                    Scope,
    [property: JsonPropertyName("clearedRoutines")]   IReadOnlyList<ClearedRoutine> ClearedRoutines,
    [property: JsonPropertyName("clearedRungCount")]  int                       ClearedRungCount,
    [property: JsonPropertyName("clearedTagNames")]   IReadOnlyList<string>     ClearedTagNames,
    [property: JsonPropertyName("tagsRetained")]      int                       TagsRetained,
    [property: JsonPropertyName("controllerTouched")] bool                      ControllerTouched,
    [property: JsonPropertyName("note")]              string                    Note,
    [property: JsonPropertyName("imageRestored"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ImageRestored = null,
    [property: JsonPropertyName("replayedBuckets"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ReplayedBuckets = null,
    [property: JsonPropertyName("commentsRestored"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? CommentsRestored = null);

/// <summary>Structured <c>logix_clear_staged</c> refusal.</summary>
/// <param name="Error">What went wrong.</param>
/// <param name="Hint">What to do next; omitted when <c>null</c>.</param>
/// <param name="Kind">Machine-readable kind (<c>ClearIncomplete</c> when the image could not be restored or verified); omitted when <c>null</c>.</param>
/// <param name="ImageRestored"><c>false</c> on <c>ClearIncomplete</c>; omitted otherwise.</param>
/// <param name="LeftoverRungs">The rungs of the session image that still differ from the pre-stage image (staged rungs left behind, changed, missing or extra rungs), each prefixed with its routine; omitted when none were found.</param>
/// <param name="ClearedRoutines">The routines whose bookkeeping was cleared; omitted otherwise.</param>
public record ClearStagedError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("hint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint = null,
    [property: JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null,
    [property: JsonPropertyName("imageRestored"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ImageRestored = null,
    [property: JsonPropertyName("leftoverRungs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? LeftoverRungs = null,
    [property: JsonPropertyName("clearedRoutines"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ClearedRoutine>? ClearedRoutines = null);

/// <summary>
/// Handler for <c>logix_clear_staged</c>: discards the session's staged rungs and tags for one
/// routine, or for every routine when none is named, <b>and returns the session image to its pre-stage state</b>.
///
/// <para>
/// A workstation stage imports the rungs into the SDK session image with <c>LeaveEdits</c>, so emptying the
/// server's bookkeeping alone would leave them there and every later import into that program would fail with
/// <c>PendingEditsBlockImport</c>. On an online session the clear therefore rebuilds the image from the controller (go offline,
/// <c>UploadAsync</c>, go online: the upload <c>logix_connect</c> performs), replays the staged imports that remain
/// (other routines, when the clear was scoped), then re-exports every cleared routine and compares it rung by rung (count, type,
/// text via <see cref="RungTextComparer"/>) with the routine as it was exported just before its first stage. A routine with no such
/// record gets only the narrower "no e/er/dN rung left" check, and the note says so. Routines that keep staged rungs are checked for
/// their staged rungs and their original rungs.
/// A match returns <c>imageRestored: true</c>. Anything else returns <c>kind: ClearIncomplete</c> and the session
/// refuses stage and publish in that program until a clear verifies it or the caller reconnects.
/// </para>
///
/// <para>
/// Staged tag declarations are held in the server's buffer and are never imported into the image. They are not recorded
/// per routine, so a single-routine clear removes them only when no other routine still holds staged rungs.
/// A session that is not online cannot stage rungs, so no staged rung is ever written into the open project file.
/// </para>
/// </summary>
public static class ClearStagedHandler
{
    /// <summary>Kind of the refusal returned while a publish is in flight in the same session.</summary>
    public const string PublishInProgressKind = "PublishInProgress";

    private const string ReconnectHint = "reconnect with logix_connect to discard the session image";

    /// <summary>
    /// Clears the staged rungs and tags for <paramref name="routine"/>, or for all routines when it is
    /// <c>null</c> or blank, restores and verifies the session image, and writes a <c>staged_cleared</c> audit line.
    /// </summary>
    /// <param name="session">The open SDK session.</param>
    /// <param name="routine">
    /// Routine name (e.g. <c>MainRoutine</c>) or full routine XPath; <c>null</c> clears every routine.
    /// </param>
    /// <param name="program">Optional program name that narrows a bare routine name; ignored for an XPath.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ClearStagedResult"/>, or a <see cref="ClearStagedError"/> (<c>ClearIncomplete</c>, <c>SdkServerFaulted</c>).</returns>
    public static async Task<object> ExecuteAsync(
        LogixProjectSession session, string? routine = null, string? program = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Dead-session short-circuit: a faulted SDK server is never called again.
        if (session.IsFaulted)
        {
            var fault = session.FaultedError();
            return new ClearStagedError(fault.Message, fault.Hint, ErrorKinds.SdkServerFaulted);
        }

        // A server fault during the rebuild below is recorded as a fault during "clear", so the later fast-fails say so.
        using var operation = session.BeginOperation(RockwellErrorHints.OperationClear);

        // Publish and clear_staged are mutually exclusive within a session. A clear that
        // ran mid-publish would rebuild the image under the import and judge the just-published rung a staging
        // leftover. Refuse before anything is read or changed; the publish is untouched and the session stays usable.
        if (session.PublishInFlight)
            return new ClearStagedError(
                "A publish is in progress in this session. logix_clear_staged was refused and changed nothing: " +
                "the staged rungs, staged tags and session image are exactly as they were.",
                Hint: "Wait for the logix_propose_rung_edit publish to return, then call logix_clear_staged again if it is still needed.",
                Kind: PublishInProgressKind);

        bool all = string.IsNullOrWhiteSpace(routine);
        var scope = all ? "all" : routine!.Trim();

        bool InScope(string xpath) => all || Matches(xpath, scope, program);

        var keys = session.StagedRungBuffer.Keys.Where(k => InScope(k.RoutineXPath)).ToList();
        // Replace counts can outlive an emptied bucket; sweep them with the same scope.
        var replaceKeys = session.StagedReplaceCounts.Keys.Where(k => InScope(k.RoutineXPath)).ToList();

        var cleared = keys
            .GroupBy(k => k.RoutineXPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ClearedRoutine(
                g.Key,
                g.Count(),
                g.Sum(k => session.StagedRungBuffer[k].Count)))
            .OrderBy(c => c.RoutineXPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int rungCount = cleared.Sum(c => c.Rungs);

        // The staged texts a cleared routine must no longer show, and every routine whose image has to be restored:
        // the routines with buckets, the routines with a recorded import (a staged delete has no bucket), and the
        // routines an earlier clear left unverified (the rebuild covers the whole image, so those are re-verified in any scope).
        var staleByRoutine = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
        {
            if (!staleByRoutine.TryGetValue(k.RoutineXPath, out var l)) staleByRoutine[k.RoutineXPath] = l = new List<string>();
            l.AddRange(session.StagedRungBuffer[k]);
        }
        foreach (var import in session.StagedImports.Where(i => InScope(i.RoutineXPath)))
            if (!staleByRoutine.ContainsKey(import.RoutineXPath)) staleByRoutine[import.RoutineXPath] = new List<string>();
        foreach (var (unverifiedXPath, texts) in session.UnverifiedClears)
        {
            if (!staleByRoutine.TryGetValue(unverifiedXPath, out var l)) staleByRoutine[unverifiedXPath] = l = new List<string>();
            l.AddRange(texts);
        }

        foreach (var k in keys) session.StagedRungBuffer.Remove(k);
        foreach (var k in replaceKeys) session.StagedReplaceCounts.Remove(k);
        foreach (var k in replaceKeys) session.StagedKeptNormal.Remove(k);
        // Kept-normal sets follow their bucket: sweep any whose key is in scope but had no replace count.
        foreach (var k in session.StagedKeptNormal.Keys.Where(k => InScope(k.RoutineXPath)).ToList())
            session.StagedKeptNormal.Remove(k);
        foreach (var k in session.StagedUnchangedReplacements.Keys.Where(k => InScope(k.RoutineXPath)).ToList())
            session.StagedUnchangedReplacements.Remove(k);
        session.ForgetStagedImports(InScope);
        // The original comments an explicit-comment replace overwrote, written back after the rebuild.
        var commentUndos = session.CommentUndos.Where(u => InScope(u.RoutineXPath)).ToList();
        session.ForgetCommentUndos(InScope);

        bool bufferEmpty = !session.StagedRungBuffer.Values.Any(b => b.Count > 0);

        // Tags are not keyed by routine: clear them when nothing else is still staged, keep them otherwise.
        List<string> tagNames;
        int tagsRetained = 0;
        if (all || bufferEmpty)
        {
            tagNames = session.StagedTagBuffer.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            session.StagedTagBuffer.Clear();
        }
        else
        {
            tagNames     = new List<string>();
            tagsRetained = session.StagedTagBuffer.Count;
        }

        bool keptStaged = !(all || bufferEmpty);
        if (!keptStaged)
        {
            session.HasStagedRungEdit = false;
            session.StagedRungCount   = 0;
            session.StagedReplacedRungTexts.Clear();
        }
        else
        {
            session.StagedRungCount = Math.Max(0, session.StagedRungCount - rungCount);
        }

        bool nothing = cleared.Count == 0 && tagNames.Count == 0 && staleByRoutine.Count == 0;

        // ── Restore and verify the session image ───────────────────────────────
        bool? imageRestored = null;
        IReadOnlyList<string>? commentsRestored = null;
        bool commentsTouchedController = false;
        int replayedBuckets = 0;
        string imageNote;

        if (staleByRoutine.Count == 0)
        {
            imageNote = session.IsOnline
                ? "No staged rung had been imported into this session's image, so nothing in the image needed restoring. "
                : "This session is not online, so it cannot stage rungs: none were written into the open project file. ";
        }
        else
        {
            var kept = session.StagedImports;
            var outcome = await RestoreImageAsync(session, staleByRoutine, kept, ct).ConfigureAwait(false);
            replayedBuckets = outcome.ReplayedBuckets;

            if (outcome.Failure is not null)
            {
                // The image may still hold staged rungs: refuse stage and publish in those programs until it is verified.
                foreach (var (xpath, texts) in staleByRoutine)
                    session.RecordUnverifiedClear(xpath, texts);
                foreach (var xpath in outcome.UnreplayedRoutines)
                    session.RecordUnverifiedClear(xpath, Array.Empty<string>());
                session.HasStagedRungEdit = true;

                AuditLogger.LogStagedCleared(
                    scope, cleared.Select(c => c.RoutineXPath), rungCount, tagNames, tagsRetained,
                    imageRestored: false, replayedBuckets: replayedBuckets);

                return new ClearStagedError(
                    "The staged bookkeeping was cleared, but this session's image could not be shown to match the " +
                    $"controller: {outcome.Failure}" +
                    (outcome.Leftover.Count > 0
                        ? $" {outcome.Leftover.Count} difference(s) from the pre-stage image were found."
                        : string.Empty),
                    Hint: ReconnectHint + ". Until then logix_propose_rung_edit refuses to stage or publish into the affected program(s).",
                    Kind: "ClearIncomplete",
                    ImageRestored: false,
                    LeftoverRungs: outcome.Leftover,
                    ClearedRoutines: cleared);
            }

            foreach (var xpath in staleByRoutine.Keys)
                session.ResolveUnverifiedClear(xpath);
            imageRestored = true;
            imageNote = outcome.Rebuilt;

            if (commentUndos.Count > 0)
            {
                var (restored, commentFailure) = await RestoreCommentsAsync(session, commentUndos, ct).ConfigureAwait(false);
                commentsRestored = restored;
                if (commentFailure is not null)
                {
                    foreach (var u in commentUndos.Where(u => !restored.Contains($"{u.RoutineXPath}#{u.RungIndex}")))
                        session.RecordCommentUndo(u);
                    AuditLogger.LogStagedCleared(
                        scope, cleared.Select(c => c.RoutineXPath), rungCount, tagNames, tagsRetained,
                        imageRestored: true, replayedBuckets: replayedBuckets);
                    return new ClearStagedError(
                        "The staged rungs were cleared and the session image was restored, but an original rung comment that an " +
                        $"explicit-comment replace overwrote could not be written back: {commentFailure}",
                        Hint: "set the rung comment again by hand (logix_propose_rung_edit with a comment, target='live') or in Studio, " +
                              "then check logix_pending_edits.",
                        Kind: "CommentRestoreFailed",
                        ImageRestored: true,
                        ClearedRoutines: cleared);
                }
                imageNote += $"{restored.Count} original rung comment(s) that a staged replace had overwritten were written back " +
                             "to the controller with the same rung import the stage used (AcceptEdits), so cancelling restored them. ";
                commentsTouchedController = restored.Count > 0;
            }
        }

        AuditLogger.LogStagedCleared(
            scope, cleared.Select(c => c.RoutineXPath), rungCount, tagNames, tagsRetained,
            imageRestored: imageRestored, replayedBuckets: replayedBuckets);

        return new ClearStagedResult(
            Status:            nothing ? "nothing_staged" : "cleared",
            Scope:             scope,
            ClearedRoutines:   cleared,
            ClearedRungCount:  rungCount,
            ClearedTagNames:   tagNames,
            TagsRetained:      tagsRetained,
            ControllerTouched: commentsTouchedController,
            Note:
                imageNote +
                "Cleared this server's staged-rung and staged-tag bookkeeping; nothing was sent to or " +
                "removed from the controller. " +
                (tagsRetained > 0
                    ? $"{tagsRetained} staged tag declaration(s) were kept because other routines still hold staged rungs " +
                      "(tags are not recorded per routine); call logix_clear_staged with no routine to clear them too. "
                    : "") +
                "Check logix_pending_edits before staging again.",
            ImageRestored:   imageRestored,
            ReplayedBuckets: imageRestored is null ? null : replayedBuckets,
            CommentsRestored: commentsRestored is { Count: > 0 } ? commentsRestored : null);
    }

    /// <summary>
    /// Writes back the original comment of each rung an explicit-comment replace overwrote, using
    /// the rung import the stage used (<c>PartialImportRungsFromXmlFileAsync</c>) with <c>AcceptEdits</c>: a rung comment is
    /// written to the controller immediately, so the rebuild from the controller still carries the new comment. A rung whose
    /// comment already is the original is left alone.
    /// </summary>
    /// <param name="session">The open SDK session.</param>
    /// <param name="undos">The undo records to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <c>routine#rung</c> keys written back, and the failure text or <c>null</c>.</returns>
    internal static async Task<(List<string> Restored, string? Failure)> RestoreCommentsAsync(
        LogixProjectSession session, IReadOnlyList<LogixProjectSession.CommentUndoEntry> undos, CancellationToken ct)
    {
        var restored = new List<string>();
        foreach (var undo in undos)
        {
            var key = $"{undo.RoutineXPath}#{undo.RungIndex}";
            string? tempFile = null;
            try
            {
                var export = await ExportRungsHandler.ExecuteAsync(undo.RoutineXPath, session, ct).ConfigureAwait(false);
                if (export is not ExportRungsResult ok)
                    return (restored, $"'{undo.RoutineXPath}' could not be exported to read rung {undo.RungIndex}'s comment.");
                var rungs = RoutineRungSelector.Select(SafeXml.ParseDocument(ok.L5X), undo.RoutineXPath);
                if (undo.RungIndex >= rungs.Count
                    || !RungTextComparer.Matches(
                        ProposeRungEditHandler.NormalizeRungText(rungs[undo.RungIndex].Element("Text")?.Value),
                        ProposeRungEditHandler.NormalizeRungText(undo.OriginalText)))
                    return (restored, $"rung {undo.RungIndex} of '{undo.RoutineXPath}' is no longer the rung whose comment was replaced.");
                if (string.Equals((rungs[undo.RungIndex].Element("Comment")?.Value ?? string.Empty).Trim(),
                                  undo.OriginalComment.Trim(), StringComparison.Ordinal))
                    continue;

                var fragment = BuildCommentRestoreFragment(undo);
                if (fragment is null)
                    return (restored, $"no import envelope was kept for rung {undo.RungIndex} of '{undo.RoutineXPath}'.");
                tempFile = Path.GetTempFileName();
                await File.WriteAllTextAsync(tempFile, fragment, ct).ConfigureAwait(false);
                var file = tempFile;
                await session.ExecuteAsync(
                    p => p.PartialImportRungsFromXmlFileAsync(
                        undo.RoutineXPath, (uint)undo.RungIndex, 1, file, PartialImportOption.AcceptEdits, ct),
                    ct).ConfigureAwait(false);
                restored.Add(key);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (restored, ex.Message);
            }
            finally
            {
                if (tempFile is not null) try { File.Delete(tempFile); } catch { /* best effort */ }
            }
        }
        return (restored, null);
    }

    /// <summary>
    /// The one-rung fragment that replaces the rung at the undo record's index with its original text and original comment,
    /// built on the envelope of the fragment the stage imported. <c>null</c> when that envelope has no rung to build on.
    /// </summary>
    internal static string? BuildCommentRestoreFragment(LogixProjectSession.CommentUndoEntry undo)
    {
        var doc = SafeXml.ParseDocument(undo.Fragment);
        var rungs = RoutineRungSelector.Select(doc, undo.RoutineXPath);
        if (rungs.Count == 0) return null;
        for (int i = 1; i < rungs.Count; i++) rungs[i].Remove();
        var rung = rungs[0];
        rung.SetAttributeValue("Number", "0");
        rung.SetAttributeValue("Type", "N");
        rung.Elements("Comment").Remove();
        rung.Elements("Text").Remove();
        rung.Add(new XElement("Text", new XCData(undo.OriginalText)));
        if (undo.OriginalComment.Length > 0)
            rung.AddFirst(new XElement("Comment", new XCData(undo.OriginalComment)));   // <Comment> precedes <Text>
        var declaration = doc.Declaration?.ToString() ?? new XDeclaration("1.0", "UTF-8", "yes").ToString();
        return declaration + "\r\n" + doc.Root!.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>What <see cref="RestoreImageAsync"/> found.</summary>
    private sealed record RestoreOutcome(
        string? Failure, string Rebuilt, int ReplayedBuckets, IReadOnlyList<string> Leftover, IReadOnlyList<string> UnreplayedRoutines);

    /// <summary>
    /// Rebuilds the session image from the controller (online) or from the source file (offline), replays the staged imports that
    /// remain, then exports each cleared routine and lists any staged rung still in it.
    /// </summary>
    private static async Task<RestoreOutcome> RestoreImageAsync(
        LogixProjectSession session,
        IReadOnlyDictionary<string, List<string>> staleByRoutine,
        IReadOnlyList<LogixProjectSession.StagedImport> kept,
        CancellationToken ct)
    {
        static RestoreOutcome Fail(string why, IEnumerable<string>? unreplayed = null, int replayed = 0, IReadOnlyList<string>? leftover = null) =>
            new(why, string.Empty, replayed, leftover ?? Array.Empty<string>(), (unreplayed ?? Array.Empty<string>()).ToList());

        string rebuilt;
        if (session.IsOnline)
        {
            // The same upload logix_connect performs. GoOffline first: a workstation edit lives only in the session image, and
            // going offline discards it; the upload then pulls the controller's image into the project.
            try
            {
                await session.ExecuteAsync(p => p.GoOfflineAsync(ct), ct).ConfigureAwait(false);
                await session.ExecuteAsync(p => p.UploadAsync(ct), ct).ConfigureAwait(false);
                await session.ExecuteAsync(p => p.GoOnlineAsync(ct), ct).ConfigureAwait(false);
                var state = await session.ExecuteAsync(p => p.ReadConnectedStateAsync(ct), ct).ConfigureAwait(false);
                if (state != ConnectedState.Online)
                    return Fail($"after the upload the session reported '{state}', not Online.", kept.Select(k => k.RoutineXPath).Distinct());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Fail($"rebuilding the session image from the controller failed: {ex.Message}", kept.Select(k => k.RoutineXPath).Distinct());
            }

            rebuilt = "The staged rungs were removed from this session's image: the image was rebuilt from the controller " +
                      "(upload). ";
        }
        else
        {
            var reloadFailure = await session.ReloadWorkingCopyAsync(ct).ConfigureAwait(false);
            if (reloadFailure is not null)
                return Fail($"this session is not online and its working copy could not be reloaded: {reloadFailure}",
                    kept.Select(k => k.RoutineXPath).Distinct());
            rebuilt = "The staged rungs were removed from this session's working copy of the project file by reloading it " +
                      "from the file you opened; the file itself was never written. ";
        }

        // Replay what remains staged, in the order it was staged.
        var replayedKeys = new HashSet<(string, uint)>();
        foreach (var import in kept)
        {
            var failure = await ReplayAsync(session, import, ct).ConfigureAwait(false);
            if (failure is not null)
                return Fail(
                    $"the staged rungs of '{import.RoutineXPath}' that were kept could not be replayed into the rebuilt image: {failure}",
                    kept.Select(k => k.RoutineXPath).Distinct(), replayedKeys.Count);
            replayedKeys.Add((import.RoutineXPath, import.InsertPosition));
        }

        // Verify every routine the rebuild touched: the cleared ones and the ones whose kept imports were replayed.
        var keptRoutines = new HashSet<string>(kept.Select(k => k.RoutineXPath), StringComparer.OrdinalIgnoreCase);
        var toVerify = staleByRoutine.Keys.Concat(keptRoutines).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        var leftover = new List<string>();
        var weaker   = new List<string>();
        int compared = 0;
        foreach (var xpath in toVerify)
        {
            var (readable, rungs, error) = await ReadRoutineRungsAsync(xpath, session, ct).ConfigureAwait(false);
            if (!readable)
                return Fail($"'{xpath}' could not be exported to verify the image: {error}", null, replayedKeys.Count);

            var baseline = session.GetRoutineBaseline(xpath);
            if (baseline is null && !keptRoutines.Contains(xpath)) weaker.Add(xpath);
            else if (baseline is not null && !keptRoutines.Contains(xpath)) compared++;

            var stagedTexts = session.StagedRungBuffer
                .Where(b => string.Equals(b.Key.RoutineXPath, xpath, StringComparison.OrdinalIgnoreCase))
                .SelectMany(b => b.Value).ToList();
            var diffs = keptRoutines.Contains(xpath)
                ? VerifyKeptRoutine(baseline, stagedTexts, rungs)
                : (baseline is not null ? CompareToBaseline(baseline, rungs) : FindStagedZoneRungs(rungs));
            leftover.AddRange(diffs.Select(d => $"{xpath}: {d}"));
        }
        if (leftover.Count > 0)
            return Fail("a re-export of the routine(s) does not match the image before anything was staged.", null, replayedKeys.Count, leftover);

        var check = string.Empty;
        if (compared > 0)
            check += "Each cleared routine with a record of its image before it was first staged was re-exported and compared with that " +
                     "record (rung count, rung types and rung text, using the publish census comparer), so it matches the controller image " +
                     "as it was before staging. ";
        if (weaker.Count > 0)
            check += $"For {string.Join(", ", weaker.Select(w => $"'{w}'"))} no pre-stage image was recorded, so the only check was that " +
                     "no staged rung (type e, er or dN) is left in it; a change that is not a staged rung would not be caught. ";
        if (keptRoutines.Count > 0)
            check += "A routine that keeps staged rungs was checked for its staged rungs and its original rungs, not rung by rung. ";

        return new RestoreOutcome(null, rebuilt + check, replayedKeys.Count, Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>Re-imports one staged fragment with <c>LeaveEdits</c>; returns the failure text, or <c>null</c>.</summary>
    private static async Task<string?> ReplayAsync(
        LogixProjectSession session, LogixProjectSession.StagedImport import, CancellationToken ct)
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, import.Fragment, ct).ConfigureAwait(false);
            await session.ExecuteAsync(
                p => p.PartialImportRungsFromXmlFileAsync(
                    import.RoutineXPath, import.InsertPosition, import.ReplaceCount, file, PartialImportOption.LeaveEdits, ct),
                ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
        finally
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Exports <paramref name="routineXPath"/> from the session image and returns every rung's type and normalised text,
    /// in order. Never throws (cancellation excepted).
    /// </summary>
    /// <param name="routineXPath">The routine XPath.</param>
    /// <param name="session">The open SDK session.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>(true, rungs, null)</c>, or <c>(false, empty, reason)</c> when the export or its parse failed.</returns>
    internal static async Task<(bool Readable, IReadOnlyList<LogixProjectSession.BaselineRung> Rungs, string? Error)> ReadRoutineRungsAsync(
        string routineXPath, LogixProjectSession session, CancellationToken ct)
    {
        var none = Array.Empty<LogixProjectSession.BaselineRung>();
        object export;
        try
        {
            export = await ExportRungsHandler.ExecuteAsync(routineXPath, session, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, none, ex.Message);
        }
        if (export is not ExportRungsResult ok)
            return (false, none, (export as ExportRungsError)?.Error ?? "the export returned no result");

        try
        {
            var doc = SafeXml.ParseDocument(ok.L5X);
            var rungs = RoutineRungSelector.Select(doc, routineXPath)
                .Select(r => new LogixProjectSession.BaselineRung(
                    string.IsNullOrEmpty((string?)r.Attribute("Type")) ? "N" : (string)r.Attribute("Type")!,
                    ProposeRungEditHandler.NormalizeRungText(r.Element("Text")?.Value)))
                .ToList();
            return (true, rungs, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, none, ex.Message);
        }
    }

    /// <summary>
    /// Compares a routine's rungs with the image recorded before anything was staged: same rung count, same type and the
    /// same text per rung (<see cref="RungTextComparer.Matches"/>, the comparer the publish census uses).
    /// </summary>
    /// <param name="baseline">The pre-stage rungs.</param>
    /// <param name="actual">The rungs of the restored image.</param>
    /// <returns>One line per difference; empty when the image matches.</returns>
    internal static IReadOnlyList<string> CompareToBaseline(
        IReadOnlyList<LogixProjectSession.BaselineRung> baseline, IReadOnlyList<LogixProjectSession.BaselineRung> actual)
    {
        var diffs = new List<string>();
        for (int i = 0; i < Math.Max(baseline.Count, actual.Count); i++)
        {
            if (i >= actual.Count)
                diffs.Add($"rung {i} is missing (expected type {baseline[i].Type}: {baseline[i].Text})");
            else if (i >= baseline.Count)
                diffs.Add($"rung {i} was not there before staging (type {actual[i].Type}): {actual[i].Text}");
            else if (!string.Equals(baseline[i].Type, actual[i].Type, StringComparison.Ordinal)
                     || !RungTextComparer.Matches(baseline[i].Text, actual[i].Text))
                diffs.Add($"rung {i} differs: before staging type {baseline[i].Type} '{baseline[i].Text}', now type {actual[i].Type} '{actual[i].Text}'");
        }
        return diffs;
    }

    /// <summary>
    /// The narrower check for a routine with no pre-stage record: the rungs in an online-edit <c>Type</c> zone that staging
    /// leaves behind (<c>e</c>, <c>er</c>, <c>dN</c>). A program that holds such a zone cannot take an import at all, so none can
    /// be the controller's own.
    /// </summary>
    private static IReadOnlyList<string> FindStagedZoneRungs(IReadOnlyList<LogixProjectSession.BaselineRung> rungs) =>
        rungs.Select((r, i) => (Rung: r, Index: i))
             .Where(x => PendingEditsHandler.ClassifyRungType(x.Rung.Type) == PendingEditsHandler.RungEditZone.Tested)
             .Select(x => $"rung {x.Index} (type {x.Rung.Type}): {x.Rung.Text}")
             .ToList();

    /// <summary>
    /// Checks a routine whose kept staged imports were replayed: every staged text still held for it must be in the
    /// image, and, when a pre-stage record exists, so must every original rung. Rung-by-rung comparison does not apply because
    /// the staged rungs legitimately change the rung list.
    /// </summary>
    private static IReadOnlyList<string> VerifyKeptRoutine(
        IReadOnlyList<LogixProjectSession.BaselineRung>? baseline, IReadOnlyList<string> stagedTexts,
        IReadOnlyList<LogixProjectSession.BaselineRung> actual)
    {
        var texts = actual.Select(r => r.Text).ToList();
        var diffs = new List<string>();
        foreach (var t in stagedTexts)
            if (!RungTextComparer.ContainsMatch(texts, t))
                diffs.Add($"a kept staged rung is missing after the replay: {t}");
        if (baseline is not null)
            foreach (var b in baseline)
                if (!RungTextComparer.ContainsMatch(texts, b.Text))
                    diffs.Add($"an original rung is missing after the replay: {b.Text}");
        return diffs;
    }

    /// <summary>
    /// True when <paramref name="routineXPath"/> is the routine the caller named: an exact XPath match, or a
    /// routine-name match (optionally narrowed to a program), case-insensitively.
    /// </summary>
    private static bool Matches(string routineXPath, string routine, string? program)
    {
        if (routine.Contains('/'))
            return string.Equals(routineXPath, routine, StringComparison.OrdinalIgnoreCase);

        var routineMatch = ProposeRungEditHandler.RoutineNamePattern.Match(routineXPath);
        if (!routineMatch.Success
            || !string.Equals(routineMatch.Groups[1].Value, routine, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(program)) return true;
        var programMatch = ProposeRungEditHandler.ProgramNamePattern.Match(routineXPath);
        return programMatch.Success
            && string.Equals(programMatch.Groups[1].Value, program.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
#endif
