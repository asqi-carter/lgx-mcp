// ── TargetRevision — which mnemonic set the target project uses ─────────────────
//
// Logix Designer v36 renamed 16 relay-ladder mnemonics to IEC 61131-3 names (see InstructionMnemonics).
// Which spelling a target accepts depends on the project's major revision, so every connect, ping,
// doctor and pending-edits result reports it. Detection is pure: it reads attributes from the L5X
// root and never touches a controller.
using System.Xml.Linq;
using L5Sharp.Core;

/// <summary>Where a <see cref="TargetRevisionInfo"/> value was read from.</summary>
public static class TargetRevisionSource
{
    /// <summary>The <c>Controller/@MajorRev</c> attribute of the open project or ACD export.</summary>
    public const string MajorRev = "MajorRev";

    /// <summary>The integer part of <c>RSLogix5000Content/@SoftwareRevision</c> (used when MajorRev is absent).</summary>
    public const string SoftwareRevision = "SoftwareRevision";

    /// <summary>Prefix added when the value came from the project uploaded from an online controller.</summary>
    public const string UploadedPrefix = "uploaded-";

    /// <summary>Neither attribute was available.</summary>
    public const string Unknown = "unknown";

    /// <summary>No project or session is present, so there is no target to describe.</summary>
    public const string None = "none";
}

/// <summary>The mnemonic set names reported as <c>mnemonicSet</c>.</summary>
public static class MnemonicSetNames
{
    /// <summary>Revision 36 and later: IEC names (<c>LT</c>, <c>MOVE</c>, ...).</summary>
    public const string Iec = "iec";

    /// <summary>Revision 35 and earlier: legacy names (<c>LES</c>, <c>MOV</c>, ...).</summary>
    public const string Legacy = "legacy";

    /// <summary>The revision could not be determined; no mnemonic check is applied.</summary>
    public const string Unknown = "unknown";
}

/// <summary>The target's major revision and the mnemonic set it implies.</summary>
/// <param name="MajorRevision">Major revision (for example 36), or <c>null</c> when unknown.</param>
/// <param name="MnemonicSet"><c>"iec"</c>, <c>"legacy"</c> or <c>"unknown"</c> (see <see cref="MnemonicSetNames"/>).</param>
/// <param name="Source">One of <see cref="TargetRevisionSource"/> (possibly with the uploaded prefix).</param>
/// <param name="Warning">Set when a target exists but its revision is unknown (no check is applied); otherwise <c>null</c>.</param>
public sealed record TargetRevisionInfo(int? MajorRevision, string MnemonicSet, string Source, string? Warning)
{
    /// <summary>
    /// The mnemonic set as the shared enum, or <c>null</c> when <see cref="MnemonicSet"/> is <c>"unknown"</c>
    /// (callers apply no mnemonic check for an unknown target).
    /// </summary>
    public global::MnemonicSet? Set => MnemonicSet switch
    {
        MnemonicSetNames.Iec    => global::MnemonicSet.Iec,
        MnemonicSetNames.Legacy => global::MnemonicSet.Legacy,
        _                       => null,
    };
}

/// <summary>Detects the target's major revision and mnemonic set.</summary>
public static class TargetRevision
{
    /// <summary>First major revision that uses the IEC mnemonic names.</summary>
    public const int FirstIecMajor = 36;

    /// <summary>Reported when there is no project or session at all.</summary>
    public static TargetRevisionInfo NoTarget { get; } =
        new(null, MnemonicSetNames.Unknown, TargetRevisionSource.None, null);

    /// <summary>
    /// The revision of the current target: the connected session's (recorded at connect), else the open
    /// project's, else <see cref="NoTarget"/>.
    /// </summary>
    /// <param name="sessions">The session registry, or <c>null</c>.</param>
    /// <param name="project">The project service, or <c>null</c>.</param>
    /// <returns>The revision to report; never <c>null</c>.</returns>
    public static TargetRevisionInfo Current(TagSessionService? sessions, ProjectService? project) =>
        sessions?.TargetRevision ?? project?.TargetRevision ?? NoTarget;

