#if LOGIX_SDK
// ── ImportRejectReplay — explain an ImportAbortedNoChanges rejection by replaying the fragment offline ──
using System.Text.RegularExpressions;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;
using ILogixProjectHandle = RockwellAutomation.LogixDesigner.ILogixProject;
using PartialImportOption = RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption;
using RequestedBuildTarget = RockwellAutomation.LogixDesigner.LogixProject.RequestedBuildTarget;
using Task = System.Threading.Tasks.Task;

/// <summary>
/// What the offline replay of a rejected import produced.
/// </summary>
/// <param name="Reasons">The import and verify errors found on the copy (empty when unavailable or when the replay found none).</param>
/// <param name="UnavailableReason">Why the replay could not give an answer, or <c>null</c> when it ran to the end.</param>
internal sealed record ImportRejectReplayOutcome(IReadOnlyList<RejectReason> Reasons, string? UnavailableReason)
{
    /// <summary>An outcome for a replay that could not run or finish.</summary>
    public static ImportRejectReplayOutcome Unavailable(string reason) =>
        new(Array.Empty<RejectReason>(), reason);
}

/// <summary>
/// When the online importer rejects a fragment with <c>XMLSrv_E_IMPORT_ABORTED_NO_CHANGES</c> and no captured
/// message names a cause, this replays the same fragment as an OFFLINE import plus verify (<c>BuildAsync</c>)
/// against an offline copy of the session's project, and reports the errors as
/// <see cref="RejectReason"/>s.
///
/// <para>
/// The copy is built the way <c>logix_build</c> builds it (<see cref="OfflineBuildCheck"/>): save a copy of the
/// session project, open it offline in a separate handle, close that handle and delete the copy afterwards.
/// The live project is never imported into or built, and closing the copy does not close the session.
/// </para>
/// </summary>
internal static class ImportRejectReplay
{
    /// <summary>
    /// <c>rejectReasonSource</c> when the reasons are the controller publish's own: taken from the ImportLog the
    /// importer wrote. Such a reason carries <c>rungIndex</c> when its text names a rung, else 0 for a single-rung import, else <c>null</c>.
    /// </summary>
    internal const string SourceController = "controller";

    /// <summary><c>rejectReasonSource</c> when the reasons came from the offline replay.</summary>
    internal const string SourceOfflineReplay = "offline-replay";

    /// <summary><c>rejectReasonSource</c> when the replay could not give an answer.</summary>
    internal const string SourceUnavailable = "unavailable";

    /// <summary>
    /// Runs <paramref name="replay"/> within <paramref name="timeout"/>. A failure or timeout is reported as an
    /// unavailable outcome with the reason; it never throws (other than for the caller's own cancellation).
    /// </summary>
    /// <param name="replay">The replay to run; it receives a token that is cancelled on timeout.</param>
    /// <param name="timeout">The bound (60 s in production).</param>
    /// <param name="ct">The caller's cancellation token.</param>
    internal static async Task<ImportRejectReplayOutcome> RunBoundedAsync(
        Func<CancellationToken, Task<ImportRejectReplayOutcome>> replay,
        TimeSpan          timeout,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<ImportRejectReplayOutcome> run;
        try { run = Task.Run(() => replay(linked.Token), CancellationToken.None); }
        catch (Exception ex) { return ImportRejectReplayOutcome.Unavailable($"the replay could not start: {ex.Message}"); }

        // Bound the wait even when the SDK call ignores the token.
        var winner = await Task.WhenAny(run, Task.Delay(timeout, ct)).ConfigureAwait(false);
        if (winner != run)
        {
            ct.ThrowIfCancellationRequested();
            linked.Cancel();
            // The abandoned replay cleans up its copy when it ends; its outcome is no longer wanted.
            _ = run.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return ImportRejectReplayOutcome.Unavailable(
                $"the offline replay did not finish within {timeout.TotalSeconds:0.##} s and was abandoned");
        }

        try
        {
            return await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return ImportRejectReplayOutcome.Unavailable(
                ex is TagServiceException tse ? tse.Error.Message : ex.Message);
        }
    }

