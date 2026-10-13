#if LOGIX_SDK
// ── Session1ScriptRunner — invokes bench/studio-edits.ps1 on the interactive
//    desktop, directly or via the logix-mcp-s1-<verb> scheduled-task pattern
//    ─────────────────────────────────────────────
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

// Alias System.Threading.Tasks.Task to avoid ambiguity with L5Sharp.Core.Task
// when both namespaces are in scope.
using Task = System.Threading.Tasks.Task;

/// <summary>
/// The Studio window a lifecycle call must act on: the controller name and the
/// project file name taken from the SDK session. The driver selects only a Logix Designer
/// window titled <c>&lt;Controller&gt; in &lt;ProjectFile&gt;.ACD</c>, or, when no project file
/// is known (an unsaved upload), titled just <c>&lt;Controller&gt;</c>.
/// </summary>
/// <param name="Controller">Controller name; <c>null</c>/empty when unknown (any controller matches).</param>
/// <param name="ProjectFile">
/// Project file name, with or without the <c>.ACD</c> extension; <c>null</c>/empty when unknown
/// (an upload session has no Studio-side file name), in which case any file matches, as does a title with no file name.
/// </param>
public sealed record StudioWindowTarget(string? Controller, string? ProjectFile);

/// <summary>
/// Production runner that invokes <c>bench/studio-edits.ps1</c> on the
/// interactive desktop, in one of two launch modes chosen by
/// <see cref="DecideLaunchMode"/> from the session id of this process:
///
/// <list type="bullet">
///   <item><b>Direct</b> (current session id is not 0 — the normal case for a
///     stdio MCP server, which is a child of the user's MCP client and so
///     already runs in the same interactive session as Studio 5000): the
///     driver is launched as a child <c>powershell.exe</c> and no scheduled
///     task is registered. A direct launch is refused with
///     <c>studio_not_in_this_session</c> when no Studio process runs in this
///     session, and with <c>studio_elevated_driver_not</c> when Studio is
///     elevated and this process is not (Windows UIPI blocks the driver).</item>
///   <item><b>Scheduled task</b> (current session id is 0 — e.g. SSH or a
///     service): this process cannot touch the desktop itself, so it
///     registers and starts a <c>logix-mcp-s1-&lt;verb&gt;</c> task with
///     <c>Register-ScheduledTask</c> / <c>Start-ScheduledTask</c> and an
///     <c>Interactive</c> principal, so the script runs on the desktop
///     (session 1+). The task run level is <c>Highest</c> when this process's
///     token is elevated and <c>Limited</c> otherwise, because a non-elevated
///     process cannot register a task that runs at Highest.</item>
/// </list>
///
/// Every success and failure payload reports the mode used as
/// <c>launchMode</c> (<c>"direct"</c> or <c>"scheduled_task"</c>), plus
/// <c>runLevel</c> in task mode.
///
/// <para>
/// In scheduled-task mode, every task name this runner registers, starts,
/// queries, or deletes is built from <see cref="BuildTaskName"/> and therefore always carries the
/// <see cref="TaskNamePrefix"/> (<c>logix-mcp-s1-</c>) prefix. This is
/// deliberate: an operator's bench can have its own unrelated pre-existing
/// scheduled tasks, and this runner must never register,
/// start, or overwrite those — only tasks it namespaced itself.
/// </para>
///
/// <para>
/// This is genuinely Windows-only desktop automation and cannot be exercised
/// from a non-Windows CI lane — <see cref="RunAsync"/> reports a clear
/// <c>not_windows</c> failure rather than attempting it when
/// <see cref="OperatingSystem.IsWindows"/> is <c>false</c>.
/// <see cref="BuildCommand"/> and <see cref="ParseResultJson"/> are pure
/// functions and are unit-tested directly, independent of the platform.
/// </para>
/// </summary>
public static class Session1ScriptRunner
{
    /// <summary>
    /// Prefix shared by every scheduled task this runner registers, starts,
    /// queries, or deletes to run the script in session 1+. Concrete task
    /// names are always <c>{TaskNamePrefix}{verb}</c> — see
    /// <see cref="BuildTaskName"/> — so this runner can never collide with,
    /// register over, or otherwise touch an unrelated pre-existing scheduled
    /// task of an operator's own.
    /// </summary>
    internal const string TaskNamePrefix = "logix-mcp-s1-";

    /// <summary>
    /// Builds the scheduled task name for <paramref name="verb"/>, e.g.
    /// <c>logix-mcp-s1-cancel</c>. Always prefixed with
    /// <see cref="TaskNamePrefix"/> — see that member's remarks for why this
    /// matters.
    /// </summary>
    /// <param name="verb">One of <c>status</c>, <c>cancel</c>, <c>accept</c>, <c>test</c>, <c>untest</c>, <c>assemble</c>.</param>
    internal static string BuildTaskName(string verb) => TaskNamePrefix + verb;

