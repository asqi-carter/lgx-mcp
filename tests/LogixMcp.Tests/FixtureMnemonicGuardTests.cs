using System.Xml.Linq;

namespace LogixMcp.Tests;

/// <summary>
/// This change fixture hygiene: Logix Designer v36+ does not accept the pre-v36 mnemonics (<c>MOV</c>, <c>LES</c>,
/// <c>GRT</c>, ...) in content labelled v36 or later, so no fixture labelled 36+ may use one in instruction
/// position. A fixture that needs the old names is labelled 35.x (legacy shape); one labelled 36+ uses the IEC
/// names (IEC shape). At least <see cref="MinimumPerShape"/> fixtures of each shape must exist, and both are
/// exercised through <see cref="ProjectService"/> and <see cref="MnemonicSetChecker"/>.
/// </summary>
public class FixtureMnemonicGuardTests
{
    /// <summary>How many fixtures of each shape must exist.</summary>
    private const int MinimumPerShape = 5;

    private static readonly string[] SourceRootsRelativeToRepo = ["tests/LogixMcp.Tests/fixtures"];

    /// <summary>One fixture's label, rung texts and the old/new mnemonics found in instruction position.</summary>
    internal sealed record FixtureScan(string Path, int? LabelledMajor, IReadOnlyList<string> RungTexts)
    {
        public bool IsIecLabelled => LabelledMajor >= TargetRevision.FirstIecMajor;
        public bool IsLegacyLabelled => LabelledMajor is > 0 and < TargetRevision.FirstIecMajor;

        /// <summary>Names of the given set found in instruction position (or as an expression function).</summary>
        public IReadOnlyList<string> NamesOfSet(MnemonicSet set)
        {
            // Check against the *other* set's target so that names of `set` are the ones reported.
            var otherMajor = set == MnemonicSet.Legacy ? TargetRevision.FirstIecMajor : TargetRevision.FirstIecMajor - 1;
            var target = new TargetRevisionInfo(otherMajor, TargetRevision.MnemonicSetFor(otherMajor),
                TargetRevisionSource.MajorRev, null);
            var result = MnemonicSetChecker.Check(RungTexts, target);
            return result.Kind == MnemonicCheckKinds.WrongMnemonicSet
                ? result.Findings.SelectMany(f => f.Found).Distinct(StringComparer.Ordinal).ToList()
                : [];
        }
    }

