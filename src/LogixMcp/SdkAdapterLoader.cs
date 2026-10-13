using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

// ── SdkAdapterLoader ──────────────────────────────────────────────────────────

/// <summary>
/// Locates and loads the <c>LogixMcp.SdkAdapter</c> assembly at runtime using a
/// custom <see cref="AssemblyLoadContext"/> that resolves
/// <c>RockwellAutomation.*</c> dependencies from the probed SDK directory.
///
/// <para>
/// This class implements the late-bind mechanism that allows a single
/// <c>LogixMcp</c> binary to ship without Rockwell vendor DLLs while still
/// activating the <c>sdk</c> backend on machines where the Rockwell SDK is
/// installed.  The adapter assembly (<c>LogixMcp.SdkAdapter.dll</c>) is
/// expected in the <c>sdk/</c> subdirectory of the host executable's base
/// directory; only the Rockwell SDK dependencies are resolved at runtime from
/// the probed location.
/// </para>
///
/// <para>
/// Probe order (first hit wins):
/// <list type="number">
///   <item>The <c>LOGIX_SDK_PATH</c> environment variable.</item>
///   <item><c>sdk/vendor/</c> under <see cref="AppContext.BaseDirectory"/> — the
///     app-local redistributable-closure payload directory populated by the
///     <c>sdk/vendor</c> publish target.</item>
///   <item>The standard Studio 5000 Logix Designer SDK install path
///     <c>C:\Program Files (x86)\Rockwell Software\Studio 5000\Logix Designer SDK\</c>.</item>
///   <item><see cref="AppContext.BaseDirectory"/> — covers SDK builds where the
///     Rockwell DLLs are copied alongside the executable by the
///     <c>CopyToMainOutput</c> build target in <c>LogixMcp.SdkAdapter.csproj</c>.</item>
/// </list>
/// A directory qualifies when it exists and contains the file
/// <c>RockwellAutomation.LogixDesigner.LogixProject.CSClient.dll</c>
/// (see <see cref="CsClientDllName"/>).
/// </para>
/// </summary>
public static class SdkAdapterLoader
{
    internal const string AdapterDllName = "LogixMcp.SdkAdapter.dll";

    /// <summary>
    /// The exact filename of the Rockwell client assembly whose presence is
    /// required for a directory to qualify as a Rockwell SDK directory.
    ///
    /// <para>
    /// This specific assembly — rather than any <c>RockwellAutomation*.dll</c> file
    /// — is the required entry point for the Logix Designer SDK runtime.  Other
    /// Rockwell assemblies (e.g. the gRPC server assembly
    /// <c>RockwellAutomation.LogixDesigner.LogixProjectServices.dll</c>) may be
    /// present without the CSClient assembly and must not qualify a directory.
    /// </para>
    /// </summary>
    internal const string CsClientDllName =
        "RockwellAutomation.LogixDesigner.LogixProject.CSClient.dll";

    /// <summary>
    /// The subdirectory (relative to <see cref="AppContext.BaseDirectory"/>) where the
    /// <c>LogixMcp.SdkAdapter.dll</c> is published.  Isolating the adapter under
    /// <c>sdk/</c> prevents the MCP host type-scanner from discovering it via ambient
    /// directory enumeration of the executable's base directory.
    /// </summary>
    internal const string AdapterSubdirectory = "sdk";

    /// <summary>
    /// The subdirectory (relative to the adapter DLL's own directory) where vendor
    /// assemblies that are transitive dependencies of the Rockwell SDK are published.
    /// This covers <c>Grpc.*</c>, <c>Google.Protobuf</c>, and any other non-Rockwell
    /// assemblies that the SDK brings in as transitive dependencies.
    ///
    /// <para>
    /// The path is derived from the adapter DLL's own location (see
    /// <see cref="GetVendorDir"/>) so that the resolver is self-consistent: wherever
    /// the adapter DLL is placed, the vendor directory is always adjacent to it.
    /// </para>
    /// </summary>
    internal const string VendorSubdirectory = "vendor";

