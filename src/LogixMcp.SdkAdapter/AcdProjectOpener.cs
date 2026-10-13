#if LOGIX_SDK
// ── AcdProjectOpener — ACD-to-L5X conversion for logix_open_project ──────────
using L5Sharp.Core;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;

// Alias System.Threading.Tasks.Task to avoid ambiguity with L5Sharp.Core.Task
// when both namespaces are in scope.
using Task = System.Threading.Tasks.Task;

/// <summary>
/// Provides the <see cref="OpenAsync"/> delegate that is registered on
/// <see cref="OpenProjectHandler.AcdConverter"/> by
/// <see cref="SdkAdapterInitializer.Initialize"/>.
///
/// <para>
/// Implements the ACD-to-L5X conversion path for <c>logix_open_project</c>:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       Copies the source ACD to the server-managed working directory via a
///       <see cref="LogixProjectSession"/> (using the same private-copy semantics
///       as the online SDK path).
///     </description>
///   </item>
///   <item>
///     <description>
///       Opens the working copy with <c>LogixProject.OpenLogixProjectAsync</c> via
///       the session.
///     </description>
///   </item>
///   <item>
///     <description>
///       Calls <c>ILogixProject.SaveAsAsync</c> with <c>detailedL5x: true</c> to
///       export a full-project L5X into a temporary file.
///     </description>
///   </item>
///   <item>
///     <description>
///       Disposes the SDK session (deleting the working ACD copy) and parses the
///       temporary L5X via <see cref="ProjectService.LoadFromAcd"/>, which sets
///       <see cref="ProjectService.FilePath"/> to the <em>original</em> ACD path
///       so that subsequent <c>logix_connect</c> sessions open against the ACD
///       (not the derived L5X) — required for online correlation.
///     </description>
///   </item>
///   <item>
///     <description>
///       Deletes the temporary L5X (already loaded in memory) and returns an
///       <see cref="OpenProjectResult"/> summary.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// Injectable factories are exposed on the constructor for unit-test overrides.
/// Production callers should use <see cref="Default"/>.
/// </para>
/// </summary>
internal sealed class AcdProjectOpener
{
    // ── Injectable factories ──────────────────────────────────────────────────

    private readonly Func<LogixProjectSession> _sessionFactory;
    private readonly Func<string, L5X>         _l5xLoader;

    // ── Production singleton ──────────────────────────────────────────────────

    /// <summary>Production singleton that uses the real SDK and <see cref="SafeXml.LoadL5x"/>.</summary>
    internal static readonly AcdProjectOpener Default = new(
        static () => new LogixProjectSession(),
        SafeXml.LoadL5x);

