<#
.SYNOPSIS
    Session-1 GUI driver for Studio 5000 Logix Designer online-edits state.
    Windows-only.

.DESCRIPTION
    INVARIANT: posted WM_COMMAND plus verified dialog text only; no keys, no
    clicks, no focus, never launches or connects Studio. This script drives a
    Studio the engineer already opened and took online by Upload. If no Logix
    Designer window exists it reports StudioNotRunning (reason
    studio_not_running); if the window title shows Studio offline it reports
    StudioOffline (reason studio_offline). Both carry the message: ask the
    engineer to open the project in Studio and go online by Upload; lgx-mcp
    never launches Studio or goes online through it. Read-only observation
    (window title, screenshots) is unaffected.

    Drives the already-open Studio 5000 project through the
    online-edits gestures that have no SDK equivalent: reading the visual
    online-edits status, cancelling all pending edits, and accepting all
    pending edits. Studio 5000 is an MFC application with no accessibility menu bar, so
    every verb is ONE posted gesture: WM_COMMAND with the menu
    command ID posted to the Studio main window (found by PID), the
    confirmation dialog's text verified, and the dialog's own Yes (or No)
    pressed by posted BN_CLICKED. There are no keystrokes, no mouse clicks, no
    cursor movement and no focus changes. If no dialog appears the result is
    command_not_applicable. Every material step is confirmed by a screenshot
    written to -ScreenshotDir so a human or an agent reviewing the run can see
    exactly what the desktop looked like at that point.

    This script must be launched on the interactive desktop (session 1 on the
    bench, via a scheduled task with an Interactive principal: Register-ScheduledTask /
    Start-ScheduledTask). Session 0 (the
    non-interactive "services" session) has no desktop: screen capture
    either no-ops or throws there instead of failing loudly,
    so this script refuses to run in session 0 up front, before any
    Add-Type/screenshot/window code executes, and reports why via the
    RESULT_JSON contract described below.

    RESULT_JSON contract: exactly one line of the form
        RESULT_JSON: {"ok":<bool>,"verb":"<verb>", ...}
    Every line also carries gestureTier (always "posted": the only gesture
    there is), dialogText (the confirmation
    dialog text), dialogWarnings (the dialog's Static texts other than the
    verb phrase and the question, non-empty, in control order), cursorMovedByDriver (true only if this
    driver itself issued cursor/mouse input during the gesture; always false, the driver is posted-only),
    cursorMovedExternally (GetCursorPos differed between gesture start and end, i.e. the user or something
    else moved it), cursorMoved (compatibility: "either" of the two) and foregroundChanged (bool comparing
    the foreground window at gesture start vs end).
    is written to stdout on every code path -- success, refusal, a failed
    gesture, or an unhandled exception. Callers (in particular the
    logix_cancel_pending_edits MCP tool) parse only
    that one line; every other line in this script's output is diagnostic
    Write-Host chatter and must not be parsed.

    The menu command IDs below are exposed as parameters; they come from
    Studio's own menu resources and are bench-verified on Studio v38.11.

.PARAMETER Verb
    status  -- report the current online-edits state (screenshot only, no
               mutating gesture).
    cancel  -- cancel all pending online edits (Logic -> Online Edits ->
               Cancel All Edits).
    accept  -- accept all pending online edits (Logic -> Online Edits ->
               Accept All Edits).
    test    -- Test Accepted Program Edits (command 32061): the new logic
               runs on the controller while the original is kept.
    untest  -- Untest Accepted Program Edits (command 32062): puts the
               original logic back.
    assemble -- Assemble Accepted Program Edits (command 32063): enabled only
               on a tested zone; irreversible.
               Every verb is posted only; no confirmation dialog within
               $ConfirmTimeoutSeconds returns the reason
               command_not_applicable.

.PARAMETER ConfirmTimeoutSeconds
    Seconds to wait for the confirmation dialog after a command is posted
    (default 10). Before posting, the driver also waits up to 10 s for the
    Studio main window to answer SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG,
    1000) and records the time spent in detail.studioIdleWaitMs.

.PARAMETER ScreenshotDir
    Directory that receives one PNG per material step. Defaults to
    $env:TEMP\lgx-mcp so operators/agents look in one place.

.EXAMPLE
    .\bench\studio-edits.ps1 -Verb status

.EXAMPLE
    .\bench\studio-edits.ps1 -Verb cancel -Program MainProgram
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('status', 'cancel', 'accept', 'test', 'untest', 'assemble')]
    [string] $Verb,

    # Program the cancel/accept verb targets. The confirmation dialog's text
    # must name this program ("Cancel edits for program '<Program>'" /
    # "Finalize all edits in program '<Program>'") before Yes is pressed;
    # when empty, no dialog can match, so IDNO is posted (fail closed).
    [Parameter(Mandatory = $false)]
    [string] $Program = '',

    # Controller name of the session this call acts for. The server
    # takes it from the SDK session and passes it as a script PARAMETER (never
    # spliced into command text). Together with -ExpectedProject it selects the
    # one Logix Designer window titled "<controller> in <file>.ACD" or, for an
    # unsaved upload, just "<controller>" (no file name); no match
    # refuses with StudioWindowNotFound (exit 20), several with
    # StudioWindowAmbiguous (exit 21), and nothing is clicked. When empty, the
    # controller is not constrained (a lone open window is still accepted;
    # several are ambiguous -- the script never silently picks the first).
    [Parameter(Mandatory = $false)]
    [string] $ExpectedController = '',

    # Project file name of the session ("Echo.ACD"; the .ACD extension is
    # optional here). Empty when the server does not know it (an upload
    # session has no Studio-side file), in which case any file name matches and a
    # title without a file name ("<controller> [..." / "<controller>*") matches too.
    [Parameter(Mandatory = $false)]
    [string] $ExpectedProject = '',

    [Parameter(Mandatory = $false)]
    [string] $ScreenshotDir = "$env:TEMP\lgx-mcp",

    # The Windows process name Logix Designer actually runs under. Windows
    # must be matched by owning process first, never by the product-marketing
    # window-title text ("Studio 5000") -- that text is not stable across
    # Logix Designer versions (v38 on Echo never contained it) and a
    # substring match on it is what caused a prior run to launch a second,
    # redundant instance.
    [Parameter(Mandatory = $false)]
    [string] $StudioProcessName = 'LogixDesigner',

    # Title regex applied only to windows owned by a $StudioProcessName
    # process. Matches both the plain form ("Logix Designer - <ctrl> in
    # <file>.ACD [...]") and the online-mode form, which prefixes a bracketed
    # marker ("[Emulator] - Logix Designer - ...", "[Rem Run] - Logix
    # Designer - ...").
    [Parameter(Mandatory = $false)]
    [string] $StudioWindowTitleRegex = '^(\[[^\]]+\] - )?Logix Designer - ',

    [Parameter(Mandatory = $false)]
    [int] $ConfirmDialogTimeoutSeconds = 15,

    # Exact window title of the Cancel/Accept confirmation dialog. Studio
    # 5000 reuses this literal title ("Logix Designer") for more than one
    # dialog, so title alone is never a safe discriminator -- this must
    # always be combined with $ConfirmDialogClassName and an owner-handle
    # check (see the FindWindowByExactTitleClassAndOwner helper used by
    # Wait-ForConfirmationDialog) rather than matched as a substring.
    [Parameter(Mandatory = $false)]
    [string] $ConfirmDialogExactTitle = 'Logix Designer',

    # Exact window class of the confirmation dialog -- the standard Win32
    # dialog-box class. Required (together with the exact title and owner
    # check) so this never matches an unrelated top-level window that
    # happens to share the "Logix Designer" title.
    [Parameter(Mandatory = $false)]
    [string] $ConfirmDialogClassName = '#32770',

    # ------------------------------------------------------------------
    # Posted-command gesture. Every verb
    # posts WM_COMMAND with these menu-resource command IDs
    # straight to the Studio main window (resolved by Studio PID), with no
    # cursor movement, no foreground change and no keystrokes. The IDs come
    # from Studio's own menu resources (LoadMenu on LogixACENU.dll /
    # AFLibENU.dll) for Studio v38.11.
    # ------------------------------------------------------------------

    # Logic -> Online Edits -> Cancel Accepted Program Edits. Menu ID 32064
    # in LogixACENU.dll / AFLibENU.dll (Studio v38.11). Expected: a confirm
    # dialog, then Yes -> "No Edits".
    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 65535)]
    [int] $CancelCommandId = 32064,

    # Logic -> Online Edits -> Finalize All Edits in Program (Ctrl+Shift+F),
    # NOT Assemble Accepted Program Edits (32063, which MFC ignores for a
    # freshly published pending edit). Menu ID 32077 in LogixACENU.dll /
    # AFLibENU.dll (Studio v38.11). Expected: a confirm dialog,
    # then Yes -> "No Edits" and the rung executes.
    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 65535)]
    [int] $AcceptCommandId = 32077,

    # View -> Errors (Alt+1): toggles Logix Designer's Errors pane. After a
    # successful accept the pane slides out as an auto-hide flyout, and while it
    # is showing a posted accept / test / untest / assemble / cancel raises no
    # confirmation dialog. Posted exactly like -AcceptCommandId, and only
    # as the single retry of a verb whose first post produced no dialog: once
    # to toggle, and once more to restore the pane if the retry also produced
    # no dialog. Never posted before the first attempt, and never while a
    # modal dialog is up. Posted only.
    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 65535)]
    [int] $ErrorsPaneToggleCommandId = 32027,

    # Logic -> Online Edits -> Test Accepted Program Edits. Menu ID 32061 in
    # LogixACENU.dll / AFLibENU.dll (Studio v38.11 resource dump). Expected: a
    # dialog, then Yes -> the new logic runs, Studio stays "Edits Present".
    # Posted only.
    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 65535)]
    [int] $TestCommandId = 32061,

    # Logic -> Online Edits -> Untest Accepted Program Edits. Menu ID 32062 in
    # LogixACENU.dll / AFLibENU.dll (Studio v38.11 resource dump). Expected: a
    # dialog, then Yes -> the original logic is back, Studio stays "Edits
    # Present". Posted only.
    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 65535)]
    [int] $UntestCommandId = 32062,

    # Logic -> Online Edits -> Assemble Accepted Program Edits. Menu ID 32063
    # in LogixACENU.dll / AFLibENU.dll (Studio v38.11 resource dump). Expected: a
    # dialog after a Test, then Yes -> "No Edits". Disabled until the
    # zone is tested, so no dialog means command_not_applicable. Posted
    # only. This is NOT the accept verb's command (32077, Finalize All Edits).
    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 65535)]
    [int] $AssembleCommandId = 32063,

    # How long to wait for the confirm dialog after a posted command before
    # concluding the command was disabled / not applicable
    # (command_not_applicable). Nothing else is tried. Studio can be slow to
    # raise the dialog, so the default is 10 s; the server passes 15 when a
    # lifecycle verb follows its own publish or lifecycle call on the same
    # routine within 30 s.
    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 600)]
    [int] $ConfirmTimeoutSeconds = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Step 0 -- Self-identify: the very first line of output must name the