    /// <summary>
    /// The standard Studio 5000 Logix Designer SDK install path on Windows.
    /// </summary>
    internal static readonly string Studio5000SdkPath =
        @"C:\Program Files (x86)\Rockwell Software\Studio 5000\Logix Designer SDK\";

    /// <summary>
    /// The standard location of the Logix Designer SDK NuGet package directory on
    /// Windows.  When a directory contains other Rockwell assemblies but is missing
    /// <see cref="CsClientDllName"/>, this path is surfaced in the error message so
    /// users know where to find the client package that ships only as a NuGet package.
    /// </summary>
    internal const string NuPkgLocation =
        @"%PUBLIC%\Documents\Studio 5000\Logix Designer SDK\dotnet\";

    /// <summary>
    /// When <see cref="TryLoad"/> encounters an exception during the adapter load
    /// sequence (ALC construction, assembly load, or module-initializer activation),
    /// this property stores a human-readable description of the failure so it can be
    /// surfaced to callers via <see cref="ConnectHandler"/> as part of a
    /// <c>ConnectSdkNotAvailableError</c> response.
    ///
    /// <para>
    /// Returns <c>null</c> when <see cref="TryLoad"/> has not yet been called, the
    /// adapter DLL was simply absent, or no Rockwell SDK directory was found — in
    /// those cases the probe results alone explain the unavailability.  Only set when
    /// an unexpected exception is caught during the actual load attempt.
    /// </para>
    /// </summary>
    public static string? LoadFailureReason { get; private set; }

    /// <summary>
    /// Records a type-scan failure into <see cref="LoadFailureReason"/> so it can be
    /// surfaced to callers via <see cref="ConnectHandler"/> as part of a
    /// <c>ConnectSdkNotAvailableError</c> response.
    ///
    /// <para>
    /// Called from <c>Program.cs</c> when
    /// <c>WithToolsFromAssembly(adapterAssembly)</c> throws a
    /// <see cref="ReflectionTypeLoadException"/> or <see cref="FileNotFoundException"/>
    /// during type discovery.  This can happen when the adapter assembly itself loads
    /// without error but a transitive dependency is missing at the moment the MCP
    /// framework enumerates the assembly's types: the probe passes (a qualifying SDK
    /// directory is found), <see cref="TryLoad"/> returns a non-null
    /// <see cref="Assembly"/>, and the failure is only revealed by the subsequent
    /// <c>GetTypes()</c> call inside the framework.
    /// </para>
    ///
    /// <para>
    /// When <paramref name="ex"/> is a <see cref="ReflectionTypeLoadException"/> and
    /// its <see cref="ReflectionTypeLoadException.LoaderExceptions"/> array is
    /// non-empty, up to the first three loader-exception messages are appended to
    /// <see cref="LoadFailureReason"/> so the caller can identify the specific missing
    /// dependency without consulting the server log.
    /// </para>
    /// </summary>
    /// <param name="assemblyLocation">
    /// Absolute file path of the adapter assembly that was being scanned (typically
    /// <see cref="Assembly.Location"/>).
    /// </param>
    /// <param name="ex">
    /// The exception thrown during type-scan.  Expected to be a
    /// <see cref="ReflectionTypeLoadException"/> or <see cref="FileNotFoundException"/>,
    /// but the method accepts any <see cref="Exception"/> to keep the call site simple.
    /// </param>
    internal static void RecordTypeScanFailure(string assemblyLocation, Exception ex)
    {
        var detail = string.Empty;
        if (ex is ReflectionTypeLoadException rtle
            && rtle.LoaderExceptions is { Length: > 0 } loaderExs)
        {
            var messages = loaderExs
                .Where(e => e is not null)
                .Select(e => e!.Message)
                .Take(3);
            detail = "; loader exceptions: " + string.Join("; ", messages);
        }

        LoadFailureReason =
            $"Type scan failed for '{assemblyLocation}': {ex.GetType().Name}: {ex.Message}{detail}";
    }

