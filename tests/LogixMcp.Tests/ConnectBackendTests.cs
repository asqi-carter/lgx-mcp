using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>Tests for <c>backend="virtual"</c> and the advertised backend list.</summary>
public class ConnectBackendTests
{
    private static readonly string FixturePath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    /// <summary>backend="virtual" behaves as useVirtual=true.</summary>
    [Fact]
    public async Task VirtualBackend_Accepted()
    {
        var projectSvc = new ProjectService();
        projectSvc.Load(FixturePath);
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "virtual");

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.True(ok.Virtual);
        Assert.Equal("virtual", ok.Backend);
    }

    /// <summary>The unrecognized-backend message lists only cip, l5x and virtual in a no-SDK build.</summary>
    [Fact]
    public async Task NoSdkBuild_ErrorOmitsSdk()
    {
        var result = (ConnectBadBackendError) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false,
            new ProjectService(), new TagSessionService(), backend: "bogus");

        Assert.Contains("virtual", result.Error, StringComparison.Ordinal);
#if !LOGIX_SDK
        Assert.DoesNotContain("sdk", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "cip", "l5x", "virtual" }, result.ValidValues);
#endif
    }

    /// <summary>A no-SDK build refuses backend="sdk" at dispatch with CapabilityDisabled and names only cip, l5x and virtual.</summary>
    [Fact]
    public async Task NoSdkBuild_SdkBackendRefused()
    {
#if !LOGIX_SDK
        var session = new TagSessionService();
        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false,
            new ProjectService(), session, backend: "sdk");

        var refused = Assert.IsType<CapabilityDisabledError>(result);
        Assert.Equal(ErrorKinds.CapabilityDisabled, refused.Kind);
        Assert.Equal("The SDK backend is not part of this build. Use cip, l5x or virtual.", refused.Error);
        Assert.Contains("cip", refused.Hint!, StringComparison.Ordinal);
        Assert.Contains("l5x", refused.Hint!, StringComparison.Ordinal);
        Assert.Contains("virtual", refused.Hint!, StringComparison.Ordinal);
        Assert.DoesNotContain("sdk", refused.Hint!, StringComparison.OrdinalIgnoreCase);
        Assert.False(session.IsConnected);
#else
        // SDK builds accept backend="sdk"; this refusal is a no-SDK-lane behaviour.
        await Task.CompletedTask;
#endif
    }

    /// <summary>A null, empty or blank backend takes the default (cip) instead of throwing.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NullBackend_DefaultsToCip(string? backend)
    {
        // cip with no gateway yields the structured MissingGateway error — proof the
        // cip branch was reached (no NullReferenceException, no BadBackend error).
        var result = await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false,
            new ProjectService(), new TagSessionService(), backend: backend);

        var error = Assert.IsType<ConnectMissingArgumentError>(result);
        Assert.Equal("MissingGateway", error.Kind);
        Assert.Contains("backend='cip'", error.Error, StringComparison.Ordinal);
    }
}