    /// <summary>
    /// Quotes <paramref name="s"/> as a PowerShell single-quoted string literal. PowerShell treats
    /// the typographic quotes U+2018, U+2019, U+201A and U+201B exactly like an ASCII apostrophe,
    /// so every one of them is doubled, not just <c>'</c>; otherwise a controller or program name
    /// containing one could close the literal and inject script text.
    /// </summary>
    internal static string PsQuote(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2).Append('\'');
        foreach (var c in s)
        {
            sb.Append(c);
            if (c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B')
                sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    /// <summary>
    /// Builds the <c>-ExpectedController</c> / <c>-ExpectedProject</c> script-parameter text for
    /// <paramref name="target"/> (each only when non-empty), with a leading space. These are
    /// parameters of <c>bench/studio-edits.ps1</c>, bound by PowerShell to its <c>param()</c>
    /// block as quoted values via <see cref="PsQuote"/> — never concatenated into command text.
    /// </summary>
    internal static string BuildTargetArguments(StudioWindowTarget? target)
    {
        var text = string.Empty;
        if (!string.IsNullOrWhiteSpace(target?.Controller))
            text += $" -ExpectedController {PsQuote(target!.Controller!)}";
        if (!string.IsNullOrWhiteSpace(target?.ProjectFile))
            text += $" -ExpectedProject {PsQuote(target!.ProjectFile!)}";
        return text;
    }

    /// <summary>
    /// Builds the <c>-ConfirmTimeoutSeconds</c> script-parameter text (with a leading space), or
    /// the empty string when <paramref name="confirmTimeoutSeconds"/> is <c>null</c> (the driver
    /// then uses its own default of 10). Always a bare integer, never user text.
    /// </summary>
    internal static string BuildConfirmTimeoutArgument(int? confirmTimeoutSeconds) =>
        confirmTimeoutSeconds is { } n
            ? $" -ConfirmTimeoutSeconds {n.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : string.Empty;

    /// <summary>
    /// The single scheduled-task name shared by every Studio-lifecycle verb
    /// (<c>cancel</c> | <c>accept</c> | <c>test</c> | <c>untest</c> | <c>assemble</c>) that goes through
    /// <c>StudioLifecycleHandler</c>. Passed as the
    /// <c>taskName</c> override to <see cref="RunAsync"/>; when no override is
    /// given the runner keeps its per-verb <see cref="BuildTaskName"/> name.
    /// </summary>
    internal const string EditsTaskName = TaskNamePrefix + "edits";

    /// <summary>
    /// The pre-0098 per-verb task name (<c>logix-mcp-s1-cancel</c>). Whenever a
    /// run registers <see cref="EditsTaskName"/> it also unregisters any task of
    /// this name left behind by an older build (see <see cref="BuildCommand"/>).
    /// </summary>
    internal const string LegacyCancelTaskName = TaskNamePrefix + "cancel";

    /// <summary>
    /// Prefix that identifies the single result line
    /// <c>bench/studio-edits.ps1</c> prints on every code path (see that
    /// script's <c>Write-ResultJson</c> function).
    /// </summary>
    internal const string ResultJsonPrefix = "RESULT_JSON: ";

    /// <summary>
    /// Historical open-window budget. The driver no longer opens
    /// Studio, so this is now only headroom. Used, together
    /// with <see cref="GestureBudgetSeconds"/>, to compute how long
    /// <see cref="RunAsync"/> polls the scheduled task's state before giving
    /// up — see <see cref="DefaultPollTimeoutSeconds"/>.
    /// </summary>
    internal const int DefaultOpenTimeoutSeconds = 120;

    /// <summary>
    /// Extra time budgeted, beyond <see cref="DefaultOpenTimeoutSeconds"/>,
    /// for the driver's own posted-command and dialog waits (each
    /// individually shorter, see <c>bench/studio-edits.ps1</c>'s
    /// <c>ConfirmTimeoutSeconds</c> / <c>ConfirmDialogTimeoutSeconds</c>
    /// parameters) so the poll timeout
    /// does not race the driver's own internal timeouts.
    /// </summary>
    internal const int GestureBudgetSeconds = 60;

    /// <summary>
    /// Default total time (in seconds) <see cref="RunAsync"/> polls the
    /// scheduled task's state before giving up and reporting a timeout —
    /// <see cref="DefaultOpenTimeoutSeconds"/> + <see cref="GestureBudgetSeconds"/>
    /// = 180 s.
    /// </summary>
    internal const int DefaultPollTimeoutSeconds = DefaultOpenTimeoutSeconds + GestureBudgetSeconds;

    /// <summary>
    /// Maximum number of bytes of the redirect log file's tail surfaced in
    /// the error detail when no <c>RESULT_JSON:</c> line is found — enough
    /// to be useful without flooding a caller with an entire log.
    /// </summary>
    internal const int LogTailMaxBytes = 2048;

    /// <summary>
    /// Returns the only path the runner will execute:
    /// <c>&lt;baseDirectory&gt;/bench/studio-edits.ps1</c>, beside the running
    /// binary (how the <c>publish-win</c>/<c>publish-linux</c> Makefile targets
    /// ship it). There is deliberately NO parent-directory
    /// or <c>*.sln</c> walk: a script found by climbing out of the install
    /// directory could be planted by anyone with write access to a parent
    /// folder and would then be launched on the interactive desktop.
    /// </summary>
    /// <param name="baseDirectory">
    /// Directory to resolve from. Defaults to
    /// <see cref="AppContext.BaseDirectory"/> when <c>null</c>; overridable
    /// for testing.
    /// </param>
    internal static string ExpectedScriptPath(string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        return Path.Combine(baseDirectory, "bench", "studio-edits.ps1");
    }

    /// <summary>
    /// Single-element list holding <see cref="ExpectedScriptPath"/>; kept as
    /// a list so <see cref="BuildNotFoundMessage"/> callers are unchanged.
    /// </summary>
    internal static IReadOnlyList<string> CandidateScriptPaths(string? baseDirectory = null)
        => new[] { ExpectedScriptPath(baseDirectory) };

    /// <summary>
    /// Returns the script beside the running binary, or <c>null</c> when it
    /// does not exist on disk. Never looks anywhere else.
    /// </summary>
    internal static string? FindDefaultScriptPath(string? baseDirectory = null)
    {
        var path = ExpectedScriptPath(baseDirectory);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Builds the "not found" error message naming the exact path the script
    /// was expected at, so an operator knows where to put it.
    /// </summary>
    internal static string BuildNotFoundMessage(IReadOnlyList<string> pathsTried)
    {
        var quoted = string.Join(", ", pathsTried.Select(p => $"'{p}'"));
        return $"studio-edits.ps1 not found. Expected it beside the binary at: {quoted}. " +
               "The runner only uses that path and does not search parent directories.";
    }

    /// <summary>
    /// Builds the action-argument string registered as the scheduled task's
    /// <c>-Argument</c> value — the literal Win32 command line Task
    /// Scheduler hands to <c>CreateProcess</c> for <c>powershell.exe</c> when
    /// the task actually runs.
    /// </summary>
    /// <remarks>
    /// This string is <b>not</b> parsed by PowerShell's own tokenizer at run
    /// time — Task Scheduler passes it straight through to
    /// <c>CommandLineToArgvW</c>, which only recognizes double quotes as a
    /// quoting character. Wrapping <paramref name="scriptPath"/>,
    /// <paramref name="verb"/>, or <paramref name="outputFile"/> in single
    /// quotes (valid PowerShell string-literal syntax, but meaningless to
    /// <c>CommandLineToArgvW</c>) causes any path containing a space to be
    /// split into multiple arguments, producing the "path format not
    /// supported" failure (<c>LastTaskResult 0xFFFD0000</c>). Double quotes
    /// must be used here instead; embedded double quotes are escaped as
    /// <c>\"</c> per <c>CommandLineToArgvW</c> escaping rules.
    /// </remarks>
    /// <param name="scriptPath">Absolute path to <c>bench/studio-edits.ps1</c>.</param>
    /// <param name="verb">One of <c>status</c>, <c>cancel</c>, <c>accept</c>, <c>test</c>, <c>untest</c>, <c>assemble</c>.</param>
    /// <param name="outputFile">
    /// Absolute path the scheduled task's combined output is redirected to.
    /// </param>
    /// <param name="confirmTimeoutSeconds">
    /// When set, passed to the driver as <c>-ConfirmTimeoutSeconds</c> (see <see cref="StudioConfirmWait"/>).
    /// </param>
    internal static string BuildActionArgument(
        string scriptPath, string verb, string outputFile, string? program = null,
        StudioWindowTarget? target = null, int? confirmTimeoutSeconds = null)
    {
        // Measured: with -File, everything after the
        // script path is passed to the SCRIPT as arguments, so "*> log" is not
        // a redirect — the driver fails with "A positional parameter cannot be
        // found that accepts argument '*'", exit 1, and the log stays empty.
        // The redirect must be parsed by PowerShell itself, i.e. inside a
        // -Command string. The OUTER double quotes are the only quoting
        // CommandLineToArgvW sees (one argument); INSIDE them PowerShell's own
        // tokenizer runs, so single-quoted literals are correct there and an
        // embedded single quote is doubled. Proven form (session 0, log written):
        //   powershell.exe -NoProfile -ExecutionPolicy Bypass
        //     -Command "& 'C:\LogixMcp\bench\studio-edits.ps1' -Verb cancel *> 'C:\...\log'"
        static string SQ(string s) => PsQuote(s);
        // The driver validates the confirmation dialog against -Program
        // and presses No on a mismatch, so the program the tool was called with
        // must reach the script. Omitted when null/empty (status verb, old callers).
        string programArg = string.IsNullOrEmpty(program) ? "" : $" -Program {SQ(program)}";
        // The expected controller and project file name go the same way, as script
        // parameters, so the driver can refuse a window that is not the session's project.
        string inner = $"& {SQ(scriptPath)} -Verb {SQ(verb)}{programArg}{BuildTargetArguments(target)}{BuildConfirmTimeoutArgument(confirmTimeoutSeconds)} *> {SQ(outputFile)}";
        // Measured: without -WindowStyle Hidden the task's
        // elevated console opens on top of Studio in session 1 and the driver's
        // menu clicks land on the console ("Select Administrator: … powershell.exe",
        // reason cancel_confirmation_dialog_not_found). With it, the same action
        // cancelled a pending rung end to end (RESULT_JSON ok, census none).
        return "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"" + inner.Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// Builds the single-line PowerShell <c>-Command</c> argument that
    /// (re)registers the <see cref="BuildTaskName"/> scheduled task to run
    /// <c>powershell.exe -File &lt;scriptPath&gt; -Verb &lt;verb&gt;</c>
    /// under an <c>Interactive</c> principal (so it runs in session 1+) and
    /// starts it, redirecting its combined output to
    /// <paramref name="outputFile"/> so the caller can read back the
    /// <c>RESULT_JSON:</c> line afterward — a scheduled task has no stdout
    /// stream the launching process can read directly.
    /// </summary>
    /// <remarks>
    /// This command deliberately does <b>not</b> block waiting for the task
    /// to leave the <c>Running</c> state — <see cref="RunAsync"/> (via
    /// <see cref="AwaitResultFromRedirectFileAsync"/>) polls the task's state
    /// itself, from the C# side, so it can enforce a timeout
    /// (<see cref="DefaultPollTimeoutSeconds"/>) that an unbounded in-process
    /// PowerShell <c>while</c> loop could not.
    /// </remarks>
    /// <param name="scriptPath">Absolute path to <c>bench/studio-edits.ps1</c>.</param>
    /// <param name="verb">One of <c>status</c>, <c>cancel</c>, <c>accept</c>, <c>test</c>, <c>untest</c>, <c>assemble</c>.</param>
    /// <param name="outputFile">
    /// Absolute path the scheduled task's combined output is redirected to.
    /// </param>
    /// <param name="elevated">
    /// Whether the current process token is elevated. The registered task's
    /// principal uses <c>-RunLevel Highest</c> only when true, otherwise
    /// <c>Limited</c> (a non-elevated caller cannot, and must not try to,
    /// register a Highest task).
    /// </param>
    /// <param name="taskName">
    /// Optional task-name override (e.g. <see cref="EditsTaskName"/>). When
    /// <c>null</c> the per-verb <see cref="BuildTaskName"/> name is used and the
    /// command is unchanged. When it differs from <see cref="LegacyCancelTaskName"/>
    /// the command first unregisters any leftover <see cref="LegacyCancelTaskName"/>
    /// task (best-effort, errors ignored).
    /// </param>
    internal static string BuildCommand(
        string scriptPath, string verb, string outputFile, bool elevated = false,
        string? taskName = null, string? program = null, StudioWindowTarget? target = null,
        int? confirmTimeoutSeconds = null)
    {
        // Single quotes around the outer PowerShell scaffolding (task name,
        // etc.); embedded single quotes are doubled per PowerShell quoting
        // rules. This scaffolding is ordinary PowerShell script text parsed
        // by the launching -Command powershell.exe process, not the
        // scheduled task's own argv — see BuildActionArgument for why the
        // inner action argument must use double quotes instead.
        static string Q(string s) => PsQuote(s);

        var legacyCleanup = taskName is not null &&
            !string.Equals(taskName, LegacyCancelTaskName, StringComparison.Ordinal);
        taskName ??= BuildTaskName(verb);

        var innerArgument = BuildActionArgument(scriptPath, verb, outputFile, program, target, confirmTimeoutSeconds);

        var steps = new List<string>();
        if (legacyCleanup)
            steps.Add(
                $"Unregister-ScheduledTask -TaskName {Q(LegacyCancelTaskName)} " +
                "-Confirm:$false -ErrorAction SilentlyContinue");
        steps.AddRange(new[]
        {
            $"$Action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument {Q(innerArgument)}",
            $"$Principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel {(elevated ? "Highest" : "Limited")}",
            $"Register-ScheduledTask -TaskName {Q(taskName)} -Action $Action -Principal $Principal -Force | Out-Null",
            $"Start-ScheduledTask -TaskName {Q(taskName)}",
        });
        return string.Join("; ", steps);
    }

    /// <summary>
    /// Builds the single-line PowerShell <c>-Command</c> argument that
    /// queries the <see cref="BuildTaskName"/> scheduled task's current
    /// <c>State</c> (e.g. <c>Running</c>, <c>Ready</c>) — used by
    /// <see cref="RunAsync"/>'s state-query poller. Kept separate from
    /// <see cref="BuildCommand"/> so the launcher process (register + start)
    /// and the polling process (repeated state queries) are independent,
    /// short-lived invocations rather than one long-blocked process.
    /// </summary>
    /// <param name="taskName">
    /// The scheduled task's name, as returned by <see cref="BuildTaskName"/>.
    /// </param>
    internal static string BuildQueryStateCommand(string taskName)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        return $"(Get-ScheduledTask -TaskName {Q(taskName)}).State";
    }

    /// <summary>
    /// Builds the single-line PowerShell <c>-Command</c> argument that
    /// queries the <see cref="BuildTaskName"/> scheduled task's
    /// <c>LastTaskResult</c> (the Win32 exit/status code Task Scheduler
    /// recorded for the most recent run — e.g. <c>0xFFFD0000</c> for the
    /// "path format not supported" quoting bug this runner guards against,
    /// see <see cref="BuildActionArgument"/>'s remarks). The value is cast to
    /// <c>uint32</c> explicitly so PowerShell always prints its unsigned
    /// decimal form rather than a signed <c>Int32</c> that would print
    /// negative for results at or above <c>0x80000000</c>.
    /// </summary>
    /// <param name="taskName">
    /// The scheduled task's name, as returned by <see cref="BuildTaskName"/>.
    /// </param>
    internal static string BuildQueryLastTaskResultCommand(string taskName)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        return $"[uint32](Get-ScheduledTaskInfo -TaskName {Q(taskName)}).LastTaskResult";
    }

    /// <summary>
    /// Formats a scheduled task's <c>LastTaskResult</c> as both hex
    /// (<c>0xXXXXXXXX</c>, zero-padded to 8 digits) and decimal, so a failure
    /// or success payload can surface both representations side by side —
    /// hex because that is how Task Scheduler documentation and error codes
    /// (e.g. <c>0xFFFD0000</c>) are conventionally written, decimal because
    /// it round-trips unambiguously through JSON.
    /// </summary>
    /// <param name="value">
    /// The raw <c>LastTaskResult</c> value, as an unsigned 32-bit integer.
    /// </param>
    internal static (string Hex, long Decimal) FormatLastTaskResult(uint value) =>
        ($"0x{value:X8}", value);

    /// <summary>
    /// Parses the last <c>RESULT_JSON: {...}</c> line found in
    /// <paramref name="capturedOutput"/> (the script's own contract
    /// guarantees exactly one; the last line is used defensively in case
    /// diagnostic <c>Write-Host</c> chatter is interleaved) into a
    /// <see cref="Session1ScriptResult"/>.
    /// </summary>
    /// <param name="capturedOutput">
    /// Combined stdout/stderr text captured from the session-1 script run.
    /// </param>
    /// <returns>
    /// The parsed <see cref="Session1ScriptResult"/>; <c>null</c> when no
    /// <c>RESULT_JSON:</c> line was found at all. When a line was found but
    /// could not be parsed as valid JSON with the expected shape, a non-null
    /// result is still returned with <see cref="Session1ScriptResult.Ok"/>
    /// <c>false</c> and <see cref="Session1ScriptResult.Malformed"/>
    /// <c>true</c> — a driver defect distinct from an absent line — carrying
    /// the raw line verbatim so the caller can inspect exactly what the
    /// driver emitted.
    /// </returns>
    internal static Session1ScriptResult? ParseResultJson(string capturedOutput)
    {
        string? line = null;
        foreach (var candidate in capturedOutput.Split('\n'))
        {
            var trimmed = candidate.TrimEnd('\r').Trim();
            if (trimmed.StartsWith(ResultJsonPrefix, StringComparison.Ordinal))
                line = trimmed;
        }

        if (line is null)
            return null;

        var jsonText = line[ResultJsonPrefix.Length..];

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            if (!root.TryGetProperty("ok", out var okProp) ||
                (okProp.ValueKind != JsonValueKind.True && okProp.ValueKind != JsonValueKind.False))
                throw new JsonException(
                    "RESULT_JSON payload is missing a boolean 'ok' property.");

            var ok = okProp.GetBoolean();
            var reason = root.TryGetProperty("reason", out var reasonProp)
                ? reasonProp.GetString() ?? string.Empty
                : string.Empty;

            var screenshots = new List<string>();
            if (root.TryGetProperty("screenshots", out var shotsProp) &&
                shotsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in shotsProp.EnumerateArray())
                    if (item.GetString() is { } s)
                        screenshots.Add(s);
            }

            var gestureTier =
                root.TryGetProperty("gestureTier", out var tierProp) &&
                tierProp.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(tierProp.GetString())
                    ? tierProp.GetString()
                    : null;

            var dialogText =
                root.TryGetProperty("dialogText", out var dialogTextProp) &&
                dialogTextProp.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(dialogTextProp.GetString())
                    ? dialogTextProp.GetString()
                    : null;

            IReadOnlyList<string>? dialogWarnings = null;
            if (root.TryGetProperty("dialogWarnings", out var warnProp))
            {
                var warnings = new List<string>();
                if (warnProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in warnProp.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } w)
                            warnings.Add(w);
                }
                else if (warnProp.ValueKind == JsonValueKind.String &&
                         warnProp.GetString() is { Length: > 0 } single)
                {
                    warnings.Add(single); // PowerShell collapses a one-element array to a scalar
                }
                dialogWarnings = warnings;
            }

