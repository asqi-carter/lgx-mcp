#if LOGIX_SDK
// ── BuildHandler — logix_build: run the SDK build check on the session's project ──
using System.Diagnostics;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;
using RequestedBuildTarget = RockwellAutomation.LogixDesigner.LogixProject.RequestedBuildTarget;

/// <summary>
/// Pure handler for the <c>logix_build</c> MCP tool. Builds an offline saved copy of the session's project
/// (<see cref="OfflineBuildCheck"/>; never the online session) through <c>BuildAsync</c> and reports the errors and warnings from the SDK's build log events, each with its location.
///
/// <para>
/// Read-only toward the controller: it never goes online, downloads or changes mode, and needs no live-writes
/// flag. It is gated on the capability table (<see cref="SdkCapabilityNames.BuildCheck"/>): on Logix Designer
/// before v37 it refuses with <c>SdkCapabilityUnavailable</c> and names Studio's Verify as the manual equivalent.
/// </para>
/// </summary>
public static class BuildHandler
{
    /// <summary>Note returned with a failed result when the log carried nothing and the error comes from the exception.</summary>
    internal const string NoLogEventsNote =
        "The SDK raised no build log events; the error text is the SDK's exception message.";

    /// <summary>Maps a normalized target name (<see cref="BuildTargetNames"/>) to the SDK's build target.</summary>
    /// <param name="target">One of <see cref="BuildTargetNames.All"/>.</param>
    /// <returns>The SDK value, or <c>null</c> when <paramref name="target"/> is not a known target.</returns>
    public static RequestedBuildTarget? MapTarget(string? target) => BuildTargetNames.Normalize(target) switch
    {
        BuildTargetNames.Default  => RequestedBuildTarget.DefaultTarget,
        BuildTargetNames.Physical => RequestedBuildTarget.PhysicalController,
        BuildTargetNames.Echo     => RequestedBuildTarget.EchoController,
        _                         => null,
    };

    /// <summary>
    /// Builds the session's project.
    /// </summary>
    /// <param name="target"><c>default</c> (when null), <c>physical</c> or <c>echo</c>.</param>
    /// <param name="session">An open session.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="BuildResult"/> (passed or failed), a <see cref="SdkCapabilityUnavailableError"/> when the build
    /// check is unavailable on this Designer, or a <see cref="BuildError"/> for a bad target or a build the SDK
    /// could not perform.
    /// </returns>
    public static async Task<object> ExecuteAsync(string? target, LogixProjectSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Dead-session short-circuit: a faulted SDK server is never called again.
        if (session.IsFaulted)
            return Faulted(session.FaultedError());

        using var operation = session.BeginOperation(RockwellErrorHints.OperationBuild);
        var result = await ExecuteCoreAsync(target, session, ct).ConfigureAwait(false);

        // A fatal during this build marks the session faulted: report that, not a build failure.
        return session.IsFaulted ? Faulted(session.FaultedError()) : result;
    }

    private static BuildError Faulted(TagServiceError e) =>
        new(e.Message, e.Hint, ErrorKinds.SdkServerFaulted, ErrorKinds.SessionFaulted);

