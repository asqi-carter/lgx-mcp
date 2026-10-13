namespace LogixMcp.Tests;

/// <summary>
/// Tests for the hardened native-library extraction in
/// <see cref="LibPlcTagNativeInit"/>.
/// </summary>
public class LibPlcTagNativeInitTests
{
    private static Stream Payload() => new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });

    [Fact]
    public void ExtractsToFreshPrivateDir_NeverReusesExisting()
    {
        var first = LibPlcTagNativeInit.ExtractVerified(Payload, "libplctag.so");
        var second = LibPlcTagNativeInit.ExtractVerified(Payload, "libplctag.so");
        try
        {
            var d1 = Path.GetDirectoryName(first)!;
            var d2 = Path.GetDirectoryName(second)!;
            Assert.NotEqual(d1, d2);
            Assert.StartsWith("libplctag_", Path.GetFileName(d1));
            Assert.NotEqual($"libplctag_{Environment.ProcessId}", Path.GetFileName(d1));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(first));

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(d1));
            }

            // A pre-planted file in a directory must never be trusted: the
            // extractor only ever writes into directories it just created.
            var planted = Path.Combine(Path.GetTempPath(), $"libplctag_{Environment.ProcessId}");
            Directory.CreateDirectory(planted);
            try
            {
                File.WriteAllBytes(Path.Combine(planted, "libplctag.so"), new byte[] { 9, 9, 9 });
                var third = LibPlcTagNativeInit.ExtractVerified(Payload, "libplctag.so");
                try
                {
                    Assert.NotEqual(planted, Path.GetDirectoryName(third));
                    Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(third));
                }
                finally { Directory.Delete(Path.GetDirectoryName(third)!, true); }
            }
            finally { Directory.Delete(planted, true); }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(first)!, true);
            Directory.Delete(Path.GetDirectoryName(second)!, true);
        }
    }

    [Fact]
    public void HashMismatch_Refused()
    {
        string? written = null;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LibPlcTagNativeInit.ExtractVerified(Payload, "libplctag.so", p =>
            {
                written = p;
                File.WriteAllBytes(p, new byte[] { 0xDE, 0xAD });
            }));

        Assert.Contains("SHA-256 mismatch", ex.Message);
        Assert.NotNull(written);
        // Refused library and its directory are cleaned up.
        Assert.False(Directory.Exists(Path.GetDirectoryName(written)!));
    }
}
