// ── StudioKinds / StudioPreLaunch — SDK-free Studio-state constants and pure helpers ──
//
// Declared outside any #if LOGIX_SDK so logix_doctor (compiled in both lanes) can use
// them. StudioLifecycleHandler and Session1ScriptRunner (SDK-only) forward to these.
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>Typed <c>kind</c> values for Studio-state refusals, plus the shared engineer message.</summary>
public static class StudioKinds
{
    /// <summary><c>kind</c> returned when no Logix Designer window is open (driver reason <c>studio_not_running</c>).</summary>
    public const string StudioNotRunningKind = "StudioNotRunning";

    /// <summary><c>kind</c> returned when Studio's window title shows it offline (driver reason <c>studio_offline</c>).</summary>
    public const string StudioOfflineKind = "StudioOffline";

    /// <summary><c>kind</c> returned when Studio runs only in another Windows session (pre-launch reason <c>studio_not_in_this_session</c>).</summary>
    public const string StudioInOtherSessionKind = "StudioInOtherSession";

    /// <summary><c>kind</c> returned when Studio is elevated and the driver is not (pre-launch reason <c>studio_elevated_driver_not</c>).</summary>
    public const string StudioElevatedKind = "StudioElevated";

    /// <summary><c>kind</c> returned when Logix Designer windows are open but none is titled <c>&lt;controller&gt; in &lt;file&gt;.ACD</c> (or, with no known file, just <c>&lt;controller&gt;</c>) for the session (driver reason <c>studio_window_not_found</c>, script exit 20).</summary>
    public const string StudioWindowNotFoundKind = "StudioWindowNotFound";

    /// <summary><c>kind</c> returned when more than one Logix Designer window matches the session's controller and project (driver reason <c>studio_window_ambiguous</c>, script exit 21).</summary>
    public const string StudioWindowAmbiguousKind = "StudioWindowAmbiguous";

    /// <summary><c>kind</c> returned when a <c>#32770</c> dialog owned by the Studio main window is already open, so the driver posted nothing (driver reason <c>studio_modal_open</c>).</summary>
    public const string StudioModalOpenKind = "StudioModalOpen";

    /// <summary>The message both Studio-state errors carry: lgx-mcp never launches Studio or takes it online.</summary>
    public const string StudioEngineerMessage =
        "ask the engineer to open the project in Studio and go online by Upload; " +
        "lgx-mcp never launches Studio or goes online through it";
}

/// <summary>Pure, platform-independent pre-launch checks for a direct Studio launch.</summary>
public static class StudioPreLaunch
{
    /// <summary>Error code for a program or routine name that fails <see cref="NamePattern"/>.</summary>
    internal const string InvalidNameCode = "invalid_argument";

    /// <summary>Allowed program/routine names: <c>^[A-Za-z_][A-Za-z0-9_]{0,39}$</c> (ASCII only, no trailing newline).</summary>
    private static readonly Regex NamePattern =
        new(@"^[A-Za-z_][A-Za-z0-9_]{0,39}\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// True when <paramref name="name"/> is a legal Logix program/routine name. Anything else
    /// (including Unicode quotes U+2018-U+201B) must never reach a PowerShell single-quoted literal.
    /// </summary>
    internal static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);

    /// <summary>
    /// Returns an <see cref="InvalidNameCode"/> refusal message when <paramref name="program"/> or
    /// <paramref name="routine"/> is non-null and invalid, otherwise <c>null</c>.
    /// </summary>
    internal static string? CheckNames(string? program, string? routine)
    {
        if (program is not null && !IsValidName(program))
            return $"{InvalidNameCode}: program name does not match ^[A-Za-z_][A-Za-z0-9_]{{0,39}}$. Nothing was launched.";
        if (routine is not null && !IsValidName(routine))
            return $"{InvalidNameCode}: routine name does not match ^[A-Za-z_][A-Za-z0-9_]{{0,39}}$. Nothing was launched.";
        return null;
    }

