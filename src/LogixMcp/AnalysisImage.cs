using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using L5Sharp.Core;

/// <summary>A pending rung that writes an analysed tag but was left out because <c>includePending</c> is false.</summary>
/// <param name="Program">Program holding the rung.</param>
/// <param name="Routine">Routine holding the rung.</param>
/// <param name="RungIndex">Rung number in the session image (the pending rung's own index).</param>
/// <param name="Tag">The analysed tag the rung writes.</param>
public sealed record PendingWriter(
    [property: JsonPropertyName("program")]   string Program,
    [property: JsonPropertyName("routine")]   string Routine,
    [property: JsonPropertyName("rungIndex")] int    RungIndex,
    [property: JsonPropertyName("tag")]       string Tag);

/// <summary>
/// Chooses which controller image the static analysis tools (<c>logix_cross_reference</c>,
/// <c>logix_verify_interlock</c>) read: the loaded project file, or the active session's controller
/// image (the <c>includePending</c> option, as lint does).
/// </summary>
internal static class AnalysisImage
{
    /// <summary>Note reported when a tag is on the controller but missing from the project file.</summary>
    internal const string FileOlderNote =
        "project file is older than the controller: this tag exists on the controller but not in the loaded project file. " +
        "Pass includePending: true to read the session's controller image.";

    /// <summary>The image chosen for one analysis call.</summary>
    /// <param name="Project">The model to analyse, or <c>null</c> when none is available.</param>
    /// <param name="Source">The <see cref="ProjectSourceLabel"/> of <paramref name="Project"/>.</param>
    /// <param name="ControllerImage">
    /// The session's controller image when the project file (not the image) is being read, otherwise <c>null</c>;
    /// used to detect tags the file lacks.
    /// </param>
    /// <param name="ImageNote">
    /// Set when the session's controller image is stale and could not be refreshed, so the result may miss
    /// same-session changes; analysis must report it.
    /// </param>
    /// <param name="PendingImage">
    /// Set when pending rungs were left out of the analysis (<c>includePending</c> is false): an image holding only those
    /// pending-only rungs, so the callers can look for pending writers. <c>null</c> when nothing was left out.
    /// </param>
    internal sealed record Selection(
        L5X? Project, string Source, L5X? ControllerImage, string? ImageNote = null, L5X? PendingImage = null);

    /// <summary>
    /// Picks the model to analyse. <paramref name="includePending"/> defaults to <c>true</c> when the session
    /// targets a live (SDK-online) controller and <c>false</c> otherwise; it only takes effect when the session
    /// holds a controller image.
    /// </summary>
    /// <param name="service">Project holder (open file, or the last upload).</param>
    /// <param name="session">Tag session, or <c>null</c> when none is attached.</param>
    /// <param name="includePending">Explicit request, or <c>null</c> for the default.</param>
    /// <returns>The chosen <see cref="Selection"/>.</returns>
    /// <remarks>
    /// <c>includePending: true</c> reads the session image (staged rungs included); <c>false</c> reads the controller's
    /// accepted image (no staged rungs) as of the last refresh, not the project file. Only a file the user opened
    /// explicitly (<c>logix_open_project</c>) is read instead; the accepted image is then what that file is compared
    /// with for the "project file is older" note, which therefore fires only when the analysis falls back to the file. Callers refresh a stale image first
    /// (<see cref="PrepareAsync"/>); one that could not be refreshed is reported through
    /// <see cref="Selection.ImageNote"/>.
    /// </remarks>
    internal static Selection Resolve(ProjectService service, TagSessionService? session, bool? includePending)
    {
        var image     = session?.ControllerImage;
        var accepted  = session?.AcceptedControllerImage;
        var include   = includePending ?? (session?.IsLiveController == true);
        var imageNote = session?.ImageStaleNote;
        // A session image this session could not keep current (accept / assemble / cancel it could not apply) says so
        // on every read that uses it, whether the session or the accepted image is read.
        var usesImage = (include && image is not null) || (accepted is not null && (service.Project is null || (includePending == false && !service.OpenedExplicitly)));
        if (usesImage && SessionImageState.StalenessOf(session) is { } stale)
            imageNote = imageNote is null ? stale.Note : $"{imageNote} {stale.Note}";
        if (include && image is not null)
            return new Selection(image, ProjectSourceLabel.SessionImage, null, imageNote);
        // Pending rungs are left out of everything below; keep them so a pending writer is reported, not hidden.
        // The accepted image is the session image itself when nothing is pending, so that case needs no scan.
        var pending = image is not null && !ReferenceEquals(accepted, image) ? ControllerImageRefresh.PendingOnly(image) : null;
        // includePending: false reads the controller's accepted image (as of the last refresh), so a rung or tag
        // this session just published is seen. Only a project file the user opened explicitly is read instead:
        // that is a deliberate choice of file, and the "project file is older" note then covers what it lacks.
        // A default of false (offline session) keeps reading the file; only an explicit false prefers the image.
        if (accepted is not null && (service.Project is null || (includePending == false && !service.OpenedExplicitly)))
            return new Selection(accepted, ProjectSourceLabel.AcceptedImage, null, imageNote, pending);
        return new Selection(service.Project, ProjectSourceLabel.ProjectFile, accepted, imageNote, pending);
    }

