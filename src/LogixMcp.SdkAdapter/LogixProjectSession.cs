#if LOGIX_SDK
// ── LogixProjectSession — session lifetime manager for ILogixProject ─────────
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;
using ILogixProjectHandle = RockwellAutomation.LogixDesigner.ILogixProject;

/// <summary>
/// Manages the lifetime of an <see cref="ILogixProject"/> gRPC-backed handle.
///
/// <para>
/// <see cref="ILogixProject"/> is a disposable gRPC channel wrapper that must
/// be opened explicitly before use and disposed deterministically when done.
/// This class enforces that contract and translates lifecycle violations into
/// structured <see cref="TagServiceException"/> errors so callers never
/// encounter a bare <see cref="ObjectDisposedException"/> or
/// <see cref="NullReferenceException"/> when the session is in the wrong state.
/// </para>
///
/// <para>
/// <b>Private-copy semantics</b>: before the SDK handle is opened, the project
/// file is copied to a server-managed working directory (see
/// <see cref="WorkingDirectoryBase"/>).  The SDK always opens the copy; the
/// original file is never locked by the server.  This is required for
/// coexistence with Studio 5000, which holds an exclusive file lock on any ACD
/// it has open.  Copying succeeds even when Studio has the original file open
/// because the OS copy requires only a shared read lock.  The working copy is
/// deleted automatically when <see cref="Dispose"/> is called.
/// </para>
///
/// <para>State machine:</para>
/// <list type="table">
///   <listheader><term>State</term><description>Meaning</description></listheader>
///   <item><term><c>NotOpened</c></term>
///         <description>Initial state — <see cref="OpenAsync"/> has not been called.</description></item>
///   <item><term><c>Open</c></term>
///         <description>After a successful <see cref="OpenAsync"/> call.</description></item>
///   <item><term><c>Closed</c></term>
///         <description>After <see cref="Dispose"/> / <see cref="CloseAsync"/> — terminal state.</description></item>
/// </list>
///
/// <para>
/// Attempting any tag or mode operation (via <see cref="GetProject"/> or
/// <see cref="ExecuteAsync{T}"/>) when not in the <c>Open</c> state raises
/// <see cref="TagServiceException"/> with
/// <see cref="TagServiceErrorKind.ConnectionFailed"/>.
/// An <see cref="ObjectDisposedException"/> propagated from the underlying
/// SDK call is likewise caught and converted to the same structured error.
/// </para>
/// </summary>
public sealed class LogixProjectSession : IDisposable
{
    // ── Private-copy working directory ─────────────────────────────────────────

    /// <summary>
    /// Base directory under which all server-managed working copies are placed.
    /// Each copy lives in a per-source-path subdirectory derived from a
    /// deterministic hash of the canonicalised source path.
    /// </summary>
    internal static string WorkingDirectoryBase { get; } =
        Path.Combine(Path.GetTempPath(), "logix-mcp-work");

