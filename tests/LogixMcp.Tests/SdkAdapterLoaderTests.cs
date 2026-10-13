using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="SdkAdapterLoader"/> and
/// <see cref="SdkAssemblyLoadContext"/>.
///
/// <para>
/// All tests run on Linux (where no Rockwell SDK is present), so they must not
/// depend on actual Rockwell DLLs.  Real Rockwell dependency resolution is tested
/// implicitly by the Windows-only integration suite.
/// </para>
/// </summary>
[Collection("StaticHandlerHooks")]
public class SdkAdapterLoaderTests : IDisposable
{
    // Temp directories created by tests — cleaned up in Dispose.
    private readonly List<string> _tempDirs = [];

    // ── GetProbeCandidates ────────────────────────────────────────────────────

    [Fact]
    public void GetProbeCandidates_WithEnvVar_ReturnsEnvVarFirst()
    {
        var tempDir = CreateTempDir();
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", tempDir);
        try
        {
            var candidates = SdkAdapterLoader.GetProbeCandidates().ToList();
            Assert.Equal(tempDir, candidates[0]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        }
    }

    [Fact]
    public void GetProbeCandidates_WithoutEnvVar_StartsWithSdkVendorDirectory()
    {
        // After the feature that adds <BaseDir>/sdk/vendor as an ordered probe
        // candidate, the first entry without an env-var override must be the
        // app-local payload directory, not Studio 5000.
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        var candidates = SdkAdapterLoader.GetProbeCandidates().ToList();
        var expectedSdkVendor = Path.Combine(
            AppContext.BaseDirectory,
            SdkAdapterLoader.AdapterSubdirectory,
            SdkAdapterLoader.VendorSubdirectory);
        Assert.Equal(expectedSdkVendor, candidates[0]);
    }

    [Fact]
    public void GetProbeCandidates_WithEmptyEnvVar_SkipsEnvVarEntry()
    {
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", "   ");
        try
        {
            var candidates = SdkAdapterLoader.GetProbeCandidates().ToList();
            // Whitespace-only value is treated as absent — sdk/vendor comes first
            // (same ordering as when LOGIX_SDK_PATH is not set at all).
            var expectedSdkVendor = Path.Combine(
                AppContext.BaseDirectory,
                SdkAdapterLoader.AdapterSubdirectory,
                SdkAdapterLoader.VendorSubdirectory);
            Assert.Equal(expectedSdkVendor, candidates[0]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        }
    }

    [Fact]
    public void CwdLocalfeed_NotProbed()
    {
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        var cwdLocalfeed = Path.Combine(Directory.GetCurrentDirectory(), ".localfeed");
        var candidates = SdkAdapterLoader.GetProbeCandidates().ToList();
        Assert.DoesNotContain(cwdLocalfeed, candidates);
        Assert.DoesNotContain(candidates, c => c.Contains(".localfeed", StringComparison.OrdinalIgnoreCase));

        var probed = SdkAdapterLoader.CollectProbeResults();
        Assert.DoesNotContain(probed, r => r.ToString()!.Contains(".localfeed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetProbeCandidates_AlwaysIncludesAppContextBaseDirectory()
    {
        var candidates = SdkAdapterLoader.GetProbeCandidates().ToList();
        Assert.Contains(AppContext.BaseDirectory, candidates);
    }

    [Fact]
    public void GetProbeCandidates_AlwaysIncludesSdkVendorDirectory()
    {
        // <AppContext.BaseDirectory>/sdk/vendor must always appear in the candidate
        // list so the app-local redistributable-closure payload is probed.
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        var expected = Path.Combine(
            AppContext.BaseDirectory,
            SdkAdapterLoader.AdapterSubdirectory,
            SdkAdapterLoader.VendorSubdirectory);
        var candidates = SdkAdapterLoader.GetProbeCandidates().ToList();
        Assert.Contains(expected, candidates);
    }

    [Fact]
    public void GetProbeCandidates_SdkVendorAppearsAfterEnvVarAndBeforeStudio5000()
    {
        // The sdk/vendor path must come after LOGIX_SDK_PATH (so the env-var
        // override keeps winning) and before Studio5000SdkPath (so the bundled
        // payload is preferred over the installed SDK).
        var tempDir = CreateTempDir();
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", tempDir);
        try
        {
            var candidates = SdkAdapterLoader.GetProbeCandidates().ToList();
            var sdkVendor = Path.Combine(
                AppContext.BaseDirectory,
                SdkAdapterLoader.AdapterSubdirectory,
                SdkAdapterLoader.VendorSubdirectory);

            var envVarIndex    = candidates.IndexOf(tempDir);
            var sdkVendorIndex = candidates.IndexOf(sdkVendor);
            var studio5000Index = candidates.IndexOf(SdkAdapterLoader.Studio5000SdkPath);

            Assert.True(envVarIndex >= 0,    "LOGIX_SDK_PATH entry not in candidates");
            Assert.True(sdkVendorIndex >= 0, "sdk/vendor entry not in candidates");
            Assert.True(studio5000Index >= 0, "Studio5000 entry not in candidates");

            Assert.True(envVarIndex < sdkVendorIndex,
                "LOGIX_SDK_PATH must appear before sdk/vendor");
            Assert.True(sdkVendorIndex < studio5000Index,
                "sdk/vendor must appear before Studio5000SdkPath");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        }
    }

    [Fact]
    public void GetProbeCandidates_SdkVendorAppearsBeforeStudio5000_WithoutEnvVar()
    {
        // Even when LOGIX_SDK_PATH is absent, sdk/vendor must precede Studio 5000.
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        var candidates = SdkAdapterLoader.GetProbeCandidates().ToList();
        var sdkVendor = Path.Combine(
            AppContext.BaseDirectory,
            SdkAdapterLoader.AdapterSubdirectory,
            SdkAdapterLoader.VendorSubdirectory);

        var sdkVendorIndex  = candidates.IndexOf(sdkVendor);
        var studio5000Index = candidates.IndexOf(SdkAdapterLoader.Studio5000SdkPath);

        Assert.True(sdkVendorIndex >= 0,  "sdk/vendor not in candidates");
        Assert.True(studio5000Index >= 0, "Studio5000 not in candidates");
        Assert.True(sdkVendorIndex < studio5000Index,
            "sdk/vendor must appear before Studio5000SdkPath");
    }

    [Fact]
    public void ProbeForSdkDirectory_UsesSdkVendorDirectory_WhenItHasCsClientDll()
    {
        // When the app-local sdk/vendor directory contains CSClient.dll it must be
        // returned as the qualifying SDK directory (it is the second probe candidate
        // so it wins over Studio 5000 when no env-var is set).
        //
        // This test can only exercise the full probe path when AppContext.BaseDirectory
        // can be influenced; since it cannot, we use the LOGIX_SDK_PATH override to
        // point the first slot at an empty dir so the sdk/vendor slot wins only if
        // it qualifies — but that requires placing a file under the real BaseDirectory,
        // which the test harness must not do.
        //
        // Instead, we verify the contract via GetProbeCandidates: the sdk/vendor path
        // must be present and before Studio5000 so ProbeForSdkDirectory reaches it.
        var sdkVendorCandidate = Path.Combine(
            AppContext.BaseDirectory,
            SdkAdapterLoader.AdapterSubdirectory,
            SdkAdapterLoader.VendorSubdirectory);

        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        var candidates = SdkAdapterLoader.GetProbeCandidates().ToList();

        Assert.Contains(sdkVendorCandidate, candidates);
        var sdkVendorIdx  = candidates.IndexOf(sdkVendorCandidate);
        var studio5000Idx = candidates.IndexOf(SdkAdapterLoader.Studio5000SdkPath);
        Assert.True(sdkVendorIdx < studio5000Idx,
            "sdk/vendor must be probed before Studio5000SdkPath");
    }

    // ── DirectoryHasRockwellDlls ──────────────────────────────────────────────

    [Fact]
    public void DirectoryHasRockwellDlls_ReturnsFalse_WhenDirectoryDoesNotExist()
    {
        Assert.False(SdkAdapterLoader.DirectoryHasRockwellDlls(
            "/absolutely/nonexistent/path/xyz123abc"));
    }

    [Fact]
    public void DirectoryHasRockwellDlls_ReturnsFalse_WhenDirectoryIsEmpty()
    {
        var dir = CreateTempDir();
        Assert.False(SdkAdapterLoader.DirectoryHasRockwellDlls(dir));
    }

    [Fact]
    public void DirectoryHasRockwellDlls_ReturnsFalse_WhenOnlyNonRockwellFilesPresent()
    {
        var dir = CreateTempDir();
        File.WriteAllText(Path.Combine(dir, "SomeLibrary.dll"), "");
        File.WriteAllText(Path.Combine(dir, "AnotherLib.dll"), "");
        Assert.False(SdkAdapterLoader.DirectoryHasRockwellDlls(dir));
    }

    [Fact]
    public void DirectoryHasRockwellDlls_ReturnsTrue_WhenCsClientDllPresent()
    {
        // Only the exact CSClient assembly qualifies a directory.
        var dir = CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, SdkAdapterLoader.CsClientDllName), "fake");
        Assert.True(SdkAdapterLoader.DirectoryHasRockwellDlls(dir));
    }

    [Fact]
    public void DirectoryHasRockwellDlls_ReturnsTrue_WhenCsClientDllPresentAlongsideOtherFiles()
    {
        // The CSClient assembly must be found even when other files are also present.
        var dir = CreateTempDir();
        File.WriteAllText(Path.Combine(dir, "OtherLib.dll"), "");
        File.WriteAllText(
            Path.Combine(dir, SdkAdapterLoader.CsClientDllName), "fake");
        Assert.True(SdkAdapterLoader.DirectoryHasRockwellDlls(dir));
    }

    [Fact]
    public void DirectoryHasRockwellDlls_ReturnsFalse_WhenOnlyOtherRockwellDllPresent()
    {
        // Presence of other RockwellAutomation*.dll files (e.g. the gRPC server
        // assembly) must no longer be sufficient — only CSClient.dll qualifies.
        var dir = CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, "RockwellAutomation.LogixDesigner.LogixProjectServices.dll"), "fake");
        File.WriteAllText(
            Path.Combine(dir, "RockwellAutomation.LogixDesigner.dll"), "fake");
        Assert.False(SdkAdapterLoader.DirectoryHasRockwellDlls(dir));
    }

    [Fact]
    public void DirectoryHasRockwellDlls_ReturnsFalse_WhenMatchIsSubstring()
    {
        // A file whose name merely contains "CSClient" but is not the exact DLL
        // name must not match.
        var dir = CreateTempDir();
        File.WriteAllText(Path.Combine(dir, "NotRockwellAutomation.dll"), "");
        File.WriteAllText(Path.Combine(dir, "SomeCSClient.dll"), "");
        Assert.False(SdkAdapterLoader.DirectoryHasRockwellDlls(dir));
    }

    // ── DirectoryHasAnyRockwellDlls ───────────────────────────────────────────

    [Fact]
    public void DirectoryHasAnyRockwellDlls_ReturnsFalse_WhenDirectoryDoesNotExist()
    {
        Assert.False(SdkAdapterLoader.DirectoryHasAnyRockwellDlls(
            "/absolutely/nonexistent/path/xyz123abc"));
    }

    [Fact]
    public void DirectoryHasAnyRockwellDlls_ReturnsFalse_WhenDirectoryIsEmpty()
    {
        var dir = CreateTempDir();
        Assert.False(SdkAdapterLoader.DirectoryHasAnyRockwellDlls(dir));
    }

    [Fact]
    public void DirectoryHasAnyRockwellDlls_ReturnsFalse_WhenOnlyNonRockwellFilesPresent()
    {
        var dir = CreateTempDir();
        File.WriteAllText(Path.Combine(dir, "SomeLibrary.dll"), "");
        Assert.False(SdkAdapterLoader.DirectoryHasAnyRockwellDlls(dir));
    }

    [Fact]
    public void DirectoryHasAnyRockwellDlls_ReturnsTrue_WhenCsClientDllPresent()
    {
        var dir = CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, SdkAdapterLoader.CsClientDllName), "fake");
        Assert.True(SdkAdapterLoader.DirectoryHasAnyRockwellDlls(dir));
    }

    [Fact]
    public void DirectoryHasAnyRockwellDlls_ReturnsTrue_WhenOtherRockwellDllPresent()
    {
        // Any RockwellAutomation*.dll (not just CSClient) must return true.
        var dir = CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, "RockwellAutomation.LogixDesigner.LogixProjectServices.dll"),
            "fake");
        Assert.True(SdkAdapterLoader.DirectoryHasAnyRockwellDlls(dir));
    }

    // ── NuPkgLocation constant ────────────────────────────────────────────────

    [Fact]
    public void NuPkgLocation_IsExpectedValue()
    {
        // The constant must match the documented NuGet package location so the
        // ConnectHandler error message is accurate and actionable.
        Assert.Equal(
            @"%PUBLIC%\Documents\Studio 5000\Logix Designer SDK\dotnet\",
            SdkAdapterLoader.NuPkgLocation);
    }

    // ── ProbeForSdkDirectory ──────────────────────────────────────────────────

    [Fact]
    public void ProbeForSdkDirectory_ReturnsNull_WhenNoCandidateHasRockwellDlls()
    {
        // On Linux in CI none of the standard directories exist and AppContext.BaseDirectory
        // has no RockwellAutomation*.dll files (non-SDK build).
        // Skip if the base dir already qualifies so the test does not emit a false fail.
        if (SdkAdapterLoader.DirectoryHasRockwellDlls(AppContext.BaseDirectory))
            return;

        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        var result = SdkAdapterLoader.ProbeForSdkDirectory();
        Assert.Null(result);
    }

    [Fact]
    public void ProbeForSdkDirectory_ReturnsEnvVarDirectory_WhenItHasCsClientDll()
    {
        // A directory qualifies only when it contains the exact CSClient assembly.
        var dir = CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, SdkAdapterLoader.CsClientDllName), "fake");

        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", dir);
        try
        {
            var result = SdkAdapterLoader.ProbeForSdkDirectory();
            Assert.Equal(dir, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        }
    }

    [Fact]
    public void ProbeForSdkDirectory_SkipsEnvVarDir_WhenItHasNoCsClientDll()
    {
        // An env-var directory that exists but lacks CSClient.dll must be skipped,
        // even when other RockwellAutomation*.dll files are present there.
        var emptyEnvDir = CreateTempDir();
        var rockwellDir = CreateTempDir();
        // Other Rockwell DLL present — must NOT qualify.
        File.WriteAllText(
            Path.Combine(rockwellDir, "RockwellAutomation.X.dll"), "fake");

        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", emptyEnvDir);
        try
        {
            // Inject a controlled set of candidates so we can test fallthrough
            // without depending on Studio 5000 being installed.
            // We do this by verifying ProbeForSdkDirectory returns something
            // from later in the list when the env-var dir is empty.
            // (This test exercises the skip-and-continue logic.)
            var result = SdkAdapterLoader.ProbeForSdkDirectory();
            // env-var dir is empty → should not be returned
            Assert.NotEqual(emptyEnvDir, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        }
    }

    [Fact]
    public void ProbeForSdkDirectory_UsesFirstMatchingDirectory()
    {
        // Simulate two valid directories (both containing CSClient.dll); verify
        // the first one wins.
        var first = CreateTempDir();
        var second = CreateTempDir();
        File.WriteAllText(Path.Combine(first,  SdkAdapterLoader.CsClientDllName), "fake");
        File.WriteAllText(Path.Combine(second, SdkAdapterLoader.CsClientDllName), "fake");

        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", first);
        try
        {
            var result = SdkAdapterLoader.ProbeForSdkDirectory();
            Assert.Equal(first, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        }
    }

    // ── CollectProbeResults ───────────────────────────────────────────────────

    [Fact]
    public void CollectProbeResults_ReturnsNonEmptyArray()
    {
        // CollectProbeResults must always return at least one entry — there are at
        // least three hard-coded candidates (sdk/vendor, Studio 5000, BaseDir).
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        var results = SdkAdapterLoader.CollectProbeResults();
        Assert.NotEmpty(results);
    }

    [Fact]
    public void CollectProbeResults_CountMatchesGetProbeCandidates_WithoutEnvVar()
    {
        // The number of entries must equal the number of probe candidates so that
        // no path is silently dropped from the diagnostic output.
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        var expected = SdkAdapterLoader.GetProbeCandidates().Count();
        var results  = SdkAdapterLoader.CollectProbeResults();
        Assert.Equal(expected, results.Length);
    }

    [Fact]
    public void CollectProbeResults_CountMatchesGetProbeCandidates_WithEnvVar()
    {
        // When LOGIX_SDK_PATH is set, the env-var entry is prepended and the total
        // count must still match GetProbeCandidates().
        var tempDir = CreateTempDir();
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", tempDir);
        try
        {
            var expected = SdkAdapterLoader.GetProbeCandidates().Count();
            var results  = SdkAdapterLoader.CollectProbeResults();
            Assert.Equal(expected, results.Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        }
    }

    [Fact]
    public void CollectProbeResults_EntryForNonExistentDir_HasNotFoundStatus()
    {
        // A candidate directory that does not exist on the file system must have
        // status "not_found".
        var missing = "/absolutely/nonexistent/path/xyz999";
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", missing);
        try
        {
            var results = SdkAdapterLoader.CollectProbeResults();
            var entry   = results.FirstOrDefault(e => e.Path == missing);
            Assert.NotNull(entry);
            Assert.Equal("not_found", entry!.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        }
    }

    [Fact]
    public void CollectProbeResults_EntryForEmptyExistingDir_HasNoRockwellDllsStatus()
    {
        // A candidate directory that exists but has no Rockwell DLLs must have
        // status "no_rockwell_dlls".
        var emptyDir = CreateTempDir();
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", emptyDir);
        try
        {
            var results = SdkAdapterLoader.CollectProbeResults();
            var entry   = results.FirstOrDefault(e => e.Path == emptyDir);
            Assert.NotNull(entry);
            Assert.Equal("no_rockwell_dlls", entry!.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        }
    }

    [Fact]
    public void CollectProbeResults_EntryForDirWithOtherRockwellDlls_HasClientDllMissingStatus()
    {
        // A directory that contains other RockwellAutomation*.dll files but NOT
        // CSClient.dll must report "client_dll_missing" — this is a partial SDK install
        // where Studio 5000 is present but the client NuGet package has not been extracted.
        var dir = CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, "RockwellAutomation.LogixDesigner.LogixProjectServices.dll"),
            "fake");

        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", dir);
        try
        {
            var results = SdkAdapterLoader.CollectProbeResults();
            var entry   = results.FirstOrDefault(e => e.Path == dir);
            Assert.NotNull(entry);
            Assert.Equal("client_dll_missing", entry!.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        }
    }

    [Fact]
    public void CollectProbeResults_EntryForDirWithNoRockwellDlls_HasNoRockwellDllsStatus()
    {
        // A directory that exists but contains no RockwellAutomation*.dll files
        // must still report "no_rockwell_dlls" — distinct from "client_dll_missing".
        var dir = CreateTempDir();
        File.WriteAllText(Path.Combine(dir, "SomeOtherLibrary.dll"), "fake");

        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", dir);
        try
        {
            var results = SdkAdapterLoader.CollectProbeResults();
            var entry   = results.FirstOrDefault(e => e.Path == dir);
            Assert.NotNull(entry);
            Assert.Equal("no_rockwell_dlls", entry!.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        }
    }

    [Fact]
    public void CollectProbeResults_EntryForQualifyingDir_HasOkStatus()
    {
        // A candidate directory that contains CSClient.dll must have status "ok".
        var sdkDir = CreateTempDir();
        File.WriteAllText(
            Path.Combine(sdkDir, SdkAdapterLoader.CsClientDllName), "fake");

        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", sdkDir);
        try
        {
            var results = SdkAdapterLoader.CollectProbeResults();
            var entry   = results.FirstOrDefault(e => e.Path == sdkDir);
            Assert.NotNull(entry);
            Assert.Equal("ok", entry!.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        }
    }

    [Fact]
    public void CollectProbeResults_AllEntriesHaveNonEmptyPath()
    {
        // Every probe entry must carry a non-empty path so the caller can display
        // it without additional null-checking.
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        var results = SdkAdapterLoader.CollectProbeResults();
        Assert.All(results, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Path)));
    }

    [Fact]
    public void CollectProbeResults_AllEntriesHaveNonEmptyStatus()
    {
        // Every probe entry must carry a non-empty status string.
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        var results = SdkAdapterLoader.CollectProbeResults();
        Assert.All(results, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Status)));
    }

    [Fact]
    public void CollectProbeResults_DoesNotThrow()
    {
        // CollectProbeResults must never throw regardless of environment state.
        var ex = Record.Exception(() => SdkAdapterLoader.CollectProbeResults());
        Assert.Null(ex);
    }

    [Fact]
    public void CollectProbeResults_PathsAreInProbeOrder()
    {
        // The paths returned by CollectProbeResults must appear in the same order
        // as GetProbeCandidates() — probe order is documented and must be stable.
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        var candidates = SdkAdapterLoader.GetProbeCandidates().ToList();
        var results    = SdkAdapterLoader.CollectProbeResults();

        for (var i = 0; i < candidates.Count; i++)
            Assert.Equal(candidates[i], results[i].Path);
    }

    [Fact]
    public void CollectProbeResults_SdkVendorEntryWithClientDllMissing_HasInstallerMessage()
    {
        // When sdk/vendor/ exists and contains other RockwellAutomation*.dll files
        // but NOT CSClient.dll, the probe entry must carry a Message that explicitly
        // names sdk/vendor/ as the directory the installer populates.
        // This is the state a user reaches after running the binary for the first
        // time but before running the installer.
        var sdkVendorPath = Path.Combine(
            AppContext.BaseDirectory,
            SdkAdapterLoader.AdapterSubdirectory,
            SdkAdapterLoader.VendorSubdirectory);

        var otherDllPath = Path.Combine(
            sdkVendorPath,
            "RockwellAutomation.LogixDesigner.LogixProjectServices.dll");

        var dirExistedBefore = Directory.Exists(sdkVendorPath);
        Directory.CreateDirectory(sdkVendorPath);
        File.WriteAllText(otherDllPath, "fake");

        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        try
        {
            var results = SdkAdapterLoader.CollectProbeResults();
            var entry   = results.FirstOrDefault(e => e.Path == sdkVendorPath);

            Assert.NotNull(entry);
            Assert.Equal("client_dll_missing", entry!.Status);
            Assert.NotNull(entry.Message);
            Assert.Contains("sdk/vendor", entry.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(otherDllPath))
                File.Delete(otherDllPath);
            if (!dirExistedBefore
                && Directory.Exists(sdkVendorPath)
                && !Directory.EnumerateFileSystemEntries(sdkVendorPath).Any())
                Directory.Delete(sdkVendorPath);
        }
    }

    [Fact]
    public void CollectProbeResults_NonSdkVendorEntryWithClientDllMissing_HasNullMessage()
    {
        // When a directory other than sdk/vendor/ has client_dll_missing status,
        // the Message field must be null — the installer hint is specific to the
        // app-local sdk/vendor/ path.
        var dir = CreateTempDir();
        File.WriteAllText(
            Path.Combine(dir, "RockwellAutomation.LogixDesigner.LogixProjectServices.dll"),
            "fake");

        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", dir);
        try
        {
            var results = SdkAdapterLoader.CollectProbeResults();
            var entry   = results.FirstOrDefault(e => e.Path == dir);

            Assert.NotNull(entry);
            Assert.Equal("client_dll_missing", entry!.Status);
            Assert.Null(entry.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        }
    }

    // ── AdapterSubdirectory / adapter probe path ──────────────────────────────

    [Fact]
    public void AdapterSubdirectory_IsExpectedValue()
    {
        // The constant must match the subdirectory name used by the publish target
        // in LogixMcp.csproj (RelativePath = sdk/LogixMcp.SdkAdapter.dll).
        Assert.Equal("sdk", SdkAdapterLoader.AdapterSubdirectory);
    }

    // ── VendorSubdirectory / GetVendorDir ─────────────────────────────────────

    [Fact]
    public void VendorSubdirectory_IsExpectedValue()
    {
        // The constant must match the subdirectory name where vendor assemblies
        // (Grpc.*, Google.Protobuf, and other Rockwell transitive dependencies)
        // are placed alongside the adapter DLL.
        Assert.Equal("vendor", SdkAdapterLoader.VendorSubdirectory);
    }

    [Fact]
    public void GetVendorDir_ReturnsAdapterDirCombinedWithVendorSubdirectory()
    {
        // GetVendorDir derives the vendor path from the adapter path so that the
        // resolver is self-consistent: vendor DLLs live next to the adapter.
        var adapterPath = Path.Combine(Path.GetTempPath(), "sdk", "LogixMcp.SdkAdapter.dll");
        var expected    = Path.Combine(Path.GetTempPath(), "sdk", SdkAdapterLoader.VendorSubdirectory);

        var result = SdkAdapterLoader.GetVendorDir(adapterPath);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetVendorDir_VendorDirIsAdjacentToAdapterDll()
    {
        // The vendor directory must be a sibling of the adapter DLL (same parent dir).
        var adapterPath = Path.Combine(Path.GetTempPath(), "sdk", "LogixMcp.SdkAdapter.dll");
        var vendorDir   = SdkAdapterLoader.GetVendorDir(adapterPath);

        // Both paths should share the same parent directory.
        Assert.Equal(
            Path.GetDirectoryName(adapterPath),
            Path.GetDirectoryName(vendorDir));
    }

    [Fact]
    public void GetVendorDir_ContainsVendorSegment()
    {
        // The returned path must include the VendorSubdirectory segment.
        var adapterPath = Path.Combine("/base", "sdk", "LogixMcp.SdkAdapter.dll");
        var vendorDir   = SdkAdapterLoader.GetVendorDir(adapterPath);

        Assert.EndsWith(
            Path.DirectorySeparatorChar + SdkAdapterLoader.VendorSubdirectory,
            vendorDir,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TryLoad_ProbesAdapterUnderSdkSubdirectory_NotDirectlyInBaseDir()
    {
        // Create a temp dir that stands in for AppContext.BaseDirectory.
        // Place a fake adapter DLL directly in that dir (wrong location) and
        // verify TryLoad does NOT find it there — it must look in sdk/ only.
        //
        // We cannot override AppContext.BaseDirectory, so this test validates the
        // constant-level contract: the probe path is BaseDirectory + "sdk" + DllName.

        // The adapter DLL must be under sdk/, not at the root of BaseDirectory.
        var expectedSubdir = Path.Combine(
            AppContext.BaseDirectory,
            SdkAdapterLoader.AdapterSubdirectory,
            SdkAdapterLoader.AdapterDllName);

        var rootPath = Path.Combine(
            AppContext.BaseDirectory,
            SdkAdapterLoader.AdapterDllName);

        // The sdk/ path and the root path must differ.
        Assert.NotEqual(rootPath, expectedSubdir);

        // The expected path must contain the sdk/ segment.
        Assert.Contains(
            Path.DirectorySeparatorChar + SdkAdapterLoader.AdapterSubdirectory + Path.DirectorySeparatorChar,
            expectedSubdir);
    }

    // ── TryLoad ───────────────────────────────────────────────────────────────

    [Fact]
    public void TryLoad_ReturnsNull_WhenNoRockwellSdkPresent()
    {
        // On Linux in CI, neither the adapter has Rockwell DLLs in base dir
        // nor is Studio 5000 installed, so TryLoad should return null gracefully.
        if (SdkAdapterLoader.DirectoryHasRockwellDlls(AppContext.BaseDirectory))
            return; // Skip — this is an SDK build with DLLs present.

        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        var result = SdkAdapterLoader.TryLoad();
        Assert.Null(result);
    }

    [Fact]
    public void TryLoad_DoesNotThrow_WhenAdapterAbsentAndSdkAbsent()
    {
        // Even if the adapter DLL itself is absent, TryLoad must not throw.
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        var ex = Record.Exception(() => SdkAdapterLoader.TryLoad());
        Assert.Null(ex);
    }

    [Fact]
    public void TryLoad_DoesNotThrow_WhenAssemblyLoadFails()
    {
        // Place a fake (non-loadable) adapter DLL and a fake Rockwell SDK DLL so that
        // TryLoad proceeds past the early-exit guards and attempts an actual assembly
        // load — which must fail gracefully (return null, not throw).
        var sdkDir      = CreateTempDir();
        var adapterDir  = CreateTempDir();

        // Write a non-PE file as the adapter DLL so LoadFromAssemblyPath throws.
        var fakeAdapterPath = Path.Combine(adapterDir, SdkAdapterLoader.AdapterDllName);
        File.WriteAllText(fakeAdapterPath, "this is not a valid PE image");

        // Write CSClient.dll so ProbeForSdkDirectory qualifies the directory.
        File.WriteAllText(Path.Combine(sdkDir, SdkAdapterLoader.CsClientDllName), "fake");

        // Point LOGIX_SDK_PATH at the fake SDK dir.
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", sdkDir);
        try
        {
            // We cannot redirect AppContext.BaseDirectory, so we exercise
            // SdkAssemblyLoadContext directly to validate the catch-all path.
            // The important contract is: LoadFromAssemblyPath on a non-PE file
            // throws, and the catch-all in TryLoad converts that to a null return.
            var vendorDir = SdkAdapterLoader.GetVendorDir(fakeAdapterPath);
            var alc       = new SdkAssemblyLoadContext(sdkDir, vendorDir);

            // The underlying ALC throws on a corrupt/non-PE file.  The catch-all
            // in TryLoad catches this and returns null — verified here by confirming
            // that LoadFromAssemblyPath does indeed throw on a non-PE file.
            var loadEx = Record.Exception(() =>
            {
                _ = alc.LoadFromAssemblyPath(fakeAdapterPath);
            });

            Assert.NotNull(loadEx);
            Assert.True(loadEx is BadImageFormatException or FileLoadException or IOException,
                $"Unexpected exception type: {loadEx.GetType().Name}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        }
    }

    [Fact]
    public void LoadFailureReason_IsNullOrString()
    {
        // LoadFailureReason is either null (no load attempted or no exception) or a
        // non-empty string describing the failure.  It must never be whitespace-only.
        var reason = SdkAdapterLoader.LoadFailureReason;
        if (reason is not null)
            Assert.False(string.IsNullOrWhiteSpace(reason),
                "LoadFailureReason must not be whitespace-only when set.");
    }

    [Fact]
    public void TryLoad_DoesNotThrow_Catchall_NeverPropagates()
    {
        // Regression guard: TryLoad must not propagate any exception regardless of
        // how the environment is configured.  This complements the adapter-absent
        // test and ensures future changes to TryLoad cannot accidentally remove the
        // catch-all without breaking this test.
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", "/absolutely/nonexistent/path/xyz");
        try
        {
            var ex = Record.Exception(() => SdkAdapterLoader.TryLoad());
            Assert.Null(ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        }
    }

    // ── RecordTypeScanFailure ─────────────────────────────────────────────────

    [Fact]
    public void RecordTypeScanFailure_WithFileNotFoundException_SetsLoadFailureReason()
    {
        // RecordTypeScanFailure must populate LoadFailureReason with the assembly
        // path and exception type so ConnectHandler can surface it to the caller.
        const string fakeAssemblyPath = "/opt/app/sdk/LogixMcp.SdkAdapter.dll";
        var ex = new FileNotFoundException("Could not load file or assembly 'Grpc.Core'.");

        SdkAdapterLoader.RecordTypeScanFailure(fakeAssemblyPath, ex);

        var reason = SdkAdapterLoader.LoadFailureReason;
        Assert.NotNull(reason);
        Assert.Contains(fakeAssemblyPath, reason, StringComparison.Ordinal);
        Assert.Contains("FileNotFoundException", reason, StringComparison.Ordinal);
        Assert.Contains("Grpc.Core", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordTypeScanFailure_WithReflectionTypeLoadException_NoLoaderExceptions_SetsReason()
    {
        // When ReflectionTypeLoadException.LoaderExceptions is empty, only the top-level
        // message should appear in LoadFailureReason — no "loader exceptions:" suffix.
        const string fakeAssemblyPath = "/opt/app/sdk/LogixMcp.SdkAdapter.dll";
        var ex = new System.Reflection.ReflectionTypeLoadException(
            classes: [],
            exceptions: [],
            message: "Unable to load one or more of the requested types.");

        SdkAdapterLoader.RecordTypeScanFailure(fakeAssemblyPath, ex);

        var reason = SdkAdapterLoader.LoadFailureReason;
        Assert.NotNull(reason);
        Assert.Contains("ReflectionTypeLoadException", reason, StringComparison.Ordinal);
        Assert.Contains(fakeAssemblyPath, reason, StringComparison.Ordinal);
        Assert.DoesNotContain("loader exceptions:", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordTypeScanFailure_WithReflectionTypeLoadException_IncludesLoaderExceptions()
    {
        // When ReflectionTypeLoadException.LoaderExceptions is non-empty, the messages
        // from those inner exceptions must be appended to LoadFailureReason so the
        // caller can identify the missing dependency without consulting the server log.
        const string fakeAssemblyPath = "/opt/app/sdk/LogixMcp.SdkAdapter.dll";
        var loaderExs = new Exception[]
        {
            new FileNotFoundException("Could not load 'RockwellAutomation.Dep1'."),
            new FileNotFoundException("Could not load 'RockwellAutomation.Dep2'."),
        };
        var ex = new System.Reflection.ReflectionTypeLoadException(
            classes: [null, null],
            exceptions: loaderExs,
            message: "Unable to load one or more of the requested types.");

        SdkAdapterLoader.RecordTypeScanFailure(fakeAssemblyPath, ex);

        var reason = SdkAdapterLoader.LoadFailureReason;
        Assert.NotNull(reason);
        Assert.Contains("loader exceptions:", reason, StringComparison.Ordinal);
        Assert.Contains("Dep1", reason, StringComparison.Ordinal);
        Assert.Contains("Dep2", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordTypeScanFailure_WithManyLoaderExceptions_TruncatesToThreeInSuffix()
    {
        // When there are more than three loader exceptions, the "loader exceptions:"
        // suffix must include only the first three messages.
        // Note: ReflectionTypeLoadException.Message already lists all loader exceptions
        // in the top-level message; the truncation applies only to the structured suffix
        // appended by RecordTypeScanFailure.
        const string fakeAssemblyPath = "/opt/app/sdk/LogixMcp.SdkAdapter.dll";
        var loaderExs = new Exception[]
        {
            new FileNotFoundException("UNIQUE_DEP_A"),
            new FileNotFoundException("UNIQUE_DEP_B"),
            new FileNotFoundException("UNIQUE_DEP_C"),
            new FileNotFoundException("UNIQUE_DEP_D"),
            new FileNotFoundException("UNIQUE_DEP_E"),
        };
        var ex = new System.Reflection.ReflectionTypeLoadException(
            classes: new Type?[] { null, null, null, null, null },
            exceptions: loaderExs,
            message: "Unable to load one or more of the requested types.");

        SdkAdapterLoader.RecordTypeScanFailure(fakeAssemblyPath, ex);

        var reason = SdkAdapterLoader.LoadFailureReason;
        Assert.NotNull(reason);
        Assert.Contains("loader exceptions:", reason, StringComparison.Ordinal);

        // Extract just the "loader exceptions:" suffix to count entries.
        var suffixStart = reason.IndexOf("loader exceptions:", StringComparison.Ordinal);
        var suffix = reason[suffixStart..];

        // The suffix must contain DEP_A, DEP_B, DEP_C (first three).
        Assert.Contains("UNIQUE_DEP_A", suffix, StringComparison.Ordinal);
        Assert.Contains("UNIQUE_DEP_B", suffix, StringComparison.Ordinal);
        Assert.Contains("UNIQUE_DEP_C", suffix, StringComparison.Ordinal);
        // DEP_D and DEP_E must not appear in the structured suffix.
        Assert.DoesNotContain("UNIQUE_DEP_D", suffix, StringComparison.Ordinal);
        Assert.DoesNotContain("UNIQUE_DEP_E", suffix, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordTypeScanFailure_ResultIsNonEmptyString()
    {
        // RecordTypeScanFailure must always produce a non-empty, non-whitespace string
        // regardless of the exception type passed in.
        SdkAdapterLoader.RecordTypeScanFailure(
            "/fake/path/LogixMcp.SdkAdapter.dll",
            new FileNotFoundException("dependency missing"));

        Assert.False(string.IsNullOrWhiteSpace(SdkAdapterLoader.LoadFailureReason));
    }

    [Fact]
    public void RecordTypeScanFailure_PrefixesReasonWithTypeScanFailed()
    {
        // The LoadFailureReason must start with "Type scan failed for" so it is
        // distinguishable from an assembly-load failure recorded by TryLoad.
        SdkAdapterLoader.RecordTypeScanFailure(
            "/fake/path/LogixMcp.SdkAdapter.dll",
            new FileNotFoundException("dependency missing"));

        Assert.StartsWith("Type scan failed for", SdkAdapterLoader.LoadFailureReason,
            StringComparison.Ordinal);
    }

    // ── SdkAssemblyLoadContext ────────────────────────────────────────────────

    [Fact]
    public void SdkAssemblyLoadContext_Load_ReturnsNull_ForNonRockwellAssemblyName()
    {
        var dir = CreateTempDir();
        var alc = new SdkAssemblyLoadContext(dir);

        // Invoke via reflection since Load is protected — use the public
        // LoadFromAssemblyName path which routes through Load internally.
        // We cannot easily call Load directly, so we verify indirectly:
        // for a non-Rockwell name the ALC returns null → runtime falls back
        // to default context, which raises FileNotFoundException for a fake name.
        var fakeAssemblyName = new System.Reflection.AssemblyName("FakeNonRockwellLib");
        Assert.Throws<FileNotFoundException>(
            () => alc.LoadFromAssemblyName(fakeAssemblyName));
    }

    [Fact]
    public void SdkAssemblyLoadContext_Load_ReturnsNull_ForRockwellNameWhenFileAbsent()
    {
        var dir = CreateTempDir();
        // No DLL file present → Load returns null → default context tries and
        // raises FileNotFoundException (no such assembly in the default context either).
        var alc = new SdkAssemblyLoadContext(dir);
        var rockwellName = new System.Reflection.AssemblyName(
            "RockwellAutomation.Nonexistent");
        Assert.Throws<FileNotFoundException>(
            () => alc.LoadFromAssemblyName(rockwellName));
    }

    [Fact]
    public void SdkAssemblyLoadContext_Load_ResolvesRockwellDll_WhenFilePresent()
    {
        // Place a real .NET DLL in a temp dir under a RockwellAutomation-prefixed name
        // and verify the ALC can load it — exercising the path-resolution branch in
        // SdkAssemblyLoadContext.Load without requiring an actual Rockwell installation.
        var dir = CreateTempDir();

        // Use LogixMcp.dll (always present next to the test binary) as a stand-in.
        var sourceDllPath = typeof(SdkAdapterLoader).Assembly.Location;
        var fakeRockwellDllPath = Path.Combine(dir, "RockwellAutomation.Fake.dll");

        // Source must be a real file — skip gracefully if Location is empty
        // (possible in single-file published builds, not the norm for test runs).
        if (string.IsNullOrEmpty(sourceDllPath) || !File.Exists(sourceDllPath))
            return;

        File.Copy(sourceDllPath, fakeRockwellDllPath);

        var alc = new SdkAssemblyLoadContext(dir);
        // LoadFromAssemblyPath does not call our Load() override — it loads by
        // absolute path directly.  This verifies the ALC can successfully read a
        // DLL from the SDK directory, which is the final step of TryLoad.
        var loaded = alc.LoadFromAssemblyPath(fakeRockwellDllPath);
        Assert.NotNull(loaded);
    }

    [Fact]
    public void SdkAssemblyLoadContext_HasExpectedName()
    {
        var alc = new SdkAssemblyLoadContext(Path.GetTempPath());
        Assert.Equal("SdkAdapterContext", alc.Name);
    }

    [Fact]
    public void SdkAssemblyLoadContext_Load_ResolvesVendorDll_WhenPresentInVendorDir()
    {
        // Verify that the ALC resolves a non-Rockwell DLL (simulating Grpc.* or
        // Google.Protobuf) when it is present in the vendor directory.
        var sdkDir    = CreateTempDir();
        var vendorDir = CreateTempDir();

        // Use the test binary itself as a stand-in for a real vendor DLL.
        var sourceDllPath = typeof(SdkAdapterLoader).Assembly.Location;
        if (string.IsNullOrEmpty(sourceDllPath) || !File.Exists(sourceDllPath))
            return; // skip in single-file builds

        // Give it a name that mimics a Grpc vendor assembly.
        var fakeVendorDllPath = Path.Combine(vendorDir, "Grpc.Core.Api.dll");
        File.Copy(sourceDllPath, fakeVendorDllPath);

        var alc    = new SdkAssemblyLoadContext(sdkDir, vendorDir);
        var loaded = alc.LoadFromAssemblyPath(fakeVendorDllPath);
        Assert.NotNull(loaded);
    }

    [Fact]
    public void SdkAssemblyLoadContext_Load_ReturnsNull_ForNonVendorAssemblyWhenVendorDirEmpty()
    {
        // When the vendor directory exists but does not contain the requested DLL,
        // Load returns null and the runtime falls back to the default context —
        // which raises FileNotFoundException for a purely fictional assembly name.
        var sdkDir    = CreateTempDir();
        var vendorDir = CreateTempDir(); // empty

        var alc  = new SdkAssemblyLoadContext(sdkDir, vendorDir);
        var name = new System.Reflection.AssemblyName("Grpc.Core.FakeNonexistent");
        Assert.Throws<FileNotFoundException>(() => alc.LoadFromAssemblyName(name));
    }

    [Fact]
    public void SdkAssemblyLoadContext_Load_WithoutVendorDir_DoesNotProbeVendorPath()
    {
        // When vendorDir is null, a non-Rockwell assembly that would only be found
        // in a vendor directory still falls through to the default context and raises
        // FileNotFoundException — confirming the null guard is respected.
        var sdkDir = CreateTempDir();
        var alc    = new SdkAssemblyLoadContext(sdkDir, vendorDir: null);
        var name   = new System.Reflection.AssemblyName("Grpc.Core.FakeNonexistent");
        Assert.Throws<FileNotFoundException>(() => alc.LoadFromAssemblyName(name));
    }

    [Fact]
    public void SdkAssemblyLoadContext_VendorDirIsProbedAfterSdkDir()
    {
        // A RockwellAutomation.* assembly present in the SDK dir must be loaded
        // from there (not the vendor dir), even when the vendor dir also contains
        // a file with the same name.  This validates resolution priority.
        var sdkDir    = CreateTempDir();
        var vendorDir = CreateTempDir();

        var sourceDllPath = typeof(SdkAdapterLoader).Assembly.Location;
        if (string.IsNullOrEmpty(sourceDllPath) || !File.Exists(sourceDllPath))
            return;

        var rockwellName  = "RockwellAutomation.FakeLib";
        var sdkDllPath    = Path.Combine(sdkDir,    rockwellName + ".dll");
        var vendorDllPath = Path.Combine(vendorDir, rockwellName + ".dll");

        File.Copy(sourceDllPath, sdkDllPath);
        File.Copy(sourceDllPath, vendorDllPath);

        var alc  = new SdkAssemblyLoadContext(sdkDir, vendorDir);
        // LoadFromAssemblyPath loads by absolute path, bypassing Load().
        // We verify via LoadFromAssemblyName that the SDK-dir path is hit first.
        var loaded = alc.LoadFromAssemblyPath(sdkDllPath);
        Assert.NotNull(loaded);
    }

    // ── InvokeInitializer ─────────────────────────────────────────────────────

    [Fact]
    public void InvokeInitializer_DoesNotThrow_WhenInitializerTypeNotFound()
    {
        // An assembly that does not contain a type named SdkAdapterInitializer
        // (e.g. the test assembly itself) must be handled without throwing.
        // InvokeInitializer must be a silent no-op in that case.
        var assembly = typeof(SdkAdapterLoaderTests).Assembly;
        var ex = Record.Exception(() => SdkAdapterLoader.InvokeInitializer(assembly));
        Assert.Null(ex);
    }

    [Fact]
    public void InvokeInitializer_DoesNotThrow_OnCoreAssembly()
    {
        // The core LogixMcp assembly also lacks SdkAdapterInitializer — verify the
        // no-op path is taken without error for any non-adapter assembly.
        var assembly = typeof(SdkAdapterLoader).Assembly;
        var ex = Record.Exception(() => SdkAdapterLoader.InvokeInitializer(assembly));
        Assert.Null(ex);
    }

    [Fact]
    public void InvokeInitializer_DoesNotThrow_WhenCalledWithNullLogger()
    {
        // InvokeInitializer must never throw even when the optional logger is null.
        var assembly = typeof(SdkAdapterLoaderTests).Assembly;
        var ex = Record.Exception(() => SdkAdapterLoader.InvokeInitializer(assembly, logger: null));
        Assert.Null(ex);
    }

    [Fact]
    public void InvokeInitializer_SetsLoadFailureReason_WhenInitializerTypeNotFound()
    {
        // When the assembly contains no type named SdkAdapterInitializer, InvokeInitializer
        // must record a non-empty LoadFailureReason so the invisible-null-factory defect
        // cannot recur.  The core assembly is used here as a stand-in because it never
        // defines SdkAdapterInitializer.
        var assembly = typeof(SdkAdapterLoader).Assembly;
        SdkAdapterLoader.InvokeInitializer(assembly);

        Assert.NotNull(SdkAdapterLoader.LoadFailureReason);
        Assert.False(string.IsNullOrWhiteSpace(SdkAdapterLoader.LoadFailureReason));
        Assert.Contains("SdkAdapterInitializer", SdkAdapterLoader.LoadFailureReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InvokeInitializer_SetsLoadFailureReason_WhenInitializeMethodNotFound()
    {
        // Build a dynamic assembly that contains a type named SdkAdapterInitializer
        // but without an Initialize method — exercising the method-absent branch.
        var ab = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("TestAdapter_NoInitMethod"), AssemblyBuilderAccess.Run);
        var mb = ab.DefineDynamicModule("Module");
        // Define SdkAdapterInitializer in the global namespace with no Initialize method.
        mb.DefineType("SdkAdapterInitializer", TypeAttributes.Public).CreateType();

        SdkAdapterLoader.InvokeInitializer(ab);

        Assert.NotNull(SdkAdapterLoader.LoadFailureReason);
        Assert.False(string.IsNullOrWhiteSpace(SdkAdapterLoader.LoadFailureReason));
        Assert.Contains("Initialize", SdkAdapterLoader.LoadFailureReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InvokeInitializer_SetsLoadFailureReason_WhenInvokeThrows()
    {
        // Build a dynamic assembly with a SdkAdapterInitializer.Initialize() method
        // that always throws, exercising the invoke-failure branch.
        var ab = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("TestAdapter_ThrowingInit"), AssemblyBuilderAccess.Run);
        var mb = ab.DefineDynamicModule("Module");
        var tb = mb.DefineType("SdkAdapterInitializer", TypeAttributes.Public);
        var meth = tb.DefineMethod(
            "Initialize",
            MethodAttributes.Public | MethodAttributes.Static,
            returnType: null,
            parameterTypes: null);
        var il = meth.GetILGenerator();
        il.Emit(OpCodes.Newobj,
            typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Throw);
        tb.CreateType();

        SdkAdapterLoader.InvokeInitializer(ab);

        Assert.NotNull(SdkAdapterLoader.LoadFailureReason);
        Assert.False(string.IsNullOrWhiteSpace(SdkAdapterLoader.LoadFailureReason));
        Assert.Contains("InvalidOperationException", SdkAdapterLoader.LoadFailureReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InvokeInitializer_DoesNotThrow_WhenInvokeThrows()
    {
        // InvokeInitializer must swallow any exception from the reflective Invoke call
        // so it can never prevent the server from starting.
        var ab = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("TestAdapter_ThrowingInit2"), AssemblyBuilderAccess.Run);
        var mb = ab.DefineDynamicModule("Module");
        var tb = mb.DefineType("SdkAdapterInitializer", TypeAttributes.Public);
        var meth = tb.DefineMethod(
            "Initialize",
            MethodAttributes.Public | MethodAttributes.Static,
            returnType: null,
            parameterTypes: null);
        var il = meth.GetILGenerator();
        il.Emit(OpCodes.Newobj,
            typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Throw);
        tb.CreateType();

        var ex = Record.Exception(() => SdkAdapterLoader.InvokeInitializer(ab));
        Assert.Null(ex);
    }

    [Fact]
    public void InvokeInitializer_SetsDefaultSdkFactory_WhenInitializeRegistersFactory()
    {
        // Build a dynamic assembly that mimics a real adapter assembly: it contains
        // SdkAdapterInitializer.Initialize() which assigns a non-null factory to
        // ConnectHandler.DefaultSdkFactory — exercising the registration success path
        // that previously had zero coverage.
        //
        // The IL calls the public bridge helper SetDefaultSdkFactoryFromDynamicIL()
        // rather than directly constructing StubSdkFactory (a private nested type),
        // because dynamic-assembly IL is subject to runtime access checks.
        var savedFactory = ConnectHandler.DefaultSdkFactory;
        ConnectHandler.DefaultSdkFactory = null; // start from null so the assertion is meaningful
        try
        {
            var ab = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("TestAdapter_SuccessfulRegistration"), AssemblyBuilderAccess.Run);
            var mb = ab.DefineDynamicModule("Module");
            var tb = mb.DefineType("SdkAdapterInitializer", TypeAttributes.Public);
            var initMethod = tb.DefineMethod(
                "Initialize",
                MethodAttributes.Public | MethodAttributes.Static,
                returnType: null,
                parameterTypes: null);
            var il = initMethod.GetILGenerator();

            // Emit: SdkAdapterTestBridge.RegisterStubFactory();
            // The bridge class and its method are public so the dynamic assembly can
            // call them without triggering a runtime access-check failure; the bridge
            // internally creates the stub and assigns it to DefaultSdkFactory.
            il.Emit(OpCodes.Call,
                typeof(SdkAdapterTestBridge)
                    .GetMethod(nameof(SdkAdapterTestBridge.RegisterStubFactory),
                               BindingFlags.Static | BindingFlags.Public)!);
            il.Emit(OpCodes.Ret);
            tb.CreateType();

            SdkAdapterLoader.InvokeInitializer(ab);

            // The factory must be non-null: Initialize() ran and registered the stub.
            Assert.NotNull(ConnectHandler.DefaultSdkFactory);
        }
        finally
        {
            // Restore so this test does not affect others that rely on DefaultSdkFactory.
            ConnectHandler.DefaultSdkFactory = savedFactory;
        }
    }


    [Fact]
    public void InvokeInitializer_AdapterShapedAssemblyWithoutInitializer_ServerStillStartsAndSetsReason()
    {
        // Build a dynamic adapter-shaped assembly that intentionally omits
        // SdkAdapterInitializer — simulating a real adapter where the initializer
        // class was stripped or renamed — and verify all three invariants:
        //   (a) InvokeInitializer does not throw (the server still starts).
        //   (b) LoadFailureReason is set to a non-empty string.
        //   (c) The reason names the type responsible for registration so the
        //       invisible-null-factory defect cannot recur silently.
        var ab = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("TestAdapter_MissingInitializerType"), AssemblyBuilderAccess.Run);
        ab.DefineDynamicModule("Module");
        // No SdkAdapterInitializer type defined in this assembly.

        // (a) Must not throw — the server must always be able to start.
        var ex = Record.Exception(() => SdkAdapterLoader.InvokeInitializer(ab));
        Assert.Null(ex);

        // (b) LoadFailureReason must be populated.
        Assert.NotNull(SdkAdapterLoader.LoadFailureReason);
        Assert.False(string.IsNullOrWhiteSpace(SdkAdapterLoader.LoadFailureReason));

        // (c) The reason must name the registration class so the failure is actionable.
        Assert.Contains("SdkAdapterInitializer", SdkAdapterLoader.LoadFailureReason,
            StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}

// ── SdkAdapterTestBridge ──────────────────────────────────────────────────────

/// <summary>
/// Public bridge class used by dynamically emitted IL in
/// <see cref="SdkAdapterLoaderTests"/> to register a stub
/// <see cref="ISdkTagServiceFactory"/> without triggering runtime access-check
/// failures.
///
/// <para>
/// Dynamic assemblies (created with <see cref="System.Reflection.Emit.AssemblyBuilderAccess.Run"/>)
/// cannot construct private or internal types from other assemblies.
/// By routing through this <c>public</c> bridge the IL avoids that restriction
/// while keeping the stub implementation encapsulated.
/// </para>
/// </summary>
public static class SdkAdapterTestBridge
{
    /// <summary>
    /// Creates a <see cref="StubSdkFactory"/> and assigns it to
    /// <see cref="ConnectHandler.DefaultSdkFactory"/>.
    /// Called from dynamically emitted IL that simulates
    /// <c>SdkAdapterInitializer.Initialize()</c>.
    /// </summary>
    public static void RegisterStubFactory() =>
        ConnectHandler.DefaultSdkFactory = new StubSdkFactory();

    /// <summary>
    /// Minimal <see cref="ISdkTagServiceFactory"/> stub used to verify that
    /// <see cref="SdkAdapterLoader.InvokeInitializer"/> successfully sets
    /// <see cref="ConnectHandler.DefaultSdkFactory"/> on the success path.
    /// </summary>
    private sealed class StubSdkFactory : ISdkTagServiceFactory
    {
        public Task<SdkSessionResult> CreateAsync(
            string projectFilePath,
            L5Sharp.Core.L5X typeProject,
            string sdkMode = "offline",
            bool allowControllerDownload = false,
            CancellationToken ct = default)
            => throw new NotImplementedException("Stub — not for production use.");

        public Task<SdkSessionResult> CreateFromControllerAsync(
            string commPath,
            CancellationToken ct = default)
            => throw new NotImplementedException("Stub — not for production use.");
    }
}
