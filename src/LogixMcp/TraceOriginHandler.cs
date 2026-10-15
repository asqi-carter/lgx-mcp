using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using L5Sharp.Core;

// ── logix_trace_origin ───────────────────────────────────────────────────────
// Walks backward from a tag to everything that decides its value: the writes (found with the same
// WriteClassifier the write gate uses), the operands they copy from and the conditions that gate them,
// and so on until a literal, a tag nothing writes, maxDepth, or a cycle.

/// <summary>An error from <c>logix_trace_origin</c>.</summary>
/// <param name="Error">What is wrong.</param>
/// <param name="Kind">Machine-readable kind (<see cref="ErrorKinds.InvalidValue"/> for bad input).</param>
/// <param name="Hint">What to do instead.</param>
public record TraceOriginError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null,
    [property: JsonPropertyName("hint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint = null);

/// <summary>
/// The result of <c>logix_trace_origin</c>. The summary fields are always present; the graph arrays
/// are present for <c>json</c> and <c>all</c>, <c>text</c> for <c>text</c> and <c>all</c>, and
/// <c>mermaid</c> for <c>mermaid</c> and <c>all</c>.
/// </summary>
public class TraceOriginResponse
{
    /// <summary>Where the project was read from; always <c>project-file</c>.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "project-file";
    /// <summary>The tag as asked.</summary>
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    /// <summary>The storage the tag resolves to once aliases are followed.</summary>
    [JsonPropertyName("resolved")] public string Resolved { get; set; } = "";
    /// <summary>Applied maxDepth.</summary>
    [JsonPropertyName("maxDepth")] public int MaxDepth { get; set; }
    /// <summary>Applied program filter.</summary>
    [JsonPropertyName("focus"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Focus { get; set; }
    /// <summary><c>true</c> when the 200-node cap stopped the trace or the text was cut to fit <c>maxChars</c>.</summary>
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    /// <summary>Applied text budget in characters.</summary>
    [JsonPropertyName("maxChars")] public int MaxChars { get; set; }
    /// <summary>Where the text was cut for <c>maxChars</c>: the depth and branch of the first omitted line.</summary>
    [JsonPropertyName("truncatedAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TruncatedAt { get; set; }
    /// <summary>Write nodes missing from <c>text</c> because of <c>maxChars</c>.</summary>
    [JsonPropertyName("omittedWrites"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? OmittedWrites { get; set; }
    /// <summary>What to do when the text was cut for <c>maxChars</c>.</summary>
    [JsonPropertyName("hint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Hint { get; set; }
    /// <summary><c>true</c> when the session image is marked stale and this trace may not show the controller's current logic; omitted otherwise.</summary>
    [JsonPropertyName("imageStale"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ImageStale { get; set; }
    /// <summary>UTC time (ISO 8601) the image was marked stale; set with <see cref="ImageStale"/>.</summary>
    [JsonPropertyName("staleSince"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StaleSince { get; set; }
    /// <summary>What the image could not follow; set with <see cref="ImageStale"/>.</summary>
    [JsonPropertyName("staleReason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StaleReason { get; set; }
    /// <summary>How to narrow a truncated trace.</summary>
    [JsonPropertyName("advice"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Advice { get; set; }
    /// <summary>
    /// Pending rungs (staged, pending or tested) that write a tag the trace follows but were left out because
    /// <c>includePending</c> is false; omitted when none.
    /// </summary>
    [JsonPropertyName("pendingWriters"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<PendingWriter>? PendingWriters { get; set; }
    /// <summary>
    /// With <c>includePending: true</c>, the rungs of the trace that are not accepted controller logic, each with its
    /// <c>state</c> (<c>staged</c>, <c>pending</c> or <c>tested</c>); omitted when none are. The write nodes of these
    /// rungs repeat the state in their <c>note</c>.
    /// </summary>
    [JsonPropertyName("pendingIncluded"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<PendingWriter>? PendingIncluded { get; set; }
    /// <summary>Says which pending rungs were left out and how to include them; omitted when none were.</summary>
    [JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Note { get; set; }
    /// <summary>Counts of what the trace found.</summary>
    [JsonPropertyName("summary")] public TraceSummary Summary { get; set; } = new();
    /// <summary>Indented text tree.</summary>
    [JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Text { get; set; }
    /// <summary>Mermaid flowchart source.</summary>
    [JsonPropertyName("mermaid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Mermaid { get; set; }
    /// <summary>Tag and write nodes.</summary>
    [JsonPropertyName("nodes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<TraceNode>? Nodes { get; set; }
    /// <summary>Edges between nodes.</summary>
    [JsonPropertyName("edges"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<TraceEdge>? Edges { get; set; }
    /// <summary>Tags nothing writes.</summary>
    [JsonPropertyName("externalInputs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<TraceExternalInput>? ExternalInputs { get; set; }
    /// <summary>S: status operands, classified <c>controllerStatus</c>: set by the controller, not external inputs.</summary>
    [JsonPropertyName("controllerStatus"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<TraceExternalInput>? ControllerStatus { get; set; }
    /// <summary>Literals the trace stopped at.</summary>
    [JsonPropertyName("literals"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<TraceLiteral>? Literals { get; set; }
    /// <summary>Constructs not followed.</summary>
    [JsonPropertyName("gaps"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<TraceGap>? Gaps { get; set; }
    /// <summary>Cycles found.</summary>
    [JsonPropertyName("cycles"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<TraceCycle>? Cycles { get; set; }
    /// <summary>Tags with writers that were not followed because maxDepth was reached.</summary>
    [JsonPropertyName("depthLimited"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? DepthLimited { get; set; }
}

/// <summary>Counts in a <see cref="TraceOriginResponse"/>.</summary>
public class TraceSummary
{
    /// <summary>Tag nodes.</summary>
    [JsonPropertyName("tags")] public int Tags { get; set; }
    /// <summary>Write nodes.</summary>
    [JsonPropertyName("writes")] public int Writes { get; set; }
    /// <summary>External inputs.</summary>
    [JsonPropertyName("externalInputs")] public int ExternalInputs { get; set; }
    /// <summary>S: status operands.</summary>
    [JsonPropertyName("controllerStatus")] public int ControllerStatus { get; set; }
    /// <summary>Literals.</summary>
    [JsonPropertyName("literals")] public int Literals { get; set; }
    /// <summary>Gaps.</summary>
    [JsonPropertyName("gaps")] public int Gaps { get; set; }
    /// <summary>Cycles.</summary>
    [JsonPropertyName("cycles")] public int Cycles { get; set; }
}

/// <summary>Implements <c>logix_trace_origin</c>.</summary>
public static class TraceOriginHandler
{
    /// <summary>Default for <c>maxDepth</c>.</summary>
    public const int DefaultMaxDepth = 6;

    /// <summary>Largest accepted <c>maxDepth</c>.</summary>
    public const int MaxDepthLimit = 50;

    /// <summary>Default for <c>maxChars</c>, the budget for the <c>text</c> output.</summary>
    public const int DefaultMaxChars = 12000;

    /// <summary>Smallest accepted <c>maxChars</c>.</summary>
    public const int MinMaxChars = 500;

    /// <summary>Largest accepted <c>maxChars</c>.</summary>
    public const int MaxCharsLimit = 1_000_000;

    private static readonly Regex RungNumberRegex = new(@"^rung (\d+)$", RegexOptions.Compiled);

    private static readonly string[] Formats = { "text", "json", "mermaid", "all" };

    /// <summary>Label for an <c>S:</c> status operand (<c>S:FS</c>, <c>S:N</c>, <c>S:Z</c>, <c>S:V</c>, <c>S:C</c>, <c>S:MINOR</c>, ...).</summary>
    public const string ControllerStatusLabel = "controller status flag (set by the controller)";

    /// <summary>Kind given to an <c>S:</c> status operand.</summary>
    public const string ControllerStatusKind = "controllerStatus";

    private static readonly Regex StatusName = new(@"^S:[A-Za-z_]\w*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True for a controller status operand such as <c>S:FS</c> or <c>S:MINOR</c>.</summary>
    /// <param name="name">Tag name without member path.</param>
    internal static bool IsControllerStatus(string name) => StatusName.IsMatch(name);

    private static readonly Regex IoName = new(@"^[A-Za-z_]\w*:(?:\d+:)?[ICOS]$", RegexOptions.Compiled);

    /// <summary>Traces the origin of <paramref name="tag"/> in the opened project.</summary>
    /// <param name="service">The project singleton.</param>
    /// <param name="tag">Tag to trace; a member path (<c>Source9.AirBlow</c>) stays one reference.</param>
    /// <param name="maxDepth">Tag hops to follow (default 6).</param>
    /// <param name="focus">Only follow writes in this program (optional).</param>
    /// <param name="format"><c>text</c> (default), <c>json</c>, <c>mermaid</c> or <c>all</c>.</param>
    /// <param name="maxChars">Budget for the text output in characters (default 12000).</param>
    /// <param name="session">Optional tag session; a stale session image is flagged on the response.</param>
    /// <param name="includePending">
    /// <c>true</c> traces the session's controller image with staged, pending and tested rungs applied; <c>false</c>
    /// traces accepted logic only and lists the rungs left out under <c>pendingWriters</c>. <c>null</c> (default) is
    /// <c>true</c> on an SDK-online session and <c>false</c> otherwise (see <see cref="AnalysisImage.Resolve"/>).
    /// </param>
    /// <returns>A <see cref="TraceOriginResponse"/> or a <see cref="TraceOriginError"/>.</returns>
    public static object Execute(ProjectService service, string tag, int? maxDepth = null, string? focus = null,
        string? format = null, int? maxChars = null, TagSessionService? session = null, bool? includePending = null)
    {
        if (!service.IsLoaded)
        {
            var np = service.NoProjectLoadedError("logix_trace_origin");
            return new TraceOriginError(np.Error) { Kind = np.Kind, Hint = np.Hint };
        }
        var selection = AnalysisImage.Resolve(service, session, includePending);
        var result = Execute(selection.Project ?? service.Project!, tag, maxDepth, focus, format, maxChars);
        if (result is TraceOriginResponse traced)
        {
            traced.Source = selection.Source;
            // Pending rungs left out of the accepted view: report who writes the traced tags, never hide them.
            var excluded = AnalysisImage.PendingWriters(selection, pendingImage =>
            {
                var g = BuildGraph(pendingImage, tag, maxDepth, focus, out _);
                return g is null
                    ? Array.Empty<PendingWriter>()
                    : g.WriteNodes
                        .Select(n => (Node: n, Rung: RungNumberRegex.Match(n.Location ?? "")))
                        .Where(x => x.Rung.Success && x.Node.Program is not null && x.Node.Routine is not null)
                        .Select(x => new PendingWriter(
                            x.Node.Program!, x.Node.Routine!, int.Parse(x.Rung.Groups[1].Value), x.Node.Target ?? tag.Trim()));
            }, session is null ? null : session.ZoneTested);
            if (excluded.Count > 0)
            {
                traced.PendingWriters = excluded;
                var excludedRungs = excluded.Select(w => (w.Program, w.Routine, w.RungIndex)).Distinct().Count();
                traced.Note = $"{AnalysisImage.DescribePending(excluded)} also {(excludedRungs == 1 ? "writes" : "write")} a tag this trace follows and " +
                              $"{(excluded.Select(w => (w.Program, w.Routine, w.RungIndex)).Distinct().Count() == 1 ? "was" : "were")} " +
                              "excluded. Pass includePending:true to include it.";
            }
            // includePending: true reads the image with pending rungs applied: label the write nodes whose rung is not accepted.
            var included = CrossReferenceHandler.PendingRungStates(selection, session);
            if (included.Count > 0 && BuildGraph(selection.Project ?? service.Project!, tag, maxDepth, focus, out _) is { } includedGraph)
            {
                var labelled = new List<PendingWriter>();
                var jsonNodes = traced.Nodes?.ToDictionary(x => x.Id, StringComparer.Ordinal);
                foreach (var n in includedGraph.WriteNodes.Where(n => n.Program is not null && n.Routine is not null))
                {
                    var m = RungNumberRegex.Match(n.Location ?? "");
                    if (!m.Success) continue;
                    var rung = int.Parse(m.Groups[1].Value);
                    if (!included.TryGetValue(CrossReferenceHandler.StateKey(n.Program!, n.Routine!, rung), out var st)) continue;
                    var stateText = st switch
                    {
                        AnalysisImage.StateStaged => "staged (not sent to the controller)",
                        AnalysisImage.StateTested => "tested (executing)",
                        _                         => "pending",
                    };
                    if (jsonNodes is not null && jsonNodes.TryGetValue(n.Id, out var shown))
                        shown.Note = shown.Note is null ? $"This rung is {stateText}." : $"{shown.Note} This rung is {stateText}.";
                    labelled.Add(new PendingWriter(n.Program!, n.Routine!, rung, n.Target ?? tag.Trim(), st));
                }
                if (labelled.Count > 0)
                {
                    traced.PendingIncluded = labelled;
                    var includedNote = CrossReferenceHandler.IncludedPendingNote(labelled);
                    traced.Note = traced.Note is null ? includedNote : $"{traced.Note} {includedNote}";
                }
            }
            if (selection.ImageNote is not null)
                traced.Note = traced.Note is null ? selection.ImageNote : $"{traced.Note} {selection.ImageNote}";
        }
        // When the session image is stale it may not show the controller's logic.
        if (result is TraceOriginResponse response
            && ProjectFileStaleness.For(session, response.Source, service) is { } stale)
        {
            response.ImageStale  = true;
            response.StaleSince  = stale.StaleSince;
            response.StaleReason = stale.StaleReason;
            // "hint" is shared with the maxChars truncation hint: keep both.
            response.Hint = response.Hint is null ? stale.Hint : $"{response.Hint} Also: {stale.Hint}.";
        }
        return result;
    }

    /// <summary>Traces the origin of <paramref name="tag"/> in <paramref name="project"/>.</summary>
    /// <param name="project">Parsed project.</param>
    /// <param name="tag">Tag to trace.</param>
    /// <param name="maxDepth">Tag hops to follow (default 6).</param>
    /// <param name="focus">Only follow writes in this program (optional).</param>
    /// <param name="format"><c>text</c> (default), <c>json</c>, <c>mermaid</c> or <c>all</c>.</param>
    /// <param name="maxChars">Budget for the text output in characters (default 12000); the text is cut at whole lines.</param>
    public static object Execute(L5X project, string tag, int? maxDepth = null, string? focus = null,
        string? format = null, int? maxChars = null)
    {
        var budget = maxChars ?? DefaultMaxChars;
        if (budget < MinMaxChars || budget > MaxCharsLimit)
            return new TraceOriginError($"maxChars must be between {MinMaxChars} and {MaxCharsLimit} (got {budget}).",
                ErrorKinds.InvalidValue, $"Leave it out for the default of {DefaultMaxChars}.");

        var fmt = string.IsNullOrWhiteSpace(format) ? "text" : format.Trim().ToLowerInvariant();
        if (!Formats.Contains(fmt))
            return new TraceOriginError($"Unknown format '{format}'.", ErrorKinds.InvalidValue,
                "Use format text, json, mermaid or all.");

        var graph = BuildGraph(project, tag, maxDepth, focus, out var error);
        if (graph is null) return error!;

        var r = new TraceOriginResponse
        {
            Tag = graph.Requested,
            Resolved = graph.Resolved,
            MaxDepth = graph.MaxDepth,
            MaxChars = budget,
            Focus = graph.Focus,
            Truncated = graph.Truncated,
            Advice = graph.Truncated ? graph.Advice : null,
            Summary = new TraceSummary
            {
                Tags = graph.TagNodes.Count(),
                Writes = graph.WriteNodes.Count(),
                ExternalInputs = graph.ExternalInputs.Count,
                ControllerStatus = graph.ControllerStatus.Count,
                Literals = graph.Literals.Count,
                Gaps = graph.Gaps.Count,
                Cycles = graph.Cycles.Count,
            },
        };
        if (fmt is "text" or "all")
        {
            var t = TraceRenderer.Text(graph, budget);
            r.Text = t.Text;
            if (t.Truncated)
            {
                r.Truncated = true;
                r.TruncatedAt = t.TruncatedAt;
                r.OmittedWrites = t.OmittedWrites;
                r.Hint = $"The text was cut at maxChars {budget}. Call again with focus (a program name) or a smaller " +
                         $"maxDepth (now {graph.MaxDepth}) to see the rest; the summary above covers the whole trace.";
            }
        }
        if (fmt is "mermaid" or "all") r.Mermaid = TraceRenderer.Mermaid(graph);
        if (fmt is "json" or "all")
        {
            r.Nodes = graph.Nodes;
            r.Edges = graph.Edges;
            r.ExternalInputs = graph.ExternalInputs;
            r.ControllerStatus = graph.ControllerStatus;
            r.Literals = graph.Literals;
            r.Gaps = graph.Gaps;
            r.Cycles = graph.Cycles;
            r.DepthLimited = graph.DepthLimited;
        }
        return r;
    }

    /// <summary>Builds the trace graph, or returns <c>null</c> with the error.</summary>
    /// <param name="project">Parsed project.</param>
    /// <param name="tag">Tag to trace.</param>
    /// <param name="maxDepth">Tag hops to follow (default 6).</param>
    /// <param name="focus">Only follow writes in this program (optional).</param>
    /// <param name="error">Why the trace could not start.</param>
    public static TraceGraph? BuildGraph(L5X project, string tag, int? maxDepth, string? focus,
        out TraceOriginError? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(tag))
        {
            error = new TraceOriginError("tag is required.", ErrorKinds.InvalidValue,
                "Pass a tag name such as 'Motor_Run', a member path such as 'Source9.AirBlow', or 'Program:Main.Tag'.");
            return null;
        }
        var depth = maxDepth ?? DefaultMaxDepth;
        if (depth < 1 || depth > MaxDepthLimit)
        {
            error = new TraceOriginError($"maxDepth must be between 1 and {MaxDepthLimit} (got {depth}).",
                ErrorKinds.InvalidValue, $"Leave it out for the default of {DefaultMaxDepth}.");
            return null;
        }

        var index = TraceIndex.Build(project);
        string? focusName = null;
        if (!string.IsNullOrWhiteSpace(focus))
        {
            focusName = index.Programs.FirstOrDefault(p => string.Equals(p, focus.Trim(), StringComparison.OrdinalIgnoreCase));
            if (focusName is null)
            {
                error = new TraceOriginError($"focus '{focus}' is not a program in this project.",
                    ErrorKinds.ProgramNotFound, "Programs: " + string.Join(", ", index.Programs.Take(30)) +
                                             (index.Programs.Count > 30 ? ", …" : ""));
                return null;
            }
        }

        // Accept 'Line_A.Tag' the way logix_xref does: a plain 'Program.Tag' names a program-scoped tag.
        var requested = tag.Trim();
        var (qualifiedProgram, qualifiedTag) = CrossReferenceHandler.ResolveTarget(project, requested);
        var lookup = qualifiedProgram is not null
                     && !requested.StartsWith("Program:", StringComparison.OrdinalIgnoreCase)
                     && !requested.StartsWith('\\')
            ? $"Program:{qualifiedProgram}.{qualifiedTag}"
            : requested;

        var root = ResolveRoot(index, lookup, out var rootError);
        if (root is null)
        {
            error = rootError;
            return null;
        }
        return new Builder(index, depth, focusName).Run(requested, root);
    }

    private static TagRef? ResolveRoot(TraceIndex index, string tag, out TraceOriginError? error)
    {
        error = null;
        var r = index.Resolve("", tag);
        if (r is null)
        {
            error = new TraceOriginError($"'{tag}' is not a tag reference.", ErrorKinds.InvalidValue,
                "Pass a tag name such as 'Motor_Run', a member path such as 'Source9.AirBlow', or 'Program:Main.Tag'.");
            return null;
        }
        if (r.Decl is not null || r.Chain.Count > 0) return r;

        // Not a controller tag: a bare name may be a program tag.
        if (!tag.StartsWith("Program:", StringComparison.OrdinalIgnoreCase) && !tag.StartsWith('\\') &&
            TraceIndex.TryParseRefText(tag, out _, out var name, out _))
        {
            var matches = index.DeclsNamed(name).Where(d => d.Scope.Length > 0).ToList();
            if (matches.Count == 1) return index.Resolve(matches[0].Scope, tag);
            if (matches.Count > 1)
            {
                error = new TraceOriginError(
                    $"'{name}' is a program tag in more than one program: {string.Join(", ", matches.Select(m => m.Scope))}.",
                    ErrorKinds.InvalidValue, $"Name the program: 'Program:{matches[0].Scope}.{name}'.");
                return null;
            }
        }

        // An I/O tag (Local:1:I.Data) is not declared in the tag list; anything else that is undeclared is a typo.
        if (IsControllerStatus(r.Name) || IoName.IsMatch(r.Name)) return r;
        error = new TraceOriginError($"Tag '{tag}' is not declared in this project.", ErrorKinds.InvalidValue,
            "Check the spelling, or use 'Program:<program>.<tag>' for a program-scoped tag.");
        return null;
    }

    // ── The walk ─────────────────────────────────────────────────────────────

    private sealed class Builder
    {
        private readonly TraceIndex _ix;
        private readonly TraceGraph _g = new();
        private readonly int _maxDepth;
        private readonly string? _focus;
        private readonly Dictionary<string, TraceNode> _tagNodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TagRef> _tagRefs = new(StringComparer.Ordinal);
        private readonly Dictionary<int, TraceNode> _writeNodes = new();
        private readonly Queue<TraceNode> _queue = new();
        private readonly HashSet<string> _edgeKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _gapKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _literalKeys = new(StringComparer.Ordinal);
        private int _tagSeq;
        private int _writeSeq;

        public Builder(TraceIndex index, int maxDepth, string? focus)
        {
            _ix = index;
            _maxDepth = maxDepth;
            _focus = focus;
            _g.MaxDepth = maxDepth;
            _g.Focus = focus;
        }

        public TraceGraph Run(string requested, TagRef root)
        {
            _g.Requested = requested;
            _g.Resolved = root.Display;
            AddRoot(root);

            while (_queue.Count > 0)
            {
                var node = _queue.Dequeue();
                if (_g.Truncated)
                {
                    node.Expanded = false;
                    node.Note ??= $"not expanded: the {TraceGraph.MaxNodes}-node cap was reached";
                    continue;
                }
                Expand(_tagRefs[node.Id], node);
            }

            FindCycles();
            AddGlobalGaps();
            return _g;
        }

        // ── Nodes ──

        private void AddRoot(TagRef root)
        {
            TraceNode? prev = null;
            foreach (var hop in root.Chain)
            {
                var alias = new TraceNode
                {
                    Id = $"t{++_tagSeq}",
                    Type = "tag",
                    Tag = hop.Display,
                    AliasOf = hop.AliasFor,
                    Depth = 0,
                    Root = prev is null,
                };
                _g.Nodes.Add(alias);
                if (prev is not null) AddEdge(prev.Id, alias.Id, "alias");
                prev = alias;
            }

            var storage = NewTag(root, 0);
            storage.Root = prev is null;
            if (prev is not null) AddEdge(prev.Id, storage.Id, "alias");
        }

        private TraceNode NewTag(TagRef r, int depth)
        {
            var node = new TraceNode
            {
                Id = $"t{++_tagSeq}",
                Type = "tag",
                Tag = r.Display,
                DataType = r.Decl?.DataType,
                Depth = depth,
            };
            _g.Nodes.Add(node);
            _tagNodes[r.NodeKey] = node;
            _tagRefs[node.Id] = r;
            _queue.Enqueue(node);
            return node;
        }

        /// <summary>The node for a tag, created when new; <c>null</c> when the node cap is reached.</summary>
        private TraceNode? TagNode(TagRef r, int depth)
        {
            if (_tagNodes.TryGetValue(r.NodeKey, out var existing)) return existing;
            if (_g.Nodes.Count >= TraceGraph.MaxNodes)
            {
                _g.Truncated = true;
                return null;
            }
            return NewTag(r, depth);
        }

        private void AddEdge(string from, string to, string kind, string? via = null, string? note = null)
        {
            if (!_edgeKeys.Add($"{from}>{to}>{kind}")) return;
            _g.Edges.Add(new TraceEdge(from, to, kind) { Via = via, Note = note });
        }

        private void AddGap(string kind, string? location, string? tag, string detail)
        {
            if (_gapKeys.Add($"{kind}|{location}|{tag}|{detail}")) _g.Gaps.Add(new TraceGap(kind, location, tag, detail));
        }

        // ── Expansion ──

        private void Expand(TagRef r, TraceNode node)
        {
            if (r.Problem is not null)
            {
                node.Expanded = false;
                node.Note = r.Problem;
                AddGap("UnresolvedAlias", null, node.Tag, r.Problem);
                return;
            }
            if (r.HasVariableIndex)
                AddGap("IndirectAddress", null, node.Tag,
                    $"{node.Tag} has an index that is a variable, so which element is meant is decided at run time. " +
                    "The trace follows the writers of every element and does not guess which one.");

            var writers = _ix.FindWriters(r, _focus, out var outside);
            foreach (var u in _ix.FindUnknown(r))
            {
                if (_focus is not null && !string.Equals(u.Program, _focus, StringComparison.OrdinalIgnoreCase)) continue;
                AddGap("UnclassifiedInstruction", $"{u.Program}/{u.Routine} {u.Location}", node.Tag,
                    $"{u.Instruction} takes {node.Tag} but its write semantics are unknown, so it may write it: {u.Text}");
            }
            if (outside > 0)
                AddGap("OutsideFocus", null, node.Tag,
                    $"{outside} write(s) to {node.Tag} in other programs were not followed because focus is '{_focus}'.");

            if (writers.Count == 0)
            {
                if (outside > 0)
                {
                    node.Expanded = true;
                    node.Note = $"no writer in program '{_focus}'; {outside} elsewhere";
                    return;
                }
                MarkExternal(r, node);
                return;
            }
            if (node.Depth >= _maxDepth)
            {
                node.Expanded = false;
                node.Note = $"{writers.Count} writer(s) not followed: maxDepth {_maxDepth} reached";
                _g.DepthLimited.Add(node.Tag!);
                return;
            }

            node.Expanded = true;
            foreach (var hit in writers)
                if (!AddWrite(node, hit)) break;
        }

        private void MarkExternal(TagRef r, TraceNode node)
        {
            node.Expanded = true;
            var d = r.Decl;
            string kind, detail;
            if (d is null && IsControllerStatus(r.Name))
            {
                // Set by the controller itself, not by logic and not by the outside world: not an external input.
                node.Note = null;
                _g.ControllerStatus.Add(new TraceExternalInput(node.Tag!, ControllerStatusKind, ControllerStatusLabel, node.Id));
                return;
            }
            if (d is { IsConstant: true })
            {
                kind = "constant";
                detail = "declared Constant: its value is set in the tag definition, never by logic.";
            }
            else if (d is { Usage: "Input" })
            {
                kind = "parameter";
                detail = "an Input parameter: its value comes from whatever the caller connects to it.";
            }
            else if (d is { TagType: "Consumed" })
            {
                kind = "consumed";
                detail = "a consumed tag: its value is produced by another controller.";
            }
            else if (IoName.IsMatch(r.Name) || (d?.DataType?.StartsWith("AB:", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                kind = "io";
                detail = "module I/O: the value comes from the field device or module, not from program logic.";
            }
            else
            {
                kind = "unwritten";
                detail = d is null
                    ? "not declared and nothing in the project writes it."
                    : "no instruction, parameter connection or MSG in the project writes it; its value is set by an operator, an HMI or an external system.";
            }
            if (_ix.FindUnknown(r).Count > 0)
                detail += " An instruction the trace cannot classify also takes it (see gaps).";
            node.Note = null;
            _g.ExternalInputs.Add(new TraceExternalInput(node.Tag!, kind, detail, node.Id));
        }

        // ── Writes ──

        /// <summary>Adds a write node under <paramref name="tagNode"/>; <c>false</c> when the node cap stopped the trace.</summary>
        private bool AddWrite(TraceNode tagNode, WriterHit hit)
        {
            var site = hit.Site;
            var note = hit.NameOnly
                ? "Matched by tag name only: this writes a tag of the same name in another scope, and the write gate treats that as a write to this tag too."
                : null;

            if (_writeNodes.TryGetValue(site.Id, out var existing))
            {
                AddEdge(tagNode.Id, existing.Id, "writtenBy", note: note);
                return true;
            }
            if (_g.Nodes.Count >= TraceGraph.MaxNodes)
            {
                _g.Truncated = true;
                return false;
            }

            var w = new TraceNode
            {
                Id = $"w{++_writeSeq}",
                Type = "write",
                Program = site.Program ?? "(controller)",
                Routine = site.Routine,
                Location = site.Location,
                Kind = site.Kind,
                Instruction = site.Instruction,
                Text = site.Text,
                Target = TraceIndex.Collapse(site.TargetText),
                Via = site.Target.Chain.Count > 0 ? site.Target.Chain[0].Display : null,
                Value = site.Value,
                ValueKind = site.ValueKind,
                Note = site.Note,
            };
            _g.Nodes.Add(w);
            _writeNodes[site.Id] = w;
            AddEdge(tagNode.Id, w.Id, "writtenBy", note: note);

            if (site.ValueKind == "implicit" && site.Value is not null) AddLiteral(w.Id, site.Value, "implicit");
            var where = $"{w.Program}/{w.Routine} {w.Location}";
            if (site.GapKind is not null)
                AddGap(site.GapKind, where, tagNode.Tag, site.GapDetail ?? "");
            if (site.Target.HasVariableIndex)
                AddGap("IndirectAddress", where, site.Target.Display,
                    $"{w.Text} writes an element chosen at run time (variable index), so it is shown as a writer of every element.");

            var conditions = site.Conditions.ToList();
            if (site.Kind == "MSG" && _ix.TriggerOf(site) is { } trigger)
            {
                conditions.AddRange(trigger.Conditions);
                w.Note += $" Started by {trigger.Program}/{trigger.Routine} {trigger.Location}.";
                w.Note = w.Note.Trim();
            }

            var depth = (tagNode.Depth ?? 0) + 1;
            var sources = new List<TraceSource>();
            var tags = new List<(TagRef Ref, string Kind, string? Via)>();
            foreach (var op in site.SourceOperands) Operand(site.Scope, op, "source", sources, tags, w, where);

            var traceConditions = new List<TraceCondition>();
            foreach (var c in conditions)
            {
                var condSources = new List<TraceSource>();
                var found = new List<TagRef>();
                foreach (var op in c.Operands) found.AddRange(Operand(site.Scope, op, "condition", condSources, tags, w, where));
                traceConditions.Add(new TraceCondition(c.Kind, c.Text, found.Select(t => t.Display).Distinct().ToList()));
            }
            w.Sources = sources.Count > 0 ? sources : null;
            w.Conditions = traceConditions.Count > 0 ? traceConditions : null;

            foreach (var (r, kind, via) in tags)
            {
                var target = TagNode(r, depth);
                if (target is null)
                {
                    w.Note = ((w.Note ?? "") + " Some operands were not followed: node cap reached.").Trim();
                    continue;
                }
                if (r.Chain.Count > 0)
                {
                    target.Aliases ??= new List<string>();
                    foreach (var hop in r.Chain)
                        if (!target.Aliases.Contains(hop.Display)) target.Aliases.Add(hop.Display);
                }
                AddEdge(w.Id, target.Id, kind, via);
            }
            return !_g.Truncated;
        }

        /// <summary>
        /// Splits one operand into literals and tag references, adding to <paramref name="sources"/> (source operands
        /// only, conditions are recorded as text) and <paramref name="tags"/>.
        /// </summary>
        private List<TagRef> Operand(string scope, string text, string role, List<TraceSource> sources,
            List<(TagRef Ref, string Kind, string? Via)> tags, TraceNode w, string where)
        {
            var found = new List<TagRef>();
            var clean = TraceIndex.Collapse(text);
            if (clean.Length == 0 || clean == "?") return found;

            void Ref(TagRef r, string shown, string? note)
            {
                found.Add(r);
                if (role == "source") sources.Add(new TraceSource(shown, r.Display, false, note));
                if (!tags.Any(t => t.Ref.NodeKey == r.NodeKey && t.Kind == role))
                    tags.Add((r, role, r.Chain.Count > 0 ? r.Chain[0].Display : null));
                if (r.HasVariableIndex)
                    AddGap("IndirectAddress", where, r.Display,
                        $"{r.Display} has an index that is a variable: which element is read is decided at run time, " +
                        "so the trace follows the writers of every element.");
            }

            if (TraceIndex.IsLiteral(clean))
            {
                if (role == "source") sources.Add(new TraceSource(clean, null, true));
                AddLiteral(w.Id, clean, role);
                return found;
            }
            if (!HasTopLevelSpace(clean) && _ix.Resolve(scope, clean) is { } single)
            {
                Ref(single, clean, null);
                return found;
            }

            var refs = TraceIndex.ExpressionRefs(clean);
            var any = false;
            foreach (var rt in refs)
            {
                if (_ix.Resolve(scope, rt) is not { } r) continue;
                any = true;
                Ref(r, rt, clean == rt ? null : $"in expression: {clean}");
            }
            if (!any && role == "source") sources.Add(new TraceSource(clean, null, true));
            return found;
        }

        /// <summary><c>true</c> when the text has whitespace outside brackets, so it is an expression, not one reference.</summary>
        private static bool HasTopLevelSpace(string text)
        {
            var depth = 0;
            foreach (var c in text)
            {
                if (c == '[') depth++;
                else if (c == ']') depth--;
                else if (depth == 0 && char.IsWhiteSpace(c)) return true;
            }
            return false;
        }

        private void AddLiteral(string node, string value, string role)
        {
            if (_literalKeys.Add($"{node}|{value}|{role}")) _g.Literals.Add(new TraceLiteral(node, value, role));
        }

        // ── Cycles and global gaps ──

        /// <summary>Marks every edge that returns to a tag already on its path, and records the loop.</summary>
        private void FindCycles()
        {
            var byId = _g.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            var outgoing = _g.Edges.Where(e => e.Kind != "alias")
                .GroupBy(e => e.From).ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
            var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = on path, 2 = done
            var path = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Visit(string id)
            {
                state[id] = 1;
                path.Add(id);
                foreach (var e in outgoing.GetValueOrDefault(id) ?? new List<TraceEdge>())
                {
                    var st = state.GetValueOrDefault(e.To);
                    if (st == 1)
                    {
                        e.Cycle = true;
                        var from = path.IndexOf(e.To);
                        var names = path.Skip(from).Where(p => byId[p].Type == "tag").Select(p => byId[p].Tag!).ToList();
                        if (byId[e.To].Type == "tag") names.Add(byId[e.To].Tag!);
                        if (names.Count > 0 && seen.Add(string.Join(">", names)))
                            _g.Cycles.Add(new TraceCycle(names));
                    }
                    else if (st == 0) Visit(e.To);
                }
                path.RemoveAt(path.Count - 1);
                state[id] = 2;
            }

            var root = _g.Nodes.First(n => n.Root);
            // Start from the storage node under the alias chain, if any.
            var start = root.Id;
            var aliasEdge = _g.Edges.Where(e => e.Kind == "alias").ToDictionary(e => e.From, e => e.To, StringComparer.Ordinal);
            for (var guard = 0; aliasEdge.TryGetValue(start, out var next) && guard < 64; guard++) start = next;
            Visit(start);
        }

        /// <summary>Gaps that are not about one tag: routines and rungs the trace could not read.</summary>
        private void AddGlobalGaps()
        {
            foreach (var u in _ix.Unreadable)
            {
                if (_focus is not null && !string.Equals(u.Program, _focus, StringComparison.OrdinalIgnoreCase)) continue;
                AddGap("UnreadableRoutine", $"{u.Program}/{u.Routine}", null,
                    "The routine is encoded or protected, so its logic could not be read. It may write traced tags.");
            }

            var names = _g.TagNodes.Select(n => _tagRefs.GetValueOrDefault(n.Id)?.Name).Where(n => n is not null).Distinct().ToList();
            foreach (var r in _ix.Unparsed)
            {
                if (_focus is not null && !string.Equals(r.Program, _focus, StringComparison.OrdinalIgnoreCase)) continue;
                var hit = names.FirstOrDefault(n => Regex.IsMatch(r.Text, $@"(?<![\w.]){Regex.Escape(n!)}(?!\w)", RegexOptions.IgnoreCase));
                if (hit is null) continue;
                AddGap("UnparsedRung", $"{r.Program}/{r.Routine} rung {r.Rung}", hit,
                    $"The rung text could not be parsed, and it mentions {hit}: {TraceIndex.Collapse(r.Text)}");
            }
        }
    }
}