    /// <summary>
    /// Replays one rung import on an offline copy of the session project and verifies it.
    /// </summary>
    /// <param name="session">The open session. Only its save-as is used; it is not imported into or built.</param>
    /// <param name="routineXPath">The routine the online import targeted.</param>
    /// <param name="insertPosition">The insert position of the online import.</param>
    /// <param name="replaceCount">The replace count of the online import.</param>
    /// <param name="fragmentFile">The file holding the same fragment the online import was given.</param>
    /// <param name="ct">Cancellation token (cancelled when the replay's time bound is reached).</param>
    /// <param name="phases">Optional timer told when the save-as and the replay on the copy begin (verifyOnStage).</param>
    /// <returns>The reasons, or an unavailable outcome with the reason. Faults of the SDK server propagate.</returns>
    internal static async Task<ImportRejectReplayOutcome> RunAsync(
        LogixProjectSession session,
        string              routineXPath,
        uint                insertPosition,
        uint                replaceCount,
        string              fragmentFile,
        CancellationToken   ct,
        StagePhaseTimer?    phases = null)
    {
        var collector = new ReplayCollector();
        var reasons   = new List<RejectReason>();

        var outcome = await OfflineBuildCheck.RunAsync(
            session, RequestedBuildTarget.DefaultTarget,
            Array.Empty<string>(), Array.Empty<string>(),
            collector, ct,
            phases: phases,
            prepareCopy: async (copy, token) =>
            {
                // Offline import of the same fragment into the copy.
                copy.AddEventHandler(collector);
                try
                {
                    await copy.PartialImportRungsFromXmlFileAsync(
                        routineXPath, insertPosition, replaceCount, fragmentFile,
                        PartialImportOption.LeaveEdits, token).ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (session.TryMapServerFault(ex) is { } fault) throw fault;
                    // The import itself failed on the copy: its log and exception are the reasons; no build.
                    reasons.AddRange(collector.Reasons(fragmentFile));
                    if (reasons.Count == 0)
                        reasons.Add(ToReason(BuildLogParser.Parse(
                            ex is TagServiceException tse ? tse.Error.Message : ex.Message), FragmentRungCount(fragmentFile)));
                    return false;
                }
                finally
                {
                    try { copy.RemoveEventHandler(collector); } catch { /* best effort */ }
                }
            }).ConfigureAwait(false);

        if (outcome.UnavailableReason is { } reason)
            return ImportRejectReplayOutcome.Unavailable(reason);

        if (reasons.Count > 0)
            return new ImportRejectReplayOutcome(reasons, null);

        // The import went through on the copy: the verify (build) errors are the reasons.
        reasons.AddRange(collector.Reasons(fragmentFile));
        if (outcome.BuildFailure is { } failure && reasons.Count == 0)
        {
            if (session.TryMapServerFault(failure) is { } fault) throw fault;
            reasons.Add(ToReason(BuildLogParser.Parse(
                failure is TagServiceException tse ? tse.Error.Message : failure.Message), FragmentRungCount(fragmentFile)));
        }
        return new ImportRejectReplayOutcome(reasons, null);
    }

    /// <summary>
    /// The most rungs the per-rung replay (<see cref="LocateFailingRungsAsync"/>) will try. A batch with more rungs
    /// is not replayed rung by rung: its unnamed reasons stay unnamed.
    /// <para>
    /// Time cost: every rung is a full offline replay (save a copy of the session project, open it offline,
    /// import the one rung, build, close, delete the copy) and each is bounded by the replay timeout
    /// (60 s in production). Expect roughly the cost of one ordinary verifyOnStage replay per rung — seconds
    /// on a small project, tens of seconds on a large one — so a failing 10-rung batch can take about ten times
    /// as long as a single replay (worst case 10 x 60 s). Rungs are replayed in order and the search does not
    /// stop at the first failing rung (a batch can hold several bad rungs), so every rung up to the cap is tried.
    /// </para>
    /// </summary>
    internal const int MaxPerRungReplay = 10;

    /// <summary>
    /// Splits an import fragment into one fragment per <c>&lt;Rung&gt;</c>: each result is the whole fragment with
    /// every other rung removed (tag declarations and the routine envelope are kept).
    /// </summary>
    /// <param name="fragmentXml">The fragment XML.</param>
    /// <returns>One XML text per rung, in order; empty when the text is not XML or holds no rung.</returns>
    internal static IReadOnlyList<string> SplitRungs(string fragmentXml)
    {
        System.Xml.Linq.XDocument doc;
        try { doc = SafeXml.ParseDocument(fragmentXml); }
        catch { return Array.Empty<string>(); }

        var count = doc.Descendants("Rung").Count();
        var result = new List<string>(count);
        for (var keep = 0; keep < count; keep++)
        {
            var copy = new System.Xml.Linq.XDocument(doc);
            foreach (var rung in copy.Descendants("Rung").Where((_, i) => i != keep).ToList())
                rung.Remove();
            result.Add(ProposeRungEditHandler.SerializeWithDeclaration(copy));
        }
        return result;
    }