    // ── Construction ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new <see cref="AcdProjectOpener"/> with the supplied factories.
    /// </summary>
    /// <param name="sessionFactory">Produces a new <see cref="LogixProjectSession"/>.</param>
    /// <param name="l5xLoader">Parses a file path into an <see cref="L5X"/> object.</param>
    internal AcdProjectOpener(
        Func<LogixProjectSession> sessionFactory,
        Func<string, L5X>         l5xLoader)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(l5xLoader);
        _sessionFactory = sessionFactory;
        _l5xLoader      = l5xLoader;
    }

    // ── Path-isolation helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Computes a unique, collision-safe output path for the exported L5X
    /// within the server's working directory.
    ///
    /// <para>
    /// The filename embeds a random 12-character hex slug so that concurrent
    /// conversions of the same ACD do not overwrite each other.  The file is
    /// placed directly under <see cref="LogixProjectSession.WorkingDirectoryBase"/>
    /// (not in a per-source-path slot) because its lifetime is transient —
    /// it is deleted as soon as it has been parsed into memory.
    /// </para>
    /// </summary>
    /// <returns>
    /// An absolute path of the form
    /// <c>{WorkingDirectoryBase}/lgx-acd-{slug}.L5X</c>.
    /// </returns>
    internal static string ComputeL5xOutputPath()
    {
        var slug = Guid.NewGuid().ToString("N")[..12];
        return Path.Combine(
            LogixProjectSession.WorkingDirectoryBase,
            $"lgx-acd-{slug}.L5X");
    }

    /// <summary>
    /// Validates that <paramref name="outputPath"/> resolves entirely within
    /// <paramref name="workingDirBase"/>, preventing writes outside the
    /// server-managed working directory.
    ///
    /// <para>
    /// Both paths are canonicalized via <see cref="Path.GetFullPath"/> before
    /// comparison so that relative segments (<c>../</c>) or other path
    /// components that could escape the working directory are detected and
    /// rejected.  A trailing directory separator is appended to
    /// <paramref name="workingDirBase"/> before the prefix check so that a
    /// sibling directory named <c>logix-mcp-work-extra</c> does not pass
    /// validation against a base of <c>logix-mcp-work</c>.
    /// </para>
    /// </summary>
    /// <param name="outputPath">
    /// Absolute or relative path of the file to be written.
    /// </param>
    /// <param name="workingDirBase">
    /// Root directory against which <paramref name="outputPath"/> is validated
    /// (typically <see cref="LogixProjectSession.WorkingDirectoryBase"/>).
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the resolved <paramref name="outputPath"/> does not start
    /// with the resolved <paramref name="workingDirBase"/>.
    /// </exception>
    internal static void AssertWithinWorkingDirectory(string outputPath, string workingDirBase)
    {
        var resolvedOutput = Path.GetFullPath(outputPath);
        // Append a trailing separator so that "/tmp/logix-mcp-work-extra/file"
        // does not pass a prefix check against "/tmp/logix-mcp-work/".
        var resolvedBase = Path.GetFullPath(workingDirBase)
                               .TrimEnd(Path.DirectorySeparatorChar,
                                        Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;

        if (!resolvedOutput.StartsWith(resolvedBase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Output path isolation violation: '{resolvedOutput}' resolves outside " +
                $"the server working directory '{resolvedBase.TrimEnd(Path.DirectorySeparatorChar)}'. " +
                "ACD conversion artifacts must be written exclusively to the " +
                "server-managed working directory.");
    }

    // ── Studio lock detection ─────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/> (or any exception in its
    /// <see cref="Exception.InnerException"/> chain) represents an OS-level file
    /// sharing violation — the characteristic error when Studio 5000 holds an
    /// exclusive lock on an ACD file and a second process attempts to read it.
    ///
    /// <para>
    /// Detection strategy (applied at each node in the exception chain):
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       <b>HResult check</b>: Win32 <c>ERROR_SHARING_VIOLATION</c> (code 32,
    ///       <c>0x20</c>) maps to HRESULT <c>0x80070020</c>.  Checking the low
    ///       16 bits of <see cref="Exception.HResult"/> isolates the Win32 facility
    ///       code and matches this value even when the high bits differ between
    ///       CLR and COM encodings.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <b>Message text fallback</b>: used when the HResult has not been set
    ///       by the OS (e.g. manually constructed exceptions in unit tests or
    ///       non-Windows environments where the error text is set by the runtime).
    ///     </description>
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="ex">
    /// The exception to inspect.  May be <c>null</c> — returns <c>false</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if any exception in the chain is an <see cref="IOException"/>
    /// that matches a file sharing / exclusive-lock error; <c>false</c> otherwise.
    /// </returns>
    internal static bool IsFileSharingViolation(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is not IOException ioEx)
                continue;

            // Win32 ERROR_SHARING_VIOLATION = 32 (0x20); HRESULT = 0x80070020.
            // Masking to the low 16 bits isolates the Win32 error code regardless
            // of how the CLR or COM layer encodes the high facility bits.
            const int errorSharingViolation = 0x20;
            if ((ioEx.HResult & 0xFFFF) == errorSharingViolation)
                return true;

            // Text fallback: matches the Windows OS error message and common
            // runtime equivalents on other platforms.
            if (ioEx.Message.Contains("used by another process",
                    StringComparison.OrdinalIgnoreCase) ||
                ioEx.Message.Contains("sharing violation",
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // ── Core conversion logic ─────────────────────────────────────────────────

    /// <summary>
    /// Converts the ACD at <paramref name="acdPath"/> to an L5X, loads the result
    /// into <paramref name="service"/>, and returns a project summary.
    ///
    /// <para>
    /// This method is registered on <see cref="OpenProjectHandler.AcdConverter"/>
    /// and must match its <c>Func&lt;string, ProjectService, CancellationToken, Task&lt;object&gt;&gt;</c>
    /// signature.
    /// </para>
    /// </summary>
    /// <param name="acdPath">
    /// Absolute or relative path to the source ACD file.  The original is never
    /// modified — a working copy is made by the SDK session.
    /// </param>
    /// <param name="service">
    /// Singleton project service into which the derived L5X content is loaded.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// An <see cref="OpenProjectResult"/> on success, or an
    /// <see cref="OpenProjectError"/> on failure.  Never throws (except for
    /// <see cref="OperationCanceledException"/> which propagates to the caller).
    /// </returns>
    internal async Task<object> OpenAsync(
        string            acdPath,
        ProjectService    service,
        CancellationToken ct = default)
    {
        if (!File.Exists(acdPath))
            return new OpenProjectError(
                $"ACD file not found: {acdPath}");

        // Derive a unique output path for the exported L5X, constrained to the
        // server's working directory.  Using a random slug avoids collisions
        // when the same ACD is opened concurrently.
        var l5xPath = ComputeL5xOutputPath();

        // Ensure the working directory exists before any file is written to it.
        Directory.CreateDirectory(LogixProjectSession.WorkingDirectoryBase);

        // Guard: verify the computed path cannot escape the working directory.
        // This is a defence-in-depth check — the path is constructed above via
        // ComputeL5xOutputPath, but the guard makes the invariant explicit and
        // testable, protecting against future refactors that might alter path
        // construction.
        AssertWithinWorkingDirectory(l5xPath, LogixProjectSession.WorkingDirectoryBase);

        var session = _sessionFactory();
        try
        {
            // ── 1. Open SDK session against working copy of ACD ───────────────
            // LogixProjectSession.DefaultWorkingCopier copies the ACD to the
            // server-managed working directory; the original file is never locked.
            try
            {
                await session.OpenAsync(acdPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TagServiceException ex)
            {
                // If the copy step failed because Studio 5000 holds an exclusive
                // OS lock on the ACD, surface a dedicated, actionable error rather
                // than a raw OS traceback.  The original IOException is preserved
                // as the InnerException of the TagServiceException.
                if (IsFileSharingViolation(ex.InnerException))
                    return new OpenProjectStudioLockError(
                        Error:
                            $"Studio 5000 has '{Path.GetFileName(acdPath)}' open exclusively " +
                            "and the server cannot create a working copy of the file.",
                        Workaround:
                            "Close the project in Studio 5000 and retry, or export it as L5X " +
                            "(File → Save As → L5X) and open the .L5X path with " +
                            "logix_open_project instead.");

                return new OpenProjectError(
                    $"SDK failed to open ACD '{Path.GetFileName(acdPath)}': " +
                    $"{ex.Error.Message}" +
                    RockwellErrorHints.AdditionalGuidance(ex.Error.Message));
            }
            catch (Exception ex)
            {
                return new OpenProjectError(
                    $"Failed to open ACD '{Path.GetFileName(acdPath)}': {ex.Message}" +
                    RockwellErrorHints.AdditionalGuidance(ex.Message));
            }

            // ── 2. Export full-project L5X ────────────────────────────────────
            try
            {
                await session.ExecuteAsync(
                    p => p.SaveAsAsync(l5xPath, force: true, detailedL5x: true, ct),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TagServiceException ex)
            {
                return new OpenProjectError(
                    $"SDK export (SaveAs) failed for '{Path.GetFileName(acdPath)}': " +
                    $"{ex.Error.Message}");
            }
            catch (Exception ex)
            {
                return new OpenProjectError(
                    $"Failed to export L5X from '{Path.GetFileName(acdPath)}': {ex.Message}");
            }
        }
        finally
        {
            // Dispose the session (deletes its working ACD copy).
            // The original ACD is untouched; the exported L5X is kept for parsing.
            session.Dispose();
        }

        // ── 3. Parse exported L5X and load into ProjectService ────────────────
        // ProjectService.FilePath is set to the original ACD path so that future
        // SDK sessions (e.g. logix_connect) open against the ACD — required for
        // online correlation.
        try
        {
            service.LoadFromAcd(l5xPath, Path.GetFullPath(acdPath));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new OpenProjectError(
                $"Failed to parse derived L5X for '{Path.GetFileName(acdPath)}': {ex.Message}");
        }
        finally
        {
            // Best-effort cleanup — the L5X is now in memory; no need to keep it on disk.
            try { File.Delete(l5xPath); } catch { /* best effort */ }
        }

        // ── 4. Return project summary including ACD derivation provenance ─────
        var project      = service.Project!;
        int totalRoutines = project.Programs.Sum(p => p.Routines.Count);

        return new OpenProjectResult(
            Controller:     project.Controller.Name,
            SchemaRevision: project.Content.SchemaRevision.ToString(),
            Programs:       project.Programs.Count,
            Tags:           project.Tags.Count,
            Routines:       totalRoutines,
            GateSource:     "acd-derived",
            DerivedL5x:     l5xPath);
    }
}
#endif
