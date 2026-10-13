#if LOGIX_SDK
using System.Text.Json;
using System.Xml.Linq;
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// logix-mcp-vazn.5 — a <c>target="workstation"</c> stage is held in the session image, which Logix does not
/// check. The result and the audit say so (<c>stagedIn</c> "session-image", <c>verified</c> false, the fixed note)
/// instead of claiming <c>structural_verify: "online"</c>. The opt-in <c>verifyOnStage</c> replays the fragment
/// offline (here a fake replay) and reports <c>verified: true</c> or the reject reasons.
/// </summary>
public class StageVerifyTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string ExactNote =
        "Online staging is not checked by Logix; the rung is checked when it is published to the controller.";

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> OpenAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession((_, _) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    private static async Task<(ProposeRungEditResult Result, JsonElement Audit)> StageWithAuditAsync(
        LogixProjectSession session, string target, bool verifyOnStage = false)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"audit-stage-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var result = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session,
                policy: null, target: target, verifyOnStage: verifyOnStage));
            var audit = File.ReadAllLines(Path.Combine(tempDir, "audit.jsonl"))
                .Select(l => JsonDocument.Parse(l).RootElement.Clone())
                .First(e => e.GetProperty("event").GetString() == "rung_edit");
            return (result, audit);
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task WorkstationStage_ReportsSessionImageUnverified_WithExactNote_AndNoStructuralVerifyOnline()
    {
        var (session, _) = await OpenAsync();

        var (result, _) = await StageWithAuditAsync(session, "workstation");

        Assert.Equal("session-image", result.StagedIn);
        Assert.False(result.Verified);
        Assert.Equal(ExactNote, result.VerifiedNote);
        Assert.Null(result.VerifyRejectReasons);

        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("structural_verify", json);
        Assert.DoesNotContain("\"online\"", json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("session-image", doc.RootElement.GetProperty("stagedIn").GetString());
        Assert.False(doc.RootElement.GetProperty("verified").GetBoolean());
    }

    [Fact]
    public async Task WorkstationStage_AuditEventUsesStagedInAndVerified_NotStructuralVerify()
    {
        var (session, _) = await OpenAsync();

        var (_, audit) = await StageWithAuditAsync(session, "workstation");

        Assert.Equal("proposed",      audit.GetProperty("outcome").GetString());
        Assert.Equal("workstation",   audit.GetProperty("destination").GetString());
        Assert.Equal("session-image", audit.GetProperty("staged_in").GetString());
        Assert.False(audit.GetProperty("verified").GetBoolean());
        Assert.False(audit.TryGetProperty("structural_verify", out _));
    }

    [Fact]
    public async Task ControllerPublish_DoesNotCarryStagedInOrVerified()
    {
        var (session, _) = await OpenAsync();

        var (result, audit) = await StageWithAuditAsync(session, "controller");

        Assert.Null(result.StagedIn);
        Assert.Null(result.Verified);
        Assert.False(audit.TryGetProperty("staged_in", out _));
        Assert.False(audit.TryGetProperty("verified", out _));
    }

    [Fact]
    public async Task VerifyOnStage_DefaultsToOff_AndDoesNotReplay()
    {
        var (session, _) = await OpenAsync();
        int calls = 0;
        session.StageVerifyReplay = (_, _, _, _, _) => { calls++; return Task.FromResult(new ImportRejectReplayOutcome([], null)); };

        var (result, _) = await StageWithAuditAsync(session, "workstation");

        Assert.Equal(0, calls);
        Assert.False(result.Verified);
    }

    [Fact]
    public async Task VerifyOnStage_FakeReplaySuccess_ReportsVerifiedTrue()
    {
        var (session, _) = await OpenAsync();
        string? replayedXPath = null, fragmentText = null;
        session.StageVerifyReplay = (xpath, _, _, file, _) =>
        {
            replayedXPath = xpath;
            fragmentText  = File.ReadAllText(file);
            return Task.FromResult(new ImportRejectReplayOutcome([], null));
        };

        var (result, audit) = await StageWithAuditAsync(session, "workstation", verifyOnStage: true);

        Assert.Equal(ValidXPath, replayedXPath);
        Assert.Contains("Rung", fragmentText);
        Assert.Equal("session-image", result.StagedIn);
        Assert.True(result.Verified);
        Assert.Null(result.VerifyRejectReasons);
        Assert.NotEqual(ExactNote, result.VerifiedNote);
        Assert.True(audit.GetProperty("verified").GetBoolean());
    }

    private static string TaggedFragment(string rungText, params string[] declaredTags)
    {
        var tags = declaredTags.Length == 0 ? "" :
            "<Tags Use=\"Context\">" +
            string.Concat(declaredTags.Select(t => $"<Tag Name=\"{t}\" TagType=\"Base\" DataType=\"BOOL\" />")) +
            "</Tags>";
        return "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller>" + tags +
            "<Programs><Program Name=\"MainProgram\"><Routines><Routine Name=\"MainRoutine\"><RLLContent>" +
            $"<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[{rungText}]]></Text></Rung>" +
            "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";
    }

    /// <summary>Fake offline replay: fails (TAGNOTFOUND) unless every tag the rung uses is declared before the Programs.</summary>
    private static Task<ImportRejectReplayOutcome> TagCheckingReplay(string file)
    {
        var doc      = XDocument.Parse(File.ReadAllText(file));
        var ctrl     = doc.Root!.Element("Controller")!;
        var programs = ctrl.Element("Programs")!;
        var declared = ctrl.Elements("Tags").Elements("Tag")
            .Where(t => t.Parent!.NodesBeforeSelf().Count() < programs.NodesBeforeSelf().Count())
            .Select(t => (string)t.Attribute("Name")!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var used = System.Text.RegularExpressions.Regex.Matches(
                (string)doc.Descendants("Text").First(), @"\((\w+)\)")
            .Select(m => m.Groups[1].Value);
        var missing = used.Where(u => !declared.Contains(u)).ToList();
        if (missing.Count == 0) return Task.FromResult(new ImportRejectReplayOutcome([], null));
        var log = ImportLogParser.ParseMessage(
            "<Exception Severity=\"Error\" Mnemonic=\"TAGNOTFOUND\" HexCode=\"80042cb3\">" +
            $"<![CDATA[Rung 0: Tag {missing[0]} not found.]]></Exception>");
        return Task.FromResult(new ImportRejectReplayOutcome(ProposeRungEditHandler.ImportLogRejectReasons(log), null));
    }

    [Fact]
    public async Task VerifyOnStage_StagedTagThenRungUsingIt_GivesVerifiedTrue()
    {
        var (session, _) = await OpenAsync();
        session.StageVerifyReplay = (_, _, _, file, _) => TagCheckingReplay(file);

        // Call 1: stage the tag declaration (with a rung that does not use it).
        var first = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, TaggedFragment("NOP();", "StagedA"), session,
            policy: null, target: "workstation", verifyOnStage: true));
        Assert.True(first.Verified);
        Assert.True(session.StagedTagBuffer.ContainsKey("StagedA"));

        // Call 2: a rung that uses the earlier staged tag, with no declaration of its own.
        var second = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 0, TaggedFragment("XIC(StagedA)OTE(StagedA);"), session,
            policy: null, target: "workstation", verifyOnStage: true));

        Assert.True(second.Verified);
        Assert.Null(second.VerifyRejectReasons);
    }

    [Fact]
    public async Task VerifyOnStage_TagDeclaredInSameCallAsRung_GivesVerifiedTrue()
    {
        var (session, _) = await OpenAsync();
        string? replayed = null;
        session.StageVerifyReplay = (_, _, _, file, _) => { replayed = File.ReadAllText(file); return TagCheckingReplay(file); };

        var result = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, TaggedFragment("XIC(SameCall)OTE(SameCall);", "SameCall"), session,
            policy: null, target: "workstation", verifyOnStage: true));

        Assert.True(result.Verified);
        // The tag declaration precedes the rungs in the replay copy.
        Assert.NotNull(replayed);
        Assert.True(replayed!.IndexOf("Name=\"SameCall\"", StringComparison.Ordinal)
                    < replayed.IndexOf("<Rung ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyOnStage_RungUsingUndeclaredTag_StillFails()
    {
        var (session, _) = await OpenAsync();
        session.StageVerifyReplay = (_, _, _, file, _) => TagCheckingReplay(file);

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, TaggedFragment("XIC(Nope)OTE(Nope);"), session,
            policy: null, target: "workstation", verifyOnStage: true);

        Assert.Equal("StageVerifyFailed", Assert.IsType<ProposeRungEditError>(result).Kind);
    }

    [Fact]
    public void VerifyRejectReasons_HasParsedMnemonicAndHex_WithTempPathStripped()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "lgx-replay-abc123", "fragment.L5X");
        var reason = ImportRejectReplay.ToReason(
            "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES (80042c01) - could not import " + tempFile);

        Assert.Equal("XMLSrv_E_IMPORT_ABORTED_NO_CHANGES", reason.Mnemonic);
        Assert.Equal("80042c01", reason.Hex);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('/', '\\'), reason.Text);
        Assert.DoesNotContain("lgx-replay-abc123", reason.Text);
        Assert.Contains("fragment.L5X", reason.Text);
    }

    [Fact]
    public void VerifyRejectReasons_HintComesFromImportLogParser_AndRungIndexIsSet()
    {
        var raw = "<Exception Severity=\"Error\" Mnemonic=\"RxCMP_E_AUDIT_INSTRNOTDEFINED\" HexCode=\"80042cae\">" +
                  "<![CDATA[Rung 2: Instruction FOO is not defined.]]></Exception>";
        var reason = ImportRejectReplay.ToReason(raw);

        Assert.Equal("RxCMP_E_AUDIT_INSTRNOTDEFINED", reason.Mnemonic);
        Assert.Equal("80042cae", reason.Hex);
        Assert.Equal(2, reason.RungIndex);
        Assert.Equal(ImportLogParser.HintFor("RxCMP_E_AUDIT_INSTRNOTDEFINED", "Rung 2: Instruction FOO is not defined."), reason.Hint);
        Assert.False(string.IsNullOrWhiteSpace(reason.Hint));

        // A single-rung fragment whose message names no rung defaults to rung 0.
        Assert.Equal(0, ImportRejectReplay.ToReason("XMLSrv_E_IMPORT_ABORTED_NO_CHANGES - no", fragmentRungCount: 1).RungIndex);
    }

    [Fact]
    public async Task StageVerifyFailed_NamesTheFailingRungIndex_NotTheInsertPosition()
    {
        var (session, _) = await OpenAsync();
        var log = ImportLogParser.ParseMessage(
            "<Exception Severity=\"Error\" Mnemonic=\"RxCMP_E_AUDIT_INSTRNOTDEFINED\" HexCode=\"80042cae\">" +
            "<![CDATA[Rung 2: Instruction FOO is not defined.]]></Exception>");
        var reasons = ProposeRungEditHandler.ImportLogRejectReasons(log);
        session.StageVerifyReplay = (_, _, _, _, _) => Task.FromResult(new ImportRejectReplayOutcome(reasons, null));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 7, 0, ProposeRungEditHandler.MinimalNopFragment, session,
            policy: null, target: "workstation", verifyOnStage: true);

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("StageVerifyFailed", err.Kind);
        Assert.Contains("Rung 2 of the fragment", err.Error);
        Assert.DoesNotContain("The rung at position 7", err.Error);
    }

    private static string ThreeRungFragment(params string[] texts)
    {
        var rungs = string.Concat(texts.Select((t, i) =>
            $"<Rung Number=\"{i}\" Type=\"N\"><Text><![CDATA[{t}]]></Text></Rung>"));
        return "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs><Program Name=\"MainProgram\">" +
               $"<Routines><Routine Name=\"MainRoutine\"><RLLContent>{rungs}</RLLContent></Routine></Routines>" +
               "</Program></Programs></Controller></RSLogix5000Content>";
    }

    // Replay fake: fails (text-only reason, no rung index) when the fragment holds the bad rung.
    private static Task<ImportRejectReplayOutcome> BadRungReplay(string file)
    {
        var xml = File.ReadAllText(file);
        if (!xml.Contains("BAD(", StringComparison.Ordinal))
            return Task.FromResult(new ImportRejectReplayOutcome([], null));
        var reason = ImportRejectReplay.ToReason(
            "RxCMP_E_AUDIT_INSTRNOTDEFINED (80042cae) - Instruction BAD is not defined.");
        return Task.FromResult(new ImportRejectReplayOutcome([reason], null));
    }

    [Fact]
    public async Task BuildPhaseReject_ThreeRungBatchWithOneBadRung_GivesRungIndex1()
    {
        var (session, _) = await OpenAsync();
        var replays = 0;
        session.StageVerifyReplay = (_, _, _, file, _) => { replays++; return BadRungReplay(file); };

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ThreeRungFragment("NOP();", "BAD();", "NOP();"), session,
            policy: null, target: "workstation", verifyOnStage: true);

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("StageVerifyFailed", err.Kind);
        var reason = Assert.Single(err.VerifyRejectReasons!);
        Assert.Equal(1, reason.RungIndex);
        Assert.Equal("RxCMP_E_AUDIT_INSTRNOTDEFINED", reason.Mnemonic);
        Assert.Equal("80042cae", reason.Hex);
        Assert.Equal("offline-replay", err.RejectReasonSource);
        Assert.Contains("Rung 1 of the fragment", err.Error);
        Assert.Equal(1 + 3, replays);   // whole batch, then one replay per rung
    }

    [Fact]
    public async Task BuildPhaseReject_PerRungReplay_IsCappedAtTenRungs()
    {
        var (session, _) = await OpenAsync();
        var replays = 0;
        session.StageVerifyReplay = (_, _, _, file, _) => { replays++; return BadRungReplay(file); };
        var texts = Enumerable.Range(0, 11).Select(i => i == 4 ? "BAD();" : "NOP();").ToArray();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ThreeRungFragment(texts), session,
            policy: null, target: "workstation", verifyOnStage: true);

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Null(Assert.Single(err.VerifyRejectReasons!).RungIndex);
        Assert.Equal(1, replays);   // 11 rungs > cap of 10: no per-rung replay
        Assert.Equal(10, ImportRejectReplay.MaxPerRungReplay);
    }

    [Fact]
    public async Task StageVerifyFailed_PieControllerEditsExist_MapsToPendingEditsBlockImport()
    {
        var (session, _) = await OpenAsync();
        var log = ImportLogParser.ParseMessage(
            "<Exception Severity=\"Error\" Mnemonic=\"XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM\" HexCode=\"80042d00\">" +
            "<![CDATA[Controller edits exist for program.]]></Exception>");
        var reasons = ProposeRungEditHandler.ImportLogRejectReasons(log);
        Assert.NotEmpty(reasons);
        session.StageVerifyReplay = (_, _, _, _, _) => Task.FromResult(new ImportRejectReplayOutcome(reasons, null));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session,
            policy: null, target: "workstation", verifyOnStage: true);

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("PendingEditsBlockImport", err.Kind);
        Assert.DoesNotContain("Fix the rung", err.Hint);
        Assert.NotNull(err.VerifyRejectReasons);
        Assert.Empty(session.StagedRungBuffer);
    }

    [Fact]
    public async Task StageVerifyFailed_DoesNotStageTheRung_AndCarriesVerifyRejectReasonsWithHints()
    {
        var (session, fake) = await OpenAsync();
        var log = ImportLogParser.ParseMessage(
            "<Exception Severity=\"Error\" Mnemonic=\"RxCMP_E_DEPRCATED_INSTR_IN_EXPRESSION\" HexCode=\"80042cb2\">" +
            "<![CDATA[Rung 0: Deprecated instruction SQR in expression.]]></Exception>");
        var reasons = ProposeRungEditHandler.ImportLogRejectReasons(log);
        session.StageVerifyReplay = (_, _, _, _, _) => Task.FromResult(new ImportRejectReplayOutcome(reasons, null));

        var tempDir = Path.Combine(Path.GetTempPath(), $"audit-stage-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var result = await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session,
                policy: null, target: "workstation", verifyOnStage: true);

            var err = Assert.IsType<ProposeRungEditError>(result);
            Assert.Equal("StageVerifyFailed", err.Kind);
            Assert.Contains("NOT staged", err.Error);
            var reason = Assert.Single(err.VerifyRejectReasons!);
            Assert.Equal("RxCMP_E_DEPRCATED_INSTR_IN_EXPRESSION", reason.Mnemonic);
            Assert.Equal("80042cb2", reason.Hex);
            Assert.Equal(0, reason.RungIndex);
            Assert.False(string.IsNullOrWhiteSpace(reason.Text));
            Assert.False(string.IsNullOrWhiteSpace(reason.Hint));

            // Nothing was staged and no import reached the session image.
            Assert.Empty(fake.CapturedXPaths);
            Assert.Empty(session.StagedRungBuffer);
            Assert.Empty(session.StagedTagBuffer);

            var json = JsonSerializer.Serialize(err);
            using var doc = JsonDocument.Parse(json);
            var wire = doc.RootElement.GetProperty("verifyRejectReasons")[0];
            Assert.True(wire.TryGetProperty("mnemonic", out _));
            Assert.True(wire.TryGetProperty("hex", out _));
            Assert.True(wire.TryGetProperty("text", out _));
            Assert.True(wire.TryGetProperty("rungIndex", out _));
            Assert.True(wire.TryGetProperty("hint", out _));

            var audit = File.ReadAllLines(Path.Combine(tempDir, "audit.jsonl"))
                .Select(l => JsonDocument.Parse(l).RootElement.Clone())
                .First(e => e.GetProperty("event").GetString() == "rung_edit");
            Assert.Equal("error", audit.GetProperty("outcome").GetString());
            Assert.False(audit.GetProperty("verified").GetBoolean());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task BuildVerify_AndVerified_AreConsistent_NeverPassedWithVerifiedFalse()
    {
        // Every verifyOnStage outcome that stages a rung: replay passes, replay unavailable, verifyOnStage off.
        var outcomes = new (Func<ImportRejectReplayOutcome?> Make, bool ExpectVerified)[]
        {
            (() => new ImportRejectReplayOutcome([], null), true),
            (() => ImportRejectReplayOutcome.Unavailable("the copy could not be opened"), false),
            (() => null, false),
        };
        foreach (var (make, expectVerified) in outcomes)
        {
            var (session, _) = await OpenAsync();
            var replay = make();
            if (replay is not null)
                session.StageVerifyReplay = (_, _, _, _, _) => Task.FromResult(replay);

            var (result, audit) = await StageWithAuditAsync(session, "workstation", verifyOnStage: replay is not null);

            Assert.Equal(expectVerified, result.Verified);
            if (result.Verified == false)
                Assert.NotEqual("passed", result.BuildVerify);
            Assert.Equal(expectVerified ? "passed" : "passed-excluding-staged", result.BuildVerify);
            if (result.BuildVerify == "passed")
                Assert.True(result.Verified);
            if (audit.TryGetProperty("build_verify", out var bv) && bv.GetString() == "passed")
                Assert.True(audit.GetProperty("verified").GetBoolean());
        }
    }

    [Fact]
    public async Task BuildVerify_AfterAPassedVerifyOnStage_IsNotPassedExcludingStaged()
    {
        var (session, _) = await OpenAsync();
        session.StageVerifyReplay = (_, _, _, _, _) => Task.FromResult(new ImportRejectReplayOutcome([], null));

        var (result, _) = await StageWithAuditAsync(session, "workstation", verifyOnStage: true);

        Assert.True(result.Verified);
        Assert.NotEqual("passed-excluding-staged", result.BuildVerify);
    }

    [Fact]
    public void DeprecatedInstrInExpression_YieldsAHintNamingTheIecFunction()
    {
        var withName = Assert.Single(ImportLogParser.ParseMessage(
            "<Exception Severity=\"Error\" Mnemonic=\"RxCMP_E_DEPRCATED_INSTR_IN_EXPRESSION\" HexCode=\"80042cb2\">" +
            "<![CDATA[Rung 0: Deprecated instruction SQR in expression.]]></Exception>"));
        Assert.Contains("SQR -> SQRT", withName.Hint);
        Assert.DoesNotContain("TRUNC", withName.Hint);

        var withoutName = Assert.Single(ImportLogParser.ParseMessage(
            "<Exception Severity=\"Error\" Mnemonic=\"RxCMP_E_DEPRCATED_INSTR_IN_EXPRESSION\" HexCode=\"80042cb2\">" +
            "<![CDATA[Rung 1: Deprecated instruction in expression.]]></Exception>"));
        Assert.Contains("SQRT", withoutName.Hint);
        Assert.Contains("TRUNC", withoutName.Hint);
    }

    [Fact]
    public async Task VerifyOnStage_ReplayUnavailable_ReportsVerifiedFalse_WithReason()
    {
        var (session, _) = await OpenAsync();
        session.StageVerifyReplay = (_, _, _, _, _) =>
            Task.FromResult(ImportRejectReplayOutcome.Unavailable("the copy could not be opened"));

        var (result, _) = await StageWithAuditAsync(session, "workstation", verifyOnStage: true);

        Assert.False(result.Verified);
        Assert.Contains("the copy could not be opened", result.VerifiedNote);
        Assert.StartsWith(ExactNote, result.VerifiedNote);
    }

    [Fact]
    public async Task VerifyOnStage_IgnoredOnControllerTarget()
    {
        var (session, _) = await OpenAsync();
        int calls = 0;
        session.StageVerifyReplay = (_, _, _, _, _) => { calls++; return Task.FromResult(new ImportRejectReplayOutcome([], null)); };

        var (result, _) = await StageWithAuditAsync(session, "controller", verifyOnStage: true);

        Assert.Equal(0, calls);
        Assert.Null(result.Verified);
        Assert.Equal("ignored: controller publishes are always checked by Logix", result.VerifiedNote);
    }
}
#endif