    /// <summary>
    /// Refreshes the session's controller image when this session changed the controller since it was taken, so the
    /// analysis that follows sees the change. A failed refresh is not fatal: the result then carries the stale note.
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async System.Threading.Tasks.Task PrepareAsync(TagSessionService? session, CancellationToken ct = default)
    {
        if (session is { IsImageStale: true })
            await session.RefreshControllerImageAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns <see cref="FileOlderNote"/> when any of <paramref name="tagNames"/> is declared by the controller
    /// image but not by the project file being analysed; otherwise <c>null</c>.
    /// </summary>
    /// <param name="selection">The chosen image.</param>
    /// <param name="tagNames">Tag names as the caller gave them.</param>
    internal static string? StaleNote(Selection selection, params string[] tagNames)
    {
        var older = FileOlderNoteFor(selection, tagNames);
        if (selection.ImageNote is null) return older;
        return older is null ? selection.ImageNote : $"{older} {selection.ImageNote}";
    }

    private static string? FileOlderNoteFor(Selection selection, string[] tagNames)
    {
        if (selection.ControllerImage is not { } image || selection.Project is not { } file) return null;
        var fileScopes  = new TagScopes(file);
        var imageScopes = new TagScopes(image);
        foreach (var name in tagNames)
        {
            var (_, tag) = CrossReferenceHandler.ResolveTarget(image, name);
            var bare = WriteClassifier.ExtractBaseName(tag) ?? tag;
            if (imageScopes.Declares(bare) && !fileScopes.Declares(bare)) return FileOlderNote;
        }
        return null;
    }

    /// <summary>
    /// Pending writers (<see cref="PendingWriter"/>) the analysis left out: those <paramref name="find"/> reports among the pending-only rungs of
    /// <see cref="Selection.PendingImage"/>. Empty when nothing was left out.
    /// </summary>
    /// <param name="selection">The chosen image.</param>
    /// <param name="find">
    /// Given the image of pending rungs, returns the pending writers of the analysed tag(s). The handler passes
    /// the same matching it uses for accepted logic, so both sides apply one rule.
    /// </param>
    /// <returns>The pending writers, in program/routine/rung order.</returns>
    internal static IReadOnlyList<PendingWriter> PendingWriters(Selection selection, Func<L5X, IEnumerable<PendingWriter>> find) =>
        selection.PendingImage is { } pending
            ? find(pending).DistinctBy(w => (w.Program, w.Routine, w.RungIndex, w.Tag)).ToList()
            : Array.Empty<PendingWriter>();

    /// <summary>"1 pending rung (Main rung 2)" / "2 pending rungs (Main rung 2, Main rung 3)".</summary>
    /// <param name="writers">The pending writers.</param>
    internal static string DescribePending(IReadOnlyList<PendingWriter> writers)
    {
        var where = string.Join(", ", writers.Select(w => $"{w.Routine} rung {w.RungIndex}").Distinct());
        var count = writers.Select(w => (w.Program, w.Routine, w.RungIndex)).Distinct().Count();
        return $"{count} pending rung{(count == 1 ? "" : "s")} ({where})";
    }

    /// <summary>
    /// Message for a tag no accepted rung writes while pending rungs do:
    /// "X is not written by any accepted rung; 1 pending rung (Main rung 2) writes it. Pass includePending:true to include it."
    /// </summary>
    /// <param name="subject">How the message names the tag (for example <c>Output tag 'X'</c>).</param>
    /// <param name="writers">The pending writers (non-empty).</param>
    internal static string NotWrittenByAccepted(string subject, IReadOnlyList<PendingWriter> writers)
    {
        var count = writers.Select(w => (w.Program, w.Routine, w.RungIndex)).Distinct().Count();
        return $"{subject} is not written by any accepted rung; {DescribePending(writers)} {(count == 1 ? "writes" : "write")} it. " +
               "Pass includePending:true to include it.";
    }

    /// <summary>Note for a tag that accepted rungs write and pending rungs also write.</summary>
    /// <param name="tag">The tag as the caller named it.</param>
    /// <param name="writers">The pending writers (non-empty).</param>
    internal static string AlsoWrittenByPending(string tag, IReadOnlyList<PendingWriter> writers)
    {
        var count = writers.Select(w => (w.Program, w.Routine, w.RungIndex)).Distinct().Count();
        return $"{DescribePending(writers)} also {(count == 1 ? "writes" : "write")} '{tag}' and {(count == 1 ? "was" : "were")} excluded. " +
               "Pass includePending:true to include it.";
    }
}

/// <summary>
/// Keeps the session's controller image in step with pending edits this session published, then accepted, assembled or
/// cancelled through Studio, or marks it stale when it cannot.
/// </summary>
/// <remarks>
/// <para>
/// A publish this session made is recorded with its rungs and replace range (<see cref="RecordPublish"/>); the same
/// merge the staging path uses puts the rungs into the image as pending. On accept or assemble the recorded change set
/// is applied to the image as accepted logic (pending rungs become ordinary rungs, replaced originals go). On cancel
/// the routine's rungs are restored to what they were before the publish.
/// </para>
/// <para>
/// When the zone is not one this session published, its rungs do not match the record, or the merge fails, the image is
/// marked stale instead: every session-image response then carries <c>imageStale</c>, <c>staleSince</c>,
/// <c>staleReason</c> and the reconnect hint (<see cref="ProjectSourceLabel.Staleness"/>). The mark belongs to the
/// image it was set on: a reconnect attaches a new session service and takes a new image, which clears it.
/// </para>
/// </remarks>
internal static class SessionImageState
{
    /// <summary>Rung <c>Type</c> codes of rungs a pending zone adds (insert and replacement).</summary>
    private static readonly HashSet<string> NewRungCodes = new(StringComparer.Ordinal) { "e", "er", "I", "IR" };

