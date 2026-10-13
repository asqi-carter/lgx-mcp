using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using L5Sharp.Core;

// ── Project index for the origin trace ───────────────────────────────────────
// One pass over the project turns every place that writes a tag into a WriteSite. Which operands
// are writes is never decided here: RLL, ST, JSR and AOI operands all go through WriteClassifier,
// so the trace and the write gate agree on what writes a tag by construction.

/// <summary>One step of a tag path below its base name: a member (<c>.AirBlow</c>) or an index (<c>[3]</c>).</summary>
/// <param name="Text">Member name, or the index text with whitespace removed.</param>
/// <param name="IsIndex"><c>true</c> for <c>[index]</c>.</param>
/// <param name="Literal"><c>true</c> for an index that is a plain number; any other index is a variable.</param>
internal readonly record struct PathTok(string Text, bool IsIndex, bool Literal);

/// <summary>A declared tag (controller or program scope).</summary>
internal sealed class TagDecl
{
    /// <summary>Owning program name; empty for controller scope.</summary>
    public string Scope { get; init; } = "";
    /// <summary>Tag name as declared.</summary>
    public string Name { get; init; } = "";
    /// <summary>The <c>&lt;Tag&gt;</c> element.</summary>
    public XElement Element { get; init; } = null!;

    /// <summary>Lookup key: scope and name, lower case.</summary>
    public string Key => TraceIndex.StorageKey(Scope, Name);
    /// <summary><c>TagType</c> attribute (Base, Alias, Produced, Consumed).</summary>
    public string TagType => Element.Attribute("TagType")?.Value ?? "Base";
    /// <summary><c>true</c> for an alias tag.</summary>
    public bool IsAlias => string.Equals(TagType, "Alias", StringComparison.OrdinalIgnoreCase);
    /// <summary>The <c>AliasFor</c> target text.</summary>
    public string AliasFor => (Element.Attribute("AliasFor")?.Value ?? "").Trim();
    /// <summary>Program-parameter usage (Input, Output, InOut, Public), or empty.</summary>
    public string Usage => Element.Attribute("Usage")?.Value ?? "";
    /// <summary>Declared data type.</summary>
    public string? DataType => Element.Attribute("DataType")?.Value;
    /// <summary><c>true</c> when declared a constant.</summary>
    public bool IsConstant => string.Equals(Element.Attribute("Constant")?.Value, "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One alias followed on the way to a tag's storage.</summary>
/// <param name="Name">The alias tag name.</param>
/// <param name="Display">The alias as reported: <c>Name</c> or <c>Program:P.Name</c>.</param>
/// <param name="AliasFor">The alias's <c>AliasFor</c> text.</param>
internal sealed record AliasHop(string Name, string Display, string AliasFor);

/// <summary>A tag reference resolved to storage: aliases followed, scope decided, member path kept.</summary>
internal sealed class TagRef
{
    /// <summary>Owning program of the storage tag; empty for controller scope.</summary>
    public string Scope { get; set; } = "";
    /// <summary>Storage tag name (not an alias).</summary>
    public string Name { get; set; } = "";
    /// <summary>Member and index path below the storage tag.</summary>
    public List<PathTok> Path { get; set; } = new();
    /// <summary>The storage tag's declaration, or <c>null</c> when it is not declared.</summary>
    public TagDecl? Decl { get; set; }
    /// <summary>Aliases followed, outermost first.</summary>
    public List<AliasHop> Chain { get; } = new();
    /// <summary>Why alias resolution stopped early, or <c>null</c>.</summary>
    public string? Problem { get; set; }

    /// <summary>Storage key (scope and name).</summary>
    public string Key => TraceIndex.StorageKey(Scope, Name);
    /// <summary>Node identity: storage key plus path, with every variable index folded to <c>[*]</c>.</summary>
    public string NodeKey => Key + "|" + string.Concat(Path.Select(t =>
        t.IsIndex ? (t.Literal ? $"[{t.Text.TrimStart('0').PadLeft(1, '0')}]" : "[*]") : "." + t.Text.ToLowerInvariant()));
    /// <summary><c>true</c> when the path has a variable index.</summary>
    public bool HasVariableIndex => Path.Any(t => t.IsIndex && !t.Literal);

    /// <summary>The reference as reported: <c>Tag.Member[2]</c> or <c>Program:P.Tag.Member</c>.</summary>
    public string Display =>
        (Scope.Length == 0 ? Name : $"Program:{Scope}.{Name}") +
        string.Concat(Path.Select(t => t.IsIndex ? $"[{t.Text}]" : "." + t.Text));
}

/// <summary>A condition as read from the project; operands are resolved when the trace uses it.</summary>
/// <param name="Kind"><c>contact</c>, <c>compare</c>, <c>one-shot</c>, <c>branch</c>, <c>if</c>, <c>case</c> or <c>loop</c>.</param>
/// <param name="Text">The condition as written.</param>
/// <param name="Operands">Operand texts the condition tests (tags, literals or expressions).</param>
internal sealed record RawCond(string Kind, string Text, IReadOnlyList<string> Operands);

/// <summary>Labels for <see cref="WriteSite.Kind"/> and the gap a write raises (not error kinds).</summary>
internal static class SiteKinds
{
    /// <summary>Function block diagram output.</summary>
    public const string Fbd = "FBD";
    /// <summary>MSG destination.</summary>
    public const string Msg = "MSG";
    /// <summary>Output or in-out program parameter declaration.</summary>
    public const string Parameter = "Parameter";
    /// <summary>Parameter connection.</summary>
    public const string Connection = "Connection";
    /// <summary>JSR parameter passing that the trace does not follow.</summary>
    public const string JsrGap = "JsrParameter";
}

/// <summary>One place where program logic (or a declaration) writes a tag.</summary>
internal sealed class WriteSite
{
    /// <summary>Sequence number, unique in the index.</summary>
    public int Id { get; set; }
    /// <summary>Program holding the write, or <c>null</c> for controller-level evidence (connections, controller MSG tags).</summary>
    public string? Program { get; set; }
    /// <summary>Routine, or the owning construct for MSG, parameters and connections.</summary>
    public string Routine { get; set; } = "";
    /// <summary>Where in the routine: <c>rung 3</c>, <c>ST line 12</c>, …</summary>
    public string Location { get; set; } = "";
    /// <summary><c>RLL</c>, <c>ST</c>, <c>FBD</c>, <c>SFC</c>, <c>MSG</c>, <c>Parameter</c> or <c>Connection</c>.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Instruction mnemonic.</summary>
    public string Instruction { get; set; } = "";
    /// <summary>The instruction or statement as written.</summary>
    public string Text { get; set; } = "";
    /// <summary>Scope operand texts resolve in (program name, or empty).</summary>
    public string Scope { get; set; } = "";
    /// <summary>The written operand as written.</summary>
    public string TargetText { get; set; } = "";
    /// <summary>The written operand resolved to storage.</summary>
    public TagRef Target { get; set; } = null!;
    /// <summary><c>true</c> when the operand named its program (<c>\Prog.Tag</c>).</summary>
    public bool Scoped { get; set; }
    /// <summary>Operands the written value is computed from.</summary>
    public List<string> SourceOperands { get; } = new();
    /// <summary>Gating conditions.</summary>
    public List<RawCond> Conditions { get; } = new();
    /// <summary>Value written (literal or implicit).</summary>
    public string? Value { get; set; }
    /// <summary><c>literal</c> or <c>implicit</c>.</summary>
    public string? ValueKind { get; set; }
    /// <summary>1 for logic, connections and MSG; 2 for a parameter declaration (used only when nothing else writes the tag).</summary>
    public int Tier { get; set; } = 1;
    /// <summary>Gap raised whenever this write is used (e.g. <c>JsrParameter</c>), or <c>null</c>.</summary>
    public string? GapKind { get; set; }
    /// <summary>Detail of <see cref="GapKind"/>.</summary>
    public string? GapDetail { get; set; }
    /// <summary>Caveat shown on the node.</summary>
    public string? Note { get; set; }
}

/// <summary>An instruction or ST call that takes tags but whose write semantics the classifier does not know.</summary>
/// <param name="Program">Program.</param>
/// <param name="Routine">Routine.</param>
/// <param name="Location">Rung or ST line.</param>
/// <param name="Instruction">Mnemonic.</param>
/// <param name="Text">As written.</param>
/// <param name="Scope">Resolution scope.</param>
/// <param name="Operands">Operand texts that might be written.</param>
internal sealed record UnknownSite(
    string Program, string Routine, string Location, string Instruction, string Text, string Scope,
    IReadOnlyList<string> Operands);

/// <summary>A routine the trace could not read (encoded or protected).</summary>
/// <param name="Program">Owning program.</param>
/// <param name="Routine">Routine name.</param>
internal sealed record UnreadableRoutine(string Program, string Routine);

/// <summary>A rung whose text the rung parser could not read.</summary>
/// <param name="Program">Program.</param>
/// <param name="Routine">Routine.</param>
/// <param name="Rung">Rung number.</param>
/// <param name="Text">Rung text.</param>
internal sealed record UnparsedRung(string Program, string Routine, string Rung, string Text);

/// <summary>A hit from <see cref="TraceIndex.FindWriters"/>.</summary>
/// <param name="Site">The write.</param>
/// <param name="NameOnly"><c>true</c> when matched by tag name across scopes, as the gate does.</param>
internal sealed record WriterHit(WriteSite Site, bool NameOnly);

/// <summary>
/// Every write in a project, indexed by the storage tag it reaches. Built once per project;
/// <see cref="TraceOriginHandler"/> queries it.
/// </summary>
internal sealed class TraceIndex
{
    private static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex NumberLiteral = new(
        @"^[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?$|^\d+#[0-9A-Fa-f_]+$", RegexOptions.Compiled);

    private static readonly Regex ExprRef = new(
        @"(?<![\w.$:#\\])(?:\\|Program:)?[A-Za-z_][\w:]*(?:\[(?:[^\[\]]|\[[^\[\]]*\])*\]|\.\w+)*(?!\w)(?!\s*\()",
        RegexOptions.Compiled);

    private static readonly Regex ForHeaderEnd = new(@"(?<![\w.$])(?:TO|DO)(?![\w])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StBlockKeyword = new(
        @"(?<![\w.$:])(END_IF|END_CASE|END_FOR|END_WHILE|END_REPEAT|IF|ELSIF|ELSE|THEN|CASE|OF|FOR|DO|WHILE|REPEAT|UNTIL)(?![\w])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> ExprWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR", "XOR", "NOT", "MOD", "TRUE", "FALSE", "THEN", "DO", "TO", "BY", "OF", "IF", "ELSE", "ELSIF",
    };

    /// <summary>Instructions that gate a rung without being a contact or comparison.</summary>
    private static readonly HashSet<string> OneShots = new(StringComparer.OrdinalIgnoreCase) { "ONS", "OSR", "OSF" };

    /// <summary>Instructions with no tag operand worth tracing (labels, flow control).</summary>
    private static readonly HashSet<string> NoTagInstructions = new(StringComparer.OrdinalIgnoreCase)
    {
        "JMP", "LBL", "SBR", "RET", "NOP", "AFI", "TND", "MCR", "UID", "UIE", "EOT", "BRK",
    };

    private readonly Dictionary<string, TagDecl> _decls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _programNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WriteSite> _sites = new();
    private readonly Dictionary<string, List<WriteSite>> _byStorage = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<WriteSite>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(UnknownSite Site, TagRef Ref)>> _unknownByStorage = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlySet<int>> _aoi = new Dictionary<string, IReadOnlySet<int>>();

    /// <summary>Program names in project order.</summary>
    public List<string> Programs { get; } = new();
    /// <summary>All write sites.</summary>
    public IReadOnlyList<WriteSite> Sites => _sites;
    /// <summary>Routines that could not be read.</summary>
    public List<UnreadableRoutine> Unreadable { get; } = new();
    /// <summary>Rungs that could not be parsed.</summary>
    public List<UnparsedRung> Unparsed { get; } = new();

    /// <summary>Storage key of a tag: scope and name, lower case.</summary>
    public static string StorageKey(string scope, string name) =>
        scope.ToLowerInvariant() + "\u0001" + name.ToLowerInvariant();

    /// <summary>Collapses runs of whitespace to one space.</summary>
    public static string Collapse(string text) => Ws.Replace(text, " ").Trim();

    // ── Build ────────────────────────────────────────────────────────────────

    /// <summary>Indexes every write in <paramref name="project"/>.</summary>
    /// <param name="project">Parsed project.</param>
    public static TraceIndex Build(L5X project)
    {
        var ix = new TraceIndex { _aoi = WriteClassifier.BuildAoiWriteMap(project) };
        ix.BuildFrom(project.Content.Serialize());
        return ix;
    }

    private void BuildFrom(XElement root)
    {
        var controllerTags = root.Descendants("Controller")
            .SelectMany(c => c.Elements("Tags").Elements("Tag")).ToList();
        foreach (var t in controllerTags) AddDecl("", t);

        var programs = root.Descendants()
            .Where(e => e.Name.LocalName is "Program" or "EquipmentPhase" && e.Attribute("Name") is not null)
            .ToList();
        foreach (var p in programs)
        {
            var name = p.Attribute("Name")!.Value;
            Programs.Add(name);
            _programNames[name] = name;
            foreach (var t in p.Elements("Tags").Elements("Tag")) AddDecl(name, t);
        }

        foreach (var p in programs)
        {
            var name = p.Attribute("Name")!.Value;
            foreach (var rt in p.Elements("Routines").Elements()) ScanRoutine(name, rt);
        }

        ScanMessages(controllerTags, programs);
        ScanParameters(programs);
        ScanConnections(root);

        foreach (var s in _sites)
        {
            var resolved = Resolve(s.Scope, s.TargetText);
            if (resolved is null) continue;
            s.Target = resolved;
            if (!_byStorage.TryGetValue(s.Target.Key, out var list)) _byStorage[s.Target.Key] = list = new();
            list.Add(s);
            if (!_byName.TryGetValue(s.Target.Name, out var named)) _byName[s.Target.Name] = named = new();
            named.Add(s);
        }
    }

    private void AddDecl(string scope, XElement tag)
    {
        var name = tag.Attribute("Name")?.Value;
        if (string.IsNullOrEmpty(name)) return;
        var d = new TagDecl { Scope = scope, Name = name, Element = tag };
        _decls[d.Key] = d;
    }

    /// <summary>Declarations with this name in any scope (controller first, then programs in project order).</summary>
    /// <param name="name">Tag name, case-insensitive.</param>
    public List<TagDecl> DeclsNamed(string name) =>
        _decls.Values.Where(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Scope.Length == 0 ? 0 : 1).ToList();

    private TagDecl? Lookup(string scope, string name) =>
        _decls.GetValueOrDefault(StorageKey(scope, name));

    /// <summary>Adds a site (its target is resolved after every declaration is known).</summary>
    private WriteSite Add(WriteSite site)
    {
        var parsed = TryParseRefText(site.TargetText, out var prog, out _, out _);
        site.Scoped = parsed && prog is not null;
        site.Id = _sites.Count + 1;
        _sites.Add(site);
        return site;
    }

    // ── Reference parsing and resolution ─────────────────────────────────────

    /// <summary><c>true</c> for operand text that is a constant or an empty placeholder (<c>?</c>).</summary>
    public static bool IsLiteral(string text)
    {
        var s = text.Trim();
        if (s.Length == 0 || s == "?") return true;
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') return true;
        if (s.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || s.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            return true;
        return NumberLiteral.IsMatch(s);
    }

    /// <summary>
    /// Parses <c>Tag</c>, <c>Tag.Member</c>, <c>Tag[2].Member.5</c>, <c>\Prog.Tag</c> and
    /// <c>Program:Prog.Tag</c>. Returns <c>false</c> for anything else (a literal or an expression).
    /// </summary>
    public static bool TryParseRefText(string raw, out string? program, out string name, out List<PathTok> path)
    {
        program = null;
        name = "";
        path = new List<PathTok>();
        var s = string.Concat(raw.Where(c => !char.IsWhiteSpace(c)));
        if (s.Length == 0) return false;

        var i = 0;
        var cross = false;
        if (s[0] == '\\') { cross = true; i = 1; }
        if (string.Compare(s, i, "Program:", 0, 8, StringComparison.OrdinalIgnoreCase) == 0) { cross = true; i += 8; }
        if (cross)
        {
            var j = i;
            while (j < s.Length && (char.IsAsciiLetterOrDigit(s[j]) || s[j] == '_')) j++;
            if (j == i || j >= s.Length || s[j] != '.') return false;
            program = s[i..j];
            i = j + 1;
        }

        var start = i;
        if (i >= s.Length || !(char.IsAsciiLetter(s[i]) || s[i] == '_')) return false;
        while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_' || s[i] == ':')) i++;
        name = s[start..i];

        while (i < s.Length)
        {
            if (s[i] == '[')
            {
                var depth = 0;
                var k = i;
                for (; k < s.Length; k++)
                {
                    if (s[k] == '[') depth++;
                    else if (s[k] == ']' && --depth == 0) break;
                }
                if (k >= s.Length) return false;
                var content = s[(i + 1)..k];
                if (content.Length == 0) return false;
                path.Add(new PathTok(content, true, content.All(char.IsAsciiDigit)));
                i = k + 1;
            }
            else if (s[i] == '.')
            {
                var k = i + 1;
                while (k < s.Length && (char.IsAsciiLetterOrDigit(s[k]) || s[k] == '_')) k++;
                if (k == i + 1) return false;
                path.Add(new PathTok(s[(i + 1)..k], false, false));
                i = k;
            }
            else return false;
        }
        return true;
    }

    /// <summary>
    /// Resolves operand text to storage: program scope first, then controller scope, then follows
    /// <c>AliasFor</c> to the non-alias tag. Returns <c>null</c> when the text is not a plain tag reference.
    /// </summary>
    /// <param name="scope">Program the operand appears in, or empty for controller scope.</param>
    /// <param name="raw">Operand text.</param>
    public TagRef? Resolve(string scope, string raw)
    {
        if (IsLiteral(raw) || !TryParseRefText(raw, out var prog, out var name, out var path)) return null;

        string curScope;
        TagDecl? decl;
        if (prog is not null)
        {
            curScope = _programNames.GetValueOrDefault(prog) ?? prog;
            decl = Lookup(curScope, name);
        }
        else if (scope.Length > 0 && (decl = Lookup(scope, name)) is not null) curScope = scope;
        else { decl = Lookup("", name); curScope = ""; }

        var r = new TagRef();
        var curName = decl?.Name ?? name;
        var curPath = path;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (decl is { IsAlias: true })
        {
            if (!seen.Add(decl.Key) || r.Chain.Count >= 32) { r.Problem = "the alias chain is circular"; break; }
            r.Chain.Add(new AliasHop(decl.Name,
                decl.Scope.Length == 0 ? decl.Name : $"Program:{decl.Scope}.{decl.Name}", decl.AliasFor));
            if (!TryParseRefText(decl.AliasFor, out var aprog, out var aname, out var apath))
            {
                r.Problem = $"AliasFor '{decl.AliasFor}' cannot be parsed";
                break;
            }
            string nextScope;
            TagDecl? next;
            if (aprog is not null)
            {
                nextScope = _programNames.GetValueOrDefault(aprog) ?? aprog;
                next = Lookup(nextScope, aname);
            }
            else if (decl.Scope.Length > 0 && (next = Lookup(decl.Scope, aname)) is not null) nextScope = decl.Scope;
            else { next = Lookup("", aname); nextScope = ""; }

            curScope = nextScope;
            curName = next?.Name ?? aname;
            curPath = apath.Concat(curPath).ToList();
            decl = next;
        }

        r.Scope = curScope;
        r.Name = curName;
        r.Path = curPath;
        r.Decl = decl;
        return r;
    }

    /// <summary>
    /// <c>true</c> when two paths below the same tag can name the same storage: one is a prefix of the
    /// other (a whole structure covers its members), and differing literal indices or member names rule it out.
    /// A variable index is compatible with every index.
    /// </summary>
    public static bool PathCompatible(IReadOnlyList<PathTok> a, IReadOnlyList<PathTok> b)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.IsIndex != y.IsIndex) return false;
            if (x.IsIndex)
            {
                if (x.Literal && y.Literal &&
                    x.Text.TrimStart('0').PadLeft(1, '0') != y.Text.TrimStart('0').PadLeft(1, '0')) return false;
            }
            else if (!string.Equals(x.Text, y.Text, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>Tag references written inside an expression (function names, keywords and literals excluded).</summary>
    public static List<string> ExpressionRefs(string expression)
    {
        var found = new List<string>();
        foreach (Match m in ExprRef.Matches(expression))
        {
            var text = m.Value;
            var head = text.TrimStart('\\');
            var firstIdent = Regex.Match(head, @"^[A-Za-z_]\w*").Value;
            if (!head.Contains('.') && !head.Contains('[') && ExprWords.Contains(firstIdent)) continue;
            if (WriteClassifier.StKeywords.Contains(text)) continue;
            if (!found.Contains(text, StringComparer.OrdinalIgnoreCase)) found.Add(text);
        }
        return found;
    }

    // ── Writer lookup ────────────────────────────────────────────────────────

    /// <summary>
    /// Finds every write that can reach <paramref name="query"/>. Logic, connections and MSG
    /// destinations first; a parameter declaration only when nothing else writes the tag; a match
    /// by name in another scope only when the tag's own scope has none (the gate matches by bare
    /// name, so the trace shows the same writers and says so).
    /// </summary>
    /// <param name="query">The tag to find writers of.</param>
    /// <param name="focus">Program filter, or <c>null</c>.</param>
    /// <param name="outsideFocus">Writers dropped by <paramref name="focus"/>.</param>
    public List<WriterHit> FindWriters(TagRef query, string? focus, out int outsideFocus)
    {
        var own = _byStorage.GetValueOrDefault(query.Key) ?? new List<WriteSite>();
        var compatible = own.Where(s => PathCompatible(s.Target.Path, query.Path)).ToList();
        var tier = compatible.Where(s => s.Tier == 1).ToList();
        if (tier.Count == 0) tier = compatible.Where(s => s.Tier == 2).ToList();
        var nameOnly = false;

        if (tier.Count == 0)
        {
            var others = (_byName.GetValueOrDefault(query.Name) ?? new List<WriteSite>())
                .Where(s => s.Target.Key != query.Key && !s.Scoped && PathCompatible(s.Target.Path, query.Path))
                .ToList();
            tier = others.Where(s => s.Tier == 1).ToList();
            if (tier.Count == 0) tier = others.Where(s => s.Tier == 2).ToList();
            nameOnly = tier.Count > 0;
        }

        var hits = new List<WriterHit>();
        outsideFocus = 0;
        foreach (var s in tier)
        {
            if (focus is not null && s.Program is not null &&
                !string.Equals(s.Program, focus, StringComparison.OrdinalIgnoreCase))
            {
                outsideFocus++;
                continue;
            }
            hits.Add(new WriterHit(s, nameOnly));
        }
        return hits;
    }

    /// <summary>Instructions that take <paramref name="query"/> but whose write semantics are unknown.</summary>
    public List<UnknownSite> FindUnknown(TagRef query)
    {
        if (_unknownByStorage.Count == 0 && _unknownSites.Count > 0)
        {
            foreach (var u in _unknownSites)
                foreach (var op in u.Operands)
                {
                    var r = Resolve(u.Scope, op);
                    if (r is null) continue;
                    if (!_unknownByStorage.TryGetValue(r.Key, out var list)) _unknownByStorage[r.Key] = list = new();
                    list.Add((u, r));
                }
        }
        return (_unknownByStorage.GetValueOrDefault(query.Key) ?? new())
            .Where(x => PathCompatible(x.Ref.Path, query.Path))
            .Select(x => x.Site).Distinct().ToList();
    }

    private readonly List<UnknownSite> _unknownSites = new();

    // ── Routines ─────────────────────────────────────────────────────────────

    private void ScanRoutine(string program, XElement rt)
    {
        var routine = rt.Attribute("Name")?.Value ?? "?";
        var type = rt.Attribute("Type")?.Value;
        var content = type switch
        {
            "RLL" => "RLLContent", "ST" => "STContent", "FBD" => "FBDContent", "SFC" => "SFCContent", _ => null,
        };
        if (rt.Name.LocalName != "Routine" || type is null || rt.DescendantsAndSelf("EncodedData").Any() ||
            (content is not null && rt.Element(content) is null))
        {
            Unreadable.Add(new UnreadableRoutine(program, rt.Attribute("Name")?.Value ?? "(encoded)"));
            return;
        }

        switch (type)
        {
            case "RLL":
                foreach (var rung in rt.Element("RLLContent")!.Elements("Rung")) ScanRung(program, routine, rung, "RLL", "");
                break;
            case "ST":
                ScanSt(rt.Element("STContent")!, program, routine, "ST", "");
                break;
            case "FBD":
                ScanFbd(rt.Element("FBDContent")!, program, routine);
                break;
            case "SFC":
                foreach (var st in rt.Element("SFCContent")!.Descendants("STContent"))
                {
                    var action = st.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Action");
                    var label = action?.Attribute("Operand")?.Value ?? action?.Attribute("ID")?.Value ?? "?";
                    ScanSt(st, program, routine, "SFC", $"action {label}, ");
                }
                foreach (var rung in rt.Element("SFCContent")!.Descendants("Rung"))
                    ScanRung(program, routine, rung, "SFC", "action ");
                break;
        }
    }

    // ── RLL ──────────────────────────────────────────────────────────────────

    private void ScanRung(string program, string routine, XElement rung, string kind, string locPrefix)
    {
        var number = rung.Attribute("Number")?.Value ?? "?";
        var text = rung.Element("Text")?.Value ?? "";
        if (!RungStructure.TryParse(text, out var nodes, out _))
        {
            Unparsed.Add(new UnparsedRung(program, routine, number, text));
            return;
        }
        var ctx = new RungCtx(program, routine, $"{locPrefix}rung {number}", kind);
        Walk(ctx, nodes, new List<RawCond>());
    }

    private sealed record RungCtx(string Program, string Routine, string Location, string Kind);

    private void Walk(RungCtx ctx, IReadOnlyList<RungNode> sequence, List<RawCond> inherited)
    {
        var local = new List<RawCond>(inherited);
        foreach (var node in sequence)
        {
            if (node is InstructionNode inst)
            {
                var cond = AsCondition(inst);
                if (cond is not null) local.Add(cond);
                else Instruction(ctx, inst, local);
            }
            else if (node is BranchNode branch)
            {
                foreach (var leg in branch.Legs) Walk(ctx, leg, local);
                var legs = branch.Legs.Select(LegGate).ToList();
                if (legs.All(l => l.Count > 0))
                    local.Add(new RawCond("branch",
                        "[" + string.Join(" | ", legs.Select(l => string.Join(" ", l.Select(c => c.Text)))) + "]",
                        legs.SelectMany(l => l).SelectMany(c => c.Operands).ToList()));
            }
        }
    }

    private static List<RawCond> LegGate(IReadOnlyList<RungNode> leg)
    {
        var gate = new List<RawCond>();
        foreach (var n in leg)
            if (n is InstructionNode i && AsCondition(i) is { } c) gate.Add(c);
        return gate;
    }

    /// <summary>The instruction as a gating condition, or <c>null</c> when it is not a contact, comparison or one-shot.</summary>
    private static RawCond? AsCondition(InstructionNode inst)
    {
        var text = InstructionText(inst);
        if (OneShots.Contains(inst.Key)) return new RawCond("one-shot", text, Array.Empty<string>());
        if (!WriteClassifier.IsReadOnlyInstruction(inst.Key)) return null;
        var contact = inst.Key.Equals("XIC", StringComparison.OrdinalIgnoreCase) ||
                      inst.Key.Equals("XIO", StringComparison.OrdinalIgnoreCase);
        return new RawCond(contact ? "contact" : "compare", text, inst.Args);
    }

    private static string InstructionText(InstructionNode inst) =>
        $"{inst.Key}({string.Join(",", inst.Args.Select(Collapse))})";

    private void Instruction(RungCtx ctx, InstructionNode inst, List<RawCond> conditions)
    {
        var key = inst.Key;
        var args = inst.Args;
        var kinds = args.Select((_, i) => WriteClassifier.ClassifyKind(key, args, i, _aoi)).ToList();
        var text = InstructionText(inst);
        var scope = ctx.Program;

        if (kinds.Contains("unknown") && !NoTagInstructions.Contains(key))
        {
            var operands = args.Where((a, i) => kinds[i] != "read" && !IsLiteral(a)).ToList();
            if (operands.Count > 0)
                _unknownSites.Add(new UnknownSite(ctx.Program, ctx.Routine, ctx.Location, key.ToUpperInvariant(), text,
                    scope, operands));
        }

        var isJsr = key.Equals("JSR", StringComparison.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            if (kinds[i] != "write" || IsLiteral(args[i])) continue;
            var site = new WriteSite
            {
                Program = ctx.Program,
                Routine = ctx.Routine,
                Location = ctx.Location,
                Kind = ctx.Kind,
                Instruction = key.ToUpperInvariant(),
                Text = text,
                Scope = scope,
                TargetText = args[i],
            };
            for (var j = 0; j < args.Count; j++)
            {
                if (kinds[j] != "read") continue;
                if (isJsr && j < 2) continue;
                if (IsLiteral(args[j]) && args[j].Trim() == "?") continue;
                site.SourceOperands.Add(args[j]);
            }
            site.Conditions.AddRange(conditions);

            switch (InstructionMnemonics.Canonical(key))
            {
                case "OTE": site.Value = "1"; site.ValueKind = "implicit"; break;
                case "OTL": site.Value = "latch"; site.ValueKind = "implicit"; break;
                case "OTU": site.Value = "unlatch"; site.ValueKind = "implicit"; break;
                case "CLR": site.Value = "0"; site.ValueKind = "implicit"; break;
                case "RES": site.Value = "reset"; site.ValueKind = "implicit"; break;
                case "MOVE" when site.SourceOperands.Count > 0 && IsLiteral(site.SourceOperands[0]):
                    site.Value = site.SourceOperands[0].Trim();
                    site.ValueKind = "literal";
                    break;
            }
            if (isJsr)
            {
                site.GapKind = SiteKinds.JsrGap;
                site.GapDetail =
                    $"JSR to routine '{(args.Count > 0 ? args[0].Trim() : "?")}' returns '{args[i].Trim()}', which the called " +
                    "routine fills in. Parameter passing is not followed: the sources shown are the values passed in, " +
                    "not what the routine computes.";
            }
            Add(site);
        }
    }

    // ── ST ───────────────────────────────────────────────────────────────────

    private sealed class StRegion
    {
        public int Start;
        public int End = int.MaxValue;
        public string Kind = "";
        public string Text = "";
        public string Expr = "";
    }

    private sealed class StFrame
    {
        public string Kind = "";
        public int CondStart;
        public string Opener = "";
        public StRegion? Region;
        public List<string> Conds = new();
    }

    /// <summary>Finds the IF / CASE / loop bodies in joined, comment-free ST text.</summary>
    private static List<StRegion> BuildStRegions(string text)
    {
        var regions = new List<StRegion>();
        var stack = new Stack<StFrame>();

        StRegion Open(StFrame f, int start, string kind, string label, string expr)
        {
            var r = new StRegion { Start = start, Kind = kind, Text = label, Expr = expr };
            regions.Add(r);
            f.Region = r;
            return r;
        }
        void Close(StFrame f, int at)
        {
            if (f.Region is { } r && r.End == int.MaxValue) r.End = at;
        }

        foreach (Match m in StBlockKeyword.Matches(text))
        {
            var kw = m.Value.ToUpperInvariant();
            var s = m.Index;
            var e = m.Index + m.Length;
            var top = stack.Count > 0 ? stack.Peek() : null;
            switch (kw)
            {
                case "IF": stack.Push(new StFrame { Kind = "if", CondStart = e, Opener = "IF" }); break;
                case "CASE": stack.Push(new StFrame { Kind = "case", CondStart = e, Opener = "CASE" }); break;
                case "FOR": stack.Push(new StFrame { Kind = "loop", CondStart = e, Opener = "FOR" }); break;
                case "WHILE": stack.Push(new StFrame { Kind = "loop", CondStart = e, Opener = "WHILE" }); break;
                case "REPEAT":
                {
                    var f = new StFrame { Kind = "repeat", CondStart = e, Opener = "REPEAT" };
                    stack.Push(f);
                    Open(f, e, "loop", "REPEAT", "");
                    break;
                }
                case "ELSIF" when top is { Kind: "if" }:
                    Close(top, s);
                    top.CondStart = e;
                    top.Opener = "ELSIF";
                    break;
                case "THEN" when top is { Kind: "if" }:
                {
                    var cond = Collapse(text[top.CondStart..s]);
                    top.Conds.Add(cond);
                    Open(top, e, "if", $"{top.Opener} {cond}", cond);
                    break;
                }
                case "ELSE" when top is { Kind: "if" }:
                    Close(top, s);
                    Open(top, e, "if", $"ELSE (none of: {string.Join("; ", top.Conds)})", string.Join(" ", top.Conds));
                    break;
                case "ELSE" when top is { Kind: "case" }:
                    Close(top, s);
                    Open(top, e, "case", $"ELSE of CASE {top.Conds.FirstOrDefault()}", top.Conds.FirstOrDefault() ?? "");
                    break;
                case "OF" when top is { Kind: "case" }:
                {
                    var expr = Collapse(text[top.CondStart..s]);
                    top.Conds.Add(expr);
                    Open(top, e, "case", $"CASE {expr} OF", expr);
                    break;
                }
                case "DO" when top is { Kind: "loop", Region: null }:
                {
                    var cond = Collapse(text[top.CondStart..s]);
                    Open(top, e, "loop", $"{top.Opener} {cond} DO", cond);
                    break;
                }
                case "UNTIL" when top is { Kind: "repeat" }:
                    Close(top, s);
                    top.CondStart = e;
                    break;
                case "END_REPEAT" when top is { Kind: "repeat" }:
                {
                    var cond = Collapse(text[top.CondStart..s]);
                    if (top.Region is { } r) { r.Text = $"REPEAT … UNTIL {cond}"; r.Expr = cond; }
                    stack.Pop();
                    break;
                }
                case "END_IF" when top is { Kind: "if" }:
                case "END_CASE" when top is { Kind: "case" }:
                case "END_FOR" when top is { Kind: "loop" }:
                case "END_WHILE" when top is { Kind: "loop" }:
                    Close(top, s);
                    stack.Pop();
                    break;
            }
        }
        return regions;
    }

    private void ScanSt(XElement st, string program, string routine, string kind, string locPrefix)
    {
        var findings = WriteClassifier.ScanStructuredTextDetailed(st, _aoi, out var text);
        var regions = BuildStRegions(text);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        List<RawCond> ConditionsAt(int pos) => regions
            .Where(r => r.Start <= pos && pos < r.End)
            .OrderBy(r => r.Start)
            .Select(r => new RawCond(r.Kind, r.Text, new[] { r.Expr }))
            .ToList();

        foreach (var f in findings)
        {
            var location = $"{locPrefix}ST line {f.Line}";

            if (!f.Proven)
            {
                var label = f.Via is null ? $"{f.Target} :=" : $"{f.Via}(…)";
                _unknownSites.Add(new UnknownSite(program, routine, location, f.Via ?? ":=", label, program,
                    new[] { f.Target }));
                continue;
            }

            if (f.Via is null)
            {
                var assignAt = text.IndexOf(":=", f.Pos, StringComparison.Ordinal);
                var end = assignAt < 0 ? text.Length : text.IndexOf(';', assignAt) is var semi and >= 0 ? semi : text.Length;
                var rhs = "";
                if (assignAt >= 0)
                {
                    var header = ForHeaderEnd.Match(text, assignAt + 2);
                    if (header.Success && header.Index < end) end = header.Index;
                    rhs = Collapse(text[(assignAt + 2)..end]);
                }
                if (!seen.Add($"{f.Pos}|{f.Target}")) continue;

                var site = new WriteSite
                {
                    Program = program,
                    Routine = routine,
                    Location = location,
                    Kind = kind,
                    Instruction = ":=",
                    Text = Collapse($"{f.Target} := {rhs}"),
                    Scope = program,
                    TargetText = f.Target,
                };
                if (rhs.Length > 0) site.SourceOperands.Add(rhs);
                if (rhs.Length > 0 && IsLiteral(rhs))
                {
                    site.Value = rhs;
                    site.ValueKind = "literal";
                }
                site.Conditions.AddRange(ConditionsAt(f.Pos));
                Add(site);
                continue;
            }

            var open = text.IndexOf('(', f.Pos);
            if (open < 0) continue;
            var args = WriteClassifier.SplitCallArguments(text, open);
            var argIndex = Enumerable.Range(0, args.Count).FirstOrDefault(a =>
                args[a].Trim() == f.Target &&
                WriteClassifier.ClassifyStCallArgument(f.Via, args, a, _aoi) == "write" &&
                !seen.Contains($"{f.Pos}|{a}"), -1);
            if (argIndex < 0 || !seen.Add($"{f.Pos}|{argIndex}")) continue;

            var isJsr = f.Via.Equals("JSR", StringComparison.OrdinalIgnoreCase);
            var call = new WriteSite
            {
                Program = program,
                Routine = routine,
                Location = location,
                Kind = kind,
                Instruction = f.Via,
                Text = $"{f.Via}({string.Join(",", args.Select(a => Collapse(a)))})",
                Scope = program,
                TargetText = f.Target,
            };
            for (var a = 0; a < args.Count; a++)
            {
                if (a == argIndex || (isJsr && a < 2)) continue;
                if (WriteClassifier.ClassifyStCallArgument(f.Via, args, a, _aoi) == "read")
                    call.SourceOperands.Add(args[a].Trim());
            }
            call.Conditions.AddRange(ConditionsAt(f.Pos));
            if (isJsr)
            {
                call.GapKind = SiteKinds.JsrGap;
                call.GapDetail =
                    $"JSR to routine '{(args.Count > 0 ? args[0].Trim() : "?")}' returns '{f.Target}', which the called " +
                    "routine fills in. Parameter passing is not followed: the sources shown are the values passed in, " +
                    "not what the routine computes.";
            }
            Add(call);
        }
    }

    // ── FBD ──────────────────────────────────────────────────────────────────

    private void ScanFbd(XElement content, string program, string routine)
    {
        foreach (var sheet in content.Elements("Sheet"))
        {
            var number = sheet.Attribute("Number")?.Value ?? "?";
            var byId = sheet.Elements().Where(e => e.Attribute("ID") is not null)
                .GroupBy(e => e.Attribute("ID")!.Value).ToDictionary(g => g.Key, g => g.First());
            var wires = sheet.Elements("Wire").ToList();

            (List<string> Sources, List<string> Blocks) Upstream(string id)
            {
                var sources = new List<string>();
                var blocks = new List<string>();
                var visited = new HashSet<string> { id };
                var work = new Stack<(string Id, int Depth)>();
                work.Push((id, 0));
                while (work.Count > 0)
                {
                    var (cur, depth) = work.Pop();
                    foreach (var w in wires.Where(w => w.Attribute("ToID")?.Value == cur))
                    {
                        var from = w.Attribute("FromID")?.Value;
                        if (from is null || !byId.TryGetValue(from, out var el) || !visited.Add(from)) continue;
                        if (el.Name.LocalName == "IRef")
                        {
                            var operand = el.Attribute("Operand")?.Value;
                            if (!string.IsNullOrWhiteSpace(operand) && !sources.Contains(operand)) sources.Add(operand);
                        }
                        else if (depth < 12)
                        {
                            var label = el.Attribute("Type")?.Value ?? el.Attribute("Name")?.Value ?? el.Name.LocalName;
                            if (!blocks.Contains(label)) blocks.Add(label);
                            work.Push((from, depth + 1));
                        }
                    }
                }
                return (sources, blocks);
            }

            foreach (var el in sheet.Elements().Where(e => e.Name.LocalName is "ORef" or "AddOnInstruction"))
            {
                var operand = el.Attribute("Operand")?.Value;
                var id = el.Attribute("ID")?.Value;
                if (string.IsNullOrWhiteSpace(operand) || id is null) continue;
                var (sources, blocks) = Upstream(id);
                var isOref = el.Name.LocalName == "ORef";
                var name = isOref ? "ORef" : el.Attribute("Name")?.Value ?? "AddOnInstruction";
                var site = new WriteSite
                {
                    Program = program,
                    Routine = routine,
                    Location = $"sheet {number} {(isOref ? "ORef" : "block")} {id}",
                    Kind = SiteKinds.Fbd,
                    Instruction = name,
                    Text = $"{name} {operand}" + (blocks.Count > 0 ? $" <- {string.Join(", ", blocks)}" : ""),
                    Scope = program,
                    TargetText = operand,
                };
                site.SourceOperands.AddRange(sources);
                Add(site);
            }
        }
    }

    // ── MSG, parameters, connections ─────────────────────────────────────────

    private void ScanMessages(List<XElement> controllerTags, List<XElement> programs)
    {
        void Scan(XElement tag, string scope)
        {
            var tagName = tag.Attribute("Name")?.Value ?? "?";
            foreach (var mp in tag.Descendants("MessageParameters"))
            {
                var remote = mp.Attribute("RemoteElement")?.Value;
                var path = mp.Attribute("ConnectionPath")?.Value;
                foreach (var (attribute, value, written, reason) in WriteClassifier.ClassifyMsgAttributes(mp))
                {
                    var v = value.Trim();
                    if (!written)
                    {
                        _unknownSites.Add(new UnknownSite(scope.Length == 0 ? "(controller)" : scope, tagName,
                            $"MessageParameters {attribute}", "MSG", $"MSG {tagName} ({attribute} '{v}')", scope,
                            new[] { v }));
                        continue;
                    }
                    var site = new WriteSite
                    {
                        Program = scope.Length == 0 ? null : scope,
                        Routine = tagName,
                        Location = $"MessageParameters {attribute}",
                        Kind = SiteKinds.Msg,
                        Instruction = "MSG",
                        Text = $"MSG {tagName} ({attribute} '{v}')",
                        Scope = scope,
                        TargetText = v,
                        Note = remote is null
                            ? "The reply of a message lands here; the value comes from the remote device."
                            : $"The reply of a message lands here; the value comes from remote element '{remote}'" +
                              (string.IsNullOrWhiteSpace(path) ? "." : $" over path '{path}'."),
                    };
                    msgSites.Add((tagName, scope, site));
                    Add(site);
                }
            }
        }

        foreach (var t in controllerTags) Scan(t, "");
        foreach (var p in programs)
        {
            var name = p.Attribute("Name")!.Value;
            foreach (var t in p.Elements("Tags").Elements("Tag")) Scan(t, name);
        }
    }

    private readonly List<(string MsgTag, string Scope, WriteSite Site)> msgSites = new();

    private void ScanParameters(List<XElement> programs)
    {
        foreach (var p in programs)
        {
            var program = p.Attribute("Name")!.Value;
            foreach (var t in p.Elements("Tags").Elements("Tag"))
            {
                var name = t.Attribute("Name")?.Value;
                var usage = t.Attribute("Usage")?.Value;
                if (string.IsNullOrEmpty(name) || usage is null) continue;
                if (!usage.Equals("Output", StringComparison.OrdinalIgnoreCase) &&
                    !usage.Equals("InOut", StringComparison.OrdinalIgnoreCase)) continue;
                Add(new WriteSite
                {
                    Program = program,
                    Routine = "(parameter)",
                    Location = $"parameter {name}",
                    Kind = SiteKinds.Parameter,
                    Instruction = $"Usage={usage}",
                    Text = $"{usage} parameter {name} of program {program}",
                    Scope = program,
                    TargetText = name,
                    Tier = 2,
                    Note = "Declared as an output parameter: the program drives it and callers read it. " +
                           "No logic in the project that writes it by name was found.",
                });
            }
        }
    }

    private void ScanConnections(XElement root)
    {
        var connections = root.Descendants("Controller").Elements("ParameterConnections")
            .Elements("ParameterConnection").ToList();
        if (connections.Count == 0) return;

        static bool Usable(string usage) =>
            usage.Equals("Output", StringComparison.OrdinalIgnoreCase) ||
            usage.Equals("InOut", StringComparison.OrdinalIgnoreCase) ||
            usage.Equals("Input", StringComparison.OrdinalIgnoreCase) ||
            usage.Equals("Public", StringComparison.OrdinalIgnoreCase);

        foreach (var c in connections)
        {
            var ends = new[] { c.Attribute("EndPoint1")?.Value, c.Attribute("EndPoint2")?.Value };
            if (ends.Any(string.IsNullOrWhiteSpace)) continue;
            var keys = ends.Select(e => WriteClassifier.ExtractScopedName(e)).ToArray();
            if (keys.Any(string.IsNullOrEmpty)) continue;

            var proven = false;
            for (var i = 0; i < 2 && !proven; i++)
            {
                var k = keys[i]!;
                var declared = k.StartsWith("Program:", StringComparison.OrdinalIgnoreCase)
                    ? ParameterUsage(k)
                    : null;
                if (declared is not null) proven = Usable(declared);
                else if (k.StartsWith("Program:", StringComparison.OrdinalIgnoreCase)) proven = true;
            }
            if (!proven) continue;

            var a = ends[0]!.Trim();
            var b = ends[1]!.Trim();
            for (var i = 0; i < 2; i++)
            {
                var self = i == 0 ? a : b;
                var other = i == 0 ? b : a;
                var site = new WriteSite
                {
                    Program = null,
                    Routine = "(ParameterConnection)",
                    Location = $"{a} <-> {b}",
                    Kind = SiteKinds.Connection,
                    Instruction = "ParameterConnection",
                    Text = $"ParameterConnection {a} <-> {b}",
                    Scope = "",
                    TargetText = self,
                };
                site.SourceOperands.Add(other);
                Add(site);
            }
        }
    }

    /// <summary>Usage of the program parameter named by a <c>Program:P.Tag</c> key, or <c>null</c> when it is not declared with one.</summary>
    private string? ParameterUsage(string scopedKey)
    {
        var rest = scopedKey["Program:".Length..];
        var dot = rest.IndexOf('.');
        if (dot < 0) return null;
        var prog = _programNames.GetValueOrDefault(rest[..dot]) ?? rest[..dot];
        var d = Lookup(prog, rest[(dot + 1)..]);
        return d is { Usage.Length: > 0 } ? d.Usage : null;
    }

    /// <summary>
    /// The conditions of the first rung that starts the MSG instruction with control tag
    /// <paramref name="msgTag"/>, with where it is, or <c>null</c> when no rung does.
    /// </summary>
    public WriteSite? TriggerOf(WriteSite msgDestination)
    {
        var entry = msgSites.FirstOrDefault(m => m.Site == msgDestination);
        if (entry.Site is null) return null;
        return _sites.FirstOrDefault(s => s.Instruction == "MSG" && s.Kind != "MSG" &&
            string.Equals(s.TargetText.Trim(), entry.MsgTag, StringComparison.OrdinalIgnoreCase) &&
            (entry.Scope.Length == 0 || string.Equals(s.Program, entry.Scope, StringComparison.OrdinalIgnoreCase)));
    }
}
