using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace LogixMcp.Tests;

/// <summary>
/// Integration tests that drive the real libplctag native library (no mocks)
/// against addresses that can never answer, to prove the CIP lane honours its
/// timeout instead of hanging. They run in the Linux sandbox: loopback needs no
/// network access, and a closed port is picked at run time so 44818 need not
/// be free.
/// </summary>
public class LibPlcTagServiceIntegrationTests
{
    /// <summary>Upper bound the connect probe must honour (the hard deadline is ~15 s).</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    /// <summary>Finds a loopback TCP port with nothing listening on it.</summary>
    private static int GetClosedLocalPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// A real <see cref="LibPlcTagService"/> pointed at a closed loopback port must
    /// fail (not hang) well inside 15 s when listing <c>@tags</c>.
    /// </summary>
    [Fact]
    public async Task Connect_ClosedLocalPort_FailsWithin15s()
    {
        var svc = new LibPlcTagService();
        await svc.ConnectAsync($"127.0.0.1:{GetClosedLocalPort()}", "1,0");

        var sw = Stopwatch.StartNew();
        var work = Task.Run(() => svc.ListTagsAsync(CancellationToken.None));
        var winner = await Task.WhenAny(work, Task.Delay(Bound + TimeSpan.FromSeconds(5)));
        sw.Stop();

        Assert.Same(work, winner);
        Assert.True(sw.Elapsed < Bound, $"ListTagsAsync took {sw.Elapsed} against a closed port.");
        await Assert.ThrowsAnyAsync<Exception>(() => work);
    }

    /// <summary>
    /// The full connect handler (default probe) against a closed loopback port
    /// returns a structured <c>ConnectionFailed</c> within the deadline.
    /// </summary>
    [Fact]
    public async Task ConnectHandler_ClosedLocalPort_ReturnsConnectionFailedWithin15s()
    {
        var gateway = $"127.0.0.1:{GetClosedLocalPort()}";
        var sw = Stopwatch.StartNew();
        var result = await ConnectHandler.ExecuteAsync(
            gateway, "1,0", useVirtual: false,
            new ProjectService(), new TagSessionService(), backend: "cip");
        sw.Stop();

        Assert.True(sw.Elapsed < Bound + TimeSpan.FromSeconds(2), $"connect took {sw.Elapsed}");
        var err = Assert.IsType<ConnectCipProbeError>(result);
        Assert.Equal("ConnectionFailed", err.Kind);
        Assert.Contains(gateway, err.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unroutable TEST-NET-1 address (RFC 5737). Skipped when the sandbox
    /// network rejects it instantly or is otherwise unsuitable; the bound is the
    /// same: never hang past the deadline.
    /// </summary>
    [Fact]
    public async Task Connect_TestNetAddress_FailsWithin15s()
    {
        var svc = new LibPlcTagService();
        await svc.ConnectAsync("192.0.2.1", "1,0");

        var sw = Stopwatch.StartNew();
        var work = Task.Run(() => svc.ListTagsAsync(CancellationToken.None));
        var winner = await Task.WhenAny(work, Task.Delay(Bound + TimeSpan.FromSeconds(5)));
        sw.Stop();

        Assert.Same(work, winner);
        Assert.True(sw.Elapsed < Bound, $"ListTagsAsync took {sw.Elapsed} against TEST-NET.");
        await Assert.ThrowsAnyAsync<Exception>(() => work);
    }
}
