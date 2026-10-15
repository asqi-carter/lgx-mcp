using System.Collections.Generic;

namespace LogixMcp.Tests;

/// <summary>
/// Regression tests for the call-site → registry wiring that was left
/// unconnected (the real run silently took the fragment fallback). Verifies
/// <see cref="LogixProjectTools.ResolveImportDatatypeDelegates"/> sources the SDK
/// delegates from <see cref="SdkImportDelegates"/> and bakes the datatype XPath
/// into the re-export reader.
/// </summary>
[Collection("StaticHandlerHooks")]
public class ImportDatatypeWiringTests : System.IDisposable
{
    // Snapshot registry state so each test restores it (static global state).
    private readonly System.Func<string, string, string, ImportCollisionOption, IReadOnlyList<string>>? _import  = SdkImportDelegates.SdkImportRunner;
    private readonly System.Func<string, string, IReadOnlyList<DataTypeMemberEntry>>? _reexp = SdkImportDelegates.ReexportReader;
    private readonly System.Func<string, int>? _build  = SdkImportDelegates.BuildRunner;
    private readonly System.Func<string, bool>? _lock   = SdkImportDelegates.StudioLockChecker;

    public void Dispose()
    {
        SdkImportDelegates.SdkImportRunner   = _import;
        SdkImportDelegates.ReexportReader    = _reexp;
        SdkImportDelegates.BuildRunner       = _build;
        SdkImportDelegates.StudioLockChecker = _lock;
    }

    [Fact]
    public void Resolve_UsesRegistry_WhenNoExplicitDelegates_AndBakesXpath()
    {
        string? seenXpath = null;
        SdkImportDelegates.SdkImportRunner   = (_, _, _, _) => System.Array.Empty<string>();
        SdkImportDelegates.BuildRunner       = _ => 0;
        SdkImportDelegates.StudioLockChecker = _ => false;
        SdkImportDelegates.ReexportReader    = (_, xpath) =>
        {
            seenXpath = xpath;
            return System.Array.Empty<DataTypeMemberEntry>();
        };

        var (lockChk, reexport, build, import) =
            LogixProjectTools.ResolveImportDatatypeDelegates(
                "UDT_Recipe", null, null, null, null);

        Assert.NotNull(import);
        Assert.NotNull(build);
        Assert.NotNull(lockChk);
        Assert.NotNull(reexport);

        // The reader gets only the saved path; the resolver must bake in the XPath.
        _ = reexport!("C:/out.ACD");
        Assert.Equal(
            "Controller/DataTypes/DataType[@Name='UDT_Recipe']", seenXpath);
    }

    [Fact]
    public void Resolve_ExplicitDelegatesWin_OverRegistry()
    {
        SdkImportDelegates.SdkImportRunner = (_, _, _, _) => throw new System.Exception("registry");
        SdkImportDelegates.BuildRunner     = _ => throw new System.Exception("registry");

        System.Func<string, string, string, ImportCollisionOption, IReadOnlyList<string>> myImport = (_, _, _, _) => System.Array.Empty<string>();
        System.Func<string, int> myBuild = _ => 7;
        System.Func<string, IReadOnlyList<DataTypeMemberEntry>> myReexport =
            _ => System.Array.Empty<DataTypeMemberEntry>();

        var (_, reexport, build, import) =
            LogixProjectTools.ResolveImportDatatypeDelegates(
                "AnyType", null, myReexport, myBuild, myImport);

        Assert.Same(myImport, import);
        Assert.Same(myBuild, build);
        Assert.Same(myReexport, reexport);  // explicit reader passed through unwrapped
    }

    [Fact]
    public void Resolve_LeavesDelegatesNull_WhenRegistryEmpty()
    {
        SdkImportDelegates.SdkImportRunner   = null;
        SdkImportDelegates.ReexportReader    = null;
        SdkImportDelegates.BuildRunner       = null;
        SdkImportDelegates.StudioLockChecker = null;

        var (lockChk, reexport, build, import) =
            LogixProjectTools.ResolveImportDatatypeDelegates(
                "AnyType", null, null, null, null);

        Assert.Null(import);
        Assert.Null(build);
        Assert.Null(lockChk);
        Assert.Null(reexport);
    }

    [Fact]
    public void DryRun_Delta_UsesAcdPathBaseline_ViaReader_NotEmptyService()
    {
        // Regression: the dry-run baseline must come from acdPath,
        // not the empty _service. With an empty baseline every target member showed
        // as "added" and the removals/retypes were invisible.
        var acd = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"lgxmcp-baseline-{System.Guid.NewGuid():N}.ACD");
        System.IO.File.WriteAllText(acd, "stub"); // must exist for the reader to be consulted
        try
        {
            var baseline = new[] // older shape (what acdPath currently holds)
            {
                new DataTypeMemberEntry("RecipeName",             "STRING", "0",  "", false, "Read/Write"),
                new DataTypeMemberEntry("ProductSettings",     "REAL",   "30", "", false, "Read/Write"),
                new DataTypeMemberEntry("ProductDescriptions", "STRING", "30", "", false, "Read/Write"),
                new DataTypeMemberEntry("DoseSettings",           "DINT",   "10", "", false, "Read/Write"),
            };
            var target = new[] // import-shaped target
            {
                new DataTypeMemberEntry("RecipeName",         "STRING", "0",  "", false, "Read/Write"),
                new DataTypeMemberEntry("ProductSettings", "STRING", "30", "", false, "Read/Write"),
                new DataTypeMemberEntry("DoseSettings",       "INT",    "10", "", false, "Read/Write"),
                new DataTypeMemberEntry("SetupValues",        "INT",    "50", "", false, "Read/Write"),
            };

            var result = ImportDatatypeHandler.Execute(
                acdPath:        acd,
                datatypeName:   "UDT_Recipe",
                l5xPath:        null,
                members:        target,
                saveAs:         acd + ".out.ACD",
                service:        null,               // empty service — old bug source
                dryRun:         true,
                reexportReader: _ => baseline);     // baseline read from acdPath

            var r     = Assert.IsType<ImportDatatypeResult>(result);
            var delta = Assert.IsType<DryRunDelta>(r.Delta);

            // Unchanged members must NOT appear as added (the regression tell).
            Assert.DoesNotContain("RecipeName", delta.Added);
            // The genuinely new member is added.
            Assert.Contains("SetupValues", delta.Added);
            // The dropped member is removed.
            Assert.Contains("ProductDescriptions", delta.Removed);
            // The type changes are retyped, not added.
            Assert.Contains(delta.Retyped, e => e.Name == "ProductSettings");
            Assert.Contains(delta.Retyped, e => e.Name == "DoseSettings");
        }
        finally
        {
            try { System.IO.File.Delete(acd); } catch { }
        }
    }
}

