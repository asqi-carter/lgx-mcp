using System.Reflection;

namespace LogixMcp.Tests;

/// <summary>
/// Regression gate: asserts that the core <c>LogixMcp</c> assembly carries zero
/// compile-time references to any <c>RockwellAutomation.*</c> assembly.
///
/// <para>
/// This test runs on Linux (where no Rockwell packages are resolvable) and
/// therefore acts as a permanent CI gate.  Any re-introduction of a direct
/// <c>PackageReference</c>, <c>using</c> directive, or type reference that
/// causes the compiler to embed a vendor assembly name in the core assembly's
/// metadata will be caught here before it reaches Windows-only environments.
/// </para>
///
/// <para>
/// <see cref="Assembly.GetReferencedAssemblies"/> returns only assemblies whose
/// types are actually consumed by the module — not merely declared as
/// <c>PackageReference</c> items.  The test therefore also catches accidental
/// type-level coupling even when the package is not referenced in the project
/// file.
/// </para>
/// </summary>
public class CoreAssemblyNoRockwellTest
{
    /// <summary>
    /// The core assembly under test — resolved via a type that lives in it so
    /// that no hard-coded path is required.
    /// </summary>
    private static readonly Assembly CoreAssembly = typeof(ConnectHandler).Assembly;

    /// <summary>
    /// Asserts that the core <c>LogixMcp</c> assembly does not reference any
    /// assembly whose name begins with <c>"RockwellAutomation"</c>.
    ///
    /// <para>
    /// Failure indicates that either a <c>PackageReference</c> to a Rockwell
    /// vendor package has been re-added to <c>LogixMcp.csproj</c>, or that
    /// source code in <c>src/LogixMcp</c> directly references a Rockwell type.
    /// Both must be removed: vendor SDK types belong exclusively in
    /// <c>LogixMcp.SdkAdapter</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void CoreAssembly_ReferencesNoRockwellAssemblies()
    {
        var rockwellRefs = CoreAssembly
            .GetReferencedAssemblies()
            .Where(n => n.Name?.StartsWith(
                "RockwellAutomation",
                StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        Assert.True(
            rockwellRefs.Count == 0,
            $"Core assembly '{CoreAssembly.GetName().Name}' must not reference any " +
            $"RockwellAutomation assembly, but the following were found: " +
            $"{string.Join(", ", rockwellRefs.Select(r => r.FullName))}. " +
            $"Remove any RockwellAutomation PackageReference from LogixMcp.csproj " +
            $"and move vendor-type usage exclusively into LogixMcp.SdkAdapter.");
    }
}
