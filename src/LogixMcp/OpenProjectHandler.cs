using System.Text.Json.Serialization;

/// <summary>
/// Strongly-typed success response returned by <see cref="OpenProjectHandler.Execute"/>.
/// </summary>
/// <param name="Controller">Name of the controller as stored in the L5X file.</param>
/// <param name="SchemaRevision">Schema revision attribute from the RSLogix5000Content element.</param>
/// <param name="Programs">Total number of programs in the project.</param>
/// <param name="Tags">Total number of controller-scoped tags.</param>
/// <param name="Routines">Total number of routines across all programs.</param>
/// <param name="GateSource">
/// Present only when the gate was derived from an ACD file via SDK conversion.
/// Value is <c>"acd-derived"</c> to indicate the L5X used for gate derivation was
/// produced by the server (not supplied by the operator). Absent (<c>null</c>) for
/// operator-provided L5X input.
/// </param>
/// <param name="DerivedL5x">
/// Absolute path of the temporary L5X file exported from the ACD during gate
/// derivation. Populated only when <see cref="GateSource"/> is <c>"acd-derived"</c>;
/// absent (<c>null</c>) for L5X input. The file is deleted after parsing but the
/// path is reported so callers can distinguish server-generated exports from
/// operator-provided ones.
/// </param>
/// <param name="PartialExport">
/// <c>true</c> when the file is a partial (routine/program) export. Such a project opens
/// for reads and analysis but never derives a write gate. Absent for a full controller export.
/// </param>
/// <param name="ExportTarget">
/// The root <c>TargetType</c> of a partial export (e.g. <c>"Routine"</c>). Absent for a full export.
/// </param>
public record OpenProjectResult(
    [property: JsonPropertyName("controller")]     string  Controller,
    [property: JsonPropertyName("schemaRevision")] string  SchemaRevision,
    [property: JsonPropertyName("programs")]       int     Programs,
    [property: JsonPropertyName("tags")]           int     Tags,
    [property: JsonPropertyName("routines")]       int     Routines,
    [property: JsonPropertyName("gateSource")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   string? GateSource = null,
    [property: JsonPropertyName("derivedL5x")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   string? DerivedL5x = null,
    [property: JsonPropertyName("partialExport")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   bool?   PartialExport = null,
    [property: JsonPropertyName("exportTarget")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   string? ExportTarget = null);

/// <summary>
/// Strongly-typed error response returned by <see cref="OpenProjectHandler.Execute"/>
/// when the file is missing or cannot be parsed.
/// </summary>
/// <param name="Error">Human-readable error message.</param>
public record OpenProjectError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Kind = null);

/// <summary>
/// Structured error returned by <see cref="OpenProjectHandler.ExecuteAsync"/> (via
/// the ACD converter) when creating a working copy of the ACD file fails because
/// Studio 5000 holds an OS-level exclusive lock on the file.
///
/// <para>
/// Although the private-copy approach normally succeeds even when Studio has a
/// project open (because the OS copy requires only a shared read lock), certain
/// Studio versions or OS configurations may hold a write-exclusive lock that
/// prevents even a read copy.  When that happens, the copy raises an
/// <see cref="System.IO.IOException"/> with
/// <c>ERROR_SHARING_VIOLATION</c> (Win32 code 32 / HRESULT 0x80070020).  This
/// dedicated error type lets callers distinguish the lock condition from a
/// generic I/O failure and display an actionable remediation hint.
/// </para>
///
/// <para>
/// Dedicated fields allow callers to distinguish this condition from a generic
/// file-not-found or parse failure, and to surface the workaround directly without
/// parsing the human-readable <paramref name="Error"/> string.
/// </para>
/// </summary>
/// <param name="Error">
/// Human-readable description stating that the ACD cannot be read because Studio
/// 5000 has the file open exclusively.
/// </param>
/// <param name="Workaround">
/// Actionable alternatives: close the project in Studio 5000 and retry, or export
/// the project as L5X (<em>File → Save As → L5X</em>) and open that L5X with
/// <c>logix_open_project</c> instead.
/// </param>
public record OpenProjectStudioLockError(
    [property: JsonPropertyName("error")]      string Error,
    [property: JsonPropertyName("workaround")] string Workaround);

/// <summary>
/// Structured error returned by <see cref="OpenProjectHandler.ExecuteAsync"/> when
/// the input file is an <c>.ACD</c> but the SDK adapter is not available (core-only
/// build or no Rockwell install), so conversion cannot run.
///
/// <para>
/// Dedicated fields allow callers to distinguish this condition from a generic
/// file-not-found or parse failure, and to surface the workaround directly without
/// parsing the human-readable <paramref name="Error"/> string.
/// </para>
/// </summary>
/// <param name="Error">
/// Human-readable description stating that ACD conversion is unavailable because
/// the Logix Designer SDK adapter is not loaded.
/// </param>
/// <param name="Workaround">
/// Actionable alternative: open the ACD in Studio 5000 and use
/// <em>File → Save As → L5X</em> to export an L5X, then open that L5X with
/// <c>logix_open_project</c>.
/// </param>
public record OpenProjectConversionUnavailableError(
    [property: JsonPropertyName("error")]      string Error,
    [property: JsonPropertyName("workaround")] string Workaround);

/// <summary>
/// Pure handler for the <c>logix_open_project</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
/// </summary>
public static class OpenProjectHandler
{
    // ── ACD conversion seam ───────────────────────────────────────────────────

    /// <summary>
    /// SDK-provided delegate for ACD-to-L5X conversion.
    ///
    /// <para>
    /// Set to a non-<c>null</c> value by <c>SdkAdapterInitializer.Initialize</c>
    /// when the <c>LogixMcp.SdkAdapter</c> assembly is loaded.  <c>null</c> in
    /// non-SDK builds or when the adapter has not been loaded.
    /// </para>
    ///
    /// <para>
    /// Signature: <c>(acdPath, service, ct) → Task&lt;object&gt;</c> where the
    /// returned object is either an <see cref="OpenProjectResult"/> on success or
    /// an <see cref="OpenProjectError"/> on failure.
    /// </para>
    /// </summary>
    public static Func<string, ProjectService, CancellationToken, Task<object>>? AcdConverter { get; set; }

    // ── L5X path (synchronous, unchanged) ────────────────────────────────────

    /// <summary>
    /// Loads the L5X file at <paramref name="path"/> into <paramref name="service"/>,
    /// then returns a summary of the loaded project.
    /// Never throws; returns <see cref="OpenProjectError"/> on failure instead.
    /// </summary>
    /// <param name="path">Absolute or relative file path of the .L5X file to open.</param>
    /// <param name="service">The project singleton that will hold the loaded content.</param>
    /// <returns>
    /// An <see cref="OpenProjectResult"/> on success,
    /// or an <see cref="OpenProjectError"/> on file-not-found or parse failure.
    /// </returns>
    public static object Execute(string path, ProjectService service)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new OpenProjectError("Path must not be empty.");

        try
        {
            service.Load(path);
            var project = service.Project!;
            int totalRoutines = project.Programs.Sum(p => p.Routines.Count);

            return new OpenProjectResult(
                Controller:     project.Controller.Name,
                SchemaRevision: project.Content.SchemaRevision.ToString(),
                Programs:       project.Programs.Count,
                Tags:           project.Tags.Count,
                Routines:       totalRoutines,
                PartialExport:  service.PartialExport is null ? null : true,
                ExportTarget:   service.PartialExport?.ExportTarget);
        }
        catch (SafeXmlException ex)
        {
            return new OpenProjectError(ex.Message, ex.Kind);
        }
        catch (Exception ex)
        {
            return new OpenProjectError(ex.Message);
        }
    }

    // ── Unified async entry point (ACD + L5X) ─────────────────────────────────

    /// <summary>
    /// Opens the project at <paramref name="path"/> and returns a summary.
    ///
    /// <para>
    /// When <paramref name="path"/> has a <c>.ACD</c> extension the ACD conversion
    /// path is taken: the file is copied to the server-managed working directory,
    /// the Logix Designer SDK opens the copy, <c>SaveAsAsync</c> exports a
    /// full-project L5X, and that L5X is parsed for gate derivation.
    /// <see cref="ProjectService.FilePath"/> is set to the original ACD path so
    /// that subsequent SDK sessions open against the ACD (not the derived L5X),
    /// which is required for online correlation.
    /// The ACD path requires <see cref="AcdConverter"/> to be registered — i.e.
    /// the adapter must have been loaded.  Returns an error object when the
    /// adapter is unavailable.
    /// </para>
    ///
    /// <para>
    /// When <paramref name="path"/> has any other extension (typically <c>.L5X</c>)
    /// the synchronous <see cref="Execute"/> path is taken unchanged.
    /// </para>
    ///
    /// <para>Never throws; returns <see cref="OpenProjectError"/> on failure.</para>
    /// </summary>
    /// <param name="path">
    /// Absolute or relative file path of the <c>.L5X</c> or <c>.ACD</c> project file.
    /// </param>
    /// <param name="service">The project singleton that will hold the loaded content.</param>
    /// <param name="ct">Cancellation token forwarded to the ACD conversion path.</param>
    /// <returns>
    /// An <see cref="OpenProjectResult"/> on success,
    /// or an <see cref="OpenProjectError"/> on failure.
    /// </returns>
    public static async Task<object> ExecuteAsync(
        string            path,
        ProjectService    service,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new OpenProjectError("Path must not be empty.");

        var ext = Path.GetExtension(path);
        if (ext.Equals(".acd", StringComparison.OrdinalIgnoreCase))
        {
            if (AcdConverter is null)
                return new OpenProjectConversionUnavailableError(
#if LOGIX_SDK
                    Error:
                        "ACD conversion is unavailable: the Logix Designer SDK adapter is not " +
                        "loaded. Ensure the server is built with EnableLogixSdk=true and that " +
                        "a licensed Studio 5000 or FactoryTalk Logix Echo installation is " +
                        "present, then restart the server.",
                    Workaround:
                        "In Studio 5000: open the ACD, then use File → Save As → L5X to export " +
                        "the project. Pass the resulting .L5X path to logix_open_project instead.");
#else
                    Error:
                        ".ACD files are not supported in this build.",
                    Workaround:
                        "Export the project to L5X and pass the .L5X path to logix_open_project.");
#endif

            try
            {
                return await AcdConverter(path, service, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new OpenProjectError(
                    $"ACD conversion failed for '{path}': {ex.Message}");
            }
        }

        // All other extensions (typically .L5X) — use the existing synchronous path.
        return Execute(path, service);
    }
}
