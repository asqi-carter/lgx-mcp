using System.Runtime.CompilerServices;
using System.Xml.Linq;
using L5Sharp.Core;

/// <summary>
/// The stale fields a session-image or project-file response carries when the session image is marked stale:
/// <c>imageStale: true</c>, <c>staleSince</c>, <c>staleReason</c> and the reconnect <c>hint</c>.
/// Every handler takes its wording from here, so they all say the same thing as lint.
/// </summary>
/// <param name="StaleSince">UTC time (ISO 8601) the image was marked stale.</param>
/// <param name="StaleReason">What the image could not follow.</param>
/// <param name="Hint">The reconnect hint (<see cref="ProjectSourceLabel.StaleHint"/>).</param>
internal sealed record StaleStamp(string StaleSince, string StaleReason, string Hint);

/// <summary>
/// Makes the project-file reads (<c>logix_get_routine</c>, <c>logix_project_summary</c>, <c>logix_find_tags</c>,
/// <c>logix_cross_reference</c> and the others that read the loaded file) follow an accepted edit this session applied
/// to the session image, or flag the image as stale.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SessionImageState"/> owns the stale mark and the merge on accept; this class only reads them. A routine
/// counts as an applied edit when its rungs in the session image differ from the rungs the image held when this class
/// first saw it for the current connect. The baseline is the loaded project when that is the upload
/// <c>logix_connect</c> registered (not a file opened explicitly), otherwise the image at the first read. Pending
/// (unaccepted) rungs are not part of the comparison: it reads <see cref="TagSessionService.AcceptedControllerImage"/>.
/// </para>
/// <para>
/// A reconnect attaches a new service and takes a new image, which starts a new baseline and drops the applied edits;
/// the stale mark is cleared by <see cref="SessionImageState"/> in the same way.
/// </para>
/// </remarks>
internal static class ProjectFileStaleness
{
    /// <summary>One ladder routine of an image and a fingerprint of its rung text and comments.</summary>
    private sealed record RoutineFingerprint(string Program, string Routine, string Fingerprint);

    private sealed class Tracker
    {
        public object?         Service;
        public DateTimeOffset? TakenAt;
        public bool            Started;
        public Dictionary<string, RoutineFingerprint>? Baseline;
        public L5X?                                    SeenImage;
        public Dictionary<string, RoutineFingerprint>? SeenPrint;
    }

    private static readonly ConditionalWeakTable<TagSessionService, Tracker> Trackers = new();
    private static readonly object Gate = new();

    /// <summary>
    /// The stale fields for a response labelled <paramref name="source"/>, or <c>null</c> when the response does not
    /// read the loaded file or the session image or the image is not marked stale.
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c> when none is attached.</param>
    /// <param name="source">The response's <c>source</c> label.</param>
    /// <param name="service">Project holder, used to start the baseline of the applied edits; optional.</param>
    /// <returns>The stamp to put on the response, or <c>null</c>.</returns>
    internal static StaleStamp? For(TagSessionService? session, string source, ProjectService? service = null)
    {
        Observe(session, service);
        if (source is not (ProjectSourceLabel.ProjectFile or ProjectSourceLabel.SessionImage or ProjectSourceLabel.AcceptedImage))
            return null;
        return SessionImageState.StalenessOf(session) is { } stale
            ? new StaleStamp(stale.SinceText, stale.Reason, ProjectSourceLabel.StaleHint)
            : null;
    }

    /// <summary>
    /// Starts the baseline of the applied-edit comparison for the session's current image if it has none. Every
    /// project-file read calls this, so the baseline is taken before an edit of this connect is accepted.
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c>.</param>
    /// <param name="service">Project holder; its project is the baseline when it is the upload logix_connect registered.</param>
    internal static void Observe(TagSessionService? session, ProjectService? service = null)
    {
        lock (Gate)
            _ = CurrentLocked(session, service);
    }