    /// <summary>
    /// Computes the deterministic path of the working copy for
    /// <paramref name="sourcePath"/>.
    ///
    /// <para>
    /// The subdirectory name is the first 12 hex characters of the SHA-256 hash
    /// of the canonicalised source path, giving a unique slot per source file
    /// that is stable across restarts.  The working copy retains the original
    /// filename so the SDK receives a recognisable extension.
    /// </para>
    /// </summary>
    /// <param name="sourcePath">
    /// Absolute or relative path to the source project file.
    /// </param>
    /// <returns>
    /// The absolute path at which the working copy will be placed.
    /// </returns>
    internal static string ComputeWorkingCopyPath(string sourcePath)
    {
        var absPath = Path.GetFullPath(sourcePath);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(absPath));
        var slot = Convert.ToHexString(hashBytes)[..12].ToLowerInvariant();
        return Path.Combine(WorkingDirectoryBase, slot, Path.GetFileName(absPath));
    }

    // ── Default copier (production) ────────────────────────────────────────────

    /// <summary>
    /// Copies <paramref name="sourcePath"/> to the deterministic working
    /// directory and returns the path of the copy.
    ///
    /// <para>
    /// Uses <see cref="File.Copy(string, string, bool)"/> with
    /// <c>overwrite: true</c> so repeated opens of the same source file
    /// refresh the copy without accumulating stale files.
    /// </para>
    /// </summary>
    private static string DefaultWorkingCopier(string sourcePath)
    {
        var destPath = ComputeWorkingCopyPath(sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.Copy(sourcePath, destPath, overwrite: true);
        return destPath;
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private enum SessionState { NotOpened, Open, Closed }

    private readonly Func<string, CancellationToken, Task<ILogixProjectHandle>> _factory;
    private readonly Func<string, CancellationToken, Task<ILogixProjectHandle>> _separateOpener;
    private readonly Func<string, string> _workingCopier;
    private ILogixProjectHandle? _project;
    private string? _workingCopyPath;
    private string? _sourcePath;
    private volatile SessionState _state = SessionState.NotOpened;
    private readonly object _stateLock = new();

    // ── Construction ──────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises a new <see cref="LogixProjectSession"/> in the
    /// <c>NotOpened</c> state.  The session uses
    /// <see cref="LogixProject.OpenLogixProjectAsync"/> to open the project
    /// and applies the default private-copy semantics (copies the file to
    /// <see cref="WorkingDirectoryBase"/> before passing it to the SDK).
    /// </summary>
    public LogixProjectSession()
        : this(OpenWithSdkAsync, DefaultWorkingCopier)
    { }

    /// <summary>Opens <paramref name="path"/> through the Logix Designer SDK in a new handle.</summary>
    internal static async Task<ILogixProjectHandle> OpenWithSdkAsync(string path, CancellationToken ct) =>
        await LogixProject.OpenLogixProjectAsync(path, (IOperationEvent?)null, ct).ConfigureAwait(false);

    /// <summary>
    /// Initialises a new <see cref="LogixProjectSession"/> with an injectable
    /// <paramref name="projectFactory"/>.  Intended for unit testing — the
    /// factory can return a fake <see cref="ILogixProject"/> without requiring
    /// a live Logix Designer installation.
    ///
    /// <para>
    /// This overload bypasses private-copy semantics (the path is forwarded
    /// to <paramref name="projectFactory"/> unchanged) so that tests can use
    /// non-existent placeholder paths without triggering a file-not-found
    /// error on the copy step.  Production callers should use the
    /// parameterless constructor instead.
    /// </para>
    /// </summary>
    /// <param name="projectFactory">
    /// Delegate that, given a project file path and cancellation token, returns
    /// an open <see cref="ILogixProject"/> handle.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="projectFactory"/> is <c>null</c>.
    /// </exception>
    public LogixProjectSession(
        Func<string, CancellationToken, Task<ILogixProjectHandle>> projectFactory)
        : this(projectFactory, static path => path)
    { }

    /// <summary>
    /// Full-control constructor for advanced testing scenarios — allows
    /// injection of both the project factory and the file-copy strategy.
    /// </summary>
    /// <param name="projectFactory">
    /// Delegate that opens an <see cref="ILogixProject"/> given a path.
    /// </param>
    /// <param name="workingCopier">
    /// Delegate that receives the caller-supplied path and returns the path
    /// that should actually be opened by the SDK (e.g. a copy of the file).
    /// The default production copier calls <see cref="DefaultWorkingCopier"/>.
    /// Pass <c>static path => path</c> to disable copying in tests.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when either argument is <c>null</c>.
    /// </exception>
    internal LogixProjectSession(
        Func<string, CancellationToken, Task<ILogixProjectHandle>> projectFactory,
        Func<string, string> workingCopier)
        : this(projectFactory, workingCopier, projectFactory)
    { }

    /// <summary>
    /// Constructor for a session whose <paramref name="projectFactory"/> does not open by path
    /// (an uploaded session's factory returns the already-open handle). <paramref name="separateOpener"/>
    /// opens other files, such as the offline build check's saved copy, in their own handle.
    /// </summary>
    internal LogixProjectSession(
        Func<string, CancellationToken, Task<ILogixProjectHandle>> projectFactory,
        Func<string, string> workingCopier,
        Func<string, CancellationToken, Task<ILogixProjectHandle>> separateOpener)
    {
        ArgumentNullException.ThrowIfNull(projectFactory);
        ArgumentNullException.ThrowIfNull(workingCopier);
        ArgumentNullException.ThrowIfNull(separateOpener);
        _factory        = projectFactory;
        _workingCopier  = workingCopier;
        _separateOpener = separateOpener;
    }

    // ── Public surface ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when the session is in the <c>Open</c> state —
    /// i.e., after <see cref="OpenAsync"/> has completed successfully and
    /// before <see cref="Dispose"/> has been called.
    /// </summary>
    public bool IsOpen => _state == SessionState.Open;

    /// <summary>
    /// Returns <c>true</c> when the session is in the <c>Closed</c> state —
    /// i.e., after <see cref="Dispose"/> or <see cref="CloseAsync"/> has been
    /// called.  A closed session cannot be reopened.
    /// </summary>
    public bool IsClosed => _state == SessionState.Closed;

    /// <summary>
    /// Returns <c>true</c> when the session has been confirmed online —
    /// i.e., after <c>GoOnlineAsync</c> succeeded and
    /// <c>ReadConnectedStateAsync</c> returned <c>ConnectedState.Online</c>.
    ///
    /// <para>
    /// Set internally by the correlation infrastructure (e.g.
    /// <c>SdkConnectFactory</c>) after the session is verified to be in the
    /// ONLINE state.  Reset to <c>false</c> when <see cref="Dispose"/> is
    /// called.
    /// </para>
    /// </summary>
    public bool IsOnline { get; internal set; }

    /// <summary>
    /// Returns <c>true</c> when the session's project images have been
    /// confirmed correlated with the controller — i.e., the controller holds
    /// the same project image as the session's working copy.
    ///
    /// <para>
    /// Set internally by the correlation infrastructure (e.g.
    /// <c>SdkConnectFactory</c>) after the session reaches the ONLINE state
    /// via the correlation ladder (direct GoOnline, upload-then-GoOnline, or
    /// download-then-GoOnline).  Reset to <c>false</c> when
    /// <see cref="Dispose"/> is called.
    /// </para>
    /// </summary>
    public bool IsCorrelated { get; internal set; }

    private volatile string? _faultDetail;

    /// <summary>
    /// <c>true</c> once the SDK server (Logix Services) reported a fatal error
    /// (<c>LgxSrv_E_FATAL_ERROR</c>) or <c>LgxSrv_E_SERVER_FAULTED</c>.
    /// A faulted session is dead: <see cref="GetProject"/> and both
    /// <see cref="ExecuteAsync{T}"/> overloads fail at once with
    /// <see cref="TagServiceErrorKind.SdkServerFaulted"/> and never reach the SDK.
    /// It can only be disposed (<c>logix_close_project</c>, or a new <c>logix_connect</c>).
    /// </summary>
    public bool IsFaulted => _faultDetail is not null;

    /// <summary>The SDK text that faulted the session, or <c>null</c> when it is not faulted.</summary>
    public string? FaultDetail => _faultDetail;

    /// <summary>
    /// Marks the session dead after a fatal SDK server error. Idempotent: the first detail is kept.
    /// </summary>
    /// <param name="detail">The SDK error text that caused the fault.</param>
    internal void MarkFaulted(string detail)
    {
        // Record the operation first so FaultedError never sees a detail without its operation.
        Interlocked.CompareExchange(ref _faultOperation, _currentOperation, null);
        Interlocked.CompareExchange(ref _faultDetail, string.IsNullOrWhiteSpace(detail) ? "LgxSrv_E_SERVER_FAULTED" : detail, null);
    }

    private volatile string? _currentOperation;
    private volatile string? _faultOperation;

    /// <summary>
    /// The operation (one of the <c>RockwellErrorHints.Operation*</c> names) that was running when the session faulted,
    /// or <c>null</c> when the session is not faulted or the operation was not recorded.
    /// </summary>
    public string? FaultOperation => _faultOperation;

    /// <summary>
    /// Names the operation a handler is running so a server fault can say which operation faulted. The outermost scope wins:
    /// a handler that calls another (clear calling export) keeps its own name. Dispose the result when the operation ends.
    /// </summary>
    /// <param name="operation">One of the <c>RockwellErrorHints.Operation*</c> names.</param>
    /// <returns>A scope to dispose when the operation ends.</returns>
    internal IDisposable BeginOperation(string operation)
    {
        if (Interlocked.CompareExchange(ref _currentOperation, operation, null) is not null)
            return NoOpScope.Instance;
        return new OperationScope(this);
    }

    private sealed class OperationScope : IDisposable
    {
        private LogixProjectSession? _owner;
        public OperationScope(LogixProjectSession owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ClearOperation();
    }

    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();
        public void Dispose() { }
    }

    private void ClearOperation() => _currentOperation = null;

    /// <summary>
    /// The structured error every call on a faulted session returns: kind
    /// <see cref="TagServiceErrorKind.SdkServerFaulted"/> with the reconnect hint, which names the
    /// operation that faulted and says what that operation could have done to the controller.
    /// </summary>
    internal TagServiceError FaultedError() => new(
        TagServiceErrorKind.SdkServerFaulted,
        "The Logix Designer SDK server (Logix Services) faulted earlier in this session " +
        (_faultOperation is null ? string.Empty : $"during {_faultOperation} ") +
        $"({_faultDetail ?? "LgxSrv_E_SERVER_FAULTED"}), so the SDK was not called.",
        SdkCode: "LgxSrv_E_SERVER_FAULTED",
        Hint: RockwellErrorHints.ServerFaultedHintFor(_faultOperation));

    /// <summary>
    /// Records <paramref name="ex"/> as a fatal fault when it names <c>LgxSrv_E_FATAL_ERROR</c> or
    /// <c>LgxSrv_E_SERVER_FAULTED</c> and returns the typed exception to throw in its place;
    /// returns <c>null</c> when <paramref name="ex"/> is not such a fault.
    /// </summary>
    /// <param name="ex">The exception thrown by an SDK call.</param>
    internal TagServiceException? TryMapServerFault(Exception ex)
    {
        if (ex is OperationCanceledException || !RockwellErrorHints.IsServerFaulted(ex)) return null;
        var text = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException) text.Append(e.Message).Append(' ');
        MarkFaulted(text.ToString().Trim());
        var hint = RockwellErrorHints.Get(RockwellErrorHintIds.ServerFaulted)!;
        return new TagServiceException(new TagServiceError(
            TagServiceErrorKind.SdkServerFaulted,
            $"{hint.Cause} SDK detail: {ex.Message}",
            SdkCode: text.ToString().Contains("LgxSrv_E_FATAL_ERROR", StringComparison.Ordinal)
                ? "LgxSrv_E_FATAL_ERROR" : "LgxSrv_E_SERVER_FAULTED",
            Hint: RockwellErrorHints.ServerFaultedHintFor(_faultOperation)), ex);
    }

    /// <summary>
    /// Name of the controller this session targets (the L5X <c>Controller/@Name</c>), used to
    /// pick the matching Logix Designer window (<c>&lt;controller&gt; in &lt;file&gt;.ACD</c>, or just
    /// <c>&lt;controller&gt;</c> when the project was uploaded and never saved) for the Studio
    /// lifecycle tools. <c>null</c> when not known.
    /// </summary>
    public string? StudioControllerName { get; internal set; }

    /// <summary>
    /// File name (no directory) of the project the engineer has open in Studio for this
    /// session, e.g. <c>Echo.ACD</c>. Set only when the session was opened from a project
    /// file the caller named; <c>null</c> for an upload session, whose ACD is server-managed
    /// and has no counterpart in Studio.
    /// </summary>
    public string? StudioProjectFileName { get; internal set; }

    /// <summary>
    /// Logix Designer version for this session, or <c>null</c> when not known. Set at connect
    /// from the project's <c>SoftwareRevision</c> (<see cref="DesignerVersionSource.ProjectRevision"/>)
    /// and replaced by the authoritative value parsed from the SDK's first unsupported-version
    /// error (<see cref="DesignerVersionSource.SdkError"/>). The SDK has no version call.
    /// </summary>
    public DesignerVersion? DesignerVersion { get; internal set; }

    /// <summary>
    /// Records a version learned from an SDK error, replacing a project-revision guess.
    /// </summary>
    /// <param name="learned">The version parsed from the error.</param>
    internal void RecordDesignerVersionFromSdk(DesignerVersion learned)
    {
        if (DesignerVersion is null || DesignerVersion.Source != DesignerVersionSource.SdkError)
            DesignerVersion = learned;
        // The unsupported-version error comes from the build check: record it as observed.
        Capabilities.ObserveUnsupportedVersion(SdkCapabilityNames.BuildCheck, learned);
    }

    private SdkCapabilities? _capabilities;

    /// <summary>
    /// This session's SDK capability table (<see cref="SdkCapabilities"/>), built on first use and kept for the
    /// session's life. It reads <see cref="DesignerVersion"/> live and holds what the SDK reported at runtime.
    /// </summary>
    public SdkCapabilities Capabilities =>
        _capabilities ??= SdkCapabilities.ForSession(
            () => DesignerVersion, DoctorEnvironment.SdkObservations?.AssemblyVersion);

    /// <summary>
    /// Staging buffer that holds tag declarations stripped from workstation-staged
    /// fragments, keyed by tag name (case-insensitive).
    ///
    /// <para>
    /// When a fragment is staged to the workstation target,
    /// <see cref="ProposeRungEditHandler"/> removes every <c>&lt;Tags&gt;</c>
    /// element from the fragment before passing it to the SDK, and stores each
    /// extracted <c>&lt;Tag&gt;</c> declaration here.  This ensures the SDK only
    /// ever sees a rungs-only fragment, restoring the staging-is-invisible
    /// invariant by construction rather than by promise.
    /// </para>
    ///
    /// <para>
    /// Entries are keyed by the <c>Name</c> attribute of each
    /// <c>&lt;Tag&gt;</c> element, using case-insensitive comparison.  A later
    /// staged fragment that declares a tag with the same name overwrites the
    /// earlier entry.  The buffer is cleared when the session is disposed.
    /// </para>
    /// </summary>
    public Dictionary<string, XElement> StagedTagBuffer { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>true</c> when a rung edit has been staged to the workstation target
    /// (<see cref="ProposeRungEditHandler"/>, <c>target="workstation"</c>) and
    /// not yet published to the controller.
    ///
    /// <para>
    /// Workstation-staged rungs accumulate in the session's in-memory project
    /// image (via <c>PartialImportOption.LeaveEdits</c>) across repeated calls;
    /// this flag tracks whether any such unassembled ("e"-typed) edit exists.
    /// Set to <c>true</c> after a successful workstation-target stage. Reset to
    /// <c>false</c> after a successful controller-target publish (the whole
    /// accumulated batch — rungs and buffered tags — is sent at once, so
    /// nothing remains only locally staged), and in <see cref="Dispose"/>.
    /// </para>
    ///
    /// <para>
    /// Used by <c>CloseProjectHandler</c> to refuse
    /// <c>logix_close_project</c> while pending work would be silently
    /// discarded.
    /// </para>
    /// </summary>
    public bool HasStagedRungEdit { get; internal set; }

    /// <summary>
    /// Cumulative number of <c>&lt;Rung&gt;</c> elements staged to the workstation
    /// target (<see cref="ProposeRungEditHandler"/>, <c>target="workstation"</c>)
    /// and not yet published to the controller.
    ///
    /// <para>
    /// Incremented by the rung count of each successful workstation-target
    /// fragment stage. Mirrors <see cref="HasStagedRungEdit"/>'s lifecycle: reset
    /// to <c>0</c> after a successful controller-target publish (the whole
    /// accumulated batch is sent at once, so nothing remains only locally
    /// staged), and in <see cref="Dispose"/>. This is an observability counter
    /// for <c>logix_pending_edits</c>, not an authoritative controller-side
    /// count — it reflects what this session has staged, not what the export
    /// shows.
    /// </para>
    /// </summary>
    public int StagedRungCount { get; internal set; }

    /// <summary>
    /// Staging buffer that holds the normalised rung <c>&lt;Text&gt;</c> content
    /// of every rung staged to the workstation target (<c>target="workstation"</c>),
    /// keyed by the <c>insertPosition</c> the rung was staged at.
    ///
    /// <para>
    /// <see cref="ProposeRungEditHandler"/> uses this buffer to deduplicate a
    /// subsequent controller-target (<c>target="controller"</c>) publish that
    /// supplies its own non-empty <c>l5xFragment</c>:
    /// any fragment rung whose normalised text is byte-equal to an entry already
    /// recorded here at the same insert position was already staged into the
    /// pending-edit zone by a prior <c>LeaveEdits</c> import, so re-sending it
    /// would import it a second time and duplicate the change set. Entries are
    /// appended (never overwritten) so multiple workstation stages at the same
    /// position accumulate correctly.
    /// </para>
    ///
    /// <para>
    /// Keyed by (routine XPath, insert position): two routines staged at the same
    /// insert position (for example position 0) keep separate rung lists.
    /// </para>
    ///
    /// <para>
    /// Cleared in full after a successful controller-target publish (the whole
    /// accumulated batch has now been sent, so nothing remains only-locally
    /// staged) and in <see cref="Dispose"/>. Mirrors the lifecycle of
    /// <see cref="StagedTagBuffer"/>, <see cref="HasStagedRungEdit"/>, and
    /// <see cref="StagedRungCount"/>.
    /// </para>
    /// </summary>
    public Dictionary<(string RoutineXPath, uint InsertPosition), List<string>> StagedRungBuffer { get; } = new();

    /// <summary>
    /// The <c>replaceCount</c> each <see cref="StagedRungBuffer"/> bucket was staged with,
    /// under the same key. A bucket at position <c>p</c> with <c>n</c> rungs and replace count <c>rc</c> occupies
    /// the controller-rung range <c>[p, p+n+rc)</c>: the staged rungs plus the originals they replace. A bucket
    /// with no entry has replace count 0. Cleared whenever the buffer is cleared.
    /// </summary>
    public Dictionary<(string RoutineXPath, uint InsertPosition), uint> StagedReplaceCounts { get; } = new();

    /// <summary>
    /// Per <see cref="StagedRungBuffer"/> bucket (same key), the indices <c>j &lt; replaceCount</c> of staged replacements
    /// whose text equals the original rung they displace. Logix keeps such a rung normal, so the session image
    /// must keep it normal too: it is neither stamped <c>IR</c> nor does it claim a displaced original. Cleared wherever
    /// <see cref="StagedReplaceCounts"/> is cleared or a key is removed.
    /// </summary>
    public Dictionary<(string RoutineXPath, uint InsertPosition), HashSet<int>> StagedKeptNormal { get; } = new();

    /// <summary>
    /// Staged replacements whose text equals the original rung they replace, keyed by routine
    /// XPath and zero-based rung index, valued with the normalised rung text. Logix keeps such a rung as normal
    /// (<c>N</c>), not pending (<c>I</c>), so the publish census must not expect it among the pending rungs.
    /// Cleared whenever the buffer is cleared.
    /// </summary>
    public Dictionary<(string RoutineXPath, int Index), string> StagedUnchangedReplacements { get; } = new();

    /// <summary>One workstation import that is held in the session image.</summary>
    /// <param name="RoutineXPath">Routine the fragment was imported into.</param>
    /// <param name="InsertPosition">Insert position the import used.</param>
    /// <param name="ReplaceCount">Replace count the import used.</param>
    /// <param name="Fragment">The exact fragment text that was imported (tag declarations already stripped).</param>
    internal sealed record StagedImport(string RoutineXPath, uint InsertPosition, uint ReplaceCount, string Fragment);

    private readonly List<StagedImport> _stagedImports = new();
    private readonly object _stagedImportsLock = new();

    /// <summary>
    /// The workstation imports that make up the staged rungs in the session image, in the order they were made.
    /// A clear rebuilds the image from the controller and replays the imports of the
    /// buckets it keeps, in this order, so the kept rungs land exactly where they were staged. Emptied by a
    /// controller publish and by <see cref="Dispose"/>.
    /// </summary>
    internal IReadOnlyList<StagedImport> StagedImports
    {
        get { lock (_stagedImportsLock) return _stagedImports.ToList(); }
    }

    /// <summary>Records a workstation import that was accepted into the session image.</summary>
    /// <param name="import">The import, as sent to the SDK.</param>
    internal void RecordStagedImport(StagedImport import)
    {
        ArgumentNullException.ThrowIfNull(import);
        lock (_stagedImportsLock) _stagedImports.Add(import);
    }

    /// <summary>Removes the recorded imports whose routine matches <paramref name="remove"/> (all of them when <c>null</c>).</summary>
    /// <param name="remove">Predicate on the routine XPath, or <c>null</c> to remove every import.</param>
    internal void ForgetStagedImports(Func<string, bool>? remove = null)
    {
        lock (_stagedImportsLock)
        {
            if (remove is null) _stagedImports.Clear();
            else _stagedImports.RemoveAll(i => remove(i.RoutineXPath));
        }
    }

    /// <summary>
    /// The original comment of a rung a workstation stage replaced with an explicit comment.
    /// An explicit comment is written to the original rung immediately, so a cancel (<c>logix_clear_staged</c>) must write
    /// <see cref="OriginalComment"/> back.
    /// </summary>
    /// <param name="RoutineXPath">Routine the rung is in.</param>
    /// <param name="RungIndex">Zero-based index of the original rung in the routine.</param>
    /// <param name="OriginalText">The original rung's raw text.</param>
    /// <param name="OriginalComment">The original rung's comment; an empty string when it had none.</param>
    /// <param name="Fragment">The fragment the stage imported; its envelope is reused to write the comment back.</param>
    internal sealed record CommentUndoEntry(
        string RoutineXPath, int RungIndex, string OriginalText, string OriginalComment, string Fragment);

    private readonly List<CommentUndoEntry> _commentUndos = new();
    private readonly object _commentUndoLock = new();

    /// <summary>The comment undo records of the staged replaces still held in the session, in staging order.</summary>
    internal IReadOnlyList<CommentUndoEntry> CommentUndos
    {
        get { lock (_commentUndoLock) return _commentUndos.ToList(); }
    }

    /// <summary>Records the original comment of a rung an explicit-comment stage replaced.</summary>
    /// <param name="entry">The undo record.</param>
    internal void RecordCommentUndo(CommentUndoEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_commentUndoLock) _commentUndos.Add(entry);
    }

    /// <summary>Removes the comment undo records whose routine matches <paramref name="remove"/> (all of them when <c>null</c>).</summary>
    /// <param name="remove">Predicate on the routine XPath, or <c>null</c> to remove every record.</param>
    internal void ForgetCommentUndos(Func<string, bool>? remove = null)
    {
        lock (_commentUndoLock)
        {
            if (remove is null) _commentUndos.Clear();
            else _commentUndos.RemoveAll(e => remove(e.RoutineXPath));
        }
    }

    private readonly List<CommentUndoEntry> _zoneCommentUndos = new();

    /// <summary>
    /// The comment undo records a publish moved onto the pending-edit zone it created. They survive the
    /// publish so that cancelling that zone (<c>logix_cancel_pending_edits</c>) can write the original rung comments back.
    /// </summary>
    internal IReadOnlyList<CommentUndoEntry> ZoneCommentUndos
    {
        get { lock (_commentUndoLock) return _zoneCommentUndos.ToList(); }
    }

    /// <summary>
    /// Moves every staged comment undo record onto the zone the publish just created, instead of dropping them.
    /// </summary>
    /// <returns>The number of records moved.</returns>
    internal int AttachCommentUndosToZone()
    {
        lock (_commentUndoLock)
        {
            var n = _commentUndos.Count;
            _zoneCommentUndos.AddRange(_commentUndos);
            _commentUndos.Clear();
            return n;
        }
    }

    /// <summary>Removes the zone comment undo records whose routine matches <paramref name="remove"/> (all of them when <c>null</c>).</summary>
    /// <param name="remove">Predicate on the routine XPath, or <c>null</c> to remove every record.</param>
    internal void ForgetZoneCommentUndos(Func<string, bool>? remove = null)
    {
        lock (_commentUndoLock)
        {
            if (remove is null) _zoneCommentUndos.Clear();
            else _zoneCommentUndos.RemoveAll(e => remove(e.RoutineXPath));
        }
    }

    /// <summary>One rung of a routine as the session image showed it before anything was staged into it.</summary>
    /// <param name="Type">The rung <c>Type</c> attribute (<c>N</c> when absent).</param>
    /// <param name="Text">The rung text, normalised with <see cref="RungTextComparer.Normalize"/>.</param>
    internal sealed record BaselineRung(string Type, string Text);

    private readonly object _baselineLock = new();
    private readonly Dictionary<string, IReadOnlyList<BaselineRung>> _routineBaselines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records the rungs of <paramref name="routineXPath"/> as exported just before the first workstation stage into it.
    /// A clear compares its re-export of the routine with this record. The first record wins;
    /// a later one is ignored.
    /// </summary>
    /// <param name="routineXPath">The routine XPath, as used for staging.</param>
    /// <param name="rungs">The routine's rungs, in order.</param>
    /// <returns><c>true</c> when the record was stored, <c>false</c> when the routine already had one.</returns>
    internal bool TryRecordRoutineBaseline(string routineXPath, IReadOnlyList<BaselineRung> rungs)
    {
        ArgumentNullException.ThrowIfNull(routineXPath);
        ArgumentNullException.ThrowIfNull(rungs);
        lock (_baselineLock) return _routineBaselines.TryAdd(routineXPath, rungs.ToList());
    }

    /// <summary>The pre-stage rungs recorded for <paramref name="routineXPath"/>, or <c>null</c> when none were recorded.</summary>
    /// <param name="routineXPath">The routine XPath.</param>
    internal IReadOnlyList<BaselineRung>? GetRoutineBaseline(string routineXPath)
    {
        lock (_baselineLock) return _routineBaselines.GetValueOrDefault(routineXPath);
    }

    /// <summary>Drops every pre-stage record. A controller publish changes the controller image, so the records no longer describe it.</summary>
    internal void ForgetRoutineBaselines()
    {
        lock (_baselineLock) _routineBaselines.Clear();
    }

    private readonly object _unverifiedClearLock = new();

    private readonly Dictionary<string, IReadOnlyList<string>> _unverifiedClears = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Routines whose last <c>logix_clear_staged</c> did not verify <c>imageRestored: true</c>:
    /// routine XPath → the rung texts a clear must find gone. Stage and publish in the same program refuse with
    /// <c>ClearIncomplete</c> while any entry remains, so nothing reports success built on a stale session image.
    /// Cleared by a later clear that verifies the routine, and on <see cref="Dispose"/>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> UnverifiedClears
    {
        get
        {
            lock (_unverifiedClearLock)
                return new Dictionary<string, IReadOnlyList<string>>(_unverifiedClears, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Records that a clear of <paramref name="routineXPath"/> was not verified.</summary>
    /// <param name="routineXPath">The routine whose session image may still hold staged rungs.</param>
    /// <param name="staleTexts">The rung texts that must be gone for the image to count as restored.</param>
    internal void RecordUnverifiedClear(string routineXPath, IReadOnlyList<string> staleTexts)
    {
        ArgumentNullException.ThrowIfNull(routineXPath);
        ArgumentNullException.ThrowIfNull(staleTexts);
        lock (_unverifiedClearLock)
            _unverifiedClears[routineXPath] = staleTexts.ToList();
    }

    /// <summary>Clears the unverified-clear record of one routine. Returns <c>true</c> when one existed.</summary>
    /// <param name="routineXPath">The routine XPath, as recorded by <see cref="RecordUnverifiedClear"/>.</param>
    internal bool ResolveUnverifiedClear(string routineXPath)
    {
        lock (_unverifiedClearLock)
            return _unverifiedClears.Remove(routineXPath);
    }

    /// <summary>
    /// Routine XPaths with an unverified clear that belong to <paramref name="program"/>. An XPath that cannot be
    /// resolved to a program counts for every program (the same rule as <see cref="UnresolvedPublishMismatchesForProgram"/>).
    /// </summary>
    /// <param name="program">Program name to match.</param>
    public IReadOnlyList<string> UnverifiedClearsForProgram(string program)
    {
        lock (_unverifiedClearLock)
            return _unverifiedClears.Keys.Where(k => MismatchBelongsToProgram(k, program)).ToList();
    }

    /// <summary>
    /// Replaces the session's project handle with a fresh open of the source file (offline-file
    /// sessions): the private working copy is copied over again from the file the caller named, which discards
    /// everything staged into the old handle. The original file on disk was never written by staging.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>null</c> on success, or the reason the working copy could not be reloaded. A failed re-open after the old handle was closed marks the session faulted.</returns>
    internal async Task<string?> ReloadWorkingCopyAsync(CancellationToken cancellationToken = default)
    {
        string source, oldWorking;
        ILogixProjectHandle? old;
        lock (_stateLock)
        {
            if (_state != SessionState.Open || _project is null)
                return "the session is not open";
            if (_sourcePath is null || _workingCopyPath is null
                || string.Equals(Path.GetFullPath(_sourcePath), Path.GetFullPath(_workingCopyPath), StringComparison.OrdinalIgnoreCase))
                return "this session has no separate working copy of a project file to reload (it was opened in place or uploaded from a controller)";
            source     = _sourcePath;
            oldWorking = _workingCopyPath;
            old        = _project;
            _project = null;
        }

        try { old.Dispose(); }
        catch (Exception) { /* best effort: the handle is replaced below either way */ }

        ILogixProjectHandle fresh;
        string copy;
        try
        {
            copy  = _workingCopier(source);
            fresh = await _factory(copy, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // The old handle is gone and could not be replaced: the session cannot be used safely.
            MarkFaulted($"reload of the working copy failed: {ex.Message}");
            return $"re-opening '{Path.GetFileName(source)}' failed: {ex.Message}";
        }

        lock (_stateLock)
        {
            if (_state == SessionState.Closed)
            {
                fresh.Dispose();
                return "the session was closed while the working copy was reloading";
            }
            _project         = fresh;
            _workingCopyPath = copy;
        }
        // The old private copy carried the staged rungs; it is no longer used.
        if (!string.Equals(oldWorking, copy, StringComparison.OrdinalIgnoreCase))
            try { File.Delete(oldWorking); } catch { /* best effort */ }
        return null;
    }

    /// <summary>
    /// XPaths of the routines that hold workstation-staged rungs (the routines behind
    /// <see cref="StagedRungBuffer"/>). Derived from the buffer keys, so it can never drift from the
    /// buffer: a routine is listed exactly while the buffer holds a rung list for it, and it empties when
    /// the buffer is cleared. The offline build check reads these routines back from its saved
    /// copy to confirm the copy carries the staged (<c>LeaveEdits</c>) edit.
    /// </summary>
    public IReadOnlyCollection<string> StagedRoutineXPaths =>
        StagedRungBuffer.Keys.Select(k => k.RoutineXPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private int _publishInFlight;

    /// <summary>
    /// <c>true</c> while a controller-target publish (<c>target="controller"</c> or <c>"live"</c>) is running in this session.
    /// <c>logix_clear_staged</c> refuses with <c>PublishInProgress</c> for as long as this is set.
    /// </summary>
    public bool PublishInFlight => Volatile.Read(ref _publishInFlight) > 0;

    /// <summary>Marks a controller publish as started. Every call must be paired with <see cref="EndPublish"/> in a <c>finally</c>.</summary>
    public void BeginPublish() => Interlocked.Increment(ref _publishInFlight);

    /// <summary>Marks a controller publish as finished (successfully or not), releasing the session for <c>logix_clear_staged</c>.</summary>
    public void EndPublish() => Interlocked.Decrement(ref _publishInFlight);

    private readonly object _publishMismatchLock = new();

    private readonly Dictionary<string, IReadOnlyList<string>> _publishMismatches =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Records that a controller publish ended in <c>PublishMismatch</c>:
    /// for each differing routine, the rung texts the publish was expected to leave pending.
    /// <c>logix_test_pending_edits</c> and <c>logix_assemble_pending_edits</c> refuse with kind
    /// <c>PublishMismatchUnresolved</c> while any entry for their program remains. A later mismatch for
    /// the same routine replaces the earlier entry.
    /// </summary>
    /// <param name="routineXPath">XPath of the routine whose post-publish census differed.</param>
    /// <param name="expectedRungs">The normalised rung texts expected pending in that routine.</param>
    public void RecordPublishMismatch(string routineXPath, IReadOnlyList<string> expectedRungs)
    {
        ArgumentNullException.ThrowIfNull(routineXPath);
        ArgumentNullException.ThrowIfNull(expectedRungs);
        lock (_publishMismatchLock)
            _publishMismatches[routineXPath] = expectedRungs.ToList();
    }

    /// <summary>
    /// Snapshot of the unresolved publish mismatches: routine XPath → the rung texts that were expected
    /// pending there. Empty when none is unresolved.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> UnresolvedPublishMismatches
    {
        get
        {
            lock (_publishMismatchLock)
                return new Dictionary<string, IReadOnlyList<string>>(_publishMismatches, StringComparer.Ordinal);
        }
    }

    /// <summary>Clears the unresolved mismatch for one routine. Returns <c>true</c> when one was cleared.</summary>
    /// <param name="routineXPath">XPath of the routine, as recorded by <see cref="RecordPublishMismatch"/>.</param>
    public bool ResolvePublishMismatch(string routineXPath)
    {
        lock (_publishMismatchLock)
            return _publishMismatches.Remove(routineXPath);
    }

    /// <summary>
    /// Returns the routine XPaths with an unresolved publish mismatch that belong to
    /// <paramref name="program"/>. An XPath that cannot be resolved to a program counts for every program,
    /// so an unparseable record can never let a test or assemble through.
    /// </summary>
    /// <param name="program">Program name to match.</param>
    public IReadOnlyList<string> UnresolvedPublishMismatchesForProgram(string program)
    {
        lock (_publishMismatchLock)
            return _publishMismatches.Keys
                .Where(k => MismatchBelongsToProgram(k, program))
                .ToList();
    }

    /// <summary>
    /// Clears every unresolved publish mismatch belonging to <paramref name="program"/>
    /// (same matching as <see cref="UnresolvedPublishMismatchesForProgram"/>). Used after a successful
    /// cancel, which discards the program's pending edits. Returns the number cleared.
    /// </summary>
    /// <param name="program">Program name whose mismatches are resolved.</param>
    public int ClearPublishMismatchesForProgram(string program)
    {
        lock (_publishMismatchLock)
        {
            var keys = _publishMismatches.Keys.Where(k => MismatchBelongsToProgram(k, program)).ToList();
            foreach (var k in keys)
                _publishMismatches.Remove(k);
            return keys.Count;
        }
    }

    private static bool MismatchBelongsToProgram(string routineXPath, string program) =>
        !PendingEditsHandler.TryParseRoutineXPath(routineXPath, out var parsedProgram, out _)
        || string.Equals(parsedProgram, program, StringComparison.Ordinal);

    /// <summary>
    /// Opens <paramref name="projectPath"/> in a SEPARATE SDK handle, without touching the session's own
    /// handle or state. The caller owns the returned handle and must dispose it.
    /// Used by the offline build check to open its saved copy.
    /// </summary>
    /// <param name="projectPath">The project file to open (no private working copy is made).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The open handle.</returns>
    internal Task<ILogixProjectHandle> OpenSeparateHandleAsync(
        string projectPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        // Not _factory: an uploaded session's factory returns its own online handle for any path, so
        // "building the copy" built online (bench 2026-10-06: a Logix Services fatal) and disposing it closed the session.
        return _separateOpener(projectPath, cancellationToken);
    }

    /// <summary>
    /// Upper bound on the offline replay that explains an <c>ImportAbortedNoChanges</c> rejection.
    /// 60 s by default; settable so tests need not wait that long.
    /// </summary>
    internal TimeSpan ImportRejectReplayTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Wait between the (at most 3) tries of an import that hit <c>RxCsE_ASA_BLOCKING_HIGHER_EXCL_LOCK</c>.
    /// 5 s by default, so the 3 tries span about 10 s; settable so tests need not wait.
    /// </summary>
    internal TimeSpan ControllerLockRetryDelay { get; set; } = ControllerLockRetry.DefaultRetryDelay;

    /// <summary>Test seam replacing the wait between lock retries. <c>null</c> in production.</summary>
    internal Func<TimeSpan, CancellationToken, System.Threading.Tasks.Task>? ControllerLockRetryWait { get; set; }

    /// <summary>
    /// Test seam for <c>verifyOnStage</c>: when set, replaces the offline replay (a save-as of the project, an
    /// offline import of the fragment, a build) that verifies a staged fragment. Arguments: routine XPath, insert
    /// position, replace count, fragment file. <c>null</c> in production.
    /// </summary>
    internal Func<string, uint, uint, string, CancellationToken, Task<ImportRejectReplayOutcome>>? StageVerifyReplay { get; set; }

    /// <summary>Test seam: the clock the <c>verifyOnStage</c> phase timer reads. <see cref="TimeProvider.System"/> in production.</summary>
    internal TimeProvider StageClock { get; set; } = TimeProvider.System;

    /// <summary>
    /// Set of normalised <c>&lt;Text&gt;</c> values for every rung that was
    /// <em>replaced or deleted</em> by this session's workstation-target staging
    /// calls.  Each entry is the <see cref="ProposeRungEditHandler.NormalizeRungText"/>
    /// output of the original rung text that was displaced by a
    /// <c>replaceCount &gt; 0</c> stage.
    ///
    /// <para>
    /// <see cref="PendingEditsHandler"/> uses this set to segregate in-session
    /// "replaced original" rungs (which appear in the controller export with an
    /// <c>r*</c> or <c>d*</c> type) into <c>staged.replacedRungs</c> rather than
    /// <c>pendingRungs</c>, so that the per-routine <c>state</c> field reflects
    /// only genuine controller-side pending state and not session-local staging
    /// artefacts.
    /// </para>
    ///
    /// <para>
    /// Cleared in full after a successful controller-target publish and in
    /// <see cref="Dispose"/>. Mirrors the lifecycle of <see cref="StagedRungBuffer"/>.
    /// </para>
    /// </summary>
    public HashSet<string> StagedReplacedRungTexts { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Pre-stage tag census: the set of controller tag names that existed
    /// <em>before</em> this session began staging workstation edits.
    ///
    /// <para>
    /// Populated by <see cref="ProposeRungEditHandler"/> from the project's tag
    /// census on the first workstation-target staging call that strips tag
    /// declarations.  Used by <see cref="PendingEditsHandler"/> to filter
    /// <c>staged.tagNames</c> so that it contains only tag names a publish would
    /// actually <em>create</em> — names absent from this census.  Context
    /// declarations for pre-existing tags (included in the L5X export envelope
    /// for context but not newly created) are excluded from the reported list.
    /// </para>
    ///
    /// <para>
    /// Cleared in <see cref="Dispose"/> only (the census remains stable across
    /// the lifetime of the session; a pre-existing tag does not become new just
    /// because a second staging call is made).
    /// </para>
    /// </summary>
    public HashSet<string> PreStageTagCensus { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>ExternalAccess</c> per tag in <see cref="PreStageTagCensus"/>, taken from the
    /// same census export and captured at the same moment. Used to refuse a caller
    /// fragment that would change an existing tag's External Access. Cleared with the census.
    /// </summary>
    public Dictionary<string, string> PreStageTagAccess { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the names of tag declarations currently in <see cref="StagedTagBuffer"/>
    /// that are absent from <see cref="PreStageTagCensus"/> — i.e., the tags that
    /// a controller-target publish would actually <em>create</em> on the controller.
    ///
    /// <para>
    /// Context declarations for tags that already existed before staging began
    /// (<see cref="PreStageTagCensus"/>) are excluded; they appear in the buffer for
    /// completeness but represent no new controller state.  An empty census is treated
    /// as "all tags are new" (the census was never populated, so no pre-existing tags
    /// are known).  The result is ordered case-insensitively.
    /// </para>
    ///
    /// <para>
    /// This is the single authoritative definition of the "new-tag filter" used by
    /// both <see cref="PendingEditsHandler"/> (for <c>staged.tagNames</c>) and the
    /// <c>ConfirmationRequired</c> refusal path in <c>LogixSdkTools</c> (for
    /// <c>stagedTagNames</c>) — both code paths call this method so the filter
    /// logic exists in exactly one place.
    /// </para>
    ///
    /// <para>Returns <c>null</c> when the filtered list is empty.</para>
    /// </summary>
    /// <returns>
    /// A case-insensitively-sorted read-only list of new tag names, or
    /// <c>null</c> when no new tags are staged.
    /// </returns>
    public IReadOnlyList<string>? GetNewStagedTagNames()
    {
        var names = StagedTagBuffer.Keys
            .Where(n => PreStageTagCensus.Count == 0 || !PreStageTagCensus.Contains(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count > 0 ? names : null;
    }

    /// <summary>
    /// Opens the Logix project at <paramref name="projectPath"/> and
    /// transitions the session to the <c>Open</c> state.
    ///
    /// <para>
    /// The underlying <see cref="LogixProject.OpenLogixProjectAsync"/> call
    /// establishes the gRPC channel to the Logix Designer service; it requires
    /// a licensed installation of Studio 5000 or FactoryTalk Logix Echo on the
    /// host.  In the sandbox environment, inject a factory via the internal
    /// constructor to avoid the live dependency.
    /// </para>
    /// </summary>
    /// <param name="projectPath">
    /// Absolute path to the .ACD or .L5X project file.
    /// Must not be <c>null</c> or whitespace.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the open operation.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="projectPath"/> is <c>null</c> or whitespace.
    /// </exception>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/> when:
    /// <list type="bullet">
    ///   <item>The session has been closed (<c>Closed</c> state).</item>
    ///   <item>The underlying SDK call fails.</item>
    /// </list>
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when called on a session that is already <c>Open</c>.
    /// Call <see cref="CloseAsync"/> first.
    /// </exception>
    public async Task OpenAsync(string projectPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        lock (_stateLock)
        {
            if (_state == SessionState.Closed)
                throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.ConnectionFailed,
                    "Session has been closed and cannot be reopened. " +
                    "Create a new LogixProjectSession to reconnect."));

            if (_state == SessionState.Open)
                throw new InvalidOperationException(
                    "Session is already open. " +
                    "Call CloseAsync (or Dispose) before reopening.");
        }

        // ── Private-copy step ──────────────────────────────────────────────────
        // Copy the source file to the server-managed working directory before
        // handing the path to the SDK.  This prevents the SDK from locking the
        // original file, allowing coexistence with Studio 5000.  The OS copy
        // requires only a shared read lock, so it succeeds even when Studio has
        // the original open exclusively.
        string workingPath;
        try
        {
            workingPath = _workingCopier(projectPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                $"Failed to create a working copy of '{projectPath}': {ex.Message}"), ex);
        }

        ILogixProjectHandle project;
        try
        {
            project = await _factory(workingPath, cancellationToken)
                          .ConfigureAwait(false);
        }
        catch (TagServiceException)
        {
            throw;  // Already structured — let it propagate as-is.
        }
        catch (OperationCanceledException)
        {
            throw;  // Propagate cancellation without wrapping.
        }
        catch (Exception ex)
        {
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                $"Failed to open Logix project at '{projectPath}': {ex.Message}"), ex);
        }

        lock (_stateLock)
        {
            // Guard against a concurrent Dispose() that raced with the await.
            if (_state == SessionState.Closed)
            {
                project.Dispose();

                // The Dispose path won't see _workingCopyPath because it was
                // set to null before we entered the state-closed branch.
                // Delete the working copy here so it is not orphaned.
                try { File.Delete(workingPath); } catch { /* best effort */ }

                throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.ConnectionFailed,
                    "Session was closed while the project was opening."));
            }

            _project         = project;
            _workingCopyPath = workingPath;
            _sourcePath      = projectPath;
            _state           = SessionState.Open;
        }
    }

    /// <summary>
    /// Closes the session and deterministically disposes the underlying
    /// <see cref="ILogixProject"/> gRPC handle.
    ///
    /// <para>Equivalent to calling <see cref="Dispose"/>.</para>
    /// </summary>
    /// <param name="cancellationToken">
    /// Accepted for API symmetry; disposal is synchronous and cannot be
    /// cancelled.
    /// </param>
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the underlying <see cref="ILogixProject"/> handle when the
    /// session is <c>Open</c>, or throws a structured
    /// <see cref="TagServiceException"/> otherwise.
    ///
    /// <para>
    /// Callers should call this method at the start of every tag or mode
    /// operation to ensure the session is valid before touching the gRPC
    /// channel.  If the session has been closed concurrently, the guard
    /// prevents the call from reaching a disposed object — eliminating
    /// <see cref="ObjectDisposedException"/> from reaching the caller.
    /// </para>
    /// </summary>
    /// <returns>The open <see cref="ILogixProject"/> handle.</returns>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/> when the
    /// session is in the <c>NotOpened</c> or <c>Closed</c> state.
    /// </exception>
    public ILogixProjectHandle GetProject()
    {
        // Read _state once to avoid TOCTOU between the switch and the return.
        var state = _state;

        // Dead-session short-circuit: never reach the SDK after a fatal.
        if (state == SessionState.Open && _faultDetail is not null)
            throw new TagServiceException(FaultedError());

        return state switch
        {
            SessionState.Open      => _project!,
            SessionState.NotOpened => throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                "Session is not open. " +
                "Call ConnectAsync / OpenAsync before performing tag or mode operations.")),
            _ /* Closed */ => throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                "Session has been closed. " +
                "Create a new LogixProjectSession to reconnect.")),
        };
    }

    /// <summary>
    /// Executes <paramref name="operation"/> against the open session and
    /// returns its result.
    ///
    /// <para>
    /// Guards the call with <see cref="GetProject"/> before invoking the
    /// operation, and converts any <see cref="ObjectDisposedException"/> thrown
    /// by the SDK into a structured
    /// <see cref="TagServiceException"/>(<see cref="TagServiceErrorKind.ConnectionFailed"/>)
    /// so callers never encounter an unhandled disposed-object error.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The result type of the operation.</typeparam>
    /// <param name="operation">
    /// Async delegate that receives the open <see cref="ILogixProject"/> and
    /// returns a <see cref="Task{T}"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Token forwarded to the caller; not used by the guard itself.
    /// </param>
    /// <returns>The result of <paramref name="operation"/>.</returns>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/> when:
    /// <list type="bullet">
    ///   <item>The session is not open.</item>
    ///   <item>The SDK throws <see cref="ObjectDisposedException"/>.</item>
    /// </list>
    /// </exception>
    public async Task<T> ExecuteAsync<T>(
        Func<ILogixProjectHandle, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var project = GetProject();

        try
        {
            return await operation(project).ConfigureAwait(false);
        }
        catch (ObjectDisposedException odex)
        {
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                "Session was closed unexpectedly while performing the operation. " +
                "The underlying gRPC handle has been disposed."), odex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && RockwellErrorHints.IsServerFaulted(ex))
        {
            throw TryMapServerFault(ex)!;
        }
    }

    /// <summary>
    /// Executes <paramref name="operation"/> against the open session.
    ///
    /// <para>
    /// Non-generic overload for operations that return <see cref="Task"/>
    /// (no result value).  Behaves identically to
    /// <see cref="ExecuteAsync{T}"/> in terms of state guard and
    /// <see cref="ObjectDisposedException"/> conversion.
    /// </para>
    /// </summary>
    /// <param name="operation">
    /// Async delegate that receives the open <see cref="ILogixProject"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Token forwarded to the caller; not used by the guard itself.
    /// </param>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/> when:
    /// <list type="bullet">
    ///   <item>The session is not open.</item>
    ///   <item>The SDK throws <see cref="ObjectDisposedException"/>.</item>
    /// </list>
    /// </exception>
    public async Task ExecuteAsync(
        Func<ILogixProjectHandle, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var project = GetProject();

        try
        {
            await operation(project).ConfigureAwait(false);
        }
        catch (ObjectDisposedException odex)
        {
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                "Session was closed unexpectedly while performing the operation. " +
                "The underlying gRPC handle has been disposed."), odex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && RockwellErrorHints.IsServerFaulted(ex))
        {
            throw TryMapServerFault(ex)!;
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <summary>
    /// Deterministically disposes the underlying <see cref="ILogixProject"/>
    /// gRPC handle and transitions the session to the <c>Closed</c> state.
    ///
    /// <para>
    /// Also deletes the server-managed working copy of the project file that
    /// was created during <see cref="OpenAsync"/>.  If the working-copy
    /// directory is left empty after the file is removed, the directory is
    /// deleted as well, keeping <see cref="WorkingDirectoryBase"/> clean.
    /// Cleanup failures are swallowed — <see cref="Dispose"/> must not throw.
    /// </para>
    ///
    /// <para>
    /// Safe to call multiple times; subsequent calls after the first are
    /// no-ops.  This ensures the gRPC channel is released without relying
    /// on a finalizer.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        ILogixProjectHandle? toDispose;
        string? copyToDelete;

        lock (_stateLock)
        {
            if (_state == SessionState.Closed) return;

            _state           = SessionState.Closed;
            toDispose        = _project;
            copyToDelete     = _workingCopyPath;
            _project         = null;
            _workingCopyPath = null;
            IsOnline         = false;
            IsCorrelated     = false;
            StagedTagBuffer.Clear();
            HasStagedRungEdit = false;
            StagedRungCount   = 0;
            StagedRungBuffer.Clear();
            StagedReplaceCounts.Clear();
            StagedKeptNormal.Clear();
            StagedUnchangedReplacements.Clear();
            ForgetStagedImports();
            ForgetCommentUndos();
            ForgetZoneCommentUndos();
            StagedReplacedRungTexts.Clear();
            lock (_unverifiedClearLock) _unverifiedClears.Clear();
            ForgetRoutineBaselines();
            lock (_publishMismatchLock) _publishMismatches.Clear();
            PreStageTagCensus.Clear();
            PreStageTagAccess.Clear();
        }

        // Dispose the SDK handle outside the lock to avoid holding it during
        // I/O teardown.  The SDK must release its lock on the working copy
        // before we attempt to delete it.
        // A faulted SDK server may throw while tearing down; the handle is released regardless.
        try { toDispose?.Dispose(); }
        catch when (_faultDetail is not null) { /* best effort on a faulted session */ }

        // ── Best-effort cleanup of working copy ────────────────────────────────
        if (copyToDelete is not null)
        {
            try
            {
                File.Delete(copyToDelete);

                // Prune the per-source-path slot directory if it is now empty.
                var dir = Path.GetDirectoryName(copyToDelete);
                if (dir is not null
                    && Directory.Exists(dir)
                    && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch
            {
                // Best-effort — cleanup failures must not propagate from Dispose.
            }
        }
    }
}
#endif
