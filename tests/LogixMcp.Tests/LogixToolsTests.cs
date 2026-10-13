using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="LogixTools"/> and <see cref="PingHandler"/>.
/// </summary>
public class LogixToolsTests
{
    [Fact]
    public void Ping_ReturnsValidJson()
    {
        var result = LogixTools.Ping();

        var doc = JsonDocument.Parse(result);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(ServerIdentity.Name, doc.RootElement.GetProperty("server").GetString());
        Assert.Equal(ServerIdentity.Version, doc.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void GetPingResponse_ReturnsValidJson()
    {
        var result = LogixTools.GetPingResponse();

        var doc = JsonDocument.Parse(result);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(ServerIdentity.Name, doc.RootElement.GetProperty("server").GetString());
        Assert.Equal(ServerIdentity.Version, doc.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void Ping_DelegatesToGetPingResponse()
    {
        Assert.Equal(LogixTools.GetPingResponse(), LogixTools.Ping());
    }

    // ------------------------------------------------------------------ //
    //  PingHandler unit tests                                              //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// (a) PingHandler.Execute() returns a typed object with the expected
    ///     status and version values — no JSON parsing required.
    /// </summary>
    [Fact]
    public void PingHandler_Execute_ReturnsObjectWithCorrectStatusAndVersion()
    {
        PingResponse response = PingHandler.Execute();

        Assert.Equal("ok",    response.Status);
        Assert.Equal(ServerIdentity.Version, response.Version);
    }

    /// <summary>
    /// (b) Serializing the return value of PingHandler.Execute() produces
    ///     valid JSON that contains exactly the keys 'status', 'server',
    ///     'version', 'auditMaxMb', 'auditKeep', 'targetMajorRevision' and 'mnemonicSet' (no extra or missing keys).
    /// </summary>
    [Fact]
    public void PingHandler_Execute_SerializesToJsonWithExactlyThreeExpectedKeys()
    {
        PingResponse response = PingHandler.Execute();
        string json = JsonSerializer.Serialize(response);

        var doc = JsonDocument.Parse(json);
        var properties = doc.RootElement
                            .EnumerateObject()
                            .Select(p => p.Name)
                            .OrderBy(n => n)
                            .ToList();

        Assert.Equal(new[] { "auditKeep", "auditMaxMb", "mnemonicSet", "server", "status", "targetMajorRevision", "version" }, properties);
    }

    /// <summary>
    /// logix_ping capabilities describe the full stage -> publish -> accept or cancel cycle,
    /// naming the accept tool when the Studio-automation flag is on.
    /// </summary>
#if LOGIX_SDK
    [Fact]
    public void PingHandler_Capabilities_DescribeFullRungEditLifecycle()
    {
        var on  = PingHandler.Execute(new Capabilities(LiveWritesEnabled: false, StudioAutomationEnabled: true));
        var off = PingHandler.Execute(new Capabilities(LiveWritesEnabled: false));

        Assert.Contains("logix_accept_pending_edits", on.Capabilities!.RungEditLifecycle, StringComparison.Ordinal);
        Assert.Contains("stage", on.Capabilities.RungEditLifecycle, StringComparison.Ordinal);
        Assert.Contains("publish", on.Capabilities.RungEditLifecycle, StringComparison.Ordinal);
        Assert.Contains("cancel", on.Capabilities.RungEditLifecycle, StringComparison.Ordinal);
        Assert.Contains("Logix Designer", off.Capabilities!.RungEditLifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain("Studio-only", off.Capabilities.RungEditLifecycle, StringComparison.Ordinal);
    }
#endif
}
