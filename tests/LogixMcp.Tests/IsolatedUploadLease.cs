namespace LogixMcp.Tests;

/// <summary>
/// A <see cref="HostUploadLease"/> private to one test: its lease file lives in a unique temp directory that is
/// deleted on <see cref="Dispose"/>. Tests that build an <see cref="SdkServiceRecovery"/>, an
/// <c>SdkConnectFactory</c> or anything else that reads the host-wide lease must pass <see cref="Lease"/> instead of
/// falling back to <see cref="HostUploadLease.Default"/>, so a lease held by a real server (or another test) on the
/// machine can never change the outcome.
/// </summary>
public sealed class IsolatedUploadLease : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "LogixMcp.Tests", "lease-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the lease handle over a unique file path.</summary>
    public IsolatedUploadLease() =>
        Lease = new HostUploadLease(LeasePath);

    /// <summary>The per-test lease.</summary>
    public HostUploadLease Lease { get; }

    /// <summary>The unique lease file path (the file exists only while the lease is held).</summary>
    public string LeasePath => Path.Combine(_dir, "upload.lease");

    /// <summary>Deletes the per-test lease directory. Safe to call twice.</summary>
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
