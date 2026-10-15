namespace LogixMcp.Tests;

/// <summary>
/// The known-instruction set (<see cref="RungReferenceChecker.KnownInstructions"/>) covers
/// the v38 instructions, an unfamiliar name only warns, and what a revision certainly rejects (AWT on v37+) is
/// still refused by the catalogue in <see cref="MnemonicSetChecker"/>.
/// </summary>
public class KnownInstructionSetTests
{
    private static ReferenceSymbols Symbols(params string[] controllerTags) =>
        new("MainProgram",
            new HashSet<string>(controllerTags, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    [Theory]
    [InlineData("ISINF")]
    [InlineData("ISNAN")]
    [InlineData("ATAN2")]
    [InlineData("HMIBC")]
    [InlineData("LV")]
    [InlineData("SATT")]
    [InlineData("ASO")]
    [InlineData("SDET")]
    [InlineData("SASI")]
    [InlineData("SCLF")]
    [InlineData("SCMD")]
    [InlineData("SOVR")]
    public void V38Instruction_IsAccepted_WithoutWarning(string name)
    {
        var result = RungReferenceChecker.Check([$"{name}(a,b);"], Symbols("a", "b"));

        Assert.False(result.Refused);
        Assert.Empty(result.Warnings);
        Assert.Contains(name, RungReferenceChecker.KnownInstructions);
    }

    [Fact]
    public void UnknownFoo_ProducesAWarning_NotARefusal()
    {
        var result = RungReferenceChecker.Check(["XIC(a)FOO(a,b);"], Symbols("a", "b"));

        Assert.False(result.Refused);
        Assert.Null(result.Kind);
        Assert.Contains(result.Warnings, w => w.Contains("FOO", StringComparison.Ordinal));
    }

    [Fact]
    public void FOO_NoSuchTag_B_WarnsAboutInstruction_AndRefusesTheTag()
    {
        var result = RungReferenceChecker.Check(["XIC(a)FOO(NoSuchTag_B);"], Symbols("a"));

        Assert.True(result.Refused);
        Assert.Equal(ReferenceCheckKinds.UndefinedTag, result.Kind);
        Assert.Contains("NoSuchTag_B", result.Message, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.Contains("FOO", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(38)]
    public void Awt_OnV37AndLater_IsStillRefused(int major)
    {
        var target = new TargetRevisionInfo(major, TargetRevision.MnemonicSetFor(major), TargetRevisionSource.MajorRev, null);

        var result = MnemonicSetChecker.Check(["AWT(chan,src,ctl,1,0);"], target);

        Assert.True(result.Refused);
        Assert.Equal(MnemonicCheckKinds.UnsupportedOnRevision, result.Kind);
        Assert.Contains("AWT", result.Message);
    }

    [Fact]
    public void Awt_IsKnown_YetUnsupportedOnV37_SoKnownAndSupportedAreSeparate()
    {
        Assert.Contains("AWT", RungReferenceChecker.KnownInstructions);
    }
}
