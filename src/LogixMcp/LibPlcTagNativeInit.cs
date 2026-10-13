// ── LibPlcTagNativeInit — single-file native-library bootstrap for libplctag ──
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

/// <summary>
/// Bootstraps the libplctag native library so that CIP tag operations work when
/// the application is published with <c>PublishSingleFile=true</c>.
///
/// <para><b>Root cause</b></para>
/// <para>
/// <c>libplctag.NativeImport 1.0.0</c> uses
/// <c>Assembly.GetExecutingAssembly().CodeBase</c> inside
/// <c>LibraryExtractor.GetExecutingAssemblyDirectory()</c>.  <c>Assembly.CodeBase</c>
/// throws <see cref="NotSupportedException"/> when the assembly is embedded in a
/// single-file bundle, because bundled assemblies have no physical file path.
/// </para>
///
/// <para><b>Fix (Approach 2)</b></para>
/// <para>
/// <see cref="EnsureInitialized"/> replaces libplctag's own extraction loop with
/// a single-file–compatible equivalent:
/// <list type="number">
///   <item>Load the <c>libplctag.NativeImport</c> assembly via its
///     <see cref="AssemblyName"/> (resolved from <c>libplctag</c>'s reference
///     list).  <c>Assembly.Load(AssemblyName)</c> works even when the assembly is
///     bundled — the runtime extracts it from the single-file payload and returns
///     the loaded instance.</item>
///   <item>Read the embedded native library via
///     <see cref="Assembly.GetManifestResourceStream"/>, which is supported for
///     bundled assemblies, and write it to a process-scoped temp directory.</item>
///   <item>Pre-load the extracted native library with
///     <see cref="NativeLibrary.Load(string)"/> so the OS has it mapped into the
///     process.</item>
///   <item>Register a <see cref="NativeLibrary.SetDllImportResolver"/> on the
///     <c>libplctag.NativeImport</c> assembly so every P/Invoke for <c>"plctag"</c>
///     resolves to our pre-loaded handle rather than the OS search path.</item>
///   <item>Set the private field
///     <c>libplctag.NativeImport.plctag.libraryAlreadyInitialized = true</c> via
///     reflection, preventing <c>ExtractLibraryIfRequired()</c> from ever calling
///     <c>LibraryExtractor.Init()</c> — which is the method that accesses
///     <c>Assembly.CodeBase</c> and throws.</item>
/// </list>
/// </para>
///
/// <para><b>Hardening</b></para>
/// <para>
/// Extraction-directory check: libplctag.NativeImport 1.0.0 exposes only a
/// <c>ForceExtractLibrary</c> boolean and an internal
/// <c>LibraryExtractor.GetExecutingAssemblyDirectory()</c>; there is no
/// extraction-directory setting, so this class extracts the library itself.
/// The library is written into a fresh, unpredictable directory created by
/// <see cref="Directory.CreateTempSubdirectory(string?)"/> (<c>libplctag_</c>
/// prefix) and restricted to 0700 on Unix.  An existing file is never reused
/// (the file is created with <see cref="FileMode.CreateNew"/>), and the SHA-256
/// of the bytes on disk is compared with the SHA-256 of the embedded resource
/// before <see cref="NativeLibrary.Load(string)"/> is called; a mismatch is
/// refused with <see cref="InvalidOperationException"/>.
/// </para>
///
/// <para><b>Alternatives rejected</b></para>
/// <list type="bullet">
///   <item><b>(1) Call LibraryExtractor.Init with an explicit directory.</b>
///     <c>LibraryExtractor</c> is <c>internal</c> to <c>libplctag.NativeImport</c>
///     and its sole overload <c>Init(bool forceExtract)</c> accepts no directory
///     parameter.  Even via reflection the method signature cannot be supplied with
///     a path; worse, the method always calls <c>GetExecutingAssemblyDirectory()</c>
///     as its first statement — before the optional extraction check — so it would
///     still throw in single-file mode regardless of the <c>forceExtract</c>
///     argument.</item>
///   <item><b>(3) Upgrade libplctag.</b>  Version 1.0.0 is the only release
///     available in the configured NuGet feed.  No newer version was present to
///     evaluate.</item>
///   <item><b>(4) Stop publishing single-file.</b>  Ruled out — the single-file
///     deployment shape is a stated requirement.</item>
/// </list>
/// </summary>
internal static class LibPlcTagNativeInit
{
    private static bool _done;

