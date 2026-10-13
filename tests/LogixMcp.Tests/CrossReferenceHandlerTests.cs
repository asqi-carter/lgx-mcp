using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="CrossReferenceHandler"/> and the
/// <c>logix_cross_reference</c> MCP tool wired through <see cref="LogixProjectTools"/>.
/// </summary>
public class CrossReferenceHandlerTests
{
    private static readonly string DemoFixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    private static readonly string CrossRefFixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoCrossRef.L5X");

    // ------------------------------------------------------------------ //
    //  Error cases                                                         //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Execute_ReturnsError_WhenNoProjectLoaded()
    {
        var svc    = new ProjectService();
        var result = CrossReferenceHandler.Execute(svc, "Cmd_MotorStart");

        Assert.IsType<CrossRefError>(result);
        var e = (CrossRefError)result;
        Assert.Contains("No project loaded", e.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_ReturnsError_WhenTagNameIsEmpty()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        var result = CrossReferenceHandler.Execute(svc, "");

        Assert.IsType<CrossRefError>(result);
    }

    [Fact]
    public void Execute_ReturnsError_WhenTagNameIsWhitespace()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        var result = CrossReferenceHandler.Execute(svc, "   ");

        Assert.IsType<CrossRefError>(result);
    }

    // ------------------------------------------------------------------ //
    //  Demo.L5X — basic read / write classification                        //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Cmd_MotorStart appears in XIC on rung 0 of MainRoutine → kind "read".
    /// </summary>
    [Fact]
    public void Execute_Demo_CmdMotorStart_ReturnsOneReadEntry()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        var result = (CrossRefResult)CrossReferenceHandler.Execute(svc, "Cmd_MotorStart");

