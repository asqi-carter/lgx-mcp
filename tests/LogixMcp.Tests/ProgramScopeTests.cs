namespace LogixMcp.Tests;

/// <summary>
/// Program-scoped tag handling in <c>logix_verify_interlock</c>, <c>logix_cross_reference</c>,
/// <c>logix_find_tags</c> and <c>logix_lint</c>, against <c>fixtures/DemoScopedInterlock.L5X</c>:
/// P1 and P2 each own Gate, Drive and a Run that shadows the controller Run; only P1.Gate is interlocked by Cond.
/// </summary>
public class ProgramScopeTests
{
    private static ProjectService Load()
    {
        var svc = new ProjectService();
        svc.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoScopedInterlock.L5X"));
        return svc;
    }

    private static VerifyInterlockResult Verify(string output, string condition = "Cond") =>
        Assert.IsType<VerifyInterlockResult>(VerifyInterlockHandler.Execute(Load(), output, condition));

    [Fact]
    public void InterlockChain_SameNamedProgramTags_AreKeptApart()
    {
        // P1.Gate is written under Cond; P2's own Gate (written under Other) must not break P1's chain.
        var p1 = Verify(@"\P1.Drive");
        Assert.True(p1.Verified);
        Assert.Equal("P1", Assert.Single(p1.Rungs).Program);
        Assert.Equal(new[] { "Gate" }, p1.Via);
        Assert.Null(p1.UnguardedWriters);

        // P2.Drive is gated by P2.Gate, whose only writer is not interlocked; P1's guarded Gate must not rescue it.
        var p2 = Verify("P2.Drive");
        Assert.False(p2.Verified);
        Assert.Equal("P2", Assert.Single(p2.Rungs).Program);
        var writer = Assert.Single(p2.UnguardedWriters!);
        Assert.Equal("Gate", writer.Tag);
        Assert.Equal("P2", writer.Program);
        Assert.Equal(0, writer.RungNumber);
    }

    [Fact]
    public void VerifyInterlock_FollowsChainThroughProgramQualifiedReference()
    {
        var r = Verify("Lamp");

        Assert.True(r.Verified);
        Assert.Equal("chained", Assert.Single(r.Rungs).Status);
        Assert.Equal(new[] { @"\P1.Gate" }, r.Via);
        Assert.Equal(@"Lamp <- \P1.Gate <- Cond", Assert.Single(r.Chain!));
    }

    [Fact]
    public void VerifyInterlock_ChainThroughReferenceToUninterlockedProgramTag_IsNotVerified()
    {
        var r = Verify("Lamp2");

        Assert.False(r.Verified);
        Assert.Equal("unguarded-writer", r.Reason);
        var writer = Assert.Single(r.UnguardedWriters!);
        Assert.Equal("P2", writer.Program);
        Assert.Equal(@"\P2.Gate", writer.Tag);
    }

    [Fact]
    public void CrossReference_ControllerTag_ExcludesProgramsOwnSameNamedTag()
    {
        var result = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(Load(), "Run"));

