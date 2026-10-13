using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

// ── Origin-trace result model ────────────────────────────────────────────────
// These types are the JSON shape of logix_trace_origin (nodes, edges, externalInputs, literals,
// gaps, cycles) and the in-memory graph the text and Mermaid renderers read.

/// <summary>One operand of a write or a gating condition, as written in the project.</summary>
/// <param name="Text">The operand text, e.g. <c>Speed_SP</c> or <c>5</c>.</param>
/// <param name="Tag">The tag it resolves to (aliases followed), or <c>null</c> for a literal.</param>
/// <param name="Literal"><c>true</c> when the operand is a constant.</param>
/// <param name="Note">Extra detail, e.g. the remote element of a MSG.</param>
public record TraceSource(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("tag"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tag,
    [property: JsonPropertyName("literal"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Literal = false,
    [property: JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null);

/// <summary>A condition that gates a write: a rung contact or comparison, or an enclosing ST construct.</summary>
/// <param name="Kind"><c>contact</c>, <c>compare</c>, <c>one-shot</c>, <c>branch</c>, <c>if</c>, <c>case</c>, <c>loop</c> or <c>sfc-action</c>.</param>
/// <param name="Text">The condition as written, e.g. <c>XIC(Fault)</c> or <c>IF Cmd_Start AND NOT Fault</c>.</param>
/// <param name="Tags">The tags the condition tests (aliases followed).</param>
public record TraceCondition(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags);

/// <summary>
/// One node of the trace: a <c>tag</c> (a storage location the trace asks about) or a <c>write</c>
/// (one place where program logic writes a tag). Fields that do not apply to the node type are omitted.
/// </summary>
public class TraceNode
{
    /// <summary>Node id: <c>t1</c>, <c>t2</c>… for tags, <c>w1</c>, <c>w2</c>… for writes.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary><c>tag</c> or <c>write</c>.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";

    // ── tag nodes ──
    /// <summary>Tag nodes: the tag path; controller tags bare, program tags as <c>Program:P.Tag</c>.</summary>
    [JsonPropertyName("tag"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Tag { get; set; }
    /// <summary>Tag nodes: <c>true</c> for the tag the trace started from.</summary>
    [JsonPropertyName("root"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Root { get; set; }
    /// <summary>Tag nodes: the tag this alias points at (<c>AliasFor</c>).</summary>
    [JsonPropertyName("aliasOf"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AliasOf { get; set; }
    /// <summary>Tag nodes: alias names through which the trace reached this storage location.</summary>
    [JsonPropertyName("aliases"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Aliases { get; set; }
    /// <summary>Tag nodes: the tag's data type when it is declared.</summary>
    [JsonPropertyName("dataType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? DataType { get; set; }
    /// <summary>Tag nodes: distance from the root in tag hops.</summary>
    [JsonPropertyName("depth"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Depth { get; set; }
    /// <summary>Tag nodes: <c>false</c> when its writers were not looked up (maxDepth, node cap).</summary>
    [JsonPropertyName("expanded"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Expanded { get; set; }

    // ── write nodes ──
    /// <summary>Write nodes: program that holds the write.</summary>
    [JsonPropertyName("program"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Program { get; set; }
    /// <summary>Write nodes: routine (or the owning construct for MSG and parameters).</summary>
    [JsonPropertyName("routine"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Routine { get; set; }
    /// <summary>Write nodes: <c>rung 11</c>, <c>ST line 4</c>, <c>FBD sheet 1 ORef 3</c>…</summary>
    [JsonPropertyName("location"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Location { get; set; }
    /// <summary>Write nodes: <c>RLL</c>, <c>ST</c>, <c>FBD</c>, <c>SFC</c>, <c>MSG</c>, <c>Parameter</c> or <c>Connection</c>.</summary>
    [JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Kind { get; set; }
    /// <summary>Write nodes: the instruction mnemonic (<c>OTU</c>, <c>MOV</c>, <c>:=</c>…).</summary>
    [JsonPropertyName("instruction"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Instruction { get; set; }
    /// <summary>Write nodes: the instruction or statement as written.</summary>
    [JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Text { get; set; }
    /// <summary>Write nodes: the operand that is written, as written.</summary>
    [JsonPropertyName("target"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Target { get; set; }
    /// <summary>Write nodes: the alias the write goes through, when it does.</summary>
    [JsonPropertyName("via"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Via { get; set; }
    /// <summary>Write nodes: the value written: a literal, or for OTE/OTL/OTU/CLR/RES <c>1</c>/<c>latch</c>/<c>unlatch</c>/<c>0</c>/<c>reset</c>.</summary>
    [JsonPropertyName("value"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Value { get; set; }
    /// <summary>Write nodes: <c>literal</c> or <c>implicit</c> when <see cref="Value"/> is set.</summary>
    [JsonPropertyName("valueKind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ValueKind { get; set; }
    /// <summary>Write nodes: the operands the written value is computed from.</summary>
    [JsonPropertyName("sources"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<TraceSource>? Sources { get; set; }
    /// <summary>Write nodes: the conditions that gate the write.</summary>
    [JsonPropertyName("conditions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<TraceCondition>? Conditions { get; set; }
    /// <summary>Any node: a caveat the reader should know.</summary>
    [JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Note { get; set; }
}

/// <summary>A directed edge of the trace graph.</summary>
/// <param name="From">Source node id.</param>
/// <param name="To">Target node id.</param>
/// <param name="Kind">
/// <c>writtenBy</c> (tag → write that drives it), <c>source</c> and <c>condition</c> (write → tag it reads),
/// or <c>alias</c> (alias tag → what it points at).
/// </param>
public record TraceEdge(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("kind")] string Kind)
{
    /// <summary>The alias name the operand was written as, when it differs from the resolved tag.</summary>
    [JsonPropertyName("via"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Via { get; set; }
    /// <summary><c>true</c> when the edge closes a cycle.</summary>
    [JsonPropertyName("cycle"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Cycle { get; set; }
    /// <summary>A caveat, e.g. that the writer was matched by name across scopes.</summary>
    [JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Note { get; set; }
}

/// <summary>A tag that nothing in the project writes: where its value must come from outside the program logic.</summary>
/// <param name="Tag">The tag.</param>
/// <param name="Kind"><c>io</c>, <c>consumed</c>, <c>constant</c>, <c>parameter</c> or <c>unwritten</c>.</param>
/// <param name="Detail">One line saying what that means.</param>
/// <param name="Node">Id of the tag node.</param>
public record TraceExternalInput(
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("node")] string Node);

/// <summary>A literal value where the trace stops.</summary>
/// <param name="Node">Write node that uses it.</param>
/// <param name="Value">The value (<c>5</c>, <c>TRUE</c>, or an implicit <c>latch</c>/<c>unlatch</c>/<c>reset</c>/<c>1</c>/<c>0</c>).</param>
/// <param name="Role"><c>source</c>, <c>condition</c> or <c>implicit</c>.</param>
public record TraceLiteral(
    [property: JsonPropertyName("node")] string Node,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("role")] string Role);

/// <summary>A construct the trace did not follow. Never guessed past.</summary>
/// <param name="Kind">
/// <c>JsrParameter</c>, <c>IndirectAddress</c>, <c>UnclassifiedInstruction</c>, <c>UnreadableRoutine</c>,
/// <c>UnparsedRung</c> or <c>OutsideFocus</c>.
/// </param>
/// <param name="Location">Where, as <c>Program/Routine rung N</c> or similar; omitted when general.</param>
/// <param name="Tag">The tag it concerns, when one.</param>
/// <param name="Detail">What was not followed and why.</param>
public record TraceGap(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("location"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Location,
    [property: JsonPropertyName("tag"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tag,
    [property: JsonPropertyName("detail")] string Detail);

/// <summary>A cycle found in the trace: the tags in order, first repeated at the end.</summary>
/// <param name="Path">Tag names around the loop.</param>
public record TraceCycle(
    [property: JsonPropertyName("path")] IReadOnlyList<string> Path);

/// <summary>The graph the trace builds; the text, JSON and Mermaid outputs are all views of it.</summary>
public class TraceGraph
{
    /// <summary>Most nodes (tags plus writes) one trace returns.</summary>
    public const int MaxNodes = 200;

    /// <summary>The tag as the caller named it.</summary>
    public string Requested { get; set; } = "";
    /// <summary>The storage location it resolves to once aliases are followed.</summary>
    public string Resolved { get; set; } = "";
    /// <summary>Nodes in creation order (breadth first from the root).</summary>
    public List<TraceNode> Nodes { get; } = new();
    /// <summary>Edges.</summary>
    public List<TraceEdge> Edges { get; } = new();
    /// <summary>Tags nothing writes.</summary>
    public List<TraceExternalInput> ExternalInputs { get; } = new();
    /// <summary>S: status operands (<c>controllerStatus</c>): set by the controller, so not external inputs.</summary>
    [JsonPropertyName("controllerStatus")] public List<TraceExternalInput> ControllerStatus { get; } = new();
    /// <summary>Literals the trace stopped at.</summary>
    public List<TraceLiteral> Literals { get; } = new();
    /// <summary>Constructs not followed.</summary>
    public List<TraceGap> Gaps { get; } = new();
    /// <summary>Cycles found.</summary>
    public List<TraceCycle> Cycles { get; } = new();
    /// <summary>Tags whose writers exist but were not followed because <c>maxDepth</c> was reached.</summary>
    public List<string> DepthLimited { get; } = new();
    /// <summary><c>true</c> when the 200-node cap stopped the trace.</summary>
    public bool Truncated { get; set; }
    /// <summary>Applied <c>maxDepth</c>.</summary>
    public int MaxDepth { get; set; }
    /// <summary>Applied program filter, or <c>null</c>.</summary>
    public string? Focus { get; set; }

    /// <summary>Write nodes only.</summary>
    public IEnumerable<TraceNode> WriteNodes => Nodes.Where(n => n.Type == "write");
    /// <summary>Tag nodes only.</summary>
    public IEnumerable<TraceNode> TagNodes => Nodes.Where(n => n.Type == "tag");

    /// <summary>How to narrow a truncated trace.</summary>
    public string Advice =>
        $"The trace reached the {MaxNodes}-node cap and stopped. Narrow it: pass focus (a program name) to follow " +
        "only that program's logic" + (MaxDepth > 1 ? $", or lower maxDepth (now {MaxDepth})" : "") +
        ", or trace one of the tags it reached instead of this one.";
}

/// <summary>Rendered trace text, with what the <c>maxChars</c> budget cut.</summary>
/// <param name="Text">The text, including the cut notice when truncated.</param>
/// <param name="Truncated"><c>true</c> when lines were cut to fit the budget.</param>
/// <param name="OmittedWrites">Write nodes that were first printed in the omitted lines.</param>
/// <param name="TruncatedAt">The depth and branch of the first omitted line, or <c>null</c> when not cut.</param>
internal sealed record TraceText(string Text, bool Truncated, int OmittedWrites, string? TruncatedAt);

// ── Text and Mermaid renderers ───────────────────────────────────────────────

/// <summary>Renders a <see cref="TraceGraph"/> as an indented text tree and as a Mermaid flowchart.</summary>
internal static class TraceRenderer
{
    private sealed class Line
    {
        public string Label = "";
        public List<string> Details = new();
        public List<Line> Children = new();
        /// <summary>Tag hops from the root, for a tag line; <c>null</c> for a write line.</summary>
        public int? Depth;
        /// <summary>The tag name, for a tag line.</summary>
        public string? Tag;
        /// <summary><c>true</c> for a write printed in full here (not "traced above").</summary>
        public bool NewWrite;
    }

    /// <summary>One output line with where it sits in the trace.</summary>
    private readonly record struct OutLine(string Text, int Depth, string Branch, bool NewWrite, bool Tree);

    /// <summary>Renders the text tree, followed by external inputs, literals, gaps and cycles, with no size limit.</summary>
    public static string Text(TraceGraph g) => Text(g, int.MaxValue).Text;

    /// <summary>
    /// Renders the text tree, followed by external inputs, literals, gaps and cycles. When the text is longer
    /// than <paramref name="maxChars"/> it is cut at whole lines and a notice line is appended.
    /// </summary>
    /// <param name="g">The whole trace graph.</param>
    /// <param name="maxChars">Output budget in characters, notice included.</param>
    public static TraceText Text(TraceGraph g, int maxChars)
    {
        var byId = g.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var outgoing = g.Edges.GroupBy(e => e.From).ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        var external = g.ExternalInputs.ToDictionary(e => e.Node, StringComparer.Ordinal);
        var status = g.ControllerStatus.ToDictionary(e => e.Node, StringComparer.Ordinal);
        var printed = new HashSet<string>(StringComparer.Ordinal);
        var onPath = new HashSet<string>(StringComparer.Ordinal);

        Line TagLine(TraceNode t, string role)
        {
            var line = new Line { Label = (role.Length > 0 ? role + " " : "") + t.Tag, Depth = t.Depth ?? 0, Tag = t.Tag };
            if (t.Aliases is { Count: > 0 }) line.Label += $"  (reached as alias {string.Join(", ", t.Aliases)})";
            if (t.AliasOf is not null) line.Label += $"  (alias of {t.AliasOf})";
            if (onPath.Contains(t.Id)) { line.Label += "  <- cycle: already on this path"; return line; }
            if (!printed.Add(t.Id)) { line.Label += "  (traced above)"; return line; }

            onPath.Add(t.Id);
            outgoing.TryGetValue(t.Id, out var edges);
            foreach (var e in (edges ?? new List<TraceEdge>()).Where(e => e.Kind == "alias"))
                line.Children.Add(TagLine(byId[e.To], ""));

            if (external.TryGetValue(t.Id, out var ext))
                line.Details.Add($"external input ({ext.Kind}): {ext.Detail}");
            else if (status.TryGetValue(t.Id, out var st))
                line.Details.Add($"{st.Kind}: {st.Detail}");
            else if (t.Expanded == false)
                line.Details.Add(t.Note ?? "not expanded");
            foreach (var e in (edges ?? new List<TraceEdge>()).Where(e => e.Kind == "writtenBy"))
                line.Children.Add(WriteLine(byId[e.To], e));
            if (t.Note is not null && t.Expanded != false) line.Details.Add(t.Note);
            onPath.Remove(t.Id);
            return line;
        }

        Line WriteLine(TraceNode w, TraceEdge via)
        {
            var label = $"written by {w.Program}/{w.Routine} {w.Location} [{w.Kind}] {w.Text}";
            if (w.Value is not null) label += $"  => {w.Value}";
            var line = new Line { Label = label, NewWrite = true };
            if (via.Note is not null) line.Details.Add(via.Note);
            if (w.Via is not null) line.Details.Add($"through alias {w.Via}");
            if (w.Note is not null) line.Details.Add(w.Note);
            foreach (var c in w.Conditions ?? new List<TraceCondition>())
                line.Details.Add($"when {c.Text}");
            foreach (var s in (w.Sources ?? new List<TraceSource>()).Where(s => s.Literal))
                line.Details.Add($"literal {s.Text}");

            if (!printed.Add(w.Id))
            {
                line.Label += "  (traced above)";
                line.Details.Clear();
                line.NewWrite = false;
                return line;
            }
            outgoing.TryGetValue(w.Id, out var edges);
            foreach (var e in edges ?? new List<TraceEdge>())
                line.Children.Add(TagLine(byId[e.To], e.Kind == "condition" ? "[gate]" : "[src]"));
            return line;
        }

        var lines = new List<OutLine>();
        var head = new StringBuilder();
        head.Append("Origin trace of ").Append(g.Requested);
        if (!string.Equals(g.Requested, g.Resolved, StringComparison.OrdinalIgnoreCase))
            head.Append(" (storage: ").Append(g.Resolved).Append(')');
        head.Append("  maxDepth ").Append(g.MaxDepth);
        if (g.Focus is not null) head.Append("  focus ").Append(g.Focus);
        lines.Add(new OutLine(head.ToString(), 0, "", false, true));

        var root = g.Nodes.First(n => n.Root);
        Emit(lines, TagLine(root, ""), "", true, 0, "", root: true);

        void Trailer(string text) => lines.Add(new OutLine(text, 0, "", false, false));
        if (g.Truncated) { Trailer(""); Trailer("TRUNCATED: " + g.Advice); }
        if (g.ExternalInputs.Count > 0)
        {
            Trailer(""); Trailer("External inputs (nothing in the project writes these):");
            foreach (var e in g.ExternalInputs) Trailer($"  {e.Tag} - {e.Kind}: {e.Detail}");
        }
        if (g.ControllerStatus.Count > 0)
        {
            Trailer(""); Trailer("Controller status (set by the controller, not external inputs):");
            foreach (var e in g.ControllerStatus) Trailer($"  {e.Tag} - {e.Kind}: {e.Detail}");
        }
        if (g.Literals.Count > 0)
        {
            Trailer(""); Trailer("Literals:");
            foreach (var l in g.Literals) Trailer($"  {l.Value} ({l.Role}, {l.Node})");
        }
        if (g.Cycles.Count > 0)
        {
            Trailer(""); Trailer("Cycles (not looped):");
            foreach (var c in g.Cycles) Trailer("  " + string.Join(" -> ", c.Path));
        }
        if (g.DepthLimited.Count > 0)
        {
            Trailer(""); Trailer($"Stopped at maxDepth {g.MaxDepth} (these have writers that were not followed):");
            foreach (var t in g.DepthLimited) Trailer("  " + t);
        }
        if (g.Gaps.Count > 0)
        {
            Trailer(""); Trailer("Gaps (not followed, never guessed):");
            foreach (var gap in g.Gaps)
                Trailer($"  [{gap.Kind}] {(gap.Location is null ? "" : gap.Location + ": ")}{gap.Detail}");
        }

        var total = lines.Sum(l => l.Text.Length + 1);
        if (total - 1 <= maxChars)
            return new TraceText(string.Join("\n", lines.Select(l => l.Text)).TrimEnd(), false, 0, null);

        // Over budget: keep whole lines, leaving room for the notice. The header line is always kept.
        var budget = maxChars - NoticeReserve;
        var used = 0;
        var keep = 0;
        while (keep < lines.Count && (keep == 0 || used + lines[keep].Text.Length + 1 <= budget))
            used += lines[keep++].Text.Length + 1;

        var omitted = lines.Skip(keep).ToList();
        var omittedWrites = omitted.Count(l => l.NewWrite);
        var first = omitted[0];
        var at = first.Tree
            ? $"depth {first.Depth}, branch {ShortBranch(first.Branch)}"
            : "after the trace tree (external inputs, literals, cycles and gaps omitted)";
        var notice = $"... output cut at maxChars {maxChars}: {omittedWrites} write(s) omitted, cut at {at}. " +
                     "Call again with focus (a program name) or a smaller maxDepth; the JSON summary counts the whole trace.";
        var text = string.Join("\n", lines.Take(keep).Select(l => l.Text)).TrimEnd() + "\n" + notice;
        return new TraceText(text, true, omittedWrites, at);
    }

    /// <summary>Room kept for the cut notice, so the cut text plus notice stays within the budget.</summary>
    private const int NoticeReserve = 420;

    private static string ShortBranch(string branch) =>
        branch.Length <= 100 ? branch : "..." + branch[^97..];

    private static void Emit(List<OutLine> lines, Line line, string prefix, bool last, int parentDepth, string parentBranch,
        bool root = false)
    {
        var depth = line.Depth ?? parentDepth;
        var branch = line.Tag is null ? parentBranch : parentBranch.Length == 0 ? line.Tag : parentBranch + " <- " + line.Tag;
        var shown = line.Tag is null && branch.Length > 0 ? branch + " <- (write)" : branch;
        var connector = root ? "" : last ? "└─ " : "├─ ";
        lines.Add(new OutLine(prefix + connector + line.Label, depth, shown, line.NewWrite, true));
        var childPrefix = root ? "" : prefix + (last ? "   " : "│  ");
        var hasChildren = line.Children.Count > 0;
        foreach (var d in line.Details)
            lines.Add(new OutLine(childPrefix + (hasChildren ? "│  " : "   ") + "· " + d, depth, shown, false, true));
        for (var i = 0; i < line.Children.Count; i++)
            Emit(lines, line.Children[i], childPrefix, i == line.Children.Count - 1, depth, branch);
    }

    /// <summary>
    /// Renders a Mermaid flowchart with one box per writing rung (or ST routine line group, MSG, parameter),
    /// flowing from where a value originates to the tag asked about. Solid arrows are data sources,
    /// dashed arrows are gating conditions; external inputs are stadium shapes.
    /// </summary>
    public static string Mermaid(TraceGraph g)
    {
        var byId = g.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var tagWriters = g.Edges.Where(e => e.Kind == "writtenBy")
            .GroupBy(e => e.From).ToDictionary(x => x.Key, x => x.Select(e => e.To).ToList(), StringComparer.Ordinal);
        var external = g.ExternalInputs.ToDictionary(e => e.Node, StringComparer.Ordinal);
        var status = g.ControllerStatus.ToDictionary(e => e.Node, StringComparer.Ordinal);

        // One box per writing location.
        var boxOf = new Dictionary<string, string>(StringComparer.Ordinal);       // write id -> box id
        var boxes = new List<(string Id, string Label)>();
        var groups = g.WriteNodes
            .GroupBy(w => $"{w.Program}\u0001{w.Routine}\u0001{w.Kind}\u0001{w.Location}", StringComparer.Ordinal)
            .ToList();
        var n = 0;
        foreach (var grp in groups)
        {
            var id = $"R{++n}";
            var first = grp.First();
            var rows = new List<string> { $"{first.Program}/{first.Routine} {first.Location}" };
            foreach (var w in grp)
            {
                rows.Add(w.Text + (w.Value is not null ? $" => {w.Value}" : ""));
                foreach (var c in w.Conditions ?? new List<TraceCondition>())
                    if (!rows.Contains("when " + c.Text)) rows.Add("when " + c.Text);
                boxOf[w.Id] = id;
            }
            boxes.Add((id, string.Join("<br/>", rows.Select(Escape))));
        }

        var sb = new StringBuilder();
        sb.AppendLine("flowchart LR");
        foreach (var (id, label) in boxes) sb.AppendLine($"  {id}[\"{label}\"]");

        // Tag boxes: the root, external inputs, and tags that were not expanded.
        var tagBox = new Dictionary<string, string>(StringComparer.Ordinal);
        string TagBox(TraceNode t)
        {
            if (tagBox.TryGetValue(t.Id, out var existing)) return existing;
            var id = "T" + t.Id[1..];
            tagBox[t.Id] = id;
            var label = Escape(t.Tag ?? t.Id);
            if (t.Root) sb.AppendLine($"  {id}((\"{label}\"))");
            else if (external.TryGetValue(t.Id, out var ext))
                sb.AppendLine($"  {id}([\"{label}<br/>external: {Escape(ext.Kind)}\"])");
            else if (status.TryGetValue(t.Id, out var cs))
                sb.AppendLine($"  {id}([\"{label}<br/>{Escape(cs.Kind)}\"])");
            else sb.AppendLine($"  {id}([\"{label}<br/>not followed\"])");
            return id;
        }

        var edges = new HashSet<string>(StringComparer.Ordinal);
        void Edge(string from, string to, string label, bool dashed)
        {
            var line = $"  {from} {(dashed ? "-.->" : "-->")}|\"{Escape(label)}\"| {to}";
            if (edges.Add(line)) sb.AppendLine(line);
        }

        // The root tag (and any alias chain above it) is fed by its writers.
        var root = g.Nodes.First(x => x.Root);
        var rootBox = TagBox(root);
        var canonical = root;
        var seenAlias = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        while (canonical.AliasOf is not null)
        {
            var next = g.Edges.FirstOrDefault(e => e.Kind == "alias" && e.From == canonical.Id);
            if (next is null || !seenAlias.Add(next.To)) break;
            canonical = byId[next.To];
        }
        if (canonical != root)
        {
            var cBox = TagBox(canonical);
            Edge(cBox, rootBox, "alias", false);
            rootBox = cBox;
        }
        foreach (var w in tagWriters.GetValueOrDefault(canonical.Id) ?? new List<string>())
            Edge(boxOf[w], rootBox, canonical.Tag ?? "", false);
        if (!tagWriters.ContainsKey(canonical.Id) && canonical != root) { /* external or limited: already boxed */ }

        // Each write box is fed by the writers of the tags it reads.
        foreach (var e in g.Edges.Where(e => e.Kind is "source" or "condition"))
        {
            if (!boxOf.TryGetValue(e.From, out var toBox)) continue;
            var tag = byId[e.To];
            var dashed = e.Kind == "condition";
            if (tagWriters.TryGetValue(tag.Id, out var ws))
                foreach (var w in ws) Edge(boxOf[w], toBox, tag.Tag ?? "", dashed);
            else
                Edge(TagBox(tag), toBox, tag.Tag ?? "", dashed);
        }

        if (g.Truncated) sb.AppendLine($"  %% truncated at {TraceGraph.MaxNodes} nodes: narrow with focus or a lower maxDepth");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Escapes label text for a double-quoted Mermaid string.</summary>
    private static string Escape(string text) =>
        Regex.Replace(text, @"[""<>&\[\]{}|]", m => $"#{(int)m.Value[0]};")
            .Replace("\r", "").Replace("\n", " ");
}
