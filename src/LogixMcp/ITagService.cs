// ── Shared result/error types for ITagService ────────────────────────────────

/// <summary>
/// Metadata describing a single PLC tag discovered by <see cref="ITagService.ListTagsAsync"/>.
/// </summary>
/// <param name="Name">Controller-scoped tag name (e.g. <c>Cmd_MotorStart</c>).</param>
/// <param name="DataType">
/// Data-type string as reported by the controller (e.g. <c>BOOL</c>, <c>REAL</c>,
/// <c>MotorCmd</c>).  May be <c>null</c> when the controller does not expose type
/// information for a particular tag.
/// </param>
public record TagInfo(string Name, string? DataType);

/// <summary>
/// The raw value read from a PLC tag by <see cref="ITagService.ReadAsync"/>.
/// Wraps the object value together with the data-type string so callers can
/// interpret the payload without a second round-trip.
/// </summary>
/// <param name="TagName">Name of the tag that was read.</param>
/// <param name="Value">
/// Boxed tag value.  Callers should use <see cref="DataType"/> to cast or
/// format appropriately.  Never <c>null</c>; the driver should represent an
/// uninitialised tag as its zero/default value.
/// </param>
/// <param name="DataType">
/// Data-type string as reported by the controller (e.g. <c>DINT</c>,
/// <c>REAL</c>).  May be <c>null</c> when the controller does not expose type
/// information.
/// </param>
/// <param name="AliasFor">
/// For a read of a program-scoped alias tag: the alias's own <c>AliasFor</c> target (for example
/// <c>_ChangeProfile.Mandrel</c>), whose type and value this result carries.  <c>null</c> for any
/// other read.
/// </param>
/// <param name="ResolvedFrom">
/// For a read of an alias tag: the base path the alias resolves to (for example <c>_ChangeProfile.Mandrel</c>
/// or <c>Ctl_Flags.5</c>), reported whether the value came from the alias's own name or from that base path.
/// <c>null</c> for any other read.
/// </param>
public record TagValue(string TagName, object Value, string? DataType, string? AliasFor = null, string? ResolvedFrom = null);

/// <summary>
/// Structured error returned when a <see cref="ITagService"/> operation fails.
/// Implementations throw (or surface via <see cref="TagServiceException"/>) using
/// one of the <see cref="TagServiceErrorKind"/> values so callers can react
/// programmatically without string parsing.
/// </summary>
/// <param name="Kind">Machine-readable error category.</param>
/// <param name="Message">Human-readable description of the failure.</param>
/// <param name="TagName">
/// Name of the tag involved in the failing operation, or <c>null</c> when the
/// error is not tag-specific (e.g. a connection failure).
/// </param>
/// <param name="SdkCode">
/// The verbatim SDK error code (e.g. <c>"RxE_COMM_DTLERROR"</c>) extracted from
/// the underlying Rockwell SDK exception, when available.  Populated only for
/// <see cref="TagServiceErrorKind.SdkServiceDegraded"/> errors surfaced by the
/// comm-layer triage path.  <c>null</c> for all other error kinds.
/// </param>
/// <param name="ControllerAddress">
/// The controller IP address or comm-path address that was being contacted when
/// the error occurred.  Populated only for
/// <see cref="TagServiceErrorKind.SdkServiceDegraded"/> errors where the IP was
/// derivable from the comm path.  <c>null</c> for all other error kinds.
/// </param>
public record TagServiceError(
    TagServiceErrorKind Kind,
    string              Message,
    string?             TagName           = null,
    string?             SdkCode           = null,
    string?             ControllerAddress = null,
    /// <summary>
    /// "tag" when a top-level tag was not found; "member" when a parent tag was
    /// resolved but a member path under it could not be located.  <c>null</c>
    /// for non-not-found error kinds.
    /// </summary>
    string?             NotFoundWhat      = null,
    /// <summary>
    /// The resolved parent path when <see cref="NotFoundWhat"/> is set.
    /// For a missing tag this is the attempted tag name itself; for a missing
    /// member this is the base tag name (the parent that was successfully found).
    /// <c>null</c> for non-not-found error kinds.
    /// </summary>
    string?             NotFoundPath      = null,
    /// <summary>
    /// Optional actionable remediation text.  Populated for
    /// <see cref="TagServiceErrorKind.CipRuntimeMissing"/>; <c>null</c> otherwise.
    /// </summary>
    string?             Hint              = null,
    /// <summary>
    /// Seconds the caller should wait before retrying. Populated for
    /// <see cref="TagServiceErrorKind.ConnectBusy"/>; <c>null</c> otherwise.
    /// </summary>
    int?                RetryAfterSeconds = null);

