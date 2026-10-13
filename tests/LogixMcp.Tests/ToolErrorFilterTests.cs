using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LogixMcp.Tests;

[McpServerToolType]
public sealed class ThrowingTools
{
    [McpServerTool(Name = "boom")]
    public string Boom() => throw new InvalidOperationException("kaput\nsecond line");

    [McpServerTool(Name = "needs_arg")]
    public string NeedsArg(string required) => required;
}

public class ToolErrorFilterTests
{
    private static JsonElement Payload(CallToolResult r) =>
        JsonDocument.Parse(((TextContentBlock)r.Content[0]).Text).RootElement;

    [Fact]
    public async Task UnhandledException_ReturnsInternalError()
    {
        var r = await ToolErrorFilter.InvokeAsync("logix_x",
            () => throw new InvalidOperationException("kaput\nsecond line"));
        Assert.True(r.IsError);
        var p = Payload(r);
        Assert.Equal("InternalError", p.GetProperty("kind").GetString());
        Assert.Equal("logix_x", p.GetProperty("tool").GetString());
        Assert.Equal("InvalidOperationException", p.GetProperty("exceptionType").GetString());
        Assert.Equal("kaput", p.GetProperty("message").GetString());
        Assert.Contains("server log", p.GetProperty("hint").GetString());
        Assert.DoesNotContain("An error occurred invoking", p.GetRawText());
    }

    [Fact]
    public async Task MissingRequiredArgument_ReturnsInvalidArguments()
    {
        var r = await ToolErrorFilter.InvokeAsync("logix_x",
            () => throw new ArgumentException("Missing a value for the required parameter 'value'."));
        var p = Payload(r);
        Assert.Equal("InvalidArguments", p.GetProperty("kind").GetString());
        Assert.Equal("value", p.GetProperty("parameter").GetString());
        Assert.Contains("'value'", p.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await ToolErrorFilter.InvokeAsync("logix_x", () => throw new OperationCanceledException()));
        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await ToolErrorFilter.InvokeAsync("logix_x", () => throw new TaskCanceledException()));
    }

    [Fact]
    public async Task SuccessfulResult_PassesThroughUnchanged()
    {
        var ok = new CallToolResult { Content = [new TextContentBlock { Text = "{}" }] };
        Assert.Same(ok, await ToolErrorFilter.InvokeAsync("t", () => new ValueTask<CallToolResult>(ok)));
    }

    private static CallToolResult Text(string json) =>
        new() { Content = [new TextContentBlock { Text = json }] };

    [Fact]
    public async Task ResultWithTopLevelError_IsMarkedIsError()
    {
        var r = await ToolErrorFilter.InvokeAsync("logix_x",
            () => new ValueTask<CallToolResult>(Text("{\"error\":\"nope\",\"kind\":\"InvalidArguments\"}")));
        Assert.True(r.IsError);
    }

    [Fact]
    public async Task BuildFailedWithoutTopLevelError_StaysNotIsError()
    {
        var json = "{\"status\":\"failed\",\"errors\":[{\"error\":\"E1\",\"message\":\"bad\"}],\"counts\":{\"error\":1}}";
        var r = await ToolErrorFilter.InvokeAsync("logix_build", () => new ValueTask<CallToolResult>(Text(json)));
        Assert.NotEqual(true, r.IsError);
    }

    [Fact]
    public async Task NoActiveSessionResult_IsMarkedIsError()
    {
        var json = JsonSerializer.Serialize(new
        {
            error = "No active session. Call logix_connect first.",
            kind = ErrorKinds.NoActiveSession,
        });
        var r = await ToolErrorFilter.InvokeAsync("logix_read", () => new ValueTask<CallToolResult>(Text(json)));
        Assert.True(r.IsError);
    }

    [Theory]
    [InlineData("{\"error\":null,\"ok\":true}")]
    [InlineData("{\"error\":\"\"}")]
    [InlineData("[{\"error\":\"x\"}]")]
    [InlineData("not json")]
    public async Task NonErrorShapes_StayNotIsError(string text)
    {
        var r = await ToolErrorFilter.InvokeAsync("t", () => new ValueTask<CallToolResult>(Text(text)));
        Assert.NotEqual(true, r.IsError);
    }

    private static async Task<(IHost Host, McpClient Client)> StartAsync()
    {
        var c2s = new Pipe();
        var s2c = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
            .WithStreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream())
            .WithToolErrorFilter()
            .WithTools<ThrowingTools>();
        var host = builder.Build();
        await host.StartAsync();
        var client = await McpClient.CreateAsync(
            new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream()));
        return (host, client);
    }

    [Fact]
    public async Task EndToEnd_ThrowingTool_ReturnsTypedError()
    {
        var (host, client) = await StartAsync();
        await using var _c = client;
        using var _h = host;
        var r = await client.CallToolAsync("boom", new Dictionary<string, object?>());
        var text = ((TextContentBlock)r.Content[0]).Text;
        Assert.True(r.IsError);
        Assert.Contains("InternalError", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.DoesNotContain("An error occurred invoking", text);
    }

    [Fact]
    public async Task EndToEnd_MissingArgument_ReturnsInvalidArguments()
    {
        var (host, client) = await StartAsync();
        await using var _c = client;
        using var _h = host;
        var r = await client.CallToolAsync("needs_arg", new Dictionary<string, object?>());
        var text = ((TextContentBlock)r.Content[0]).Text;
        Assert.Contains("InvalidArguments", text);
        Assert.Contains("required", text);
    }
}
