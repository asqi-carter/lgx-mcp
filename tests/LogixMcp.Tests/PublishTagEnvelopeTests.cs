#if LOGIX_SDK
using System.Xml.Linq;
using RockwellAutomation.LogixDesigner;

using SdkTask = System.Threading.Tasks.Task;
using Task    = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

// ── PublishTagEnvelopeTests ────────────────────────────────────────────────────

/// <summary>
/// Regression tests for an earlier change:
/// the controller (publish) path of <see cref="ProposeRungEditHandler.ExecuteAsync"/>
/// must never rewrite the caller's own fragment tag envelope (requirement 1), and
/// must merge any buffered tag declarations into that envelope rather than
/// silently dropping them (requirement 2).
///
/// <para>
/// Before this fix, every controller publish called
/// <see cref="ProposeRungEditHandler.StripTagsFromFragment"/> and then
/// re-injected a bare <c>&lt;Tags&gt;</c> container via
/// <c>controllerElem.AddFirst</c> whenever the merged tag map (staging buffer
/// plus the fragment's own declarations) was non-empty — which is true for any
/// export-shaped fragment, since a real export always carries its own
/// <c>&lt;Tags Use="Context"&gt;</c> container. That destroyed the export's own
/// envelope (container removed, replaced with one lacking <c>Use="Context"</c>,
/// moved to the front of <c>&lt;Controller&gt;</c>) on every single publish, and
/// the SDK importer then rejected the request.
/// </para>
///
/// <para>
/// These tests use a fake <see cref="ILogixProject"/> (<see cref="ProposeRungEditRecordingFake"/>,
/// shared with <c>ProposeRungEditHandlerTests</c>) that captures the exact XML text
/// handed to <c>PartialImportRungsFromXmlFileAsync</c>, and compare it against the
/// caller's input fragment.
/// </para>
/// </summary>
public class PublishTagEnvelopeTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    /// <summary>
    /// A fragment shaped like a real Logix Designer export: a controller-scoped
    /// <c>&lt;Tags Use="Context"&gt;</c> container — carrying an existing tag
    /// declaration — positioned immediately before <c>&lt;Programs&gt;</c>, exactly
    /// as the exporter emits it. No XML declaration, so a correct fix must add one
    /// without touching anything else.
    /// </summary>
    private const string ExportShapedFragment =
        "<RSLogix5000Content SchemaRevision=\"1.0\">" +
          "<Controller>" +
            "<Tags Use=\"Context\">" +
              "<Tag Name=\"ExistingTag1\" DataType=\"BOOL\" />" +
            "</Tags>" +
            "<Programs>" +
              "<Program Name=\"MainProgram\">" +
                "<Routines>" +
                  "<Routine Name=\"MainRoutine\">" +
                    "<RLLContent>" +
                      "<Rung Number=\"0\" Type=\"N\">" +
                        "<Text><![CDATA[NOP();]]></Text>" +
                      "</Rung>" +
                    "</RLLContent>" +
                  "</Routine>" +
                "</Routines>" +
              "</Program>" +
            "</Programs>" +
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
    /// With an empty staging buffer, a controller-path publish hands the SDK
    /// exactly the caller's fragment, byte-for-byte — only an XML declaration is
    /// added, per the 0062 fix, because the caller's fragment omitted one.
    /// </summary>
    [Fact]
    public async Task EmptyBuffer_PublishFragmentIsImportedUnchanged()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        Assert.Empty(session.StagedTagBuffer);

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ExportShapedFragment, session, target: "controller",
            // ExistingTag1 is a pre-existing tag; a name outside the census would be forced Read Only.
            preExistingTagNames: new[] { "ExistingTag1" });

        Assert.NotNull(fake.LastCapturedXmlContent);
        var expected = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                       ExportShapedFragment;
        Assert.Equal(expected, fake.LastCapturedXmlContent);
    }

    /// <summary>
    /// The export's own &lt;Tags Use="Context"&gt; container — its position
    /// immediately before &lt;Programs&gt;, its <c>Use</c> attribute, and its
    /// existing tag declaration — survives a controller publish completely
    /// untouched. This is the exact corruption an earlier change found: the old code
    /// stripped this container and re-injected a bare &lt;Tags&gt; (no
    /// <c>Use</c> attribute) at the front of &lt;Controller&gt; via
    /// <c>AddFirst</c>.
    /// </summary>
    [Fact]
    public async Task PublishNeverStripsTheFragmentsOwnTagsContainer()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ExportShapedFragment, session, target: "controller");

        Assert.NotNull(fake.LastCapturedXmlContent);
        var sdkDoc = XDocument.Parse(fake.LastCapturedXmlContent!);

        var controllerElem = sdkDoc.Root!.Element("Controller");
        Assert.NotNull(controllerElem);

        // Exactly one <Tags> container — never stripped, never duplicated.
        var tagsContainers = controllerElem!.Elements("Tags").ToList();
        Assert.Single(tagsContainers);
        var tagsContainer = tagsContainers[0];

        // Attribute preserved.
        Assert.Equal("Context", (string?)tagsContainer.Attribute("Use"));

        // Position preserved: still the element immediately before <Programs>.
        var siblingAfter = tagsContainer.ElementsAfterSelf().FirstOrDefault();
        Assert.NotNull(siblingAfter);
        Assert.Equal("Programs", siblingAfter!.Name.LocalName);

        // Existing declaration preserved verbatim.
        var tag = tagsContainer.Element("Tag");
        Assert.NotNull(tag);
        Assert.Equal("ExistingTag1", (string?)tag!.Attribute("Name"));
        Assert.Equal("BOOL", (string?)tag.Attribute("DataType"));
    }

    // ── This change requirement 2: buffered-tag merging ────────────────
    //
    // When the session's staging buffer holds tag declarations accumulated from
    // prior workstation-target calls, a controller publish must merge each one
    // (not already declared in the fragment, by name, case-insensitive) into the
    // fragment's own <Tags Use="Context"> container — creating that container in
    // the exporter's own position when the fragment has none. This replaces the
    // StripTagsFromFragment + AddFirst approach that an earlier change removed.

    /// <summary>
    /// A fragment shaped like a real export but with no controller-scoped
    /// <c>&lt;Tags&gt;</c> container of any kind — only <c>&lt;DataTypes&gt;</c>
    /// and <c>&lt;Programs&gt;</c> — so a merge must create one from scratch.
    /// </summary>
    private const string FragmentWithNoTagsContainer =
        "<RSLogix5000Content SchemaRevision=\"1.0\">" +
          "<Controller>" +
            "<DataTypes />" +
            "<Programs>" +
              "<Program Name=\"MainProgram\">" +
                "<Routines>" +
                  "<Routine Name=\"MainRoutine\">" +
                    "<RLLContent>" +
                      "<Rung Number=\"0\" Type=\"N\">" +
                        "<Text><![CDATA[NOP();]]></Text>" +
                      "</Rung>" +
                    "</RLLContent>" +
                  "</Routine>" +
                "</Routines>" +
              "</Program>" +
            "</Programs>" +
          "</Controller>" +
        "</RSLogix5000Content>";

    /// <summary>
    /// A buffered <c>&lt;Tag&gt;</c> element shaped like the ones
    /// <see cref="ProposeRungEditHandler.StripTagsFromFragment"/> extracts and
    /// stores in <see cref="LogixProjectSession.StagedTagBuffer"/>.
    /// </summary>
    private static XElement MakeBufferedTag(string name, string dataType) =>
        new("Tag", new XAttribute("Name", name), new XAttribute("DataType", dataType));

    /// <summary>
    /// Requirement 2: a buffered tag not already declared in the fragment is
    /// appended inside the fragment's existing <c>&lt;Tags Use="Context"&gt;</c>
    /// element — that element's position, attribute and pre-existing declaration
    /// are all preserved untouched alongside the newly-appended tag.
    /// </summary>
    [Fact]
    public async Task BufferedTags_AreAppendedInsideTheExistingContextContainer()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        session.StagedTagBuffer["NewBufferedTag"] = MakeBufferedTag("NewBufferedTag", "DINT");

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ExportShapedFragment, session, target: "controller");

        Assert.NotNull(fake.LastCapturedXmlContent);
        var sdkDoc = XDocument.Parse(fake.LastCapturedXmlContent!);
        var controllerElem = sdkDoc.Root!.Element("Controller");
        Assert.NotNull(controllerElem);

        // Still exactly one <Tags> container — the buffered tag was merged into
        // it, not added as a second sibling container.
        var tagsContainers = controllerElem!.Elements("Tags").ToList();
        Assert.Single(tagsContainers);
        var tagsContainer = tagsContainers[0];

        Assert.Equal("Context", (string?)tagsContainer.Attribute("Use"));

        // Position preserved: still the element immediately before <Programs>.
        var siblingAfter = tagsContainer.ElementsAfterSelf().FirstOrDefault();
        Assert.NotNull(siblingAfter);
        Assert.Equal("Programs", siblingAfter!.Name.LocalName);

        // The fragment's own pre-existing declaration is untouched.
        var existingTag = tagsContainer.Elements("Tag")
            .FirstOrDefault(t => string.Equals((string?)t.Attribute("Name"), "ExistingTag1",
                StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(existingTag);
        Assert.Equal("BOOL", (string?)existingTag!.Attribute("DataType"));

        // The buffered tag was appended inside the same container.
        var bufferedTag = tagsContainer.Elements("Tag")
            .FirstOrDefault(t => string.Equals((string?)t.Attribute("Name"), "NewBufferedTag",
                StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(bufferedTag);
        Assert.Equal("DINT", (string?)bufferedTag!.Attribute("DataType"));
    }

    /// <summary>
    /// Requirement 2: when the fragment has no <c>&lt;Tags Use="Context"&gt;</c>
    /// element, one is created — with the <c>Use="Context"</c> attribute — and
    /// inserted after <c>&lt;DataTypes&gt;</c> and before <c>&lt;Programs&gt;</c>,
    /// matching the exporter's own position, to carry the buffered tag.
    /// </summary>
    [Fact]
    public async Task BufferedTags_CreateAContextContainerInExportPosition_WhenFragmentHasNone()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        session.StagedTagBuffer["NewBufferedTag"] = MakeBufferedTag("NewBufferedTag", "BOOL");

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithNoTagsContainer, session, target: "controller");

        Assert.NotNull(fake.LastCapturedXmlContent);
        var sdkDoc = XDocument.Parse(fake.LastCapturedXmlContent!);
        var controllerElem = sdkDoc.Root!.Element("Controller");
        Assert.NotNull(controllerElem);

        var tagsContainers = controllerElem!.Elements("Tags").ToList();
        Assert.Single(tagsContainers);
        var tagsContainer = tagsContainers[0];
        Assert.Equal("Context", (string?)tagsContainer.Attribute("Use"));

        // Positioned immediately after <DataTypes> ...
        var siblingBefore = tagsContainer.ElementsBeforeSelf().LastOrDefault();
        Assert.NotNull(siblingBefore);
        Assert.Equal("DataTypes", siblingBefore!.Name.LocalName);

        // ... and immediately before <Programs>.
        var siblingAfter = tagsContainer.ElementsAfterSelf().FirstOrDefault();
        Assert.NotNull(siblingAfter);
        Assert.Equal("Programs", siblingAfter!.Name.LocalName);

        var bufferedTag = tagsContainer.Element("Tag");
        Assert.NotNull(bufferedTag);
        Assert.Equal("NewBufferedTag", (string?)bufferedTag!.Attribute("Name"));
        Assert.Equal("BOOL", (string?)bufferedTag.Attribute("DataType"));
    }

    /// <summary>
    /// Requirement 2: a fragment-declared tag wins on a same-named collision with
    /// a buffered tag — the fragment's own declaration (and its attributes) are
    /// left completely untouched, the buffered copy is simply not appended, and
    /// nothing is ever removed from the fragment.
    /// </summary>
    [Fact]
    public async Task FragmentDeclaredTag_TakesPrecedenceOverSameNamedBufferedTag_NoRemovalOccurs()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        // Buffered copy of ExistingTag1 with a different DataType than the
        // fragment's own declaration (BOOL) — if the buffered copy won, or if
        // the fragment's declaration were removed, this would be detectable.
        session.StagedTagBuffer["ExistingTag1"] = MakeBufferedTag("ExistingTag1", "DINT");

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ExportShapedFragment, session, target: "controller");

        Assert.NotNull(fake.LastCapturedXmlContent);
        var sdkDoc = XDocument.Parse(fake.LastCapturedXmlContent!);
        var controllerElem = sdkDoc.Root!.Element("Controller");
        Assert.NotNull(controllerElem);

        // Exactly one <Tags> container, exactly one <Tag> inside it — the
        // buffered duplicate was not appended alongside the fragment's own.
        var tagsContainer = Assert.Single(controllerElem!.Elements("Tags"));
        var tags = tagsContainer.Elements("Tag").ToList();
        var existingTagMatches = tags.Where(t => string.Equals(
            (string?)t.Attribute("Name"), "ExistingTag1", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(existingTagMatches);

        // The fragment's own declaration (BOOL), not the buffered one (DINT), is
        // what reached the SDK — and it was not removed.
        Assert.Equal("BOOL", (string?)existingTagMatches[0].Attribute("DataType"));
    }

    // ── This change requirement 3: merged staged-tag names in ──────────
    // controller publish rejection error messages ────────────────────────────────
    //
    // When a controller-path publish is rejected by the SDK import call, the
    // returned error must name the buffered tags that were merged into the
    // fragment for that attempt (see ProposeRungEditHandler.MergeBufferedTagsIntoFragment),
    // and state that the session's staging buffer is retried on every controller
    // publish — the buffer is left untouched by a rejected publish (it is only
    // cleared after a confirmed successful publish), so the exact same buffered
    // tags will be re-merged and resent on the next controller publish attempt.

    /// <summary>
    /// A controller-path publish that the SDK rejects (via
    /// <see cref="ProposeRungEditRecordingFake.ThrowOnImport"/>) must return an
    /// error whose message lists the name of the buffered tag that was merged
    /// into the fragment for this attempt, and states that the session's staging
    /// buffer is retried on every controller publish. The buffer itself must be
    /// left untouched by the rejection.
    /// </summary>
    [Fact]
    public async Task RejectedPublish_NamesMergedStagedTags()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        session.StagedTagBuffer["NewBufferedTag"] = MakeBufferedTag("NewBufferedTag", "DINT");
        fake.ThrowOnImport = new InvalidOperationException("simulated SDK rejection");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ExportShapedFragment, session, target: "controller");

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Contains("NewBufferedTag", err.Error);
        Assert.Contains("retried on every controller publish", err.Error,
            StringComparison.OrdinalIgnoreCase);

        // The rejection must not clear the staging buffer — the same buffered
        // tag will be re-merged and resent on the caller's next publish attempt.
        Assert.True(session.StagedTagBuffer.ContainsKey("NewBufferedTag"));
    }

    /// <summary>
    /// When the staging buffer was empty at the time of a rejected controller
    /// publish, the error message still names the merged-tag list as empty
    /// rather than omitting the disclosure entirely.
    /// </summary>
    [Fact]
    public async Task RejectedPublish_EmptyBufferListsEmptyMergedTags()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        Assert.Empty(session.StagedTagBuffer);
        fake.ThrowOnImport = new InvalidOperationException("simulated SDK rejection");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ExportShapedFragment, session, target: "controller");

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Contains("Merged staged tag names for this publish attempt: []", err.Error);
        Assert.Contains("retried on every controller publish", err.Error,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A rejected <em>workstation</em>-path publish (target left at its default)
    /// must not carry the merged-staged-tags disclosure — that disclosure only
    /// applies to the controller (publish) path, since the workstation path never
    /// calls <see cref="ProposeRungEditHandler.MergeBufferedTagsIntoFragment"/>.
    /// </summary>
    [Fact]
    public async Task RejectedWorkstationStage_DoesNotNameMergedStagedTags()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        session.StagedTagBuffer["NewBufferedTag"] = MakeBufferedTag("NewBufferedTag", "DINT");
        fake.ThrowOnImport = new InvalidOperationException("simulated SDK rejection");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ExportShapedFragment, session);

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.DoesNotContain("Merged staged tag names", err.Error);
    }

    // ── Delta-based CreatedByThisSession ────────
    //
    // A controller-path publish must only mark tag names as "created" when they
    // are absent from the pre-publish controller tag census (preExistingTagNames).
    // A context declaration of a tag that already existed on the controller —
    // e.g. ExportShapedFragment's <Tags Use="Context"><Tag Name="ExistingTag1" />
    // — must never appear in TagsCreated and must never trigger the
    // CreatedByThisSession self-grant write lock.

    /// <summary>
    /// A fragment shaped like a real export whose &lt;Tags Use="Context"&gt;
    /// container declares two tags: <c>ExistingTag1</c> (already on the
    /// controller, present in <c>preExistingTagNames</c>) and
    /// <c>BrandNewTag</c> (genuinely new, absent from the census). Only the
    /// latter is a real creation.
    /// </summary>
    private const string ExportShapedFragmentWithMixedTags =
        "<RSLogix5000Content SchemaRevision=\"1.0\">" +
          "<Controller>" +
            "<Tags Use=\"Context\">" +
              "<Tag Name=\"ExistingTag1\" DataType=\"BOOL\" />" +
              "<Tag Name=\"BrandNewTag\" DataType=\"BOOL\" />" +
            "</Tags>" +
            "<Programs>" +
              "<Program Name=\"MainProgram\">" +
                "<Routines>" +
                  "<Routine Name=\"MainRoutine\">" +
                    "<RLLContent>" +
                      "<Rung Number=\"0\" Type=\"N\">" +
                        "<Text><![CDATA[NOP();]]></Text>" +
                      "</Rung>" +
                    "</RLLContent>" +
                  "</Routine>" +
                "</Routines>" +
              "</Program>" +
            "</Programs>" +
          "</Controller>" +
        "</RSLogix5000Content>";

    /// <summary>
    /// Requirement 1: a context declaration of a tag that already
    /// existed on the controller before this publish (per the
    /// <c>preExistingTagNames</c> census) is excluded from
    /// <see cref="ProposeRungEditResult.TagsCreated"/> — only names absent from
    /// the census (genuine creations) are reported/marked as created.
    /// </summary>
    [Fact]
    public async Task ExistingContextTags_AreNotMarkedCreatedByThisSession()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ExportShapedFragmentWithMixedTags, session,
            target: "controller",
            preExistingTagNames: new[] { "ExistingTag1" });

        var proposed = Assert.IsType<ProposeRungEditResult>(result);
        Assert.NotNull(proposed.TagsCreated);
        Assert.DoesNotContain(proposed.TagsCreated!, e => string.Equals(
            e.TagName, "ExistingTag1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(proposed.TagsCreated!, e => string.Equals(
            e.TagName, "BrandNewTag", StringComparison.OrdinalIgnoreCase));
    }

    // ── Self-grant guard still enforced for genuinely new tags ──
    //
    // The delta logic added by an earlier change must
    // only ever narrow TagsCreated to exclude pre-existing context tags — it must
    // never weaken the self-grant guard for tags that are genuinely
    // absent from the pre-publish census. This test locks in the other half of
    // the delta: a name not found in preExistingTagNames is unconditionally
    // reported as created, exactly as before the fix, regardless of
    // what other (pre-existing) tags the same fragment also declares.

    /// <summary>
    /// Requirement 3: in a controller publish whose fragment mixes a
    /// pre-existing context tag (<c>ExistingTag1</c>, present in
    /// <c>preExistingTagNames</c>) with a genuinely new one (<c>BrandNewTag</c>,
    /// absent from the census), the new tag is still reported in
    /// <see cref="ProposeRungEditResult.TagsCreated"/> — the delta computation
    /// keys only on census membership, not on the presence of other pre-existing
    /// tags in the same publish. This is the source of the
    /// <see cref="TagSessionService.RecordCreatedTags"/> registration that the
    /// This change self-grant guard (<c>WriteTagHandler</c>) checks against.
    /// </summary>
    [Fact]
    public async Task NewTagInPublish_IsMarkedCreatedByThisSession()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ExportShapedFragmentWithMixedTags, session,
            target: "controller",
            preExistingTagNames: new[] { "ExistingTag1" });

        var proposed = Assert.IsType<ProposeRungEditResult>(result);
        Assert.NotNull(proposed.TagsCreated);

        var createdEntry = proposed.TagsCreated!.SingleOrDefault(e => string.Equals(
            e.TagName, "BrandNewTag", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(createdEntry);
    }

    // ── writableViaLogixWrite disclosure for session-created tags ──
    //
    // Tags created by the current session's publish are immediately registered under
    // the CreatedByThisSession self-grant guard, which causes logix_write
    // to deny writes for the lifetime of the current session.  The writableViaLogixWrite
    // field must therefore always be false for session-created tags — even for those in
    // the Cmd_/Sim_ granted namespace — and the human-readable message must state that
    // these tags are writable only from a new session (logix_connect).

    /// <summary>
    /// A fragment containing a tag in the Cmd_ namespace, which would ordinarily
    /// be eligible for logix_write writes, but is blocked for the current session
    /// by the CreatedByThisSession guard.
    /// </summary>
    private const string FragmentWithCmdPrefixTag =
        "<RSLogix5000Content SchemaRevision=\"1.0\">" +
          "<Controller>" +
            "<Tags Use=\"Context\">" +
              "<Tag Name=\"Cmd_SpindleEnable\" DataType=\"BOOL\" />" +
            "</Tags>" +
            "<Programs>" +
              "<Program Name=\"MainProgram\">" +
                "<Routines>" +
                  "<Routine Name=\"MainRoutine\">" +
                    "<RLLContent>" +
                      "<Rung Number=\"0\" Type=\"N\">" +
                        "<Text><![CDATA[NOP();]]></Text>" +
                      "</Rung>" +
                    "</RLLContent>" +
                  "</Routine>" +
                "</Routines>" +
              "</Program>" +
            "</Programs>" +
          "</Controller>" +
        "</RSLogix5000Content>";

    /// <summary>
    /// For every tag in <c>tagsCreated[]</c>, <c>writableViaLogixWrite</c>
    /// is <c>false</c> — even when the tag name falls in a granted command namespace
    /// (<c>Cmd_</c>) — because the <c>CreatedByThisSession</c> guard blocks
    /// <c>logix_write</c> for the lifetime of the current session.
    ///
    /// <para>
    /// External Access Read Only until an engineer sets them to Read/Write in Logix
    /// Designer (it no longer says they become writable from a new session).
    /// </para>
    /// </summary>
    [Fact]
    public async Task CreatedTag_WritableViaLogixWrite_IsFalse_AndMessageSaysReadOnly()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithCmdPrefixTag, session, target: "controller");

        var proposed = Assert.IsType<ProposeRungEditResult>(result);
        Assert.NotNull(proposed.TagsCreated);
        Assert.NotEmpty(proposed.TagsCreated!);

        // All session-created tags must have writableViaLogixWrite=false — the
        // CreatedByThisSession gate blocks logix_write for the session lifetime.
        foreach (var entry in proposed.TagsCreated!)
            Assert.False(entry.WritableViaLogixWrite,
                $"Tag '{entry.TagName}' was created by this session and must have " +
                "writableViaLogixWrite=false (CreatedByThisSession blocks logix_write).");

        // an earlier change (replaces the an earlier change "writable from a new session" text): the
        // message says created tags are External Access Read Only until an engineer
        // sets them to Read/Write, and never promises a reconnect makes them writable.
        Assert.DoesNotContain("now writable", proposed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("new session", proposed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("External Access Read Only", proposed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Read/Write in Logix Designer", proposed.Message, StringComparison.OrdinalIgnoreCase);
    }
}
#endif
