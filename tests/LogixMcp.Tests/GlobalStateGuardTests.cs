using System.Runtime.CompilerServices;

namespace LogixMcp.Tests;

/// <summary>
/// Guards that no test leaves process-wide state changed. The environment is snapshotted when the test
/// assembly loads; this class runs in a non-parallel collection, so nothing else runs while it checks
/// and every earlier serial test must already have restored what it changed.
/// </summary>
[Collection("ProcessEnvironment")]
public class GlobalStateGuardTests
{
    private static readonly string[] Watched =
    {
        "LGX_CONNECT_SYNC_BUDGET_S", "LGX_ENABLE_CONTROLLER_DOWNLOAD", "LGX_AUDIT_FAIL_CLOSED",
        "LGX_GATE_COMMAND_PREFIXES", "LGX_WRITE_ALLOWLIST", "LOGIX_SDK_PATH", "AB_SERVER_HOST", "AB_SERVER_PATH",
    };

    private static Dictionary<string, string?> _baseline = new();
    private static TextWriter? _baselineError;

    [ModuleInitializer]
    internal static void Snapshot()
    {
        _baseline = Watched.ToDictionary(n => n, n => Environment.GetEnvironmentVariable(n));
        _baselineError = Console.Error;
    }

    /// <summary>Every watched environment variable has the value it had when the test assembly loaded.</summary>
    [Fact]
    public void EnvironmentVariables_AreRestoredByTests()
    {
        foreach (var (name, expected) in _baseline)
            Assert.Equal(expected, Environment.GetEnvironmentVariable(name));
    }

    /// <summary>Stderr was not left redirected.</summary>
    [Fact]
    public void ConsoleError_IsNotLeftRedirected() => Assert.Same(_baselineError, Console.Error);

    /// <summary>The shared confirm-wait tracker is never written by the unit tests (they use their own instances).</summary>
    [Fact]
    public void SharedStudioConfirmWait_HasNoRecordForTestRoutines()
    {
        Assert.Equal(StudioConfirmWait.DefaultSeconds, StudioConfirmWait.Shared.SecondsFor("MainProgram", "MainRoutine"));
    }
}
