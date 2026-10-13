using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for the post-connect CIP probe in <see cref="ConnectHandler.ExecuteAsync"/>:
/// a wrong or missing CIP path must fail fast at connect time with
/// <c>CipPathInvalid</c> / <c>ConnectionFailed</c> rather than on the first tag read.
/// The probe is injected so no network access occurs.
/// </summary>
public class ConnectHandlerCipProbeTests
{
    private const string Gateway = "198.51.100.10";

    private static Func<ITagService, CancellationToken, Task> FailingProbe(string reason = "probe timed out") =>
        (_, _) => throw new TagServiceException(new TagServiceError(
            TagServiceErrorKind.ConnectionFailed, reason));

    private static Task<object> Connect(
        string gateway, string path,
        Func<ITagService, CancellationToken, Task> probe,
        TagSessionService? sessions = null) =>
        ConnectHandler.ExecuteAsync(
            gateway, path, useVirtual: false,
            new ProjectService(), sessions ?? new TagSessionService(),
            backend: "cip", cipConnectProbe: probe);

    [Fact]
    public async Task EmptyPath_ProbeFails_ReturnsCipPathInvalidWithSlotHint()
    {
        var result = await Connect(Gateway, "", FailingProbe());

        var err = Assert.IsType<ConnectCipProbeError>(result);
        Assert.Equal("CipPathInvalid", err.Kind);
        Assert.Contains(Gateway, err.Error, StringComparison.Ordinal);
        Assert.Contains(
            "ControlLogix/CompactLogix 5x80 controllers usually need path '1,<slot>', for example '1,0'",
            err.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitPath_ProbeFails_ReturnsConnectionFailedNamingGatewayAndPath()
    {
        var result = await Connect(Gateway, "1,7", FailingProbe());

        var err = Assert.IsType<ConnectCipProbeError>(result);
        Assert.Equal("ConnectionFailed", err.Kind);
        Assert.Contains(Gateway, err.Error, StringComparison.Ordinal);
        Assert.Contains("1,7", err.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeSucceeds_ReturnsConnected()
    {
        var sessions = new TagSessionService();
        var result = await Connect(Gateway, "1,0", (_, _) => Task.CompletedTask, sessions);

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Equal("connected", ok.Status);
        Assert.Equal("cip", ok.Backend);
    }

    [Fact]
    public async Task ProbeFails_NoSessionAttached()
    {
        var sessions = new TagSessionService();
        await Connect(Gateway, "1,7", FailingProbe(), sessions);

        Assert.False(sessions.IsConnected);
    }

    [Fact]
    public async Task MissingGateway_Unchanged_ProbeNeverRuns()
    {
        var probed = false;
        var result = await Connect("", "1,0", (_, _) => { probed = true; return Task.CompletedTask; });

        var err = Assert.IsType<ConnectMissingArgumentError>(result);
        Assert.Equal("MissingGateway", err.Kind);
        Assert.False(probed);
    }
}
