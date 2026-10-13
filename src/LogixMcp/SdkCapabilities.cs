// ── SdkCapabilities — what the Logix Designer SDK on this machine/session can do ──
//
// One table per session. Each entry is available / unavailable / unknown, with the source that
// decided it: a version rule (the build check needs Designer 37 or later), the install
// (the SDK is not present), or "observed" (the SDK itself said so at runtime). A tool that needs
// an unavailable capability refuses before it calls the SDK (SdkCapabilityUnavailable).
//
// Core assembly: no Rockwell types. The adapter holds the per-session instance; with no session
// the same shape is reported from install facts alone (the lgx://sdk/capabilities resource).
using System.Text.Json.Serialization;

/// <summary>Status values of one capability entry.</summary>
public static class SdkCapabilityStatus
{
    /// <summary>The SDK can do this here.</summary>
    public const string Available = "available";

    /// <summary>The SDK cannot do this here; a tool needing it refuses.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>Not yet known; it is learned the first time the SDK is asked.</summary>
    public const string Unknown = "unknown";
}

/// <summary>Where the status of a capability entry came from.</summary>
public static class SdkCapabilitySource
{
    /// <summary>A rule on the Logix Designer version (for example the build check needs v37 or later).</summary>
    public const string VersionRule = "version-rule";

    /// <summary>The SDK reported it at runtime (for example its unsupported-version error).</summary>
    public const string Observed = "observed";

    /// <summary>The SDK is not installed or not loaded in this build.</summary>
    public const string Install = "install";

    /// <summary>Nothing is known yet.</summary>
    public const string None = "none";
}

/// <summary>Stable names of the capability entries.</summary>
public static class SdkCapabilityNames
{
    /// <summary><c>BuildAsync</c>, the SDK build check.</summary>
    public const string BuildCheck = "buildCheck";

    /// <summary>Finalize edits (accept an edit zone as the live program).</summary>
    public const string Finalize = "finalizeEdits";

    /// <summary>Deleting a rung through the SDK.</summary>
    public const string RungDelete = "rungDelete";

    /// <summary>Program export with <c>TestEdits</c> (reads edit-zone state).</summary>
    public const string ProgramExportTestEdits = "programExportTestEdits";

    /// <summary>Writing a tag on an online controller through the SDK.</summary>
    public const string OnlineTagWrite = "onlineTagWrite";

    /// <summary>Uploading the controller's project.</summary>
    public const string Upload = "upload";

    /// <summary>Logix Designer major.minor version (informational).</summary>
    public const string DesignerVersion = "designerVersion";

    /// <summary>Version of the Rockwell SDK package (informational).</summary>
    public const string SdkPackageVersion = "sdkPackageVersion";

    /// <summary>Every entry, in report order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        BuildCheck, Finalize, RungDelete, ProgramExportTestEdits,
        OnlineTagWrite, Upload, DesignerVersion, SdkPackageVersion,
    ];
}

/// <summary>One row of the capability table.</summary>
/// <param name="Name">One of <see cref="SdkCapabilityNames"/>.</param>
/// <param name="Status"><see cref="SdkCapabilityStatus"/> value.</param>
/// <param name="Source"><see cref="SdkCapabilitySource"/> value.</param>
/// <param name="Detail">One sentence an engineer can read: why this status.</param>
/// <param name="Value">For the version entries, the version text; omitted otherwise.</param>
public sealed record SdkCapability(
    [property: JsonPropertyName("name")]   string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("value"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Value = null);

/// <summary>A snapshot of the capability table, as <c>logix_connect</c>, <c>logix_doctor</c> and the resource report it.</summary>
/// <param name="SdkLoaded">Whether the SDK adapter is loaded in this process.</param>
/// <param name="Session">Whether the entries come from an SDK session; false means install facts only.</param>
/// <param name="Entries">The entries, in <see cref="SdkCapabilityNames.All"/> order.</param>
public sealed record SdkCapabilitiesReport(
    [property: JsonPropertyName("sdkLoaded")] bool SdkLoaded,
    [property: JsonPropertyName("session")]   bool Session,
    [property: JsonPropertyName("entries")]   IReadOnlyList<SdkCapability> Entries)
{
    /// <summary>The entry named <paramref name="name"/>, or null.</summary>
    public SdkCapability? Get(string name) => Entries.FirstOrDefault(e => e.Name == name);

    /// <summary>Value equality: the same flags and the same entries in the same order.</summary>
    public bool Equals(SdkCapabilitiesReport? other) =>
        other is not null && SdkLoaded == other.SdkLoaded && Session == other.Session
        && Entries.SequenceEqual(other.Entries);

    /// <summary>Hash code consistent with <see cref="Equals(SdkCapabilitiesReport?)"/>.</summary>
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(SdkLoaded);
        h.Add(Session);
        foreach (var e in Entries) h.Add(e);
        return h.ToHashCode();
    }
}

