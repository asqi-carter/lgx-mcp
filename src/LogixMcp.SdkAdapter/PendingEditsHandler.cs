#if LOGIX_SDK
// ── PendingEditsHandler — classifies rung edit-zone state from an export ─────
using System.Text.Json.Serialization;
using System.Xml.Linq;
using RockwellAutomation.LogixDesigner;

using ConnectedState = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState;
using ControllerMode = RockwellAutomation.LogixDesigner.LogixProject.ControllerMode;

// Alias System.Threading.Tasks.Task to avoid ambiguity with L5Sharp.Core.Task
// when both namespaces are in scope.
using Task = System.Threading.Tasks.Task;

// ── Result / error types ──────────────────────────────────────────────────────

/// <summary>
/// A single non-normal ("pending edit zone") rung found while classifying a
/// routine's export.
/// </summary>
/// <param name="Number">The rung's <c>Number</c> attribute from the L5X export.</param>
/// <param name="Type">
/// The raw L5X <c>Type</c> attribute value — see
/// <see cref="PendingEditsHandler.RungTypeCodeTable"/> for the full code table.
/// </param>
/// <param name="Text">Neutral-text content of the rung.</param>
public record PendingEditRungEntry(
    [property: JsonPropertyName("number")] int    Number,
    [property: JsonPropertyName("type")]   string Type,
    [property: JsonPropertyName("text")]   string Text);

/// <summary>
/// Edit-zone state of a single routine, as observed in one export snapshot.
/// </summary>
/// <param name="Program">Name of the program containing the routine.</param>
/// <param name="Routine">Name of the routine.</param>
/// <param name="Rungs">
/// Number of rungs the <em>controller</em> holds in the routine (normal + non-normal),
/// excluding rungs this session staged to the workstation target. Always equal to
/// <c>ControllerRungs</c>; kept under this name for existing callers.
/// </param>
/// <param name="PendingRungs">
/// Every rung whose L5X <c>Type</c> attribute is not <c>N</c> (Normal), in rung order.
/// Empty when the routine has no edit-zone markers.
/// </param>
/// <param name="State">
/// <c>"none"</c> — no non-normal rung types found.
/// <c>"pending"</c> — an untested edit zone was found (bare <c>I</c>/<c>D</c>/<c>R</c>/<c>IR</c>,
/// the offline/<c>LeaveEdits</c>-image markers).
/// <c>"tested"</c> — a live, testable online edit zone was found (the <c>e</c>-prefixed-family
/// markers: <c>e</c>, <c>er</c>, <c>dN</c>).
/// <c>"accepted"</c> — an accepted-into-assembly edit zone was found (the <c>r</c>-prefixed
/// markers: <c>rR</c>, <c>rI</c>, <c>rN</c>).
/// <b>Known limitation (bench-confirmed):</b> a rung published with
/// <c>PartialImportOption.AcceptEdits</c> has been observed to export as plain <c>N</c> with
/// no distinguishing marker at all. When that happens this reader cannot tell the accepted
/// rung apart from a rung that was never edited, and reports <c>"none"</c>. A <c>"none"</c>
/// result is therefore not proof that the controller has no accepted-but-unassembled edits —
/// only that none are visible in this export.
/// </param>
/// <param name="ProgramTestedEdits">
/// The <c>TestEdits</c> attribute of the routine's parent <c>&lt;Program&gt;</c> element:
/// <c>true</c> when Studio's Test Edits is active for the program, <c>false</c> when not.
/// <c>null</c> (serialised as JSON <c>null</c>, never omitted) when the attribute was absent
/// or unreadable — the handler never guesses. The routine export does not carry this
/// attribute; for a routine scope it is read from an extra program export.
/// </param>
/// <param name="StagedRungs">
/// Number of rungs in the routine's export that this session staged to the workstation
/// target and that were therefore excluded from <paramref name="Rungs"/>. The
/// server-managed ACD holds a staged rung as a normal rung, so it is identified by the
/// census of staged rung texts (<see cref="LogixProjectSession.StagedRungBuffer"/>).
/// This count lives only on the server: nothing staged has reached the controller.
/// </param>
/// <param name="ZoneState">
/// Per-routine tested state of the pending edit zone, additive to <paramref name="State"/>
/// (which is unchanged): <c>"none"</c> when nothing is pending; <c>"untested"</c> when edits
/// exist and <c>TestEdits=false</c>; <c>"tested"</c> when edits exist and <c>TestEdits=true</c>;
/// <c>"unknown"</c> when edits exist but <c>TestEdits</c> could not be read.
/// </param>
public record RoutinePendingEditState(
    [property: JsonPropertyName("program")]      string                                Program,
    [property: JsonPropertyName("routine")]      string                                Routine,
    [property: JsonPropertyName("rungs")]        int                                   Rungs,
    [property: JsonPropertyName("pendingRungs")] IReadOnlyList<PendingEditRungEntry>   PendingRungs,
    [property: JsonPropertyName("state")]        string                                State,
    [property: JsonPropertyName("programTestedEdits")] bool?                           ProgramTestedEdits = null,
    [property: JsonPropertyName("zoneState")]    string                                ZoneState = "none",
    [property: JsonPropertyName("stagedRungs")]  int                                   StagedRungs = 0)
{
    /// <summary>Rungs the controller holds in this routine; always equals <see cref="Rungs"/>.</summary>
    [JsonPropertyName("controllerRungs")]
    public int ControllerRungs => Rungs;
}

