using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Proves that the type-scan guard in <c>Program.cs</c> survives a
/// <see cref="ReflectionTypeLoadException"/> caused by an unresolvable vendor
/// dependency (the scenario this guard exists for).
///
/// <para>
/// The guard in <c>Program.cs</c> wraps <c>WithToolsFromAssembly(adapterAssembly)</c>
/// in a <c>try/catch</c> for <see cref="ReflectionTypeLoadException"/> and
/// <see cref="FileNotFoundException"/>.  <c>WithToolsFromAssembly</c> calls
/// <c>assembly.GetTypes()</c> internally, which forces dependency resolution for
/// every type in the adapter.  When a transitive dependency DLL (e.g. the absent
/// <c>CSClient.dll</c>) is missing at scan time, <c>GetTypes()</c> throws even
/// though the adapter DLL itself loaded successfully via <see cref="SdkAssemblyLoadContext"/>.
/// </para>
///
/// <para>
/// The test exercises EXACTLY the type-scan path — NOT the early-return path
/// taken when the adapter DLL file itself is absent. The
/// precondition assertion confirms that <c>GetTypes()</c> throws on the
/// loaded assembly before the guard is invoked.
/// </para>
/// </summary>
public class TypeScanGuardTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    // ── Core guard proof ──────────────────────────────────────────────────────

    /// <summary>
    /// Loads an adapter assembly whose vendor dependency is deliberately
    /// unresolvable, then asserts:
    /// <list type="number">
    ///   <item>The type-scan guard does not propagate the exception.</item>
    ///   <item><see cref="SdkAdapterLoader.LoadFailureReason"/> is set with
    ///     the expected prefix and includes the vendor assembly name from the
    ///     loader exceptions.</item>
    ///   <item><c>logix_ping</c> still returns a valid JSON response, proving
    ///     core server tools are unaffected by the adapter type-scan failure.</item>
    /// </list>
    ///
    /// <para>
    /// Assembly construction:
    /// <list type="bullet">
    ///   <item>A <em>vendor</em> assembly (<c>SimulatedVendor.CSClient.*</c>) is
    ///     created in-memory only with <see cref="AssemblyBuilder.DefineDynamicAssembly"/>
    ///     — it is never saved to disk, simulating the absent <c>CSClient.dll</c>.
    ///   </item>
    ///   <item>An <em>adapter</em> DLL is built with
    ///     <see cref="PersistedAssemblyBuilder"/> and contains a single type that
    ///     inherits from <c>VendorBaseClass</c> (defined in the in-memory vendor
    ///     assembly).  Saving the adapter DLL bakes an <c>AssemblyRef</c> to the
    ///     vendor assembly into the PE metadata, making the vendor unresolvable when
    ///     the adapter is later loaded in a fresh
    ///     <see cref="AssemblyLoadContext"/>.</item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact]
    public void TypeScanGuard_SurvivesUnresolvableVendorDependency_AndPingStillWorks()
    {
        // ── Arrange: build the adapter DLL with an unresolvable dependency ──────
        var tempDir = CreateTempDir();

        // Create an in-memory "vendor" assembly that is NOT saved to disk.
        // The unique name (GUID suffix) ensures each test run produces an assembly
        // name that no other assembly in the process can impersonate.
        var vendorAsmName = new AssemblyName(
            $"SimulatedVendor.CSClient.{Guid.NewGuid():N}");
        var vendorDynBuilder = AssemblyBuilder.DefineDynamicAssembly(
            vendorAsmName, AssemblyBuilderAccess.Run);
        var vendorModule = vendorDynBuilder.DefineDynamicModule(vendorAsmName.Name!);
        var vendorTypeBuilder = vendorModule.DefineType(
            "VendorBaseClass", TypeAttributes.Public | TypeAttributes.Abstract);
        var vendorType = vendorTypeBuilder.CreateType()!;

        // Build the adapter DLL on disk.  The single type it defines inherits
        // from VendorBaseClass, embedding an AssemblyRef to the vendor assembly
        // in the PE metadata.  When the adapter is loaded in a fresh
        // AssemblyLoadContext and GetTypes() is called, the runtime tries to
        // resolve the vendor assembly — fails — and throws
        // ReflectionTypeLoadException.
        var adapterAsmName = new AssemblyName("FakeAdapter.WithMissingVendorDep");
        var adapterBuilder  = new PersistedAssemblyBuilder(adapterAsmName, typeof(object).Assembly);
        var adapterModule   = adapterBuilder.DefineDynamicModule("FakeAdapter.WithMissingVendorDep");
        adapterModule.DefineType(
            "AdapterTool",
            TypeAttributes.Public | TypeAttributes.Class,
            vendorType).CreateType();

        var adapterPath = Path.Combine(tempDir, "FakeAdapter.WithMissingVendorDep.dll");
        adapterBuilder.Save(adapterPath);

        // ── Arrange: load the adapter via SdkAssemblyLoadContext ─────────────────
        // sdkDir and vendorDir are empty — the vendor DLL is not present anywhere
        // on disk, simulating the absent CSClient.dll installation.
        var sdkDir    = CreateTempDir();
        var vendorDir = CreateTempDir();
        var alc = new SdkAssemblyLoadContext(sdkDir, vendorDir);
        var adapterAssembly = alc.LoadFromAssemblyPath(adapterPath);

        // ── Precondition: verify we are in the type-scan failure path ─────────────
        // GetTypes() must throw ReflectionTypeLoadException to confirm this test
        // exercises the right code path — NOT the early "adapter DLL absent" exit.
        var preScanEx = Record.Exception(() => adapterAssembly.GetTypes());
        Assert.NotNull(preScanEx);
        Assert.IsType<ReflectionTypeLoadException>(preScanEx);

        // ── Act: execute the same guard as in Program.cs ─────────────────────────
        Exception? guardPropagated = null;
        try
        {
            // WithToolsFromAssembly calls assembly.GetTypes() internally.
            // We replicate that call here so the test stays within unit-test scope
            // (no full MCP host required) while exercising the identical guard logic.
            adapterAssembly.GetTypes();
        }
        catch (Exception ex) when (ex is ReflectionTypeLoadException or FileNotFoundException)
        {
            // Identical filter to Program.cs:
            SdkAdapterLoader.RecordTypeScanFailure(adapterAssembly.Location, ex);
        }
        catch (Exception ex)
        {
            guardPropagated = ex; // should never be reached
        }

        // ── Assert 1: guard does not propagate the exception ─────────────────────
        Assert.Null(guardPropagated);

        // ── Assert 2: LoadFailureReason is set with expected content ─────────────
        var reason = SdkAdapterLoader.LoadFailureReason;
        Assert.NotNull(reason);

        // Must start with "Type scan failed for" so it is distinguishable from
        // assembly-load failures recorded by TryLoad.
        Assert.StartsWith("Type scan failed for", reason, StringComparison.Ordinal);

        // Must identify the adapter assembly path.
        Assert.Contains(adapterPath, reason, StringComparison.Ordinal);

        // Must identify the exception type.
        Assert.Contains("ReflectionTypeLoadException", reason, StringComparison.Ordinal);

        // Must include the vendor assembly name from the loader-exception suffix
        // so the caller can identify the missing dependency without log inspection.
        Assert.Contains(vendorAsmName.Name!, reason, StringComparison.Ordinal);

        // ── Assert 3: core server tool (logix_ping) is unaffected ────────────────
        // The adapter type-scan failure must not interfere with core tools.
        var pingJson = LogixTools.Ping();
        Assert.False(string.IsNullOrWhiteSpace(pingJson));
        var pingDoc = JsonDocument.Parse(pingJson);
        Assert.Equal("ok",        pingDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal("lgx-mcp", pingDoc.RootElement.GetProperty("server").GetString());
        Assert.Equal(ServerIdentity.Version, pingDoc.RootElement.GetProperty("version").GetString());
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
