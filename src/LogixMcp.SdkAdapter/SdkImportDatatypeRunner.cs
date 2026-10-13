#if LOGIX_SDK
// ── SdkImportDatatypeRunner — real SDK backing for logix_import_datatype ──────
// Implements the three SDK-backed operations registered on SdkImportDelegates:
// import+save, re-export-and-parse, and build. Each runs on a fresh offline
// LogixProjectSession (private-copy semantics; the caller's ACD is never held
// open, and the working copy is deleted on dispose). This mirrors the Python
// SDK path that bench-proved a UDT edit
// (partial_import_from_xml_file → save) but reuses the tree's own session
// wrapper for exception mapping and lifecycle.
//
// Note: this is the real import; it replaces an earlier File.WriteAllText stub.
using System.Collections.Generic;
using System.Text;
using RockwellAutomation.LogixDesigner;

using Task                 = System.Threading.Tasks.Task;
using ImportCollisionOptions = RockwellAutomation.LogixDesigner.LogixProject.ImportCollisionOptions;

/// <summary>
/// SDK implementations wired onto <see cref="SdkImportDelegates"/> by
/// <c>SdkConnectFactory.Initialize</c>.
/// </summary>
internal static class SdkImportDatatypeRunner
{
    // ── Registration ──────────────────────────────────────────────────────────

    /// <summary>Wires the three runners onto the SDK-free registry.</summary>
    internal static void Register()
    {
        SdkImportDelegates.SdkImportRunner = Import;
        SdkImportDelegates.ReexportReader  = Reexport;
        SdkImportDelegates.BuildRunner     = Build;
        // StudioLockChecker: leave null — the handler's own IsLockedByStudio probe
        // is correct and needs no SDK.
    }

    // ── Import + save ───────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the ACD at <paramref name="saveAsAcd"/>, imports the datatype L5X at
    /// <paramref name="xpath"/> with the mapped collision option, and saves the
    /// project back to <paramref name="saveAsAcd"/> as an ACD. The handler has
    /// already copied the input ACD to this path, so the input is never touched.
    /// </summary>
    private static void Import(
        string saveAsAcd, string l5xToImport, string xpath, ImportCollisionOption collision)
    {
        var sdkCollision = Map(collision);
        RunSync(async ct =>
        {
            using var session = new LogixProjectSession();
            await session.OpenAsync(saveAsAcd, ct).ConfigureAwait(false);
            await session.ExecuteAsync(
                p => p.PartialImportFromXmlFileAsync(xpath, l5xToImport, sdkCollision, false, ct),
                ct).ConfigureAwait(false);
            // Persist to the caller's path (detailedL5x:false → ACD, force:true → overwrite
            // the seed copy). SaveAsync() would only save the private working copy, which
            // is deleted on dispose.
            await session.ExecuteAsync(
                p => p.SaveAsAsync(saveAsAcd, true, false, ct),
                ct).ConfigureAwait(false);
        });
    }

    // ── Re-export + parse ─────────────────────────────────────────────────────

    /// <summary>
    /// Exports the datatype at <paramref name="xpath"/> from the saved ACD to a
    /// temp L5X and parses its members via
    /// <see cref="ImportDatatypeHandler.ExtractTargetMembersFromFragment"/>.
    /// </summary>
    private static IReadOnlyList<DataTypeMemberEntry> Reexport(string acdPath, string xpath)
    {
        var temp = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N") + ".L5X");
        try
        {
            RunSync(async ct =>
            {
                using var session = new LogixProjectSession();
                await session.OpenAsync(acdPath, ct).ConfigureAwait(false);
                await session.ExecuteAsync(
                    p => p.PartialExportToXmlFileAsync(xpath, temp, ct),
                    ct).ConfigureAwait(false);
            });
            var l5x = System.IO.File.ReadAllText(temp, Encoding.UTF8);
            return ImportDatatypeHandler.ExtractTargetMembersFromFragment(l5x);
        }
        finally
        {
            try { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); }
            catch { /* best effort */ }
        }
    }

    // ── Build ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the saved ACD through the same capability check and build path as <c>logix_build</c>
    /// (<see cref="BuildHandler"/>). Returns the error count (0 on a clean build). Throws
    /// <see cref="SdkBuildUnavailableException"/> when the build check is unavailable on this Designer
    /// (before v37): the import already landed, so the handler reports <c>buildVerify: "unavailable"</c>, not a
    /// failure. Any other exception propagates and is reported as a build error.
    /// </summary>
    private static int Build(string acdPath)
    {
        var errors = 0;
        RunSync(async ct =>
        {
            using var session = new LogixProjectSession();
            await session.OpenAsync(acdPath, ct).ConfigureAwait(false);
            errors = await BuildSessionAsync(session, ct).ConfigureAwait(false);
        });
        return errors;
    }

    /// <summary>
    /// Runs the build check on an open session and maps the outcome for the import handler.
    /// </summary>
    /// <param name="session">An open session on the saved project.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of build errors (0 when clean).</returns>
    /// <exception cref="SdkBuildUnavailableException">The build check is unavailable on this Designer.</exception>
    /// <exception cref="InvalidOperationException">The SDK could not perform the build.</exception>
    internal static async Task<int> BuildSessionAsync(LogixProjectSession session, System.Threading.CancellationToken ct)
    {
        var outcome = await BuildHandler.ExecuteAsync(BuildTargetNames.Default, session, ct).ConfigureAwait(false);
        switch (outcome)
        {
            case BuildResult r:
                return r.Status == BuildResult.Passed ? 0 : System.Math.Max(1, r.Errors.Count);
            case SdkCapabilityUnavailableError u:
                throw new SdkBuildUnavailableException(u.Error, u.Version);
            case BuildError e:
                throw new System.InvalidOperationException(e.Error);
            default:
                throw new System.InvalidOperationException("Unexpected build outcome.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ImportCollisionOptions Map(ImportCollisionOption c) => c switch
    {
        ImportCollisionOption.Overwrite => ImportCollisionOptions.OverwriteOnColl,
        ImportCollisionOption.Discard   => ImportCollisionOptions.DiscardOnColl,
        ImportCollisionOption.Cancel    => ImportCollisionOptions.CancelOnColl,
        _                               => ImportCollisionOptions.OverwriteOnColl,
    };

    // Run async SDK work synchronously from the sync tool method. Task.Run isolates
    // it from any ambient synchronization context so GetResult cannot deadlock.
    private static void RunSync(System.Func<System.Threading.CancellationToken, Task> work) =>
        Task.Run(() => work(System.Threading.CancellationToken.None)).GetAwaiter().GetResult();
}
#endif