# resolved path of the script that is actually executing and the verb it was
# asked to perform, so a captured log is unambiguous about which copy ran.
# ---------------------------------------------------------------------------

$ResolvedScriptPath = if ($PSCommandPath) { $PSCommandPath } else { $MyInvocation.MyCommand.Path }
Write-Host "studio-edits.ps1: running from $ResolvedScriptPath, Verb=$Verb"

# ---------------------------------------------------------------------------
# RESULT_JSON contract -- exactly one line, on every code path.
# ---------------------------------------------------------------------------

$script:ResultEmitted     = $false
$script:CurrentSessionId  = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
$script:Screenshots       = New-Object System.Collections.Generic.List[string]

# One entry per posted gesture (Invoke-PostedCommandGesture call), recording
# the before/after screenshot pair captured around that specific gesture so a
# caller can see exactly what the desktop looked like on both sides of each
# gesture without having to correlate the flat $script:Screenshots list by
# filename convention. Surfaced in RESULT_JSON as "gestures".
$script:Gestures          = New-Object System.Collections.Generic.List[hashtable]

# This change observability, reported at the top level of RESULT_JSON:
#   gestureTier       -- always 'posted' (the only gesture there is).
#   cursorMovedByDriver   -- true only if this driver itself issued any cursor or mouse input
#                            during the gesture. The driver is posted-only, so always false.
#   cursorMovedExternally -- GetCursorPos differed between gesture start and end (the user, or
#                            anything other than this driver, moved the cursor).
#   cursorMoved           -- kept for compatibility: "either" (cursorMovedByDriver -or
#                            cursorMovedExternally).
#   foregroundChanged -- GetForegroundWindow differed between start and end.
#   dialogText        -- the confirmation dialog's text ($script:ConfirmationDialogText).
#   dialogWarnings    -- the dialog's Static texts other than the verb phrase and
#                        the question, non-empty, in control order
#                        ($script:DialogWarnings).
$script:GestureTierUsed        = 'posted'
$script:GestureStartCursor     = $null
$script:GestureStartForeground = [IntPtr]::Zero
$script:CursorMoved            = $false
$script:CursorMovedByDriver    = $false
$script:CursorMovedExternally  = $false
$script:ForegroundChanged      = $false
$script:ConfirmationDialogText = ''
$script:DialogWarnings         = @()

# Set once the Logix Designer main window is found (Get-StudioWindow).
# Wait-ForConfirmationDialog reads this to require the dialog be owned by the
# main Studio window.
$script:StudioWindowHandle = [IntPtr]::Zero

# Milliseconds Wait-StudioIdle spent before the command was posted; $null until
# it has run. Merged into RESULT_JSON as detail.studioIdleWaitMs.
$script:StudioIdleWaitMs = $null

# Errors-pane toggle bookkeeping, reported at the top level of RESULT_JSON:
#   errorsPaneToggled      -- $true when -ErrorsPaneToggleCommandId was posted at least once.
#   retriedAfterPaneToggle -- $true when the second post of the verb produced the dialog.
#   paneToggleRestored     -- $true when the toggle was posted a second time to restore the pane.
$script:ErrorsPaneToggled      = $false
$script:RetriedAfterPaneToggle = $false
$script:PaneToggleRestored     = $false
#   errorsPaneLayout -- where the pane was left after a toggle: 'auto-hide', 'docked', 'hidden' or
#                             'unknown' (the window enumeration failed); $null (omitted) when the pane was never toggled.
#   errorsPaneTabsBefore / errorsPaneTabsAfter -- integer count of auto-hide tabs (BCGPControlBar id 1 under a
#                             BCGPDockBar) sampled before the driver's toggle and at the end; $null when unknown.
#                             For the bench only; the server ignores them.
$script:ErrorsPaneLayout       = $null
$script:ErrorsPaneTabsBefore   = $null
$script:ErrorsPaneTabsAfter    = $null
$script:PaneSampleBefore       = $null

<#
.SYNOPSIS
    Print the single RESULT_JSON: line this script's callers parse. A safety
    latch guarantees a second call is a silent no-op -- the contract is
    exactly one line, never zero, never two.
#>
function Write-ResultJson {
    param(
        [Parameter(Mandatory = $true)]  [bool]     $Ok,
        [Parameter(Mandatory = $false)] [string]   $Reason = '',
        [Parameter(Mandatory = $false)] [hashtable] $Detail = @{}
    )

    if ($script:ResultEmitted) {
        return
    }

    if ($null -ne $script:StudioIdleWaitMs) {
        $Detail = $Detail.Clone()
        $Detail['studioIdleWaitMs'] = [int] $script:StudioIdleWaitMs
    }

    $doc = [ordered]@{
        ok          = $Ok
        verb        = $Verb
        sessionId   = $script:CurrentSessionId
        reason      = $Reason
        gestureTier       = $script:GestureTierUsed
        dialogText        = $script:ConfirmationDialogText
        dialogWarnings    = @($script:DialogWarnings)
        errorsPaneToggled      = [bool] $script:ErrorsPaneToggled
        retriedAfterPaneToggle = [bool] $script:RetriedAfterPaneToggle
        paneToggleRestored     = [bool] $script:PaneToggleRestored
        errorsPaneLayout       = $script:ErrorsPaneLayout
        errorsPaneTabsBefore   = $script:ErrorsPaneTabsBefore
        errorsPaneTabsAfter    = $script:ErrorsPaneTabsAfter
        cursorMovedByDriver   = [bool] $script:CursorMovedByDriver
        cursorMovedExternally = [bool] $script:CursorMovedExternally
        cursorMoved       = [bool] $script:CursorMoved
        foregroundChanged = [bool] $script:ForegroundChanged
        screenshots = @($script:Screenshots)
        gestures    = @($script:Gestures)
        detail      = $Detail
    }

    Write-Output ("RESULT_JSON: " + ($doc | ConvertTo-Json -Compress -Depth 6))
    $script:ResultEmitted = $true
}

# ---------------------------------------------------------------------------
# Step 1 -- Refuse to run in session 0.
#
# Session 0 is the non-interactive "services" session on Windows. It has no
# desktop: screen capture either
# silently do nothing or throw there instead of failing loudly. Detecting
# this up front -- before Add-Type, before any window/screenshot code runs --
# means a mis-scheduled run (e.g. a scheduled task created without the
# Interactive principal this script requires) fails with a clear,
# actionable RESULT_JSON instead of hanging or corrupting partial state.
# ---------------------------------------------------------------------------

if ($script:CurrentSessionId -eq 0) {
    Write-Host "FAIL: refusing to run in session 0 (non-interactive services session -- no desktop, cannot drive the Studio 5000 GUI)" -ForegroundColor Red
    Write-ResultJson -Ok $false -Reason 'session_0_refused' -Detail @{
        message   = 'This script must run on the interactive desktop (session 1+). Launch it via a scheduled task with an Interactive principal (Register-ScheduledTask / Start-ScheduledTask), not from a service or a session-0 scheduled task.'
        sessionId = $script:CurrentSessionId
    }
    exit 1
}