    private static async Task<object> ExecuteCoreAsync(string? target, LogixProjectSession session, CancellationToken ct)
    {

        var name = BuildTargetNames.Normalize(target);
        var sdkTarget = MapTarget(target);
        if (name is null || sdkTarget is null)
            return new BuildError(
                $"Unknown build target '{target}'. Use one of: {string.Join(", ", BuildTargetNames.All)}.",
                "Omit target (or pass \"default\") to let the SDK choose.", ErrorKinds.InvalidValue);

        if (session.Capabilities.Require(SdkCapabilityNames.BuildCheck) is { } refusal)
            return refusal;

        // Never build inside the online session: save a copy, open it offline in a separate handle and build that.
        var collector = new Collector();
        var clock = Stopwatch.StartNew();
        OfflineBuildOutcome outcome;
        try
        {
            outcome = await OfflineBuildCheck.RunAsync(
                session, sdkTarget.Value,
                session.StagedRungBuffer.Values.SelectMany(v => v).Distinct(StringComparer.Ordinal).ToList(),
                session.StagedRoutineXPaths.ToList(),
                collector, ct).ConfigureAwait(false);
        }
        catch (TagServiceException ex) when (ex.Error.Kind == TagServiceErrorKind.SdkServerFaulted)
        {
            return Faulted(ex.Error);
        }
        finally { clock.Stop(); }

        if (outcome.UnavailableReason is { } reason)
            return new BuildError(
                reason,
                "buildVerify is unavailable: nothing was built. The online session was not changed; " +
                "Studio's Verify (Logic → Verify → Controller) is the manual build check.",
                ErrorKinds.BuildNotPerformed, BuildVerify: "unavailable");

        var failure = outcome.BuildFailure;

        var version = session.DesignerVersion?.ToString();

        if (failure is not null)
        {
            var text = ExceptionText(failure);
            var unsupported = DesignerVersionInfo.TryParseUnsupportedVersion(text);
            if (unsupported is not null)
            {
                session.RecordDesignerVersionFromSdk(unsupported);
                return session.Capabilities.Require(SdkCapabilityNames.BuildCheck)
                    ?? new SdkCapabilityUnavailableError(
                        $"'{SdkCapabilityNames.BuildCheck}' is not available on Logix Designer {unsupported}.",
                        SdkCapabilityNames.BuildCheck, unsupported.ToString(),
                        "Studio's Verify (Logic → Verify → Controller) is the manual build check.");
            }

            if (failure is TagServiceException)
                return new BuildError(
                    $"The session is not usable for a build: {failure.Message}",
                    "Call logix_connect (or logix_open_project) again and retry; nothing was changed.", ErrorKinds.NoActiveSession);

            if (failure is OperationNotPerformedException)
                return new BuildError(
                    $"The SDK could not perform the build: {failure.Message}",
                    "This is not a build failure; the build did not run. Retry; nothing was changed.", ErrorKinds.BuildNotPerformed);

            // The build ran and did not succeed.
            var errors = collector.Errors.ToList();
            string? note = null;
            if (errors.Count == 0)
            {
                errors.Add(BuildLogParser.Parse(failure.Message));
                note = NoLogEventsNote;
            }
            return new BuildResult(BuildResult.Failed, name, errors, collector.Warnings.ToList(),
                clock.ElapsedMilliseconds, version, note);
        }

        // BuildAsync returned without throwing: passed, unless the log reported errors anyway.
        var status = collector.Errors.Count == 0 ? BuildResult.Passed : BuildResult.Failed;
        string? passedNote = null;
        if (status == BuildResult.Passed && session.StagedRungBuffer.Values.Any(v => v.Count > 0))
        {
            // Logix does not check rungs staged in the session image.
            status     = BuildResult.PassedExcludingStaged;
            passedNote = BuildResult.PassedExcludingStagedNote;
        }
        return new BuildResult(status, name, collector.Errors.ToList(), collector.Warnings.ToList(),
            clock.ElapsedMilliseconds, version, passedNote);
    }

    /// <summary>The text of an exception and everything it wraps.</summary>
    private static string ExceptionText(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            sb.Append(e.Message).Append(' ');
            if (e is TagServiceException tse) sb.Append(tse.Error.Message).Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>Collects the build log events the SDK raises while <c>BuildAsync</c> runs.</summary>
    private sealed class Collector : IOperationEvent
    {
        private readonly object _gate = new();
        private readonly List<BuildIssue> _errors = new();
        private readonly List<BuildIssue> _warnings = new();

        public IReadOnlyList<BuildIssue> Errors   { get { lock (_gate) return _errors.ToArray(); } }
        public IReadOnlyList<BuildIssue> Warnings { get { lock (_gate) return _warnings.ToArray(); } }

        public void LogProgress(bool logProgressNew) { }
        public void Progress(string projectFile, int value) { }
        public void LogStatus(bool logStatusNew) { }
        public void LogError(bool logErrorNew) { }

        public void Status(string projectFile, string msg) => Add(BuildLogParser.ClassifyStatusChannel(msg), msg);

        public void Error(string projectFile, string msg) => Add(BuildLogParser.ClassifyErrorChannel(msg), msg);

        private void Add(BuildLogParser.Severity severity, string msg)
        {
            if (severity == BuildLogParser.Severity.None) return;
            var issue = BuildLogParser.Parse(msg);
            lock (_gate)
            {
                var list = severity == BuildLogParser.Severity.Warning ? _warnings : _errors;
                if (!list.Contains(issue)) list.Add(issue);
            }
        }
    }
}
#endif
