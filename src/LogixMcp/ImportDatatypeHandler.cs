using System.Text.Json.Serialization;
using System.Xml.Linq;
using L5Sharp.Core;

// ── Delta types ───────────────────────────────────────────────────────────────

/// <summary>
/// Represents a member whose <c>DataType</c> changed between the current project
/// definition and the proposed import target.
/// </summary>
/// <param name="Name">Member name (as it appears in the target).</param>
/// <param name="From">DataType in the current (existing) definition.</param>
/// <param name="To">DataType in the proposed import target.</param>
public record RetypedEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")]   string To);

/// <summary>
/// Represents a member whose name changed only in case (letter capitalisation)
/// between the current project definition and the proposed import target.
/// </summary>
/// <param name="From">Name as it exists in the current (existing) definition.</param>
/// <param name="To">Name as it appears in the proposed import target.</param>
public record RenamedEntry(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")]   string To);

/// <summary>
/// Structural delta between the existing datatype definition in the project and the
/// proposed import target, computed when <c>dryRun=true</c>.
/// </summary>
/// <param name="Added">
/// Names of members present in the target but absent in the current definition.
/// Empty list when the type is new or all current members are retained.
/// </param>
/// <param name="Removed">
/// Names of members present in the current definition but absent in the target.
/// Empty list when no current members are dropped.
/// </param>
/// <param name="Retyped">
/// Members whose name matches (case-insensitive) but whose <c>DataType</c> differs.
/// Each entry carries both the original (<c>from</c>) and the proposed (<c>to</c>) type.
/// </param>
/// <param name="Renamed">
/// Members whose name matches case-insensitively but differs in capitalisation only.
/// Each entry carries the original (<c>from</c>) and the proposed (<c>to</c>) spelling.
/// </param>
/// <param name="Reordered">
/// <c>true</c> when the relative order of the common (unremoved, non-added) members
/// differs between the current definition and the target.
/// </param>
public record DryRunDelta(
    [property: JsonPropertyName("added")]     IReadOnlyList<string>        Added,
    [property: JsonPropertyName("removed")]   IReadOnlyList<string>        Removed,
    [property: JsonPropertyName("retyped")]   IReadOnlyList<RetypedEntry>  Retyped,
    [property: JsonPropertyName("renamed")]   IReadOnlyList<RenamedEntry>  Renamed,
    [property: JsonPropertyName("reordered")] bool                         Reordered);

// ── ImportCollisionOption ─────────────────────────────────────────────────────

/// <summary>
/// Controls how the import behaves when the target datatype already exists
/// in the destination project.
/// Wired to <c>RockwellAutomation.LogixDesigner.LogixProject.ImportCollisionOptions</c>
/// by caller for SDK-backed imports.
/// </summary>
public enum ImportCollisionOption
{
    /// <summary>Replace the existing type definition with the imported one.</summary>
    Overwrite,

    /// <summary>Keep the existing type unchanged and skip the import.</summary>
    Discard,

    /// <summary>Abort the import and return an error if the type already exists.</summary>
    Cancel
}

// ── Response types ────────────────────────────────────────────────────────────

/// <summary>
/// Success response returned by <see cref="ImportDatatypeHandler.Execute"/>.
/// </summary>
/// <param name="DatatypeName">Name of the datatype that was or would be imported.</param>
/// <param name="L5XFragment">
/// The L5X XML fragment that was used (or would be used) for the import.
/// Contains the target type with <c>Use="Target"</c> and any dependent
/// user-defined types with <c>Use="Context"</c>.
/// </param>
/// <param name="Collision">
/// The <see cref="ImportCollisionOption"/> that was specified (as a string).
/// </param>
/// <param name="DryRun">
/// <c>true</c> when no file was written (preview mode);
/// <c>false</c> when the fragment was written to <see cref="SavedTo"/>.
/// </param>
/// <param name="SavedTo">
/// Absolute path of the file written when <see cref="DryRun"/> is <c>false</c>.
/// <c>null</c> in dry-run mode.
/// </param>
/// <param name="DependentTags">
/// All controller-scoped and program-scoped tags in the loaded project whose
/// <c>DataType</c> matches <see cref="DatatypeName"/> (case-insensitive).
/// Empty when no project is loaded or no tags reference the type.
/// This is informational only — the presence of dependent tags does not prevent
/// or gate the import operation.
/// </param>
/// <param name="Advisory">
/// Fixed advisory sentence reminding the caller that a controller download is
/// required after the import and that downloading clears controller memory.
/// Always present.
/// </param>
/// <param name="Delta">
/// Structural delta between the existing definition and the proposed import.
/// Only present when <see cref="DryRun"/> is <c>true</c>; <c>null</c> otherwise.
/// </param>
/// <param name="Verified">
/// <c>true</c> when the re-exported member list from the saved file matches the
/// target member list member-for-member (same names, same types, same order).
/// <c>false</c> when a mismatch is detected.
/// <c>null</c> when <see cref="DryRun"/> is <c>true</c> (verification is not
/// performed in dry-run mode because no file is written).
/// </param>
/// <param name="Build">
/// Result of the build verification step performed after the file is saved.
/// Contains the error count (e.g. <c>"0"</c>) when <c>build()</c> succeeds.
/// Set to <c>"unsupported on &lt;version&gt;"</c> when <c>build()</c> raises an
/// 'Operation not supported' exception (e.g. on older SDK versions).
/// <c>null</c> when <see cref="DryRun"/> is <c>true</c> or no build runner is
/// configured — never silently omitted when build is attempted.
/// </param>
/// <param name="BuildVerify">
/// What the build check actually did: <c>"passed"</c> (0 errors), <c>"failed"</c> (the build reported
/// errors, or the SDK build call failed), or <c>"unavailable"</c> (Logix Designer before v37 cannot run the SDK
/// build check; the import still landed and is reported as such, never as a failure). <c>null</c> when no build
/// was attempted (dry run, or no build runner configured).
/// </param>
/// <param name="DesignerVersion">
/// The Logix Designer version, when the build check reported it unavailable and the version is known.
/// </param>
/// <param name="Refused">
/// Set to a human-readable refusal message when the caller requested a real import
/// (<c>dryRun=false</c>) but did not pass <c>confirm=true</c>.
/// The response then mirrors a dry-run response (with <see cref="Delta"/> populated)
/// so the caller can inspect what would change before confirming.
/// <c>null</c> in all other cases.
/// </param>
/// <param name="WillCreate">
/// <c>true</c> on a dry-run preview when the datatype does not exist in the source ACD,
/// meaning the real run will create it.
/// <c>null</c> when the type exists.
/// </param>
/// <param name="Warnings">
/// Human-readable warnings about the preview; carries the
/// "will create" note when <see cref="WillCreate"/> is <c>true</c>.
/// <c>null</c> when there are none.
/// </param>
/// <param name="SdkMessages">
/// Status and error messages the SDK reported during the import. Omitted when there are none (dry runs,
/// the no-SDK fallback, or a silent import).
/// </param>
public record ImportDatatypeResult(
    [property: JsonPropertyName("datatypeName")]   string                    DatatypeName,
    [property: JsonPropertyName("l5xFragment")]    string                    L5XFragment,
    [property: JsonPropertyName("collision")]      string                    Collision,
    [property: JsonPropertyName("dryRun")]         bool                      DryRun,
    [property: JsonPropertyName("savedTo")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   string?                   SavedTo      = null,
    [property: JsonPropertyName("dependentTags")]  IReadOnlyList<TagEntry>?  DependentTags = null,
    [property: JsonPropertyName("advisory")]       string                    Advisory      =
        ImportDatatypeHandler.DownloadAdvisory,
    [property: JsonPropertyName("delta")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   DryRunDelta?              Delta         = null,
    [property: JsonPropertyName("verified")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   bool?                     Verified      = null,
    [property: JsonPropertyName("build")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   string?                   Build         = null,
    [property: JsonPropertyName("refused")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   string?                   Refused       = null,
    [property: JsonPropertyName("willCreate")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   bool?                     WillCreate    = null,
    [property: JsonPropertyName("warnings")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   IReadOnlyList<string>?    Warnings      = null,
    [property: JsonPropertyName("note")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   string?                   Note          = null,
    [property: JsonPropertyName("buildVerify")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   string?                   BuildVerify   = null,
    [property: JsonPropertyName("designerVersion")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   string?                   DesignerVersion = null,
    [property: JsonPropertyName("sdkMessages")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   IReadOnlyList<string>?    SdkMessages   = null);

/// <summary>
/// Typed error returned when the SDK import ran but the re-exported type does not have the requested members.
/// The temp file was removed; nothing was written (no <c>savedTo</c>, no download advice, no build result).
/// </summary>
/// <param name="Error">Human-readable description.</param>
/// <param name="DatatypeName">Name of the datatype.</param>
/// <param name="ExpectedMembers">The requested (target) visible member list.</param>
/// <param name="ActualMembers">The visible member list re-exported from the imported result.</param>
/// <param name="Collision">The collision option that was used.</param>
/// <param name="Reason">Why the import did not apply.</param>
/// <param name="SdkMessages">The SDK's own import messages (omitted when empty).</param>
/// <param name="Hint">What to do next.</param>
/// <param name="Kind">Always <see cref="ErrorKinds.ImportNotApplied"/>.</param>
/// <param name="HiddenHosts">Hidden host members (for BOOL bits) seen in the re-export, reported apart from the visible members; omitted when there are none.</param>
public record ImportNotAppliedError(
    [property: JsonPropertyName("error")]           string                              Error,
    [property: JsonPropertyName("datatypeName")]    string                              DatatypeName,
    [property: JsonPropertyName("expectedMembers")] IReadOnlyList<DataTypeMemberEntry>  ExpectedMembers,
    [property: JsonPropertyName("actualMembers")]   IReadOnlyList<DataTypeMemberEntry>  ActualMembers,
    [property: JsonPropertyName("collision")]       string                              Collision,
    [property: JsonPropertyName("reason")]          string                              Reason,
    [property: JsonPropertyName("sdkMessages")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                    IReadOnlyList<string>?              SdkMessages,
    [property: JsonPropertyName("hint")]            string                              Hint,
    [property: JsonPropertyName("kind")]            string                              Kind = ErrorKinds.ImportNotApplied,
    [property: JsonPropertyName("hiddenHosts")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                    IReadOnlyList<DataTypeMemberEntry>? HiddenHosts = null);

/// <summary>
/// Typed error returned when <c>collision: Cancel</c> met an existing type and the SDK aborted the import.
/// The temp file was removed; nothing was written.
/// </summary>
/// <param name="Error">Human-readable description including the SDK message.</param>
/// <param name="DatatypeName">Name of the datatype.</param>
/// <param name="Collision">The collision option that was used (<c>Cancel</c>).</param>
/// <param name="SdkMessages">The SDK's own import messages (omitted when empty).</param>
/// <param name="Hint">What to do next.</param>
/// <param name="Kind">Always <see cref="ErrorKinds.ImportCancelledOnCollision"/>.</param>
public record ImportCancelledOnCollisionError(
    [property: JsonPropertyName("error")]        string                Error,
    [property: JsonPropertyName("datatypeName")] string                DatatypeName,
    [property: JsonPropertyName("collision")]    string                Collision,
    [property: JsonPropertyName("sdkMessages")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                 IReadOnlyList<string>? SdkMessages,
    [property: JsonPropertyName("hint")]         string                Hint,
    [property: JsonPropertyName("kind")]         string                Kind = ErrorKinds.ImportCancelledOnCollision);

/// <summary>
/// Generic validation error returned by <see cref="ImportDatatypeHandler.Execute"/>.
/// </summary>
/// <param name="Error">Human-readable description of the validation failure.</param>
/// <param name="Kind">
/// Machine-readable classification, e.g. <c>"SaveAsEqualsInput"</c>,
/// <c>"InvalidInput"</c>, <c>"FileNotFound"</c>, <c>"WriteError"</c>.
/// </param>
public record ImportDatatypeError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("kind")]  string Kind);

/// <summary>
/// Error returned when the input ACD file is held open exclusively by Studio 5000,
/// preventing the server from creating a working copy or reading the file.
/// </summary>
/// <param name="Error">
/// Human-readable message stating that the ACD is locked by Studio 5000.
/// </param>
/// <param name="Workaround">
/// Actionable alternatives: close the project in Studio 5000 and retry, or export
/// it as L5X and use the <c>.L5X</c> path instead.
/// </param>
public record ImportDatatypeStudioLockError(
    [property: JsonPropertyName("error")]      string Error,
    [property: JsonPropertyName("workaround")] string Workaround);

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Pure handler for the <c>logix_import_datatype</c> MCP tool.
///
/// <para>
/// Offline-only and confirm-gated (<paramref name="dryRun"/> defaults to
/// <c>true</c>): the handler builds or reads the L5X fragment that would be
/// imported, returns it for inspection, and only writes to
/// <paramref name="saveAs"/> when <paramref name="dryRun"/> is
/// explicitly <c>false</c>.
/// </para>
///
/// <para>
/// The handler refuses when <paramref name="saveAs"/> resolves to the same
/// path as <paramref name="acdPath"/> (the tool never overwrites the input),
/// and refuses when the ACD file appears to be locked by Studio 5000.
/// </para>
/// </summary>
public static class ImportDatatypeHandler
{
    /// <summary>Note returned when the no-SDK fallback wrote only an L5X fragment.</summary>
    internal const string FallbackNote =
        "No project import was performed; the data type was written as an L5X fragment.";

    /// <summary>
    /// Fixed advisory sentence included in every successful response.
    /// Reminds the caller that a controller download is required after the
    /// datatype import and that downloading clears controller memory.
    /// </summary>
    internal const string DownloadAdvisory =
        "After a successful import, a controller download is required to apply the change; " +
        "downloading clears controller memory and restarts the running process.";

    /// <summary>Dry-run warning (and <c>note</c>) when the datatype does not exist in the source ACD.</summary>
    internal const string WillCreateNote = "this real run will create the type";

    /// <summary>Hint carried by <see cref="ImportNotAppliedError"/>.</summary>
    internal const string NotAppliedHint =
        "the saved file was not written; nothing changed. If the type is in use, check the SDK messages; " +
        "a type that must be replaced wholesale needs collision: Overwrite";

    /// <summary>Reason used when <c>collision: Discard</c> left the existing type as it was.</summary>
    internal const string DiscardKeptReason = "collision=Discard kept the existing type";

    /// <summary>Note used when the type already had the requested members.</summary>
    internal const string AlreadyHadMembersNote = "the type already had these members";

    /// <summary>Note used when the import could not be verified for want of a re-export reader.</summary>
    internal const string UnverifiedNote = "the import could not be verified (no re-export reader is available)";

    // Primitive / built-in Logix types that never require a Context entry.
    private static readonly HashSet<string> BuiltInTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "BOOL", "BIT", "SINT",  "INT",   "DINT",  "LINT",
            "USINT","UINT",  "UDINT", "ULINT",
            "REAL", "LREAL", "STRING",
            "BYTE", "WORD",  "DWORD", "LWORD",
            "COUNTER", "TIMER", "CONTROL",
            "MESSAGE", "MOTION_GROUP",
        };

    // ── Public Execute entry point ─────────────────────────────────────────────

    /// <summary>
    /// Validates inputs, builds or reads the L5X fragment, and optionally writes it.
    /// Never throws; returns an error record on failure.
    /// </summary>
    /// <param name="acdPath">
    /// Path to the ACD project file that the import would target.
    /// Used for the Studio-lock check and the saveAs ≠ input guard;
    /// the file is not modified.
    /// </param>
    /// <param name="datatypeName">
    /// Name of the datatype to import (the <c>Use="Target"</c> type in the L5X
    /// fragment).
    /// </param>
    /// <param name="l5xPath">
    /// Optional path to an existing L5X export file containing the type with
    /// <c>Use="Target"</c>. Mutually exclusive with <paramref name="members"/>.
    /// When supplied the file is read verbatim without re-rendering.
    /// </param>
    /// <param name="members">
    /// Optional ordered member list in <c>logix_list_datatypes</c> shape
    /// (<see cref="DataTypeMemberEntry"/>). Mutually exclusive with
    /// <paramref name="l5xPath"/>. When supplied the handler renders the L5X
    /// fragment including any dependent user-defined types as
    /// <c>Use="Context"</c> entries.
    /// </param>
    /// <param name="saveAs">
    /// Required output path for the L5X fragment. Must not resolve to the same
    /// path as <paramref name="acdPath"/>.
    /// </param>
    /// <param name="service">
    /// Optional project service used to resolve dependent types for the
    /// <c>Use="Context"</c> section when <paramref name="members"/> is supplied.
    /// When <c>null</c> no Context entries are added.
    /// </param>
    /// <param name="collision">
    /// How to handle a type name collision in the destination project.
    /// Defaults to <see cref="ImportCollisionOption.Overwrite"/>.
    /// </param>
    /// <param name="dryRun">
    /// When <c>true</c> (default) no file is written; the fragment is returned
    /// for inspection only. When <c>false</c> the fragment is written to
    /// <paramref name="saveAs"/> — but only when <paramref name="confirm"/> is
    /// also <c>true</c>.
    /// </param>
    /// <param name="confirm">
    /// Must be <c>true</c> for a real import (<paramref name="dryRun"/>=<c>false</c>)
    /// to proceed. When <c>false</c> (the default) and <paramref name="dryRun"/> is
    /// also <c>false</c>, the handler returns the dry-run delta result plus a
    /// <c>refused</c> field without performing any write or SDK call.
    /// Has no effect when <paramref name="dryRun"/> is <c>true</c>.
    /// </param>
    /// <param name="studioLockChecker">
    /// Optional override for Studio-lock detection; used in unit tests.
    /// When <c>null</c> the default <see cref="IsLockedByStudio"/> probe is used.
    /// </param>
    /// <param name="reexportReader">
    /// Optional override for the re-export member extraction step performed when
    /// <paramref name="dryRun"/> is <c>false</c>.  Receives the path of the saved
    /// file and returns the member list re-extracted from it.  When <c>null</c>
    /// the default implementation reads the file from disk and calls
    /// <see cref="ExtractTargetMembersFromFragment"/>.  Inject a custom reader in
    /// unit tests to simulate a re-export mismatch without writing differing file
    /// content to disk.
    /// </param>
    /// <param name="buildRunner">
    /// Optional delegate for the build verification step performed when
    /// <paramref name="dryRun"/> is <c>false</c>.  Receives the path of the saved
    /// file and returns the number of build errors (0 on a clean build).
    /// When the underlying build engine does not support this operation the
    /// delegate must throw any exception whose message contains the text
    /// <c>"not supported"</c> (case-insensitive); the handler will then report the
    /// build field as <c>"unsupported on &lt;version&gt;"</c> rather than silently
    /// omitting it.  When <c>null</c> (the default) no build step is performed and
    /// the <c>build</c> field is omitted from the response.
    /// </param>
    /// <param name="sdkImportRunner">
    /// Optional delegate for the SDK import sequence performed when
    /// <paramref name="dryRun"/> is <c>false</c>.
    ///
    /// <para>
    /// When non-<c>null</c>, the handler replaces the default
    /// <c>File.WriteAllText</c> stub with the full SDK sequence:
    /// <list type="number">
    ///   <item><description>
    ///     The handler copies <paramref name="acdPath"/> to <paramref name="saveAs"/>
    ///     so the input is never modified.
    ///   </description></item>
    ///   <item><description>
    ///     The handler writes the L5X fragment to a randomly-named temp <c>.L5X</c>
    ///     file that is deleted after the delegate returns (or throws).
    ///   </description></item>
    ///   <item><description>
    ///     This delegate is invoked with
    ///     <c>(saveAs, tempL5xPath, xpath, collision)</c> and is responsible for
    ///     the SDK steps: open <paramref name="saveAs"/> via the Logix Designer
    ///     SDK, call <c>PartialExportToXmlFileAsync</c> (to capture the current
    ///     type context), call
    ///     <c>PartialImportFromXmlFileAsync(xpath, tempL5xPath, …)</c>, and call
    ///     <c>SaveAsync</c> to persist the changes.
    ///   </description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// When <c>null</c> (the default), the handler falls back to writing the
    /// rendered L5X fragment verbatim to <paramref name="saveAs"/> via
    /// <c>File.WriteAllText</c> (original behaviour).
    /// </para>
    ///
    /// <para>
    /// In the SDK path <paramref name="reexportReader"/> is required for
    /// <c>verified</c> to be set; when both are non-<c>null</c> the handler calls
    /// <c>reexportReader(saveAs)</c> after the delegate returns and compares the
    /// result member-for-member against the target list.
    /// </para>
    /// </param>
    /// <returns>
    /// An <see cref="ImportDatatypeResult"/> on success, an
    /// <see cref="ImportDatatypeStudioLockError"/> when the ACD is locked, or an
    /// <see cref="ImportDatatypeError"/> for all other validation failures.
    /// </returns>
    public static object Execute(
        string                              acdPath,
        string                              datatypeName,
        string?                             l5xPath,
        IReadOnlyList<DataTypeMemberEntry>? members,
        string                              saveAs,
        ProjectService?                     service            = null,
        ImportCollisionOption               collision          = ImportCollisionOption.Overwrite,
        bool                                dryRun             = true,
        bool                                confirm            = false,
        Func<string, bool>?                 studioLockChecker  = null,
        Func<string, IReadOnlyList<DataTypeMemberEntry>>? reexportReader = null,
        Func<string, int>?                  buildRunner        = null,
        Func<string, string, string, ImportCollisionOption, IReadOnlyList<string>>? sdkImportRunner = null,
        bool                                overwrite          = false)
    {
        var result = ExecuteCore(
            acdPath, datatypeName, l5xPath, members, saveAs, service, collision,
            dryRun, confirm, studioLockChecker, reexportReader, buildRunner, sdkImportRunner,
            overwrite);

        // Every real (non-dry) run is audited with its outcome.
        if (!dryRun)
        {
            AuditLogger.LogDatatypeImport(
                acdPath      ?? string.Empty,
                saveAs       ?? string.Empty,
                datatypeName ?? string.Empty,
                collision.ToString(),
                DescribeOutcome(result) + DescribeSdkMessages(result));
        }

        return result;
    }

    /// <summary>Maps a handler result to the short outcome string written to the audit log.</summary>
    private static string DescribeOutcome(object result) => result switch
    {
        ImportDatatypeResult { Refused: not null }  => "refused_no_confirm",
        ImportDatatypeResult                        => "success",
        ImportNotAppliedError                       => "error:" + ErrorKinds.ImportNotApplied,
        ImportCancelledOnCollisionError             => "error:" + ErrorKinds.ImportCancelledOnCollision,
        ImportDatatypeError err                     => "error:" + err.Kind,
        ImportDatatypeStudioLockError               => "error:StudioLocked",
        _                                           => "error:Unknown",
    };

    /// <summary>Appends the SDK's import messages (when any) to the audit outcome.</summary>
    private static string DescribeSdkMessages(object result)
    {
        var messages = result switch
        {
            ImportDatatypeResult r              => r.SdkMessages,
            ImportNotAppliedError e             => e.SdkMessages,
            ImportCancelledOnCollisionError c   => c.SdkMessages,
            _                                   => null,
        };
        return messages is { Count: > 0 } ? "; sdk: " + string.Join(" | ", messages) : string.Empty;
    }

    // Valid Logix-style identifier; also guarantees the name is safe to embed in an XPath literal.
    private static readonly System.Text.RegularExpressions.Regex DatatypeNameRegex =
        new(@"^[A-Za-z_][A-Za-z0-9_]{0,39}$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Maximum size of an <c>l5xPath</c> input file that will be read.</summary>
    internal const long MaxL5xBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Returns an error message when <paramref name="resolvedSaveAs"/> is not an
    /// allowed output location (wrong extension, or inside the server's install
    /// directory or the Windows directory); otherwise <c>null</c>.
    /// </summary>
    /// <param name="resolvedSaveAs">Fully-resolved destination path.</param>
    private static string? ValidateSaveAsPath(string resolvedSaveAs)
    {
        var ext = Path.GetExtension(resolvedSaveAs);
        if (!ext.Equals(".ACD", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".L5X", StringComparison.OrdinalIgnoreCase))
            return "saveAs must end in .ACD or .L5X.";

        var forbidden = new List<string> { AppContext.BaseDirectory };
        var winDir = Environment.GetEnvironmentVariable("WINDIR");
        if (!string.IsNullOrWhiteSpace(winDir)) forbidden.Add(winDir);
        var sysWin = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(sysWin)) forbidden.Add(sysWin);

        foreach (var dir in forbidden)
        {
            string full;
            try { full = Path.GetFullPath(dir); }
            catch { continue; }
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                 + Path.DirectorySeparatorChar;
            if (resolvedSaveAs.StartsWith(full, StringComparison.OrdinalIgnoreCase))
                return "saveAs must not be inside the server install directory or the Windows directory.";
        }
        return null;
    }

    /// <summary>
    /// Validates that <paramref name="l5xPath"/> ends in .L5X and parses as XML
    /// whose root element is <c>RSLogix5000Content</c>. Never echoes file content.
    /// </summary>
    /// <param name="l5xPath">Caller-supplied L5X path (must exist).</param>
    /// <returns>An error message and kind, or <c>null</c> when the file is acceptable.</returns>
    private static (string Message, string Kind)? ValidateL5xContent(string l5xPath)
    {
        try
        {
            var doc = SafeXml.LoadDocument(l5xPath);
            if (doc.Root?.Name.LocalName != "RSLogix5000Content")
                return ("l5xPath is not an RSLogix 5000 L5X file (root element must be RSLogix5000Content).",
                        "InvalidInput");
            return null;
        }
        catch (SafeXmlException ex) when (ex.Kind == SafeXml.InputTooLarge)
        {
            return ("l5xPath file is too large.", SafeXml.InputTooLarge);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException
                                      or UnauthorizedAccessException)
        {
            // Deliberately omit ex.Message: it can quote file content.
            return ("l5xPath could not be parsed as an RSLogix 5000 L5X file.", SafeXml.InvalidL5x);
        }
    }

    /// <summary>
    /// Checks that every <paramref name="members"/> entry is present and carries a non-empty
    /// <c>name</c> and <c>dataType</c>. Entries can arrive partially populated because the MCP
    /// binder leaves absent JSON properties null.
    /// </summary>
    /// <param name="members">The caller-supplied member list.</param>
    /// <returns>An error naming the zero-based member index and missing field, or <c>null</c> when valid.</returns>
    internal static string? ValidateMembers(IReadOnlyList<DataTypeMemberEntry?> members)
    {
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            if (m is null)
                return $"members[{i}] is null — each member must be an object with 'name' and 'dataType'.";
            if (m.BindError is not null)
                return $"members[{i}] has an invalid value: {m.BindError}.";
            if (string.IsNullOrWhiteSpace(m.Name))
                return $"members[{i}] is missing required field 'name'.";
            if (string.IsNullOrWhiteSpace(m.DataType))
                return $"members[{i}] is missing required field 'dataType'.";
        }
        return null;
    }

    /// <summary>
    /// Fills optional member fields left null by the binder (dimension, radix, externalAccess)
    /// with neutral defaults so rendering never throws.
    /// </summary>
    private static IReadOnlyList<DataTypeMemberEntry> NormalizeMembers(
        IReadOnlyList<DataTypeMemberEntry> members) =>
        members.Select(m => m with
        {
            Dimension      = m.Dimension      ?? "0",
            Radix          = m.Radix          ?? string.Empty,
            ExternalAccess = m.ExternalAccess ?? "Read/Write",
        }).ToList();

    private static object ExecuteCore(
        string                              acdPath,
        string                              datatypeName,
        string?                             l5xPath,
        IReadOnlyList<DataTypeMemberEntry>? members,
        string                              saveAs,
        ProjectService?                     service            = null,
        ImportCollisionOption               collision          = ImportCollisionOption.Overwrite,
        bool                                dryRun             = true,
        bool                                confirm            = false,
        Func<string, bool>?                 studioLockChecker  = null,
        Func<string, IReadOnlyList<DataTypeMemberEntry>>? reexportReader = null,
        Func<string, int>?                  buildRunner        = null,
        Func<string, string, string, ImportCollisionOption, IReadOnlyList<string>>? sdkImportRunner = null,
        bool                                overwrite          = false)
    {
        // ── 1. Validate saveAs ≠ acdPath ──────────────────────────────────────
        if (string.IsNullOrWhiteSpace(saveAs))
            return new ImportDatatypeError(
                "saveAs must not be empty.", "InvalidInput");

        if (string.IsNullOrWhiteSpace(acdPath))
            return new ImportDatatypeError(
                "acdPath must not be empty.", "InvalidInput");

        var resolvedAcd    = Path.GetFullPath(acdPath);
        var resolvedSaveAs = Path.GetFullPath(saveAs);

        if (string.Equals(resolvedAcd, resolvedSaveAs, StringComparison.OrdinalIgnoreCase))
            return new ImportDatatypeError(
                "saveAs must not equal acdPath — the tool never overwrites the input ACD. " +
                "Provide a different path for saveAs.",
                "SaveAsEqualsInput");

        var saveAsProblem = ValidateSaveAsPath(resolvedSaveAs);
        if (saveAsProblem is not null)
            return new ImportDatatypeError(saveAsProblem, "InvalidSavePath");

        // ── 1b. Destination safety (real runs only) ───────────────────────────
        // Never destroy an existing file unless the caller opted in with overwrite=true,
        // and never write an ACD without the SDK (the fallback only writes an L5X fragment).
        if (!dryRun)
        {
            if (sdkImportRunner is null &&
                !string.Equals(Path.GetExtension(resolvedSaveAs), ".L5X",
                               StringComparison.OrdinalIgnoreCase))
                return new ImportDatatypeError(
                    "The Logix Designer SDK is not available, so only an .L5X saveAs can be " +
                    "written (as an L5X fragment). An .ACD saveAs requires the SDK.",
                    ErrorKinds.SdkRequired);

            if (!overwrite && (File.Exists(resolvedSaveAs) || Directory.Exists(resolvedSaveAs)))
                return new ImportDatatypeError(
                    $"saveAs '{saveAs}' already exists. Nothing was written. " +
                    "Choose a new path or pass overwrite=true to replace it.",
                    ErrorKinds.TargetExists);
        }

        // ── 2. Check for Studio lock ───────────────────────────────────────────
        var checker = studioLockChecker ?? IsLockedByStudio;
        if (File.Exists(acdPath) && checker(acdPath))
            return new ImportDatatypeStudioLockError(
                Error:
                    $"Studio 5000 has '{Path.GetFileName(acdPath)}' open exclusively. " +
                    "The server cannot read the file while Studio holds an exclusive lock.",
                Workaround:
                    "Close the project in Logix Designer and retry, or export it as L5X " +
                    "and pass the .L5X path instead.");

        // ── 3. Validate remaining inputs ───────────────────────────────────────
        if (string.IsNullOrWhiteSpace(datatypeName))
            return new ImportDatatypeError(
                "datatypeName must not be empty.", "InvalidInput");

        if (!DatatypeNameRegex.IsMatch(datatypeName))
            return new ImportDatatypeError(
                "datatypeName must start with a letter or underscore, contain only letters, " +
                "digits and underscores, and be at most 40 characters.", "InvalidInput");

        if (l5xPath is null && members is null)
            return new ImportDatatypeError(
                "Either l5xPath or members must be supplied — they are mutually exclusive " +
                "but one is required.",
                "InvalidInput");

        if (l5xPath is not null && members is not null)
            return new ImportDatatypeError(
                "Provide either l5xPath or members, not both — they are mutually exclusive.",
                "InvalidInput");

        if (members is not null)
        {
            var memberProblem = ValidateMembers(members);
            if (memberProblem is not null)
                return new ImportDatatypeError(memberProblem, "InvalidInput");

            members = NormalizeMembers(members);
        }

        // ── 4. Build or read the L5X fragment ─────────────────────────────────
        string fragment;

        if (l5xPath is not null)
        {
            // Pass-through: read and return the existing L5X file verbatim
            if (!l5xPath.EndsWith(".L5X", StringComparison.OrdinalIgnoreCase))
                return new ImportDatatypeError(
                    "l5xPath must end in .L5X.", "InvalidInput");

            if (!File.Exists(l5xPath))
                return new ImportDatatypeError(
                    $"l5xPath not found: {l5xPath}", "FileNotFound");

            var l5xProblem = ValidateL5xContent(l5xPath);
            if (l5xProblem is { } problem)
                return new ImportDatatypeError(problem.Message, problem.Kind);

            try
            {
                fragment = File.ReadAllText(l5xPath, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return new ImportDatatypeError(
                    $"Failed to read l5xPath '{l5xPath}': {ex.Message}", "ReadError");
            }
        }
        else
        {
            // Render from members, resolving Context types from the project
            var rendered = RenderL5XFragment(datatypeName, members!, service);
            if (rendered.Error is not null)
                return new ImportDatatypeError(rendered.Error, InvalidFragmentKind);
            fragment = rendered.Fragment!;
        }

        // ── 4b. New-type check — before any copy, write or SDK call ────────────
        // A type absent from the source ACD is created by the real run (the SDK creates it when the file
        // is imported at Controller/DataTypes) and flagged with willCreate on a dry-run preview.  Uses the
        // same baseline resolution as the dry-run delta.
        var currentMembersBaseline = ResolveCurrentMembers(datatypeName, service, acdPath, reexportReader);
        var isNewType = currentMembersBaseline.Count == 0;

        // ── 5. Confirm gate — refuse a real import without explicit confirmation ──
        // When the caller requests a real import (dryRun=false) but has not passed
        // confirm=true, return the dry-run delta so the caller can review what
        // would change before confirming.  No file is written and no SDK calls
        // are made in this branch.
        if (!dryRun && !confirm)
        {
            var dependentTagsRefused = ScanDependentTags(service, datatypeName);

            var currentMembersRefused = currentMembersBaseline;
            var targetMembersRefused  = members is not null
                ? members
                : ExtractTargetMembersFromFragment(fragment);
            var deltaRefused = ComputeDelta(currentMembersRefused, targetMembersRefused);

            return new ImportDatatypeResult(
                DatatypeName:  datatypeName,
                L5XFragment:   fragment,
                Collision:     collision.ToString(),
                DryRun:        true,
                SavedTo:       null,
                DependentTags: dependentTagsRefused,
                Advisory:      DownloadAdvisory,
                Delta:         deltaRefused,
                Verified:      null,
                Build:         null,
                Refused:
                    "confirm=true required for a real import — " +
                    "review the delta above and re-invoke with confirm=true to proceed.");
        }

        // ── 6. Write when not a dry run ────────────────────────────────────────
        string? savedTo  = null;
        bool?   verified = null;
        string? build    = null;
        string? buildVerify = null;
        string? designerVersion = null;
        string? note     = null;
        IReadOnlyList<string>? sdkMessages = null;

        if (!dryRun)
        {
            // Compute the XPath that targets this DataType in the SDK index.  A new type has no per-type
            // path yet (the SDK raises "XPath not valid"), so it is imported at Controller/DataTypes; the
            // verification re-export afterwards reads the new type at its own path.
            var xpath = isNewType
                ? "Controller/DataTypes"
                : $"Controller/DataTypes/DataType[@Name='{datatypeName}']";

            // Temp file in the destination directory so the final File.Move is an atomic
            // same-volume rename. Only this temp file is ever deleted on failure; a
            // pre-existing saveAs is never touched until the move.
            var destDir  = Path.GetDirectoryName(resolvedSaveAs) ?? ".";
            var tempDest = Path.Combine(
                destDir,
                $".{Path.GetFileNameWithoutExtension(resolvedSaveAs)}.{Guid.NewGuid():N}.tmp" +
                Path.GetExtension(resolvedSaveAs));

            void RemoveTemp()
            {
                try { if (File.Exists(tempDest)) File.Delete(tempDest); }
                catch { /* best effort */ }
                RemoveBackups(tempDest);
            }

            // Moves tempDest into place; returns an error (and removes the temp) on failure.
            ImportDatatypeError? CommitTemp()
            {
                try
                {
                    File.Move(tempDest, resolvedSaveAs, overwrite);
                    return null;
                }
                catch (IOException) when (!overwrite && File.Exists(resolvedSaveAs))
                {
                    RemoveTemp();
                    return new ImportDatatypeError(
                        $"saveAs '{saveAs}' already exists. Nothing was written. " +
                        "Choose a new path or pass overwrite=true to replace it.",
                        ErrorKinds.TargetExists);
                }
                catch (Exception ex)
                {
                    RemoveTemp();
                    return new ImportDatatypeError(
                        $"Failed to move result into place at '{saveAs}': {ex.Message}",
                        "WriteError");
                }
            }

            if (sdkImportRunner is not null)
            {
                // ── SDK real-run path ──────────────────────────────────────────
                // Step 1: copy acdPath → temp so the input and any existing saveAs
                // are never modified.
                if (File.Exists(acdPath))
                {
                    try
                    {
                        File.Copy(acdPath, tempDest, overwrite: false);
                    }
                    catch (Exception ex)
                    {
                        RemoveTemp();
                        return new ImportDatatypeError(
                            $"Failed to copy ACD to '{saveAs}': {ex.Message}",
                            "WriteError");
                    }
                }

                // Step 4: write the L5X fragment to a temp .L5X file.
                var tempL5x = Path.Combine(
                    Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".L5X");
                try
                {
                    try
                    {
                        File.WriteAllText(tempL5x, fragment, System.Text.Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        RemoveTemp();
                        return new ImportDatatypeError(
                            $"Failed to write temp L5X fragment: {ex.Message}",
                            "WriteError");
                    }

                    // Steps 2, 3, 5, 6: open the temp copy via SDK, export, import, save.
                    try
                    {
                        var captured = sdkImportRunner(tempDest, tempL5x, xpath, collision);
                        sdkMessages = captured is { Count: > 0 } ? captured : null;
                    }
                    catch (Exception ex)
                    {
                        RemoveTemp();
                        var failureMessages = ex is SdkImportException { SdkMessages.Count: > 0 } sie
                            ? sie.SdkMessages
                            : null;

                        // collision=Cancel on an existing type: the SDK aborts with its own mnemonic.
                        if (ex.Message.Contains(SdkImportException.AbortedNoChangesMnemonic, StringComparison.Ordinal) ||
                            (failureMessages?.Any(m => m.Contains(
                                SdkImportException.AbortedNoChangesMnemonic, StringComparison.Ordinal)) ?? false))
                        {
                            return new ImportCancelledOnCollisionError(
                                Error: $"collision=Cancel: '{datatypeName}' already exists, so the SDK cancelled the import: " +
                                       ex.Message,
                                DatatypeName: datatypeName,
                                Collision:    collision.ToString(),
                                SdkMessages:  failureMessages,
                                Hint:         "Nothing was written. Use collision: Overwrite to replace the existing type, " +
                                              "or Discard to keep it.");
                        }

                        return new ImportDatatypeError(
                            $"SDK import failed for '{datatypeName}': {ex.Message}" +
                            (failureMessages is null ? string.Empty : " SDK messages: " + string.Join(" | ", failureMessages)),
                            "SdkError");
                    }

                    // ── File-size sanity check ─────────────────────────────────
                    // A real ACD project should be larger than 100 KB. A fragment-
                    // sized output signals that SaveAsync failed silently.
                    var savedFileInfo = new FileInfo(tempDest);
                    long savedFileLen = savedFileInfo.Exists ? savedFileInfo.Length : 0L;
                    if (savedFileLen <= 100 * 1024)
                    {
                        RemoveTemp();
                        return new ImportDatatypeError(
                            $"savedTo file '{resolvedSaveAs}' is {savedFileLen} bytes " +
                            "— expected a real ACD project (> 100 KB). " +
                            "The SDK save appears to have produced a fragment rather than a full project file.",
                            "SavedFileNotAProject");
                    }

                    // ── Verify before the file exists ──────────────────────────
                    // Re-export the type from the temp result BEFORE the atomic move. Anything but an exact
                    // match means the import did not apply: remove the temp and report it typed, so no file
                    // is left at saveAs and the caller is never told to download.
                    var targetMemberList = members is not null
                        ? members
                        : ExtractTargetMembersFromFragment(fragment);

                    if (reexportReader is null)
                    {
                        // Cannot verify: keep verified=null and say so.
                        note = UnverifiedNote;
                    }
                    else
                    {
                        IReadOnlyList<DataTypeMemberEntry> actualMembers;
                        string? reexportFailure = null;
                        try
                        {
                            actualMembers = reexportReader(tempDest) ?? Array.Empty<DataTypeMemberEntry>();
                        }
                        catch (Exception ex)
                        {
                            // Fail closed: a type that cannot be read back is not a verified import.
                            actualMembers = Array.Empty<DataTypeMemberEntry>();
                            reexportFailure = ex.Message;
                        }

                        if (!MembersMatchExactly(actualMembers, targetMemberList))
                        {
                            RemoveTemp();
                            var keptExisting = reexportFailure is null && collision == ImportCollisionOption.Discard
                                && !isNewType && MembersMatchExactly(actualMembers, currentMembersBaseline);
                            var reason = keptExisting
                                ? DiscardKeptReason
                                : reexportFailure is not null
                                    ? "the type could not be re-exported to verify the import: " + reexportFailure
                                    : "the re-exported members do not match the requested members";
                            var hiddenHosts = actualMembers.Where(m => m.Hidden).ToList();
                            return new ImportNotAppliedError(
                                Error: $"The SDK import of '{datatypeName}' did not apply: {reason}.",
                                DatatypeName:    datatypeName,
                                ExpectedMembers: VisibleMembers(targetMemberList),
                                ActualMembers:   VisibleMembers(actualMembers),
                                Collision:       collision.ToString(),
                                Reason:          reason,
                                SdkMessages:     sdkMessages,
                                Hint:            NotAppliedHint,
                                HiddenHosts:     hiddenHosts.Count > 0 ? hiddenHosts : null);
                        }

                        verified = true;
                        if (collision == ImportCollisionOption.Discard && !isNewType &&
                            MembersMatchExactly(currentMembersBaseline, targetMemberList))
                            note = AlreadyHadMembersNote;
                    }

                    var commitErr = CommitTemp();
                    if (commitErr is not null) return commitErr;

                    // The SDK leaves a .bak beside the file it saved; only saveAs should remain.
                    RemoveBackups(tempDest);

                    savedTo = resolvedSaveAs;
                }
                finally
                {
                    // Step 7: delete the temp L5X (always, even on failure).
                    try
                    {
                        if (File.Exists(tempL5x)) File.Delete(tempL5x);
                    }
                    catch { /* best effort */ }
                }
            }
            else
            {
                // ── Fallback path: write L5X fragment to temp, then move ───────
                // No project import is performed, so the result is never verified.
                try
                {
                    File.WriteAllText(tempDest, fragment, System.Text.Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    RemoveTemp();
                    return new ImportDatatypeError(
                        $"Failed to write L5X fragment to '{saveAs}': {ex.Message}",
                        "WriteError");
                }

                var commitErr = CommitTemp();
                if (commitErr is not null) return commitErr;

                savedTo = resolvedSaveAs;
                note    = FallbackNote;
            }

            // Verification already ran against the temp file, before the move (SDK path); the
            // no-SDK fallback wrote only an L5X fragment, so verified stays null there.
            if (savedTo is not null)
            {
                // ── Build verification (when a runner is wired in) ─────────────
                // Attempt build() and report its error count, or set the field to
                // "unsupported on <version>" when build() raises a not-supported
                // exception.  The field is never silently omitted when build is
                // attempted.
                if (buildRunner is not null)
                {
                    try
                    {
                        int errorCount = buildRunner(savedTo);
                        build = errorCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        buildVerify = errorCount == 0 ? "passed" : "failed";
                    }
                    catch (SdkBuildUnavailableException unavailable)
                    {
                        // The capability table says the build check cannot run here. The import
                        // already landed; report what happened, not a failure.
                        designerVersion = unavailable.DesignerVersion;
                        var version = unavailable.DesignerVersion ?? TryExtractSoftwareRevision(fragment) ?? "unknown";
                        build = $"unsupported on {version}";
                        buildVerify = "unavailable";
                    }
                    catch (Exception buildEx)
                        when (buildEx.Message.Contains("not supported",
                                  StringComparison.OrdinalIgnoreCase))
                    {
                        var version = TryExtractSoftwareRevision(fragment) ?? "unknown";
                        build = $"unsupported on {version}";
                        buildVerify = "unavailable";
                    }
                    catch (Exception buildEx)
                    {
                        build = $"error: {buildEx.Message}";
                        buildVerify = "failed";
                    }
                }
            }
        }

        if (buildVerify == "unavailable")
            note = note is null ? DesignerVersionInfo.BuildUnavailableNote : note + " " + DesignerVersionInfo.BuildUnavailableNote;

        // ── 7. Scan for dependent tags (informational — never blocks) ─────────
        var dependentTags = ScanDependentTags(service, datatypeName);

        // ── 8. Compute structural delta when in dry-run mode ─────────────────
        DryRunDelta? delta = null;
        if (dryRun)
        {
            var currentMembers = currentMembersBaseline;
            var targetMembers  = members is not null
                ? members
                : ExtractTargetMembersFromFragment(fragment);
            delta = ComputeDelta(currentMembers, targetMembers);
        }

        return new ImportDatatypeResult(
            DatatypeName:  datatypeName,
            L5XFragment:   fragment,
            Collision:     collision.ToString(),
            DryRun:        dryRun,
            SavedTo:       savedTo,
            DependentTags: dependentTags,
            Advisory:      DownloadAdvisory,
            Delta:         delta,
            Verified:      verified,
            Build:         build,
            WillCreate:    dryRun && isNewType ? true : null,
            Warnings:      dryRun && isNewType ? new[] { WillCreateNote } : null,
            Note:          note,
            BuildVerify:   buildVerify,
            DesignerVersion: designerVersion,
            SdkMessages:   sdkMessages);
    }

    // ── Re-export member comparison ───────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when the visible members of <paramref name="reexported"/> match
    /// those of <paramref name="target"/> member-for-member: same count, and each pair has the
    /// same <see cref="DataTypeMemberEntry.Name"/> (ordinal) and the same
    /// <see cref="DataTypeMemberEntry.DataType"/> (case-insensitive, <c>BIT</c> equal to
    /// <c>BOOL</c>). Hidden host members, <c>Target</c>, <c>BitNumber</c> and radix are ignored.
    /// </summary>
    internal static bool MembersMatchExactly(
        IReadOnlyList<DataTypeMemberEntry> reexported,
        IReadOnlyList<DataTypeMemberEntry> target)
    {
        var left  = VisibleMembers(reexported);
        var right = VisibleMembers(target);
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Name, right[i].Name, StringComparison.Ordinal))
                return false;
            if (!SameType(left[i].DataType, right[i].DataType))
                return false;
        }

        return true;
    }

    /// <summary>The members that are not hidden (hidden members are the SINT hosts that carry BOOL bits).</summary>
    internal static IReadOnlyList<DataTypeMemberEntry> VisibleMembers(
        IReadOnlyList<DataTypeMemberEntry> members) =>
        members.Where(m => !m.Hidden).ToList();

    /// <summary>Maps the L5X spelling <c>BIT</c> to <c>BOOL</c>; other types are returned unchanged.</summary>
    internal static string NormalizeDataType(string dataType) =>
        string.Equals(dataType, "BIT", StringComparison.OrdinalIgnoreCase) ? "BOOL" : dataType;

    private static bool SameType(string a, string b) =>
        string.Equals(NormalizeDataType(a), NormalizeDataType(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes every backup the SDK wrote beside <paramref name="tempDest"/>: <c>tempDest.bak</c>
    /// and any <c>&lt;tempDest stem&gt;*.bak</c> in the same directory. Best effort.
    /// </summary>
    private static void RemoveBackups(string tempDest)
    {
        try
        {
            var dir  = Path.GetDirectoryName(tempDest);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            var stem = Path.GetFileNameWithoutExtension(tempDest);

            var candidates = new HashSet<string>(StringComparer.Ordinal) { tempDest + ".bak" };
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, stem + "*.bak"))
                    candidates.Add(f);

            foreach (var f in candidates)
            {
                try { if (File.Exists(f)) File.Delete(f); }
                catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }

    // ── Software-revision extraction ──────────────────────────────────────────

    /// <summary>
    /// Parses the <c>SoftwareRevision</c> attribute from the root element of the
    /// supplied L5X <paramref name="fragment"/> and returns it as a string.
    /// Returns <c>null</c> when the attribute is absent or the fragment cannot be
    /// parsed.
    /// </summary>
    internal static string? TryExtractSoftwareRevision(string fragment)
    {
        try
        {
            var doc = SafeXml.ParseDocument(fragment);
            return doc.Root?.Attribute("SoftwareRevision")?.Value;
        }
        catch
        {
            return null;
        }
    }

    // ── Studio lock detection ──────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when the file at <paramref name="path"/> is held open
    /// exclusively (i.e. locked by Studio 5000 or any other process that requested
    /// exclusive access).
    ///
    /// <para>
    /// Detection strategy: attempts to open the file with
    /// <see cref="FileAccess.Read"/> and <see cref="FileShare.ReadWrite"/> (which
    /// succeeds unless another opener holds an exclusive lock). An
    /// <see cref="IOException"/> on that probe indicates the file is locked.
    /// </para>
    ///
    /// <para>
    /// On Windows, <c>FileShare.None</c> in the holder maps to
    /// <c>ERROR_SHARING_VIOLATION</c> (HRESULT 0x80070020), which the OS enforces
    /// against any opener. On Linux, .NET's internal per-process FileShare table is
    /// enforced: within the same process a <c>FileShare.None</c> holder blocks any
    /// second open regardless of the requested share mode.
    /// </para>
    /// </summary>
    /// <param name="path">Absolute or relative path to the file to probe.</param>
    /// <returns>
    /// <c>true</c> if an <see cref="IOException"/> is raised when attempting to
    /// open the file; <c>false</c> if the file can be opened without error.
    /// </returns>
    internal static bool IsLockedByStudio(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var _ = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    // ── Dependent-tag scanning ────────────────────────────────────────────────

    /// <summary>
    /// Scans the loaded project for controller-scoped and program-scoped tags
    /// whose <c>DataType</c> matches <paramref name="datatypeName"/>
    /// (case-insensitive).
    ///
    /// <para>
    /// Returns an empty list when <paramref name="service"/> is <c>null</c>,
    /// not loaded, or the project contains no tags of the given type.
    /// Never throws.
    /// </para>
    /// </summary>
    /// <param name="service">
    /// Optional project service.  When <c>null</c> or not loaded an empty list
    /// is returned without error.
    /// </param>
    /// <param name="datatypeName">
    /// The type name to match against each tag's <c>DataType</c>
    /// (case-insensitive).
    /// </param>
    /// <returns>
    /// A read-only list of <see cref="TagEntry"/> records, one per matching
    /// tag, with <c>Scope = "Controller"</c> for controller tags or the
    /// program name for program-scoped tags.
    /// </returns>
    internal static IReadOnlyList<TagEntry> ScanDependentTags(
        ProjectService? service,
        string          datatypeName)
    {
        if (service?.IsLoaded != true)
            return Array.Empty<TagEntry>();

        var project = service.Project!;
        var result  = new List<TagEntry>();

        // Controller-scoped tags
        foreach (var tag in project.Tags)
        {
            if (string.Equals(tag.DataType, datatypeName, StringComparison.OrdinalIgnoreCase))
                result.Add(new TagEntry(
                    Name:        tag.Name,
                    Type:        tag.DataType,
                    Scope:       "Controller",
                    Description: tag.Description ?? string.Empty));
        }

        // Program-scoped tags
        foreach (var program in project.Programs)
        {
            foreach (var tag in program.Tags)
            {
                if (string.Equals(tag.DataType, datatypeName, StringComparison.OrdinalIgnoreCase))
                    result.Add(new TagEntry(
                        Name:        tag.Name,
                        Type:        tag.DataType,
                        Scope:       program.Name,
                        Description: tag.Description ?? string.Empty));
            }
        }

        return result;
    }

    // ── L5X fragment rendering ─────────────────────────────────────────────────

    /// <summary>Error kind returned when a rendered fragment fails validation.</summary>
    internal const string InvalidFragmentKind = "InvalidL5xFragment";

    /// <summary>
    /// Renders an L5X fragment suitable for import via
    /// <c>PartialImportFromXmlFileAsync</c>.
    ///
    /// <para>
    /// The target type is emitted with <c>Use="Target"</c>.  Any member whose
    /// <c>DataType</c> references a user-defined type found in
    /// <paramref name="service"/> is emitted immediately after with
    /// <c>Use="Context"</c>, providing the SDK with the type definition it needs
    /// to resolve the target's members at import time.
    /// </para>
    ///
    /// <para>
    /// Members are written with L5X spellings (see <see cref="ToL5xExternalAccess"/> and
    /// <see cref="ToL5xRadix"/>), BOOL members are expanded to a hidden SINT host plus
    /// <c>Target</c>/<c>BitNumber</c> members, and the finished fragment is checked with
    /// <see cref="ValidateRenderedFragment"/> before it is returned.
    /// </para>
    /// </summary>
    /// <returns>
    /// The fragment, or an error message naming the first rule the fragment broke
    /// (in which case the fragment is <c>null</c> and nothing may be written).
    /// </returns>
    private static (string? Fragment, string? Error) RenderL5XFragment(
        string                              datatypeName,
        IReadOnlyList<DataTypeMemberEntry>  members,
        ProjectService?                     service)
    {
        var targetMembers = ExpandBoolMembers(datatypeName, members);

        // Collect dependent UDT names (resolve from the loaded project)
        var contextTypes = new List<(string Name, IReadOnlyList<DataTypeMemberEntry> Members)>();

        if (service?.IsLoaded == true)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { datatypeName };

            foreach (var member in targetMembers)
            {
                var memberTypeName = member.DataType;
                if (BuiltInTypes.Contains(memberTypeName)) continue;
                if (!seen.Add(memberTypeName))             continue;

                var dt = service.Project!.DataTypes
                    .FirstOrDefault(d => string.Equals(
                        d.Name, memberTypeName, StringComparison.OrdinalIgnoreCase));
                if (dt is null || dt.Class != DataTypeClass.User) continue;

                var dtMembers = dt.Members
                    .Select(m => new DataTypeMemberEntry(
                        Name:           m.Name,
                        DataType:       m.DataType,
                        Dimension:      m.Dimension.ToString(),
                        Radix:          m.Radix?.Name          ?? string.Empty,
                        Hidden:         m.Hidden,
                        ExternalAccess: m.ExternalAccess?.Name ?? string.Empty,
                        Target:         m.Target is { Length: > 0 } t ? t : null,
                        BitNumber:      m.Target is { Length: > 0 } ? m.BitNumber : null))
                    .ToList();

                contextTypes.Add((dt.Name, ExpandBoolMembers(dt.Name, dtMembers)));
            }
        }

        // Build the <DataTypes> element containing Target + Context types
        var dataTypesElem = new XElement("DataTypes",
            BuildDataTypeElement(datatypeName, targetMembers, use: "Target"));

        foreach (var (name, ctxMembers) in contextTypes)
            dataTypesElem.Add(BuildDataTypeElement(name, ctxMembers, use: "Context"));

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement("RSLogix5000Content",
                new XAttribute("SchemaRevision",   "1.0"),
                new XAttribute("SoftwareRevision", "36.1"),
                new XAttribute("TargetName",       datatypeName),
                new XAttribute("TargetType",       "DataType"),
                // Always "true": the SDK's PartialImportFromXmlFileAsync silently imports nothing from a
                // root with ContainsContext="false" (no exception, no import-log error). Found by bisecting the import.
                new XAttribute("ContainsContext",   "true"),
                new XElement("Controller",
                    dataTypesElem)));

        var problem = ValidateRenderedFragment(doc);
        if (problem is not null)
            return (null, "The rendered L5X fragment failed validation, so nothing was written: " + problem);

        return (doc.ToString(), null);
    }

    // ── L5X spellings ──────────────────────────────────────────────────────────

    /// <summary>Allowed <c>ExternalAccess</c> values in an L5X file.</summary>
    internal static readonly string[] L5xExternalAccessValues = { "Read/Write", "Read Only", "None" };

    /// <summary>Allowed <c>Radix</c> values in an L5X data type member.</summary>
    internal static readonly string[] L5xRadixValues =
    {
        "Binary", "Octal", "Decimal", "Hex", "Exponential", "Float", "ASCII", "Unicode",
        "Date/Time", "Date/Time (ns)", "Time", "Time (ns)",
    };

    private static string SpellingKey(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>
    /// Maps an external-access spelling to the L5X form (<c>Read/Write</c>, <c>Read Only</c>,
    /// <c>None</c>). Accepts both the L5X form and the list-style form (<c>ReadWrite</c>,
    /// <c>ReadOnly</c>), in any case. An empty value means the default, <c>Read/Write</c>.
    /// </summary>
    /// <param name="value">The caller-supplied or listed spelling.</param>
    /// <returns>The L5X spelling, or <paramref name="value"/> unchanged when it is not recognised.</returns>
    internal static string ToL5xExternalAccess(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Read/Write";
        return SpellingKey(value) switch
        {
            "readwrite" => "Read/Write",
            "readonly"  => "Read Only",
            "none"      => "None",
            _           => value,
        };
    }

    /// <summary>
    /// Maps a radix spelling to the L5X form (<c>ASCII</c>, <c>Decimal</c>, <c>Hex</c>,
    /// <c>Float</c> and so on). Accepts both the L5X form and the list-style form
    /// (<c>Ascii</c>, <c>DateTime</c>, <c>DateTimeNs</c>), in any case.
    /// </summary>
    /// <param name="value">The caller-supplied or listed spelling.</param>
    /// <returns>
    /// The L5X spelling; <c>null</c> when the radix is empty or not applicable
    /// (<c>NullType</c>, <c>Null</c>, <c>NA</c>), meaning the attribute is omitted; or
    /// <paramref name="value"/> unchanged when it is not recognised.
    /// </returns>
    internal static string? ToL5xRadix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return SpellingKey(value) switch
        {
            "nulltype" or "null" or "na" or "notapplicable" => null,
            "binary"       => "Binary",
            "octal"        => "Octal",
            "decimal"      => "Decimal",
            "hex"          => "Hex",
            "exponential"  => "Exponential",
            "float"        => "Float",
            "ascii"        => "ASCII",
            "unicode"      => "Unicode",
            "datetime"     => "Date/Time",
            "datetimens"   => "Date/Time (ns)",
            "time"         => "Time",
            "timens"       => "Time (ns)",
            _              => value,
        };
    }

    // ── BOOL expansion ─────────────────────────────────────────────────────────

    private static bool IsBool(DataTypeMemberEntry m) =>
        string.Equals(NormalizeDataType(m.DataType), "BOOL", StringComparison.OrdinalIgnoreCase);

    private static bool IsHostType(string dataType) =>
        HostBits(dataType) > 0;

    private static int HostBits(string dataType) =>
        dataType.ToUpperInvariant() switch
        {
            "SINT" => 8,
            "INT"  => 16,
            "DINT" => 32,
            _      => 0,
        };

    /// <summary>
    /// Puts BOOL members into L5X form: every BOOL lives in a hidden integer host member and
    /// carries <c>Target</c> (the host's name) and <c>BitNumber</c>.
    ///
    /// <para>
    /// A BOOL that already names a <c>Target</c> keeps it, and its hidden host is kept where it
    /// is. A BOOL without a <c>Target</c> is packed, in order, into a generated hidden SINT host
    /// (eight bits per host) placed immediately before its first BOOL. Hidden host-shaped
    /// members that no BOOL references are dropped, because the generated hosts replace them.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<DataTypeMemberEntry> ExpandBoolMembers(
        string                             typeName,
        IReadOnlyList<DataTypeMemberEntry> members)
    {
        members = members.Select(m => IsBool(m) ? m with { DataType = "BOOL" } : m).ToList();
        var referenced = new HashSet<string>(
            members.Where(m => IsBool(m) && !string.IsNullOrEmpty(m.Target)).Select(m => m.Target!),
            StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(members.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

        var result   = new List<DataTypeMemberEntry>();
        string? host = null;
        var used     = 0;
        var hostSeq  = 0;

        foreach (var m in members)
        {
            if (m.Hidden && IsHostType(m.DataType) && m.Dimension is null or "0" &&
                !referenced.Contains(m.Name))
                continue; // regenerated below when BOOLs need it

            if (!IsBool(m) || !string.IsNullOrEmpty(m.Target))
            {
                result.Add(m);
                continue;
            }

            if (host is null || used >= 8)
            {
                string candidate;
                do
                {
                    var stem = "ZZZZZZZZZZ" + typeName;
                    var suffix = hostSeq++.ToString();
                    if (stem.Length + suffix.Length > 40) stem = stem[..(40 - suffix.Length)];
                    candidate = stem + suffix;
                } while (names.Contains(candidate));

                names.Add(candidate);
                host = candidate;
                used = 0;
                result.Add(new DataTypeMemberEntry(
                    Name: host, DataType: "SINT", Dimension: "0", Radix: "Decimal",
                    Hidden: true, ExternalAccess: "Read/Write"));
            }

            result.Add(m with { Target = host, BitNumber = used++ });
        }

        return result;
    }

    // ── Element building and validation ────────────────────────────────────────

    /// <summary>
    /// Builds a single <c>&lt;DataType&gt;</c> element with the given
    /// <paramref name="use"/> attribute and an ordered <c>&lt;Members&gt;</c> child.
    /// Spellings are mapped to L5X form; <c>Radix</c> is omitted when empty or not applicable.
    /// </summary>
    private static XElement BuildDataTypeElement(
        string                             name,
        IReadOnlyList<DataTypeMemberEntry> members,
        string                             use)
    {
        var membersElem = new XElement("Members",
            members.Select(m =>
            {
                var elem = new XElement("Member",
                    new XAttribute("Name",      m.Name),
                    new XAttribute("DataType",  IsBool(m) && !string.IsNullOrEmpty(m.Target) ? "BIT" : m.DataType),
                    new XAttribute("Dimension", string.IsNullOrWhiteSpace(m.Dimension) ? "0" : m.Dimension));

                var radix = ToL5xRadix(m.Radix);
                if (radix is not null)
                    elem.Add(new XAttribute("Radix", radix));

                elem.Add(new XAttribute("Hidden", m.Hidden.ToString().ToLowerInvariant()));

                if (!string.IsNullOrEmpty(m.Target))
                    elem.Add(new XAttribute("Target", m.Target));
                if (m.BitNumber is { } bit)
                    elem.Add(new XAttribute("BitNumber", bit));

                elem.Add(new XAttribute("ExternalAccess", ToL5xExternalAccess(m.ExternalAccess)));
                return elem;
            }));

        return new XElement("DataType",
            new XAttribute("Use",    use),
            new XAttribute("Name",   name),
            new XAttribute("Family", "NoFamily"),
            new XAttribute("Class",  "User"),
            membersElem);
    }

    /// <summary>
    /// Checks a rendered fragment against the L5X rules the import relies on: member names are
    /// present and unique; <c>ExternalAccess</c> and (when present) <c>Radix</c> use L5X
    /// spellings; dimensions are non-negative integers; every BOOL names an earlier hidden
    /// integer host with <c>Target</c> and an in-range, unused <c>BitNumber</c>; and no other
    /// member carries <c>Target</c> or <c>BitNumber</c>.
    /// </summary>
    /// <param name="doc">The rendered fragment.</param>
    /// <returns>A message naming the first broken rule, or <c>null</c> when the fragment is valid.</returns>
    internal static string? ValidateRenderedFragment(XDocument doc)
    {
        foreach (var dt in doc.Descendants("DataType"))
        {
            var typeName = dt.Attribute("Name")?.Value ?? "(unnamed)";
            var byName   = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            var bitsUsed = new HashSet<(string Host, int Bit)>();
            var index    = -1;

            foreach (var m in dt.Element("Members")?.Elements("Member") ?? Enumerable.Empty<XElement>())
            {
                index++;
                var name = m.Attribute("Name")?.Value ?? string.Empty;
                var where = $"{typeName}.members[{index}] ('{name}')";

                if (!DatatypeNameRegex.IsMatch(name))
                    return $"{where}: member name must start with a letter or underscore, contain only letters, digits and underscores, and be at most 40 characters.";
                if (!byName.TryAdd(name, m))
                    return $"{where}: duplicate member name.";

                var dataType = m.Attribute("DataType")?.Value;
                if (string.IsNullOrWhiteSpace(dataType))
                    return $"{where}: dataType is empty.";

                var dim = m.Attribute("Dimension")?.Value ?? "0";
                if (!int.TryParse(dim, out var dimN) || dimN < 0)
                    return $"{where}: dimension '{dim}' must be a non-negative integer.";

                var access = m.Attribute("ExternalAccess")?.Value;
                if (access is null || !L5xExternalAccessValues.Contains(access, StringComparer.Ordinal))
                    return $"{where}: externalAccess '{access}' is not one of Read/Write, Read Only, None.";

                var radix = m.Attribute("Radix")?.Value;
                if (radix is not null && !L5xRadixValues.Contains(radix, StringComparer.Ordinal))
                    return $"{where}: radix '{radix}' is not an L5X radix (for example ASCII, Decimal, Hex, Float).";

                var target = m.Attribute("Target")?.Value;
                var bitStr = m.Attribute("BitNumber")?.Value;

                if (!string.Equals(NormalizeDataType(dataType), "BOOL", StringComparison.OrdinalIgnoreCase))
                {
                    if (target is not null || bitStr is not null)
                        return $"{where}: only BOOL members may carry Target or BitNumber.";
                    continue;
                }

                if (string.IsNullOrEmpty(target) || bitStr is null)
                    return $"{where}: a BOOL member needs a hidden host member (Target) and a BitNumber.";
                if (!byName.TryGetValue(target, out var host))
                    return $"{where}: Target '{target}' must name a hidden host member declared earlier in the type.";
                if (host.Attribute("Hidden")?.Value != "true" ||
                    host.Attribute("Dimension")?.Value is not (null or "0") ||
                    !IsHostType(host.Attribute("DataType")?.Value ?? string.Empty))
                    return $"{where}: Target '{target}' must be a hidden, non-array SINT, INT or DINT member.";
                if (!int.TryParse(bitStr, out var bit) || bit < 0 ||
                    bit >= HostBits(host.Attribute("DataType")!.Value))
                    return $"{where}: BitNumber '{bitStr}' is out of range for host '{target}'.";
                if (!bitsUsed.Add((target.ToUpperInvariant(), bit)))
                    return $"{where}: bit {bit} of host '{target}' is already used by another BOOL.";
            }
        }

        return null;
    }

    // ── Delta computation ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the members of the named datatype as it currently exists in the
    /// loaded project, or an empty list when the project is not loaded or the
    /// type does not exist.
    /// </summary>
    internal static IReadOnlyList<DataTypeMemberEntry> ExtractCurrentMembersFromProject(
        string          datatypeName,
        ProjectService? service)
    {
        if (service?.IsLoaded != true)
            return Array.Empty<DataTypeMemberEntry>();

        var dt = service.Project!.DataTypes
            .FirstOrDefault(d => string.Equals(
                d.Name, datatypeName, StringComparison.OrdinalIgnoreCase));

        if (dt is null || dt.Class != DataTypeClass.User)
            return Array.Empty<DataTypeMemberEntry>();

        return dt.Members
            .Select(m => new DataTypeMemberEntry(
                Name:           m.Name,
                DataType:       m.DataType,
                Dimension:      m.Dimension.ToString(),
                Radix:          m.Radix?.Name          ?? string.Empty,
                Hidden:         m.Hidden,
                ExternalAccess: m.ExternalAccess?.Name ?? string.Empty))
            .ToList();
    }

    /// <summary>
    /// Resolves the CURRENT members of the datatype for delta computation. Prefers an
    /// offline read of <paramref name="acdPath"/> via <paramref name="currentReader"/>
    /// (the same SDK re-export reader used for verification) so the dry-run delta has a
    /// real baseline even when no project is loaded in <paramref name="service"/>. Falls
    /// back to the loaded project, then to empty (a brand-new type absent from acdPath
    /// correctly yields an all-"added" delta).
    ///
    /// <para>Note: the baseline must be read from acdPath. A baseline taken from an empty
    /// <c>_service</c> reports every target member as "added" and hides removals and
    /// retypes.</para>
    /// </summary>
    internal static IReadOnlyList<DataTypeMemberEntry> ResolveCurrentMembers(
        string                                            datatypeName,
        ProjectService?                                   service,
        string                                            acdPath,
        Func<string, IReadOnlyList<DataTypeMemberEntry>>? currentReader)
    {
        if (currentReader is not null && File.Exists(acdPath))
        {
            try
            {
                var fromAcd = currentReader(acdPath);
                if (fromAcd is not null)
                    return fromAcd;
            }
            catch
            {
                // Offline read failed (missing type, SDK error) — fall back below.
            }
        }

        return ExtractCurrentMembersFromProject(datatypeName, service);
    }

    /// <summary>
    /// Parses the L5X fragment returned by this handler and extracts the ordered
    /// member list from the <c>Use="Target"</c> <c>&lt;DataType&gt;</c> element.
    /// Returns an empty list when parsing fails or the Target element is absent.
    /// </summary>
    internal static IReadOnlyList<DataTypeMemberEntry> ExtractTargetMembersFromFragment(
        string fragment)
    {
        try
        {
            var doc        = SafeXml.ParseDocument(fragment);
            var targetElem = doc.Root?
                .Element("Controller")?
                .Element("DataTypes")?
                .Elements("DataType")
                .FirstOrDefault(e => e.Attribute("Use")?.Value == "Target");

            if (targetElem is null)
                return Array.Empty<DataTypeMemberEntry>();

            return targetElem.Element("Members")?
                .Elements("Member")
                .Select(m => new DataTypeMemberEntry(
                    Name:           m.Attribute("Name")?.Value           ?? string.Empty,
                    DataType:       NormalizeDataType(m.Attribute("DataType")?.Value ?? string.Empty),
                    Dimension:      m.Attribute("Dimension")?.Value      ?? "0",
                    Radix:          m.Attribute("Radix")?.Value          ?? string.Empty,
                    Hidden:         m.Attribute("Hidden")?.Value         == "true",
                    ExternalAccess: m.Attribute("ExternalAccess")?.Value ?? string.Empty,
                    Target:         m.Attribute("Target")?.Value,
                    BitNumber:      int.TryParse(m.Attribute("BitNumber")?.Value, out var bit) ? bit : null))
                .ToList()
                ?? (IReadOnlyList<DataTypeMemberEntry>)Array.Empty<DataTypeMemberEntry>();
        }
        catch
        {
            return Array.Empty<DataTypeMemberEntry>();
        }
    }

    /// <summary>
    /// Computes the structural delta between the current member list (from the
    /// existing project definition) and the target member list (from the proposed
    /// import).
    ///
    /// <para>
    /// Members are matched by name using a case-insensitive comparison so that a
    /// member whose name changes only in capitalisation is classified as
    /// <c>renamed</c> rather than removed-and-added.
    /// </para>
    /// </summary>
    /// <param name="currentMembers">
    /// Ordered members of the type as it exists in the project today.
    /// Pass an empty list for a type that does not yet exist in the project.
    /// </param>
    /// <param name="targetMembers">
    /// Ordered members of the type as proposed by the import.
    /// </param>
    /// <returns>
    /// A <see cref="DryRunDelta"/> describing added, removed, retyped, renamed,
    /// and reordered changes.
    /// </returns>
    internal static DryRunDelta ComputeDelta(
        IReadOnlyList<DataTypeMemberEntry> currentMembers,
        IReadOnlyList<DataTypeMemberEntry> targetMembers)
    {
        currentMembers = VisibleMembers(currentMembers);
        targetMembers  = VisibleMembers(targetMembers);

        var added   = new List<string>();
        var removed = new List<string>();
        var retyped = new List<RetypedEntry>();
        var renamed = new List<RenamedEntry>();

        // Index current and target members by lower-case name for O(1) lookup
        var currentByLower = currentMembers
            .ToDictionary(m => m.Name.ToLowerInvariant(), m => m);
        var targetByLower = targetMembers
            .ToDictionary(m => m.Name.ToLowerInvariant(), m => m);

        // Walk target: detect added, retyped, and renamed members
        foreach (var (lowerName, targetMember) in targetByLower)
        {
            if (!currentByLower.TryGetValue(lowerName, out var currentMember))
            {
                // No case-insensitive match → added
                added.Add(targetMember.Name);
            }
            else
            {
                // Same name (case-insensitive) → check for type and case changes
                if (!SameType(currentMember.DataType, targetMember.DataType))
                {
                    retyped.Add(new RetypedEntry(
                        Name: targetMember.Name,
                        From: currentMember.DataType,
                        To:   targetMember.DataType));
                }

                if (!string.Equals(
                        currentMember.Name, targetMember.Name,
                        StringComparison.Ordinal))
                {
                    renamed.Add(new RenamedEntry(
                        From: currentMember.Name,
                        To:   targetMember.Name));
                }
            }
        }

        // Walk current: detect removed members (not matched in target)
        foreach (var (lowerName, currentMember) in currentByLower)
        {
            if (!targetByLower.ContainsKey(lowerName))
                removed.Add(currentMember.Name);
        }

        // Reordered: compare the relative order of the common members
        var commonCurrentOrder = currentMembers
            .Where(m => targetByLower.ContainsKey(m.Name.ToLowerInvariant()))
            .Select(m => m.Name.ToLowerInvariant())
            .ToList();

        var commonTargetOrder = targetMembers
            .Where(m => currentByLower.ContainsKey(m.Name.ToLowerInvariant()))
            .Select(m => m.Name.ToLowerInvariant())
            .ToList();

        var reordered = !commonCurrentOrder.SequenceEqual(commonTargetOrder);

        return new DryRunDelta(
            Added:     added,
            Removed:   removed,
            Retyped:   retyped,
            Renamed:   renamed,
            Reordered: reordered);
    }
}