# ---------------------------------------------------------------------------
# Step 2 -- GUI plumbing: Win32 P/Invoke for window discovery and posted
# messages, System.Drawing/Windows.Forms for screenshots.
# ---------------------------------------------------------------------------

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$Win32GuiSource = @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class Win32Gui
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

    /// <summary>Current cursor position as "x,y" (empty string if unreadable).</summary>
    public static string GetCursorPosition()
    {
        POINT p;
        return GetCursorPos(out p) ? (p.X + "," + p.Y) : "";
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // Child-window lookup used to find the confirmation dialog's Yes button by
    // class+text so it can be driven by a posted WM_COMMAND.
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

    // Posted commands: PostMessage is asynchronous and touches neither
    // the cursor, the keyboard nor the foreground window.
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_COMMAND = 0x0111;
    // Stock MessageBox IDYES. NOT what Studio's Finalize dialog uses: its Yes
    // is a custom MFC button with control ID 1 and ignores WM_COMMAND 6
    // (the dialog stays open and the edit stays pending).
    // PostYes therefore posts the Yes button's OWN control ID.
    public const int IDYES = 6;

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    /// <summary>
    /// Poll until the window no longer exists or the timeout elapses.
    /// Returns true when the window is gone.
    /// </summary>
    public static bool WaitForWindowGone(IntPtr hWnd, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!IsWindow(hWnd)) { return true; }
            System.Threading.Thread.Sleep(200);
        }
        return !IsWindow(hWnd);
    }

    /// <summary>
    /// Post WM_COMMAND(commandId, 0) to a window: the same message MFC
    /// receives when the matching menu item is chosen. Returns the
    /// PostMessage result.
    /// </summary>
    public static bool PostCommand(IntPtr hWnd, int commandId)
    {
        return PostMessage(hWnd, WM_COMMAND, (IntPtr)commandId, IntPtr.Zero);
    }

    // Read-only child-window enumeration (the Errors pane layout read): no message is sent to any window.
    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);

    /// <summary>One descendant window of the Studio main window, as read by EnumDescendants.</summary>
    public sealed class ChildInfo
    {
        public IntPtr Handle;
        public string ClassName = "";
        public string Text = "";
        public bool Visible;
        public int ControlId;
        public string ParentClassName = "";
    }

    private static string ClassNameOf(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) { return ""; }
        var sb = new StringBuilder(256);
        return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>
    /// Every descendant window of hWndParent (class, text, visibility, control id, parent's class).
    /// Read-only: EnumChildWindows, GetClassName, GetWindowText, IsWindowVisible, GetDlgCtrlID, GetParent.
    /// </summary>
    public static List<ChildInfo> EnumDescendants(IntPtr hWndParent)
    {
        var handles = new List<IntPtr>();
        EnumChildWindows(hWndParent, delegate (IntPtr h, IntPtr l) { handles.Add(h); return true; }, IntPtr.Zero);
        var result = new List<ChildInfo>(handles.Count);
        foreach (IntPtr h in handles)
        {
            var text = new StringBuilder(256);
            GetWindowText(h, text, text.Capacity);
            result.Add(new ChildInfo
            {
                Handle = h,
                ClassName = ClassNameOf(h),
                Text = text.ToString(),
                Visible = IsWindowVisible(h),
                ControlId = GetDlgCtrlID(h),
                ParentClassName = ClassNameOf(GetParent(h))
            });
        }
        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    public const uint WM_NULL = 0x0000;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>
    /// True when the window's message loop answered SendMessageTimeout(WM_NULL,
    /// SMTO_ABORTIFHUNG, timeoutMs): the window is not busy or hung.
    /// </summary>
    public static bool AnswersWmNull(IntPtr hWnd, uint timeoutMs)
    {
        IntPtr result;
        return SendMessageTimeout(hWnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, timeoutMs, out result) != IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    public static extern int GetDlgCtrlID(IntPtr hWnd);

    // The Studio confirm dialog's "&No" is posted as WM_COMMAND command id 2
    // (per the requirement; note this is NOT the stock IDNO == 7).
    public const int NoCommandId = 2;

    /// <summary>
    /// Locate the confirm dialog's Yes button: the child "Button" with
    /// control ID 1 and text "&amp;Yes". Returns IntPtr.Zero when no such
    /// child exists (the caller then posts the dialog's own No).
    /// </summary>
    public static IntPtr FindYesButton(IntPtr dialogHandle)
    {
        IntPtr child = IntPtr.Zero;
        while (true)
        {
            child = FindWindowEx(dialogHandle, child, "Button", "&Yes");
            if (child == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            if (GetDlgCtrlID(child) == 1)
            {
                return child;
            }
        }
    }

    /// <summary>
    /// Press a confirm dialog's Yes button by posting WM_COMMAND(&lt;Yes
    /// button control ID&gt;, hYesButton) to the dialog. Returns false when the Yes button hwnd
    /// cannot be found (nothing is posted then).
    /// </summary>
    public static bool PostYes(IntPtr dialogHandle)
    {
        IntPtr yes = FindYesButton(dialogHandle);
        if (yes == IntPtr.Zero)
        {
            return false;
        }
        return PostMessage(dialogHandle, WM_COMMAND, (IntPtr)GetDlgCtrlID(yes), yes);
    }

    /// <summary>
    /// Refuse a confirm dialog by posting WM_COMMAND(2, hNoButton) to it
    /// (hNoButton is IntPtr.Zero when the "&amp;No" child is not found).
    /// </summary>
    public static bool PostNo(IntPtr dialogHandle)
    {
        IntPtr no = FindWindowEx(dialogHandle, IntPtr.Zero, "Button", "&No");
        return PostMessage(dialogHandle, WM_COMMAND, (IntPtr)NoCommandId, no);
    }

    /// <summary>
    /// Text of every "Static" child of a dialog, joined with a newline.
    /// </summary>
    public static string GetDialogStaticText(IntPtr dialogHandle)
    {
        StringBuilder all = new StringBuilder();
        IntPtr child = IntPtr.Zero;
        while (true)
        {
            child = FindWindowEx(dialogHandle, child, "Static", null);
            if (child == IntPtr.Zero)
            {
                break;
            }
            StringBuilder sb = new StringBuilder(1024);
            GetWindowText(child, sb, sb.Capacity);
            if (sb.Length > 0)
            {
                if (all.Length > 0) { all.Append('\n'); }
                all.Append(sb.ToString());
            }
        }
        return all.ToString();
    }

    /// <summary>
    /// Text of every non-empty "Static" child of a dialog, one array entry per
    /// control, in control (z) order. Unlike GetDialogStaticText the entries
    /// are not joined, so a multi-line warning stays one entry.
    /// </summary>
    public static string[] GetDialogStaticTexts(IntPtr dialogHandle)
    {
        List<string> all = new List<string>();
        IntPtr child = IntPtr.Zero;
        while (true)
        {
            child = FindWindowEx(dialogHandle, child, "Static", null);
            if (child == IntPtr.Zero)
            {
                break;
            }
            StringBuilder sb = new StringBuilder(1024);
            GetWindowText(child, sb, sb.Capacity);
            if (sb.Length > 0)
            {
                all.Add(sb.ToString());
            }
        }
        return all.ToArray();
    }

    // Class-name + owner-handle lookups used to pin down the Cancel/Accept
    // confirmation dialog (v38): Studio 5000 reuses the literal title
    // "Logix Designer" for more than one window, so title alone is never a
    // safe discriminator -- see FindWindowByExactTitleClassAndOwner.
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    // GetWindow command that returns the owner of the given window.
    public const uint GW_OWNER = 4;

    // (handle, title, owning-process-id) triple returned by the
    // process+title-regex Studio window lookup below. A plain KeyValuePair
    // has no slot for the process id, and the whole point of this struct is
    // to carry it through to the "already running" log line and the
    // RESULT_JSON detail a caller sees.
    public struct WindowInfo
    {
        public IntPtr Handle;
        public string Title;
        public int ProcessId;
    }

    public static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Locate a top-level, visible window matched by exact title AND exact
    /// window class AND owner handle -- never by a substring match on title
    /// alone. Studio 5000 v38 reuses the literal title "Logix Designer" for
    /// more than one window/dialog, so title alone cannot safely identify
    /// the Cancel/Accept confirmation dialog; requiring its window class
    /// (the standard Win32 dialog-box class, "#32770") and that it is owned
    /// by the Studio main window handle pins down exactly the one dialog
    /// this script means to click. Returns IntPtr.Zero when no window
    /// satisfies all three conditions.
    /// </summary>
    public static IntPtr FindWindowByExactTitleClassAndOwner(string exactTitle, string expectedClassName, IntPtr expectedOwner)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hWnd)) return true;

            string title = GetWindowTitle(hWnd);
            if (!string.Equals(title, exactTitle, StringComparison.Ordinal)) return true;

            var classSb = new StringBuilder(256);
            GetClassName(hWnd, classSb, classSb.Capacity);
            if (!string.Equals(classSb.ToString(), expectedClassName, StringComparison.Ordinal)) return true;

            if (GetWindow(hWnd, GW_OWNER) != expectedOwner) return true;

            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Locate any visible top-level window of class <paramref name="expectedClassName"/>
    /// (the dialog class "#32770") owned by <paramref name="expectedOwner"/>, whatever
    /// its title. Used to detect a Studio dialog that is already open before a verb is
    /// posted. Returns IntPtr.Zero when there is none.
    /// </summary>
    public static IntPtr FindOpenDialogOwnedBy(string expectedClassName, IntPtr expectedOwner)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hWnd)) return true;

            var classSb = new StringBuilder(256);
            GetClassName(hWnd, classSb, classSb.Capacity);
            if (!string.Equals(classSb.ToString(), expectedClassName, StringComparison.Ordinal)) return true;

            if (GetWindow(hWnd, GW_OWNER) != expectedOwner) return true;

            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Two-phase Studio window lookup, matched by owning process rather than
    /// by any product-marketing window-title text:
    ///   1. Enumerate every running process named <paramref name="processName"/>
    ///      (e.g. "LogixDesigner") and collect its process ids.
    ///   2. Enumerate visible top-level windows, keep only those owned by one
    ///      of those process ids, and keep only the ones whose title matches
    ///      <paramref name="titleRegexPattern"/>.
    /// Returns every match (there can legitimately be more than one Logix
    /// Designer window/process open) so the caller can refuse to launch a
    /// second instance whenever this list is non-empty.
    /// </summary>
    public static List<WindowInfo> FindStudioWindowsByProcessAndTitleRegex(string processName, string titleRegexPattern)
    {
        var found = new List<WindowInfo>();

        var pids = new HashSet<int>();
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName(processName))
        {
            pids.Add(proc.Id);
            proc.Dispose();
        }
        if (pids.Count == 0)
        {
            return found;
        }

        var titleRegex = new System.Text.RegularExpressions.Regex(titleRegexPattern);
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hWnd)) return true;

            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (!pids.Contains((int)pid)) return true;

            string title = GetWindowTitle(hWnd);
            if (!string.IsNullOrEmpty(title) && titleRegex.IsMatch(title))
            {
                found.Add(new WindowInfo { Handle = hWnd, Title = title, ProcessId = (int)pid });
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

}
"@

