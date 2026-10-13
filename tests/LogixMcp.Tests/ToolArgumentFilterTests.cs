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
public sealed class ArgEchoTools
{
    public static int Calls;

    [McpServerTool(Name = "arg_echo")]
    public string Echo(string name = "", int count = 0)
    {
        Interlocked.Increment(ref Calls);
        return $"{name}:{count}";
    }
}

public class ToolArgumentFilterTests
{
    private static JsonElement Payload(CallToolResult r) =>
        JsonDocument.Parse(((TextContentBlock)r.Content[0]).Text).RootElement;

    [Fact]
    public void Levenshtein_SdkCommPathToCommPath_IsThree()
    {
        Assert.Equal(3, ToolArgumentFilter.Levenshtein("sdkCommPath", "commPath"));
        Assert.Equal(0, ToolArgumentFilter.Levenshtein("", ""));
        Assert.Equal(3, ToolArgumentFilter.Levenshtein("abc", ""));
    }

    [Fact]
    public void Check_AllKnown_ReturnsNull()
    {
        Assert.Null(ToolArgumentFilter.Check("t", ["commPath"], ["commPath", "controllerIp"]));
        Assert.Null(ToolArgumentFilter.Check("t", ["COMMPATH"], ["commPath"]));
        Assert.Null(ToolArgumentFilter.Check("t", null, ["commPath"]));
    }

    [Fact]
    public void Check_Unknown_ListsUnknownAndValidAndSuggests()
    {
        var r = ToolArgumentFilter.Check("logix_doctor", ["sdkCommPath"], ["commPath", "controllerIp"]);
        Assert.NotNull(r);
        Assert.True(r!.IsError);
        var p = Payload(r);
        Assert.Equal("InvalidArguments", p.GetProperty("kind").GetString());
        Assert.Equal("sdkCommPath", p.GetProperty("unknownArguments")[0].GetString());
        Assert.Equal(2, p.GetProperty("validArguments").GetArrayLength());
        Assert.Equal("commPath", p.GetProperty("suggestions").GetProperty("sdkCommPath").GetString());
        Assert.Contains("Did you mean 'commPath'", p.GetProperty("error").GetString());
    }

    [Fact]
    public void Check_FarName_NoSuggestion()
    {
        var r = ToolArgumentFilter.Check("t", ["completelyDifferentName"], ["commPath"]);
        var p = Payload(r!);
        Assert.False(p.TryGetProperty("suggestions", out _));
        Assert.DoesNotContain("Did you mean", p.GetProperty("error").GetString());
    }

    private static async Task<(IHost Host, McpClient Client)> StartAsync()
    {
        var c2s = new Pipe();
        var s2c = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TagSessionService>();
        builder.Services.AddSingleton<LogixDoctorTools>(sp =>
            new LogixDoctorTools(sp.GetRequiredService<TagSessionService>(), new Capabilities(false)));
        builder.Services.AddMcpServer()
            .WithStreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream())
            .WithToolErrorFilter()
            .WithTools<LogixDoctorTools>()
            .WithTools<ArgEchoTools>();
        var host = builder.Build();
        await host.StartAsync();
        var client = await McpClient.CreateAsync(
            new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream()));
        return (host, client);
    }

    [Fact]
    public async Task LogixDoctor_WithSdkCommPath_ReturnsInvalidArgumentsWithSuggestion()
    {
        var (host, client) = await StartAsync();
        await using var _c = client;
        using var _h = host;
        var r = await client.CallToolAsync("logix_doctor",
            new Dictionary<string, object?> { ["sdkCommPath"] = "AB_ETHIP-1\\10.0.0.5" });
        Assert.True(r.IsError);
        var p = JsonDocument.Parse(((TextContentBlock)r.Content[0]).Text).RootElement;
        Assert.Equal("InvalidArguments", p.GetProperty("kind").GetString());
        Assert.Equal("sdkCommPath", p.GetProperty("unknownArguments")[0].GetString());
        var valid = p.GetProperty("validArguments").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("commPath", valid);
        Assert.Contains("controllerIp", valid);
        Assert.Equal("commPath", p.GetProperty("suggestions").GetProperty("sdkCommPath").GetString());
    }

    [Fact]
    public async Task UnknownArgument_RejectedBeforeToolRuns_AndValidCallStillWorks()
    {
        var (host, client) = await StartAsync();
        await using var _c = client;
        using var _h = host;
        var before = ArgEchoTools.Calls;
        var bad = await client.CallToolAsync("arg_echo",
            new Dictionary<string, object?> { ["name"] = "x", ["cnt"] = 1 });
        Assert.True(bad.IsError);
        Assert.Contains("InvalidArguments", ((TextContentBlock)bad.Content[0]).Text);
        Assert.Equal(before, ArgEchoTools.Calls);

        var ok = await client.CallToolAsync("arg_echo",
            new Dictionary<string, object?> { ["name"] = "x", ["count"] = 2 });
        Assert.NotEqual(true, ok.IsError);
        Assert.Equal("x:2", ((TextContentBlock)ok.Content[0]).Text);
    }
}