    private sealed record Publish(
        string Program, string Routine, int InsertPosition, int ReplaceCount,
        IReadOnlyList<string> Rungs, bool AppliedPending, XElement? PreContent, IReadOnlyList<string?>? Comments = null);

    private sealed class State
    {
        public object?         Service;
        public DateTimeOffset? TakenAt;
        public DateTimeOffset? StaleSince;
        public string?         StaleReason;
        public readonly List<Publish> Publishes = new();
        public readonly HashSet<string> Unfollowed = new(StringComparer.OrdinalIgnoreCase);

        public void Reset(object? service, DateTimeOffset? takenAt)
        {
            Service = service; TakenAt = takenAt;
            StaleSince = null; StaleReason = null;
            Publishes.Clear();
            Unfollowed.Clear();
        }
    }

    private static readonly ConditionalWeakTable<TagSessionService, State> States = new();
    private static readonly object Gate = new();

    /// <summary>The state of the session's current image, or <c>null</c> when it holds none. A new image (reconnect) starts clean.</summary>
    private static State? Current(TagSessionService? session)
    {
        if (session?.ControllerImage is null) return null;
        var state = States.GetOrCreateValue(session);
        if (!ReferenceEquals(state.Service, session.Service) || state.TakenAt != session.ControllerImageTakenAt)
            state.Reset(session.Service, session.ControllerImageTakenAt);
        return state;
    }

    /// <summary>The stale mark on the session's current image, or <c>null</c> when it is not marked.</summary>
    /// <param name="session">Tag session, or <c>null</c>.</param>
    internal static SessionImageStaleness? StalenessOf(TagSessionService? session)
    {
        lock (Gate)
            return Current(session) is { StaleSince: { } since, StaleReason: { } reason }
                ? new SessionImageStaleness(since, reason)
                : null;
    }

    /// <summary>
    /// Marks the session's current image stale (the first mark wins, so <c>staleSince</c> does not move). Does nothing
    /// when the session holds no image.
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c>.</param>
    /// <param name="reason">What the image could not follow.</param>
    internal static void MarkStale(TagSessionService? session, string reason)
    {
        lock (Gate)
        {
            if (Current(session) is not { } state) return;
            state.StaleSince  ??= DateTimeOffset.UtcNow;
            state.StaleReason ??= reason;
        }
    }