/// <summary>
/// The <c>TestEdits</c> state of one in-scope <c>&lt;Program&gt;</c>, as read from the
/// program's L5X export.
/// </summary>
/// <param name="Program">Program name.</param>
/// <param name="TestedEdits">
/// <c>true</c>/<c>false</c> from the <c>TestEdits</c> attribute; <c>null</c> (serialised, not
/// omitted) when the attribute is absent or not a boolean.
/// </param>
/// <param name="Note">Explanation when <paramref name="TestedEdits"/> is <c>null</c>; omitted otherwise.</param>
public record ProgramTestedEditsState(
    [property: JsonPropertyName("program")]     string  Program,
    [property: JsonPropertyName("testedEdits")] bool?   TestedEdits,
    [property: JsonPropertyName("note"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Note = null);

/// <summary>
/// Snapshot of the current session's local staging buffer — what has been
/// staged to the workstation target (<see cref="ProposeRungEditHandler"/>,
/// <c>target="workstation"</c>) and not yet published to the controller.
///
/// <para>
/// Unlike <see cref="RoutinePendingEditState"/> (derived from a live controller
/// export), this reflects only what <em>this</em> session has staged
/// in-process — it is sourced from <see cref="LogixProjectSession.StagedRungCount"/>
/// and <see cref="LogixProjectSession.StagedTagBuffer"/>, not from the export.
/// Lets a caller inspect what the next <c>target="controller"</c> publish will
/// send before committing, without needing to remember every prior staging call.
/// </para>
/// </summary>
/// <param name="RungCount">
/// Cumulative number of rungs staged to the workstation target in this session
/// and not yet published. <c>0</c> when nothing is staged.
/// </param>
/// <param name="TagNames">
/// Names of every <em>new</em> tag declaration currently held in the session's
/// staging buffer, sorted case-insensitively. "New" means absent from the
/// pre-stage controller tag census (<see cref="LogixProjectSession.PreStageTagCensus"/>):
/// context declarations for tags that already existed on the controller are
/// excluded.  Empty when the buffer holds no new tags.
/// </param>
/// <param name="ReplacedRungs">
/// Rungs that were present in the controller export <em>only because this
/// session's stage replaced or deleted them</em> (i.e., their normalised text
/// was recorded in <see cref="LogixProjectSession.StagedReplacedRungTexts"/>).
/// These are the <em>original</em> rungs displaced by a
/// <c>replaceCount &gt; 0</c> workstation-target stage; they appear in the
/// export with an <c>r*</c> or <c>d*</c> type but are not controller-side
/// pending edits.  <c>null</c> (omitted from JSON) when no replaced rungs are
/// tracked for this session.
/// </param>
public record StagedEditsState(
    [property: JsonPropertyName("rungCount")]    int                                    RungCount,
    [property: JsonPropertyName("tagNames")]     IReadOnlyList<string>                  TagNames,
    [property: JsonPropertyName("replacedRungs")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                 IReadOnlyList<PendingEditRungEntry>?   ReplacedRungs = null);

/// <summary>
/// Successful response from <see cref="PendingEditsHandler.ExecuteAsync"/>.
/// </summary>
/// <param name="Connected">
/// <c>true</c> when <c>ReadConnectedStateAsync</c> reported
/// <see cref="ConnectedState.Online"/>; <c>false</c> otherwise (Offline, Connected-but-not-Online,
/// or Unknown).
/// </param>
/// <param name="ControllerMode">
/// The controller mode as reported by <c>ReadControllerModeAsync</c>:
/// <c>"Run"</c>, <c>"Program"</c>, <c>"Test"</c>, <c>"Faulted"</c>, or <c>"Unknown"</c>.
/// </param>
/// <param name="Note">
/// An optional annotation for unusual mode conditions.  Present (non-<c>null</c>) only
/// when the SDK returns <c>"Unknown"</c> for the controller mode while the connected
/// state is <see cref="ConnectedState.Online"/>.  When present, the value is
/// <c>"SDK returned Unknown while online; Echo controllers have shown this"</c>.
/// <c>null</c> in all other cases and omitted from the serialised JSON output.
/// </param>
/// <param name="Routines">Edit-zone state for every routine found at the requested scope.</param>
/// <param name="Staged">
/// This session's local staging-buffer snapshot — staged rung count and staged
/// tag names. See <see cref="StagedEditsState"/>.
/// </param>
/// <param name="ObservedAt">UTC timestamp at which this snapshot was taken.</param>
/// <param name="Programs">
/// Per-program <c>TestEdits</c> state for every program in scope (see
/// <see cref="ProgramTestedEditsState"/>). <c>null</c> only for results built without
/// reading a program export.
/// </param>
/// <param name="TargetMajorRevision">
/// Major revision of the session's target (the uploaded project's value for an online target);
/// <c>null</c> when unknown.
/// </param>
/// <param name="MnemonicSet"><c>iec</c> (36 and above), <c>legacy</c> (35 and below) or <c>unknown</c> (no check applied).</param>
public record PendingEditsResult(
    [property: JsonPropertyName("connected")]      bool                                     Connected,
    [property: JsonPropertyName("controllerMode")] string                                   ControllerMode,
    [property: JsonPropertyName("note")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   string?                                  Note,
    [property: JsonPropertyName("routines")]       IReadOnlyList<RoutinePendingEditState>   Routines,
    [property: JsonPropertyName("staged")]         StagedEditsState                         Staged,
    [property: JsonPropertyName("observedAt")]     DateTimeOffset                           ObservedAt,
    [property: JsonPropertyName("programs")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                   IReadOnlyList<ProgramTestedEditsState>?  Programs = null,
    [property: JsonPropertyName("targetMajorRevision")] int?                                    TargetMajorRevision = null,
    [property: JsonPropertyName("mnemonicSet")]    string                                   MnemonicSet = MnemonicSetNames.Unknown)
{
    /// <summary>Warnings about the target (an unknown revision applies no mnemonic check); omitted when none.</summary>
    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Warnings { get; init; }
}

/// <summary>
/// Error response from <see cref="PendingEditsHandler.ExecuteAsync"/>.
/// </summary>
/// <param name="Error">Human-readable description of the failure.</param>
/// <param name="Hint">Optional remediation hint; omitted from JSON when null.</param>
public record PendingEditsError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("hint"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Hint = null,
    [property: JsonPropertyName("kind"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Kind = null,
    [property: JsonPropertyName("session"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Session = null);

// ── PendingEditsHandler ────────────────────────────────────────────────────────

/// <summary>
/// Pure handler for the <c>logix_pending_edits</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
///
/// <para>
/// <b>Strictly read-only.</b> This handler calls only
/// <see cref="ILogixProject.ReadConnectedStateAsync"/>,
/// <see cref="ILogixProject.ReadControllerModeAsync"/>, and (via
/// <see cref="ExportRungsHandler"/>) <see cref="ILogixProject.PartialExportToXmlFileAsync"/>.
/// It never calls any <c>PartialImport*</c> method and never touches the controller's
/// program or edit state. No project needs to be open — it operates purely over the
/// active online SDK session (the "upload route").
/// </para>
///
/// <para>
/// <b>Rung <c>Type</c> code table</b> (from the L5X export schema, verified 2026-09-07
/// against <c>RockwellAutomation.LogixDesigner.CSClient</c> 2.2.1109 and the third-party
/// <c>L5Sharp.Core.RungType</c> enum, and partially bench-confirmed):
/// <list type="table">
///   <listheader><term>Code</term><description>SDK name — zone</description></listheader>
///   <item><term><c>N</c></term><description>Normal — no pending edit. Also the default
///     when the <c>Type</c> attribute is absent.</description></item>
///   <item><term><c>I</c></term><description>Insert — pending, untested
///     (offline/<c>LeaveEdits</c>-image insert).</description></item>
///   <item><term><c>D</c></term><description>Delete — pending, untested.</description></item>
///   <item><term><c>R</c></term><description>Replace — pending, untested.</description></item>
///   <item><term><c>IR</c></term><description>InsertReplace — pending, untested
///     (combined insert+replace).</description></item>
///   <item><term><c>e</c></term><description>PendingInsertRung — tested: an insert edit
///     currently live in the online session (bench-confirmed, an earlier change: a
///     <c>logix_propose_rung_edit target="controller"</c> insert re-exported as
///     <c>Type="e"</c> before being accepted).</description></item>
///   <item><term><c>er</c></term><description>PendingReplaceRung — tested: a replace edit
///     currently live in the online session.</description></item>
///   <item><term><c>dN</c></term><description>PendingDeleteRung — tested: a delete edit
///     currently live in the online session.</description></item>
///   <item><term><c>rR</c></term><description>PendingReplace — accepted: a replace edit
///     accepted into the assembly-pending track.</description></item>
///   <item><term><c>rI</c></term><description>PendingReplaceInsert — accepted: an
///     insert+replace edit accepted into the assembly-pending track.</description></item>
///   <item><term><c>rN</c></term><description>PendingReplaceNormal — accepted: an edit
///     accepted and normalized, pending assembly.</description></item>
/// </list>
/// <b>Known, bench-confirmed limitation:</b> a rung published to the
/// controller with <c>PartialImportOption.AcceptEdits</c> has been observed to export
/// as plain <c>N</c> — none of the <c>r</c>-prefixed "accepted" codes above. This reader
/// reports exactly what the export shows; it cannot currently distinguish "no edit" from
/// "an accepted-but-unassembled edit the export does not mark". Any code not in this
/// table is treated conservatively as <c>"pending"</c> rather than silently as normal.
/// </para>
/// </summary>
public static class PendingEditsHandler
{
    /// <summary>Coarse edit-zone bucket a rung <c>Type</c> code is classified into.</summary>
    public enum RungEditZone
    {
        /// <summary>Type "N" (or absent) — no pending edit.</summary>
        None = 0,
        /// <summary>Bare I/D/R/IR — untested, offline/LeaveEdits-image pending edit.</summary>
        Pending = 1,
        /// <summary>Codes e/er/dN — a live, testable online edit zone.</summary>
        Tested = 2,
        /// <summary>rR/rI/rN — accepted into the assembly-pending track.</summary>
        Accepted = 3,
    }

    /// <summary>
    /// The full rung <c>Type</c> code table. See the type-level doc comment for the
    /// provenance of each code and its zone. Exposed internally so tests can assert
    /// the table's shape without duplicating it.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, RungEditZone> RungTypeCodeTable =
        new Dictionary<string, RungEditZone>(StringComparer.Ordinal)
        {
            ["N"]  = RungEditZone.None,
            ["I"]  = RungEditZone.Pending,
            ["D"]  = RungEditZone.Pending,
            ["R"]  = RungEditZone.Pending,
            ["IR"] = RungEditZone.Pending,
            ["e"]  = RungEditZone.Tested,
            ["er"] = RungEditZone.Tested,
            ["dN"] = RungEditZone.Tested,
            ["rR"] = RungEditZone.Accepted,
            ["rI"] = RungEditZone.Accepted,
            ["rN"] = RungEditZone.Accepted,
        };

    /// <summary>
    /// The single source of truth for position counting (bench evidence <c>raw-r5/p6a.jsonl</c>).
    /// These <see cref="RungTypeCodeTable"/> codes mark "the original that disappears after the edit": the session
    /// image keeps the displaced or removed original, but the user never sees it once the edit is published, so it
    /// never counts toward a position (hints, conflicts, inserts, <c>SafeVisiblePosition</c>).
    /// <list type="bullet">
    ///   <item><c>rN</c> - the replaced original in a session image (p6a: index 1 after a workstation replace of rung 0).</item>
    ///   <item><c>dN</c> - the deleted original in a session image.</item>
    ///   <item><c>R</c>, <c>D</c> - the replaced / deleted original in a pending-zone (offline, LeaveEdits) image.</item>
    /// </list>
    /// Every other code is "a rung the user will see": <c>N</c> (untouched original), <c>e</c> (staged insert),
    /// <c>er</c> (the staged replacement rung, p6a index 0), <c>I</c> and <c>IR</c> (pending-zone insert / insert-replace),
    /// and <c>rR</c>/<c>rI</c> (accepted edits, which stay in the routine). Unknown codes are also counted as visible.
    /// </summary>
    internal static readonly IReadOnlySet<string> HiddenOriginalRungCodes =
        new HashSet<string>(StringComparer.Ordinal) { "rN", "dN", "R", "D" };

    /// <summary>True when the rung <c>Type</c> code is an original that disappears after the edit (see <see cref="HiddenOriginalRungCodes"/>).</summary>
    internal static bool IsHiddenOriginalCode(string? code) =>
        code is not null && HiddenOriginalRungCodes.Contains(code);

    /// <summary>
    /// Classifies a raw L5X rung <c>Type</c> attribute value into a
    /// <see cref="RungEditZone"/>. A missing or empty value is treated as <c>"N"</c>
    /// (the schema default). Any code not present in <see cref="RungTypeCodeTable"/>
    /// is classified as <see cref="RungEditZone.Pending"/> — never silently as
    /// <see cref="RungEditZone.None"/> — so an unrecognised marker is always surfaced
    /// as a pending edit for a human to investigate, not dropped.
    /// </summary>
    internal static RungEditZone ClassifyRungType(string? type)
    {
        var code = string.IsNullOrEmpty(type) ? "N" : type;
        return RungTypeCodeTable.TryGetValue(code, out var zone) ? zone : RungEditZone.Pending;
    }

    private static string ZoneToState(RungEditZone zone) => zone switch
    {
        RungEditZone.None     => "none",
        RungEditZone.Pending  => "pending",
        RungEditZone.Tested   => "tested",
        RungEditZone.Accepted => "accepted",
        _                     => "pending",
    };

    /// <summary>
    /// Builds the SDK XPath for the requested scope.
    /// </summary>
    /// <param name="program">Program name, or <c>null</c>/blank for all programs (<c>Controller/Programs</c>).</param>
    /// <param name="routine">
    /// Routine name, or <c>null</c>/blank for every routine in <paramref name="program"/>.
    /// Ignored when <paramref name="program"/> is blank.
    /// </param>
    internal static string BuildXPath(string? program, string? routine)
    {
        if (string.IsNullOrWhiteSpace(program))
            return "Controller/Programs";

        var progXPath = $"Controller/Programs/Program[@Name='{program}']";
        return string.IsNullOrWhiteSpace(routine)
            ? progXPath
            : $"{progXPath}/Routines/Routine[@Name='{routine}']";
    }

    /// <summary>
    /// Resolves a routine XPath of the form
    /// <c>Controller/Programs/Program[@Name='P']/Routines/Routine[@Name='R']</c> (single or double
    /// quotes) to its program and routine names. Returns <c>false</c> for any other shape.
    /// </summary>
    internal static bool TryParseRoutineXPath(string xPath, out string program, out string routine)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            xPath ?? string.Empty,
            @"Program\[@Name=['""]([^'""]+)['""]\]/Routines/Routine\[@Name=['""]([^'""]+)['""]\]");
        program = m.Success ? m.Groups[1].Value : string.Empty;
        routine = m.Success ? m.Groups[2].Value : string.Empty;
        return m.Success;
    }

    /// <summary>
    /// Compares (program, routine) keys ignoring case: Logix names are case-insensitive, so a rung
    /// staged into <c>mainroutine</c> belongs to <c>MainRoutine</c>.
    /// </summary>
    internal sealed class RoutineNameKeyComparer : IEqualityComparer<(string Program, string Routine)>
    {
        /// <summary>The shared instance.</summary>
        internal static readonly RoutineNameKeyComparer Instance = new();

        /// <inheritdoc />
        public bool Equals((string Program, string Routine) x, (string Program, string Routine) y) =>
            string.Equals(x.Program, y.Program, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Routine, y.Routine, StringComparison.OrdinalIgnoreCase);

        /// <inheritdoc />
        public int GetHashCode((string Program, string Routine) k) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(k.Program),
                StringComparer.OrdinalIgnoreCase.GetHashCode(k.Routine));
    }

    /// <summary>Note attached to a program whose <c>TestEdits</c> attribute could not be read.</summary>
    internal const string TestEditsAbsentNote =
        "TestEdits attribute absent from the program export; tested state is unknown (not guessed).";

    /// <summary>
    /// Parses the <c>TestEdits</c> attribute of a <c>&lt;Program&gt;</c> element.
    /// Returns <c>null</c> when absent or not a boolean — never a guess.
    /// </summary>
    internal static bool? ParseTestEdits(XElement programElem)
    {
        var raw = (string?)programElem.Attribute("TestEdits");
        return bool.TryParse(raw?.Trim(), out var value) ? value : null;
    }

    /// <summary>
    /// Reads the <c>TestEdits</c> state of every <c>&lt;Program&gt;</c> in an L5X document.
    /// </summary>
    internal static List<ProgramTestedEditsState> ReadProgramTestedEdits(string l5x)
    {
        var doc = SafeXml.ParseDocument(l5x);
        return doc.Descendants("Program")
            .Select(p =>
            {
                var tested = ParseTestEdits(p);
                return new ProgramTestedEditsState(
                    (string?)p.Attribute("Name") ?? string.Empty,
                    tested,
                    tested is null ? TestEditsAbsentNote : null);
            })
            .ToList();
    }

    /// <summary>Derives the per-routine <c>zoneState</c> from pending rungs and <c>TestEdits</c>.</summary>
    internal static string ComputeZoneState(bool hasPendingEdits, bool? testedEdits) =>
        !hasPendingEdits  ? "none"
        : testedEdits is true  ? "tested"
        : testedEdits is false ? "untested"
        : "unknown";

    /// <summary>
    /// Parses an exported L5X document and returns the edit-zone state of every
    /// RLL (ladder) routine it contains. Non-RLL routines (ST/FBD/SFC — no
    /// <c>RLLContent</c> element) are silently skipped, mirroring
    /// <see cref="GetRoutineHandler"/>'s ladder-only scope.
    /// </summary>
    /// <param name="l5x">The L5X XML string to parse.</param>
    /// <param name="sessionStagedTexts">
    /// Optional census (normalised rung <c>&lt;Text&gt;</c> → count) of the rungs staged by
    /// the current session via <c>target="workstation"</c> and not yet published to the
    /// controller. Each export rung of any <c>Type</c> whose normalised text matches an
    /// unconsumed entry is counted in <see cref="RoutinePendingEditState.StagedRungs"/>
    /// instead of <see cref="RoutinePendingEditState.Rungs"/>, and is excluded from
    /// <see cref="RoutinePendingEditState.PendingRungs"/> and the worst-zone calculation —
    /// it is a server-side staged edit, not a controller rung (the server-managed ACD
    /// holds it as a normal rung). The census is not routine-scoped, so an unscoped read
    /// may attribute a staged text to another routine's identical rung; the total never
    /// exceeds what was staged. When <c>null</c> or empty nothing is reclassified.
    /// </param>
    /// <param name="stagedByRoutine">
    /// Optional per-routine census, keyed by (program, routine) name. When supplied, each routine
    /// consumes only its own entry first, so identical staged text in two routines is never attributed
    /// across them; <paramref name="sessionStagedTexts"/> then acts only as a shared fallback for staged
    /// rungs whose routine could not be resolved. When <c>null</c> the flat census is used unscoped.
    /// </param>
    /// <param name="sessionReplacedRungTexts">
    /// Optional set of normalised rung <c>&lt;Text&gt;</c> values for rungs that were
    /// <em>replaced or deleted</em> by this session's workstation-target staging calls.
    /// Any non-normal rung (typically <c>r*</c> or <c>d*</c> types) whose normalised
    /// text is present in this set is classified as a session-originated replaced rung:
    /// it is excluded from <see cref="RoutinePendingEditState.PendingRungs"/> and the
    /// worst-zone calculation, and instead collected in the returned
    /// <c>replacedRungs</c> list.  When <c>null</c> or empty, no rungs are reclassified
    /// as replaced.
    /// </param>
    /// <param name="programTestedEditsOverride">
    /// Optional program-name → <c>TestEdits</c> map taking precedence over the attribute on the
    /// parsed document's own <c>&lt;Program&gt;</c> element. Used for routine-scope exports,
    /// which do not carry the attribute. A program not in the map falls back to the document.
    /// </param>
    /// <returns>
    /// A tuple of (routines, replacedRungs) where <c>routines</c> is the per-routine
    /// pending-edit state reflecting only controller-side edits, and <c>replacedRungs</c>
    /// is the flat list of rungs identified as session-originated replaced originals
    /// across all routines.
    /// </returns>
    internal static (List<RoutinePendingEditState> Routines, List<PendingEditRungEntry> ReplacedRungs) ParseRoutines(
        string l5x,
        IReadOnlyDictionary<string, int>? sessionStagedTexts = null,
        IReadOnlySet<string>? sessionReplacedRungTexts = null,
        IReadOnlyDictionary<string, bool?>? programTestedEditsOverride = null,
        IReadOnlyDictionary<(string Program, string Routine), IReadOnlyDictionary<string, int>>? stagedByRoutine = null)
    {
        var doc = SafeXml.ParseDocument(l5x);
        var routines      = new List<RoutinePendingEditState>();

        // Routine and program names are case-insensitive: look the per-routine census up that way
        // whatever comparer the caller's dictionary was built with.
        if (stagedByRoutine is not null)
            stagedByRoutine = stagedByRoutine.ToDictionary(
                kv => kv.Key, kv => kv.Value, RoutineNameKeyComparer.Instance);

        // Remaining census of session-staged rung texts (text -> how many staged). Each
        // matching export rung consumes one, so the staged total can never exceed what
        // this session actually staged, even when a controller rung has identical text.
        var stagedBudget = sessionStagedTexts is { Count: > 0 }
            ? new Dictionary<string, int>(sessionStagedTexts, StringComparer.Ordinal)
            : null;
        var replacedRungs = new List<PendingEditRungEntry>();

        foreach (var programElem in doc.Descendants("Program"))
        {
            var programName = (string?)programElem.Attribute("Name") ?? string.Empty;
            var testedEdits = programTestedEditsOverride is not null
                              && programTestedEditsOverride.TryGetValue(programName, out var overridden)
                ? overridden
                : ParseTestEdits(programElem);

            var routineElems = programElem.Element("Routines")?.Elements("Routine")
                                ?? Enumerable.Empty<XElement>();

            foreach (var routineElem in routineElems)
            {
                var routineName = (string?)routineElem.Attribute("Name") ?? string.Empty;

                var rllContent = routineElem.Element("RLLContent");
                // A self-closed `<Routine Type="RLL" Name="X"/>` has no RLLContent
                // element but is still a ladder routine: it has 0 rungs and edit state none, and
                // is included (an empty routine can sit inside a program's edit zone). Other children
                // (e.g. a <Description>) do not change that: any RLL routine without RLLContent is empty.
                var isSelfClosedRll = rllContent is null
                    && string.Equals((string?)routineElem.Attribute("Type"), "RLL", StringComparison.OrdinalIgnoreCase);
                if (rllContent is null && !isSelfClosedRll)
                    continue; // Not a ladder routine — no rungs to classify.

                // Per-routine budget (when a per-routine census was supplied); the shared
                // flat budget remains the fallback for staged rungs with no resolvable routine.
                Dictionary<string, int>? routineBudget = null;
                if (stagedByRoutine is not null
                    && stagedByRoutine.TryGetValue((programName, routineName), out var routineCensus)
                    && routineCensus.Count > 0)
                    routineBudget = new Dictionary<string, int>(routineCensus, StringComparer.Ordinal);

                var pendingRungs = new List<PendingEditRungEntry>();
                var worstZone     = RungEditZone.None;
                var rungCount     = 0;
                var stagedCount   = 0;

                foreach (var rungElem in rllContent?.Elements("Rung") ?? Enumerable.Empty<XElement>())
                {
                    var number = (int?)rungElem.Attribute("Number") ?? rungCount + stagedCount;
                    var type   = (string?)rungElem.Attribute("Type") ?? "N";
                    var text   = rungElem.Element("Text")?.Value ?? string.Empty;

                    var zone = ClassifyRungType(type);

                    // A rung this session staged is not a controller rung, whatever its
                    // Type: the server-managed ACD holds a staged LeaveEdits rung as a
                    // normal rung (Type N), so match on the staged-text census first.
                    if (routineBudget is not null || stagedBudget is not null)
                    {
                        var key = ProposeRungEditHandler.NormalizeRungText(text);
                        // The shared comparator, so a staged `TON(T,3000,0)` is
                        // recognised as the exported `TON(T,?,?)`.
                        if (routineBudget is not null
                            && RungTextComparer.FindKey(routineBudget, key) is { } mineKey)
                        {
                            routineBudget[mineKey]--;
                            stagedCount++;
                            continue;
                        }
                        if (stagedBudget is not null
                            && RungTextComparer.FindKey(stagedBudget, key) is { } leftKey)
                        {
                            stagedBudget[leftKey]--;
                            stagedCount++;
                            continue;
                        }
                    }

                    rungCount++;
                    if (zone != RungEditZone.None)
                    {
                        var normalizedText = ProposeRungEditHandler.NormalizeRungText(text);

                        // If the rung's normalised text matches a session-replaced rung,
                        // it is the original rung displaced by this session's replace/delete
                        // stage. Move it to the replacedRungs list so it appears under
                        // staged.replacedRungs rather than pendingRungs, and exclude it from
                        // the worst-zone calculation so that state reflects only genuine
                        // controller-side pending edits.
                        if (sessionReplacedRungTexts is { Count: > 0 } && RungTextComparer.AnyExpectedMatches(sessionReplacedRungTexts, normalizedText))
                        {
                            replacedRungs.Add(new PendingEditRungEntry(number, type, text));
                            continue;
                        }

                        pendingRungs.Add(new PendingEditRungEntry(number, type, text));
                        if (zone > worstZone)
                            worstZone = zone;
                    }
                }

                routines.Add(new RoutinePendingEditState(
                    Program:      programName,
                    Routine:      routineName,
                    Rungs:        rungCount,
                    PendingRungs: pendingRungs,
                    State:        ZoneToState(worstZone),
                    ProgramTestedEdits: testedEdits,
                    ZoneState:    ComputeZoneState(pendingRungs.Count > 0, testedEdits),
                    StagedRungs:  stagedCount));
            }
        }

        return (routines, replacedRungs);
    }

    /// <summary>
    /// Reads the edit-zone state of the requested routine(s) on the active online
    /// SDK session. Read-only — never calls any <c>PartialImport*</c> method and
    /// never mutates controller or session state.
    /// </summary>
    /// <param name="program">
    /// Optional program name to scope the read to. When <c>null</c>/blank, every
    /// program in the controller is read.
    /// </param>
    /// <param name="routine">
    /// Optional routine name to scope the read to. Requires <paramref name="program"/>
    /// to also be supplied. When <c>null</c>/blank, every routine in the scoped
    /// program(s) is read.
    /// </param>
    /// <param name="session">
    /// Open <see cref="LogixProjectSession"/> to use. Must already be in the
    /// <c>Open</c> state — this method does not open or close it. No project file
    /// needs to have been opened via <c>logix_open_project</c> (upload route).
    /// </param>
    /// <param name="ct">Cancellation token forwarded to the SDK calls.</param>
    /// <returns>
    /// A <see cref="PendingEditsResult"/> on success, or a <see cref="PendingEditsError"/>
    /// on failure. Never throws (except <see cref="OperationCanceledException"/>).
    /// </returns>
    public static async Task<object> ExecuteAsync(
        string?              program,
        string?              routine,
        LogixProjectSession  session,
        CancellationToken    ct = default)
    {
        // Dead-session short-circuit: report session: faulted without calling the SDK.
        if (session.IsFaulted)
            return Faulted(session.FaultedError());

        using var operation = session.BeginOperation(RockwellErrorHints.OperationPendingRead);
        var result = await ExecuteCoreAsync(program, routine, session, ct).ConfigureAwait(false);

        // A fatal during this call marks the session faulted: report that, not the wrapped message.
        return result is PendingEditsError && session.IsFaulted ? Faulted(session.FaultedError()) : result;
    }

    /// <summary>
    /// A fresh read that shows a routine's zone holding every rung the failed
    /// publish was expected to leave pending (as <c>I</c>/<c>IR</c> rungs, the census's own criterion)
    /// resolves that routine's recorded <c>PublishMismatch</c>. Routines absent from this read, or whose
    /// zone still differs, stay unresolved.
    /// </summary>
    /// <param name="session">The session holding the recorded mismatches.</param>
    /// <param name="routines">The routines read just now.</param>
    internal static void ResolveMatchingPublishMismatches(
        LogixProjectSession session, IReadOnlyList<RoutinePendingEditState> routines)
    {
        foreach (var (mismatchXPath, expected) in session.UnresolvedPublishMismatches)
        {
            if (!TryParseRoutineXPath(mismatchXPath, out var prog, out var rtn))
                continue;
            var read = routines.FirstOrDefault(r =>
                string.Equals(r.Program, prog, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Routine, rtn, StringComparison.OrdinalIgnoreCase));
            if (read is null)
                continue;

            var found = read.PendingRungs
                .Where(r => r.Type == "I" || r.Type == "IR")
                .Select(r => ProposeRungEditHandler.NormalizeRungText(r.Text))
                .ToList();
            // The shared comparator (the export's `?` operands match the staged literals).
            if (expected.All(e => RungTextComparer.ContainsMatch(found, e)))
                session.ResolvePublishMismatch(mismatchXPath);
        }
    }

    /// <summary>
    /// Builds the <c>PublishMismatchUnresolved</c> refusal for a test or assemble of
    /// <paramref name="program"/>, or <c>null</c> when the session holds no unresolved mismatch for it.
    /// </summary>
    /// <param name="session">The session holding the recorded mismatches.</param>
    /// <param name="toolName">The refusing tool, named in the message.</param>
    /// <param name="program">Program the tool targets.</param>
    /// <param name="launch">Launch plan stamped on the refusal (the runner is never invoked).</param>
    internal static CancelPendingEditsError? RefuseOnUnresolvedPublishMismatch(
        LogixProjectSession session, string toolName, string program,
        (string? LaunchMode, string? RunLevel) launch)
    {
        var unresolved = session.UnresolvedPublishMismatchesForProgram(program);
        if (unresolved.Count == 0)
            return null;

        var names = string.Join(", ", unresolved.Select(x => $"'{x}'"));
        return new CancelPendingEditsError(
            $"{toolName} refused: an earlier publish ended in PublishMismatch for {names} and it is " +
            "still unresolved. The controller's pending zone does not match what was staged, so testing " +
            "or assembling it could leave rungs missing on the controller.",
            LaunchMode: launch.LaunchMode,
            RunLevel:   launch.RunLevel,
            Launched:   false,
            Kind:       "PublishMismatchUnresolved",
            Hint:
                "Review the zone with logix_pending_edits. The mismatch clears when logix_pending_edits " +
                "shows the zone holds every staged rung, or when logix_cancel_pending_edits succeeds " +
                "(then re-stage and publish again).");
    }

    private static PendingEditsError Faulted(TagServiceError e) =>
        new(e.Message, e.Hint, ErrorKinds.SdkServerFaulted, ErrorKinds.SessionFaulted);

    private static async Task<object> ExecuteCoreAsync(
        string?              program,
        string?              routine,
        LogixProjectSession  session,
        CancellationToken    ct)
    {
        if (!string.IsNullOrWhiteSpace(routine) && string.IsNullOrWhiteSpace(program))
            return new PendingEditsError(
                "routine was supplied without program. Program is required whenever " +
                "routine is specified.");

        if ((program?.Contains('\'') ?? false) || (routine?.Contains('\'') ?? false))
            return new PendingEditsError(
                "program and routine must not contain a single-quote character — " +
                "it cannot be safely embedded in the SDK XPath.");

        // ── connected / controllerMode — pure reads, no side effects ───────────
        ConnectedState connectedState;
        try
        {
            connectedState = await session.ExecuteAsync(
                p => p.ReadConnectedStateAsync(ct), ct).ConfigureAwait(false);
        }
        catch (TagServiceException ex)
        {
            return new PendingEditsError($"ReadConnectedStateAsync failed: {ex.Error.Message}");
        }
        catch (OperationNotPerformedException ex)
        {
            return new PendingEditsError($"ReadConnectedStateAsync could not be performed: {ex.Message}");
        }
        catch (OperationFailedException ex)
        {
            return new PendingEditsError($"ReadConnectedStateAsync failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PendingEditsError($"Unexpected error reading connected state: {ex.Message}");
        }

        ControllerMode controllerMode;
        try
        {
            controllerMode = await session.ExecuteAsync(
                p => p.ReadControllerModeAsync(ct), ct).ConfigureAwait(false);
        }
        catch (TagServiceException ex)
        {
            return new PendingEditsError($"ReadControllerModeAsync failed: {ex.Error.Message}");
        }
        catch (OperationNotPerformedException ex)
        {
            return new PendingEditsError($"ReadControllerModeAsync could not be performed: {ex.Message}");
        }
        catch (OperationFailedException ex)
        {
            return new PendingEditsError($"ReadControllerModeAsync failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PendingEditsError($"Unexpected error reading controller mode: {ex.Message}");
        }

        // ── export — delegates to ExportRungsHandler, which is itself read-only ─
        var xPath        = BuildXPath(program, routine);
        var exportResult = await ExportRungsHandler.ExecuteAsync(xPath, session, ct)
            .ConfigureAwait(false);

        if (exportResult is ExportRungsError exportError)
            return new PendingEditsError($"Export failed for '{xPath}': {exportError.Error}");

        var l5x = ((ExportRungsResult)exportResult).L5X;

        // ── Build the session-staged rung-text set for ParseRoutines filtering ──
        // StagedRungBuffer maps (routine XPath, insertPosition) → List<normalised rung
        // text>. Group it per routine so each routine's staged rungs are counted only
        // against that routine (two routines staged at the same position stay separate).
        // Keys whose XPath cannot be resolved to a (program, routine) feed a shared
        // fallback census. ParseRoutines uses these to exclude session-staged rungs from
        // the controller rung count and from the controller-pending classification so
        // that rungs / pendingRungs / state reflect only what the controller itself holds
        // (item 7).
        Dictionary<(string Program, string Routine), IReadOnlyDictionary<string, int>>? stagedByRoutine = null;
        Dictionary<string, int>? sessionStagedTexts = null;
        if (session.StagedRungBuffer.Count > 0)
        {
            var perRoutine = new Dictionary<(string, string), Dictionary<string, int>>(RoutineNameKeyComparer.Instance);
            var unresolved = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (bufferKey, texts) in session.StagedRungBuffer)
            {
                var target = TryParseRoutineXPath(bufferKey.RoutineXPath, out var prog, out var rtn)
                    ? (perRoutine.TryGetValue((prog, rtn), out var existing)
                        ? existing
                        : perRoutine[(prog, rtn)] = new Dictionary<string, int>(StringComparer.Ordinal))
                    : unresolved;
                foreach (var t in texts)
                    target[t] = target.TryGetValue(t, out var n) ? n + 1 : 1;
            }
            stagedByRoutine = perRoutine.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyDictionary<string, int>)kv.Value,
                RoutineNameKeyComparer.Instance);
            sessionStagedTexts = unresolved.Count > 0 ? unresolved : null;
        }

        // Build the session-replaced rung-text set: the normalised texts of original
        // rungs displaced by this session's replace/delete stages. ParseRoutines will
        // use this to segregate those rungs into staged.replacedRungs rather than
        // pendingRungs so that state reflects only genuine controller-side pending
        // edits.
        IReadOnlySet<string>? sessionReplacedRungTexts = session.StagedReplacedRungTexts.Count > 0
            ? session.StagedReplacedRungTexts
            : null;

        // ── TestEdits — read from the program export ───────────────────────────
        // A program/controller-scope export carries TestEdits on each <Program>. A
        // routine-scope export does NOT, so issue one extra export of the parent
        // program (fresh temp file, same ExportRungsHandler path) and read it there.
        List<ProgramTestedEditsState> programs;
        Dictionary<string, bool?>?    testedOverride = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(routine))
            {
                var programXPath = BuildXPath(program, null);
                var programExport = await ExportRungsHandler.ExecuteAsync(programXPath, session, ct)
                    .ConfigureAwait(false);

                if (programExport is ExportRungsResult programOk)
                {
                    programs = ReadProgramTestedEdits(programOk.L5X);
                }
                else
                {
                    // Degrade to null + note rather than failing the read: never guess.
                    var why = ((ExportRungsError)programExport).Error;
                    programs = new List<ProgramTestedEditsState>
                    {
                        new(program!, null,
                            $"Program export for TestEdits failed ({why}); tested state is unknown (not guessed)."),
                    };
                }

                testedOverride = programs.ToDictionary(p => p.Program, p => p.TestedEdits);
                // A program that exported no matching <Program> element still needs an entry.
                if (!testedOverride.ContainsKey(program!))
                {
                    programs.Add(new ProgramTestedEditsState(program!, null, TestEditsAbsentNote));
                    testedOverride[program!] = null;
                }
            }
            else
            {
                programs = ReadProgramTestedEdits(l5x);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PendingEditsError(
                $"Failed to parse the exported L5X for '{xPath}': {ex.Message}");
        }

        List<RoutinePendingEditState> routines;
        List<PendingEditRungEntry>    replacedRungs;
        try
        {
            (routines, replacedRungs) = ParseRoutines(
                l5x, sessionStagedTexts, sessionReplacedRungTexts, testedOverride, stagedByRoutine);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PendingEditsError(
                $"Failed to parse the exported L5X for '{xPath}': {ex.Message}");
        }

        if (routines.Count == 0)
            return new PendingEditsError(
                $"No RLL (ladder) routines found at '{xPath}'. " +
                "logix_pending_edits only classifies ladder routines — check the " +
                "program/routine name, or that the routine's Type is RLL.");

        // ── Staged tag names — filtered to new tags only ──────────────────────
        // Delegate to the session's shared helper: excludes tag names that already
        // existed in the controller's tag inventory before this session began staging
        // (the pre-stage census). Context declarations for pre-existing tags are
        // included in the L5X export envelope for completeness but represent no new
        // controller state that a publish would create. The helper returns null when
        // the filtered list is empty; we coerce to an empty list for the non-nullable
        // StagedEditsState.TagNames field.
        var stagedTagNames = (IReadOnlyList<string>)(session.GetNewStagedTagNames() ?? Array.Empty<string>());

        // Map the SDK ControllerMode enum to a display string.
        // When the SDK returns a numeric value that is not a defined enum member
        // (which Echo controllers have been observed to do), we normalise the
        // string to "Unknown" rather than surfacing a bare integer.
        var modeString = ControllerModeReader.MapMode(controllerMode);

        var isOnline = connectedState == ConnectedState.Online;

        // Annotate the "Unknown" mode only when the session is confirmed online.
        // An Unknown mode while ONLINE is unexpected (Echo controllers have shown
        // this); off-line Unknown is unremarkable (the mode simply hasn't been
        // read yet) and is returned without annotation.
        var modeNote = (modeString == "Unknown" && isOnline)
            ? "SDK returned Unknown while online; Echo controllers have shown this"
            : (string?)null;

        IReadOnlyList<PendingEditRungEntry>? stagedReplacedRungs =
            replacedRungs.Count > 0 ? replacedRungs : null;

        ResolveMatchingPublishMismatches(session, routines);

        return new PendingEditsResult(
            Connected:      isOnline,
            ControllerMode: modeString,
            Note:           modeNote,
            Routines:       routines,
            Staged:         new StagedEditsState(
                                  Math.Max(session.StagedRungCount, session.StagedRungBuffer.Values.Sum(v => v.Count)),
                                  stagedTagNames, stagedReplacedRungs),
            ObservedAt:     DateTimeOffset.UtcNow,
            Programs:       programs);
    }
}
#endif
