using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>Argument validation for logix_connect (backend='cip').</summary>
public class ConnectArgumentTests
{
    private static Task<object> Connect(string gateway, string path, Action onProbe) =>
        ConnectHandler.ExecuteAsync(
            gateway, path, useVirtual: false,
            new ProjectService(), new TagSessionService(),
            backend: "cip",
            cipConnectProbe: (_, _) => { onProbe(); return Task.CompletedTask; });

    /// <summary>
    /// A gateway or path carrying '&amp;', '=', control characters or non-space whitespace
    /// is refused with InvalidArgument before any libplctag/probe call.
    /// </summary>
    [Theory]
    [InlineData("198.51.100.10&elem_count=9", "1,0", "gateway")]
    [InlineData("198.51.100.10", "1,0&name=x", "path")]
    [InlineData("198.51.100.10=x", "1,0", "gateway")]
    [InlineData("198.51.100.10", "1,0=2", "path")]
    [InlineData("198.51.100.10 ", "1,0", "gateway")]
    [InlineData("198.51.100.10 x", "1,0", "gateway")]
    [InlineData("198.51.100.10\n", "1,0", "gateway")]
    [InlineData("198.51.100.10\t", "1,0", "gateway")]
    [InlineData("198.51.100.10\0", "1,0", "gateway")]
    [InlineData("198.51.100.10", "1,0\r\n", "path")]
    [InlineData("198.51.100.10", "1,0\t", "path")]
    [InlineData("198.51.100.10", "1,0 ", "path")]
    public async Task GatewayAndPath_AttributeInjectionRefused(string gateway, string path, string parameter)
    {
        var probed = false;
        var result = await Connect(gateway, path, () => probed = true);

        var err = Assert.IsType<ConnectMissingArgumentError>(result);
        Assert.Equal("InvalidArgument", err.Kind);
        Assert.Equal(parameter, err.Parameter);
        Assert.False(probed, "probe/libplctag must not be reached");
    }

    /// <summary>Spaces separating path segments remain valid.</summary>
    [Fact]
    public async Task Path_WithSeparatingSpaces_IsAccepted()
    {
        var result = await Connect("198.51.100.10", "1, 0", () => { });
        Assert.IsType<ConnectResult>(result);
    }

    /// <summary>Full-width look-alikes of '&amp;' and '=' (and other non-ASCII) are refused before any probe.</summary>
    [Theory]
    [InlineData("198.51.100.10\uFF06elem_count=9", "1,0", "gateway")]
    [InlineData("198.51.100.10", "1,0\uFF06name\uFF1Dx", "path")]
    [InlineData("198.51.100.10\uFF1Dx", "1,0", "gateway")]
    [InlineData("198.51.100.10", "1,0\uFF1D2", "path")]
    [InlineData("198.51.100.\u0661\u0660", "1,0", "gateway")]
    [InlineData("198.51.100.10", "1,\u0660", "path")]
    public async Task FullWidthMetacharacters_Refused(string gateway, string path, string parameter)
    {
        var probed = false;
        var result = await Connect(gateway, path, () => probed = true);

        var err = Assert.IsType<ConnectMissingArgumentError>(result);
        Assert.Equal("InvalidArgument", err.Kind);
        Assert.Equal(parameter, err.Parameter);
        Assert.False(probed, "probe/libplctag must not be reached");
    }
}
