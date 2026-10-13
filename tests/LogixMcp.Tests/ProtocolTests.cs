using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LogixMcp.Tests;

/// <summary>
/// Verifies the MCP protocol handshake against the pinned ModelContextProtocol
/// 2.2.0 SDK, using an in-process server/client pair over in-memory pipes.
/// </summary>
public class ProtocolTests
{
    /// <summary>
    /// The server must complete initialize and report a protocol version that the
    /// 2.2 SDK itself defines (2.2 supports 2024-11-05 through 2026-07-28), along
    /// with the server identity and instructions the host configures.
    /// </summary>
    [Fact]
    public async Task ServerReportsProtocolVersion_FromSdk22()
    {
        var sdkVersion = typeof(McpServer).Assembly.GetName().Version;
        Assert.NotNull(sdkVersion);
        Assert.Equal(2, sdkVersion!.Major);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var serverOptions = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerIdentity.Name, Version = ServerIdentity.Version },
            ServerInstructions = LogixTools.CanonicalInstructions,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            serverOptions);
        var serverRun = server.RunAsync(cts.Token);

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
            cancellationToken: cts.Token);

        var negotiated = client.NegotiatedProtocolVersion;
        Assert.False(string.IsNullOrEmpty(negotiated));
        Assert.Contains(negotiated, new[] { "2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25", "2026-07-28" });
        Assert.Equal(ServerIdentity.Name, client.ServerInfo.Name);
        Assert.Equal(LogixTools.CanonicalInstructions, client.ServerInstructions);

        await client.DisposeAsync();
        cts.Cancel();
        try { await serverRun; } catch (OperationCanceledException) { }
    }
}