/// <summary>
/// Machine-readable category for a <see cref="TagServiceError"/>.
/// </summary>
public enum TagServiceErrorKind
{
    /// <summary>Could not establish or maintain the TCP/IP connection to the gateway.</summary>
    ConnectionFailed,

    /// <summary>Tag name was not found in the controller tag database.</summary>
    TagNotFound,

    /// <summary>
    /// Write was rejected — either by the controller (access denied) or by the
    /// local write-gate policy before the request reached the wire.
    /// </summary>
    WriteDenied,

    /// <summary>
    /// The value supplied to <see cref="ITagService.WriteAsync"/> cannot be
    /// converted to the tag's declared data type.
    /// </summary>
    TypeMismatch,

    /// <summary>
    /// The controller holds a different project image than the one currently
    /// open in the SDK session (RxCL_E_IMAGES_INCOMPATIBLE).  GoOnline (rung 1)
    /// and Upload-then-GoOnline (rung 2) were both tried and failed.  See the
    /// <c>hint</c> field of the resulting error for non-destructive-first
    /// remediation steps.
    /// </summary>
    ImagesIncompatible,

    /// <summary>
    /// A tier-1 <c>logix_pending_edits</c> read confirms the target routine
    /// (or the controller generally) holds a non-<c>"none"</c> edit state —
    /// pending, accepted, or tested rungs are present — and an import
    /// attempted against that routine failed as a result.  Renamed from an earlier,
    /// withdrawn kind <c>ControllerHasPendingEdits</c>: that kind was
    /// inferred from a "Project is not open." heuristic that hardware
    /// verification proved does not indicate pending edits at all.
    /// This kind is only assigned after the evidence-based
    /// <c>logix_pending_edits</c> read confirms a non-<c>"none"</c> state.
    /// The user must commit (<c>logix_finalize_edits</c>) or discard (Logix
    /// Designer) the pending edits before the operation can succeed.
    /// </summary>
    PendingEditsPresent,

    /// <summary>
    /// The Logix Designer SDK's importer rejected a reference in the
    /// imported L5X fragment (an undefined tag or routine) and aborted the
    /// import with no changes made to the controller
    /// (<c>XMLSrv_E_IMPORT_ABORTED_NO_CHANGES</c>).  This is distinct from
    /// <see cref="PendingEditsPresent"/>: the controller had no pending edit
    /// state at all — the fragment itself was invalid.  Check every tag
    /// referenced in the fragment against <c>logix_find_tags</c>.
    /// </summary>
    ImportRejectedByImporter,

    /// <summary>
    /// The Logix Designer SDK reported an <c>RxE_COMM_*</c> comm-layer error
    /// (e.g. <c>RxE_COMM_DTLERROR</c>) from an online tag operation or session
    /// establishment.  The <c>LdSdkService</c> process is running but its
    /// internal communications layer has faulted — distinct from a dead PLC,
    /// where a TCP probe on port 44818 would also fail.
    /// </summary>
    SdkCommLayerFaulted,

    /// <summary>
    /// The Logix Designer SDK reported an <c>RxE_COMM_*</c> comm-layer error
    /// (e.g. <c>RxE_COMM_DTLERROR</c>) on the connect/upload path, and a
    /// subsequent TCP probe confirmed that the controller answers on port 44818.
    ///
    /// <para>
    /// This indicates that the controller itself is reachable on the network but
    /// <c>LdSdkService</c> is running with a faulted communications layer.  The
    /// condition is distinct from <see cref="ConnectionFailed"/>: a TCP probe
    /// on port 44818 succeeds (controller is alive), so the fault is isolated
    /// to the SDK service's comm layer, not to controller reachability.
    /// </para>
    ///
    /// <para>
    /// An operator must restart the <c>LdSdkService</c> process to restore
    /// communications.  This kind is never emitted for the file-based
    /// <c>GoOnlineAsync</c> path — that path uses
    /// <see cref="ProjectNotCorrelated"/> instead.
    /// </para>
    /// </summary>
    SdkServiceDegraded,