    /// <summary>
    /// Full path of the native library extracted by the last successful
    /// <see cref="EnsureInitialized"/>; <see langword="null"/> before that.
    /// </summary>
    internal static string? ExtractedLibraryPath { get; private set; }
    private static readonly object _lock = new();

    /// <summary>
    /// Ensures the native plctag library is extracted and loaded before the first
    /// P/Invoke call.  Safe to call any number of times; only the first call
    /// performs work.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown when libplctag does not ship a native binary for the current OS and
    /// architecture combination.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the expected embedded resource or internal reflection target is
    /// missing — which would indicate a breaking change in the
    /// <c>libplctag.NativeImport</c> package.
    /// </exception>
    public static void EnsureInitialized()
    {
        if (_done) return;
        lock (_lock)
        {
            if (_done) return;
            DoInit();
            _done = true;
        }
    }

    // ── Implementation ────────────────────────────────────────────────────────

    private static void DoInit()
    {
        var nativeImportAsm = LoadNativeImportAssembly();

        var (resourceName, nativeLibFileName) = GetPlatformResourceInfo();

        // Extract the embedded native library into a fresh private directory
        // so it has a real file-system path that NativeLibrary.Load() can consume.
        var nativeLibPath = ExtractVerified(
            () => nativeImportAsm.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded resource '{resourceName}' not found in " +
                    $"libplctag.NativeImport. " +
                    $"Available resources: " +
                    string.Join(", ", nativeImportAsm.GetManifestResourceNames())),
            nativeLibFileName);
        ExtractedLibraryPath = nativeLibPath;

        // Pre-load the native library so the OS has it mapped into this process.
        var nativeHandle = NativeLibrary.Load(nativeLibPath);

        // Redirect every future P/Invoke for "plctag" in the NativeImport assembly
        // to our already-loaded handle.  Returning IntPtr.Zero for other names
        // falls through to the runtime's default resolution.
        NativeLibrary.SetDllImportResolver(nativeImportAsm, (name, _, _) =>
            name == "plctag" ? nativeHandle : IntPtr.Zero);