    /// <summary>
    /// Finds which rung of a multi-rung batch failed verify, by replaying one rung at a time (the offline copy is
    /// fresh for each). Only the first <see cref="MaxPerRungReplay"/> rungs are tried; see that constant for the
    /// time cost.
    /// </summary>
    /// <param name="fragmentXml">The whole batch fragment.</param>
    /// <param name="replayRung">Replays the single-rung fragment (rung index, its XML) and returns the outcome.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The reasons found for the individual rungs, each with <see cref="RejectReason.RungIndex"/> set to the rung's
    /// index in the batch; empty when the batch has one rung, more than the cap, or no single rung failed.
    /// </returns>
    internal static async Task<IReadOnlyList<RejectReason>> LocateFailingRungsAsync(
        string fragmentXml,
        Func<int, string, CancellationToken, Task<ImportRejectReplayOutcome>> replayRung,
        CancellationToken ct)
    {
        var rungs = SplitRungs(fragmentXml);
        if (rungs.Count < 2 || rungs.Count > MaxPerRungReplay)
            return Array.Empty<RejectReason>();

        var found = new List<RejectReason>();
        for (var i = 0; i < rungs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await replayRung(i, rungs[i], ct).ConfigureAwait(false);
            if (outcome.UnavailableReason is not null) continue;
            found.AddRange(outcome.Reasons.Select(r => r with { RungIndex = i }));
        }
        return found;
    }

    /// <summary>
    /// Gives a rung index to the reasons that name none: the reasons of the per-rung replay replace them.
    /// Reasons that already name a rung are kept as they are.
    /// </summary>
    /// <param name="reasons">The reasons of the whole-batch replay.</param>
    /// <param name="perRung">The reasons <see cref="LocateFailingRungsAsync"/> found.</param>
    /// <returns>The reasons with the unnamed ones located; unchanged when the per-rung replay found nothing.</returns>
    internal static IReadOnlyList<RejectReason> ApplyPerRungReasons(
        IReadOnlyList<RejectReason> reasons, IReadOnlyList<RejectReason> perRung)
    {
        if (perRung.Count == 0 || reasons.All(r => r.RungIndex is not null)) return reasons;
        return reasons.Where(r => r.RungIndex is not null).Concat(perRung).ToList();
    }

    private static int? FragmentRungCount(string fragmentFile)
    {
        try { return ImportLogParser.CountRungs(File.ReadAllText(fragmentFile)); } catch { return null; }
    }