    /// <summary>
    /// The Logix Designer SDK's server process (Logix Services) reported a fatal
    /// error (<c>LgxSrv_E_FATAL_ERROR</c>) or is already faulted from an earlier
    /// fatal (<c>LgxSrv_E_SERVER_FAULTED</c>).  The session is dead: later calls
    /// return this kind at once without reaching the SDK.  The session must be
    /// re-established with <c>logix_connect</c> (or closed with
    /// <c>logix_close_project</c>); the controller was not changed by the failed
    /// operation.
    /// </summary>
    SdkServerFaulted,

    /// <summary>
    /// A file-opened project (L5X or uncorrelated ACD) cannot be correlated
    /// with the controller via the file-based <c>GoOnlineAsync</c> path.
    ///
    /// <para>
    /// Opening a project file is for analysis and gate derivation only; it does
    /// not prove the file matches what the controller is running.  The correct
    /// path for an online session is the comm-path/upload route — call
    /// <c>logix_connect</c> with <c>sdkMode="online"</c> and a
    /// <c>gateway</c> or <c>sdkCommPath</c> without a pre-opened project file.
    /// </para>
    ///
    /// <para>
    /// This error is produced by <see cref="SdkCommErrorClassifier"/> when
    /// <c>GoOnlineAsync</c> on the file-based path throws a Rockwell
    /// comm-layer code (<c>RxE_COMM_*</c>).  The TCP probe is not invoked.
    /// </para>
    /// </summary>
    ProjectNotCorrelated,

    /// <summary>
    /// A write was attempted against a UDT member path (e.g. <c>Tag.Member</c>)
    /// via the SDK backend, which cannot address individual UDT members directly.
    /// Use <c>backend=cip</c> for member-level writes.
    /// </summary>
    MemberWriteUnsupported,

    /// <summary>
    /// A STRING (or StringFamily user-defined string) member was read from a
    /// byte array, but its <c>LEN</c> field is negative or exceeds the declared
    /// <c>DATA</c> capacity, so the content cannot be decoded safely.  The
    /// error message names the member and the raw <c>LEN</c> value.
    /// </summary>
    StringLengthInvalid,

    /// <summary>
    /// A member read via the SDK byte-array decode path named something that
    /// cannot be decoded as a single leaf value — a member whose type is
    /// itself a structure (UDT), or a member path the decoder does not
    /// support.  The message names the member and, for structures, its type
    /// and advises reading its atomic or STRING members individually.
    /// </summary>
    MemberPathUnsupported,

    /// <summary>
    /// The wire protocol layer rejected the request or response — libplctag
    /// reported <c>ErrorBadParam</c>, <c>ErrorBadConfig</c>, or
    /// <c>ErrorBadData</c> (e.g. a malformed tag name/attribute string, bad
    /// gateway/path configuration, or a corrupt reply).  Distinct from
    /// <see cref="TypeMismatch"/>, which is reserved for the server's own
    /// value-to-declared-type conversion failures before anything is sent.
    /// The message names the raw libplctag status and the tag.
    /// </summary>
    ProtocolError,

    /// <summary>
    /// The libplctag status <c>ErrorOutOfBounds</c> was reported: the request addressed data
    /// outside the tag's buffer (for example a write wider than the tag).
    /// The message names the raw libplctag status and the tag.
    /// </summary>
    OutOfBounds,

    /// <summary>
    /// A read could not be typed: the tag's data type is not declared in an
    /// open project and is not a whole controller-scoped atomic tag in the
    /// controller tag list.  No value is returned — the type is never guessed
    /// from the tag's byte size.  Open the project (<c>logix_open_project</c>).
    /// </summary>
    TypeUnknown,

    /// <summary>
    /// A read targeted a structure, user-defined type or STRING.  Whole
    /// structures are not decoded; the message lists the member paths when the
    /// project is loaded.  Read the members individually.
    /// </summary>
    StructureReadUnsupported,

    /// <summary>
    /// The CIP library's native component (<c>plctag</c>) could not be loaded
    /// because the Microsoft Visual C++ 2015-2022 Redistributable (x64) is not
    /// installed.  This is a host-machine problem, not a controller problem, so
    /// no controller-reachability advice applies.
    /// </summary>
    CipRuntimeMissing,

    /// <summary>
    /// The value to write is NaN or ±Infinity.  Refused before the backend, whether or
    /// not the tag has bounds, because every Logix compare with NaN is false and so
    /// range checks silently stop tripping.  Nothing was written.
    /// </summary>
    InvalidValue,

    /// <summary>
    /// The tag name or member path contains a control character (after leading and
    /// trailing whitespace is trimmed), so it cannot be matched to the write-gate
    /// policy or its member bounds. Refused before the backend; nothing was written.
    /// </summary>
    InvalidTagName,