    /// <summary>
    /// Attempts to locate and load the <c>LogixMcp.SdkAdapter</c> assembly.
    ///
    /// <para>
    /// When the adapter DLL is found in the <c>sdk/</c> subdirectory of
    /// <see cref="AppContext.BaseDirectory"/> and a qualifying Rockwell SDK directory is
    /// discovered by the probe, <see cref="TryLoad"/> constructs a
    /// <see cref="SdkAssemblyLoadContext"/> for that directory and loads the adapter
    /// through it.  The adapter's
    /// <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/> then
    /// fires automatically, registering <c>SdkConnectFactory</c> as
    /// <see cref="ConnectHandler.DefaultSdkFactory"/> before the first tool call is
    /// dispatched.
    /// </para>
    ///
    /// <para>
    /// Returns <c>null</c> — without throwing — when either the adapter DLL is absent
    /// from <c>AppContext.BaseDirectory/sdk/</c> or no qualifying Rockwell SDK directory
    /// is found during the probe.  In that case the <c>sdk</c> backend remains
    /// unavailable and <see cref="ConnectHandler"/> surfaces a structured
    /// <c>ConnectSdkNotAvailableError</c> to the caller.
    /// </para>
    /// </summary>
    /// <param name="logger">
    /// Optional logger for diagnostic messages.  Pass <c>null</c> to suppress
    /// all logging.
    /// </param>
    /// <returns>
    /// The loaded <see cref="Assembly"/> on success; <c>null</c> when the adapter
    /// DLL is absent or no Rockwell SDK directory is found.
    /// </returns>
    public static Assembly? TryLoad(ILogger? logger = null)
    {
        var adapterPath = Path.Combine(AppContext.BaseDirectory, AdapterSubdirectory, AdapterDllName);
        if (!File.Exists(adapterPath))
        {
            logger?.LogDebug(
                "SDK adapter DLL not found at '{AdapterPath}'; SDK backend unavailable.",
                adapterPath);
            return null;
        }

        var sdkDir = ProbeForSdkDirectory();
        if (sdkDir is null)
        {
            logger?.LogDebug(
                "No Rockwell SDK directory found during probe; SDK backend unavailable.");
            return null;
        }

        logger?.LogDebug(
            "Rockwell SDK directory found at '{SdkDir}'; loading adapter from '{AdapterPath}'.",
            sdkDir, adapterPath);

        // Wrap the entire ALC construction + assembly load + module-initializer
        // activation in a single catch-all so that any failure — corrupt assembly,
        // version mismatch, unresolvable dependency, or a throwing module initializer
        // — is recorded rather than propagated.  The server must always start
        // successfully; the failure reason is surfaced only when a caller requests
        // logix_connect backend="sdk".
        try
        {
            // Derive the vendor directory from the adapter's own location before
            // constructing the ALC.  The resolver must be registered before the
            // first LoadFromAssemblyPath call so that every assembly the adapter
            // imports during module initialisation is already covered.
            var vendorDir = GetVendorDir(adapterPath);
            var alc = new SdkAssemblyLoadContext(sdkDir, vendorDir);
            var assembly = alc.LoadFromAssemblyPath(adapterPath);

            // Invoke SdkAdapterInitializer.Initialize reflectively so that the
            // factory registration fires even in hosts that do not run module
            // initializers automatically.  The [ModuleInitializer] attribute is
            // kept on the method — it is harmless when both paths execute —
            // because the assignment is idempotent.
            InvokeInitializer(assembly, logger);

            return assembly;
        }
        catch (Exception ex)
        {
            LoadFailureReason =
                $"Assembly load failed for '{adapterPath}': {ex.GetType().Name}: {ex.Message}";
            logger?.LogDebug(ex,
                "Failed to load SDK adapter from '{AdapterPath}'; SDK backend unavailable.",
                adapterPath);
            return null;
        }
    }

