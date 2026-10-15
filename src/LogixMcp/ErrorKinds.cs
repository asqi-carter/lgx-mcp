/// <summary>
/// Shared machine-readable <c>kind</c> values carried by structured tool errors.
/// </summary>
public static class ErrorKinds
{
    /// <summary>
    /// The tool needs a live session (<c>logix_connect</c>) and none is active.
    /// </summary>
    public const string NoActiveSession = "NoActiveSession";

    /// <summary>
    /// A rung edit was attempted on an SDK session that is not online (an offline project session): a pending
    /// controller edit needs a session connected to a live controller (G25).
    /// </summary>
    public const string SessionNotOnline = "SessionNotOnline";

    /// <summary>
    /// A <c>logix_connect</c> is still running in the background. Returned (with <c>phase</c>, <c>elapsedMs</c>,
    /// <c>connectId</c>) instead of <see cref="NoActiveSession"/> by tools that need the session, and when a
    /// <c>logix_connect</c> with different arguments is refused. Poll <c>logix_connect_status</c>.
    /// </summary>
    public const string ConnectInProgress = "ConnectInProgress";

    /// <summary>
    /// <c>logix_connect_status</c> was called but no background <c>logix_connect</c> is running and none has run.
    /// </summary>
    public const string NoConnectInProgress = "NoConnectInProgress";

    /// <summary>
    /// <c>logix_connect_status</c> found no connect in this server, but the cross-instance connect registry shows
    /// a connect owned by another server process (running, or finished with an outcome). This instance cannot use
    /// that connection.
    /// </summary>
    public const string ConnectOwnedByOtherInstance = "ConnectOwnedByOtherInstance";

    /// <summary>
    /// ListIdentity could not be read from the controller, so its product cannot be established.
    /// </summary>
    public const string IdentityUnavailable = "IdentityUnavailable";

    /// <summary>
    /// The controller's ListIdentity product name does not start with <c>Emulate</c>; scripted
    /// tests (<c>logix_run_test</c>) never run on real hardware.
    /// </summary>
    public const string EmulatorOnly = "EmulatorOnly";

    /// <summary>
    /// A <c>logix_run_test</c> case sets a tag that tests may never write (program-written,
    /// External Access not Read/Write, created by this session, or unclassifiable). Nothing was written.
    /// </summary>
    public const string TestWriteRefused = "TestWriteRefused";

    /// <summary>
    /// The controller fault record (CIP class 0x73 instance 1) could not be read or parsed
    /// (<c>logix_diagnose</c>).
    /// </summary>
    public const string FaultRecordUnavailable = "FaultRecordUnavailable";

    /// <summary>
    /// <c>logix_write</c> on a live CIP session that has no project-derived write gate and no
    /// site allowlist. Nothing was written.
    /// </summary>
    public const string WriteGateRequired = "WriteGateRequired";

    /// <summary>
    /// <c>LGX_AUDIT_FAIL_CLOSED=1</c> is set and the pre-write check found the audit log cannot
    /// be opened for append (audit directory not writable, disk full). Nothing was written to the controller.
    /// </summary>
    public const string AuditUnavailable = "AuditUnavailable";

    /// <summary>
    /// <c>logix_write</c> value is NaN or ±Infinity (as a string such as <c>NaN</c>,
    /// <c>-Infinity</c>, <c>∞</c>, or as a number). Refused before the backend. Nothing was written.
    /// </summary>
    public const string InvalidValue = nameof(TagServiceErrorKind.InvalidValue);

    /// <summary>
    /// A <c>program</c> argument names a program the open project does not have (<c>logix_lint</c>).
    /// </summary>
    public const string ProgramNotFound = "ProgramNotFound";

    /// <summary>
    /// <c>logix_import_datatype</c> saveAs already exists and <c>overwrite=true</c> was not
    /// passed. Nothing was written.
    /// </summary>
    public const string TargetExists = "TargetExists";

    /// <summary>
    /// The operation needs the Logix Designer SDK and it is not available (for example an
    /// <c>.ACD</c> saveAs for <c>logix_import_datatype</c> without the SDK). Nothing was written.
    /// </summary>
    public const string SdkRequired = "SdkRequired";

    /// <summary>
    /// Untrusted XML / L5X input exceeded the 64 MB cap enforced by <c>SafeXml</c>. Nothing was parsed.
    /// </summary>
    public const string InputTooLarge = "InputTooLarge";

