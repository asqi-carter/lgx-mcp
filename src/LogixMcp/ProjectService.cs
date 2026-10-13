using L5Sharp.Core;

/// <summary>
/// Singleton service that holds the currently-open Logix L5X project in memory.
/// Registered in DI as a singleton so all tool handlers share the same instance.
/// </summary>
public class ProjectService
{
    private L5X?    _project;
    private string? _filePath;
    private string? _uploadedFrom;
    private bool _openedExplicitly;
    private PartialExportInfo? _partialExport;
    private TargetRevisionInfo? _targetRevision;

    /// <summary>Gets the currently-loaded L5X project, or <c>null</c> if none is open.</summary>
    public L5X? Project => _project;

    /// <summary>
    /// Gets the file-system path of the currently-loaded L5X project, or <c>null</c>
    /// if no project is open.  Set by <see cref="Load"/> and cleared by <see cref="Clear"/>.
    /// Used by <see cref="ConnectHandler"/> to open a <see cref="LogixProjectSession"/>
    /// when <c>backend='sdk'</c> is requested.
    /// </summary>
    public string? FilePath => _filePath;

    /// <summary>
    /// Comm path of the controller the project was uploaded from, or <c>null</c> when it was
    /// loaded from a file. Set by <see cref="RegisterFromUpload"/>.
    /// </summary>
    public string? UploadedFrom => _uploadedFrom;

    /// <summary>
    /// Set when the loaded file is a partial (routine/program) export rather than a full
    /// controller export, otherwise <c>null</c>. A partial export is open for reads and
    /// analysis but never derives a write gate.
    /// </summary>
    public PartialExportInfo? PartialExport => _partialExport;

    /// <summary>
    /// Major revision and mnemonic set of the loaded project (<c>Controller/@MajorRev</c>, falling back to the
    /// integer part of <c>@SoftwareRevision</c>), or <c>null</c> when no project is open.
    /// </summary>
    public TargetRevisionInfo? TargetRevision => _targetRevision;

    /// <summary>
    /// <c>true</c> when the current project was opened by the user (<see cref="Load"/> /
    /// <see cref="LoadFromAcd"/>), <c>false</c> when it came from a controller upload or nothing is loaded.
    /// An explicitly opened project is never replaced by <see cref="RegisterFromUpload"/>.
    /// </summary>
    public bool OpenedExplicitly => _openedExplicitly;

    /// <summary>Returns <c>true</c> when a project has been successfully loaded.</summary>
    public bool IsLoaded => _project is not null;