    /// <summary>Error code for a direct launch whose session has no Studio process.</summary>
    internal const string StudioNotInThisSessionCode = "studio_not_in_this_session";

    /// <summary>Error code for a direct launch when no LogixDesigner process exists in any session.</summary>
    internal const string StudioNotRunningCode = "studio_not_running";

    /// <summary>Error code for a direct launch whose driver is less privileged than Studio.</summary>
    internal const string StudioElevatedDriverNotCode = "studio_elevated_driver_not";

    /// <summary>
    /// Session-mismatch check for a direct launch from
    /// <paramref name="currentSessionId"/>: returns <c>null</c> when a
    /// <c>LogixDesigner</c> process runs in that session, otherwise the
    /// <c>studio_not_in_this_session</c> refusal message naming both the
    /// server's session id and Studio's session id(s).
    /// </summary>
    internal static string? CheckStudioInThisSession(
        int currentSessionId, IReadOnlyList<int> studioSessionIds)
    {
        if (studioSessionIds.Contains(currentSessionId))
            return null;

        if (studioSessionIds.Count == 0)
            return $"{StudioNotRunningCode}: {StudioKinds.StudioEngineerMessage}. Nothing was launched.";

        var studio = $"LogixDesigner is running in session {string.Join(", ", studioSessionIds.Distinct())}";
        return $"{StudioNotInThisSessionCode}: this lgx-mcp server is in session {currentSessionId}, " +
               $"but {studio}; a direct launch cannot drive Studio in another session. " +
               "Nothing was launched.";
    }

    /// <summary>Driver <c>reason</c> when no open Logix Designer window matches the expected title.</summary>
    internal const string StudioWindowNotFoundCode = "studio_window_not_found";

    /// <summary>Driver <c>reason</c> when more than one open Logix Designer window matches the expected title.</summary>
    internal const string StudioWindowAmbiguousCode = "studio_window_ambiguous";

    /// <summary>Script exit code for <see cref="StudioWindowNotFoundCode"/> (bench/studio-edits.ps1).</summary>
    internal const int StudioWindowNotFoundExitCode = 20;

    /// <summary>Script exit code for <see cref="StudioWindowAmbiguousCode"/> (bench/studio-edits.ps1).</summary>
    internal const int StudioWindowAmbiguousExitCode = 21;

    /// <summary>
    /// Maps a <c>bench/studio-edits.ps1</c> exit code to the window-targeting
    /// <c>kind</c>, or <c>null</c> when the code is not a window-targeting refusal.
    /// </summary>
    internal static string? WindowKindOfExitCode(int exitCode) => exitCode switch
    {
        StudioWindowNotFoundExitCode  => StudioKinds.StudioWindowNotFoundKind,
        StudioWindowAmbiguousExitCode => StudioKinds.StudioWindowAmbiguousKind,
        _                             => null,
    };

    /// <summary>
    /// Maps a driver <c>reason</c> to the window-targeting <c>kind</c>, or <c>null</c>.
    /// </summary>
    internal static string? WindowKindOfReason(string? reason) => reason switch
    {
        StudioWindowNotFoundCode  => StudioKinds.StudioWindowNotFoundKind,
        StudioWindowAmbiguousCode => StudioKinds.StudioWindowAmbiguousKind,
        _                         => null,
    };

    /// <summary>
    /// Typed <c>kind</c> for a pre-launch refusal <paramref name="reason"/> (by its leading
    /// error code), or <c>null</c> when the reason is not a Studio lifecycle refusal.
    /// </summary>
    internal static string? PreLaunchKindOf(string? reason)
    {
        if (reason is null) return null;
        if (reason.StartsWith(StudioNotRunningCode, StringComparison.Ordinal))
            return StudioKinds.StudioNotRunningKind;
        if (reason.StartsWith(StudioNotInThisSessionCode, StringComparison.Ordinal))
            return StudioKinds.StudioInOtherSessionKind;
        if (reason.StartsWith(StudioElevatedDriverNotCode, StringComparison.Ordinal))
            return StudioKinds.StudioElevatedKind;
        return null;
    }
}
