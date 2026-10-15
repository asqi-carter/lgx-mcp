using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using L5Sharp.Core;

/// <summary>One program of a <see cref="ControllerFingerprint"/>.</summary>
/// <param name="Program">Program name.</param>
/// <param name="TestEdits">The program's <c>TestEdits</c> attribute, or <c>null</c> when absent or unreadable.</param>
/// <param name="Routines">Rung count per routine name (case-insensitive); a routine that is not ladder logic counts 0.</param>
public sealed record ProgramFingerprint(string Program, bool? TestEdits, IReadOnlyDictionary<string, int> Routines);

/// <summary>
/// A cheap summary of the controller used to notice changes made outside this session: for each program its
/// routine names, rung count per routine and <c>TestEdits</c>, plus the controller's name and serial.
/// </summary>
/// <param name="Identity">Controller name and serial as the source reports them, or <c>null</c> when it reports none.</param>
/// <param name="Programs">The programs.</param>
public sealed record ControllerFingerprint(ControllerIdentity? Identity, IReadOnlyList<ProgramFingerprint> Programs)
{
    /// <summary>Builds the fingerprint of an L5X document: a full image or any export that carries <c>&lt;Program&gt;</c> elements.</summary>
    /// <param name="l5x">L5X text.</param>
    /// <returns>The fingerprint.</returns>
    public static ControllerFingerprint FromL5x(string l5x)
    {
        var doc        = SafeXml.ParseDocument(l5x);
        var controller = doc.Descendants("Controller").FirstOrDefault();
        var identity   = controller is null ? null
            : new ControllerIdentity((string?)controller.Attribute("Name"), ControllerIdentity.ParseSerial((string?)controller.Attribute("ProjectSN")));
        var programs = doc.Descendants("Program")
            .Select(p => new ProgramFingerprint(
                (string?)p.Attribute("Name") ?? "",
                bool.TryParse(((string?)p.Attribute("TestEdits"))?.Trim(), out var t) ? t : null,
                p.Descendants("Routine")
                    .GroupBy(r => (string?)r.Attribute("Name") ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Element("RLLContent")?.Elements("Rung").Count() ?? 0, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        return new ControllerFingerprint(identity, programs);
    }
}

/// <summary>Why the session's controller is no longer the one it connected to.</summary>
/// <param name="Message">What changed: the name/serial before and after, or "project re-downloaded".</param>
/// <param name="Before">Identity the session connected to, as text; <c>null</c> for a re-download.</param>
/// <param name="After">Identity the controller reports now, as text; <c>null</c> for a re-download.</param>
public sealed record ControllerChangeInfo(string Message, string? Before, string? After);

/// <summary>Structured <see cref="ErrorKinds.ControllerChanged"/> error returned by image readers.</summary>
/// <param name="Error">Human-readable message.</param>
/// <param name="Kind"><see cref="ErrorKinds.ControllerChanged"/>.</param>
/// <param name="Hint">What to do next.</param>
/// <param name="Before">Identity before; omitted for a re-download.</param>
/// <param name="After">Identity after; omitted for a re-download.</param>
public sealed record ControllerChangedError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("kind")]  string Kind = ErrorKinds.ControllerChanged,
    [property: JsonPropertyName("hint")]  string Hint = AnalysisImage.ControllerChangedHint,
    [property: JsonPropertyName("before"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Before = null,
    [property: JsonPropertyName("after"),  JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? After = null);

/// <summary>A pending rung that writes an analysed tag but was left out because <c>includePending</c> is false.</summary>
/// <param name="Program">Program holding the rung.</param>
/// <param name="Routine">Routine holding the rung.</param>
/// <param name="RungIndex">Rung number in the session image (the pending rung's own index).</param>
/// <param name="Tag">The analysed tag the rung writes.</param>
/// <param name="State">
/// <c>staged</c> (held on the workstation, not yet sent to the controller), <c>pending</c> (an untested or
/// not-known-tested edit on the controller) or <c>tested</c> (a tested edit, executing on the controller).
/// </param>
public sealed record PendingWriter(
    [property: JsonPropertyName("program")]   string Program,
    [property: JsonPropertyName("routine")]   string Routine,
    [property: JsonPropertyName("rungIndex")] int    RungIndex,
    [property: JsonPropertyName("tag")]       string Tag,
    [property: JsonPropertyName("state")]     string State = AnalysisImage.StatePending);

/// <summary>
/// Chooses which controller image the static analysis tools (<c>logix_cross_reference</c>,
/// <c>logix_verify_interlock</c>) read: the loaded project file, or the active session's controller
/// image (the <c>includePending</c> option, as lint does).
/// </summary>
internal static class AnalysisImage
{
    /// <summary>Hint of the <see cref="ErrorKinds.ControllerChanged"/> error.</summary>
    internal const string ControllerChangedHint =
        "call logix_connect again; staged rungs of this session are discarded on reconnect";

    /// <summary>Name of the result field that reports a refresh forced by an out-of-band change.</summary>
    internal const string RefreshedBecauseField = "imageRefreshedBecause";

    /// <summary>A rung held on the workstation that the controller does not have.</summary>
    internal const string StateStaged = "staged";

    /// <summary>A pending edit on the controller that is untested or not known to be tested.</summary>
    internal const string StatePending = "pending";

    /// <summary>A tested edit: its logic executes on the controller.</summary>
    internal const string StateTested = "tested";

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
            return new Selection(ControllerImageRefresh.PendingApplied(image), ProjectSourceLabel.SessionImage, null, imageNote);
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
    /// Brings the session's controller image up to date before a read: first notices changes made outside this session
    /// (the controller fingerprint, probed at most once per <see cref="TagSessionService.ImageFingerprintTtl"/>), then
    /// refreshes the stale programs, whether this session or another party changed them. A failed refresh is not fatal:
    /// the result then carries the stale note.
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="forceProbe"><c>true</c> to probe the fingerprint regardless of the TTL (<c>logix_diff_against_controller</c>).</param>
    /// <returns>
    /// <see cref="TagSessionService.OutOfBandReason"/> when a refresh forced by an out-of-band change has not been reported
    /// yet (the caller puts it on its result as <c>imageRefreshedBecause</c>), otherwise <c>null</c>.
    /// </returns>
    internal static async System.Threading.Tasks.Task<string?> PrepareAsync(
        TagSessionService? session, CancellationToken ct = default, bool forceProbe = false)
    {
        if (session is null) return null;
        await session.CheckControllerFingerprintAsync(forceProbe, ct).ConfigureAwait(false);
        // A changed controller is not refreshed: the image is no longer this controller's, and readers refuse.
        if (session.ControllerChange is null && session.IsImageStale)
            await session.RefreshControllerImageAsync(ct).ConfigureAwait(false);
        return session.TakeImageRefreshedBecause();
    }

    /// <summary>
    /// The <see cref="ErrorKinds.ControllerChanged"/> error for a session whose controller changed, or <c>null</c>.
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c>.</param>
    internal static ControllerChangedError? ChangedErrorOf(TagSessionService? session) =>
        session?.ControllerChange is { } c ? new ControllerChangedError($"The controller changed outside this session: {c.Message}.", Before: c.Before, After: c.After) : null;

    /// <summary>
    /// Runs an image read: prepares the image (<see cref="PrepareAsync"/>), refuses with
    /// <see cref="ErrorKinds.ControllerChanged"/> when the controller is no longer the one the session connected to, and
    /// otherwise stamps <c>imageRefreshedBecause</c> on the read's JSON result when this read followed an out-of-band refresh.
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c>.</param>
    /// <param name="read">The read; returns the serialized result.</param>
    /// <param name="forceProbe"><c>true</c> to probe regardless of the TTL.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async System.Threading.Tasks.Task<string> ReadAsync(
        TagSessionService? session, Func<string> read, bool forceProbe = false, CancellationToken ct = default)
    {
        var because = await PrepareAsync(session, ct, forceProbe).ConfigureAwait(false);
        if (ChangedErrorOf(session) is { } changed) return JsonSerializer.Serialize(changed);
        return StampRefreshed(read(), because);
    }

    /// <summary>Adds <c>imageRefreshedBecause</c> to a JSON object result; returns <paramref name="json"/> unchanged when there is no reason or it is not an object.</summary>
    /// <param name="json">The serialized result.</param>
    /// <param name="because">The reason from <see cref="PrepareAsync"/>, or <c>null</c>.</param>
    internal static string StampRefreshed(string json, string? because)
    {
        if (because is null) return json;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj) return json;
            obj[RefreshedBecauseField] = because;
            return obj.ToJsonString();
        }
        catch (JsonException) { return json; }
    }

    /// <summary>What <see cref="Compare"/> found.</summary>
    /// <param name="Change">Set when the controller is a different one or was re-downloaded.</param>
    /// <param name="ChangedPrograms">Programs whose routine set, rung counts or <c>TestEdits</c> differ from the image.</param>
    internal sealed record FingerprintComparison(ControllerChangeInfo? Change, IReadOnlyList<string> ChangedPrograms);

    /// <summary>
    /// Compares the controller's fingerprint with the image's. A different name or serial (where both sides report one) or
    /// a program or routine the image holds that the controller lacks (a re-download) is a <see cref="FingerprintComparison.Change"/>;
    /// otherwise each program whose routine names, rung counts or <c>TestEdits</c> differ is changed.
    /// </summary>
    /// <param name="image">Fingerprint of the session image.</param>
    /// <param name="now">Fingerprint the controller reports now.</param>
    /// <param name="skipProgram">Programs the comparison leaves out (already waiting for a refresh).</param>
    internal static FingerprintComparison Compare(ControllerFingerprint image, ControllerFingerprint now, Func<string, bool> skipProgram)
    {
        static string Describe(ControllerIdentity? i) =>
            i is null ? "unknown" : $"'{i.Name ?? "n/a"}' (serial {(i.Serial is { } s ? $"0x{s:X8}" : "n/a")})";

        if (image.Identity is { } before && now.Identity is { } after &&
            ((before.Name is { } bn && after.Name is { } an && !string.Equals(bn, an, StringComparison.OrdinalIgnoreCase)) ||
             (before.Serial is { } bs && after.Serial is { } asr && bs != asr)))
            return new FingerprintComparison(
                new ControllerChangeInfo($"was {Describe(before)}, now {Describe(after)}", Describe(before), Describe(after)), []);

        var current = now.Programs.ToDictionary(p => p.Program, StringComparer.OrdinalIgnoreCase);
        foreach (var held in image.Programs)
        {
            if (skipProgram(held.Program)) continue;
            if (!current.TryGetValue(held.Program, out var there))
                return new FingerprintComparison(new ControllerChangeInfo($"project re-downloaded (program '{held.Program}' is gone)", null, null), []);
            var lost = held.Routines.Keys.FirstOrDefault(r => !there.Routines.ContainsKey(r));
            if (lost is not null)
                return new FingerprintComparison(new ControllerChangeInfo($"project re-downloaded (routine '{held.Program}/{lost}' is gone)", null, null), []);
        }

        var heldByName = image.Programs.ToDictionary(p => p.Program, StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();
        foreach (var program in now.Programs)
        {
            if (skipProgram(program.Program)) continue;
            if (!heldByName.TryGetValue(program.Program, out var held) ||
                held.Routines.Count != program.Routines.Count ||
                program.Routines.Any(r => !held.Routines.TryGetValue(r.Key, out var rungs) || rungs != r.Value) ||
                (held.TestEdits is { } ht && program.TestEdits is { } nt && ht != nt))
                changed.Add(program.Program);
        }
        return new FingerprintComparison(null, changed);
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
    internal static IReadOnlyList<PendingWriter> PendingWriters(
        Selection selection, Func<L5X, IEnumerable<PendingWriter>> find, Func<string, bool?>? zoneTested = null)
    {
        if (selection.PendingImage is not { } pending) return Array.Empty<PendingWriter>();
        var root = pending.Content.Serialize();
        return find(pending)
            .Select(w => w with { State = StateOfRung(root, w, zoneTested) })
            .DistinctBy(w => (w.Program, w.Routine, w.RungIndex, w.Tag))
            .ToList();
    }

    /// <summary>
    /// The state word of a pending-only rung: <c>e</c> and <c>er</c> rungs are <c>staged</c>; an <c>I</c> or <c>IR</c> rung
    /// is <c>tested</c> when its program's zone is known to be tested and <c>pending</c> otherwise (the export marks a
    /// tested and an untested zone alike).
    /// </summary>
    private static string StateOfRung(System.Xml.Linq.XElement root, PendingWriter writer, Func<string, bool?>? zoneTested)
    {
        var rung = root.Descendants("Program")
            .Where(p => string.Equals((string?)p.Attribute("Name"), writer.Program, StringComparison.OrdinalIgnoreCase))
            .Descendants("Routine")
            .Where(r => string.Equals((string?)r.Attribute("Name"), writer.Routine, StringComparison.OrdinalIgnoreCase))
            .Descendants("Rung")
            .FirstOrDefault(r => (string?)r.Attribute("Number") == writer.RungIndex.ToString());
        return ((string?)rung?.Attribute("Type")) switch
        {
            "e" or "er" => StateStaged,
            _ => zoneTested?.Invoke(writer.Program) == true ? StateTested : StatePending,
        };
    }

    /// <summary>
    /// "1 staged rung (Main rung 2; not yet sent to the controller)", "1 pending rung (Main rung 2)",
    /// "1 tested rung (Main rung 2; executing)"; writers in more than one state are joined with " and ".
    /// </summary>
    /// <param name="writers">The pending writers.</param>
    internal static string DescribePending(IReadOnlyList<PendingWriter> writers)
    {
        var parts = new List<string>();
        foreach (var state in new[] { StateStaged, StatePending, StateTested })
        {
            var group = writers.Where(w => w.State == state).ToList();
            if (group.Count == 0) continue;
            var where = string.Join(", ", group.Select(w => $"{w.Routine} rung {w.RungIndex}").Distinct());
            var count = group.Select(w => (w.Program, w.Routine, w.RungIndex)).Distinct().Count();
            var tail  = state switch
            {
                StateStaged => "; not yet sent to the controller",
                StateTested => "; executing",
                _           => "",
            };
            parts.Add($"{count} {state} rung{(count == 1 ? "" : "s")} ({where}{tail})");
        }
        return string.Join(" and ", parts);
    }

    private static int DistinctRungs(IReadOnlyList<PendingWriter> writers) =>
        writers.Select(w => (w.Program, w.Routine, w.RungIndex)).Distinct().Count();

    /// <summary>
    /// Message for a tag no accepted rung writes while staged, pending or tested rungs do:
    /// "X is not written by any accepted rung; 1 pending rung (Main rung 2) writes it. Pass includePending:true to include it."
    /// </summary>
    /// <param name="subject">How the message names the tag (for example <c>Output tag 'X'</c>).</param>
    /// <param name="writers">The pending writers (non-empty).</param>
    internal static string NotWrittenByAccepted(string subject, IReadOnlyList<PendingWriter> writers)
    {
        var count = DistinctRungs(writers);
        return $"{subject} is not written by any accepted rung; {DescribePending(writers)} {(count == 1 ? "writes" : "write")} it. " +
               "Pass includePending:true to include it.";
    }

    /// <summary>Note for a tag that accepted rungs write and staged, pending or tested rungs also write.</summary>
    /// <param name="tag">The tag as the caller named it.</param>
    /// <param name="writers">The pending writers (non-empty).</param>
    internal static string AlsoWrittenByPending(string tag, IReadOnlyList<PendingWriter> writers)
    {
        var count = DistinctRungs(writers);
        return $"{DescribePending(writers)} also {(count == 1 ? "writes" : "write")} '{tag}' and {(count == 1 ? "was" : "were")} excluded. " +
               "Pass includePending:true to include it.";
    }
}

/// <summary>One block of rungs staged on the workstation and not yet published (a bucket of the staged-rung buffer).</summary>
/// <param name="Program">Program holding the routine.</param>
/// <param name="Routine">Routine the block is staged into.</param>
/// <param name="InsertPosition">Rung position the block starts at.</param>
/// <param name="ReplaceCount">How many existing rungs from <paramref name="InsertPosition"/> the block replaces.</param>
/// <param name="Rungs">Text of the staged rungs, in order (compared through <c>RungTextComparer.Normalize</c>).</param>
/// <param name="KeptNormal">
/// Indices <c>j &lt; ReplaceCount</c> of replacements whose text equals the original rung they displace. Logix keeps
/// such a rung normal, so a refresh neither stamps it <c>IR</c> nor lets it claim a displaced original. Empty by default.
/// </param>
internal sealed record StagedBlock(
    string Program, string Routine, int InsertPosition, int ReplaceCount, IReadOnlyList<string> Rungs,
    IReadOnlySet<int>? KeptNormal = null)
{
    /// <summary>Replacement indices that stay normal in the session image; never <c>null</c>.</summary>
    public IReadOnlySet<int> KeptNormal { get; init; } = KeptNormal ?? new HashSet<int>();
}

/// <summary>
/// Implemented by a controller image source that also knows the rungs this session has staged but not published. A
/// refresh asks it, so an export that shows a staged rung as plain <c>N</c> does not leave a plain copy in the image.
/// </summary>
internal interface IStagedRungSource
{
    /// <summary>The blocks currently staged for <paramref name="program"/> (empty when none).</summary>
    /// <param name="program">Program being refreshed.</param>
    IReadOnlyList<StagedBlock> StagedBlocks(string program);
}

/// <summary>
/// The session image's staleness mark and the re-stamping of workstation-staged blocks. The image itself is
/// only ever written by <see cref="TagSessionService.RefreshControllerImageAsync"/>, which merges the controller's
/// export of each stale program and then calls <see cref="ReapplyStaged"/>.
/// </summary>
/// <remarks>
/// <para>
/// When the image cannot be brought in step with the controller, it is marked stale: every session-image response then
/// carries <c>imageStale</c>, <c>staleSince</c>, <c>staleReason</c> and the reconnect hint
/// (<see cref="ProjectSourceLabel.Staleness"/>). A failed refresh marks it with the export error; a later successful
/// refresh clears the mark. The mark belongs to the image it was set on: a reconnect attaches a new session service and
/// takes a new image, which clears it.
/// </para>
/// </remarks>
internal static class SessionImageState
{
    private sealed class State
    {
        public object?         Service;
        public DateTimeOffset? TakenAt;
        public DateTimeOffset? StaleSince;
        public string?         StaleReason;

        public void Reset(object? service, DateTimeOffset? takenAt)
        {
            Service = service; TakenAt = takenAt;
            StaleSince = null; StaleReason = null;
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

    /// <summary>Clears the stale mark on the session's current image (a successful refresh calls this). Does nothing when it is not marked.</summary>
    /// <param name="session">Tag session, or <c>null</c>.</param>
    internal static void ClearStale(TagSessionService? session)
    {
        lock (Gate)
        {
            if (Current(session) is not { } state) return;
            state.StaleSince  = null;
            state.StaleReason = null;
        }
    }

    /// <summary>
    /// Stamps this session's workstation-staged blocks for <paramref name="program"/> onto a freshly merged export of it.
    /// A workstation-staged replace or insert can come back from the export as plain <c>N</c> rungs; each staged new rung
    /// the export shows as <c>N</c> is stamped <c>IR</c> (replacement) or <c>I</c> (insert), and the originals it
    /// displaces are stamped <c>R</c> or <c>D</c>, so the accepted view never holds a plain copy of a staged rung.
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c> (unused: kept so callers pass the session the image belongs to).</param>
    /// <param name="image">The refreshed image, after the program's export was merged into it. It is not modified.</param>
    /// <param name="program">Program that was merged.</param>
    /// <param name="staged">The blocks currently staged for the program, or <c>null</c> for none.</param>
    /// <returns>A stamped copy of <paramref name="image"/>, or <paramref name="image"/> itself when nothing was stamped.</returns>
    /// <remarks>
    /// Rungs are matched by <see cref="RungTextComparer.Normalize"/>. Only rungs the export shows as <c>N</c> are touched,
    /// each at most once; a rung that matches no staged block is left as exported. Any failure leaves the image as exported.
    /// </remarks>
    internal static L5X ReapplyStaged(
        TagSessionService? session, L5X image, string program, IReadOnlyList<StagedBlock>? staged = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var blocks = (staged ?? []).Where(b => Same(b.Program, program) && b.Rungs.Count > 0).ToList();
        if (blocks.Count == 0) return image;
        try
        {
            var doc     = SafeXml.ParseDocument(image.ToString());
            var changed = false;
            foreach (var block in blocks)
            {
                if (FindContent(doc, block.Program, block.Routine) is not { } content) continue;
                changed |= StampStagedBlock(content, block);
            }
            return changed ? SafeXml.ToL5x(doc) : image;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return image; }
    }

    /// <summary>Stamps one staged block in a routine; returns <c>true</c> when any rung changed.</summary>
    private static bool StampStagedBlock(XElement content, StagedBlock block)
    {
        // Replacements identical to the rung they displace stay normal: not stamped, and they claim no original.
        var keptNormal = block.KeptNormal;
        var keptCount  = keptNormal is null ? 0
            : keptNormal.Count(j => j >= 0 && j < block.ReplaceCount && j < block.Rungs.Count);
        var rungs      = content.Elements("Rung").ToList();
        var consumed   = new HashSet<XElement>();

        // Index of the first unconsumed plain-N rung with this text at or after 'from', else anywhere.
        int Find(string text, int from)
        {
            var key = RungTextComparer.Normalize(text);
            for (var pass = 0; pass < 2; pass++)
                for (var i = pass == 0 ? from : 0; i < rungs.Count; i++)
                {
                    if (consumed.Contains(rungs[i]) || !IsPlain(rungs[i])) continue;
                    if (RungTextComparer.Normalize((string?)rungs[i].Element("Text")) == key) return i;
                }
            return -1;
        }

        var changed  = false;
        var firstNew = -1;
        var lastNew  = -1;
        for (var j = 0; j < block.Rungs.Count; j++)
        {
            if (keptNormal is not null && j < block.ReplaceCount && keptNormal.Contains(j)) continue;
            var at = Find(block.Rungs[j], firstNew >= 0 ? firstNew : 0);
            if (at < 0) continue;
            consumed.Add(rungs[at]);
            rungs[at].SetAttributeValue("Type", j < block.ReplaceCount ? "IR" : "I");
            if (firstNew < 0) firstNew = at;
            lastNew = Math.Max(lastNew, at);
            changed = true;
        }
        if (firstNew < 0) return changed;

        // A staged block has no recorded originals: the plain rungs right after it are the ones it displaces.
        // Kept-normal replacements displace nothing: only the stamped replacements claim an original.
        var stampedReplacements = Math.Min(block.ReplaceCount, block.Rungs.Count) - keptCount;
        for (var k = 0; k < block.ReplaceCount - keptCount && lastNew + 1 + k < rungs.Count; k++)
        {
            var original = rungs[lastNew + 1 + k];
            if (consumed.Contains(original) || !IsPlain(original)) break;
            original.SetAttributeValue("Type", k < stampedReplacements ? "R" : "D");
            changed = true;
        }
        return changed;
    }

    /// <summary>A rung the export shows as ordinary logic: <c>N</c> or no <c>Type</c>.</summary>
    private static bool IsPlain(XElement rung) => ((string?)rung.Attribute("Type")) is null or "N";

    /// <summary>A routine of the program as the controller exports it, with every rung (raw <c>Type</c>, text).</summary>
    /// <param name="Routine">Routine name.</param>
    /// <param name="Rungs">The routine's rungs as <c>logix_pending_edits</c> read them.</param>
    internal sealed record ZoneRoutine(string Routine, IReadOnlyList<(string Type, string Text)> Rungs);

    /// <summary>What <see cref="Reconcile"/> did for one program.</summary>
    /// <param name="Program">Program whose zone the image did not show.</param>
    /// <param name="Routines">The routines whose exported rungs differ from the image.</param>
    /// <param name="Outcome">Always <c>stale</c>.</param>
    internal sealed record Reconciliation(string Program, IReadOnlyList<string> Routines, string Outcome);

    /// <summary>
    /// <c>logix_pending_edits</c> observed <paramref name="program"/>: when the export shows a pending zone that the
    /// session image does not hold (a zone in a program this session did not publish to, or one another process changed),
    /// marks the image stale with "pending edits of &lt;program&gt; were resolved outside lgx-mcp". The caller marks the
    /// program for refresh; the next successful <see cref="TagSessionService.RefreshControllerImageAsync"/> clears the mark.
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c>.</param>
    /// <param name="program">Program that was read.</param>
    /// <param name="exported">The program's routines as exported just now: every rung (raw <c>Type</c>, text).</param>
    /// <returns>What was done, or <c>null</c> when the export shows no zone or the image already holds it.</returns>
    internal static Reconciliation? Reconcile(
        TagSessionService? session, string program, IReadOnlyList<ZoneRoutine> exported)
    {
        ArgumentNullException.ThrowIfNull(exported);
        lock (Gate)
        {
            if (Current(session) is not { } state) return null;
            if (!exported.Any(e => e.Rungs.Any(r => !IsNormalType(r.Type)))) return null;   // no zone on the controller

            XDocument doc;
            try { doc = SafeXml.ParseDocument(session!.ControllerImage!.ToString()); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }

            var differing = exported
                .Where(e => !ImageHolds(doc, program, e))
                .Select(e => e.Routine)
                .ToList();
            if (differing.Count == 0) return null;

            state.StaleSince  ??= DateTimeOffset.UtcNow;
            state.StaleReason ??= $"pending edits of {program} were resolved outside lgx-mcp";
            return new Reconciliation(program, differing, "stale");
        }
    }

    /// <summary>True when the image's routine holds exactly the exported rungs (same <c>Type</c> codes, same text).</summary>
    private static bool ImageHolds(XDocument doc, string program, ZoneRoutine exported)
    {
        if (FindContent(doc, program, exported.Routine) is not { } content) return false;
        var have = content.Elements("Rung").Select(r => (IsPlain(r) ? "N" : (string)r.Attribute("Type")!, RungText(r))).ToList();
        var want = exported.Rungs.Select(r => (IsNormalType(r.Type) ? "N" : r.Type, r.Text)).ToList();
        return have.Count == want.Count && have.Zip(want).All(p => p.First.Item1 == p.Second.Item1 && RungTextComparer.Matches(p.First.Item2, p.Second.Item2));
    }

    private static string RungText(XElement rung) => rung.Element("Text")?.Value ?? "";

    private static bool IsNormalType(string? type) => string.IsNullOrEmpty(type) || type == "N";

    private static XElement? FindContent(XDocument doc, string program, string routine) =>
        doc.Descendants("Program").FirstOrDefault(p => Same((string?)p.Attribute("Name") ?? "", program))
            ?.Descendants("Routine").FirstOrDefault(r => Same((string?)r.Attribute("Name") ?? "", routine))
            ?.Element("RLLContent");

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
