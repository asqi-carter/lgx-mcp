#if LOGIX_SDK
using System.Collections.Generic;
using System.Text.Json;
using System.Xml.Linq;
using RockwellAutomation.LogixDesigner;

using PartialImportOption = RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption;
using Task                = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// <c>target="live"</c> on <c>logix_propose_rung_edit</c>: the handler
/// dispatch and the pre-import gates.
/// Live shares the controller fragment handling (tag merge, <c>tagsCreated</c>) but
/// imports with <c>FinalizeEdits</c>; every gate refuses before any SDK import.
/// </summary>
public class ProposeRungEditLiveTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string ValidFragment = ProposeRungEditHandler.MinimalNopFragment;

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)>
        BuildOpenSessionAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    /// <summary>
    /// target="live" imports with FinalizeEdits; target="controller" still uses
    /// AcceptEdits and the default/workstation target still uses LeaveEdits.
    /// </summary>
    [Fact]
    public async Task Live_DispatchesFinalizeEdits_ControllerStillAcceptEdits_WorkstationStillLeaveEdits()
    {
        var (liveSession, liveFake) = await BuildOpenSessionAsync();
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, liveSession, policy: null, target: "live");
        Assert.Equal(new[] { PartialImportOption.FinalizeEdits }, liveFake.CapturedImportOptions);
        Assert.Single(liveFake.CapturedXPaths);
        Assert.Empty(liveFake.CapturedWithTargetXPaths);

        var (ctrlSession, ctrlFake) = await BuildOpenSessionAsync();
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, ctrlSession, policy: null, target: "controller");
        Assert.Equal(new[] { PartialImportOption.AcceptEdits }, ctrlFake.CapturedImportOptions);

        var (wsSession, wsFake) = await BuildOpenSessionAsync();
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, wsSession, policy: null, target: "workstation");
        Assert.Equal(new[] { PartialImportOption.LeaveEdits }, wsFake.CapturedImportOptions);

        var (defSession, defFake) = await BuildOpenSessionAsync();
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, defSession, policy: null);
        Assert.Equal(new[] { PartialImportOption.LeaveEdits }, defFake.CapturedImportOptions);
    }

    /// <summary>
    /// target="live" uses the controller fragment handling: buffered staged tags are
    /// merged into the import and reported in <c>tagsCreated</c>, and the tag is
    /// registered as created-by-session.
    /// </summary>
    [Fact]
    public async Task Live_MergesStagedTags_AndReportsTagsCreated()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        session.StagedTagBuffer["Cmd_Probe_BT21"] = new XElement("Tag",
            new XAttribute("Name", "Cmd_Probe_BT21"), new XAttribute("DataType", "BOOL"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "live");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("live", success.Status);
        Assert.NotNull(success.TagsCreated);
        Assert.Contains(success.TagsCreated!, e =>
            string.Equals(e.TagName, "Cmd_Probe_BT21", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PartialImportOption.FinalizeEdits, Assert.Single(fake.CapturedImportOptions));

        // The staged tag buffer is flushed by the publish, as on the controller path.
        Assert.Empty(session.StagedTagBuffer);
    }

    // ── External Access change refusal ────────────────────────────────────────

    /// <summary>
    /// target="live" with a caller fragment that re-declares a census tag with a different
    /// ExternalAccess is refused with ExternalAccessChangeRefused before any import.
    /// </summary>
    [Fact]
    public async Task Live_ExistingTagExternalAccessChange_Refused_NoImport()
    {
        const string fragment =
            "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller>" +
            "<Tags Use=\"Context\">" +
            "<Tag Name=\"ExistingTag\" DataType=\"BOOL\" ExternalAccess=\"Read/Write\"/>" +
            "<Tag Name=\"NewRo\" DataType=\"BOOL\" ExternalAccess=\"Read Only\"/>" +
            "</Tags><Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\" Type=\"N\">" +
            "<Text><![CDATA[NOP();]]></Text></Rung></RLLContent></Routine>" +
            "</Routines></Program></Programs></Controller></RSLogix5000Content>";

        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = ExportWithRungs(("N", "NOP();"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, fragment, session, policy: null, target: "live",
            preExistingTagNames: new[] { "ExistingTag" },
            preExistingTagAccess: new Dictionary<string, string> { ["ExistingTag"] = "Read Only" });

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("ExternalAccessChangeRefused", error.Kind);
        Assert.Contains("ExistingTag", error.Error);
        Assert.Contains("Read/Write", error.Error);
        Assert.Contains("Read Only", error.Error);
        Assert.Contains("External Access is the engineer's write grant; change it in Logix Designer", error.Error);
        Assert.Empty(fake.CapturedImportOptions);
        Assert.Empty(fake.CapturedXPaths);
        Assert.Empty(fake.CapturedWithTargetXPaths);
    }

    // ── Gate helpers ──────────────────────────────────────────────────────────

    private static Capabilities AllEnabled(PublishMode mode = PublishMode.Confirm) =>
        new(LiveWritesEnabled: true, PublishMode: mode, LiveFinalizeEnabled: true);

    private static LogixSdkTools ToolsWith(Capabilities caps) =>
        new(new ProjectService(), new TagSessionService(), caps, new StudioConfirmWait());

    /// <summary>Export skeleton for MainRoutine carrying the supplied (type, text) rungs.</summary>
    private static string ExportWithRungs(params (string Type, string Text)[] rungs)
    {
        var body = string.Concat(rungs.Select((r, i) =>
            $"<Rung Number=\"{i}\" Type=\"{r.Type}\"><Text><![CDATA[{r.Text}]]></Text></Rung>"));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
            "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" " +
            "ContainsContext=\"true\"><Controller Name=\"DemoController\">" +
            "<Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" + body +
            "</RLLContent></Routine></Routines></Program></Programs>" +
            "</Controller></RSLogix5000Content>";
    }

    private static string KindOf(object result) =>
        Assert.IsType<ProposeRungEditError>(result).Kind ?? string.Empty;

    // ── Gate 1: capability flag ───────────────────────────────────────────────

    /// <summary>
    /// Without LGX_ENABLE_LIVE_FINALIZE, target="live" is refused with
    /// CapabilityDisabled (naming the flag and target="controller" as the pending
    /// alternative) before any session work or import — even with every other
    /// capability on and confirmedByUser=true.
    /// </summary>
    [Fact]
    public async Task Live_RefusedWithoutFlag_CapabilityDisabled_NoImport()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var tools = ToolsWith(new Capabilities(
            LiveWritesEnabled: true, PublishMode: PublishMode.Auto, LiveFinalizeEnabled: false));

        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, ValidFragment, session: session,
            target: "live", confirmedByUser: true);

        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("CapabilityDisabled", root.GetProperty("kind").GetString());
        var error = root.GetProperty("error").GetString()!;
        Assert.Contains("LGX_ENABLE_LIVE_FINALIZE", error);
        Assert.Contains("target='controller'", error);
        Assert.Contains("LGX_ENABLE_LIVE_FINALIZE", root.GetProperty("hint").GetString());

        Assert.Empty(fake.CapturedImportOptions);
        Assert.Empty(fake.CapturedXPaths);
        Assert.Empty(fake.CapturedWithTargetXPaths);
        Assert.Equal(0, fake.ExportCallCount);
    }

    // ── Gate 2: confirmedByUser ───────────────────────────────────────────────

    /// <summary>
    /// target="live" needs confirmedByUser=true even when LGX_PUBLISH_MODE=auto
    /// (where target="controller" does not); with it, the call proceeds and imports
    /// with FinalizeEdits.
    /// </summary>
    [Fact]
    public async Task Live_RequiresConfirmedByUser_EvenInAutoPublishMode()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var tools = ToolsWith(AllEnabled(PublishMode.Auto));

        var refused = await tools.GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, ValidFragment, session: session,
            target: "live", confirmedByUser: false);

        var root = JsonDocument.Parse(refused).RootElement;
        Assert.Equal("ConfirmationRequired", root.GetProperty("kind").GetString());
        Assert.Equal(0, root.GetProperty("stagedRungCount").GetInt32());
        Assert.Empty(fake.CapturedImportOptions);
        Assert.Equal(0, fake.ExportCallCount);

        // Control: target="controller" in Auto mode is NOT gated on confirmation.
        var (ctrlSession, ctrlFake) = await BuildOpenSessionAsync();
        var ctrlJson = await tools.GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, ValidFragment, session: ctrlSession,
            target: "controller", confirmedByUser: false);
        Assert.DoesNotContain("ConfirmationRequired", ctrlJson);
        Assert.Single(ctrlFake.CapturedImportOptions);

        // With confirmedByUser=true, live proceeds.
        var accepted = await tools.GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, ValidFragment, session: session,
            target: "live", confirmedByUser: true);
        Assert.Equal("live", JsonDocument.Parse(accepted).RootElement.GetProperty("status").GetString());
        Assert.Equal(new[] { PartialImportOption.FinalizeEdits }, fake.CapturedImportOptions);
    }

    // ── Gate 3: staged rung buffer ────────────────────────────────────────────

    /// <summary>
    /// A non-empty staged rung buffer refuses target="live" with StagedRungsPresent
    /// before any import; staged tags alone do not (see
    /// <see cref="Live_MergesStagedTags_AndReportsTagsCreated"/>).
    /// </summary>
    [Fact]
    public async Task Live_RefusedWhenStagedRungsPresent()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        session.StagedRungCount     = 1;
        session.HasStagedRungEdit   = true;
        session.StagedRungBuffer[(ValidXPath, 0u)] = new List<string> { "XIC(A)OTE(B);" };

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "live");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("StagedRungsPresent", error.Kind);
        Assert.Contains("staged rung", error.Error);
        Assert.Contains("target='controller'", error.Hint);
        Assert.Empty(fake.CapturedImportOptions);
        Assert.Empty(fake.CapturedXPaths);
        // The staged state is untouched by the refusal.
        Assert.Equal(1, session.StagedRungCount);
    }

    // ── Gate 4: pending zone ──────────────────────────────────────────────────

    /// <summary>
    /// A pending edit zone in the routine's program refuses target="live" with
    /// PendingZoneExists, listing the pending rungs and naming both resolution tools
    /// (and the Studio-automation flag only as their precondition).
    /// </summary>
    [Fact]
    public async Task Live_RefusedWhenPendingZoneExists_NamesAcceptAndCancelTools()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = ExportWithRungs(("N", "NOP();"), ("I", "XIC(PendingProbe)OTE(PendingOut);"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "live");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("PendingZoneExists", error.Kind);
        Assert.Contains("XIC(PendingProbe)OTE(PendingOut);", error.Error);
        Assert.Contains("logix_accept_pending_edits", error.Hint);
        Assert.Contains("logix_cancel_pending_edits", error.Hint);
        Assert.Contains("LGX_ENABLE_STUDIO_AUTOMATION", error.Hint);
        Assert.Empty(fake.CapturedImportOptions);
        Assert.Empty(fake.CapturedXPaths);
    }

    // ── Gate 5: stale-target check ────────────────────────────────────────────

    /// <summary>
    /// replaceCount &gt; 0 requires expectedRungText with one entry per replaced rung;
    /// a pure insert does not.
    /// </summary>
    [Fact]
    public async Task Live_Replace_RequiresExpectedRungText()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var missing = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 1, ValidFragment, session, policy: null, target: "live");
        Assert.Equal("ExpectedRungTextRequired", KindOf(missing));

        var wrongCount = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 1, ValidFragment, session, policy: null, target: "live",
            expectedRungText: new[] { "NOP();", "NOP();" });
        Assert.Equal("ExpectedRungTextRequired", KindOf(wrongCount));

        Assert.Empty(fake.CapturedImportOptions);

        // Pure insert: the parameter is optional.
        var insert = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "live");
        Assert.IsType<ProposeRungEditResult>(insert);
        Assert.Single(fake.CapturedImportOptions);
    }

    /// <summary>
    /// A mismatch between expectedRungText and a fresh export is refused with
    /// StaleTarget giving both texts and no import; a match (after the same
    /// normalization as replacedRungTextsThisCall) proceeds with FinalizeEdits.
    /// </summary>
    [Fact]
    public async Task Live_Replace_StaleTarget_RefusedWithBothTexts()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = ExportWithRungs(("N", "XIC(Now)OTE(Out);"), ("N", "NOP();"));

        var stale = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 1, ValidFragment, session, policy: null, target: "live",
            expectedRungText: new[] { "XIC(Before)OTE(Out);" });

        var error = Assert.IsType<ProposeRungEditError>(stale);
        Assert.Equal("StaleTarget", error.Kind);
        Assert.Contains("XIC(Before)OTE(Out);", error.Error);   // what the caller expected
        Assert.Contains("XIC(Now)OTE(Out);", error.Error);      // what the controller has now
        Assert.Empty(fake.CapturedImportOptions);
        Assert.Empty(fake.CapturedXPaths);

        // Match, with differing whitespace/line endings: normalized identically.
        var ok = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 1, ValidFragment, session, policy: null, target: "live",
            expectedRungText: new[] { "  XIC(Now)OTE(Out);\r\n" });
        Assert.IsType<ProposeRungEditResult>(ok);
        Assert.Equal(new[] { PartialImportOption.FinalizeEdits }, fake.CapturedImportOptions);

        // Replacing a rung that does not exist is stale too.
        var (s2, f2) = await BuildOpenSessionAsync();
        var beyond = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 5, 1, ValidFragment, s2, policy: null, target: "live",
            expectedRungText: new[] { "NOP();" });
        Assert.Equal("StaleTarget", KindOf(beyond));
        Assert.Empty(f2.CapturedImportOptions);
    }

    // ── Comment preservation ────────────────────────────────

    private static string ExportWithCommentedRung(string text, string comment) =>
        ExportWithRungs(("N", text)).Replace(
            "<Text>", $"<Comment><![CDATA[{comment}]]></Comment><Text>");

    /// <summary>
    /// A fragment rung with no &lt;Comment&gt; inherits the comment of the rung it
    /// replaces; the result lists the carried-over rung numbers in commentsPreserved.
    /// </summary>
    [Fact]
    public async Task Live_Replace_PreservesExistingComment_WhenFragmentHasNone()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = ExportWithCommentedRung("XIC(Now)OTE(Out);", "Keep me");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 1, ValidFragment, session, policy: null, target: "live",
            expectedRungText: new[] { "XIC(Now)OTE(Out);" });

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal(new[] { 0 }, success.CommentsPreserved);

        var imported = XDocument.Parse(fake.LastCapturedXmlContent!);
        var rung = Assert.Single(imported.Descendants("Rung"));
        Assert.Equal("Keep me", rung.Element("Comment")?.Value);
        Assert.Equal("NOP();", rung.Element("Text")?.Value);
        Assert.Equal("Comment", rung.Elements().First().Name.LocalName);
        Assert.Contains("\"commentsPreserved\":[0]", JsonSerializer.Serialize(success));
    }

    /// <summary>
    /// A fragment rung that carries its own &lt;Comment&gt; is not overwritten and is
    /// not reported in commentsPreserved.
    /// </summary>
    [Fact]
    public async Task Live_Replace_FragmentOwnComment_IsNotOverwritten()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = ExportWithCommentedRung("XIC(Now)OTE(Out);", "Old comment");
        var fragment = ValidFragment.Replace(
            "<Text>", "<Comment><![CDATA[New comment]]></Comment><Text>");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 1, fragment, session, policy: null, target: "live",
            expectedRungText: new[] { "XIC(Now)OTE(Out);" });

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Null(success.CommentsPreserved);
        var imported = XDocument.Parse(fake.LastCapturedXmlContent!);
        var rung = Assert.Single(imported.Descendants("Rung"));
        Assert.Equal("New comment", rung.Element("Comment")?.Value);
        Assert.Single(rung.Elements("Comment"));
    }

    // ── Post-import verification, live result, audit ─────────

    private static readonly string DistinctFragment =
        ValidFragment.Replace("NOP();", "XIC(LiveIn)OTE(LiveOut);");

    private static JsonElement ReadAuditEvent(string dir, string eventName) =>
        File.ReadAllLines(Path.Combine(dir, "audit.jsonl"))
            .Select(l => JsonDocument.Parse(l).RootElement)
            .First(e => e.TryGetProperty("event", out var ev) && ev.GetString() == eventName);

    /// <summary>
    /// A verified live import returns status "live" with finalized=true and the
    /// after-rungs (all normal, the imported text at insertPosition), and writes an
    /// edits_finalized audit event.
    /// </summary>
    [Fact]
    public async Task Live_Success_ReturnsStatusLiveFinalizedTrue_AndAudits()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"audit-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var (session, fake) = await BuildOpenSessionAsync();

            var result = await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 1, 0, DistinctFragment, session, policy: null, target: "live");

            var success = Assert.IsType<ProposeRungEditResult>(result);
            Assert.Equal("live", success.Status);
            Assert.True(success.Finalized);
            Assert.Equal("verified", success.Verification);
            Assert.NotNull(success.AfterRungs);
            Assert.Equal(2, success.AfterRungs!.Count);
            Assert.All(success.AfterRungs, r => Assert.Equal("N", r.Type));
            Assert.Equal("XIC(LiveIn)OTE(LiveOut);", success.AfterRungs[1].Text);
            Assert.Equal(PartialImportOption.FinalizeEdits, Assert.Single(fake.CapturedImportOptions));

            var json = JsonSerializer.Serialize(success);
            Assert.Contains("\"finalized\":true", json);
            Assert.Contains("\"afterRungs\"", json);

            var audit = ReadAuditEvent(tempDir, "edits_finalized");
            Assert.Equal("MainProgram", audit.GetProperty("program").GetString());
            Assert.Equal("MainRoutine", audit.GetProperty("routine").GetString());
            Assert.Equal("finalized", audit.GetProperty("outcome").GetString());
            Assert.Equal("none", audit.GetProperty("after_state").GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// The SDK call succeeded but the after-state is not clean (imported rung not
    /// there, or a rung still pending): LiveFinalizeNotConfirmed carrying both states,
    /// never status "live", and no "finalized" audit outcome.
    /// </summary>
    [Fact]
    public async Task Live_AfterStateNotClean_IsLiveFinalizeNotConfirmed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"audit-live-nc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            // (a) the import is not reflected in the after-state.
            var (session, fake) = await BuildOpenSessionAsync();
            fake.ReflectFinalizedImportsInExport = false;

            var missing = await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, DistinctFragment, session, policy: null, target: "live");

            // The import returned without error but none of its rungs are in the routine.
            var error = Assert.IsType<ProposeRungEditError>(missing);
            Assert.Equal("ImportAddedNoRungs", error.Kind);
            Assert.NotNull(error.BeforeRungs);
            Assert.NotNull(error.AfterRungs);
            Assert.Contains("NOP();", error.Error);
            Assert.Single(fake.CapturedImportOptions);   // the SDK call did happen

            // (b) a rung is left pending after the import.
            var (session2, fake2) = await BuildOpenSessionAsync();
            fake2.PostFinalizeExportContent = ExportWithRungs(
                ("N", "NOP();"), ("I", "XIC(LiveIn)OTE(LiveOut);"));

            var pending = await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 1, 0, DistinctFragment, session2, policy: null, target: "live");

            var pendingError = Assert.IsType<ProposeRungEditError>(pending);
            Assert.Equal("LiveFinalizeNotConfirmed", pendingError.Kind);
            Assert.Contains("1:I", pendingError.Error);

            var lines = File.ReadAllLines(Path.Combine(tempDir, "audit.jsonl"))
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(e => e.TryGetProperty("event", out var ev) && ev.GetString() == "edits_finalized")
                .ToList();
            Assert.DoesNotContain(lines, e => e.GetProperty("outcome").GetString() == "finalized");
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// When the SDK refuses the import (e.g. an edit zone appeared in between), its
    /// mnemonic is returned in the error and no live result is produced.
    /// </summary>
    [Fact]
    public async Task Live_SdkRefusal_ReturnsSdkMnemonic()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ThrowOnImport = new OperationFailedException(
            "XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM - The program contains accepted " +
            "logic edits which must be accepted or cancelled before Import is permitted online.",
            ValidXPath);

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "live");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Contains("XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM", error.Error);
    }

    // ── Live rung delete ───────────────────────────

    private static readonly IReadOnlyList<RungInput> NoRungs = Array.Empty<RungInput>();

    private static string ThreeRungExport() =>
        ExportWithRungs(("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"), ("N", "XIC(C)OTE(OutC);"));

    /// <summary>
    /// Explicit empty rungs + replaceCount on target="live" imports an empty RLLContent with
    /// FinalizeEdits and the caller's replaceCount UNCHANGED (not forced to 0); the routine
    /// then has before - replaceCount rungs with the following rungs shifted up.
    /// </summary>
    [Fact]
    public async Task LiveDelete_EmptyRungsWithReplaceCount_ImportsFinalizeWithCallerReplaceCount()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = ThreeRungExport();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 1, null, session, policy: null, target: "live",
            expectedRungText: new[] { "XIC(B)OTE(OutB);" }, rungs: NoRungs);

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("live", success.Status);
        Assert.Equal(1u, success.ReplaceCount);
        Assert.Null(success.StagedReplaceCount);
        Assert.Equal(PartialImportOption.FinalizeEdits, Assert.Single(fake.CapturedImportOptions));
        Assert.Equal(1u, Assert.Single(fake.CapturedReplaceCounts));
        Assert.Equal(1u, Assert.Single(fake.CapturedInsertPositions));

        var imported = XDocument.Parse(fake.LastCapturedXmlContent!);
        Assert.Empty(imported.Descendants("Rung"));
        Assert.NotNull(imported.Descendants("RLLContent").SingleOrDefault());

        Assert.NotNull(success.AfterRungs);
        Assert.Equal(
            new[] { "XIC(A)OTE(OutA);", "XIC(C)OTE(OutC);" },
            success.AfterRungs!.Select(r => r.Text).ToArray());
        Assert.All(success.AfterRungs, r => Assert.Equal("N", r.Type));

        // Two rungs at once.
        var (s2, f2) = await BuildOpenSessionAsync();
        f2.ExportContent = ThreeRungExport();
        var two = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 2, null, s2, policy: null, target: "live",
            expectedRungText: new[] { "XIC(A)OTE(OutA);", "XIC(B)OTE(OutB);" }, rungs: NoRungs);
        var twoOk = Assert.IsType<ProposeRungEditResult>(two);
        Assert.Equal(2u, Assert.Single(f2.CapturedReplaceCounts));
        Assert.Equal("XIC(C)OTE(OutC);", Assert.Single(twoOk.AfterRungs!).Text);
    }

    /// <summary>
    /// A live delete needs expectedRungText with exactly one entry per deleted rung;
    /// omitted rungs (null) with replaceCount &gt; 0 is not a delete and is refused too.
    /// </summary>
    [Fact]
    public async Task LiveDelete_RequiresExpectedRungText_PerDeletedRung()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = ThreeRungExport();

        var missing = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 2, null, session, policy: null, target: "live", rungs: NoRungs);
        Assert.Equal("ExpectedRungTextRequired", KindOf(missing));

        var tooFew = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 2, null, session, policy: null, target: "live",
            expectedRungText: new[] { "XIC(A)OTE(OutA);" }, rungs: NoRungs);
        Assert.Equal("ExpectedRungTextRequired", KindOf(tooFew));

        // Omitted rungs is not a delete: refused, never a silent no-op import.
        var omitted = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 1, null, session, policy: null, target: "live",
            expectedRungText: new[] { "XIC(A)OTE(OutA);" });
        Assert.Equal("DeleteNeedsExplicitEmptyRungs", KindOf(omitted));

        Assert.Empty(fake.CapturedImportOptions);
    }

    /// <summary>A delete whose expectedRungText no longer matches a fresh export is refused (StaleTarget), no import.</summary>
    [Fact]
    public async Task LiveDelete_StaleTarget_Refused()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = ThreeRungExport();

        var stale = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 1, null, session, policy: null, target: "live",
            expectedRungText: new[] { "XIC(Old)OTE(OutB);" }, rungs: NoRungs);

        var error = Assert.IsType<ProposeRungEditError>(stale);
        Assert.Equal("StaleTarget", error.Kind);
        Assert.Contains("XIC(Old)OTE(OutB);", error.Error);
        Assert.Contains("XIC(B)OTE(OutB);", error.Error);
        Assert.Empty(fake.CapturedImportOptions);

        // Deleting beyond the end of the routine is stale as well.
        var beyond = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 2, 2, null, session, policy: null, target: "live",
            expectedRungText: new[] { "XIC(C)OTE(OutC);", "NOP();" }, rungs: NoRungs);
        Assert.Equal("StaleTarget", KindOf(beyond));
        Assert.Empty(fake.CapturedImportOptions);
    }

    /// <summary>
    /// When the re-exported routine does not have before - replaceCount rungs (or the
    /// following rungs did not shift up unchanged) the result is LiveFinalizeNotConfirmed,
    /// never status "live".
    /// </summary>
    [Fact]
    public async Task LiveDelete_AfterRungCountWrong_IsLiveFinalizeNotConfirmed()
    {
        // (a) the delete is not reflected: the routine still has all three rungs.
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = ThreeRungExport();
        fake.ReflectFinalizedImportsInExport = false;

        var unchanged = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 1, null, session, policy: null, target: "live",
            expectedRungText: new[] { "XIC(B)OTE(OutB);" }, rungs: NoRungs);
        var error = Assert.IsType<ProposeRungEditError>(unchanged);
        Assert.Equal("LiveFinalizeNotConfirmed", error.Kind);
        Assert.Contains("expected 2 rung(s)", error.Error);
        Assert.NotNull(error.BeforeRungs);
        Assert.NotNull(error.AfterRungs);
        Assert.Single(fake.CapturedImportOptions);   // the SDK call did happen

        // (b) right count, but the wrong rung is gone (following rungs did not shift as expected).
        var (session2, fake2) = await BuildOpenSessionAsync();
        fake2.ExportContent = ThreeRungExport();
        fake2.PostFinalizeExportContent = ExportWithRungs(("N", "XIC(B)OTE(OutB);"), ("N", "XIC(C)OTE(OutC);"));
        var wrongRung = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 1, null, session2, policy: null, target: "live",
            expectedRungText: new[] { "XIC(B)OTE(OutB);" }, rungs: NoRungs);
        Assert.Equal("LiveFinalizeNotConfirmed", KindOf(wrongRung));

        // (c) right count, but a rung is left pending.
        var (session3, fake3) = await BuildOpenSessionAsync();
        fake3.ExportContent = ThreeRungExport();
        fake3.PostFinalizeExportContent = ExportWithRungs(("N", "XIC(A)OTE(OutA);"), ("D", "XIC(C)OTE(OutC);"));
        var pending = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 1, null, session3, policy: null, target: "live",
            expectedRungText: new[] { "XIC(B)OTE(OutB);" }, rungs: NoRungs);
        Assert.Equal("LiveFinalizeNotConfirmed", KindOf(pending));
    }

    /// <summary>
    /// A verified delete returns deletedRungs [{number,text,comment}] (comment from the
    /// pre-import export) and audits edits_finalized with operation "delete".
    /// </summary>
    [Fact]
    public async Task LiveDelete_ReturnsDeletedRungsWithComments_AndAudits()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"audit-live-del-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var (session, fake) = await BuildOpenSessionAsync();
            fake.ExportContent = ThreeRungExport().Replace(
                "<Text><![CDATA[XIC(B)OTE(OutB);]]>",
                "<Comment><![CDATA[Rung B comment]]></Comment><Text><![CDATA[XIC(B)OTE(OutB);]]>");

            var result = await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 1, 2, null, session, policy: null, target: "live",
                expectedRungText: new[] { "XIC(B)OTE(OutB);", "XIC(C)OTE(OutC);" }, rungs: NoRungs);

            var success = Assert.IsType<ProposeRungEditResult>(result);
            Assert.Equal("live", success.Status);
            Assert.Equal("verified", success.Verification);
            Assert.NotNull(success.DeletedRungs);
            Assert.Equal(2, success.DeletedRungs!.Count);
            Assert.Equal(new DeletedRungEntry(1, "XIC(B)OTE(OutB);", "Rung B comment"), success.DeletedRungs[0]);
            Assert.Equal(new DeletedRungEntry(2, "XIC(C)OTE(OutC);", null), success.DeletedRungs[1]);
            Assert.Equal("XIC(A)OTE(OutA);", Assert.Single(success.AfterRungs!).Text);

            var json = JsonSerializer.Serialize(success);
            Assert.Contains("\"deletedRungs\":[{\"number\":1,\"text\":\"XIC(B)OTE(OutB);\",\"comment\":\"Rung B comment\"}", json);
            Assert.Contains("\"afterRungs\"", json);

            var audit = ReadAuditEvent(tempDir, "edits_finalized");
            Assert.Equal("delete", audit.GetProperty("operation").GetString());
            Assert.Equal("finalized", audit.GetProperty("outcome").GetString());
            Assert.Equal("MainRoutine", audit.GetProperty("routine").GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// The tool layer keeps an explicit empty rungs list distinct from omitted rungs:
    /// with every live gate satisfied the delete imports; tags next to a delete are refused.
    /// </summary>
    [Fact]
    public async Task LiveDelete_ThroughToolLayer_EmptyRungsArrayDeletes_TagsRefused()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = ThreeRungExport();
        var tools = ToolsWith(AllEnabled());

        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath, 2, 1, null, session: session, target: "live", confirmedByUser: true,
            expectedRungText: new[] { "XIC(C)OTE(OutC);" }, rungs: Array.Empty<RungInput>());
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("live", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("deletedRungs").GetArrayLength());
        Assert.Equal(1u, Assert.Single(fake.CapturedReplaceCounts));

        var (s2, f2) = await BuildOpenSessionAsync();
        f2.ExportContent = ThreeRungExport();
        var withTags = await tools.GetProposeRungEditResponseAsync(
            ValidXPath, 2, 1, null, session: s2, target: "live", confirmedByUser: true,
            expectedRungText: new[] { "XIC(C)OTE(OutC);" }, rungs: Array.Empty<RungInput>(),
            tags: new[] { new TagInput("T1", "BOOL") });
        Assert.Equal("DeleteWithTags", JsonDocument.Parse(withTags).RootElement.GetProperty("kind").GetString());
        Assert.Empty(f2.CapturedImportOptions);
    }

    /// <summary>
    /// Empty rungs with replaceCount &gt; 0 on controller/workstation (handler and tool layer)
    /// is refused with kind DeleteNeedsLive and never imports.
    /// </summary>
    [Fact]
    public async Task EmptyRungs_ControllerOrWorkstation_DeleteNeedsLive()
    {
        foreach (var target in new[] { "controller", "workstation" })
        {
            var (session, fake) = await BuildOpenSessionAsync();
            fake.ExportContent = ThreeRungExport();

            var result = await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 2, 1, null, session, policy: null, target: target,
                confirmedByUser: true, rungs: Array.Empty<RungInput>());
            var error = Assert.IsType<ProposeRungEditError>(result);
            Assert.Equal("DeleteNeedsLive", error.Kind);
            Assert.Empty(fake.CapturedImportOptions);

            var (s2, f2) = await BuildOpenSessionAsync();
            f2.ExportContent = ThreeRungExport();
            var json = await ToolsWith(AllEnabled()).GetProposeRungEditResponseAsync(
                ValidXPath, 2, 1, null, session: s2, target: target, confirmedByUser: true,
                rungs: Array.Empty<RungInput>());
            Assert.Equal("DeleteNeedsLive",
                JsonDocument.Parse(json).RootElement.GetProperty("kind").GetString());
            Assert.Empty(f2.CapturedImportOptions);
        }
    }

    /// <summary>
    /// This change behaviour kept: a target="controller" publish with no fragment and a
    /// non-zero replaceCount still imports with replaceCount 0 (AcceptEdits) and reports the
    /// caller's value as stagedReplaceCount — the live-path narrowing does not touch it.
    /// </summary>
    [Fact]
    public async Task ControllerEmptyFragmentPublish_StillUsesReplaceCountZero()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = ThreeRungExport();
        session.StagedRungCount = 1;   // something is staged, so the empty publish is not a NothingStaged refusal

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 1, null, session, policy: null, target: "controller",
            confirmedByUser: true);

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal(PartialImportOption.AcceptEdits, Assert.Single(fake.CapturedImportOptions));
        Assert.Equal(0u, Assert.Single(fake.CapturedReplaceCounts));
        Assert.Equal(0u, success.ReplaceCount);
        Assert.Equal(1u, success.StagedReplaceCount);
    }

    // ── Undo block ──────────────────────────────────────────

    /// <summary>Replays an undo block exactly as a caller would pass it back to the tool.</summary>
    private static System.Threading.Tasks.Task<object> ReplayUndoAsync(
        LogixProjectSession session, UndoBlock undo) =>
        ProposeRungEditHandler.ExecuteAsync(
            undo.RoutineXPath, undo.InsertPosition, undo.ReplaceCount, null, session,
            policy: null, target: "live", confirmedByUser: true,
            expectedRungText: undo.ExpectedRungText,
            rungs: undo.Rungs.Select(r => new RungInput(r.Text, r.Comment)).ToList());

    private static string WithComment(string export, string rungText, string comment) =>
        export.Replace($"<Text><![CDATA[{rungText}]]>",
            $"<Comment><![CDATA[{comment}]]></Comment><Text><![CDATA[{rungText}]]>");

    private static List<(string Text, string? Comment)> CurrentRungs(ProposeRungEditRecordingFake fake) =>
        XDocument.Parse(fake.ExportContent!).Descendants("Rung")
            .Select(r => (r.Element("Text")!.Value, (string?)r.Element("Comment")?.Value)).ToList();

    /// <summary>A live insert returns an undo block that deletes exactly the inserted rungs.</summary>
    [Fact]
    public async Task LiveInsert_UndoBlock_DeletesInsertedRungs()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = ThreeRungExport();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 0, null, session, policy: null, target: "live", confirmedByUser: true,
            rungs: new[] { new RungInput("XIC(X)OTE(OutX);"), new RungInput("XIC(Y)OTE(OutY);") });

        var success = Assert.IsType<ProposeRungEditResult>(result);
        var undo = Assert.IsType<UndoBlock>(success.Undo);
        Assert.Equal(ValidXPath, undo.RoutineXPath);
        Assert.Equal(1u, undo.InsertPosition);
        Assert.Equal(2u, undo.ReplaceCount);
        Assert.Empty(undo.Rungs);
        Assert.Equal(new[] { "XIC(X)OTE(OutX);", "XIC(Y)OTE(OutY);" }, undo.ExpectedRungText);
        Assert.Contains("\"undo\":{\"routineXPath\"", JsonSerializer.Serialize(success));

        var undone = Assert.IsType<ProposeRungEditResult>(await ReplayUndoAsync(session, undo));
        Assert.Equal("live", undone.Status);
        Assert.Equal(2u, fake.CapturedReplaceCounts[^1]);
        Assert.Equal(
            new[] { "XIC(A)OTE(OutA);", "XIC(B)OTE(OutB);", "XIC(C)OTE(OutC);" },
            CurrentRungs(fake).Select(r => r.Text));
    }

    /// <summary>
    /// A live replace returns the original text and comment; replaying it restores both, and
    /// the comment carry-over does not overwrite the restored (or absent) comment.
    /// </summary>
    [Fact]
    public async Task LiveReplace_UndoBlock_RestoresOriginalTextAndComment()
    {
        // (a) original rung has a comment; replacement has none (carry-over would keep it anyway).
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = WithComment(ThreeRungExport(), "XIC(B)OTE(OutB);", "Original B");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 1, null, session, policy: null, target: "live", confirmedByUser: true,
            expectedRungText: new[] { "XIC(B)OTE(OutB);" },
            rungs: new[] { new RungInput("XIC(X)OTE(OutX);", "New comment") });
        var undo = Assert.IsType<UndoBlock>(Assert.IsType<ProposeRungEditResult>(result).Undo);
        Assert.Equal(1u, undo.ReplaceCount);
        Assert.Equal(new UndoRungEntry("XIC(B)OTE(OutB);", "Original B"), Assert.Single(undo.Rungs));
        Assert.Equal(new[] { "XIC(X)OTE(OutX);" }, undo.ExpectedRungText);
        Assert.Equal(("XIC(X)OTE(OutX);", "New comment"), CurrentRungs(fake)[1]);

        Assert.IsType<ProposeRungEditResult>(await ReplayUndoAsync(session, undo));
        Assert.Equal(("XIC(B)OTE(OutB);", "Original B"), CurrentRungs(fake)[1]);

        // (b) original rung had NO comment; the replacement's comment must not survive the undo.
        var (s2, f2) = await BuildOpenSessionAsync();
        f2.ExportContent = ThreeRungExport();
        var r2 = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 1, null, s2, policy: null, target: "live", confirmedByUser: true,
            expectedRungText: new[] { "XIC(B)OTE(OutB);" },
            rungs: new[] { new RungInput("XIC(X)OTE(OutX);", "Only on replacement") });
        var undo2 = Assert.IsType<UndoBlock>(Assert.IsType<ProposeRungEditResult>(r2).Undo);
        Assert.Equal(string.Empty, Assert.Single(undo2.Rungs).Comment);

        var undone2 = Assert.IsType<ProposeRungEditResult>(await ReplayUndoAsync(s2, undo2));
        Assert.Null(undone2.CommentsPreserved);
        Assert.Equal(("XIC(B)OTE(OutB);", (string?)null), CurrentRungs(f2)[1]);
    }

    /// <summary>A live delete returns the deleted rungs (with comments); replaying it re-inserts them.</summary>
    [Fact]
    public async Task LiveDelete_UndoBlock_ReinsertsDeletedRungs()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = WithComment(ThreeRungExport(), "XIC(B)OTE(OutB);", "Comment B");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 2, null, session, policy: null, target: "live", confirmedByUser: true,
            expectedRungText: new[] { "XIC(B)OTE(OutB);", "XIC(C)OTE(OutC);" }, rungs: NoRungs);
        var undo = Assert.IsType<UndoBlock>(Assert.IsType<ProposeRungEditResult>(result).Undo);

        Assert.Equal(1u, undo.InsertPosition);
        Assert.Equal(0u, undo.ReplaceCount);
        Assert.Empty(undo.ExpectedRungText);
        Assert.Equal(
            new[] { new UndoRungEntry("XIC(B)OTE(OutB);", "Comment B"), new UndoRungEntry("XIC(C)OTE(OutC);", "") },
            undo.Rungs);
        Assert.Single(CurrentRungs(fake));

        Assert.IsType<ProposeRungEditResult>(await ReplayUndoAsync(session, undo));
        Assert.Equal(
            new (string, string?)[]
            {
                ("XIC(A)OTE(OutA);", null), ("XIC(B)OTE(OutB);", "Comment B"), ("XIC(C)OTE(OutC);", null),
            },
            CurrentRungs(fake));
    }

    /// <summary>
    /// The undo call is an ordinary live call (FinalizeEdits) and returns its own undo block (redo),
    /// which re-applies the original edit.
    /// </summary>
    [Fact]
    public async Task UndoBlock_PassedBack_IsLiveCall_WithItsOwnUndo()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = ThreeRungExport();

        var first = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 1, null, session, policy: null, target: "live", confirmedByUser: true,
            expectedRungText: new[] { "XIC(B)OTE(OutB);" },
            rungs: new[] { new RungInput("XIC(X)OTE(OutX);", "X comment") }));
        var undo = Assert.IsType<UndoBlock>(first.Undo);

        var second = Assert.IsType<ProposeRungEditResult>(await ReplayUndoAsync(session, undo));
        Assert.Equal("live", second.Status);
        Assert.Equal(2, fake.CapturedImportOptions.Count);
        Assert.All(fake.CapturedImportOptions, o => Assert.Equal(PartialImportOption.FinalizeEdits, o));

        var redo = Assert.IsType<UndoBlock>(second.Undo);
        Assert.Equal(1u, redo.InsertPosition);
        Assert.Equal(1u, redo.ReplaceCount);
        Assert.Equal(new[] { "XIC(B)OTE(OutB);" }, redo.ExpectedRungText);
        Assert.Equal(new UndoRungEntry("XIC(X)OTE(OutX);", "X comment"), Assert.Single(redo.Rungs));

        Assert.IsType<ProposeRungEditResult>(await ReplayUndoAsync(session, redo));
        Assert.Equal(("XIC(X)OTE(OutX);", "X comment"), CurrentRungs(fake)[1]);
    }

    /// <summary>If the routine changed after the original call, replaying the undo block is a StaleTarget and imports nothing.</summary>
    [Fact]
    public async Task UndoBlock_AfterIntermediateChange_StaleTarget()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = ThreeRungExport();

        var first = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 0, null, session, policy: null, target: "live", confirmedByUser: true,
            rungs: new[] { new RungInput("XIC(X)OTE(OutX);") }));
        var undo = Assert.IsType<UndoBlock>(first.Undo);

        // Intermediate change to the rung the undo would delete.
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 1, null, session, policy: null, target: "live", confirmedByUser: true,
            expectedRungText: new[] { "XIC(X)OTE(OutX);" },
            rungs: new[] { new RungInput("XIC(Z)OTE(OutZ);") }));
        int importsBefore = fake.CapturedImportOptions.Count;

        var error = Assert.IsType<ProposeRungEditError>(await ReplayUndoAsync(session, undo));
        Assert.Equal("StaleTarget", error.Kind);
        Assert.Equal(importsBefore, fake.CapturedImportOptions.Count);
        Assert.Equal("XIC(Z)OTE(OutZ);", CurrentRungs(fake)[1].Text);
    }
}
#endif
