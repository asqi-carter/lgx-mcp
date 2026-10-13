namespace LogixMcp.Tests;

/// <summary>
/// Reachability regression guard for all <see cref="ITagService"/> implementations.
///
/// Uses reflection to enumerate every concrete (non-abstract, non-interface) class in
/// the LogixMcp assembly that implements <see cref="ITagService"/>, then asserts that
/// each one is reachable from <see cref="ConnectHandler"/> — i.e. that at least one
/// combination of <c>backend</c> parameter value or <c>useVirtual=true</c> causes
/// <see cref="ConnectHandler.ExecuteAsync"/> to instantiate that type.
///
/// <para>
/// The guard protects against "orphan" implementations: concrete classes that satisfy
/// the <see cref="ITagService"/> contract but are never selected by any
/// <see cref="ConnectHandler"/> code path.  Such dead implementations accumulate
/// untested code that can diverge silently from the interface contract.
/// </para>
///
/// <para>
/// To add a new backend:
/// <list type="number">
///   <item>Create the concrete <see cref="ITagService"/> class.</item>
///   <item>Wire it into <see cref="ConnectHandler.ExecuteAsync"/> (add a new
///     <c>backend</c> value or <c>useVirtual</c> branch).</item>
///   <item>Add the new type to <see cref="ConnectHandlerReachableTypes"/> in this
///     file.</item>
/// </list>
/// Skipping step 3 causes <see cref="AllConcreteImplementations_AreReachableFromConnectHandler"/>
/// to fail — that failure is intentional and expected.
/// </para>
/// </summary>
public class TagServiceReachabilityTests
{
    // ── Registry of ConnectHandler-reachable ITagService types ────────────────
    //
    // This is the authoritative list of every concrete ITagService that
    // ConnectHandler.ExecuteAsync can produce, one entry per code path.
    //
    // MAINTENANCE NOTE: When you add a new ITagService implementation AND wire it
    // into ConnectHandler, add its Type here.  If you add an implementation without
    // updating this set, AllConcreteImplementations_AreReachableFromConnectHandler
    // will fail — that is intentional.

    private static readonly IReadOnlySet<Type> ConnectHandlerReachableTypes =
        new HashSet<Type>
        {
            // virtual=true path — in-memory store seeded from the open project's tags.
            typeof(VirtualTagService),

            // backend="cip" path — live EtherNet/IP connection via libplctag.
            typeof(LibPlcTagService),

            // backend="l5x" path — fully offline, backed by the open L5X project.
            typeof(L5XOfflineTagService),

            // backend="sdk" path — TypedSdkTagService lives in LogixMcp.SdkAdapter.dll (not
            // scanned here because this test is scoped to typeof(ITagService).Assembly, i.e.
            // the core LogixMcp assembly).  The adapter registers itself at runtime via
            // ConnectHandler.DefaultSdkFactory; no entry in this registry is needed.

            // Write-gate decorator applied in all project-loaded paths (wraps the inner service).
            typeof(GatedTagService),

            // backend="sdk" online upload path — routes tag reads and writes over CIP or the SDK
            // (LGX_TAG_ROUTE); wraps the SDK service beneath the write gate.
            typeof(RoutedTagService),
        };

    // ── Main regression guard ─────────────────────────────────────────────────

    /// <summary>
    /// Scans the LogixMcp assembly for every concrete <see cref="ITagService"/>
    /// implementation and asserts that each one is reachable from
    /// <see cref="ConnectHandler"/>.
    ///
    /// <para>
    /// The test fails immediately when reflection finds a type that is not listed in
    /// <see cref="ConnectHandlerReachableTypes"/>, reporting the exact class names of
    /// all unreachable implementations so the developer knows what to fix.
    /// </para>
    /// </summary>
    [Fact]
    public void AllConcreteImplementations_AreReachableFromConnectHandler()
    {
        // Locate the assembly that contains ITagService (LogixMcp.dll).
        var assembly = typeof(ITagService).Assembly;

        // Enumerate every concrete class in that assembly that satisfies ITagService.
        var concreteImplementations = assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .Where(t => typeof(ITagService).IsAssignableFrom(t))
            .ToList();

        // Every discovered type must appear in the reachability registry.
        var unreachable = concreteImplementations
            .Where(t => !ConnectHandlerReachableTypes.Contains(t))
            .ToList();

        Assert.True(unreachable.Count == 0,
            "The following ITagService implementation(s) exist in the LogixMcp assembly " +
            "but are NOT reachable from ConnectHandler — no backend value or virtual=true " +
            "causes ConnectHandler.ExecuteAsync to produce them:\n" +
            string.Join("\n", unreachable.Select(t => $"  • {t.FullName}")) +
            "\n\nFix options:\n" +
            "  (a) Wire the implementation into ConnectHandler.ExecuteAsync and add its\n" +
            "      Type to ConnectHandlerReachableTypes in TagServiceReachabilityTests.cs.\n" +
            "  (b) Remove the unused implementation from the assembly.");
    }

