#if LOGIX_SDK
using System.Collections.Generic;
using System.Xml.Linq;
using RockwellAutomation.LogixDesigner;

using PartialImportOption = RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption;
using SdkTask             = System.Threading.Tasks.Task;
using Task                = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

// ── PublishStagedSetTests ────────────────────────────────────────────────────

/// <summary>
/// Regression tests: the final <c>target="controller"</c>
/// publish of a staged change set may omit <c>l5xFragment</c> entirely (null, empty,
/// or whitespace-only) and send the previously staged buffer only, without merging in
/// any additional rung content of its own.
///
/// <para>
/// Before this fix, <see cref="ProposeRungEditHandler.ExecuteAsync"/> rejected any
/// call whose <c>l5xFragment</c> was blank before it even looked at <c>target</c>,
/// forcing a caller to resend the same fragment it had already staged with
/// <c>target="workstation"</c>. Because a controller-target publish sends the entire
/// accumulated workstation batch (whatever is already pending from prior
/// <see cref="PartialImportOption.LeaveEdits"/> imports) alongside whatever fragment
/// it is given, resending the staged fragment doubled the change set (Tier D run 4,
/// A 3-rung stage plus a 3-rung resend imported 6 rungs).
/// </para>
///
/// <para>
/// These tests use the same <see cref="ProposeRungEditRecordingFake"/> fake used by
/// <c>ProposeRungEditHandlerTests</c> and <c>PublishTagEnvelopeTests</c>, which
/// captures the exact XML text handed to
/// <see cref="ILogixProject.PartialImportRungsFromXmlFileAsync"/> on every call.
/// </para>
/// </summary>
[Collection("AuditLoggerDir")]
public class PublishStagedSetTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    /// <summary>
    /// A single-rung fragment representative of what an agent stages with
    /// <c>target="workstation"</c> before publishing.
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
                        "<Text><![CDATA[XIC(StagedTag)OTE(StagedCoil);]]></Text>" +
                      "</Rung>" +
                    "</RLLContent>" +
                  "</Routine>" +
                "</Routines>" +
              "</Program>" +
            "</Programs>" +
          "</Controller>" +
        "</RSLogix5000Content>";

    /// <summary>
    /// A two-rung publish fragment used by the dedup tests:
    /// rung 0 is byte-equal (after normalisation) to the single rung staged by
    /// <see cref="StagedRungFragment"/>; rung 1 is genuinely new content that was
    /// never staged. Used to prove dedup removes only the duplicate, never the
    /// new rung alongside it.
    /// </summary>
    private const string DuplicatePlusNewRungFragment =
        "<RSLogix5000Content>" +
          "<Controller>" +
            "<Programs>" +
              "<Program Name=\"MainProgram\">" +
                "<Routines>" +
                  "<Routine Name=\"MainRoutine\">" +
                    "<RLLContent>" +
                      "<Rung Number=\"0\" Type=\"N\">" +
                        "<Text><![CDATA[XIC(StagedTag)OTE(StagedCoil);]]></Text>" +
                      "</Rung>" +
                      "<Rung Number=\"1\" Type=\"N\">" +
                        "<Text><![CDATA[XIC(NewTag)OTE(NewCoil);]]></Text>" +
                      "</Rung>" +
                    "</RLLContent>" +
                  "</Routine>" +
                "</Routines>" +
              "</Program>" +
            "</Programs>" +
          "</Controller>" +
        "</RSLogix5000Content>";

    /// <summary>
    /// Export content for <see cref="ProposeRungEditRecordingFake.ExportContent"/>
    /// that reflects the staged rung from <see cref="StagedRungFragment"/> as a
    /// Type="I" (insert-pending) rung.  Used in tests that stage a rung with
    /// <c>target="workstation"</c> then publish with <c>target="controller"</c>
    /// and need the post-publish pending census check to succeed
    /// i.e. the controller export must show the staged rung as pending <c>I</c>
    /// so the census verification passes and the handler returns
    /// <see cref="ProposeRungEditResult"/> rather than
    /// <see cref="ProposeRungEditMismatch"/>.
    /// </summary>
    private const string StagedCoilPendingExport =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
        "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" " +
        "ContainsContext=\"true\">" +
        "<Controller Name=\"DemoController\">" +
        "<Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Use=\"Target\" Name=\"MainRoutine\" Type=\"RLL\">" +
        "<RLLContent>" +
        "<Rung Number=\"0\" Type=\"I\"><Text><![CDATA[XIC(StagedTag)OTE(StagedCoil);]]></Text></Rung>" +
        "</RLLContent>" +
        "</Routine></Routines></Program></Programs>" +
        "</Controller>" +
        "</RSLogix5000Content>";

    // ── Helper: build an open session backed by a recording fake ─────────────
    // Mirrors ProposeRungEditHandlerTests.BuildOpenSessionAsync so this file's
    // tests exercise ExecuteAsync exactly the same way.

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)>
        BuildOpenSessionAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        // Mark as online and correlated so the handler's pre-flight guard passes.
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    /// <summary>
    /// The contractual test: stage a rung with
    /// <c>target="workstation"</c>, then publish with <c>target="controller"</c> and
    /// an EMPTY <c>l5xFragment</c>. This must not raise a validation error, and the
    /// fragment the publish call hands to the SDK must carry no <c>&lt;Rung&gt;</c>
    /// content of its own — proving nothing new was merged in and only the buffer
    /// already staged by the prior <c>LeaveEdits</c> import can land in the
    /// controller's pending-edit zone.
    /// </summary>
    [Fact]
    public async Task ControllerPublish_WithEmptyFragment_SendsStagedBufferOnly()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var capturedBodies = new List<string>();
        fake.XmlFileContentCaptor = body => capturedBodies.Add(body);

        // Stage a rung locally first (target="workstation" — LeaveEdits, in-memory only).
        var stageResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation");
        Assert.IsType<ProposeRungEditResult>(stageResult);

        // Reflect the staged rung as a pending I-type rung in the export so the
        // post-publish census check passes and the handler returns
        // ProposeRungEditResult rather than ProposeRungEditMismatch.
        fake.ExportContent = StagedCoilPendingExport;

        // Publish with target="controller" and an EMPTY l5xFragment.
        var publishResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        // No validation error — an empty l5xFragment is accepted for target="controller".
        var success = Assert.IsType<ProposeRungEditResult>(publishResult);
        Assert.Equal("proposed",  success.Status);
        Assert.Equal(ValidXPath,  success.RoutineXPath);

        // Two SDK import calls total: the workstation stage, then the controller publish.
        Assert.Equal(2, capturedBodies.Count);
        Assert.Equal(2, fake.CapturedImportOptions.Count);
        Assert.Equal(PartialImportOption.LeaveEdits,  fake.CapturedImportOptions[0]);
        Assert.Equal(PartialImportOption.AcceptEdits, fake.CapturedImportOptions[1]);

        // The publish call's own fragment has a valid RLLContent element (so the SDK
        // accepts the envelope) but NO <Rung> children — nothing new is merged into
        // it. Whatever ends up pending on the controller is exactly what the prior
        // LeaveEdits import already staged, not a re-sent or duplicated copy of it.
        var publishedDoc = XDocument.Parse(capturedBodies[1]);
        Assert.NotEmpty(publishedDoc.Descendants("RLLContent"));
        Assert.Empty(publishedDoc.Descendants("Rung"));

        // The synthesised envelope still addresses the correct program/routine.
        var programElem = publishedDoc.Descendants("Program").Single();
        var routineElem = publishedDoc.Descendants("Routine").Single();
        Assert.Equal("MainProgram", (string?)programElem.Attribute("Name"));
        Assert.Equal("MainRoutine", (string?)routineElem.Attribute("Name"));

        // Sanity: the FIRST (staging) call's own fragment did carry the staged rung.
        Assert.Contains("StagedCoil", capturedBodies[0]);
    }

    /// <summary>
    /// An absent (<c>null</c>) l5xFragment is accepted identically to an empty one
    /// for <c>target="controller"</c> — the acceptance criterion says "empty or
    /// absent", and callers going through the MCP tool surface may omit the
    /// parameter entirely rather than pass <c>""</c>.
    /// </summary>
    [Fact]
    public async Task ControllerPublish_WithAbsentFragment_DoesNotRaiseValidationError()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        session.StagedRungCount = 1;   // something is staged, so the empty publish is not a NothingStaged refusal

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, null, session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("proposed", success.Status);
        Assert.Single(fake.CapturedImportOptions);
        Assert.Equal(PartialImportOption.AcceptEdits, fake.CapturedImportOptions[0]);
    }

    /// <summary>
    /// A whitespace-only l5xFragment is treated the same as empty/absent for
    /// target="controller" (all three are "no fragment supplied").
    /// </summary>
    [Fact]
    public async Task ControllerPublish_WithWhitespaceFragment_DoesNotRaiseValidationError()
    {
        var (session, _) = await BuildOpenSessionAsync();
        session.StagedRungCount = 1;   // something is staged, so the empty publish is not a NothingStaged refusal

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "   ", session, target: "controller");

        Assert.IsType<ProposeRungEditResult>(result);
    }

    /// <summary>
    /// Regression guard: target="workstation" has no controller-side buffer to
    /// flush, so an empty l5xFragment there is still rejected exactly as before
    /// this change (see also ProposeRungEditHandlerTests.ExecuteAsync_EmptyL5xFragment_ReturnsError).
    /// </summary>
    [Fact]
    public async Task WorkstationPublish_WithEmptyFragment_StillReturnsValidationError()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "workstation");

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Contains("l5xFragment", err.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fake.CapturedXPaths);
    }

    /// <summary>
    /// When session.StagedTagBuffer already holds tags from a prior workstation
    /// stage, an empty-fragment controller publish still merges them into the
    /// synthesised envelope — the tag buffer is flushed exactly as it would be
    /// alongside a fragment-carrying publish (continues to
    /// apply to the synthesised envelope).
    /// </summary>
    [Fact]
    public async Task ControllerPublish_WithEmptyFragment_StillMergesBufferedTags()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        session.StagedTagBuffer["BufferedTag1"] =
            new XElement("Tag", new XAttribute("Name", "BufferedTag1"), new XAttribute("DataType", "BOOL"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        Assert.IsType<ProposeRungEditResult>(result);
        Assert.NotNull(fake.LastCapturedXmlContent);
        var publishedDoc = XDocument.Parse(fake.LastCapturedXmlContent!);
        var tagNames = publishedDoc.Descendants("Tag")
            .Select(t => (string?)t.Attribute("Name"))
            .ToList();
        Assert.Contains("BufferedTag1", tagNames);

        // The buffer is cleared after a successful controller publish.
        Assert.Empty(session.StagedTagBuffer);
    }

    /// <summary>
    /// The contractual test: stage a rung with
    /// <c>target="workstation"</c>, then publish with <c>target="controller"</c>
    /// resending the SAME fragment (a non-empty <c>l5xFragment</c>) at the same
    /// <c>insertPosition</c> — the exact agent mistake from Tier D run 4
    /// That previously imported the rung a second time and
    /// doubled the change set. The duplicate rung must be recognised and dropped
    /// before the SDK ever sees it, so it is sent to the controller exactly once
    /// (via the earlier <c>LeaveEdits</c> stage) rather than twice.
    /// </summary>
    [Fact]
    public async Task ControllerPublish_DuplicateFragmentRungs_AreSentOnce()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var capturedBodies = new List<string>();
        fake.XmlFileContentCaptor = body => capturedBodies.Add(body);

        // Stage a rung locally first (target="workstation" — LeaveEdits, in-memory only).
        var stageResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation");
        Assert.IsType<ProposeRungEditResult>(stageResult);

        // Reflect the staged rung as a pending I-type rung so the post-publish
        // census check passes.
        fake.ExportContent = StagedCoilPendingExport;

        // Publish with target="controller", resending the IDENTICAL fragment that
        // was already staged at the same insertPosition.
        var publishResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, StagedRungFragment, session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(publishResult);
        Assert.Equal("proposed", success.Status);

        // Two SDK import calls total: the workstation stage, then the controller publish.
        Assert.Equal(2, capturedBodies.Count);

        // The FIRST (staging) call's own fragment carried the rung.
        Assert.Contains("StagedCoil", capturedBodies[0]);

        // The publish call's own fragment must NOT carry the duplicate rung again —
        // it was already staged and is committed by AcceptEdits below without being
        // resent. No <Rung> elements at all should reach the SDK on the publish call.
        var publishedDoc = XDocument.Parse(capturedBodies[1]);
        Assert.Empty(publishedDoc.Descendants("Rung"));
    }

    /// <summary>
    /// The contractual test: the publish response reports how
    /// many rungs were already staged at the target insert position, how many
    /// rungs the publish's own fragment carried, and how many of those were
    /// dropped as duplicates — proving genuinely new content in the same fragment
    /// survives dedup while the duplicate does not.
    /// </summary>
    [Fact]
    public async Task Response_ReportsStagedFragmentAndDroppedCounts()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var capturedBodies = new List<string>();
        fake.XmlFileContentCaptor = body => capturedBodies.Add(body);

        // Stage one rung with target="workstation" at insertPosition 0.
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation");

        // Reflect the staged rung as a pending I-type rung so the post-publish
        // census check passes.
        fake.ExportContent = StagedCoilPendingExport;

        // Publish with target="controller" and a two-rung fragment at insertPosition 1,
        // just past the staged range [0..0] (a fragment at 0 is now refused with
        // StagedPositionConflict). Nothing is staged at
        // position 1, so nothing is dropped as a duplicate.
        var publishResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 0, DuplicatePlusNewRungFragment, session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(publishResult);

        // Numeric fields on the response (bd acceptance criterion).
        Assert.Equal(0, success.StagedRungs);
        Assert.Equal(2, success.FragmentRungs);
        Assert.Equal(0, success.DroppedDuplicates);

        // Both fragment rungs reach the SDK on the publish call.
        var publishedDoc = XDocument.Parse(capturedBodies[1]);
        var rungTexts = publishedDoc.Descendants("Rung")
            .Select(r => r.Element("Text")?.Value)
            .ToList();
        Assert.Equal(2, rungTexts.Count);
        Assert.Contains("NewCoil", rungTexts[1]);
    }

    // ── This change contractual tests ────────────────────────────────────
    // These three tests verify that the empty-fragment controller publish path
    // now uses a real PartialExportToXmlFileAsync call (not a hand-built
    // RSLogix5000Content root) and that the resulting envelope carries all
    // SDK-required attributes.

    /// <summary>
    /// Contractual test: when target="controller" is called
    /// with no l5xFragment, the handler obtains the publish envelope from a real
    /// <see cref="ILogixProject.PartialExportToXmlFileAsync"/> call — not by
    /// hand-building an <c>RSLogix5000Content</c> root. The envelope must carry
    /// attributes that only come from the SDK export (e.g. <c>TargetName</c>,
    /// <c>TargetType</c>, <c>SoftwareRevision</c>) and must have no
    /// <c>&lt;Rung&gt;</c> elements of its own.
    /// </summary>
    [Fact]
    public async Task EmptyPublish_BuildsEnvelopeFromRoutineExport_NotSynthesised()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        // Override with a recognisable export that carries SDK-only attributes.
        const string exportWithSdkAttributes =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
            "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
            "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" " +
            "ContainsContext=\"true\">" +
            "<Controller Name=\"DemoController\">" +
            "<Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Use=\"Target\" Name=\"MainRoutine\" Type=\"RLL\">" +
            "<RLLContent>" +
            "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
            "</RLLContent>" +
            "</Routine></Routines></Program></Programs>" +
            "</Controller>" +
            "</RSLogix5000Content>";
        fake.ExportContent = exportWithSdkAttributes;

        var capturedBodies = new List<string>();
        fake.XmlFileContentCaptor = body => capturedBodies.Add(body);
        session.StagedRungCount = 1;   // something is staged, so the empty publish is not a NothingStaged refusal

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        // Publish must succeed.
        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("proposed", success.Status);

        // The export path was used: the name resolution (an earlier change: Controller/Programs), the envelope
        // export and the commit-position read (an earlier change: the rung count of the session image), plus the
        // program-scoped pending-edit pre-check read (logix-mcp-h50h.6).
        Assert.Equal(4, fake.ExportCallCount);
        Assert.Equal(ValidXPath, fake.LastExportXPath);

        // One SDK import call was made (the controller publish).
        Assert.Single(capturedBodies);

        // The envelope came from the real export, not a synthesised build.
        // A hand-built envelope would carry no TargetName/TargetType/SoftwareRevision.
        var doc  = XDocument.Parse(capturedBodies[0]);
        var root = doc.Root!;
        Assert.Equal("RSLogix5000Content", root.Name.LocalName);
        Assert.NotNull(root.Attribute("TargetName"));
        Assert.NotNull(root.Attribute("TargetType"));
        Assert.NotNull(root.Attribute("SoftwareRevision"));

        // RLLContent is present but the rungs were stripped (empty publish = no new rungs).
        Assert.NotEmpty(doc.Descendants("RLLContent"));
        Assert.Empty(doc.Descendants("Rung"));
    }

    /// <summary>
    /// Contractual test: the envelope produced by the empty-
    /// fragment controller publish path carries <c>SchemaRevision</c> and
    /// <c>SoftwareRevision</c> attributes on the <c>RSLogix5000Content</c> root —
    /// both come from the live SDK export and would be absent from any hand-built
    /// skeleton.
    /// </summary>
    [Fact]
    public async Task EmptyPublish_EnvelopeCarriesSchemaAndSoftwareRevision()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        const string exportWithRevisions =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
            "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
            "TargetName=\"MainRoutine\" TargetType=\"Routine\">" +
            "<Controller Name=\"DemoController\">" +
            "<Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Use=\"Target\" Name=\"MainRoutine\" Type=\"RLL\">" +
            "<RLLContent/>" +
            "</Routine></Routines></Program></Programs>" +
            "</Controller>" +
            "</RSLogix5000Content>";
        fake.ExportContent = exportWithRevisions;

        var capturedBodies = new List<string>();
        fake.XmlFileContentCaptor = body => capturedBodies.Add(body);
        session.StagedRungCount = 1;   // something is staged, so the empty publish is not a NothingStaged refusal

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, null, session, target: "controller");

        Assert.IsType<ProposeRungEditResult>(result);
        Assert.Single(capturedBodies);

        var root = XDocument.Parse(capturedBodies[0]).Root!;
        Assert.Equal("1.0",   (string?)root.Attribute("SchemaRevision"));
        Assert.Equal("36.00", (string?)root.Attribute("SoftwareRevision"));
    }

    // ── This change contractual tests ──────────────────────────────────────
    // These tests verify that the tag-delta computation unions the caller's
    // fragment tag names with any buffer-flushed tag names so that tagsCreated
    // reflects the full set of tags that land on the controller, AND that
    // buffer-flushed tags are registered with the session self-grant guard so
    // that logix_write to them in the same session is denied.

    /// <summary>
    /// Contractual test: when target="controller" is called with
    /// an empty fragment but the session's StagedTagBuffer holds a tag, the publish
    /// response's <c>tagsCreated</c> list must include that buffer-flushed tag name.
    /// </summary>
    [Fact]
    public async Task EmptyPublish_TagsCreated_IncludesBufferFlushedTags()
    {
        var (session, _) = await BuildOpenSessionAsync();
        session.StagedTagBuffer["BufTag"] =
            new XElement("Tag", new XAttribute("Name", "BufTag"), new XAttribute("DataType", "BOOL"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.NotNull(success.TagsCreated);
        Assert.Contains(success.TagsCreated!, e => string.Equals(e.TagName, "BufTag", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Contractual test: when the caller's fragment carries no
    /// <c>&lt;Tags&gt;</c> block but the session's StagedTagBuffer holds a tag, the
    /// publish response's <c>tagsCreated</c> list must include that buffer-flushed
    /// tag name.
    /// </summary>
    [Fact]
    public async Task FragmentWithoutTagDecl_TagsCreated_IncludesBufferFlushedTags()
    {
        var (session, _) = await BuildOpenSessionAsync();
        session.StagedTagBuffer["BufOnlyTag"] =
            new XElement("Tag", new XAttribute("Name", "BufOnlyTag"), new XAttribute("DataType", "DINT"));

        // Fragment with a rung but no <Tags> element.
        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, StagedRungFragment, session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.NotNull(success.TagsCreated);
        Assert.Contains(success.TagsCreated!, e => string.Equals(e.TagName, "BufOnlyTag", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Contractual test: when a buffer-flushed tag name is
    /// already present in <c>preExistingTagNames</c> it must appear in
    /// <c>contextTags</c> (not <c>tagsCreated</c>), honouring the an earlier change
    /// delta semantics.
    /// </summary>
    [Fact]
    public async Task BufferFlushedTag_AlreadyInCensus_IsContextNotCreated()
    {
        var (session, _) = await BuildOpenSessionAsync();
        session.StagedTagBuffer["PreExistingTag"] =
            new XElement("Tag", new XAttribute("Name", "PreExistingTag"), new XAttribute("DataType", "BOOL"));

        var preExisting = new List<string> { "PreExistingTag" };

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller",
            preExistingTagNames: preExisting);

        var success = Assert.IsType<ProposeRungEditResult>(result);

        // Must NOT appear in tagsCreated.
        if (success.TagsCreated is not null)
            Assert.DoesNotContain(success.TagsCreated, e => string.Equals(e.TagName, "PreExistingTag", StringComparison.OrdinalIgnoreCase));

        // Must appear in contextTags.
        Assert.NotNull(success.ContextTags);
        Assert.Contains(success.ContextTags!, n => string.Equals(n, "PreExistingTag", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Contractual test: after a controller publish that
    /// flushes a buffered tag, the tag must be registered in the session
    /// self-grant guard (<see cref="TagSessionService"/>) so that a subsequent
    /// <c>logix_write</c> to it in the same session returns
    /// <see cref="TagServiceErrorKind.WriteDenied"/> with reason
    /// <c>CreatedByThisSession</c> and the proposal id as provenance.
    ///
    /// <para>
    /// This exercises the full call chain:
    /// <see cref="LogixSdkTools.GetProposeRungEditResponseAsync"/> →
    /// <see cref="ProposeRungEditHandler.ExecuteAsync"/> (builds
    /// <c>publishedTagEntries</c> from <c>mergedStagedTagNames</c>) →
    /// <see cref="TagSessionService.RecordCreatedTags"/> →
    /// <see cref="WriteTagHandler.ExecuteAsync"/> (session-created guard fires).
    /// </para>
    /// </summary>
    [Fact]
    public async Task BufferFlushedTag_IsRegisteredCreatedByThisSession()
    {
        var (session, _) = await BuildOpenSessionAsync();
        session.StagedTagBuffer["BufTag"] =
            new XElement("Tag", new XAttribute("Name", "BufTag"), new XAttribute("DataType", "BOOL"));

        // Pre-attach an inner service so the session is connected.
        // Attach BEFORE the publish so the registry is not cleared afterward.
        var innerService = new VirtualTagService(
            new Dictionary<string, (object, string?)> { ["BufTag"] = (false, "BOOL") });
        var tagSessionService = new TagSessionService();
        tagSessionService.Attach(innerService, isVirtual: false);

        var tools = new LogixSdkTools(new ProjectService(), tagSessionService, new Capabilities(LiveWritesEnabled: true, PublishMode: PublishMode.Auto), new StudioConfirmWait());

        // Controller publish: BufTag comes from the staged buffer (no fragment
        // tags).  GetProposeRungEditResponseAsync must call RecordCreatedTags
        // with "BufTag" so the self-grant guard is armed for this session.
        await tools.GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        // (1) The session self-grant registry must now hold BufTag.
        Assert.True(
            tagSessionService.TryGetSessionCreatedTagEntry("BufTag", out var regEntry),
            "Expected BufTag to be registered in the session-created tag registry " +
            "after a controller publish that flushed it from the StagedTagBuffer.");
        Assert.NotNull(regEntry);
        var proposalId = regEntry!.ProposalId;
        Assert.NotEmpty(proposalId);

        // (2) logix_write to BufTag in the same session must return WriteDenied
        //     with CreatedByThisSession reason and the proposal id as provenance.
        var writeResult = await WriteTagHandler.ExecuteAsync("BufTag", "1", tagSessionService);
        var writeError  = Assert.IsType<WriteTagError>(writeResult);
        Assert.Equal(nameof(TagServiceErrorKind.WriteDenied), writeError.Kind);
        Assert.Contains(
            "CreatedByThisSession", writeError.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(proposalId, writeError.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Supplementary test: the <c>writableViaLogixWrite</c>
    /// flag on the <see cref="ProposeRungEditResult.TagsCreated"/> entry for a
    /// buffer-flushed tag must be <c>false</c>, consistent with the session
    /// self-grant guard that blocks writes for the session lifetime (an earlier change
    /// requirement 3).
    /// </summary>
    [Fact]
    public async Task BufferFlushedTag_WritableViaLogixWrite_IsFalse()
    {
        var (session, _) = await BuildOpenSessionAsync();
        session.StagedTagBuffer["BufTag2"] =
            new XElement("Tag", new XAttribute("Name", "BufTag2"), new XAttribute("DataType", "DINT"));

        // Call the handler directly so we can inspect the typed result object.
        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.NotNull(success.TagsCreated);

        var entry = success.TagsCreated!.FirstOrDefault(e =>
            string.Equals(e.TagName, "BufTag2", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry);
        Assert.False(
            entry!.WritableViaLogixWrite,
            "writableViaLogixWrite must be false for a buffer-flushed tag: the " +
            "CreatedByThisSession guard blocks logix_write for the session lifetime.");
    }

    /// <summary>
    /// Contractual test: when a buffer-flushed tag is committed
    /// to the controller during a publish, the audit log contains a
    /// <c>tags_created</c> event for that tag carrying the publish's
    /// <c>proposal_id</c>.  The event shape is identical to those emitted for
    /// fragment-declared tags created in the same publish call (audit parity).
    /// </summary>
    [Fact]
    public async Task BufferFlushedTag_EmitsTagsCreatedAudit_WithPublishProposalId()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"audit-buf-flush-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var (session, _) = await BuildOpenSessionAsync();

            // ── Step 1: workstation stage that declares a tag ─────────────────
            // This emits a tags_staged audit event carrying the staging call's
            // proposal_id (hereafter "staging_id").
            const string fragmentWithStagedTag =
                "<RSLogix5000Content><Controller>" +
                  "<Tags Use=\"Context\"><Tag Name=\"StagedAuditTag\" DataType=\"BOOL\" /></Tags>" +
                  "<Programs><Program Name=\"MainProgram\"><Routines>" +
                  "<Routine Name=\"MainRoutine\"><RLLContent>" +
                  "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[XIC(StagedAuditTag)NOP();]]></Text></Rung>" +
                  "</RLLContent></Routine></Routines></Program></Programs>" +
                "</Controller></RSLogix5000Content>";

            await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, fragmentWithStagedTag, session, target: "workstation");

            // StagedAuditTag is now in the session buffer via the strip step above.
            // Add an extra tag directly — simulates a tag staged in an earlier call
            // that carries no corresponding rung in the current publish fragment.
            const string extraBufTagName = "ExtraBufferedAuditTag";
            session.StagedTagBuffer[extraBufTagName] =
                new XElement("Tag",
                    new XAttribute("Name",     extraBufTagName),
                    new XAttribute("DataType", "DINT"));

            // ── Step 2: controller publish (empty fragment) ───────────────────
            // Both buffer tags land on the controller via MergeBufferedTagsIntoFragment.
            // The publish generates a fresh proposal_id — distinct from staging_id.
            await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, "", session, target: "controller");

            // ── Verify audit log ──────────────────────────────────────────────
            var auditPath = Path.Combine(tempDir, "audit.jsonl");
            Assert.True(File.Exists(auditPath),
                "Audit file must exist after a successful controller publish.");

            var allEvents = File.ReadAllLines(auditPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .ToList();

            var stagedEvents = allEvents
                .Where(e => e.TryGetProperty("event", out var ev) && ev.GetString() == "tags_staged")
                .ToList();
            var createdEvents = allEvents
                .Where(e => e.TryGetProperty("event", out var ev) && ev.GetString() == "tags_created")
                .ToList();

            // The workstation stage must have emitted at least one tags_staged event.
            Assert.NotEmpty(stagedEvents);

            // The extra buffer-flushed tag must appear in a tags_created event.
            var bufTagEvent = createdEvents.FirstOrDefault(e =>
                e.TryGetProperty("tag", out var tn) &&
                string.Equals(tn.GetString(), extraBufTagName, StringComparison.OrdinalIgnoreCase));

            Assert.True(
                bufTagEvent.ValueKind != System.Text.Json.JsonValueKind.Undefined,
                $"Expected a tags_created audit event for buffer-flushed tag '{extraBufTagName}'.");

            // The event must carry the publish's proposal_id (non-empty).
            Assert.True(bufTagEvent.TryGetProperty("proposal_id", out var publishPidProp),
                "tags_created event for a buffer-flushed tag must carry a 'proposal_id' field.");
            var publishProposalId = publishPidProp.GetString()!;
            Assert.NotEmpty(publishProposalId);

            // The publish's proposal_id must differ from the staging call's proposal_id:
            // each ExecuteAsync invocation generates a fresh GUID — proposal_ids are
            // never shared across calls.
            var stagingProposalId = stagedEvents[0].GetProperty("proposal_id").GetString()!;
            Assert.NotEqual(stagingProposalId, publishProposalId);

            // Audit parity: the buffer-flushed tag event must carry the same fields as
            // a fragment-declared tag event: ts, event, proposal_id, tag, granted_namespace.
            Assert.True(bufTagEvent.TryGetProperty("ts",                out _),
                "tags_created event must carry a 'ts' timestamp field.");
            Assert.True(bufTagEvent.TryGetProperty("granted_namespace", out _),
                "tags_created event must carry a 'granted_namespace' field.");

            // All tags_created events from the controller publish share the same
            // proposal_id, whether the tag came from the fragment or the buffer.
            foreach (var ev in createdEvents)
            {
                Assert.True(ev.TryGetProperty("proposal_id", out var pid));
                Assert.Equal(publishProposalId, pid.GetString());
            }
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Contractual static-analysis test: no source file in
    /// <c>src/LogixMcp.SdkAdapter/</c> may contain a
    /// <c>new XElement("RSLogix5000Content"</c> expression — the only way that
    /// pattern appears is if code is hand-building the L5X root instead of
    /// obtaining it from <see cref="ILogixProject.PartialExportToXmlFileAsync"/>.
    /// After the deletion of <c>BuildEmptyRllContentEnvelope</c> this search
    /// must return zero hits.
    /// </summary>
    [Fact]
    public void NoCodePath_SynthesisesRSLogix5000ContentRoot()
    {
        // Walk up from the test output directory until we find the .sln file,
        // which marks the repository root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.GetFiles("*.sln").Length == 0)
            dir = dir.Parent;

        Assert.NotNull(dir); // sln must be findable

        var srcAdapterDir = Path.Combine(dir!.FullName, "src", "LogixMcp.SdkAdapter");
        Assert.True(Directory.Exists(srcAdapterDir),
            $"Expected SdkAdapter source directory at '{srcAdapterDir}'.");

        const string pattern = "new XElement(\"RSLogix5000Content\"";

        var hits = Directory
            .EnumerateFiles(srcAdapterDir, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file =>
                File.ReadAllLines(file)
                    .Select((line, i) => (file: Path.GetFileName(file), lineNo: i + 1, line))
                    .Where(t => t.line.Contains(pattern, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            hits.Count == 0,
            "Found hand-built RSLogix5000Content root(s) — delete them and use " +
            "PartialExportToXmlFileAsync instead:\n" +
            string.Join("\n", hits.Select(h => $"  {h.file}:{h.lineNo}: {h.line.Trim()}")));
    }

    // ── This change contractual tests ────────────────────────────────────
    // These three tests verify that the SDK import is called with replaceCount=0
    // when the publish fragment carries no new rungs (either because l5xFragment
    // was empty or all fragment rungs were dropped as staged duplicates), and that
    // the caller's replaceCount is kept when new rungs are present.

    /// <summary>
    /// Contractual test: stage a rung with
    /// <c>target="workstation"</c> using <c>replaceCount=1</c>, then publish with
    /// <c>target="controller"</c> and an EMPTY <c>l5xFragment</c> also carrying
    /// <c>replaceCount=1</c>.  The staged image already contains the replacement
    /// from the workstation stage; re-applying <c>replaceCount=1</c> in the
    /// AcceptEdits call would overwrite the staged rung.  The SDK import must
    /// receive <c>replaceCount=0</c>, and the response must include
    /// <c>stagedReplaceCount=1</c> and a <c>note</c> explaining the override.
    /// </summary>
    [Fact]
    public async Task EmptyPublish_AfterReplaceStage_ImportsWithReplaceCountZero()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        // Stage a rung with replaceCount=1 (target="workstation").
        var stageResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, insertPosition: 0, replaceCount: 1, StagedRungFragment,
            session, target: "workstation");
        Assert.IsType<ProposeRungEditResult>(stageResult);

        // Reflect the staged rung as a pending I-type rung so the post-publish
        // census check passes.
        fake.ExportContent = StagedCoilPendingExport;

        // Publish with an EMPTY fragment and replaceCount=1 (target="controller").
        var publishResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, insertPosition: 0, replaceCount: 1, "",
            session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(publishResult);

        // The SDK publish call must have received replaceCount=0, not 1.
        Assert.Equal(2, fake.CapturedReplaceCounts.Count);
        Assert.Equal(1u, fake.CapturedReplaceCounts[0]); // staging call keeps caller value
        Assert.Equal(0u, fake.CapturedReplaceCounts[1]); // publish call overrides to 0

        // The response reports the rungs the publish replaced: the one the workstation stage replaced (i3b6.4).
        Assert.Equal(1u, success.ReplaceCount);

        // The response preserves the caller's original replaceCount for transparency.
        Assert.Equal(1u, success.StagedReplaceCount);

        // A note explains the override.
        Assert.NotNull(success.Note);
        Assert.NotEmpty(success.Note!);
    }

    /// <summary>
    /// Contractual test: stage a rung with
    /// <c>target="workstation"</c>, then publish with <c>target="controller"</c>
    /// resending the IDENTICAL fragment with <c>replaceCount=2</c>.  All fragment
    /// rungs are dropped as staged duplicates, leaving no new rungs in the outgoing
    /// fragment.  The SDK import must receive <c>replaceCount=0</c>, and the
    /// response must include <c>stagedReplaceCount=2</c> and a <c>note</c>.
    /// </summary>
    [Fact]
    public async Task AllDuplicatePublish_AfterReplaceStage_ImportsWithReplaceCountZero()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        // Stage the rung with replaceCount=0 (target="workstation").
        var stageResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, insertPosition: 0, replaceCount: 0, StagedRungFragment,
            session, target: "workstation");
        Assert.IsType<ProposeRungEditResult>(stageResult);

        // Reflect the staged rung as a pending I-type rung so the post-publish
        // census check passes.
        fake.ExportContent = StagedCoilPendingExport;

        // Publish by resending the IDENTICAL fragment with replaceCount=2
        // (simulating a caller who believes they need to replace 2 existing rungs).
        var publishResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, insertPosition: 0, replaceCount: 2, StagedRungFragment,
            session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(publishResult);

        // All fragment rungs were duplicates and were dropped → SDK must receive replaceCount=0.
        Assert.Equal(2, fake.CapturedReplaceCounts.Count);
        Assert.Equal(0u, fake.CapturedReplaceCounts[0]); // staging call
        Assert.Equal(0u, fake.CapturedReplaceCounts[1]); // publish call overrides to 0

        // The response reflects the effective replaceCount (0).
        Assert.Equal(0u, success.ReplaceCount);

        // The response preserves the caller's original replaceCount.
        Assert.Equal(2u, success.StagedReplaceCount);

        // A note is present.
        Assert.NotNull(success.Note);
        Assert.NotEmpty(success.Note!);

        // Dedup counts: 1 fragment rung, 1 dropped duplicate.
        Assert.Equal(1, success.DroppedDuplicates);
    }

    // ── This change contractual tests ────────────────────────────────────
    // These two tests verify the post-publish pending census verification:
    // after AcceptEdits, the handler exports the routine and verifies
    // that every staged rung appears as a pending I/IR rung in the export.  On
    // a mismatch it must return ProposeRungEditMismatch (kind='PublishMismatch');
    // on a successful match it must include verifiedPendingRungs in the
    // ProposeRungEditResult and must never return status='proposed' on mismatch.

    /// <summary>
    /// Contractual test: stage a rung with
    /// <c>target="workstation"</c>, then publish with <c>target="controller"</c>
    /// WITHOUT setting the export content to reflect the staged rung as a pending
    /// I-type rung.  The post-publish census reads back no I/IR rungs (the fake's
    /// default export contains only a NOP <c>Type="N"</c> rung), so the handler
    /// must return <see cref="ProposeRungEditMismatch"/> with
    /// <c>kind='PublishMismatch'</c>, a non-empty <c>expectedRungs</c> list
    /// containing the staged rung text, and a <c>pendingRungs</c> list reflecting
    /// the actual I/IR rungs found (none in this case).  The handler must never
    /// return <c>status='proposed'</c> in this situation.
    /// </summary>
    [Fact]
    public async Task Publish_PendingCensusMissingStagedRung_ReturnsPublishMismatch()
    {
        var (session, _) = await BuildOpenSessionAsync();

        // Stage a rung with target="workstation" (adds to StagedRungBuffer).
        var stageResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation");
        Assert.IsType<ProposeRungEditResult>(stageResult);

        // Do NOT set fake.ExportContent — the default export contains only a
        // NOP Type="N" rung. The post-publish census will find zero I/IR pending
        // rungs, which does not match the one staged rung → triggers mismatch.

        // Publish with target="controller" and an empty fragment.
        var publishResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        // Must return ProposeRungEditMismatch — never status='proposed'.
        var mismatch = Assert.IsType<ProposeRungEditMismatch>(publishResult);
        Assert.Equal("PublishMismatch", mismatch.Kind);

        // expectedRungs must contain the staged rung text.
        Assert.NotEmpty(mismatch.ExpectedRungs);

        // pendingRungs reflects whatever I/IR rungs were actually found (empty here).
        Assert.NotNull(mismatch.PendingRungs);
    }

    /// <summary>
    /// Contractual test: stage a rung with
    /// <c>target="workstation"</c>, set the export content to reflect the staged
    /// rung as a pending <c>Type="I"</c> rung (simulating a successful
    /// AcceptEdits), then publish with <c>target="controller"</c>.  The
    /// post-publish census finds the staged rung text among the I-type rungs —
    /// the match succeeds — and the handler must return
    /// <see cref="ProposeRungEditResult"/> with <c>verifiedPendingRungs</c> equal
    /// to the number of confirmed pending rungs (1 in this case).
    /// </summary>
    [Fact]
    public async Task Publish_PendingCensusMatches_ReportsVerifiedPendingRungs()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        // Stage a rung with target="workstation".
        var stageResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation");
        Assert.IsType<ProposeRungEditResult>(stageResult);

        // Set the export content to reflect the staged rung as a pending I-type
        // rung so the post-publish census check passes.
        fake.ExportContent = StagedCoilPendingExport;

        // Publish with target="controller" and an empty fragment.
        var publishResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        // Must return ProposeRungEditResult (not mismatch).
        var success = Assert.IsType<ProposeRungEditResult>(publishResult);
        Assert.Equal("proposed", success.Status);

        // verifiedPendingRungs must equal the number of confirmed pending rungs (1).
        Assert.NotNull(success.VerifiedPendingRungs);
        Assert.Equal(1, success.VerifiedPendingRungs);
    }

    /// <summary>
    /// Contractual test: when the publish fragment carries at
    /// least one genuinely new rung (not a staged duplicate), the caller's
    /// <c>replaceCount</c> is forwarded to the SDK unchanged.  Neither
    /// <c>stagedReplaceCount</c> nor <c>note</c> appears in the response.
    /// </summary>
    [Fact]
    public async Task Publish_WithFragmentRungs_KeepsCallerReplaceCount()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        // Stage one rung at position 0 (target="workstation").
        var stageResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, insertPosition: 0, replaceCount: 0, StagedRungFragment,
            session, target: "workstation");
        Assert.IsType<ProposeRungEditResult>(stageResult);

        // Reflect the staged rung as a pending I-type rung so the post-publish
        // census check passes.
        fake.ExportContent = StagedCoilPendingExport;

        // Publish a two-rung fragment just past the staged range [0..0] (position 0
        // is refused with StagedPositionConflict). replaceCount=3 must reach the SDK unchanged.
        var publishResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, insertPosition: 1, replaceCount: 3, DuplicatePlusNewRungFragment,
            session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(publishResult);

        // The SDK publish call must have received replaceCount=3 (caller's value).
        Assert.Equal(2, fake.CapturedReplaceCounts.Count);
        Assert.Equal(0u, fake.CapturedReplaceCounts[0]); // staging call
        Assert.Equal(3u, fake.CapturedReplaceCounts[1]); // publish call keeps caller value

        // The response echoes the caller's replaceCount.
        Assert.Equal(3u, success.ReplaceCount);

        // No override occurred — stagedReplaceCount and note must be absent.
        Assert.Null(success.StagedReplaceCount);
        Assert.Null(success.Note);
    }

    // ── Per-routine post-publish census ─

    private const string SecondXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='SecondRoutine']";

    private static string RoutineFragment(string routine, string text) =>
        "<RSLogix5000Content><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        $"<Routine Name=\"{routine}\"><RLLContent><Rung Number=\"0\" Type=\"N\">" +
        $"<Text><![CDATA[{text}]]></Text></Rung></RLLContent></Routine>" +
        "</Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static string RoutineExport(string routine, string type, string text) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
        $"TargetName=\"{routine}\" TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\">" +
        "<Controller Name=\"DemoController\"><Programs><Program Name=\"MainProgram\"><Routines>" +
        $"<Routine Use=\"Target\" Name=\"{routine}\" Type=\"RLL\"><RLLContent>" +
        $"<Rung Number=\"0\" Type=\"{type}\"><Text><![CDATA[{text}]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    /// <summary>
    /// Two routines each hold a staged rung. The no-fragment publish re-exports both, compares each
    /// against its own expected text, and reports one publishedRoutines entry per routine.
    /// </summary>
    [Fact]
    public async Task Publish_TwoStagedRoutines_CensusSucceedsPerRoutine()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ReflectAcceptedImportsInExport = false;
        fake.ExportContentByXPath[ProposeRungEditRecordingFake.ProgramsXPath] =
            ProposeRungEditRecordingFake.ProgramsExport("MainRoutine", "SecondRoutine");

        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, RoutineFragment("MainRoutine", "XIC(A)OTE(B);"), session, target: "workstation"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            SecondXPath, 0, 0, RoutineFragment("SecondRoutine", "XIC(C)OTE(D);"), session, target: "workstation"));

        fake.ExportContentByXPath[ValidXPath]  = RoutineExport("MainRoutine",   "I", "XIC(A)OTE(B);");
        fake.ExportContentByXPath[SecondXPath] = RoutineExport("SecondRoutine", "I", "XIC(C)OTE(D);");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("verified", success.Verification);
        Assert.NotNull(success.PublishedRoutines);
        Assert.Equal(2, success.PublishedRoutines!.Count);
        Assert.Contains(success.PublishedRoutines, r => r.Routine == ValidXPath  && r.InsertedOrReplacing == 1);
        Assert.Contains(success.PublishedRoutines, r => r.Routine == SecondXPath && r.InsertedOrReplacing == 1);
        Assert.Equal(2, success.VerifiedPendingRungs);
        Assert.Contains(SecondXPath, fake.ExportXPaths);
    }

    /// <summary>
    /// Two routines are staged; only the second's export lacks its pending rung. The mismatch names
    /// exactly that routine, with its own expected and found texts, and not the matching routine.
    /// </summary>
    [Fact]
    public async Task Publish_OneRoutineDiffers_MismatchNamesOnlyThatRoutine()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ReflectAcceptedImportsInExport = false;
        fake.ExportContentByXPath[ProposeRungEditRecordingFake.ProgramsXPath] =
            ProposeRungEditRecordingFake.ProgramsExport("MainRoutine", "SecondRoutine");

        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, RoutineFragment("MainRoutine", "XIC(A)OTE(B);"), session, target: "workstation"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            SecondXPath, 0, 0, RoutineFragment("SecondRoutine", "XIC(C)OTE(D);"), session, target: "workstation"));

        fake.ExportContentByXPath[ValidXPath]  = RoutineExport("MainRoutine",   "I", "XIC(A)OTE(B);");
        fake.ExportContentByXPath[SecondXPath] = RoutineExport("SecondRoutine", "I", "XIC(OTHER)OTE(D);");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        var mismatch = Assert.IsType<ProposeRungEditMismatch>(result);
        Assert.Equal("PublishMismatch", mismatch.Kind);
        var differing = Assert.Single(mismatch.Routines!);
        Assert.Equal(SecondXPath, differing.Routine);
        Assert.Equal(new[] { "XIC(C)OTE(D);" }, differing.ExpectedRungs);
        Assert.Equal(new[] { "XIC(OTHER)OTE(D);" }, differing.FoundRungs);
        Assert.Contains(SecondXPath, mismatch.Error);
        Assert.DoesNotContain(ValidXPath, mismatch.Error);
    }

    // ── Server-chosen commit position ─

    /// <summary>
    /// Builds a routine export whose rungs are the given (type, text) pairs, in order —
    /// the session image the handler counts to find the end of the routine.
    /// </summary>
    private static string ImageExport(params (string Type, string Text)[] rungs)
    {
        var body = string.Concat(rungs.Select((r, i) =>
            $"<Rung Number=\"{i}\" Type=\"{r.Type}\"><Text><![CDATA[{r.Text}]]></Text></Rung>"));
        return
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
            "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
            "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" " +
            "ContainsContext=\"true\">" +
            "<Controller Name=\"DemoController\">" +
            "<Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Use=\"Target\" Name=\"MainRoutine\" Type=\"RLL\">" +
            $"<RLLContent>{body}</RLLContent>" +
            "</Routine></Routines></Program></Programs>" +
            "</Controller></RSLogix5000Content>";
    }

    /// <summary>
    /// Staged replace at 0: the staged new rung sits AT the caller's insertPosition 0 in the
    /// image, so importing there would drop it. The server must commit at the end of the
    /// routine image (3 rungs: staged I, replaced original R, an untouched N) with
    /// replaceCount 0, and report the caller's position as ignoredInsertPosition.
    /// </summary>
    [Fact]
    public async Task StagedReplaceAtZero_SelectsEndOfRoutinePosition()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var stageResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, insertPosition: 0, replaceCount: 1, StagedRungFragment,
            session, target: "workstation");
        Assert.IsType<ProposeRungEditResult>(stageResult);

        fake.ExportContent = ImageExport(
            ("I", "XIC(StagedTag)OTE(StagedCoil);"),
            ("R", "XIC(OldTag)OTE(OldCoil);"),
            ("N", "XIC(OtherTag)OTE(OtherCoil);"));

        var publishResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, insertPosition: 0, replaceCount: 0, "",
            session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(publishResult);
        Assert.Equal(2, fake.CapturedInsertPositions.Count);
        Assert.Equal(0u, fake.CapturedInsertPositions[0]); // staging call keeps caller position
        Assert.Equal(2u, fake.CapturedInsertPositions[1]); // publish: end of the controller's rungs (R + N)
        Assert.Equal(0u, fake.CapturedReplaceCounts[1]);
        Assert.Equal(0u, success.InsertPosition);   // derived from landedIndices: the staged rung sits at index 0
        Assert.Equal(new[] { 0 }, success.PublishedRoutines!.Single().LandedIndices);
        Assert.Equal(1u, success.ReplaceCount);   // i3b6.4: the staged replace of 1 rung, not the caller's 0
        Assert.Null(success.IgnoredInsertPosition);   // G52: honoured position, no field
        // G44: the caller's 0 equals the first landed index, so no "ignored" sentence is emitted.
        Assert.True(success.Note is null || !success.Note.Contains("was ignored"));
    }

    /// <summary>
    /// Staged insert at the end: the caller's insertPosition (1) is the staged rung's own slot
    /// in a 2-rung image. The server must still pick the end of the controller's rungs (1), not position 1.
    /// </summary>
    [Fact]
    public async Task StagedInsertAtEnd_SelectsEndOfRoutinePosition()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var stageResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, insertPosition: 1, replaceCount: 0, StagedRungFragment,
            session, target: "workstation");
        Assert.IsType<ProposeRungEditResult>(stageResult);

        fake.ExportContent = ImageExport(
            ("N", "XIC(OtherTag)OTE(OtherCoil);"),
            ("I", "XIC(StagedTag)OTE(StagedCoil);"));

        var publishResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, insertPosition: 1, replaceCount: 0, "",
            session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(publishResult);
        Assert.Equal(1u, fake.CapturedInsertPositions[1]); // controller has 1 rung; the staged one is image-only
        Assert.Equal(0u, fake.CapturedReplaceCounts[1]);
        Assert.Equal(1u, success.InsertPosition);
        Assert.Null(success.IgnoredInsertPosition);   // G52: honoured position, no field
        Assert.True(success.Note is null || !success.Note.Contains("was ignored"));   // landed at 1 == caller's 1
    }

    /// <summary>
    /// Nothing staged in the call's routine: the position is still the end of the routine
    /// image (the call's routine here has 4 rungs), whatever insertPosition the caller passed.
    /// </summary>
    [Fact]
    public async Task NothingStagedInCallsRoutine_SelectsEndOfRoutinePosition()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        fake.ExportContent = ImageExport(
            ("N", "XIC(A)OTE(B);"), ("N", "XIC(C)OTE(D);"),
            ("N", "XIC(E)OTE(F);"), ("N", "XIC(G)OTE(H);"));
        session.StagedRungCount = 1;   // staged work exists elsewhere, so the empty publish is not a NothingStaged refusal

        var publishResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, insertPosition: 0, replaceCount: 0, "",
            session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(publishResult);
        Assert.Equal(4u, Assert.Single(fake.CapturedInsertPositions));
        Assert.Equal(0u, Assert.Single(fake.CapturedReplaceCounts));
        Assert.Equal(4u, success.InsertPosition);
        Assert.Equal(0u, success.IgnoredInsertPosition);
    }

    /// <summary>
    /// This change end to end: 6 controller rungs + 2 rungs staged at the end (8 in the image).
    /// The empty publish must import at 6 (the controller's end), and the rung_edit audit
    /// event's insert_position must record that same imported position.
    /// </summary>
    [Fact]
    public async Task StagedInsertsAfterControllerRungs_CommitAtControllerCount_AndAuditRecordsIt()
    {
        var auditDir = Path.Combine(Path.GetTempPath(), $"staged-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(auditDir);
        AuditLogger.OverrideLogDir(auditDir);
        try
        {
            var (session, fake) = await BuildOpenSessionAsync();

            var stageResult = await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, insertPosition: 6, replaceCount: 0, StagedRungFragment,
                session, target: "workstation");
            Assert.IsType<ProposeRungEditResult>(stageResult);
            var secondStage = await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, insertPosition: 7, replaceCount: 0,
                StagedRungFragment.Replace("StagedTag", "StagedTag2"),
                session, target: "workstation");
            Assert.IsType<ProposeRungEditResult>(secondStage);

            fake.ExportContent = ImageExport(
                ("N", "XIC(A)OTE(B);"), ("N", "XIC(C)OTE(D);"), ("N", "XIC(E)OTE(F);"),
                ("N", "XIC(G)OTE(H);"), ("N", "XIC(I1)OTE(J);"), ("N", "XIC(K)OTE(L);"),
                ("I", "XIC(StagedTag)OTE(StagedCoil);"), ("I", "XIC(StagedTag2)OTE(StagedCoil);"));

            var publishResult = await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, insertPosition: 0, replaceCount: 0, "",
                session, target: "controller");

            var success = Assert.IsType<ProposeRungEditResult>(publishResult);
            Assert.Equal(6u, fake.CapturedInsertPositions[^1]);
            Assert.Equal(6u, success.InsertPosition);

            var auditLines = File.ReadAllLines(Path.Combine(auditDir, "audit.jsonl"))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => System.Text.Json.JsonDocument.Parse(l))
                .Where(d => d.RootElement.GetProperty("event").GetString() == "rung_edit")
                .ToList();
            Assert.Equal(6u, auditLines[^1].RootElement.GetProperty("insert_position").GetUInt32());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(auditDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>The position choice is the image's rung count, never negative.</summary>
    [Theory]
    [InlineData(0, 0u)]
    [InlineData(1, 1u)]
    [InlineData(10, 10u)]
    [InlineData(-1, 0u)]
    public void ChooseNoFragmentCommitPosition_IsImageRungCount(int count, uint expected) =>
        Assert.Equal(expected, ProposeRungEditHandler.ChooseNoFragmentCommitPosition(count));

    /// <summary>6 controller rungs + 7 staged rungs (13 in the image) commits at 6, not 13.</summary>
    [Fact]
    public void ChooseNoFragmentCommitPosition_SixControllerPlusSevenStaged_YieldsSix() =>
        Assert.Equal(6u, ProposeRungEditHandler.ChooseNoFragmentCommitPosition(13, 7));

    /// <summary>A routine with 0 controller rungs and 2 staged rungs (2 in the image) commits at 0.</summary>
    [Fact]
    public void ChooseNoFragmentCommitPosition_ZeroControllerPlusTwoStaged_YieldsZero() =>
        Assert.Equal(0u, ProposeRungEditHandler.ChooseNoFragmentCommitPosition(2, 2));

    /// <summary>Staged counts are summed per routine: other routines' buckets never leak in.</summary>
    [Fact]
    public void CountStagedRungsForRoutine_TwoRoutinesStagedInOneSet_EachGetsOwnCount()
    {
        var buffer = new Dictionary<(string RoutineXPath, uint InsertPosition), List<string>>
        {
            [("RoutineA", 0u)]  = new() { "a1", "a2", "a3" },
            [("RoutineA", 10u)] = new() { "a4", "a5", "a6", "a7" },
            [("RoutineB", 0u)]  = new() { "b1", "b2" },
        };

        var stagedA = ProposeRungEditHandler.CountStagedRungsForRoutine(buffer, "RoutineA");
        var stagedB = ProposeRungEditHandler.CountStagedRungsForRoutine(buffer, "RoutineB");

        Assert.Equal(7, stagedA);
        Assert.Equal(2, stagedB);
        Assert.Equal(0, ProposeRungEditHandler.CountStagedRungsForRoutine(buffer, "RoutineC"));
        // A: 6 controller + 7 staged = 13 in image; B: 3 controller + 2 staged = 5 in image.
        Assert.Equal(6u, ProposeRungEditHandler.ChooseNoFragmentCommitPosition(13, stagedA));
        Assert.Equal(3u, ProposeRungEditHandler.ChooseNoFragmentCommitPosition(5, stagedB));
    }

    // ── StagedPositionConflict ─────

    /// <summary>
    /// A fragment publish whose insertPosition falls on a staged rung in the same routine is
    /// refused with StagedPositionConflict before any import; the refusal names the staged
    /// range and advises staging the edit and publishing with no fragment.
    /// </summary>
    [Fact]
    public async Task FragmentPublish_OnStagedRung_RefusesWithStagedPositionConflict()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var imports = 0;
        fake.XmlFileContentCaptor = _ => imports++;

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation");
        Assert.Equal(1, imports);

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, DuplicatePlusNewRungFragment, session, target: "controller");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("StagedPositionConflict", error.Kind);
        Assert.Contains("0..0", error.Error);
        Assert.Contains("no fragment", error.Hint!);
        Assert.Equal(1, imports);                       // no import for the refused publish
        Assert.Single(session.StagedRungBuffer);        // staged state untouched
    }

    /// <summary>The conflict range covers insert positions inside it, not just its start or past its end.</summary>
    [Theory]
    [InlineData(5u, false)]
    [InlineData(2u, false)]
    [InlineData(3u, true)]
    [InlineData(4u, true)]
    [InlineData(5u, false, "/other")]
    public void FindStagedPositionConflicts_MatchesOnlyOnOrInsideSameRoutineRange(
        uint position, bool expectConflict, string? routine = null)
    {
        var buffer = new Dictionary<(string, uint), List<string>>
        {
            [("/r", 3u)] = new() { "a", "b" },   // occupies 3..4
        };
        var ranges = ProposeRungEditHandler.FindStagedPositionConflicts(buffer, routine ?? "/r", position);
        Assert.Equal(expectConflict, ranges.Count > 0);
        if (expectConflict) Assert.Equal("3..4", Assert.Single(ranges));
    }

    // ── logix-mcp-64f5.2: replace pairs are protected on every target ─

    /// <summary>
    /// Stages the replace pair {(ValidXPath, 0): n=1, rc=1} directly in the session buffer, as
    /// "replace rung 0" leaves it, without going through an import.
    /// </summary>
    private static void SeedReplacePair(LogixProjectSession session)
    {
        session.StagedRungBuffer[(ValidXPath, 0u)]       = new List<string> { "XIC(StagedTag)OTE(StagedCoil);" };
        session.StagedReplaceCounts[(ValidXPath, 0u)]    = 1;
        session.HasStagedRungEdit = true;
        session.StagedRungCount   = 1;
    }

    /// <summary>
    /// Buffer {(MR,0): n=1, rc=1} over an image [staged, replaced original, last original] + a controller-target insert
    /// on the staged rung is refused with StagedPositionConflict. Positions count visible rungs (logix-mcp-qpqy.3), so
    /// the replaced original is never addressable: visible 1 is the last original, which is allowed (see
    /// <c>StagedVisiblePositionTests</c>).
    /// </summary>
    [Fact]
    public async Task ReplacePair_InsertInsideReplacedRange_RefusesWithStagedPositionConflict()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var imports = 0;
        fake.XmlFileContentCaptor = _ => imports++;
        fake.ExportContent = StagedCoilPendingExport.Replace(
            "</RLLContent>",
            "<Rung Number=\"1\" Type=\"R\"><Text><![CDATA[NOP();]]></Text></Rung>" +
            "<Rung Number=\"2\" Type=\"N\"><Text><![CDATA[XIC(Last)OTE(LastOut);]]></Text></Rung></RLLContent>");
        SeedReplacePair(session);

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, StagedRungFragment.Replace("StagedCoil", "OtherCoil"), session, target: "controller");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("StagedPositionConflict", error.Kind);
        Assert.Contains("0..0", error.Error);                  // names the staged rung, in visible positions
        Assert.Contains("position 0", error.Error);            // and the new stage
        Assert.Contains("safe position is 2", error.Hint!);    // the visible rungs: staged + last original
        Assert.Equal(3, error.StagedImage!.Count);
        Assert.Equal(0, imports);
        Assert.Single(session.StagedRungBuffer);
    }

    /// <summary>The same buffer + an insert at 2 (just past the replaced original) is allowed.</summary>
    [Fact]
    public async Task ReplacePair_InsertPastReplacedRange_IsAllowed()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        SeedReplacePair(session);

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 2, 0, StagedRungFragment, session, target: "workstation");

        var error = result as ProposeRungEditError;
        Assert.True(error is null, error?.Error);
        Assert.True(session.StagedRungBuffer.ContainsKey((ValidXPath, 2u)));
    }

    /// <summary>The conflict is also detected when the new stage targets the workstation (no controller publish).</summary>
    [Fact]
    public async Task ReplacePair_ConflictIsAlsoDetectedOnTheWorkstationTarget()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var imports = 0;
        fake.XmlFileContentCaptor = _ => imports++;
        SeedReplacePair(session);

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 0, StagedRungFragment, session, target: "workstation");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("StagedPositionConflict", error.Kind);
        Assert.Equal(0, imports);
        Assert.Single(session.StagedRungBuffer);               // nothing more staged
    }

    /// <summary>A new stage whose own replace range reaches into a staged bucket conflicts too.</summary>
    [Fact]
    public void FindStagedBucketConflicts_NewReplaceRangeOverlapsExistingRange()
    {
        var buffer = new Dictionary<(string, uint), List<string>> { [("/r", 3u)] = new() { "a" } };
        var rcs    = new Dictionary<(string, uint), uint> { [("/r", 3u)] = 1 };      // occupies [3,5)

        Assert.Single(ProposeRungEditHandler.FindStagedBucketConflicts(buffer, rcs, "/r", 2, 2));  // [2,4) overlaps
        Assert.Empty(ProposeRungEditHandler.FindStagedBucketConflicts(buffer, rcs, "/r", 2, 1));   // [2,3) does not
        Assert.Empty(ProposeRungEditHandler.FindStagedBucketConflicts(buffer, rcs, "/r", 5, 0));   // past the end
        Assert.Single(ProposeRungEditHandler.FindStagedBucketConflicts(buffer, rcs, "/R", 4, 0));  // case-insensitive routine
    }

    /// <summary>The publish re-check refuses a conflicting buffer before any import (here, an empty-fragment publish).</summary>
    [Fact]
    public async Task Publish_RecheckRefusesAConflictingBuffer_BeforeAnyImport()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var imports = 0;
        fake.XmlFileContentCaptor = _ => imports++;
        fake.ExportContent = StagedCoilPendingExport;
        SeedReplacePair(session);
        // A second bucket inside the first one's range (as a stale or cross-session buffer could hold).
        session.StagedRungBuffer[(ValidXPath, 1u)] = new List<string> { "XIC(C)OTE(D);" };

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, null, session, target: "controller");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("StagedPositionConflict", error.Kind);
        Assert.Contains("conflicting buckets", error.Error);
        Assert.Equal(0, imports);
        Assert.Equal(2, session.StagedRungBuffer.Count);       // buffer untouched
    }

    // ── PublishMismatchUnresolved ───

    private const string NoPendingExport =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
        "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" " +
        "ContainsContext=\"true\">" +
        "<Controller Name=\"DemoController\">" +
        "<Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Use=\"Target\" Name=\"MainRoutine\" Type=\"RLL\">" +
        "<RLLContent>" +
        "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
        "</RLLContent>" +
        "</Routine></Routines></Program></Programs>" +
        "</Controller>" +
        "</RSLogix5000Content>";

    private static LogixSdkTools LifecycleTools() =>
        new(new ProjectService(), new TagSessionService(),
            new Capabilities(LiveWritesEnabled: true, StudioAutomationEnabled: true));

    /// <summary>Stages one rung, then publishes while the export lacks it: the census fails and the session records it.</summary>
    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)>
        SessionAfterPublishMismatchAsync()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation"));
        var publish = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");
        Assert.Equal("PublishMismatch", Assert.IsType<ProposeRungEditMismatch>(publish).Kind);
        return (session, fake);
    }

    [Fact]
    public async Task PublishMismatch_IsRecordedInTheSession()
    {
        var (session, _) = await SessionAfterPublishMismatchAsync();

        var recorded = Assert.Single(session.UnresolvedPublishMismatches);
        Assert.Equal(ValidXPath, recorded.Key);
        Assert.Contains("XIC(StagedTag)OTE(StagedCoil);", recorded.Value);
    }

    [Fact]
    public async Task PublishMismatchUnresolved_RefusesTestAndAssemble_UntilResolved()
    {
        var (session, fake) = await SessionAfterPublishMismatchAsync();
        var tools = LifecycleTools();

        var verbs = new List<string>();
        Func<string, CancellationToken, Task<Session1ScriptResult>> runner = (verb, _) =>
        {
            verbs.Add(verb);
            return Task.FromResult(new Session1ScriptResult(true, "gesture_sent", Array.Empty<string>()));
        };

        foreach (var call in new Func<Task<string>>[]
        {
            () => tools.GetTestPendingEditsResponseAsync(
                "MainProgram", "MainRoutine", true, session, runner, () => true),
            () => tools.GetAssemblePendingEditsResponseAsync(
                "MainProgram", "MainRoutine", true, session, runner, () => true),
        })
        {
            var root = System.Text.Json.JsonDocument.Parse(await call()).RootElement;
            Assert.Equal("PublishMismatchUnresolved", root.GetProperty("kind").GetString());
            Assert.Contains("PublishMismatch", root.GetProperty("error").GetString());
        }
        Assert.Empty(verbs);   // refused before any Studio command

        // A mismatch in another program does not block this one.
        Assert.Empty(session.UnresolvedPublishMismatchesForProgram("OtherProgram").Where(
            k => PendingEditsHandler.TryParseRoutineXPath(k, out var p, out _) && p == "OtherProgram"));
    }

    [Fact]
    public async Task PublishMismatchUnresolved_ClearsWhenPendingEditsShowsTheStagedZone()
    {
        var (session, fake) = await SessionAfterPublishMismatchAsync();

        // A read while the zone still lacks the staged rung leaves the mismatch in place.
        fake.ExportContent = NoPendingExport;
        Assert.IsType<PendingEditsResult>(
            await PendingEditsHandler.ExecuteAsync("MainProgram", "MainRoutine", session));
        Assert.Single(session.UnresolvedPublishMismatches);

        // A fresh read showing the staged rung as pending resolves it.
        fake.ExportContent = StagedCoilPendingExport;
        Assert.IsType<PendingEditsResult>(
            await PendingEditsHandler.ExecuteAsync("MainProgram", "MainRoutine", session));
        Assert.Empty(session.UnresolvedPublishMismatches);

        // ...and test now gets past the guard to the (fake) Studio driver.
        var verbs = new List<string>();
        var json = await LifecycleTools().GetTestPendingEditsResponseAsync(
            "MainProgram", "MainRoutine", true, session,
            (verb, _) =>
            {
                verbs.Add(verb);
                return Task.FromResult(new Session1ScriptResult(true, "gesture_sent", Array.Empty<string>()));
            },
            () => true);
        Assert.NotEqual("PublishMismatchUnresolved",
            System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("kind").GetString());
        Assert.Contains("test", verbs);
    }

    [Fact]
    public async Task PublishMismatchUnresolved_ClearsOnSuccessfulCancel()
    {
        var (session, fake) = await SessionAfterPublishMismatchAsync();
        var tools = LifecycleTools();

        // The zone holds a rung other than the staged one when the cancel starts (so the cancel's own
        // pending-edits read does not resolve the mismatch); the (fake) cancel gesture removes it.
        fake.ExportContent = StagedCoilPendingExport.Replace(
            "XIC(StagedTag)OTE(StagedCoil);", "XIC(Other)OTE(OtherCoil);");
        Func<string, CancellationToken, Task<Session1ScriptResult>> runner = (verb, _) =>
        {
            fake.ExportContent = NoPendingExport;
            return Task.FromResult(new Session1ScriptResult(true, "gesture_sent", Array.Empty<string>()));
        };

        // A cancel that fails (zone unchanged) must not clear the mismatch.
        var failed = await tools.GetCancelPendingEditsResponseAsync(
            "MainProgram", "MainRoutine", true, session,
            (verb, _) => Task.FromResult(new Session1ScriptResult(false, "command_not_applicable", Array.Empty<string>())),
            () => true);
        Assert.Contains("error", failed);
        Assert.Single(session.UnresolvedPublishMismatches);

        var ok = await tools.GetCancelPendingEditsResponseAsync(
            "MainProgram", "MainRoutine", true, session, runner, () => true);
        Assert.Contains("\"status\"", ok);
        Assert.Empty(session.UnresolvedPublishMismatches);
    }

    // ── logix-mcp-64f5.1: canonical routine names and typed XPath errors ───

    private const string LowercaseXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='mainroutine']";

    /// <summary>A double-quoted XPath (<c>[@Name="MainRoutine"]</c>) is accepted, not rejected.</summary>
    [Fact]
    public async Task DoubleQuotedXPath_IsAccepted()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        const string doubleQuoted =
            "Controller/Programs/Program[@Name=\"MainProgram\"]/Routines/Routine[@Name=\"MainRoutine\"]";

        var result = await ProposeRungEditHandler.ExecuteAsync(
            doubleQuoted, 0, 0, StagedRungFragment, session, target: "workstation");

        Assert.IsType<ProposeRungEditResult>(result);
        // The SDK only ever sees the canonical single-quoted XPath.
        Assert.Equal(ValidXPath, Assert.Single(fake.CapturedXPaths));
    }

    /// <summary>An <c>@Nme</c> typo is a typed InvalidRoutineXPath naming the expected shape; no SDK import.</summary>
    [Fact]
    public async Task NmeTypoXPath_ReturnsInvalidRoutineXPath_WithExpectedShape()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        const string typo =
            "Controller/Programs/Program[@Nme='MainProgram']/Routines/Routine[@Name='MainRoutine']";

        var result = await ProposeRungEditHandler.ExecuteAsync(
            typo, 0, 0, StagedRungFragment, session, target: "workstation");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("InvalidRoutineXPath", error.Kind);
        Assert.Contains(ProposeRungEditHandler.ExpectedRoutineXPathShape, error.Error);
        Assert.Contains("@Nme", error.Error);
        Assert.Empty(fake.CapturedXPaths);
        Assert.Empty(session.StagedRungBuffer);
    }

    /// <summary>A wrong program name is a typed ProgramNotFound listing the project's programs.</summary>
    [Fact]
    public async Task WrongProgramName_ReturnsProgramNotFound_ListingPrograms()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        const string wrong =
            "Controller/Programs/Program[@Name='NoSuchProgram']/Routines/Routine[@Name='MainRoutine']";

        var result = await ProposeRungEditHandler.ExecuteAsync(
            wrong, 0, 0, StagedRungFragment, session, target: "workstation");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("ProgramNotFound", error.Kind);
        Assert.Contains("NoSuchProgram", error.Error);
        Assert.Contains("MainProgram", error.Error);
        Assert.Empty(fake.CapturedXPaths);
        Assert.Empty(session.StagedRungBuffer);
    }

    /// <summary>
    /// <c>mainroutine</c> is canonicalised to <c>MainRoutine</c> in the staged-buffer key, in the
    /// pending_edits grouping (one staged rung, state none) and in the audit line.
    /// </summary>
    [Fact]
    public async Task MainroutineLowercase_IsCanonicalisedInBufferKey_PendingEditsAndAudit()
    {
        var auditDir = Path.Combine(Path.GetTempPath(), $"canon-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(auditDir);
        AuditLogger.OverrideLogDir(auditDir);
        try
        {
            var (session, fake) = await BuildOpenSessionAsync();

            var staged = await ProposeRungEditHandler.ExecuteAsync(
                LowercaseXPath, 0, 0, StagedRungFragment, session, target: "workstation");
            Assert.IsType<ProposeRungEditResult>(staged);

            // Buffer key uses the exported casing.
            var key = Assert.Single(session.StagedRungBuffer.Keys);
            Assert.Equal(ValidXPath, key.RoutineXPath);
            Assert.Equal(ValidXPath, Assert.Single(fake.CapturedXPaths));

            // pending_edits groups the staged rung under MainRoutine: it is staged, not a controller edit.
            fake.ExportContent = ImageExport(("N", "XIC(A)OTE(B);"), ("I", "XIC(StagedTag)OTE(StagedCoil);"));
            var pending = Assert.IsType<PendingEditsResult>(
                await PendingEditsHandler.ExecuteAsync("MainProgram", "MainRoutine", session));
            var routine = Assert.Single(pending.Routines);
            Assert.Equal("MainRoutine", routine.Routine);
            Assert.Equal(1, routine.StagedRungs);
            Assert.Equal("none", routine.State);

            // The audit line records the canonical routine.
            var line = File.ReadAllLines(Path.Combine(auditDir, "audit.jsonl"))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .First(e => e.GetProperty("event").GetString() == "rung_edit");
            Assert.Equal(ValidXPath, line.GetProperty("routine").GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(auditDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>When the export fails, the routine-existence check returns that error; it never fails open.</summary>
    [Fact]
    public async Task RoutineExistenceCheck_WhenExportFails_ReturnsError_NotFailOpen()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = null; // every export throws

        var error = await ProposeRungEditHandler.CheckTargetRoutineExistsAsync(ValidXPath, session, default);

        Assert.NotNull(error);
        Assert.Equal("RoutineCheckFailed", error!.Kind);
        Assert.Contains("Controller/Programs", error.Error);

        // The same failure stops a propose before any import, and nothing is staged.
        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation");
        Assert.Equal("RoutineCheckFailed", Assert.IsType<ProposeRungEditError>(result).Kind);
        Assert.Empty(fake.CapturedXPaths);
        Assert.Empty(session.StagedRungBuffer);
    }

    // ── logix-mcp-am0x.4: same-text replacements are N, not a PublishMismatch ──

    private static string RoutineExport(params (string Type, string Text)[] rungs) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
        "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\">" +
        "<Controller Name=\"DemoController\"><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Use=\"Target\" Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" +
        string.Concat(rungs.Select((r, i) =>
            $"<Rung Number=\"{i}\" Type=\"{r.Type}\"><Text><![CDATA[{r.Text}]]></Text></Rung>")) +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    /// <summary>A replacement whose text equals the original stays N: no PublishMismatch, index reported.</summary>
    [Fact]
    public async Task Publish_SameTextReplacement_AcceptedAsN_NoPublishMismatch()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        // The original rung 0 already carries the staged text, and stays N after the import.
        fake.ExportContent = RoutineExport(("N", "XIC(StagedTag)OTE(StagedCoil);"));

        var stage = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 1, StagedRungFragment, session, target: "workstation");
        Assert.IsType<ProposeRungEditResult>(stage);

        var publish = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(publish);
        Assert.Equal("proposed", success.Status);
        Assert.Equal(new[] { 0 }, success.UnchangedReplacements);
    }

    /// <summary>A replace whose only change is identical text creates no zone, so it is not "sent to the controller" (G35).</summary>
    [Fact]
    public async Task Publish_IdenticalTextReplace_DoesNotSayEditSentToController()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = RoutineExport(("N", "XIC(StagedTag)OTE(StagedCoil);"));

        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 1, StagedRungFragment, session, target: "workstation"));
        var publish = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(publish);
        Assert.Equal(new[] { 0 }, success.UnchangedReplacements);
        Assert.DoesNotContain("EDIT SENT TO CONTROLLER", success.Message, StringComparison.Ordinal);
        Assert.Contains("NO CHANGE", success.Message, StringComparison.Ordinal);
        Assert.Null(success.ZoneNote);
    }

    /// <summary>A genuinely missing pending rung is still a PublishMismatch next to a same-text replacement.</summary>
    [Fact]
    public async Task Publish_SameTextReplacementPlusGenuineMissingRung_StillReportsMismatch()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = RoutineExport(("N", "XIC(StagedTag)OTE(StagedCoil);"));

        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 1, StagedRungFragment, session, target: "workstation"));
        const string other =
            "<RSLogix5000Content><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\" Type=\"N\">" +
            "<Text><![CDATA[XIC(OtherTag)OTE(OtherCoil);]]></Text></Rung></RLLContent></Routine>" +
            "</Routines></Program></Programs></Controller></RSLogix5000Content>";
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 5, 0, other, session, target: "workstation"));

        // The controller never shows the second rung as pending.
        var publish = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        var mismatch = Assert.IsType<ProposeRungEditMismatch>(publish);
        Assert.Equal("PublishMismatch", mismatch.Kind);
    }

    /// <summary>Pure check: an unchanged replacement is accepted once; anything else stays missing.</summary>
    [Fact]
    public void ApplyUnchangedReplacements_AcceptsEachUnchangedOnce()
    {
        var (missing, accepted) = ProposeRungEditHandler.ApplyUnchangedReplacements(
            ["XIC(A)OTE(B);", "XIC(A)OTE(B);", "XIC(C)OTE(D);"],
            [],
            [(3, "XIC(A)OTE(B);")]);

        Assert.Equal(new[] { 3 }, accepted);
        Assert.Equal(new[] { "XIC(A)OTE(B);", "XIC(C)OTE(D);" }, missing);
    }

    // ── logix-mcp-5642.1: replace of a routine's LAST rung publishes the new rung (G32) ──
    // These tests use the real session-image state codes captured on the bench: er (staged replacement),
    // e (staged insert), rN (replaced original), N (untouched original). Never I/R in a session image.

    private static string OneRungFragment(string text) =>
        "<RSLogix5000Content><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\" Type=\"N\">" +
        $"<Text><![CDATA[{text}]]></Text></Rung></RLLContent></Routine>" +
        "</Routines></Program></Programs></Controller></RSLogix5000Content>";

    /// <summary>
    /// Models the controller after an AcceptEdits import: the imported rungs replace <c>replaceCount</c> image rungs at
    /// the insert position and show as pending <c>IR</c>. Records the last import for the assertions.
    /// </summary>
    private sealed class TailPublishModel
    {
        public uint Position; public uint ReplaceCount; public List<string> ImportedTexts = [];
        public int AcceptImports;
    }

    private static TailPublishModel ModelAcceptedImport(ProposeRungEditRecordingFake fake)
    {
        var model = new TailPublishModel();
        fake.ReflectAcceptedImportsInExport = false;
        fake.OnImportRungs = (option, pos, count, body) =>
        {
            if (option != PartialImportOption.AcceptEdits) return;
            model.AcceptImports++;
            model.Position = pos; model.ReplaceCount = count;
            var imported = XDocument.Parse(body).Descendants("Rung").ToList();
            model.ImportedTexts = imported.Select(r => r.Element("Text")!.Value.Trim()).ToList();

            var export  = XDocument.Parse(fake.ExportContent!);
            var content = export.Descendants("RLLContent").First();
            var rungs   = content.Elements("Rung").ToList();
            rungs.RemoveRange((int)pos, (int)Math.Min(count, (uint)(rungs.Count - (int)pos)));
            rungs.InsertRange((int)pos, imported.Select(r => new XElement("Rung",
                new XAttribute("Type", "IR"), new XElement("Text", new XCData(r.Element("Text")!.Value.Trim())))));
            content.RemoveNodes();
            for (int i = 0; i < rungs.Count; i++) { rungs[i].SetAttributeValue("Number", i); content.Add(rungs[i]); }
            fake.ExportContent = export.ToString(SaveOptions.DisableFormatting);
        };
        return model;
    }

    private static List<(string Type, string Text)> PostPublishImage(ProposeRungEditRecordingFake fake) =>
        XDocument.Parse(fake.ExportContent!).Descendants("Rung")
            .Select(r => ((string)r.Attribute("Type")!, r.Element("Text")!.Value.Trim())).ToList();

    /// <summary>2-rung routine, replace rung 1: image `N, er(new), rN(orig)`; the import set carries the new rung at index 1.</summary>
    [Fact]
    public async Task TwoRungRoutine_ReplaceLastRung_PublishesNewRungAtIndexOne()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = RoutineExport(("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 1, OneRungFragment("XIC(NewB)OTE(OutNewB);"), session, target: "workstation"));
        fake.ExportContent = RoutineExport(("N", "XIC(A)OTE(OutA);"), ("er", "XIC(NewB)OTE(OutNewB);"), ("rN", "XIC(B)OTE(OutB);"));
        var model = ModelAcceptedImport(fake);

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, null, session, target: "controller");

        var ok = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("proposed", ok.Status);
        Assert.Equal(1, model.AcceptImports);
        Assert.Equal(1u, model.Position);
        Assert.Equal(2u, model.ReplaceCount);                       // the new rung's slot and the hidden original
        Assert.Equal(new[] { "XIC(NewB)OTE(OutNewB);" }, model.ImportedTexts);
        var image = PostPublishImage(fake);
        Assert.Equal(2, image.Count);
        Assert.Equal(("N", "XIC(A)OTE(OutA);"), image[0]);
        Assert.Equal(("IR", "XIC(NewB)OTE(OutNewB);"), image[1]);
    }

    /// <summary>Replace rung 1 plus an insert after it: both new rungs are sent in order, no PublishMismatch.</summary>
    [Fact]
    public async Task TwoRungRoutine_ReplaceLastRungPlusInsertAfter_PublishesBothNewRungs()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = RoutineExport(("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 1, OneRungFragment("XIC(NewB)OTE(OutNewB);"), session, target: "workstation"));
        fake.ExportContent = RoutineExport(("N", "XIC(A)OTE(OutA);"), ("er", "XIC(NewB)OTE(OutNewB);"), ("rN", "XIC(B)OTE(OutB);"));
        // The user sees 2 rungs (A, NewB); the insert after them lands past the hidden original.
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 2, 0, OneRungFragment("XIC(Ins)OTE(OutIns);"), session, target: "workstation"));
        fake.ExportContent = RoutineExport(
            ("N", "XIC(A)OTE(OutA);"), ("er", "XIC(NewB)OTE(OutNewB);"), ("rN", "XIC(B)OTE(OutB);"), ("e", "XIC(Ins)OTE(OutIns);"));
        var model = ModelAcceptedImport(fake);

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, null, session, target: "controller");

        Assert.IsType<ProposeRungEditResult>(result);               // not a ProposeRungEditMismatch
        Assert.Equal(1u, model.Position);
        Assert.Equal(3u, model.ReplaceCount);
        Assert.Equal(new[] { "XIC(NewB)OTE(OutNewB);", "XIC(Ins)OTE(OutIns);" }, model.ImportedTexts);
        var image = PostPublishImage(fake);
        Assert.Equal(3, image.Count);
        Assert.Equal(("IR", "XIC(NewB)OTE(OutNewB);"), image[1]);
        Assert.Equal(("IR", "XIC(Ins)OTE(OutIns);"), image[2]);
    }

    /// <summary>1-rung routine, replace rung 0: image `er(new), rN(orig)`; the new rung is published at index 0.</summary>
    [Fact]
    public async Task OneRungRoutine_ReplaceRungZero_PublishesNewRung()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = RoutineExport(("N", "XIC(A)OTE(OutA);"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 1, OneRungFragment("XIC(NewA)OTE(OutNewA);"), session, target: "workstation"));
        fake.ExportContent = RoutineExport(("er", "XIC(NewA)OTE(OutNewA);"), ("rN", "XIC(A)OTE(OutA);"));
        var model = ModelAcceptedImport(fake);

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, null, session, target: "controller");

        Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal(0u, model.Position);
        Assert.Equal(2u, model.ReplaceCount);
        Assert.Equal(new[] { "XIC(NewA)OTE(OutNewA);" }, model.ImportedTexts);
        Assert.Equal(new[] { ("IR", "XIC(NewA)OTE(OutNewA);") }, PostPublishImage(fake));
    }

    /// <summary>
    /// The staged image holds two new rungs but the import set can carry only the one this session staged:
    /// StagedImportIncomplete is returned and no controller import is sent.
    /// </summary>
    [Fact]
    public async Task StagedImageHasMoreNewRungsThanImportSet_ReturnsStagedImportIncomplete_AndSendsNothing()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = RoutineExport(("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 1, OneRungFragment("XIC(NewB)OTE(OutNewB);"), session, target: "workstation"));
        // The image carries a second new rung the session's staged set does not account for.
        fake.ExportContent = RoutineExport(
            ("N", "XIC(A)OTE(OutA);"), ("er", "XIC(NewB)OTE(OutNewB);"), ("rN", "XIC(B)OTE(OutB);"), ("e", "XIC(Ghost)OTE(OutGhost);"));
        var model = ModelAcceptedImport(fake);

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, null, session, target: "controller");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("StagedImportIncomplete", error.Kind);
        Assert.Equal(4, error.StagedImage!.Count);
        Assert.Equal(0, model.AcceptImports);
        Assert.Equal(new[] { PartialImportOption.LeaveEdits }, fake.CapturedImportOptions);   // only the stage, nothing published
        Assert.Single(session.StagedRungBuffer);                                               // staged state untouched
    }

    // ── logix-mcp-5642.4 (G23): landed indices of every staged rung, per routine ──

    /// <summary>A staged-only insert (no fragment on the publish) reports the staged rung's index from the post-publish image.</summary>
    [Fact]
    public async Task StagedOnlyInsert_ReportsLandedIndexFromPostPublishImage()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ReflectAcceptedImportsInExport = false;
        fake.ExportContent = ImageExport(("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 2, 0, StagedRungFragment, session, target: "workstation"));

        fake.ExportContent = ImageExport(
            ("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"), ("I", "XIC(StagedTag)OTE(StagedCoil);"));
        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("verified", success.Verification);
        var routine = Assert.Single(success.PublishedRoutines!);
        Assert.Equal(ValidXPath, routine.Routine);
        Assert.Equal(new[] { 2 }, routine.LandedIndices);
        Assert.Contains("rung landed at index 2", success.Message);   // census agrees with the position
        Assert.Equal(2u, success.InsertPosition);
    }

    /// <summary>A staged-only replace whose new rung sits at index 0 reports landed index 0.</summary>
    [Fact]
    public async Task StagedOnlyReplaceAtZero_ReportsLandedIndexZero()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 1, StagedRungFragment, session, target: "workstation"));

        fake.ExportContent = ImageExport(
            ("I", "XIC(StagedTag)OTE(StagedCoil);"),
            ("R", "XIC(OldTag)OTE(OldCoil);"),
            ("N", "XIC(OtherTag)OTE(OtherCoil);"));
        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        var routine = Assert.Single(success.PublishedRoutines!);
        Assert.Equal(new[] { 0 }, routine.LandedIndices);
    }

    /// <summary>A publish with a staged rung and its own fragment rung reports both landed indices for the routine.</summary>
    [Fact]
    public async Task MixedOwnPlusStagedPublish_ReportsLandedIndicesForBoth()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ReflectAcceptedImportsInExport = false;
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation"));

        fake.ExportContent = ImageExport(
            ("I", "XIC(StagedTag)OTE(StagedCoil);"), ("I", "XIC(NewTag)OTE(NewCoil);"));
        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 0, OneRungFragment("XIC(NewTag)OTE(NewCoil);"), session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        var routine = Assert.Single(success.PublishedRoutines!);
        Assert.Equal(new[] { 0, 1 }, routine.LandedIndices);
    }

    /// <summary>Two routines each holding a staged rung report their own landed indices, not a shared one.</summary>
    [Fact]
    public async Task StagedOnlyPublish_LandedIndicesAreReportedPerRoutine()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ReflectAcceptedImportsInExport = false;
        fake.ExportContentByXPath[ProposeRungEditRecordingFake.ProgramsXPath] =
            ProposeRungEditRecordingFake.ProgramsExport("MainRoutine", "SecondRoutine");
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, RoutineFragment("MainRoutine", "XIC(A)OTE(B);"), session, target: "workstation"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            SecondXPath, 0, 0, RoutineFragment("SecondRoutine", "XIC(C)OTE(D);"), session, target: "workstation"));

        fake.ExportContentByXPath[ValidXPath]  = RoutineExport("MainRoutine", "I", "XIC(A)OTE(B);");
        fake.ExportContentByXPath[SecondXPath] =
            RoutineExport(("N", "XIC(X)OTE(Y);"), ("I", "XIC(C)OTE(D);")).Replace("MainRoutine", "SecondRoutine");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal(new[] { 0 }, success.PublishedRoutines!.Single(r => r.Routine == ValidXPath).LandedIndices);
        Assert.Equal(new[] { 1 }, success.PublishedRoutines!.Single(r => r.Routine == SecondXPath).LandedIndices);
    }

    // ── logix-mcp-wu06.4 (G38): publish prose and insertPosition derive from landedIndices ──

    /// <summary>Staged-only insert at 1: the note, the message, insertPosition and landedIndices all say index 1.</summary>
    [Fact]
    public async Task StagedOnlyInsertAtOne_NoteMessageInsertPositionAndLandedIndicesAgree()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ReflectAcceptedImportsInExport = false;
        fake.ExportContent = ImageExport(("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 0, StagedRungFragment, session, target: "workstation"));

        fake.ExportContent = ImageExport(
            ("N", "XIC(A)OTE(OutA);"), ("I", "XIC(StagedTag)OTE(StagedCoil);"), ("N", "XIC(B)OTE(OutB);"));
        var success = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller"));

        var routine = Assert.Single(success.PublishedRoutines!);
        Assert.Equal(new[] { 1 }, routine.LandedIndices);
        Assert.Equal(1u, success.InsertPosition);
        Assert.Contains("rung landed at index 1", success.Message);
        Assert.NotNull(success.Note);
        Assert.Contains("rung landed at index 1", success.Note);
        Assert.DoesNotContain("end of the routine", success.Note);
        Assert.DoesNotContain("position 2", success.Note);
        Assert.DoesNotContain("insert position", success.Message);
    }

    /// <summary>Replace at 0 plus insert at 2: both landed indices are named, in plural wording, and insertPosition is the first.</summary>
    [Fact]
    public async Task ReplaceAtZeroPlusInsertAtTwo_NoteMessageInsertPositionAndLandedIndicesAgree()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = RoutineExport(("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 1, OneRungFragment("XIC(NewA)OTE(OutNewA);"), session, target: "workstation"));
        fake.ExportContent = RoutineExport(
            ("er", "XIC(NewA)OTE(OutNewA);"), ("rN", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 2, 0, OneRungFragment("XIC(Ins)OTE(OutIns);"), session, target: "workstation"));
        // Post-publish controller image: the replaced original is gone, the new rungs are pending at 0 and 2.
        fake.ReflectAcceptedImportsInExport = false;
        fake.ExportContent = RoutineExport(
            ("IR", "XIC(NewA)OTE(OutNewA);"), ("N", "XIC(B)OTE(OutB);"), ("I", "XIC(Ins)OTE(OutIns);"));

        var published = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, null, session, target: "controller");
        if (published is ProposeRungEditMismatch mm) Assert.Fail(System.Text.Json.JsonSerializer.Serialize(mm));
        var success = Assert.IsType<ProposeRungEditResult>(published);

        var routine = Assert.Single(success.PublishedRoutines!);
        Assert.Equal(new[] { 0, 2 }, routine.LandedIndices);
        Assert.Equal(0u, success.InsertPosition);
        Assert.Contains("rungs landed at indices 0 and 2", success.Message);
        Assert.DoesNotContain("rung landed at index", success.Message.Replace("rungs landed", ""));
        Assert.True(success.Note is null || !success.Note.Contains("was ignored"));   // first landed index 0 == caller's 0
        Assert.Contains("2 staged rungs sent", success.Message);
        Assert.Contains("replacing 1 rung.", success.Message);   // the caller replaced one rung; not the image's 2 or 3
        Assert.DoesNotContain("replacing 3 rungs", success.Message);
    }

    /// <summary>Two routines in one set: each routine is described on its own, and insertPosition follows the call's routine.</summary>
    [Fact]
    public async Task TwoRoutines_EachDescribedSeparately_NoteMessageInsertPositionAndLandedIndicesAgree()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ReflectAcceptedImportsInExport = false;
        fake.ExportContentByXPath[ProposeRungEditRecordingFake.ProgramsXPath] =
            ProposeRungEditRecordingFake.ProgramsExport("MainRoutine", "SecondRoutine");
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, RoutineFragment("MainRoutine", "XIC(A)OTE(B);"), session, target: "workstation"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            SecondXPath, 0, 0, RoutineFragment("SecondRoutine", "XIC(C)OTE(D);"), session, target: "workstation"));

        fake.ExportContentByXPath[ValidXPath]  = RoutineExport("MainRoutine", "I", "XIC(A)OTE(B);");
        fake.ExportContentByXPath[SecondXPath] =
            RoutineExport(("N", "XIC(X)OTE(Y);"), ("I", "XIC(C)OTE(D);")).Replace("MainRoutine", "SecondRoutine");

        var success = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller"));

        Assert.Equal(new[] { 0 }, success.PublishedRoutines!.Single(r => r.Routine == ValidXPath).LandedIndices);
        Assert.Equal(new[] { 1 }, success.PublishedRoutines!.Single(r => r.Routine == SecondXPath).LandedIndices);
        Assert.Null(success.InsertPosition);   // G43: several routines published, positions are per routine
        Assert.Equal(0u, success.PublishedRoutines!.Single(r => r.Routine == ValidXPath).InsertPosition);
        Assert.Equal(1u, success.PublishedRoutines!.Single(r => r.Routine == SecondXPath).InsertPosition);
        Assert.Contains($"Routine '{ValidXPath}', rung landed at index 0", success.Message);
        Assert.Contains($"routine '{SecondXPath}', rung landed at index 1", success.Message);
        // G52: the caller's 0 equals the call's routine's first landed index (0), so neither the field nor the note appears.
        Assert.Null(success.IgnoredInsertPosition);
        Assert.True(success.Note is null || !success.Note.Contains("was ignored"));
    }

    /// <summary>G52: an honoured insert position produces no ignoredInsertPosition field and no note.</summary>
    [Fact]
    public async Task HonouredInsertPosition_NoIgnoredFieldAndNoNote()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ReflectAcceptedImportsInExport = false;
        fake.ExportContent = ImageExport(("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 2, 0, StagedRungFragment, session, target: "workstation"));
        fake.ExportContent = ImageExport(
            ("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"), ("I", "XIC(StagedTag)OTE(StagedCoil);"));

        var success = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 2, 0, "", session, target: "controller"));

        Assert.Equal(new[] { 2 }, success.PublishedRoutines!.Single().LandedIndices);
        Assert.Null(success.IgnoredInsertPosition);
        Assert.True(success.Note is null || !success.Note.Contains("was ignored"));
        Assert.DoesNotContain("ignoredInsertPosition", System.Text.Json.JsonSerializer.Serialize(success));
    }

    /// <summary>G52: an ignored insert position produces both the field and the note.</summary>
    [Fact]
    public async Task IgnoredInsertPosition_ProducesFieldAndNote()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ReflectAcceptedImportsInExport = false;
        fake.ExportContent = ImageExport(("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 2, 0, StagedRungFragment, session, target: "workstation"));
        fake.ExportContent = ImageExport(
            ("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"), ("I", "XIC(StagedTag)OTE(StagedCoil);"));

        var success = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller"));

        Assert.Equal(0u, success.IgnoredInsertPosition);
        Assert.NotNull(success.Note);
        Assert.Contains("Caller's insertPosition (0) was ignored", success.Note);
    }

    /// <summary>
    /// G52: in a multi-routine publish the rule is applied to the call's own routine. The same staged set published
    /// from MainRoutine (caller 0, landed 0: honoured) and from SecondRoutine (caller 0, landed 1: ignored).
    /// </summary>
    [Fact]
    public async Task MultiRoutinePublish_MixedHonouredAndIgnored_RuleAppliedPerRoutine()
    {
        foreach (var (callXPath, expectIgnored) in new[] { (ValidXPath, false), (SecondXPath, true) })
        {
            var (session, fake) = await BuildOpenSessionAsync();
            fake.ReflectAcceptedImportsInExport = false;
            fake.ExportContentByXPath[ProposeRungEditRecordingFake.ProgramsXPath] =
                ProposeRungEditRecordingFake.ProgramsExport("MainRoutine", "SecondRoutine");
            Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, RoutineFragment("MainRoutine", "XIC(A)OTE(B);"), session, target: "workstation"));
            Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
                SecondXPath, 0, 0, RoutineFragment("SecondRoutine", "XIC(C)OTE(D);"), session, target: "workstation"));
            fake.ExportContentByXPath[ValidXPath]  = RoutineExport("MainRoutine", "I", "XIC(A)OTE(B);");
            fake.ExportContentByXPath[SecondXPath] =
                RoutineExport(("N", "XIC(X)OTE(Y);"), ("I", "XIC(C)OTE(D);")).Replace("MainRoutine", "SecondRoutine");

            var success = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
                callXPath, 0, 0, "", session, target: "controller"));

            Assert.Equal(2, success.PublishedRoutines!.Count);
            if (expectIgnored)
            {
                Assert.Equal(0u, success.IgnoredInsertPosition);
                Assert.NotNull(success.Note);
                Assert.Contains("Caller's insertPosition (0) was ignored", success.Note);
                Assert.Contains($"In '{ValidXPath}' rung landed at index 0", success.Note);
                Assert.Contains($"In '{SecondXPath}' rung landed at index 1", success.Note);
            }
            else
            {
                Assert.Null(success.IgnoredInsertPosition);
                Assert.True(success.Note is null || !success.Note.Contains("was ignored"));
            }
        }
    }

    /// <summary>G43: a two-routine publish reports a null top-level insertPosition and correct per-routine values.</summary>
    [Fact]
    public async Task MultiRoutinePublish_TopLevelInsertPositionIsNull_PerRoutinePositionsCorrect()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ReflectAcceptedImportsInExport = false;
        fake.ExportContentByXPath[ProposeRungEditRecordingFake.ProgramsXPath] =
            ProposeRungEditRecordingFake.ProgramsExport("MainRoutine", "SecondRoutine");
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, RoutineFragment("MainRoutine", "XIC(A)OTE(B);"), session, target: "workstation"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            SecondXPath, 0, 0, RoutineFragment("SecondRoutine", "XIC(C)OTE(D);"), session, target: "workstation"));
        fake.ExportContentByXPath[ValidXPath]  = RoutineExport("MainRoutine", "I", "XIC(A)OTE(B);");
        fake.ExportContentByXPath[SecondXPath] =
            RoutineExport(("N", "XIC(X)OTE(Y);"), ("I", "XIC(C)OTE(D);")).Replace("MainRoutine", "SecondRoutine");

        var success = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            SecondXPath, 0, 0, "", session, target: "controller"));

        Assert.Null(success.InsertPosition);
        Assert.Contains("\"insertPosition\":null", System.Text.Json.JsonSerializer.Serialize(success));
        var main = success.PublishedRoutines!.Single(r => r.Routine == ValidXPath);
        var second = success.PublishedRoutines!.Single(r => r.Routine == SecondXPath);
        Assert.Equal(new[] { 0 }, main.LandedIndices);
        Assert.Equal(0u, main.InsertPosition);
        Assert.Equal(new[] { 1 }, second.LandedIndices);
        Assert.Equal(1u, second.InsertPosition);
    }

    /// <summary>G43: a single-routine publish keeps its top-level insertPosition.</summary>
    [Fact]
    public async Task SingleRoutinePublish_TopLevelInsertPositionUnchanged()
    {
        Assert.Equal(3u, ProposeRungEditHandler.ReportedInsertPosition(
            ValidXPath, [new PublishedRoutine(ValidXPath, 2, [3, 4], 3u)], 0u));
        Assert.Equal(7u, ProposeRungEditHandler.ReportedInsertPosition(ValidXPath, null, 7u));
        Assert.Null(ProposeRungEditHandler.ReportedInsertPosition(
            ValidXPath, [new PublishedRoutine(ValidXPath, 1, [0], 0u), new PublishedRoutine(SecondXPath, 1, [1], 1u)], 0u));
    }

    /// <summary>G44: a caller position equal to the first landed index gives no "ignored" sentence.</summary>
    [Fact]
    public async Task CallerInsertPositionEqualToFirstLandedIndex_NoIgnoredSentence()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ReflectAcceptedImportsInExport = false;
        fake.ExportContent = ImageExport(("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 2, 0, StagedRungFragment, session, target: "workstation"));
        fake.ExportContent = ImageExport(
            ("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"), ("I", "XIC(StagedTag)OTE(StagedCoil);"));

        var success = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 2, 0, "", session, target: "controller"));

        Assert.Equal(new[] { 2 }, success.PublishedRoutines!.Single().LandedIndices);
        Assert.True(success.Note is null || !success.Note.Contains("was ignored"));
    }

    /// <summary>G44: a caller position different from the first landed index keeps the "ignored" sentence.</summary>
    [Fact]
    public async Task CallerInsertPositionDifferentFromFirstLandedIndex_KeepsIgnoredSentence()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ReflectAcceptedImportsInExport = false;
        fake.ExportContent = ImageExport(("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 2, 0, StagedRungFragment, session, target: "workstation"));
        fake.ExportContent = ImageExport(
            ("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"), ("I", "XIC(StagedTag)OTE(StagedCoil);"));

        var success = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller"));

        Assert.NotNull(success.Note);
        Assert.Contains("Caller's insertPosition (0) was ignored", success.Note);
        Assert.Contains("rung landed at index 2", success.Note);
    }

    /// <summary>A 1-rung routine replace: singular wording throughout, landed index 0, and no end-of-routine sentence.</summary>
    [Fact]
    public async Task OneRungRoutineReplace_UsesSingularWording_AndNoEndOfRoutineSentence()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = RoutineExport(("N", "XIC(A)OTE(OutA);"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 1, OneRungFragment("XIC(NewA)OTE(OutNewA);"), session, target: "workstation"));
        fake.ExportContent = RoutineExport(("er", "XIC(NewA)OTE(OutNewA);"), ("rN", "XIC(A)OTE(OutA);"));
        ModelAcceptedImport(fake);

        var success = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, null, session, target: "controller"));

        var routine = Assert.Single(success.PublishedRoutines!);
        Assert.Equal(new[] { 0 }, routine.LandedIndices);
        Assert.Equal(0u, success.InsertPosition);
        Assert.Contains("rung landed at index 0", success.Message);
        Assert.DoesNotContain("rungs landed", success.Message);
        Assert.Contains("1 staged rung sent", success.Message);
        Assert.Contains("replacing 1 rung", success.Message);
        Assert.DoesNotContain("replacing 1 rungs", success.Message);
        Assert.DoesNotContain("replacing 3 rungs", success.Message);
        if (success.Note is not null)
        {
            Assert.DoesNotContain("end of the routine", success.Note);
            Assert.DoesNotContain("rungs landed", success.Note);
            Assert.DoesNotContain("(s)", success.Note);
        }
    }

    /// <summary>The tail rule: only a run that holds both a hidden original and a new rung needs the explicit commit.</summary>
    [Theory]
    [InlineData(new[] { "N", "er", "rN" }, true)]
    [InlineData(new[] { "er", "rN" }, true)]
    [InlineData(new[] { "N", "er", "rN", "e" }, true)]
    [InlineData(new[] { "er", "rN", "N" }, false)]      // a middle replace: the controller-end position is already safe
    [InlineData(new[] { "N", "N", "e" }, false)]        // inserts only
    [InlineData(new[] { "N", "dN" }, false)]            // delete only: no new rung to lose
    public void StagedTailNeedsExplicitCommit_OnlyWhenTailHoldsHiddenOriginalAndNewRung(string[] states, bool expected)
    {
        var image = states.Select((st, i) => new StagedImageRung(i, "t" + i, st)).ToList();
        Assert.Equal(expected, ProposeRungEditHandler.StagedTailNeedsExplicitCommit(image));
    }
}
#endif
