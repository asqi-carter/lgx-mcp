using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="VerifyInterlockHandler"/> and the
/// <c>logix_verify_interlock</c> MCP tool wired through <see cref="LogixProjectTools"/>.
/// </summary>
public class VerifyInterlockHandlerTests
{
    /// <summary>Demo.L5X: single rung XIC(Cmd_MotorStart)XIC(Safety_OK)OTE(Motor_Run).</summary>
    private static readonly string DemoFixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    /// <summary>
    /// DemoInterlock.L5X: multi-rung interlock scenarios, XIC and XIO polarity cases.
    /// </summary>
    private static readonly string InterlockFixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoInterlock.L5X");

    // ------------------------------------------------------------------ //
    //  Error cases                                                         //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Execute_ReturnsError_WhenNoProjectLoaded()
    {
        var svc    = new ProjectService();
        var result = VerifyInterlockHandler.Execute(svc, "Motor_Run", "Safety_OK");

        Assert.IsType<VerifyInterlockError>(result);
        var e = (VerifyInterlockError)result;
        Assert.Contains("No project loaded", e.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_ReturnsError_WhenOutputTagIsEmpty()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        var result = VerifyInterlockHandler.Execute(svc, "", "Safety_OK");

        Assert.IsType<VerifyInterlockError>(result);
        var e = (VerifyInterlockError)result;
        Assert.Contains("outputTag", e.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_ReturnsError_WhenOutputTagIsWhitespace()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        var result = VerifyInterlockHandler.Execute(svc, "   ", "Safety_OK");

        Assert.IsType<VerifyInterlockError>(result);
    }

    [Fact]
    public void Execute_ReturnsError_WhenConditionTagIsEmpty()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        var result = VerifyInterlockHandler.Execute(svc, "Motor_Run", "");

        Assert.IsType<VerifyInterlockError>(result);
        var e = (VerifyInterlockError)result;
        Assert.Contains("conditionTag", e.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_ReturnsError_WhenConditionTagIsWhitespace()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        var result = VerifyInterlockHandler.Execute(svc, "Motor_Run", "   ");

        Assert.IsType<VerifyInterlockError>(result);
    }

    [Fact]
    public void Execute_ReturnsError_WhenOutputTagNeverWritten()
    {
        var svc = new ProjectService();
        svc.Load(InterlockFixturePath);

        // E_Stop is only used as XIC (read), never as a write destination.
        var result = VerifyInterlockHandler.Execute(svc, "E_Stop", "Safety_OK");

        Assert.IsType<VerifyInterlockError>(result);
        var e = (VerifyInterlockError)result;
        Assert.Contains("E_Stop", e.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_ReturnsError_WhenOutputTagDoesNotExist()
    {
        var svc = new ProjectService();
        svc.Load(InterlockFixturePath);

        var result = VerifyInterlockHandler.Execute(svc, "NoSuchTag", "Safety_OK");

        Assert.IsType<VerifyInterlockError>(result);
    }

    // ------------------------------------------------------------------ //
    //  Demo.L5X — single rung, verified = true                            //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Motor_Run is written once (rung 0 of MainRoutine).
    /// That rung includes XIC(Safety_OK) → verified=true, polarity=XIC.
    /// </summary>
    [Fact]
    public void Execute_Demo_VerifiedTrue_SingleRung_XIC()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        var result = (VerifyInterlockResult)VerifyInterlockHandler.Execute(svc, "Motor_Run", "Safety_OK");

        Assert.True(result.Verified);
        Assert.Equal("Motor_Run",  result.OutputTag);
        Assert.Equal("Safety_OK",  result.ConditionTag);
        Assert.Single(result.Rungs);

        var rung = result.Rungs[0];
        Assert.Equal("MainProgram",  rung.Program);
        Assert.Equal("MainRoutine",  rung.Routine);
        Assert.Equal(0,              rung.RungNumber);
        Assert.True(rung.ConditionPresent);
        Assert.Equal("XIC",          rung.Polarity);
        Assert.Contains("OTE(Motor_Run)", rung.RungText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Condition tag that is not present in the rung: Cmd_MotorStart is on rung 0
    /// as XIC, but if we check Motor_Run against a different condition tag that is
    /// absent from the rung, conditionPresent should be false and verified=false.
    /// </summary>
    [Fact]
    public void Execute_Demo_VerifiedFalse_ConditionAbsent()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        // NonexistentCondition never appears in any rung.
        var result = (VerifyInterlockResult)VerifyInterlockHandler.Execute(svc, "Motor_Run", "NonexistentCondition");

        Assert.False(result.Verified);
        Assert.Single(result.Rungs);
        Assert.False(result.Rungs[0].ConditionPresent);
        Assert.Null(result.Rungs[0].Polarity);
    }

    // ------------------------------------------------------------------ //
    //  DemoInterlock.L5X — multi-rung, verified = false                   //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Motor_Run is written on two rungs (0 and 1 of MainRoutine).
    /// Rung 0 has Safety_OK as XIC; rung 1 does not.
    /// verify_interlock should return verified=false with 2 rung entries.
    /// </summary>
    [Fact]
    public void Execute_Interlock_VerifiedFalse_SecondRungMissingCondition()
    {
        var svc = new ProjectService();
        svc.Load(InterlockFixturePath);

        var result = (VerifyInterlockResult)VerifyInterlockHandler.Execute(svc, "Motor_Run", "Safety_OK");

        Assert.False(result.Verified);
        Assert.Equal(2, result.Rungs.Count);

        var rung0 = result.Rungs.Single(r => r.RungNumber == 0);
        Assert.True(rung0.ConditionPresent);
        Assert.Equal("XIC", rung0.Polarity);

        var rung1 = result.Rungs.Single(r => r.RungNumber == 1);
        Assert.False(rung1.ConditionPresent);
        Assert.Null(rung1.Polarity);
    }

    [Fact]
    public void Execute_Interlock_VerifiedFalse_BothRungs_ProgramRoutineNames()
    {
        var svc = new ProjectService();
        svc.Load(InterlockFixturePath);

        var result = (VerifyInterlockResult)VerifyInterlockHandler.Execute(svc, "Motor_Run", "Safety_OK");

        Assert.All(result.Rungs, r => Assert.Equal("MainProgram",  r.Program));
        Assert.All(result.Rungs, r => Assert.Equal("MainRoutine",  r.Routine));
    }

    [Fact]
    public void Execute_Interlock_VerifiedFalse_RungTextContainsOTE()
    {
        var svc = new ProjectService();
        svc.Load(InterlockFixturePath);

        var result = (VerifyInterlockResult)VerifyInterlockHandler.Execute(svc, "Motor_Run", "Safety_OK");

        Assert.All(result.Rungs, r =>
            Assert.Contains("OTE(Motor_Run)", r.RungText, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ //
    //  DemoInterlock.L5X — single rung, verified = true, XIC polarity     //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Safe_Output is written by exactly one rung (SafeRoutine, rung 0) which
    /// includes XIC(Safety_OK). Result should be verified=true, polarity=XIC.
    /// </summary>
    [Fact]
    public void Execute_Interlock_VerifiedTrue_SafeOutput_XIC()
    {
        var svc = new ProjectService();
        svc.Load(InterlockFixturePath);

        var result = (VerifyInterlockResult)VerifyInterlockHandler.Execute(svc, "Safe_Output", "Safety_OK");

        Assert.True(result.Verified);
        Assert.Single(result.Rungs);

        var rung = result.Rungs[0];
        Assert.Equal("MainProgram", rung.Program);
        Assert.Equal("SafeRoutine", rung.Routine);
        Assert.Equal(0,             rung.RungNumber);
        Assert.True(rung.ConditionPresent);
        Assert.Equal("XIC",         rung.Polarity);
    }

    // ------------------------------------------------------------------ //
    //  DemoInterlock.L5X — single rung, verified = true, XIO polarity     //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Alarm_Light is written by exactly one rung (AlarmRoutine, rung 0) which
    /// includes XIO(Safety_OK). Result should be verified=true, polarity=XIO.
    /// </summary>
    [Fact]
    public void Execute_Interlock_VerifiedTrue_AlarmLight_XIO()
    {
        var svc = new ProjectService();
        svc.Load(InterlockFixturePath);

        var result = (VerifyInterlockResult)VerifyInterlockHandler.Execute(svc, "Alarm_Light", "Safety_OK");

        Assert.True(result.Verified);
        Assert.Single(result.Rungs);

        var rung = result.Rungs[0];
        Assert.Equal("MainProgram",  rung.Program);
        Assert.Equal("AlarmRoutine", rung.Routine);
        Assert.Equal(0,              rung.RungNumber);
        Assert.True(rung.ConditionPresent);
        Assert.Equal("XIO",          rung.Polarity);
    }

    // ------------------------------------------------------------------ //
    //  Case insensitivity                                                  //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Execute_Demo_CaseInsensitive_OutputTag()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        var result = VerifyInterlockHandler.Execute(svc, "MOTOR_RUN", "Safety_OK");

        Assert.IsType<VerifyInterlockResult>(result);
        Assert.True(((VerifyInterlockResult)result).Verified);
    }

    [Fact]
    public void Execute_Demo_CaseInsensitive_ConditionTag()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        var result = VerifyInterlockHandler.Execute(svc, "Motor_Run", "SAFETY_OK");

        Assert.IsType<VerifyInterlockResult>(result);
        Assert.True(((VerifyInterlockResult)result).Verified);
    }

    // ------------------------------------------------------------------ //
    //  JSON serialization                                                  //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Execute_SerializesToValidJson_VerifiedTrue()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        var json = JsonSerializer.Serialize(VerifyInterlockHandler.Execute(svc, "Motor_Run", "Safety_OK"));
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("verified").GetBoolean());
        Assert.Equal("Motor_Run", doc.RootElement.GetProperty("outputTag").GetString());
        Assert.Equal("Safety_OK", doc.RootElement.GetProperty("conditionTag").GetString());

        var rungs = doc.RootElement.GetProperty("rungs");
        Assert.Equal(JsonValueKind.Array, rungs.ValueKind);
        Assert.Equal(1, rungs.GetArrayLength());

        var first = rungs[0];
        Assert.Equal("MainProgram",  first.GetProperty("program").GetString());
        Assert.Equal("MainRoutine",  first.GetProperty("routine").GetString());
        Assert.Equal(0,              first.GetProperty("rungNumber").GetInt32());
        Assert.True(                 first.GetProperty("conditionPresent").GetBoolean());
        Assert.Equal("XIC",          first.GetProperty("polarity").GetString());
        Assert.Contains("OTE(Motor_Run)", first.GetProperty("rungText").GetString(),
                        StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_SerializesToValidJson_VerifiedFalse()
    {
        var svc = new ProjectService();
        svc.Load(InterlockFixturePath);

        var json = JsonSerializer.Serialize(VerifyInterlockHandler.Execute(svc, "Motor_Run", "Safety_OK"));
        var doc  = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.GetProperty("verified").GetBoolean());
        Assert.Equal(2,     doc.RootElement.GetProperty("rungs").GetArrayLength());
    }

    [Fact]
    public void Execute_ErrorSerializesToJsonWithErrorKey()
    {
        var svc    = new ProjectService();
        var result = VerifyInterlockHandler.Execute(svc, "Motor_Run", "Safety_OK");
        var json   = JsonSerializer.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void Execute_SerializesToValidJson_PolarityNullWhenAbsent()
    {
        var svc = new ProjectService();
        svc.Load(InterlockFixturePath);

        var json = JsonSerializer.Serialize(VerifyInterlockHandler.Execute(svc, "Motor_Run", "Safety_OK"));
        var doc  = JsonDocument.Parse(json);

        // Rung 1 has no condition → polarity should be null (JSON null value).
        var rungs = doc.RootElement.GetProperty("rungs").EnumerateArray().ToList();
        var rung1 = rungs.Single(r => r.GetProperty("rungNumber").GetInt32() == 1);
        Assert.Equal(JsonValueKind.Null, rung1.GetProperty("polarity").ValueKind);
    }

    // ------------------------------------------------------------------ //
    //  LogixProjectTools delegation                                         //
    // ------------------------------------------------------------------ //

    [Fact]
    public void LogixProjectTools_VerifyInterlock_ReturnsErrorJsonBeforeOpen()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        var json  = tools.GetVerifyInterlockResponse("Motor_Run", "Safety_OK");

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void LogixProjectTools_VerifyInterlock_ReturnsVerifiedTrueAfterOpen()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        tools.GetOpenProjectResponse(DemoFixturePath);

        var json = tools.GetVerifyInterlockResponse("Motor_Run", "Safety_OK");
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("verified").GetBoolean());
    }

    // ------------------------------------------------------------------ //
    //  Series versus branch (DemoInterlockBranch.L5X, condition = Cond)    //
    // ------------------------------------------------------------------ //

    private static readonly string BranchFixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoInterlockBranch.L5X");

    private static VerifyInterlockResult Branch(string output)
    {
        var svc = new ProjectService();
        svc.Load(BranchFixturePath);
        return Assert.IsType<VerifyInterlockResult>(VerifyInterlockHandler.Execute(svc, output, "Cond"));
    }

    [Fact]
    public void Execute_ContactInSeries_ReturnsVerified()
    {
        var r = Branch("Out_Series");

        Assert.True(r.Verified);
        Assert.Equal("verified", r.Result);
        Assert.Equal("guarded", r.Rungs[0].Status);
        Assert.Null(r.Rungs[0].BypassPaths);
    }

    [Fact]
    public void Execute_ContactInOneBranchLeg_ReturnsBypassed_NamingBypassPath()
    {
        var r = Branch("Out_Bypass");

        Assert.False(r.Verified);
        Assert.Equal("bypassed", r.Result);
        var rung = Assert.Single(r.Rungs);
        Assert.Equal("bypassed", rung.Status);
        Assert.True(rung.ConditionPresent);
        var path = Assert.Single(rung.BypassPaths!);
        Assert.Contains("XIC(Bypass)", path);
        Assert.Contains("leg 2", path);
        Assert.NotNull(r.Note);
    }

    [Fact]
    public void Execute_BypassedResult_IsNeverReportedAsVerified()
    {
        foreach (var output in new[] { "Out_Bypass", "Out_XioBypass", "Out_LegWrite", "Out_EmptyLeg" })
        {
            var r = Branch(output);
            Assert.False(r.Verified, output);
            Assert.NotEqual("verified", r.Result);
        }

        var svc = new ProjectService();
        svc.Load(BranchFixturePath);
        var json = JsonDocument.Parse(new LogixProjectTools(svc).GetVerifyInterlockResponse("Out_Bypass", "Cond"));
        Assert.False(json.RootElement.GetProperty("verified").GetBoolean());
        Assert.Equal("bypassed", json.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public void Execute_XioContact_HandledLikeXic()
    {
        var series = Branch("Out_XioSeries");
        Assert.True(series.Verified);
        Assert.Equal("XIO", series.Rungs[0].Polarity);

        var bypass = Branch("Out_XioBypass");
        Assert.False(bypass.Verified);
        Assert.Equal("bypassed", bypass.Result);
        Assert.Contains("XIC(Bypass)", Assert.Single(bypass.Rungs[0].BypassPaths!));
    }

    [Fact]
    public void Execute_ContactInEveryLegOrBeforeBranch_ReturnsVerified()
    {
        Assert.True(Branch("Out_BothLegs").Verified);
        Assert.True(Branch("Out_Nested").Verified);
    }

    [Fact]
    public void Execute_OutputWrittenInsideUnguardedLeg_ReturnsBypassed()
    {
        var r = Branch("Out_LegWrite");

        Assert.Equal("bypassed", r.Result);
        Assert.Contains("XIC(Bypass)", Assert.Single(r.Rungs[0].BypassPaths!));
    }

    [Fact]
    public void Execute_EmptyBranchLeg_ReturnsBypassed()
    {
        var r = Branch("Out_EmptyLeg");

        Assert.Equal("bypassed", r.Result);
        Assert.Contains("empty wire", Assert.Single(r.Rungs[0].BypassPaths!));
    }

    [Fact]
    public void Execute_ContactAfterOutput_IsNotVerified()
    {
        var r = Branch("Out_After");

        Assert.False(r.Verified);
    }

    // ------------------------------------------------------------------ //
    //  Intermediate-bit chains (DemoInterlockChain.L5X, condition = Cond)  //
    // ------------------------------------------------------------------ //

    private static readonly string ChainFixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoInterlockChain.L5X");

    private static VerifyInterlockResult Chain(string output)
    {
        var svc = new ProjectService();
        svc.Load(ChainFixturePath);
        return Assert.IsType<VerifyInterlockResult>(VerifyInterlockHandler.Execute(svc, output, "Cond"));
    }

    [Fact]
    public void Execute_IntermediateBitChain_ReturnsVerified_WithVia()
    {
        var r = Chain("Drive");

        Assert.True(r.Verified);
        Assert.Equal("verified", r.Result);
        Assert.Equal(new[] { "StartBit" }, r.Via);
        Assert.Equal("chained", Assert.Single(r.Rungs).Status);
        Assert.Equal("Drive <- StartBit <- Cond", Assert.Single(r.Chain!));
    }

    [Fact]
    public void Execute_SecondUninterlockedWriterOfIntermediateBit_IsNotVerified_NamingWriter()
    {
        var r = Chain("DriveBad");

        Assert.False(r.Verified);
        Assert.Equal("unverified", r.Result);
        Assert.Equal("unguarded-writer", r.Reason);
        var w = Assert.Single(r.UnguardedWriters!);
        Assert.Equal("BadBit", w.Tag);
        Assert.Equal(4, w.RungNumber);
        Assert.Contains("XIC(Other)OTE(BadBit)", w.RungText);
    }

    [Fact]
    public void Execute_OtlOtuPair_OnlyOtlWriterNeedsInterlock()
    {
        var r = Chain("DriveLU");

        Assert.True(r.Verified);
        Assert.Equal(new[] { "LatchBit" }, r.Via);
    }

    [Fact]
    public void Execute_DepthThreeChain_IsVerified()
    {
        var r = Chain("Drive3");

        Assert.True(r.Verified);
        Assert.Equal(new[] { "D3A", "D3B", "D3C" }, r.Via);
        Assert.Equal("Drive3 <- D3A <- D3B <- D3C <- Cond", Assert.Single(r.Chain!));
        Assert.Null(r.Reason);
    }

    [Fact]
    public void Execute_DepthFourChain_IsNotVerified_WithDepthLimitReason()
    {
        var r = Chain("Drive4");

        Assert.False(r.Verified);
        Assert.Equal("depth-limit", r.Reason);
        Assert.Equal("Drive4 <- D4A <- D4B <- D4C <- D4D <- Cond", Assert.Single(r.Chain!));
        Assert.Equal(4, r.Via!.Count);

        var svc  = new ProjectService();
        svc.Load(ChainFixturePath);
        var json = JsonDocument.Parse(new LogixProjectTools(svc).GetVerifyInterlockResponse("Drive4", "Cond"));
        Assert.Equal("depth-limit", json.RootElement.GetProperty("reason").GetString());
        Assert.False(json.RootElement.GetProperty("verified").GetBoolean());
    }

    [Fact]
    public void Execute_DirectlyGuardedOutput_HasNoViaOrChain()
    {
        var r = Chain("StartBit");

        Assert.True(r.Verified);
        Assert.Null(r.Via);
        Assert.Null(r.Chain);
    }

    // ------------------------------------------------------------------ //
    //  Wording and reasons (item 10) //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Execute_OtlGuarded_OtuUnguarded_IsVerified_WithSafeDirectionNote_AndListsOtuWriter()
    {
        var r = Chain("OutLU");

        Assert.True(r.Verified);
        Assert.Equal("verified", r.Result);
        Assert.Equal("unlatch writer not interlocked (unlatching is safe-direction)", r.Note);
        var w = Assert.Single(r.UnguardedWriters!);
        Assert.Equal("OutLU", w.Tag);
        Assert.Equal(18, w.RungNumber);
        Assert.Contains("OTU(OutLU)", w.RungText);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void Execute_OtuOfChainBit_IsVerified_WithNote_AndListsOtuWriter()
    {
        var r = Chain("DriveLU");

        Assert.True(r.Verified);
        Assert.Equal("unlatch writer not interlocked (unlatching is safe-direction)", r.Note);
        var w = Assert.Single(r.UnguardedWriters!);
        Assert.Equal("LatchBit", w.Tag);
        Assert.Equal(7, w.RungNumber);
    }

    [Fact]
    public void Execute_ConditionAfterOutput_IsNotVerified_WithConditionAfterOutputReason()
    {
        var r = Chain("OutAfter");

        Assert.False(r.Verified);
        Assert.Equal("unverified", r.Result);
        Assert.Equal("condition-after-output", r.Reason);
        Assert.Equal("condition-after-output", Assert.Single(r.Rungs).Status);
        Assert.Contains("condition-after-output", r.Note);
    }

    [Fact]
    public void Execute_XioOfGuardedBit_CarriesAReason()
    {
        var r = Chain("DriveXio");

        Assert.False(r.Verified);
        Assert.Equal("xio-of-guarded-bit", r.Reason);
        Assert.Contains("XioBit", r.Note);
    }

    [Fact]
    public void Execute_DepthFourChain_ListsOnlyTheChain_NotUnrelatedWriters()
    {
        var r = Chain("Drive4");

        Assert.Equal("depth-limit", r.Reason);
        Assert.Null(r.UnguardedWriters);
        Assert.Single(r.Chain!);
    }
}