    /// <summary>The repository root (the directory with the solution file), or <c>null</c> when not found.</summary>
    internal static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (dir.GetFiles("*.sln").Length > 0) return dir.FullName;
        return null;
    }

    /// <summary>Every L5X fixture: from the source tree when found, plus the copied output.</summary>
    private static IEnumerable<string> FixtureFiles()
    {
        var roots = new List<string>();
        if (RepoRoot() is { } repo)
            roots.AddRange(SourceRootsRelativeToRepo.Select(r => Path.Combine(repo, r)));
        roots.Add(Path.Combine(AppContext.BaseDirectory, "fixtures"));

        return roots.Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".l5x", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal);
    }

    /// <summary>Scans one L5X document: its label (greater of Controller/@MajorRev and the root's SoftwareRevision) and rung texts.</summary>
    internal static FixtureScan Scan(string path, string xml)
    {
        var root = XDocument.Parse(xml).Root;
        int? fromMajorRev = TargetRevision.ParseMajor((string?)root?.Element("Controller")?.Attribute("MajorRev"));
        int? fromSoftware = TargetRevision.ParseMajor(((string?)root?.Attribute("SoftwareRevision"))?.TrimStart('v', 'V'));
        int? labelled = fromMajorRev is null ? fromSoftware : fromSoftware is null ? fromMajorRev : Math.Max(fromMajorRev.Value, fromSoftware.Value);

        // Only rung <Text> is instruction position; comments, descriptions and Structured Text are not.
        var rungs = (root?.Descendants("Rung") ?? [])
            .Select(r => r.Element("Text")?.Value ?? string.Empty)
            .ToList();
        return new FixtureScan(path, labelled, rungs);
    }

    private static List<FixtureScan> ScanAll() =>
        FixtureFiles().Select(f => Scan(f, File.ReadAllText(f))).ToList();

    private static string FixtureRoot() => Path.Combine(AppContext.BaseDirectory, "fixtures");

    // ── The guard ────────────────────────────────────────────────────────────

    [Fact]
    public void Fixtures_LabelledV36OrHigher_UseNoOldMnemonicInInstructionPosition()
    {
        var scans = ScanAll();
        Assert.NotEmpty(scans);

        var offenders = scans
            .Where(s => s.IsIecLabelled)
            .Select(s => (s.Path, Names: s.NamesOfSet(MnemonicSet.Legacy)))
            .Where(x => x.Names.Count > 0)
            .Select(x => $"{x.Path}: {string.Join(", ", x.Names)}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Fixtures labelled SoftwareRevision 36+ must use the IEC mnemonics. Relabel to 35.x or rename (MOV->MOVE, LES->LT, ...):\n"
            + string.Join('\n', offenders));
    }

    [Fact]
    public void Guard_DetectsOldMnemonic_InFixtureLabelled36()
    {
        var bad = Scan("synthetic-bad.L5X", SyntheticFixture("36.1", "XIC(Run)MOV(Src,Dst);"));
        Assert.True(bad.IsIecLabelled);
        Assert.Equal(["MOV"], bad.NamesOfSet(MnemonicSet.Legacy));

        var viaV = Scan("synthetic-bad-v.L5X", SyntheticFixture("v36.00", "GRT(Src,10)OTE(Run);", majorRev: null));
        Assert.True(viaV.IsIecLabelled);
        Assert.Equal(["GRT"], viaV.NamesOfSet(MnemonicSet.Legacy));

        var inExpression = Scan("synthetic-expr.L5X", SyntheticFixture("37.0", "XIC(Run)CPT(Dst,SQR(Src));"));
        Assert.Equal(["SQR"], inExpression.NamesOfSet(MnemonicSet.Legacy));
    }

    [Fact]
    public void Guard_AllowsOldMnemonic_InFixtureLabelled35_AndIecMnemonic_InFixtureLabelled36()
    {
        var legacy = Scan("synthetic-legacy.L5X", SyntheticFixture("35.1", "XIC(Run)MOV(Src,Dst);"));
        Assert.True(legacy.IsLegacyLabelled);
        Assert.False(legacy.IsIecLabelled);
        Assert.Equal(["MOV"], legacy.NamesOfSet(MnemonicSet.Legacy));
        Assert.Empty(legacy.NamesOfSet(MnemonicSet.Iec));

        var iec = Scan("synthetic-iec.L5X", SyntheticFixture("36.1", "XIC(Run)MOVE(Src,Dst);"));
        Assert.True(iec.IsIecLabelled);
        Assert.Empty(iec.NamesOfSet(MnemonicSet.Legacy));
        Assert.Equal(["MOVE"], iec.NamesOfSet(MnemonicSet.Iec));
    }

    [Fact]
    public void Guard_IgnoresOldNames_InCommentsAndTagNames()
    {
        var xml = """
            <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1">
              <Controller Name="C" MajorRev="36" MinorRev="1"><Programs><Program Name="P"><Routines><Routine Name="R" Type="RLL"><RLLContent>
                <Rung Number="0" Type="N"><Comment><![CDATA[Was MOV(a,b) before v36.]]></Comment><Text><![CDATA[XIC(MOV_Done)OTE(LES_Out);]]></Text></Rung>
              </RLLContent></Routine></Routines></Program></Programs></Controller>
            </RSLogix5000Content>
            """;
        Assert.Empty(Scan("synthetic-comment.L5X", xml).NamesOfSet(MnemonicSet.Legacy));
    }

    // ── Both shapes exist and are exercised ──────────────────────────────────

    [Fact]
    public void Fixtures_HaveAtLeastFiveOfEachShape_LegacyLabelledAndIecLabelled()
    {
        var scans = ScanAll();
        // Distinct by file name: the eval corpus keeps verbatim copies of some fixtures.
        var legacyShape = scans.Where(s => s.IsLegacyLabelled && s.NamesOfSet(MnemonicSet.Legacy).Count > 0)
            .DistinctBy(s => Path.GetFileName(s.Path), StringComparer.OrdinalIgnoreCase).ToList();
        var iecShape = scans.Where(s => s.IsIecLabelled && s.NamesOfSet(MnemonicSet.Iec).Count > 0)
            .DistinctBy(s => Path.GetFileName(s.Path), StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(legacyShape.Count >= MinimumPerShape,
            $"Need at least {MinimumPerShape} fixtures labelled 35.x that use the old names; found {legacyShape.Count}.");
        Assert.True(iecShape.Count >= MinimumPerShape,
            $"Need at least {MinimumPerShape} fixtures labelled 36+ that use the IEC names; found {iecShape.Count}.");
    }

    public static TheoryData<string> LegacyShapeFixtures() => ShapeData(legacy: true);

    public static TheoryData<string> IecShapeFixtures() => ShapeData(legacy: false);

    private static TheoryData<string> ShapeData(bool legacy)
    {
        var names = new List<string>();
        foreach (var scan in ScanAll().Where(s => legacy
                     ? s.IsLegacyLabelled && s.NamesOfSet(MnemonicSet.Legacy).Count > 0
                     : s.IsIecLabelled && s.NamesOfSet(MnemonicSet.Iec).Count > 0))
        {
            // Only fixtures that are also copied to the test output can be loaded by name.
            var rel = Path.GetFileName(scan.Path);
            if (!names.Contains(rel) && Directory.EnumerateFiles(FixtureRoot(), rel, SearchOption.AllDirectories).Any())
                names.Add(rel);
        }
        var data = new TheoryData<string>();
        foreach (var n in names.Count == 0 ? ["none"] : names) data.Add(n);
        return data;
    }

    private static string OutputFixture(string fileName) =>
        fileName == "none"
            ? throw new InvalidOperationException("no fixtures of this shape")
            : Directory.EnumerateFiles(FixtureRoot(), fileName, SearchOption.AllDirectories).First();

    [Theory]
    [MemberData(nameof(LegacyShapeFixtures))]
    public void LegacyLabelledFixture_LoadsAsLegacySet_AndPassesLegacyCheck(string fileName)
    {
        var path = OutputFixture(fileName);
        var svc = new ProjectService();
        svc.Load(path);

        Assert.Equal(MnemonicSetNames.Legacy, svc.TargetRevision!.MnemonicSet);
        Assert.True(svc.TargetRevision.MajorRevision < TargetRevision.FirstIecMajor);

        var rungs = Scan(path, File.ReadAllText(path)).RungTexts;
        Assert.False(MnemonicSetChecker.Check(rungs, svc.TargetRevision).Refused);
        // Published to a v36+ target the same rungs would be refused: the shapes really differ.
        var iec = new TargetRevisionInfo(38, MnemonicSetNames.Iec, TargetRevisionSource.MajorRev, null);
        Assert.Equal(MnemonicCheckKinds.WrongMnemonicSet, MnemonicSetChecker.Check(rungs, iec).Kind);
    }

    [Theory]
    [MemberData(nameof(IecShapeFixtures))]
    public void IecLabelledFixture_LoadsAsIecSet_AndPassesIecCheck(string fileName)
    {
        var path = OutputFixture(fileName);
        var svc = new ProjectService();
        svc.Load(path);

        Assert.Equal(MnemonicSetNames.Iec, svc.TargetRevision!.MnemonicSet);
        Assert.True(svc.TargetRevision.MajorRevision >= TargetRevision.FirstIecMajor);

        var rungs = Scan(path, File.ReadAllText(path)).RungTexts;
        Assert.False(MnemonicSetChecker.Check(rungs, svc.TargetRevision).Refused);
        var legacy = new TargetRevisionInfo(35, MnemonicSetNames.Legacy, TargetRevisionSource.MajorRev, null);
        Assert.Equal(MnemonicCheckKinds.WrongMnemonicSet, MnemonicSetChecker.Check(rungs, legacy).Kind);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string SyntheticFixture(string softwareRevision, string rungText, string? majorRev = "")
    {
        var controllerRev = majorRev is null ? string.Empty
            : $" MajorRev=\"{(majorRev.Length > 0 ? majorRev : softwareRevision.Split('.')[0])}\"";
        return $"""
            <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="{softwareRevision}">
              <Controller Name="C"{controllerRev}><Programs><Program Name="P"><Routines><Routine Name="R" Type="RLL"><RLLContent>
                <Rung Number="0" Type="N"><Text><![CDATA[{rungText}]]></Text></Rung>
              </RLLContent></Routine></Routines></Program></Programs></Controller>
            </RSLogix5000Content>
            """;
    }
}