Add-Type -TypeDefinition $Win32GuiSource -Language CSharp

<#
.SYNOPSIS
    Capture the full virtual screen and write it as a PNG named after the
    current verb and the calling step. Returns the written file path and
    records it in $script:Screenshots so it is always included in the final
    RESULT_JSON, whatever the outcome of the step that requested it.
#>
function Save-Screenshot {
    param([Parameter(Mandatory = $true)] [string] $Step)

    if (-not (Test-Path -LiteralPath $ScreenshotDir -PathType Container)) {
        New-Item -ItemType Directory -Path $ScreenshotDir -Force | Out-Null
    }

    $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp    = New-Object System.Drawing.Bitmap ($bounds.Width, $bounds.Height)
    $gfx    = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $gfx.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
        $file  = Join-Path $ScreenshotDir "studio-edits-$Verb-$Step-$stamp.png"
        $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
        $script:Screenshots.Add($file) | Out-Null
        Write-Host "    screenshot ($Step): $file"
        return $file
    } finally {
        $gfx.Dispose()
        $bmp.Dispose()
    }
}

<#
.SYNOPSIS
    Poll for the Cancel/Accept confirmation dialog, up to $TimeoutSeconds.
    Located by exact title ($ConfirmDialogExactTitle) AND exact window class
    ($ConfirmDialogClassName) AND owner handle ($script:StudioWindowHandle)
    -- never by a substring match on title alone (Studio 5000 reuses the
    literal title "Logix Designer" for more than one window). Returns the
    dialog handle, or [IntPtr]::Zero on timeout -- the genuine "no dialog
    appeared" signal (command_not_applicable).
#>
function Wait-ForConfirmationDialog {
    param([Parameter(Mandatory = $true)] [int] $TimeoutSeconds)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $hwnd = [Win32Gui]::FindWindowByExactTitleClassAndOwner(
            $ConfirmDialogExactTitle, $ConfirmDialogClassName, $script:StudioWindowHandle)
        if ($hwnd -ne [IntPtr]::Zero) { return $hwnd }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return [IntPtr]::Zero
}

# Seconds after the confirmation deadline during which a late dialog is still
# validated and completed; after that it is closed with its own No (never left open).
$script:LateDialogGraceSeconds = 2

# Set when a Studio dialog was already open before the verb was posted (nothing was
# posted); the dialog's title and text are kept for the result.
$script:StudioModalOpen        = $false
$script:ModalDialogTitle       = ''
# Set when the expected dialog appeared too late to complete and was closed with No.
$script:ConfirmationLateClosed = $false

<#
.SYNOPSIS
    Pure: what to do with a confirmation dialog first seen $SecondsAfterDeadline
    seconds after the confirmation deadline. 'complete' (validate its text and
    answer it) when that is within $GraceSeconds (default 2), otherwise 'close'
    (post the dialog's own No/Cancel; never leave it open).
#>
function Get-LateDialogDisposition {
    param(
        [Parameter(Mandatory = $true)]  [double] $SecondsAfterDeadline,
        [Parameter(Mandatory = $false)] [double] $GraceSeconds = 2
    )
    if ($SecondsAfterDeadline -le $GraceSeconds) { return 'complete' }
    return 'close'
}

<#
.SYNOPSIS
    Before any verb is posted: if a #32770 dialog owned by the Studio main window
    is already open, record its title and text (script:ModalDialogTitle,
    script:ConfirmationDialogText), set script:StudioModalOpen and return $true.
    The caller then posts nothing and reports reason studio_modal_open.
#>
function Test-StudioModalDialogOpen {
    param([Parameter(Mandatory = $true)] [IntPtr] $WindowHandle)

    $dlg = [Win32Gui]::FindOpenDialogOwnedBy($ConfirmDialogClassName, $WindowHandle)
    if ($dlg -eq [IntPtr]::Zero) { return $false }
    $script:ModalDialogTitle       = [Win32Gui]::GetWindowTitle($dlg)
    $script:ConfirmationDialogText = [Win32Gui]::GetDialogStaticText($dlg)
    $script:StudioModalOpen        = $true
    Write-Host "    a Studio dialog is already open ('$($script:ModalDialogTitle)') -- posting nothing" -ForegroundColor Yellow
    return $true
}

<#
.SYNOPSIS
    Keep looking for the confirmation dialog for $script:LateDialogGraceSeconds after
    $Deadline (the moment Wait-ForConfirmationDialog gave up). Returns the dialog
    handle when it appears in that window (the caller validates and completes), else
    [IntPtr]::Zero.
#>
function Wait-ForLateConfirmationDialog {
    param([Parameter(Mandatory = $true)] [datetime] $Deadline)

    $limit = $Deadline.AddSeconds($script:LateDialogGraceSeconds)
    while ((Get-Date) -lt $limit) {
        $hwnd = [Win32Gui]::FindWindowByExactTitleClassAndOwner(
            $ConfirmDialogExactTitle, $ConfirmDialogClassName, $script:StudioWindowHandle)
        if ($hwnd -ne [IntPtr]::Zero) { return $hwnd }
        Start-Sleep -Milliseconds 100
    }
    return [IntPtr]::Zero
}

<#
.SYNOPSIS
    Last look before the driver exits: if the expected confirmation dialog is open
    now, it appeared after the late window, so close it with its own No/Cancel
    (posted, never clicked), record its text in script:ConfirmationDialogText and set
    script:ConfirmationLateClosed. A confirmation is never left open. Returns $true
    when a dialog was closed.
#>
function Close-LateConfirmationDialog {
    param([Parameter(Mandatory = $true)] [datetime] $Deadline)

    $hwnd = [Win32Gui]::FindWindowByExactTitleClassAndOwner(
        $ConfirmDialogExactTitle, $ConfirmDialogClassName, $script:StudioWindowHandle)
    if ($hwnd -eq [IntPtr]::Zero) { return $false }
    $late = ((Get-Date) - $Deadline).TotalSeconds
    if ((Get-LateDialogDisposition -SecondsAfterDeadline $late -GraceSeconds $script:LateDialogGraceSeconds) -ne 'close') { return $false }
    $script:ConfirmationDialogText = [Win32Gui]::GetDialogStaticText($hwnd)
    [Win32Gui]::PostNo($hwnd) | Out-Null
    $script:ConfirmationLateClosed = $true
    Write-Host "    confirmation dialog appeared ${late}s after the deadline -- posted its own No" -ForegroundColor Yellow
    return $true
}

<#
.SYNOPSIS
    Wait, up to $MaxSeconds (default 10), for the Studio main window to answer
    SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, 1000), i.e. for Studio to be
    idle enough to take a posted command. Records the time spent in
    $script:StudioIdleWaitMs (reported as detail.studioIdleWaitMs). Returns
    $true when the window answered; $false when it did not within the budget,
    in which case the caller still posts (the confirmation wait then decides).