    /// <summary>
    /// Loads the L5X file at <paramref name="path"/> and stores it as the current project.
    /// Any previously-loaded project is replaced.
    /// </summary>
    /// <param name="path">Absolute or relative path to the .L5X file.</param>
    /// <exception cref="FileNotFoundException">Thrown when <paramref name="path"/> does not exist.</exception>
    /// <exception cref="Exception">Any L5Sharp parse error propagates to the caller.</exception>
    public void Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"L5X file not found: {path}", path);
        var doc = SafeXml.LoadDocument(path);
        _project       = SafeXml.ToL5x(doc);
        _partialExport = PartialExportInfo.Detect(doc.Root);
        _targetRevision = global::TargetRevision.Detect(doc.Root);
        _filePath      = path;
        _uploadedFrom  = null;
        _openedExplicitly = true;
    }

    /// <summary>
    /// Loads an L5X project from <paramref name="l5xPath"/> (a derived L5X export) and records
    /// <paramref name="acdPath"/> as the SDK session file path.
    ///
    /// <para>
    /// Used by the ACD-to-L5X conversion path in <c>logix_open_project</c>:
    /// the L5X is produced by <c>SaveAsAsync</c> on an SDK session opened against the ACD,
    /// and the original ACD path is preserved in <see cref="FilePath"/> so that subsequent
    /// SDK sessions (e.g. in <c>logix_connect</c>) open against the ACD rather than the
    /// derived L5X — which is required for online correlation to succeed.
    /// </para>
    /// </summary>
    /// <param name="l5xPath">
    /// Absolute path to the derived L5X file.  Must exist at the time of the call.
    /// </param>
    /// <param name="acdPath">
    /// Absolute or relative path to the original ACD file.  Stored in <see cref="FilePath"/>
    /// so that subsequent SDK sessions open against the ACD (not the L5X) for correlation.
    /// </param>
    /// <exception cref="FileNotFoundException">
    /// Thrown when <paramref name="l5xPath"/> does not exist.
    /// </exception>
    /// <exception cref="Exception">Any L5Sharp parse error propagates to the caller.</exception>
    public void LoadFromAcd(string l5xPath, string acdPath)
    {
        if (!File.Exists(l5xPath))
            throw new FileNotFoundException($"L5X file not found: {l5xPath}", l5xPath);
        var doc = SafeXml.LoadDocument(l5xPath);
        _project       = SafeXml.ToL5x(doc);
        _partialExport = PartialExportInfo.Detect(doc.Root);
        _targetRevision = global::TargetRevision.Detect(doc.Root);
        _filePath      = acdPath;
        _uploadedFrom  = null;
        _openedExplicitly = true;
    }

    /// <summary>
    /// Registers an already-parsed L5X project and a file path into the service
    /// without requiring the L5X file to exist on disk.
    ///
    /// <para>
    /// Used by <see cref="ConnectHandler"/> to populate the service after a
    /// no-project-file upload session (<c>CreateFromControllerAsync</c>) completes.
    /// Subsequent tool calls that guard on <see cref="IsLoaded"/> or
    /// <see cref="FilePath"/> will see a valid project without requiring a
    /// separate <c>logix_open_project</c> invocation.
    /// </para>
    ///
    /// <para>
    /// This is a no-op only when the loaded project was opened explicitly (<see cref="OpenedExplicitly"/>), so an
    /// explicit <c>logix_open_project</c> invocation is never silently overwritten. A project that an earlier
    /// upload registered is replaced, so analysis always sees the latest controller image.
    /// </para>
    /// </summary>
    /// <param name="project">Already-parsed L5X to register as the current project.</param>
    /// <param name="acdPath">
    /// Absolute path to the uploaded ACD file.  Stored in <see cref="FilePath"/>
    /// so that subsequent SDK sessions can open against the same ACD.
    /// </param>
    /// <param name="commPath">Comm path the project was uploaded from; reported by <c>logix_document</c>.</param>
    public void RegisterFromUpload(L5X project, string acdPath, string? commPath = null)
    {
        if (IsLoaded && _openedExplicitly) return;
        _project       = project;
        _partialExport = null;   // an upload from a controller is a full controller image
        _targetRevision = global::TargetRevision.Detect(project, uploaded: true);
        _filePath      = acdPath;
        _uploadedFrom = commPath;
        _openedExplicitly = false;
    }

    /// <summary>
    /// Re-reads the currently-loaded project file from disk and replaces the in-memory model, so that
    /// read tools (<c>logix_get_routine</c>, <c>logix_find_tags</c>, ...) see an edit that was saved
    /// to the file out-of-band (e.g. an offline rung edit). Does nothing when no project is loaded.
    /// </summary>
    /// <exception cref="FileNotFoundException">Thrown when the file no longer exists.</exception>
    /// <exception cref="Exception">Any L5Sharp parse error propagates; the previous model is kept.</exception>
    public void Reload()
    {
        if (_filePath is null || _project is null) return;
        var explicitly = _openedExplicitly;
        var uploadedFrom = _uploadedFrom;
        Load(_filePath);
        _openedExplicitly = explicitly;
        _uploadedFrom = uploadedFrom;
    }

    /// <summary>Clears the current project from memory.</summary>
    public void Clear()
    {
        _project       = null;
        _partialExport = null;
        _targetRevision = null;
        _filePath      = null;
        _uploadedFrom = null;
        _openedExplicitly = false;
    }
}
