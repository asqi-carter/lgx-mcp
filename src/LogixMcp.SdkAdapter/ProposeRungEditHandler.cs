#if LOGIX_SDK
// ── ProposeRungEditHandler — wraps PartialImportRungsFromXmlFileAsync ─────────
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;

using PartialImportOption   = RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption;
using ConnectedState        = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState;
using RequestedBuildTarget  = RockwellAutomation.LogixDesigner.LogixProject.RequestedBuildTarget;

// Alias System.Threading.Tasks.Task to avoid ambiguity with L5Sharp.Core.Task
// when both namespaces are in scope.
using Task = System.Threading.Tasks.Task;

// ── Result / error types ──────────────────────────────────────────────────────

/// <summary>
/// Prominent safety flags for a proposal response, surfacing three specific risk
/// categories without refusing the operation.
///
/// <para>
/// All three fields are lists of base tag names.  A field is <c>null</c> (omitted
/// from JSON) when no tags fall into that category for this proposal.
/// </para>
///
/// <list type="bullet">
///   <item><term><see cref="WritesToSafetyTags"/></term>
///     <description>
///     Tags classified as <see cref="DenyReason.SafetyTag"/> by the active policy.
///     These are safety-interlock signals that the gate is designed to protect; the
///     proposed rung writes to them.
///     </description></item>
///   <item><term><see cref="InvalidatesGrantBasis"/></term>
///     <description>
///     Tags that are currently <c>"granted"</c> in the active policy and that the
///     proposed rung would turn into OTE/write destinations — making them
///     program-written.  If the edit is accepted the gate will continue to grant
///     writes to these tags while they are actually driven by program logic, which
///     is exactly the condition the gate's <c>ProgramWritten</c> deny exists to
///     prevent.
///     </description></item>
///   <item><term><see cref="WritesUnknownTags"/></term>
///     <description>
///     Tags that are absent from the active policy entirely — they do not exist in
///     the project as currently loaded.  Accepting this edit would introduce a new
///     tag reference that the gate has never evaluated.
///     </description></item>
/// </list>
/// </summary>
/// <param name="WritesToSafetyTags">
/// Tags classified as <see cref="DenyReason.SafetyTag"/> in the active policy, or
/// <c>null</c> when no safety-tagged write destinations are present.
/// </param>
/// <param name="InvalidatesGrantBasis">
/// Tags currently granted by the policy that this fragment writes to (would become
/// program-written on accept), or <c>null</c> when no granted tags are write
/// destinations.
/// </param>
/// <param name="WritesUnknownTags">
/// Tags absent from the policy entirely (not in the project), or <c>null</c> when
/// every write destination is known to the policy.
/// </param>
public record ProposalSafetyFlags(
    [property: JsonPropertyName("writesToSafetyTags"),    JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? WritesToSafetyTags,
    [property: JsonPropertyName("invalidatesGrantBasis"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? InvalidatesGrantBasis,
    [property: JsonPropertyName("writesUnknownTags"),     JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? WritesUnknownTags);

/// <summary>
/// Gate analysis annotation for a single write destination found in a proposed rung fragment.
/// Included in the <see cref="ProposeRungEditResult"/> and
/// <see cref="ProposeRungEditOfflineResult"/> payloads so the reviewing engineer can
/// see each write destination, its gate classification, and its provenance at decision time.
/// </summary>
/// <param name="Tag">
/// Base tag name (stripped of member-access suffixes) found as a proven write operand
/// in the fragment.
/// </param>
/// <param name="Classification">
/// Gate classification derived from the write-gate policy:
/// <c>"granted"</c> when the policy grants this tag for writing;
/// a <see cref="DenyReason"/> name (e.g. <c>"ProgramWritten"</c>, <c>"SafetyTag"</c>,
/// <c>"NotDesignated"</c>, <c>"UnknownWriteContext"</c>) when the policy denies it;
/// or <c>"not-in-policy"</c> when the tag is absent from the policy entirely.
/// </param>
/// <param name="Provenance">
/// Human-readable location string identifying the L5X element in the fragment where
/// the write was found, e.g. <c>"fragment[5] via OTE"</c>.
/// </param>
public record GateAnnotationEntry(
    [property: JsonPropertyName("tag")]            string Tag,
    [property: JsonPropertyName("classification")] string Classification,
    [property: JsonPropertyName("provenance")]     string Provenance);

/// <summary>
/// Disclosure entry for a single tag that was created on the controller during a
/// publish operation (i.e. a call to
/// <see cref="ProposeRungEditHandler.ExecuteAsync"/> with <c>target="controller"</c>).
///
/// <para>
/// Tag creation is immediate and permanent on the controller — it is not a pending
/// edit that can be reverted by cancelling rung edits in Logix Designer.  This
/// record surfaces that fact alongside each tag name so callers cannot miss it.
/// </para>
/// </summary>
/// <param name="TagName">
/// The base tag name that was created on the controller (e.g. <c>"Cmd_MotorStart"</c>).
/// </param>
/// <param name="WritableViaLogixWrite">
/// Always <c>false</c> for tags created by the current session's publish.
/// Tags created by this publish are immediately registered under the
/// <c>CreatedByThisSession</c> self-grant guard, which causes
/// <c>logix_write</c> to deny writes for the lifetime of the current session.
/// Created tags are also External Access Read Only, so they stay unwritable
/// until an engineer sets them to Read/Write in Logix Designer.
/// </param>
public record PublishedTagEntry(
    [property: JsonPropertyName("tagName")]               string TagName,
    [property: JsonPropertyName("writableViaLogixWrite")] bool   WritableViaLogixWrite);

/// <summary>
/// Successful response returned by <see cref="ProposeRungEditHandler.ExecuteAsync"/>.
/// </summary>
/// <param name="Status">Always <c>"proposed"</c> on success.</param>
/// <param name="RoutineXPath">The routine XPath that was targeted.</param>
/// <param name="InsertPosition">The zero-based rung position at which the import was inserted.</param>
/// <param name="ReplaceCount">The number of existing rungs replaced (0 = pure insert).</param>
/// <param name="Message">Human-readable summary of the operation outcome.</param>
/// <param name="GateAnnotations">
/// Gate analysis annotation for each proven write destination found in the fragment,
/// or <c>null</c> when no policy was provided or the fragment contains no write operands.
/// Each entry carries the tag name, its gate classification, and its provenance in the fragment.
/// </param>
/// <param name="SafetyFlags">
/// Prominent flags for the three safety-relevant proposal cases: writes to safety-tagged
/// tags, writes that invalidate the gate's own grant basis, and writes to tags that do
/// not exist in the project.  <c>null</c> when no flags apply.  See
/// <see cref="ProposalSafetyFlags"/>.
/// </param>
/// <param name="StagedTags">
/// The full list of tag names currently held in the session's
/// <see cref="LogixProjectSession.StagedTagBuffer"/> at the time this response was
/// produced, sorted alphabetically.  Populated only on the workstation (staging) path
/// when at least one tag declaration has been buffered across one or more prior calls.
/// <c>null</c> (omitted from JSON) when the buffer is empty.
///
/// <para>
/// This field lets the agent explain why a rung references a tag that does not yet
/// exist on the controller: the tag was stripped from a prior staged fragment and is
/// being held locally until the fragment is promoted to the controller path.
/// </para>
/// </param>
/// <param name="TagsCreated">
/// Disclosure list of the tags that were newly created on the controller as part of
/// this publish operation (i.e. when <c>target="controller"</c>) — the delta between
/// the fragment's declared tag names and the controller's pre-publish tag census.
/// Populated only on the controller path when at least one tag name
/// declared by the fragment (directly, or merged in from the session's
/// <see cref="LogixProjectSession.StagedTagBuffer"/>) was absent from that census.
/// <c>null</c> (omitted from JSON) when no tags were created.
///
/// <para>
/// <b>IMPORTANT — LIVE CREATION</b>: unlike the rungs in this publish, tag creation is
/// not staged for review.  Each tag listed here is created on the controller
/// IMMEDIATELY at publish time — as soon as the <c>target="controller"</c> call
/// returns — and is OUTSIDE the pending-edit zone: only the rungs are held
/// pending for the operator to accept or cancel in Logix Designer.  Tag creation is
/// immediate and permanent; cancelling rung edits in Logix Designer will NOT remove
/// these tags.  Each entry carries a <c>writableViaLogixWrite</c> flag that is always
/// <c>false</c> for session-created tags: they are External Access Read Only
/// And the <c>CreatedByThisSession</c> guard also blocks
/// <c>logix_write</c> for the lifetime of this session; only an engineer setting
/// External Access to Read/Write in Logix Designer makes them writable.
/// </para>
///
/// <para>
/// A tag name the fragment declared via a context block that already existed
/// before this publish is deliberately excluded here — see
/// <see cref="ContextTags"/> — so this list never misrepresents a pre-existing tag
/// as created.
/// </para>
/// </param>
/// <param name="ContextTags">
/// Disclosure list of tag names the publishing fragment declared (directly, or via
/// tags merged in from the session's staging buffer) that were already present in
/// the controller's pre-publish tag census. These tags were referenced
/// by the fragment but not created or otherwise modified by this publish, and — per
/// the delta semantics used for <see cref="TagsCreated"/> — they are never marked
/// created-by-this-session, so they remain writable via <c>logix_write</c> under
/// whatever gate policy already applied to them.  Populated only on the controller
/// path when at least one declared tag name matched the pre-publish census.
/// <c>null</c> (omitted from JSON) when no declared tag names were pre-existing.
/// </param>
/// <param name="ExternalAccessForced">
/// Tags declared by a caller-supplied <c>l5xFragment</c> that were not in the
/// pre-stage tag census and whose <c>ExternalAccess</c> was not <c>"Read Only"</c>
/// (absent, <c>"Read/Write"</c>, <c>"None"</c>, ...); the server rewrote each to
/// <c>"Read Only"</c> before import (entry = bare tag name). Data types Logix rejects as
/// Read Only (HMIBC) are instead given <c>"Read/Write"</c>, including tags the server built
/// from typed <c>tags</c>; their entry is <c>"Name: Read/Write (reason)"</c>. External Access is the engineer-owned write grant
/// for created tags, so an agent cannot self-grant it. UDT member-level access is left
/// alone. <c>null</c> (omitted from JSON) when nothing was forced.
/// </param>
/// <param name="StagedRungs">
/// The number of rungs already recorded in the session's staged-rung buffer
/// (<see cref="LogixProjectSession.StagedRungBuffer"/>) at <c>insertPosition</c>
/// at the time this publish ran — i.e. how many rungs a prior
/// <c>target="workstation"</c> stage had already left pending at this position.
/// Populated only on the controller (publish) path.
/// <c>null</c> (omitted from JSON) on the workstation path.
/// </param>
/// <param name="FragmentRungs">
/// The number of <c>&lt;Rung&gt;</c> elements this publish's own <c>l5xFragment</c>
/// carried, before duplicate removal. <c>0</c> for the empty-fragment publish path.
/// Populated only on the controller (publish) path.
/// <c>null</c> (omitted from JSON) on the workstation path.
/// </param>
/// <param name="DroppedDuplicates">
/// The number of <paramref name="FragmentRungs"/> that were dropped because their
/// normalised text byte-equalled a rung already counted in
/// <paramref name="StagedRungs"/> at the same insert position, and so were not
/// sent to the SDK a second time. Populated only on the
/// controller (publish) path. <c>null</c> (omitted from JSON) on the workstation
/// path.
/// </param>
/// <param name="StagedReplaceCount">
/// The caller's original <c>replaceCount</c> value when it was silently overridden
/// to <c>0</c> for the SDK import call — present whenever the outgoing fragment
/// carried no new rungs (either because <c>l5xFragment</c> was empty/absent or
/// every fragment rung was dropped as a staged duplicate) AND the caller passed a
/// non-zero <c>replaceCount</c>. The staged image already contained the
/// replacement from the prior <c>target="workstation"</c> call; re-applying the
/// same <c>replaceCount</c> in the <c>AcceptEdits</c> SDK call would overwrite
/// those staged rungs, so <c>0</c> is used instead.  <c>null</c> (omitted from
/// JSON) when the caller's <c>replaceCount</c> was used as-is.
/// </param>
/// <param name="Verification">
/// Explicit post-publish verification status. Controller path only;
/// <c>null</c> (omitted from JSON) for <c>target="workstation"</c>. One of:
/// <c>"verified"</c> (the pending census was read and every expected rung — staged
/// buffer plus this publish's own fragment rungs — was found as an I/IR rung),
/// <c>"unverified"</c> (the census could not be read back), or
/// <c>"notApplicable"</c> (nothing was staged and the fragment carried no rungs).
/// </param>
/// <param name="UnexpectedPendingRungs">
/// Count of pending I/IR rungs in the routine after the publish that were neither
/// expected nor already present before the publish (e.g. another session's pending
/// edits). Informational only — never fails the publish. <c>null</c> (omitted from
/// JSON) when 0 or when it could not be determined.
/// </param>
/// <param name="Note">
/// Human-readable explanation present whenever <see cref="StagedReplaceCount"/> is
/// set — describes why the caller's <c>replaceCount</c> was not forwarded to the
/// SDK and what value was used instead. <c>null</c> (omitted from JSON) when the
/// caller's <c>replaceCount</c> was used as-is.
/// </param>
/// <param name="PresetsVerified">
/// <c>[{tag, pre}]</c> for each literal preset of a TON/TOF/RTO/CTU/CTD in the published rungs
/// whose <c>&lt;tag&gt;.PRE</c>, read back after a controller or live publish, equals the literal.
/// <c>null</c> (omitted) when there was none or no tag service was available.
/// </param>
/// <param name="PresetMismatches">
/// <c>PresetMismatch</c> entries (tag, expected, found) for presets whose tag holds another value.
/// Part of the census result; it never sets <c>PublishMismatchUnresolved</c> because the rung text matched.
/// </param>
/// <param name="PresetsUnread">an earlier change: tags whose <c>.PRE</c> could not be read back (neither verified nor mismatched).</param>
/// <param name="InsertPosition">
/// The zero-based position of the call's rungs. For a controller publish it is the first index the call's routine's
/// rungs landed at. <c>null</c> when more than one routine was published (G43): positions are then reported
/// per routine in <c>publishedRoutines[i].insertPosition</c> and <c>landedIndices</c>.
/// </param>
/// <param name="IgnoredInsertPosition">
/// The caller's <c>insertPosition</c> when it was ignored: a controller publish whose outgoing
/// fragment carries no new rungs commits the staged buffer at a server-chosen position (the end
/// of the call's routine in the session image), reported as <see cref="InsertPosition"/>.
/// <c>null</c> (omitted from JSON) when the caller's position was used, i.e. when the first index the call's routine's
/// rungs landed at equals it (G52; the same rule as the "was ignored" note).
/// </param>
public record ProposeRungEditResult(
    [property: JsonPropertyName("status")]          string Status,
    [property: JsonPropertyName("routineXPath")]    string RoutineXPath,
    [property: JsonPropertyName("insertPosition")]  uint?  InsertPosition,
    [property: JsonPropertyName("replaceCount")]    uint   ReplaceCount,
    [property: JsonPropertyName("message")]         string Message,
    [property: JsonPropertyName("gateAnnotations")] IReadOnlyList<GateAnnotationEntry>? GateAnnotations = null,
    [property: JsonPropertyName("safetyFlags")]     ProposalSafetyFlags? SafetyFlags = null,
    [property: JsonPropertyName("stagedTags"),           JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? StagedTags = null,
    [property: JsonPropertyName("tagsCreated"),          JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PublishedTagEntry>? TagsCreated = null,
    [property: JsonPropertyName("contextTags"),          JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ContextTags = null,
    [property: JsonPropertyName("stagedRungs"),          JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int?  StagedRungs = null,
    [property: JsonPropertyName("fragmentRungs"),        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int?  FragmentRungs = null,
    [property: JsonPropertyName("droppedDuplicates"),    JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int?  DroppedDuplicates = null,
    [property: JsonPropertyName("stagedReplaceCount"),   JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? StagedReplaceCount = null,
    [property: JsonPropertyName("note"),                 JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null,
    [property: JsonPropertyName("verifiedPendingRungs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int?  VerifiedPendingRungs = null,
    [property: JsonPropertyName("publishMode"),          JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublishMode = null,
    [property: JsonPropertyName("verification"),         JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Verification = null,
    [property: JsonPropertyName("unexpectedPendingRungs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UnexpectedPendingRungs = null,
    [property: JsonPropertyName("commentsPreserved"),    JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? CommentsPreserved = null,
    [property: JsonPropertyName("finalized"),            JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Finalized = null,
    [property: JsonPropertyName("afterRungs"),           JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PendingEditRungEntry>? AfterRungs = null,
    [property: JsonPropertyName("envelope"),             JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Envelope = null,
    [property: JsonPropertyName("deletedRungs"),         JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<DeletedRungEntry>? DeletedRungs = null,
    [property: JsonPropertyName("undo"),                 JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UndoBlock? Undo = null,
    [property: JsonPropertyName("zoneNote"),             JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ZoneNote = null,
    [property: JsonPropertyName("externalAccessForced"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ExternalAccessForced = null,
    [property: JsonPropertyName("buildVerify"),          JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BuildVerify = null,
    [property: JsonPropertyName("designerVersion"),      JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DesignerVersion = null,
    [property: JsonPropertyName("ignoredInsertPosition"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? IgnoredInsertPosition = null,
    [property: JsonPropertyName("publishedRoutines"),    JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PublishedRoutine>? PublishedRoutines = null,
    [property: JsonPropertyName("unchangedReplacements"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? UnchangedReplacements = null,
    [property: JsonPropertyName("presetsVerified"),      JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PresetVerified>? PresetsVerified = null,
    [property: JsonPropertyName("presetMismatches"),     JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PresetMismatch>? PresetMismatches = null,
    [property: JsonPropertyName("presetsUnread"),        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? PresetsUnread = null,
    [property: JsonPropertyName("warnings"),             JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null,
    [property: JsonPropertyName("stagedIn"),             JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StagedIn = null,
    [property: JsonPropertyName("verified"),             JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Verified = null,
    [property: JsonPropertyName("verifiedNote"),         JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VerifiedNote = null,
    [property: JsonPropertyName("verifyRejectReasons"),  JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RejectReason>? VerifyRejectReasons = null,
    [property: JsonPropertyName("retries"),              JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Retries = null,
    [property: JsonPropertyName("commentRestorableOnCancel"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? CommentRestorableOnCancel = null,
    [property: JsonPropertyName("postImportBuildCheck"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PostImportBuildCheck = null,
    [property: JsonPropertyName("phaseTimings"),         JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PhaseTiming>? PhaseTimings = null);

/// <summary>
/// One routine the post-publish census confirmed (<c>publishedRoutines</c>).
/// </summary>
/// <param name="Routine">The routine XPath.</param>
/// <param name="InsertedOrReplacing">
/// Number of rungs of this routine found pending (I/IR) in the post-publish export that are among the
/// rungs expected for this routine.
/// </param>
/// <param name="LandedIndices">
/// Zero-based indices, in the post-publish export of this routine, of every pending rung that is among the
/// rungs expected for it (its staged rungs plus this call's own fragment rungs). Reported for staged-only
/// publishes as well as publishes that carry a fragment.
/// </param>
/// <param name="InsertPosition">
/// The first of <paramref name="LandedIndices"/>: the position this routine's rungs start at. Omitted without landed indices.
/// </param>
/// <param name="ReplaceCount">
/// Existing rungs of this routine the publish replaced: its staged replace counts plus, for the routine the call
/// targets, the call's own replace count. The top-level <c>replaceCount</c> is the sum over all routines.
/// </param>
/// <param name="PublishedTexts">
/// Not serialized. Texts of the pending rungs that landed, in order, so the caller can follow the publish in the
/// session image. Present only when the landed rungs form one contiguous block.
/// </param>
/// <param name="PublishedComments">
/// Not serialized. Parallel to <paramref name="PublishedTexts"/>: the comment each landed rung carries on the
/// controller (a preserved original comment, an explicit new comment, or <c>null</c> for none), so the session image
/// can keep rung comments on accept.
/// </param>
public sealed record PublishedRoutine(
    [property: JsonPropertyName("routine")]             string Routine,
    [property: JsonPropertyName("insertedOrReplacing")] int    InsertedOrReplacing,
    [property: JsonPropertyName("landedIndices"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? LandedIndices = null,
    [property: JsonPropertyName("insertPosition"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? InsertPosition = null,
    [property: JsonPropertyName("replaceCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? ReplaceCount = null,
    [property: JsonIgnore] IReadOnlyList<string>? PublishedTexts = null,
    [property: JsonIgnore] IReadOnlyList<string?>? PublishedComments = null);

/// <summary>
/// One routine whose post-publish pending census differed from what was expected for it alone.
/// </summary>
/// <param name="Routine">The routine XPath.</param>
/// <param name="ExpectedRungs">This routine's expected texts: its staged rungs plus the fragment's rungs when the fragment targets it.</param>
/// <param name="FoundRungs">The I/IR rung texts actually found in this routine's post-publish export.</param>
public sealed record RoutineCensusMismatch(
    [property: JsonPropertyName("routine")]       string                Routine,
    [property: JsonPropertyName("expectedRungs")] IReadOnlyList<string> ExpectedRungs,
    [property: JsonPropertyName("foundRungs")]    IReadOnlyList<string> FoundRungs);

/// <summary>
/// What was observed on the routine after an error that followed a successful import
/// (<c>pendingAfterError</c>). Never inferred: <see cref="State"/> is <c>"unknown"</c> unless a
/// read-back (or a successful <c>GoOfflineAsync</c> on the workstation path) confirmed it.
/// </summary>
/// <param name="State"><c>"pending"</c> (rungs found), <c>"none"</c> (confirmed none) or <c>"unknown"</c>.</param>
/// <param name="Rungs">Pending rung texts read back; <c>null</c> when <see cref="State"/> is <c>"unknown"</c>.</param>
/// <param name="ReadbackError">Why the read-back failed; only set when <see cref="State"/> is <c>"unknown"</c>.</param>
public sealed record PendingAfterError(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("rungs"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<string>? Rungs = null,
    [property: JsonPropertyName("readbackError"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? ReadbackError = null);

/// <summary>
/// One rung of an <see cref="UndoBlock"/> pre-image. Has the same JSON shape as
/// <see cref="RungInput"/> (<c>text</c>, <c>comment</c>) so it can be passed straight back as
/// the <c>rungs</c> argument. <see cref="Comment"/> is never <c>null</c>: an empty string means
/// "this rung had no comment" and tells the live path not to carry a comment over from the
/// rung it replaces (the carry-over must not overwrite a restored comment).
/// </summary>
/// <param name="Text">Normalized rung text as exported immediately before the live import.</param>
/// <param name="Comment">The rung's original comment, or an empty string when it had none.</param>
public sealed record UndoRungEntry(
    [property: JsonPropertyName("text")]    string Text,
    [property: JsonPropertyName("comment")] string Comment);

/// <summary>
/// Server-built undo handle returned on every successful <c>target="live"</c> result.
/// Passing its fields back to <c>logix_propose_rung_edit(target='live', confirmedByUser=true,
/// routineXPath, insertPosition, replaceCount, rungs, expectedRungText)</c> restores the rungs
/// the original call replaced or removed. It is an ordinary live call: guarded by the usual
/// stale check (<c>StaleTarget</c> if anything changed since) and it returns its own
/// <see cref="UndoBlock"/> (redo). The block travels in the response; the server stores nothing.
/// </summary>
/// <param name="RoutineXPath">The routine the original call changed.</param>
/// <param name="InsertPosition">Zero-based position of the first rung the original call touched.</param>
/// <param name="ReplaceCount">Number of rungs the original call left at <c>InsertPosition</c>.</param>
/// <param name="Rungs">Pre-image: the rungs the original call replaced/removed (empty for a pure insert).</param>
/// <param name="ExpectedRungText">Texts now at <c>InsertPosition</c>…<c>InsertPosition + ReplaceCount - 1</c>.</param>
public sealed record UndoBlock(
    [property: JsonPropertyName("routineXPath")]     string                       RoutineXPath,
    [property: JsonPropertyName("insertPosition")]   uint                         InsertPosition,
    [property: JsonPropertyName("replaceCount")]     uint                         ReplaceCount,
    [property: JsonPropertyName("rungs")]            IReadOnlyList<UndoRungEntry> Rungs,
    [property: JsonPropertyName("expectedRungText")] IReadOnlyList<string>        ExpectedRungText);

/// <summary>
/// One rung removed by a live rung delete: its number before the delete,
/// its normalized text and its comment (<c>null</c> when it had none).
/// </summary>
/// <param name="Number">Zero-based rung number before the delete.</param>
/// <param name="Text">Normalized rung text as exported immediately before the import.</param>
/// <param name="Comment">The rung's comment, or <c>null</c> when it had none.</param>
public record DeletedRungEntry(
    [property: JsonPropertyName("number")]  int     Number,
    [property: JsonPropertyName("text")]    string  Text,
    [property: JsonPropertyName("comment")] string? Comment);

/// <summary>
/// Successful response returned by <see cref="ProposeRungEditHandler.ExecuteOfflineAsync"/>
/// when the rung edit is applied directly to the project file without contacting a controller.
/// </summary>
/// <param name="Status">Always <c>"saved-to-file"</c> on success.</param>
/// <param name="FilePath">Absolute path to the project file that was modified on disk.</param>
/// <param name="RoutineXPath">The routine XPath that was targeted.</param>
/// <param name="InsertPosition">The zero-based rung position at which the rungs were inserted.</param>
/// <param name="ReplaceCount">The number of existing rungs replaced (0 = pure insert).</param>
/// <param name="Message">
/// Human-readable summary explicitly stating that the file was modified on disk and
/// no controller was contacted.  Never uses the word "proposed".
/// </param>
/// <param name="GateAnnotations">
/// Gate analysis annotation for each proven write destination found in the fragment,
/// or <c>null</c> when no policy was provided or the fragment contains no write operands.
/// Each entry carries the tag name, its gate classification, and its provenance in the fragment.
/// </param>
/// <param name="SafetyFlags">
/// Prominent flags for the three safety-relevant proposal cases: writes to safety-tagged
/// tags, writes that invalidate the gate's own grant basis, and writes to tags that do
/// not exist in the project.  <c>null</c> when no flags apply.  See
/// <see cref="ProposalSafetyFlags"/>.
/// </param>
/// <param name="PublishMode">Publish mode stamped by the tool layer.</param>
/// <param name="Envelope">
/// Who built the import envelope: <c>"server-built"</c> or <c>"caller"</c>.
/// </param>
public record ProposeRungEditOfflineResult(
    [property: JsonPropertyName("status")]          string Status,
    [property: JsonPropertyName("filePath")]        string FilePath,
    [property: JsonPropertyName("routineXPath")]    string RoutineXPath,
    [property: JsonPropertyName("insertPosition")]  uint   InsertPosition,
    [property: JsonPropertyName("replaceCount")]    uint   ReplaceCount,
    [property: JsonPropertyName("message")]         string Message,
    [property: JsonPropertyName("gateAnnotations")] IReadOnlyList<GateAnnotationEntry>? GateAnnotations = null,
    [property: JsonPropertyName("safetyFlags")]     ProposalSafetyFlags? SafetyFlags = null,
    [property: JsonPropertyName("publishMode"),     JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublishMode = null,
    [property: JsonPropertyName("envelope"),        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Envelope = null,
    [property: JsonPropertyName("warnings"),        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null);

/// <summary>
/// Error response returned by <see cref="ProposeRungEditHandler.ExecuteAsync"/>
/// when validation fails or the SDK call is unsuccessful.
/// </summary>
/// <param name="Error">Human-readable description of the failure.</param>
/// <param name="Hint">
/// Optional imperative remediation text.  Present when an unambiguous corrective
/// action is known — e.g. for a build failure, states that the rung must be fixed
/// and what is known about the pending edit.  <c>null</c> otherwise.
/// </param>
/// <param name="DocsAnchor">
/// Optional stable slug referencing the relevant <c>docs/</c> section
/// (e.g. <c>"propose-rung-edit-errors"</c>).  <c>null</c> when no targeted
/// documentation exists for this error.
/// </param>
/// <param name="Kind">
/// Optional machine-readable error category for a controller-target import
/// failure: <c>"ImportRejectedByImporter"</c> when the
/// SDK importer rejected a reference in the fragment
/// (<c>XMLSrv_E_IMPORT_ABORTED_NO_CHANGES</c>) with no changes made on the
/// controller, or <c>"PendingEditsPresent"</c> when a <c>logix_pending_edits</c>
/// tier-1 read confirms a non-<c>"none"</c> edit-zone state blocked the import.
/// <c>"BuildFailedEditPending"</c> when the SDK build check really failed after an import that
/// went to the controller: the edit is on the controller and was not rolled back.
/// <c>null</c> for every other error on this handler (including the unrelated
/// envelope-validation failures, which never carry a Kind).
/// </param>
/// <param name="PendingAfterError">
/// Present on every error returned after the import succeeded: the pending rungs read back,
/// or <c>"unknown"</c> with the read-back error.
/// </param>
/// <param name="DesignerVersion">Logix Designer <c>major.minor</c> when known; set on build-check errors.</param>
/// <param name="TagsInFragment">
/// Present on an import-failure error: the names of every tag declared in the fragment that was sent
/// to the SDK (the fragment after envelope building and staged-tag merging), so the caller can see whether
/// its tags reached the import instead of guessing. Empty when the fragment declared none.
/// </param>
/// <param name="Rungs">
/// Present on <c>Kind="WrongMnemonicSet"</c> and <c>Kind="UnsupportedOnRevision"</c>: one entry per offending
/// rung with <c>rungIndex</c>, <c>found</c>, <c>expected</c>, <c>targetMajorRevision</c> and <c>suggestedText</c>.
/// </param>
/// <param name="ImportLog">
/// Present on every failed-import error (an empty array when the SDK streamed nothing): the importer's log
/// entries parsed from the captured callbacks as <c>{severity, mnemonic, hex, text, hint?}</c>.
/// </param>
public record ProposeRungEditError(
    [property: JsonPropertyName("error")]       string  Error,
    [property: JsonPropertyName("hint")]        string? Hint       = null,
    [property: JsonPropertyName("docs_anchor")] string? DocsAnchor = null,
    [property: JsonPropertyName("kind"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Kind = null,
    [property: JsonPropertyName("beforeRungs"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<PendingEditRungEntry>? BeforeRungs = null,
    [property: JsonPropertyName("afterRungs"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<PendingEditRungEntry>? AfterRungs = null,
    [property: JsonPropertyName("pendingAfterError"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               PendingAfterError? PendingAfterError = null,
    [property: JsonPropertyName("designerVersion"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? DesignerVersion = null,
    [property: JsonPropertyName("session"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Session = null,
    [property: JsonPropertyName("rejectReasons"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<RejectReason>? RejectReasons = null,
    [property: JsonPropertyName("rejectReasonSource"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? RejectReasonSource = null,
    [property: JsonPropertyName("tagsInFragment"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<string>? TagsInFragment = null,
    [property: JsonPropertyName("rungs"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<MnemonicFinding>? Rungs = null,
    [property: JsonPropertyName("importLog"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<ImportLogEntry>? ImportLog = null,
    [property: JsonPropertyName("verifyRejectReasons"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<RejectReason>? VerifyRejectReasons = null,
    [property: JsonPropertyName("stagedImage"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<StagedImageRung>? StagedImage = null,
    [property: JsonPropertyName("retries"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               int? Retries = null,
    [property: JsonPropertyName("preCheck"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               PublishPreCheckReport? PreCheck = null,
    [property: JsonPropertyName("phaseTimings"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<PhaseTiming>? PhaseTimings = null,
    [property: JsonPropertyName("retryAfterSeconds"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               int? RetryAfterSeconds = null);

/// <summary>
/// The tag and instruction pre-check finding reported alongside a <c>PendingEditsBlockImport</c> refusal, so one
/// round trip shows both problems (logix-mcp-qpqy.12).
/// </summary>
/// <param name="Kind">The pre-check refusal kind (<c>UndefinedTag</c>, <c>UnknownInstruction</c>, ...).</param>
/// <param name="Error">The pre-check message.</param>
/// <param name="Hint">The pre-check hint, when it has one.</param>
/// <param name="RejectReasons">Every finding of the pre-check, one entry each.</param>
public sealed record PublishPreCheckReport(
    [property: JsonPropertyName("kind"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("hint"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint = null,
    [property: JsonPropertyName("rejectReasons"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RejectReason>? RejectReasons = null);

/// <summary>
/// One rung of the session's staged routine image, as listed on a <c>StagedPositionConflict</c> (logix-mcp-qpqy.3).
/// </summary>
/// <param name="Index">The rung's index in the image (what Studio numbers the rung). Positions in hints and inserts do
/// NOT use this index: they count only visible rungs, and rungs in state <c>R</c> or <c>D</c> are not counted.</param>
/// <param name="Text">The rung's neutral text.</param>
/// <param name="State">The rung's edit state (the L5X <c>Type</c>): <c>N</c> normal, <c>I</c>/<c>IR</c> staged,
/// <c>R</c>/<c>D</c> a replaced or deleted original that is hidden once the edit is published.</param>
public sealed record StagedImageRung(
    [property: JsonPropertyName("index")] int    Index,
    [property: JsonPropertyName("text")]  string Text,
    [property: JsonPropertyName("state")] string State);

/// <summary>
/// One reason the importer rejected a fragment, found by replaying it offline.
/// </summary>
/// <param name="RungIndex">The rung the reason text names, when it names one; <c>null</c> (written as <c>rungIndex: null</c>) otherwise.</param>
/// <param name="Text">The verify error text.</param>
/// <param name="Mnemonic">The importer's mnemonic (e.g. <c>RxCMP_E_OPERAND_TAGNOTFOUND</c>) when the reason came from the captured ImportLog.</param>
/// <param name="Hex">The importer's hex code (e.g. <c>80042c01</c>) when the reason came from the captured ImportLog.</param>
/// <param name="Hint">A plain-language hint for a mnemonic the server recognises (from <see cref="ImportLogParser"/>); omitted otherwise.</param>
/// <param name="Kind">The pre-check category (<c>UndefinedTag</c>, <c>TagNotPublic</c>, <c>UndefinedMember</c>) when the reason came from the reference pre-check; omitted otherwise.</param>
public sealed record RejectReason(
    [property: JsonPropertyName("rungIndex")] int? RungIndex,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("mnemonic"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mnemonic = null,
    [property: JsonPropertyName("hex"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hex = null,
    [property: JsonPropertyName("hint"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint = null,
    [property: JsonPropertyName("kind"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null);

/// <summary>
/// One typed rung supplied to <c>logix_propose_rung_edit</c> via the <c>rungs</c>
/// parameter. Records are used so the MCP SDK publishes a JSON schema.
/// </summary>
/// <param name="Text">
/// Rung text only (e.g. <c>XIC(Start)OTE(Lamp);</c>) — never XML. A missing trailing
/// <c>;</c> is appended by the server.
/// </param>
/// <param name="Comment">
/// Optional rung comment (plain text). Omitted (null) keeps the comment of the rung being replaced;
/// an explicit empty string clears it; any other text replaces it.
/// </param>
public sealed record RungInput(
    [property: JsonPropertyName("text")]    string  Text,
    [property: JsonPropertyName("comment"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Comment = null);

/// <summary>
/// One typed tag declaration supplied to <c>logix_propose_rung_edit</c> via the
/// <c>tags</c> parameter.
/// </summary>
/// <param name="Name">Tag name; <c>^[A-Za-z_][A-Za-z0-9_]{0,39}$</c> and no <c>__</c>.</param>
/// <param name="DataType">Data type name (e.g. <c>BOOL</c>, <c>DINT</c>, <c>TIMER</c>); non-empty.</param>
/// <param name="Radix">Optional display radix.</param>
/// <param name="Description">Optional tag description (plain text).</param>
/// <param name="Dimensions">Optional array dimensions (e.g. <c>10</c>).</param>
public sealed record TagInput(
    [property: JsonPropertyName("name")]     string  Name,
    [property: JsonPropertyName("dataType")] string  DataType,
    [property: JsonPropertyName("radix"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Radix = null,
    [property: JsonPropertyName("description"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Description = null,
    [property: JsonPropertyName("dimensions"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Dimensions = null);

/// <summary>
/// Error response returned by <see cref="ProposeRungEditHandler.ExecuteAsync"/>
/// when the post-publish pending census does not match the expected staged rungs.
///
/// <para>
/// Returned instead of <see cref="ProposeRungEditResult"/> when a controller-target
/// publish (target="controller") completes its SDK AcceptEdits call but the
/// subsequent pending-census read finds that not all staged rungs appear as
/// <c>I</c>/<c>IR</c>-type pending rungs in the export.  This means the controller's
/// pending state does not match what was expected — the engineer must review in
/// Studio 5000 (Logic → Online Edits) before proceeding, and must never receive a
/// <c>status='proposed'</c> response in this situation.
/// </para>
/// </summary>
/// <param name="Error">Human-readable description of the mismatch and remediation steps.</param>
/// <param name="Kind">Always <c>"PublishMismatch"</c>.</param>
/// <param name="ExpectedRungs">
/// Normalised <c>&lt;Text&gt;</c> content of every rung expected to appear as a pending
/// <c>I</c>/<c>IR</c> rung, over the differing routines only (flat; see <c>Routines</c> for the per-routine split).
/// </param>
/// <param name="PendingRungs">
/// Normalised <c>&lt;Text&gt;</c> content of every <c>I</c>/<c>IR</c>-type rung
/// that was actually found in the post-publish exports of the differing routines only.
/// </param>
/// <param name="PendingAfterError">Pending rungs read back, for the call's routine when it differs, else the first differing routine.</param>
/// <param name="Routines">
/// The differing routine(s), each with its own expected and found texts. Routines
/// whose census matched are not listed.
/// </param>
public record ProposeRungEditMismatch(
    [property: JsonPropertyName("error")]         string                Error,
    [property: JsonPropertyName("kind")]          string                Kind,
    [property: JsonPropertyName("expectedRungs")] IReadOnlyList<string> ExpectedRungs,
    [property: JsonPropertyName("pendingRungs")]  IReadOnlyList<string> PendingRungs,
    [property: JsonPropertyName("pendingAfterError"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               PendingAfterError? PendingAfterError = null,
    [property: JsonPropertyName("routines"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<RoutineCensusMismatch>? Routines = null);

// ── ProposeRungEditHandler ────────────────────────────────────────────────────

/// <summary>
/// Tracks the single <c>rung_edit</c> audit line of one <c>logix_propose_rung_edit</c> call (an earlier change
/// requirement 8). The first write wins; later writes for the same call are dropped, so a call can never
/// leave two lines.
/// </summary>
internal sealed class RungEditAuditScope
{
    private readonly uint   _insertPosition;
    private readonly string _destination;

    /// <summary>The routine the audit line names; updated to the exported casing once it is resolved.</summary>
    public string Routine { get; set; }

    /// <summary>Whether this call has already written its audit line.</summary>
    public bool Written { get; private set; }

    /// <summary>
    /// The correlation id of this call's <c>"sending"</c> intent line, or <c>null</c> when none was written
    /// (nothing was sent to the controller). The final outcome line carries it.
    /// </summary>
    public string? Correlation { get; private set; }

    /// <summary>
    /// Writes the <c>"sending"</c> intent line before a controller-changing import (logix-mcp-qpqy.4). It is not
    /// this call's single outcome line: <see cref="Written"/> stays <c>false</c>, and the outcome line completes
    /// it through the shared <see cref="Correlation"/>. Only the first call writes.
    /// </summary>
    /// <param name="insertPosition">Insert position the import will use.</param>
    /// <param name="rungTexts">Normalised texts of the rungs the import sends, recorded on the line.</param>
    public void LogSending(uint insertPosition, IReadOnlyList<string>? rungTexts = null)
    {
        if (Correlation is not null)
            return;
        var id = AuditLogger.NewRungEditCorrelation();
        AuditLogger.LogRungEditSending(Routine, insertPosition, _destination, id, rungTexts);
        Correlation = id;
    }

    /// <summary>Creates the scope for one call.</summary>
    /// <param name="routine">Routine XPath as supplied by the caller.</param>
    /// <param name="insertPosition">Insert position as supplied by the caller.</param>
    /// <param name="destination">Resolved destination: controller, live or workstation.</param>
    public RungEditAuditScope(string routine, uint insertPosition, string destination)
    {
        Routine         = routine;
        _insertPosition = insertPosition;
        _destination    = destination;
    }

    /// <summary>Writes this call's audit line unless one was already written. Same arguments as <see cref="AuditLogger.LogRungEdit"/>.</summary>
    public void Log(
        string                              routine,
        uint                                insertPosition,
        string                              outcome,
        string?                             structuralVerify,
        string?                             destination       = null,
        IReadOnlyList<AuditGateAnnotation>? gateAnnotations   = null,
        bool?                               confirmedByUser   = null,
        string?                             buildVerify       = null,
        string?                             designerVersion   = null,
        string?                             stagedIn          = null,
        bool?                               verified          = null,
        string?                             kind              = null)
    {
        if (Written)
            return;
        Written = true;
        AuditLogger.LogRungEdit(routine, insertPosition, outcome, structuralVerify, destination,
            gateAnnotations, confirmedByUser, buildVerify, designerVersion, stagedIn, verified, kind,
            correlation: Correlation);
    }

    /// <summary>Writes a bare line for the call (routine, position and destination as known) unless one exists.</summary>
    /// <param name="outcome">The outcome to record.</param>
    /// <param name="kind">The refusal kind, if any.</param>
    public void LogUnlessWritten(string outcome, string? kind) =>
        Log(Routine, _insertPosition, outcome, null, _destination, kind: kind);
}

/// <summary>
/// Pure handler for the <c>logix_propose_rung_edit</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
///
/// <para>
/// Writes the supplied inline L5X fragment to a temporary file, then calls
/// the appropriate SDK overload based on the <c>target</c> parameter:
/// <list type="bullet">
///   <item><term><c>null</c> / <c>"workstation"</c></term>
///     <description>
///     Calls <see cref="ILogixProject.PartialImportRungsFromXmlFileAsync"/> with
///     <see cref="PartialImportOption.LeaveEdits"/>.  The edit stays in the
///     server's in-memory project image only (pre-PRD behaviour, strictly
///     unchanged).
///     </description></item>
///   <item><term><c>"controller"</c></term>
///     <description>
///     Calls <c>PartialImportRungsFromXmlFileAsync</c> with
///     <see cref="PartialImportOption.AcceptEdits"/>, so the edit is immediately
///     accepted and visible to any Logix Designer session online with the
///     controller.
///     </description></item>
/// </list>
/// Only <see cref="PartialImportOption.LeaveEdits"/> and
/// <see cref="PartialImportOption.AcceptEdits"/> are ever dispatched; an
/// explicit guard enforces this invariant at runtime, making any other
/// option impossible.
/// </para>
///
/// <para>
/// The temporary file is always deleted before the method returns, whether the
/// SDK call succeeds or fails.  Callers never supply or manage file paths.
/// </para>
/// </summary>
public static class ProposeRungEditHandler
{
    /// <summary>
    /// A minimal, complete L5X fragment containing a single NOP rung that the
    /// Logix Designer SDK accepts for import via
    /// <see cref="ILogixProject.PartialImportRungsFromXmlFileAsync"/>.
    ///
    /// <para>
    /// The SDK requires the full RSLogix5000Content envelope — a bare
    /// <c>&lt;Rung&gt;</c> or <c>&lt;RLLContent&gt;</c> element is rejected
    /// with "allows only to import RLLContent".  This constant captures the
    /// exact required nesting so it can be referenced by both production
    /// tool-description strings and tests, ensuring they can never drift apart.
    /// </para>
    ///
    /// <para>
    /// Required envelope:
    /// <c>RSLogix5000Content &gt; Controller &gt; Programs &gt; Program &gt;
    /// Routines &gt; Routine &gt; RLLContent &gt; Rung &gt; Text &gt; CDATA</c>
    /// </para>
    ///
    /// <para>
    /// The <c>Program Name</c> and <c>Routine Name</c> attributes must match
    /// the target routine identified by <c>routineXPath</c>.
    /// </para>
    /// </summary>
    public const string MinimalNopFragment =
        "<RSLogix5000Content SchemaRevision=\"1.0\">" +
          "<Controller>" +
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

    // ── Typed rungs/tags input validation ─────────────

    private static readonly System.Text.RegularExpressions.Regex TypedTagNameRegex =
        new(@"^[A-Za-z_][A-Za-z0-9_]{0,39}\z",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private const string TypedInputSkillPointer = ProposeRungChangeSkillPointer;

    /// <summary>
    /// Trailing pointer to the <c>lgx-propose-rung-change</c> playbook, appended to
    /// <c>RoundTripHint</c>, the <c>EnvelopeIncomplete</c>/<c>InvalidRungText</c> messages and
    /// <c>XMLSrv_E_*</c> import-failure errors so the agent knows which skill to load.
    /// </summary>
    internal const string ProposeRungChangeSkillPointer =
        " See logix_get_skill(\"lgx-propose-rung-change\").";

    /// <summary>
    /// Appends <see cref="ProposeRungChangeSkillPointer"/> to <paramref name="message"/> when the
    /// SDK failure text or captured ImportLog names an <c>XMLSrv_E_*</c> mnemonic.
    /// </summary>
    private static string WithSkillPointerForXmlSrvFailure(
        string message, string sdkDetail, IReadOnlyList<string> importLogEntries)
    {
        var guidance = RockwellErrorHints.AdditionalGuidance(sdkDetail);
        if (guidance.Length == 0)
            guidance = importLogEntries
                .Select(RockwellErrorHints.AdditionalGuidance)
                .FirstOrDefault(g => g.Length > 0) ?? string.Empty;

        return RockwellErrorHints.IsXmlSrvFailure(sdkDetail)
            || importLogEntries.Any(RockwellErrorHints.IsXmlSrvFailure)
                ? message + ProposeRungChangeSkillPointer + guidance
                : message + guidance;
    }

    /// <summary>
    /// Validates the typed <c>rungs</c>/<c>tags</c> input of
    /// <c>logix_propose_rung_edit</c> without performing any I/O or SDK call.
    ///
    /// <para>
    /// Returns <c>null</c> when the input is valid (or absent). Otherwise returns a
    /// <see cref="ProposeRungEditError"/> with <c>Kind</c> =
    /// <c>AmbiguousInput</c> (typed input together with <c>l5xFragment</c>),
    /// <c>InvalidRungText</c> (empty text, or text containing <c>&lt;![CDATA[</c>,
    /// <c>]]&gt;</c> or <c>&lt;Rung</c>) or <c>InvalidTag</c> (bad name or empty
    /// data type; the message names the offending field).
    /// </para>
    /// </summary>
    /// <param name="l5xFragment">The raw fragment argument, if any.</param>
    /// <param name="rungs">Typed rungs, if any.</param>
    /// <param name="tags">Typed tags, if any.</param>
    /// <param name="normalizedRungs">
    /// The rungs with text trimmed and a trailing <c>;</c> appended when missing;
    /// empty when there are none or validation failed.
    /// </param>
    /// <returns>An error, or <c>null</c> when the input is valid.</returns>
    public static ProposeRungEditError? ValidateTypedInput(
        string?                 l5xFragment,
        IReadOnlyList<RungInput>? rungs,
        IReadOnlyList<TagInput>?  tags,
        out IReadOnlyList<RungInput> normalizedRungs)
    {
        normalizedRungs = Array.Empty<RungInput>();
        bool hasRungs = rungs is { Count: > 0 };
        bool hasTags  = tags  is { Count: > 0 };

        if ((hasRungs || hasTags) && !string.IsNullOrWhiteSpace(l5xFragment))
            return new ProposeRungEditError(
                "Ambiguous input: 'rungs'/'tags' and 'l5xFragment' are mutually exclusive. " +
                "Pass either the typed rungs/tags or a raw l5xFragment, not both. " +
                "No import was attempted.",
                Hint: "Prefer rungs/tags; use l5xFragment only for the advanced export round trip." +
                      TypedInputSkillPointer,
                Kind: "AmbiguousInput");

        var normalized = new List<RungInput>();
        if (hasRungs)
        {
            for (int i = 0; i < rungs!.Count; i++)
            {
                var text = rungs[i]?.Text?.Trim();
                if (string.IsNullOrEmpty(text))
                    return new ProposeRungEditError(
                        $"Invalid rung: rungs[{i}].text must not be empty." + TypedInputSkillPointer,
                        Hint: "The text field takes rung text such as XIC(Start)OTE(Lamp);" +
                              TypedInputSkillPointer,
                        Kind: "InvalidRungText");

                if (text.Contains("<![CDATA[", StringComparison.Ordinal) ||
                    text.Contains("]]>", StringComparison.Ordinal) ||
                    text.Contains("<Rung", StringComparison.OrdinalIgnoreCase))
                    return new ProposeRungEditError(
                        $"Invalid rung: rungs[{i}].text takes rung text (for example " +
                        "XIC(Start)OTE(Lamp);), not XML. Remove any CDATA or <Rung> markup; " +
                        "the server builds the L5X." + TypedInputSkillPointer,
                        Hint: "Pass only the instruction text of the rung.",
                        Kind: "InvalidRungText");

                if (!text.EndsWith(';'))
                    text += ";";
                normalized.Add(new RungInput(text, rungs[i]!.Comment));
            }
        }

        if (hasTags)
        {
            for (int i = 0; i < tags!.Count; i++)
            {
                var name = tags[i]?.Name;
                if (name is null || !TypedTagNameRegex.IsMatch(name) ||
                    name.Contains("__", StringComparison.Ordinal))
                    return new ProposeRungEditError(
                        $"Invalid tag: tags[{i}].name '{name}' is not a valid tag name. " +
                        "It must match ^[A-Za-z_][A-Za-z0-9_]{0,39}$ and must not contain '__'.",
                        Hint: "Tag names start with a letter or underscore, use letters, digits " +
                              "and underscores only, and are at most 40 characters.",
                        Kind: "InvalidTag");

                if (string.IsNullOrWhiteSpace(tags[i].DataType))
                    return new ProposeRungEditError(
                        $"Invalid tag: tags[{i}].dataType must not be empty (tag '{name}').",
                        Hint: "Give the tag's data type, for example BOOL, DINT or TIMER.",
                        Kind: "InvalidTag");
            }
        }

        normalizedRungs = normalized;
        return null;
    }
    /// <summary><c>stagedIn</c> value of a workstation stage: the server's in-memory project image.</summary>
    internal const string StagedInSessionImage = "session-image";

    /// <summary><c>buildVerify</c> when the build ran clean.</summary>
    internal const string BuildVerifyPassed = "passed";

    /// <summary>
    /// <c>postImportBuildCheck</c> when verifyOnStage's offline replay passed and the online import did not change
    /// the fragment text, so the post-import build-check copy was not made.
    /// </summary>
    internal const string PostImportBuildCheckSkippedReplayPassed = "skipped-replay-passed";

    /// <summary>
    /// Whether the text the online import used is still exactly the text a passing verifyOnStage replay verified.
    /// </summary>
    /// <param name="importFile">The file the online import read.</param>
    /// <param name="fragmentNow">The import fragment as it is after the import.</param>
    /// <param name="replayed">The import fragment the replay verified.</param>
    /// <returns><c>true</c> only when the variable and the file both equal the replayed text.</returns>
    private static async Task<bool> ImportTextUnchangedAsync(string importFile, string fragmentNow, string replayed)
    {
        if (!string.Equals(fragmentNow, replayed, StringComparison.Ordinal)) return false;
        try
        {
            return string.Equals(
                await File.ReadAllTextAsync(importFile).ConfigureAwait(false), replayed, StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// <c>buildVerify</c> when the build passed but the rungs staged in the session image were not checked by Logix.
    /// </summary>
    internal const string BuildVerifyPassedExcludingStaged = "passed-excluding-staged";

    /// <summary>The note returned with <see cref="BuildVerifyPassedExcludingStaged"/>.</summary>
    internal const string PassedExcludingStagedNote =
        "buildVerify is passed-excluding-staged: the build passed, but Logix does not check rungs staged in the " +
        "session image, so the staged rungs are not covered by it. They are checked when published to the controller.";

    /// <summary>
    /// <c>verifiedNote</c> of a workstation stage that was not replayed offline. Logix does not check an
    /// online-staged rung, so the stage is never reported as verified.
    /// </summary>
    internal const string StagedNotVerifiedNote =
        "Online staging is not checked by Logix; the rung is checked when it is published to the controller.";

    /// <summary>
    /// Proposes rung edits to the open Logix Designer project by writing
    /// <paramref name="l5xFragment"/> to a temporary file and calling
    /// <see cref="ILogixProject.PartialImportRungsFromXmlFileAsync"/> with
    /// <see cref="PartialImportOption.LeaveEdits"/>.
    ///
    /// <para>
    /// The session must already be in the <c>Open</c> state — this method does
    /// not open or close it.  The temporary file is created internally and is
    /// always deleted before the method returns, whether the SDK call succeeds
    /// or fails.
    /// </para>
    /// </summary>
    /// <param name="routineXPath">
    /// XPath string identifying the target routine, e.g.
    /// <c>Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']</c>.
    /// Must not be <c>null</c> or whitespace.
    /// </param>
    /// <param name="insertPosition">
    /// Zero-based rung position at which the imported rungs are inserted.
    /// </param>
    /// <param name="replaceCount">
    /// Number of existing rungs to replace starting at
    /// <paramref name="insertPosition"/>.  Pass <c>0</c> to insert without
    /// replacing any rungs.
    /// </param>
    /// <param name="l5xFragment">
    /// Inline L5X XML text containing the rungs to import.  This text is written
    /// verbatim to a temporary file; the caller is responsible for ensuring it is
    /// valid L5X.
    /// <para>
    /// May be <c>null</c>, empty, or whitespace-only ONLY when <paramref name="target"/>
    /// is <c>"controller"</c>: a minimal envelope with an
    /// empty <c>&lt;RLLContent/&gt;</c> is synthesised for the routine identified by
    /// <paramref name="routineXPath"/>, so the publish sends the staged buffer only
    /// (previously staged rungs plus any buffered tags) without merging in any new
    /// rungs. For any other target, an empty/whitespace fragment is rejected.
    /// </para>
    /// </param>
    /// <param name="session">
    /// Open <see cref="LogixProjectSession"/> to use for the import.  Must
    /// already be in the <c>Open</c> state.  This method does not open or close
    /// the session.
    /// </param>
    /// <param name="policy">
    /// Optional <see cref="WriteGatePolicy"/> derived from the loaded Logix project.
    /// When non-<c>null</c>, the proposed fragment is parsed for write operands via
    /// <see cref="WriteClassifier.ScanFragment"/> and every operand is evaluated
    /// against the policy before any file I/O or SDK call is attempted.  If any
    /// operand is not in the policy's grant list, a <see cref="ProposeRungEditError"/>
    /// is returned immediately with the same provenance string that
    /// <see cref="GatedTagService"/> produces, and the SDK is never called.
    /// When <c>null</c>, no gate check is performed.
    /// </param>
    /// <param name="target">
    /// Controls where the rung edit lands after import.
    /// <list type="bullet">
    ///   <item><term><c>null</c> (default)</term>
    ///     <description>Identical to <c>"workstation"</c> — preserves pre-PRD behaviour.</description></item>
    ///   <item><term><c>"workstation"</c></term>
    ///     <description>
    ///     Calls <c>PartialImportRungsFromXmlFileAsync</c> with <c>LeaveEdits</c>.
    ///     Rungs are held in the server's in-memory project image; tag declarations
    ///     are stripped from the fragment before the SDK call and held in the
    ///     session's staging buffer.  <b>Neither reaches the controller or any
    ///     other session.</b>
    ///     </description></item>
    ///   <item><term><c>"live"</c></term>
    ///     <description>
    ///     an earlier change. Identical fragment handling to <c>"controller"</c> (tag merge,
    ///     context tags, <c>tagsCreated</c>, write lock) but imported with
    ///     <c>FinalizeEdits</c>, so the edit is published and finalized in one call
    ///     and executes immediately. Requires a clean program (nothing pending).
    ///     </description></item>
    ///   <item><term><c>"controller"</c></term>
    ///     <description>
    ///     Calls <c>PartialImportRungsFromXmlFileAsync</c> with <c>AcceptEdits</c>.
    ///     The edit is sent to the physical controller and is
    ///     <b>visible to any Logix Designer session that is online with the controller</b>.
    ///     </description></item>
    /// </list>
    /// Any other value is rejected with a <see cref="ProposeRungEditError"/>.
    /// </param>
    /// <param name="preExistingTagNames">
    /// Optional census of tag names the controller already had before this publish
    /// (e.g. from <c>logix_find_tags</c> / the server's already-uploaded project
    /// state), compared case-insensitively. On the controller (publish) path, any
    /// name in the fragment's <c>&lt;Tags&gt;</c> declarations that also appears in
    /// this census is a pre-existing tag merely re-declared for context (e.g. by
    /// an export-shaped fragment) — it is excluded from <see cref="ProposeRungEditResult.TagsCreated"/>
    /// so the self-grant write guard (<c>DenyReason.CreatedByThisSession</c>) is
    /// never applied to it. When <c>null</c> or empty, every declared name is
    /// treated as newly created — this preserves behaviour.
    /// Ignored on the workstation path.
    /// </param>
    /// <param name="preExistingTagAccess">
    /// Optional <c>ExternalAccess</c> value per pre-existing tag (case-insensitive name
    /// to value), taken from the same census export as <paramref name="preExistingTagNames"/>.
    /// A caller-fragment declaration that names one of these tags with a different
    /// <c>ExternalAccess</c> is refused with kind <c>ExternalAccessChangeRefused</c> before
    /// any import, on every target. A value recorded in
    /// <see cref="LogixProjectSession.PreStageTagAccess"/> takes precedence.
    /// </param>
    /// <param name="presetReader">
    /// Optional tag service (the SDK route) used after a <c>controller</c> or <c>live</c> publish to read
    /// <c>&lt;tag&gt;.PRE</c> for each literal preset in the published rungs. <c>null</c> skips
    /// the check.
    /// </param>
    /// <param name="verifyOnStage">
    /// Opt-in, <c>target="workstation"</c> only; default <c>false</c>. Before the fragment is imported into the
    /// session image, it is replayed into an offline copy of the session project and verified (a save-as of the
    /// project, an offline import, a build). The result then reports <c>verified: true</c>, or <c>verified: false</c>
    /// with the reject reasons (or why the replay was unavailable). It adds the time of a project save-as, an
    /// offline open, an import and a build (typically several seconds to tens of seconds on a large project;
    /// bounded at 60 s) to each stage call. Off, the stage reports <c>verified: false</c> with
    /// <see cref="StagedNotVerifiedNote"/> and costs nothing extra. It never changes the staged result's
    /// success: a failed verify is reported, not enforced.
    /// </param>
    /// <param name="ct">Cancellation token forwarded to all async operations.</param>
    /// <returns>
    /// A <see cref="ProposeRungEditResult"/> on success, or a
    /// <see cref="ProposeRungEditError"/> on any validation, gate, or SDK failure.
    /// Never throws (except <see cref="OperationCanceledException"/> for
    /// cancellation).
    /// </returns>
    public static async System.Threading.Tasks.Task<object> ExecuteAsync(
        string               routineXPath,
        uint                 insertPosition,
        uint                 replaceCount,
        string?              l5xFragment,
        LogixProjectSession  session,
        WriteGatePolicy?     policy = null,
        string?              target = null,
        IReadOnlyCollection<string>? preExistingTagNames = null,
        CancellationToken    ct = default,
        bool                 confirmedByUser = false,
        IReadOnlyList<string>? expectedRungText = null,
        IReadOnlyList<RungInput>? rungs = null,
        IReadOnlyList<TagInput>?  tags  = null,
        IReadOnlyDictionary<string, string>? preExistingTagAccess = null,
        ITagService?         presetReader = null,
        bool                 verifyOnStage = false)
    {
        // Exactly one rung_edit audit line per call. The core logs its own outcome
        // (proposed / publish-mismatch / nothing-staged / error / build-failed); anything that returns
        // without having logged one was refused before the import and is logged here as "refused" + kind.
        var isLive = string.Equals(target, "live", StringComparison.OrdinalIgnoreCase);
        var destination = isLive ? "live"
            : string.Equals(target, "controller", StringComparison.OrdinalIgnoreCase) ? "controller"
            : "workstation";
        var audit = new RungEditAuditScope(routineXPath ?? string.Empty, insertPosition, destination);
        // A controller-bound call is a publish; logix_clear_staged refuses while one runs.
        bool publishing = destination != "workstation";
        if (publishing) session.BeginPublish();
        object result;
        try
        {
            result = await ExecuteCoreAsync(
                routineXPath!, insertPosition, replaceCount, l5xFragment, session, audit, policy, target,
                preExistingTagNames, ct, confirmedByUser, expectedRungText, rungs, tags, preExistingTagAccess,
                presetReader, verifyOnStage).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            audit.LogUnlessWritten("cancelled", null);
            throw;
        }
        catch (Exception)
        {
            audit.LogUnlessWritten("error", null);
            throw;
        }
        finally
        {
            if (publishing) session.EndPublish();
        }

        if (result is ProposeRungEditError refusal)
            audit.LogUnlessWritten("refused", refusal.Kind);
        else
            audit.LogUnlessWritten("error", null);
        return result;
    }

    /// <summary>Kind of the typed error returned when the post-import state check could not confirm Online or Connected.</summary>
    public const string PostImportCheckInconclusiveKind = "PostImportCheckInconclusive";

    private static async System.Threading.Tasks.Task<object> ExecuteCoreAsync(
        string               routineXPath,
        uint                 insertPosition,
        uint                 replaceCount,
        string?              l5xFragment,
        LogixProjectSession  session,
        RungEditAuditScope   audit,
        WriteGatePolicy?     policy,
        string?              target,
        IReadOnlyCollection<string>? preExistingTagNames,
        CancellationToken    ct,
        bool                 confirmedByUser,
        IReadOnlyList<string>? expectedRungText,
        IReadOnlyList<RungInput>? rungs,
        IReadOnlyList<TagInput>?  tags,
        IReadOnlyDictionary<string, string>? preExistingTagAccess,
        ITagService?         presetReader,
        bool                 verifyOnStage)
    {
        // verifyOnStage phase timings (workstation staging with the offline replay asked for). The clock starts here, so
        // the work before the replay (reading and merging the staged session image) is the named staged-merge phase
        // and the phases account for the whole call (G34).
        var stagePhases = verifyOnStage
            && !string.Equals(target, "live", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(target, "controller", StringComparison.OrdinalIgnoreCase)
            ? new StagePhaseTimer(session.StageClock) : null;
        stagePhases?.Enter(StagePhases.StagedMerge);

        // Dead-session short-circuit: a faulted SDK server is never called again.
        if (session.IsFaulted)
            return ServerFaulted(session.FaultedError());

        // A server fault from here on names the operation that was running (stage into the image, or publish to the controller).
        using var faultOperation = session.BeginOperation(
            string.Equals(target, "controller", StringComparison.OrdinalIgnoreCase)
            || string.Equals(target, "live", StringComparison.OrdinalIgnoreCase)
                ? RockwellErrorHints.OperationPublish : RockwellErrorHints.OperationStage);

        // ── Online and correlated session guard ───────────────────────────────
        // A pending controller edit requires the session to be both online
        // (connected to a live controller) and correlated (project images are
        // aligned).  This guard fires before any import logic so that neither
        // a stale, disconnected, nor uncorrelated session can produce a
        // status: 'proposed' response.

        if (!session.IsOnline)
            return new ProposeRungEditError(
                "Session is not online. A pending controller edit requires an " +
                "online correlated session. Ensure the session has been connected " +
                "to a live controller (sdkMode=\"online\") before proposing rung edits.",
                Kind: ErrorKinds.SessionNotOnline);

        if (!session.IsCorrelated)
            return new ProposeRungEditError(
                "Session is not correlated. A pending controller edit requires an " +
                "online correlated session. Ensure the controller and project images " +
                "are aligned before proposing rung edits.");

        // ── Unverified clear ─────────────────────────
        // A logix_clear_staged that could not show the session image matches the controller leaves staged rungs
        // that the server no longer tracks. A stage or publish built on that image could report success for the
        // wrong content, so every target is refused for the affected program until a clear verifies it.
        if (!string.IsNullOrWhiteSpace(routineXPath)
            && PendingEditsHandler.TryParseRoutineXPath(routineXPath, out var clearProgram, out _)
            && session.UnverifiedClearsForProgram(clearProgram) is { Count: > 0 } unverifiedClears)
            return new ProposeRungEditError(
                $"Refused: the last logix_clear_staged did not verify that this session's image of program '{clearProgram}' " +
                $"matches the controller (routine(s): {string.Join(", ", unverifiedClears.Select(x => $"'{x}'"))}). " +
                "Staged rungs may still be in the session image, so a stage or publish now could report success built " +
                "on a stale image. No import was attempted.",
                Hint: "Run logix_clear_staged again; it re-checks the image and lifts this refusal once it verifies " +
                      "imageRestored=true. If it keeps failing, reconnect with logix_connect to discard the session image.",
                Kind: "ClearIncomplete");

        // ── Input validation ──────────────────────────────────────────────────

        if (string.IsNullOrWhiteSpace(routineXPath))
            return new ProposeRungEditError(
                "routineXPath must not be empty. " +
                "Provide the XPath string that identifies the target routine, e.g. " +
                "Controller/Programs/Program[@Name='MainProgram']" +
                "/Routines/Routine[@Name='MainRoutine'].");

        // ── Target validation ─────────────────────────────────────────────────
        // Accept null (= workstation, pre-PRD default), "workstation", "controller", or
        // "live" (an earlier change: controller fragment handling, imported with FinalizeEdits).
        // Any other value is rejected before any file I/O or SDK call. Validated
        // before the l5xFragment check below because the empty-fragment rule
        // Depends on knowing the resolved target.
        if (target is not null &&
            !string.Equals(target, "workstation", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(target, "controller",  StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(target, "live",        StringComparison.OrdinalIgnoreCase))
        {
            return new ProposeRungEditError(
                $"Invalid target '{target}'. " +
                "Accepted values: 'workstation' (rungs held in the server's in-memory image; " +
                "tag declarations held in the session staging buffer — neither reaches the " +
                "controller or any other session) or 'controller' (rungs and buffered tag " +
                "declarations sent to the physical controller, visible to any Logix Designer " +
                "session online with the controller) or 'live' (same fragment handling as " +
                "'controller', but finalized in one call so the edit executes immediately). " +
                "Omit the parameter to use the default workstation behaviour.");
        }

        // ── Resolve dispatch destination ──────────────────────────────────────
        // Compute the resolved dispatch destination now — after target validation
        // has confirmed the value is acceptable — so that every LogRungEdit call
        // below (gate denial, error, build-failed, proposed) records what path
        // was actually taken rather than echoing the raw user-supplied parameter.
        //
        // The audit trail requires that a controller-bound edit and a workstation
        // edit produce distinct rung_edit log entries.  resolvedDestination always
        // carries one of "controller" or "workstation" (never null, never the raw
        // user input) and is passed as the destination field to every LogRungEdit
        // call in this method regardless of which code path is taken.
        //
        // target="live" shares every controller-path behaviour (tag merge,
        // context tags, tagsCreated/contextTags, created-by-session write lock,
        // empty-fragment publish) — sendToController is therefore true for both. The
        // ONLY difference is the PartialImportOption chosen at the import site below.
        bool isLiveTarget = string.Equals(
            target, "live", StringComparison.OrdinalIgnoreCase);
        bool sendToController = isLiveTarget || string.Equals(
            target, "controller", StringComparison.OrdinalIgnoreCase);
        string resolvedDestination = isLiveTarget ? "live"
            : sendToController ? "controller" : "workstation";

        // ── Typed rungs/tags ───────────────────────────────────────
        // Re-validated here (LogixSdkTools validates first) so a direct caller of
        // the handler gets the same refusals. Valid typed input is turned into an
        // import fragment by RungFragmentBuilder from the real export envelope.
        if (ValidateTypedInput(l5xFragment, rungs, tags, out var typedRungs) is { } typedError)
            return typedError;
        bool hasTypedRungs = typedRungs.Count > 0;
        bool hasTypedTags  = tags is { Count: > 0 };
        bool hasTypedInput = hasTypedRungs || hasTypedTags;

        // ── Motion tags need an offline project (G27) ─────────────────────────
        // The session is online here (guard above). Logix creates AXIS_*, MOTION_GROUP and COORDINATE_SYSTEM tags
        // only offline, so refuse before any export, replay (verifyOnStage) or import: no controller round-trip.
        if (MotionTagRefusal(tags, l5xFragment) is { } motionRefusal)
            return motionRefusal;

        // ── Live rung delete ───────────────────────────────────────
        // target="live" with an EXPLICIT empty rungs list (non-null, zero entries) and
        // replaceCount > 0 means "delete replaceCount rungs at insertPosition". Omitted
        // rungs (null) is NOT a delete: the MCP binding hands this method null when the
        // parameter is absent and an empty array when the caller sent `rungs: []`, so the
        // two are distinguishable and no separate deleteRungs flag is needed. Bench:
        // PartialImportRungsFromXmlFile(xpath, start, replace, emptyRLLContent, FinalizeEdits)
        // removed the rungs in 1.3-1.4 s, later rungs renumbered (table).
        bool explicitEmptyRungs = rungs is { Count: 0 };
        if (isLiveTarget && explicitEmptyRungs && replaceCount > 0 && hasTypedTags)
            return new ProposeRungEditError(
                "A live rung delete (rungs: [] with replaceCount > 0) cannot be combined with " +
                "tags: the delete removes rungs only and the SDK cannot delete or create tags " +
                "as part of it. Send the tags in a separate call. No import was attempted.",
                Kind: "DeleteWithTags");
        bool isLiveDelete = isLiveTarget && explicitEmptyRungs && replaceCount > 0
            && !hasTypedTags && string.IsNullOrWhiteSpace(l5xFragment);

        // A delete must never create a zone: on controller/workstation an explicit empty
        // rungs list with replaceCount > 0 is refused. Only target="live" deletes (above).
        // Omitted rungs (null) with replaceCount > 0 is the empty-fragment publish
        // and is left untouched.
        if (!isLiveTarget && explicitEmptyRungs && replaceCount > 0)
            return new ProposeRungEditError(
                "An empty rungs list (rungs: []) with replaceCount > 0 is a rung delete, which " +
                "is only supported with target=\"live\". On target=\"controller\" or " +
                "\"workstation\" it would create a pending-edit zone instead of deleting. " +
                "Use target=\"live\" to delete rungs, or omit rungs/pass replaceCount 0 for an " +
                "empty-fragment publish. No import was attempted.",
                Kind: "DeleteNeedsLive");

        if (hasTypedTags && !hasTypedRungs && !sendToController)
            return new ProposeRungEditError(
                "tags without rungs is only accepted when target=\"controller\" (or \"live\"): " +
                "a tag-only publish sends the declared tags with the staged buffer. Provide at " +
                "least one rung for target=\"workstation\". No import was attempted.");

        // ── Canonical routine names ──────────────────
        // Parse the XPath once, resolve program and routine against the session's export
        // ignoring case, and use the exported casing from here on: staged-buffer keys,
        // audit, results. Typed errors (InvalidRoutineXPath, ProgramNotFound, RoutineNotFound)
        // are returned before any import, so a rejected XPath never leaves a staged rung.
        var canonical = await ResolveRoutineNamesAsync(routineXPath, session, ct).ConfigureAwait(false);
        if (canonical.Error is not null)
            return canonical.Error;
        routineXPath = canonical.XPath!;
        audit.Routine = routineXPath;

        // ── Live-target pre-import gates (gates 3–5) ───────────────────
        // Gates 1 (LGX_ENABLE_LIVE_FINALIZE) and 2 (confirmedByUser) are enforced by
        // LogixSdkTools before the session is touched. The remaining gates need the
        // session and run here, in order, BEFORE any SDK import. Every gate fails
        // closed: a state that cannot be read is a refusal, never a pass.
        if (isLiveTarget)
        {
            var liveRefusal = await CheckLiveTargetGatesAsync(
                routineXPath, insertPosition, replaceCount, session, expectedRungText, ct)
                .ConfigureAwait(false);
            if (liveRefusal is not null)
                return liveRefusal;
        }

        // ── Empty-fragment controller publish ─────────
        // A final target="controller" publish may omit l5xFragment entirely (null,
        // empty, or whitespace-only) to send ONLY the previously staged buffer —
        // whatever rungs were left pending by prior target="workstation" calls and
        // whatever tags are sitting in session.StagedTagBuffer — without merging in
        // any additional rungs of its own. This is the fix for the duplication
        // observed in Tier D run 4: resending the same fragment
        // that was already staged doubled the change set.
        //
        // When this applies, the real export envelope is obtained by calling
        // PartialExportToXmlFileAsync on the routineXPath then stripping all <Rung>
        // children from every <RLLContent> element — it carries no rung content of
        // its own, so the SDK import call below inserts nothing new; any buffered
        // tags are still merged into it further down so the publish flushes the tag
        // buffer exactly as a fragment-carrying publish would. Using the real export
        // (rather than a hand-built skeleton) guarantees the envelope carries all
        // SDK-required attributes (SchemaRevision, SoftwareRevision, TargetName,
        // TargetType, TargetSubType, ContainsContext, Use markers) exactly as the
        // SDK expects them on import. Workstation-target calls are unaffected: an
        // empty fragment there still returns the error below, since
        // target="workstation" has no controller-side buffer to flush to.
        // Typed rungs are never an "empty" publish: they carry new rungs of their own.
        // A tag-only typed publish IS one (no new rungs), with the tags added to the
        // export envelope below.
        bool emptyFragmentControllerPublish =
            sendToController && string.IsNullOrWhiteSpace(l5xFragment) && !hasTypedRungs;

        // ── Nothing staged ───────────────────────────
        // A controller publish that carries no new rungs, no typed tags, and finds no staged rungs and no
        // staged tags has nothing to send. Importing the stripped envelope would report "edit sent to
        // controller" for an edit that never happened (after a reconnect the session buffer is gone). Refuse
        // before any export or import call and audit the outcome "nothing-staged".
        if (emptyFragmentControllerPublish && !isLiveTarget && !hasTypedTags && !SessionHoldsStagedWork(session))
        {
            audit.Log(routineXPath, insertPosition, "nothing-staged", null, resolvedDestination);
            return new ProposeRungEditError(
                "Nothing is staged: the publish carries no rungs, and this session holds no staged rungs and " +
                "no staged tags, so there is nothing to send. No import was attempted. A new session " +
                "(logix_connect) starts with an empty staging buffer.",
                Hint:
                    "Stage the rungs with target=\"workstation\" in this session, then publish; or pass the rungs " +
                    "(or tags) in this call. logix_pending_edits shows what the controller itself holds.",
                Kind: "NothingStaged");
        }

        // ── Pending-edit pre-check for a controller publish (logix-mcp-h50h.6) ─
        // The SDK refuses every import into a program that holds accepted edits and answers with the raw
        // XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM. Read the program's edit state first, the way
        // logix_create_routine does, and refuse with PendingEditsBlockImport before any export or import.
        // (target="live" has its own PendingZoneExists gate above; workstation staging imports nothing
        // into the controller.)
        if (sendToController && !isLiveTarget)
        {
            var blocked = await CheckControllerPendingEditsAsync(routineXPath, session, ct)
                .ConfigureAwait(false);
            if (blocked is not null)
            {
                audit.Log(routineXPath, insertPosition, "pending-edits-block-import", null, resolvedDestination);
                // One round trip shows every problem: also run the tag and instruction pre-check (reads only).
                var preCheckError = await PreCheckBlockedPublishAsync(
                        routineXPath, insertPosition, l5xFragment, typedRungs, tags, hasTypedInput,
                        preExistingTagNames, session, ct)
                    .ConfigureAwait(false);
                return AttachPreCheck(blocked, preCheckError);
            }
        }

        string effectiveFragment;
        string? envelopeSource = null;
        IReadOnlyList<string>? externalAccessForced = null;
        if (emptyFragmentControllerPublish || hasTypedInput)
        {
            envelopeSource = "server-built";
            var exportResult = await ExportAndStripRungsAsync(routineXPath, session, ct)
                .ConfigureAwait(false);
            if (exportResult is ProposeRungEditError exportError)
                return exportError;
            effectiveFragment = (string)exportResult;

            if (hasTypedInput || isLiveDelete)
            {
                try
                {
                    // For a live delete typedRungs is empty: the builder yields the export
                    // envelope with an empty <RLLContent/>.
                    effectiveFragment = RungFragmentBuilder.BuildOnline(
                        effectiveFragment, insertPosition, typedRungs, tags);
                    externalAccessForced = ServerBuiltForcedAccess(
                        tags, session, preExistingTagNames, externalAccessForced);
                }
                catch (InvalidOperationException ex)
                {
                    return new ProposeRungEditError(
                        $"Could not build the import fragment for '{routineXPath}' from the " +
                        $"typed rungs/tags: {ex.Message}",
                        Hint:       RoundTripHint,
                        DocsAnchor: RungEditDocsAnchor);
                }
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(l5xFragment))
                return new ProposeRungEditError(
                    "l5xFragment must not be empty. " +
                    "Provide the inline L5X XML text containing the rungs to import. " +
                    "(An empty l5xFragment is only accepted when target=\"controller\" — " +
                    "it publishes the staged buffer only.)");

            // ── Fragment envelope validation ────────────────────────────────────
            // Validate the required RSLogix5000Content > … > RLLContent nesting
            // before any gate check, temp-file write, or SDK call.  The SDK's own
            // error ("allows only to import RLLContent") does not name the missing
            // element or show the expected path — this check does.
            var envelopeError = ValidateFragmentEnvelope(l5xFragment);
            if (envelopeError is not null)
                return envelopeError;

            // ── Normalize a hand-written envelope ──────
            // A fragment whose root has no SchemaRevision was not exported by the SDK
            // and would be rejected by it (XMLSrv_E_INVALID_SCHEMA_REVISION). Rungs and
            // tags are lifted out and rebuilt from a fresh export; anything else is
            // refused before the SDK is touched. An exported envelope passes unchanged.
            var rawPlan = PlanRawFragment(l5xFragment, routineXPath);
            if (rawPlan.Error is not null)
                return rawPlan.Error;

            if (rawPlan.Rebuild)
            {
                var exportResult = await ExportAndStripRungsAsync(routineXPath, session, ct)
                    .ConfigureAwait(false);
                if (exportResult is ProposeRungEditError exportError)
                    return exportError;
                try
                {
                    effectiveFragment = RungFragmentBuilder.BuildOnline(
                        (string)exportResult, insertPosition, rawPlan.Rungs, rawPlan.Tags);
                    externalAccessForced = ServerBuiltForcedAccess(
                        rawPlan.Tags, session, preExistingTagNames, externalAccessForced);
                }
                catch (InvalidOperationException ex)
                {
                    return new ProposeRungEditError(
                        $"Could not rebuild the import fragment for '{routineXPath}' from the " +
                        $"hand-written l5xFragment: {ex.Message}",
                        Hint:       RoundTripHint,
                        DocsAnchor: RungEditDocsAnchor);
                }
                envelopeSource = "server-built";
            }
            else
            {
                effectiveFragment = l5xFragment;
                envelopeSource    = "caller";

                // External Access is the engineer's write grant: a caller-supplied
                // fragment may not change an existing tag's access (refused before any
                // import, every target) nor declare a new tag with anything but Read Only.
                var accessChange = FindExternalAccessChange(
                    effectiveFragment, session.PreStageTagAccess, preExistingTagAccess);
                if (accessChange is not null)
                    return accessChange;

                var forceCensus = new HashSet<string>(
                    session.PreStageTagCensus, StringComparer.OrdinalIgnoreCase);
                if (preExistingTagNames is not null)
                    foreach (var n in preExistingTagNames)
                        forceCensus.Add(n);
                effectiveFragment = ForceNewTagsReadOnly(
                    effectiveFragment, forceCensus, out var forcedNames);
                if (forcedNames.Count > 0)
                    externalAccessForced = forcedNames;
            }
        }

        // ── Visible positions → staged-image positions (logix-mcp-qpqy.3) ─────
        // Callers count the rungs they will SEE once the edit is published: original rungs not replaced, plus
        // staged rungs. The session image still holds a replaced original (state R or D), so a visible position
        // is translated to the image index the SDK import and the staged buckets use. From here on insertPosition
        // and replaceCount are image coordinates; the caller's values are kept for messages. Only read when
        // something is staged (otherwise the image has no hidden rungs and the two coordinate systems agree).
        uint callerInsertPosition = insertPosition;
        uint callerReplaceCount   = replaceCount;
        IReadOnlyList<StagedImageRung>? stagedImage = null;
        if (!isLiveTarget && session.HasStagedRungEdit)
        {
            stagedImage = await ReadStagedImageAsync(routineXPath, session, ct).ConfigureAwait(false);
            if (stagedImage is not null && !(sendToController && emptyFragmentControllerPublish))
                (insertPosition, replaceCount) = ToImageCoordinates(stagedImage, insertPosition, replaceCount);
        }

        // ── Duplicate rung dedup against the staged buffer ─
        // When target="controller" is called WITH a non-empty l5xFragment (as opposed
        // to the empty-fragment publish-only path above), the caller may be resending
        // rungs it already staged with target="workstation" at the same insertPosition
        // — the exact scenario that doubled the change set in Tier D run 4 (see the
        // class-level doc comment on ControllerPublish_DuplicateFragmentRungs_AreSentOnce
        // in PublishStagedSetTests). Each fragment rung whose normalised <Text> content
        // is byte-equal to a rung already recorded in session.StagedRungBuffer at this
        // insertPosition is dropped from the outgoing fragment before it ever reaches
        // the SDK — it is already pending from the prior LeaveEdits import, so AcceptEdits
        // below will commit it exactly once. Every distinct rung is still published:
        // duplicates are removed, not the whole fragment. stagedRungCountAtPosition and
        // the fragment/dropped counts are surfaced on the response (requirement 2's
        // "response says how many staged and how many fragment rungs were published and
        // how many duplicates were dropped").
        int fragmentRungCount          = 0;
        int droppedDuplicateRungCount  = 0;
        int? stagedRungCountAtPosition = sendToController
            ? (session.StagedRungBuffer.TryGetValue((routineXPath, insertPosition), out var stagedTextsAtPos)
                ? stagedTextsAtPos.Count
                : 0)
            : null;

        if (sendToController && !emptyFragmentControllerPublish)
        {
            effectiveFragment = DedupeFragmentRungsAgainstStagedBuffer(
                effectiveFragment,
                routineXPath,
                insertPosition,
                session.StagedRungBuffer,
                out fragmentRungCount,
                out droppedDuplicateRungCount);
        }

        // ── Staged-position conflict ─
        // A stage or fragment publish that carries new rungs (or replaces rungs) at a position on or inside a
        // range this session staged in the SAME routine would import onto a staged (LeaveEdits) rung: the SDK
        // drops that rung and leaves its replaced original as D. A bucket at p with n rungs and replace count rc
        // occupies [p, p+n+rc). Runs on EVERY target, workstation included. Refuse before any import and name
        // both buckets and a safe position.
        if (!isLiveTarget)
        {
            int newRungCount = sendToController
                ? (emptyFragmentControllerPublish ? 0 : fragmentRungCount - droppedDuplicateRungCount)
                : RoutineRungSelector.Select(SafeXml.ParseDocument(effectiveFragment), routineXPath).Count;

            if (newRungCount > 0 || (!sendToController && replaceCount > 0))
            {
                var conflicts = FindStagedBucketConflicts(
                    session.StagedRungBuffer, session.StagedReplaceCounts, routineXPath, insertPosition,
                    replaceCount, newRungCount);
                if (conflicts.Count > 0)
                    return StagedPositionConflictError(
                        routineXPath, callerInsertPosition, newRungCount, callerReplaceCount, conflicts,
                        sendToController ? "publish" : "stage", stagedImage);
            }

            // Publish re-check: before any import, the whole buffer must be consistent.
            if (sendToController)
            {
                var bufferConflicts = FindStagedBufferConflicts(session.StagedRungBuffer, session.StagedReplaceCounts);
                if (bufferConflicts.Count > 0)
                {
                    var (first, second) = bufferConflicts[0];
                    return new ProposeRungEditError(
                        $"The publish was refused: the staged buffer holds conflicting buckets: {first.Describe()} and " +
                        $"{second.Describe()}. Importing them would delete a replaced original and lose a staged " +
                        "rung. No import was attempted.",
                        Hint:
                            "Check logix_pending_edits, then discard the staged edits with logix_clear_staged " +
                            "(it clears this session's staged rungs and tags, removes the staged rungs from the session image by rebuilding it " +
                            "from the controller, and writes nothing to the controller) " +
                            $"and re-stage them at non-overlapping positions (a safe position after the first bucket is {SafeVisiblePosition(first.End, stagedImage)}).",
                        Kind: "StagedPositionConflict",
                        StagedImage: stagedImage);
                }
            }
        }

        // ── Tag and instruction pre-check ────────────
        // Every tag operand must exist in the session (controller or program scope, aliases included) or be
        // declared by this call; every instruction must be a Logix instruction or an AOI in the project.
        // Refused before anything is staged or imported. A reference the export cannot settle is let through
        // with a warning, never refused.
        var (referenceError, referenceWarnings) = await CheckReferencesAsync(
            routineXPath, effectiveFragment, sendToController, preExistingTagNames, session, ct, verifyOnStage)
            .ConfigureAwait(false);
        if (referenceError is not null)
            return referenceError;

        // ── Effective replace count for the SDK import ────────────────────────
        // When the outgoing fragment carries no new rungs — either because the
        // caller supplied an empty l5xFragment (emptyFragmentControllerPublish)
        // or every fragment rung was dropped as a staged duplicate — the staged
        // image already contains the replacement that the caller's replaceCount
        // targeted.  Re-applying the caller's replaceCount in the AcceptEdits
        // SDK call would overwrite those staged rungs.  Use 0 instead so
        // AcceptEdits commits the staged rungs without replacing anything at the
        // target position.  The caller's original replaceCount is preserved as
        // StagedReplaceCount on the success response (with a Note) so the caller
        // can tell their value was not silently lost — it was already applied
        // during the prior target="workstation" staging call.
        //
        // This change narrowing: this override exists for the staged-buffer controller
        // publish and must NOT apply on the live path. target="live" has no
        // staged rungs (gate 3), so an empty fragment there is either an explicit delete
        // (rungs: [] — the caller's replaceCount is forwarded unchanged, per the an earlier change
        // bench table: an empty RLLContent with replace=N removes N rungs) or an ambiguous
        // call that would silently become a no-op — which is refused below instead.
        bool fragmentCarriesNoNewRungs = sendToController && !isLiveTarget &&
            (fragmentRungCount - droppedDuplicateRungCount == 0);
        if (isLiveTarget && !isLiveDelete && replaceCount > 0 &&
            fragmentRungCount - droppedDuplicateRungCount == 0)
        {
            return new ProposeRungEditError(
                $"target='live' with replaceCount={replaceCount} but no new rungs was refused: " +
                "the call would not change anything. To DELETE rungs pass an explicit empty " +
                "list (rungs: []) together with replaceCount and expectedRungText; to replace " +
                "them pass the new rungs. No import was attempted.",
                Hint:
                    "rungs omitted is not a delete; rungs: [] (explicit empty list) with " +
                    "replaceCount > 0 on target='live' deletes replaceCount rungs at insertPosition.",
                Kind: "DeleteNeedsExplicitEmptyRungs");
        }
        uint effectiveReplaceCount = fragmentCarriesNoNewRungs ? 0u : replaceCount;
        uint? ignoredCallerReplaceCount = (fragmentCarriesNoNewRungs && replaceCount != 0u)
            ? replaceCount
            : (uint?)null;

        // ── Server-chosen commit position ────────────
        // The empty "commit the staged buffer" AcceptEdits import must never land on
        // a staged (LeaveEdits) rung in the session image: the SDK drops that rung and
        // leaves its replaced original as D (bench 2026-10-06, replace at 0). The end
        // of the routine is always safe, so on this path the server picks it and
        // ignores the caller's insertPosition, reporting it as ignoredInsertPosition.
        // "end of the routine" is the CONTROLLER's rung count — the session
        // image count minus the rungs this session staged into that routine's image
        // (session.StagedRungBuffer). Counting staged rungs overshot the controller's
        // end (6 controller + 7 staged gave 13). insertPosition is reassigned here, so
        // the rung-edit audit event records the position actually imported.
        uint? ignoredCallerInsertPosition = null;
        StagedTailCommit? tailCommit = null;
        if (fragmentCarriesNoNewRungs)
        {
            var (imageReadable, imageRungCount, imageError) =
                await ReadImageRungCountAsync(routineXPath, session, ct).ConfigureAwait(false);
            if (!imageReadable)
            {
                return new ProposeRungEditError(
                    $"The staged publish was refused: the current rung count of '{routineXPath}' " +
                    "could not be read from the session image, so a safe commit position " +
                    $"cannot be chosen ({imageError}). No import was attempted.",
                    Hint: "Retry the publish; if it keeps failing, check logix_pending_edits.",
                    Kind: "CommitPositionUnreadable");
            }
            ignoredCallerInsertPosition = insertPosition;
            insertPosition = ChooseNoFragmentCommitPosition(
                imageRungCount,
                CountStagedRungsForRoutine(session.StagedRungBuffer, routineXPath));

            // This change requirement 1 (G32): when the routine's LAST rung (or its tail) is replaced, the image ends
            // `..., er(new), rN(orig)`. The controller-end position above lands on rN, and the empty import there
            // drops the new rung (the original is left deleted). Such a tail is committed by an explicit import of
            // every visible staged rung (er, e) in order over the whole tail, which also removes the hidden
            // originals (rN, dN). The import set is checked against the staged image before anything is sent.
            if (stagedImage is not null && StagedTailNeedsExplicitCommit(stagedImage))
            {
                var (elementsReadable, imageRungs) =
                    await ReadImageRungElementsAsync(routineXPath, session, ct).ConfigureAwait(false);
                if (elementsReadable)
                {
                    var stagedTextsForRoutine = session.StagedRungBuffer
                        .Where(kv => string.Equals(kv.Key.RoutineXPath, routineXPath, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(kv => kv.Value)
                        .ToList();
                    var tailError = PlanStagedTailCommit(imageRungs!, stagedTextsForRoutine, routineXPath, out tailCommit);
                    if (tailError is not null)
                        return tailError;
                    if (tailCommit is not null)
                    {
                        insertPosition         = tailCommit.Position;
                        effectiveReplaceCount  = tailCommit.ReplaceCount;
                    }
                }
            }
        }

        // ── Proposal ID ───────────────────────────────────────────────────────
        // A unique opaque token generated once per ExecuteAsync invocation.
        // Carried into every tags_staged and tags_created audit event so the
        // complete tag creation history for a session can be reconstructed from
        // audit.jsonl alone: match tags_staged lines by proposal_id to find which
        // staging call buffered each declaration, then match tags_created lines by
        // proposal_id to find the publish call that committed them to the controller.
        var proposalId = Guid.NewGuid().ToString("N");

        // ── Fragment scan (advisory) ──────────────────────────────────────────
        // When a WriteGatePolicy is provided, scan the proposed fragment for write
        // operands using WriteClassifier.  The scan result is included in the
        // response annotation so the reviewing engineer can see each write
        // destination, its gate classification, and its provenance at decision time.

        WriteClassifier.RungScan? fragmentScan = null;
        if (policy is not null)
        {
            try
            {
                fragmentScan = WriteClassifier.ScanFragment(effectiveFragment, routineXPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ProposeRungEditError(
                    $"Failed to parse l5xFragment as XML for gate check: {ex.Message}");
            }
        }

        // ── Tag stripping (workstation path) / envelope passthrough (controller path) ─
        // Workstation path: strip every <Tags> element from the fragment before the
        // SDK ever sees it.  <Tags> declarations inside a staged-but-not-sent fragment
        // would otherwise reach the SDK and attempt to touch the controller's tag
        // namespace, violating the staging-is-invisible invariant.  Stripped tag
        // declarations are held in the session's StagedTagBuffer (keyed by tag name)
        // so they are not permanently discarded — the buffer survives for the lifetime
        // of the session and can be consulted by future operations.
        //
        // Controller (publish) path: an earlier change found that the previous implementation
        // here — StripTagsFromFragment followed by controllerElem.AddFirst re-injecting
        // a bare <Tags> container — destroyed the fragment's OWN <Tags Use="Context">
        // envelope (the exporter's own container, its position before <Programs>, its
        // Use attribute, and any existing-tag declarations it carried) on every single
        // controller publish, because the merged-tag map is non-empty whenever the
        // fragment itself declares any tag at all, not merely when the staging buffer
        // has entries. The mangled container then made the SDK importer reject the
        // publish outright. The fix (requirement 1): never rewrite the caller's
        // fragment when the staging buffer is empty. It is passed to the SDK
        // byte-for-byte, with only an XML declaration prepended when the caller
        // omitted one (the 0062 fix — the SDK rejects a declaration-less fragment
        // with XMLSrv_E_ERROR_READING_XML).
        //
        // Requirement 2: when the staging buffer is non-empty, each buffered <Tag>
        // not already declared anywhere in the fragment (by name, case-insensitive)
        // is appended inside the fragment's existing <Tags Use="Context"> element —
        // or, if the fragment has none, a new one is created with Use="Context",
        // positioned after <DataTypes> and before <Programs> (the exporter's own
        // position). Fragment-declared tags always win on a name clash; nothing is
        // ever removed. See MergeBufferedTagsIntoFragment.
        string fragmentToImport = effectiveFragment;
        IReadOnlyList<PublishedTagEntry>? publishedTagEntries = null;

        // Populated on the controller (publish) path with the names of tags the
        // fragment declared via a context block that already existed in the
        // pre-publish census. These are the mirror image
        // of publishedTagEntries: declared-but-not-created, so the disclosure never
        // misrepresents a pre-existing tag as newly created while still surfacing
        // that the fragment referenced it.
        IReadOnlyList<string>? contextTagNames = null;

        // Populated on the controller (publish) path with the names of the
        // buffered tags actually merged into fragmentToImport (see
        // MergeBufferedTagsIntoFragment). Stays empty on the workstation path,
        // and empty when the staging buffer was empty. Surfaced in the SDK
        // import-rejection error messages below so a caller can tell a
        // buffer-induced failure apart from a rung-text problem — the staging
        // buffer is NOT cleared on a rejected publish, so these same tags are
        // retried on every subsequent controller publish in this session until
        // one succeeds.
        IReadOnlyList<string> mergedStagedTagNames = Array.Empty<string>();

        // Populated on every replace path (workstation, controller, live) with the routine
        // rung numbers whose existing <Comment> was carried into the imported replacement
        // rung because the fragment rung had none. An explicit comment (including "") wins. Null when nothing was preserved.
        IReadOnlyList<int>? commentsPreserved = null;

        // The replaced rungs (index into the routine) whose comment this call sets explicitly
        // (typed comment, "" included, or a <Comment> in the fragment) to something other than the original. Computed
        // once from the fragment about to be imported; empty for a live call (finalized, nothing to cancel) and for an
        // append. commentUndoCaptured is set once the originals were read from the pre-import export.
        List<int> explicitCommentRungs = [];
        List<(int Index, string Text, string Comment)> commentOriginals = [];
        bool commentUndoCaptured = false;
        bool commentExplicit = false;

        // Populated on the live path with the routine's rungs as exported immediately
        // before the FinalizeEdits import (null when that export was unreadable). Reported
        // beside the after-state when the post-import verification cannot confirm.
        IReadOnlyList<PendingEditRungEntry>? liveBeforeRungs = null;

        // Populated on the live-delete path with the rungs about to be
        // removed (number, normalized text, comment), read from the same pre-import export.
        List<DeletedRungEntry>? deletedRungs = null;

        // Populated on the live path (undo block) with the rungs this call is about to
        // replace or remove — text and comment from the same pre-import export. Empty
        // string comment = the rung had none.
        List<UndoRungEntry>? liveUndoPreImage = null;

        // Populated on the workstation path with the number of <Rung> elements in
        // the fragment about to be staged (post tag-strip). Added to
        // session.StagedRungCount once the import call below succeeds, so a
        // failed staging attempt never inflates the count. See
        // LogixProjectSession.StagedRungCount for the field's lifecycle.
        int stagedRungCountThisCall = 0;

        // Populated on the workstation path with the normalised <Text> content of
        // each <Rung> element about to be staged (post tag-strip), in document
        // order. Appended to session.StagedRungBuffer[insertPosition] once the
        // import call below succeeds, so a later target="controller" publish that
        // resends the same rung(s) at this insertPosition can recognise and drop
        // the duplicates. A failed
        // staging attempt never populates the buffer.
        List<string> stagedRungTextsThisCall = new();

        // Populated on the workstation path when replaceCount > 0 with the
        // normalised <Text> content of each rung that will be displaced by this
        // stage (the "original" rungs at insertPosition..insertPosition+replaceCount-1
        // before the LeaveEdits import overwrites them). Added to
        // session.StagedReplacedRungTexts once the import call below succeeds, so
        // PendingEditsHandler can segregate them into staged.replacedRungs rather
        // than pendingRungs and report state="none" when no genuine controller-side
        // pending edits remain. A failed staging attempt never populates the set.
        List<string> replacedRungTextsThisCall = [];

        // Replacement rungs of this stage whose text equals the original they replace (zero-based rung index,
        // text). Logix keeps those as N, so the publish census must not expect them pending (logix-mcp-am0x.4).
        List<(int Index, string Text)> unchangedReplacementsThisCall = [];

        // Tag declarations this call put into the staged tag buffer, with the entry each replaced (null when new).
        // A verifyOnStage failure restores them, so a rejected stage leaves the session image as it found it.
        List<(string Name, XElement? Prior)> tagBufferUndo = [];

        if (!sendToController)
        {
            // Workstation path: strip and buffer.
            var extractedTags = StripTagsFromFragment(effectiveFragment, out var stripped);
            if (extractedTags.Count > 0)
            {
                // Capture the pre-stage tag census on the first staging call that
                // strips tag declarations. The census records controller tag names
                // that already existed before any staging in this session, so that
                // logix_pending_edits can restrict staged.tagNames to only new tags
                // (names absent from the census). Only populated on the first call
                // so that a pre-existing tag does not migrate to "new" across calls.
                if (session.PreStageTagCensus.Count == 0 && preExistingTagNames is { Count: > 0 })
                {
                    foreach (var censusName in preExistingTagNames)
                        session.PreStageTagCensus.Add(censusName);
                    if (preExistingTagAccess is not null)
                        foreach (var (accessName, accessValue) in preExistingTagAccess)
                            session.PreStageTagAccess[accessName] = accessValue;
                }

                fragmentToImport = stripped;
                var newlyStagedNames = new List<string>();

                // Only declarations for tags that did NOT exist before staging are
                // "staged". A declaration whose name is in the pre-stage census is a
                // context declaration (the exporter's own <Tags Use="Context"> envelope,
                // or an export-shaped caller fragment) of a tag already on the controller:
                // it is dropped with the stripped container, never buffered, and never
                // reported as stagedTags (item 6). When no census is available
                // nothing can be told apart, so every declaration is kept (old behaviour).
                var stagingCensus = new HashSet<string>(
                    session.PreStageTagCensus, StringComparer.OrdinalIgnoreCase);
                if (preExistingTagNames is not null)
                    foreach (var n in preExistingTagNames)
                        stagingCensus.Add(n);

                foreach (var tag in extractedTags)
                {
                    var tagName = (string?)tag.Attribute("Name");
                    if (!string.IsNullOrWhiteSpace(tagName) && !stagingCensus.Contains(tagName))
                    {
                        tagBufferUndo.Add((tagName, session.StagedTagBuffer.GetValueOrDefault(tagName)));
                        session.StagedTagBuffer[tagName] = tag;
                        newlyStagedNames.Add(tagName);
                    }
                }
                // Emit one tags_staged audit event per declaration so the complete
                // staging history is independently queryable in audit.jsonl.
                if (newlyStagedNames.Count > 0)
                    AuditLogger.LogTagsStaged(proposalId, newlyStagedNames);
            }

            // Record the routine as the image shows it BEFORE the first stage into it, so a later
            // logix_clear_staged can compare its restored image with this pre-stage state. Only while the image still holds
            // no staged import for the routine; best-effort (a failed read leaves the clear with its narrower check).
            if (session.GetRoutineBaseline(routineXPath) is null
                && !session.StagedImports.Any(i => string.Equals(i.RoutineXPath, routineXPath, StringComparison.OrdinalIgnoreCase)))
            {
                var (baselineReadable, baselineRungs, _) =
                    await ClearStagedHandler.ReadRoutineRungsAsync(routineXPath, session, ct).ConfigureAwait(false);
                if (baselineReadable)
                    session.TryRecordRoutineBaseline(routineXPath, baselineRungs);
            }

            var stagedRungElems = RoutineRungSelector.Select(
                SafeXml.ParseDocument(fragmentToImport), routineXPath);
            stagedRungCountThisCall = stagedRungElems.Count;
            stagedRungTextsThisCall = stagedRungElems
                .Select(r => NormalizeRungText(r.Element("Text")?.Value))
                .ToList();

            // When this stage replaces or deletes existing rungs, capture the
            // normalised texts of the original rungs that are about to be
            // displaced. These rungs will appear in the controller export as
            // R/D/IR-type (LeaveEdits-produced replace/delete zone markers) after
            // the import; recording their texts here lets PendingEditsHandler
            // (via session.StagedReplacedRungTexts) move them to
            // staged.replacedRungs instead of reporting them as controller-side
            // pending edits. The export is done before the LeaveEdits import so
            // the current in-memory project image still reflects the pre-stage
            // state. A failure to export or parse is treated as best-effort:
            // the import itself is unaffected, but staged.replacedRungs may be
            // incomplete for this staging call.
            if (replaceCount > 0)
            {
                var preImportExportResult = await ExportRungsHandler.ExecuteAsync(
                    routineXPath, session, ct).ConfigureAwait(false);
                if (preImportExportResult is ExportRungsResult preImportSuccess)
                {
                    try
                    {
                        var preImportDoc = SafeXml.ParseDocument(preImportSuccess.L5X);
                        var preImportRungs = RoutineRungSelector.Select(preImportDoc, routineXPath);
                        for (uint i = 0; i < replaceCount; i++)
                        {
                            var idx = (int)(insertPosition + i);
                            if (idx < preImportRungs.Count)
                            {
                                var rawText = preImportRungs[idx].Element("Text")?.Value;
                                replacedRungTextsThisCall.Add(NormalizeRungText(rawText));
                            }
                        }

                        unchangedReplacementsThisCall = FindUnchangedReplacements(
                            stagedRungTextsThisCall,
                            preImportRungs.Select(r => NormalizeRungText(r.Element("Text")?.Value)).ToList(),
                            insertPosition, replaceCount);

                        // Remember the original comment of every rung this stage gives an
                        // explicit comment, so a cancel can write it back.
                        commentExplicit = HasExplicitCommentIntent(fragmentToImport, replaceCount, typedRungs, routineXPath);
                        explicitCommentRungs = FindExplicitCommentChanges(
                            fragmentToImport, preImportRungs, insertPosition, replaceCount, typedRungs, routineXPath,
                            commentOriginals);
                        commentUndoCaptured = true;

                        // A staged replacement keeps the comment of the rung it displaces
                        // unless the fragment rung supplies its own (comment: "" clears it).
                        fragmentToImport = PreserveExistingRungComments(
                            fragmentToImport, preImportRungs, insertPosition, replaceCount,
                            out var stagedPreservedRungs, typedRungs, routineXPath);
                        if (stagedPreservedRungs.Count > 0)
                            commentsPreserved = stagedPreservedRungs;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Best-effort: if the parse fails, leave replacedRungTextsThisCall
                        // empty. The import itself is unaffected.
                        replacedRungTextsThisCall.Clear();
                        unchangedReplacementsThisCall.Clear();
                        commentsPreserved = null;
                        commentUndoCaptured = false;
                        commentOriginals.Clear();
                        if (HasExplicitCommentIntent(fragmentToImport, replaceCount, typedRungs, routineXPath))
                            explicitCommentRungs = [-1];
                    }
                }
                else if (HasExplicitCommentIntent(fragmentToImport, replaceCount, typedRungs, routineXPath))
                {
                    explicitCommentRungs = [-1];   // unreadable: the originals could not be captured
                }
            }

            if (explicitCommentRungs.Count > 0 && !commentUndoCaptured)
                return CommentNotReversibleError(routineXPath, "workstation",
                    "the original comments could not be read from the session image before the replace");
        }
        else
        {
            // Controller (publish) path: merge any buffered tag declarations into
            // the fragment's own tag envelope (requirement 2). When the buffer is
            // empty this only ensures the fragment carries an XML declaration —
            // nothing else about it is touched (requirement 1).
            fragmentToImport = MergeBufferedTagsIntoFragment(
                effectiveFragment, session.StagedTagBuffer, out mergedStagedTagNames);

            // Non-live controller publish that replaces rungs: carry the replaced rungs'
            // comments into the fragment exactly as the live path does (best-effort).
            if (!isLiveTarget && replaceCount > 0 && !fragmentCarriesNoNewRungs)
            {
                var publishExportResult = await ExportRungsHandler.ExecuteAsync(
                    routineXPath, session, ct).ConfigureAwait(false);
                if (publishExportResult is ExportRungsResult publishExport)
                {
                    try
                    {
                        var publishRungs = RoutineRungSelector.Select(
                            SafeXml.ParseDocument(publishExport.L5X), routineXPath);
                        // Nothing in this server cancels an accepted controller edit and writes the
                        // original comment back, so an explicit comment that changes a replaced rung is refused.
                        if (FindExplicitCommentChanges(
                                fragmentToImport, publishRungs, insertPosition, replaceCount, typedRungs, routineXPath, []).Count > 0)
                            return CommentNotReversibleError(routineXPath, "controller",
                                "a cancel of the pending edit does not write the original comment back");
                        fragmentToImport = PreserveExistingRungComments(
                            fragmentToImport, publishRungs, insertPosition, replaceCount,
                            out var publishPreserved, typedRungs, routineXPath);
                        if (publishPreserved.Count > 0)
                            commentsPreserved = publishPreserved;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        commentsPreserved = null;
                        if (HasExplicitCommentIntent(fragmentToImport, replaceCount, typedRungs, routineXPath))
                            return CommentNotReversibleError(routineXPath, "controller",
                                "the original comments could not be read before the replace");
                    }
                }
                else if (HasExplicitCommentIntent(fragmentToImport, replaceCount, typedRungs, routineXPath))
                {
                    return CommentNotReversibleError(routineXPath, "controller",
                        "the original comments could not be read before the replace");
                }
            }

            // Live replace: a fragment rung without its own <Comment> inherits the
            // comment of the rung it replaces (pre-import capture); a fragment rung
            // that carries a <Comment> always wins. Best-effort — an export or parse
            // failure leaves the fragment untouched.
            // The same pre-import export is kept as the "before" rung snapshot that
            // the live post-import verification reports alongside the after-state.
            if (isLiveTarget)
            {
                var preImportExportResult = await ExportRungsHandler.ExecuteAsync(
                    routineXPath, session, ct).ConfigureAwait(false);
                if (preImportExportResult is ExportRungsResult preImportSuccess)
                {
                    try
                    {
                        var preImportRungs = RoutineRungSelector.Select(
                            SafeXml.ParseDocument(preImportSuccess.L5X), routineXPath);
                        liveBeforeRungs = SnapshotRungs(preImportRungs);
                        liveUndoPreImage = [];
                        for (int i = 0; i < (int)replaceCount; i++)
                        {
                            int idx = (int)insertPosition + i;
                            if (idx >= preImportRungs.Count)
                                break;
                            liveUndoPreImage.Add(new UndoRungEntry(
                                NormalizeRungText(preImportRungs[idx].Element("Text")?.Value),
                                preImportRungs[idx].Element("Comment")?.Value ?? string.Empty));
                        }
                        if (isLiveDelete)
                        {
                            deletedRungs = [];
                            for (int i = 0; i < (int)replaceCount; i++)
                            {
                                int idx = (int)insertPosition + i;
                                if (idx >= preImportRungs.Count)
                                    break;
                                var commentText = preImportRungs[idx].Element("Comment")?.Value;
                                deletedRungs.Add(new DeletedRungEntry(
                                    idx,
                                    NormalizeRungText(preImportRungs[idx].Element("Text")?.Value),
                                    string.IsNullOrWhiteSpace(commentText) ? null : commentText));
                            }
                        }
                        if (replaceCount > 0 && !isLiveDelete)
                        {
                            fragmentToImport = PreserveExistingRungComments(
                                fragmentToImport, preImportRungs, insertPosition, replaceCount,
                                out var preservedRungs, typedRungs, routineXPath);
                            if (preservedRungs.Count > 0)
                                commentsPreserved = preservedRungs;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        commentsPreserved = null;
                        liveBeforeRungs   = null;
                        deletedRungs      = null;
                        liveUndoPreImage  = null;
                    }
                }
            }

            // A delete cannot be verified (or reported) without the rungs it removes: fail
            // closed before the import if the pre-import export was unreadable.
            if (isLiveDelete && (liveBeforeRungs is null || deletedRungs is not { Count: > 0 } ||
                                 deletedRungs.Count != (int)replaceCount))
            {
                return new ProposeRungEditError(
                    $"target='live' delete refused: the rungs to delete in '{routineXPath}' " +
                    "could not be read back from a fresh export immediately before the import, " +
                    "so the delete cannot be verified. No import was attempted.",
                    Kind: "StaleTarget");
            }

            // An undo block needs the pre-image of every rung a live replace removes: fail
            // closed before the import if it could not be read (the gate-5 export just worked,
            // so this only fires if the routine changed or became unreadable in between).
            if (isLiveTarget && !isLiveDelete && replaceCount > 0 &&
                (liveUndoPreImage is null || liveUndoPreImage.Count != (int)replaceCount))
            {
                return new ProposeRungEditError(
                    $"target='live' replace refused: the rungs to replace in '{routineXPath}' " +
                    "could not be read back from a fresh export immediately before the import, " +
                    "so the undo block cannot be built. No import was attempted.",
                    Kind: "StaleTarget");
            }

            // Disclosure covers both the caller's own fragment tag declarations and
            // any buffer-flushed tags merged in by MergeBufferedTagsIntoFragment.
            // The union ensures buffer-flushed names appear in tagsCreated (or
            // contextTags) even when the caller's fragment carries no <Tags> block.
            var fragmentTagNames = SafeXml.ParseDocument(effectiveFragment)
                .Descendants("Tags")
                .Elements("Tag")
                .Select(t => (string?)t.Attribute("Name"))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Union with buffer-flushed names so both disclosure fields reflect
            // every tag that landed on the controller (+ an earlier change).
            var allTagNames = fragmentTagNames
                .Union(mergedStagedTagNames, StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (allTagNames.Count > 0)
            {
                // Requirement: "created" means the delta against the
                // controller's pre-publish tag census, not every name the fragment
                // declares. A context declaration of a tag that already existed
                // before this publish must neither be reported as created nor
                // trigger the self-grant write lock — only names absent
                // from preExistingTagNames are newly created.
                var preExistingCensus = preExistingTagNames is { Count: > 0 }
                    ? new HashSet<string>(preExistingTagNames, StringComparer.OrdinalIgnoreCase)
                    : null;

                var newlyCreatedNames = preExistingCensus is null
                    ? allTagNames
                    : allTagNames.Where(n => !preExistingCensus.Contains(n)).ToList();

                if (newlyCreatedNames.Count > 0)
                {
                    // Tags created by this session are immediately registered under
                    // the CreatedByThisSession self-grant guard, which
                    // causes logix_write to deny writes for the lifetime of this session.
                    // writableViaLogixWrite is false for tags created Read Only — even for tags in
                    // a granted namespace (Cmd_/Sim_) — because External Access gates them; it is
                    // true only for tags the server itself had to create Read/Write (G28).
                    // G28: a tag the server itself created External Access Read/Write (externalAccessForced,
                    // e.g. HMIBC) is not Read Only, so it is reported writable.
                    var readWriteForced = ReadWriteForcedNames(externalAccessForced);
                    publishedTagEntries = newlyCreatedNames
                        .Select(name => new PublishedTagEntry(
                            TagName: name,
                            WritableViaLogixWrite: readWriteForced.Contains(name)))
                        .ToList();
                }

                // The remainder of the declared tags — those already present in the
                // pre-publish census — are surfaced separately as contextTags so the
                // disclosure stays complete without claiming they were created.
                if (preExistingCensus is not null)
                {
                    var existingDeclaredNames = allTagNames
                        .Where(preExistingCensus.Contains)
                        .ToList();

                    if (existingDeclaredNames.Count > 0)
                        contextTagNames = existingDeclaredNames;
                }
            }
        }

        // ── Snapshot staged tag names (workstation path only) ─────────────────
        // After the stripping step the buffer reflects the cumulative set of tag
        // declarations held across all prior staging calls for this session.
        // Capture the sorted key list now so it can be included in the response;
        // this lets the agent explain why a rung references a tag that does not
        // yet exist on the controller.
        IReadOnlyList<string>? stagedTagNames = !sendToController && session.StagedTagBuffer.Count > 0
            ? session.StagedTagBuffer.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList()
            : null;

        // ── Write fragment to temp file and call SDK ──────────────────────────

        // GetTempFileName() creates the file atomically.  The file is always
        // deleted in the finally block — success, failure, or cancellation.
        var tempFile = Path.GetTempFileName();

        try
        {
            if (tailCommit is not null)
                fragmentToImport = InjectRungsIntoFragment(fragmentToImport, tailCommit.NewRungs);

            await File.WriteAllTextAsync(tempFile, fragmentToImport, ct)
                      .ConfigureAwait(false);

            // ── Expected pending set + pre-publish baseline ─────────
            // Controller path only. The expected set is every rung the publish should
            // leave pending as I/IR: the staged buffer (prior workstation stages) PLUS
            // the rungs carried by this publish's own (post-dedupe) fragment. Before an earlier change
            // only the staged buffer was considered, so a direct controller
            // publish with no prior stage was never verified.
            // The baseline (pending I/IR rungs present before the import, e.g. from
            // another session) is read only when something is expected, so the
            // empty-fragment/no-stage publish keeps its single export call.
            // The census is per routine. Every routine holding staged rungs is
            // re-exported (StagedRoutineXPaths) plus the call's own routine, and each is compared
            // only against ITS expected texts: its staged rungs, plus this fragment's rungs when
            // the fragment targets it. Captured here because the staged buffer is cleared before
            // the census runs.
            Dictionary<string, List<string>>? expectedByRoutine = null;
            List<string> ownFragmentTexts = [];
            // This call's own fragment rungs with the comment each carries into the import (after comment
            // preservation), so the landed rung's comment can be reported.
            List<(string Text, string? Comment)> ownFragmentComments = [];
            // Same-text replacements per routine (logix-mcp-am0x.4): staged ones from the session, plus this call's
            // own direct replacement. Captured here because the session's record is cleared before the census runs.
            var unchangedByRoutine = new Dictionary<string, List<(int Index, string Text)>>(StringComparer.OrdinalIgnoreCase);
            var prePublishPendingByRoutine = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (sendToController)
            {
                var byRoutine = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (bufferKey, texts) in session.StagedRungBuffer)
                {
                    if (!byRoutine.TryGetValue(bufferKey.RoutineXPath, out var routineList))
                        byRoutine[bufferKey.RoutineXPath] = routineList = new List<string>();
                    routineList.AddRange(texts);
                }
                if (!emptyFragmentControllerPublish)
                {
                    if (!byRoutine.TryGetValue(routineXPath, out var ownList))
                        byRoutine[routineXPath] = ownList = new List<string>();
                    ownFragmentTexts = RoutineRungSelector.Select(
                            SafeXml.ParseDocument(effectiveFragment), routineXPath)
                        .Select(r => NormalizeRungText(r.Element("Text")?.Value))
                        .ToList();
                    ownList.AddRange(ownFragmentTexts);
                    ownFragmentComments = RoutineRungSelector.Select(
                            SafeXml.ParseDocument(fragmentToImport), routineXPath)
                        .Select(r => (NormalizeRungText(r.Element("Text")?.Value),
                            r.Element("Comment") is { } c && !string.IsNullOrWhiteSpace(c.Value) ? c.Value : null))
                        .ToList();

                    // A direct replacement: compare each replacement rung with the original it displaces, read
                    // before the import (best-effort; an unreadable export just means none are recognised).
                    if (effectiveReplaceCount > 0 && ownFragmentTexts.Count > 0)
                    {
                        var originals = await ReadAllRungTextsAsync(routineXPath, session, ct).ConfigureAwait(false);
                        if (originals is not null)
                        {
                            var direct = FindUnchangedReplacements(ownFragmentTexts, originals, insertPosition, effectiveReplaceCount);
                            if (direct.Count > 0)
                                unchangedByRoutine[routineXPath] = direct;
                        }
                    }
                }
                foreach (var ((unchangedRoutine, unchangedIndex), unchangedText) in session.StagedUnchangedReplacements)
                {
                    if (!unchangedByRoutine.TryGetValue(unchangedRoutine, out var unchangedList))
                        unchangedByRoutine[unchangedRoutine] = unchangedList = [];
                    unchangedList.Add((unchangedIndex, unchangedText));
                }
                if (byRoutine.Values.Sum(l => l.Count) > 0)
                {
                    // The call's own routine is always part of the census.
                    if (!byRoutine.ContainsKey(routineXPath))
                        byRoutine[routineXPath] = new List<string>();
                    expectedByRoutine = byRoutine;
                    foreach (var censusRoutine in byRoutine.Keys)
                    {
                        var (baselineReadable, baselineTexts) =
                            await ReadPendingRungTextsAsync(censusRoutine, session, ct)
                                .ConfigureAwait(false);
                        if (baselineReadable)
                            prePublishPendingByRoutine[censusRoutine] = baselineTexts;
                    }
                }
            }

            // Declared outside the inner try so every catch block below (SDK exception
            // handling) can read whatever the operation-event hook captured before the
            // failure, even though the capture itself is populated inside the try.
            var importLogCapture = new ImportOperationEventCapture();
            var importRetryState = new ControllerLockRetryState();
            int logMark = 0, statusMark = 0;   // import-log entries captured before the last try

            // When the online importer rejects the fragment and names no cause, the classifier
            // replays this same fragment offline (a saved copy, never the live project) and verifies it.
            Func<CancellationToken, Task<ImportRejectReplayOutcome>> replayImportReject = replayCt =>
                ImportRejectReplay.RunAsync(
                    session, routineXPath, insertPosition, effectiveReplaceCount, tempFile, replayCt);

            // verifyOnStage (opt-in, workstation only): replay the fragment into an offline copy of the project and
            // verify it BEFORE the online import, so the copy does not already carry the staged rung. Never changes
            // anything online; a failed or unavailable replay is reported, not enforced.
            bool stagingToWorkstation = !sendToController;
            string? replayPassedImportText = null;   // the import text the passing replay verified
            bool? stageVerified = null;
            string? stageVerifiedNote = null;
            if (!stagingToWorkstation && verifyOnStage)
                stageVerifiedNote = VerifyOnStageIgnoredOnControllerNote;
            if (stagingToWorkstation)
            {
                stageVerified     = false;
                stageVerifiedNote = StagedNotVerifiedNote;
                if (verifyOnStage)
                {
                    // The online import file has its tag declarations stripped (they are buffered, not imported).
                    // The offline copy has no such tags, so a rung using one would fail TAGNOTFOUND. Replay a
                    // separate file whose tag envelope carries every staged declaration (earlier calls and this
                    // one) ahead of the programs/rungs; the online import keeps using tempFile unchanged.
                    stagePhases?.Enter(StagePhases.ImageExport);
                    var replayFile = tempFile;
                    string? replayTagFile = null;
                    if (session.StagedTagBuffer.Count > 0)
                    {
                        replayTagFile = Path.GetTempFileName();
                        await File.WriteAllTextAsync(
                            replayTagFile,
                            BuildStageVerifyReplayFragment(fragmentToImport, session.StagedTagBuffer),
                            ct).ConfigureAwait(false);
                        replayFile = replayTagFile;
                    }
                    ImportRejectReplayOutcome verifyOutcome;
                    try
                    {
                        var stageReplay = session.StageVerifyReplay is { } fake
                            ? (Func<CancellationToken, Task<ImportRejectReplayOutcome>>)(replayCt =>
                                fake(routineXPath, insertPosition, effectiveReplaceCount, replayFile, replayCt))
                            : replayCt => ImportRejectReplay.RunAsync(
                                session, routineXPath, insertPosition, effectiveReplaceCount, replayFile, replayCt,
                                stagePhases);
                        // The real replay marks save-as and replay-copy itself; a test replay is timed as one copy.
                        if (session.StageVerifyReplay is not null) stagePhases?.Enter(StagePhases.ReplayCopy);
                        verifyOutcome = await ImportRejectReplay.RunBoundedAsync(
                            stageReplay, session.ImportRejectReplayTimeout, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (replayTagFile is not null)
                            try { File.Delete(replayTagFile); } catch { /* best-effort */ }
                    }
                    if (verifyOutcome.UnavailableReason is { } unavailableWhy)
                        stageVerifiedNote = StagedNotVerifiedNote +
                            $" verifyOnStage could not run the offline replay: {unavailableWhy}";
                    else if (verifyOutcome.Reasons.Count > 0)
                    {
                        // Build-phase reasons often carry text only. When one names no rung and the batch has several,
                        // replay one rung at a time (capped, see ImportRejectReplay.MaxPerRungReplay) to find which.
                        if (verifyOutcome.Reasons.Any(r => r.RungIndex is null))
                        {
                            var perRung = await ImportRejectReplay.LocateFailingRungsAsync(
                                fragmentToImport,
                                async (rungIndex, rungXml, rungCt) =>
                                {
                                    var rungFile = Path.GetTempFileName();
                                    try
                                    {
                                        await File.WriteAllTextAsync(rungFile,
                                            session.StagedTagBuffer.Count > 0
                                                ? BuildStageVerifyReplayFragment(rungXml, session.StagedTagBuffer)
                                                : rungXml, rungCt).ConfigureAwait(false);
                                        var pos   = insertPosition + (uint)rungIndex;
                                        var count = (uint)rungIndex < effectiveReplaceCount ? 1u : 0u;
                                        return await ImportRejectReplay.RunBoundedAsync(
                                            session.StageVerifyReplay is { } fake2
                                                ? (Func<CancellationToken, Task<ImportRejectReplayOutcome>>)(c =>
                                                    fake2(routineXPath, pos, count, rungFile, c))
                                                : c => ImportRejectReplay.RunAsync(
                                                    session, routineXPath, pos, count, rungFile, c),
                                            session.ImportRejectReplayTimeout, rungCt).ConfigureAwait(false);
                                    }
                                    finally { try { File.Delete(rungFile); } catch { /* best-effort */ } }
                                },
                                ct).ConfigureAwait(false);
                            verifyOutcome = verifyOutcome with
                            {
                                Reasons = ImportRejectReplay.ApplyPerRungReasons(verifyOutcome.Reasons, perRung),
                            };
                        }
                        // Meaningful reasons first; a reason that is only an unformatted template is dropped (G27).
                        verifyOutcome = verifyOutcome with { Reasons = OrderRejectReasons(verifyOutcome.Reasons) };
                        var verifyDeclaredTypes = DeclaredTagTypes(session.StagedTagBuffer);
                        // The rung failed verify: it is NOT staged. Undo the tag declarations buffered above (the rung
                        // buffers are only filled after this point), so the session image is as it was before the call.
                        for (var i = tagBufferUndo.Count - 1; i >= 0; i--)
                        {
                            var (undoName, prior) = tagBufferUndo[i];
                            if (prior is null) session.StagedTagBuffer.Remove(undoName);
                            else session.StagedTagBuffer[undoName] = prior;
                        }
                        // The offline replay can name XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM: the program holds
                        // accepted edits, which the rung cannot fix. Report that, not "fix the rung".
                        if (verifyOutcome.Reasons.Any(IsPieEditsExistReason))
                        {
                            audit.Log(routineXPath, insertPosition, "error", null,
                                destination:     resolvedDestination,
                                stagedIn:        StagedInSessionImage,
                                verified:        false,
                                designerVersion: session.DesignerVersion?.ToString(),
                                kind:            RockwellErrorHints.PendingEditsBlockImportKind);
                            return PendingEditsBlockImportFromSdk(
                                    string.Join("; ", verifyOutcome.Reasons.Select(r => r.Text)),
                                    [], string.Empty, string.Empty)
                                with { VerifyRejectReasons = verifyOutcome.Reasons, PhaseTimings = stagePhases?.Snapshot(includeSubPhases: true) };
                        }
                        if (ReadOnlyAccessRejection(verifyOutcome.Reasons, [], verifyDeclaredTypes) is { } verifyAccessError)
                        {
                            audit.Log(routineXPath, insertPosition, "error", null,
                                destination:     resolvedDestination,
                                stagedIn:        StagedInSessionImage,
                                verified:        false,
                                designerVersion: session.DesignerVersion?.ToString(),
                                kind:            ReadOnlyAccessRejectedKind);
                            return verifyAccessError with
                            {
                                VerifyRejectReasons = verifyOutcome.Reasons,
                                RejectReasonSource  = ImportRejectReplay.SourceOfflineReplay,
                                PhaseTimings        = stagePhases?.Snapshot(includeSubPhases: true),
                            };
                        }
                        audit.Log(routineXPath, insertPosition, "error", null,
                            destination:     resolvedDestination,
                            stagedIn:        StagedInSessionImage,
                            verified:        false,
                            designerVersion: session.DesignerVersion?.ToString(),
                            kind:            "StageVerifyFailed");
                        return new ProposeRungEditError(
                            $"StageVerifyFailed: verifyOnStage replayed the fragment into an offline copy of the project and " +
                            $"verify rejected it ({verifyOutcome.Reasons.Count} reason(s)). " +
                            $"{StageVerifyFailedRungPhrase(verifyOutcome.Reasons)} of the fragment for '{routineXPath}' failed; " +
                            $"the fragment (insert position {insertPosition}) was NOT staged. {StageVerifyFailedReasonSummary(verifyOutcome.Reasons)}",
                            Hint:
                                "Fix the rung using verifyRejectReasons (each carries the importer's mnemonic, hex code, rung " +
                                "index, text and, for known mnemonics, a hint) and stage it again. Pass verifyOnStage=false to " +
                                "stage without the offline check; the rung is then checked when it is published to the controller.",
                            Kind: "StageVerifyFailed",
                            RejectReasonSource: ImportRejectReplay.SourceOfflineReplay,
                            VerifyRejectReasons: verifyOutcome.Reasons,
                            PhaseTimings: stagePhases?.Snapshot(includeSubPhases: true));
                    }
                    else
                    {
                        stageVerified          = true;
                        replayPassedImportText = fragmentToImport;
                        stageVerifiedNote =
                            "verifyOnStage: the staged fragment was imported into an offline copy of the project and " +
                            "verify passed. The rung is still checked again when it is published to the controller.";
                    }
                }
            }

            stagePhases?.Enter(StagePhases.OnlineImport);
            try
            {
                // Route to the correct SDK overload based on target.
                // null / "workstation" → PartialImportRungsFromXmlFileAsync with LeaveEdits
                //   (pre-PRD behaviour, edit is in-memory only and cannot be seen by any
                //   other session).
                // "controller" → the same PartialImportRungsFromXmlFileAsync (both targets use the
                //   rung import since 2026-08-04) with AcceptEdits: sends the edit to the physical controller and
                //   immediately accepts it, so it is visible to any Logix Designer session
                //   that is online with the controller.
                //
                // "live" → same rung API with FinalizeEdits: the import
                //   publishes and finalizes in one call, so the edit executes immediately.
                //
                // Permitted-options guard: LeaveEdits (workstation) and AcceptEdits
                // (controller) are unchanged.  FinalizeEdits is permitted ONLY when the
                // resolved target is live.  The conditional below is the only place an
                // import option is selected; the guard below it makes any other
                // combination a runtime error so the invariant cannot be violated
                // silently during future refactors.

                var importOption = isLiveTarget
                    ? PartialImportOption.FinalizeEdits
                    : sendToController
                        ? PartialImportOption.AcceptEdits
                        : PartialImportOption.LeaveEdits;

                // Explicit guard.  Why FinalizeEdits is allowed here, and only here:
                // it is ONE call that leaves a CLEAN program — no
                // pending-edit zone is created — and it is BENCH-VERIFIED on real
                // hardware (HARDWARE-VERIFIED "One-call publish + finalize through the
                // SDK": a new rung went live in one call, executed, and Studio showed
                // No Edits).  Everywhere else FinalizeEdits would assemble edits
                // without operator review, so it stays forbidden: workstation may only
                // LeaveEdits, controller may only AcceptEdits.
                bool optionPermitted = importOption switch
                {
                    PartialImportOption.FinalizeEdits => isLiveTarget,
                    PartialImportOption.AcceptEdits   => sendToController && !isLiveTarget,
                    PartialImportOption.LeaveEdits    => !sendToController,
                    _                                 => false,
                };
                if (!optionPermitted)
                    throw new InvalidOperationException(
                        $"Internal error: import option '{importOption}' is not permitted " +
                        $"for destination '{resolvedDestination}'. Only LeaveEdits " +
                        "(workstation), AcceptEdits (controller) and FinalizeEdits " +
                        "(live) are valid import options for ProposeRungEditHandler.");

                // BOTH destinations use PartialImportRungsFromXmlFileAsync. The ONLY
                // difference between them is the PartialImportOption above.
                //
                // Do not reintroduce PartialImportWithTargetFromXmlFileAsync here. It
                // was used for the controller path until 2026-08-04 and crashed Logix
                // 5000 Services every time, taking the whole SDK service down
                // (LgxSrv_E_UNHANDLED_EXCEPTION, then LgxSrv_E_SERVER_FAULTED on every
                // subsequent call). Three reasons it was wrong:
                //   1. it is documented for importing *named objects* whose name may be
                //      changed at import time — not rungs; the rung API is this one.
                //   2. its second parameter is the SDK's `targetName`, and the literal
                //      "controller" was being passed into it — our own `target`
                //      parameter value leaking into an unrelated SDK argument.
                //   3. it has no insertPosition/replaceCount, so both were silently
                //      dropped and the caller's requested position was ignored.
                // The failure looked like a vendor defect from the outside, because the
                // crash surfaces inside Rockwell's own Dispose bookkeeping. It was ours.
                //
                // importLogCapture registers an IOperationEvent with the SDK session for
                // the duration of this one import call so that, on failure, the real
                // ImportLog the SDK streams through its event hook (requirement 2
                // "<Exception Severity=… Mnemonic='XMLSrv_E_…'>" with line/column) is
                // available to quote in the structured error below, rather than relying on
                // the exception's own (often terser) Message alone.
                // Intent line (logix-mcp-qpqy.4): an import that can change the controller is recorded BEFORE it is
                // sent, so a server killed mid-import still leaves a trace. The outcome line completes it through
                // the same correlation id; an unfinished one is reported as orphaned on the next connect.
                if (sendToController || isLiveTarget)
                {
                    // The rung texts this call sends to its routine are recorded so the next connect can tell a true
                    // orphan (its rungs sit in the pending zone) from a stale line (logix-mcp-am0x.5).
                    IReadOnlyList<string>? sendingTexts = null;
                    try
                    {
                        sendingTexts =
                            expectedByRoutine is not null && expectedByRoutine.TryGetValue(routineXPath, out var sentTexts)
                                ? sentTexts
                                : RoutineRungSelector.Select(SafeXml.ParseDocument(fragmentToImport), routineXPath)
                                    .Select(r => NormalizeRungText(r.Element("Text")?.Value))
                                    .ToList();
                    }
                    catch (Exception) { /* best effort: a line without texts falls back to a routine-level match */ }
                    audit.LogSending(insertPosition, sendingTexts);
                }

                // logix-mcp-qpqy.8: RxCsE_ASA_BLOCKING_HIGHER_EXCL_LOCK (another client or Studio holds the
                // controller lock) is transient, so only that mnemonic is retried: 3 tries in all, about 10 s.
                // Every other failure is thrown on the first try. The retries made are reported (importRetries).
                await ControllerLockRetry.RunAsync(
                    attempt => session.ExecuteAsync(
                        async project =>
                        {
                            logMark    = importLogCapture.Errors.Count;
                            statusMark = importLogCapture.StatusMessages.Count;
                            project.AddEventHandler(importLogCapture);
                            try
                            {
                                await project.PartialImportRungsFromXmlFileAsync(
                                    routineXPath,
                                    insertPosition,
                                    effectiveReplaceCount,
                                    tempFile,
                                    importOption,
                                    ct).ConfigureAwait(false);
                            }
                            finally
                            {
                                project.RemoveEventHandler(importLogCapture);
                            }
                        },
                        ct),
                    ex => RockwellErrorHints.IsControllerLocked(ex)
                        || importLogCapture.Errors.Skip(logMark).Any(e => RockwellErrorHints.IsMatch(e, RockwellErrorHintIds.ControllerLocked))
                        || importLogCapture.StatusMessages.Skip(statusMark).Any(m => RockwellErrorHints.IsMatch(m, RockwellErrorHintIds.ControllerLocked)),
                    session.ControllerLockRetryDelay,
                    importRetryState,
                    ct,
                    session.ControllerLockRetryWait).ConfigureAwait(false);
            }
            catch (TagServiceException ex) when (ex.Error.Kind == TagServiceErrorKind.SdkServerFaulted)
            {
                audit.Log(routineXPath, insertPosition, "error", null,
                    destination:     resolvedDestination,
                    confirmedByUser: sendToController ? (bool?)confirmedByUser : null);
                return ServerFaulted(ex.Error);
            }
            catch (TagServiceException ex)
            {
                audit.Log(routineXPath, insertPosition, "error", null,
                    destination:     resolvedDestination,
                    confirmedByUser: sendToController ? (bool?)confirmedByUser : null);

                if (ControllerLockedError(ex.Error.Message, importLogCapture, importRetryState, logMark, statusMark) is { } locked)
                    return WithImportFailure(locked, fragmentToImport, importLogCapture);

                // TagServiceException on this path (session.ExecuteAsync/GetProject) is
                // raised only with TagServiceErrorKind.ConnectionFailed — the session was
                // never opened or was disposed mid-call. That is a session-lifecycle
                // failure, not an import-content failure, so it carries no evidence about
                // edit state or import cause; classifying it would be outside an earlier change
                // requirement 2's evidence domain (and would waste a tier-1 read against a
                // session that cannot service it). Skip classification for it.
                if (sendToController && ex.Error.Kind != TagServiceErrorKind.ConnectionFailed)
                {
                    var classified = await ClassifyControllerImportFailureAsync(
                        ex.Error.Message, routineXPath, session, mergedStagedTagNames,
                        importLogCapture.Errors, importLogCapture.StatusMessages,
                        replayImportReject, ct).ConfigureAwait(false);
                    if (classified is not null)
                        return WithImportFailure(classified, fragmentToImport, importLogCapture);
                }
                else if (!sendToController && ex.Error.Kind != TagServiceErrorKind.ConnectionFailed
                    && ClassifyWorkstationPieFailure(ex.Error.Message, importLogCapture) is { } stagePie)
                    return WithImportFailure(stagePie, fragmentToImport, importLogCapture);

                return WithImportFailure(new ProposeRungEditError(
                    WithSkillPointerForXmlSrvFailure(
                    $"SDK error while proposing rung edit: {ex.Error.Message}" +
                    (sendToController ? BuildMergedStagedTagsNote(mergedStagedTagNames) : string.Empty),
                    ex.Error.Message, importLogCapture.Errors),
                    Hint:       RoundTripHint,
                    DocsAnchor: RungEditDocsAnchor), fragmentToImport, importLogCapture);
            }
            catch (OperationNotPerformedException ex)
            {
                audit.Log(routineXPath, insertPosition, "error", null,
                    destination:     resolvedDestination,
                    confirmedByUser: sendToController ? (bool?)confirmedByUser : null);

                if (ControllerLockedError(ex.Message, importLogCapture, importRetryState, logMark, statusMark) is { } locked)
                    return WithImportFailure(locked, fragmentToImport, importLogCapture);

                if (sendToController)
                {
                    var classified = await ClassifyControllerImportFailureAsync(
                        ex.Message, routineXPath, session, mergedStagedTagNames,
                        importLogCapture.Errors, importLogCapture.StatusMessages,
                        replayImportReject, ct).ConfigureAwait(false);
                    if (classified is not null)
                        return WithImportFailure(classified, fragmentToImport, importLogCapture);
                }
                else if (ClassifyWorkstationPieFailure(ex.Message, importLogCapture) is { } stagePie)
                    return WithImportFailure(stagePie, fragmentToImport, importLogCapture);

                return WithImportFailure(new ProposeRungEditError(
                    WithSkillPointerForXmlSrvFailure(
                    $"Rung edit could not be performed for '{routineXPath}': {ex.Message}" +
                    (sendToController ? BuildMergedStagedTagsNote(mergedStagedTagNames) : string.Empty),
                    ex.Message, importLogCapture.Errors),
                    Hint:       RoundTripHint,
                    DocsAnchor: RungEditDocsAnchor), fragmentToImport, importLogCapture);
            }
            catch (OperationFailedException ex)
            {
                audit.Log(routineXPath, insertPosition, "error", null,
                    destination:     resolvedDestination,
                    confirmedByUser: sendToController ? (bool?)confirmedByUser : null);

                if (ControllerLockedError(ex.Message, importLogCapture, importRetryState, logMark, statusMark) is { } locked)
                    return WithImportFailure(locked, fragmentToImport, importLogCapture);

                // Every controller-target import failure is classified
                // requirement 2: an XMLSrv_E_IMPORT_ABORTED_NO_CHANGES importer rejection
                // is named ImportRejectedByImporter (never a pending-edit issue), and any
                // other failure runs the tier-1 logix_pending_edits read — never a
                // "Project is not open." or message-substring heuristic — to decide
                // whether to name PendingEditsPresent. See
                // ClassifyControllerImportFailureAsync for the full rule.
                if (sendToController)
                {
                    var classified = await ClassifyControllerImportFailureAsync(
                        ex.Message, routineXPath, session, mergedStagedTagNames,
                        importLogCapture.Errors, importLogCapture.StatusMessages,
                        replayImportReject, ct).ConfigureAwait(false);
                    if (classified is not null)
                        return WithImportFailure(classified, fragmentToImport, importLogCapture);
                }
                else if (ClassifyWorkstationPieFailure(ex.Message, importLogCapture) is { } stagePie)
                    return WithImportFailure(stagePie, fragmentToImport, importLogCapture);

                return WithImportFailure(new ProposeRungEditError(
                    WithSkillPointerForXmlSrvFailure(
                    $"Rung edit operation failed for '{routineXPath}': {ex.Message}" +
                    (sendToController ? BuildMergedStagedTagsNote(mergedStagedTagNames) : string.Empty),
                    ex.Message, importLogCapture.Errors),
                    Hint:       RoundTripHint,
                    DocsAnchor: RungEditDocsAnchor), fragmentToImport, importLogCapture);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                audit.Log(routineXPath, insertPosition, "error", null,
                    destination:     resolvedDestination,
                    confirmedByUser: sendToController ? (bool?)confirmedByUser : null);

                if (ControllerLockedError(ex.Message, importLogCapture, importRetryState, logMark, statusMark) is { } locked)
                    return WithImportFailure(locked, fragmentToImport, importLogCapture);

                if (sendToController)
                {
                    var classified = await ClassifyControllerImportFailureAsync(
                        ex.Message, routineXPath, session, mergedStagedTagNames,
                        importLogCapture.Errors, importLogCapture.StatusMessages,
                        replayImportReject, ct).ConfigureAwait(false);
                    if (classified is not null)
                        return WithImportFailure(classified, fragmentToImport, importLogCapture);
                }
                else if (ClassifyWorkstationPieFailure(ex.Message, importLogCapture) is { } stagePie)
                    return WithImportFailure(stagePie, fragmentToImport, importLogCapture);

                return WithImportFailure(new ProposeRungEditError(
                    WithSkillPointerForXmlSrvFailure(
                    $"Unexpected SDK error while proposing rung edit for '{routineXPath}': {ex.Message}" +
                    (sendToController ? BuildMergedStagedTagsNote(mergedStagedTagNames) : string.Empty),
                    ex.Message, importLogCapture.Errors),
                    Hint:       RoundTripHint,
                    DocsAnchor: RungEditDocsAnchor), fragmentToImport, importLogCapture);
            }

            // ── Post-import state verification ───────────────────────────────
            // A successful import return code alone is insufficient evidence that
            // the edit is actually pending on the controller.  Read back the
            // connected/edit state via the SDK to confirm the session is still
            // Online — the only verifiable indicator that LeaveEdits left a
            // pending edit as expected.  If the state is not Online (e.g. the
            // session dropped to Connected, Offline, or Unknown), or if the call
            // itself fails, surface a structured error describing what was observed
            // rather than silently claiming the edit is proposed.

            stagePhases?.Enter(StagePhases.PostImportCheck);
            ConnectedState postImportState;
            try
            {
                postImportState = await session.ExecuteAsync(
                    project => project.ReadConnectedStateAsync(ct),
                    ct).ConfigureAwait(false);
            }
            catch (TagServiceException ex) when (ex.Error.Kind == TagServiceErrorKind.SdkServerFaulted)
            {
                audit.Log(routineXPath, insertPosition, "error", null,
                    destination:     resolvedDestination,
                    confirmedByUser: sendToController ? (bool?)confirmedByUser : null);
                return ServerFaulted(ex.Error);
            }
            catch (TagServiceException ex)
            {
                audit.Log(routineXPath, insertPosition, "error", null,
                    destination:     resolvedDestination,
                    confirmedByUser: sendToController ? (bool?)confirmedByUser : null,
                    buildVerify:     "skipped",
                    designerVersion: session.DesignerVersion?.ToString());
                return new ProposeRungEditError(
                    $"Rung import was accepted but the post-import state check failed: " +
                    $"{ex.Error.Message}",
                    PendingAfterError: await ReadPendingAfterErrorAsync(routineXPath, session, ct)
                        .ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                audit.Log(routineXPath, insertPosition, "error", null,
                    destination:     resolvedDestination,
                    confirmedByUser: sendToController ? (bool?)confirmedByUser : null,
                    buildVerify:     "skipped",
                    designerVersion: session.DesignerVersion?.ToString());
                return new ProposeRungEditError(
                    $"Rung import was accepted but ReadConnectedStateAsync failed for " +
                    $"'{routineXPath}': {ex.Message}",
                    PendingAfterError: await ReadPendingAfterErrorAsync(routineXPath, session, ct)
                        .ConfigureAwait(false));
            }

            // This change requirement 3 (G24): "Connected" is the SDK's name for a session that is attached to the
            // controller and holds edits; it is equivalent to Online for this check. Only a state that is neither
            // is inconclusive, and that is reported as a typed PostImportCheckInconclusive carrying what a
            // read-back saw (pendingAfterError) — never the untyped "may not have been applied".
            if (postImportState != ConnectedState.Online && postImportState != ConnectedState.Connected)
            {
                audit.Log(
                    routineXPath, insertPosition, "error", postImportState.ToString(),
                    destination:     resolvedDestination,
                    confirmedByUser: sendToController ? (bool?)confirmedByUser : null,
                    buildVerify:     "skipped",
                    designerVersion: session.DesignerVersion?.ToString());
                var observed = await ReadPendingAfterErrorAsync(routineXPath, session, ct).ConfigureAwait(false);
                return new ProposeRungEditError(
                    $"Rung import was accepted but the post-import state check is inconclusive: the session " +
                    $"reported '{postImportState}' (expected Online or Connected). " +
                    (observed.State == "pending"
                        ? "The read-back shows the rung(s) pending (see pendingAfterError), so the edit was applied."
                        : "See pendingAfterError for what the read-back observed; check logix_pending_edits before retrying."),
                    Hint: "Call logix_pending_edits to confirm the state; the session is not blocked, so stage or publish can follow.",
                    Kind: PostImportCheckInconclusiveKind,
                    PendingAfterError: observed);
            }

            // ── Structural build verify ───────────────────────────────────────
            // BuildAsync(DefaultTarget) compiles the entire controller to detect
            // structural errors in the imported rung (e.g. undefined tag references).
            // This gate runs AFTER the state check so we know the edit is in place.
            //
            // Three outcomes, reported as buildVerify:
            //   passed      — the build ran and succeeded.
            //   unavailable — Logix Designer before v37 cannot run the SDK build check.
            // That is NOT a build failure: no rollback, the
            //                 normal post-import verification below still runs, and the
            //                 Designer version is remembered so later publishes skip the call.
            //   failed      — the build really failed (an error return, below).
            //
            // Why DefaultTarget: the emulated/physical distinction is not reliably
            // observable from the session handle; DefaultTarget lets the SDK choose
            // the correct compile target at the cost of a potential recompile, not
            // a wrong answer.
            // The build never runs inside the online session (it would put a build fault in the live handle):
            // a saved copy of the session project is opened offline in a separate handle and built instead
            // (OfflineBuildCheck). The copy must carry this edit, or buildVerify is "unavailable" with the reason.
            string buildVerify;
            string? buildUnavailableReason = null;
            string? postImportBuildCheck   = null;
            if (session.Capabilities.Require(SdkCapabilityNames.BuildCheck) is not null)
            {
                // The capability table says the build check is unavailable on this session (the SDK already
                // told it the Designer is below v37): do not call again.
                buildVerify = "unavailable";
            }
            else if (replayPassedImportText is not null
                     && await ImportTextUnchangedAsync(tempFile, fragmentToImport, replayPassedImportText).ConfigureAwait(false))
            {
                // This change req. 3 (G34): verifyOnStage already imported this exact text into an offline copy and the
                // copy built, and the online import did not change it. A second copy cycle would repeat that build
                // (12 to 14 s on a large project), so it is skipped. A failing replay never gets here.
                buildVerify         = BuildVerifyPassed;
                postImportBuildCheck = PostImportBuildCheckSkippedReplayPassed;
            }
            else
            {
                if (stagePhases is not null) postImportBuildCheck = "ran";
                try
                {
                    var expectedRungTexts = RoutineRungSelector.Select(
                            SafeXml.ParseDocument(fragmentToImport), routineXPath)
                        .Select(r => NormalizeRungText(r.Element("Text")?.Value))
                        .Concat(session.StagedRungBuffer.Values.SelectMany(v => v))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    var stagedRoutines = session.StagedRoutineXPaths.Append(routineXPath)
                        .Distinct(StringComparer.Ordinal).ToList();

                    var outcome = await OfflineBuildCheck.RunAsync(
                        session, RequestedBuildTarget.DefaultTarget,
                        expectedRungTexts, stagedRoutines, null, ct).ConfigureAwait(false);

                    if (outcome.StagedEditLost && stagingToWorkstation)
                    {
                        // The saved copy lacks a staged edit. Not a note: an error, and the
                        // staging bookkeeping below is skipped, so nothing more is staged.
                        audit.Log(routineXPath, insertPosition, "error", null,
                            destination:     resolvedDestination,
                            buildVerify:     "unavailable",
                            designerVersion: session.DesignerVersion?.ToString());
                        return new ProposeRungEditError(
                            $"StagedEditLost: {outcome.UnavailableReason} The stage at position {insertPosition} of " +
                            $"'{routineXPath}' was not recorded, and no further rungs were staged.",
                            Hint: StagedEditLostHint(session.StagedRungBuffer.Values.Sum(v => v.Count)),
                            Kind: "StagedEditLost");
                    }
                    if (outcome.UnavailableReason is { } reason)
                    {
                        buildVerify            = "unavailable";
                        buildUnavailableReason = reason;
                    }
                    else if (outcome.BuildFailure is { } copyBuildFailure)
                        throw copyBuildFailure;
                    else
                        buildVerify = "passed";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TagServiceException faultEx) when (faultEx.Error.Kind == TagServiceErrorKind.SdkServerFaulted)
                {
                    // Logix Services died during the structural build check. The session is dead; the
                    // build did not fail on the rung, so do not roll back through the dead SDK.
                    audit.Log(routineXPath, insertPosition, "build-failed", null,
                        destination:     resolvedDestination,
                        confirmedByUser: sendToController ? (bool?)confirmedByUser : null,
                        buildVerify:     "failed",
                        designerVersion: session.DesignerVersion?.ToString());
                    return ServerFaulted(faultEx.Error);
                }
                catch (Exception buildEx)
                {
                    var unsupported = DesignerVersionInfo.TryParseUnsupportedVersion(ExceptionText(buildEx));
                    if (unsupported is not null)
                    {
                        session.RecordDesignerVersionFromSdk(unsupported);
                        buildVerify = "unavailable";
                    }
                    else
                    {
                        return await BuildFailedAfterImportAsync(
                            buildEx, routineXPath, insertPosition, session, resolvedDestination,
                            sendToController, isLiveTarget, confirmedByUser, audit, ct).ConfigureAwait(false);
                    }
                }
            }
            string? designerVersionText = session.DesignerVersion?.ToString();

            // The build of the saved copy carries the staged rungs, but Logix does not check
            // a rung staged in the session image, so a passed build says nothing about them. Say so, unless
            // verifyOnStage replayed and verified this stage.
            string? passedExcludingStagedNote = null;
            if (buildVerify == BuildVerifyPassed && stagingToWorkstation && stageVerified != true)
            {
                buildVerify               = BuildVerifyPassedExcludingStaged;
                passedExcludingStagedNote = PassedExcludingStagedNote;
            }

            // The rung_edit audit line is written once, below, after the post-publish census,
            // so it reports what the census found rather than what the import call returned.

            // ── Clear staged buffer after successful controller publish ────────
            // The buffered tag declarations have now been committed to the
            // controller and must not be re-sent on a subsequent publish call.
            // Clear after the build verify confirms the operation succeeded so
            // a build failure leaves the buffer intact for a retry.
            //
            // HasStagedRungEdit mirrors the same lifecycle: a controller-path
            // publish sends the entire accumulated workstation batch at once,
            // so nothing is left only-locally-staged afterwards. A workstation
            // -path stage leaves the rung in the session's in-memory image
            // only, so the flag is set so logix_close_project can refuse to
            // silently discard it.
            // N10: what this publish sent, captured before the buffer is cleared, for the success message.
            // Counted across EVERY routine holding staged rungs: the publish sends them all.
            var publishRungsByRoutine = sendToController
                ? CountStagedRungsByRoutine(session.StagedRungBuffer)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            uint publishStagedReplace = sendToController
                ? (uint)session.StagedReplaceCounts.Values.Sum(v => (long)v) : 0u;
            // Per-routine staged replace counts, captured before the buffer is cleared (publishedRoutines[i].replaceCount).
            var publishStagedReplaceByRoutine = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            if (sendToController)
                foreach (var kv in session.StagedReplaceCounts)
                {
                    publishStagedReplaceByRoutine.TryGetValue(kv.Key.RoutineXPath, out var acc);
                    publishStagedReplaceByRoutine[kv.Key.RoutineXPath] = acc + kv.Value;
                }
            // The rungs this publish replaced, over all routines. A tail commit's replace count is in image rungs (the
            // new rungs' slots plus the hidden originals), so the staged replace counts stand in for it; otherwise the
            // call's own replace count adds to them. The success message, replaceCount and publishedRoutines all use this.
            uint publishReplaceTotal = tailCommit is not null ? publishStagedReplace : effectiveReplaceCount + publishStagedReplace;
            if (sendToController)
            {
                session.StagedTagBuffer.Clear();
                session.HasStagedRungEdit = false;
                session.StagedRungCount   = 0;
                session.StagedRungBuffer.Clear();
                session.StagedReplaceCounts.Clear();
                session.ForgetStagedImports();
                // The original comments stay with the zone this publish creates, so a cancel
                // of that zone (logix_cancel_pending_edits) can write them back.
                session.AttachCommentUndosToZone();
                session.ForgetRoutineBaselines();
                session.StagedReplacedRungTexts.Clear();
                // PreStageTagCensus is NOT cleared here — it remains stable for the
                // lifetime of the session so that future pending-edits reads still
                // filter correctly even after a publish.
            }
            else
            {
                session.HasStagedRungEdit = true;
                session.StagedRungCount  += stagedRungCountThisCall;

                // Remember the exact import so a clear of ANOTHER routine can rebuild the
                // session image from the controller and replay this one.
                session.RecordStagedImport(new LogixProjectSession.StagedImport(
                    routineXPath, insertPosition, effectiveReplaceCount, fragmentToImport));

                // The originals an explicit comment overwrote, for logix_clear_staged to write back.
                foreach (var (origIdx, origText, origComment) in commentOriginals)
                    session.RecordCommentUndo(new LogixProjectSession.CommentUndoEntry(
                        routineXPath, origIdx, origText, origComment, fragmentToImport));

                if (stagedRungTextsThisCall.Count > 0)
                {
                    var bufferKey = (routineXPath, insertPosition);
                    if (!session.StagedRungBuffer.TryGetValue(bufferKey, out var bucket))
                    {
                        bucket = new List<string>();
                        session.StagedRungBuffer[bufferKey] = bucket;
                    }
                    bucket.AddRange(stagedRungTextsThisCall);
                }
                // The bucket remembers its replaceCount, so its originals' range stays reserved.
                if (replaceCount > 0 && stagedRungTextsThisCall.Count > 0)
                    session.StagedReplaceCounts[(routineXPath, insertPosition)] = replaceCount;

                // Record the normalised texts of the original rungs displaced by this
                // replace/delete stage. PendingEditsHandler uses this set to move those
                // rungs from pendingRungs to staged.replacedRungs so that the routine's
                // state field reflects only genuine controller-side pending edits.
                foreach (var text in replacedRungTextsThisCall)
                    session.StagedReplacedRungTexts.Add(text);
                if (stagedRungTextsThisCall.Count > 0)
                    foreach (var (unchangedIndex, unchangedText) in unchangedReplacementsThisCall)
                        session.StagedUnchangedReplacements[(routineXPath, unchangedIndex)] = unchangedText;
            }

            // ── Emit tags_created audit events (controller path only) ─────────
            // Write one event per declaration that was just committed to the
            // controller so the complete tag-creation history is queryable from
            // audit.jsonl alone.  Emitted after the buffer clear so the record
            // is only written when the publish has been confirmed by the build
            // verify (i.e. it genuinely succeeded).
            if (publishedTagEntries is { Count: > 0 })
            {
                // Compute the granted-namespace flag for the audit record separately:
                // WritableViaLogixWrite is always false on the PublishedTagEntry (the
                // CreatedByThisSession guard blocks logix_write for the session lifetime),
                // but the audit log still records whether the tag is in a command namespace
                // so the log is useful for post-session analysis.
                var auditGateConfig = GateConfigResolver.Current;
                AuditLogger.LogTagsCreated(
                    proposalId,
                    publishedTagEntries.Select(e => (e.TagName,
                        auditGateConfig.CommandPrefixes.Any(p =>
                            e.TagName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))));
            }

            // ── Post-publish pending census verification ───────────────
            // After AcceptEdits committed the staged rungs to the controller, export
            // the routine and verify that every staged rung appears as a pending I/IR
            // type rung.  A mismatch means the controller's pending state does not
            // match what was expected: never return status='proposed' in this case —
            // return ProposeRungEditMismatch with kind='PublishMismatch' instead and
            // instruct the engineer to review in Studio 5000.  On a successful match,
            // include verifiedPendingRungs in the ProposeRungEditResult.
            //
            // Skipped when expectedByRoutine is null (no staged or fragment rungs before
            // the publish) or when the export itself fails (indeterminate, treated as
            // unverified rather than a hard mismatch so a transient SDK error does not
            // block every publish).
            // The single rung_edit audit line of this call. Every exit below the import
            // writes it through here: "proposed" only when nothing contradicted the edit, "publish-mismatch" or
            // "error" otherwise. verified is false when the census could not read the controller back.
            void LogCensusAudit(string outcome, bool? censusVerified = null) =>
                audit.Log(routineXPath, insertPosition, outcome,
                    stagingToWorkstation ? null : "online",
                    destination:     resolvedDestination,
                    gateAnnotations: outcome == "proposed"
                                         ? ToAuditAnnotations(BuildGateAnnotations(fragmentScan, policy))
                                         : null,
                    confirmedByUser: sendToController ? (bool?)confirmedByUser : null,
                    buildVerify:     buildVerify,
                    designerVersion: designerVersionText,
                    stagedIn:        stagingToWorkstation ? StagedInSessionImage : null,
                    verified:        stagingToWorkstation ? stageVerified : censusVerified);

            int? verifiedPendingRungs = null;
            int? unexpectedPendingRungs = null;
            // Controller path always reports an explicit status; workstation omits it.
            string? verification = sendToController ? "notApplicable" : null;
            // A live (FinalizeEdits) import leaves NOTHING pending, so the
            // pending-census check below (which expects the rungs as pending I/IR) does
            // not apply. Live has its own post-import verification: re-export the
            // routine and require every rung normal (N), the imported texts at
            // insertPosition…, and logix_pending_edits reporting none.
            IReadOnlyList<PendingEditRungEntry>? liveAfterRungs = null;
            if (isLiveTarget)
            {
                var programName = ProgramNamePattern.Match(routineXPath).Groups[1].Value;
                var routineName = RoutineNamePattern.Match(routineXPath).Groups[1].Value;
                var (liveConfirmed, afterRungs, failureDetail) = await VerifyLiveFinalizeAsync(
                    routineXPath, programName, routineName, insertPosition, fragmentToImport,
                    session, ct,
                    deleteBeforeRungs: isLiveDelete ? liveBeforeRungs : null,
                    deleteCount:       isLiveDelete ? replaceCount : 0u).ConfigureAwait(false);
                liveAfterRungs = afterRungs;

                if (!liveConfirmed)
                {
                    if (!isLiveDelete && afterRungs is not null)
                    {
                        var importedTexts = RoutineRungSelector.Select(
                                SafeXml.ParseDocument(fragmentToImport), routineXPath)
                            .Select(r => NormalizeRungText(r.Element("Text")?.Value)).ToList();
                        var afterTexts = afterRungs.Select(r => r.Text).ToList();
                        var noRungs = CheckImportAddedRungs(
                            routineXPath, importedTexts,
                            importedTexts.Count(t => RungTextComparer.ContainsMatch(afterTexts, t)),
                            afterTexts);
                        if (noRungs is not null)
                        {
                            LogCensusAudit("publish-mismatch");
                            return new ProposeRungEditError(
                                $"{noRungs.Error} Before the import: {DescribeRungs(liveBeforeRungs)}. " +
                                $"After the import: {DescribeRungs(afterRungs)}.",
                                Hint:
                                    "Do not assume the edit is live. Inspect the routine with " +
                                    "logix_export_rungs and logix_pending_edits before retrying.",
                                Kind:        "ImportAddedNoRungs",
                                BeforeRungs: liveBeforeRungs,
                                AfterRungs:  afterRungs,
                                PendingAfterError: await ReadPendingAfterErrorAsync(routineXPath, session, ct)
                                    .ConfigureAwait(false));
                        }
                    }

                    LogCensusAudit("error");
                    AuditLogger.LogFinalizeEdits(programName, routineName, "unconfirmed",
                        beforeState: "none",
                        afterState:  "unconfirmed",
                        gateAnnotations: ToAuditAnnotations(BuildGateAnnotations(fragmentScan, policy)),
                        operation: isLiveDelete ? "delete" : null);
                    return new ProposeRungEditError(
                        $"LiveFinalizeNotConfirmed: the FinalizeEdits import of '{routineXPath}' " +
                        $"returned without error, but the re-exported routine is not in the " +
                        $"expected clean state: {failureDetail}. Before the import: " +
                        $"{DescribeRungs(liveBeforeRungs)}. After the import: " +
                        $"{DescribeRungs(afterRungs)}.{BuildMergedStagedTagsNote(mergedStagedTagNames)}",
                        Hint:
                            "Do not assume the edit is live. Inspect the routine with " +
                            "logix_export_rungs and logix_pending_edits (or Logic -> Online " +
                            "Edits in Logix Designer) before retrying.",
                        Kind:        "LiveFinalizeNotConfirmed",
                        BeforeRungs: liveBeforeRungs,
                        AfterRungs:  afterRungs,
                        PendingAfterError: await ReadPendingAfterErrorAsync(routineXPath, session, ct)
                            .ConfigureAwait(false));
                }

                AuditLogger.LogFinalizeEdits(programName, routineName, "finalized",
                    beforeState: "none",
                    afterState:  "none",
                    gateAnnotations: ToAuditAnnotations(BuildGateAnnotations(fragmentScan, policy)),
                    operation: isLiveDelete ? "delete" : null);
                verification = "verified";
            }
            IReadOnlyList<PublishedRoutine>? publishedRoutines = null;
            IReadOnlyList<int>? unchangedReplacements = null;
            uint RoutineReplaceCount(string routine)
            {
                publishStagedReplaceByRoutine.TryGetValue(routine, out var staged);
                return staged + (tailCommit is null && string.Equals(routine, routineXPath, StringComparison.OrdinalIgnoreCase)
                    ? effectiveReplaceCount : 0u);
            }
            if (!isLiveTarget && expectedByRoutine is not null)
            {
                var mismatches = new List<RoutineCensusMismatch>();
                var published  = new List<PublishedRoutine>();
                bool anyUnreadable = false;
                int  verifiedTotal = 0;
                int  unexpectedTotal = 0;
                var  unchangedIndexes = new List<int>();
                IReadOnlyList<string>? ownActual = null;
                IReadOnlyList<string>? firstMismatchActual = null;

                foreach (var (censusRoutine, expectedTexts) in expectedByRoutine)
                {
                    var (censusReadable, actualPendingIndexed, _) =
                        await ReadPendingRungsWithIndexAsync(censusRoutine, session, ct)
                            .ConfigureAwait(false);

                    if (!censusReadable)
                    {
                        anyUnreadable = true;
                        continue;
                    }
                    IReadOnlyList<string> actualPendingTexts = actualPendingIndexed.Select(p => p.Text).ToList();

                    // A successful import that left none of this call's own rungs behind is
                    // ImportAddedNoRungs (not a partial PublishMismatch).
                    // Only this call's own fragment rungs count here: a staged-only publish keeps
                    // reporting PublishMismatch.
                    unchangedByRoutine.TryGetValue(censusRoutine, out var routineUnchanged);
                    routineUnchanged ??= [];
                    var ownExpected = string.Equals(censusRoutine, routineXPath, StringComparison.OrdinalIgnoreCase)
                        ? ApplyUnchangedReplacements(ownFragmentTexts, actualPendingTexts, routineUnchanged).Missing
                        : ownFragmentTexts;
                    var noRungs = string.Equals(censusRoutine, routineXPath, StringComparison.OrdinalIgnoreCase)
                        ? CheckImportAddedRungs(
                            censusRoutine, ownExpected,
                            actualPendingTexts.Count(t => RungTextComparer.AnyExpectedMatches(ownFragmentTexts, t)),
                            actualPendingTexts)
                        : null;
                    if (noRungs is not null)
                    {
                        session.RecordPublishMismatch(censusRoutine, expectedTexts);
                        LogCensusAudit("publish-mismatch");
                        return noRungs with
                        {
                            PendingAfterError = new PendingAfterError("pending", actualPendingTexts),
                            Routines = [new RoutineCensusMismatch(censusRoutine, expectedTexts, actualPendingTexts)],
                        };
                    }

                    // Expected text vs exported text goes through the shared comparator,
                    // so an export's `?` preset/accumulator matches the staged literal.
                    // logix-mcp-am0x.4: a replacement whose text equals the original stays N, so it is accepted
                    // as N at its index instead of being counted missing from the pending set.
                    var (stillMissing, acceptedUnchanged) =
                        ApplyUnchangedReplacements(expectedTexts, actualPendingTexts, routineUnchanged);
                    bool allFound = stillMissing.Count == 0;
                    unchangedIndexes.AddRange(acceptedUnchanged);

                    if (!allFound)
                    {
                        mismatches.Add(new RoutineCensusMismatch(censusRoutine, expectedTexts, actualPendingTexts));
                        firstMismatchActual ??= actualPendingTexts;
                        if (string.Equals(censusRoutine, routineXPath, StringComparison.OrdinalIgnoreCase))
                            ownActual = actualPendingTexts;
                        continue;
                    }

                    // Census matched for this routine — count the actual pending I/IR rungs
                    // that are among its expected rungs (those we verified).
                    int verifiedHere = actualPendingTexts.Count(t => RungTextComparer.AnyExpectedMatches(expectedTexts, t));
                    verifiedTotal += verifiedHere;

                    // Where every expected rung (staged and this call's own) landed, read from the post-publish
                    // census itself and reported per routine. Independent of whether the call carried a fragment,
                    // so a staged-only publish reports its staged rungs' indices too.
                    var landedIndices = actualPendingIndexed
                        .Where(p => RungTextComparer.AnyExpectedMatches(expectedTexts, p.Text))
                        .Select(p => p.Index)
                        .Distinct().OrderBy(i => i)
                        .ToList();

                    if (expectedTexts.Count > 0)
                    {
                        // The landed rung texts, only when they are exactly the expected rungs as one block and no
                        // replacement stayed unchanged: then the session image can follow the publish.
                        IReadOnlyList<string>? landedTexts = null;
                        IReadOnlyList<string?>? landedComments = null;
                        if (landedIndices.Count == expectedTexts.Count && routineUnchanged.Count == 0 &&
                            landedIndices[^1] - landedIndices[0] == landedIndices.Count - 1)
                            landedTexts = actualPendingIndexed
                                .Where(p => landedIndices.Contains(p.Index))
                                .OrderBy(p => p.Index).Select(p => p.Text).ToList();
                        if (landedTexts is not null)
                            landedComments = actualPendingIndexed
                                .Where(p => landedIndices.Contains(p.Index))
                                .OrderBy(p => p.Index)
                                .Select(p => LandedRungComment(p.Text, p.Comment, censusRoutine, routineXPath, ownFragmentComments))
                                .ToList();
                        published.Add(new PublishedRoutine(censusRoutine, verifiedHere,
                            landedIndices.Count > 0 ? landedIndices : null,
                            landedIndices.Count > 0 ? (uint)landedIndices[0] : (uint?)null,
                            RoutineReplaceCount(censusRoutine),
                            landedTexts, landedComments));
                    }

                    // Informational only: pending rungs that are neither expected nor
                    // were already present before the publish (needs a readable baseline).
                    if (prePublishPendingByRoutine.TryGetValue(censusRoutine, out var baseline))
                    {
                        unexpectedTotal += actualPendingTexts.Count(t =>
                            !RungTextComparer.AnyExpectedMatches(expectedTexts, t)
                            && !RungTextComparer.AnyExpectedMatches(baseline, t));
                    }
                }

                if (mismatches.Count > 0)
                {
                    var names = string.Join(", ", mismatches.Select(m => $"'{m.Routine}'"));
                    var pendingForError = ownActual ?? firstMismatchActual ?? Array.Empty<string>();
                    // Remember the mismatch so test/assemble refuse
                    // until it is resolved (cancel, or a pending_edits read that matches).
                    foreach (var m in mismatches)
                        session.RecordPublishMismatch(m.Routine, m.ExpectedRungs);
                    LogCensusAudit("publish-mismatch");
                    return new ProposeRungEditMismatch(
                        Error:
                            $"Post-publish pending census mismatch for {names}: " +
                            "the controller's pending I/IR rungs do not match what was " +
                            "expected after AcceptEdits. The AcceptEdits call completed " +
                            "without error, but re-exporting the routine found a different " +
                            "set of I/IR-type pending rungs than the staged buffer predicted. " +
                            "See routines for the expected and found texts of each differing routine. " +
                            "Review the pending edits in Studio 5000 (Logic → Online Edits) " +
                            "before proceeding — do not accept or assemble until the " +
                            "discrepancy is understood. If the edit is incorrect, cancel it " +
                            "in Studio 5000 (Logic → Online Edits → Cancel Edits).",
                        Kind:          "PublishMismatch",
                        ExpectedRungs: mismatches.SelectMany(m => m.ExpectedRungs).ToList(),
                        PendingRungs:  mismatches.SelectMany(m => m.FoundRungs).ToList(),
                        PendingAfterError: new PendingAfterError("pending", pendingForError),
                        Routines:      mismatches);
                }

                if (anyUnreadable)
                {
                    verification = "unverified";
                }
                else
                {
                    verification        = "verified";
                    verifiedPendingRungs = verifiedTotal;
                    publishedRoutines   = published;
                    if (unchangedIndexes.Count > 0)
                        unchangedReplacements = unchangedIndexes.Distinct().OrderBy(i => i).ToList();
                    if (unexpectedTotal > 0)
                        unexpectedPendingRungs = unexpectedTotal;
                }
            }

            // ── Post-publish preset verification ────────────────────────
            // A routine export writes a timer's/counter's preset as `?`, so the census above cannot
            // prove the preset landed. For every new or replaced rung with a literal preset on a table
            // instruction, read <tag>.PRE and compare. A difference is a PresetMismatch in the result;
            // it is NOT a PublishMismatch and records nothing in the session's unresolved set, because
            // the rung text itself matched (a text difference already returned above).
            IReadOnlyList<PresetVerified>? presetsVerified = null;
            IReadOnlyList<PresetMismatch>? presetMismatches = null;
            IReadOnlyList<string>? presetsUnread = null;
            if (presetReader is not null && sendToController && expectedByRoutine is not null)
            {
                var verifiedPresets = new List<PresetVerified>();
                var mismatchedPresets = new List<PresetMismatch>();
                var unreadPresets = new List<string>();
                foreach (var (presetRoutine, presetTexts) in expectedByRoutine)
                {
                    var expectedPresets = PresetVerifier.ExtractLiteralPresets(presetTexts);
                    if (expectedPresets.Count == 0)
                        continue;
                    var presetProgram = ProgramNamePattern.Match(presetRoutine).Groups[1].Value;
                    var verification0 = await PresetVerifier.VerifyAsync(
                        presetReader, presetProgram, expectedPresets, ct).ConfigureAwait(false);
                    verifiedPresets.AddRange(verification0.Verified);
                    mismatchedPresets.AddRange(verification0.Mismatches);
                    unreadPresets.AddRange(verification0.Unread);
                }
                if (verifiedPresets.Count > 0)   presetsVerified   = verifiedPresets;
                if (mismatchedPresets.Count > 0) presetMismatches  = mismatchedPresets;
                if (unreadPresets.Count > 0)     presetsUnread     = unreadPresets;
            }

            // Build a path-specific message that accurately states where the edit landed.
            // Controller path: edits were accepted on the controller and are visible to any
            //   Logix Designer session online with it — do not say "not accepted" or imply
            //   pending-edit visibility limited to Studio.
            // Workstation path: rungs are in the server's in-memory image; tag declarations
            //   are in the session staging buffer — neither reaches the controller or any
            //   other session.  Do not imply Studio 5000 visibility or pending-edits state
            //   in Logix Designer.
            string tagDisclosure = BuildTagDisclosure(publishedTagEntries, externalAccessForced);
            // an earlier change G35: a replace whose every rung has the text of the rung it displaces leaves nothing pending
            // (Logix keeps such a rung normal), creates no zone and sends nothing the controller must assemble.
            bool identicalTextOnly = sendToController && !isLiveTarget
                && unchangedReplacements is { Count: > 0 }
                && (verifiedPendingRungs ?? 0) == 0
                && (publishedTagEntries?.Count ?? 0) == 0;
            // A tail commit injects the staged tail into the import fragment; those rungs are staged, not the call's own,
            // and are already counted in publishRungsByRoutine (counting them again miscounted the rungs sent).
            int ownPublishRungs = sendToController && tailCommit is null ? CountFragmentRungs(fragmentToImport, routineXPath) : 0;
            // Every position sentence and the reported insertPosition come from the census's landedIndices.
            string landedHeader = DescribeLandedHeader(routineXPath, publishedRoutines, insertPosition);
            uint? reportedInsertPosition = ReportedInsertPosition(routineXPath, publishedRoutines, insertPosition);
            string successMessage = isLiveDelete
                ? $"RUNGS DELETED LIVE. Routine '{routineXPath}', deleted {Plural(replaceCount, "rung")} " +
                  $"at position {insertPosition} (see deletedRungs for their text and comments). " +
                  "The delete was imported with FinalizeEdits in one call (an empty RLLContent " +
                  "replacing the rungs): nothing is left pending and the logic change executes " +
                  "on the controller immediately. VERIFIED: the routine was re-exported after " +
                  "the import — it has the expected number of rungs, every rung is normal, the " +
                  "following rungs shifted up with unchanged text, and logix_pending_edits " +
                  "reports nothing pending."
                : isLiveTarget
                ? $"EDIT PUBLISHED AND FINALIZED LIVE. " +
                  $"Routine '{routineXPath}', insert position {insertPosition}, " +
                  $"{DescribeReplacing(effectiveReplaceCount)}. " +
                  "The rungs were imported with FinalizeEdits in one call: nothing is left " +
                  "pending and the logic executes on the controller immediately." +
                  tagDisclosure +
                  " VERIFIED: the routine was re-exported after the import — every rung is " +
                  "normal, the imported rungs are at the requested position, and " +
                  "logix_pending_edits reports nothing pending."
                : identicalTextOnly
                ? $"NO CHANGE — IDENTICAL TEXT. {landedHeader}: " +
                  $"{Plural(unchangedReplacements!.Count, "replacement rung")} (at {string.Join(", ", unchangedReplacements)}) " +
                  "had the same text as the rung it replaces, so the controller kept the rung as it was. " +
                  "Nothing is pending and no edit zone was created; there is nothing to assemble or cancel." +
                  tagDisclosure
                : sendToController
                ? $"EDIT SENT TO CONTROLLER — ACCEPTED BUT NOT YET ASSEMBLED. " +
                  $"{landedHeader}, " +
                  DescribePublishContents(
                      AddRungs(publishRungsByRoutine, routineXPath, ownPublishRungs),
                      publishedTagEntries?.Count ?? 0,
                      publishReplaceTotal,
                      ownPublishRungs) + ". " +
                  "The edits were sent to the controller as accepted-but-not-assembled edits " +
                  "and are visible to any Logix Designer session that is online with the controller." +
                  tagDisclosure +
                  (verification == "unverified"
                      ? " NOT VERIFIED: the controller's pending state could not be read " +
                        "back after the publish, so it is unconfirmed that the rungs landed " +
                        "as expected. Check Logic → Online Edits in Studio 5000 to confirm " +
                        "the pending edits before assembling or accepting them."
                      : string.Empty) +
                  (unexpectedPendingRungs.HasValue
                      ? $" Note: {Plural(unexpectedPendingRungs.Value, "other pending rung")} in this " +
                        "routine, not part of this publish (possibly another session's edits)."
                      : string.Empty)
                : $"STAGED LOCALLY — NOT SENT TO CONTROLLER. " +
                  $"Routine '{routineXPath}', insert position {insertPosition}, " +
                  $"{DescribeReplacing(replaceCount)}. " +
                  "The rungs are held in the server's in-memory project image and any tag " +
                  "declarations have been stripped and held in the session staging buffer. " +
                  "Neither the rungs nor the tag declarations have reached the controller " +
                  "or any other session. Call logix_propose_rung_edit with " +
                  "target='controller' to publish the complete change set.";

            if (presetMismatches is not null)
                successMessage += " PresetMismatch: " + string.Join("; ", presetMismatches.Select(m =>
                    $"{m.Tag}.PRE is {m.Found}, the rung says {m.Expected}")) +
                    ". The rung text landed as expected; only the preset differs (see presetMismatches).";
            if (presetsUnread is not null)
                successMessage += " Preset not verified (could not read .PRE): " +
                    string.Join(", ", presetsUnread.Distinct(StringComparer.Ordinal)) + ".";

            // Undo block (live only): pre-image of what this call replaced/removed, plus the
            // texts it left at insertPosition so the undo call's stale check can detect
            // intervening changes. Carried in the response; nothing is stored server-side.
            UndoBlock? undoBlock = null;
            if (isLiveTarget)
            {
                int newRungCount = RoutineRungSelector.Select(SafeXml.ParseDocument(fragmentToImport), routineXPath).Count;
                var expectedAfter = (liveAfterRungs ?? Array.Empty<PendingEditRungEntry>())
                    .Skip((int)insertPosition).Take(newRungCount)
                    .Select(r => r.Text).ToList();
                undoBlock = new UndoBlock(
                    routineXPath,
                    insertPosition,
                    (uint)newRungCount,
                    liveUndoPreImage ?? [],
                    expectedAfter);
            }

            string? replaceCountNote = ignoredCallerReplaceCount.HasValue
                ? $"Caller's replaceCount ({ignoredCallerReplaceCount.Value}) was not forwarded " +
                  "to the SDK — the fragment carries no new rungs so replaceCount=0 was used. " +
                  "The staged image already contains the replacement from the prior " +
                  "target=\"workstation\" call."
                : null;
            if (stagedImage is not null && (callerInsertPosition != insertPosition || callerReplaceCount != replaceCount) &&
                !ignoredCallerInsertPosition.HasValue)
            {
                var visibleNote =
                    $"Position {callerInsertPosition} counts the rungs you will see after the publish; the session " +
                    $"image holds replaced originals, so the import was made at image position {insertPosition}" +
                    $"{(callerReplaceCount != replaceCount ? $" (replacing {replaceCount} image rung(s))" : string.Empty)}.";
                replaceCountNote = replaceCountNote is null ? visibleNote : replaceCountNote + " " + visibleNote;
            }
            // G52: one rule decides both the note and the ignoredInsertPosition field (null when the position was honoured).
            uint? reportedIgnoredInsertPosition =
                ignoredCallerInsertPosition.HasValue &&
                CallerInsertPositionIgnored(routineXPath, publishedRoutines, ignoredCallerInsertPosition.Value)
                    ? ignoredCallerInsertPosition
                    : null;
            if (reportedIgnoredInsertPosition.HasValue)
            {
                var landedSentence = DescribeLandedSentence(publishedRoutines);
                var positionNote =
                    $"Caller's insertPosition ({reportedIgnoredInsertPosition.Value}) was ignored — " +
                    (tailCommit is not null
                        ? $"the routine's staged tail was committed explicitly: " +
                          $"{Plural(tailCommit.NewRungs.Count, "new rung")} sent in order, replacing {Plural(tailCommit.ReplaceCount, "image rung")} " +
                          "(the new rungs and the originals they replace). "
                        : "the staged set was committed at a server-chosen position. ") +
                    (landedSentence ?? $"The landed indices could not be read back (committed from image position {insertPosition}).");
                replaceCountNote = replaceCountNote is null
                    ? positionNote
                    : replaceCountNote + " " + positionNote;
            }
            if (passedExcludingStagedNote is not null)
                replaceCountNote = replaceCountNote is null
                    ? passedExcludingStagedNote
                    : replaceCountNote + " " + passedExcludingStagedNote;
            if (buildVerify == "unavailable")
            {
                var unavailableNote = buildUnavailableReason ?? DesignerVersionInfo.BuildUnavailableNote;
                replaceCountNote = replaceCountNote is null
                    ? unavailableNote
                    : replaceCountNote + " " + unavailableNote;
            }

            LogCensusAudit("proposed", verification == "unverified" ? false : null);

            // verifyOnStage: the stage's own temp file is the last thing it creates; deleting it is the cleanup phase.
            IReadOnlyList<PhaseTiming>? stagePhaseTimings = null;
            if (stagePhases is not null)
            {
                stagePhases.Enter(StagePhases.Cleanup);
                if (File.Exists(tempFile))
                    try { File.Delete(tempFile); } catch { /* best-effort */ }
                stagePhaseTimings = stagePhases.Snapshot(includeSubPhases: true);
            }

            return new ProposeRungEditResult(
                Status:               isLiveTarget ? "live" : "proposed",
                RoutineXPath:         routineXPath,
                InsertPosition:       reportedInsertPosition,
                ReplaceCount:         (sendToController && !isLiveTarget) ? publishReplaceTotal : effectiveReplaceCount,
                Message:              successMessage,
                GateAnnotations:      BuildGateAnnotations(fragmentScan, policy),
                SafetyFlags:          BuildSafetyFlags(fragmentScan, policy),
                StagedTags:           stagedTagNames,
                TagsCreated:          publishedTagEntries,
                ContextTags:          contextTagNames,
                StagedRungs:          stagedRungCountAtPosition,
                FragmentRungs:        sendToController ? fragmentRungCount : (int?)null,
                DroppedDuplicates:    sendToController ? droppedDuplicateRungCount : (int?)null,
                StagedReplaceCount:   ignoredCallerReplaceCount,
                Note:                 replaceCountNote,
                VerifiedPendingRungs: verifiedPendingRungs,
                Verification:         verification,
                UnexpectedPendingRungs: unexpectedPendingRungs,
                CommentsPreserved:   commentsPreserved,
                Finalized:           isLiveTarget ? true : (bool?)null,
                AfterRungs:          isLiveTarget ? liveAfterRungs : null,
                Envelope:            envelopeSource,
                DeletedRungs:        isLiveDelete ? deletedRungs : null,
                Undo:                undoBlock,
                ZoneNote:            (sendToController && !isLiveTarget && !identicalTextOnly)
                                         ? BuildZoneNote(routineXPath)
                                         : null,
                ExternalAccessForced: externalAccessForced,
                BuildVerify:         buildVerify,
                DesignerVersion:     designerVersionText,
                IgnoredInsertPosition: reportedIgnoredInsertPosition,
                PublishedRoutines:   publishedRoutines,
                UnchangedReplacements: unchangedReplacements,
                PresetsVerified:     presetsVerified,
                PresetMismatches:    presetMismatches,
                PresetsUnread:       presetsUnread,
                StagedIn:            stagingToWorkstation ? StagedInSessionImage : null,
                Verified:            stageVerified,
                VerifiedNote:        stageVerifiedNote,
                Warnings:            referenceWarnings,
                Retries:             importRetryState.Retries > 0 ? importRetryState.Retries : (int?)null,
                CommentRestorableOnCancel: stagingToWorkstation && commentExplicit && commentUndoCaptured
                                         ? true : (bool?)null,
                PostImportBuildCheck: postImportBuildCheck,
                PhaseTimings:        stagePhaseTimings);
        }
        finally
        {
            // Delete the temporary file in all code paths — success, failure, or cancellation.
            // Best-effort: if the delete fails (e.g. file locked), do not propagate the error.
            if (File.Exists(tempFile))
                try { File.Delete(tempFile); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Builds the error for a build check that really failed (not the v36 unsupported-version case)
    /// after the import succeeded, reporting only what was observed.
    ///
    /// <para>
    /// Workstation (<c>LeaveEdits</c>): the edit lives only in this session's image, so
    /// <c>GoOfflineAsync</c> does discard it; the "nothing left pending" wording is used only after
    /// that call succeeded. Controller (<c>AcceptEdits</c>) and live (<c>FinalizeEdits</c>): the edit
    /// is already on the controller and going offline cannot remove it, so no GoOffline is made; the
    /// routine's pending rungs are read back and returned as <c>BuildFailedEditPending</c>.
    /// </para>
    /// </summary>
    private static ProposeRungEditError ServerFaulted(TagServiceError e) =>
        new(e.Message, Hint: e.Hint, Kind: ErrorKinds.SdkServerFaulted, Session: ErrorKinds.SessionFaulted);

    private static async Task<ProposeRungEditError> BuildFailedAfterImportAsync(
        Exception           buildEx,
        string              routineXPath,
        uint                insertPosition,
        LogixProjectSession session,
        string              resolvedDestination,
        bool                sendToController,
        bool                isLiveTarget,
        bool                confirmedByUser,
        RungEditAuditScope  audit,
        CancellationToken   ct)
    {
        audit.Log(routineXPath, insertPosition, "build-failed", null,
            destination:     resolvedDestination,
            confirmedByUser: sendToController ? (bool?)confirmedByUser : null,
            buildVerify:     "failed",
            designerVersion: session.DesignerVersion?.ToString());

        var versionText = session.DesignerVersion?.ToString();

        if (!sendToController)
        {
            try
            {
                await session.ExecuteAsync(p => p.GoOfflineAsync(ct), ct).ConfigureAwait(false);
            }
            catch (Exception rollbackEx) when (rollbackEx is not OperationCanceledException)
            {
                // GoOffline failed: what is pending is unknown until read back. Say so.
                var observed = await ReadPendingAfterErrorAsync(routineXPath, session, ct).ConfigureAwait(false);
                return new ProposeRungEditError(
                    $"Rung build failed for '{routineXPath}': {buildEx.Message}  " +
                    $"Going offline to discard the workstation edit also failed: {rollbackEx.Message}",
                    Hint: "Fix the rung logic and call logix_propose_rung_edit again. " +
                          "The edit may still be present: see pendingAfterError for what was read back.",
                    PendingAfterError: observed,
                    DesignerVersion:   versionText);
            }

            return new ProposeRungEditError(
                $"Rung build failed for '{routineXPath}': {buildEx.Message}",
                Hint: "Fix the rung logic and call logix_propose_rung_edit again. " +
                      NothingPendingAfterGoOfflineText +
                      " Reconnect (logix_connect with sdkMode=\"online\") before re-proposing.",
                PendingAfterError: new PendingAfterError("none", Array.Empty<string>()),
                DesignerVersion:   versionText);
        }

        var pending = await ReadPendingAfterErrorAsync(routineXPath, session, ct).ConfigureAwait(false);
        string where = isLiveTarget
            ? "The rung was imported with FinalizeEdits, so it is not pending: it is live on the controller."
            : "The rung was imported with AcceptEdits and is on the controller; going offline cannot remove it, so the session was left as it is.";
        string observedText = pending.State switch
        {
            "pending" => $"Read-back found {pending.Rungs!.Count} pending rung(s): " +
                         string.Join(" | ", pending.Rungs),
            "none"    => "Read-back found no pending rungs in this routine.",
            _         => $"The pending edits could not be read back ({pending.ReadbackError}); their state is unknown.",
        };
        return new ProposeRungEditError(
            $"Rung build failed for '{routineXPath}': {buildEx.Message}  {where} {observedText}",
            Hint: isLiveTarget
                ? "Inspect the routine with logix_export_rungs and correct or undo the live change with another " +
                  "logix_propose_rung_edit call; a finalized change is not cancelled in Studio."
                : "Cancel in Studio (Logic → Online Edits → Cancel Accepted Program Edits) or call " +
                  "logix_cancel_pending_edits (needs LGX_ENABLE_STUDIO_AUTOMATION=1), then fix the rung logic and call logix_propose_rung_edit again.",
            Kind:              "BuildFailedEditPending",
            PendingAfterError: pending,
            DesignerVersion:   versionText);
    }

    /// <summary>
    /// Builds the <c>zoneNote</c> attached to every successful <c>target="controller"</c>
    /// result: a pending zone blocks further imports into the program until it is resolved,
    /// and a live change could instead have been undone with one SDK call.
    /// </summary>
    /// <param name="routineXPath">Routine XPath; the program name is extracted from it.</param>
    /// <returns>The zone note text naming the affected program.</returns>
    internal static string BuildZoneNote(string routineXPath)
    {
        var m = ProgramNamePattern.Match(routineXPath ?? string.Empty);
        string program = m.Success ? m.Groups[1].Value : "the program";
        return $"this pending zone blocks further imports into '{program}' until it is accepted " +
               "or cancelled in Studio (or with the Studio lifecycle tools when " +
               "LGX_ENABLE_STUDIO_AUTOMATION=1); a live change could instead be undone with one " +
               "SDK call (undo)";
    }

    // ── Fragment envelope validation ──────────────────────────────────────────

    /// <summary>
    /// Stable slug for the docs/ section that covers rung-edit fragment errors.
    /// Every <see cref="ProposeRungEditError"/> returned by the envelope validator
    /// and the SDK import rejection handlers carries this anchor so callers can
    /// locate the corresponding documentation without parsing the error message.
    /// </summary>
    private const string RungEditDocsAnchor = "propose-rung-edit-errors";

    /// <summary>
    /// Imperative first-move hint shared by both envelope validation errors and
    /// the SDK import rejection: directs the caller toward the canonical
    /// export→edit→import round-trip workflow.
    /// </summary>
    private const string RoundTripHint =
        "Use logix_export_rungs to export the current rungs at the target routine — " +
        "this gives you a valid L5X fragment in the exact envelope shape the SDK requires. " +
        "Edit the exported fragment, then call logix_propose_rung_edit with the modified text." +
        ProposeRungChangeSkillPointer;

    /// <summary>
    /// Called when exporting the target routine failed: a missing routine is the most common
    /// cause, so look for it and return the typed <c>RoutineNotFound</c> error when confirmed;
    /// otherwise return <paramref name="generic"/> unchanged.
    /// </summary>
    private static async Task<object> RoutineNotFoundOrAsync(
        string              routineXPath,
        LogixProjectSession session,
        CancellationToken   ct,
        ProposeRungEditError generic)
    {
        var missing = await CheckTargetRoutineExistsAsync(routineXPath, session, ct)
            .ConfigureAwait(false);
        return (object?)missing ?? generic;
    }

    /// <summary>Hint attached to every <c>RoutineNotFound</c> error.</summary>
    internal const string RoutineNotFoundHint =
        "Create the routine with logix_create_routine first (type RLL), then propose into it.";

    /// <summary>Expected routine XPath shape, quoted in every <c>InvalidRoutineXPath</c> error.</summary>
    internal const string ExpectedRoutineXPathShape =
        "Controller/Programs/Program[@Name='<program>']/Routines/Routine[@Name='<routine>']";

    /// <summary>
    /// Strict routine XPath shape: <c>[Controller/]Programs/Program[@Name='P']/Routines/Routine[@Name='R']</c>,
    /// single or double quotes, nothing before or after.
    /// </summary>
    private static readonly Regex RoutineXPathShape = new(
        @"^\s*(?:Controller/)?Programs/Program\[@Name\s*=\s*(?<q1>['""])(?<p>[^'""]+)\k<q1>\]" +
        @"/Routines/Routine\[@Name\s*=\s*(?<q2>['""])(?<r>[^'""]+)\k<q2>\]\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a routine XPath once into its (program, routine) names, accepting single or double
    /// quotes. No SDK call is made.
    /// </summary>
    /// <param name="routineXPath">The caller-supplied routine XPath.</param>
    /// <param name="program">The program name as written; empty when the parse fails.</param>
    /// <param name="routine">The routine name as written; empty when the parse fails.</param>
    /// <returns><c>true</c> when the XPath has the expected shape.</returns>
    internal static bool TryParseRoutineXPath(string? routineXPath, out string program, out string routine)
    {
        var m = RoutineXPathShape.Match(routineXPath ?? string.Empty);
        program = m.Success ? m.Groups["p"].Value : string.Empty;
        routine = m.Success ? m.Groups["r"].Value : string.Empty;
        return m.Success;
    }

    /// <summary>Builds the canonical (single-quoted) routine XPath for the given names.</summary>
    /// <param name="program">Program name, in the casing the export uses.</param>
    /// <param name="routine">Routine name, in the casing the export uses.</param>
    /// <returns>The routine XPath.</returns>
    internal static string BuildRoutineXPath(string program, string routine) =>
        $"Controller/Programs/Program[@Name='{program}']/Routines/Routine[@Name='{routine}']";

    /// <summary>Builds the <c>InvalidRoutineXPath</c> error for an XPath that does not parse.</summary>
    private static ProposeRungEditError InvalidRoutineXPathError(string? routineXPath) => new(
        $"routineXPath '{routineXPath}' is not a valid routine XPath. Expected shape: " +
        $"{ExpectedRoutineXPathShape} (single or double quotes). No import was attempted.",
        Hint:       "Copy the routineXPath from logix_create_routine or build it in the expected shape.",
        DocsAnchor: RungEditDocsAnchor,
        Kind:       "InvalidRoutineXPath");

    /// <summary>
    /// Reads the programs and their routine names from the session's export of
    /// <c>Controller/Programs</c>. Never fails open: when the export or its parse fails, the failure
    /// is returned as an error.
    /// </summary>
    /// <param name="session">The open session used for the export.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The (program, routines) list, or an error when the export or parse failed.</returns>
    internal static async Task<(List<(string Program, List<string> Routines)>? Programs, ProposeRungEditError? Error)>
        ReadProgramRoutinesAsync(LogixProjectSession session, CancellationToken ct)
    {
        const string programsXPath = "Controller/Programs";
        var tempFile = Path.GetTempFileName();
        try
        {
            await session.ExecuteAsync(
                project => project.PartialExportToXmlFileAsync(programsXPath, tempFile, ct),
                ct).ConfigureAwait(false);
            var content = await File.ReadAllTextAsync(tempFile, ct).ConfigureAwait(false);
            var doc = SafeXml.ParseDocument(content);
            var programs = doc.Descendants("Program")
                .Select(p => (Name: (string?)p.Attribute("Name"), Element: p))
                .Where(p => !string.IsNullOrEmpty(p.Name))
                .Select(p => (Program: p.Name!, Routines: p.Element.Descendants("Routine")
                    .Select(r => (string?)r.Attribute("Name"))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .ToList()))
                .ToList();
            return (programs, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = ex is TagServiceException tse ? tse.Error.Message : ex.Message;
            return (null, new ProposeRungEditError(
                $"Could not check that the routine exists: the export of '{programsXPath}' failed: {reason}. " +
                "No import was attempted.",
                Hint:       "Retry; if it keeps failing, check the session with logix_doctor.",
                DocsAnchor: RungEditDocsAnchor,
                Kind:       "RoutineCheckFailed"));
        }
        finally
        {
            if (File.Exists(tempFile))
                try { File.Delete(tempFile); } catch { /* ignored */ }
        }
    }

    /// <summary>
    /// Resolves the program and routine named by <paramref name="routineXPath"/> against the
    /// session's export, ignoring case, and returns them in the exported casing. Typed errors, all
    /// before any import: <c>InvalidRoutineXPath</c> (with the expected shape), <c>ProgramNotFound</c>
    /// (listing the programs), <c>RoutineNotFound</c> (listing the routines), and, when the export
    /// itself fails, that failure (never fails open).
    /// </summary>
    /// <param name="routineXPath">Routine XPath, single or double quotes.</param>
    /// <param name="session">The open session used for the export.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The canonical names and XPath on success; otherwise the error.</returns>
    internal static async Task<(string? Program, string? Routine, string? XPath, ProposeRungEditError? Error)>
        ResolveRoutineNamesAsync(string? routineXPath, LogixProjectSession session, CancellationToken ct)
    {
        if (!TryParseRoutineXPath(routineXPath, out var programName, out var routineName))
            return (null, null, null, InvalidRoutineXPathError(routineXPath));

        var (programs, readError) = await ReadProgramRoutinesAsync(session, ct).ConfigureAwait(false);
        if (readError is not null)
            return (null, null, null, readError);

        var program = programs!.FirstOrDefault(p =>
            string.Equals(p.Program, programName, StringComparison.OrdinalIgnoreCase));
        if (program.Program is null)
        {
            var programList = programs!.Count == 0
                ? "(none)" : string.Join(", ", programs.Select(p => p.Program));
            return (null, null, null, new ProposeRungEditError(
                $"Program '{programName}' does not exist in this project. " +
                $"Programs in this project: {programList}. No import was attempted.",
                Hint:       "Use one of the listed program names (case is ignored).",
                DocsAnchor: RungEditDocsAnchor,
                Kind:       "ProgramNotFound"));
        }

        var routine = program.Routines.FirstOrDefault(r =>
            string.Equals(r, routineName, StringComparison.OrdinalIgnoreCase));
        if (routine is null)
        {
            var listing = program.Routines.Count == 0 ? "(none)" : string.Join(", ", program.Routines);
            return (null, null, null, new ProposeRungEditError(
                $"Routine '{routineName}' does not exist in program '{program.Program}'. " +
                $"Routines in this program: {listing}. No import was attempted.",
                Hint:       RoutineNotFoundHint,
                DocsAnchor: RungEditDocsAnchor,
                Kind:       "RoutineNotFound"));
        }

        return (program.Program, routine, BuildRoutineXPath(program.Program, routine), null);
    }

    /// <summary>
    /// Confirms the routine named by <paramref name="routineXPath"/> exists, resolving the program and
    /// routine against the session's export ignoring case. Never fails open: when the XPath does not
    /// parse or the export fails, the corresponding typed error is returned.
    /// </summary>
    /// <param name="routineXPath">Routine XPath, e.g. <c>Controller/Programs/Program[@Name='P']/Routines/Routine[@Name='R']</c>.</param>
    /// <param name="session">The open session used for the export.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>null</c> when the routine exists; otherwise a <see cref="ProposeRungEditError"/> (Kind <c>InvalidRoutineXPath</c>, <c>ProgramNotFound</c>, <c>RoutineNotFound</c> or <c>RoutineCheckFailed</c>).</returns>
    internal static async Task<ProposeRungEditError?> CheckTargetRoutineExistsAsync(
        string              routineXPath,
        LogixProjectSession session,
        CancellationToken   ct) =>
        (await ResolveRoutineNamesAsync(routineXPath, session, ct).ConfigureAwait(false)).Error;

    /// <summary>
    /// Exports <paramref name="xPath"/> from the session and returns the text, or <c>null</c> when the export
    /// failed (the pre-check then lets references it cannot settle through).
    /// </summary>
    private static async Task<string?> TryExportTextAsync(LogixProjectSession session, string xPath, CancellationToken ct)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await session.ExecuteAsync(
                project => project.PartialExportToXmlFileAsync(xPath, tempFile, ct), ct).ConfigureAwait(false);
            return await File.ReadAllTextAsync(tempFile, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (File.Exists(tempFile))
                try { File.Delete(tempFile); } catch { /* ignored */ }
        }
    }

    /// <summary>
    /// The tag and instruction pre-check, run on every stage and publish. Checks the rungs
    /// this call carries and, on a publish, the rungs already staged for the same routine, against the session's
    /// controller tags, the routine's program tags, other programs' tags (for <c>\Program.Tag</c>), the project's
    /// AOIs, the tags this call declares and the tags already staged. Nothing is refused that the export cannot
    /// settle: that comes back as a warning.
    /// </summary>
    /// <param name="routineXPath">The canonical routine XPath.</param>
    /// <param name="fragment">The effective import fragment (typed rungs and tags already built into it).</param>
    /// <param name="sendToController">Whether this call is a publish.</param>
    /// <param name="census">Controller and program tag names from the loaded project, when known.</param>
    /// <param name="session">The open session used for the exports.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="verifyOnStage">
    /// When <c>true</c>, a reference to a program that does not exist is refused with kind <c>UnknownProgram</c>;
    /// otherwise it is only a warning.
    /// </param>
    /// <returns>An <c>UndefinedTag</c> / <c>UnknownProgram</c> error, or warnings (possibly none).</returns>
    internal static async Task<(ProposeRungEditError? Error, IReadOnlyList<string>? Warnings)> CheckReferencesAsync(
        string                       routineXPath,
        string                       fragment,
        bool                         sendToController,
        IReadOnlyCollection<string>? census,
        LogixProjectSession          session,
        CancellationToken            ct,
        bool                         verifyOnStage = false)
    {
        var rungTexts = MnemonicSetChecker.RungTextsFromFragment(fragment, routineXPath).ToList();
        if (sendToController)
            foreach (var (key, texts) in session.StagedRungBuffer)
                if (string.Equals(key.Item1, routineXPath, StringComparison.OrdinalIgnoreCase))
                    rungTexts.AddRange(texts);
        if (rungTexts.Count == 0 || !TryParseRoutineXPath(routineXPath, out var program, out _))
            return (null, null);

        var warnings = new List<string>();

        var tagsXml = await TryExportTextAsync(session, "Controller/Tags", ct).ConfigureAwait(false);
        var controllerTags = tagsXml is not null ? RungReferenceChecker.ControllerTagsFromExport(tagsXml) : null;
        if (census is not null)
        {
            controllerTags ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in census) controllerTags.Add(n);
        }

        var programsXml = await TryExportTextAsync(session, "Controller/Programs", ct).ConfigureAwait(false);
        var programTags = programsXml is not null ? RungReferenceChecker.ProgramTagsFromExport(programsXml) : null;
        IReadOnlySet<string>? targetProgramTags = null;
        if (programTags is not null && programTags.TryGetValue(program, out var found))
            targetProgramTags = found;

        var localProgramTags = programsXml is not null ? RungReferenceChecker.LocalProgramTagsFromExport(programsXml) : null;
        var outputParameters = programsXml is not null ? RungReferenceChecker.OutputParametersFromExport(programsXml) : null;
        var programNames = programsXml is not null ? RungReferenceChecker.ProgramNamesFromExport(programsXml) : null;
        var programTagTypes = programsXml is not null ? RungReferenceChecker.ProgramTagTypesFromExport(programsXml) : null;
        var controllerTagTypes = tagsXml is not null ? RungReferenceChecker.ControllerTagTypesFromExport(tagsXml) : null;
        // The data types are exported only when some tag has a type whose members are not already known
        // (a user type); the predefined TIMER, COUNTER, CONTROL and STRING need no export.
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? typeMembers = null;
        if (RungReferenceChecker.NeedsDataTypes(controllerTagTypes, programTagTypes))
        {
            var typesXml = await TryExportTextAsync(session, "Controller/DataTypes", ct).ConfigureAwait(false);
            typeMembers = typesXml is not null ? RungReferenceChecker.DataTypeMembersFromExport(typesXml) : null;
        }

        var aoiNames = await TryExportTextAsync(session, "Controller/AddOnInstructionDefinitions", ct).ConfigureAwait(false) is { } aoiXml
            ? RungReferenceChecker.AoiNamesFromExport(aoiXml) : null;

        var declared = RungReferenceChecker.DeclaredTagsInFragment(fragment);
        foreach (var name in session.StagedTagBuffer.Keys) declared.Add(name);

        var result = RungReferenceChecker.Check(
            rungTexts,
            new ReferenceSymbols(program, controllerTags, targetProgramTags, programTags, declared, aoiNames,
                localProgramTags, controllerTagTypes, programTagTypes, typeMembers, outputParameters, programNames));

        if (result.Refused)
            return (new ProposeRungEditError(
                result.Message!,
                Hint: result.Hint is not null
                    ? (result.Kind == ReferenceCheckKinds.UndefinedTag
                        ? "Declare the tag in this call's tags list, or fix the name; " + result.Hint + "."
                        : result.Hint)
                    : result.Kind == ReferenceCheckKinds.UndefinedTag
                    ? "Declare the tag in this call's tags list (or create it first), or fix the name. Module I/O, " +
                      "\\Program.Tag, array and bit paths and aliases are accepted when they exist."
                    : "Use a Logix instruction name, or define the Add-On Instruction in the project first.",
                DocsAnchor: RungEditDocsAnchor,
                Kind: result.Kind,
                Rungs: null,
                RejectReasons: PreCheckRejectReasons(result)), null);

        if (verifyOnStage && result.UnknownPrograms is { Count: > 0 } unknownPrograms)
            return (new ProposeRungEditError(
                string.Join(" ", unknownPrograms.Select(RungReferenceChecker.DescribeUnknownProgram)) +
                " verifyOnStage refused the rung; nothing was imported or staged.",
                Hint: "Correct the program name in the \\Program.Tag operand, or create that program first. " +
                      "Pass verifyOnStage=false to stage the rung anyway; Logix rejects it at publish.",
                DocsAnchor: RungEditDocsAnchor,
                Kind: ReferenceCheckKinds.UnknownProgram,
                Rungs: null,
                RejectReasons: unknownPrograms
                    .Select(f => new RejectReason(f.RungIndex, RungReferenceChecker.DescribeUnknownProgram(f),
                        Kind: ReferenceCheckKinds.UnknownProgram))
                    .ToList()), null);

        warnings.AddRange(result.Warnings);
        return (null, warnings.Count > 0 ? warnings : null);
    }

    /// <summary>
    /// Maps every finding of a refused pre-check to a <see cref="RejectReason"/>, so all categories found
    /// (undefined tag, non-Public tag, undefined member) come back together in one <c>rejectReasons</c> list
    /// rather than only the first category (N7).
    /// </summary>
    internal static IReadOnlyList<RejectReason> PreCheckRejectReasons(ReferenceCheckResult result) =>
        result.Findings
            .Select(f => new RejectReason(f.RungIndex, RungReferenceChecker.DescribeFinding(f),
                Mnemonic: f.Kind == ReferenceCheckKinds.CrossProgramOutputNotWritable
                    ? ReferenceCheckKinds.DirectWriteOutputMnemonic : null,
                Kind: f.Kind))
            .ToList();

    /// <summary>
    /// Exports the routine at <paramref name="routineXPath"/> via
    /// <see cref="ILogixProject.PartialExportToXmlFileAsync"/>, removes all
    /// <c>&lt;Rung&gt;</c> child elements from every <c>&lt;RLLContent&gt;</c>
    /// element in the resulting document, and returns the stripped XML as a
    /// <see cref="string"/> (or a <see cref="ProposeRungEditError"/> on failure).
    ///
    /// <para>
    /// Used exclusively by the empty-fragment controller publish path (an earlier change
    /// requirement 1 / an earlier change): when <c>target="controller"</c> is called
    /// with no <c>l5xFragment</c>, the SDK export provides the envelope so the
    /// SDK import call inserts nothing new while still committing whatever rungs are
    /// already pending from prior <c>target="workstation"</c> calls, and so
    /// <see cref="MergeBufferedTagsIntoFragment"/> still has a well-formed
    /// <c>&lt;Controller&gt;</c> element to merge buffered tags into.
    /// Using the real export (rather than a hand-built skeleton) guarantees the
    /// envelope carries all SDK-required attributes (<c>SchemaRevision</c>,
    /// <c>SoftwareRevision</c>, <c>TargetName</c>, <c>TargetType</c>,
    /// <c>TargetSubType</c>, <c>ContainsContext</c>, and <c>Use</c> markers).
    /// </para>
    /// </summary>
    /// <param name="routineXPath">
    /// SDK XPath identifying the routine to export, e.g.
    /// <c>Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']</c>.
    /// </param>
    /// <param name="session">The open <see cref="LogixProjectSession"/> to use for the export.</param>
    /// <param name="ct">Cancellation token forwarded to the SDK call and file reads.</param>
    /// <returns>
    /// The stripped L5X envelope as a <see cref="string"/> on success, or a
    /// <see cref="ProposeRungEditError"/> on failure.  Never throws.
    /// </returns>
    private static async Task<object> ExportAndStripRungsAsync(
        string              routineXPath,
        LogixProjectSession session,
        CancellationToken   ct)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            try
            {
                await session.ExecuteAsync(
                    project => project.PartialExportToXmlFileAsync(routineXPath, tempFile, ct),
                    ct).ConfigureAwait(false);
            }
            catch (TagServiceException ex)
            {
                return await RoutineNotFoundOrAsync(routineXPath, session, ct, new ProposeRungEditError(
                    $"Could not export routine '{routineXPath}' for empty-publish envelope: {ex.Error.Message}",
                    Hint:       RoundTripHint,
                    DocsAnchor: RungEditDocsAnchor)).ConfigureAwait(false);
            }
            catch (OperationFailedException ex)
            {
                return await RoutineNotFoundOrAsync(routineXPath, session, ct, new ProposeRungEditError(
                    $"Could not export routine '{routineXPath}' for empty-publish envelope: {ex.Message}",
                    Hint:       RoundTripHint,
                    DocsAnchor: RungEditDocsAnchor)).ConfigureAwait(false);
            }
            catch (OperationNotPerformedException ex)
            {
                return await RoutineNotFoundOrAsync(routineXPath, session, ct, new ProposeRungEditError(
                    $"Export of routine '{routineXPath}' was not performed: {ex.Message}",
                    Hint:       RoundTripHint,
                    DocsAnchor: RungEditDocsAnchor)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await RoutineNotFoundOrAsync(routineXPath, session, ct, new ProposeRungEditError(
                    $"Unexpected error exporting routine '{routineXPath}' for empty-publish envelope: {ex.Message}",
                    Hint:       RoundTripHint,
                    DocsAnchor: RungEditDocsAnchor)).ConfigureAwait(false);
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(tempFile, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new ProposeRungEditError(
                    $"Failed to read exported content for empty-publish envelope: {ex.Message}",
                    Hint:       RoundTripHint,
                    DocsAnchor: RungEditDocsAnchor);
            }

            return StripTargetRoutineRungs(content);
        }
        finally
        {
            if (File.Exists(tempFile))
                try { File.Delete(tempFile); } catch { /* ignored */ }
        }
    }

    /// <summary>
    /// Removes the <c>&lt;Rung&gt;</c> children of the <c>&lt;RLLContent&gt;</c> under the
    /// <c>&lt;Routine Use="Target"&gt;</c> element of a routine export, so the envelope carries
    /// no rung content of its own — only the accumulated staged buffer (from prior
    /// <c>target="workstation"</c> calls) lands in the controller's pending-edit zone via
    /// AcceptEdits. Context definitions (for example an AOI's <c>Logic</c> routine) are left
    /// exactly as exported: stripping them makes the online import present a changed AOI
    /// (<c>XMLSrv_E_ONLINE_OVERWRITE_UDI</c>).
    /// </summary>
    /// <param name="content">The exported L5X text.</param>
    /// <returns>The serialized L5X with only the target routine's rungs removed.</returns>
    internal static string StripTargetRoutineRungs(string content)
    {
        var doc = SafeXml.ParseDocument(content);
        foreach (var routine in doc.Descendants("Routine")
                     .Where(r => (string?)r.Attribute("Use") == "Target"))
            EnsureRllContent(routine)?.Elements("Rung").Remove();

        return SerializeWithDeclaration(doc);
    }

    /// <summary>
    /// Shared self-closed-routine handling for every import-envelope builder.
    /// An export of an empty routine can come back as the self-closed shape
    /// <c>&lt;Routine Use="Target" Name="X" Type="RLL"/&gt;</c>, which has no
    /// <c>&lt;RLLContent&gt;</c> to write rungs into (and imports as a silent 0-rung success).
    /// This opens the element into a full one by adding an empty <c>&lt;RLLContent/&gt;</c>.
    /// </summary>
    /// <param name="routine">A <c>Routine</c> element of an export envelope.</param>
    /// <returns>
    /// The routine's <c>RLLContent</c> (existing or newly added), or <c>null</c> when the routine
    /// is not an RLL routine (only RLL routines carry rungs).
    /// </returns>
    internal static XElement? EnsureRllContent(XElement routine)
    {
        if (routine.Element("RLLContent") is { } existing)
            return existing;

        var type = (string?)routine.Attribute("Type");
        if (type is not null && !string.Equals(type, "RLL", StringComparison.OrdinalIgnoreCase))
            return null;

        var rll = new XElement("RLLContent");
        routine.Add(rll);
        return rll;
    }

    /// <summary>
    /// Pairs each replacement rung with the original it displaces and returns those whose text is identical
    /// (logix-mcp-am0x.4). Logix keeps such a rung as normal (<c>N</c>), not pending (<c>I</c>).
    /// </summary>
    /// <param name="replacementTexts">Normalised texts of the replacement rungs, in order.</param>
    /// <param name="routineTexts">Normalised texts of ALL the routine's rungs before the import.</param>
    /// <param name="insertPosition">Zero-based index of the first displaced original.</param>
    /// <param name="replaceCount">How many originals the replacement displaces.</param>
    /// <returns>The zero-based rung index and text of every same-text replacement.</returns>
    internal static List<(int Index, string Text)> FindUnchangedReplacements(
        IReadOnlyList<string> replacementTexts,
        IReadOnlyList<string> routineTexts,
        uint                  insertPosition,
        uint                  replaceCount)
    {
        var result = new List<(int, string)>();
        for (int i = 0; i < replaceCount && i < replacementTexts.Count; i++)
        {
            int idx = (int)insertPosition + i;
            if (idx < routineTexts.Count && RungTextComparer.Matches(routineTexts[idx], replacementTexts[i]))
                result.Add((idx, replacementTexts[i]));
        }
        return result;
    }

    /// <summary>
    /// Census check that accepts same-text replacements as <c>N</c> (logix-mcp-am0x.4): every expected text not
    /// found among the pending rungs is matched against the remaining same-text replacements (each used once);
    /// what is still unmatched is a genuine mismatch.
    /// </summary>
    /// <param name="expectedTexts">The texts the publish should leave pending.</param>
    /// <param name="actualPendingTexts">The I/IR rung texts the controller export shows.</param>
    /// <param name="unchanged">The routine's same-text replacements (index, text).</param>
    /// <returns>The expected texts still missing, and the indexes of the same-text replacements that were accepted.</returns>
    internal static (List<string> Missing, List<int> Accepted) ApplyUnchangedReplacements(
        IReadOnlyList<string>              expectedTexts,
        IReadOnlyList<string>              actualPendingTexts,
        IReadOnlyList<(int Index, string Text)> unchanged)
    {
        var remaining = unchanged.ToList();
        var missing   = new List<string>();
        var accepted  = new List<int>();
        foreach (var expected in expectedTexts)
        {
            if (RungTextComparer.ContainsMatch(actualPendingTexts, expected))
                continue;
            int hit = remaining.FindIndex(u => RungTextComparer.Matches(u.Text, expected));
            if (hit < 0)
            {
                missing.Add(expected);
                continue;
            }
            accepted.Add(remaining[hit].Index);
            remaining.RemoveAt(hit);
        }
        return (missing, accepted);
    }

    /// <summary>
    /// Exports the routine and returns the normalised text of every rung (any type), or <c>null</c> when the
    /// export or parse fails. Never throws (cancellation excepted).
    /// </summary>
    private static async System.Threading.Tasks.Task<IReadOnlyList<string>?> ReadAllRungTextsAsync(
        string              routineXPath,
        LogixProjectSession session,
        CancellationToken   ct)
    {
        try
        {
            var export = await ExportRungsHandler.ExecuteAsync(routineXPath, session, ct).ConfigureAwait(false);
            if (export is not ExportRungsResult ok)
                return null;
            return RoutineRungSelector.Select(SafeXml.ParseDocument(ok.L5X), routineXPath)
                .Select(r => NormalizeRungText(r.Element("Text")?.Value))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fails an import that should have added rungs but landed none (kind
    /// <c>ImportAddedNoRungs</c>). The SDK reports success for an import whose envelope carried
    /// no <c>RLLContent</c> to import, so success alone does not prove rungs landed.
    /// </summary>
    /// <param name="routineXPath">The routine that was imported into.</param>
    /// <param name="expectedTexts">Texts of the rungs the import was expected to add.</param>
    /// <param name="landedCount">How many of the expected rungs were found in the routine afterwards.</param>
    /// <param name="foundTexts">Rung texts found in the post-import read (for the report).</param>
    /// <returns>An <c>ImportAddedNoRungs</c> mismatch, or <c>null</c> when the expected rungs landed (or none were expected).</returns>
    internal static ProposeRungEditMismatch? CheckImportAddedRungs(
        string                routineXPath,
        IReadOnlyList<string> expectedTexts,
        int                   landedCount,
        IReadOnlyList<string> foundTexts)
    {
        if (expectedTexts.Count == 0 || landedCount > 0)
            return null;

        return new ProposeRungEditMismatch(
            Error:
                $"ImportAddedNoRungs: the import into '{routineXPath}' returned without error, " +
                $"but none of the {expectedTexts.Count} expected rung(s) are in the routine " +
                "afterwards. The SDK reports success for an import that carried no rungs " +
                "(for example an envelope built from a self-closed routine). Nothing was added; " +
                "check the routine with logix_export_rungs / logix_pending_edits before retrying.",
            Kind:          "ImportAddedNoRungs",
            ExpectedRungs: expectedTexts,
            PendingRungs:  foundTexts);
    }

    // ── Post-publish pending census helpers ───────────────────────────

    /// <summary>
    /// Exports the routine at <paramref name="routineXPath"/> and returns the
    /// normalised <c>&lt;Text&gt;</c> content of every <c>I</c>/<c>IR</c>-type
    /// (pending-insert / pending-insert-replace) rung found in the export.
    ///
    /// <para>
    /// Used exclusively by the post-publish pending census verification step in
    /// <see cref="ExecuteAsync"/>: after an <c>AcceptEdits</c> SDK call the export
    /// is read to verify that staged rungs appear as I/IR pending rungs, as the
    /// controller should reflect.
    /// </para>
    /// </summary>
    /// <param name="routineXPath">The XPath of the target routine.</param>
    /// <param name="session">The open SDK session to use for the export.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>(true, pendingTexts)</c> when the export and parse succeeded;
    /// <c>(false, empty)</c> when the export failed or the document could not be
    /// parsed — in which case the census check is skipped rather than treated as a
    /// hard mismatch (a transient SDK error must not block every publish).
    /// </returns>
    private static async System.Threading.Tasks.Task<(bool IsReadable, IReadOnlyList<string> PendingTexts)>
        ReadPendingRungTextsAsync(
            string              routineXPath,
            LogixProjectSession session,
            CancellationToken   ct)
    {
        var (readable, texts, _) = await ReadPendingRungTextsWithErrorAsync(routineXPath, session, ct)
            .ConfigureAwait(false);
        return (readable, texts);
    }

    /// <summary>
    /// Picks the commit position for a controller publish whose outgoing fragment carries no new
    /// rungs (corrected by an earlier change): the end of the routine as the
    /// <em>controller</em> sees it, i.e. the routine's session-image rung count minus the rungs this
    /// session staged into that image for the same routine. Staged inserts (target="workstation")
    /// are image-only rungs the controller does not have, so counting them put the position past the
    /// controller's end (6 controller + 7 staged gave 13). Replaced originals are still controller
    /// rungs and stay in the count.
    /// </summary>
    /// <param name="imageRungCount">Number of rungs in the routine's session image.</param>
    /// <param name="stagedRungCount">Number of rungs staged into that routine's image by this session.</param>
    /// <returns>The zero-based controller end-of-routine insert position (never negative).</returns>
    internal static uint ChooseNoFragmentCommitPosition(int imageRungCount, int stagedRungCount = 0) =>
        (uint)Math.Max(0, imageRungCount - Math.Max(0, stagedRungCount));

    /// <summary>
    /// The explicit commit of a routine's staged tail (G32).
    /// </summary>
    /// <param name="Position">Image index of the first rung of the tail.</param>
    /// <param name="ReplaceCount">Image rungs the import replaces: every tail rung, new and hidden original alike.</param>
    /// <param name="NewRungs">The visible staged rungs (<c>er</c>, <c>e</c>) of the tail, in image order, as they are sent.</param>
    internal sealed record StagedTailCommit(uint Position, uint ReplaceCount, IReadOnlyList<XElement> NewRungs);

    private static bool IsNewStagedState(string state) => state is "er" or "e";

    /// <summary>
    /// True when the image ends in a run of staged rungs that holds a replaced or deleted original AND a new rung
    /// (<c>..., er, rN</c>): the case where the controller-end commit position lands inside the run and the empty
    /// commit import drops the new rung. A run of staged inserts only, or of deletions only, is committed as before.
    /// </summary>
    /// <param name="image">The session's staged routine image.</param>
    /// <returns><c>true</c> when the tail needs an explicit import.</returns>
    internal static bool StagedTailNeedsExplicitCommit(IReadOnlyList<StagedImageRung> image)
    {
        int start = image.Count;
        while (start > 0 && (IsHiddenState(image[start - 1].State) || IsNewStagedState(image[start - 1].State)))
            start--;
        var tail = image.Skip(start).ToList();
        return tail.Any(r => IsHiddenState(r.State)) && tail.Any(r => IsNewStagedState(r.State));
    }

    /// <summary>
    /// Builds the explicit import for the staged tail of a routine image: the visible staged rungs (<c>er</c>, <c>e</c>)
    /// in order, replacing the whole tail (so <c>rN</c>/<c>dN</c> originals are removed). Each new rung must be one this
    /// session staged (its text is in <paramref name="stagedTexts"/>); before anything is sent the number of new rungs in
    /// the import set is compared with the number in the staged image and a shortfall is refused as
    /// <c>StagedImportIncomplete</c>.
    /// </summary>
    /// <param name="imageRungs">The routine's session-image <c>Rung</c> elements, in order.</param>
    /// <param name="stagedTexts">Normalised texts of the rungs this session staged into the routine.</param>
    /// <param name="routineXPath">The routine being published.</param>
    /// <param name="plan">The commit to send; <c>null</c> when the image has no tail needing one or on refusal.</param>
    /// <returns>The typed refusal, or <c>null</c> when the plan is complete (or not needed).</returns>
    internal static ProposeRungEditError? PlanStagedTailCommit(
        IReadOnlyList<XElement> imageRungs,
        IReadOnlyList<string>   stagedTexts,
        string                  routineXPath,
        out StagedTailCommit?   plan)
    {
        plan = null;
        var image = imageRungs
            .Select((r, i) => new StagedImageRung(
                i, NormalizeRungText(r.Element("Text")?.Value), (string?)r.Attribute("Type") ?? "N"))
            .ToList();
        if (!StagedTailNeedsExplicitCommit(image))
            return null;

        int start = image.Count;
        while (start > 0 && (IsHiddenState(image[start - 1].State) || IsNewStagedState(image[start - 1].State)))
            start--;

        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in stagedTexts)
        {
            var key = NormalizeRungText(t);
            remaining[key] = remaining.GetValueOrDefault(key) + 1;
        }

        int imageNew = 0;
        var send = new List<XElement>();
        for (int i = start; i < image.Count; i++)
        {
            if (!IsNewStagedState(image[i].State)) continue;
            imageNew++;
            if (remaining.GetValueOrDefault(image[i].Text) <= 0) continue;
            remaining[image[i].Text]--;
            var copy = new XElement(imageRungs[i]);
            copy.SetAttributeValue("Type", "N");
            copy.SetAttributeValue("Number", send.Count);
            send.Add(copy);
        }

        if (send.Count != imageNew)
        {
            return new ProposeRungEditError(
                $"The staged publish was refused: the routine '{routineXPath}' holds {Plural(imageNew, "new staged rung")} " +
                $"in its session image, but the import set carries {send.Count}. Sending it would leave a replaced original " +
                "deleted with no new rung. Nothing was sent.",
                Hint: "Run logix_pending_edits to compare the staged set with the image, then logix_clear_staged and stage the " +
                      "change again.",
                Kind: "StagedImportIncomplete",
                StagedImage: image);
        }

        plan = new StagedTailCommit((uint)start, (uint)(image.Count - start), send);
        return null;
    }

    /// <summary>Adds <paramref name="rungs"/> to the fragment's <c>RLLContent</c> (an empty commit fragment has none).</summary>
    /// <param name="fragment">The fragment XML to import.</param>
    /// <param name="rungs">The rungs to append in order.</param>
    /// <returns>The fragment XML with an XML declaration and the rungs in its <c>RLLContent</c>.</returns>
    internal static string InjectRungsIntoFragment(string fragment, IReadOnlyList<XElement> rungs)
    {
        var doc     = SafeXml.ParseDocument(fragment);
        var content = doc.Descendants("RLLContent").First();
        foreach (var r in rungs)
            content.Add(new XElement(r));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
               doc.Root!.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Reads the routine's session-image <c>Rung</c> elements (one export); not readable on any failure.</summary>
    private static async System.Threading.Tasks.Task<(bool IsReadable, IReadOnlyList<XElement>? Rungs)>
        ReadImageRungElementsAsync(string routineXPath, LogixProjectSession session, CancellationToken ct)
    {
        try
        {
            var exportResult = await ExportRungsHandler.ExecuteAsync(routineXPath, session, ct).ConfigureAwait(false);
            if (exportResult is not ExportRungsResult exported) return (false, null);
            return (true, RoutineRungSelector.Select(SafeXml.ParseDocument(exported.L5X), routineXPath).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, null);
        }
    }

    /// <summary>
    /// Totals the rungs staged into <paramref name="routineXPath"/>'s session image, summed over
    /// every position bucket of that routine only (other routines staged in the same set are ignored).
    /// </summary>
    /// <param name="buffer">The session's staged rung buffer, keyed by (routine, position).</param>
    /// <param name="routineXPath">The routine being published.</param>
    /// <returns>The number of staged rungs for that routine; 0 when none.</returns>
    internal static int CountStagedRungsForRoutine(
        IReadOnlyDictionary<(string RoutineXPath, uint InsertPosition), List<string>> buffer,
        string routineXPath) =>
        buffer.Where(kv => string.Equals(kv.Key.RoutineXPath, routineXPath, StringComparison.OrdinalIgnoreCase))
              .Sum(kv => kv.Value.Count);

    /// <summary>
    /// A staged bucket that a new stage (or another bucket) collides with.
    /// </summary>
    /// <param name="RoutineXPath">The bucket's routine.</param>
    /// <param name="Position">The bucket's insert position <c>p</c> (controller-rung coordinates).</param>
    /// <param name="RungCount">The number of rungs <c>n</c> staged in the bucket.</param>
    /// <param name="ReplaceCount">The bucket's replace count <c>rc</c>.</param>
    internal sealed record StagedBucketRef(string RoutineXPath, uint Position, int RungCount, uint ReplaceCount)
    {
        /// <summary>Exclusive end of the occupied range: <c>p + n + rc</c>. Also the first safe insert position.</summary>
        public long End => (long)Position + RungCount + ReplaceCount;

        /// <summary>The occupied range as <c>"p..end"</c> (inclusive), e.g. <c>"0..1"</c>.</summary>
        public string Range => $"{Position}..{Math.Max(Position, End - 1)}";

        /// <summary>Human-readable description naming the bucket.</summary>
        public string Describe() =>
            $"bucket (routine '{RoutineXPath}', position {Position}, {Plural(RungCount, "staged rung")}, replaceCount {ReplaceCount}, range {Range})";
    }

    /// <summary>
    /// True when a stage at <paramref name="position"/> replacing <paramref name="replaceCount"/> rungs collides with
    /// the occupied range <c>[p, p+n+rc)</c> of <paramref name="bucket"/>: its insert position falls in the range, or
    /// its own replace range overlaps it.
    /// </summary>
    private static bool CollidesWith(StagedBucketRef bucket, uint position, uint replaceCount)
    {
        if (bucket.End <= bucket.Position) return false;                    // empty bucket occupies nothing
        if (position >= bucket.Position && position < bucket.End) return true;
        return replaceCount > 0
            && position < bucket.End
            && (long)position + replaceCount > bucket.Position;
    }

    private static IEnumerable<StagedBucketRef> StagedBuckets(
        IReadOnlyDictionary<(string RoutineXPath, uint InsertPosition), List<string>> buffer,
        IReadOnlyDictionary<(string RoutineXPath, uint InsertPosition), uint>? replaceCounts)
    {
        foreach (var (key, texts) in buffer.OrderBy(kv => kv.Key.RoutineXPath, StringComparer.OrdinalIgnoreCase)
                                           .ThenBy(kv => kv.Key.InsertPosition))
        {
            uint rc = 0;
            replaceCounts?.TryGetValue(key, out rc);
            if (texts.Count == 0 && rc == 0) continue;
            yield return new StagedBucketRef(key.RoutineXPath, key.InsertPosition, texts.Count, rc);
        }
    }

    /// <summary>
    /// Finds the staged buckets in <paramref name="routineXPath"/> that a new stage at <paramref name="insertPosition"/>
    /// replacing <paramref name="replaceCount"/> rungs would collide with. A bucket at
    /// <c>p</c> with <c>n</c> rungs and replace count <c>rc</c> occupies <c>[p, p+n+rc)</c>: the new stage conflicts when
    /// its insert position falls in that range, or its own replace range overlaps it. Positions are controller-rung
    /// coordinates.
    /// </summary>
    /// <param name="buffer">The session's staged rung buffer, keyed by (routine, position).</param>
    /// <param name="replaceCounts">The replace count of each bucket; <c>null</c> or a missing key means 0.</param>
    /// <param name="routineXPath">The routine of the new stage.</param>
    /// <param name="insertPosition">The new stage's insert position.</param>
    /// <param name="replaceCount">The new stage's replace count.</param>
    /// <returns>The colliding buckets; empty when none.</returns>
    internal static IReadOnlyList<StagedBucketRef> FindStagedBucketConflicts(
        IReadOnlyDictionary<(string RoutineXPath, uint InsertPosition), List<string>> buffer,
        IReadOnlyDictionary<(string RoutineXPath, uint InsertPosition), uint>? replaceCounts,
        string routineXPath,
        uint insertPosition,
        uint replaceCount,
        int newRungCount = 0)
    {
        // One rule for stage and publish: when the new stage carries rungs it becomes a bucket of its own, and it
        // conflicts with an existing bucket exactly when the pair would conflict at publish
        // (BucketsCollide, the rule FindStagedBufferConflicts applies). A stage that adds no rungs (a delete) is
        // never recorded as a bucket, so only the one-directional range check applies to it.
        var proposed = newRungCount > 0
            ? new StagedBucketRef(routineXPath, insertPosition, newRungCount, replaceCount)
            : null;
        return StagedBuckets(buffer, replaceCounts)
            .Where(b => string.Equals(b.RoutineXPath, routineXPath, StringComparison.OrdinalIgnoreCase)
                        && (proposed is not null
                            ? BucketsCollide(b, proposed)
                            : CollidesWith(b, insertPosition, replaceCount)))
            .ToList();
    }

    /// <summary>
    /// The single bucket-pair conflict rule shared by stage and publish: two buckets in the same routine collide when
    /// either one's start position lies inside the other's occupied range, or either one's replace range reaches into
    /// the other's. Symmetric, so the order the buckets were staged in does not matter.
    /// </summary>
    private static bool BucketsCollide(StagedBucketRef a, StagedBucketRef b) =>
        CollidesWith(a, b.Position, b.ReplaceCount) || CollidesWith(b, a.Position, a.ReplaceCount);

    /// <summary>
    /// Finds every pair of staged buckets in the same routine whose ranges collide: the
    /// publish-time re-check that runs across all buckets before any import.
    /// </summary>
    /// <param name="buffer">The session's staged rung buffer, keyed by (routine, position).</param>
    /// <param name="replaceCounts">The replace count of each bucket; <c>null</c> or a missing key means 0.</param>
    /// <returns>Each conflicting pair; empty when the buffer is consistent.</returns>
    internal static IReadOnlyList<(StagedBucketRef First, StagedBucketRef Second)> FindStagedBufferConflicts(
        IReadOnlyDictionary<(string RoutineXPath, uint InsertPosition), List<string>> buffer,
        IReadOnlyDictionary<(string RoutineXPath, uint InsertPosition), uint>? replaceCounts)
    {
        var buckets = StagedBuckets(buffer, replaceCounts).ToList();
        var pairs   = new List<(StagedBucketRef, StagedBucketRef)>();
        for (int i = 0; i < buckets.Count; i++)
            for (int j = i + 1; j < buckets.Count; j++)
            {
                var a = buckets[i];
                var b = buckets[j];
                if (!string.Equals(a.RoutineXPath, b.RoutineXPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (BucketsCollide(a, b))
                    pairs.Add((a, b));
            }
        return pairs;
    }

    /// <summary>
    /// Finds the staged rung ranges in <paramref name="routineXPath"/> that contain
    /// <paramref name="insertPosition"/> (extended by an earlier change with replace counts).
    /// A bucket staged at position <c>p</c> with <c>n</c> rungs and replace count <c>rc</c> occupies the
    /// range <c>[p, p+n+rc)</c>; an insert position inside it lands on a staged rung or its replaced original.
    /// </summary>
    /// <param name="buffer">The session's staged rung buffer, keyed by (routine, position).</param>
    /// <param name="routineXPath">The publish call's routine.</param>
    /// <param name="insertPosition">The caller's insert position.</param>
    /// <param name="replaceCounts">The replace count of each bucket; <c>null</c> means every bucket has 0.</param>
    /// <returns>Display strings (e.g. <c>"0..2"</c>) of every conflicting staged range; empty when none.</returns>
    internal static IReadOnlyList<string> FindStagedPositionConflicts(
        IReadOnlyDictionary<(string RoutineXPath, uint InsertPosition), List<string>> buffer,
        string routineXPath,
        uint insertPosition,
        IReadOnlyDictionary<(string RoutineXPath, uint InsertPosition), uint>? replaceCounts = null) =>
        FindStagedBucketConflicts(buffer, replaceCounts, routineXPath, insertPosition, 0)
            .Select(b => b.Range)
            .ToList();

    /// <summary>
    /// Builds the <c>StagedPositionConflict</c> refusal for a new stage that collides with staged buckets.
    /// </summary>
    private static ProposeRungEditError StagedPositionConflictError(
        string routineXPath, uint insertPosition, int newRungCount, uint replaceCount,
        IReadOnlyList<StagedBucketRef> conflicts, string action,
        IReadOnlyList<StagedImageRung>? stagedImage = null)
    {
        var existing = string.Join("; ", conflicts.Select(c => DescribeBucketVisible(c, stagedImage)));
        long safe    = SafeVisiblePosition(conflicts.Max(c => c.End), stagedImage);
        var countNote = stagedImage is null
            ? string.Empty
            : " Positions count the rungs you will see after the publish (original rungs not replaced, plus staged " +
              "rungs); stagedImage lists the session image, where rungs in state rN or dN (R or D in a pending-zone image) are replaced or deleted originals that do not count.";
        return new ProposeRungEditError(
            $"The {action} was refused: the new stage (routine '{routineXPath}', position {insertPosition}, " +
            $"{newRungCount} rung(s), replaceCount {replaceCount}) conflicts with already staged {existing}. " +
            "Staging there would clobber a staged rung or the original it replaces. No import was attempted." +
            countNote,
            Hint:
                $"Use a position at or after the end of the staged bucket (a safe position is {safe}), or stage this " +
                "edit in one call, then publish with no fragment (target=\"controller\", no l5xFragment/rungs): " +
                "the server commits every staged rung. To start over, discard the staged rungs with logix_clear_staged " +
                "(it also removes them from the session image).",
            Kind: "StagedPositionConflict",
            StagedImage: stagedImage);
    }

    /// <summary>
    /// Rung states that are hidden once the edit is published: the original a replace displaced (<c>rN</c>, or <c>R</c>
    /// in a pending-zone image) or a delete removed (<c>dN</c>, or <c>D</c>). They stay in the session image but never
    /// count toward a position. The code list lives in <see cref="PendingEditsHandler.HiddenOriginalRungCodes"/>.
    /// </summary>
    private static bool IsHiddenState(string state) => PendingEditsHandler.IsHiddenOriginalCode(state);

    /// <summary>The number of rungs the user will see after the publish: every image rung that is not a replaced or deleted original.</summary>
    /// <param name="image">The session's staged routine image.</param>
    /// <returns>The visible rung count.</returns>
    internal static int VisibleRungCount(IReadOnlyList<StagedImageRung> image) =>
        image.Count(r => !IsHiddenState(r.State));

    /// <summary>
    /// Translates a position and replace count counted in visible rungs (original rungs not replaced, plus staged
    /// rungs) into the image coordinates the SDK import and the staged buckets use. Hidden replaced originals are
    /// skipped; a position at or past the visible end maps to the same distance past the image end, so an image
    /// without hidden rungs maps every position to itself.
    /// </summary>
    /// <param name="image">The session's staged routine image.</param>
    /// <param name="visiblePosition">The caller's insert position, in visible rungs.</param>
    /// <param name="visibleReplaceCount">The caller's replace count, in visible rungs.</param>
    /// <returns>The image insert position and the image replace count (rungs spanned, hidden ones between included).</returns>
    internal static (uint Position, uint ReplaceCount) ToImageCoordinates(
        IReadOnlyList<StagedImageRung> image, uint visiblePosition, uint visibleReplaceCount)
    {
        var visible = image.Where(r => !IsHiddenState(r.State)).Select(r => r.Index).ToList();
        int count   = visible.Count;
        if (visiblePosition >= count)
            return ((uint)image.Count + (visiblePosition - (uint)count), visibleReplaceCount);

        uint position = (uint)visible[(int)visiblePosition];
        if (visibleReplaceCount == 0)
            return (position, 0);

        long endVisible = (long)visiblePosition + visibleReplaceCount;
        int  lastInside = (int)Math.Min(endVisible, count) - 1;
        long span       = visible[lastInside] - position + 1 + Math.Max(0, endVisible - count);
        return (position, (uint)span);
    }

    /// <summary>
    /// A safe position, counted in visible rungs, for a staged range that ends at image index <paramref name="imageEnd"/>:
    /// the end of the visible routine, which is always safe. Without a readable image the image end is returned unchanged.
    /// </summary>
    private static long SafeVisiblePosition(long imageEnd, IReadOnlyList<StagedImageRung>? image)
    {
        if (image is null) return imageEnd;
        long endAsVisible = imageEnd - image.Count(r => r.Index < imageEnd && IsHiddenState(r.State));
        return Math.Max(VisibleRungCount(image), endAsVisible);
    }

    /// <summary>
    /// Describes a conflicting bucket in visible positions when the image is readable; otherwise in image positions.
    /// </summary>
    private static string DescribeBucketVisible(StagedBucketRef bucket, IReadOnlyList<StagedImageRung>? image)
    {
        if (image is null) return bucket.Describe();
        var inside = image.Where(r => r.Index >= bucket.Position && r.Index < bucket.End && !IsHiddenState(r.State)).ToList();
        if (inside.Count == 0) return bucket.Describe();
        int first = image.Count(r => r.Index < inside[0].Index && !IsHiddenState(r.State));
        return $"bucket (routine '{bucket.RoutineXPath}', position {first}, {Plural(bucket.RungCount, "staged rung")}, " +
               $"replaceCount {bucket.ReplaceCount}, range {first}..{first + inside.Count - 1})";
    }

    /// <summary>
    /// Reads the routine's session image (one export) as <c>[{index, text, state}]</c>. <c>null</c> when it cannot be read.
    /// </summary>
    private static async System.Threading.Tasks.Task<IReadOnlyList<StagedImageRung>?> ReadStagedImageAsync(
        string routineXPath, LogixProjectSession session, CancellationToken ct)
    {
        object exportResult;
        try
        {
            exportResult = await ExportRungsHandler.ExecuteAsync(routineXPath, session, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        if (exportResult is not ExportRungsResult exported) return null;

        try
        {
            return RoutineRungSelector.Select(SafeXml.ParseDocument(exported.L5X), routineXPath)
                .Select((r, i) => new StagedImageRung(
                    i, NormalizeRungText(r.Element("Text")?.Value), (string?)r.Attribute("Type") ?? "N"))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>true</c> when the session holds anything a controller publish would send: a staged rung bucket, a
    /// non-zero staged-rung counter, or a staged tag declaration.
    /// </summary>
    /// <param name="session">The open SDK session.</param>
    /// <returns><c>true</c> when at least one staged rung or staged tag exists.</returns>
    internal static bool SessionHoldsStagedWork(LogixProjectSession session) =>
        session.StagedRungCount > 0
        || session.StagedRungBuffer.Values.Any(b => b.Count > 0)
        || session.StagedTagBuffer.Count > 0;

    /// <summary>
    /// Reads the routine's current rung count from the session image (one export).
    /// Never throws (cancellation excepted).
    /// </summary>
    /// <param name="routineXPath">The routine to count.</param>
    /// <param name="session">The open SDK session.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>(true, count, null)</c> on success; <c>(false, 0, reason)</c> otherwise.</returns>
    private static async System.Threading.Tasks.Task<(bool IsReadable, int RungCount, string? Error)>
        ReadImageRungCountAsync(string routineXPath, LogixProjectSession session, CancellationToken ct)
    {
        object exportResult;
        try
        {
            exportResult = await ExportRungsHandler.ExecuteAsync(routineXPath, session, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, 0, ex.Message);
        }

        if (exportResult is not ExportRungsResult exportSuccess)
            return (false, 0,
                (exportResult as ExportRungsError)?.Error ?? "the routine export returned no result");

        try
        {
            var count = RoutineRungSelector.Select(SafeXml.ParseDocument(exportSuccess.L5X), routineXPath).Count;
            return (true, count, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, 0, ex.Message);
        }
    }

    /// <summary>
    /// Same read as <see cref="ReadPendingRungTextsAsync"/>, but also returns why the read failed.
    /// Never throws (cancellation excepted).
    /// </summary>
    /// <param name="routineXPath">The XPath of the target routine.</param>
    /// <param name="session">The open SDK session to use for the export.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>(true, pendingTexts, null)</c> on success; <c>(false, empty, reason)</c> when the export failed
    /// or the document could not be parsed.
    /// </returns>
    private static async System.Threading.Tasks.Task<(bool IsReadable, IReadOnlyList<string> PendingTexts, string? Error)>
        ReadPendingRungTextsWithErrorAsync(
            string              routineXPath,
            LogixProjectSession session,
            CancellationToken   ct)
    {
        var (readable, pending, error) = await ReadPendingRungsWithIndexAsync(routineXPath, session, ct)
            .ConfigureAwait(false);
        return (readable, pending.Select(p => p.Text).ToList(), error);
    }

    /// <summary>
    /// Same read as <see cref="ReadPendingRungTextsWithErrorAsync"/>, but each pending (I/IR) rung also carries its
    /// zero-based index among ALL of the routine's rungs in the export, so a caller can say where a rung landed.
    /// Never throws (cancellation excepted).
    /// </summary>
    /// <param name="routineXPath">The XPath of the target routine.</param>
    /// <param name="session">The open SDK session to use for the export.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>(true, pendingRungs, null)</c> on success; <c>(false, empty, reason)</c> when the export failed
    /// or the document could not be parsed.
    /// </returns>
    private static async System.Threading.Tasks.Task<(bool IsReadable, IReadOnlyList<(int Index, string Text, string? Comment)> Pending, string? Error)>
        ReadPendingRungsWithIndexAsync(
            string              routineXPath,
            LogixProjectSession session,
            CancellationToken   ct)
    {
        object exportResult;
        try
        {
            exportResult = await ExportRungsHandler.ExecuteAsync(routineXPath, session, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, Array.Empty<(int, string, string?)>(), ex.Message);
        }

        if (exportResult is not ExportRungsResult exportSuccess)
            return (false, Array.Empty<(int, string, string?)>(),
                (exportResult as ExportRungsError)?.Error ?? "the routine export returned no result");

        try
        {
            var doc = SafeXml.ParseDocument(exportSuccess.L5X);
            var pending = RoutineRungSelector.Select(doc, routineXPath)
                .Select((r, i) => (Index: i, Type: (string?)r.Attribute("Type") ?? "N", Rung: r))
                .Where(x => x.Type == "I" || x.Type == "IR")
                .Select(x => (x.Index, NormalizeRungText(x.Rung.Element("Text")?.Value),
                    x.Rung.Element("Comment") is { } c && !string.IsNullOrWhiteSpace(c.Value) ? c.Value : null))
                .ToList();

            return (true, pending, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, Array.Empty<(int, string, string?)>(), ex.Message);
        }
    }

    /// <summary>
    /// Reads back the routine's pending (I/IR) rungs after an error that followed a successful import,
    /// and reports exactly what was observed: the rungs, or <c>"unknown"</c> with the read-back error.
    /// It never reports <c>"none"</c> without a successful read.
    /// </summary>
    /// <param name="routineXPath">The routine that was imported into.</param>
    /// <param name="session">The open SDK session.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The observed state; never <c>null</c>.</returns>
    private static async System.Threading.Tasks.Task<PendingAfterError> ReadPendingAfterErrorAsync(
        string              routineXPath,
        LogixProjectSession session,
        CancellationToken   ct)
    {
        var (readable, texts, error) = await ReadPendingRungTextsWithErrorAsync(routineXPath, session, ct)
            .ConfigureAwait(false);
        if (!readable)
            return new PendingAfterError("unknown", null, error ?? "the read-back failed");
        return new PendingAfterError(texts.Count == 0 ? "none" : "pending", texts);
    }

    /// <summary>The text of an exception and everything it wraps, for matching SDK messages.</summary>
    private static string ExceptionText(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            sb.Append(e.Message).Append(' ');
            if (e is TagServiceException tse) sb.Append(tse.Error.Message).Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Wording used ONLY after <c>GoOfflineAsync</c> succeeded on the workstation (LeaveEdits) path,
    /// where going offline does discard the unaccepted edit. No other branch may say this.
    /// </summary>
    internal const string NothingPendingAfterGoOfflineText =
        "Nothing was left pending on the controller — the failed build caused the session to go " +
        "offline, discarding the workstation edit.";

    // ── Duplicate rung dedup helpers ──

    /// <summary>
    /// Normalises a rung's <c>&lt;Text&gt;</c> content for byte-equal duplicate
    /// comparison: unifies line endings and trims leading/trailing whitespace.
    /// A <c>null</c> input normalises to the empty string.
    /// </summary>
    /// <param name="text">The raw <c>&lt;Text&gt;</c> element value, or <c>null</c>.</param>
    /// <returns>The normalised text, suitable for ordinal equality comparison.</returns>
    internal static string NormalizeRungText(string? text) => RungTextComparer.Normalize(text);

    /// <summary>
    /// Removes every <c>&lt;Rung&gt;</c> element from <paramref name="effectiveFragment"/>'s
    /// <c>&lt;RLLContent&gt;</c> whose normalised <c>&lt;Text&gt;</c> content
    /// (see <see cref="NormalizeRungText"/>) is byte-equal to an entry already recorded
    /// in <paramref name="stagedRungBuffer"/> at <paramref name="insertPosition"/>.
    ///
    /// <para>
    /// This is the fix for the duplication observed in Tier D run 4: when a
    /// <c>target="controller"</c> publish supplies a non-empty fragment that re-sends
    /// rungs already staged with <c>target="workstation"</c> at the same insert position,
    /// those duplicate rungs are dropped here — before the fragment ever reaches the SDK
    /// — so each distinct rung is published only once. Rungs whose text does not match
    /// any staged entry at this position (new content) are left untouched.
    /// </para>
    /// </summary>
    /// <param name="effectiveFragment">The controller-publish fragment about to be sent.</param>
    /// <param name="routineXPath">
    /// The routine this publish targets. Only rungs staged in this routine are considered
    /// duplicates; a different routine staged at the same position is a separate list.
    /// </param>
    /// <param name="insertPosition">
    /// The insert position this publish targets — used to look up
    /// <paramref name="stagedRungBuffer"/> so only rungs staged at the SAME position are
    /// considered duplicates (per requirement 2: "the same position").
    /// </param>
    /// <param name="stagedRungBuffer">
    /// <see cref="LogixProjectSession.StagedRungBuffer"/> — normalised staged rung texts
    /// keyed by (routine XPath, insert position) they were staged at.
    /// </param>
    /// <param name="fragmentRungCount">
    /// Set to the total number of <c>&lt;Rung&gt;</c> elements found in
    /// <paramref name="effectiveFragment"/> before any were dropped.
    /// </param>
    /// <param name="droppedDuplicateCount">
    /// Set to the number of fragment rungs removed because they duplicated an
    /// already-staged rung at <paramref name="insertPosition"/>.
    /// </param>
    /// <returns>
    /// <paramref name="effectiveFragment"/> unchanged when no duplicates were found
    /// (including when it has no <c>&lt;RLLContent&gt;</c> or no rungs at all); otherwise
    /// the fragment re-serialised with the duplicate <c>&lt;Rung&gt;</c> elements removed.
    /// </returns>
    internal static string DedupeFragmentRungsAgainstStagedBuffer(
        string                                  effectiveFragment,
        string                                  routineXPath,
        uint                                    insertPosition,
        IReadOnlyDictionary<(string RoutineXPath, uint InsertPosition), List<string>> stagedRungBuffer,
        out int                                 fragmentRungCount,
        out int                                 droppedDuplicateCount)
    {
        fragmentRungCount     = 0;
        droppedDuplicateCount = 0;

        var doc        = SafeXml.ParseDocument(effectiveFragment);
        var rllContent = RoutineRungSelector.SelectContent(doc, routineXPath);
        if (rllContent is null)
            return effectiveFragment;

        var rungElems = rllContent.Elements("Rung").ToList();
        fragmentRungCount = rungElems.Count;
        if (rungElems.Count == 0)
            return effectiveFragment;

        if (!stagedRungBuffer.TryGetValue((routineXPath, insertPosition), out var stagedTexts) ||
            stagedTexts.Count == 0)
            return effectiveFragment;

        foreach (var rung in rungElems)
        {
            var normalizedText = NormalizeRungText(rung.Element("Text")?.Value);
            // The shared comparator. The fragment rung is the expected text; a staged text
            // that carries an export's `?` operand stands for the same rung with any preset.
            if (RungTextComparer.ContainsMatch(stagedTexts, normalizedText))
            {
                droppedDuplicateCount++;
                rung.Remove();
            }
        }

        return droppedDuplicateCount > 0 ? SerializeWithDeclaration(doc) : effectiveFragment;
    }

    /// <summary>
    /// Validates that <paramref name="l5xFragment"/> contains the required
    /// <c>RSLogix5000Content</c> root element and an <c>RLLContent</c> inner
    /// element nested inside it.
    ///
    /// <para>
    /// The SDK rejects any fragment that does not satisfy this nesting with the
    /// opaque error "allows only to import RLLContent", which does not tell the
    /// caller which element is absent or what the correct shape is.  This method
    /// surfaces a specific, actionable error that names the missing element and
    /// shows the full expected nesting path before any SDK call is attempted.
    /// </para>
    ///
    /// <para>
    /// Expected nesting path:
    /// <c>RSLogix5000Content &gt; Controller &gt; Programs &gt; Program &gt;
    /// Routines &gt; Routine &gt; RLLContent &gt; Rung &gt; Text &gt; CDATA</c>
    /// </para>
    /// </summary>
    /// <param name="l5xFragment">The L5X XML fragment text to validate.</param>
    /// <returns>
    /// A <see cref="ProposeRungEditError"/> describing the first missing element when
    /// the envelope is malformed, or <c>null</c> when both required elements are
    /// present.  Every returned error carries a non-empty <see cref="ProposeRungEditError.Hint"/>
    /// and a non-empty <see cref="ProposeRungEditError.DocsAnchor"/>.
    /// </returns>
    internal static ProposeRungEditError? ValidateFragmentEnvelope(string l5xFragment)
    {
        const string ExpectedNesting =
            "RSLogix5000Content > Controller > Programs > Program > " +
            "Routines > Routine > RLLContent > Rung > Text > CDATA";

        XDocument doc;
        try
        {
            doc = SafeXml.ParseDocument(l5xFragment);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProposeRungEditError(
                $"l5xFragment is not valid XML: {ex.Message}",
                Hint:       RoundTripHint,
                DocsAnchor: RungEditDocsAnchor);
        }

        // L5X is un-namespaced. Every downstream lookup (RLLContent, Tags/Tag, External
        // Access forcing) matches elements by un-namespaced name, so a fragment carrying a
        // default xmlns or a prefixed element would silently skip those steps. Refuse it.
        var namespaced = doc.Root is null
            ? null
            : doc.Descendants().FirstOrDefault(e => e.Name.Namespace != XNamespace.None);
        if (namespaced is not null)
        {
            return new ProposeRungEditError(
                $"l5xFragment contains namespaced element <{namespaced.Name.LocalName}> " +
                $"(namespace '{namespaced.Name.NamespaceName}'). L5X fragments must not declare " +
                $"an XML namespace (no default xmlns and no prefixed elements); " +
                $"External Access forcing and rung lookup cannot be applied to namespaced elements.",
                Hint:       "Remove the xmlns declaration (and any element prefixes) from the fragment. " + RoundTripHint,
                DocsAnchor: RungEditDocsAnchor,
                Kind:       ErrorKinds.InvalidL5x);
        }

        if (doc.Root?.Name.LocalName != "RSLogix5000Content")
        {
            var actualRoot = doc.Root?.Name.LocalName ?? "(none)";
            return new ProposeRungEditError(
                $"l5xFragment is missing the required RSLogix5000Content root element " +
                $"(found <{actualRoot}> instead). " +
                $"The fragment must be wrapped in the full RSLogix5000Content envelope. " +
                $"Expected nesting: {ExpectedNesting}",
                Hint:       RoundTripHint,
                DocsAnchor: RungEditDocsAnchor);
        }

        if (!doc.Descendants("RLLContent").Any())
        {
            // A self-closed <Routine .../> (what an export of an empty routine looks like) has nothing to
            // import into: name it, rather than the generic missing-element error.
            if (doc.Descendants("Routine").Any())
                return new ProposeRungEditError(
                    "l5xFragment's <Routine> element is self-closed (or has no <RLLContent> child), so it " +
                    "carries no rungs and the import would succeed while adding nothing. " +
                    $"Expected nesting: {ExpectedNesting}",
                    Hint:       "Pass the rungs with the rungs parameter, or open the routine element with an " +
                                "<RLLContent> child holding the <Rung> elements. " + RoundTripHint,
                    DocsAnchor: RungEditDocsAnchor,
                    Kind:       "EmptyRoutineEnvelope");

            return new ProposeRungEditError(
                $"l5xFragment is missing the required <RLLContent> element inside " +
                $"the RSLogix5000Content root. " +
                $"The SDK accepts only fragments that include an RLLContent section " +
                $"(use the SDK error \"allows only to import RLLContent\" as a hint). " +
                $"Expected nesting: {ExpectedNesting}",
                Hint:       RoundTripHint,
                DocsAnchor: RungEditDocsAnchor);
        }

        return null;
    }

    // ── Hand-written envelope normalization ──────────

    /// <summary>
    /// Root attributes an SDK export carries and a hand-written envelope usually lacks.
    /// </summary>
    private static readonly string[] ExportRootAttributes =
    {
        "SchemaRevision", "SoftwareRevision", "TargetName", "TargetType",
        "TargetSubType", "ContainsContext",
    };

    /// <summary>
    /// Outcome of <see cref="PlanRawFragment"/>.
    /// </summary>
    /// <param name="Error">Refusal to return before any SDK call, or <c>null</c>.</param>
    /// <param name="Rebuild">
    /// <c>true</c> when the fragment must be rebuilt by <see cref="RungFragmentBuilder"/>
    /// from <paramref name="Rungs"/>/<paramref name="Tags"/>; <c>false</c> for a pass-through.
    /// </param>
    /// <param name="Rungs">Rungs lifted from a hand-written fragment (validated, normalized).</param>
    /// <param name="Tags">Tags lifted from a hand-written fragment.</param>
    internal sealed record RawFragmentPlan(
        ProposeRungEditError?    Error,
        bool                     Rebuild,
        IReadOnlyList<RungInput> Rungs,
        IReadOnlyList<TagInput>  Tags);

    /// <summary>
    /// Decides how to treat a raw <c>l5xFragment</c> that already passed
    /// <see cref="ValidateFragmentEnvelope"/>.
    ///
    /// <list type="bullet">
    ///   <item>Root has <c>SchemaRevision</c>: pass through unchanged (<c>envelope="caller"</c>).</item>
    ///   <item>No <c>SchemaRevision</c> and only Rung (in RLLContent) and Tag elements
    ///     (with their Comment/Text/Description children): lift them out for a rebuild
    ///     (<c>envelope="server-built"</c>). Program/routine names must match
    ///     <paramref name="routineXPath"/>.</item>
    ///   <item>No <c>SchemaRevision</c> and anything else (DataTypes,
    ///     AddOnInstructionDefinitions, other routines, …): <c>Kind="EnvelopeIncomplete"</c>.</item>
    /// </list>
    /// </summary>
    /// <param name="l5xFragment">Well-formed fragment text with an RSLogix5000Content root.</param>
    /// <param name="routineXPath">The target routine XPath.</param>
    /// <returns>The plan; never throws.</returns>
    internal static RawFragmentPlan PlanRawFragment(string l5xFragment, string routineXPath)
    {
        var none = Array.Empty<RungInput>();
        var noTags = Array.Empty<TagInput>();

        XElement root;
        try { root = SafeXml.ParseDocument(l5xFragment).Root!; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RawFragmentPlan(
                new ProposeRungEditError($"l5xFragment is not valid XML: {ex.Message}",
                    Hint: RoundTripHint, DocsAnchor: RungEditDocsAnchor),
                false, none, noTags);
        }

        if (root.Attribute("SchemaRevision") is not null)
            return new RawFragmentPlan(null, false, none, noTags);

        var missing = ExportRootAttributes.Where(a => root.Attribute(a) is null).ToList();

        RawFragmentPlan Incomplete(string what) => new(
            new ProposeRungEditError(
                $"l5xFragment's RSLogix5000Content root is missing the attributes the SDK " +
                $"requires ({string.Join(", ", missing)}) and the fragment contains {what}, " +
                "so it cannot be rebuilt by the server. Pass the rungs/tags parameters instead, " +
                "or pass the l5x from logix_export_rungs unchanged apart from your edits. " +
                "No import was attempted." + TypedInputSkillPointer,
                Hint:       RoundTripHint,
                DocsAnchor: RungEditDocsAnchor,
                Kind:       "EnvelopeIncomplete"),
            false, none, noTags);

        var controllers = root.Elements().ToList();
        if (controllers.Any(e => e.Name.LocalName != "Controller") || controllers.Count != 1)
            return Incomplete("content other than a single <Controller>");
        var controller = controllers[0];

        var tagContainers = new List<XElement>();
        var rungElements  = new List<XElement>();
        XElement? program = null;
        foreach (var child in controller.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Tags":
                    tagContainers.Add(child);
                    break;
                case "Programs":
                    if (program is not null || child.Elements().Count() != 1 ||
                        child.Elements().Single().Name.LocalName != "Program")
                        return Incomplete("more than one program");
                    program = child.Elements().Single();
                    break;
                default:
                    return Incomplete($"<{child.Name.LocalName}>");
            }
        }

        if (program is null)
            return Incomplete("no <Programs> element");

        // An unparseable routineXPath skips the name checks (the SDK/offline path reports it).
        var programName = ProgramNamePattern.Match(routineXPath ?? string.Empty).Groups[1].Value;
        var routineName = RoutineNamePattern.Match(routineXPath ?? string.Empty).Groups[1].Value;
        if (programName.Length > 0 &&
            !string.Equals((string?)program.Attribute("Name"), programName, StringComparison.OrdinalIgnoreCase))
            return new RawFragmentPlan(
                new ProposeRungEditError(
                    $"l5xFragment's program '{(string?)program.Attribute("Name")}' does not match " +
                    $"the program '{programName}' in routineXPath. No import was attempted.",
                    Hint: RoundTripHint, DocsAnchor: RungEditDocsAnchor),
                false, none, noTags);

        XElement? routine = null;
        foreach (var child in program.Elements())
        {
            if (child.Name.LocalName != "Routines")
                return Incomplete($"<{child.Name.LocalName}> inside the program");
            if (routine is not null || child.Elements().Count() != 1 ||
                child.Elements().Single().Name.LocalName != "Routine")
                return Incomplete("other routines");
            routine = child.Elements().Single();
        }
        if (routine is null)
            return Incomplete("no <Routines> element");

        if (routineName.Length > 0 &&
            !string.Equals((string?)routine.Attribute("Name"), routineName, StringComparison.OrdinalIgnoreCase))
            return new RawFragmentPlan(
                new ProposeRungEditError(
                    $"l5xFragment's routine '{(string?)routine.Attribute("Name")}' does not match " +
                    $"the routine '{routineName}' in routineXPath. No import was attempted.",
                    Hint: RoundTripHint, DocsAnchor: RungEditDocsAnchor),
                false, none, noTags);

        foreach (var child in routine.Elements())
        {
            if (child.Name.LocalName != "RLLContent")
                return Incomplete($"<{child.Name.LocalName}> inside the routine");
            foreach (var rung in child.Elements())
            {
                if (rung.Name.LocalName != "Rung" ||
                    rung.Elements().Any(e => e.Name.LocalName is not ("Comment" or "Text")))
                    return Incomplete($"<{rung.Name.LocalName}> inside RLLContent");
                rungElements.Add(rung);
            }
        }

        var tagElements = new List<XElement>();
        foreach (var container in tagContainers)
        {
            foreach (var tag in container.Elements())
            {
                var tagType = (string?)tag.Attribute("TagType");
                if (tag.Name.LocalName != "Tag" ||
                    (tagType is not null && !string.Equals(tagType, "Base", StringComparison.OrdinalIgnoreCase)) ||
                    tag.Elements().Any(e => e.Name.LocalName != "Description"))
                    return Incomplete($"a tag declaration beyond a plain base tag ('{(string?)tag.Attribute("Name")}')");
                tagElements.Add(tag);
            }
        }

        var rungs = rungElements
            .Select(r => new RungInput(
                r.Element("Text")?.Value ?? string.Empty,
                r.Element("Comment") is { } c && c.Value.Length > 0 ? c.Value : null))
            .ToList();
        var tags = tagElements
            .Select(t => new TagInput(
                (string?)t.Attribute("Name") ?? string.Empty,
                (string?)t.Attribute("DataType") ?? string.Empty,
                (string?)t.Attribute("Radix"),
                t.Element("Description") is { } d && d.Value.Length > 0 ? d.Value : null,
                (string?)t.Attribute("Dimensions")))
            .ToList();

        // Same validation/normalization as typed input (rung text, tag names).
        if (ValidateTypedInput(null, rungs, tags, out var normalized) is { } invalid)
            return new RawFragmentPlan(invalid, false, none, noTags);

        return new RawFragmentPlan(null, true, normalized, tags);
    }

    // ── Tag stripping helper ──────────────────────────────────────────────────

    /// <summary>
    /// Removes every <c>&lt;Tags&gt;</c> container element from
    /// <paramref name="l5xFragment"/> and returns the stripped XML string
    /// together with the individual <c>&lt;Tag&gt;</c> children that were
    /// inside those containers.
    ///
    /// <para>
    /// Both controller-scoped <c>&lt;Tags&gt;</c> elements (directly under
    /// <c>&lt;Controller&gt;</c>) and program-scoped <c>&lt;Tags&gt;</c>
    /// elements (under <c>&lt;Program&gt;</c>) are removed.  The returned tag
    /// list preserves document order across all containers.
    /// </para>
    ///
    /// <para>
    /// The caller is responsible for ensuring <paramref name="l5xFragment"/>
    /// is well-formed XML before calling this method (envelope validation must
    /// have run first).
    /// </para>
    /// </summary>
    /// <param name="l5xFragment">
    /// Valid L5X XML fragment text from which <c>&lt;Tags&gt;</c> containers
    /// are to be stripped.
    /// </param>
    /// <param name="strippedFragment">
    /// The fragment XML text with all <c>&lt;Tags&gt;</c> containers removed.
    /// Equal to <paramref name="l5xFragment"/> (modulo formatting) when the
    /// fragment contains no <c>&lt;Tags&gt;</c> elements.
    /// </param>
    /// <returns>
    /// All <c>&lt;Tag&gt;</c> child elements that were inside the removed
    /// <c>&lt;Tags&gt;</c> containers, cloned and detached, in document order.
    /// An empty list when the fragment contains no <c>&lt;Tags&gt;</c>
    /// elements.
    /// </returns>
    internal static IReadOnlyList<XElement> StripTagsFromFragment(
        string l5xFragment, out string strippedFragment)
    {
        var doc = SafeXml.ParseDocument(l5xFragment);
        var extractedTags = new List<XElement>();

        // Collect all <Tags> containers first to avoid modifying the
        // XDocument while iterating it.
        var tagsContainers = doc.Descendants("Tags").ToList();

        foreach (var container in tagsContainers)
        {
            // Clone each <Tag> child before the container is removed so the
            // returned elements are detached from the document.
            foreach (var tag in container.Elements("Tag"))
                extractedTags.Add(new XElement(tag));

            container.Remove();
        }

        // Serialise without adding indentation: the SDK does not care about
        // whitespace and keeping the output compact avoids surprising callers
        // that compare strings.
        strippedFragment = SerializeWithDeclaration(doc);
        return extractedTags;
    }

    /// <summary>
    /// Builds the fragment the <c>verifyOnStage</c> offline replay imports: the stripped stage fragment with every
    /// staged tag declaration (earlier calls and the current one) placed in the controller's
    /// <c>&lt;Tags Use="Context"&gt;</c> envelope, ahead of the programs and their rungs. Falls back to the
    /// fragment unchanged (plus an XML declaration) when it has no <c>&lt;Controller&gt;</c> to merge into.
    /// </summary>
    /// <param name="strippedFragment">The stage fragment with its tag containers removed.</param>
    /// <param name="stagedTagBuffer">The session's staged tag declarations, keyed by name.</param>
    /// <returns>The replay fragment text, always carrying an XML declaration.</returns>
    internal static string BuildStageVerifyReplayFragment(
        string strippedFragment, IReadOnlyDictionary<string, XElement> stagedTagBuffer) =>
        MergeBufferedTagsIntoFragment(strippedFragment, stagedTagBuffer, out _);

    /// <summary>
    /// Serialises <paramref name="doc"/> compactly WITH its XML declaration.
    /// <para>
    /// <see cref="XDocument.ToString()"/> silently drops the
    /// <c>&lt;?xml version="1.0" encoding="UTF-8" standalone="yes"?&gt;</c>
    /// line, and the Logix SDK's XML importer rejects a declaration-less
    /// fragment with <c>XMLSrv_E_ERROR_READING_XML</c> ("malformed or invalid
    /// XML format"). Measured on Bench 2, 2026-09-06
    /// (<c>scripts/exp-xmlprobe.py</c>): the export unchanged imports; the same
    /// export minus its declaration is rejected; the export minus every
    /// <c>&lt;Tags&gt;</c> container (declaration kept) imports. Every
    /// fragment this handler writes for the SDK must go through this method.
    /// </para>
    /// </summary>
    internal static string SerializeWithDeclaration(XDocument doc)
    {
        var declaration = doc.Declaration is not null
            ? doc.Declaration.ToString()
            : new XDeclaration("1.0", "UTF-8", "yes").ToString();
        return declaration + "\r\n" + doc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Refuses a caller fragment that re-declares a pre-existing tag with a different
    /// <c>ExternalAccess</c> than the census recorded. External Access is the engineer's
    /// write grant and lgx-mcp never changes it. A declaration with no
    /// <c>ExternalAccess</c> attribute, or a tag absent from both census maps, is not a
    /// change. Only <c>&lt;Tag&gt;</c> elements directly inside a <c>&lt;Tags&gt;</c>
    /// container are examined. <paramref name="sessionAccess"/> wins over
    /// <paramref name="callAccess"/> when both hold the tag.
    /// </summary>
    /// <param name="fragment">Caller's L5X fragment text.</param>
    /// <param name="sessionAccess">Session-recorded pre-stage access per tag.</param>
    /// <param name="callAccess">Access per tag supplied with this call; may be null.</param>
    /// <returns>An <c>ExternalAccessChangeRefused</c> error, or null when nothing changes.</returns>
    internal static ProposeRungEditError? FindExternalAccessChange(
        string fragment,
        IReadOnlyDictionary<string, string> sessionAccess,
        IReadOnlyDictionary<string, string>? callAccess)
    {
        XDocument doc;
        try { doc = SafeXml.ParseDocument(fragment); }
        catch (System.Xml.XmlException) { return null; }

        foreach (var tag in doc.Descendants("Tags").Elements("Tag"))
        {
            var name     = (string?)tag.Attribute("Name");
            var declared = (string?)tag.Attribute("ExternalAccess");
            if (string.IsNullOrWhiteSpace(name) || declared is null)
                continue;

            string? census = null;
            if (sessionAccess.TryGetValue(name, out var fromSession))
                census = fromSession;
            else if (callAccess is not null)
                foreach (var (k, v) in callAccess)
                    if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) { census = v; break; }

            if (census is null || string.Equals(census, declared, StringComparison.Ordinal))
                continue;

            return new ProposeRungEditError(
                $"Tag '{name}' already exists with ExternalAccess '{census}'; the fragment declares " +
                $"'{declared}'. {ExternalAccessChangeMessage}. No import was attempted.",
                Hint: "Re-declare the tag with its current ExternalAccess, or omit the attribute. " +
                      "An engineer grants writes by setting External Access in Logix Designer.",
                Kind: "ExternalAccessChangeRefused");
        }
        return null;
    }

    /// <summary>
    /// <c>externalAccessForced</c> entries for server-built tags created <c>Read/Write</c>
    /// because Logix rejects their type as Read Only (HMIBC). Pre-existing tags are skipped.
    /// </summary>
    private static IReadOnlyList<string>? ServerBuiltForcedAccess(
        IEnumerable<TagInput>? tags,
        LogixProjectSession session,
        IEnumerable<string>? preExistingTagNames,
        IReadOnlyList<string>? existing)
    {
        var census = new HashSet<string>(session.PreStageTagCensus, StringComparer.OrdinalIgnoreCase);
        if (preExistingTagNames is not null)
            foreach (var n in preExistingTagNames)
                census.Add(n);
        var entries = RungFragmentBuilder.ForcedReadWriteEntries(tags, census);
        if (existing is not null)
            entries.InsertRange(0, existing);
        return entries.Count == 0 ? null : entries;
    }

    /// <summary>The refusal message for an attempted External Access change.</summary>
    internal const string ExternalAccessChangeMessage =
        "External Access is the engineer's write grant; change it in Logix Designer";

    /// <summary>
    /// Forces every tag declaration in <paramref name="fragment"/> whose name is not in
    /// <paramref name="census"/> to <c>ExternalAccess="Read Only"</c> (<c>"Read/Write"</c> for
    /// data types Logix rejects as Read Only, e.g. HMIBC), whatever the caller
    /// wrote (including an absent attribute, which Logix reads as Read/Write). Only
    /// <c>&lt;Tag&gt;</c> elements directly inside a <c>&lt;Tags&gt;</c> container are
    /// touched; UDT member-level access (<c>&lt;Member&gt;</c>) is left alone. A name in the
    /// census is a pre-existing tag re-declared for context and is never modified.
    /// When nothing needs forcing the fragment is returned byte-for-byte.
    /// </summary>
    /// <param name="fragment">Caller's L5X fragment text.</param>
    /// <param name="census">Pre-stage tag names, compared case-insensitively. May be empty.</param>
    /// <param name="forced">
    /// Entries for tags whose access was rewritten, in document order, deduplicated: the bare
    /// name for Read Only, <c>"Name: Read/Write (reason)"</c> for a Read/Write-required type.
    /// </param>
    /// <returns>The (possibly rewritten) fragment text; unparseable XML is returned unchanged.</returns>
    internal static string ForceNewTagsReadOnly(
        string fragment, IReadOnlyCollection<string> census, out List<string> forced)
    {
        forced = new List<string>();
        XDocument doc;
        try { doc = SafeXml.ParseDocument(fragment); }
        catch (System.Xml.XmlException) { return fragment; }

        var known = census as ISet<string> ?? new HashSet<string>(census, StringComparer.OrdinalIgnoreCase);
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in doc.Descendants("Tags").Elements("Tag"))
        {
            var name = (string?)tag.Attribute("Name");
            if (string.IsNullOrWhiteSpace(name) || known.Contains(name))
                continue;
            var wanted = RungFragmentBuilder.ExternalAccessFor((string?)tag.Attribute("DataType"));
            var actual = (string?)tag.Attribute("ExternalAccess");
            if (string.Equals(actual, wanted, StringComparison.Ordinal))
                continue;
            // Read/Write-required types (HMIBC): an absent attribute already reads as Read/Write.
            var readWriteReason = RungFragmentBuilder.ReadWriteRequiredReason((string?)tag.Attribute("DataType"));
            if (readWriteReason is not null && actual is null)
                continue;
            tag.SetAttributeValue("ExternalAccess", wanted);
            if (seen.Add(name))
                forced.Add(readWriteReason is null
                    ? name
                    : RungFragmentBuilder.DescribeForcedReadWrite(name, readWriteReason));
        }

        return forced.Count == 0 ? fragment : SerializeWithDeclaration(doc);
    }

    /// <summary>
    /// Returns <paramref name="fragment"/> unchanged when it already begins with an
    /// XML declaration; otherwise prepends one.
    /// <para>
    /// Unlike <see cref="SerializeWithDeclaration"/>, this never parses or
    /// re-serialises the fragment through <see cref="XDocument"/> — the caller's text
    /// is preserved byte-for-byte (whitespace, attribute order, comments, everything).
    /// This is the only transformation the controller (publish) path is permitted to
    /// apply to a caller's fragment: the SDK's importer
    /// rejects a declaration-less fragment with <c>XMLSrv_E_ERROR_READING_XML</c> (the
    /// 0062 fix), but nothing else about the fragment — least of all its own
    /// <c>&lt;Tags&gt;</c> envelope — may be rewritten.
    /// </para>
    /// </summary>
    internal static string EnsureXmlDeclaration(string fragment)
    {
        var trimmed = fragment.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            return fragment;

        return new XDeclaration("1.0", "UTF-8", "yes").ToString() + "\r\n" + fragment;
    }

    /// <summary>
    /// Merges buffered tag declarations into a controller-publish fragment's own
    /// tag envelope.
    /// <para>
    /// When <paramref name="stagedTagBuffer"/> is empty, this is equivalent to
    /// <see cref="EnsureXmlDeclaration"/>: the fragment is returned byte-for-byte
    /// (only an XML declaration may be prepended), preserving requirement 1.
    /// </para>
    /// <para>
    /// Otherwise the fragment is parsed, and every buffered <c>&lt;Tag&gt;</c>
    /// whose <c>Name</c> is not already declared anywhere in the fragment (any
    /// <c>&lt;Tags&gt;</c> container, compared case-insensitively) is cloned and
    /// appended inside the fragment's own controller-scoped
    /// <c>&lt;Tags Use="Context"&gt;</c> element. Fragment-declared tags always win
    /// on a name clash — the corresponding buffered declaration is skipped and
    /// nothing already present in the fragment is ever removed or altered.
    /// </para>
    /// <para>
    /// If the fragment has no <c>&lt;Tags Use="Context"&gt;</c> element under
    /// <c>&lt;Controller&gt;</c>, one is created (with <c>Use="Context"</c>) and
    /// inserted immediately after <c>&lt;DataTypes&gt;</c> when present, otherwise
    /// immediately before <c>&lt;Programs&gt;</c> — matching the exporter's own
    /// position — but only when there is at least one buffered tag left to merge
    /// after the name-clash filter above; a fragment with nothing new to add is
    /// never touched.
    /// </para>
    /// </summary>
    /// <param name="l5xFragment">The caller's controller-publish fragment text.</param>
    /// <param name="stagedTagBuffer">
    /// The session's buffered tag declarations, keyed by tag name
    /// (<see cref="LogixProjectSession.StagedTagBuffer"/>).
    /// </param>
    /// <param name="mergedTagNames">
    /// The names of the buffered tags that were actually appended into the
    /// fragment (i.e. excluding any skipped because the fragment already declared
    /// them). Empty when the buffer was empty or every buffered name collided
    /// with an existing fragment declaration.
    /// </param>
    /// <returns>The fragment text to hand to the SDK, always carrying an XML declaration.</returns>
    internal static string MergeBufferedTagsIntoFragment(
        string l5xFragment,
        IReadOnlyDictionary<string, XElement> stagedTagBuffer,
        out IReadOnlyList<string> mergedTagNames)
    {
        var merged = new List<string>();

        if (stagedTagBuffer.Count == 0)
        {
            mergedTagNames = merged;
            return EnsureXmlDeclaration(l5xFragment);
        }

        var doc = SafeXml.ParseDocument(l5xFragment);
        var controllerElem = doc.Root?.Element("Controller");
        if (controllerElem is null)
        {
            // No <Controller> element to merge into — nothing structural to do.
            mergedTagNames = merged;
            return EnsureXmlDeclaration(l5xFragment);
        }

        // Tag names already declared anywhere in the fragment (any <Tags>
        // container, any scope) win on a name clash — the buffered copy is
        // skipped rather than overwriting or duplicating the declaration.
        var existingNames = doc.Descendants("Tags")
            .Elements("Tag")
            .Select(t => (string?)t.Attribute("Name"))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The fragment's own controller-scoped context container, if it has one.
        XElement? contextContainer = controllerElem.Elements("Tags")
            .FirstOrDefault(e => string.Equals(
                (string?)e.Attribute("Use"), "Context", StringComparison.OrdinalIgnoreCase));

        foreach (var kvp in stagedTagBuffer.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (existingNames.Contains(kvp.Key))
                continue;

            if (contextContainer is null)
            {
                // Create the container in the exporter's own position: after
                // <DataTypes> when present, otherwise immediately before
                // <Programs>. Falls back to appending under <Controller> when
                // the fragment has neither (should not occur for a well-formed
                // rung-edit fragment, but avoids silently dropping the tag).
                contextContainer = new XElement("Tags", new XAttribute("Use", "Context"));
                var dataTypesElem = controllerElem.Element("DataTypes");
                var programsElem  = controllerElem.Element("Programs");
                if (dataTypesElem is not null)
                    dataTypesElem.AddAfterSelf(contextContainer);
                else if (programsElem is not null)
                    programsElem.AddBeforeSelf(contextContainer);
                else
                    controllerElem.Add(contextContainer);
            }

            contextContainer.Add(new XElement(kvp.Value));
            merged.Add(kvp.Key);
        }

        mergedTagNames = merged;
        return SerializeWithDeclaration(doc);
    }

    // ── Offline helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Regex that extracts the <c>Program</c> name from a Logix SDK XPath of the form
    /// <c>…/Program[@Name='…']/…</c>.
    /// </summary>
    internal static readonly Regex ProgramNamePattern =
        new(@"Program\[@Name=['""]([^'""]+)['""]\]", RegexOptions.Compiled);

    /// <summary>
    /// Regex that extracts the <c>Routine</c> name from a Logix SDK XPath of the form
    /// <c>…/Routine[@Name='…']</c>.
    /// </summary>
    internal static readonly Regex RoutineNamePattern =
        new(@"Routine\[@Name=['""]([^'""]+)['""]\]", RegexOptions.Compiled);

    /// <summary>
    /// Applies rung edits directly to the L5X project file on disk without requiring
    /// a connected Logix Designer session or controller.
    ///
    /// <para>
    /// The caller must pass <c>offline: true</c> explicitly — this mode is never
    /// activated by fallback or by omitting a parameter.  When called, this method:
    /// <list type="bullet">
    ///   <item>Parses <paramref name="routineXPath"/> to locate the target program and routine.</item>
    ///   <item>Loads the L5X file at <paramref name="projectFilePath"/> as an XML document.</item>
    ///   <item>Inserts the rungs from <paramref name="l5xFragment"/> at
    ///     <paramref name="insertPosition"/>, replacing <paramref name="replaceCount"/>
    ///     existing rungs.</item>
    ///   <item>Re-numbers all rungs sequentially (0-based) after the modification.</item>
    ///   <item>Saves the modified document back to <paramref name="projectFilePath"/>.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The returned <see cref="ProposeRungEditOfflineResult"/> has
    /// <c>status="saved-to-file"</c> and includes the absolute file path that was
    /// written.  It never uses the word "proposed" and never suggests checking Studio
    /// 5000 for pending edits — the change is durable on disk immediately.
    /// </para>
    ///
    /// <para>
    /// No controller is contacted and no Logix Designer session is required.
    /// The write-gate policy is still enforced when <paramref name="policy"/> is
    /// non-<c>null</c>.
    /// </para>
    /// </summary>
    /// <param name="routineXPath">
    /// Logix SDK XPath identifying the target routine, e.g.
    /// <c>Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']</c>.
    /// Used to locate the program and routine within the L5X XML.
    /// Must not be <c>null</c> or whitespace.
    /// </param>
    /// <param name="insertPosition">Zero-based rung index at which the import is inserted.</param>
    /// <param name="replaceCount">
    /// Number of existing rungs to replace starting at <paramref name="insertPosition"/>.
    /// Pass <c>0</c> to insert without replacing any rungs.
    /// </param>
    /// <param name="l5xFragment">
    /// Inline L5X XML containing the rungs to import.  Must not be <c>null</c> or
    /// whitespace.  Must parse as valid XML and contain at least one <c>&lt;Rung&gt;</c>
    /// element.
    /// </param>
    /// <param name="projectFilePath">
    /// Absolute path to the <c>.L5X</c> file to modify.  The file must exist.
    /// This file is read, modified, and overwritten in-place.
    /// </param>
    /// <param name="policy">
    /// Optional <see cref="WriteGatePolicy"/>.  When non-<c>null</c>, the fragment is
    /// evaluated against the policy before any file I/O.  Denial returns a
    /// <see cref="ProposeRungEditError"/> immediately without touching the file.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ProposeRungEditOfflineResult"/> on success, or a
    /// <see cref="ProposeRungEditError"/> on any validation, gate, or I/O failure.
    /// Never throws (except <see cref="OperationCanceledException"/> for cancellation).
    /// </returns>
    public static async System.Threading.Tasks.Task<object> ExecuteOfflineAsync(
        string           routineXPath,
        uint             insertPosition,
        uint             replaceCount,
        string           l5xFragment,
        string           projectFilePath,
        WriteGatePolicy? policy = null,
        CancellationToken ct    = default)
    {
        // Exactly one rung_edit audit line per call; a validation or gate refusal that
        // returned before the file was touched is logged here as "refused" with its kind.
        var audit = new RungEditAuditScope(routineXPath ?? string.Empty, insertPosition, "file");
        object result;
        try
        {
            result = await ExecuteOfflineCoreAsync(
                routineXPath!, insertPosition, replaceCount, l5xFragment, projectFilePath, audit, policy, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            audit.LogUnlessWritten("cancelled", null);
            throw;
        }
        catch (Exception)
        {
            audit.LogUnlessWritten("error", null);
            throw;
        }

        if (result is ProposeRungEditError refusal)
            audit.LogUnlessWritten("refused", refusal.Kind);
        else
            audit.LogUnlessWritten("error", null);
        return result;
    }

    private static async System.Threading.Tasks.Task<object> ExecuteOfflineCoreAsync(
        string             routineXPath,
        uint               insertPosition,
        uint               replaceCount,
        string             l5xFragment,
        string             projectFilePath,
        RungEditAuditScope audit,
        WriteGatePolicy?   policy,
        CancellationToken  ct)
    {
        // ── Input validation ──────────────────────────────────────────────────

        if (string.IsNullOrWhiteSpace(routineXPath))
            return new ProposeRungEditError(
                "routineXPath must not be empty. " +
                "Provide the XPath string that identifies the target routine, e.g. " +
                "Controller/Programs/Program[@Name='MainProgram']" +
                "/Routines/Routine[@Name='MainRoutine'].");

        if (string.IsNullOrWhiteSpace(l5xFragment))
            return new ProposeRungEditError(
                "l5xFragment must not be empty. " +
                "Provide the inline L5X XML text containing the rungs to import.");

        if (string.IsNullOrWhiteSpace(projectFilePath))
            return new ProposeRungEditError(
                "projectFilePath must not be empty.");

        if (!File.Exists(projectFilePath))
            return new ProposeRungEditError(
                $"Project file not found at path: '{projectFilePath}'.");

        // ── Fragment envelope validation ──────────────────────────────────────
        // Same pre-SDK check as the online path: validate RSLogix5000Content root
        // and RLLContent inner element before any gate check or file I/O.

        var envelopeErrorOffline = ValidateFragmentEnvelope(l5xFragment);
        if (envelopeErrorOffline is not null)
            return envelopeErrorOffline;

        // Normalize a hand-written envelope (no SchemaRevision).
        var rawPlanOffline = PlanRawFragment(l5xFragment, routineXPath);
        if (rawPlanOffline.Error is not null)
            return rawPlanOffline.Error;

        string envelopeSourceOffline = "caller";
        if (rawPlanOffline.Rebuild)
        {
            var offProgram = ProgramNamePattern.Match(routineXPath);
            var offRoutine = RoutineNamePattern.Match(routineXPath);
            if (!offProgram.Success || !offRoutine.Success)
                return new ProposeRungEditError(
                    $"Could not parse program or routine name from routineXPath '{routineXPath}'. " +
                    "Expected format: Controller/Programs/Program[@Name='...']/Routines/Routine[@Name='...'].");
            try
            {
                var projectRoot = SafeXml.LoadDocument(projectFilePath).Root
                    ?? throw new InvalidOperationException("the project file has no root element");
                l5xFragment = RungFragmentBuilder.BuildOffline(
                    projectRoot, offProgram.Groups[1].Value, offRoutine.Groups[1].Value,
                    insertPosition, rawPlanOffline.Rungs, rawPlanOffline.Tags);
                envelopeSourceOffline = "server-built";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ProposeRungEditError(
                    $"Could not rebuild the offline import fragment from '{projectFilePath}': {ex.Message}");
            }
        }

        // ── Fragment scan (advisory) ──────────────────────────────────────────
        // When a WriteGatePolicy is provided, scan the proposed fragment for write
        // operands using WriteClassifier.  The scan result is included in the
        // response annotation so the reviewing engineer can see each write
        // destination, its gate classification, and its provenance at decision time.

        WriteClassifier.RungScan? fragmentScanOffline = null;
        if (policy is not null)
        {
            try
            {
                fragmentScanOffline = WriteClassifier.ScanFragment(l5xFragment, routineXPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ProposeRungEditError(
                    $"Failed to parse l5xFragment as XML for gate check: {ex.Message}");
            }
        }

        // ── Parse routineXPath ────────────────────────────────────────────────
        // Extract the program and routine names from the Logix SDK XPath string.
        // Expected format: Controller/Programs/Program[@Name='…']/Routines/Routine[@Name='…']

        var programMatch = ProgramNamePattern.Match(routineXPath);
        var routineMatch = RoutineNamePattern.Match(routineXPath);

        if (!programMatch.Success || !routineMatch.Success)
            return new ProposeRungEditError(
                $"Could not parse program or routine name from routineXPath '{routineXPath}'. " +
                "Expected format: Controller/Programs/Program[@Name='...']/Routines/Routine[@Name='...'].");

        var programName = programMatch.Groups[1].Value;
        var routineName = routineMatch.Groups[1].Value;

        // ── Parse fragment for rung elements ──────────────────────────────────

        XElement[] newRungs;
        try
        {
            var fragmentDoc = SafeXml.ParseDocument(l5xFragment);
            newRungs = RoutineRungSelector.Select(fragmentDoc, routineXPath).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProposeRungEditError(
                $"Failed to parse l5xFragment as XML: {ex.Message}");
        }

        if (newRungs.Length == 0)
            return new ProposeRungEditError(
                "l5xFragment contains no <Rung> elements to import.");

        // ── Load, modify, and save project file ───────────────────────────────

        try
        {
            XDocument projectDoc;
            try
            {
                projectDoc = await System.Threading.Tasks.Task.Run(
                    () => SafeXml.LoadDocument(projectFilePath), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ProposeRungEditError(
                    $"Failed to load project file '{projectFilePath}': {ex.Message}");
            }

            // Locate the target program element.
            var programElem = projectDoc.Descendants("Program")
                .FirstOrDefault(p => string.Equals(
                    (string?)p.Attribute("Name"), programName,
                    StringComparison.OrdinalIgnoreCase));

            if (programElem is null)
                return new ProposeRungEditError(
                    $"Program '{programName}' not found in project file '{projectFilePath}'.");

            // Locate the target routine element within the program.
            var routineElem = programElem.Descendants("Routine")
                .FirstOrDefault(r => string.Equals(
                    (string?)r.Attribute("Name"), routineName,
                    StringComparison.OrdinalIgnoreCase));

            if (routineElem is null)
                return new ProposeRungEditError(
                    $"Routine '{routineName}' not found in program '{programName}' " +
                    $"in project file '{projectFilePath}'.");

            // Locate the RLL rung container element.
            var rllContent = routineElem.Element("RLLContent");
            if (rllContent is null)
                return new ProposeRungEditError(
                    $"Routine '{routineName}' in program '{programName}' has no " +
                    "<RLLContent> element. Only RLL routines support rung edits.");

            // ── Apply insert / replace ─────────────────────────────────────────
            // Step 1: remove replaceCount rungs starting at insertPosition.
            var existingRungs = rllContent.Elements("Rung").ToList();
            for (uint i = 0; i < replaceCount; i++)
            {
                var idx = (int)(insertPosition + i);
                if (idx < existingRungs.Count)
                    existingRungs[idx].Remove();
            }

            // Step 2: insert the new rungs at insertPosition.
            var remainingRungs = rllContent.Elements("Rung").ToList();
            if ((int)insertPosition >= remainingRungs.Count)
            {
                // Append at end.
                foreach (var rung in newRungs)
                    rllContent.Add(new XElement(rung));
            }
            else
            {
                // Insert before the rung currently at insertPosition.
                var insertBefore = remainingRungs[(int)insertPosition];
                foreach (var rung in newRungs)
                    insertBefore.AddBeforeSelf(new XElement(rung));
            }

            // Step 3: re-number all rungs sequentially (0-based).
            var allRungs = rllContent.Elements("Rung").ToList();
            for (int i = 0; i < allRungs.Count; i++)
                allRungs[i].SetAttributeValue("Number", i);

            // Step 4: save the modified document back to the original file path.
            await System.Threading.Tasks.Task.Run(
                () => projectDoc.Save(projectFilePath), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            audit.Log(routineXPath, insertPosition, "error", null,
                destination: "file");
            return new ProposeRungEditError(
                $"Failed to apply offline rung edit to '{projectFilePath}': {ex.Message}");
        }

        var absPath = Path.GetFullPath(projectFilePath);
        audit.Log(routineXPath, insertPosition, "saved-to-file", null,
            destination:     "file",
            gateAnnotations: ToAuditAnnotations(BuildGateAnnotations(fragmentScanOffline, policy)));
        return new ProposeRungEditOfflineResult(
            Status:          "saved-to-file",
            FilePath:        absPath,
            RoutineXPath:    routineXPath,
            InsertPosition:  insertPosition,
            ReplaceCount:    replaceCount,
            Message:         $"FILE MODIFIED — NO CONTROLLER CONTACTED. " +
                             $"The project file at '{absPath}' was updated directly on disk. " +
                             $"Routine '{routineXPath}', insert position {insertPosition}, " +
                             $"{DescribeReplacing(replaceCount)}. " +
                             "This is an offline file-only change: the L5X file has been " +
                             "saved to disk with the new rung content. " +
                             "No controller was contacted and no Logix Designer session was used. " +
                             "The project file must be opened in Studio 5000 or downloaded to " +
                             "a controller for the change to take effect in a running system.",
            GateAnnotations: BuildGateAnnotations(fragmentScanOffline, policy),
            SafetyFlags:     BuildSafetyFlags(fragmentScanOffline, policy),
            Envelope:        envelopeSourceOffline);
    }

    // ── Gate annotation helpers ───────────────────────────────────────────────

    /// <summary>
    /// Builds the gate analysis annotation list from a fragment scan result and the
    /// active write-gate policy.
    ///
    /// <para>
    /// Returns an ordered list of <see cref="GateAnnotationEntry"/> records — one
    /// for each proven write destination found in the fragment — sorted ascending by
    /// tag name.  Returns <c>null</c> when the scan was not performed (no policy
    /// was available) or when the fragment contained no proven write operands.
    /// </para>
    /// </summary>
    /// <param name="scan">
    /// Fragment scan produced by <see cref="WriteClassifier.ScanFragment"/>, or
    /// <c>null</c> when no scan was performed (policy was <c>null</c>).
    /// </param>
    /// <param name="policy">
    /// The write-gate policy used to classify each write destination, or <c>null</c>
    /// when no policy is available.
    /// </param>
    /// <returns>
    /// A non-empty, alphabetically ordered list of <see cref="GateAnnotationEntry"/>
    /// records, or <c>null</c> when there are no write destinations to annotate.
    /// </returns>
    internal static IReadOnlyList<GateAnnotationEntry>? BuildGateAnnotations(
        WriteClassifier.RungScan? scan,
        WriteGatePolicy?          policy)
    {
        if (scan is null || scan.ProgramWritten.Count == 0)
            return null;

        var annotations = scan.ProgramWritten
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new GateAnnotationEntry(
                Tag:            kv.Key,
                Classification: ClassifyTagForAnnotation(kv.Key, policy),
                Provenance:     kv.Value))
            .ToList();

        return annotations.Count > 0 ? annotations : null;
    }

    /// <summary>
    /// Builds the prominent safety flags for a proposal response by examining each
    /// write destination in the fragment scan against the active write-gate policy.
    ///
    /// <para>
    /// Three disjoint categories are checked:
    /// <list type="number">
    ///   <item><description>
    ///     <b>writesToSafetyTags</b> — the tag is denied by the policy with reason
    ///     <see cref="DenyReason.SafetyTag"/>, indicating a safety-interlock signal.
    ///   </description></item>
    ///   <item><description>
    ///     <b>invalidatesGrantBasis</b> — the tag is currently granted by the policy,
    ///     but the proposed rung writes to it (OTE destination).  Accepting this edit
    ///     would make the tag program-written while the policy continues granting
    ///     writes to it — exactly the condition.
    ///   </description></item>
    ///   <item><description>
    ///     <b>writesUnknownTags</b> — the tag is absent from the policy entirely
    ///     (neither granted nor denied), meaning it does not exist in the project as
    ///     currently loaded.
    ///   </description></item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="scan">
    /// Fragment scan produced by <see cref="WriteClassifier.ScanFragment"/>, or
    /// <c>null</c> when no scan was performed (policy was <c>null</c>).
    /// </param>
    /// <param name="policy">
    /// The write-gate policy used to classify each write destination, or <c>null</c>
    /// when no policy is available.
    /// </param>
    /// <returns>
    /// A <see cref="ProposalSafetyFlags"/> with at least one non-null field, or
    /// <c>null</c> when no safety flags apply to this proposal.
    /// </returns>
    internal static ProposalSafetyFlags? BuildSafetyFlags(
        WriteClassifier.RungScan? scan,
        WriteGatePolicy?          policy)
    {
        if (scan is null || policy is null || scan.ProgramWritten.Count == 0)
            return null;

        var safetyTagWrites  = new List<string>();
        var invalidatesGrant = new List<string>();
        var unknownTagWrites = new List<string>();

        foreach (var tagName in scan.ProgramWritten.Keys)
        {
            // Case 1: tag is denied as SafetyTag by the active policy.
            var deny = policy.Denies.FirstOrDefault(d =>
                string.Equals(d.Tag, tagName, StringComparison.OrdinalIgnoreCase));

            if (deny is not null)
            {
                if (deny.Reason == DenyReason.SafetyTag)
                    safetyTagWrites.Add(tagName);
                // Other deny reasons (ProgramWritten, NotDesignated, UnknownWriteContext)
                // are not safety flags for this method's purposes.
                continue;
            }

            // Case 2: tag is currently granted — writing it as an OTE destination
            // would make it program-written and invalidate the grant.
            if (policy.Grants.Any(g =>
                    string.Equals(g.Tag, tagName, StringComparison.OrdinalIgnoreCase)))
            {
                invalidatesGrant.Add(tagName);
                continue;
            }

            // Case 3: tag is absent from the policy entirely — not in the project.
            unknownTagWrites.Add(tagName);
        }

        // Return null when no flags fired — avoids a safetyFlags: {} stub in JSON.
        if (safetyTagWrites.Count == 0 && invalidatesGrant.Count == 0 && unknownTagWrites.Count == 0)
            return null;

        return new ProposalSafetyFlags(
            WritesToSafetyTags:    safetyTagWrites.Count  > 0 ? safetyTagWrites.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList()  : null,
            InvalidatesGrantBasis: invalidatesGrant.Count > 0 ? invalidatesGrant.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList() : null,
            WritesUnknownTags:     unknownTagWrites.Count > 0 ? unknownTagWrites.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList() : null);
    }

    /// <summary>
    /// Returns the gate classification string for <paramref name="tagName"/> against
    /// <paramref name="policy"/>:
    /// <list type="bullet">
    ///   <item><c>"granted"</c> — the policy grants the tag for writing.</item>
    ///   <item>A <see cref="DenyReason"/> name (e.g. <c>"ProgramWritten"</c>,
    ///     <c>"SafetyTag"</c>, <c>"NotDesignated"</c>, <c>"UnknownWriteContext"</c>)
    ///     — the policy denies the tag for that reason.</item>
    ///   <item><c>"not-in-policy"</c> — the tag does not appear in the policy at all
    ///     (e.g. a tag that is referenced in the fragment but not defined in the project).</item>
    /// </list>
    /// </summary>
    /// <param name="tagName">Base tag name to classify.</param>
    /// <param name="policy">The active write-gate policy, or <c>null</c>.</param>
    /// <returns>A non-null classification string.</returns>
    internal static string ClassifyTagForAnnotation(string tagName, WriteGatePolicy? policy)
    {
        if (policy is null)
            return "no-policy";

        if (policy.Grants.Any(g =>
                string.Equals(g.Tag, tagName, StringComparison.OrdinalIgnoreCase)))
            return "granted";

        var deny = policy.Denies.FirstOrDefault(d =>
            string.Equals(d.Tag, tagName, StringComparison.OrdinalIgnoreCase));

        return deny is not null ? deny.Reason.ToString() : "not-in-policy";
    }

    /// <summary>
    /// Converts a list of <see cref="GateAnnotationEntry"/> records (the handler's
    /// response payload type) to a list of <see cref="AuditGateAnnotation"/> records
    /// (the audit logger's type) for persistence in <c>audit.jsonl</c>.
    ///
    /// <para>
    /// The two types carry identical fields — tag, classification, provenance — but
    /// are defined in separate assemblies: <see cref="GateAnnotationEntry"/> lives in
    /// the SDK adapter (behind <c>#if LOGIX_SDK</c>) while
    /// <see cref="AuditGateAnnotation"/> lives in the core assembly so that
    /// <see cref="AuditLogger"/> can serialise it without taking an SDK dependency.
    /// </para>
    /// </summary>
    /// <param name="annotations">
    /// The gate annotation list built by <see cref="BuildGateAnnotations"/>, or
    /// <c>null</c> when no annotations are available.
    /// </param>
    /// <returns>
    /// A list of <see cref="AuditGateAnnotation"/> records with the same elements in
    /// the same order, or <c>null</c> when <paramref name="annotations"/> is <c>null</c>.
    /// </returns>
    internal static IReadOnlyList<AuditGateAnnotation>? ToAuditAnnotations(
        IReadOnlyList<GateAnnotationEntry>? annotations)
    {
        if (annotations is null)
            return null;

        return annotations
            .Select(e => new AuditGateAnnotation(e.Tag, e.Classification, e.Provenance))
            .ToList();
    }

    // ── Tag disclosure helper ─────────────────────────────────────────────────

    /// <summary>
    /// Builds the mandatory tag-creation disclosure text appended to the controller
    /// publish success message.
    ///
    /// <para>
    /// Returns an empty string when <paramref name="entries"/> is <c>null</c> or
    /// empty (a publish with no buffered tags and no <c>&lt;Tags&gt;</c> in the
    /// fragment says nothing about tags).
    /// </para>
    ///
    /// <para>
    /// When tags are present the returned string:
    /// <list type="bullet">
    ///   <item>Lists every tag name that was created.</item>
    ///   <item>States plainly that tag creation is immediate and permanent —
    ///     cancelling rung edits in Logix Designer will NOT remove them.</item>
    ///   <item>States that created tags are External Access Read Only: readable, and
    ///     not writable through lgx-mcp or any CIP client until an engineer sets them
    ///     to Read/Write in Logix Designer. It no longer flags
    ///     <c>Cmd_*</c>/<c>Sim_*</c> names as newly writable.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="entries">
    /// The published-tag disclosure list produced during the controller publish path,
    /// or <c>null</c> when no tags were merged.
    /// </param>
    /// <returns>
    /// A non-null disclosure string (may be empty when no tags were created).
    /// </returns>
    /// <summary>"1 rung" / "N rungs": <paramref name="noun"/> with the right plural for <paramref name="count"/>.</summary>
    /// <param name="count">The count.</param>
    /// <param name="noun">The singular noun, e.g. <c>"staged rung"</c>.</param>
    internal static string Plural(long count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>
    /// "rung landed at index 1" for one index, "rungs landed at indices 0 and 2" / "0, 1 and 2" for several.
    /// </summary>
    /// <param name="indices">The zero-based landed indices of one routine, ascending; must not be empty.</param>
    internal static string DescribeLandedIndices(IReadOnlyList<int> indices)
    {
        if (indices.Count == 1)
            return $"rung landed at index {indices[0]}";
        var list = string.Join(", ", indices.Take(indices.Count - 1)) + " and " + indices[^1];
        return $"rungs landed at indices {list}";
    }

    private static List<PublishedRoutine> RoutinesWithLanded(IReadOnlyList<PublishedRoutine>? published) =>
        published?.Where(r => r.LandedIndices is { Count: > 0 }).ToList() ?? [];

    /// <summary>
    /// The opening of a controller publish message, built from the census's landed indices: <c>Routine 'X', rung landed
    /// at index 1</c>; one clause per routine when several landed rungs (<c>Routine 'A', rung landed at index 0;
    /// routine 'B', rungs landed at indices 1 and 2</c>). Without landed indices it falls back to the import position.
    /// </summary>
    /// <param name="routineXPath">The call's routine.</param>
    /// <param name="published">The census's per-routine result; <c>null</c> when none.</param>
    /// <param name="insertPosition">The position the import was made at (fallback).</param>
    internal static string DescribeLandedHeader(
        string routineXPath, IReadOnlyList<PublishedRoutine>? published, uint insertPosition)
    {
        var landed = RoutinesWithLanded(published);
        if (landed.Count == 0)
            return $"Routine '{routineXPath}', insert position {insertPosition}";
        return string.Join("; ", landed.Select((r, i) =>
            $"{(i == 0 ? "Routine" : "routine")} '{r.Routine}', {DescribeLandedIndices(r.LandedIndices!)}"));
    }

    /// <summary>The note sentence naming where every routine's rungs landed, or <c>null</c> without landed indices.</summary>
    /// <param name="published">The census's per-routine result.</param>
    internal static string? DescribeLandedSentence(IReadOnlyList<PublishedRoutine>? published)
    {
        var landed = RoutinesWithLanded(published);
        if (landed.Count == 0)
            return null;
        return string.Join("; ", landed.Select(r => $"In '{r.Routine}' {DescribeLandedIndices(r.LandedIndices!)}")) + ".";
    }

    /// <summary>
    /// The top-level <c>insertPosition</c> of a controller publish: the first index the call's own routine's rungs
    /// landed at. Falls back to the import position when the census gave no landed indices for that routine.
    /// </summary>
    /// <param name="routineXPath">The call's routine.</param>
    /// <param name="published">The census's per-routine result.</param>
    /// <param name="insertPosition">The position the import was made at (fallback).</param>
    internal static uint LandedInsertPosition(
        string routineXPath, IReadOnlyList<PublishedRoutine>? published, uint insertPosition)
    {
        var own = RoutinesWithLanded(published).FirstOrDefault(r =>
            string.Equals(r.Routine, routineXPath, StringComparison.OrdinalIgnoreCase));
        return own is null ? insertPosition : (uint)own.LandedIndices!.Min();
    }

    /// <summary>
    /// The top-level <c>insertPosition</c> of a publish (G43): <c>null</c> when more than one routine was
    /// published (positions are per routine in <c>publishedRoutines</c>); otherwise <see cref="LandedInsertPosition"/>.
    /// </summary>
    /// <param name="routineXPath">The call's routine.</param>
    /// <param name="published">The census's per-routine result.</param>
    /// <param name="insertPosition">The position the import was made at (fallback).</param>
    internal static uint? ReportedInsertPosition(
        string routineXPath, IReadOnlyList<PublishedRoutine>? published, uint insertPosition) =>
        published is { Count: > 1 } ? null : LandedInsertPosition(routineXPath, published, insertPosition);

    /// <summary>
    /// Whether the "Caller's insertPosition (N) was ignored" sentence applies (G44): only when the first index the
    /// call's routine's rungs landed at differs from the caller's position, judged per routine (G52). Without landed indices the position cannot be
    /// shown to match, so the sentence stays.
    /// </summary>
    /// <param name="routineXPath">The call's routine.</param>
    /// <param name="published">The census's per-routine result.</param>
    /// <param name="callerInsertPosition">The <c>insertPosition</c> the caller passed.</param>
    internal static bool CallerInsertPositionIgnored(
        string routineXPath, IReadOnlyList<PublishedRoutine>? published, uint callerInsertPosition)
    {
        // G52: the caller's position belongs to the call's own routine, so the rule is applied to that routine's
        // landed indices even when several routines were published (other routines carry no caller position).
        var own = RoutinesWithLanded(published).FirstOrDefault(r =>
            string.Equals(r.Routine, routineXPath, StringComparison.OrdinalIgnoreCase));
        return own is null || (uint)own.LandedIndices!.Min() != callerInsertPosition;
    }

    /// <summary>"replacing N rungs" with correct plurals, or "replacing no rungs" for 0.</summary>
    /// <param name="replaceCount">The number of existing rungs replaced.</param>
    internal static string DescribeReplacing(uint replaceCount) =>
        replaceCount == 0 ? "replacing no rungs" : $"replacing {Plural(replaceCount, "rung")}";

    /// <summary>
    /// Says what a controller publish sent (N10): a tags-only publish is "tags sent; no rungs"; otherwise
    /// the staged rung count with correct plurals, the replace count (the sum over the staged buckets plus
    /// this call's own), and the tags created.
    /// </summary>
    /// <param name="rungs">Rungs sent (staged plus the call's own).</param>
    /// <param name="tags">Tag declarations created by the publish.</param>
    /// <param name="replaceCount">Existing rungs the publish replaced.</param>
    /// <param name="ownRungs">How many of <paramref name="rungs"/> are the call's own rather than staged (default 0).</param>
    internal static string DescribePublishContents(int rungs, int tags, uint replaceCount, int ownRungs = 0)
    {
        if (rungs == 0)
            return tags > 0 ? "tags sent; no rungs" : "no rungs or tags sent";

        var text = $"{PublishedRungsPhrase(rungs, ownRungs)} sent, {DescribeReplacing(replaceCount)}";
        return tags > 0 ? $"{text}; {Plural(tags, "tag")} created" : text;
    }

    /// <summary>
    /// The hint for a <c>StagedEditLost</c> error. It says to cancel and re-stage only when the session holds other
    /// staged rungs; when nothing else is staged, there is nothing to re-stage (G8).
    /// </summary>
    /// <param name="otherStagedRungs">Rungs recorded in the session's staging buffer besides the lost one.</param>
    internal static string StagedEditLostHint(int otherStagedRungs) =>
        otherStagedRungs > 0
            ? "Check logix_pending_edits for the real state of the session image, then cancel the " +
              "staged edits and re-stage them."
            : "Check logix_pending_edits for the real state of the session image. No other rungs are " +
              "staged in this session, so there is nothing besides this rung to cancel or re-stage.";

    /// <summary>
    /// "3 staged rungs" when every rung sent came from the staging buffer, "2 rungs" when all of them are the call's own
    /// (a direct publish, nothing was staged), and "3 rungs (1 staged, 2 from this call)" for a mix (G28).
    /// </summary>
    /// <param name="total">Rungs sent, staged plus the call's own.</param>
    /// <param name="ownRungs">How many of them are the call's own rungs rather than staged ones.</param>
    internal static string PublishedRungsPhrase(int total, int ownRungs)
    {
        var own = Math.Clamp(ownRungs, 0, total);
        if (own == 0)
            return Plural(total, "staged rung");
        if (own == total)
            return Plural(total, "rung");
        return $"{Plural(total, "rung")} ({total - own} staged, {own} from this call)";
    }

    /// <summary>
    /// Says what a controller publish sent when it spans routines: the total staged rung count, then a
    /// per-routine breakdown when more than one routine had rungs, e.g.
    /// <c>3 staged rungs sent (BeaconRoutine 2, MainRoutine 1), replacing no rungs</c>.
    /// </summary>
    /// <param name="rungsByRoutine">Rungs sent per routine XPath (staged plus the call's own).</param>
    /// <param name="tags">Tag declarations created by the publish.</param>
    /// <param name="replaceCount">Existing rungs the publish replaced, over all routines.</param>
    /// <param name="ownRungs">How many of the rungs are the call's own rather than staged (default 0).</param>
    internal static string DescribePublishContents(
        IReadOnlyDictionary<string, int> rungsByRoutine, int tags, uint replaceCount, int ownRungs = 0)
    {
        var sent = rungsByRoutine.Where(kv => kv.Value > 0).ToList();
        var total = sent.Sum(kv => kv.Value);
        var text = DescribePublishContents(total, tags, replaceCount, ownRungs);
        if (sent.Count < 2)
            return text;

        // Short routine names; fall back to Program/Routine when two programs share a routine name.
        var names = sent.Select(kv => RoutineNamePattern.Match(kv.Key).Groups[1].Value).ToList();
        var duplicate = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
        var parts = sent.Select((kv, i) =>
            {
                var label = string.IsNullOrEmpty(names[i]) ? kv.Key : names[i];
                if (duplicate && !string.IsNullOrEmpty(names[i]))
                    label = ProgramNamePattern.Match(kv.Key).Groups[1].Value + "/" + names[i];
                return (Label: label, Count: kv.Value);
            })
            .OrderBy(p => p.Label, StringComparer.Ordinal)
            .Select(p => $"{p.Label} {p.Count}");
        var marker = $"{PublishedRungsPhrase(total, ownRungs)} sent";
        return text.Replace(marker, $"{marker} ({string.Join(", ", parts)})", StringComparison.Ordinal);
    }

    /// <summary>Staged rung counts keyed by routine XPath, over every routine in <paramref name="buffer"/>.</summary>
    /// <param name="buffer">The session's staged rung buffer, keyed by (routine, position).</param>
    internal static Dictionary<string, int> CountStagedRungsByRoutine(
        IReadOnlyDictionary<(string RoutineXPath, uint InsertPosition), List<string>> buffer)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, texts) in buffer)
            result[key.RoutineXPath] = result.GetValueOrDefault(key.RoutineXPath) + texts.Count;
        return result;
    }

    /// <summary>A copy of <paramref name="counts"/> with <paramref name="extra"/> rungs added to <paramref name="routineXPath"/>.</summary>
    private static Dictionary<string, int> AddRungs(
        IReadOnlyDictionary<string, int> counts, string routineXPath, int extra)
    {
        var copy = new Dictionary<string, int>(counts, StringComparer.OrdinalIgnoreCase);
        if (extra > 0)
            copy[routineXPath] = copy.GetValueOrDefault(routineXPath) + extra;
        return copy;
    }

    /// <summary>The rungs of <paramref name="routineXPath"/> in <paramref name="fragment"/>; 0 when unreadable.</summary>
    private static int CountFragmentRungs(string? fragment, string routineXPath)
    {
        if (string.IsNullOrWhiteSpace(fragment))
            return 0;
        try { return RoutineRungSelector.Select(SafeXml.ParseDocument(fragment), routineXPath).Count; }
        catch (Exception) { return 0; }
    }

    /// <summary>Sum of the replace counts of the staged buckets of <paramref name="routineXPath"/>.</summary>
    internal static uint SumStagedReplaceCountsForRoutine(
        IReadOnlyDictionary<(string RoutineXPath, uint InsertPosition), uint> counts, string routineXPath) =>
        (uint)counts.Where(kv => string.Equals(kv.Key.RoutineXPath, routineXPath, StringComparison.OrdinalIgnoreCase))
                    .Sum(kv => (long)kv.Value);

    /// <summary>
    /// Names of the tags in <paramref name="externalAccessForced"/> that were created External Access
    /// <c>Read/Write</c> (entries of the form <c>"Name: Read/Write (reason)"</c>); bare-name entries were forced
    /// Read Only and are not included.
    /// </summary>
    /// <param name="externalAccessForced">The call's <c>externalAccessForced</c> entries, or <c>null</c>.</param>
    internal static HashSet<string> ReadWriteForcedNames(IReadOnlyList<string>? externalAccessForced)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (externalAccessForced is null)
            return names;
        foreach (var entry in externalAccessForced)
        {
            var colon = entry.IndexOf(": Read/Write", StringComparison.Ordinal);
            if (colon > 0)
                names.Add(entry[..colon]);
        }
        return names;
    }

    /// <summary>
    /// The tag-creation disclosure appended to a controller publish message. Tags created External Access Read Only
    /// are described as such; tags the server created Read/Write (<paramref name="externalAccessForced"/>) are not
    /// called Read Only (G28).
    /// </summary>
    /// <param name="entries">The tags this publish created.</param>
    /// <param name="externalAccessForced">The call's <c>externalAccessForced</c> entries, or <c>null</c>.</param>
    internal static string BuildTagDisclosure(
        IReadOnlyList<PublishedTagEntry>? entries, IReadOnlyList<string>? externalAccessForced = null)
    {
        if (entries is null || entries.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.Append(" TAGS CREATED: ");
        sb.Append(string.Join(", ", entries.Select(e => e.TagName)));
        sb.Append(". Tag creation is immediate on the controller and is NOT a pending edit" +
                  " — cancelling rung edits in Logix Designer will NOT remove these tags.");

        var readWrite = ReadWriteForcedNames(externalAccessForced);
        var readWriteCreated = entries.Select(e => e.TagName).Where(readWrite.Contains).ToList();
        var readOnlyCreated  = entries.Select(e => e.TagName).Where(n => !readWrite.Contains(n)).ToList();

        // Created tags are External Access Read Only. This replaces the
        // This change text that flagged Cmd_*/Sim_* names as newly writable.
        if (readWriteCreated.Count == 0)
        {
            sb.Append(" Created tags are External Access Read Only; they are readable and not" +
                      " writable through lgx-mcp or any CIP client until an engineer sets them" +
                      " to Read/Write in Logix Designer (logix_write denies them as" +
                      " ExternalAccessNotReadWrite).");
        }
        else
        {
            sb.Append($" {string.Join(", ", readWriteCreated)} {(readWriteCreated.Count == 1 ? "was" : "were")} created" +
                      " External Access Read/Write because Logix rejects Read Only for the data type" +
                      " (see externalAccessForced).");
            if (readOnlyCreated.Count > 0)
                sb.Append($" {string.Join(", ", readOnlyCreated)} {(readOnlyCreated.Count == 1 ? "is" : "are")}" +
                          " External Access Read Only: readable, not writable through lgx-mcp or any CIP client" +
                          " until an engineer sets External Access to Read/Write in Logix Designer.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Maps a failed import that names <c>RxCsE_ASA_BLOCKING_HIGHER_EXCL_LOCK</c> (in the exception text or any
    /// captured log entry) to kind <c>ControllerLocked</c> with the retryable hint, and reports the retries made.
    /// Returns <c>null</c> for every other failure.
    /// </summary>
    /// <param name="sdkDetail">The failed import's exception text.</param>
    /// <param name="capture">The import log captured during the failed import.</param>
    /// <param name="retryState">The retries the bounded retry made.</param>
    /// <param name="logMark">Number of error entries captured before the last try; only later ones count.</param>
    /// <param name="statusMark">Number of status entries captured before the last try; only later ones count.</param>
    private static ProposeRungEditError? ControllerLockedError(
        string sdkDetail, ImportOperationEventCapture capture, ControllerLockRetryState retryState,
        int logMark, int statusMark)
    {
        bool named = RockwellErrorHints.IsMatch(sdkDetail, RockwellErrorHintIds.ControllerLocked)
            || capture.Errors.Skip(logMark).Any(e => RockwellErrorHints.IsMatch(e, RockwellErrorHintIds.ControllerLocked))
            || capture.StatusMessages.Skip(statusMark).Any(m => RockwellErrorHints.IsMatch(m, RockwellErrorHintIds.ControllerLocked));
        if (!named) return null;

        return new ProposeRungEditError(
            $"The controller is locked by another client or Studio (RxCsE_ASA_BLOCKING_HIGHER_EXCL_LOCK); " +
            $"the import was retried {retryState.Retries} time(s) and still failed. Nothing was imported. SDK detail: {sdkDetail}",
            Hint:    RockwellErrorHints.ControllerLockedHint,
            Kind:    ErrorKinds.ControllerLocked,
            Retries: retryState.Retries);
    }

    /// <summary>
    /// Finishes a failed-import error: adds the tags declared in the fragment and the structured
    /// <c>importLog</c> parsed from the callbacks captured during the import (an empty array when none were
    /// captured). Any recognised entry hint is also appended to the error text.
    /// </summary>
    private static ProposeRungEditError WithImportFailure(
        ProposeRungEditError error, string fragment, ImportOperationEventCapture capture)
    {
        var rungCount = ImportLogParser.CountRungs(fragment);
        var log   = ImportLogParser.Parse(capture.Errors, capture.StatusMessages, rungCount);
        var hints = log.Where(e => e.Hint is not null)
                       .Select(e => e.Hint!)
                       .Distinct()
                       .ToList();
        var text  = hints.Count == 0
            ? error.Error
            : error.Error + "  ImportLog hint: " + string.Join(" ", hints);
        var result = error with { Error = text, ImportLog = log };
        if (result.RejectReasons is null && result.RejectReasonSource is null &&
            result.Kind != ErrorKinds.ControllerBusy &&
            ImportLogRejectReasons(log) is { Count: > 0 } reasons)
        {
            result = result with { RejectReasons = reasons, RejectReasonSource = ImportRejectReplay.SourceController };
        }
        // A single-rung import: any reject that names no rung belongs to rung 0. Reasons that already name a
        // rung (import-log attribution) are kept as they are; multi-rung rejects naming none stay null.
        if (result.RejectReasons is { Count: > 0 } existing &&
            result.RejectReasonSource == ImportRejectReplay.SourceController)
            result = result with { RejectReasons = AttributeSingleRung(existing, rungCount) };
        return WithTagsInFragment(result, fragment);
    }

    /// <summary>
    /// Gives rung 0 to the reasons that name no rung when the import set has exactly one rung. Reasons that already
    /// name a rung (the import log's attribution is preferred) and all reasons of a multi-rung import are unchanged.
    /// </summary>
    /// <param name="reasons">The reject reasons.</param>
    /// <param name="fragmentRungCount">The number of rungs in the imported fragment.</param>
    /// <returns>The reasons with unnamed rungs attributed to rung 0 for a single-rung import.</returns>
    internal static IReadOnlyList<RejectReason> AttributeSingleRung(
        IReadOnlyList<RejectReason> reasons, int fragmentRungCount) =>
        fragmentRungCount == 1 && reasons.Any(r => r.RungIndex is null)
            ? reasons.Select(r => r.RungIndex is null ? r with { RungIndex = 0 } : r).ToList()
            : reasons;

    /// <summary>
    /// Returns <paramref name="error"/> with <see cref="ProposeRungEditError.TagsInFragment"/> set to the
    /// names of the tags declared in <paramref name="fragment"/>, the text that was sent to the SDK.
    /// </summary>
    /// <param name="error">The import-failure error to annotate.</param>
    /// <param name="fragment">The L5X fragment that was imported.</param>
    private static ProposeRungEditError WithTagsInFragment(ProposeRungEditError error, string fragment) =>
        error with { TagsInFragment = TagNamesInFragment(fragment) };

    /// <summary>
    /// Lists the names of every <c>Tag</c> declared under a <c>Tags</c> element of an L5X fragment,
    /// in document order. Returns an empty list when the fragment cannot be parsed.
    /// </summary>
    /// <param name="fragment">The L5X fragment text.</param>
    internal static IReadOnlyList<string> TagNamesInFragment(string fragment)
    {
        try
        {
            return SafeXml.ParseDocument(fragment)
                .Descendants("Tags").Elements("Tag")
                .Select(t => (string?)t.Attribute("Name"))
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Builds the suffix appended to a controller-publish rejection error message
    /// naming the staged tags that were merged into the fragment for this publish
    /// attempt (see <see cref="MergeBufferedTagsIntoFragment"/>).
    /// <para>
    /// Every SDK import-rejection catch on the controller (publish) path calls this
    /// so a caller can distinguish a buffer-induced failure from a rung-text
    /// problem: the session's staged-tag buffer is left intact by a rejected
    /// publish (it is only cleared after a confirmed success — see the
    /// "Clear staged buffer after successful controller publish" block above), so
    /// the exact same buffered tags are re-merged and retried on every subsequent
    /// controller publish in this session until one succeeds.
    /// </para>
    /// </summary>
    /// <param name="mergedTagNames">
    /// The names returned via <see cref="MergeBufferedTagsIntoFragment"/>'s
    /// <c>mergedTagNames</c> out parameter for this publish attempt. Empty when
    /// the staging buffer was empty (or every buffered name collided with a
    /// fragment-declared tag), in which case the returned text names an empty list.
    /// </param>
    private static string BuildMergedStagedTagsNote(IReadOnlyList<string> mergedTagNames) =>
        $" Merged staged tag names for this publish attempt: [{string.Join(", ", mergedTagNames)}]. " +
        "This session's staged-tag buffer is retried on every controller publish in this " +
        "session — it is not cleared by a rejected publish — so these tags will be re-merged " +
        "and resent on the next logix_propose_rung_edit call with target='controller'.";

    /// <summary>The refusal for an explicit-comment replace whose original comment cannot be restored on cancel.</summary>
    private static ProposeRungEditError CommentNotReversibleError(string routineXPath, string path, string why) =>
        new($"An explicit comment on a replace is written to the original rung immediately, and on the {path} path " +
            $"cancelling the edit would not bring the original comment back: {why}. Nothing was imported into '{routineXPath}'.",
            Hint: CommentNotReversibleHint,
            Kind: "CommentNotReversible");

    /// <summary>Hint carried by <c>CommentNotReversible</c>.</summary>
    internal const string CommentNotReversibleHint =
        "omit comment to keep the original, or set it after the edit is finalized";

    /// <summary>
    /// The explicit comment each of the first <paramref name="replaceCount"/> fragment rungs carries, by fragment index:
    /// a typed rung's <c>comment</c> (an empty string included), else the text of a <c>&lt;Comment&gt;</c> element in the
    /// fragment. A rung that supplies none is absent. Never throws: an unparseable fragment yields an empty map.
    /// </summary>
    private static Dictionary<int, string> ExplicitFragmentComments(
        string fragment, uint replaceCount, IReadOnlyList<RungInput>? typedRungs, string? routineXPath)
    {
        var map = new Dictionary<int, string>();
        if (replaceCount == 0) return map;
        try
        {
            var fragmentRungs = RoutineRungSelector.Select(SafeXml.ParseDocument(fragment), routineXPath);
            for (int i = 0; i < fragmentRungs.Count && i < (int)replaceCount; i++)
            {
                if (typedRungs is not null && i < typedRungs.Count && typedRungs[i].Comment is { } typed)
                    map[i] = typed;
                else if (fragmentRungs[i].Element("Comment") is { } c)
                    map[i] = c.Value;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            map.Clear();
        }
        return map;
    }

    /// <summary>True when the fragment gives any rung it replaces an explicit comment.</summary>
    private static bool HasExplicitCommentIntent(
        string fragment, uint replaceCount, IReadOnlyList<RungInput>? typedRungs, string? routineXPath) =>
        ExplicitFragmentComments(fragment, replaceCount, typedRungs, routineXPath).Count > 0;

    /// <summary>
    /// Finds the replaced rungs whose explicit comment differs from the original and records each original
    /// (index, raw text, comment) in <paramref name="originals"/>. A comment equal to the original changes nothing and
    /// needs no undo.
    /// </summary>
    /// <returns>The routine rung indexes whose comment this stage changes.</returns>
    internal static List<int> FindExplicitCommentChanges(
        string fragment, IReadOnlyList<XElement> preImportRungs, uint insertPosition, uint replaceCount,
        IReadOnlyList<RungInput>? typedRungs, string? routineXPath,
        List<(int Index, string Text, string Comment)> originals)
    {
        var changed = new List<int>();
        foreach (var (i, explicitComment) in ExplicitFragmentComments(fragment, replaceCount, typedRungs, routineXPath))
        {
            int idx = (int)insertPosition + i;
            if (idx >= preImportRungs.Count) continue;
            var original = preImportRungs[idx].Element("Comment")?.Value ?? string.Empty;
            if (string.Equals(original.Trim(), explicitComment.Trim(), StringComparison.Ordinal)) continue;
            changed.Add(idx);
            originals.Add((idx, preImportRungs[idx].Element("Text")?.Value ?? string.Empty, original));
        }
        return changed;
    }

    /// <summary>
    /// Carries the existing <c>&lt;Comment&gt;</c> of each rung being replaced into the
    /// corresponding fragment rung (the i-th fragment rung replaces the rung at
    /// <paramref name="insertPosition"/> + i, for i &lt; <paramref name="replaceCount"/>)
    /// when the fragment rung has no <c>&lt;Comment&gt;</c> of its own. A fragment rung
    /// that carries a <c>&lt;Comment&gt;</c> is never overwritten, nor is a typed rung whose
    /// <c>comment</c> is explicitly supplied (an empty string means "no comment"). When nothing is
    /// carried over the fragment is returned unchanged (byte-for-byte).
    /// </summary>
    /// <param name="fragment">The L5X fragment about to be imported.</param>
    /// <param name="preImportRungs">The routine's rungs captured before the import.</param>
    /// <param name="insertPosition">Zero-based index of the first replaced rung.</param>
    /// <param name="replaceCount">Number of existing rungs the import replaces.</param>
    /// <param name="preservedRungs">Routine rung numbers whose comment was carried over.</param>
    /// <param name="typedRungs">The typed rungs the fragment was built from, when any.</param>
    /// <returns>The fragment text to import.</returns>
    internal static string PreserveExistingRungComments(
        string                fragment,
        IReadOnlyList<XElement> preImportRungs,
        uint                  insertPosition,
        uint                  replaceCount,
        out List<int>         preservedRungs,
        IReadOnlyList<RungInput>? typedRungs = null,
        string?               routineXPath = null)
    {
        preservedRungs = [];
        var doc = SafeXml.ParseDocument(fragment);
        var fragmentRungs = RoutineRungSelector.Select(doc, routineXPath);

        for (int i = 0; i < fragmentRungs.Count && i < (int)replaceCount; i++)
        {
            int idx = (int)insertPosition + i;
            if (idx >= preImportRungs.Count)
                break;
            if (fragmentRungs[i].Element("Comment") is not null)
                continue;
            // A typed rung with an explicit comment (even "") is authoritative: an undo
            // block restores "no comment" as "" and that must not pick up the current comment.
            if (typedRungs is not null && i < typedRungs.Count && typedRungs[i].Comment is not null)
                continue;
            var existing = preImportRungs[idx].Element("Comment");
            if (existing is null || string.IsNullOrWhiteSpace(existing.Value))
                continue;

            // <Comment> precedes <Text> in an L5X rung.
            fragmentRungs[i].AddFirst(new XElement(existing));
            preservedRungs.Add(idx);
        }

        if (preservedRungs.Count == 0)
            return fragment;

        var declaration = doc.Declaration?.ToString() ?? new XDeclaration("1.0", "UTF-8", "yes").ToString();
        return declaration + "\r\n" + doc.Root!.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// The comment a landed rung carries on the controller: for one of this call's own fragment rungs, the comment the
    /// fragment imported (a preserved original or an explicit new one, none when it had none); for any other landed
    /// (staged) rung, the comment read back from the post-publish export.
    /// </summary>
    /// <param name="landedText">Normalized text of the landed rung.</param>
    /// <param name="exportedComment">The comment on the rung in the post-publish export, or <c>null</c>.</param>
    /// <param name="censusRoutine">The routine the rung landed in.</param>
    /// <param name="callRoutine">The routine this call's own fragment targets.</param>
    /// <param name="ownFragmentComments">This call's fragment rungs: normalized text and imported comment.</param>
    /// <returns>The comment, or <c>null</c> for none.</returns>
    internal static string? LandedRungComment(
        string landedText, string? exportedComment, string censusRoutine, string callRoutine,
        IReadOnlyList<(string Text, string? Comment)> ownFragmentComments)
    {
        if (string.Equals(censusRoutine, callRoutine, StringComparison.OrdinalIgnoreCase))
            foreach (var (text, comment) in ownFragmentComments)
                if (RungTextComparer.AnyExpectedMatches([text], landedText))
                    return comment;
        return string.IsNullOrWhiteSpace(exportedComment) ? null : exportedComment;
    }

    // ── target="live" post-import verification ─────────────────────

    /// <summary>
    /// Converts exported <c>&lt;Rung&gt;</c> elements into number/type/normalized-text
    /// entries (a missing <c>Type</c> attribute means <c>N</c>, a normal rung).
    /// </summary>
    /// <param name="rungs">The routine's <c>&lt;Rung&gt;</c> elements in document order.</param>
    /// <returns>One <see cref="PendingEditRungEntry"/> per rung.</returns>
    private static IReadOnlyList<PendingEditRungEntry> SnapshotRungs(IEnumerable<XElement> rungs) =>
        rungs.Select((r, i) => new PendingEditRungEntry(
            i,
            (string?)r.Attribute("Type") ?? "N",
            NormalizeRungText(r.Element("Text")?.Value))).ToList();

    /// <summary>Compact one-line rendering of a rung snapshot for error messages.</summary>
    private static string DescribeRungs(IReadOnlyList<PendingEditRungEntry>? rungs) =>
        rungs is null
            ? "unreadable"
            : "[" + string.Join(", ", rungs.Select(r => $"{r.Number}:{r.Type}:{r.Text}")) + "]";

    /// <summary>
    /// Verifies a <c>target="live"</c> FinalizeEdits import by re-exporting the routine.
    /// Confirmed only when (a) no rung is non-normal (all <c>N</c>), (b) the imported
    /// rung texts sit at <paramref name="insertPosition"/> onwards, and (c) a fresh
    /// <see cref="PendingEditsHandler"/> read reports state <c>none</c>.
    /// </summary>
    /// <param name="routineXPath">XPath of the routine that was imported into.</param>
    /// <param name="program">Program name parsed from the XPath.</param>
    /// <param name="routine">Routine name parsed from the XPath.</param>
    /// <param name="insertPosition">Zero-based position the import inserted at.</param>
    /// <param name="importedFragment">The fragment XML that was imported.</param>
    /// <param name="session">The open online session.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Whether the state is confirmed clean, the after-rungs (null when the export
    /// was unreadable), and a human-readable description of what failed.
    /// </returns>
    private static async Task<(bool Confirmed, IReadOnlyList<PendingEditRungEntry>? AfterRungs, string Detail)>
        VerifyLiveFinalizeAsync(
            string              routineXPath,
            string              program,
            string              routine,
            uint                insertPosition,
            string              importedFragment,
            LogixProjectSession session,
            CancellationToken   ct,
            IReadOnlyList<PendingEditRungEntry>? deleteBeforeRungs = null,
            uint                deleteCount = 0)
    {
        var export = await ExportRungsHandler.ExecuteAsync(routineXPath, session, ct)
            .ConfigureAwait(false);
        if (export is not ExportRungsResult exported)
        {
            var detail = (export as ExportRungsError)?.Error ?? "unexpected response";
            return (false, null, $"the routine could not be re-exported ({detail})");
        }

        IReadOnlyList<PendingEditRungEntry> after;
        List<string> importedTexts;
        try
        {
            after = SnapshotRungs(RoutineRungSelector.Select(SafeXml.ParseDocument(exported.L5X), routineXPath));
            importedTexts = RoutineRungSelector.Select(SafeXml.ParseDocument(importedFragment), routineXPath)
                .Select(r => NormalizeRungText(r.Element("Text")?.Value)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, null, $"the re-export could not be parsed ({ex.Message})");
        }

        var problems = new List<string>();

        var notNormal = after.Where(r => r.Type != "N").ToList();
        if (notNormal.Count > 0)
            problems.Add("rung(s) still pending: " +
                string.Join(", ", notNormal.Select(r => $"{r.Number}:{r.Type}")));

        for (int i = 0; i < importedTexts.Count; i++)
        {
            int idx = (int)insertPosition + i;
            if (idx >= after.Count ||
                !RungTextComparer.Matches(importedTexts[i], after[idx].Text))
            {
                problems.Add($"imported rung {i} is not at position {idx}");
            }
        }

        // This change live delete: the routine must have exactly before - deleteCount rungs
        // and every rung that followed the deleted ones must have shifted up unchanged.
        // Rungs before insertPosition must be untouched as well.
        if (deleteBeforeRungs is not null)
        {
            int expectedCount = deleteBeforeRungs.Count - (int)deleteCount;
            if (after.Count != expectedCount)
                problems.Add($"expected {expectedCount} rung(s) after deleting {deleteCount} " +
                    $"of {deleteBeforeRungs.Count}, found {after.Count}");
            else
            {
                for (int k = 0; k < after.Count; k++)
                {
                    int src = k < (int)insertPosition ? k : k + (int)deleteCount;
                    if (!RungTextComparer.Matches(deleteBeforeRungs[src].Text, after[k].Text))
                    {
                        problems.Add($"rung {k} does not match the text of former rung {src}");
                        break;
                    }
                }
            }
        }

        object pendingResponse;
        try
        {
            pendingResponse = await PendingEditsHandler.ExecuteAsync(program, routine, session, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pendingResponse = new PendingEditsError(ex.Message);
        }

        if (pendingResponse is PendingEditsResult pending)
        {
            var dirty = pending.Routines.Where(r => r.State != "none").ToList();
            if (dirty.Count > 0)
                problems.Add("logix_pending_edits reports " + string.Join("; ",
                    dirty.Select(r => $"{r.Program}/{r.Routine} state={r.State}")));
        }
        else
        {
            problems.Add("logix_pending_edits could not be read (" +
                ((pendingResponse as PendingEditsError)?.Error ?? "unexpected response") + ")");
        }

        return (problems.Count == 0, after, string.Join("; ", problems));
    }

    // ── logix-mcp-h50h.6 — pending-edit pre-check for a controller publish ────

    /// <summary>
    /// Refuses a <c>target="controller"</c> publish with <c>PendingEditsBlockImport</c> when any routine
    /// of the target program holds edits (state other than <c>none</c>), read program-wide the way
    /// <c>logix_pending_edits</c> and <c>logix_create_routine</c> read them. Performs only a read; the
    /// caller must not import when this returns a refusal. A state that cannot be read (for example a
    /// program with no rung yet) is not a refusal here: the import proceeds and a refusal by the SDK is
    /// still classified afterwards.
    /// </summary>
    /// <param name="routineXPath">Canonical XPath of the target routine.</param>
    /// <param name="session">The open online session.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ProposeRungEditError"/> of kind <c>PendingEditsBlockImport</c>, or <c>null</c> to proceed.</returns>
    private static async Task<ProposeRungEditError?> CheckControllerPendingEditsAsync(
        string routineXPath, LogixProjectSession session, CancellationToken ct)
    {
        var programMatch = ProgramNamePattern.Match(routineXPath);
        if (!programMatch.Success)
            return null;
        var program = programMatch.Groups[1].Value;

        object read;
        try
        {
            read = await PendingEditsHandler.ExecuteAsync(program, null, session, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        if (read is not PendingEditsResult result)
            return null;

        var held = result.Routines.Where(r => r.State != "none").ToList();
        if (held.Count == 0)
            return null;

        return new ProposeRungEditError(
            $"PendingEditsBlockImport: program '{program}' has edits that are not yet assembled " +
            $"({string.Join(", ", held.Select(r => $"{r.Routine}: {r.State}"))}). The SDK refuses an import " +
            "into a program that has them, so the publish was not sent. No import was attempted.",
            Hint:
                "Resolve them first: logix_assemble_pending_edits (finalize) or logix_cancel_pending_edits " +
                "(needs LGX_ENABLE_STUDIO_AUTOMATION=1), or accept, finalize or cancel them in Logix Designer " +
                "(Logic -> Online Edits). Then publish again. target='workstation' can still stage further " +
                "edits meanwhile.",
            DocsAnchor: PendingEditsPresentDocsAnchor,
            Kind:       "PendingEditsBlockImport");
    }

    // ── logix-mcp-qpqy.12 — pre-check reported with a pending-edits block ─────

    /// <summary>
    /// Runs the tag and instruction pre-check for a publish that the pending-edit gate has refused, so the
    /// refusal can report both problems. Builds the same effective fragment the publish would send (typed input,
    /// a rebuilt hand-written fragment or the caller's fragment as is) and checks it together with the rungs
    /// already staged. Performs only reads; never imports, stages or throws (other than for cancellation). A
    /// fragment that cannot be built, or a pre-check that cannot be run, reports nothing.
    /// </summary>
    /// <param name="routineXPath">The canonical routine XPath.</param>
    /// <param name="insertPosition">The publish's insert position.</param>
    /// <param name="l5xFragment">The caller's fragment, if any.</param>
    /// <param name="typedRungs">The normalized typed rungs.</param>
    /// <param name="tags">The typed tags.</param>
    /// <param name="hasTypedInput">Whether the call carries typed rungs or tags.</param>
    /// <param name="census">Tag names the caller's census knows.</param>
    /// <param name="session">The open online session.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The pre-check refusal, or <c>null</c> when it found nothing to refuse (or could not run).</returns>
    private static async Task<ProposeRungEditError?> PreCheckBlockedPublishAsync(
        string                       routineXPath,
        uint                         insertPosition,
        string?                      l5xFragment,
        IReadOnlyList<RungInput>     typedRungs,
        IReadOnlyList<TagInput>?     tags,
        bool                         hasTypedInput,
        IReadOnlyCollection<string>? census,
        LogixProjectSession          session,
        CancellationToken            ct)
    {
        try
        {
            string fragment;
            if (hasTypedInput || string.IsNullOrWhiteSpace(l5xFragment))
            {
                var exported = await ExportAndStripRungsAsync(routineXPath, session, ct).ConfigureAwait(false);
                if (exported is not string envelope) return null;
                fragment = hasTypedInput
                    ? RungFragmentBuilder.BuildOnline(envelope, insertPosition, typedRungs, tags)
                    : envelope;
            }
            else
            {
                if (ValidateFragmentEnvelope(l5xFragment) is not null) return null;
                var plan = PlanRawFragment(l5xFragment, routineXPath);
                if (plan.Error is not null) return null;
                if (plan.Rebuild)
                {
                    var exported = await ExportAndStripRungsAsync(routineXPath, session, ct).ConfigureAwait(false);
                    if (exported is not string envelope) return null;
                    fragment = RungFragmentBuilder.BuildOnline(envelope, insertPosition, plan.Rungs, plan.Tags);
                }
                else
                {
                    fragment = l5xFragment;
                }
            }

            var (error, _) = await CheckReferencesAsync(
                routineXPath, fragment, sendToController: true, census, session, ct).ConfigureAwait(false);
            return error;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds the tag and instruction pre-check result to a <c>PendingEditsBlockImport</c> refusal. The refusal keeps
    /// its kind; the message and hint name both problems and <c>preCheck</c> carries the pre-check's own kind and
    /// every finding.
    /// </summary>
    /// <param name="blocked">The pending-edits refusal.</param>
    /// <param name="preCheck">The pre-check refusal, or <c>null</c> when the pre-check found nothing.</param>
    /// <returns><paramref name="blocked"/> unchanged when <paramref name="preCheck"/> is <c>null</c>.</returns>
    internal static ProposeRungEditError AttachPreCheck(ProposeRungEditError blocked, ProposeRungEditError? preCheck)
    {
        if (preCheck is null) return blocked;
        return blocked with
        {
            Error = blocked.Error + $" The tag and instruction pre-check also failed ({preCheck.Kind}): {preCheck.Error}",
            Hint  = preCheck.Hint is null ? blocked.Hint : blocked.Hint + " Also fix the pre-check: " + preCheck.Hint,
            PreCheck = new PublishPreCheckReport(preCheck.Kind, preCheck.Error, preCheck.Hint, preCheck.RejectReasons),
        };
    }

    // ── target="live" pre-import gates ─────────────────────────────

    /// <summary>
    /// Runs gates 3–5 for <c>target="live"</c> in order and returns the first refusal,
    /// or <c>null</c> when every gate passes. Performs only reads (pending-edits read,
    /// rung export); never imports.
    /// <list type="number">
    ///   <item><c>StagedRungsPresent</c> — the session's staged rung buffer must be
    ///   empty (the staged tag buffer is allowed and is merged into the import).</item>
    ///   <item><c>PendingZoneExists</c> — a fresh <see cref="PendingEditsHandler"/> read for
    ///   the routine's program must report state <c>none</c>.</item>
    ///   <item><c>ExpectedRungTextRequired</c> / <c>StaleTarget</c> — when
    ///   <paramref name="replaceCount"/> &gt; 0, <paramref name="expectedRungText"/> must carry
    ///   one entry per replaced rung and match a fresh export of those rungs, both sides
    ///   normalized with <see cref="NormalizeRungText"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="routineXPath">XPath of the target routine.</param>
    /// <param name="insertPosition">Zero-based index of the first replaced/inserted rung.</param>
    /// <param name="replaceCount">Number of existing rungs the call replaces.</param>
    /// <param name="session">The open online session.</param>
    /// <param name="expectedRungText">Caller's last-read text of each rung to be replaced.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ProposeRungEditError"/> refusal, or <c>null</c> to proceed.</returns>
    private static async Task<ProposeRungEditError?> CheckLiveTargetGatesAsync(
        string                 routineXPath,
        uint                   insertPosition,
        uint                   replaceCount,
        LogixProjectSession    session,
        IReadOnlyList<string>? expectedRungText,
        CancellationToken      ct)
    {
        // Gate 3 — staged rung buffer must be empty.
        int stagedRungs = Math.Max(
            session.StagedRungCount,
            session.StagedRungBuffer.Values.Sum(v => v.Count));
        if (stagedRungs > 0 || session.HasStagedRungEdit)
        {
            return new ProposeRungEditError(
                $"target='live' refused: this session has {Plural(stagedRungs, "staged rung")} " +
                "(target='workstation'). A live import finalizes immediately and cannot " +
                "carry a staged rung set. No import was attempted.",
                Hint:
                    "Publish the staged rungs first with target='controller' (and accept or " +
                    "cancel the resulting pending edit), or discard them with logix_clear_staged (it also removes them from the " +
                    "session image), then retry " +
                    "target='live'. Staged tags are allowed and are merged into the import.",
                Kind: "StagedRungsPresent");
        }

        // Gate 4 — the routine's program must have no pending edit zone.
        var programMatch = ProgramNamePattern.Match(routineXPath);
        var routineMatch = RoutineNamePattern.Match(routineXPath);
        if (!programMatch.Success || !routineMatch.Success)
        {
            return new ProposeRungEditError(
                $"target='live' refused: cannot determine the program/routine from " +
                $"'{routineXPath}', so the pending-edit state cannot be verified. " +
                "No import was attempted.",
                Kind: "PendingZoneExists");
        }

        object pendingResponse;
        try
        {
            pendingResponse = await PendingEditsHandler.ExecuteAsync(
                programMatch.Groups[1].Value, routineMatch.Groups[1].Value, session, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pendingResponse = new PendingEditsError(ex.Message);
        }

        if (pendingResponse is PendingEditsError { Kind: ErrorKinds.SdkServerFaulted } faultedRead)
            return ServerFaulted(session.IsFaulted ? session.FaultedError()
                : new TagServiceError(TagServiceErrorKind.SdkServerFaulted, faultedRead.Error, Hint: faultedRead.Hint));

        if (pendingResponse is not PendingEditsResult pending)
        {
            var detail = (pendingResponse as PendingEditsError)?.Error ?? "unexpected response";
            return new ProposeRungEditError(
                $"target='live' refused: the pending-edit state of '{routineXPath}' could " +
                $"not be read ({detail}), so a clean program cannot be confirmed. " +
                "No import was attempted.",
                Kind: "PendingZoneExists");
        }

        var dirty = pending.Routines.Where(r => r.State != "none").ToList();
        if (dirty.Count > 0)
        {
            var summary = string.Join("; ", dirty.Select(r =>
                $"{r.Program}/{r.Routine} state={r.State} pendingRungs=[" +
                string.Join(",", r.PendingRungs.Select(p => $"{p.Number}:{p.Type}:{p.Text}")) + "]"));
            return new ProposeRungEditError(
                $"target='live' refused: '{routineXPath}' has a pending edit zone ({summary}). " +
                "A live import requires a program with nothing pending. No import was attempted.",
                Hint:
                    "Resolve the pending edits first: call logix_accept_pending_edits or " +
                    "logix_cancel_pending_edits (these tools require " +
                    "LGX_ENABLE_STUDIO_AUTOMATION=1; otherwise the engineer accepts or cancels " +
                    "them in Logix Designer, Logic -> Online Edits), then retry target='live'.",
                DocsAnchor: PendingEditsPresentDocsAnchor,
                Kind:       "PendingZoneExists");
        }

        // Gate 5 — stale-target check for replaces.
        if (replaceCount == 0)
            return null;

        if (expectedRungText is null || expectedRungText.Count != (int)replaceCount)
        {
            return new ProposeRungEditError(
                $"target='live' with replaceCount={replaceCount} requires expectedRungText " +
                $"with exactly {replaceCount} entr{(replaceCount == 1 ? "y" : "ies")} (one per " +
                $"replaced rung; received {expectedRungText?.Count ?? 0}). No import was attempted.",
                Hint:
                    "Pass the text of each rung you intend to replace, as last read from " +
                    "logix_export_rungs, in rung order.",
                Kind: "ExpectedRungTextRequired");
        }

        var export = await ExportRungsHandler.ExecuteAsync(routineXPath, session, ct)
            .ConfigureAwait(false);
        if (export is not ExportRungsResult exported)
        {
            var detail = (export as ExportRungsError)?.Error ?? "unexpected response";
            return new ProposeRungEditError(
                $"target='live' refused: could not export '{routineXPath}' to verify the " +
                $"rungs to replace ({detail}). No import was attempted.",
                Kind: "StaleTarget");
        }

        List<XElement> currentRungs;
        try
        {
            currentRungs = RoutineRungSelector.Select(SafeXml.ParseDocument(exported.L5X), routineXPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProposeRungEditError(
                $"target='live' refused: the export of '{routineXPath}' could not be parsed " +
                $"({ex.Message}). No import was attempted.",
                Kind: "StaleTarget");
        }

        for (int i = 0; i < (int)replaceCount; i++)
        {
            int idx = (int)insertPosition + i;
            string? actual = idx < currentRungs.Count
                ? NormalizeRungText(currentRungs[idx].Element("Text")?.Value)
                : null;
            string expected = NormalizeRungText(expectedRungText[i]);

            if (actual is null || !RungTextComparer.Matches(expected, actual))
            {
                return new ProposeRungEditError(
                    $"target='live' refused (StaleTarget): rung {idx} of '{routineXPath}' no " +
                    "longer matches the text you expected, so it changed since you read it. " +
                    $"expectedRungText[{i}]: \"{expected}\"; " +
                    $"current rung text: {(actual is null ? "<no rung at this index>" : $"\"{actual}\"")}. " +
                    "No import was attempted.",
                    Hint:
                        "Re-read the routine with logix_export_rungs, re-derive the edit from " +
                        "the current text, and retry with the fresh expectedRungText.",
                    Kind: "StaleTarget");
            }
        }

        return null;
    }

    // ── Name the edit state and the import cause ──────

    // The SDK mnemonics this classifier reacts to (XMLSrv_E_IMPORT_ABORTED_NO_CHANGES and
    // XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM) are entries of RockwellErrorHints, with the
    // cause and next step (bench evidence: docs/HARDWARE-VERIFIED.md). An importer rejection carries no edit-zone state at all,
    // so it is never classified as a pending-edit issue. The edits-exist mnemonic documents the
    // one-publish-per-cycle constraint; it is not a branch condition here (the classifier stays
    // evidence-based).

    /// <summary>Stable docs slug for the <c>ControllerBusy</c> kind.</summary>
    private const string ControllerBusyDocsAnchor = "controller-busy";

    /// <summary>
    /// True when a fresh program-scoped read shows any routine of <paramref name="program"/> in a non-<c>"none"</c>
    /// edit state. A read that fails counts as "no pending edits": the caller then reports the retryable busy error.
    /// </summary>
    /// <param name="program">The program name.</param>
    /// <param name="session">The open session.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<bool> ProgramHasPendingEditsAsync(
        string program, LogixProjectSession session, CancellationToken ct)
    {
        try
        {
            var read = await PendingEditsHandler.ExecuteAsync(program, null, session, ct).ConfigureAwait(false);
            return read is PendingEditsResult result && result.Routines.Any(r => r.State != "none");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Stable docs slug for the <c>ImportRejectedByImporter</c> kind.</summary>
    private const string ImportRejectedByImporterDocsAnchor = "import-rejected-by-importer";

    /// <summary>
    /// Stable docs slug for the <c>PendingEditsPresent</c> kind (unchanged from the
    /// withdrawn <c>ControllerHasPendingEdits</c> anchor so existing links
    /// keep resolving).
    /// </summary>
    private const string PendingEditsPresentDocsAnchor = "controller-has-pending-edits";

    /// <summary>
    /// Classifies a controller-target (<c>target="controller"</c>) import failure.
    /// Returns <c>null</c> when neither classification below
    /// applies, so the caller's own generic error message is used instead.
    ///
    /// <para>
    /// <b>Rule (name the edit state and the import cause, never infer it):</b>
    /// <list type="number">
    ///   <item>
    ///   If <paramref name="sdkDetail"/> names <c>XMLSrv_E_IMPORT_ABORTED_NO_CHANGES</c> (<see cref="RockwellErrorHints"/>),
    ///   the SDK importer itself rejected an undefined reference (tag or routine) in
    ///   the fragment and made no changes on the controller. This is classified
    ///   <c>ImportRejectedByImporter</c> — never a pending-edit issue, regardless of
    ///   whatever edit-zone state the controller happens to be carrying separately.
    ///   </item>
    ///   <item>
    ///   Otherwise, <paramref name="routineXPath"/> is parsed for its program/routine
    ///   names and a fresh <see cref="PendingEditsHandler.ExecuteAsync"/> tier-1 read
    ///   (export + classify) is run. If it reports any routine
    ///   at that scope in a non-<c>"none"</c> state, the failure is classified
    ///   <c>PendingEditsPresent</c>, naming the routine(s),
    ///   their pending rungs (number, type, text), stating that the Logix Designer SDK
    ///   allows one publish per accept/cancel cycle, naming both valid transitions —
    ///   with <c>LGX_ENABLE_STUDIO_AUTOMATION=1</c>, <c>logix_accept_pending_edits</c> or
    ///   <c>logix_cancel_pending_edits</c>; otherwise accept/cancel in Logix Designer — and noting
    ///   that further edits can still be staged with <c>target="workstation"</c> while
    ///   the zone is pending. This covers the SDK's
    ///   <c>XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM</c> failure (see docs/HARDWARE-VERIFIED.md)
    ///   as well as any other controller-target import failure the tier-1 read confirms
    ///   is blocked by a pending edit zone.
    ///   </item>
    /// </list>
    /// This method never inspects the exception message for a "Project is not open."
    /// (or any other) heuristic to reach the <c>PendingEditsPresent</c> verdict — that
    /// heuristic is withdrawn; the verdict is evidence-based only, from the
    /// tier-1 read.
    /// </para>
    /// </summary>
    /// <param name="sdkDetail">
    /// The failed import's exception message (or <see cref="TagServiceError.Message"/>
    /// for a <see cref="TagServiceException"/>).
    /// </param>
    /// <param name="routineXPath">The XPath of the routine the import targeted.</param>
    /// <param name="session">The open session to read pending-edit state from.</param>
    /// <param name="mergedStagedTagNames">
    /// Forwarded to <see cref="BuildMergedStagedTagsNote"/> so the classified error
    /// still names any buffered tags merged into this publish attempt.
    /// </param>
    /// <param name="statusMessages">
    /// Raw <c>Status</c> callbacks captured during the failed import, separate from the error log.
    /// Those that look like an import or verify message are quoted in the classified error.
    /// </param>
    /// <param name="replayImportReject">
    /// Runs the offline replay of the same fragment; invoked only when the importer rejected the
    /// fragment with <c>ImportAbortedNoChanges</c> and no captured message names a cause.
    /// </param>
    /// <param name="importLogEntries">
    /// Raw <see cref="RockwellAutomation.LogixDesigner.Logging.IOperationEvent.Error"/>
    /// callbacks captured during the failed import (see
    /// <see cref="ImportOperationEventCapture"/>) — the SDK's real <c>ImportLog</c>.
    /// Quoted verbatim (mnemonic and text) in the classified error when non-empty.
    /// </param>
    /// <param name="ct">Cancellation token forwarded to the tier-1 read.</param>
    private static async Task<ProposeRungEditError?> ClassifyControllerImportFailureAsync(
        string                sdkDetail,
        string                routineXPath,
        LogixProjectSession   session,
        IReadOnlyList<string> mergedStagedTagNames,
        IReadOnlyList<string> importLogEntries,
        IReadOnlyList<string> statusMessages,
        Func<CancellationToken, Task<ImportRejectReplayOutcome>>? replayImportReject,
        CancellationToken     ct)
    {
        // Never surface a raw '%1!s!' template (G27): format the SDK text before it is quoted anywhere.
        sdkDetail        = ImportLogParser.FormatMessage(sdkDetail);
        importLogEntries = importLogEntries.Select(e => ImportLogParser.FormatMessage(e)).ToList();
        statusMessages   = statusMessages.Select(e => ImportLogParser.FormatMessage(e)).ToList();
        var importLogNote = FormatImportLogNote(importLogEntries);
        var quotedStatus  = SelectCauseStatusMessages(statusMessages);
        var statusNote    = FormatStatusNote(quotedStatus);

        // 0. The SDK named the one-publish-per-cycle mnemonic anywhere (exception text, ImportLog or a
        //    Status callback): the program holds accepted edits, whatever the tier-1 read below finds.
        //    It wins over the generic "aborted, no changes" line the importer writes next to it.
        bool pieNamed = RockwellErrorHints.IsMatch(sdkDetail, RockwellErrorHintIds.PieEditsExistForProgram)
            || importLogEntries.Any(e => RockwellErrorHints.IsMatch(e, RockwellErrorHintIds.PieEditsExistForProgram))
            || statusMessages.Any(m => RockwellErrorHints.IsMatch(m, RockwellErrorHintIds.PieEditsExistForProgram));

        // 0b. RxE_ILLEGAL_OPERATIONS (80042021, G53): a publish race. With a rung-level reason the importer
        //     really rejected the fragment (branch 1 below). With none, re-check the program's edit state: a
        //     pending zone now means another client's edit won (PendingEditsBlockImport); otherwise the other
        //     client was editing at the same moment, a retryable ControllerBusy. The host upload lease is not
        //     consulted: it serialises controller uploads during connect, not edits to a program, so it says nothing
        //     about who edited this program; the program's edit state is the evidence for this race.
        bool illegalOpNamed = !pieNamed
            && (RockwellErrorHints.IsMatch(sdkDetail, RockwellErrorHintIds.IllegalOperations)
                || importLogEntries.Any(e => RockwellErrorHints.IsMatch(e, RockwellErrorHintIds.IllegalOperations))
                || statusMessages.Any(m => RockwellErrorHints.IsMatch(m, RockwellErrorHintIds.IllegalOperations)));
        bool illegalOpHasReasons = false;
        if (illegalOpNamed)
        {
            illegalOpHasReasons = ImportLogRejectReasons(ImportLogParser.Parse(importLogEntries, statusMessages))
                .Any(r => !RockwellErrorHints.IsMatch(r.Mnemonic, RockwellErrorHintIds.IllegalOperations));
            if (!illegalOpHasReasons)
            {
                var raceProgram = ProgramNamePattern.Match(routineXPath);
                if (raceProgram.Success && await ProgramHasPendingEditsAsync(
                        raceProgram.Groups[1].Value, session, ct).ConfigureAwait(false))
                    return PendingEditsBlockImportFromSdk(sdkDetail, mergedStagedTagNames, importLogNote, statusNote);

                return new ProposeRungEditError(
                    $"ControllerBusy: the SDK refused the import into '{routineXPath}' as out of sequence " +
                    "(RxE_ILLEGAL_OPERATIONS, 80042021) and named no rung-level reason; the program holds no pending " +
                    "edits, so another client was editing at the same moment. Nothing was imported. " +
                    $"SDK detail: {sdkDetail}" +
                    BuildMergedStagedTagsNote(mergedStagedTagNames) +
                    importLogNote +
                    statusNote +
                    ProposeRungChangeSkillPointer,
                    Hint:              RockwellErrorHints.IllegalOperationsBusyHint + ProposeRungChangeSkillPointer,
                    DocsAnchor:        ControllerBusyDocsAnchor,
                    Kind:              ErrorKinds.ControllerBusy,
                    RetryAfterSeconds: RockwellErrorHints.PublishRaceRetryAfterSeconds);
            }
        }

        // 1. The importer rejected a reference in the fragment — nothing changed on
        //    the controller. Never labeled a pending-edit issue.
        if (!pieNamed && (illegalOpHasReasons
                || RockwellErrorHints.IsMatch(sdkDetail, RockwellErrorHintIds.ImportAbortedNoChanges)))
        {
            // Replay only when nothing captured (Error log or an import/verify-looking Status
            // message) names a cause. The replay runs on an offline copy and changes nothing online.
            IReadOnlyList<RejectReason>? rejectReasons = null;
            string? rejectSource = null;
            string replayNote = string.Empty;
            // The importer's own generic "aborted, no changes" line is the symptom, not a cause.
            // A cause the importer's own log named (mnemonic + text) is returned as structured rejectReasons
            // with source "controller"; the replay is then not needed.
            var logReasons = ImportLogRejectReasons(
                ImportLogParser.Parse(importLogEntries, statusMessages));
            if (logReasons.Count > 0)
            {
                rejectReasons = logReasons;
                rejectSource  = ImportRejectReplay.SourceController;
            }
            bool causeNamed =
                logReasons.Count > 0 ||
                importLogEntries.Any(e => !RockwellErrorHints.IsMatch(e, RockwellErrorHintIds.ImportAbortedNoChanges)) ||
                quotedStatus.Any(m => !RockwellErrorHints.IsMatch(m, RockwellErrorHintIds.ImportAbortedNoChanges));
            if (!causeNamed && replayImportReject is not null)
            {
                var replay = await ImportRejectReplay.RunBoundedAsync(
                    replayImportReject, session.ImportRejectReplayTimeout, ct).ConfigureAwait(false);
                if (replay.UnavailableReason is { } why)
                {
                    rejectSource = ImportRejectReplay.SourceUnavailable;
                    replayNote   = $"  Offline replay of the fragment was unavailable: {why}";
                }
                else
                {
                    rejectSource  = ImportRejectReplay.SourceOfflineReplay;
                    rejectReasons = replay.Reasons;
                    replayNote    = replay.Reasons.Count == 0
                        ? "  An offline replay of the fragment (import plus verify on a saved copy) reported no errors."
                        : "  Offline replay (import plus verify on a saved copy) reported: " +
                          string.Join("; ", replay.Reasons.Select(r =>
                              r.RungIndex is { } i ? $"rung {i}: \"{r.Text}\"" : $"\"{r.Text}\""));
                }
            }

            rejectReasons = rejectReasons is null ? null : OrderRejectReasons(rejectReasons);

            // G26: Logix rejected a tag's External Access for its type and this server did not know the type.
            if (ReadOnlyAccessRejection(
                    rejectReasons ?? [],
                    importLogEntries.Concat(quotedStatus).Append(sdkDetail),
                    DeclaredTagTypes(session.StagedTagBuffer)) is { } accessError)
                return accessError with { RejectReasons = rejectReasons, RejectReasonSource = rejectSource };

            return new ProposeRungEditError(
                $"The Logix Designer SDK importer rejected the fragment targeted at " +
                $"'{routineXPath}'; no changes were made on the controller. SDK detail: " +
                $"{sdkDetail}" +
                BuildMergedStagedTagsNote(mergedStagedTagNames) +
                importLogNote +
                statusNote +
                replayNote +
                ProposeRungChangeSkillPointer,
                Hint:
                    ImportRejectedHintFor(rejectReasons, ImportLogParser.Parse(importLogEntries, statusMessages)) +
                    ProposeRungChangeSkillPointer,
                DocsAnchor: ImportRejectedByImporterDocsAnchor,
                Kind:       "ImportRejectedByImporter",
                RejectReasons:      rejectReasons,
                RejectReasonSource: rejectSource);
        }

        // 2. Evidence-based pending-edits check: run the tier-1 logix_pending_edits
        //    read over the failed import's own scope. Only a
        //    non-"none" result from that read — never a string heuristic on sdkDetail —
        //    may produce a PendingEditsPresent verdict.
        var programMatch = ProgramNamePattern.Match(routineXPath);
        var routineMatch = RoutineNamePattern.Match(routineXPath);
        if (!programMatch.Success || !routineMatch.Success)
            return pieNamed ? PendingEditsBlockImportFromSdk(sdkDetail, mergedStagedTagNames, importLogNote, statusNote) : null;

        var program = programMatch.Groups[1].Value;
        var routine = routineMatch.Groups[1].Value;

        object pendingEditsResponse;
        try
        {
            pendingEditsResponse = await PendingEditsHandler.ExecuteAsync(
                program, routine, session, ct).ConfigureAwait(false);
        }
        catch (Exception readEx) when (readEx is not OperationCanceledException)
        {
            // The tier-1 read itself failed — fall back to the caller's generic error
            // rather than claiming a state we could not confirm. The SDK's own mnemonic is
            // still evidence enough to name PendingEditsBlockImport.
            return pieNamed ? PendingEditsBlockImportFromSdk(sdkDetail, mergedStagedTagNames, importLogNote, statusNote) : null;
        }

        if (pendingEditsResponse is not PendingEditsResult pendingEdits)
            return pieNamed ? PendingEditsBlockImportFromSdk(sdkDetail, mergedStagedTagNames, importLogNote, statusNote) : null;

        var nonNoneRoutines = pendingEdits.Routines
            .Where(r => r.State != "none")
            .ToList();

        if (nonNoneRoutines.Count == 0)
            return pieNamed ? PendingEditsBlockImportFromSdk(sdkDetail, mergedStagedTagNames, importLogNote, statusNote) : null;

        var routineSummary = string.Join("; ", nonNoneRoutines.Select(r =>
            $"{r.Program}/{r.Routine} state={r.State} pendingRungCount={r.PendingRungs.Count} " +
            $"pendingRungs=[{string.Join(",", r.PendingRungs.Select(p => $"{p.Number}:{p.Type}:{p.Text}"))}]"));

        return new ProposeRungEditError(
            $"logix_pending_edits reports a non-'none' edit-zone state for the routine(s) " +
            $"targeted by '{routineXPath}' ({routineSummary}), and the controller-target " +
            $"import failed as a result: one publish per accept/cancel cycle — the Logix " +
            $"Designer SDK refuses every further import into a program while its edit zone " +
            $"is still pending. SDK detail: {sdkDetail}" +
            BuildMergedStagedTagsNote(mergedStagedTagNames) +
            importLogNote +
            statusNote +
            ProposeRungChangeSkillPointer,
            Hint:
                "The controller already holds pending logic edits for this routine, and the " +
                "Logix Designer SDK allows one publish per accept/cancel cycle: no further " +
                "target='controller' import into this program is possible until the pending " +
                "edits are resolved. With LGX_ENABLE_STUDIO_AUTOMATION=1 and the engineer's " +
                "go-ahead, call logix_accept_pending_edits or logix_cancel_pending_edits " +
                "(confirm=true); otherwise the engineer accepts or cancels them in Logix " +
                "Designer (Logic -> Online Edits). Meanwhile, further edits can still be " +
                "staged offline with target='workstation' while this zone is pending — only " +
                "the controller-target publish is blocked. Once the zone is resolved, call " +
                "logix_propose_rung_edit with target='controller' again." +
                ProposeRungChangeSkillPointer,
            DocsAnchor: PendingEditsPresentDocsAnchor,
            Kind:       pieNamed ? RockwellErrorHints.PendingEditsBlockImportKind : "PendingEditsPresent");
    }

    /// <summary>
    /// Maps a failed <c>target="workstation"</c> (stage) import that names
    /// <c>XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM</c> to the same <c>PendingEditsBlockImport</c> error the
    /// controller and live paths return (same message and hint). Any other failure returns <c>null</c>, so the
    /// caller's generic error is used. The mnemonic may be on the exception text, the ImportLog or a Status callback.
    /// </summary>
    /// <param name="sdkDetail">The failed import's exception message.</param>
    /// <param name="capture">The ImportLog and Status messages captured during the failed import.</param>
    /// <returns>A <c>PendingEditsBlockImport</c> error, or <c>null</c> when the mnemonic is not named.</returns>
    private static ProposeRungEditError? ClassifyWorkstationPieFailure(
        string sdkDetail, ImportOperationEventCapture capture)
    {
        bool pieNamed = RockwellErrorHints.IsMatch(sdkDetail, RockwellErrorHintIds.PieEditsExistForProgram)
            || capture.Errors.Any(e => RockwellErrorHints.IsMatch(e, RockwellErrorHintIds.PieEditsExistForProgram))
            || capture.StatusMessages.Any(m => RockwellErrorHints.IsMatch(m, RockwellErrorHintIds.PieEditsExistForProgram));
        if (!pieNamed)
            return null;
        return PendingEditsBlockImportFromSdk(
            sdkDetail, [], FormatImportLogNote(capture.Errors),
            FormatStatusNote(SelectCauseStatusMessages(capture.StatusMessages)));
    }

    /// <summary>
    /// Builds the <c>PendingEditsBlockImport</c> error for an import the SDK refused with
    /// <c>XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM</c> when no tier-1 read could confirm the edit state.
    /// </summary>
    /// <param name="sdkDetail">The failed import's exception message.</param>
    /// <param name="mergedStagedTagNames">Buffered tags merged into the attempt, named in the error.</param>
    /// <param name="importLogNote">The formatted ImportLog note (may be empty).</param>
    /// <param name="statusNote">The formatted Status note (may be empty).</param>
    /// <returns>A <see cref="ProposeRungEditError"/> of kind <c>PendingEditsBlockImport</c>.</returns>
    private static ProposeRungEditError PendingEditsBlockImportFromSdk(
        string                sdkDetail,
        IReadOnlyList<string> mergedStagedTagNames,
        string                importLogNote,
        string                statusNote) =>
        new(
            "PendingEditsBlockImport: the SDK refused the import because the target program holds " +
            "accepted edits that are not yet assembled (one publish per accept/cancel cycle). " +
            $"SDK detail: {sdkDetail}" +
            BuildMergedStagedTagsNote(mergedStagedTagNames) +
            importLogNote +
            statusNote +
            ProposeRungChangeSkillPointer,
            Hint:
                "Resolve the pending edits first: logix_assemble_pending_edits or logix_cancel_pending_edits " +
                "(needs LGX_ENABLE_STUDIO_AUTOMATION=1), or accept or cancel them in Logix Designer " +
                "(Logic -> Online Edits). Then publish again. target='workstation' can still stage further " +
                "edits meanwhile." + ProposeRungChangeSkillPointer,
            DocsAnchor: PendingEditsPresentDocsAnchor,
            Kind:       RockwellErrorHints.PendingEditsBlockImportKind);

    /// <summary>The note returned when <c>verifyOnStage</c> is passed with a controller or live target.</summary>
    internal const string VerifyOnStageIgnoredOnControllerNote =
        "ignored: controller publishes are always checked by Logix";

    /// <summary>
    /// Names the rung(s) verify rejected, by the index in the fragment that the importer's log gave — not the insert
    /// position. Says so plainly when no reason names a rung.
    /// </summary>
    internal static string StageVerifyFailedRungPhrase(IReadOnlyList<RejectReason> reasons)
    {
        var rungs = reasons.Where(r => r.RungIndex is not null).Select(r => r.RungIndex!.Value).Distinct().OrderBy(i => i).ToList();
        return rungs.Count switch
        {
            0 => "A rung (the verify log did not name which)",
            1 => $"Rung {rungs[0]}",
            _ => "Rungs " + string.Join(", ", rungs),
        };
    }

    /// <summary>True when a verify reject reason names <c>XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM</c>.</summary>
    /// <param name="reason">The reject reason.</param>
    internal static bool IsPieEditsExistReason(RejectReason reason) =>
        RockwellErrorHints.IsMatch(reason.Mnemonic, RockwellErrorHintIds.PieEditsExistForProgram)
        || RockwellErrorHints.IsMatch(reason.Text, RockwellErrorHintIds.PieEditsExistForProgram);

    /// <summary>One short sentence per reason (mnemonic and hint where known) for the <c>StageVerifyFailed</c> message.</summary>
    private static string StageVerifyFailedReasonSummary(IReadOnlyList<RejectReason> reasons) =>
        string.Join(" ", reasons.Take(3).Select(r =>
            "[" + (r.Mnemonic is null ? "" : r.Mnemonic + " ") +
            (r.RungIndex is { } rung ? $"rung {rung}: " : "") + r.Text + "]" +
            (r.Hint is null ? "" : " " + r.Hint))) +
        (reasons.Count > 3 ? $" (+{reasons.Count - 3} more in verifyRejectReasons)" : "");

    /// <summary>
    /// The captured ImportLog entries that name a cause — an entry with a mnemonic, other than the importer's own
    /// generic "aborted, no changes" line and other than warnings — as <see cref="RejectReason"/>s.
    /// </summary>
    /// <param name="log">The parsed import log.</param>
    /// <returns>The reasons in log order; empty when the log names no cause.</returns>
    internal static IReadOnlyList<RejectReason> ImportLogRejectReasons(IReadOnlyList<ImportLogEntry> log) =>
        log.Where(e => !string.IsNullOrWhiteSpace(e.Mnemonic)
                       && !string.Equals(e.Severity, "Warning", StringComparison.OrdinalIgnoreCase)
                       && !RockwellErrorHints.IsMatch(e.Mnemonic!, RockwellErrorHintIds.ImportAbortedNoChanges))
           .Select(e =>
           {
               var rung = ImportLogRungPattern.Match(e.Text);
               return new RejectReason(
                   e.RungIndex ?? (rung.Success && int.TryParse(rung.Groups[1].Value, out var i) ? i : null),
                   e.Text, e.Mnemonic, e.Hex, e.Hint);
           })
           .ToList();

    /// <summary><c>kind</c> of a tag declaration Logix rejected as External Access Read Only for a type this server does not know (G26).</summary>
    internal const string ReadOnlyAccessRejectedKind = "ReadOnlyExternalAccessRejected";

    /// <summary>
    /// Puts the reasons that say something first and drops those that are only an unformatted template
    /// (<c>%1!s!</c>) when any reason is meaningful, so the first entry is always the one to read (G27).
    /// </summary>
    /// <param name="reasons">The reasons in the order the importer gave them.</param>
    /// <returns>The reasons, meaningful ones first with their relative order kept.</returns>
    internal static IReadOnlyList<RejectReason> OrderRejectReasons(IReadOnlyList<RejectReason> reasons)
    {
        var cleaned = reasons
            .Select(r => ImportLogParser.HasPlaceholder(r.Text) ? r with { Text = ImportLogParser.FormatMessage(r.Text) } : r)
            .ToList();
        var meaningful = cleaned.Where(r => ImportLogParser.IsMeaningful(r.Text)).ToList();
        return meaningful.Count == 0 ? cleaned : meaningful;
    }

    /// <summary>The hint on <c>ImportRejectedByImporter</c>, built from the reasons or, failing those, the import log (G30).</summary>
    /// <param name="reasons">The structured reject reasons, when any.</param>
    /// <param name="log">The parsed import log of the same attempt.</param>
    internal static string ImportRejectedHintFor(IReadOnlyList<RejectReason>? reasons, IReadOnlyList<ImportLogEntry>? log = null)
    {
        var fromReasons = (reasons ?? []).Select(r => (r.Mnemonic, (string?)r.Text, r.RungIndex, r.Hint));
        var fromLog = (log ?? []).Where(e => !string.Equals(e.Severity, "Warning", StringComparison.OrdinalIgnoreCase))
            .Select(e => (e.Mnemonic, (string?)e.Text, e.RungIndex, e.Hint));
        return RockwellErrorHints.ImportRejectedHint(fromReasons.Concat(fromLog));
    }

    /// <summary>Tag name to data type for the declarations in a staged tag buffer.</summary>
    /// <param name="buffer">The session's staged tag declarations.</param>
    internal static IReadOnlyDictionary<string, string> DeclaredTagTypes(IReadOnlyDictionary<string, XElement> buffer) =>
        buffer.Where(kv => (string?)kv.Value.Attribute("DataType") is { Length: > 0 })
              .ToDictionary(kv => kv.Key, kv => (string)kv.Value.Attribute("DataType")!, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A typed error when the importer rejected a tag's External Access for its type and the type is not one this
    /// server already creates Read/Write (G26); names the type(s). <c>null</c> when no reason says so.
    /// </summary>
    /// <param name="reasons">Reject reasons from the import or the verify replay.</param>
    /// <param name="otherTexts">Further raw text from the same attempt (import log, status, exception).</param>
    /// <param name="declaredTags">Tag name to data type for the declarations in the attempt.</param>
    internal static ProposeRungEditError? ReadOnlyAccessRejection(
        IReadOnlyList<RejectReason> reasons, IEnumerable<string?> otherTexts, IReadOnlyDictionary<string, string> declaredTags)
    {
        var types = RungFragmentBuilder.DetectReadOnlyRejectedTypes(
            reasons.Select(r => (string?)r.Text).Concat(otherTexts), declaredTags);
        if (types.Count == 0) return null;
        var named = string.Join(", ", types);
        return new ProposeRungEditError(
            $"{ReadOnlyAccessRejectedKind}: Logix rejected the External Access of a tag of data type {named} " +
            "('Incompatible External Access Value'). The server creates new tags External Access Read Only, which this " +
            $"type does not accept. Nothing was imported or staged.",
            Hint: $"Create the {named} tag in Logix Designer with External Access Read/Write, then reference it from the rung; " +
                  "or report the type so the server can create it Read/Write.",
            DocsAnchor: ImportRejectedByImporterDocsAnchor,
            Kind: ReadOnlyAccessRejectedKind);
    }

    /// <summary>
    /// A <c>MotionTagRequiresOffline</c> error when the call declares a tag of a motion data type (AXIS_*,
    /// MOTION_GROUP, COORDINATE_SYSTEM), in <paramref name="tags"/> or in the tag declarations of
    /// <paramref name="l5xFragment"/>; <c>null</c> otherwise. Pure: no export, replay or import (G27).
    /// </summary>
    /// <param name="tags">Typed tag declarations.</param>
    /// <param name="l5xFragment">A raw fragment, when given; tags marked <c>Use="Context"</c> already exist and are skipped.</param>
    internal static ProposeRungEditError? MotionTagRefusal(IReadOnlyList<TagInput>? tags, string? l5xFragment)
    {
        var found = new List<(string Name, string Type)>();
        if (tags is not null)
            foreach (var t in tags)
                if (ReferenceCheckKinds.IsMotionTagType(t?.DataType)) found.Add((t!.Name, t.DataType.Trim()));
        if (!string.IsNullOrWhiteSpace(l5xFragment) && l5xFragment.Contains("DataType", StringComparison.Ordinal))
        {
            try
            {
                foreach (var el in SafeXml.ParseDocument(l5xFragment).Descendants("Tag"))
                    if ((string?)el.Attribute("Use") is not "Context"
                        && ReferenceCheckKinds.IsMotionTagType((string?)el.Attribute("DataType")))
                        found.Add(((string?)el.Attribute("Name") ?? "?", ((string)el.Attribute("DataType")!).Trim()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* unparseable: the importer reports it */ }
        }
        if (found.Count == 0) return null;
        var list = string.Join(", ", found.Select(f => $"{f.Name} ({f.Type})").Distinct());
        return new ProposeRungEditError(
            $"{ReferenceCheckKinds.MotionTagRequiresOffline}: {list} cannot be created while the project is online. " +
            "Logix creates axis, motion group and coordinate system tags only offline. Nothing was staged or imported, " +
            "and the controller was not contacted.",
            Hint: "Create the motion tag in Logix Designer with the project offline (then download), or reference an existing " +
                  "motion tag from the rung instead of declaring it.",
            DocsAnchor: RungEditDocsAnchor,
            Kind: ReferenceCheckKinds.MotionTagRequiresOffline);
    }

    private static readonly Regex ImportLogRungPattern =
        new(@"\bRung\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Formats the SDK's real <c>ImportLog</c> entries — captured via
    /// <see cref="ImportOperationEventCapture"/> from the operation-event hook during
    /// the failed import — into a note appended to a classified error message.
    /// Quotes each entry verbatim, and separately quotes the <c>Mnemonic='...'</c>
    /// value when the entry carries one, so both the mnemonic and the full text are
    /// visible in the error. Returns an empty string when no
    /// entries were captured (e.g. a test double that never drives the hook, or an SDK
    /// build that does not stream one for this failure).
    /// </summary>
    internal static string FormatImportLogNote(IReadOnlyList<string> importLogEntries)
    {
        if (importLogEntries.Count == 0)
            return string.Empty;

        var parts = new List<string>();
        foreach (var entry in importLogEntries)
        {
            var mnemonicMatch = ImportLogMnemonicPattern.Match(entry);
            parts.Add(mnemonicMatch.Success
                ? $"ImportLog: mnemonic \"{mnemonicMatch.Groups["mnemonic"].Value}\", text \"{entry}\""
                : $"ImportLog: \"{entry}\"");
        }

        return "  " + string.Join(" ", parts);
    }

    /// <summary>
    /// Keeps the <c>Status</c> callbacks that look like an import or verify message: those containing
    /// "rung", "instruction", "error", "unknown", "undefined" or "invalid" (case-insensitive).
    /// </summary>
    internal static IReadOnlyList<string> SelectCauseStatusMessages(IReadOnlyList<string> statusMessages) =>
        statusMessages
            .Where(m => !string.IsNullOrWhiteSpace(m) && StatusCausePattern.IsMatch(m) && !StatusProgressPattern.IsMatch(m))
            .ToList();

    /// <summary>Formats the matching <c>Status</c> messages for the classified error; empty when none.</summary>
    internal static string FormatStatusNote(IReadOnlyList<string> quotedStatus) =>
        quotedStatus.Count == 0
            ? string.Empty
            : "  " + string.Join(" ", quotedStatus.Select(m => $"Status: \"{m}\""));

    /// <summary>Progress chatter such as "Partial Import Rungs From Xml File started": names no cause even though it says "Rungs".</summary>
    private static readonly Regex StatusProgressPattern =
        new(@"^\s*[A-Za-z ]+\s(started|completed|finished|complete)\.?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StatusCausePattern =
        new(@"rung|instruction|error|unknown|undefined|invalid",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Extracts the <c>Mnemonic='...'</c> value from a captured
    /// <c>&lt;Exception Severity=… Mnemonic='XMLSrv_E_…'&gt;</c> ImportLog entry.
    /// </summary>
    private static readonly Regex ImportLogMnemonicPattern =
        new(@"Mnemonic\s*=\s*'(?<mnemonic>[^']*)'", RegexOptions.Compiled);

    /// <summary>
    /// Minimal <see cref="IOperationEvent"/> implementation registered on the SDK
    /// session for the duration of a single controller-target import call, so that a
    /// failed import's real <c>ImportLog</c> — streamed by the SDK through
    /// <see cref="IOperationEvent.Error"/> — is available to
    /// quote in the classified error even though the SDK exception's own
    /// <c>Message</c> is often terser. <see cref="IOperationEvent.Progress"/> and
    /// <see cref="IOperationEvent.Status"/> are ignored; only <c>Error</c> callbacks
    /// are captured.
    /// </summary>
    private sealed class ImportOperationEventCapture : IOperationEvent
    {
        private readonly List<string> _errors = new();
        private readonly List<string> _status = new();

        /// <summary>
        /// Every message passed to <see cref="Status"/>, in order, kept apart from <see cref="Errors"/>.
        /// </summary>
        public IReadOnlyList<string> StatusMessages => _status;

        /// <summary>
        /// Every message passed to <see cref="Error"/>, in the order the SDK raised
        /// them, for the lifetime of this capture instance (one import call).
        /// </summary>
        public IReadOnlyList<string> Errors => _errors;

        public void LogProgress(bool logProgressNew) { }
        public void Progress(string projectFile, int value) { }
        public void LogStatus(bool logStatusNew) { }
        public void Status(string projectFile, string msg) => _status.Add(msg);
        public void LogError(bool logErrorNew) { }
        public void Error(string projectFile, string msg) => _errors.Add(msg);
    }
}
#endif