#>
function Wait-StudioIdle {
    param(
        [Parameter(Mandatory = $true)]  [IntPtr] $WindowHandle,
        [Parameter(Mandatory = $false)] [int]    $MaxSeconds = 10
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $idle = $false
    do {
        if ([Win32Gui]::AnswersWmNull($WindowHandle, 1000)) { $idle = $true; break }
        Start-Sleep -Milliseconds 250
    } while ($sw.Elapsed.TotalSeconds -lt $MaxSeconds)
    $sw.Stop()
    $script:StudioIdleWaitMs = [int] $sw.ElapsedMilliseconds
    if ($idle) {
        Write-Host "    Studio main window answered WM_NULL after $($script:StudioIdleWaitMs) ms"
    } else {
        Write-Host "    Studio main window did not answer WM_NULL within ${MaxSeconds}s -- posting anyway" -ForegroundColor Yellow
    }
    return $idle
}

<#
.SYNOPSIS
    Find the Logix Designer window: first by owning process
    (-StudioProcessName, default 'LogixDesigner'), then by title regex
    (-StudioWindowTitleRegex) among that process's windows -- never by the
    product-marketing name ("Studio 5000"), which is not stable across Logix
    Designer versions. Returns every match; the caller uses a non-empty
    result to decide whether Studio is running at all.
#>
function Find-StudioDesignerWindows {
    return [Win32Gui]::FindStudioWindowsByProcessAndTitleRegex($StudioProcessName, $StudioWindowTitleRegex)
}

<#
.SYNOPSIS
    Parse the online state and mode token from a Logix Designer window title
    using the bracketed prefix (`[Emulator] - Logix Designer - ...`,
    `[Rem Run] - Logix Designer - ...`) -- never a substring match on the
    literal word "online", which the real online title never contains (PRD
    0075b defect 2: online titles are exactly this bracketed-prefix form).
    Absence of the bracketed prefix means offline.
.OUTPUTS
    A hashtable @{ Online = <bool>; Mode = <string, or $null when offline> }.
#>
function Get-StudioOnlineStateFromTitle {
    param([Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Title)

    if ($Title -and $Title -match '^\[([^\]]+)\] - ') {
        return @{ Online = $true; Mode = $Matches[1] }
    }
    return @{ Online = $false; Mode = $null }
}

<#
.SYNOPSIS
    Determine the edits indicator ("No Edits" / "Edits Present") for the
    status verb from the window title: 'Edits Present' when the title carries
    that phrase. Defaults to 'No Edits' otherwise -- a window with no
    evidence of pending edits is presumed clean. (Studio 5000 is an MFC
    application with no accessibility tree for this control, so the title is
    the only source.)
#>
function Get-StudioEditsIndicator {
    param(
        [Parameter(Mandatory = $true)] [IntPtr] $WindowHandle,
        [Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Title
    )

    if ($Title -and $Title -match '(?i)edits\s+present') {
        return 'Edits Present'
    }

    return 'No Edits'
}

# ---------------------------------------------------------------------------
# Step 3 -- Find the already-open Logix Designer window (read-only) and refuse
# with a typed reason when Studio is not running or is offline. This script
# never launches Studio and never takes it online.
# ---------------------------------------------------------------------------

# Message carried by both the StudioNotRunning and StudioOffline refusals.
$script:StudioEngineerMessage = 'ask the engineer to open the project in Studio and go online by Upload; lgx-mcp never launches Studio or goes online through it'

# Exit codes for the window-targeting refusals. The server maps them
# (and the RESULT_JSON reason) to the kinds StudioWindowNotFound and
# StudioWindowAmbiguous. Every other refusal keeps exit code 1.
$script:ExitWindowNotFound  = 20
$script:ExitWindowAmbiguous = 21

<#
.SYNOPSIS
    The title the target window must have, in both shapes Studio produces:
    "<controller> in <file>.ACD" (a saved project) and "<controller>" alone (an
    upload never saved, so the title has no file name). An empty controller
    renders as "*". With a project file only the "in <file>.ACD" form is
    accepted; without one either form is ("<controller> (any file, or an
    unsaved upload)"). Pure (no GUI): used for the match and named in every
    refusal.
#>
function Get-ExpectedStudioTitle {
    param(
        [Parameter(Mandatory = $false)] [AllowEmptyString()] [string] $Controller = '',
        [Parameter(Mandatory = $false)] [AllowEmptyString()] [string] $ProjectFile = ''
    )

    $file = ''
    if (-not [string]::IsNullOrWhiteSpace($ProjectFile)) {
        $file = ($ProjectFile.Trim() -split '[\\/]')[-1] -replace '(?i)\.ACD$', ''
    }
    $ctrl = if ([string]::IsNullOrWhiteSpace($Controller)) { '*' } else { $Controller.Trim() }
    if ($file) { return "$ctrl in $file.ACD" }
    return "$ctrl (any file, or an unsaved upload)"
}

<#
.SYNOPSIS
    Pure check: does a Logix Designer window title name the expected controller
    (and project file)? After the optional online-mode prefix ("[Rem Run] - ")
    and "Logix Designer - " it accepts two shapes, case-insensitively:
      1. "<controller> in <file>.ACD" plus any suffix Studio appends (" [1756-L83E
         38.11]", " - Online", a modified-marker "*"); the file must end at
         ".ACD" ("Echo.ACD" never matches "Echo.ACD.bak").
      2. "<controller>" followed by " [", "*" or the end of the title (no file
         name: an upload that was never saved). Only accepted when ProjectFile
         is empty; with a project file only shape 1 with that file matches.
    The controller must match whole ("Echo" never matches "Echo2", and
    "PLC_LN02" never matches "PLC_LN02_Template"). An empty controller matches
    any, and an empty file matches any file.
#>
function Test-StudioWindowTitleMatch {
    param(
        [Parameter(Mandatory = $true)]  [AllowEmptyString()] [string] $Title,
        [Parameter(Mandatory = $false)] [AllowEmptyString()] [string] $Controller = '',
        [Parameter(Mandatory = $false)] [AllowEmptyString()] [string] $ProjectFile = ''
    )

    $file = ''
    if (-not [string]::IsNullOrWhiteSpace($ProjectFile)) {
        $file = ($ProjectFile.Trim() -split '[\\/]')[-1] -replace '(?i)\.ACD$', ''
    }
    $ctrlPattern = if ([string]::IsNullOrWhiteSpace($Controller)) { '.+?' } else { [regex]::Escape($Controller.Trim()) }
    $filePattern = if ($file) { [regex]::Escape($file) } else { '.+?' }
    $prefix = '(?i)^(\[[^\]]+\] - )?Logix Designer - ' + $ctrlPattern
    if ($Title -match ($prefix + ' in ' + $filePattern + '\.ACD(?![\w.])')) { return $true }
    if (-not $file -and $Title -match ($prefix + '(?= \[|\*|\s*$)')) { return $true }
    return $false
}

<#
.SYNOPSIS
    Pure selection of the target window from the open Logix Designer windows
    (anything with a .Title). Returns @{ Status; Window; Count; ExpectedTitle }
    where Status is 'ok' (exactly one match), 'not_found' (windows are open but
    none match) or 'ambiguous' (more than one matches). It never picks "the
    first". The caller clicks only when Status is 'ok'.
#>
function Select-StudioWindowForTarget {
    param(
        [Parameter(Mandatory = $true)]  [AllowEmptyCollection()] [object[]] $Windows,
        [Parameter(Mandatory = $false)] [AllowEmptyString()] [string] $Controller = '',
        [Parameter(Mandatory = $false)] [AllowEmptyString()] [string] $ProjectFile = ''
    )

    $expected = Get-ExpectedStudioTitle -Controller $Controller -ProjectFile $ProjectFile
    $matched  = @($Windows | Where-Object {
        Test-StudioWindowTitleMatch -Title $_.Title -Controller $Controller -ProjectFile $ProjectFile
    })
    $status = if (@($matched).Count -eq 1) { 'ok' } elseif (@($matched).Count -eq 0) { 'not_found' } else { 'ambiguous' }
    return @{
        Status        = $status
        Window        = if (@($matched).Count -eq 1) { $matched[0] } else { $null }
        MatchCount    = @($matched).Count
        ExpectedTitle = $expected
    }
}

# Set by Get-StudioWindow when it returns $null for a window-targeting reason
# (not_found / ambiguous); the main flow turns it into the typed refusal.
$script:WindowRefusal = $null

<#
.SYNOPSIS
    Return the ([Win32Gui]::WindowInfo) of the already-open Logix Designer
    window for -ExpectedController / -ExpectedProject, or $null when there is
    none (Studio not running) or no single window qualifies
    ($script:WindowRefusal is then set to the not_found / ambiguous refusal).
    Read-only: it never launches Studio, however it is called.
#>
function Get-StudioWindow {
    $script:WindowRefusal = $null
    $existing = @(Find-StudioDesignerWindows)
    if (@($existing).Count -eq 0) {
        Write-Host "    no Logix Designer window found (process '$StudioProcessName', title regex '$StudioWindowTitleRegex')"
        return $null
    }

    $pick = Select-StudioWindowForTarget -Windows $existing -Controller $ExpectedController -ProjectFile $ExpectedProject
    $titles = (($existing | ForEach-Object { "'$($_.Title)'" }) -join ', ')
    if ($pick.Status -eq 'not_found') {
        Write-Host "    no Logix Designer window matches '$($pick.ExpectedTitle)' (open: $titles)"
        $script:WindowRefusal = @{
            Reason   = 'studio_window_not_found'
            Kind     = 'StudioWindowNotFound'
            ExitCode = $script:ExitWindowNotFound
            Text     = "No open Logix Designer window is titled '$($pick.ExpectedTitle)' (open windows: $titles). Nothing was clicked: open that project in Studio (a saved project is titled '<controller> in <file>.ACD', an unsaved upload just '<controller>'), or close the other Studio windows"
            Detail   = @{ expectedTitle = $pick.ExpectedTitle; matches = 0; openWindows = @($existing | ForEach-Object { $_.Title }) }
        }
        return $null
    }
    if ($pick.Status -eq 'ambiguous') {
        Write-Host "    $($pick.MatchCount) Logix Designer windows match '$($pick.ExpectedTitle)' (open: $titles)"
        $script:WindowRefusal = @{
            Reason   = 'studio_window_ambiguous'
            Kind     = 'StudioWindowAmbiguous'
            ExitCode = $script:ExitWindowAmbiguous
            Text     = "$($pick.MatchCount) open Logix Designer windows match '$($pick.ExpectedTitle)' (open windows: $titles). Nothing was clicked: close the duplicate Studio windows"
            Detail   = @{ expectedTitle = $pick.ExpectedTitle; matches = $pick.MatchCount; openWindows = @($existing | ForEach-Object { $_.Title }) }
        }
        return $null
    }

    $only = $pick.Window
    Write-Host "    Logix Designer window open (PID=$($only.ProcessId)) for '$($pick.ExpectedTitle)': $($only.Title)"
    $script:StudioWindowHandle = $only.Handle
    return $only
}

<#
.SYNOPSIS
    Record the cursor position and foreground window at the start of a
    gesture (Start-GestureObservation) and compare them at its end
    (Complete-GestureObservation), setting $script:CursorMovedExternally,
    $script:CursorMovedByDriver (always $false: the driver issues no cursor or
    mouse input), $script:CursorMoved ("either", for compatibility) and
    $script:ForegroundChanged for RESULT_JSON.
#>
function Start-GestureObservation {
    $script:GestureStartCursor     = [Win32Gui]::GetCursorPosition()
    $script:GestureStartForeground = [Win32Gui]::GetForegroundWindow()
}

function Complete-GestureObservation {
    if ($null -eq $script:GestureStartCursor) { return }
    $script:CursorMovedExternally = ([Win32Gui]::GetCursorPosition() -ne $script:GestureStartCursor)
    # The driver is posted-only: it never issues cursor or mouse input.
    $script:CursorMovedByDriver   = $false
    $script:CursorMoved           = ($script:CursorMovedByDriver -or $script:CursorMovedExternally)
    $script:ForegroundChanged = ([Win32Gui]::GetForegroundWindow() -ne $script:GestureStartForeground)
}

# Set by Confirm-DialogTextThenPressYes when the dialog text did not match;
# the gesture returns immediately (there is no fallback gesture) and the main flow
# reports confirmation_dialog_mismatch with this text.
$script:ConfirmationMismatch     = $false
# Set when Yes was posted but the confirmation dialog was still open after
# $ConfirmDialogTimeoutSeconds (the press was not taken).
$script:ConfirmationStillOpen    = $false
# Set (posted-only verbs) when the Yes button hwnd was not found; No was posted.
$script:ConfirmationYesMissing   = $false
$script:ConfirmationDialogText   = ''

<#
.SYNOPSIS
    Pure text check for the confirmation dialog: returns $true only when
    $DialogText contains the verb phrase for $Verb naming $ExpectedProgram --
    cancel: "Cancel edits for program '<Program>'"; accept: "Finalize all
    edits in program '<Program>'"; test / untest / assemble: "<Verb> edits for
    program '<Program>'" -- compared case-insensitively. An empty
    $ExpectedProgram never matches.
#>
function Test-ConfirmationDialogText {
    param(
        [Parameter(Mandatory = $true)] [string] $DialogText,
        [Parameter(Mandatory = $true)] [string] $VerbName,
        [Parameter(Mandatory = $false)] [string] $ExpectedProgram = ''
    )

    if ([string]::IsNullOrWhiteSpace($ExpectedProgram)) { return $false }
    $phrase = switch ($VerbName) {
        'cancel' { "Cancel edits for program '$ExpectedProgram'" }
        'accept' { "Finalize all edits in program '$ExpectedProgram'" }
        'test'     { "Test edits for program '$ExpectedProgram'" }
        'untest'   { "Untest edits for program '$ExpectedProgram'" }
        'assemble' { "Assemble edits for program '$ExpectedProgram'" }
        default  { return $false }
    }
    return ($DialogText.IndexOf($phrase, [StringComparison]::OrdinalIgnoreCase) -ge 0)
}

<#
.SYNOPSIS
    Pure: the confirmation dialog's warnings. From the dialog's
    Static texts (one entry per control, in control order) drop the verb
    phrase for $VerbName / $ExpectedProgram and the question (the last Static
    whose trimmed text ends in '?'), then keep every remaining non-empty
    text, trimmed, in order. Returns a (possibly empty) string array.
#>
function Get-DialogWarnings {
    param(
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]] $StaticTexts,
        [Parameter(Mandatory = $true)] [string] $VerbName,
        [Parameter(Mandatory = $false)] [string] $ExpectedProgram = ''
    )

    $questionIndex = -1
    for ($i = @($StaticTexts).Count - 1; $i -ge 0; $i--) {
        if ($StaticTexts[$i].Trim().EndsWith('?')) { $questionIndex = $i; break }
    }

    $warnings = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt @($StaticTexts).Count; $i++) {
        if ($i -eq $questionIndex) { continue }
        $text = $StaticTexts[$i].Trim()
        if ($text.Length -eq 0) { continue }
        if (Test-ConfirmationDialogText -DialogText $text -VerbName $VerbName -ExpectedProgram $ExpectedProgram) { continue }
        $warnings.Add($text)
    }
    return , $warnings.ToArray()
}