    /// <summary>
    /// Maps a major revision to a mnemonic set name: 36 and above is <c>"iec"</c>, 35 and below is
    /// <c>"legacy"</c>, <c>null</c> (or a non-positive value) is <c>"unknown"</c>.
    /// </summary>
    /// <param name="majorRevision">Major revision, or <c>null</c> when unknown.</param>
    /// <returns>The mnemonic set name.</returns>
    public static string MnemonicSetFor(int? majorRevision) =>
        majorRevision is null or <= 0 ? MnemonicSetNames.Unknown
        : majorRevision >= FirstIecMajor ? MnemonicSetNames.Iec
        : MnemonicSetNames.Legacy;

    /// <summary>
    /// Detects the revision from the <c>RSLogix5000Content</c> root of an L5X/ACD export: <c>Controller/@MajorRev</c>
    /// first, then the integer part of <c>@SoftwareRevision</c>. A missing or unparseable value in both gives
    /// <c>"unknown"</c> with a warning.
    /// </summary>
    /// <param name="root">The <c>RSLogix5000Content</c> element, or <c>null</c>.</param>
    /// <param name="uploaded">
    /// <c>true</c> when <paramref name="root"/> is the project uploaded from an online controller; the
    /// <see cref="TargetRevisionInfo.Source"/> then carries <see cref="TargetRevisionSource.UploadedPrefix"/>.
    /// </param>
    /// <returns>The detected revision; never <c>null</c>.</returns>
    public static TargetRevisionInfo Detect(XElement? root, bool uploaded = false)
    {
        var prefix = uploaded ? TargetRevisionSource.UploadedPrefix : string.Empty;

        if (ParseMajor((string?)root?.Element("Controller")?.Attribute("MajorRev")) is { } major)
            return Known(major, prefix + TargetRevisionSource.MajorRev);

        if (ParseMajor((string?)root?.Attribute("SoftwareRevision")) is { } fromSoftware)
            return Known(fromSoftware, prefix + TargetRevisionSource.SoftwareRevision);

        return UnknownRevision(prefix);
    }

    /// <summary>Detects the revision from a parsed project (see <see cref="Detect(XElement?, bool)"/>).</summary>
    /// <param name="project">The project, or <c>null</c> (gives <see cref="NoTarget"/>).</param>
    /// <param name="uploaded"><c>true</c> when the project is the one uploaded from an online controller.</param>
    /// <returns>The detected revision.</returns>
    public static TargetRevisionInfo Detect(L5X? project, bool uploaded = false)
    {
        if (project is null) return NoTarget;
        try { return Detect(project.Content.Serialize(), uploaded); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnknownRevision(uploaded ? TargetRevisionSource.UploadedPrefix : string.Empty);
        }
    }

    /// <summary>
    /// Parses a major revision from text such as <c>"36"</c>, <c>"36.11"</c> or <c>"35.00"</c> (the integer part).
    /// </summary>
    /// <param name="text">Attribute text (may be <c>null</c>).</param>
    /// <returns>The major revision, or <c>null</c> when absent, unparseable or not positive.</returns>
    public static int? ParseMajor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var head = text.Trim().Split('.')[0];
        return int.TryParse(head, System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : null;
    }

    private static TargetRevisionInfo Known(int major, string source) =>
        new(major, MnemonicSetFor(major), source, null);

    /// <summary>
    /// The revision of a target that exists but whose revision cannot be read: <c>"unknown"</c> with a warning.
    /// </summary>
    /// <param name="uploaded"><c>true</c> when the unreadable value was the uploaded project's.</param>
    /// <returns>The unknown revision.</returns>
    public static TargetRevisionInfo UnknownTarget(bool uploaded = false) =>
        UnknownRevision(uploaded ? TargetRevisionSource.UploadedPrefix : string.Empty);

    private static TargetRevisionInfo UnknownRevision(string prefix) =>
        new(null, MnemonicSetNames.Unknown, prefix + TargetRevisionSource.Unknown,
            "The target's major revision is unknown (no Controller/@MajorRev or RSLogix5000Content/@SoftwareRevision), " +
            "so mnemonicSet is 'unknown' and no mnemonic check is applied.");
}