    /// <summary>
    /// Routines changed since connect by an edit this session applied to the session image, as <c>program/routine</c>,
    /// in name order. Empty when nothing changed, the session holds no image or the image is new (reconnect).
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c>.</param>
    /// <param name="service">Project holder, used to start the baseline; optional.</param>
    /// <returns>The routine names.</returns>
    internal static IReadOnlyList<string> AppliedEdits(TagSessionService? session, ProjectService? service = null)
    {
        lock (Gate)
            return AppliedLocked(session, service).Select(r => $"{r.Program}/{r.Routine}")
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The image to serve <paramref name="program"/>/<paramref name="routine"/> from when this session applied an edit
    /// to it, or when the session image holds the routine and the project file does not (a routine created this
    /// session, whatever the fingerprint baseline holds); otherwise <c>null</c> (the project file is read).
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c>.</param>
    /// <param name="service">Project holder, used to start the baseline; optional.</param>
    /// <param name="program">Program name (case-insensitive).</param>
    /// <param name="routine">Routine name (case-insensitive).</param>
    /// <returns>The session's accepted image, or <c>null</c>.</returns>
    internal static L5X? ImageFor(TagSessionService? session, ProjectService? service, string program, string routine)
    {
        lock (Gate)
        {
            if (AppliedLocked(session, service).Any(r =>
                    string.Equals(r.Program, program, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.Routine, routine, StringComparison.OrdinalIgnoreCase)))
                return session!.AcceptedControllerImage;
        }
        return IsCreatedThisSession(session, service, program, routine) ? session!.AcceptedControllerImage : null;
    }

    /// <summary>
    /// Whether the session's accepted image holds <paramref name="program"/>/<paramref name="routine"/> and the project
    /// file does not, which is a routine created this session. Independent of the applied-edit baseline.
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c>.</param>
    /// <param name="service">Project holder whose project is the file side of the comparison.</param>
    /// <param name="program">Program name (case-insensitive).</param>
    /// <param name="routine">Routine name (case-insensitive).</param>
    /// <returns><c>true</c> when the routine exists only in the session image.</returns>
    internal static bool IsCreatedThisSession(TagSessionService? session, ProjectService? service, string program, string routine)
    {
        if (session?.AcceptedControllerImage is not { } image || service is not { IsLoaded: true }) return false;
        var inImage = image.Programs
            .FirstOrDefault(p => string.Equals(p.Name, program, StringComparison.OrdinalIgnoreCase))?.Routines
            .Any(r => string.Equals(r.Name, routine, StringComparison.OrdinalIgnoreCase)) ?? false;
        if (!inImage) return false;
        var inFile = service.Project!.Programs
            .FirstOrDefault(p => string.Equals(p.Name, program, StringComparison.OrdinalIgnoreCase))?.Routines
            .Any(r => string.Equals(r.Name, routine, StringComparison.OrdinalIgnoreCase)) ?? false;
        return !inFile;
    }

    private static List<RoutineFingerprint> AppliedLocked(TagSessionService? session, ProjectService? service)
    {
        if (CurrentLocked(session, service) is not { Baseline: { } baseline } || session?.AcceptedControllerImage is not { } accepted)
            return [];
        var tracker = Trackers.GetOrCreateValue(session);
        var now     = Fingerprints(accepted, ref tracker.SeenImage, ref tracker.SeenPrint);
        if (now is null) return [];
        // A routine the baseline does not hold was created after connect: it counts as an applied edit too.
        return now.Where(kv => !baseline.TryGetValue(kv.Key, out var was) || was.Fingerprint != kv.Value.Fingerprint)
            .Select(kv => kv.Value).ToList();
    }

    /// <summary>The tracker of the session's current image (a new image starts a new baseline), or <c>null</c> with no image.</summary>
    private static Tracker? CurrentLocked(TagSessionService? session, ProjectService? service)
    {
        if (session?.ControllerImage is null) return null;
        var tracker = Trackers.GetOrCreateValue(session);
        if (!tracker.Started || !ReferenceEquals(tracker.Service, session.Service) || tracker.TakenAt != session.ControllerImageTakenAt)
        {
            tracker.Service   = session.Service;
            tracker.TakenAt   = session.ControllerImageTakenAt;
            tracker.Started   = true;
            tracker.Baseline  = null;
            tracker.SeenImage = null;
            tracker.SeenPrint = null;
        }
        if (tracker.Baseline is null)
        {
            // The upload logix_connect registered is the image as connected; a file the user opened is not.
            L5X? from = service is { IsLoaded: true, OpenedExplicitly: false } ? ControllerImageRefresh.Accepted(service.Project!) : session.AcceptedControllerImage;
            if (from is not null)
            {
                L5X? seen = null; Dictionary<string, RoutineFingerprint>? print = null;
                tracker.Baseline = Fingerprints(from, ref seen, ref print);
            }
        }
        return tracker;
    }

    private static Dictionary<string, RoutineFingerprint>? Fingerprints(
        L5X image, ref L5X? seenImage, ref Dictionary<string, RoutineFingerprint>? seenPrint)
    {
        if (ReferenceEquals(seenImage, image) && seenPrint is not null) return seenPrint;
        try
        {
            var doc    = SafeXml.ParseDocument(image.ToString());
            var prints = new Dictionary<string, RoutineFingerprint>(StringComparer.OrdinalIgnoreCase);
            foreach (var program in doc.Descendants("Program"))
            {
                var programName = (string?)program.Attribute("Name");
                if (programName is null) continue;
                foreach (var routine in program.Element("Routines")?.Elements("Routine") ?? [])
                {
                    var routineName = (string?)routine.Attribute("Name");
                    if (routineName is null || routine.Element("RLLContent") is not { } content) continue;
                    var text = string.Join("", content.Elements("Rung").Select(r =>
                        Normalise((string?)r.Element("Text") ?? "") + "\u0001" + Normalise((string?)r.Element("Comment") ?? "") + "\u0002"));
                    prints[$"{programName}/{routineName}"] = new RoutineFingerprint(programName, routineName, text);
                }
            }
            seenImage = image;
            seenPrint = prints;
            return prints;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Normalise(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