    /// <summary>
    /// Renders probe results as one human-readable line per probed directory, numbered in probe
    /// order: <c>1. &lt;path&gt;: &lt;why it qualified or was rejected&gt;</c>. Shared by the
    /// <c>logix_connect</c> SDK-not-available error and the <c>logix_doctor</c> sdkInstalled check.
    /// </summary>
    /// <param name="probeResults">Entries from <see cref="CollectProbeResults"/>.</param>
    /// <returns>The numbered lines, in probe order.</returns>
    public static IReadOnlyList<string> DescribeProbeResults(IReadOnlyList<SdkProbeEntry> probeResults) =>
        probeResults.Select((e, i) =>
        {
            var reason = e.Status switch
            {
                "not_found"        => "directory not found",
                "no_rockwell_dlls" => "directory exists but contains no RockwellAutomation*.dll files",
                "client_dll_missing" =>
                    $"directory has Rockwell DLLs but is missing {CsClientDllName} " +
                    $"(this assembly ships as a NuGet package; " +
                    $"look in {NuPkgLocation})",
                "ok"               => "ok (Rockwell DLLs present)",
                _                  => e.Status,
            };
            return $"{i + 1}. {e.Path}: {reason}";
        }).ToList();

    /// <summary>
    /// Probes all candidate SDK directories in priority order and returns a
    /// <see cref="SdkProbeEntry"/> for every candidate, describing whether it
    /// qualified or why it was rejected.
    ///
    /// <para>
    /// Used by <see cref="ConnectHandler"/> to populate the
    /// <see cref="ConnectSdkNotAvailableError.ProbedPaths"/> field so the caller
    /// can self-diagnose SDK availability issues without consulting documentation.
    /// </para>
    ///
    /// <para>
    /// Status strings:
    /// <list type="bullet">
    ///   <item><c>"not_found"</c> — directory does not exist.</item>
    ///   <item><c>"no_rockwell_dlls"</c> — directory exists but contains no
    ///     <c>RockwellAutomation*.dll</c> files at all.</item>
    ///   <item><c>"client_dll_missing"</c> — directory exists and contains other
    ///     <c>RockwellAutomation*.dll</c> files (partial SDK install) but is missing
    ///     the required <see cref="CsClientDllName"/> client assembly, which ships
    ///     only as a NuGet package under <see cref="NuPkgLocation"/>.</item>
    ///   <item><c>"ok"</c> — directory qualifies (<see cref="CsClientDllName"/>
    ///     present).</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <returns>
    /// Array of <see cref="SdkProbeEntry"/> records, one per probe candidate,
    /// in probe order (same order as <see cref="GetProbeCandidates"/>).
    /// </returns>
    public static SdkProbeEntry[] CollectProbeResults()
    {
        var results     = new List<SdkProbeEntry>();
        var sdkVendorPath = Path.Combine(AppContext.BaseDirectory, AdapterSubdirectory, VendorSubdirectory);

        foreach (var dir in GetProbeCandidates())
        {
            string  status;
            string? message = null;

            if (!Directory.Exists(dir))
            {
                status = "not_found";
            }
            else if (File.Exists(Path.Combine(dir, CsClientDllName)))
            {
                status = "ok";
            }
            else if (DirectoryHasAnyRockwellDlls(dir))
            {
                // Partial SDK install: other Rockwell assemblies are present but the
                // client assembly (which ships only as a NuGet package) is missing.
                status = "client_dll_missing";

                // When the failing path is the app-local sdk/vendor/ directory, surface
                // an actionable message: this is the state a user reaches after running
                // the binary for the first time but before running the installer.
                if (dir == sdkVendorPath)
                    message = "sdk/vendor/ is the directory the installer populates — " +
                              "run the installer to place the required Rockwell assemblies here";
            }
            else
            {
                status = "no_rockwell_dlls";
            }
            results.Add(new SdkProbeEntry(dir, status, message));
        }
        return results.ToArray();
    }

    /// <summary>
    /// Probes candidate directories in priority order and returns the first one that
    /// contains <see cref="CsClientDllName"/>.
    /// Returns <c>null</c> when no qualifying directory is found.
    /// </summary>
    internal static string? ProbeForSdkDirectory()
    {
        foreach (var dir in GetProbeCandidates())
        {
            if (DirectoryHasRockwellDlls(dir))
                return dir;
        }
        return null;
    }

