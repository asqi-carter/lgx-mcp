#if LOGIX_SDK
namespace LogixMcp.Tests;

/// <summary>Startup removal of stale <c>ctl-*</c> controller-upload work directories.</summary>
public class StaleControllerWorkDirCleanupTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "lgx-ctl-clean-" + Guid.NewGuid().ToString("N"));

    public StaleControllerWorkDirCleanupTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    private string MakeDir(string name, TimeSpan age)
    {
        var dir = Path.Combine(_base, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "uploaded.ACD"), "x");
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - age);
        return dir;
    }

    [Fact]
    public void Startup_RemovesCtlWorkDirsOlderThan24h_AndKeepsTheRest()
    {
        var old       = MakeDir("ctl-aaaaaaaaaaaa", TimeSpan.FromHours(25));
        var recent    = MakeDir("ctl-bbbbbbbbbbbb", TimeSpan.FromHours(1));
        var projSlot  = MakeDir("0123456789ab",     TimeSpan.FromHours(48));   // not a ctl-* dir

        var removed = SdkAdapterInitializer.CleanStaleControllerWorkDirs(
            _base, SdkAdapterInitializer.StaleControllerWorkDirAge, DateTime.UtcNow);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(projSlot));
    }

    [Fact]
    public void Startup_MissingWorkBase_IsNotAnError() =>
        Assert.Equal(0, SdkAdapterInitializer.CleanStaleControllerWorkDirs(
            Path.Combine(_base, "absent"), TimeSpan.FromHours(24), DateTime.UtcNow));
}
#endif