    /// <summary>
    /// Records a publish this session made as pending edits, and (by default) merges it into the session image as
    /// pending rungs: the replaced originals become <c>R</c>/<c>D</c> and the new rungs <c>IR</c>/<c>I</c>.
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c>.</param>
    /// <param name="program">Program holding the routine.</param>
    /// <param name="routine">Routine that was published to.</param>
    /// <param name="insertPosition">Rung position the change starts at.</param>
    /// <param name="replaceCount">How many existing rungs from <paramref name="insertPosition"/> the change replaces.</param>
    /// <param name="rungTexts">Neutral text of the new rungs, in order (the first <paramref name="replaceCount"/> replace, the rest insert).</param>
    /// <param name="applyToImage"><c>false</c> to only record the change set and leave the image alone.</param>
    /// <param name="rungComments">
    /// Comment each new rung carries on the controller, parallel to <paramref name="rungTexts"/> (<c>null</c> or an empty
    /// entry for none). The merge writes them into the image, so the rungs keep their comments on accept.
    /// <c>null</c> (or a list of another length) means no comments are known and the new rungs carry none.
    /// </param>
    /// <returns><c>true</c> when the change set was recorded (and, if asked, merged); otherwise <c>false</c> and nothing changed.</returns>
    internal static bool RecordPublish(
        TagSessionService? session, string program, string routine, int insertPosition, int replaceCount,
        IReadOnlyList<string> rungTexts, bool applyToImage = true, IReadOnlyList<string?>? rungComments = null)
    {
        ArgumentNullException.ThrowIfNull(rungTexts);
        if (insertPosition < 0 || replaceCount < 0 || (rungTexts.Count == 0 && replaceCount == 0)) return false;
        var comments = rungComments is not null && rungComments.Count == rungTexts.Count ? rungComments.ToList() : null;
        lock (Gate)
        {
            if (Current(session) is not { } state) return false;
            XElement? pre = null;
            if (applyToImage)
            {
                try
                {
                    var doc = SafeXml.ParseDocument(session!.ControllerImage!.ToString());
                    if (FindContent(doc, program, routine) is not { } content) return false;
                    pre = new XElement(content);
                    if (!Splice(content, insertPosition, replaceCount, rungTexts, pending: true, comments)) return false;
                    Replace(session, doc);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
            }
            state.Publishes.Add(new Publish(program, routine, insertPosition, replaceCount, rungTexts.ToList(), applyToImage, pre, comments));
            return true;
        }
    }

    /// <summary>
    /// Notes that this session published pending edits to <paramref name="program"/> in a shape the image cannot follow
    /// (for example landed rungs that do not start at the reported insert position, or a publish to several routines).
    /// The next accept, assemble or cancel of that program's zone marks the image stale with that as the reason.
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c>.</param>
    /// <param name="program">Program the unrecorded publish went to.</param>
    internal static void RecordUnfollowedPublish(TagSessionService? session, string program)
    {
        lock (Gate)
        {
            if (Current(session) is not { } state) return;
            state.Unfollowed.Add(program);
        }
    }

    /// <summary>A routine of the pending zone and the pending rungs (type code, text) it holds.</summary>
    /// <param name="Routine">Routine name.</param>
    /// <param name="Rungs">The routine's pending rungs as <c>logix_pending_edits</c> reports them.</param>
    internal sealed record ZoneRoutine(string Routine, IReadOnlyList<(string Type, string Text)> Rungs);

    /// <summary>
    /// A pending zone of <paramref name="program"/> was accepted or assembled: applies the change sets this session
    /// published to the image when they are exactly what the zone held, otherwise marks the image stale.
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c>.</param>
    /// <param name="program">Program whose zone was accepted.</param>
    /// <param name="zone">The routines that held pending edits before the gesture, or <c>null</c> when that is not known.</param>
    /// <param name="what">The gesture, for the stale reason (for example <c>accepted</c>).</param>
    internal static void OnAccepted(TagSessionService? session, string program, IReadOnlyList<ZoneRoutine>? zone, string what)
    {
        lock (Gate)
        {
            if (Current(session) is not { } state) return;
            var records = TakeRecords(state, program);
            var reason  = state.Unfollowed.Remove(program)
                ? UnfollowedReason(program, what)
                : TryAccept(session!, program, zone, records, what);
            if (reason is not null) MarkStaleLocked(state, reason);
        }
    }

    /// <summary>
    /// A pending zone of <paramref name="program"/> was cancelled: restores each routine this session published to
    /// to its rungs from before the publish when the image was changed for it, otherwise marks the image stale.
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c>.</param>
    /// <param name="program">Program whose zone was cancelled.</param>
    /// <param name="zone">The routines that held pending edits before the gesture, or <c>null</c> when that is not known.</param>
    internal static void OnCancelled(TagSessionService? session, string program, IReadOnlyList<ZoneRoutine>? zone)
    {
        lock (Gate)
        {
            if (Current(session) is not { } state) return;
            var records = TakeRecords(state, program);
            var reason  = state.Unfollowed.Remove(program)
                ? UnfollowedReason(program, "cancelled")
                : TryCancel(session!, program, zone, records);
            if (reason is not null) MarkStaleLocked(state, reason);
        }
    }

    private static string UnfollowedReason(string program, string what) =>
        $"{what} pending edits in program '{program}' that this session published in a shape the session image cannot " +
        "follow (the landed rungs are not one block starting at the reported insert position, or span several routines); " +
        "the session image was not updated";

    private static void MarkStaleLocked(State state, string reason)
    {
        state.StaleSince  ??= DateTimeOffset.UtcNow;
        state.StaleReason ??= reason;
    }

    private static List<Publish> TakeRecords(State state, string program)
    {
        var records = state.Publishes.Where(p => Same(p.Program, program)).ToList();
        state.Publishes.RemoveAll(p => Same(p.Program, program));
        return records;
    }

    private static string? TryAccept(
        TagSessionService session, string program, IReadOnlyList<ZoneRoutine>? zone, List<Publish> records, string what)
    {
        string NotKnown(string why) =>
            $"{what} pending edits in program '{program}' that this session did not publish with known rungs ({why}); " +
            "the session image was not updated";

        if (zone is null) return NotKnown("the program's pending zone could not be read");
        if (records.Count == 0) return NotKnown("no publish by this session is recorded");

        // The zone must be exactly what this session published: same routines, same new rungs.
        var recorded = records.Select(r => r.Routine).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var inZone   = zone.Select(z => z.Routine).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (recorded.Count != inZone.Count || recorded.Any(r => !inZone.Contains(r, StringComparer.OrdinalIgnoreCase)))
            return NotKnown($"the zone holds routines {string.Join(", ", inZone)} but this session published to {string.Join(", ", recorded)}");
        foreach (var routine in recorded)
        {
            var want = records.Where(r => Same(r.Routine, routine)).SelectMany(r => r.Rungs).Select(Normalise).Order(StringComparer.Ordinal).ToList();
            var have = zone.Where(z => Same(z.Routine, routine)).SelectMany(z => z.Rungs)
                .Where(r => NewRungCodes.Contains(r.Type)).Select(r => Normalise(r.Text)).Order(StringComparer.Ordinal).ToList();
            if (!want.SequenceEqual(have, StringComparer.Ordinal))
                return NotKnown($"the pending rungs of '{routine}' differ from the ones this session published");
        }

        try
        {
            var doc = SafeXml.ParseDocument(session.ControllerImage!.ToString());
            foreach (var routine in recorded)
            {
                var mine = records.Where(r => Same(r.Routine, routine)).ToList();
                if (FindContent(doc, program, routine) is not { } content)
                    return NotKnown($"routine '{routine}' is not in the session image");
                if (mine.All(r => r.AppliedPending))
                    AcceptPending(content);
                else if (mine.All(r => !r.AppliedPending))
                {
                    foreach (var r in mine)
                        if (!Splice(content, r.InsertPosition, r.ReplaceCount, r.Rungs, pending: false, r.Comments))
                            return NotKnown($"the replace range of '{routine}' is outside the session image");
                }
                else return NotKnown($"the publishes to '{routine}' were merged inconsistently");
            }
            Replace(session, doc);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return NotKnown($"merge failed: {ex.Message}");
        }
    }

    private static string? TryCancel(
        TagSessionService session, string program, IReadOnlyList<ZoneRoutine>? zone, List<Publish> records)
    {
        string NotKnown(string why) =>
            $"cancelled pending edits in program '{program}' ({why}); the session image was not restored";

        if (zone is null) return NotKnown("the program's pending zone could not be read");
        if (records.Count == 0) return NotKnown("no publish by this session is recorded");
        if (records.Any(r => r.PreContent is null)) return NotKnown("the session image was not changed for the publish");
        var recorded = records.Select(r => r.Routine).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (zone.Any(z => !recorded.Contains(z.Routine, StringComparer.OrdinalIgnoreCase)))
            return NotKnown("the zone holds edits this session did not publish");

        try
        {
            var doc = SafeXml.ParseDocument(session.ControllerImage!.ToString());
            foreach (var routine in recorded)
            {
                // The earliest publish of the routine holds the rungs from before any of them.
                var first = records.First(r => Same(r.Routine, routine));
                if (FindContent(doc, program, routine) is not { } content)
                    return NotKnown($"routine '{routine}' is not in the session image");
                content.ReplaceWith(new XElement(first.PreContent!));
            }
            Replace(session, doc);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return NotKnown($"restore failed: {ex.Message}");
        }
    }

    /// <summary>Pending rungs become ordinary rungs, replaced and deleted originals go, rungs are renumbered.</summary>
    private static void AcceptPending(XElement content)
    {
        foreach (var rung in content.Elements("Rung").ToList())
        {
            var code = (string?)rung.Attribute("Type");
            if (code is "R" or "D") rung.Remove();
            else if (code is "I" or "IR") rung.SetAttributeValue("Type", "N");
        }
        Renumber(content);
    }

    /// <summary>
    /// Applies a change set to a routine's rung list. Pending: the new rungs (<c>IR</c>/<c>I</c>) land AT the insert position and the
    /// replaced originals follow them, kept as <c>R</c> (or <c>D</c> when nothing replaces them). Not pending: the
    /// originals go and the new rungs, now ordinary, stay at the insert position. Returns <c>false</c> when the replace range is outside the routine.
    /// </summary>
    private static bool Splice(
        XElement content, int insertPosition, int replaceCount, IReadOnlyList<string> texts, bool pending,
        IReadOnlyList<string?>? comments = null)
    {
        var rungs = content.Elements("Rung").ToList();
        if (insertPosition + replaceCount > rungs.Count) return false;
        // Pending, the new block sits in front of the replaced originals; accepted, the originals are gone and the
        // block sits in front of whatever followed them. Either way it starts at insertPosition.
        var rangeEnd = insertPosition + replaceCount;
        var anchorIndex = pending ? insertPosition : rangeEnd;
        var after = anchorIndex < rungs.Count ? rungs[anchorIndex] : null;

        for (var i = insertPosition; i < insertPosition + replaceCount; i++)
        {
            if (pending) rungs[i].SetAttributeValue("Type", i - insertPosition < texts.Count ? "R" : "D");
            else rungs[i].Remove();
        }

        var added = texts.Select((t, j) =>
        {
            var rung = new XElement("Rung",
                new XAttribute("Number", 0),
                new XAttribute("Type", pending ? (j < replaceCount ? "IR" : "I") : "N"));
            // L5X order: Comment precedes Text. A rung with no comment gets no element, so its comment reads as empty.
            if (comments is not null && j < comments.Count && !string.IsNullOrEmpty(comments[j]))
                rung.Add(new XElement("Comment", new XCData(comments[j]!)));
            rung.Add(new XElement("Text", new XCData(t)));
            return rung;
        }).ToList();
        if (after is not null) after.AddBeforeSelf(added);
        else content.Add(added);
        Renumber(content);
        return true;
    }

    private static void Renumber(XElement content)
    {
        var n = 0;
        foreach (var rung in content.Elements("Rung")) rung.SetAttributeValue("Number", n++);
    }

    private static XElement? FindContent(XDocument doc, string program, string routine) =>
        doc.Descendants("Program").FirstOrDefault(p => Same((string?)p.Attribute("Name") ?? "", program))
            ?.Descendants("Routine").FirstOrDefault(r => Same((string?)r.Attribute("Name") ?? "", routine))
            ?.Element("RLLContent");

    /// <summary>Installs the merged document as the session's image, keeping its take time and any pending refresh.</summary>
    private static void Replace(TagSessionService session, XDocument doc)
    {
        var wasStale = session.IsImageStale;
        var takenAt  = session.ControllerImageTakenAt ?? DateTimeOffset.UtcNow;
        session.SetControllerImage(SafeXml.ToL5x(doc), takenAt);
        if (wasStale) session.MarkImageStale();
    }

    private static string Normalise(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