<#
.SYNOPSIS
    Read the confirmation dialog's Static text and validate it BEFORE pressing
    Yes. On a mismatch post the dialog's own No (WM_COMMAND 2 with the "&No"
    button hwnd), take no further action, record the text in
    $script:ConfirmationDialogText, set $script:ConfirmationMismatch and
    return $false. On a match press the dialog's own Yes by posting
    WM_COMMAND(<Yes button control ID>, hYesButton) -- the BN_CLICKED the
    button itself would send. When the Yes button hwnd cannot be found, post
    No, set $script:ConfirmationYesMissing and return $false: there is no
    other way to press it.
#>
function Confirm-DialogTextThenPressYes {
    param(
        [Parameter(Mandatory = $true)] [IntPtr] $DialogHandle,
        [Parameter(Mandatory = $true)] [string] $Step
    )

    $dialogText = [Win32Gui]::GetDialogStaticText($DialogHandle)
    $script:ConfirmationDialogText = $dialogText
    if (-not (Test-ConfirmationDialogText -DialogText $dialogText -VerbName $Verb -ExpectedProgram $Program)) {
        Write-Host "    confirmation dialog text does not match verb '$Verb' / program '$Program' -- posting No: $dialogText" -ForegroundColor Yellow
        [Win32Gui]::PostNo($DialogHandle) | Out-Null
        $script:ConfirmationMismatch   = $true
        $script:ConfirmationDialogText = $dialogText
        return $false
    }

    Write-Host "    confirmation dialog text validated -- posting the dialog's own Yes ($Step)"
    $script:DialogWarnings = @(Get-DialogWarnings -StaticTexts ([string[]] [Win32Gui]::GetDialogStaticTexts($DialogHandle)) `
        -VerbName $Verb -ExpectedProgram $Program)
    if (-not [Win32Gui]::PostYes($DialogHandle)) {
        Write-Host "    Yes button hwnd not found for verb '$Verb' -- posting No" -ForegroundColor Yellow
        [Win32Gui]::PostNo($DialogHandle) | Out-Null
        $script:ConfirmationYesMissing = $true
        return $false
    }
    # Never report success on the post alone: the dialog must close.
    if (-not [Win32Gui]::WaitForWindowGone($DialogHandle, $ConfirmDialogTimeoutSeconds * 1000)) {
        Write-Host "    confirmation dialog still open ${ConfirmDialogTimeoutSeconds}s after Yes -- stopping" -ForegroundColor Yellow
        $script:ConfirmationStillOpen = $true
        return $false
    }
    return $true
}

<#
.SYNOPSIS
    Read the Errors pane layout from its window (#61): a read-only Win32 enumeration of the Studio main window's
    child windows (EnumChildWindows + GetClassName + GetWindowText + IsWindowVisible + GetDlgCtrlID + GetParent).
    No focus, no message and no click: the only writes anywhere near the pane are the at most one PostMessage of
    the toggle command made by Invoke-PostedCommandGesture / Complete-ErrorsPaneLayout.

    Why Win32 and not an accessibility-tree query: the docked pane's automation name carries trailing spaces ('Errors  ', window
    text '  Errors  '), and in the auto-hide and hidden layouts no element named like Errors is exposed at all, so a
    name query reads 'absent' in every layout and cannot tell them apart. The window tree can.

    What it finds:
      - the Errors bar: a child of class BCGPControlBar* whose trimmed text is like Errors* (control id 349 in
        Logix Designer v38). It exists as a window only while the pane is docked or its auto-hide flyout is out;
        errorsBarVisible is IsWindowVisible of it.
      - the auto-hide tabs: the strip of nameless BCGPControlBar* windows with control id 1 whose parent is a
        BCGPDockBar*; autoHideTabs is their count. The count changes when the pane enters or leaves auto-hide
        (or is hidden), which is how Resolve-ErrorsPaneLayout tells auto-hide from hidden.

    Returns an object with errorsBar (the window handle, $null when there is none), errorsBarVisible, autoHideTabs,
    childCount and failed ($true when the enumeration threw). Never throws.
#>
function Get-ErrorsPaneLayout {
    param([Parameter(Mandatory = $true)] [IntPtr] $WindowHandle)

    $sample = [pscustomobject] @{
        errorsBar        = $null
        errorsBarVisible = $false
        autoHideTabs     = 0
        childCount       = 0
        failed           = $false
    }
    try {
        $children = @([Win32Gui]::EnumDescendants($WindowHandle))
        $sample.childCount = @($children).Count
        foreach ($c in $children) {
            if ($c.ClassName -notlike 'BCGPControlBar*') { continue }
            if ($null -eq $sample.errorsBar -and $c.Text.Trim() -like 'Errors*') {
                $sample.errorsBar        = $c.Handle
                $sample.errorsBarVisible = [bool] $c.Visible
            }
            if ($c.ControlId -eq 1 -and $c.ParentClassName -like 'BCGPDockBar*') {
                $sample.autoHideTabs++
            }
        }
    } catch {
        Write-Host "    Errors-pane window enumeration failed ($_)" -ForegroundColor Yellow
        $sample.failed = $true
    }
    return $sample
}

<#
.SYNOPSIS
    Pure classification of the Errors pane layout (#61); no Win32 call, no side effect. -Before is the
    Get-ErrorsPaneLayout sample taken immediately before the driver's own toggle post ($null when none was taken),
    -Now the sample taken at the end. Returns one of four strings:
      'unknown'   -- Now is missing, the enumeration threw, or it found no Studio child windows at all;
      'docked'    -- the Errors bar is visible;
      'auto-hide' -- the bar is not visible, Before exists and had no visible bar, and the number of auto-hide tabs
                     now equals the sampled number;
      'hidden'    -- anything else.
    A wrong 'hidden' makes the caller post the toggle again and flip a visible pane, so every doubt that is not
    'unknown' still has to be backed by the window evidence above.
#>
function Resolve-ErrorsPaneLayout {
    param(
        [Parameter(Mandatory = $false)] $Before = $null,
        [Parameter(Mandatory = $false)] $Now = $null
    )

    if ($null -eq $Now -or $Now.failed -or $Now.childCount -le 0) { return 'unknown' }
    if ($Now.errorsBarVisible) { return 'docked' }
    if ($null -ne $Before -and -not $Before.failed -and -not $Before.errorsBarVisible `
            -and $Now.autoHideTabs -eq $Before.autoHideTabs) {
        return 'auto-hide'
    }
    return 'hidden'
}

