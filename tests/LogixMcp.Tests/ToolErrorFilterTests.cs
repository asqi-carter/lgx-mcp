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

/// <summary>Gate shared between a test and <see cref="FakeConnectTools"/>.</summary>
public sealed class ConnectGate
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Throw { get; set; }
}

/// <summary>
/// A logix_connect that blocks on a gate before adopting the mark through RunAsync, plus a status tool. The tools are
/// created at runtime (no tool attributes), so the docs guards that scan the assembly for logix_* tools ignore them.
/// </summary>
public sealed class FakeConnectTools(ConnectState state, ConnectGate gate)
{
    public IEnumerable<McpServerTool> Create() =>
    [
        McpServerTool.Create(Connect, new McpServerToolCreateOptions { Name = "logix_connect" }),
        McpServerTool.Create(Status, new McpServerToolCreateOptions { Name = "logix_connect_status" }),
    ];

    public async Task<string> Connect(
        string gateway = "", string path = "", bool useVirtual = false, string backend = "cip",
        string sdkMode = "offline", bool allowControllerDownload = false, string? sdkCommPath = null,
        bool confirmedByUser = false)
    {
        gate.Entered.TrySetResult();
        await gate.Release.Task;
        if (gate.Throw) throw new InvalidOperationException("connect failed before adopting the mark");
        var response = await state.RunAsync(_ => Task.FromResult("{\"status\":\"connected\"}"), TimeSpan.FromSeconds(10));
        return state.WithServerInstance(response);
    }

    public Task<string> Status(CancellationToken cancellationToken) =>
        state.GetStatusJsonAsync(null, cancellationToken);
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

    private const string UInt32Conversion =
        "The JSON value could not be converted to System.UInt32. Path: $ | LineNumber: 0 | BytePositionInLine: 2.";

    private static IReadOnlyDictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    /// <summary>A negative insertPosition bound to a uint is InvalidArguments naming the argument, not InternalError.</summary>
    [Fact]
    public async Task JsonBindingFailure_OneNegativeNumber_ReturnsInvalidArguments_NamingTheParameter()
    {
        var r = await ToolErrorFilter.InvokeAsync("logix_propose_rung_edit",
            () => throw new JsonException(UInt32Conversion),
            arguments: Args("""{"insertPosition": -1, "replaceCount": 0, "routineXPath": "x"}"""));
        Assert.True(r.IsError);
        var p = Payload(r);
        Assert.Equal("InvalidArguments", p.GetProperty("kind").GetString());
        Assert.Equal("insertPosition", p.GetProperty("parameter").GetString());
        Assert.Contains("0 upward", p.GetProperty("hint").GetString());
        Assert.False(p.TryGetProperty("parameters", out _));
        Assert.DoesNotContain("server log", p.GetRawText());
    }

    [Fact]
    public async Task JsonBindingFailure_TwoNegativeNumbers_ReturnsInvalidArguments_ListingBoth()
    {
        var r = await ToolErrorFilter.InvokeAsync("logix_propose_rung_edit",
            () => throw new JsonException(UInt32Conversion),
            arguments: Args("""{"replaceCount": -1, "insertPosition": -1, "routineXPath": "x"}"""));
        var p = Payload(r);
        Assert.Equal("InvalidArguments", p.GetProperty("kind").GetString());
        Assert.False(p.TryGetProperty("parameter", out _));
        Assert.Equal(["insertPosition", "replaceCount"], p.GetProperty("parameters").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task JsonBindingFailure_WithoutArguments_IsStillInvalidArguments_WithoutAParameter()
    {
        var r = await ToolErrorFilter.InvokeAsync("logix_x", () => throw new JsonException(UInt32Conversion));
        var p = Payload(r);
        Assert.Equal("InvalidArguments", p.GetProperty("kind").GetString());
        Assert.False(p.TryGetProperty("parameter", out _));
        Assert.False(p.TryGetProperty("parameters", out _));
        Assert.Contains("0 upward", p.GetProperty("hint").GetString());
    }

    [Theory]
    [InlineData("System.UInt32", "UInt32")]
    [InlineData("System.Boolean", "Boolean")]
    [InlineData("Int32", "Int32")]
    public void ConversionTargetType_IsParsedFromTheMessage(string typeInMessage, string expected) =>
        Assert.Equal(expected, ToolErrorFilter.ConversionTargetType($"The JSON value could not be converted to {typeInMessage}. Path: $"));

    [Fact]
    public void ArgumentsThatCannotBe_SignedTarget_NamesOnlyFractions()
    {
        var names = ToolErrorFilter.ArgumentsThatCannotBe("Int32", Args("""{"a": -1, "b": 1.5, "c": "x"}"""));
        Assert.Equal(["b"], names);
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

    private static async Task<(IHost Host, McpClient Client, ConnectState State, ConnectGate Gate)> StartConnectAsync()
    {
        var c2s = new Pipe();
        var s2c = new Pipe();
        var state = new ConnectState();
        var gate = new ConnectGate();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(gate);
        builder.Services.AddMcpServer()
            .WithStreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream())
            .WithToolErrorFilter()
            .WithTools(new FakeConnectTools(state, gate).Create());
        var host = builder.Build();
        await host.StartAsync();
        var client = await McpClient.CreateAsync(
            new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream()));
        return (host, client, state, gate);
    }

    private static JsonElement TextPayload(CallToolResult r) =>
        JsonDocument.Parse(((TextContentBlock)r.Content[0]).Text).RootElement.Clone();

    /// <summary>A status poll without connectId, sent while logix_connect runs, answers connecting/starting.</summary>
    [Fact]
    public async Task EndToEnd_ConnectStatusDuringConnect_AnswersConnecting_NeverNoConnectInProgress()
    {
        var (host, client, state, gate) = await StartConnectAsync();
        await using var _c = client;
        using var _h = host;

        var connect = client.CallToolAsync("logix_connect", new Dictionary<string, object?>()).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var during = TextPayload(await client.CallToolAsync("logix_connect_status", new Dictionary<string, object?>()));
        Assert.False(during.TryGetProperty("kind", out _), during.GetRawText());
        Assert.Equal("connecting", during.GetProperty("status").GetString());
        Assert.Equal("starting", during.GetProperty("phase").GetString());
        Assert.False(during.TryGetProperty("connectId", out _));

        gate.Release.SetResult();
        var connected = TextPayload(await connect.WaitAsync(TimeSpan.FromSeconds(10)));
        var after = TextPayload(await client.CallToolAsync("logix_connect_status", new Dictionary<string, object?>()));
        Assert.Equal("connected", after.GetProperty("status").GetString());
        Assert.Equal(
            connected.GetProperty("serverInstance").GetProperty("startedAt").GetRawText(),
            after.GetProperty("serverInstance").GetProperty("startedAt").GetRawText());
        Assert.Null(state.Current);
    }

    /// <summary>The filter's mark is cleared when the connect tool throws before adopting it.</summary>
    [Fact]
    public async Task EndToEnd_ConnectToolThrows_ClearsTheMark()
    {
        var (host, client, state, gate) = await StartConnectAsync();
        await using var _c = client;
        using var _h = host;
        gate.Throw = true;

        var connect = client.CallToolAsync("logix_connect", new Dictionary<string, object?>()).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(state.Current?.Provisional);

        gate.Release.SetResult();
        var r = await connect.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(r.IsError);
        Assert.Null(state.Current);
    }
}