    /// <summary>
    /// <c>LGX_TAG_ROUTE=cip</c> and the CIP route to the controller could not be opened. Nothing was
    /// read or written; the SDK route is not used in this mode.
    /// </summary>
    TagRouteUnavailable,

    /// <summary>
    /// A read, type lookup or write of an alias tag could not follow the alias's
    /// <c>AliasFor</c> to a readable target: the target does not exist, the chain is circular or deeper than 4 aliases, or the
    /// target is module I/O whose type the project does not declare.  Nothing was read.
    /// </summary>
    AliasUnresolved,

    /// <summary>
    /// A bit-of-integer path (<c>Tag.N</c> or <c>Tag.Member.N</c>) named a bit the integer does not have
    /// (for example bit 40 of a DINT, which has bits 0 to 31). Nothing was read or written.
    /// </summary>
    BitOutOfRange,

    /// <summary>
    /// A write targeted one bit of an integer (<c>Tag.N</c>). Over CIP that would be a read-modify-write of
    /// the whole integer, which can overwrite a change the running program makes between the read and the
    /// write, so it is refused. Nothing was written. Write the whole integer, or use a BOOL member.
    /// </summary>
    BitWriteUnsupported,

    /// <summary>
    /// A read of module I/O (<c>Local:1:I.Data</c>) on an offline session found the module and its type, but the
    /// offline file holds no value for it (the export carries no decorated data for that module). The message
    /// names the type. Nothing was read; it is not a missing tag.
    /// </summary>
    ValueUnavailableOffline,

    /// <summary>
    /// Another upload or open holds the host-wide upload lease (another server process, or another connect in this
    /// one) and did not release it within the bounded wait. Nothing was uploaded and the other session was not
    /// disturbed. The error carries <see cref="TagServiceError.RetryAfterSeconds"/>.
    /// </summary>
    ConnectBusy,

    /// <summary>
    /// A call found the session's project closed (<c>"Project is not open."</c>), typically because another server
    /// process's reconnect, or a restart of the SDK service, closed it. The session is marked lost and reports
    /// <c>connected:false</c>; re-establish it with <c>logix_connect</c>.
    /// </summary>
    SessionLost,

    /// <summary>Catch-all for unexpected driver or protocol errors.</summary>
    Unknown,
}

/// <summary>
/// Exception thrown by <see cref="ITagService"/> implementations when an
/// operation fails.  Carries a structured <see cref="TagServiceError"/> for
/// programmatic handling alongside the standard exception message and inner
/// exception.
/// </summary>
public class TagServiceException : Exception
{
    /// <summary>Structured error details.</summary>
    public TagServiceError Error { get; }

    /// <summary>
    /// Initialises a new <see cref="TagServiceException"/>.
    /// </summary>
    /// <param name="error">Structured error payload.</param>
    /// <param name="inner">Optional underlying exception that caused this failure.</param>
    public TagServiceException(TagServiceError error, Exception? inner = null)
        : base(error.Message, inner)
    {
        Error = error;
    }
}

/// <summary>
/// Syntax check for a tag name before it is handed to a backend (libplctag parses the name
/// as an attribute string, so characters such as <c>&amp;</c> would inject extra attributes).
/// </summary>
public static class TagNameValidator
{
    private const string Ident = "[A-Za-z_][A-Za-z0-9_]*";
    private const string Index = @"\[[0-9]+(?:,[0-9]+){0,2}\]";

