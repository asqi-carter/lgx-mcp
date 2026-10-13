using System.Reflection;
using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for <see cref="RockwellErrorHints"/>, the one table of Rockwell SDK error codes: one test per
/// table entry, and a source scan that the adapter call sites classify through the table.
/// </summary>
public class RockwellErrorHintsTests
{
    /// <summary>Representative SDK text for each table entry, and the kind the server must report for it.</summary>
    private static readonly Dictionary<string, (string Text, string? Kind)> Samples = new()
    {
        [RockwellErrorHintIds.ImagesIncompatible] =
            ("RxCL_E_IMAGES_INCOMPATIBLE: the images are not compatible", nameof(TagServiceErrorKind.ImagesIncompatible)),
        [RockwellErrorHintIds.CannotUploadPhysAddr] =
            ("RxCL_E_CANNOT_UPLOAD_PHYS_ADDR: cannot upload", nameof(TagServiceErrorKind.ImagesIncompatible)),
        [RockwellErrorHintIds.CommLayer] =
            ("RxE_COMM_DTLERROR: communications error", nameof(TagServiceErrorKind.SdkCommLayerFaulted)),
        [RockwellErrorHintIds.ServerFaulted] =
            ("LgxSrv_E_FATAL_ERROR: the Logix Services server faulted", nameof(TagServiceErrorKind.SdkServerFaulted)),
        [RockwellErrorHintIds.ProjectNotOpen] =
            ("Project is not open.", nameof(TagServiceErrorKind.SdkServiceDegraded)),
        [RockwellErrorHintIds.ServiceUnreachable] =
            ("Could not reach LdSdkService", nameof(TagServiceErrorKind.SdkServiceDegraded)),
        [RockwellErrorHintIds.ImportAbortedNoChanges] =
            ("XMLSrv_E_IMPORT_ABORTED_NO_CHANGES - The Import was cancelled due to errors. No changes were made.",
             nameof(TagServiceErrorKind.ImportRejectedByImporter)),
        [RockwellErrorHintIds.PieEditsExistForProgram] =
            ("XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM - The program contains accepted logic edits",
             RockwellErrorHints.PendingEditsBlockImportKind),
        [RockwellErrorHintIds.IncompatibleImportTarget] =
            ("XMLSrv_E_INCOMPATIBLE_IMPORT_TARGET", nameof(TagServiceErrorKind.ImportRejectedByImporter)),
        [RockwellErrorHintIds.DataTooNew] =
            ("RxE_DATA_TOO_NEW", ErrorKinds.SdkCapabilityUnavailable),
        [RockwellErrorHintIds.RequiredVersionNotInstalled] =
            ("Required Logix Designer version 37.0 is not installed", ErrorKinds.SdkCapabilityUnavailable),
        [RockwellErrorHintIds.MorphToThisControllerNotSupported] =
            ("RxCsE_MORPH_TO_THIS_CONTROLLER_NOT_SUPPORTED", ErrorKinds.SdkCapabilityUnavailable),
        [RockwellErrorHintIds.ControllerLocked] =
            ("RxCsE_ASA_BLOCKING_HIGHER_EXCL_LOCK", ErrorKinds.ControllerLocked),
        [RockwellErrorHintIds.IllegalOperations] =
            ("RxE_ILLEGAL_OPERATIONS - Sequence of operations not allowed", ErrorKinds.ControllerBusy),
        [RockwellErrorHintIds.UnsupportedOnDesignerVersion] =
            ("Operation not supported on Logix Designer version 36.5.", ErrorKinds.SdkCapabilityUnavailable),
        [RockwellErrorHintIds.XmlSrvAny] =
            ("XMLSrv_E_ERROR_READING_XML", null),
    };

    public static IEnumerable<object[]> TableIds() =>
        RockwellErrorHints.Table.Select(h => new object[] { h.Id });