            return new Session1ScriptResult(
                ok, reason, screenshots,
                RawResultJsonLine: line,
                ParsedResultJson:  root.Clone(),
                Malformed:         false,
                GestureTier:       gestureTier,
                DialogText:        dialogText,
                DialogWarnings:    dialogWarnings);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new Session1ScriptResult(
                Ok:                false,
                Reason:            $"RESULT_JSON line was found but could not be parsed as " +
                                   $"valid JSON with the expected shape: {ex.Message}",
                Screenshots:       Array.Empty<string>(),
                RawResultJsonLine: line,
                ParsedResultJson:  null,
                Malformed:         true);
        }
    }

    /// <summary>
    /// Returns the last <paramref name="maxBytes"/> bytes (UTF-8) of
    /// <paramref name="content"/>, or all of it when it is already no longer
    /// than that. Used to surface a bounded, useful excerpt of the redirect
    /// log file in error details instead of either nothing or an unbounded
    /// dump — see <see cref="AwaitResultFromRedirectFileAsync"/>.
    /// </summary>
    /// <param name="content">Text to take the tail of (e.g. a log file's contents).</param>
    /// <param name="maxBytes">
    /// Maximum number of UTF-8 bytes to return. Defaults to
    /// <see cref="LogTailMaxBytes"/>.
    /// </param>
    internal static string ReadTail(string content, int maxBytes = LogTailMaxBytes)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        if (bytes.Length <= maxBytes)
            return content;

        var tailBytes = bytes[^maxBytes..];
        return System.Text.Encoding.UTF8.GetString(tailBytes);
    }

    /// <summary>
    /// Polls <paramref name="getTaskStateAsync"/> until it reports a state
    /// other than <c>Running</c> (or <paramref name="timeout"/> elapses),
    /// then reads <paramref name="outputFile"/> — the scheduled task's
    /// <c>*&gt;</c> redirect log — for the last <c>RESULT_JSON:</c> line via
    /// <see cref="ParseResultJson"/>.
    /// </summary>
    /// <remarks>
    /// The launching process's own stdout is never consulted here: a
    /// scheduled task has no stdout stream the launcher can read, and the
    /// driver's output goes exclusively to <paramref name="outputFile"/>.
    /// This method is a pure/injectable
    /// dependency of <see cref="RunAsync"/> — <paramref name="getTaskStateAsync"/>
    /// takes the place of a real <c>Get-ScheduledTask</c> query so the
    /// polling and log-tail behavior can be unit-tested without a real
    /// Windows scheduled task.
    /// </remarks>
    /// <param name="getTaskStateAsync">
    /// Queries the scheduled task's current <c>State</c> (e.g. <c>Running</c>,
    /// <c>Ready</c>).
    /// </param>
    /// <param name="outputFile">
    /// Absolute path to the <c>*&gt;</c> redirect log file the scheduled
    /// task's combined output was written to.
    /// </param>
    /// <param name="taskName">
    /// The scheduled task's name, used only to build a clear error message.
    /// </param>
    /// <param name="timeout">
    /// Maximum time to wait for the task to leave the <c>Running</c> state.
    /// — see <see cref="DefaultPollTimeoutSeconds"/> for the 180 s default.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="pollInterval">
    /// Delay between state queries. Defaults to 500 ms; overridable so tests
    /// can run the loop near-instantly.
    /// </param>
    /// <param name="getLastTaskResultAsync">
    /// Optional query for the scheduled task's <c>LastTaskResult</c> (see
    /// <see cref="BuildQueryLastTaskResultCommand"/>), consulted once after
    /// the task leaves the <c>Running</c> state (or the poll times out) so
    /// its value — hex and decimal — can be attached to the returned
    /// <see cref="Session1ScriptResult"/> regardless of whether a
    /// <c>RESULT_JSON:</c> line was found. Failures
    /// querying it (including a <c>null</c> delegate, the default) are
    /// swallowed and simply leave
    /// <see cref="Session1ScriptResult.LastTaskResultHex"/>/
    /// <see cref="Session1ScriptResult.LastTaskResultDecimal"/> unset — this
    /// is a best-effort diagnostic, not a requirement for the method to
    /// succeed.
    /// </param>
    /// <returns>
    /// The parsed <see cref="Session1ScriptResult"/> when a
    /// <c>RESULT_JSON:</c> line was found in the redirect log; otherwise a
    /// failure result whose <see cref="Session1ScriptResult.Reason"/>
    /// includes up to <see cref="LogTailMaxBytes"/> bytes of the log file's
    /// tail (via <see cref="ReadTail"/>) so the caller can see what the
    /// driver actually wrote. Either way, <see cref="Session1ScriptResult.TaskName"/>
    /// is always set to <paramref name="taskName"/>, and
    /// <see cref="Session1ScriptResult.LastTaskResultHex"/>/
    /// <see cref="Session1ScriptResult.LastTaskResultDecimal"/> are set
    /// whenever <paramref name="getLastTaskResultAsync"/> successfully
    /// returns a value.
    /// </returns>
    internal static async Task<Session1ScriptResult> AwaitResultFromRedirectFileAsync(
        Func<CancellationToken, Task<string>>    getTaskStateAsync,
        string                                   outputFile,
        string                                   taskName,
        TimeSpan                                 timeout,
        CancellationToken                        ct                     = default,
        TimeSpan?                                pollInterval           = null,
        Func<CancellationToken, Task<uint?>>?    getLastTaskResultAsync = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        var deadline = DateTime.UtcNow + timeout;
        var timedOut = false;

        while (true)
        {
            var state = await getTaskStateAsync(ct).ConfigureAwait(false);
            if (!string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
                break;

            if (DateTime.UtcNow >= deadline)
            {
                timedOut = true;
                break;
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }

        var capturedOutput = File.Exists(outputFile)
            ? await File.ReadAllTextAsync(outputFile, ct).ConfigureAwait(false)
            : string.Empty;

        // Best-effort: the task has left the Running state (or we gave up
        // waiting), so its LastTaskResult -- the Win32 exit/status code Task
        // Scheduler recorded for the run -- is now meaningful. Surfaced on
        // both the success and failure path below.
        uint? lastTaskResult = null;
        if (getLastTaskResultAsync is not null)
        {
            try
            {
                lastTaskResult = await getLastTaskResultAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // best-effort diagnostic only -- never fail the call over it.
            }
        }

        string? lastTaskResultHex     = null;
        long?   lastTaskResultDecimal = null;
        if (lastTaskResult is { } rawResult)
        {
            var (hex, dec) = FormatLastTaskResult(rawResult);
            lastTaskResultHex     = hex;
            lastTaskResultDecimal = dec;
        }

        var result = ParseResultJson(capturedOutput);
        if (result is not null)
            return result with
            {
                TaskName              = taskName,
                LastTaskResultHex     = lastTaskResultHex,
                LastTaskResultDecimal = lastTaskResultDecimal,
            };

        // No RESULT_JSON line, but Task Scheduler's LastTaskResult is the driver's exit code:
        // 20/21 are the window-targeting refusals.
        if (lastTaskResult is { } exitCode && exitCode <= int.MaxValue &&
            StudioPreLaunch.WindowKindOfExitCode((int)exitCode) is { } windowKind)
            return new Session1ScriptResult(
                false,
                windowKind == StudioKinds.StudioWindowNotFoundKind
                    ? StudioPreLaunch.StudioWindowNotFoundCode
                    : StudioPreLaunch.StudioWindowAmbiguousCode,
                Array.Empty<string>(),
                TaskName:              taskName,
                LastTaskResultHex:     lastTaskResultHex,
                LastTaskResultDecimal: lastTaskResultDecimal,
                Kind:                  windowKind);

        var tail = ReadTail(capturedOutput);
        var timeoutNote = timedOut
            ? $" (gave up after {timeout.TotalSeconds:0}s still waiting for the task to leave " +
              "the Running state)"
            : string.Empty;
        var lastTaskResultNote = lastTaskResultHex is not null
            ? $" LastTaskResult: {lastTaskResultHex} ({lastTaskResultDecimal})."
            : string.Empty;

        return new Session1ScriptResult(
            false,
            $"{taskName} task completed but no RESULT_JSON: line was found in its redirect " +
            $"log file '{outputFile}'{timeoutNote}. Launcher stdout is never consulted for a " +
            $"scheduled task -- only this file. Log tail (last {tail.Length} chars): '{tail}'" +
            $"{lastTaskResultNote}",
            Array.Empty<string>(),
            TaskName:              taskName,
            LastTaskResultHex:     lastTaskResultHex,
            LastTaskResultDecimal: lastTaskResultDecimal);
    }

    /// <summary>
    /// How <see cref="RunAsync"/> reaches the interactive desktop.
    /// </summary>
    internal enum LaunchMode
    {
        /// <summary>
        /// Register + start an Interactive <c>logix-mcp-s1-&lt;verb&gt;</c>
        /// scheduled task (this process is in session 0, so it cannot touch
        /// the desktop itself).
        /// </summary>
        ScheduledTask,

        /// <summary>
        /// Launch the driver as a child <c>powershell.exe</c> of this
        /// process (this process is already on an interactive desktop,
        /// session != 0). No scheduled task is registered.
        /// </summary>
        Direct,
    }

    /// <summary>
    /// Decides the <see cref="LaunchMode"/> from the environment probe: a
    /// non-zero current session id means this process is already on an
    /// interactive desktop, so the driver is run directly; session 0 keeps
    /// the scheduled-task path.
    /// </summary>
    internal static LaunchMode DecideLaunchMode(IEnvironmentProbe probe) =>
        probe.CurrentSessionId != 0 ? LaunchMode.Direct : LaunchMode.ScheduledTask;

    /// <summary>Wire value of <c>launchMode</c> for <see cref="LaunchMode.Direct"/>.</summary>
    internal const string LaunchModeDirectName = "direct";

    /// <summary>Wire value of <c>launchMode</c> for <see cref="LaunchMode.ScheduledTask"/>.</summary>
    internal const string LaunchModeScheduledTaskName = "scheduled_task";

    /// <summary>
    /// The <c>launchMode</c> value carried on every payload:
    /// <c>"direct"</c> or <c>"scheduled_task"</c>.
    /// </summary>
    internal static string LaunchModeName(LaunchMode mode) =>
        mode == LaunchMode.Direct ? LaunchModeDirectName : LaunchModeScheduledTaskName;

    /// <summary>
    /// The scheduled-task run level (<c>"Highest"</c> when the caller's token is
    /// elevated, otherwise <c>"Limited"</c>) — the same value
    /// <see cref="BuildCommand"/> registers the task with.
    /// </summary>
    internal static string RunLevelName(bool elevated) => elevated ? "Highest" : "Limited";

    /// <summary>
    /// Reports the <c>(launchMode, runLevel)</c> that <see cref="RunAsync"/>
    /// would use for <paramref name="probe"/>, via the same
    /// <see cref="DecideLaunchMode"/> / <see cref="LaunchModeName"/> /
    /// <see cref="RunLevelName"/> logic, without launching anything. Lets a
    /// caller that never reaches the runner (a refusal before it is invoked)
    /// still report how the call would have been launched.
    /// </summary>
    /// <param name="probe">
    /// Environment probe; defaults to a new <see cref="EnvironmentProbe"/>
    /// when <c>null</c>.
    /// </param>
    /// <returns>
    /// <c>LaunchMode</c> is <c>"direct"</c> or <c>"scheduled_task"</c>;
    /// <c>RunLevel</c> is <c>"Highest"</c>/<c>"Limited"</c> in task mode and
    /// <c>null</c> in direct mode. A probe failure never throws: both fields
    /// are <c>null</c>.
    /// </returns>
    public static (string? LaunchMode, string? RunLevel) PlanLaunch(IEnvironmentProbe? probe = null)
    {
        try
        {
            var effectiveProbe = probe ?? new EnvironmentProbe();
            var mode = DecideLaunchMode(effectiveProbe);
            var elevated = effectiveProbe.IsElevated;
            return (LaunchModeName(mode),
                    mode == LaunchMode.ScheduledTask ? RunLevelName(elevated) : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Stamps <paramref name="result"/> with <c>launchMode</c>, and with
    /// <c>runLevel</c> in task mode (always <c>null</c> for a direct launch,
    /// which registers no task).
    /// </summary>
    internal static Session1ScriptResult WithLaunchInfo(
        Session1ScriptResult result, LaunchMode mode, bool elevated) =>
        result with
        {
            LaunchMode = LaunchModeName(mode),
            RunLevel   = mode == LaunchMode.ScheduledTask ? RunLevelName(elevated) : null,
        };

    /// <summary>Error code for a direct launch whose session has no Studio process.</summary>
    internal const string StudioNotInThisSessionCode = StudioPreLaunch.StudioNotInThisSessionCode;

    /// <summary>Error code for a direct launch when no LogixDesigner process exists in any session.</summary>
    internal const string StudioNotRunningCode = StudioPreLaunch.StudioNotRunningCode;

    /// <summary>Forwards to <see cref="StudioPreLaunch.CheckStudioInThisSession"/> (SDK-free).</summary>
    internal static string? CheckStudioInThisSession(
        int currentSessionId, IReadOnlyList<int> studioSessionIds) =>
        StudioPreLaunch.CheckStudioInThisSession(currentSessionId, studioSessionIds);

    /// <summary>Forwards to <see cref="StudioPreLaunch.PreLaunchKindOf"/> (SDK-free).</summary>
    internal static string? PreLaunchKindOf(string? reason) => StudioPreLaunch.PreLaunchKindOf(reason);

    /// <summary>Error code for a direct launch whose driver is less privileged than Studio.</summary>
    internal const string StudioElevatedDriverNotCode = StudioPreLaunch.StudioElevatedDriverNotCode;

    /// <summary>Exact refusal text for <see cref="StudioElevatedDriverNotCode"/>.</summary>
    internal const string StudioElevatedDriverNotMessage =
        "Studio 5000 is running elevated; this lgx-mcp server is not, so Windows blocks it from " +
        "driving Studio. Start Studio normally, or run the MCP client elevated.";

    /// <summary>
    /// Integrity-mismatch check for a direct launch: returns <c>null</c> when
    /// Studio's integrity is not higher than the driver's, otherwise the
    /// <c>studio_elevated_driver_not</c> refusal message. An Access-denied
    /// when opening Studio's process token is reported by the probe as
    /// "higher", so it is mapped here rather than surfacing raw.
    /// </summary>
    internal static string? CheckStudioIntegrity(IEnvironmentProbe probe) =>
        probe.StudioIntegrityHigherThanCurrent
            ? $"{StudioElevatedDriverNotCode}: {StudioElevatedDriverNotMessage}"
            : null;

    /// <summary>Outcome of one child-process run (see <see cref="ProcessRunner"/>).</summary>
    internal sealed record ProcessRunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut = false);

    /// <summary>
    /// Starts <paramref name="psi"/>, captures its output, and waits for it
    /// to exit (killing it after <paramref name="timeout"/> when given).
    /// Returns <c>null</c> when the process could not be started. Injectable
    /// so the launch decision can be unit-tested without a real
    /// <c>powershell.exe</c>.
    /// </summary>
    internal delegate Task<ProcessRunResult?> ProcessRunner(
        ProcessStartInfo psi, TimeSpan? timeout, CancellationToken ct);

    /// <summary>Default <see cref="ProcessRunner"/> backed by <see cref="Process"/>.</summary>
    internal static async Task<ProcessRunResult?> RunProcessAsync(
        ProcessStartInfo psi, TimeSpan? timeout, CancellationToken ct)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;

        using var process = Process.Start(psi);
        if (process is null)
            return null;

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        var timedOut = false;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t)
            timeoutCts.CancelAfter(t);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessRunResult(process.HasExited ? process.ExitCode : -1, stdout, stderr, timedOut);
    }

    /// <summary>
    /// Runs <c>bench/studio-edits.ps1 -Verb &lt;verb&gt;</c> on the
    /// interactive desktop and returns its parsed result. When this process
    /// is in session 0 (see <see cref="IEnvironmentProbe.CurrentSessionId"/>)
    /// it uses the <c>logix-mcp-s1-&lt;verb&gt;</c> scheduled-task pattern
    /// (see <see cref="BuildTaskName"/>); from any other session it launches
    /// the driver directly as a child <c>powershell.exe</c> and registers no
    /// scheduled task (see <see cref="DecideLaunchMode"/>).
    /// </summary>
    /// <param name="verb">One of <c>status</c>, <c>cancel</c>, <c>accept</c>, <c>test</c>, <c>untest</c>, <c>assemble</c>.</param>
    /// <param name="scriptPath">
    /// Optional explicit path to <c>bench/studio-edits.ps1</c>. When
    /// <c>null</c>, resolved via <see cref="FindDefaultScriptPath"/>.
    /// </param>
    /// <param name="openTimeoutSeconds">
    /// Historical open-window budget (the driver no longer opens Studio); combined with
    /// <see cref="GestureBudgetSeconds"/> to compute the timeout budget used
    /// by both launch modes (poll timeout for the task, child-process
    /// timeout for direct launch). Defaults to
    /// <see cref="DefaultOpenTimeoutSeconds"/> (120 s), i.e. 180 s.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="probe">
    /// Optional environment probe; defaults to <see cref="EnvironmentProbe"/>.
    /// </param>
    /// <param name="taskName">
    /// Optional scheduled-task name override (see <see cref="EditsTaskName"/>);
    /// <c>null</c> keeps the per-verb <see cref="BuildTaskName"/> name.
    /// </param>
    /// <returns>
    /// A <see cref="Session1ScriptResult"/> describing the outcome.
    /// <see cref="Session1ScriptResult.Ok"/> is <c>false</c> (never throws,
    /// except <see cref="OperationCanceledException"/>) when this host is not
    /// Windows, the script cannot be found, the launch itself fails, the
    /// timeout budget is exceeded, or no parseable <c>RESULT_JSON:</c> line
    /// is produced.
    /// </returns>
    public static Task<Session1ScriptResult> RunAsync(
        string             verb,
        string?            scriptPath = null,
        int?               openTimeoutSeconds = null,
        CancellationToken  ct = default,
        IEnvironmentProbe? probe = null,
        string?            taskName = null,
        string?            program = null,
        StudioWindowTarget? target = null,
        int?               confirmTimeoutSeconds = null) =>
        RunCoreAsync(verb, scriptPath, openTimeoutSeconds, probe, runner: null, isWindows: null, ct, taskName, program, target,
            confirmTimeoutSeconds);

    /// <summary>
    /// <see cref="RunAsync"/> with every environment dependency injectable
    /// (probe, process runner, OS check) for tests.
    /// </summary>
    internal static async Task<Session1ScriptResult> RunCoreAsync(
        string             verb,
        string?            scriptPath,
        int?               openTimeoutSeconds,
        IEnvironmentProbe? probe,
        ProcessRunner?     runner,
        Func<bool>?        isWindows,
        CancellationToken  ct,
        string?            taskName = null,
        string?            program = null,
        StudioWindowTarget? target = null,
        int?               confirmTimeoutSeconds = null)
    {
        // Decide the launch mode up front so that EVERY payload -- success,
        // refusal, timeout, parse failure, not_windows, script-not-found --
        // reports it. A probe failure falls back to
        // the established scheduled-task path.
        var mode = LaunchMode.ScheduledTask;
        var elevated = false;
        var effectiveProbe = probe ?? new EnvironmentProbe();
        try
        {
            mode = DecideLaunchMode(effectiveProbe);
            elevated = effectiveProbe.IsElevated;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Probe failure: fall back to the established scheduled-task path.
        }

        // Refuse names that could break out of a PowerShell single-quoted literal (e.g. Unicode
        // quotes U+2018-U+201B) BEFORE any script lookup or process runs.
        if (StudioPreLaunch.CheckNames(program, null) is { } badName)
            return WithLaunchInfo(
                new Session1ScriptResult(false, badName, Array.Empty<string>(), Launched: false),
                mode, elevated);

        var result = await RunCoreForModeAsync(
            verb, scriptPath, openTimeoutSeconds, effectiveProbe, mode, elevated,
            runner, isWindows, ct, taskName, program, target, confirmTimeoutSeconds).ConfigureAwait(false);
        return WithLaunchInfo(result, mode, elevated);
    }

    private static async Task<Session1ScriptResult> RunCoreForModeAsync(
        string               verb,
        string?              scriptPath,
        int?                 openTimeoutSeconds,
        IEnvironmentProbe    effectiveProbe,
        LaunchMode           mode,
        bool                 elevated,
        ProcessRunner?       runner,
        Func<bool>?          isWindows,
        CancellationToken    ct,
        string?              taskNameOverride = null,
        string?              program = null,
        StudioWindowTarget?  target = null,
        int?                 confirmTimeoutSeconds = null)
    {
        runner    ??= RunProcessAsync;
        isWindows ??= OperatingSystem.IsWindows;

        // Computed unconditionally, first, from verb alone -- this is the
        // task name this call would register (or did register) regardless of
        // which branch below returns, so every failure (and the eventual
        // success) can name it.
        var taskName = taskNameOverride ?? BuildTaskName(verb);

        if (!isWindows())
            return new Session1ScriptResult(
                false,
                "not_windows: the logix-mcp-s1 session-1 runner requires Windows " +
                "(Register-ScheduledTask/Start-ScheduledTask are Windows-only).",
                Array.Empty<string>(),
                TaskName: taskName,
                Launched: false);

        string? resolvedScriptPath;
        IReadOnlyList<string> pathsTried;
        if (scriptPath is not null)
        {
            resolvedScriptPath = scriptPath;
            pathsTried = new[] { scriptPath };
        }
        else
        {
            pathsTried = CandidateScriptPaths();
            resolvedScriptPath = pathsTried.FirstOrDefault(File.Exists);
        }

        if (resolvedScriptPath is null || !File.Exists(resolvedScriptPath))
            return new Session1ScriptResult(
                false,
                BuildNotFoundMessage(pathsTried),
                Array.Empty<string>(),
                TaskName: taskName,
                Launched: false);

        if (mode == LaunchMode.Direct)
        {
            try
            {
                var refusal = CheckStudioInThisSession(
                    effectiveProbe.CurrentSessionId, effectiveProbe.LogixDesignerSessionIds);
                if (refusal is not null)
                    return new Session1ScriptResult(
                        false, refusal, Array.Empty<string>(), Launched: false,
                        Kind: PreLaunchKindOf(refusal));

                var integrityRefusal = CheckStudioIntegrity(effectiveProbe);
                if (integrityRefusal is not null)
                    return new Session1ScriptResult(
                        false, integrityRefusal, Array.Empty<string>(), Launched: false,
                        Kind: PreLaunchKindOf(integrityRefusal));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Probe failure while checking: proceed with the launch.
            }
        }

        var outputFile = Path.Combine(Path.GetTempPath(), $"{taskName}-{Guid.NewGuid():N}.log");
        var timeout = TimeSpan.FromSeconds(
            (openTimeoutSeconds ?? DefaultOpenTimeoutSeconds) + GestureBudgetSeconds);

        try
        {
            return mode == LaunchMode.Direct
                ? await RunDirectAsync(verb, resolvedScriptPath, outputFile, timeout, runner, ct, program, target, confirmTimeoutSeconds)
                    .ConfigureAwait(false)
                : await RunViaScheduledTaskAsync(
                    verb, taskName, resolvedScriptPath, outputFile, timeout, runner, elevated, ct,
                    taskNameOverride, program, target, confirmTimeoutSeconds)
                    .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var what = mode == LaunchMode.Direct ? "direct launch" : $"{taskName} task invocation";
            return new Session1ScriptResult(
                false, $"{what} failed: {ex.Message}", Array.Empty<string>(),
                TaskName: mode == LaunchMode.Direct ? null : taskName);
        }
        finally
        {
            try { if (File.Exists(outputFile)) File.Delete(outputFile); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Direct-launch mode: runs the driver as a child <c>powershell.exe</c>
    /// with the same argument string as the scheduled task's action (see
    /// <see cref="BuildActionArgument"/>: <c>-Command "&amp; '&lt;script&gt;'
    /// -Verb '&lt;verb&gt;' *&gt; '&lt;log&gt;'"</c>), <c>CreateNoWindow</c>,
    /// and the same timeout budget. The result is parsed with
    /// <see cref="ParseResultJson"/> from the redirect file, falling back to
    /// captured stdout. No scheduled task is registered.
    /// </summary>
    private static async Task<Session1ScriptResult> RunDirectAsync(
        string verb, string scriptPath, string outputFile, TimeSpan timeout,
        ProcessRunner runner, CancellationToken ct, string? program = null,
        StudioWindowTarget? target = null, int? confirmTimeoutSeconds = null)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            Arguments       = BuildActionArgument(scriptPath, verb, outputFile, program, target, confirmTimeoutSeconds),
            UseShellExecute = false,
            CreateNoWindow  = true,
        };

        var run = await runner(psi, timeout, ct).ConfigureAwait(false);
        if (run is null)
            return new Session1ScriptResult(
                false, "Failed to start powershell.exe for direct launch of the driver.",
                Array.Empty<string>());

        var fileText = File.Exists(outputFile)
            ? await File.ReadAllTextAsync(outputFile, ct).ConfigureAwait(false)
            : string.Empty;
        var captured = fileText + "\n" + run.Stdout;

        var parsed = ParseResultJson(captured);
        if (parsed is not null)
            return parsed;

        // No RESULT_JSON line, but the driver's exit code can still say the window
        // targeting refused: map 20/21 to the typed kinds.
        if (StudioPreLaunch.WindowKindOfExitCode(run.ExitCode) is { } windowKind)
            return new Session1ScriptResult(
                false,
                windowKind == StudioKinds.StudioWindowNotFoundKind
                    ? StudioPreLaunch.StudioWindowNotFoundCode
                    : StudioPreLaunch.StudioWindowAmbiguousCode,
                Array.Empty<string>(),
                Kind: windowKind);

        var tail = ReadTail(captured.Trim());
        var timeoutNote = run.TimedOut
            ? $" (killed after {timeout.TotalSeconds:0}s timeout budget)"
            : string.Empty;
        return new Session1ScriptResult(
            false,
            $"Direct launch of the driver finished (exit code {run.ExitCode}) but no RESULT_JSON: " +
            $"line was found in its redirect log '{outputFile}' or stdout{timeoutNote}. " +
            $"Output tail (last {tail.Length} chars): '{tail}' stderr: '{run.Stderr}'",
            Array.Empty<string>());
    }

    /// <summary>
    /// Scheduled-task mode (session 0): register + start the
    /// <c>logix-mcp-s1-&lt;verb&gt;</c> task, then poll it to completion.
    /// </summary>
    private static async Task<Session1ScriptResult> RunViaScheduledTaskAsync(
        string verb, string taskName, string scriptPath, string outputFile, TimeSpan pollTimeout,
        ProcessRunner runner, bool elevated, CancellationToken ct, string? taskNameOverride = null,
        string? program = null, StudioWindowTarget? target = null, int? confirmTimeoutSeconds = null)
    {
        var command = BuildCommand(scriptPath, verb, outputFile, elevated, taskNameOverride, program, target, confirmTimeoutSeconds);

        static ProcessStartInfo PowerShell(string command)
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);
            return psi;
        }

        // Step 1: register + start the task. This launcher process exits
        // as soon as Start-ScheduledTask returns -- it does not wait for
        // the task to finish, so this call itself is quick and its
        // stdout is only ever launcher/registration diagnostics, never
        // the driver's own output (that goes exclusively to
        // outputFile — see BuildCommand's remarks).
        var launch = await runner(PowerShell(command), null, ct).ConfigureAwait(false);
        if (launch is null)
            return new Session1ScriptResult(
                false, $"Failed to start powershell.exe for the {taskName} task.", Array.Empty<string>(),
                TaskName: taskName);

        if (launch.ExitCode != 0)
            return new Session1ScriptResult(
                false,
                $"Failed to register/start the {taskName} scheduled task (launcher exit " +
                $"code {launch.ExitCode}). stdout: '{launch.Stdout}' stderr: '{launch.Stderr}'",
                Array.Empty<string>(),
                TaskName: taskName);

        // Step 2: poll the task's own state (never the launcher's
        // stdout, which is empty here) until it leaves the Running
        // state, then read the redirect log for the driver's
        // RESULT_JSON: line — see AwaitResultFromRedirectFileAsync.
        async Task<string> QueryTaskStateAsync(CancellationToken queryCt)
        {
            var q = await runner(PowerShell(BuildQueryStateCommand(taskName)), null, queryCt)
                .ConfigureAwait(false);
            return q is null ? string.Empty : q.Stdout.Trim();
        }

        // Best-effort LastTaskResult query (see BuildQueryLastTaskResultCommand),
        // surfaced on both success and failure payloads.
        async Task<uint?> QueryLastTaskResultAsync(CancellationToken queryCt)
        {
            var q = await runner(PowerShell(BuildQueryLastTaskResultCommand(taskName)), null, queryCt)
                .ConfigureAwait(false);
            return q is not null && q.ExitCode == 0 && uint.TryParse(q.Stdout.Trim(), out var value)
                ? value
                : null;
        }

        return await AwaitResultFromRedirectFileAsync(
            QueryTaskStateAsync, outputFile, taskName, pollTimeout, ct,
            getLastTaskResultAsync: QueryLastTaskResultAsync).ConfigureAwait(false);
    }
}
#endif