        Assert.Single(result.References);
        var entry = result.References[0];
        Assert.Equal("MainProgram",   entry.Program);
        Assert.Equal("MainRoutine",   entry.Routine);
        Assert.Equal(0,               entry.RungNumber);
        Assert.Equal("XIC",           entry.Instruction);
        Assert.Equal("read",          entry.Kind);
    }

    /// <summary>
    /// Motor_Run appears in OTE on rung 0 of MainRoutine → kind "write".
    /// </summary>
    [Fact]
    public void Execute_Demo_MotorRun_ReturnsOneWriteEntry()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        var result = (CrossRefResult)CrossReferenceHandler.Execute(svc, "Motor_Run");

        Assert.Single(result.References);
        var entry = result.References[0];
        Assert.Equal("MainProgram",   entry.Program);
        Assert.Equal("MainRoutine",   entry.Routine);
        Assert.Equal(0,               entry.RungNumber);
        Assert.Equal("OTE",           entry.Instruction);
        Assert.Equal("write",         entry.Kind);
    }

    /// <summary>
    /// Safety_OK appears in XIC on rung 0 → kind "read".
    /// </summary>
    [Fact]
    public void Execute_Demo_SafetyOK_ReturnsOneReadEntry()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        var result = (CrossRefResult)CrossReferenceHandler.Execute(svc, "Safety_OK");

        Assert.Single(result.References);
        Assert.Equal("XIC",   result.References[0].Instruction);
        Assert.Equal("read",  result.References[0].Kind);
    }

    /// <summary>
    /// Tags that exist but are not used in any rung return an empty list.
    /// </summary>
    [Fact]
    public void Execute_Demo_UnusedTag_ReturnsEmptyList()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        var result = (CrossRefResult)CrossReferenceHandler.Execute(svc, "Cmd_SpeedSetpoint");

        Assert.Empty(result.References);
    }

    /// <summary>
    /// A tag that does not exist in the project at all returns an empty list.
    /// </summary>
    [Fact]
    public void Execute_Demo_NonexistentTag_ReturnsEmptyList()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        var result = (CrossRefResult)CrossReferenceHandler.Execute(svc, "NoSuchTag");

        Assert.Empty(result.References);
    }

    /// <summary>
    /// Tag-name comparison is case-insensitive.
    /// </summary>
    [Fact]
    public void Execute_Demo_CaseInsensitive()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);

        var result = (CrossRefResult)CrossReferenceHandler.Execute(svc, "CMD_MOTORSTART");

        Assert.Single(result.References);
        Assert.Equal("read", result.References[0].Kind);
    }

    // ------------------------------------------------------------------ //
    //  DemoCrossRef.L5X — richer instruction coverage                      //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Bit_A appears on rung 0 in both XIC (read) and OTE (write),
    /// rung 1 in OTL (write), and rung 2 in XIC (read), and rung 5 in OTE (write).
    /// </summary>
    [Fact]
    public void Execute_CrossRef_BitA_ReturnsMultipleEntries()
    {
        var svc = new ProjectService();
        svc.Load(CrossRefFixturePath);

        var result   = (CrossRefResult)CrossReferenceHandler.Execute(svc, "Bit_A");
        var refs     = result.References.OrderBy(r => r.RungNumber).ThenBy(r => r.Instruction).ToList();

        // Rung 0: XIC(Bit_A) → read, OTE(Bit_A) → write
        // Rung 1: OTL(Bit_A) → write
        // Rung 2: XIC(Bit_A) → read
        // Rung 5: OTE(Bit_A) → write
        Assert.Equal(5, refs.Count);
        Assert.Contains(refs, r => r.RungNumber == 0 && r.Instruction == "XIC" && r.Kind == "read");
        Assert.Contains(refs, r => r.RungNumber == 0 && r.Instruction == "OTE" && r.Kind == "write");
        Assert.Contains(refs, r => r.RungNumber == 1 && r.Instruction == "OTL" && r.Kind == "write");
        Assert.Contains(refs, r => r.RungNumber == 2 && r.Instruction == "XIC" && r.Kind == "read");
        Assert.Contains(refs, r => r.RungNumber == 5 && r.Instruction == "OTE" && r.Kind == "write");
    }

    /// <summary>
    /// MOV(Real_Y, Real_Z): Real_Y is arg 0 → read; Real_Z is arg 1 → write.
    /// </summary>
    [Fact]
    public void Execute_CrossRef_MOV_SourceIsRead()
    {
        var svc = new ProjectService();
        svc.Load(CrossRefFixturePath);

        var result = (CrossRefResult)CrossReferenceHandler.Execute(svc, "Real_Y");

        // Appears in MOV arg 0 (read) and ADD arg 1 (read)
        Assert.True(result.References.Count >= 1);
        var movRef = result.References.First(r => r.Instruction == "MOV");
        Assert.Equal(3,      movRef.RungNumber);
        Assert.Equal("read", movRef.Kind);
    }

    [Fact]
    public void Execute_CrossRef_MOV_DestinationIsWrite()
    {
        var svc = new ProjectService();
        svc.Load(CrossRefFixturePath);

        var result = (CrossRefResult)CrossReferenceHandler.Execute(svc, "Real_Z");

        // Appears in MOV arg 1 (write) and ADD arg 2 (write)
        var movRef = result.References.First(r => r.Instruction == "MOV");
        Assert.Equal(3,       movRef.RungNumber);
        Assert.Equal("write", movRef.Kind);
    }

    /// <summary>
    /// ADD(Real_X, Real_Y, Real_Z): args 0 and 1 are reads, arg 2 is write.
    /// </summary>
    [Fact]
    public void Execute_CrossRef_ADD_SourceIsRead()
    {
        var svc = new ProjectService();
        svc.Load(CrossRefFixturePath);

        var result = (CrossRefResult)CrossReferenceHandler.Execute(svc, "Real_X");

        var addRef = result.References.Single(r => r.Instruction == "ADD");
        Assert.Equal(4,      addRef.RungNumber);
        Assert.Equal("read", addRef.Kind);
    }

    [Fact]
    public void Execute_CrossRef_ADD_DestinationIsWrite()
    {
        var svc = new ProjectService();
        svc.Load(CrossRefFixturePath);

        var result = (CrossRefResult)CrossReferenceHandler.Execute(svc, "Real_Z");

        var addRef = result.References.First(r => r.Instruction == "ADD");
        Assert.Equal(4,       addRef.RungNumber);
        Assert.Equal("write", addRef.Kind);
    }

    /// <summary>
    /// XIC(Motor.Enable) — looking up base tag "Motor" should match the member-access
    /// argument "Motor.Enable" and classify it as a read.
    /// </summary>
    [Fact]
    public void Execute_CrossRef_MemberAccess_MatchesBaseTag()
    {
        var svc = new ProjectService();
        svc.Load(CrossRefFixturePath);

        var result = (CrossRefResult)CrossReferenceHandler.Execute(svc, "Motor");

        Assert.Single(result.References);
        var entry = result.References[0];
        Assert.Equal(5,      entry.RungNumber);
        Assert.Equal("XIC",  entry.Instruction);
        Assert.Equal("read", entry.Kind);
    }

    /// <summary>
    /// TON(Timer1, ...) — the timer instruction writes its backing structure
    /// (.ACC/.DN/.TT/.EN), so Timer1 is classified "write".
    ///
    /// <para>
    /// This assertion previously expected "unknown" because TON was absent from
    /// the KindMap. That was a defect, not a specification: a tag driven by a
    /// timer escaped the ProgramWritten deny and could be granted write access
    /// by name convention alone. Confirmed against real v36 rung text
    /// (<c>TON(TestTimer,?,?)</c>) on 2026-07-31.
    /// </para>
    /// </summary>
    [Fact]
    public void Execute_CrossRef_TimerInstruction_KindIsWrite()
    {
        var svc = new ProjectService();
        svc.Load(CrossRefFixturePath);

        var result = (CrossRefResult)CrossReferenceHandler.Execute(svc, "Timer1");

        Assert.Single(result.References);
        var entry = result.References[0];
        Assert.Equal(6,       entry.RungNumber);
        Assert.Equal("TON",   entry.Instruction);
        Assert.Equal("write", entry.Kind);
    }

    /// <summary>
    /// A mnemonic absent from the KindMap and from the project's Add-On
    /// Instruction definitions still classifies as "unknown" — the fail-closed
    /// signal the gate engine depends on.
    /// </summary>
    [Fact]
    public void ClassifyKind_UnmappedInstruction_IsUnknown()
    {
        Assert.Equal("unknown", WriteClassifier.ClassifyKind("ZZQ", 0));
        Assert.Equal("unknown", WriteClassifier.ClassifyKind("ZZQ", 3, null));
    }

    // ------------------------------------------------------------------ //
    //  JSON serialization                                                   //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Execute_SerializesToValidJson_WithReferencesArray()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);
        var json = JsonSerializer.Serialize(CrossReferenceHandler.Execute(svc, "Cmd_MotorStart"));
        var doc  = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("references").ValueKind);
        Assert.Equal(1, doc.RootElement.GetProperty("references").GetArrayLength());
        var first = doc.RootElement.GetProperty("references")[0];
        Assert.Equal("MainProgram", first.GetProperty("program").GetString());
        Assert.Equal("MainRoutine", first.GetProperty("routine").GetString());
        Assert.Equal(0,             first.GetProperty("rungNumber").GetInt32());
        Assert.Equal("XIC",         first.GetProperty("instruction").GetString());
        Assert.Equal("read",        first.GetProperty("kind").GetString());
    }

    /// <summary>
    /// logix_cross_reference reads only the opened/uploaded project
    /// file, never the live controller and never a pending edit. The response must
    /// declare this with source: "project-file".
    /// </summary>
    [Fact]
    public void Execute_SerializesWithSourceProjectFile()
    {
        var svc = new ProjectService();
        svc.Load(DemoFixturePath);
        var result = (CrossRefResult)CrossReferenceHandler.Execute(svc, "Cmd_MotorStart");
        Assert.Equal("project-file", result.Source);

        var json = JsonSerializer.Serialize(CrossReferenceHandler.Execute(svc, "Cmd_MotorStart"));
        var doc  = JsonDocument.Parse(json);
        Assert.Equal("project-file", doc.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public void Execute_ErrorSerializesToJsonWithErrorKey()
    {
        var svc    = new ProjectService();
        var result = CrossReferenceHandler.Execute(svc, "AnyTag");
        var json   = JsonSerializer.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    // ------------------------------------------------------------------ //
    //  LogixProjectTools delegation                                         //
    // ------------------------------------------------------------------ //

    [Fact]
    public void LogixProjectTools_CrossReference_ReturnsErrorJsonBeforeOpen()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        var json  = tools.GetCrossReferenceResponse("Motor_Run");

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void LogixProjectTools_CrossReference_ReturnsMatchAfterOpen()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        tools.GetOpenProjectResponse(DemoFixturePath);
        var json = tools.GetCrossReferenceResponse("Motor_Run");

        var doc  = JsonDocument.Parse(json);
        var refs = doc.RootElement.GetProperty("references");
        Assert.Equal(JsonValueKind.Array, refs.ValueKind);
        Assert.Equal(1, refs.GetArrayLength());
        Assert.Equal("write", refs[0].GetProperty("kind").GetString());
    }
}
