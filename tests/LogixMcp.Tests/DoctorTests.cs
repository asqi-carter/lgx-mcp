using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>Tests for the <c>designerVersion</c> field of <c>logix_doctor</c>.</summary>
public class DoctorTests
{
    private static Task<DoctorResult> Run(DoctorHandlerTests.FakeEnv env) =>
        DoctorHandler.ExecuteAsync(null, null, env, new Capabilities(LiveWritesEnabled: false, StudioAutomationEnabled: false));

    [Fact]
    public async Task DesignerVersionFromSessionOrUnknown()
    {
        // No session: unknown until connect, and the SDK assembly version is never substituted.
        var none = await Run(new DoctorHandlerTests.FakeEnv { Session = null, SdkAssemblyVersion = "9.9.9.9" });
        Assert.Equal("unknown until connect", none.DesignerVersion);
        Assert.DoesNotContain("9.9.9.9", none.DesignerVersion);

        // Session that has not learned a Designer version: still not the SDK assembly version.
        var unlearned = await Run(new DoctorHandlerTests.FakeEnv
        {
            Session = new("sdk", "offline-project", false, "uncorrelated", null),
            SdkAssemblyVersion = "9.9.9.9",
        });
        Assert.Equal("unknown until connect", unlearned.DesignerVersion);

        // Session with a version: reported, and the SDK assembly version stays out of it.
        var known = await Run(new DoctorHandlerTests.FakeEnv
        {
            Session = new("sdk", "offline-project", false, "uncorrelated", null, "36.0 (ProjectRevision)"),
            SdkAssemblyVersion = "9.9.9.9",
        });
        Assert.Equal("36.0 (ProjectRevision)", known.DesignerVersion);
        Assert.DoesNotContain("9.9.9.9", known.DesignerVersion);
    }
}
