#if LOGIX_SDK
using System.Text.Json;
using System.Xml.Linq;
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

// ── PublishModeTests ──────────────────────────────────────────────────────────

/// <summary>
/// Contractual acceptance tests for Confirm mode enforcement on controller-target
/// publishes.
///
/// <para>
/// In <see cref="PublishMode.Confirm"/> mode (the default when
/// <c>LGX_PUBLISH_MODE</c> is unset), a call to
/// <c>logix_propose_rung_edit</c> with <c>target="controller"</c> that omits
/// <c>confirmedByUser=true</c> must be refused before any SDK call is made,
/// returning <c>kind="ConfirmationRequired"</c>.  The refusal must echo the
/// session's current staged state (rung count, tag names) and must append a
/// <c>capability_refusal</c> entry to the audit trail with
/// <c>env_var="LGX_PUBLISH_MODE"</c>.
/// </para>
///
/// <para>
/// Workstation-target staging is never gated by this check — it proceeds
/// regardless of <c>confirmedByUser</c> and regardless of publish mode.
/// </para>
/// </summary>
public class PublishModeTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    /// <summary>
    /// Export content for <see cref="ProposeRungEditRecordingFake.ExportContent"/>
    /// that reflects <see cref="StagedRungFragment"/>'s rung as a Type="I"
    /// (insert-pending) rung.  Used in controller-publish tests to make the
    /// post-publish pending census check pass so the handler returns
    /// <see cref="ProposeRungEditResult"/> rather than
    /// <see cref="ProposeRungEditMismatch"/>.
    /// </summary>
    private const string ConfirmTestCoilPendingExport =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
        "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" " +
        "ContainsContext=\"true\">" +
        "<Controller Name=\"DemoController\">" +
        "<Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\" Type=\"RLL\">" +
        "<RLLContent>" +
        "<Rung Number=\"0\" Type=\"I\"><Text><![CDATA[XIC(ConfirmTestTag)OTE(ConfirmTestCoil);]]></Text></Rung>" +
        "</RLLContent>" +
        "</Routine></Routines></Program></Programs>" +
        "</Controller>" +
        "</RSLogix5000Content>";

    /// <summary>
    /// Returns a <see cref="Capabilities"/> instance with
    /// <c>LiveWritesEnabled = true</c> and
    /// <c>PublishMode = PublishMode.Auto</c> — the Auto mode configuration under test.
    /// </summary>
    private static Capabilities AutoCapabilities() =>
        new(LiveWritesEnabled: true, PublishMode: PublishMode.Auto);

    /// <summary>
    /// A simple single-rung fragment used for workstation staging in tests that
    /// need something staged before the confirmation gate fires.
    /// </summary>
    private const string StagedRungFragment =
        "<RSLogix5000Content>" +
          "<Controller>" +
            "<Programs>" +
              "<Program Name=\"MainProgram\">" +
                "<Routines>" +
                  "<Routine Name=\"MainRoutine\">" +
                    "<RLLContent>" +
                      "<Rung Number=\"0\" Type=\"N\">" +
                        "<Text><![CDATA[XIC(ConfirmTestTag)OTE(ConfirmTestCoil);]]></Text>" +
                      "</Rung>" +
                    "</RLLContent>" +
                  "</Routine>" +
                "</Routines>" +
              "</Program>" +
            "</Programs>" +
          "</Controller>" +
        "</RSLogix5000Content>";

    /// <summary>
    /// A single-rung fragment that also declares a tag, used to verify that
    /// the confirmation-required error echoes staged tag names.
    /// </summary>
    private const string StagedRungWithTagFragment =
        "<RSLogix5000Content>" +
          "<Controller>" +
            "<Tags Use=\"Context\">" +
              "<Tag Name=\"ConfirmTestTag\" DataType=\"BOOL\" />" +
            "</Tags>" +
            "<Programs>" +
              "<Program Name=\"MainProgram\">" +
                "<Routines>" +
                  "<Routine Name=\"MainRoutine\">" +
                    "<RLLContent>" +
                      "<Rung Number=\"0\" Type=\"N\">" +
                        "<Text><![CDATA[XIC(ConfirmTestTag)OTE(ConfirmTestCoil);]]></Text>" +
                      "</Rung>" +
                    "</RLLContent>" +
                  "</Routine>" +
                "</Routines>" +
              "</Program>" +
            "</Programs>" +
          "</Controller>" +
        "</RSLogix5000Content>";

    // ── Helper ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an open <see cref="LogixProjectSession"/> backed by a
    /// <see cref="ProposeRungEditRecordingFake"/> so tests can assert on the
    /// SDK calls that were (or were not) made.
    /// </summary>
    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)>
        BuildOpenSessionAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    /// <summary>
    /// Returns a <see cref="Capabilities"/> instance with
    /// <c>LiveWritesEnabled = true</c> and
    /// <c>PublishMode = PublishMode.Confirm</c> — the configuration under test.
    /// </summary>
    private static Capabilities ConfirmCapabilities() =>
        new(LiveWritesEnabled: true, PublishMode: PublishMode.Confirm);

    // ══════════════════════════════════════════════════════════════════════════
    // Confirm_ControllerPublish_WithoutConfirmedByUser_IsRefused_BeforeSdkCall
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Contractual test: in Confirm mode, a controller-target
    /// publish without <c>confirmedByUser = true</c> must be refused with
    /// <c>kind = "ConfirmationRequired"</c> BEFORE any SDK call is made.
    /// </summary>
    [Fact]
    public async Task Confirm_ControllerPublish_WithoutConfirmedByUser_IsRefused_BeforeSdkCall()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var tools = new LogixSdkTools(
            new ProjectService(),
            new TagSessionService(),
            ConfirmCapabilities(),
            new StudioConfirmWait());

        // Attempt a controller-target publish without confirmedByUser.
        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath,
            insertPosition:  0,
            replaceCount:    0,
            l5xFragment:     null,
            session:         session,
            target:          "controller",
            confirmedByUser: false);

        // Must return kind="ConfirmationRequired".
        var doc  = JsonDocument.Parse(json);
        var kind = doc.RootElement.GetProperty("kind").GetString();
        Assert.Equal("ConfirmationRequired", kind);

        // The error message must be present.
        Assert.True(
            doc.RootElement.TryGetProperty("error", out var errorEl),
            "Expected 'error' field in ConfirmationRequiredError response.");
        var error = errorEl.GetString();
        Assert.NotNull(error);
        Assert.NotEmpty(error!);

        // No SDK import call must have been made — the gate fires before any SDK work.
        Assert.Empty(fake.CapturedImportOptions);
        Assert.Empty(fake.CapturedXPaths);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Confirm_ControllerPublish_WithConfirmedByUser_Proceeds
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Contractual test: in Confirm mode, a controller-target
    /// publish WITH <c>confirmedByUser = true</c> must pass through the
    /// confirmation gate and proceed to the SDK layer (no refusal).
    /// </summary>
    [Fact]
    public async Task Confirm_ControllerPublish_WithConfirmedByUser_Proceeds()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        session.StagedRungCount = 1;   // something is staged, so the empty publish is not a NothingStaged refusal

        var tools = new LogixSdkTools(
            new ProjectService(),
            new TagSessionService(),
            ConfirmCapabilities(),
            new StudioConfirmWait());

        // Attempt a controller-target publish WITH confirmedByUser = true.
        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath,
            insertPosition:  0,
            replaceCount:    0,
            l5xFragment:     null,
            session:         session,
            target:          "controller",
            confirmedByUser: true);

        // Must NOT return kind="ConfirmationRequired".
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("kind", out var kindEl))
            Assert.NotEqual("ConfirmationRequired", kindEl.GetString());

        // At least one SDK import call must have been made — the gate let it through.
        Assert.NotEmpty(fake.CapturedImportOptions);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Confirm_WorkstationStage_Unaffected
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Contractual test: workstation-target staging must be
    /// completely unaffected by the Confirm mode gate, regardless of the value
    /// of <c>confirmedByUser</c>.  A <c>target="workstation"</c> call with
    /// <c>confirmedByUser = false</c> (or omitted) must succeed exactly as if
    /// the gate did not exist.
    /// </summary>
    [Fact]
    public async Task Confirm_WorkstationStage_Unaffected()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var tools = new LogixSdkTools(
            new ProjectService(),
            new TagSessionService(),
            ConfirmCapabilities(),
            new StudioConfirmWait());

        // Workstation-target staging: confirmedByUser is false (default) — the
        // gate must NOT fire.
        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath,
            insertPosition:  0,
            replaceCount:    0,
            l5xFragment:     StagedRungFragment,
            session:         session,
            target:          "workstation",
            confirmedByUser: false);

        // Must NOT return kind="ConfirmationRequired".
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("kind", out var kindEl))
            Assert.NotEqual("ConfirmationRequired", kindEl.GetString());

        // The SDK import call was made — staging proceeded normally.
        Assert.NotEmpty(fake.CapturedImportOptions);

        // The call must produce a standard ProposeRungEditResult (status="proposed").
        Assert.True(
            doc.RootElement.TryGetProperty("status", out var statusEl),
            "Expected 'status' field in ProposeRungEditResult.");
        Assert.Equal("proposed", statusEl.GetString());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ConfirmationRequired_IsAudited_AndEchoesStaged
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Contractual test: when the
    /// confirmation gate fires, the refusal must:
    /// <list type="bullet">
    ///   <item>Append a <c>capability_refusal</c> audit event with
    ///     <c>operation="logix_propose_rung_edit"</c> and
    ///     <c>env_var="LGX_PUBLISH_MODE"</c>, matching the
    ///     <see cref="AuditLogger.LogCapabilityRefusal"/> format used by
    ///     <c>CapabilityDisabled</c> refusals.</item>
    ///   <item>Echo the session's current staged state: a non-zero
    ///     <c>stagedRungCount</c> and the staged tag names in
    ///     <c>stagedTagNames</c>.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task ConfirmationRequired_IsAudited_AndEchoesStaged()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(), $"confirm-gate-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var (session, _) = await BuildOpenSessionAsync();

            // ── Step 1: stage a rung + tag with target="workstation" ──────────
            // This populates session.StagedRungBuffer and session.StagedTagBuffer
            // without any audit involvement here.
            await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath,
                insertPosition: 0,
                replaceCount:   0,
                StagedRungWithTagFragment,
                session,
                target: "workstation");

            // Verify that the staging step did create staged state.
            Assert.True(
                session.StagedRungCount > 0,
                "Precondition: staging must have incremented StagedRungCount.");

            // ── Step 2: attempt controller-target publish without confirmedByUser ─
            var tools = new LogixSdkTools(
                new ProjectService(),
                new TagSessionService(),
                ConfirmCapabilities(),
                new StudioConfirmWait());

            var json = await tools.GetProposeRungEditResponseAsync(
                ValidXPath,
                insertPosition:  0,
                replaceCount:    0,
                l5xFragment:     null,
                session:         session,
                target:          "controller",
                confirmedByUser: false);

            // ── Verify kind ────────────────────────────────────────────────────
            var doc = JsonDocument.Parse(json);
            Assert.Equal(
                "ConfirmationRequired",
                doc.RootElement.GetProperty("kind").GetString());

            // ── Verify echoed staged rung count ────────────────────────────────
            Assert.True(
                doc.RootElement.TryGetProperty("stagedRungCount", out var rungCountEl),
                "Expected 'stagedRungCount' in ConfirmationRequiredError.");
            Assert.True(
                rungCountEl.GetInt32() > 0,
                "stagedRungCount must be > 0 when rungs have been staged.");

            // ── Verify echoed staged tag names ─────────────────────────────────
            Assert.True(
                doc.RootElement.TryGetProperty("stagedTagNames", out var tagNamesEl),
                "Expected 'stagedTagNames' in ConfirmationRequiredError when tags are staged.");
            Assert.Equal(JsonValueKind.Array, tagNamesEl.ValueKind);
            Assert.NotEmpty(tagNamesEl.EnumerateArray());

            // ── Verify audit trail ─────────────────────────────────────────────
            var auditPath = Path.Combine(tempDir, "audit.jsonl");
            Assert.True(File.Exists(auditPath), "Audit file must exist after a refusal.");

            var auditLines = File.ReadAllLines(auditPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonDocument.Parse(l).RootElement)
                .ToList();

            // At least one capability_refusal event must have been written.
            var refusalEvent = auditLines.FirstOrDefault(e =>
                e.TryGetProperty("event", out var ev) &&
                ev.GetString() == "capability_refusal");

            Assert.True(
                refusalEvent.ValueKind != JsonValueKind.Undefined,
                "Expected a 'capability_refusal' audit event after a ConfirmationRequired refusal.");

            // The event must name the denied operation.
            Assert.Equal(
                "logix_propose_rung_edit",
                refusalEvent.GetProperty("operation").GetString());

            // The event must name the governing environment variable (matches
            // CapabilityDisabled format).
            Assert.Equal(
                Capabilities.EnvVarPublishMode,
                refusalEvent.GetProperty("env_var").GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }
    // ══════════════════════════════════════════════════════════════════════════
    // ConfirmationRequired_StagedTagNames_ExcludePreExistingContextTags
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Contractual test: the <c>stagedTagNames</c> field in a
    /// <c>ConfirmationRequired</c> refusal must contain only tag names that are
    /// absent from <see cref="LogixProjectSession.PreStageTagCensus"/> — i.e.,
    /// only tags that a publish would actually <em>create</em> on the controller.
    ///
    /// <para>
    /// When the staging buffer holds both a genuinely new tag (<c>"NewTag"</c>)
    /// and a context declaration for a pre-existing tag (<c>"ExistingTag"</c>),
    /// and the pre-stage census records <c>"ExistingTag"</c>, the refusal must
    /// echo only <c>"NewTag"</c> in <c>stagedTagNames</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ConfirmationRequired_StagedTagNames_ExcludePreExistingContextTags()
    {
        var (session, _) = await BuildOpenSessionAsync();

        // Staging buffer: one genuinely new tag and one pre-existing context declaration.
        session.StagedRungCount = 1;
        session.StagedTagBuffer["NewTag"] = new XElement("Tag",
            new XAttribute("Name", "NewTag"),
            new XAttribute("DataType", "BOOL"));
        session.StagedTagBuffer["ExistingTag"] = new XElement("Tag",
            new XAttribute("Name", "ExistingTag"),
            new XAttribute("DataType", "DINT"));

        // Pre-stage census: the controller already knew about ExistingTag.
        session.PreStageTagCensus.Add("ExistingTag");

        var tools = new LogixSdkTools(
            new ProjectService(),
            new TagSessionService(),
            ConfirmCapabilities(),
            new StudioConfirmWait());

        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath,
            insertPosition:  0,
            replaceCount:    0,
            l5xFragment:     null,
            session:         session,
            target:          "controller",
            confirmedByUser: false);

        var doc = JsonDocument.Parse(json);
        Assert.Equal("ConfirmationRequired", doc.RootElement.GetProperty("kind").GetString());

        // stagedTagNames must be present and contain only the genuinely new tag.
        Assert.True(
            doc.RootElement.TryGetProperty("stagedTagNames", out var tagNamesEl),
            $"Expected 'stagedTagNames' in ConfirmationRequiredError. Full response: {json}");
        Assert.Equal(JsonValueKind.Array, tagNamesEl.ValueKind);
        var tagNames = tagNamesEl.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "NewTag" }, tagNames);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ConfirmationRequired_StagedTagNames_MatchPendingEditsStagedTagNames
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Contractual test: the <c>stagedTagNames</c> in a
    /// <c>ConfirmationRequired</c> refusal and the <c>staged.tagNames</c> in a
    /// <c>logix_pending_edits</c> response must be identical for the same session
    /// state — both code paths use <see cref="LogixProjectSession.GetNewStagedTagNames"/>
    /// as the single filter definition.
    /// </summary>
    [Fact]
    public async Task ConfirmationRequired_StagedTagNames_MatchPendingEditsStagedTagNames()
    {
        // Use FakePendingEditsProject so we can call PendingEditsHandler directly.
        var fake = new FakePendingEditsProject
        {
            ExportContent =
                "<RSLogix5000Content><Controller><Programs>" +
                "<Program Name=\"MainProgram\"><Routines>" +
                "<Routine Name=\"MainRoutine\"><RLLContent>" +
                "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
                "</RLLContent></Routine>" +
                "</Routines></Program>" +
                "</Programs></Controller></RSLogix5000Content>",
        };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        session.IsOnline     = true;
        session.IsCorrelated = true;

        // Staging buffer: one new tag and one pre-existing context declaration.
        session.StagedRungCount = 1;
        session.StagedTagBuffer["NewMotorTag"] = new XElement("Tag",
            new XAttribute("Name", "NewMotorTag"));
        session.StagedTagBuffer["AlreadyPresent"] = new XElement("Tag",
            new XAttribute("Name", "AlreadyPresent"));
        session.PreStageTagCensus.Add("AlreadyPresent");

        // ── ConfirmationRequired path ─────────────────────────────────────────
        var tools = new LogixSdkTools(
            new ProjectService(),
            new TagSessionService(),
            ConfirmCapabilities(),
            new StudioConfirmWait());

        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath,
            insertPosition:  0,
            replaceCount:    0,
            l5xFragment:     null,
            session:         session,
            target:          "controller",
            confirmedByUser: false);

        var confirmDoc = JsonDocument.Parse(json);
        Assert.Equal(
            "ConfirmationRequired",
            confirmDoc.RootElement.GetProperty("kind").GetString());

        IReadOnlyList<string> confirmTagNames = confirmDoc.RootElement
            .TryGetProperty("stagedTagNames", out var confirmTagNamesEl)
            ? confirmTagNamesEl.EnumerateArray().Select(e => e.GetString()!).ToList()
            : Array.Empty<string>();

        // ── PendingEdits path ─────────────────────────────────────────────────
        var pendingResult = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        IReadOnlyList<string> pendingTagNames = pendingResult.Staged.TagNames;

        // Both code paths must produce the same tag name list.
        Assert.Equal(confirmTagNames, pendingTagNames);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Auto_ControllerPublish_WithoutConfirmedByUser_Proceeds
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Contractual test: in Auto mode a controller-target
    /// publish succeeds even when <c>confirmedByUser</c> is omitted (defaulting
    /// to <c>false</c>).  No <c>ConfirmationRequired</c> refusal must be raised
    /// and the handler must return <c>status = "proposed"</c>.
    /// </summary>
    [Fact]
    public async Task Auto_ControllerPublish_WithoutConfirmedByUser_Proceeds()
    {
        // Arrange ─────────────────────────────────────────────────────────────
        var (session, fake) = await BuildOpenSessionAsync();

        var tools = new LogixSdkTools(
            new ProjectService(),
            new TagSessionService(),
            AutoCapabilities(),
            new StudioConfirmWait());

        // Stage a rung locally first (target="workstation" — in-memory only).
        var stageResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation");
        Assert.IsType<ProposeRungEditResult>(stageResult);

        // Supply a pending-I export so the post-publish census check
        // passes and the handler returns ProposeRungEditResult rather than
        // ProposeRungEditMismatch.
        fake.ExportContent = ConfirmTestCoilPendingExport;

        // Act ─────────────────────────────────────────────────────────────────
        // Publish to controller WITHOUT confirmedByUser (omitted → default false).
        // In Auto mode this must NOT be refused with kind="ConfirmationRequired".
        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath,
            insertPosition:  0,
            replaceCount:    0,
            l5xFragment:     null,      // empty-fragment publish of staged buffer
            session:         session,
            offline:         false,
            target:          "controller");
            // confirmedByUser not passed — defaults to false

        // Assert ──────────────────────────────────────────────────────────────
        var doc = JsonDocument.Parse(json);

        // Auto mode must not refuse with ConfirmationRequired.
        if (doc.RootElement.TryGetProperty("kind", out var kindEl))
            Assert.NotEqual("ConfirmationRequired", kindEl.GetString());

        // The publish must have proceeded and returned a successful result.
        Assert.True(
            doc.RootElement.TryGetProperty("status", out var statusEl),
            $"Expected 'status' field in response (publish should have proceeded). " +
            $"Full response: {json}");
        Assert.Equal("proposed", statusEl.GetString());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Auto_ControllerPublish_ConfirmedByUserTrue_IsAcceptedSilently
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// In Auto mode, passing <c>confirmedByUser = true</c> is silently accepted:
    /// it produces the same successful <c>status = "proposed"</c> result as a
    /// call that omits the parameter entirely.  No side effect is introduced by
    /// the extra flag.
    /// </summary>
    [Fact]
    public async Task Auto_ControllerPublish_ConfirmedByUserTrue_IsAcceptedSilently()
    {
        // Arrange ─────────────────────────────────────────────────────────────
        var (session, fake) = await BuildOpenSessionAsync();

        var tools = new LogixSdkTools(
            new ProjectService(),
            new TagSessionService(),
            AutoCapabilities(),
            new StudioConfirmWait());

        // Stage a rung locally first.
        var stageResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation");
        Assert.IsType<ProposeRungEditResult>(stageResult);

        fake.ExportContent = ConfirmTestCoilPendingExport;

        // Act ─────────────────────────────────────────────────────────────────
        // Publish with confirmedByUser: true — must behave identically to omitting it.
        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath,
            insertPosition:  0,
            replaceCount:    0,
            l5xFragment:     null,
            session:         session,
            offline:         false,
            target:          "controller",
            confirmedByUser: true);

        // Assert ──────────────────────────────────────────────────────────────
        var doc = JsonDocument.Parse(json);

        // Must not be a ConfirmationRequired refusal.
        if (doc.RootElement.TryGetProperty("kind", out var kindEl))
            Assert.NotEqual("ConfirmationRequired", kindEl.GetString());

        // And the publish must have proceeded.
        Assert.True(
            doc.RootElement.TryGetProperty("status", out var statusEl),
            $"Expected 'status' field in response. Full response: {json}");
        Assert.Equal("proposed", statusEl.GetString());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PublishResponse_StatesPublishMode
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Contractual test: every publish response must include a
    /// <c>publishMode</c> field reflecting the server's current publish mode —
    /// <c>"auto"</c> in Auto mode and <c>"confirm"</c> in Confirm mode.
    ///
    /// <para>
    /// A controller-target publish in Auto mode (no confirmation required) and in
    /// Confirm mode (with <c>confirmedByUser=true</c>) are both exercised, verifying
    /// that the <c>publishMode</c> field is present and contains the correct string
    /// for each configuration.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PublishResponse_StatesPublishMode()
    {
        // ── Auto mode: publishMode must equal "auto" ───────────────────────────
        {
            var (session, fake) = await BuildOpenSessionAsync();

            var tools = new LogixSdkTools(
                new ProjectService(),
                new TagSessionService(),
                AutoCapabilities(),
                new StudioConfirmWait());

            // Stage a rung locally (workstation target — does not touch the controller).
            await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation");

            // Supply a pending-I export so the post-publish census check passes.
            fake.ExportContent = ConfirmTestCoilPendingExport;

            var json = await tools.GetProposeRungEditResponseAsync(
                ValidXPath,
                insertPosition:  0,
                replaceCount:    0,
                l5xFragment:     null,
                session:         session,
                offline:         false,
                target:          "controller");
                // confirmedByUser not passed — defaults to false (fine in Auto mode)

            var doc = JsonDocument.Parse(json);

            // The publish must have succeeded.
            Assert.True(
                doc.RootElement.TryGetProperty("status", out var statusEl),
                $"Expected 'status' field in Auto-mode publish response. Full response: {json}");
            Assert.Equal("proposed", statusEl.GetString());

            // publishMode must be present and equal "auto".
            Assert.True(
                doc.RootElement.TryGetProperty("publishMode", out var modeEl),
                $"Expected 'publishMode' field in Auto-mode publish response. Full response: {json}");
            Assert.Equal("auto", modeEl.GetString());
        }

        // ── Confirm mode: publishMode must equal "confirm" ─────────────────────
        {
            var (session, fake) = await BuildOpenSessionAsync();

            var tools = new LogixSdkTools(
                new ProjectService(),
                new TagSessionService(),
                ConfirmCapabilities(),
                new StudioConfirmWait());

            // Stage a rung locally (workstation target — does not touch the controller).
            await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation");

            // Supply a pending-I export so the post-publish census check passes.
            fake.ExportContent = ConfirmTestCoilPendingExport;

            // Confirm mode requires confirmedByUser=true to reach the controller.
            var json = await tools.GetProposeRungEditResponseAsync(
                ValidXPath,
                insertPosition:  0,
                replaceCount:    0,
                l5xFragment:     null,
                session:         session,
                offline:         false,
                target:          "controller",
                confirmedByUser: true);

            var doc = JsonDocument.Parse(json);

            // The publish must have succeeded.
            Assert.True(
                doc.RootElement.TryGetProperty("status", out var statusEl),
                $"Expected 'status' field in Confirm-mode publish response. Full response: {json}");
            Assert.Equal("proposed", statusEl.GetString());

            // publishMode must be present and equal "confirm".
            Assert.True(
                doc.RootElement.TryGetProperty("publishMode", out var modeEl),
                $"Expected 'publishMode' field in Confirm-mode publish response. Full response: {json}");
            Assert.Equal("confirm", modeEl.GetString());
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Honest confirmation wording
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The refusal text must not tell the model to call again with
    /// <c>confirmedByUser=true</c>; it must tell it to present the staged set,
    /// wait for explicit approval, and only then publish.
    /// </summary>
    [Fact]
    public async Task ConfirmationRequired_Text_DoesNotInstructRetryWithConfirmedByUserTrue()
    {
        var (session, _) = await BuildOpenSessionAsync();
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService(), ConfirmCapabilities(), new StudioConfirmWait());

        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, null,
            session: session, target: "controller", confirmedByUser: false);

        var error = JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()!;
        Assert.DoesNotContain("again with", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confirmedByUser=true", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("explicit approval", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot verify", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rung count", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The <c>confirmedByUser</c> parameter description must say the server cannot
    /// verify the user's approval and that the agent must never self-set the flag.
    /// </summary>
    [Fact]
    public void ConfirmedByUser_Description_SaysServerCannotVerifyApproval()
    {
        var method = typeof(LogixSdkTools).GetMethod(nameof(LogixSdkTools.ProposeRungEdit));
        Assert.NotNull(method);

        var param = method!.GetParameters().Single(p => p.Name == "confirmedByUser");
        var attr = param.GetCustomAttributes(inherit: false)
            .FirstOrDefault(a => a.GetType().Name == "DescriptionAttribute");
        Assert.NotNull(attr);
        var desc = attr!.GetType().GetProperty("Description")!.GetValue(attr) as string;
        Assert.NotNull(desc);

        Assert.Contains("cannot verify", desc!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("explicitly approved", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never set it on its own judgement", desc, StringComparison.OrdinalIgnoreCase);
    }
}
#endif
