#if LOGIX_SDK
// ── TypedSdkTagService — Logix Designer SDK ITagService with L5X type resolution ──
using RockwellAutomation.LogixDesigner;
using L5Sharp.Core;
using Google.Protobuf;

// Alias System.Threading.Tasks.Task to avoid ambiguity with L5Sharp.Core.Task
// when both namespaces are in scope.
using Task = System.Threading.Tasks.Task;

using ILogixProjectHandle = RockwellAutomation.LogixDesigner.ILogixProject;
using OperationMode       = RockwellAutomation.LogixDesigner.LogixProject.OperationMode;
using SdkDataType         = RockwellAutomation.LogixDesigner.LogixProject.DataType;

/// <summary>
/// Concrete <see cref="ITagService"/> implementation backed by the Logix
/// Designer SDK (<see cref="ILogixProject"/> gRPC handle) with type resolution
/// from an open L5X project.
///
/// <para>
/// Unlike <see cref="LibPlcTagService"/>, which infers a tag's type from its
/// byte size on the wire (silently misidentifying REAL as DINT when both are
/// 4 bytes), this service resolves each tag's <em>declared</em> type from the
/// L5X project and routes every read/write to the matching typed SDK accessor:
/// <c>GetTagValueBOOLAsync</c>, <c>GetTagValueREALAsync</c>, etc.
/// </para>
///
/// <para>
/// Silent coercion is explicitly prohibited.  Passing an <see cref="int"/> to a
/// REAL tag — or a <see cref="float"/> to a DINT tag — raises
/// <see cref="TagServiceErrorKind.TypeMismatch"/> before any SDK call is made.
/// The key acceptance scenario is a REAL round-tripping as
/// <see cref="float"/> rather than as a DINT bit-pattern.
/// </para>
///
/// <para>
/// Tag names are passed directly to the SDK typed accessors in their native
/// dotted form: plain names (<c>Tag</c>), member paths (<c>Tag.Member</c>),
/// indexed accesses (<c>Tag[i].Member</c>, <c>Tag.Member[i]</c>), and
/// program-scoped paths (<c>Program:Name.Tag.Member</c>) are all accepted by
/// the SDK without any manual XPath construction.
/// </para>
///
/// <para>
/// The <see cref="LogixProjectSession"/> must be in the <c>Open</c> state
/// (via <see cref="LogixProjectSession.OpenAsync"/>) before any tag operation
/// is attempted.  Operations against a closed or not-yet-opened session return
/// a structured <see cref="TagServiceErrorKind.ConnectionFailed"/> error.
/// </para>
/// </summary>
public sealed class TypedSdkTagService : ITagService, IControllerModeSource, IRoutineL5xSource, IControllerImageSource, ISessionTypeSource, IAliasAware
{
    private readonly LogixProjectSession _session;
    private readonly L5X _typeProject;
    private readonly OperationMode _operationMode;

    /// <summary>
    /// Guards every access to <see cref="_typeProject"/>'s tag lookup: type
    /// resolution takes it for the duration of a (synchronous) lookup, and the
    /// type-miss refresh takes it while adding the exported tag.
    /// </summary>
    private readonly object _typeLock = new();

    private long _typeSnapshotTicks = DateTimeOffset.UtcNow.UtcTicks;

    /// <summary>
    /// UTC time the type snapshot (<c>_typeProject</c>) was built or last extended by an
    /// on-demand tag refresh. Read by <c>logix_doctor</c> to report the snapshot's age.
    /// </summary>
    public DateTimeOffset TypeSnapshotAt =>
        new(Interlocked.Read(ref _typeSnapshotTicks), TimeSpan.Zero);

    /// <summary>
    /// Serialises type-miss refreshes so concurrent misses on the same name
    /// issue one SDK export, not one each (the later caller re-checks the
    /// lookup after acquiring the gate).
    /// </summary>
    private readonly SemaphoreSlim _typeRefreshGate = new(1, 1);

    // ── Injectable delegates for SCM race-avoidance re-query ──────

    /// <summary>
    /// Delegate that queries whether the <c>LdSdkService</c> Windows service is
    /// currently running.  Injectable for unit tests.
    ///
    /// <para>
    /// Returns <c>true</c> when the service is running; <c>false</c> otherwise.
    /// On any query failure the default implementation returns <c>true</c> to avoid
    /// falsely asserting a stopped service.
    /// </para>
    /// </summary>
    private readonly Func<bool> _ldSdkServiceRunning;

    /// <summary>
    /// Delegate that checks whether at least one <c>LdSdkServer.exe</c> process
    /// is visible in the process list.  Injectable for unit tests.
    ///
    /// <para>
    /// Returns <c>true</c> when at least one <c>LdSdkServer</c> process is found;
    /// <c>false</c> when no such process is running.  On any query failure the
    /// default implementation returns <c>true</c> to avoid falsely asserting a
    /// dead service.
    /// </para>
    /// </summary>
    private readonly Func<bool> _ldSdkServerProcessRunning;

    /// <summary>
    /// Delegate used to introduce timed delays inside
    /// <see cref="BuildDeadSessionExceptionForInSessionAsync"/> between successive
    /// service/process re-queries.  Injectable for unit tests so tests complete
    /// without real waits.
    ///
    /// <para>
    /// Signature: <c>(duration, ct) → Task</c>.
    /// The production constructor wires this to <c>Task.Delay</c>.
    /// </para>
    /// </summary>
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // ── Production default implementations ────────────────────────────────────

