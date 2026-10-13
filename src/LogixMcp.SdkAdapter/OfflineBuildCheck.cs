#if LOGIX_SDK
// ── OfflineBuildCheck — structural build check on an offline saved copy of the session project ──
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;
using ILogixProjectHandle = RockwellAutomation.LogixDesigner.ILogixProject;
using RequestedBuildTarget = RockwellAutomation.LogixDesigner.LogixProject.RequestedBuildTarget;
using Task = System.Threading.Tasks.Task;

/// <summary>
/// What the offline build check produced: either the build could not be attempted
/// (<see cref="UnavailableReason"/> is set) or it ran and either succeeded
/// (<see cref="BuildFailure"/> is <c>null</c>) or threw (<see cref="BuildFailure"/> is the SDK exception).
/// </summary>
/// <param name="UnavailableReason">Why no build was run, or <c>null</c> when the copy was built.</param>
/// <param name="BuildFailure">The exception <c>BuildAsync</c> threw on the copy, or <c>null</c> when it returned.</param>
/// <param name="StagedEditLost">
/// <c>true</c> when the copy was read back and definitely lacks a staged edit;
/// <see cref="UnavailableReason"/> then carries the explanation. <c>false</c> for every other outcome, including a
/// read-back that failed (the edit is then unconfirmed, not known to be lost).
/// </param>
internal sealed record OfflineBuildOutcome(string? UnavailableReason, Exception? BuildFailure, bool StagedEditLost = false)
{
    /// <summary>The build could not be attempted, so <c>buildVerify</c> is <c>"unavailable"</c>.</summary>
    public bool Unavailable => UnavailableReason is not null;
}

/// <summary>
/// Runs the structural build check OFFLINE: it never builds inside the online session. It saves a copy of the
/// session project (SDK save-as) to a temp path under the server work directory, opens the copy in a separate SDK
/// handle, confirms the copy carries the staged (<c>LeaveEdits</c>) workstation edit, builds the copy, then closes
/// the copy handle and deletes the temp copy.
///
/// <para>
/// When the SDK cannot save the project (for example an online project with edits pending), the copy cannot be
/// opened, or the copy lacks the staged edit, the outcome is <c>unavailable</c> with the reason and NO build runs
/// (not online, not on the copy).
/// </para>
/// </summary>
internal static class OfflineBuildCheck
{
    /// <summary>Directory name under <see cref="LogixProjectSession.WorkingDirectoryBase"/> that holds the temp copies.</summary>
    internal const string CopyDirectoryName = "build-verify";

