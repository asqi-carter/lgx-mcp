namespace LogixMcp.Tests;

/// <summary>
/// Shared xunit collection fixture for integration tests that require a live
/// Allen-Bradley / Logix controller to be reachable over EtherNet/IP.
/// </summary>
/// <remarks>
/// Connection parameters are read from environment variables at startup:
/// <list type="bullet">
///   <item><term>AB_SERVER_HOST</term><description>
///     IP address or hostname of the target controller.  When this variable is
///     absent the entire collection is skipped.
///   </description></item>
///   <item><term>AB_SERVER_PATH</term><description>
///     ControlLogix slot path (e.g. <c>1,0</c>).  Defaults to <c>"1,0"</c>
///     when unset.
///   </description></item>
/// </list>
/// </remarks>
public sealed class IntegrationFixture
{
    /// <summary>
    /// IP address or hostname of the AB controller, or <see langword="null"/>
    /// when <c>AB_SERVER_HOST</c> is not set.
    /// </summary>
    public string? Host { get; }

    /// <summary>
    /// ControlLogix slot path.  Defaults to <c>"1,0"</c> when
    /// <c>AB_SERVER_PATH</c> is not set.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// <see langword="true"/> when a live controller host is configured and
    /// integration tests should execute; <see langword="false"/> otherwise.
    /// </summary>
    public bool IsAvailable => Host is not null;

    /// <summary>
    /// Human-readable message explaining why the test is being skipped, or an
    /// empty string when <see cref="IsAvailable"/> is <see langword="true"/>.
    /// </summary>
    public string SkipReason =>
        IsAvailable
            ? string.Empty
            : "AB_SERVER_HOST environment variable is not set. "
              + "Set AB_SERVER_HOST to the IP or hostname of a reachable "
              + "Allen-Bradley controller to run integration tests.";

    /// <summary>
    /// Initialises the fixture by reading connection parameters from the
    /// process environment.
    /// </summary>
    public IntegrationFixture()
    {
        Host = Environment.GetEnvironmentVariable("AB_SERVER_HOST");
        Path = Environment.GetEnvironmentVariable("AB_SERVER_PATH") ?? "1,0";
    }
}