<#
.SYNOPSIS
    Never finish in HIDDEN, and never flip the pane on a wrong read (#60, #61): after an Errors-pane toggle, once
    the verb has completed and no dialog is open, read the layout (Get-ErrorsPaneLayout) and classify it with
    Resolve-ErrorsPaneLayout against $script:PaneSampleBefore. Only when the result is 'hidden' is
    -ErrorsPaneToggleCommandId posted once more (the only write here: one PostMessage, no WM_SYSCOMMAND, no command
    32173, no click, no foreground change), then 600 ms later the layout is read and classified again. Records the
    result in $script:ErrorsPaneLayout and the auto-hide tab counts in $script:ErrorsPaneTabsBefore/After. The extra
    600 ms wait and the window reads stay inside the server's Session1ScriptRunner.GestureBudgetSeconds.
#>
function Complete-ErrorsPaneLayout {
    if (-not $script:ErrorsPaneToggled) { return }
    if ([Win32Gui]::FindOpenDialogOwnedBy($ConfirmDialogClassName, $WindowHandle) -ne [IntPtr]::Zero) {
        $script:ErrorsPaneLayout = 'unknown'
        return
    }

    $before = $script:PaneSampleBefore
    $now    = Get-ErrorsPaneLayout -WindowHandle $WindowHandle
    $layout = Resolve-ErrorsPaneLayout -Before $before -Now $now
    if ($layout -eq 'hidden') {
        Write-Host "    Errors pane is hidden: posting WM_COMMAND $ErrorsPaneToggleCommandId once more to bring it back docked" -ForegroundColor Yellow
        if ([Win32Gui]::PostCommand($WindowHandle, $ErrorsPaneToggleCommandId)) {
            Start-Sleep -Milliseconds 600
            $now    = Get-ErrorsPaneLayout -WindowHandle $WindowHandle
            $layout = Resolve-ErrorsPaneLayout -Before $before -Now $now
        }
    }
    $script:ErrorsPaneLayout = $layout
    if ($null -ne $before -and -not $before.failed) { $script:ErrorsPaneTabsBefore = [int] $before.autoHideTabs }
    if ($layout -ne 'unknown') { $script:ErrorsPaneTabsAfter = [int] $now.autoHideTabs }
}

<#
.SYNOPSIS
    The only gesture this script makes: post WM_COMMAND
    $CommandId to the Studio main window (resolved by Studio PID in
    Get-StudioWindow) and, if the confirmation dialog appears within
    $ConfirmTimeoutSeconds, validate its text and press the
    dialog's own Yes via Confirm-DialogTextThenPressYes. No cursor, no
    foreground change, no keystrokes. Records the before/after screenshot pair
    in $script:Gestures. Returns $true when the dialog appeared, matched, was
    answered Yes and closed; $false when no dialog appeared (the command was
    disabled or not applicable: the caller reports command_not_applicable) or
    when the text mismatched / Yes could not be pressed (the matching
    $script:Confirmation* flag is set and the caller reports it).
#>
function Invoke-PostedCommandGesture {
    param(
        [Parameter(Mandatory = $true)] [IntPtr] $WindowHandle,
        [Parameter(Mandatory = $true)] [int]    $CommandId,
        [Parameter(Mandatory = $true)] [string] $Step
    )

    $script:GestureTierUsed = 'posted'
    $beforeScreenshot = Save-Screenshot -Step "$Step-before"

    # A dialog already open on Studio would swallow the command or be mistaken for the
    # confirmation: post nothing and report studio_modal_open.
    if (Test-StudioModalDialogOpen -WindowHandle $WindowHandle) {
        return $false
    }

    Wait-StudioIdle -WindowHandle $WindowHandle | Out-Null

    # No dialog after a successful accept can be Logix Designer's Errors pane showing as an
    # auto-hide flyout. So the verb command is posted at most twice: when the first post raises no
    # dialog, post -ErrorsPaneToggleCommandId once, wait 600 ms, take the screenshot pair, and post
    # the verb command again. If that also raises no dialog, toggle the pane back and give up.
    # Never before the first attempt; never while any dialog is open (the modal check above stays first).
    $result          = $false
    $confirmDlg      = [IntPtr]::Zero
    $confirmDeadline = Get-Date
    $paneGestures    = @()
    $attemptNumber   = 0
    while ($true) {
        $attemptNumber++
        Write-Host "    posted: WM_COMMAND $CommandId to the Studio main window (no cursor, no focus, no keys)"
        if (-not [Win32Gui]::PostCommand($WindowHandle, $CommandId)) {
            Write-Host "    PostMessage of command $CommandId failed" -ForegroundColor Yellow
            if ($attemptNumber -eq 1) { return $false }
            break
        }

        $confirmDeadline = (Get-Date).AddSeconds($ConfirmTimeoutSeconds)
        $confirmDlg = Wait-ForConfirmationDialog -TimeoutSeconds $ConfirmTimeoutSeconds
        if ($confirmDlg -eq [IntPtr]::Zero) {
            # A slow Studio may show the dialog just after the deadline: still complete it
            # when it appears within the late window, while the driver is still running.
            $confirmDlg = Wait-ForLateConfirmationDialog -Deadline $confirmDeadline
        }
        if ($confirmDlg -ne [IntPtr]::Zero) {
            if ($attemptNumber -gt 1) { $script:RetriedAfterPaneToggle = $true }
            break
        }
        if ($attemptNumber -gt 1) {
            # The retry also raised nothing: put the pane back where it was (it is a toggle).
            if ([Win32Gui]::FindOpenDialogOwnedBy($ConfirmDialogClassName, $WindowHandle) -eq [IntPtr]::Zero) {
                Write-Host "    retry produced no dialog: posting WM_COMMAND $ErrorsPaneToggleCommandId again to restore the Errors pane" -ForegroundColor Yellow
                if ([Win32Gui]::PostCommand($WindowHandle, $ErrorsPaneToggleCommandId)) {
                    $script:PaneToggleRestored = $true
                    Start-Sleep -Milliseconds 600
                }
            }
            break
        }
        if ([Win32Gui]::FindOpenDialogOwnedBy($ConfirmDialogClassName, $WindowHandle) -ne [IntPtr]::Zero) { break }

        $toggleBefore = Save-Screenshot -Step "$Step-pane-toggle-before"
        # The pane is now in its starting layout (auto-hide with the flyout out, docked or hidden): sample it
        # before the first toggle post so Complete-ErrorsPaneLayout can tell auto-hide from hidden afterwards.
        $script:PaneSampleBefore = Get-ErrorsPaneLayout -WindowHandle $WindowHandle
        Write-Host "    no dialog: posting WM_COMMAND $ErrorsPaneToggleCommandId (Errors pane toggle) once, then retrying" -ForegroundColor Yellow
        if (-not [Win32Gui]::PostCommand($WindowHandle, $ErrorsPaneToggleCommandId)) {
            Write-Host "    PostMessage of command $ErrorsPaneToggleCommandId failed -- not retrying" -ForegroundColor Yellow
            break
        }
        $script:ErrorsPaneToggled = $true
        Start-Sleep -Milliseconds 600
        $toggleAfter = Save-Screenshot -Step "$Step-pane-toggle-after"
        $paneGestures += @{ step = "$Step-pane-toggle"; beforeScreenshot = $toggleBefore; afterScreenshot = $toggleAfter }
    }

    if ($confirmDlg -eq [IntPtr]::Zero) {
        Write-Host "    posted command $CommandId produced no confirmation dialog within ${ConfirmTimeoutSeconds}s (disabled or not applicable)" -ForegroundColor Yellow
    } else {
        $result = Confirm-DialogTextThenPressYes -DialogHandle $confirmDlg -Step $Step
    }

    $afterScreenshot = Save-Screenshot -Step "$Step-after"

    # Never leave a confirmation open: a dialog that appeared after the late window is
    # closed with its own No before the driver exits.
    if ($confirmDlg -eq [IntPtr]::Zero) {
        Close-LateConfirmationDialog -Deadline $confirmDeadline | Out-Null
    }

    # Never finish with the Errors pane hidden after a toggle (read-only window reads, at most one more post).
    Complete-ErrorsPaneLayout
    $script:Gestures.Add(@{
        step             = $Step
        beforeScreenshot = $beforeScreenshot
        afterScreenshot  = $afterScreenshot
    }) | Out-Null
    foreach ($g in $paneGestures) { $script:Gestures.Add($g) | Out-Null }
    return $result
}

<#
.SYNOPSIS
    test / untest / assemble: post the verb's command ID
    (-TestCommandId 32061, -UntestCommandId 32062, -AssembleCommandId 32063)
    to the Studio main window and validate the dialog text before Yes. No
    dialog within $ConfirmTimeoutSeconds means the command was
    disabled or not applicable (for example Assemble on an untested zone) and
    the caller reports command_not_applicable. Returns $true when the dialog
    appeared, matched and was answered Yes and closed.
#>
function Invoke-PostedOnlyVerbGesture {
    param([Parameter(Mandatory = $true)] [IntPtr] $WindowHandle)

    $commandId = switch ($Verb) {
        'test'     { $TestCommandId }
        'untest'   { $UntestCommandId }
        'assemble' { $AssembleCommandId }
        default    { throw "Invoke-PostedOnlyVerbGesture: verb '$Verb' is not a test / untest / assemble verb." }
    }

    return (Invoke-PostedCommandGesture -WindowHandle $WindowHandle -CommandId $commandId -Step "$Verb-posted")
}

<#
.SYNOPSIS
    cancel: Logic -> Online Edits -> Cancel Accepted Program Edits, posted as
    WM_COMMAND -CancelCommandId. Posted only; no dialog is command_not_applicable.
#>
function Invoke-CancelGesture {
    param([Parameter(Mandatory = $true)] [IntPtr] $WindowHandle)
    return (Invoke-PostedCommandGesture -WindowHandle $WindowHandle -CommandId $CancelCommandId -Step 'cancel-posted')
}

<#
.SYNOPSIS
    accept: Logic -> Online Edits -> Finalize All Edits in Program (NOT
    Assemble Accepted Program Edits), posted as WM_COMMAND -AcceptCommandId.
    Posted only; no dialog is command_not_applicable.
#>
function Invoke-AcceptGesture {
    param([Parameter(Mandatory = $true)] [IntPtr] $WindowHandle)
    return (Invoke-PostedCommandGesture -WindowHandle $WindowHandle -CommandId $AcceptCommandId -Step 'accept-posted')
}

# ---------------------------------------------------------------------------
# Step 4 -- Main flow. Wrapped in try/catch/finally so every path -- success,
# a caught gesture failure, or an unhandled exception -- still emits exactly
# one RESULT_JSON line before the script exits.
# ---------------------------------------------------------------------------

$exitCode = 0

# Reports a posted-verb outcome (cancel / accept / test / untest / assemble).
# Sets $script:VerbExitCode (its output stream carries the RESULT_JSON line, so it
# cannot also return the code). Every failure path names its reason; no dialog at all
# is command_not_applicable for every verb.
function Write-PostedVerbResult {
    param(
        [Parameter(Mandatory = $true)] [bool]   $GestureOk,
        [Parameter(Mandatory = $true)] [string] $SuccessReason,
        [Parameter(Mandatory = $true)] [string] $SuccessMessage
    )

    if ($GestureOk) {
        Write-ResultJson -Ok $true -Reason $SuccessReason -Detail @{ message = $SuccessMessage }
        $script:VerbExitCode = 0
        return
    }
    if ($script:StudioModalOpen) {
        Write-ResultJson -Ok $false -Reason 'studio_modal_open' -Detail @{
            message     = "A Studio dialog ('$($script:ModalDialogTitle)') is already open; nothing was posted. Answer or close the open Studio dialog, then retry."
            dialogTitle = $script:ModalDialogTitle
            dialogText  = $script:ConfirmationDialogText
        }
    } elseif ($script:ConfirmationLateClosed) {
        Write-ResultJson -Ok $false -Reason 'confirmation_late_closed' -Detail @{
            message    = "The $Verb confirmation dialog appeared after the ${ConfirmTimeoutSeconds}s wait (plus the $($script:LateDialogGraceSeconds)s late window); it was closed with its own No and nothing was changed."
            dialogText = $script:ConfirmationDialogText
        }
    } elseif ($script:ConfirmationMismatch) {
        Write-ResultJson -Ok $false -Reason 'confirmation_dialog_mismatch' -Detail @{
            message    = "The confirmation dialog text did not name the expected verb and program ('$Verb' / '$Program'); No was posted and no further action was taken."
            dialogText = $script:ConfirmationDialogText
        }
    } elseif ($script:ConfirmationStillOpen) {
        Write-ResultJson -Ok $false -Reason 'confirmation_dialog_still_open' -Detail @{
            message    = "Yes was posted to the confirmation dialog but it was still open after ${ConfirmDialogTimeoutSeconds}s; no further action was taken."
            dialogText = $script:ConfirmationDialogText
        }
    } elseif ($script:ConfirmationYesMissing) {
        Write-ResultJson -Ok $false -Reason 'confirmation_yes_button_not_found' -Detail @{
            message    = "The Yes button of the $Verb confirmation dialog was not found; No was posted and no further action was taken."
            dialogText = $script:ConfirmationDialogText
        }
    } else {
        Write-ResultJson -Ok $false -Reason 'command_not_applicable' -Detail @{
            message = "The posted $Verb command produced no confirmation dialog within ${ConfirmTimeoutSeconds}s: the command is disabled or not applicable in the current Studio state (for example Assemble needs a tested zone). The command is only ever posted; the only other posts are the single Errors-pane toggle retry and, after a toggle, one more toggle when the pane is found hidden (errorsPaneToggled=$($script:ErrorsPaneToggled), paneToggleRestored=$($script:PaneToggleRestored))."
        }
    }
    $script:VerbExitCode = 1
}

try {
    Write-Host "==> studio-edits: verb=$Verb sessionId=$($script:CurrentSessionId)"

    $studioWindow = Get-StudioWindow
    $refusal      = $null
    if ($null -eq $studioWindow -and $null -ne $script:WindowRefusal) {
        # Windows are open but none (or several) is the session's project: refuse,
        # click nothing.
        $refusal = $script:WindowRefusal
    } elseif ($null -eq $studioWindow) {
        $refusal = @{
            Reason = 'studio_not_running'
            Kind   = 'StudioNotRunning'
            Text   = "No Logix Designer window is open: $($script:StudioEngineerMessage)."
        }
    } else {
        Save-Screenshot -Step 'project-open' | Out-Null
        Save-Screenshot -Step 'online-state' | Out-Null

        # Every verb except status posts an online-edits command, which only works
        # against a Studio that is online. The bracketed title prefix is the signal.
        if ($Verb -ne 'status') {
            $gateTitle = [Win32Gui]::GetWindowTitle($studioWindow.Handle)
            if (-not (Get-StudioOnlineStateFromTitle -Title $gateTitle).Online) {
                $refusal = @{
                    Reason = 'studio_offline'
                    Kind   = 'StudioOffline'
                    Text   = "Studio is open but offline ('$gateTitle'): $($script:StudioEngineerMessage)."
                }
            }
        }
    }

    if ($null -ne $refusal) {
        $refusalDetail = @{
            kind    = $refusal.Kind
            message = $refusal.Text
        }
        if ($refusal.ContainsKey('Detail')) {
            foreach ($key in $refusal.Detail.Keys) { $refusalDetail[$key] = $refusal.Detail[$key] }
        }
        Write-ResultJson -Ok $false -Reason $refusal.Reason -Detail $refusalDetail
        $exitCode = if ($refusal.ContainsKey('ExitCode')) { [int] $refusal.ExitCode } else { 1 }
    } else {
    switch ($Verb) {
        'status' {
            Save-Screenshot -Step 'status' | Out-Null

            $statusTitle     = [Win32Gui]::GetWindowTitle($studioWindow.Handle)
            $onlineState     = Get-StudioOnlineStateFromTitle -Title $statusTitle
            $editsIndicator  = Get-StudioEditsIndicator -WindowHandle $studioWindow.Handle -Title $statusTitle

            # ok=true here reports that {online, mode, editsIndicator,
            # faults} were successfully read, independent of what they
            # say -- a title-derived online=
            # $false or editsIndicator='Edits Present' is still a
            # successful read, not a failure.
            Write-ResultJson -Ok $true -Reason 'status_captured' -Detail @{
                online         = $onlineState.Online
                mode           = $onlineState.Mode
                editsIndicator = $editsIndicator
                faults         = @()
                message        = 'Online state read from the bracketed window-title prefix; editsIndicator read from the window title text.'
            }
        }
        'cancel' {
            Start-GestureObservation
            $gestureOk = Invoke-CancelGesture -WindowHandle $studioWindow.Handle
            Complete-GestureObservation
            Save-Screenshot -Step 'cancel-result' | Out-Null
            Write-PostedVerbResult -GestureOk $gestureOk -SuccessReason 'cancel_gesture_sent' `
                -SuccessMessage 'Cancel Accepted Program Edits was posted to the Studio main window (posted WM_COMMAND only) and the confirmation dialog was answered Yes and closed. Verify the result visually (or via logix_pending_edits) -- see the final screenshot.'
            $exitCode = $script:VerbExitCode
        }
        { $_ -in @('test', 'untest', 'assemble') } {
            Start-GestureObservation
            $gestureOk = Invoke-PostedOnlyVerbGesture -WindowHandle $studioWindow.Handle
            Complete-GestureObservation
            Save-Screenshot -Step "$Verb-result" | Out-Null
            Write-PostedVerbResult -GestureOk $gestureOk -SuccessReason "${Verb}_gesture_sent" `
                -SuccessMessage "The $Verb command was posted to the Studio main window (posted WM_COMMAND only) and the confirmation dialog was answered Yes and closed. Check zoneState with logix_pending_edits (program TestEdits) and verify behaviour with logix_read."
            $exitCode = $script:VerbExitCode
        }
        'accept' {
            Start-GestureObservation
            $gestureOk = Invoke-AcceptGesture -WindowHandle $studioWindow.Handle
            Complete-GestureObservation
            Save-Screenshot -Step 'accept-result' | Out-Null
            Write-PostedVerbResult -GestureOk $gestureOk -SuccessReason 'accept_gesture_sent' `
                -SuccessMessage 'Finalize All Edits in Program was posted to the Studio main window (posted WM_COMMAND only) and the confirmation dialog was answered Yes and closed. Verify the result visually (or via logix_pending_edits) -- see the final screenshot.'
            $exitCode = $script:VerbExitCode
        }
    }
    }
} catch {
    Write-Host "FAIL: unhandled exception: $_" -ForegroundColor Red
    try { Save-Screenshot -Step 'unhandled-exception' | Out-Null } catch { }
    Write-ResultJson -Ok $false -Reason 'unhandled_exception' -Detail @{ message = "$_" }
    $exitCode = 1
} finally {
    # Safety net: whatever happened above, guarantee the RESULT_JSON contract
    # (exactly one line, never zero) holds even for a code path this script's
    # author did not anticipate.
    if (-not $script:ResultEmitted) {
        Write-ResultJson -Ok $false -Reason 'no_result_emitted_before_finally' -Detail @{}
        $exitCode = 1
    }
}

exit $exitCode
