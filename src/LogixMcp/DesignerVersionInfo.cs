// ── DesignerVersionInfo — Logix Designer version facts and the v36 build-check rule ───────
//
// Logix Designer before v37 cannot run the SDK build check (BuildAsync): the SDK answers
// "Operation not supported on Logix Designer version 36.5." This is not a build failure and
// must never be reported as one. The pinned message pattern, the minimum major
// version and the shared note live here, in one place, so a test can pin them.
using System.Text.RegularExpressions;

/// <summary>Where a Logix Designer version number was learned from.</summary>
public static class DesignerVersionSource
{
    /// <summary>Parsed from the SDK's own unsupported-version error: authoritative for the Designer that answered.</summary>
    public const string SdkError = "sdk-error";

    /// <summary>
    /// The <c>SoftwareRevision</c> of the project the session was opened from. A project cannot be opened by an older
    /// Designer, so a revision of 37 or later proves the Designer is 37 or later; a lower revision proves nothing.
    /// </summary>
    public const string ProjectRevision = "project-revision";
}

/// <summary>A Logix Designer version (major.minor) and where it was learned from.</summary>
/// <param name="Major">Major version, e.g. 36.</param>
/// <param name="Minor">Minor version, e.g. 5.</param>
/// <param name="Source">One of <see cref="DesignerVersionSource"/>.</param>
public sealed record DesignerVersion(int Major, int Minor, string Source)
{
    /// <summary>The version as <c>major.minor</c>, e.g. <c>36.5</c>.</summary>
    public override string ToString() => $"{Major}.{Minor}";

    /// <summary>
    /// Whether the SDK build check can run: <c>true</c> for 37 and later, <c>false</c> when the SDK itself said the
    /// version is below 37, and <c>null</c> (unknown) when only a project revision below 37 is known.
    /// </summary>
    public bool? BuildCheckAvailable =>
        Major >= DesignerVersionInfo.MinBuildCheckMajor ? true
        : Source == DesignerVersionSource.SdkError ? false
        : null;

    /// <summary>
    /// <c>true</c> when the version is authoritative (came from the SDK) and below 37, so the build call can be skipped.
    /// </summary>
    public bool KnownBelowBuildCheck =>
        Source == DesignerVersionSource.SdkError && Major < DesignerVersionInfo.MinBuildCheckMajor;
}

/// <summary>Pinned facts about the Logix Designer version gate on the SDK build check.</summary>
public static class DesignerVersionInfo
{
    /// <summary>First Logix Designer major version whose SDK can run the build check.</summary>
    public const int MinBuildCheckMajor = 37;

    /// <summary>
    /// The SDK's unsupported-version message, e.g. <c>Operation not supported on Logix Designer version 36.5.</c>
    /// The SDK exposes no error type or code for this, so the message text is the only handle; it is kept here, once,
    /// and pinned by a test.
    /// </summary>
    public const string UnsupportedVersionPattern = @"not supported on Logix Designer version (\d+)\.(\d+)";

    /// <summary>The note returned with <c>buildVerify: "unavailable"</c>.</summary>
    public const string BuildUnavailableNote =
        "Logix Designer before v37 cannot run the SDK build check; Studio's own verification on accept still applies.";

    private static readonly Regex UnsupportedVersionRegex =
        new(UnsupportedVersionPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses the Designer version out of an SDK unsupported-version error message.
    /// </summary>
    /// <param name="message">The exception message (may be <c>null</c>).</param>
    /// <returns>The authoritative version, or <c>null</c> when the message is not an unsupported-version error.</returns>
    public static DesignerVersion? TryParseUnsupportedVersion(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var m = UnsupportedVersionRegex.Match(message);
        if (!m.Success) return null;
        return int.TryParse(m.Groups[1].Value, out var major) && int.TryParse(m.Groups[2].Value, out var minor)
            ? new DesignerVersion(major, minor, DesignerVersionSource.SdkError)
            : null;
    }

    /// <summary>
    /// Parses a project <c>SoftwareRevision</c> such as <c>36.00</c> or <c>37.1</c>.
    /// </summary>
    /// <param name="softwareRevision">The attribute value (may be <c>null</c>).</param>
    /// <returns>The version tagged <see cref="DesignerVersionSource.ProjectRevision"/>, or <c>null</c> when unparseable.</returns>
    public static DesignerVersion? FromProjectRevision(string? softwareRevision)
    {
        if (string.IsNullOrWhiteSpace(softwareRevision)) return null;
        var parts = softwareRevision.Trim().Split('.');
        if (!int.TryParse(parts[0], out var major)) return null;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var mn) ? mn : 0;
        return new DesignerVersion(major, minor, DesignerVersionSource.ProjectRevision);
    }
}
