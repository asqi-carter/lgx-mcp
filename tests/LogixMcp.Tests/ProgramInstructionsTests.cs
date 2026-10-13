#if LOGIX_SDK
namespace LogixMcp.Tests;

/// <summary>
/// The always-resident server instructions must
/// describe only what the server enforces about Confirm mode.
/// </summary>
public class ProgramInstructionsTests
{
    [Fact]
    public void PublishMode_DoesNotClaimUserMustApprove()
    {
        var text = LogixTools.CanonicalInstructions;

        Assert.DoesNotContain("user must approve", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("confirmedByUser=true", text);
        Assert.Contains("only do after the user explicitly approves", text);
    }

    [Fact]
    public void DoesNotClaimPendingTagsReturnTagNotFound()
    {
        var text = LogixTools.CanonicalInstructions;

        Assert.DoesNotContain("return TagNotFound", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("created on the controller immediately", text);
        Assert.Contains("CreatedByThisSession", text);
    }
}
#endif