    /// <summary>
    /// Production implementation of <see cref="_ldSdkServiceRunning"/>: queries
    /// the Windows service control manager for the current status of
    /// <c>LdSdkService</c>.
    ///
    /// <para>
    /// Returns <c>true</c> when the service is
    /// <see cref="System.ServiceProcess.ServiceControllerStatus.Running"/>;
    /// <c>false</c> for any other status.  On any query failure the method
    /// returns <c>true</c> so callers retain the service-faulted message rather
    /// than falsely asserting a stopped service.
    /// </para>
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Interoperability", "CA1416:Validate platform compatibility",
        Justification = "TypedSdkTagService.cs is compiled only under EnableLogixSdk=true, " +
                        "which is a Windows-only build condition.")]
    private static bool DefaultLdSdkServiceRunning()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController("LdSdkService");
            return sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Production implementation of <see cref="_ldSdkServerProcessRunning"/>:
    /// checks whether at least one <c>LdSdkServer.exe</c> process is currently
    /// visible in the Windows process list.
    ///
    /// <para>
    /// Returns <c>true</c> when <c>Process.GetProcessesByName("LdSdkServer")</c>
    /// returns at least one entry; <c>false</c> when the list is empty.  On any
    /// query failure the method returns <c>true</c> to avoid falsely asserting a
    /// dead service.
    /// </para>
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Interoperability", "CA1416:Validate platform compatibility",
        Justification = "TypedSdkTagService.cs is compiled only under EnableLogixSdk=true, " +
                        "which is a Windows-only build condition.")]
    private static bool DefaultLdSdkServerProcessRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("LdSdkServer").Length > 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Initialises a new <see cref="TypedSdkTagService"/>.
    /// </summary>
    /// <param name="session">
    /// The Logix Designer SDK session that wraps the
    /// <see cref="ILogixProject"/> gRPC handle.  Must not be <c>null</c>.
    /// The session must be opened via
    /// <see cref="LogixProjectSession.OpenAsync"/> before performing tag
    /// operations.
    /// </param>
    /// <param name="typeProject">
    /// The in-memory L5X project used for tag-type resolution.  Must not be
    /// <c>null</c>.  Every declared tag type is looked up here; the SDK wire
    /// is never consulted for type information.
    /// </param>
    /// <param name="operationMode">
    /// <see cref="OperationMode.Offline"/> for reads/writes against the
    /// project file only; <see cref="OperationMode.Online"/> for reads/writes
    /// against a live controller.  Defaults to
    /// <see cref="OperationMode.Offline"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="session"/> or
    /// <paramref name="typeProject"/> is <c>null</c>.
    /// </exception>
    public TypedSdkTagService(
        LogixProjectSession session,
        L5X typeProject,
        OperationMode operationMode = OperationMode.Offline)
        : this(session, typeProject, operationMode,
               DefaultLdSdkServiceRunning, DefaultLdSdkServerProcessRunning,
               static (duration, ct) => Task.Delay(duration, ct))
    { }

    /// <summary>
    /// Testing constructor — allows injection of service-state checkers and delay
    /// so unit tests can verify the dead-session re-query path without real waits
    /// or Windows service queries.
    ///
    /// <para>
    /// When <paramref name="ldSdkServiceRunning"/> or
    /// <paramref name="ldSdkServerProcessRunning"/> is <c>null</c>, defaults to
    /// <c>() =&gt; true</c> (service assumed running), keeping existing tests
    /// passing without modification.  When <paramref name="delay"/> is
    /// <c>null</c>, defaults to <c>Task.Delay</c>.
    /// </para>
    /// </summary>
    internal TypedSdkTagService(
        LogixProjectSession session,
        L5X typeProject,
        OperationMode operationMode,
        Func<bool>? ldSdkServiceRunning,
        Func<bool>? ldSdkServerProcessRunning,
        Func<TimeSpan, CancellationToken, Task>? delay)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(typeProject);
        _session                   = session;
        _typeProject               = typeProject;
        _operationMode             = operationMode;
        _ldSdkServiceRunning       = ldSdkServiceRunning       ?? (() => true);
        _ldSdkServerProcessRunning = ldSdkServerProcessRunning ?? (() => true);
        _delay                     = delay ?? (static (duration, ct) => Task.Delay(duration, ct));
    }

    /// <summary>
    /// Exposes the underlying <see cref="LogixProjectSession"/> so that SDK tool
    /// methods (e.g. <see cref="LogixSdkTools"/>) can reuse the active live
    /// session rather than opening a redundant new gRPC handle.
    ///
    /// <para>
    /// Approach rationale: the session is already held here (option a — property on
    /// <see cref="TypedSdkTagService"/>), making it directly reachable from the tool
    /// layer via the <see cref="GatedTagService.InnerService"/> traversal path.
    /// This avoids introducing a new global or a parallel registration in
    /// <see cref="TagSessionService"/>.
    /// </para>
    /// </summary>
    public LogixProjectSession Session => _session;

    /// <inheritdoc />
    /// <remarks>
    /// Exports the routine through <see cref="ExportRungsHandler"/> (the same export
    /// <c>logix_export_rungs</c> returns, pending edits included).
    /// </remarks>
    public async Task<string> ExportRoutineL5xAsync(string program, string routine, CancellationToken ct = default)
    {
        if (program.Contains('\'') || routine.Contains('\''))
            throw new ArgumentException("Program and routine names cannot contain a quote.");
        var xPath = $"Controller/Programs/Program[@Name='{program}']/Routines/Routine[@Name='{routine}']";
        var result = await ExportRungsHandler.ExecuteAsync(xPath, _session, ct).ConfigureAwait(false);
        return result switch
        {
            ExportRungsResult ok    => ok.L5X,
            ExportRungsError  error => throw new InvalidOperationException(error.Error),
            _                       => throw new InvalidOperationException("The export returned nothing."),
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// One partial export of the whole program, pending edits included: the
    /// refresh of the session's controller image after a same-session change, not a full upload.
    /// </remarks>
    public async Task<string> ExportProgramL5xAsync(string program, CancellationToken ct = default)
    {
        if (program.Contains('\''))
            throw new ArgumentException("Program name cannot contain a quote.");
        return await ExportPartAsync($"Controller/Programs/Program[@Name='{program}']", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<string> ExportControllerTagsL5xAsync(CancellationToken ct = default) =>
        ExportPartAsync("Controller/Tags", ct);

    private async Task<string> ExportPartAsync(string xPath, CancellationToken ct)
    {
        var result = await ExportRungsHandler.ExecuteAsync(xPath, _session, ct).ConfigureAwait(false);
        return result switch
        {
            ExportRungsResult ok    => ok.L5X,
            ExportRungsError  error => throw new InvalidOperationException(error.Error),
            _                       => throw new InvalidOperationException("The export returned nothing."),
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the mode via <see cref="ControllerModeReader"/> — the same SDK call
    /// (<c>ReadControllerModeAsync</c>) and mapping that <c>logix_pending_edits</c>
    /// uses — so the two tools cannot disagree.
    /// </remarks>
    public Task<ControllerModeReading> ReadControllerModeAsync(CancellationToken ct = default) =>
        ControllerModeReader.ReadAsync(_session, ct);

    // ── ITagService ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Validates that the <see cref="LogixProjectSession"/> is in the
    /// <c>Open</c> state.  The <paramref name="gateway"/> and
    /// <paramref name="path"/> arguments are not used — session lifecycle
    /// is managed separately via
    /// <see cref="LogixProjectSession.OpenAsync"/>.
    /// </remarks>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/> when
    /// the session has not been opened or has been disposed.
    /// </exception>
    public Task ConnectAsync(string gateway, string path,
        CancellationToken cancellationToken = default)
    {
        // GetProject() throws TagServiceException(ConnectionFailed) when the
        // session is not in the Open state.
        _session.GetProject();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// No-op.  Session lifecycle is managed via
    /// <see cref="LogixProjectSession"/>.  Callers should call
    /// <see cref="LogixProjectSession.CloseAsync"/> or
    /// <see cref="LogixProjectSession.Dispose"/> to release the SDK handle.
    /// </remarks>
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// For <b>atomic / plain tags</b> (<c>Tag</c>, <c>Program:Name.Tag</c>):
    /// resolves the declared type from the L5X project, translates to SDK XPath
    /// via <see cref="TagXPathTranslator.Translate"/>, and calls the matching
    /// typed <c>GetTagValueXXXAsync</c> accessor.
    /// </para>
    /// <para>
    /// For <b>UDT member paths</b> (<c>Tag.Member</c>, <c>Tag.Member[i]</c>,
    /// <c>Tag.Sub.Member</c>, <c>Program:Name.Tag.Member</c>): reads the parent
    /// tag as a raw byte array via <c>GetTagValueAsync(…, ByteArray)</c>, then
    /// uses <see cref="UdtLayout"/> to compute the member's byte offset within
    /// the parent and decodes the appropriate typed value from the slice.
    /// This avoids <c>XMLSrv_E_TARGET_NOT_FOUND</c> errors that the SDK typed
    /// accessors return for dotted member paths.
    /// </para>
    /// <para>
    /// The key distinction from <see cref="LibPlcTagService"/>: a REAL tag
    /// returns a <see cref="float"/> value (not an <see cref="int"/> bit-
    /// pattern), because the type is known from the project before the SDK
    /// call is issued.
    /// </para>
    /// </remarks>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/> when
    /// the session is not open, <see cref="TagServiceErrorKind.TagNotFound"/>
    /// when the tag cannot be resolved, or
    /// <see cref="TagServiceErrorKind.Unknown"/> when the SDK call fails.
    /// </exception>
    public System.Threading.Tasks.Task<TagValue> ReadAsync(
        string tagName,
        CancellationToken cancellationToken = default)
        => Aliases.ReadAsync(tagName, ReadPlainAsync, aliasNameFirst: false, cancellationToken);

    /// <summary>
    /// The shared alias resolver over this session's declared types. An alias tag has no data of its own in the
    /// SDK session (an offline project, or an online one read over the SDK route), so a read goes through its base
    /// and carries <c>aliasFor</c> / <c>resolvedFrom</c>.
    /// </summary>
    public AliasResolver Aliases => _aliases ??= new AliasResolver(this);

    private AliasResolver? _aliases;

    private async System.Threading.Tasks.Task<TagValue> ReadPlainAsync(
        string tagName,
        CancellationToken cancellationToken)
    {
        // Bit of an integer (Tag.5, Tag.Member.5, Local:1:I.Data.3): read the parent integer and select
        // the bit, with the same range rule as the CIP route.
        if (LibPlcTagService.TryParseBitPath(tagName, out var bitParent, out var bit)
            && await TryReadBitAsync(tagName, bitParent, bit, cancellationToken) is { } bitValue)
            return bitValue;

        // Module I/O has no tag in an offline file: its value, if any, is in the export's decorated structure.
        if (_operationMode == OperationMode.Offline && ModuleIoTypes.IsModuleIoPath(tagName)
            && ReadOfflineModuleIo(tagName) is { } ioValue)
            return ioValue;

        // UDT member paths (dot-notation) are routed through the byte-array
        // decode path to avoid XMLSrv_E_TARGET_NOT_FOUND from the typed
        // SDK accessors.
        // Array-element names (Tag[i], Program:P.Tag[i]) take the same route: the
        // typed accessors would see XPath Tag[@Name='Tag[i]'], which the SDK
        // reports as XMLSrv_E_TARGET_NOT_FOUND.
        if (IsMemberPath(tagName) || tagName.Contains('['))
            return await ReadMemberViaByteArrayAsync(tagName, cancellationToken);

        var declaredType = await ResolveDeclaredTypeWithRefreshAsync(tagName, cancellationToken);

        // REGRESSION-CITE: 09c88af
        var xpath = TagXPathTranslator.Translate(tagName);

        var (value, type) = await _session.ExecuteAsync(
            async project => await ReadTypedAsync(project, xpath, declaredType, cancellationToken),
            cancellationToken);

        return new TagValue(tagName, value, type);
    }

    private CipTypeResolver? _typeResolver;

    /// <summary>
    /// The shared type resolver over this session's declarations (the one the CIP route uses), so a bit path's
    /// parent is typed the same way on every backend: project tags and members, aliases, module I/O.
    /// </summary>
    private CipTypeResolver TypeResolver => _typeResolver ??= CipTypeResolver.ForSession(
        this, _ => System.Threading.Tasks.Task.FromResult<IReadOnlyList<TagInfo>>(Array.Empty<TagInfo>()));

    /// <summary>
    /// Reads <c>parent.bit</c> when <paramref name="parentPath"/> is an integer. Returns <c>null</c> when the
    /// parent is not a typed integer, so the caller keeps its normal (member / not-found) behaviour.
    /// </summary>
    /// <exception cref="TagServiceException"><see cref="TagServiceErrorKind.BitOutOfRange"/> beyond the integer's width.</exception>
    private async System.Threading.Tasks.Task<TagValue?> TryReadBitAsync(
        string tagName, string parentPath, int bit, CancellationToken ct)
    {
        var parent = await TypeResolver.ResolveAsync(parentPath, ct).ConfigureAwait(false);
        if (!parent.IsResolved || parent.Kind != CipTypeKind.Atomic || parent.TypeName is null) return null;

        var type  = parent.TypeName.ToUpperInvariant();
        var width = type switch
        {
            "SINT" or "USINT" => 8,
            "INT" or "UINT"   => 16,
            "DINT" or "UDINT" => 32,
            "LINT" or "ULINT" => 64,
            _                 => 0,
        };
        if (width == 0) return null;
        if (bit >= width) throw LibPlcTagService.BitOutOfRange(tagName, type, bit);

        var read = await ReadPlainAsync(parentPath, ct).ConfigureAwait(false);
        var raw = read.Value switch
        {
            sbyte v  => (long)v,
            short v  => v,
            int v    => v,
            long v   => v,
            byte v   => v,
            ushort v => v,
            uint v   => v,
            ulong v  => unchecked((long)v),
            _        => throw new TagServiceException(new TagServiceError(
                            TagServiceErrorKind.TypeMismatch,
                            $"Cannot read '{tagName}': its parent '{parentPath}' did not return an integer.",
                            tagName)),
        };
        return new TagValue(tagName, ((raw >> bit) & 1L) != 0, "BOOL");
    }

    /// <summary>
    /// Reads module I/O on an offline session from the file's decorated data. <c>null</c> when the path names no
    /// module in the project (the caller reports it as not found).
    /// </summary>
    /// <exception cref="TagServiceException">
    /// <see cref="TagServiceErrorKind.ValueUnavailableOffline"/> when the module exists but the file holds no value.
    /// </exception>
    private TagValue? ReadOfflineModuleIo(string tagName)
    {
        var reading = Lookup(project =>
            CipTypeResolver.TryParsePath(tagName, out var segments, out _)
                ? ModuleIoTypes.ReadValue(project, segments)
                : null);
        if (reading is null || !reading.IsModule) return null;

        if (reading.Value is null)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ValueUnavailableOffline,
                reading.Reason ?? $"The offline file holds no value for module I/O '{tagName}' (type {reading.TypeName}).",
                tagName,
                Hint: "connect to the controller to read live module I/O"));

        return new TagValue(tagName, reading.Value, reading.TypeName);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Resolves <paramref name="tagName"/>'s declared type from the L5X
    /// project, validates that <paramref name="value"/> is compatible with
    /// that type (raising <see cref="TagServiceErrorKind.TypeMismatch"/> when
    /// it is not), translates the name to SDK XPath form via
    /// <see cref="TagXPathTranslator.Translate"/>, then routes to the matching
    /// typed <c>SetTagValueXXXAsync</c> accessor.
    /// </para>
    /// <para>
    /// Accepted path forms: controller-scoped bare names (<c>Tag</c>) and
    /// program-scoped names (<c>Program:Name.Tag</c>).  Dot-notation member
    /// paths are not accepted — use the byte-array read route for UDT member
    /// reads.
    /// </para>
    /// <para>
    /// Silent coercion is explicitly prohibited: passing <c>(int)42</c> to a
    /// REAL tag raises <see cref="TagServiceErrorKind.TypeMismatch"/> — the
    /// caller must pass <c>42.0f</c> instead.
    /// </para>
    /// </remarks>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/>,
    /// <see cref="TagServiceErrorKind.TagNotFound"/>, or
    /// <see cref="TagServiceErrorKind.TypeMismatch"/> as appropriate.
    /// </exception>
    public async Task WriteAsync(string tagName, object value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        // NaN / ±Infinity are refused before anything else (InvalidValue).
        WriteTypeRule.RejectNonFinite(tagName, value, "TypedSdkTagService");

        // Reject UDT member-path writes immediately — the SDK typed accessors
        // cannot address individual UDT members; the CIP backend must be used instead.
        if (IsMemberPath(tagName))
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.MemberWriteUnsupported,
                "SDK backend cannot address a UDT member; use backend=cip for member writes",
                tagName));

        var declaredType = ResolveDeclaredType(tagName);
        ValidateWriteTypeCompatibility(tagName, declaredType, value);

        // REGRESSION-CITE: 09c88af
        var xpath = TagXPathTranslator.Translate(tagName);

        await _session.ExecuteAsync(
            project => WriteTypedAsync(project, xpath, declaredType, value, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns controller-scoped tags from the in-memory L5X project.
    /// The session must be open; this method validates the session state
    /// before reading from the project.  No SDK call is issued.
    /// </remarks>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/> when
    /// the session has not been opened or has been disposed.
    /// </exception>
    public System.Threading.Tasks.Task<IReadOnlyList<TagInfo>> ListTagsAsync(
        CancellationToken cancellationToken = default)
    {
        // Validate that the session is open before accessing the project.
        _session.GetProject();

        IReadOnlyList<TagInfo> list = _typeProject.Tags
            .Select(t => new TagInfo(t.Name, t.DataType))
            .ToList()
            .AsReadOnly();

        return System.Threading.Tasks.Task.FromResult(list);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when <paramref name="tagName"/> is a UDT member path
    /// (a dot-notation path where the dot represents a UDT member access rather
    /// than the program-scope separator in a program-scoped name).
    ///
    /// <para>
    /// For controller-scoped names any dot denotes a UDT member (e.g.
    /// <c>"Tag.Member"</c>).  For program-scoped names
    /// (<c>"Program:ProgramName.TagName"</c>) the first dot is the required
    /// program/tag separator; a <em>second</em> dot denotes a UDT member (e.g.
    /// <c>"Program:Main.Tag.Member"</c>).
    /// </para>
    /// </summary>
    private static bool IsMemberPath(string tagName)
    {
        if (tagName.StartsWith("Program:", StringComparison.OrdinalIgnoreCase))
        {
            // "Program:ProgramName.TagName"        — the single dot is required; not a member path.
            // "Program:ProgramName.TagName.Member" — the second dot is a UDT member access.
            var rest     = tagName["Program:".Length..];
            var firstDot = rest.IndexOf('.');
            if (firstDot < 0) return false;
            return rest.IndexOf('.', firstDot + 1) >= 0;
        }
        // For controller-scoped names, any dot indicates a UDT member path.
        return tagName.IndexOf('.') >= 0;
    }

    /// <summary>
    /// Reads a UDT member path by fetching the parent tag as a raw byte array
    /// from the SDK and decoding the member value at its computed byte offset.
    ///
    /// <para>
    /// This path avoids <c>XMLSrv_E_TARGET_NOT_FOUND</c> errors that the
    /// SDK typed accessors return for dotted member paths.
    /// </para>
    ///
    /// <para>
    /// The member's declared type is obtained from <see cref="UdtLayout"/>
    /// (which reads the L5X DataType definition) rather than from L5Sharp
    /// <c>Tag.Member()</c> traversal.  This keeps the byte-array decode path
    /// independent of whether L5Sharp can navigate a given UDT tag's members.
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task<TagValue> ReadMemberViaByteArrayAsync(
        string tagName, CancellationToken ct)
    {
        // ── Split into base tag, leading array subscripts and member path ─────
        ParseReadPath(tagName, out var parentTagName, out var subscripts, out var memberPath);

        // ── Resolve the base tag's declared type (TagNotFound when absent) ────
        var parentType = await ResolveDeclaredTypeWithRefreshAsync(parentTagName, ct);

        // ── Translate base tag to SDK XPath ───────────────────────────────────
        var parentXPath = TagXPathTranslator.Translate(parentTagName);

        // ── Fetch the parent tag as a raw byte array ──────────────────────────
        ByteString byteString;
        try
        {
            byteString = await _session.ExecuteAsync(
                project => project.GetTagValueAsync(parentXPath, _operationMode, SdkDataType.BYTE_ARRAY),
                ct);
        }
        catch (TagServiceException)
        {
            throw;
        }
        catch (Exception ex)
            when (ex.Message.Contains("TARGET_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
        {
            // The base tag is absent on the controller: typed kind, not Unknown.
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TagNotFound,
                $"SDK could not find tag '{parentXPath}': {ex.Message}",
                tagName,
                SdkCode: ex.Message,
                NotFoundWhat: "tag",
                NotFoundPath: tagName), ex);
        }
        catch (Exception ex)
        {
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.Unknown,
                $"SDK GetTagValueAsync (ByteArray) failed for '{parentXPath}': {ex.Message}",
                tagName), ex);
        }

        var bytes = byteString.ToByteArray();

        // ── Compute byte offset/size and member type via UdtLayout ────────────
        // The member's declared type is read directly from the L5X DataType
        // definition by UdtLayout — no L5Sharp Tag.Member() traversal required.
        MemberLayout layout;
        try
        {
            layout = ResolveReadLayout(parentTagName, parentType, subscripts, memberPath);
        }
        catch (TagServiceException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TagNotFound,
                $"Could not resolve '{tagName}' (member '{memberPath ?? "<element>"}' of type '{parentType}'): {ex.Message}",
                tagName,
                NotFoundWhat: "member",
                NotFoundPath: parentTagName), ex);
        }

        // The declared type of the member comes from UdtLayout (L5X DataType
        // definition), not from L5Sharp Tag navigation.
        var memberType = layout.LogixType;

        // ── Reject non-leaf members (nested structures) ───────────────────────
        // Only atomic and STRING-family leaves are decodable; a member whose
        // type is itself a UDT must be read leaf by leaf.
        if (layout.StringDataLength < 0 && !IsAtomicType(memberType))
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.MemberPathUnsupported,
                $"member '{memberPath ?? parentTagName}' is a structure (type {memberType}); " +
                "read its atomic or STRING members individually",
                tagName));

        // ── Validate the byte array covers the member ─────────────────────────
        if (layout.ByteOffset + layout.ByteSize > bytes.Length)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.Unknown,
                $"Byte array for '{parentTagName}' has {bytes.Length} bytes but " +
                $"member '{memberPath ?? "<element>"}' requires bytes [{layout.ByteOffset}..{layout.ByteOffset + layout.ByteSize - 1}].",
                tagName));

        // ── Decode the typed value from the byte slice ────────────────────────
        if (layout.StringDataLength >= 0)
        {
            var text = DecodeStringBytes(bytes, layout, tagName);
            return new TagValue(tagName, text, memberType);
        }

        var value = DecodeAtomicBytes(bytes, layout, memberType);
        return new TagValue(tagName, value, memberType);
    }

    /// <summary>
    /// Splits a read path into the base tag name (with any <c>Program:P.</c>
    /// prefix), the array subscripts that directly follow the base tag, and the
    /// member path beneath them.
    ///
    /// <para>
    /// <c>"Tag[2]"</c> → (<c>Tag</c>, [2], null);
    /// <c>"Program:P.Tag[1,2].Member"</c> → (<c>Program:P.Tag</c>, [1,2], <c>Member</c>);
    /// <c>"Tag.A.B"</c> → (<c>Tag</c>, [], <c>A.B</c>).
    /// </para>
    /// </summary>
    /// <exception cref="TagServiceException">
    /// <see cref="TagServiceErrorKind.TagNotFound"/> when the path is malformed.
    /// </exception>
    private static void ParseReadPath(string tagName,
        out string parentTagName, out List<int> subscripts, out string? memberPath)
    {
        TagServiceException Malformed() => new(new TagServiceError(
            TagServiceErrorKind.TagNotFound,
            $"Malformed tag path '{tagName}'.",
            tagName,
            NotFoundWhat: "tag",
            NotFoundPath: tagName));

        var prefix = string.Empty;
        var rest   = tagName;
        const string programPrefix = "Program:";
        if (tagName.StartsWith(programPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var afterPrefix = tagName[programPrefix.Length..];
            var dot         = afterPrefix.IndexOf('.');
            if (dot <= 0) throw Malformed();
            prefix = programPrefix + afterPrefix[..dot] + ".";
            rest   = afterPrefix[(dot + 1)..];
        }

        var end       = rest.IndexOfAny(new[] { '.', '[' });
        var baseName  = end < 0 ? rest : rest[..end];
        var remaining = end < 0 ? string.Empty : rest[end..];
        if (baseName.Length == 0) throw Malformed();

        parentTagName = prefix + baseName;
        subscripts    = new List<int>();

        while (remaining.StartsWith('['))
        {
            var close = remaining.IndexOf(']');
            if (close < 0) throw Malformed();
            foreach (var part in remaining[1..close].Split(','))
            {
                if (!int.TryParse(part.Trim(), out var idx) || idx < 0) throw Malformed();
                subscripts.Add(idx);
            }
            remaining = remaining[(close + 1)..];
        }

        if (remaining.Length == 0)
            memberPath = null;
        else if (remaining.StartsWith('.') && remaining.Length > 1)
            memberPath = remaining[1..];
        else
            throw Malformed();
    }

    /// <summary>
    /// Computes the byte layout of the element or member a read path selects
    /// within the base tag's byte array.
    /// </summary>
    /// <param name="parentTagName">Base tag name (with any program prefix).</param>
    /// <param name="parentType">Declared type of the base tag (the element type for arrays).</param>
    /// <param name="subscripts">Array subscripts applied to the base tag; empty for none.</param>
    /// <param name="memberPath">Member path beneath the element, or <c>null</c> for the element itself.</param>
    /// <exception cref="TagServiceException">
    /// <see cref="TagServiceErrorKind.TagNotFound"/> when the subscripts do not fit the
    /// tag's declared dimensions.
    /// </exception>
    private MemberLayout ResolveReadLayout(string parentTagName, string parentType,
        IReadOnlyList<int> subscripts, string? memberPath)
    {
        var udt = new UdtLayout(_typeProject);

        TagServiceException BadSubscript(string why) => new(new TagServiceError(
            TagServiceErrorKind.TagNotFound,
            $"Array subscript [{string.Join(",", subscripts)}] on '{parentTagName}' is invalid: {why}",
            parentTagName,
            NotFoundWhat: "member",
            NotFoundPath: parentTagName));

        var elementOffset = 0;
        var elementSize   = 0;
        var linear        = 0;
        var isArray       = subscripts.Count > 0;

        if (isArray)
        {
            TryGetRefreshTarget(parentTagName, out var programName, out var baseName);
            var tag = FindTag(programName, baseName)
                ?? throw BadSubscript("tag not found in the L5X project.");

            // Read the declared dimensions from the tag's own Dimensions attribute
            // ("4", "2 3", "2,3,4"), the same source DeclaredMemberTypes.RankOf uses.
            var rawDims = tag.Serialize().Attribute("Dimensions")?.Value;
            List<int> dims;
            if (rawDims is not null)
            {
                dims = rawDims
                    .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => int.TryParse(x, out var n) ? n : 0)
                    .Where(n => n > 0)
                    .ToList();
            }
            else
            {
                var d = tag.Dimensions;
                dims = d.IsEmpty
                    ? new List<int>()
                    : new[] { (int)d.X, (int)d.Y, (int)d.Z }.Take((int)d.Rank).ToList();
            }
            if (dims.Count == 0)
                throw BadSubscript("the tag is not an array.");
            if (dims.Count != subscripts.Count)
                throw BadSubscript($"the tag has {dims.Count} dimension(s).");

            for (var i = 0; i < dims.Count; i++)
            {
                if (subscripts[i] >= dims[i])
                    throw BadSubscript($"index {subscripts[i]} is out of range (length {dims[i]}).");
                linear = linear * dims[i] + subscripts[i];
            }

            elementSize   = udt.SizeOf(parentType);
            elementOffset = linear * elementSize;
        }

        if (memberPath is null)
        {
            // BOOL arrays are bit-packed 32 to a DINT.
            if (isArray && string.Equals(parentType, "BOOL", StringComparison.OrdinalIgnoreCase))
                return new MemberLayout((linear / 32) * 4, 4, "BOOL", linear % 32);

            return new MemberLayout(elementOffset, elementSize, parentType,
                StringDataLength: udt.StringDataLength(parentType) ?? -1);
        }

        var member = udt.Resolve(parentType, memberPath);
        return member with { ByteOffset = member.ByteOffset + elementOffset };
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="logixType"/> is a Logix atomic
    /// type that <see cref="DecodeAtomicBytes"/> can decode.
    /// </summary>
    private static bool IsAtomicType(string logixType) =>
        logixType.ToUpperInvariant() is "BOOL" or "SINT" or "INT" or "DINT" or "LINT"
            or "USINT" or "UINT" or "UDINT" or "ULINT" or "REAL" or "LREAL";

    /// <summary>
    /// Decodes a typed .NET value from the byte slice described by
    /// <paramref name="layout"/> within <paramref name="bytes"/>, using the
    /// Logix type name <paramref name="logixType"/> to select the decoder.
    ///
    /// <para>
    /// Logix byte arrays are little-endian.  BOOL values are decoded from bit
    /// <see cref="MemberLayout.BitIndex"/> of the host word at
    /// <see cref="MemberLayout.ByteOffset"/>.
    /// </para>
    /// </summary>
    private static object DecodeAtomicBytes(byte[] bytes, MemberLayout layout, string logixType)
    {
        int offset = layout.ByteOffset;

        return logixType.ToUpperInvariant() switch
        {
            "BOOL" when layout.BitIndex >= 0
                => DecodePackedBool(bytes, offset, layout.ByteSize, layout.BitIndex),
            "BOOL"
                => (bytes[offset] & 1) != 0,
            "SINT"   => (sbyte)bytes[offset],
            "INT"    => BitConverter.ToInt16(bytes, offset),
            "DINT"   => BitConverter.ToInt32(bytes, offset),
            "LINT"   => BitConverter.ToInt64(bytes, offset),
            "USINT"  => bytes[offset],
            "UINT"   => BitConverter.ToUInt16(bytes, offset),
            "UDINT"  => BitConverter.ToUInt32(bytes, offset),
            "ULINT"  => BitConverter.ToUInt64(bytes, offset),
            "REAL"   => BitConverter.ToSingle(bytes, offset),
            "LREAL"  => BitConverter.ToDouble(bytes, offset),
            _ => throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.Unknown,
                $"Cannot decode type '{logixType}' from byte array — only atomic types supported.",
                "byte-array")),
        };
    }

    /// <summary>
    /// Decodes a <c>STRING</c> or StringFamily user-defined string member.
    /// <c>LEN</c> is a little-endian DINT at the member offset; <c>DATA</c> is
    /// <c>SINT[n]</c> at +4 (<see cref="MemberLayout.StringDataLength"/> = n:
    /// 82 for STRING, the declared dimension for user types).  The first
    /// <c>LEN</c> bytes are decoded as Latin-1 so bytes 0x80-0xFF round-trip.
    /// </summary>
    /// <param name="bytes">Raw parent-tag byte array.</param>
    /// <param name="layout">Resolved member layout (must be a string member).</param>
    /// <param name="tagName">Full tag/member path, for error reporting.</param>
    /// <returns>The decoded string.</returns>
    /// <exception cref="TagServiceException">
    /// <see cref="TagServiceErrorKind.StringLengthInvalid"/> when LEN is
    /// negative or greater than the DATA length.
    /// </exception>
    private static string DecodeStringBytes(byte[] bytes, MemberLayout layout, string tagName)
    {
        const int DataOffset = 4;
        int offset  = layout.ByteOffset;
        int dataLen = layout.StringDataLength;

        if (offset + DataOffset + dataLen > bytes.Length)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.Unknown,
                $"Byte array has {bytes.Length} bytes but string member '{tagName}' requires " +
                $"{DataOffset + dataLen} bytes at offset {offset}.",
                tagName));

        int len = BitConverter.ToInt32(bytes, offset);
        if (len < 0 || len > dataLen)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.StringLengthInvalid,
                $"String member '{tagName}' has invalid LEN {len} (valid range 0..{dataLen}).",
                tagName));

        return System.Text.Encoding.Latin1.GetString(bytes, offset + DataOffset, len);
    }

    /// <summary>
    /// Extracts a single BOOL bit from a packed host word (SINT=1 byte,
    /// DINT=4 bytes) at <paramref name="byteOffset"/> within
    /// <paramref name="bytes"/>.
    /// </summary>
    private static bool DecodePackedBool(byte[] bytes, int byteOffset, int hostSize, int bitIndex)
    {
        uint host = hostSize switch
        {
            1 => bytes[byteOffset],
            4 => BitConverter.ToUInt32(bytes, byteOffset),
            _ => BitConverter.ToUInt32(bytes, byteOffset), // fallback to 4-byte read
        };
        return (host & (1u << bitIndex)) != 0;
    }

    /// <summary>
    /// Resolves the declared Logix data-type string for
    /// <paramref name="tagName"/> from the in-memory L5X project.
    /// </summary>
    /// <param name="tagName">
    /// Tag name in one of the three supported forms:
    /// <list type="bullet">
    ///   <item>Plain controller-scoped name: <c>"Cmd_MotorStart"</c></item>
    ///   <item>UDT member path: <c>"Cmd_Motor.Speed"</c></item>
    ///   <item>Program-scoped: <c>"Program:MainProgram.SomeTag"</c></item>
    /// </list>
    /// </param>
    /// <returns>
    /// The declared data-type string (e.g. <c>"BOOL"</c>, <c>"REAL"</c>,
    /// <c>"DINT"</c>).
    /// </returns>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.TagNotFound"/> when the
    /// tag cannot be resolved in the L5X project.
    /// </exception>
    private string ResolveDeclaredType(string tagName)
    {
        lock (_typeLock)
            return ResolveDeclaredTypeCore(tagName);
    }

    /// <summary>
    /// Read/poll type resolution with a one-shot refresh on a tag miss.
    ///
    /// <para>
    /// A tag created after the type project was loaded (for
    /// example by a publish or by Studio 5000) is absent from
    /// <see cref="_typeProject"/>, so a read of it used to fail with
    /// <see cref="TagServiceErrorKind.TagNotFound"/> even though the controller
    /// has it.  On such a miss we do ONE targeted SDK partial export of the tag,
    /// add it to <see cref="_typeProject"/>, and resolve again.  The refresh runs
    /// at most once per miss — there is no retry loop and no polling — and
    /// <see cref="TagServiceErrorKind.TagNotFound"/> is returned only when the
    /// export also has no such tag.  Writes deliberately do not call this: they
    /// keep their gate order, and the gate denies created tags before type
    /// resolution matters.
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task<string> ResolveDeclaredTypeWithRefreshAsync(
        string tagName, CancellationToken ct)
    {
        try
        {
            return ResolveDeclaredType(tagName);
        }
        catch (TagServiceException ex)
            when (ex.Error.Kind == TagServiceErrorKind.TagNotFound
                  && ex.Error.NotFoundWhat != "member")
        {
            if (!await RefreshTypeFromSdkExportAsync(tagName, ct).ConfigureAwait(false))
                throw;

            // Single re-resolve; if it still misses, that TagNotFound is final.
            return ResolveDeclaredType(tagName);
        }
    }

    /// <summary>
    /// Exports <c>Controller/Tags/Tag[@Name='base']</c> (or the program-scoped
    /// equivalent) from the SDK session, parses it with L5Sharp and adds the tag
    /// to <see cref="_typeProject"/>.  Reuses <see cref="ExportRungsHandler"/>'s
    /// partial-export helper.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the tag is now present in the type project;
    /// <c>false</c> when the name is not refreshable, the export failed, or the
    /// export contained no such tag.  Never throws except for cancellation.
    /// </returns>
    private async System.Threading.Tasks.Task<bool> RefreshTypeFromSdkExportAsync(
        string tagName, CancellationToken ct)
    {
        if (!TryGetRefreshTarget(tagName, out var programName, out var baseName))
            return false;

        await _typeRefreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A concurrent caller may have refreshed while we waited.
            if (FindTag(programName, baseName) is not null)
                return true;

            // Program-scoped tags can only be added to a program we already know.
            if (programName is not null && FindProgram(programName) is null)
                return false;

            var xPath = programName is null
                ? $"Controller/Tags/Tag[@Name='{baseName}']"
                : $"Controller/Programs/Program[@Name='{programName}']/Tags/Tag[@Name='{baseName}']";

            var exported = await ExportRungsHandler.ExecuteAsync(xPath, _session, ct)
                .ConfigureAwait(false);
            if (exported is not ExportRungsResult ok)
                return false;

            Tag? exportedTag;
            try
            {
                var parsed = SafeXml.ParseL5x(ok.L5X);
                exportedTag = programName is null
                    ? parsed.Tags.FirstOrDefault(t => NameEquals(t.Name, baseName))
                    : parsed.Programs
                        .FirstOrDefault(p => NameEquals(p.Name, programName))
                        ?.Tags.FirstOrDefault(t => NameEquals(t.Name, baseName));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unparseable export: treat as "no such tag".
                return false;
            }

            if (exportedTag is null)
                return false;

            lock (_typeLock)
            {
                Interlocked.Exchange(ref _typeSnapshotTicks, DateTimeOffset.UtcNow.UtcTicks);
                if (programName is null)
                {
                    _typeProject.Tags.Add(exportedTag);
                }
                else
                {
                    FindProgram(programName)?.Tags.Add(exportedTag);
                }
            }

            return FindTag(programName, baseName) is not null;
        }
        finally
        {
            _typeRefreshGate.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The CIP route resolves types through this same lookup, so it takes the same lock the
    /// SDK route takes for its own lookups.
    /// </remarks>
    public TResult Lookup<TResult>(Func<L5X, TResult> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        lock (_typeLock)
            return lookup(_typeProject);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The same one-shot partial re-export the SDK route runs on a tag miss
    /// (<see cref="RefreshTypeFromSdkExportAsync"/>); also called after a publish that created tags.
    /// </remarks>
    public System.Threading.Tasks.Task<bool> RefreshAsync(string tagPath, CancellationToken cancellationToken = default)
        => RefreshTypeFromSdkExportAsync(tagPath, cancellationToken);

    private static bool NameEquals(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private L5Sharp.Core.Program? FindProgram(string programName)
    {
        lock (_typeLock)
            return _typeProject.Programs.FirstOrDefault(p => NameEquals(p.Name, programName));
    }

    private Tag? FindTag(string? programName, string baseName)
    {
        lock (_typeLock)
        {
            if (programName is null)
                return _typeProject.Tags.FirstOrDefault(t => NameEquals(t.Name, baseName));

            return FindProgram(programName)?.Tags.FirstOrDefault(t => NameEquals(t.Name, baseName));
        }
    }

    /// <summary>
    /// Splits <paramref name="tagName"/> into the (optional) program name and the
    /// base tag name (stripped of any member path or array index) that a refresh
    /// should export.  Returns <c>false</c> when the name is not a plain
    /// identifier (so it can never be interpolated into an XPath unsafely).
    /// </summary>
    private static bool TryGetRefreshTarget(string tagName, out string? programName, out string baseName)
    {
        programName = null;
        baseName    = string.Empty;

        var rest = tagName;
        const string prefix = "Program:";
        if (rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = rest[prefix.Length..];
            var dot = rest.IndexOf('.');
            if (dot <= 0) return false;
            programName = rest[..dot];
            rest        = rest[(dot + 1)..];
        }

        var end = rest.IndexOfAny(new[] { '.', '[' });
        baseName = end >= 0 ? rest[..end] : rest;

        return IsIdentifier(baseName) && (programName is null || IsIdentifier(programName));
    }

    private static bool IsIdentifier(string s)
        => s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    private string ResolveDeclaredTypeCore(string tagName)
    {
        // ── Program-scoped: "Program:ProgramName.TagName[.Member...]" ─────────
        if (tagName.StartsWith("Program:", StringComparison.OrdinalIgnoreCase))
            return ResolveProgramScopedType(tagName);

        // ── Controller-scoped (simple name or UDT dot-notation) ───────────────
        // Resolved from our own XML lookup (controller tag collection + DataType
        // definitions via UdtLayout). L5Sharp's whole-project lookup is never used
        // here: it tokenises the whole project and throws when any rung (e.g. an
        // AOI rung with "Units per sec2") is unparsable.
        SplitTagPath(tagName, out var baseName, out var memberPath);

        var baseTag = _typeProject.Tags.FirstOrDefault(t => NameEquals(t.Name, baseName));
        if (baseTag is null)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TagNotFound,
                $"Tag '{tagName}' not found in the L5X project.",
                tagName,
                NotFoundWhat: "tag",
                NotFoundPath: tagName));

        if (memberPath is null)
            return DeclaredTypeFromTag(baseTag);

        return ResolveMemberType(baseTag, memberPath)
            ?? throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TagNotFound,
                $"Member '{memberPath}' not found on tag '{baseName}' in the L5X project.",
                tagName,
                NotFoundWhat: "member",
                NotFoundPath: baseName));
    }

    /// <summary>
    /// Splits a tag path (without any <c>Program:</c> prefix) into the base tag name and
    /// the member path beneath it. Array subscripts directly on the base tag
    /// (<c>Tag[3].Member</c>) are dropped, since an element has the tag's declared type.
    /// </summary>
    /// <param name="path">Tag path such as <c>Tag</c>, <c>Tag.A.B</c> or <c>Tag[2].A</c>.</param>
    /// <param name="baseName">The base tag name.</param>
    /// <param name="memberPath">The member path, or <c>null</c> when the path names the tag itself.</param>
    private static void SplitTagPath(string path, out string baseName, out string? memberPath)
    {
        var end = path.IndexOfAny(new[] { '.', '[' });
        if (end < 0)
        {
            baseName   = path;
            memberPath = null;
            return;
        }

        baseName = path[..end];
        var rest = path[end..];
        while (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close < 0) break;
            rest = rest[(close + 1)..];
        }

        memberPath = rest.StartsWith('.') && rest.Length > 1 ? rest[1..] : null;
    }

    /// <summary>
    /// Resolves the declared type for a program-scoped tag name of the form
    /// <c>"Program:ProgramName.TagName"</c> or
    /// <c>"Program:ProgramName.TagName.Member"</c>.
    /// </summary>
    /// <param name="tagName">The full program-scoped tag name.</param>
    /// <returns>The declared data-type string.</returns>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.TagNotFound"/> when the
    /// program or tag cannot be resolved.
    /// </exception>
    private string ResolveProgramScopedType(string tagName)
    {
        const string prefix = "Program:";
        var rest = tagName[prefix.Length..];

        var firstDot = rest.IndexOf('.');
        if (firstDot < 0)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TagNotFound,
                $"Invalid program-scoped tag name '{tagName}'. " +
                $"Expected format: 'Program:ProgramName.TagName'.",
                tagName,
                NotFoundWhat: "tag",
                NotFoundPath: tagName));

        var programName = rest[..firstDot];
        var memberPath  = rest[(firstDot + 1)..];

        // Locate the program in the L5X project.
        var program = FindProgramUnlocked(programName);

        if (program is null)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TagNotFound,
                $"Program '{programName}' not found in the L5X project.",
                tagName,
                NotFoundWhat: "tag",
                NotFoundPath: tagName));

        // Separate the base tag name from any sub-member path.
        SplitTagPath(memberPath, out var baseTagName, out var subMemberPath);

        // Look up the base tag in the program's tag collection.
        var programTag = program.Tags.FirstOrDefault(t => NameEquals(t.Name, baseTagName));

        if (programTag is null)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TagNotFound,
                $"Tag '{baseTagName}' not found in program '{programName}'.",
                tagName,
                NotFoundWhat: "tag",
                NotFoundPath: $"Program:{programName}"));

        if (subMemberPath is not null)
        {
            var memberType = ResolveMemberType(programTag, subMemberPath);
            if (memberType is null)
                throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.TagNotFound,
                    $"Member path '{subMemberPath}' not found under tag '{baseTagName}' " +
                    $"in program '{programName}'.",
                    tagName,
                    NotFoundWhat: "member",
                    NotFoundPath: $"Program:{programName}.{baseTagName}"));
            return memberType;
        }

        return DeclaredTypeFromTag(programTag);
    }

    private L5Sharp.Core.Program? FindProgramUnlocked(string programName)
        => _typeProject.Programs.FirstOrDefault(p => NameEquals(p.Name, programName));

    /// <summary>
    /// Resolves the declared type of <paramref name="memberPath"/> beneath
    /// <paramref name="baseTag"/> from the project's own DataType and AOI definitions plus
    /// the predefined types (<see cref="DeclaredMemberTypes"/>), not from L5Sharp member
    /// navigation or its project index.
    /// </summary>
    /// <param name="baseTag">The root tag the path starts from.</param>
    /// <param name="memberPath">Member path (e.g. <c>Speed</c>, <c>Drive.Speed</c>, <c>Arr[2]</c>).</param>
    /// <returns>The member's Logix type, or <c>null</c> when the path cannot be resolved.</returns>
    private string? ResolveMemberType(Tag baseTag, string memberPath)
    {
        try
        {
            return new DeclaredMemberTypes(_typeProject).Resolve(DeclaredTypeFromTag(baseTag), memberPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
                                       or FormatException or OverflowException)
        {
            // Malformed definitions or path — treat as not found.
            return null;
        }
    }

    /// <summary>
    /// The Logix declared-type string of <paramref name="tag"/>, read from the tag's own
    /// <c>DataType</c> attribute (no value decoding, no project index).
    /// </summary>
    /// <param name="tag">The resolved tag.</param>
    /// <returns>The Logix type name string.</returns>
    private static string DeclaredTypeFromTag(Tag tag)
        => tag.Serialize().Attribute("DataType")?.Value ?? tag.DataType;

    /// <summary>
    /// Validates that <paramref name="value"/>'s .NET type is compatible with
    /// the Logix <paramref name="declaredType"/> without any coercion.
    ///
    /// <para>
    /// Every Logix atomic type maps to exactly one .NET type; the caller must
    /// supply that exact type.  For example, a REAL tag requires
    /// <see cref="float"/> — passing <see cref="int"/> or
    /// <see cref="double"/> is a type mismatch.
    /// </para>
    /// </summary>
    /// <param name="tagName">Tag name used in the error payload.</param>
    /// <param name="declaredType">Logix type string resolved from the L5X project.</param>
    /// <param name="value">The value to validate.</param>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.TypeMismatch"/> when the
    /// .NET type of <paramref name="value"/> does not exactly match the
    /// expected type for <paramref name="declaredType"/>.
    /// </exception>
    private static void ValidateWriteTypeCompatibility(
        string tagName, string declaredType, object value)
    {
        // Shared with the CIP backend so both return the same TypeMismatch.
        WriteTypeRule.Validate(tagName, declaredType, value);
    }

    /// <summary>
    /// Routes a tag read to the appropriate typed SDK accessor based on
    /// <paramref name="declaredType"/> and the configured
    /// <see cref="OperationMode"/>.
    /// </summary>
    /// <param name="project">Open SDK project handle.</param>
    /// <param name="xpath">Tag path passed directly to the SDK typed accessor.</param>
    /// <param name="declaredType">Logix type string resolved from the L5X project.</param>
    /// <returns>
    /// A tuple of the boxed .NET value and the Logix type string.
    /// </returns>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.Unknown"/> when the type is
    /// not one of the supported atomic types.
    /// </exception>
    private async System.Threading.Tasks.Task<(object Value, string DataType)>
        ReadTypedAsync(ILogixProjectHandle project, string xpath, string declaredType,
            CancellationToken ct)
    {
        try
        {
            return declaredType.ToUpperInvariant() switch
            {
                "BOOL"   => ((object)await project.GetTagValueBOOLAsync(xpath,  _operationMode), "BOOL"),
                "SINT"   => ((object)await project.GetTagValueSINTAsync(xpath,  _operationMode), "SINT"),
                "INT"    => ((object)await project.GetTagValueINTAsync(xpath,   _operationMode), "INT"),
                "DINT"   => ((object)await project.GetTagValueDINTAsync(xpath,  _operationMode), "DINT"),
                "LINT"   => ((object)await project.GetTagValueLINTAsync(xpath,  _operationMode), "LINT"),
                "USINT"  => ((object)await project.GetTagValueUSINTAsync(xpath, _operationMode), "USINT"),
                "UINT"   => ((object)await project.GetTagValueUINTAsync(xpath,  _operationMode), "UINT"),
                "UDINT"  => ((object)await project.GetTagValueUDINTAsync(xpath, _operationMode), "UDINT"),
                "ULINT"  => ((object)await project.GetTagValueULINTAsync(xpath, _operationMode), "ULINT"),
                "REAL"   => ((object)await project.GetTagValueREALAsync(xpath,  _operationMode), "REAL"),
                "LREAL"  => ((object)await project.GetTagValueLREALAsync(xpath, _operationMode), "LREAL"),
                "STRING" => ((object)await project.GetTagValueSTRINGAsync(xpath, _operationMode), "STRING"),
                _ => throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.Unknown,
                    $"Tag type '{declaredType}' is not supported for typed read access. " +
                    $"Only atomic types (BOOL, SINT, INT, DINT, LINT, REAL, LREAL, " +
                    $"USINT, UINT, UDINT, ULINT, STRING) are supported.",
                    xpath)),
            };
        }
        catch (TagServiceException)
        {
            throw;
        }
        catch (OperationNotPerformedException ex)
            when (SdkCommErrorClassifier.IsDeadSessionIndicator(ex))
        {
            // Dead-session indicator (e.g. "Unable to complete connection to LdSdkService")
            // from an in-session tag operation — the service died mid-session.
            // Apply the SCM race-avoidance re-query protocol and surface ConnectionFailed,
            // NOT TagNotFound.
            throw await BuildDeadSessionExceptionForInSessionAsync(ex, xpath, ct)
                .ConfigureAwait(false);
        }
        catch (OperationNotPerformedException ex)
        {
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TagNotFound,
                $"SDK could not perform GetTagValue for '{xpath}': {ex.Message}",
                xpath), ex);
        }
        catch (OperationFailedException ex)
            when (SdkCommErrorClassifier.IsDeadSessionIndicator(ex)
                  || SdkCommErrorClassifier.IsCommLayerError(ex))
        {
            // OperationFailedException carrying a dead-session indicator
            // ('Unable to complete connection to LdSdkService',
            //  'Could not reach LdSdkService') or an RxE_COMM_* comm-layer
            // code — apply the same SCM race-avoidance re-query discriminator
            // as the connect path. Must not fall
            // through to the Unknown handler below.
            throw await BuildDeadSessionExceptionForInSessionAsync(ex, xpath, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (SdkCommErrorClassifier.IsCommLayerError(ex))
        {
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.SdkCommLayerFaulted,
                $"SDK comm-layer fault during GetTagValue for '{xpath}': {ex.Message}",
                xpath), ex);
        }
        catch (OperationFailedException ex)
        {
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.Unknown,
                $"SDK GetTagValue failed for '{xpath}': {ex.Message}",
                xpath), ex);
        }
    }

    /// <summary>
    /// Routes a tag write to the appropriate typed SDK setter based on
    /// <paramref name="declaredType"/> and the configured
    /// <see cref="OperationMode"/>.
    /// </summary>
    /// <param name="project">Open SDK project handle.</param>
    /// <param name="xpath">SDK XPath for the tag.</param>
    /// <param name="declaredType">Logix type string resolved from the L5X project.</param>
    /// <param name="value">
    /// The value to write.  Must have already passed
    /// <see cref="ValidateWriteTypeCompatibility"/> so that all casts here
    /// are safe.
    /// </param>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.Unknown"/> when the type is
    /// not one of the supported atomic types.
    /// </exception>
    private async Task WriteTypedAsync(
        ILogixProjectHandle project, string xpath, string declaredType, object value,
        CancellationToken ct)
    {
        try
        {
            switch (declaredType.ToUpperInvariant())
            {
                case "BOOL":   await project.SetTagValueBOOLAsync(xpath,  _operationMode, (bool)value);   break;
                case "SINT":   await project.SetTagValueSINTAsync(xpath,  _operationMode, (sbyte)value);  break;
                case "INT":    await project.SetTagValueINTAsync(xpath,   _operationMode, (short)value);  break;
                case "DINT":   await project.SetTagValueDINTAsync(xpath,  _operationMode, (int)value);    break;
                case "LINT":   await project.SetTagValueLINTAsync(xpath,  _operationMode, (long)value);   break;
                case "USINT":  await project.SetTagValueUSINTAsync(xpath, _operationMode, (byte)value);   break;
                case "UINT":   await project.SetTagValueUINTAsync(xpath,  _operationMode, (ushort)value); break;
                case "UDINT":  await project.SetTagValueUDINTAsync(xpath, _operationMode, (uint)value);   break;
                case "ULINT":  await project.SetTagValueULINTAsync(xpath, _operationMode, (ulong)value);  break;
                case "REAL":   await project.SetTagValueREALAsync(xpath,  _operationMode, (float)value);  break;
                case "LREAL":  await project.SetTagValueLREALAsync(xpath, _operationMode, (double)value); break;
                case "STRING": await project.SetTagValueSTRINGAsync(xpath, _operationMode, (string)value); break;
                default:
                    throw new TagServiceException(new TagServiceError(
                        TagServiceErrorKind.Unknown,
                        $"Tag type '{declaredType}' is not supported for typed write access. " +
                        $"Only atomic types (BOOL, SINT, INT, DINT, LINT, REAL, LREAL, " +
                        $"USINT, UINT, UDINT, ULINT, STRING) are supported.",
                        xpath));
            }
        }
        catch (TagServiceException)
        {
            throw;
        }
        catch (OperationNotPerformedException ex)
            when (SdkCommErrorClassifier.IsDeadSessionIndicator(ex))
        {
            // Dead-session indicator from an in-session write — service died mid-session.
            // Apply the SCM race-avoidance re-query and surface ConnectionFailed,
            // NOT TagNotFound.
            throw await BuildDeadSessionExceptionForInSessionAsync(ex, xpath, ct)
                .ConfigureAwait(false);
        }
        catch (OperationNotPerformedException ex)
        {
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TagNotFound,
                $"SDK could not perform SetTagValue for '{xpath}': {ex.Message}",
                xpath), ex);
        }
        catch (OperationFailedException ex)
            when (SdkCommErrorClassifier.IsDeadSessionIndicator(ex)
                  || SdkCommErrorClassifier.IsCommLayerError(ex))
        {
            // OperationFailedException carrying a dead-session indicator or an
            // RxE_COMM_* comm-layer code — apply the same SCM race-avoidance
            // re-query discriminator as the connect path.
            // Must not fall through to the Unknown handler below.
            throw await BuildDeadSessionExceptionForInSessionAsync(ex, xpath, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (SdkCommErrorClassifier.IsCommLayerError(ex))
        {
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.SdkCommLayerFaulted,
                $"SDK comm-layer fault during SetTagValue for '{xpath}': {ex.Message}",
                xpath), ex);
        }
        catch (OperationFailedException ex)
        {
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.Unknown,
                $"SDK SetTagValue failed for '{xpath}': {ex.Message}",
                xpath), ex);
        }
    }

    /// <summary>
    /// Builds a <see cref="TagServiceException"/> for a dead-session condition
    /// detected during an in-session tag operation, applying the SCM
    /// race-avoidance re-query protocol before choosing between the definitive
    /// "service is not running" error and the "service communications layer
    /// faulted" error.
    ///
    /// <para>
    /// <b>SCM race-avoidance re-query protocol.</b>  The Windows Service Control
    /// Manager has been measured to lag behind <c>LdSdkServer.exe</c> process
    /// death by up to 5 seconds.  To avoid falsely asserting "service is running"
    /// when only the SCM entry is stale, the combined service check
    /// (<see cref="_ldSdkServiceRunning"/> AND
    /// <see cref="_ldSdkServerProcessRunning"/>) is performed up to three times:
    /// <list type="number">
    ///   <item>Query 1 — immediately on entry.</item>
    ///   <item>Query 2 — after a 2-second wait (via <see cref="_delay"/>).</item>
    ///   <item>Query 3 — after a further 5-second wait.</item>
    /// </list>
    /// If any query finds the service not running (SCM status ≠ RUNNING, or the
    /// <c>LdSdkServer.exe</c> process is absent), the method immediately emits
    /// the definitive "service is down" error.  Only when <b>all three</b>
    /// queries agree the service is effectively running does the method emit the
    /// service-faulted error.
    /// </para>
    ///
    /// <para>
    /// No TCP probe is issued on this path (unlike the connect-time path in
    /// <see cref="SdkConnectFactory"/>).  At in-session time the controller was
    /// reachable at connect; the issue is the service state, not the controller.
    /// </para>
    ///
    /// <para>
    /// <b>This method never starts, stops, or otherwise controls the service.</b>
    /// It only reads state via <see cref="_ldSdkServiceRunning"/> and
    /// <see cref="_ldSdkServerProcessRunning"/>.
    /// </para>
    /// </summary>
    /// <param name="ex">
    /// The original <see cref="OperationNotPerformedException"/> whose message
    /// triggered dead-session detection.  Preserved as the
    /// <see cref="Exception.InnerException"/> of the returned exception.
    /// </param>
    /// <param name="context">
    /// Short descriptor of the in-session operation (typically the SDK XPath),
    /// included in the error message for operator diagnostics.
    /// </param>
    /// <param name="ct">Cancellation token forwarded to the delay calls.</param>
    private async System.Threading.Tasks.Task<TagServiceException>
        BuildDeadSessionExceptionForInSessionAsync(
            Exception ex, string context, CancellationToken ct)
    {
        // Combined check: SCM says RUNNING AND LdSdkServer.exe process is present.
        bool IsEffectivelyRunning() => _ldSdkServiceRunning() && _ldSdkServerProcessRunning();

        // ── Query 1: immediate ────────────────────────────────────────────────
        if (!IsEffectivelyRunning())
        {
            AuditLogger.LogSdkServiceDiagnosis(ex.Message, "not-probed", "ConnectionFailed");
            return ServiceDownExceptionForInSession(ex, context);
        }

        // ── Query 2: after 2-second wait ──────────────────────────────────────
        await _delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        if (!IsEffectivelyRunning())
        {
            AuditLogger.LogSdkServiceDiagnosis(ex.Message, "not-probed", "ConnectionFailed");
            return ServiceDownExceptionForInSession(ex, context);
        }

        // ── Query 3: after further 5-second wait ──────────────────────────────
        await _delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (!IsEffectivelyRunning())
        {
            AuditLogger.LogSdkServiceDiagnosis(ex.Message, "not-probed", "ConnectionFailed");
            return ServiceDownExceptionForInSession(ex, context);
        }

        // ── All three queries confirm the service is effectively running ───────
        // The service is up but the session died — the communications layer has
        // likely faulted.  Report the observed fact and advise a restart.
        AuditLogger.LogSdkServiceDiagnosis(ex.Message, "not-probed", "ConnectionFailed");
        return new TagServiceException(new TagServiceError(
            TagServiceErrorKind.ConnectionFailed,
            $"SDK operation for '{context}' failed: \"{ex.Message}\" " +
            $"The Logix SDK service (LdSdkService) is running but its " +
            $"communications layer may have faulted. Check the Windows " +
            $"Application event log for LdSdkServer.exe errors and restart " +
            $"the LdSdkService service, then retry."),
            ex);
    }

    /// <summary>
    /// Builds the definitive "service is not running" exception used by
    /// <see cref="BuildDeadSessionExceptionForInSessionAsync"/> when any of
    /// the three combined service/process queries confirms the service is down.
    /// </summary>
    private static TagServiceException ServiceDownExceptionForInSession(
        Exception ex, string context)
        => new TagServiceException(new TagServiceError(
            TagServiceErrorKind.ConnectionFailed,
            $"SDK operation for '{context}' failed: \"{ex.Message}\" " +
            $"The Logix SDK service (LdSdkService) is not running; it has crashed " +
            $"or been stopped. Recovery will restart it — wait a few seconds and " +
            $"retry."),
            ex);
}
#endif