    /// <summary>
    /// Untrusted XML / L5X input is malformed or contains a DTD, which <c>SafeXml</c> prohibits.
    /// </summary>
    public const string InvalidL5x = "InvalidL5x";

    /// <summary>
    /// A live-controller change (<c>logix_write</c>, mode change, publish, download) was refused because
    /// <c>LGX_ENABLE_LIVE_WRITES</c> is not set on the server. Nothing was sent to the controller.
    /// </summary>
    public const string CapabilityDisabled = "CapabilityDisabled";

    /// <summary>
    /// <c>logix_import_datatype</c> ran the SDK import but the re-exported type does not have the requested
    /// members (for example <c>collision: Discard</c> kept the existing type). The temp file was removed;
    /// nothing was written.
    /// </summary>
    public const string ImportNotApplied = "ImportNotApplied";

    /// <summary>
    /// <c>logix_import_datatype</c> with <c>collision: Cancel</c> met an existing type, so the SDK aborted the
    /// import (<c>XMLSrv_E_IMPORT_ABORTED_NO_CHANGES</c>). Nothing was written.
    /// </summary>
    public const string ImportCancelledOnCollision = "ImportCancelledOnCollision";

    /// <summary>
    /// <c>logix_connect</c> found a registered project whose file no longer exists on disk (an upload's copy is removed
    /// when its session closes). Nothing was connected; reconnect with a comm path or open a project file.
    /// </summary>
    public const string ProjectFileMissing = "ProjectFileMissing";

    /// <summary>
    /// <c>logix_connect</c> needs a loaded project (virtual mode, <c>backend="l5x"</c>, or an offline SDK connect) and none
    /// is loaded. Nothing was connected; the <c>hint</c> names the next steps.
    /// </summary>
    public const string NoProjectLoaded = "NoProjectLoaded";

    /// <summary>
    /// The tool needs an SDK capability that is unavailable on this Logix Designer version or install
    /// (see <c>lgx://sdk/capabilities</c>). Refused before the SDK was called; nothing was changed.
    /// </summary>
    public const string SdkCapabilityUnavailable = "SdkCapabilityUnavailable";

    /// <summary>
    /// The SDK server (Logix Services) faulted (<c>LgxSrv_E_FATAL_ERROR</c> / <c>LgxSrv_E_SERVER_FAULTED</c>); the
    /// session is dead and the SDK was not called. Re-establish it with <c>logix_connect</c>.
    /// </summary>
    public const string SdkServerFaulted = nameof(TagServiceErrorKind.SdkServerFaulted);

    /// <summary>
    /// Another server process (or another connect) holds the host-wide upload lease past the bounded wait. Nothing
    /// was uploaded; retry after <c>retryAfterSeconds</c>.
    /// </summary>
    public const string ConnectBusy = nameof(TagServiceErrorKind.ConnectBusy);

    /// <summary>
    /// The session's project was found closed (<c>"Project is not open."</c>); the session is marked lost and
    /// reports <c>connected:false</c>. Re-establish it with <c>logix_connect</c>.
    /// </summary>
    public const string SessionLost = nameof(TagServiceErrorKind.SessionLost);

    /// <summary>
    /// Value of the <c>session</c> field that <c>logix_pending_edits</c>, <c>logix_doctor</c> and the other
    /// SDK-backed tools report when the SDK server has faulted (kind <see cref="SdkServerFaulted"/>).
    /// </summary>
    public const string SessionFaulted = "faulted";

    /// <summary>
    /// <c>logix_build</c> asked the SDK to build and the SDK could not perform the operation (a server or
    /// internal error). The build did not run, so this is not a build failure; nothing was changed.
    /// </summary>
    public const string BuildNotPerformed = "BuildNotPerformed";

    /// <summary>
    /// <c>LGX_TAG_ROUTE=cip</c> is set and CIP cannot connect to the session's controller. Nothing was
    /// read or written; the SDK route is not used in <c>cip</c> mode.
    /// </summary>
    public const string TagRouteUnavailable = nameof(TagServiceErrorKind.TagRouteUnavailable);

    /// <summary>
    /// A tool threw an unexpected exception. The error carries the tool name, exception type and the
    /// first line of its message; the full exception is in the server log.
    /// </summary>
    public const string InternalError = "InternalError";

    /// <summary>
    /// A required tool argument was missing or could not be bound. Nothing was changed.
    /// </summary>
    public const string InvalidArguments = "InvalidArguments";

