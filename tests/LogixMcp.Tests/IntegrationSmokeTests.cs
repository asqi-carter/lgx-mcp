namespace LogixMcp.Tests;

/// <summary>
/// Smoke tests that verify the <see cref="IntegrationTestBase"/> infrastructure
/// correctly skips when <c>AB_SERVER_HOST</c> is absent, and exposes the right
/// connection parameters when the variable is present.
/// </summary>
/// <remarks>
/// These tests exercise the fixture wiring itself, not the PLC stack.  They
/// are deliberately lightweight and are always safe to run.
/// </remarks>
[Collection("ProcessEnvironment")]
public class IntegrationFixtureTests
{
    // ── IntegrationFixture construction ───────────────────────────────────────

    [Fact]
    public void IntegrationFixture_WhenHostEnvVarAbsent_IsAvailableIsFalse()
    {
        // Arrange – ensure the env var is absent for the duration of this test.
        var previous = Environment.GetEnvironmentVariable("AB_SERVER_HOST");
        try
        {
            Environment.SetEnvironmentVariable("AB_SERVER_HOST", null);

            // Act
            var fixture = new IntegrationFixture();

            // Assert
            Assert.False(fixture.IsAvailable);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AB_SERVER_HOST", previous);
        }
    }

    [Fact]
    public void IntegrationFixture_WhenHostEnvVarAbsent_SkipReasonIsNonEmpty()
    {
        var previous = Environment.GetEnvironmentVariable("AB_SERVER_HOST");
        try
        {
            Environment.SetEnvironmentVariable("AB_SERVER_HOST", null);

            var fixture = new IntegrationFixture();

            Assert.False(string.IsNullOrWhiteSpace(fixture.SkipReason));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AB_SERVER_HOST", previous);
        }
    }

    [Fact]
    public void IntegrationFixture_WhenHostEnvVarSet_IsAvailableIsTrue()
    {
        var previous = Environment.GetEnvironmentVariable("AB_SERVER_HOST");
        try
        {
            Environment.SetEnvironmentVariable("AB_SERVER_HOST", "198.51.100.1");

            var fixture = new IntegrationFixture();

            Assert.True(fixture.IsAvailable);
            Assert.Equal("198.51.100.1", fixture.Host);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AB_SERVER_HOST", previous);
        }
    }

    [Fact]
    public void IntegrationFixture_WhenPathEnvVarAbsent_PathDefaultsTo1Comma0()
    {
        var previous = Environment.GetEnvironmentVariable("AB_SERVER_PATH");
        try
        {
            Environment.SetEnvironmentVariable("AB_SERVER_PATH", null);

            var fixture = new IntegrationFixture();

            Assert.Equal("1,0", fixture.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AB_SERVER_PATH", previous);
        }
    }

    [Fact]
    public void IntegrationFixture_WhenPathEnvVarSet_PathMatchesEnvVar()
    {
        var previousPath = Environment.GetEnvironmentVariable("AB_SERVER_PATH");
        try
        {
            Environment.SetEnvironmentVariable("AB_SERVER_PATH", "2,3");

            var fixture = new IntegrationFixture();

            Assert.Equal("2,3", fixture.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AB_SERVER_PATH", previousPath);
        }
    }

    [Fact]
    public void IntegrationFixture_WhenHostPresent_SkipReasonIsEmpty()
    {
        var previous = Environment.GetEnvironmentVariable("AB_SERVER_HOST");
        try
        {
            Environment.SetEnvironmentVariable("AB_SERVER_HOST", "10.0.0.1");

            var fixture = new IntegrationFixture();

            Assert.Equal(string.Empty, fixture.SkipReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AB_SERVER_HOST", previous);
        }
    }
}