    /// <summary>Every table entry has a sample here, so none can be added without a test.</summary>
    [Fact]
    public void EveryTableEntry_HasASample()
    {
        Assert.Equal(
            RockwellErrorHints.Table.Select(h => h.Id).OrderBy(x => x, StringComparer.Ordinal),
            Samples.Keys.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(RockwellErrorHints.Table.Count, RockwellErrorHints.Table.Select(h => h.Id).Distinct().Count());
    }

    /// <summary>One test per table entry: its sample text matches it, and the kind is the expected one.</summary>
    [Theory]
    [MemberData(nameof(TableIds))]
    public void TableEntry_MatchesItsSdkText_AndReportsItsKind(string id)
    {
        var entry = RockwellErrorHints.Get(id);
        Assert.NotNull(entry);
        var (text, kind) = Samples[id];
        Assert.True(entry!.IsMatch(text), $"{id} did not match '{text}'");
        Assert.Equal(kind, entry.Kind);
        Assert.Same(entry, RockwellErrorHints.Find(text));
    }

    /// <summary>Every entry names a cause and a next step, a known kind, and no internal labels.</summary>
    [Theory]
    [MemberData(nameof(TableIds))]
    public void TableEntry_IsPhrasedForAnEngineer(string id)
    {
        var entry = RockwellErrorHints.Get(id)!;
        Assert.False(string.IsNullOrWhiteSpace(entry.Cause));
        Assert.False(string.IsNullOrWhiteSpace(entry.NextStep));
        Assert.EndsWith(".", entry.Cause);
        Assert.EndsWith(".", entry.NextStep);
        Assert.DoesNotMatch(@"\bPRD\b|\btier[- ]?\d|\bR\d\b|\bbd-", entry.Cause + " " + entry.NextStep);

        if (entry.Kind is not null)
        {
            var known = typeof(ErrorKinds).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
                .Concat(Enum.GetNames<TagServiceErrorKind>())
                .ToHashSet();
            Assert.Contains(entry.Kind, known);
        }
    }

    // ── The five entries added with the table ────────────────────────────────

    [Fact]
    public void IncompatibleImportTarget_ExplainsFragmentRootVersusXPath()
    {
        var e = RockwellErrorHints.Get(RockwellErrorHintIds.IncompatibleImportTarget)!;
        Assert.True(e.IsMatch("Exception Mnemonic='XMLSrv_E_INCOMPATIBLE_IMPORT_TARGET'"));
        Assert.Contains("root element", e.Cause);
        Assert.Contains("XPath", e.Cause + e.NextStep);
        Assert.Contains("XPath target", RockwellErrorHints.AdditionalGuidance("XMLSrv_E_INCOMPATIBLE_IMPORT_TARGET"));
    }

    [Fact]
    public void DataTooNew_ExplainsProjectFromNewerDesigner()
    {
        var e = RockwellErrorHints.Get(RockwellErrorHintIds.DataTooNew)!;
        Assert.True(e.IsMatch("Open failed: RxE_DATA_TOO_NEW"));
        Assert.Contains("newer version of Logix Designer", e.Cause);
        Assert.Contains("Install", e.NextStep);
    }

    [Theory]
    [InlineData("Required Logix Designer version 36.0 is not installed")]
    [InlineData("Required Logix Designer version 37 is not installed.")]
    [InlineData("required logix designer version 38.11 is not installed")]
    public void RequiredDesignerVersionNotInstalled_MatchesAnyVersionNumber(string text)
    {
        var e = RockwellErrorHints.Get(RockwellErrorHintIds.RequiredVersionNotInstalled)!;
        Assert.True(e.IsMatch(text));
        Assert.Same(e, RockwellErrorHints.Find(text));
        Assert.False(e.IsMatch("Studio 5000 is not installed on this host."));
    }

    [Fact]
    public void MorphToThisControllerNotSupported_ExplainsControllerTypeChange()
    {
        var e = RockwellErrorHints.Get(RockwellErrorHintIds.MorphToThisControllerNotSupported)!;
        Assert.True(e.IsMatch("RxCsE_MORPH_TO_THIS_CONTROLLER_NOT_SUPPORTED"));
        Assert.Contains("controller type", e.Cause);
    }

    [Fact]
    public void UnsupportedOperationOnV36_PointsAtTheCapabilityTable()
    {
        var e = RockwellErrorHints.Get(RockwellErrorHintIds.UnsupportedOnDesignerVersion)!;
        Assert.True(e.IsMatch("Operation not supported on Logix Designer version 36.5."));
        Assert.Contains("lgx://sdk/capabilities", e.NextStep);
        Assert.Equal(DesignerVersionInfo.UnsupportedVersionPattern, e.Pattern);
    }

    // ── Behaviour of the existing mappings is unchanged ──────────────────────

    [Fact]
    public void CommLayer_LooksThroughTheInnerExceptionChain()
    {
        var ex = new InvalidOperationException("outer", new Exception("RxE_COMM_OTHER"));
        Assert.True(RockwellErrorHints.IsCommLayerError(ex));
        Assert.False(RockwellErrorHints.IsCommLayerError(new Exception("RxCL_E_IMAGES_INCOMPATIBLE")));
    }

    [Fact]
    public void Incompatibility_LooksAtTheOuterMessageOnly()
    {
        Assert.True(RockwellErrorHints.IsIncompatibility(new Exception("x CANNOT_UPLOAD_PHYS_ADDR")));
        Assert.True(RockwellErrorHints.IsIncompatibility(new Exception("x images_incompatible")));
        Assert.False(RockwellErrorHints.IsIncompatibility(
            new Exception("outer", new Exception("IMAGES_INCOMPATIBLE"))));
    }

    [Fact]
    public void XmlSrvCatchAll_IsCaseSensitive_AndSpecificEntriesWinOverIt()
    {
        Assert.True(RockwellErrorHints.IsXmlSrvFailure("XMLSrv_E_ERROR_READING_XML"));
        Assert.False(RockwellErrorHints.IsXmlSrvFailure("xmlsrv_e_error_reading_xml"));
        Assert.Equal(RockwellErrorHintIds.IncompatibleImportTarget,
            RockwellErrorHints.Find("XMLSrv_E_INCOMPATIBLE_IMPORT_TARGET")!.Id);
    }

    [Fact]
    public void AdditionalGuidance_IsEmptyForCodesTheCallSitesAlreadyExplain()
    {
        Assert.Equal(string.Empty, RockwellErrorHints.AdditionalGuidance("XMLSrv_E_IMPORT_ABORTED_NO_CHANGES"));
        Assert.Equal(string.Empty, RockwellErrorHints.AdditionalGuidance("RxE_COMM_DTLERROR"));
        Assert.Equal(string.Empty, RockwellErrorHints.AdditionalGuidance(null));
    }

    // ── Source scan: the old call sites go through the table ─────────────────

    private static readonly string[] OldCodeTokens =
    [
        "RxE_COMM_", "IMAGES_INCOMPATIBLE", "CANNOT_UPLOAD_PHYS_ADDR", "Project is not open",
        "Could not reach LdSdkService", "Unable to complete connection to LdSdkService", "XMLSrv_E_",
        "not supported on Logix Designer version",
    ];

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogixMcp.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string CodeOnly(string path) =>
        string.Join('\n', File.ReadAllLines(path).Select(l => Regex.Replace(l, @"//.*$", string.Empty)));

    /// <summary>The adapter sources are not part of the public tree; the scans skip there.</summary>
    private static string AdapterDir()
    {
        var dir = Path.Combine(Root(), "src", "LogixMcp.SdkAdapter");
        Skip.IfNot(Directory.Exists(dir), "SDK adapter sources are not in this tree.");
        return dir;
    }

    private static string Adapter(string file) =>
        CodeOnly(Path.Combine(AdapterDir(), file));

    [SkippableTheory]
    [InlineData("ProposeRungEditHandler.cs")]
    [InlineData("SdkConnectFactory.cs")]
    [InlineData("SdkCommErrorClassifier.cs")]
    [InlineData("AcdProjectOpener.cs")]
    public void OldCallSites_ClassifyThroughRockwellErrorHints(string file) =>
        Assert.Contains("RockwellErrorHints", Adapter(file));

    [SkippableFact]
    public void TypedSdkTagService_ClassifiesThroughTheSharedClassifier_WhichUsesTheTable()
    {
        var typed = Adapter("TypedSdkTagService.cs");
        Assert.True(typed.Contains("SdkCommErrorClassifier") || typed.Contains("RockwellErrorHints"));
        Assert.Contains("RockwellErrorHints.IsCommLayerError", Adapter("SdkCommErrorClassifier.cs"));
        Assert.Contains("RockwellErrorHints.IsProjectNotOpen", Adapter("SdkCommErrorClassifier.cs"));
        Assert.Contains("RockwellErrorHints.IsServiceUnreachable", Adapter("SdkCommErrorClassifier.cs"));
    }

    [SkippableFact]
    public void NoAdapterCode_MatchesSdkErrorCodesInline()
    {
        var adapterDir = AdapterDir();
        var sep = Path.DirectorySeparatorChar;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(adapterDir, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}")))
        {
            var code = CodeOnly(file);
            foreach (var token in OldCodeTokens.Where(code.Contains))
                offenders.Add($"{Path.GetFileName(file)}: {token}");
        }
        Assert.True(offenders.Count == 0, "SDK error codes outside RockwellErrorHints: " + string.Join("; ", offenders));
    }
}