/// <summary>
/// The structured refusal for a tool whose SDK capability is unavailable. Nothing was sent to the SDK.
/// </summary>
/// <param name="Error">Plain-language refusal for the engineer.</param>
/// <param name="Capability">The capability that is unavailable (<see cref="SdkCapabilityNames"/>).</param>
/// <param name="Version">The Logix Designer version, when known; omitted otherwise.</param>
/// <param name="WhatStillWorks">What the engineer can still do.</param>
/// <param name="Kind">Always <see cref="ErrorKinds.SdkCapabilityUnavailable"/>.</param>
public sealed record SdkCapabilityUnavailableError(
    [property: JsonPropertyName("error")]      string Error,
    [property: JsonPropertyName("capability")] string Capability,
    [property: JsonPropertyName("version"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version,
    [property: JsonPropertyName("whatStillWorks")] string WhatStillWorks,
    [property: JsonPropertyName("kind")]       string Kind = ErrorKinds.SdkCapabilityUnavailable);

/// <summary>
/// The capability table. Built once per session (<see cref="ForSession"/>); the version is read live from the
/// session, so a version learned later is picked up, and <see cref="Observe"/> records what the SDK itself reported.
/// With no session, <see cref="NoSession"/> reports what the install alone shows.
/// </summary>
public sealed class SdkCapabilities
{
    /// <summary>
    /// What still works when each capability is unavailable. Phrased for an engineer; the manual equivalent first.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> StillWorks = new Dictionary<string, string>
    {
        [SdkCapabilityNames.BuildCheck] =
            "Rung edits are still imported and Studio verifies them on accept; Studio's Verify (Logic → Verify → Controller) is the manual build check.",
        [SdkCapabilityNames.Finalize] =
            "Pending edits can still be reviewed and accepted in Studio (Logic → Online Edits).",
        [SdkCapabilityNames.RungDelete] =
            "Rungs can still be added or replaced; delete the rung in Logix Designer.",
        [SdkCapabilityNames.ProgramExportTestEdits] =
            "Routines can still be exported without edit-zone state; check the zone in Studio (Logic → Online Edits).",
        [SdkCapabilityNames.OnlineTagWrite] =
            "Tags can still be read, and written over the CIP backend when live writes are enabled.",
        [SdkCapabilityNames.Upload] =
            "Open the project file (logix_open_project) and work from it, or upload in Logix Designer.",
    };

    private readonly Func<DesignerVersion?> _version;
    private readonly string?                _sdkPackageVersion;
    private readonly bool                   _sdkLoaded;
    private readonly bool                   _session;
    private readonly object                 _gate = new();
    private readonly Dictionary<string, SdkCapability> _observed = new(StringComparer.Ordinal);

    private SdkCapabilities(Func<DesignerVersion?> version, string? sdkPackageVersion, bool sdkLoaded, bool session)
    {
        _version           = version;
        _sdkPackageVersion = sdkPackageVersion;
        _sdkLoaded         = sdkLoaded;
        _session           = session;
    }

    /// <summary>Creates the table for one SDK session.</summary>
    /// <param name="designerVersion">Reads the session's current Designer version (null when unknown); called on every report.</param>
    /// <param name="sdkPackageVersion">Version of the loaded Rockwell SDK package, when known.</param>
    public static SdkCapabilities ForSession(Func<DesignerVersion?> designerVersion, string? sdkPackageVersion)
    {
        ArgumentNullException.ThrowIfNull(designerVersion);
        return new SdkCapabilities(designerVersion, sdkPackageVersion, sdkLoaded: true, session: true);
    }

    /// <summary>Creates a fixed-version table for one SDK session (a snapshot of <paramref name="designerVersion"/>).</summary>
    /// <param name="designerVersion">The Designer version, or null when unknown.</param>
    /// <param name="sdkPackageVersion">Version of the loaded Rockwell SDK package, when known.</param>
    public static SdkCapabilities ForSession(DesignerVersion? designerVersion, string? sdkPackageVersion) =>
        ForSession(() => designerVersion, sdkPackageVersion);

    /// <summary>
    /// The table with no session: what is known from the install alone. Nothing about a Designer version is known.
    /// </summary>
    /// <param name="sdkLoaded">Whether the SDK adapter is loaded.</param>
    /// <param name="sdkPackageVersion">Version of the loaded Rockwell SDK package, when known.</param>
    public static SdkCapabilitiesReport NoSession(bool sdkLoaded, string? sdkPackageVersion) =>
        new SdkCapabilities(() => null, sdkPackageVersion, sdkLoaded, session: false).Report();

    /// <summary>
    /// Records what the SDK itself reported for <paramref name="name"/>; an observation replaces the rule-based entry.
    /// </summary>
    /// <param name="name">One of <see cref="SdkCapabilityNames"/>.</param>
    /// <param name="status"><see cref="SdkCapabilityStatus.Available"/> or <see cref="SdkCapabilityStatus.Unavailable"/>.</param>
    /// <param name="detail">Why, in a sentence.</param>
    public void Observe(string name, string status, string detail)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate) _observed[name] = new SdkCapability(name, status, SdkCapabilitySource.Observed, detail);
    }

    /// <summary>
    /// Records the SDK's unsupported-version error for <paramref name="name"/>.
    /// </summary>
    /// <param name="name">The capability the SDK refused.</param>
    /// <param name="version">The version parsed from the SDK's error.</param>
    public void ObserveUnsupportedVersion(string name, DesignerVersion version) =>
        Observe(name, SdkCapabilityStatus.Unavailable,
            $"The SDK reported the operation unsupported on Logix Designer {version}.");

    /// <summary>A snapshot of the table now.</summary>
    public SdkCapabilitiesReport Report()
    {
        var version = _version();
        var entries = new List<SdkCapability>();
        foreach (var name in SdkCapabilityNames.All)
            entries.Add(Resolve(name, version));
        return new SdkCapabilitiesReport(_sdkLoaded, _session, entries);
    }

    /// <summary>
    /// The refusal for a tool that needs <paramref name="name"/>, or <c>null</c> when it may proceed
    /// (available, or unknown — the first call then observes the answer).
    /// </summary>
    /// <param name="name">One of <see cref="SdkCapabilityNames"/>.</param>
    public SdkCapabilityUnavailableError? Require(string name)
    {
        var report  = Report();
        var entry   = report.Get(name);
        if (entry is null || entry.Status != SdkCapabilityStatus.Unavailable) return null;

        var version = report.Get(SdkCapabilityNames.DesignerVersion)?.Value;
        var where   = version is null ? string.Empty : $" on Logix Designer {version}";
        var works   = StillWorks.TryGetValue(name, out var w) ? w : "Everything that does not need this capability still works.";
        return new SdkCapabilityUnavailableError(
            $"'{name}' is not available{where}: {entry.Detail}",
            name, version, works);
    }

    private SdkCapability Resolve(string name, DesignerVersion? version)
    {
        switch (name)
        {
            case SdkCapabilityNames.DesignerVersion:
                if (!_sdkLoaded)
                    return new(name, SdkCapabilityStatus.Unavailable, SdkCapabilitySource.Install, "The Logix Designer SDK is not available in this build or on this machine.");
                return version is null
                    ? new(name, SdkCapabilityStatus.Unknown, SdkCapabilitySource.None,
                        _session ? "The session's Designer version is not known yet." : "A Designer version is known once an SDK session is open.")
                    : new(name, SdkCapabilityStatus.Available, version.Source == DesignerVersionSource.SdkError
                            ? SdkCapabilitySource.Observed : SdkCapabilitySource.VersionRule,
                        $"Learned from {version.Source}.", version.ToString());

            case SdkCapabilityNames.SdkPackageVersion:
                if (!_sdkLoaded)
                    return new(name, SdkCapabilityStatus.Unavailable, SdkCapabilitySource.Install, "The Logix Designer SDK is not available in this build or on this machine.");
                return _sdkPackageVersion is null
                    ? new(name, SdkCapabilityStatus.Unknown, SdkCapabilitySource.None, "The SDK package version could not be read.")
                    : new(name, SdkCapabilityStatus.Available, SdkCapabilitySource.Install, "Version of the loaded Rockwell SDK package.", _sdkPackageVersion);
        }

        if (!_sdkLoaded)
            return new(name, SdkCapabilityStatus.Unavailable, SdkCapabilitySource.Install,
                "The Logix Designer SDK is not available in this build or on this machine.");

        lock (_gate)
            if (_observed.TryGetValue(name, out var seen)) return seen;

        if (name == SdkCapabilityNames.BuildCheck)
        {
            var min = DesignerVersionInfo.MinBuildCheckMajor;
            var known = $"Needs Logix Designer {min} or later; this is {version}.";
            return version?.BuildCheckAvailable switch
            {
                true  => new(name, SdkCapabilityStatus.Available, SdkCapabilitySource.VersionRule, known),
                false => new(name, SdkCapabilityStatus.Unavailable, SdkCapabilitySource.VersionRule, known),
                null  => new(name, SdkCapabilityStatus.Unknown, SdkCapabilitySource.None,
                    $"Needs Logix Designer {min} or later. The installed version is not known yet; the first logix_build reports it."),
            };
        }

        return new(name, SdkCapabilityStatus.Unknown, SdkCapabilitySource.None,
            "No version rule applies; the first time the SDK is asked it reports whether this works.");
    }
}
