namespace LogixMcp.Tests;

/// <summary>
/// <c>logix_import_datatype</c> must never destroy an
/// existing file, must write atomically, and must not pretend a fragment is a project.
/// </summary>
public class ImportDatatypeSafetyTests
{
    private static readonly DataTypeMemberEntry[] OneMember =
    {
        new("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
    };

    private static ProjectService DemoService()
    {
        var svc = new ProjectService();
        svc.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X"));
        return svc;
    }

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"logixmcp-safety-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    private static string FakeAcd(string dir)
    {
        var acd = Path.Combine(dir, "in.ACD");
        File.WriteAllBytes(acd, new byte[101 * 1024]);
        return acd;
    }

    private static object Run(string acd, string saveAs, bool overwrite = false,
        Func<string, string, string, ImportCollisionOption, IReadOnlyList<string>>? sdk = null) =>
        ImportDatatypeHandler.Execute(
            acdPath: acd, datatypeName: "MotorCmd", l5xPath: null, members: OneMember,
            saveAs: saveAs, service: DemoService(), dryRun: false, confirm: true,
            reexportReader: _ => OneMember, sdkImportRunner: sdk, overwrite: overwrite);

    [Fact]
    public void ExistingSaveAs_RefusedWithoutOverwrite()
    {
        var dir = NewDir();
        try
        {
            var acd  = FakeAcd(dir);
            var dest = Path.Combine(dir, "out.L5X");
            File.WriteAllText(dest, "PRECIOUS");

            var err = Assert.IsType<ImportDatatypeError>(Run(acd, dest));
            Assert.Equal("TargetExists", err.Kind);
            Assert.Equal("PRECIOUS", File.ReadAllText(dest));

            // Same refusal on the SDK path; runner must not even be called.
            var calls = 0;
            var destAcd = Path.Combine(dir, "out.ACD");
            File.WriteAllText(destAcd, "PRECIOUS");
            var err2 = Assert.IsType<ImportDatatypeError>(
                Run(acd, destAcd, sdk: (_, _, _, _) => { calls++; return Array.Empty<string>(); }));
            Assert.Equal("TargetExists", err2.Kind);
            Assert.Equal(0, calls);
            Assert.Equal("PRECIOUS", File.ReadAllText(destAcd));

            // With overwrite=true the file is replaced and no temp files remain.
            var ok = Assert.IsType<ImportDatatypeResult>(Run(acd, dest, overwrite: true));
            Assert.NotEqual("PRECIOUS", File.ReadAllText(dest));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp*"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailedImport_LeavesPreExistingFileIntact()
    {
        var dir = NewDir();
        try
        {
            var acd  = FakeAcd(dir);
            var dest = Path.Combine(dir, "out.ACD");
            File.WriteAllText(dest, "PRECIOUS");

            // SDK throws → overwrite=true must still leave the original untouched.
            var err = Assert.IsType<ImportDatatypeError>(Run(acd, dest, overwrite: true,
                sdk: (_, _, _, _) => throw new InvalidOperationException("boom")));
            Assert.Equal("SdkError", err.Kind);
            Assert.Equal("PRECIOUS", File.ReadAllText(dest));

            // SDK "succeeds" but produces a fragment-sized file → refused, original intact.
            var err2 = Assert.IsType<ImportDatatypeError>(Run(acd, dest, overwrite: true,
                sdk: (tmp, _, _, _) => { File.WriteAllText(tmp, "tiny"); return Array.Empty<string>(); }));
            Assert.Equal("SavedFileNotAProject", err2.Kind);
            Assert.Equal("PRECIOUS", File.ReadAllText(dest));

            // Only the pre-existing files remain: no leaked temp files.
            Assert.Equal(
                new[] { "in.ACD", "out.ACD" },
                Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void NoSdkFallback_AcdSaveAsRefused()
    {
        var dir = NewDir();
        try
        {
            var acd  = FakeAcd(dir);
            var dest = Path.Combine(dir, "out.ACD");

            var err = Assert.IsType<ImportDatatypeError>(Run(acd, dest));
            Assert.Equal("SdkRequired", err.Kind);
            Assert.False(File.Exists(dest));
            Assert.Equal(new[] { "in.ACD" },
                Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void NoSdkFallback_VerifiedIsNull()
    {
        var dir = NewDir();
        try
        {
            var acd  = FakeAcd(dir);
            var dest = Path.Combine(dir, "out.L5X");

            // A matching reexportReader must not make the fragment "verified".
            var ok = Assert.IsType<ImportDatatypeResult>(Run(acd, dest));
            Assert.Null(ok.Verified);
            Assert.Equal("No project import was performed; the data type was written as an L5X fragment.",
                ok.Note);
            Assert.True(File.Exists(dest));
            Assert.Equal(ok.L5XFragment, File.ReadAllText(dest));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp*"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
