/// <summary>
/// Single source of truth for the <c>source</c> label that <c>logix_list_modules</c>,
/// <c>logix_lint</c> and <c>logix_document</c> put on their responses.
/// </summary>
public static class ProjectSourceLabel
{
    /// <summary>Content read from a project opened via <c>logix_open_project</c>.</summary>
    public const string ProjectFile = "project-file";

    /// <summary>Content read from an active session's controller image.</summary>
    public const string SessionImage = "session-image";

    /// <summary>Content read from an active session's accepted controller image (staged rungs left out).</summary>
    public const string AcceptedImage = "accepted-image";

    /// <summary>
    /// Returns <see cref="SessionImage"/> when <paramref name="session"/> targets a live
    /// controller, otherwise <see cref="ProjectFile"/>.
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c> when none is attached.</param>
    /// <returns>The wire label for the response's <c>source</c> field.</returns>
    public static string Resolve(TagSessionService? session) =>
        session?.IsLiveController == true ? SessionImage : ProjectFile;

    /// <summary>The hint every stale session-image response carries.</summary>
    public const string StaleHint = "reconnect (logix_connect) to refresh before relying on this result";

    /// <summary>
    /// The staleness of the session image behind a response labelled <paramref name="source"/>, or <c>null</c> when
    /// the response does not read the session image or the image is not marked stale. The mark is set when this
    /// session accepted, assembled or cancelled pending edits it could not apply to the image, and is cleared by the
    /// next successful connect (see <see cref="SessionImageState"/>).
    /// </summary>
    /// <param name="session">Tag session, or <c>null</c> when none is attached.</param>
    /// <param name="source">The response's <c>source</c> label.</param>
    /// <returns>The staleness to stamp on the response, or <c>null</c>.</returns>
    public static SessionImageStaleness? Staleness(TagSessionService? session, string source) =>
        source is SessionImage or AcceptedImage ? SessionImageState.StalenessOf(session) : null;
}

/// <summary>
/// Why a session-image response may not show the controller's current logic: <c>imageStale: true</c>,
/// <c>staleSince</c>, <c>staleReason</c> and the reconnect <c>hint</c>.
/// </summary>
/// <param name="Since">UTC time the image was marked stale.</param>
/// <param name="Reason">What happened that the image could not follow.</param>
public sealed record SessionImageStaleness(DateTimeOffset Since, string Reason)
{
    /// <summary><see cref="Since"/> as ISO 8601 text, for the wire.</summary>
    public string SinceText => Since.ToString("o");

    /// <summary>The note form of the staleness, for results that carry a single note string.</summary>
    public string Note =>
        $"session image is stale since {SinceText}: {Reason}; {ProjectSourceLabel.StaleHint}.";
}