        // Only Top's use is the controller Run; P1 and P2 read their own.
        var only = Assert.Single(result.References);
        Assert.Equal("Top", only.Program);
    }

    [Theory]
    [InlineData(@"\P1.Run", "P1")]
    [InlineData("P2.Run", "P2")]
    public void CrossReference_ProgramQualifiedTag_ReturnsOnlyThatProgramsReferences(string name, string program)
    {
        var result = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(Load(), name));

        var only = Assert.Single(result.References);
        Assert.Equal(program, only.Program);
    }

    [Fact]
    public void CrossReference_SameNamedProgramTags_AreReportedPerProgramWhenQualified()
    {
        var p1 = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(Load(), @"\P1.Drive"));
        var p2 = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(Load(), "P2.Drive"));

        Assert.Equal("P1", Assert.Single(p1.References).Program);
        Assert.Equal("P2", Assert.Single(p2.References).Program);
        Assert.All(p1.References.Concat(p2.References), r => Assert.Equal("write", r.Kind));
    }

    [Fact]
    public void AmbiguousTagName_CrossReference_BareNameInTwoPrograms_ListsQualifiedNames()
    {
        var error = Assert.IsType<CrossRefError>(CrossReferenceHandler.Execute(Load(), "Drive"));

        Assert.Equal("AmbiguousTagName", error.Kind);
        Assert.Equal(ErrorKinds.AmbiguousTagName, error.Kind);
        Assert.Equal(new[] { @"\P1.Drive", @"\P2.Drive" }, error.Matches);
        Assert.Contains(@"\P1.Drive", error.Error);
        Assert.Contains(@"\P2.Drive", error.Error);
    }

    [Fact]
    public void AmbiguousTagName_CrossReference_MemberPathOfBareName_KeepsMemberInQualifiedNames()
    {
        var error = Assert.IsType<CrossRefError>(CrossReferenceHandler.Execute(Load(), "Drive.0"));

        Assert.Equal(ErrorKinds.AmbiguousTagName, error.Kind);
        Assert.Equal(new[] { @"\P1.Drive.0", @"\P2.Drive.0" }, error.Matches);
    }

    [Theory]
    [InlineData("Drive", "Cond")]
    [InlineData("Lamp", "Gate")]
    public void AmbiguousTagName_VerifyInterlock_BareNameInTwoPrograms_ListsQualifiedNames(string output, string condition)
    {
        var error = Assert.IsType<VerifyInterlockError>(VerifyInterlockHandler.Execute(Load(), output, condition));

        Assert.Equal(ErrorKinds.AmbiguousTagName, error.Kind);
        var ambiguous = output == "Drive" ? "Drive" : "Gate";
        Assert.Equal(new[] { $@"\P1.{ambiguous}", $@"\P2.{ambiguous}" }, error.Matches);
        Assert.Equal(ambiguous, error.Tag);
    }

    [Fact]
    public void VerifyInterlock_AmbiguousBareConditionName_ReturnsAmbiguousTagName()
    {
        // The output is qualified, but the bare condition 'Gate' exists in P1 and P2: refuse, do not
        // quietly resolve it against the output's program.
        var error = Assert.IsType<VerifyInterlockError>(VerifyInterlockHandler.Execute(Load(), @"\P1.Drive", "Gate"));

        Assert.Equal(ErrorKinds.AmbiguousTagName, error.Kind);
        Assert.Equal("Gate", error.Tag);
        Assert.Equal(new[] { @"\P1.Gate", @"\P2.Gate" }, error.Matches);
        Assert.Contains(@"\P2.Gate", error.Error);

        // A qualified condition resolves.
        Assert.IsType<VerifyInterlockResult>(VerifyInterlockHandler.Execute(Load(), @"\P1.Drive", @"\P1.Gate"));
    }

    [Fact]
    public void AmbiguousTagName_ControllerTagWithSameName_ResolvesWithoutError()
    {
        // The controller declares Run and so do P1 and P2: the bare name is the controller tag.
        Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(Load(), "Run"));
        // Verify may still report "never written", but it must not report the name as ambiguous.
        var verify = VerifyInterlockHandler.Execute(Load(), "Lamp", "Run");
        Assert.Null((verify as VerifyInterlockError)?.Kind);
    }

    [Fact]
    public void AmbiguousTagName_QualifiedNameOrSingleOwner_ResolvesWithoutError()
    {
        Assert.IsType<VerifyInterlockResult>(VerifyInterlockHandler.Execute(Load(), @"\P1.Drive", "Cond"));
        Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(Load(), @"\P2.Gate"));
    }

    [Fact]
    public void FindTags_SameNamedProgramTags_AreKeptApartByScope()
    {
        var result = Assert.IsType<FindTagsResult>(FindTagsHandler.Execute(Load(), "Gate"));

        Assert.Equal(new[] { "P1", "P2" }, result.Tags.Select(t => t.Scope).OrderBy(s => s).ToArray());

        var p2 = Assert.IsType<FindTagsResult>(FindTagsHandler.Execute(Load(), @"\P2.Gate"));
        Assert.Equal("P2", Assert.Single(p2.Tags).Scope);
        // Only Top's \P2.Gate rung is a cross-program reference to P2.Gate (its \P1.Gate rung is P1's tag).
        var cross = Assert.Single(p2.CrossProgramReferences!);
        Assert.Equal("Top", cross.Program);
        Assert.Equal(1, cross.RungNumber);
        Assert.Equal("P2.Gate", cross.Target);
    }

    [Fact]
    public void FindTags_ControllerAndProgramTagsOfTheSameName_DoNotShareUsage()
    {
        var result = Assert.IsType<FindTagsResult>(FindTagsHandler.Execute(Load(), "Run"));

        Assert.Equal(new[] { "Controller", "P1", "P2" }, result.Tags.Select(t => t.Scope).OrderBy(s => s).ToArray());
        Assert.Null(result.Note);   // every Run is used by a parsed rung
    }

    [Fact]
    public void Lint_ProgramThatDoesNotExist_ReturnsProgramNotFound()
    {
        var error = Assert.IsType<LintError>(LintHandler.Execute(Load(), "NoSuchProgram"));

        Assert.Equal("ProgramNotFound", error.Kind);
        Assert.Contains("NoSuchProgram", error.Error);
        Assert.Contains("P1", error.Error);
        Assert.Contains("\"kind\":\"ProgramNotFound\"", LintHandler.Serialize(error));
    }
}