    /// <summary>
    /// Yields probe candidate directories in priority order:
    /// <list type="number">
    ///   <item><c>LOGIX_SDK_PATH</c> environment variable (when set and non-empty).</item>
    ///   <item><c>sdk/vendor/</c> under <see cref="AppContext.BaseDirectory"/> — the
    ///     app-local redistributable-closure payload directory, discoverable without
    ///     requiring the installer or any environment variable.</item>
    ///   <item>Studio 5000 Logix Designer SDK install path (<see cref="Studio5000SdkPath"/>).</item>
    ///   <item><see cref="AppContext.BaseDirectory"/> — implicit fallback for SDK builds.</item>
    /// </list>
    /// </summary>
    internal static IEnumerable<string> GetProbeCandidates()
    {
        var envPath = Environment.GetEnvironmentVariable("LOGIX_SDK_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            yield return envPath;

        // App-local redistributable-closure payload: <BaseDirectory>/sdk/vendor.
        // Ordered after LOGIX_SDK_PATH (env-var override keeps winning) and before
        // the Studio 5000 install path so the bundled payload is discovered without
        // requiring the installer or the user to set any environment variable.
        yield return Path.Combine(AppContext.BaseDirectory, AdapterSubdirectory, VendorSubdirectory);

        yield return Studio5000SdkPath;

        // NOTE: the current working directory is deliberately NOT probed. MCP clients
        // launch the server with the user's workspace as cwd, so a cwd-relative
        // candidate would load attacker-controlled DLLs from a cloned repo.

        // Implicit fallback: SDK builds copy Rockwell DLLs alongside the executable
        // via the CopyToMainOutput build target in LogixMcp.SdkAdapter.csproj.
        yield return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Derives the vendor directory path from the path of the adapter DLL.
    ///
    /// <para>
    /// The vendor directory is always placed in the <see cref="VendorSubdirectory"/>
    /// subdirectory adjacent to the adapter DLL, making the resolver self-consistent:
    /// wherever the adapter is deployed, the vendor assemblies are expected alongside
    /// it under <c>vendor/</c>.
    /// </para>
    ///
    /// <para>
    /// Example: if <paramref name="adapterPath"/> is
    /// <c>/opt/app/sdk/LogixMcp.SdkAdapter.dll</c>, this returns
    /// <c>/opt/app/sdk/vendor</c>.
    /// </para>
    /// </summary>
    /// <param name="adapterPath">
    /// Absolute path to the adapter DLL.  Must not be <c>null</c>.
    /// </param>
    /// <returns>
    /// Absolute path of the vendor directory (which may or may not exist on
    /// the file system at the time of the call).
    /// </returns>
    internal static string GetVendorDir(string adapterPath) =>
        Path.Combine(Path.GetDirectoryName(adapterPath)!, VendorSubdirectory);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="dir"/> exists on the file system and
    /// contains the file <see cref="CsClientDllName"/>
    /// (<c>RockwellAutomation.LogixDesigner.LogixProject.CSClient.dll</c>).
    ///
    /// <para>
    /// Presence of other <c>RockwellAutomation*.dll</c> files (for example the gRPC
    /// server assembly <c>RockwellAutomation.LogixDesigner.LogixProjectServices.dll</c>)
    /// is not sufficient; only the exact CSClient assembly qualifies a directory.
    /// </para>
    /// </summary>
    /// <param name="dir">Directory path to inspect.</param>
    internal static bool DirectoryHasRockwellDlls(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        return File.Exists(Path.Combine(dir, CsClientDllName));
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="dir"/> exists and contains at least
    /// one file whose name starts with <c>RockwellAutomation</c> (case-insensitive)
    /// and ends with <c>.dll</c>.
    ///
    /// <para>
    /// Used by <see cref="CollectProbeResults"/> to distinguish a partial SDK install
    /// (other Rockwell assemblies present but <see cref="CsClientDllName"/> missing) from
    /// a directory with no Rockwell content at all.  This lets the caller surface a more
    /// actionable status (<c>"client_dll_missing"</c>) instead of the generic
    /// <c>"no_rockwell_dlls"</c>.
    /// </para>
    /// </summary>
    /// <param name="dir">Directory path to inspect.</param>
    internal static bool DirectoryHasAnyRockwellDlls(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        return Directory.EnumerateFiles(dir, "RockwellAutomation*.dll").Any();
    }

    /// <summary>
    /// Invokes <c>SdkAdapterInitializer.Initialize()</c> reflectively on the loaded
    /// adapter assembly, ensuring the factory registration fires even in hosts that do
    /// not execute module initializers automatically.
    ///
    /// <para>
    /// When the <c>SdkAdapterInitializer</c> type is absent, its <c>Initialize</c>
    /// method is absent, or the reflective <see cref="System.Reflection.MethodBase.Invoke"/>
    /// call throws, the failure message is captured into <see cref="LoadFailureReason"/>
    /// so that the invisible-null-factory defect cannot recur: any post-load state where
    /// <see cref="ConnectHandler.DefaultSdkFactory"/> is <c>null</c> will always have a
    /// corresponding entry in <see cref="LoadFailureReason"/>.  All failures are swallowed
    /// (no re-throw) and additionally logged at debug level so they cannot prevent the
    /// server from starting.
    /// </para>
    /// </summary>
    /// <param name="assembly">
    /// The loaded adapter assembly.  Must not be <c>null</c>.
    /// </param>
    /// <param name="logger">
    /// Optional logger for diagnostic messages.  Pass <c>null</c> to suppress all
    /// logging.
    /// </param>
    internal static void InvokeInitializer(Assembly assembly, ILogger? logger = null)
    {
        var initType = assembly.GetType("SdkAdapterInitializer");
        if (initType is null)
        {
            LoadFailureReason =
                "Initializer type 'SdkAdapterInitializer' not found in adapter assembly; " +
                "factory registration skipped.";
            logger?.LogDebug(
                "SdkAdapterInitializer type not found in adapter assembly; " +
                "skipping reflective initialization.");
            return;
        }

        var initMethod = initType.GetMethod(
            "Initialize",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        if (initMethod is null)
        {
            LoadFailureReason =
                "Initializer method 'Initialize' not found on 'SdkAdapterInitializer' " +
                "in adapter assembly; factory registration skipped.";
            logger?.LogDebug(
                "SdkAdapterInitializer.Initialize method not found; " +
                "skipping reflective initialization.");
            return;
        }

        try
        {
            initMethod.Invoke(null, null);
        }
        catch (Exception ex)
        {
            // Unwrap TargetInvocationException so the recorded message names the
            // actual exception rather than the reflection wrapper.
            var cause = ex is System.Reflection.TargetInvocationException { InnerException: { } inner }
                ? inner
                : ex;
            LoadFailureReason =
                $"Initializer 'SdkAdapterInitializer.Initialize' threw " +
                $"{cause.GetType().Name}: {cause.Message}";
            logger?.LogDebug(ex,
                "SdkAdapterInitializer.Initialize threw during reflective invocation; " +
                "factory registration skipped.");
            return;
        }

        logger?.LogDebug(
            "SdkAdapterInitializer.Initialize invoked reflectively on adapter assembly.");
    }
}

// ── SdkAssemblyLoadContext ────────────────────────────────────────────────────

/// <summary>
/// Custom <see cref="AssemblyLoadContext"/> that resolves
/// <c>RockwellAutomation.*</c> assemblies from a configured SDK directory and
/// vendor assemblies (<c>Grpc.*</c>, <c>Google.Protobuf</c>, and other Rockwell
/// transitive dependencies) from an optional vendor directory, while deferring all
/// other assembly resolution to the default context.
///
/// <para>
/// Deferring non-vendor assemblies to the default context is essential for
/// correct type identity: <c>LogixMcp</c> core types such as
/// <see cref="ConnectHandler"/>, <see cref="ISdkTagServiceFactory"/>, and
/// <see cref="ITagService"/> must be the same CLR instances whether they are
/// referenced from the core assembly or from the adapter loaded into this context.
/// Without this sharing, interface dispatch and type-cast operations between the
/// two contexts would silently fail at runtime.
/// </para>
///
/// <para>
/// Resolution order:
/// <list type="number">
///   <item><c>RockwellAutomation.*</c> is probed from <paramref name="sdkDir"/>.</item>
///   <item>Any other assembly present in <paramref name="vendorDir"/> is loaded from
///     there.  This covers <c>Grpc.*</c>, <c>Google.Protobuf</c>, and all other
///     transitive vendor dependencies of the Rockwell SDK.</item>
///   <item>All remaining assemblies (including all core <c>LogixMcp</c> types) fall
///     back to the default <see cref="AssemblyLoadContext"/>.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class SdkAssemblyLoadContext : AssemblyLoadContext
{
    private readonly string _sdkDir;
    private readonly string? _vendorDir;

    /// <summary>
    /// Initialises a new <see cref="SdkAssemblyLoadContext"/> that resolves
    /// Rockwell assemblies from <paramref name="sdkDir"/> and vendor assemblies
    /// (<c>Grpc.*</c>, <c>Google.Protobuf</c>, etc.) from <paramref name="vendorDir"/>.
    /// </summary>
    /// <param name="sdkDir">
    /// Directory containing <c>RockwellAutomation*.dll</c> files.
    /// Must not be <c>null</c>.
    /// </param>
    /// <param name="vendorDir">
    /// Directory containing vendor assemblies that are transitive dependencies of
    /// the Rockwell SDK (e.g. <c>Grpc.*</c>, <c>Google.Protobuf</c>).  Pass
    /// <c>null</c> to skip vendor directory probing (the directory need not exist
    /// at construction time; probing is attempted on each <see cref="Load"/> call).
    /// </param>
    public SdkAssemblyLoadContext(string sdkDir, string? vendorDir = null)
        : base(name: "SdkAdapterContext", isCollectible: false)
    {
        _sdkDir = sdkDir;
        _vendorDir = vendorDir;
    }

    /// <summary>
    /// Resolves assemblies from the configured SDK and vendor directories.
    ///
    /// <para>
    /// <c>RockwellAutomation.*</c> assemblies are resolved from <see cref="_sdkDir"/>.
    /// Any assembly whose DLL is found in <see cref="_vendorDir"/> (when set) is also
    /// resolved from there — this covers <c>Grpc.*</c>, <c>Google.Protobuf</c>, and
    /// other transitive Rockwell SDK dependencies.  All other assembly names return
    /// <c>null</c>, causing the runtime to fall back to the default
    /// <see cref="AssemblyLoadContext"/> and share those type identities with the core
    /// assembly.
    /// </para>
    /// </summary>
    /// <param name="assemblyName">The assembly identity to resolve.</param>
    /// <returns>
    /// The loaded <see cref="Assembly"/> when found in <see cref="_sdkDir"/> or
    /// <see cref="_vendorDir"/>; <c>null</c> for all other names (fallback to the
    /// default context).
    /// </returns>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // RockwellAutomation.* assemblies are resolved from the probed SDK directory.
        if (assemblyName.Name?.StartsWith(
                "RockwellAutomation", StringComparison.OrdinalIgnoreCase) == true)
        {
            var candidate = Path.Combine(_sdkDir, assemblyName.Name + ".dll");
            if (File.Exists(candidate))
                return LoadFromAssemblyPath(candidate);
        }

        // Grpc.*, Google.Protobuf, and other transitive vendor dependencies of the
        // Rockwell SDK are resolved from the vendor directory (derived from the
        // adapter DLL's own location by SdkAdapterLoader.GetVendorDir).
        // The vendor directory is probed for any DLL present there, so the list of
        // covered assemblies is determined by the directory contents rather than a
        // hard-coded prefix list — future transitive dependencies are handled
        // automatically without code changes.
        if (_vendorDir is not null && assemblyName.Name is { Length: > 0 } name)
        {
            var candidate = Path.Combine(_vendorDir, name + ".dll");
            if (File.Exists(candidate))
                return LoadFromAssemblyPath(candidate);
        }

        // Defer to the default context for all other assemblies (including all
        // LogixMcp core types) so that type identities are shared across contexts.
        return null;
    }
}