    // ── Sanity checks for the registry itself ─────────────────────────────────

    /// <summary>
    /// Ensures every entry in <see cref="ConnectHandlerReachableTypes"/> still
    /// implements <see cref="ITagService"/>.  Guards against stale registry entries
    /// left behind after a rename or interface change.
    /// </summary>
    [Fact]
    public void ReachabilityRegistry_AllEntriesImplementITagService()
    {
        foreach (var type in ConnectHandlerReachableTypes)
        {
            Assert.True(typeof(ITagService).IsAssignableFrom(type),
                $"Registry entry '{type.FullName}' does not implement ITagService. " +
                $"Remove it from ConnectHandlerReachableTypes in TagServiceReachabilityTests.cs.");
        }
    }

    /// <summary>
    /// Ensures every entry in <see cref="ConnectHandlerReachableTypes"/> is a
    /// concrete, instantiable class — not abstract or an interface —  because
    /// <see cref="ConnectHandler"/> always calls <c>new</c> directly.
    /// </summary>
    [Fact]
    public void ReachabilityRegistry_AllEntriesAreConcreteClasses()
    {
        foreach (var type in ConnectHandlerReachableTypes)
        {
            Assert.False(type.IsAbstract,
                $"Registry entry '{type.FullName}' is abstract — " +
                $"ConnectHandler cannot instantiate it directly.");

            Assert.False(type.IsInterface,
                $"Registry entry '{type.FullName}' is an interface — " +
                $"ConnectHandler cannot instantiate it directly.");
        }
    }

    /// <summary>
    /// Verifies that <see cref="ConnectHandlerReachableTypes"/> is in sync with the
    /// LogixMcp assembly in both directions: every concrete implementation is in the
    /// registry, and every registry entry still exists in the assembly.
    ///
    /// <para>
    /// A "not in registry" miss is also caught by
    /// <see cref="AllConcreteImplementations_AreReachableFromConnectHandler"/>;
    /// this test adds the reverse check for stale registry entries that remain after
    /// an implementation is removed from the assembly.
    /// </para>
    /// </summary>
    [Fact]
    public void ReachabilityRegistry_IsBidirectionallySyncedWithAssembly()
    {
        var assembly = typeof(ITagService).Assembly;

        var assemblyTypes = assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .Where(t => typeof(ITagService).IsAssignableFrom(t))
            .ToHashSet();

        // Forward: every assembly type must be in the registry.
        var notInRegistry = assemblyTypes
            .Where(t => !ConnectHandlerReachableTypes.Contains(t))
            .ToList();

        // Reverse: every registry entry must still exist in the assembly.
        var staleInRegistry = ConnectHandlerReachableTypes
            .Where(t => !assemblyTypes.Contains(t))
            .ToList();

        var diagnostics = new List<string>();

        if (notInRegistry.Count > 0)
            diagnostics.Add(
                "Missing from registry — add to ConnectHandler and ConnectHandlerReachableTypes:\n" +
                string.Join("\n", notInRegistry.Select(t => $"  • {t.FullName}")));

        if (staleInRegistry.Count > 0)
            diagnostics.Add(
                "Stale registry entries — remove from ConnectHandlerReachableTypes:\n" +
                string.Join("\n", staleInRegistry.Select(t => $"  • {t.FullName}")));

        Assert.True(diagnostics.Count == 0,
            "ConnectHandlerReachableTypes is out of sync with the LogixMcp assembly:\n\n" +
            string.Join("\n\n", diagnostics));
    }
}