    /// <summary>
    /// The target program holds edits that are not yet assembled, so the SDK refuses every import into it
    /// (<c>XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM</c>). Reported by the pre-check and by the mapping of that
    /// SDK mnemonic on every import path. Nothing was imported.
    /// </summary>
    public const string PendingEditsBlockImport = "PendingEditsBlockImport";

    /// <summary>
    /// A cancel or accept of pending edits was refused because the routine (and, for cancel, its program) has
    /// nothing pending: <c>logix_pending_edits</c> reports <c>state='none'</c>. Nothing was sent to Studio.
    /// </summary>
    public const string NothingPending = "NothingPending";

    /// <summary>
    /// Another client, or Studio 5000 itself, holds the exclusive lock the controller needs for the operation
    /// (<c>RxCsE_ASA_BLOCKING_HIGHER_EXCL_LOCK</c>), for example while it is importing. Transient: the server
    /// retried a bounded number of times before reporting it. Nothing was imported.
    /// </summary>
    public const string ControllerLocked = "ControllerLocked";

    /// <summary>
    /// A publish failed with <c>RxE_ILLEGAL_OPERATIONS</c> (<c>80042021</c>, "Sequence of operations not allowed") with no
    /// rung-level reason and the program holds no pending edits afterwards: another client was editing at the same moment.
    /// Nothing was imported. Retryable; carries <c>retryAfterSeconds</c>.
    /// </summary>
    public const string ControllerBusy = "ControllerBusy";

    /// <summary>
    /// A bare tag name matches program-scoped tags in two or more programs and no controller tag has that name,
    /// so the request cannot pick one. The error lists the qualified names (<c>\Program.Tag</c>); the matches are
    /// never merged. Returned by <c>logix_verify_interlock</c> and <c>logix_cross_reference</c>.
    /// </summary>
    public const string AmbiguousTagName = "AmbiguousTagName";

    /// <summary>
    /// <c>logix_connect</c> with <c>backend="sdk"</c> could not route the <c>commPath</c> (the SDK route error).
    /// The status mirror carries the same kind.
    /// </summary>
    public const string CommPathInvalid = "CommPathInvalid";

    /// <summary>
    /// A <c>routine</c> argument names a routine the program does not have. The message lists the routines the
    /// program does have.
    /// </summary>
    public const string RoutineNotFound = "RoutineNotFound";

    /// <summary>
    /// A <c>tags[]</c> declaration names a tag that already exists with a different data type than the one
    /// declared. Nothing was staged or published.
    /// </summary>
    public const string TagTypeConflict = "TagTypeConflict";

    /// <summary>
    /// A <c>tags[]</c> declaration names a data type that is neither an atomic type nor a type in the project.
    /// Refused at stage; nothing was staged.
    /// </summary>
    public const string UnknownDataType = "UnknownDataType";

    /// <summary>
    /// A workstation-stage import was refused because the importer's log carries the rung-text scanner's syntax
    /// error (for example <c>XIC(A)[OTE(B);</c>). The error carries the log's line and column and the offending
    /// text. Nothing was staged.
    /// </summary>
    public const string RungSyntaxError = "RungSyntaxError";

    /// <summary>
    /// A tag path indexes an array past its dimension (<c>Arr[5]</c> on a <c>[5]</c> array). The error names the
    /// tag, the index and the dimension. Nothing was read or written.
    /// </summary>
    public const string IndexOutOfRange = "IndexOutOfRange";

    /// <summary>
    /// A tag path names a member the tag's data type does not have. The error lists the members the type has.
    /// Nothing was read or written.
    /// </summary>
    public const string MemberNotFound = "MemberNotFound";

    /// <summary>
    /// <c>logix_write</c> targeted a whole structure, user-defined type, STRING or TIMER. Writes are one leaf at
    /// a time; the error lists the members. Nothing was written.
    /// </summary>
    public const string StructureWriteUnsupported = "StructureWriteUnsupported";

    /// <summary>
    /// The controller behind the session changed outside this session in a way the session image cannot follow: a
    /// different controller name or serial, or a re-download of the project (routines the image holds are gone).
    /// Image readers refuse until <c>logix_connect</c> runs again; live tag reads and <c>logix_pending_edits</c> still work.
    /// </summary>
    public const string ControllerChanged = "ControllerChanged";

    /// <summary>
    /// An accept was refused because the program's pending zone is a pending zone this server did not publish
    /// (another tool or person staged it), so accepting would make edits permanent that nobody here reviewed.
    /// </summary>
    public const string ForeignPendingZone = "ForeignPendingZone";
}
