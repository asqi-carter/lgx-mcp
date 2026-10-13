namespace LogixMcp.Tests;

/// <summary>
/// <see cref="StudioConfirmWait"/>: the server passes <c>-ConfirmTimeoutSeconds 15</c>
/// when a lifecycle verb follows its own publish or lifecycle call on the same routine within 30 s,
/// and the driver's 10 s default otherwise.
/// </summary>
public class StudioConfirmWaitTests
{
    private DateTimeOffset _now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    // Each test gets its own instance and clock: no process-wide state is read or written.
    private readonly StudioConfirmWait _wait;

    public StudioConfirmWaitTests() => _wait = new StudioConfirmWait(() => _now);

    /// <summary>Calls on a private instance never reach the shared instance production code uses.</summary>
    [Fact]
    public void SharedInstance_IsUntouchedByPrivateInstances()
    {
        _wait.Record("GuardProgram", "GuardRoutine");
        Assert.Equal(15, _wait.SecondsFor("GuardProgram", "GuardRoutine"));
        Assert.Equal(10, StudioConfirmWait.Shared.SecondsFor("GuardProgram", "GuardRoutine"));
    }

    [Fact]
    public void NoEarlierCall_UsesTheDefault10()
    {
        Assert.Equal(10, _wait.SecondsFor("MainProgram", "MainRoutine"));
    }

    [Fact]
    public void LifecycleVerbWithin30sOfOwnCall_SameRoutine_Passes15()
    {
        _wait.Record("MainProgram", "MainRoutine");
        _now += TimeSpan.FromSeconds(30);
        Assert.Equal(15, _wait.SecondsFor("MainProgram", "MainRoutine"));
        Assert.Equal(15, _wait.SecondsFor("mainprogram", "mainroutine"));
    }

    [Fact]
    public void LifecycleVerbAfter30s_FallsBackToDefault()
    {
        _wait.Record("MainProgram", "MainRoutine");
        _now += TimeSpan.FromSeconds(30.5);
        Assert.Equal(10, _wait.SecondsFor("MainProgram", "MainRoutine"));
    }

    [Fact]
    public void DifferentRoutine_IsNotAffected()
    {
        _wait.Record("MainProgram", "MainRoutine");
        Assert.Equal(10, _wait.SecondsFor("MainProgram", "Other"));
        Assert.Equal(10, _wait.SecondsFor("Other", "MainRoutine"));
    }

    [Fact]
    public void BlankNames_AreIgnored()
    {
        _wait.Record("", "");
        Assert.Equal(10, _wait.SecondsFor("", ""));
    }
}