    /// <summary>
    /// Builds an offline saved copy of the session's project.
    /// </summary>
    /// <param name="session">The open session whose project is copied. Its own handle is only used for save-as.</param>
    /// <param name="target">The SDK build target.</param>
    /// <param name="expectedRungTexts">
    /// Normalised rung texts (see <see cref="ProposeRungEditHandler.NormalizeRungText"/>) the copy must carry.
    /// Empty means there is no rung text to look for, so the staged-edit check is skipped.
    /// </param>
    /// <param name="routineXPaths">Routines to read back from the copy when looking for <paramref name="expectedRungTexts"/>.</param>
    /// <param name="eventHandler">Optional build log handler, attached to the copy handle for the build only.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="prepareCopy">
    /// Optional step run on the copy handle (never the session) after it is opened and checked, before the build.
    /// Returns <c>false</c> to skip the build (the outcome is then a build that neither ran nor failed).
    /// </param>
    /// <param name="phases">Optional timer told when the save-as and the replay on the copy begin (verifyOnStage).</param>
    /// <returns>The outcome; never <c>null</c>.</returns>
    /// <exception cref="TagServiceException">
    /// With kind <see cref="TagServiceErrorKind.SdkServerFaulted"/> when Logix Services faulted (the session is then marked faulted).
    /// </exception>
    internal static async Task<OfflineBuildOutcome> RunAsync(
        LogixProjectSession       session,
        RequestedBuildTarget      target,
        IReadOnlyCollection<string> expectedRungTexts,
        IReadOnlyCollection<string> routineXPaths,
        IOperationEvent?          eventHandler,
        CancellationToken         ct,
        Func<ILogixProjectHandle, CancellationToken, Task<bool>>? prepareCopy = null,
        StagePhaseTimer?          phases = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var copyDir  = Path.Combine(LogixProjectSession.WorkingDirectoryBase, CopyDirectoryName, Guid.NewGuid().ToString("N"));
        var copyPath = Path.Combine(copyDir, "verify-copy.ACD");
        bool pending = session.HasStagedRungEdit || expectedRungTexts.Count > 0;
        ILogixProjectHandle? copy = null;
        var replayStarted = false;   // sub-phases are recorded only once the replay-copy phase has begun
        try
        {
            Directory.CreateDirectory(copyDir);

            // 1. Save a copy of the session project. The session handle is used for nothing else.
            phases?.Enter(StagePhases.SaveAs);
            try
            {
                await session.ExecuteAsync(
                    p => p.SaveAsAsync(copyPath, true, false, ct), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (TagServiceException ex) when (ex.Error.Kind == TagServiceErrorKind.SdkServerFaulted) { throw; }
            catch (Exception ex)
            {
                return Unavailable(
                    $"The SDK could not save a copy of the session project{(pending ? " with edits pending" : string.Empty)}: " +
                    $"{Describe(ex)}. No build was run, and none is run inside the online session.");
            }

            // 2. Open the copy offline in a separate handle.
            phases?.Enter(StagePhases.ReplayCopy);
            replayStarted = true;
            phases?.EnterSubPhase(StagePhases.ReplayCopyStep("open-copy"));
            try
            {
                copy = await session.OpenSeparateHandleAsync(copyPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (session.TryMapServerFault(ex) is { } fault) throw fault;
                return Unavailable(
                    $"The saved copy of the session project could not be opened offline: {Describe(ex)}. No build was run.");
            }

            // 3. The copy must carry the staged edit, or building it proves nothing about the edit.
            if (expectedRungTexts.Count > 0)
            {
                phases?.EnterSubPhase(StagePhases.ReplayCopyStep("verify-staged"));
                var missing = await FindMissingStagedRungsAsync(copy, expectedRungTexts, routineXPaths, ct)
                    .ConfigureAwait(false);
                if (missing is not null)
                    return new OfflineBuildOutcome(missing.Value.Reason, null, StagedEditLost: missing.Value.Lost);
            }

            // 3b. Caller-supplied work on the copy (an earlier change: replay an import), still never the session.
            if (prepareCopy is not null)
            {
                phases?.EnterSubPhase(StagePhases.ReplayCopyStep("import"));
                if (!await prepareCopy(copy, ct).ConfigureAwait(false))
                    return new OfflineBuildOutcome(null, null);
            }

            // 4. Build the copy, never the online session.
            phases?.EnterSubPhase(StagePhases.ReplayCopyStep("build"));
            if (eventHandler is not null) copy.AddEventHandler(eventHandler);
            try
            {
                await copy.BuildAsync(target, ct).ConfigureAwait(false);
                return new OfflineBuildOutcome(null, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A fatal from Logix Services marks the session faulted (one server serves both handles).
                if (session.TryMapServerFault(ex) is { } fault) throw fault;
                return new OfflineBuildOutcome(null, ex);
            }
            finally
            {
                if (eventHandler is not null)
                    try { copy.RemoveEventHandler(eventHandler); } catch { /* best effort */ }
            }
        }
        finally
        {
            if (replayStarted) phases?.EnterSubPhase(StagePhases.ReplayCopyStep("close"));
            try { copy?.Dispose(); } catch { /* a faulted server may throw on teardown; the handle is released regardless */ }
            if (replayStarted) phases?.EnterSubPhase(StagePhases.ReplayCopyStep("delete-copy"));
            try { if (Directory.Exists(copyDir)) Directory.Delete(copyDir, recursive: true); } catch { /* best effort */ }
            if (replayStarted) phases?.EndSubPhase();
        }
    }

    private static OfflineBuildOutcome Unavailable(string reason) => new(reason, null);

    /// <summary>How old an offline copy must be before <see cref="CleanStaleCopies"/> removes it.</summary>
    internal static readonly TimeSpan StaleCopyAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Removes leftover offline copies (<c>build-verify\{guid}</c> folders that a crashed or killed run did not
    /// clean up) under <paramref name="copyRoot"/>. A copy is removed only when nothing in it was written within
    /// <paramref name="maxAge"/> and none of its files is locked (open in another process). Best effort: never throws.
    /// </summary>
    /// <param name="copyRoot">The <c>build-verify</c> folder.</param>
    /// <param name="maxAge">Minimum age for removal (24 h in production).</param>
    /// <param name="nowUtc">The current UTC time.</param>
    /// <returns>The number of copies removed.</returns>
    internal static int CleanStaleCopies(string copyRoot, TimeSpan maxAge, DateTime nowUtc)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(copyRoot)) return 0;
            foreach (var dir in Directory.EnumerateDirectories(copyRoot, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
                    var newest = files.Select(File.GetLastWriteTimeUtc)
                        .Append(Directory.GetLastWriteTimeUtc(dir)).Max();
                    if (nowUtc - newest <= maxAge) continue;
                    if (files.Any(IsLocked)) continue;
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
                catch { /* in use or denied: leave it for the next connect */ }
            }
        }
        catch { /* best effort */ }
        return removed;
    }

    private static bool IsLocked(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>Removes stale offline copies under the server work directory; see <see cref="CleanStaleCopies"/>.</summary>
    /// <returns>The number of copies removed.</returns>
    internal static int CleanStaleCopies() => CleanStaleCopies(
        Path.Combine(LogixProjectSession.WorkingDirectoryBase, CopyDirectoryName), StaleCopyAge, DateTime.UtcNow);

    private static string Describe(Exception ex) => ex is TagServiceException tse ? tse.Error.Message : ex.Message;

    /// <summary>
    /// Reads the named routines back from the copy and checks every expected staged rung text is present.
    /// </summary>
    /// <returns>
    /// A reason when the copy lacks the edit (<c>Lost</c> true) or the read-back failed (<c>Lost</c> false), or <c>null</c>
    /// when the edit is present.
    /// </returns>
    private static async Task<(string Reason, bool Lost)?> FindMissingStagedRungsAsync(
        ILogixProjectHandle         copy,
        IReadOnlyCollection<string> expectedRungTexts,
        IReadOnlyCollection<string> routineXPaths,
        CancellationToken           ct)
    {
        if (routineXPaths.Count == 0)
            return ("The saved copy could not be checked for the staged edit: no staged routine is recorded. No build was run.", false);

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var xPath in routineXPaths)
        {
            var temp = Path.GetTempFileName();
            try
            {
                await copy.PartialExportToXmlFileAsync(xPath, temp, ct).ConfigureAwait(false);
                var content = await File.ReadAllTextAsync(temp, ct).ConfigureAwait(false);
                foreach (var rung in SafeXml.ParseDocument(content).Descendants("Rung"))
                    present.Add(ProposeRungEditHandler.NormalizeRungText(rung.Element("Text")?.Value));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return ($"The saved copy could not be read back to confirm it carries the staged edit ('{xPath}': {Describe(ex)}). No build was run.", false);
            }
            finally
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        }

        // The shared comparator, so an exported `TON(T,?,?)` carries the expected `TON(T,3000,0)`.
        var absent = expectedRungTexts.Where(t => !RungTextComparer.ContainsMatch(present, t)).ToList();
        return absent.Count == 0
            ? null
            : ($"The saved copy does not carry the staged edit ({absent.Count} staged rung(s) missing from " +
               $"{string.Join(", ", routineXPaths)}). No build was run.", true);
    }
}
#endif
