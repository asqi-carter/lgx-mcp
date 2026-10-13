namespace LogixMcp.Tests;

/// <summary>Tests for the <c>logix_poll</c> input limits.</summary>
public class PollTagsTests
{
    private static TagSessionService BuildSession()
    {
        var inner = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["T"] = (false, "BOOL"),
        });
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: true);
        return session;
    }

    /// <summary>Out-of-range count, tag count and intervalMs are refused with the limit named.</summary>
    [Fact]
    public async Task Limits_Enforced()
    {
        var session = BuildSession();

        var zero = Assert.IsType<PollError>(
            await PollTagsHandler.ExecuteAsync(["T"], count: 1, intervalMs: 0, session));
        Assert.Equal(ErrorKinds.InvalidValue, zero.Kind);
        Assert.Contains("100", zero.Error);

        var tooMany = Assert.IsType<PollError>(
            await PollTagsHandler.ExecuteAsync(["T"], count: 1001, intervalMs: 100, session));
        Assert.Equal(ErrorKinds.InvalidValue, tooMany.Kind);
        Assert.Contains("1000", tooMany.Error);

        var manyTags = Assert.IsType<PollError>(
            await PollTagsHandler.ExecuteAsync(
                Enumerable.Repeat("T", 65).ToArray(), count: 1, intervalMs: 100, session));
        Assert.Equal(ErrorKinds.InvalidValue, manyTags.Kind);
        Assert.Contains("64", manyTags.Error);

        // Boundary values are accepted.
        Assert.IsType<PollResult>(
            await PollTagsHandler.ExecuteAsync(
                Enumerable.Repeat("T", 64).ToArray(), count: 1, intervalMs: 100, session));
    }
}
