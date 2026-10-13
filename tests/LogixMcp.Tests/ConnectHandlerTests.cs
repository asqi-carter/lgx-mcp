using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for the CIP connect note when no project is loaded (ungated session):
/// the note must state the write rule up front.
/// </summary>
public class ConnectHandlerTests
{
    private static Task<object> ConnectCip(Capabilities? capabilities = null) =>
        ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false,
            new ProjectService(), new TagSessionService(),
            backend: "cip",
            capabilities: capabilities,
            cipConnectProbe: (_, _) => Task.CompletedTask);

    /// <summary>No project and no allowlist: note says reads only; not gated.</summary>
    [Fact]
    public async Task CipNoProject_NoteSaysReadsOnly()
    {
        var ok = Assert.IsType<ConnectResult>(await ConnectCip());

        Assert.False(ok.Gated);
        Assert.Contains(
            "No project loaded: reads only. Writes are refused until a project is open or LGX_WRITE_ALLOWLIST is set.",
            ok.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("without gate-checking", ok.Message, StringComparison.Ordinal);
    }

    /// <summary>No project but an allowlist is active: note says writes are allowlist-limited; not gated.</summary>
    [Fact]
    public async Task CipNoProject_Allowlist_NoteSaysAllowlistOnly()
    {
        var file = Path.Combine(Path.GetTempPath(), $"lgx-allow-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "Motor1_Speed\n");
        try
        {
            var ok = Assert.IsType<ConnectResult>(
                await ConnectCip(new Capabilities(false, WriteAllowlistPath: file)));

            Assert.False(ok.Gated);
            Assert.Contains(
                "No project loaded: writes are limited to the site allowlist.",
                ok.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("reads only", ok.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(file); }
    }

    /// <summary>
    /// A probe whose native call ignores cancellation (blocks for a long time)
    /// must not block the tool: the hard deadline returns ConnectionFailed naming
    /// gateway, path and elapsed time, and the next connect is answered.
    /// </summary>
    [Fact]
    public async Task CipProbe_HangingNativeCall_ReturnsConnectionFailedWithinDeadline()
    {
        using var release = new ManualResetEventSlim(false);
        try
        {
            var sessions = new TagSessionService();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Ignores the token entirely, like a stuck native call.
            var result = await ConnectHandler.ExecuteAsync(
                "198.51.100.10", "1,7", useVirtual: false,
                new ProjectService(), sessions,
                backend: "cip",
                cipConnectProbe: (_, _) => Task.Run(() => release.Wait(TimeSpan.FromMinutes(1))),
                cipProbeDeadline: TimeSpan.FromMilliseconds(500));
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"connect blocked for {sw.Elapsed}");
            var err = Assert.IsType<ConnectCipProbeError>(result);
            Assert.Equal("ConnectionFailed", err.Kind);
            Assert.Equal("198.51.100.10", err.Gateway);
            Assert.Equal("1,7", err.Path);
            Assert.Contains("198.51.100.10", err.Error, StringComparison.Ordinal);
            Assert.Contains("1,7", err.Error, StringComparison.Ordinal);
            Assert.Matches(@"after \d+\.\d s", err.Error);

            // The server answers the next request after the timed-out connect.
            var next = await ConnectHandler.ExecuteAsync(
                "198.51.100.10", "1,0", useVirtual: false,
                new ProjectService(), sessions,
                backend: "cip",
                cipConnectProbe: (_, _) => Task.CompletedTask);
            Assert.IsType<ConnectResult>(next);
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>An empty path with a timed-out probe is still ConnectionFailed, not CipPathInvalid.</summary>
    [Fact]
    public async Task CipProbe_HangingNativeCall_EmptyPath_IsConnectionFailed()
    {
        using var release = new ManualResetEventSlim(false);
        try
        {
            var result = await ConnectHandler.ExecuteAsync(
                "198.51.100.10", "", useVirtual: false,
                new ProjectService(), new TagSessionService(),
                backend: "cip",
                cipConnectProbe: (_, _) => Task.Run(() => release.Wait(TimeSpan.FromMinutes(1))),
                cipProbeDeadline: TimeSpan.FromMilliseconds(300));

            Assert.Equal("ConnectionFailed", Assert.IsType<ConnectCipProbeError>(result).Kind);
        }
        finally
        {
            release.Set();
        }
    }
}