        // Mark the library as already initialised so libplctag never calls
        // LibraryExtractor.Init() — the method that accesses Assembly.CodeBase.
        MarkLibraryAsInitialized(nativeImportAsm);
    }

    /// <summary>
    /// Extracts a native library into a brand-new private directory and verifies
    /// it against the source bytes.  The directory is never reused and the file is
    /// created with <see cref="FileMode.CreateNew"/>.  On any failure the new
    /// directory is deleted.
    /// </summary>
    /// <param name="openResource">Opens the embedded resource stream.</param>
    /// <param name="fileName">File name to write inside the new directory.</param>
    /// <param name="afterWrite">
    /// Test seam invoked with the written file's path after the write and before
    /// verification (used to simulate tampering).  Production passes
    /// <see langword="null"/>.
    /// </param>
    /// <returns>The full path of the verified native library.</returns>
    /// <exception cref="InvalidOperationException">
    /// The SHA-256 of the file on disk differs from that of the embedded resource.
    /// </exception>
    internal static string ExtractVerified(
        Func<Stream> openResource, string fileName, Action<string>? afterWrite = null)
    {
        var dir = Directory.CreateTempSubdirectory("libplctag_");
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dir.FullName,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var path = Path.Combine(dir.FullName, fileName);
            byte[] expectedHash;
            using (var stream = openResource())
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[81920];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    fs.Write(buffer, 0, n);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                expectedHash = sha.Hash!;
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            afterWrite?.Invoke(path);

            byte[] actualHash;
            using (var check = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                actualHash = SHA256.HashData(check);

            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                throw new InvalidOperationException(
                    $"SHA-256 mismatch for extracted native library '{path}': " +
                    $"expected {Convert.ToHexString(expectedHash)}, " +
                    $"got {Convert.ToHexString(actualHash)}. Refusing to load.");

            return path;
        }
        catch
        {
            try { dir.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Loads the <c>libplctag.NativeImport</c> assembly by resolving its
    /// <see cref="AssemblyName"/> from <c>libplctag</c>'s reference list.
    /// This works in both regular and single-file deployments:
    /// <list type="bullet">
    ///   <item>Regular: returns the already-loaded DLL instance.</item>
    ///   <item>Single-file: the runtime loads from the bundle.</item>
    /// </list>
    /// </summary>
    private static Assembly LoadNativeImportAssembly()
    {
        // typeof(libplctag.Tag) is already in the compilation — its assembly
        // references libplctag.NativeImport. Resolving via GetReferencedAssemblies
        // gives us the exact AssemblyName to pass to Assembly.Load, which works
        // from both a physical DLL and a single-file bundle.
        var libplctagAsm = typeof(libplctag.Tag).Assembly;
        var nativeImportName = libplctagAsm.GetReferencedAssemblies()
            .FirstOrDefault(n => n.Name == "libplctag.NativeImport")
            ?? throw new InvalidOperationException(
                "libplctag.dll does not list libplctag.NativeImport as a referenced assembly. " +
                "The package structure may have changed.");

        return Assembly.Load(nativeImportName);
    }

    /// <summary>
    /// Returns the embedded-resource name and the on-disk file name for the
    /// native library appropriate to the current OS and process architecture.
    /// </summary>
    private static (string resourceName, string nativeLibFileName) GetPlatformResourceInfo()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 =>
                    ("libplctag.NativeImport.runtime.win_x64.plctag.dll", "plctag.dll"),
                Architecture.X86 =>
                    ("libplctag.NativeImport.runtime.win_x86.plctag.dll", "plctag.dll"),
                var arch => throw new PlatformNotSupportedException(
                    $"libplctag does not ship a Windows native library for {arch}."),
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 =>
                    ("libplctag.NativeImport.runtime.linux_x64.libplctag.so", "libplctag.so"),
                Architecture.X86 =>
                    ("libplctag.NativeImport.runtime.linux_x86.libplctag.so", "libplctag.so"),
                var arch => throw new PlatformNotSupportedException(
                    $"libplctag does not ship a Linux native library for {arch}."),
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 =>
                    ("libplctag.NativeImport.runtime.osx_x64.libplctag.dylib", "libplctag.dylib"),
                var arch => throw new PlatformNotSupportedException(
                    $"libplctag does not ship a macOS native library for {arch}."),
            };
        }

        throw new PlatformNotSupportedException(
            $"libplctag is not supported on {RuntimeInformation.OSDescription}.");
    }

    /// <summary>
    /// Uses reflection to set the private static field
    /// <c>libplctag.NativeImport.plctag.libraryAlreadyInitialized</c> to
    /// <see langword="true"/>, suppressing libplctag's own extraction loop
    /// which calls the incompatible <c>Assembly.CodeBase</c>.
    /// </summary>
    private static void MarkLibraryAsInitialized(Assembly nativeImportAsm)
    {
        var plctagType = nativeImportAsm.GetType("libplctag.NativeImport.plctag")
            ?? throw new InvalidOperationException(
                "Could not find type 'libplctag.NativeImport.plctag' via reflection. " +
                "The libplctag.NativeImport internal structure may have changed.");

        var field = plctagType.GetField(
            "libraryAlreadyInitialized",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "Could not find field 'libraryAlreadyInitialized' on " +
                "'libplctag.NativeImport.plctag'. " +
                "The libplctag.NativeImport internal structure may have changed.");

        field.SetValue(null, true);
    }
}