    // A rooted path that names a temp folder or an import/project file, e.g. C:\Work\Temp\frag.L5X.
    private static readonly Regex TempPath = new(
        @"(?:[A-Za-z]:[\\/]|(?<![\w.])/)(?:[^\s""'<>|:*?\\/]+[\\/])+(?<file>[^\s""'<>|:*?\\/]+\.(?:l5x|l5k|acd|tmp))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HexToken = new(
        @"(?:\bHexCode\s*=\s*[""']?|\b0x|\(\s*)(?<h>[0-9a-f]{8})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Removes the replay's temp folder from a message: a path to an import or project file is cut down to the file
    /// name, and the literal temp folder path is dropped.
    /// </summary>
    /// <param name="message">The raw message.</param>
    /// <returns>The message without the temp path.</returns>
    internal static string StripTempPath(string message)
    {
        var text = TempPath.Replace(message, m => m.Groups["file"].Value);
        try
        {
            var tmp = Path.GetTempPath();
            if (tmp.Length > 1)
            {
                text = text.Replace(tmp, string.Empty, StringComparison.OrdinalIgnoreCase);
                var trimmed = tmp.TrimEnd('/', '\\');
                if (trimmed.Length > 1) text = text.Replace(trimmed + "/", string.Empty, StringComparison.OrdinalIgnoreCase)
                                                .Replace(trimmed + "\\", string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { /* best effort */ }
        return text;
    }

    /// <summary>
    /// Builds a structured reason from a raw importer or verify message: the mnemonic and hex code (from an
    /// <c>&lt;Exception&gt;</c> element or the text), the hint and rung index from <see cref="ImportLogParser"/>, and the
    /// text with the temp path stripped.
    /// </summary>
    /// <param name="message">The raw message.</param>
    /// <param name="fallbackRung">The rung to report when the message names none (e.g. from the build log).</param>
    /// <param name="fragmentRungCount">The fragment's rung count; exactly 1 makes an unnamed rung 0.</param>
    /// <returns>The structured reason.</returns>
    internal static RejectReason ToReason(string message, int? fallbackRung = null, int? fragmentRungCount = null)
    {
        var clean = ImportLogParser.FormatMessage(StripTempPath(message ?? string.Empty));
        var entry = ImportLogParser.Parse(new[] { clean }, Array.Empty<string>(), fragmentRungCount)
            .FirstOrDefault(e => e.Mnemonic is not null)
            ?? ImportLogParser.Parse(new[] { clean }, Array.Empty<string>(), fragmentRungCount).FirstOrDefault();

        var mnemonic = entry?.Mnemonic;
        var hex      = entry?.Hex;
        if (hex is null && HexToken.Match(clean) is { Success: true } hm) hex = hm.Groups["h"].Value.ToLowerInvariant();
        var text = entry is { Text.Length: > 0 } ? entry.Text : clean;
        return new RejectReason(
            entry?.RungIndex ?? fallbackRung,
            text,
            mnemonic,
            hex,
            ImportLogParser.HintFor(mnemonic, text) ?? entry?.Hint);
    }

    private static RejectReason ToReason(BuildIssue issue, int? fragmentRungCount = null) =>
        ToReason(issue.Message, issue.Rung ?? (fragmentRungCount == 1 ? 0 : null), fragmentRungCount);

    /// <summary>Collects error-looking messages the SDK raises on the copy while it imports and verifies.</summary>
    private sealed class ReplayCollector : IOperationEvent
    {
        private readonly object _gate = new();
        private readonly List<BuildIssue> _issues = new();

        private readonly List<string> _errorMessages  = new();
        private readonly List<string> _statusMessages = new();

        public IReadOnlyList<BuildIssue> Issues { get { lock (_gate) return _issues.ToArray(); } }

        /// <summary>
        /// The reasons the replay collected, built from the same <see cref="ImportLogParser"/> the failed online
        /// import uses (mnemonic, hex, rung, hint), then any error the build log named that the import log did not.
        /// </summary>
        /// <param name="fragmentFile">The replayed fragment; its rung count lets a single-rung log default to rung 0.</param>
        public IReadOnlyList<RejectReason> Reasons(string fragmentFile)
        {
            string[] errors, statuses;
            BuildIssue[] issues;
            lock (_gate) { errors = _errorMessages.ToArray(); statuses = _statusMessages.ToArray(); issues = _issues.ToArray(); }

            int? rungCount = null;
            try { rungCount = ImportLogParser.CountRungs(File.ReadAllText(fragmentFile)); } catch { /* best effort */ }

            var result = new List<RejectReason>(ProposeRungEditHandler.ImportLogRejectReasons(
                ImportLogParser.Parse(errors, statuses, rungCount)));
            foreach (var issue in issues)
            {
                var covered = result.Any(r => r.Text.Contains(issue.Message, StringComparison.Ordinal)
                                              || issue.Message.Contains(r.Text, StringComparison.Ordinal));
                if (!covered) result.Add(ToReason(issue));
            }
            return result;
        }

        public void LogProgress(bool logProgressNew) { }
        public void Progress(string projectFile, int value) { }
        public void LogStatus(bool logStatusNew) { }
        public void LogError(bool logErrorNew) { }

        public void Status(string projectFile, string msg)
        {
            lock (_gate) _statusMessages.Add(msg);
            Add(BuildLogParser.ClassifyStatusChannel(msg), msg);
        }

        public void Error(string projectFile, string msg)
        {
            lock (_gate) _errorMessages.Add(msg);
            Add(BuildLogParser.ClassifyErrorChannel(msg), msg);
        }

        private void Add(BuildLogParser.Severity severity, string msg)
        {
            if (severity != BuildLogParser.Severity.Error) return;
            var issue = BuildLogParser.Parse(msg);
            lock (_gate) { if (!_issues.Contains(issue)) _issues.Add(issue); }
        }
    }
}
#endif
