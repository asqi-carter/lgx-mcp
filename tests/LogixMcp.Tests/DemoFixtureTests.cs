using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Fixture-load tests that verify <c>fixtures/Demo.L5X</c> is a valid L5X file
/// that L5Sharp can parse without errors.
/// </summary>
public class DemoFixtureTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    /// <summary>
    /// Loads Demo.L5X and asserts the controller name matches the expected value.
    /// </summary>
    [Fact]
    public void DemoL5X_Load_ControllerNameIsDemoController()
    {
        var content = L5X.Load(FixturePath);

        Assert.Equal("DemoController", content.Controller.Name);
    }

    /// <summary>
    /// Loads Demo.L5X and asserts the total number of controller-scoped tags
    /// equals seven (Cmd_MotorStart, Cmd_SpeedSetpoint, Sim_LineSpeed,
    /// Safety_OK, Motor_Run, Cmd_Motor, SomeRandomTag).
    /// </summary>
    [Fact]
    public void DemoL5X_Load_ControllerTagCountIsSix()
    {
        var content = L5X.Load(FixturePath);

        Assert.Equal(7, content.Tags.Count);
    }
}
