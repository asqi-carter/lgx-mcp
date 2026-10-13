// ── EnvironmentProbe — process-environment facts for Studio automation ─────────
//
// SDK-free: compiled in SDK and non-SDK builds (logix_doctor's studio check and the
// Session1ScriptRunner launch-mode decision both consume IEnvironmentProbe).
using System.Diagnostics;

/// <summary>
/// Reports facts about the current process environment that decide how
/// <see cref="Session1ScriptRunner.RunAsync"/> reaches the Studio desktop.
/// Injectable so the decision can be tested without a real Windows desktop.
/// </summary>
public interface IEnvironmentProbe
{
    /// <summary>Windows session id of the current process (0 = services session).</summary>
    int CurrentSessionId { get; }

    /// <summary>Whether the current process token is elevated.</summary>
    bool IsElevated { get; }

    /// <summary>Session ids of all running <c>LogixDesigner</c> (Studio 5000) processes.</summary>
    IReadOnlyList<int> LogixDesignerSessionIds { get; }

    /// <summary>
    /// Whether a running Studio process's integrity level is higher than the
    /// current process's (so UI automation from here would be blocked by UIPI).
    /// </summary>
    bool StudioIntegrityHigherThanCurrent { get; }
}

/// <summary>
/// Default <see cref="IEnvironmentProbe"/> backed by the real process, token
/// and Studio process list. Every member is evaluated lazily and tolerates
/// failure (reporting the conservative value) rather than throwing.
/// </summary>
public sealed class EnvironmentProbe : IEnvironmentProbe
{
    private const string StudioProcessName = "LogixDesigner";

    /// <inheritdoc />
    public int CurrentSessionId
    {
        get
        {
            using var self = Process.GetCurrentProcess();
            return self.SessionId;
        }
    }

    /// <inheritdoc />
    public bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return false;
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<int> LogixDesignerSessionIds
    {
        get
        {
            var ids = new List<int>();
            foreach (var p in Process.GetProcessesByName(StudioProcessName))
            {
                try { ids.Add(p.SessionId); }
                catch { /* process exited */ }
                finally { p.Dispose(); }
            }
            return ids;
        }
    }

    /// <inheritdoc />
    public bool StudioIntegrityHigherThanCurrent
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return false;
            try
            {
                using var self = Process.GetCurrentProcess();
                var currentSession = self.SessionId;
                var currentRid = GetIntegrityRid(self.Id);
                return EvaluateStudioIntegrity(currentSession, currentRid, EnumerateStudios(currentSession));
            }
            catch { /* conservative default below */ }
            return false;
        }
    }

    // Lazily yields (session, rid) for each LogixDesigner process. The token
    // is opened only for processes in the current session; other-session
    // processes are reported with a null rid but are skipped by
    // EvaluateStudioIntegrity before the rid is considered.
    private static IEnumerable<(int session, int? rid)> EnumerateStudios(int currentSession)
    {
        foreach (var p in Process.GetProcessesByName(StudioProcessName))
        {
            using (p)
            {
                int session;
                try { session = p.SessionId; }
                catch { continue; /* process exited */ }
                yield return (session, session == currentSession ? GetIntegrityRid(p.Id) : null);
            }
        }
    }

    /// <summary>
    /// Pure, platform-independent integrity decision: true when any Studio
    /// process in <paramref name="currentSession"/> runs at a higher integrity
    /// level than the current process. Studios in other sessions are ignored.
    /// Within the current session a Studio whose token could not be read
    /// (<c>rid</c> null) conservatively counts as higher.
    /// </summary>
    /// <param name="currentSession">Session id of the current process.</param>
    /// <param name="currentRid">Integrity RID of the current process, or null if unreadable.</param>
    /// <param name="studios">Session id and integrity RID (null if unreadable) per Studio process.</param>
    internal static bool EvaluateStudioIntegrity(
        int currentSession, int? currentRid, IEnumerable<(int session, int? rid)> studios)
    {
        foreach (var (session, rid) in studios)
        {
            if (session != currentSession)
                continue;
            if (rid is null || (currentRid is not null && rid > currentRid))
                return true;
        }
        return false;
    }

    // Integrity RID (e.g. 0x2000 medium, 0x3000 high) or null when the token
    // could not be opened.
    private static int? GetIntegrityRid(int pid)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        const uint TokenQuery = 0x0008;
        const int TokenIntegrityLevel = 25;

        var hProcess = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (hProcess == IntPtr.Zero)
            return null;
        IntPtr hToken = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(hProcess, TokenQuery, out hToken))
                return null;

            NativeMethods.GetTokenInformation(hToken, TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);
            if (needed == 0)
                return null;
            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!NativeMethods.GetTokenInformation(hToken, TokenIntegrityLevel, buffer, needed, out _))
                    return null;
                // TOKEN_MANDATORY_LABEL begins with the label SID pointer.
                var sid = System.Runtime.InteropServices.Marshal.ReadIntPtr(buffer);
                var countPtr = NativeMethods.GetSidSubAuthorityCount(sid);
                var count = System.Runtime.InteropServices.Marshal.ReadByte(countPtr);
                var ridPtr = NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1));
                return System.Runtime.InteropServices.Marshal.ReadInt32(ridPtr);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
        }
        finally
        {
            if (hToken != IntPtr.Zero) NativeMethods.CloseHandle(hToken);
            NativeMethods.CloseHandle(hProcess);
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(
            IntPtr token, int infoClass, IntPtr info, uint length, out uint returnLength);

        [System.Runtime.InteropServices.DllImport("advapi32.dll")]
        internal static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [System.Runtime.InteropServices.DllImport("advapi32.dll")]
        internal static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    }
}