    private static readonly System.Text.RegularExpressions.Regex Pattern = new(
        $@"\A{Ident}(?::[A-Za-z0-9_]+)*(?:{Index})*(?:\.(?:{Ident}|[0-9]+)(?:{Index})*)*\z",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>true</c> when every path segment is an identifier (<c>[A-Za-z_][A-Za-z0-9_]*</c>),
    /// optionally with <c>[n]</c>/<c>[n,m]</c> indices, joined by <c>.</c> (members, bit numbers)
    /// and <c>:</c> (<c>Program:</c> and module tags).
    /// </summary>
    /// <param name="tagName">Candidate tag name.</param>
    public static bool IsValid(string? tagName) =>
        !string.IsNullOrEmpty(tagName) && IsAscii(tagName) && Pattern.IsMatch(tagName);

    private static bool IsAscii(string s)
    {
        foreach (var c in s)
        {
            if (c > 0x7F) return false;
        }
        return true;
    }

    /// <summary>
    /// Throws <see cref="TagServiceException"/> with <see cref="TagServiceErrorKind.InvalidTagName"/>
    /// when <paramref name="tagName"/> is not a valid tag path.
    /// </summary>
    /// <param name="tagName">Tag name to check.</param>
    /// <param name="operation">Verb for the message, e.g. <c>Read</c> or <c>Write</c>.</param>
    public static void ThrowIfInvalid(string? tagName, string operation)
    {
        if (IsValid(tagName)) return;
        var shown = (tagName ?? string.Empty).Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        throw new TagServiceException(new TagServiceError(
            TagServiceErrorKind.InvalidTagName,
            $"{operation} refused: tag name '{shown}' is not a valid tag path. Allowed: identifier " +
            "segments ([A-Za-z_][A-Za-z0-9_]*) joined by '.', with [n] or [n,m] indices and ':' " +
            "for Program:/module tags.",
            tagName ?? string.Empty));
    }
}

// ── ITagService ───────────────────────────────────────────────────────────────

/// <summary>
/// Architectural contract for communicating with an Allen-Bradley / Logix PLC
/// over EtherNet/IP.  Every concrete driver (live CIP, simulator, stub) and the
/// write-gate decorator implement this interface.
///
/// <para>
/// All methods are asynchronous and <see cref="System.Threading.CancellationToken"/>-aware.
/// Implementations must throw <see cref="TagServiceException"/> on failures so
/// callers can inspect the structured <see cref="TagServiceError"/> without
/// parsing exception messages.
/// </para>
/// </summary>
public interface ITagService
{
    /// <summary>
    /// Establishes a connection to the Logix controller reachable at
    /// <paramref name="gateway"/> via the CIP path <paramref name="path"/>.
    /// </summary>
    /// <param name="gateway">
    /// IP address or hostname of the EtherNet/IP gateway (e.g. <c>"198.51.100.10"</c>).
    /// </param>
    /// <param name="path">
    /// CIP backplane path to the controller slot (e.g. <c>"1,0"</c>).
    /// Use an empty string or <c>"1,0"</c> for a single-slot chassis.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the connection attempt.</param>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/> when the
    /// gateway is unreachable or the path is invalid.
    /// </exception>
    Task ConnectAsync(string gateway, string path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gracefully closes the connection to the controller and releases any
    /// driver-managed resources.  Safe to call even when not connected;
    /// implementations must not throw in that case.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the disconnect operation.</param>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the current value of the named tag from the controller.
    /// </summary>
    /// <param name="tagName">
    /// Controller-scoped tag name (e.g. <c>"Cmd_MotorStart"</c>).
    /// Member access notation (e.g. <c>"Cmd_Motor.Speed"</c>) is supported when
    /// the underlying driver allows it.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the read operation.</param>
    /// <returns>
    /// A <see cref="TagValue"/> containing the raw value and its data-type string.
    /// </returns>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.TagNotFound"/> when the tag does
    /// not exist, or <see cref="TagServiceErrorKind.ConnectionFailed"/> when the
    /// controller is not reachable.
    /// </exception>
    Task<TagValue> ReadAsync(string tagName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="value"/> to the named tag on the controller.
    /// </summary>
    /// <param name="tagName">
    /// Controller-scoped tag name (e.g. <c>"Cmd_SpeedSetpoint"</c>).
    /// </param>
    /// <param name="value">
    /// Value to write.  The implementation is responsible for converting
    /// <paramref name="value"/> to the controller's native data type; a
    /// <see cref="TagServiceException"/> with <see cref="TagServiceErrorKind.TypeMismatch"/>
    /// is thrown when conversion is not possible.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the write operation.</param>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.TagNotFound"/>,
    /// <see cref="TagServiceErrorKind.WriteDenied"/>,
    /// <see cref="TagServiceErrorKind.TypeMismatch"/>, or
    /// <see cref="TagServiceErrorKind.ConnectionFailed"/> as appropriate.
    /// </exception>
    Task WriteAsync(string tagName, object value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns metadata for all controller-scoped tags visible to the connected
    /// session.  Data-type information is included where the controller exposes it.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the listing operation.</param>
    /// <returns>
    /// A read-only list of <see cref="TagInfo"/> records, one per discovered tag,
    /// in an unspecified order.  May return an empty list when no tags are
    /// accessible; never returns <c>null</c>.
    /// </returns>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/> when the
    /// controller is not reachable.
    /// </exception>
    Task<IReadOnlyList<TagInfo>> ListTagsAsync(
        CancellationToken cancellationToken = default);
}
