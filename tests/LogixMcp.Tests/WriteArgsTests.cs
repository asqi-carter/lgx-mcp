using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LogixMcp.Tests;

/// <summary>
/// logix_write accepts JSON numbers and booleans for <c>value</c> and converts them to the same
/// canonical text the string form uses.
/// </summary>
public class WriteArgsTests : IDisposable
{
    private readonly string _dir;

    public WriteArgsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"write-args-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        AuditLogger.OverrideLogDir(_dir);
        TrafficLogger.OverrideLogDir(_dir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        TrafficLogger.OverrideLogDir(null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private sealed class RecordingService : ITagService
    {
        public List<object> Writes { get; } = [];
        private object _value = 0;

        public Task ConnectAsync(string gateway, string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TagInfo>>([]);
        public Task<TagValue> ReadAsync(string tagName, CancellationToken ct = default) =>
            Task.FromResult(new TagValue(tagName, _value, "DINT"));
        public Task WriteAsync(string tagName, object value, CancellationToken ct = default)
        {
            Writes.Add(value);
            _value = value;
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("42", "42")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("3.5", "3.5")]
    [InlineData("\"42\"", "42")]
    [InlineData("1e2", "100")]
    [InlineData("-7", "-7")]
    public void CanonicalText_MatchesStringForm(string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.True(WriteTagHandler.TryCanonicalValueText(doc.RootElement, out var text));
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[1]")]
    public void CanonicalText_RejectsNonScalar(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.False(WriteTagHandler.TryCanonicalValueText(doc.RootElement, out _));
    }

    [Fact]
    public async Task NumericAndBooleanValuesAccepted()
    {
        var service = new RecordingService();
        var session = new TagSessionService();
        session.Attach(service, isVirtual: true);

        var c2s = new Pipe();
        var s2c = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ProjectService>();
        builder.Services.AddSingleton(session);
        builder.Services.AddSingleton<ControllerModeService>();
        builder.Services.AddSingleton(new Capabilities(LiveWritesEnabled: true));
        builder.Services.AddMcpServer()
            .WithStreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream())
            .WithToolErrorFilter()
            .WithTools<LogixTagTools>();
        using var host = builder.Build();
        await host.StartAsync();
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream()));

        async Task<JsonElement> Call(object value)
        {
            var r = await client.CallToolAsync("logix_write",
                new Dictionary<string, object?> { ["tag"] = "Cmd_X", ["value"] = value });
            var text = ((TextContentBlock)r.Content[0]).Text;
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        var number = await Call(42);
        var numberText = await Call("42");
        var real = await Call(3.5);
        var flag = await Call(true);
        var flagText = await Call("true");

        Assert.Equal("ok", number.GetProperty("status").GetString());
        Assert.Equal("ok", numberText.GetProperty("status").GetString());
        Assert.Equal(numberText.GetProperty("written").GetRawText(), number.GetProperty("written").GetRawText());
        Assert.Equal("ok", real.GetProperty("status").GetString());
        Assert.Equal(3.5, real.GetProperty("written").GetDouble());
        Assert.Equal("ok", flag.GetProperty("status").GetString());
        Assert.Equal(flagText.GetProperty("written").GetRawText(), flag.GetProperty("written").GetRawText());
        Assert.Equal(5, service.Writes.Count);
        Assert.Equal(service.Writes[1], service.Writes[0]);
        Assert.Equal(service.Writes[4], service.Writes[3]);
        Assert.Equal(true, service.Writes[3]);

        var bad = await Call(new Dictionary<string, object?>());
        Assert.Equal("InvalidArguments", bad.GetProperty("kind").GetString());
        Assert.Equal(5, service.Writes.Count);
    }
}
