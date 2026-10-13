using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using L5Sharp.Core;

// ── Rung structure (branches) ────────────────────────────────────────────────

/// <summary>One element of a parsed rung: an instruction or a branch.</summary>
internal abstract record RungNode;

/// <summary>An instruction with its top-level operands, as written in the neutral text.</summary>
/// <param name="Key">Instruction mnemonic as written, e.g. <c>XIC</c>.</param>
/// <param name="Args">Operands split on top-level commas; whitespace is trimmed, nothing is dropped.</param>
internal sealed record InstructionNode(string Key, IReadOnlyList<string> Args) : RungNode;

/// <summary>A parallel branch: two or more legs, each a series of elements. A leg may be empty (a bypass wire).</summary>
/// <param name="Legs">The legs, top to bottom.</param>
internal sealed record BranchNode(IReadOnlyList<IReadOnlyList<RungNode>> Legs) : RungNode;

/// <summary>
/// Builds the branch structure of a rung from its neutral text. The text is tokenised by the
/// L5Sharp <see cref="NeutralText"/> tokenizer, reached through <see cref="RungParser"/> (which the
/// write classifier and every lint rule also use); this class only groups those tokens into
/// instructions and <c>[ ... , ... ]</c> branches, which the flat instruction list cannot show.
/// It does not tokenise anything itself.
/// </summary>
internal static class RungStructure
{
    /// <summary>
    /// Parses <paramref name="text"/>. Returns <c>false</c> with <paramref name="error"/> set when the
    /// text cannot be tokenised or its brackets do not balance; the caller then shows the raw text.
    /// </summary>
    /// <param name="text">Rung neutral text, e.g. <c>XIC(a)[XIC(b),XIC(c)]OTE(d);</c>.</param>
    /// <param name="nodes">The top-level sequence on success.</param>
    /// <param name="error">Why parsing failed, or <c>null</c>.</param>
    public static bool TryParse(string text, out IReadOnlyList<RungNode> nodes, out string? error)
    {
        nodes = Array.Empty<RungNode>();
        error = null;
        List<NeutralToken> tokens;
        try
        {
            // RungParser masks the '\' of a cross-program reference so token indexes stay valid.
            tokens = RungParser.Tokenize(text).ToList();
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }

        var pos = 0;
        try
        {
            var seq = ParseSequence(text, tokens, ref pos, 0);
            if (tokens[pos].Type == TokenType.SemiColon) pos++;
            if (tokens[pos].Type != TokenType.EOF)
                throw new FormatException($"unexpected '{tokens[pos].Value}' at position {tokens[pos].Index}");
            nodes = seq;
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static List<RungNode> ParseSequence(string text, List<NeutralToken> tokens, ref int pos, int depth)
    {
        var seq = new List<RungNode>();
        while (true)
        {
            var t = tokens[pos];
            if (t.Type == TokenType.Identifier)
            {
                pos++;
                var args = new List<string>();
                if (tokens[pos].Type == TokenType.OpenParen)
                {
                    var open = tokens[pos];
                    var level = 0;
                    var argStart = open.Index + 1;
                    var squares = 0;
                    var closed = false;
                    while (!closed)
                    {
                        var k = tokens[pos];
                        if (k.Type == TokenType.EOF) throw new FormatException("unbalanced '('");
                        if (k.Type == TokenType.OpenParen) level++;
                        else if (k.Type == TokenType.OpenBracket) squares++;
                        else if (k.Type == TokenType.CloseBracket) squares--;
                        else if (k.Type == TokenType.CloseParen)
                        {
                            if (--level == 0)
                            {
                                var last = text[argStart..k.Index].Trim();
                                if (last.Length > 0 || args.Count > 0) args.Add(last);
                                closed = true;
                            }
                        }
                        else if (k.Type == TokenType.Comma && level == 1 && squares == 0)
                        {
                            args.Add(text[argStart..k.Index].Trim());
                            argStart = k.Index + 1;
                        }
                        pos++;
                    }
                }
                seq.Add(new InstructionNode(t.Value, args));
            }
            else if (t.Type == TokenType.OpenBracket)
            {
                pos++;
                var legs = new List<IReadOnlyList<RungNode>>();
                while (true)
                {
                    legs.Add(ParseSequence(text, tokens, ref pos, depth + 1));
                    if (tokens[pos].Type == TokenType.Comma) { pos++; continue; }
                    if (tokens[pos].Type == TokenType.CloseBracket) { pos++; break; }
                    throw new FormatException("unbalanced '['");
                }
                seq.Add(new BranchNode(legs));
            }
            else if (t.Type == TokenType.Comma || t.Type == TokenType.CloseBracket)
            {
                if (depth == 0) throw new FormatException($"unexpected '{t.Value}' outside a branch");
                return seq;
            }
            else if (t.Type == TokenType.SemiColon || t.Type == TokenType.EOF)
            {
                if (depth > 0) throw new FormatException("unbalanced '['");
                return seq;
            }
            else
            {
                throw new FormatException($"unexpected '{t.Value}' at position {t.Index}");
            }
        }
    }
}

// ── Layout ───────────────────────────────────────────────────────────────────

/// <summary>Looks up project-authored text and AOI parameter names while a rung is drawn.</summary>
internal interface IRungContext
{
    /// <summary>
    /// Description lines for an operand (the tag description, then the bit or member description
    /// when there is one). Empty when the operand is not a tag or has no description.
    /// </summary>
    IReadOnlyList<string> Describe(string operand);

    /// <summary>
    /// The operand labels of an add-on instruction call (instance tag first), or <c>null</c> when
    /// <paramref name="key"/> is not an add-on instruction defined in the project.
    /// </summary>
    IReadOnlyList<string>? AoiLabels(string key);
}

/// <summary>A context with no descriptions and no add-on instructions.</summary>
internal sealed class EmptyRungContext : IRungContext
{
    public static readonly EmptyRungContext Instance = new();
    public IReadOnlyList<string> Describe(string operand) => Array.Empty<string>();
    public IReadOnlyList<string>? AoiLabels(string key) => null;
}

/// <summary>One drawn rung: its rows and whether it had to wrap onto continuation rows.</summary>
/// <param name="Lines">The diagram rows, each at most <see cref="RungDiagram.MaxWidth"/> characters.</param>
/// <param name="Wrapped"><c>true</c> when the rung continues on further rows (marked <c>…</c>).</param>
/// <param name="Parsed"><c>false</c> when the rung text could not be structured and is shown as raw text.</param>
internal sealed record DrawnRung(IReadOnlyList<string> Lines, bool Wrapped, bool Parsed);

/// <summary>
/// Draws one rung as box-drawing ladder text between two power rails. Pure: no I/O, no project
/// access beyond the <see cref="IRungContext"/> it is given.
/// </summary>
internal static class RungDiagram
{
    /// <summary>Longest output line, in characters.</summary>
    public const int MaxWidth = 120;

    private const int RailWidth = 2;          // left and right power rail
    private const int MarginWidth = 2;        // one wire character either side of the drawing
    private const int MinInner = 24;          // shortest drawing width, so an empty rung still reads as a rung
    private const int MaxNameColumn = 32;     // widest tag-name column before the name itself wraps
    private const int MinDescColumn = 14;     // narrowest column a description is wrapped to
    private const int MaxBoxInner = 34;       // widest instruction box interior
    private const int MinBranchLimit = 12;    // below this a nested branch cannot be drawn at all

    // ── Public entry ─────────────────────────────────────────────────────────

    /// <summary>Draws <paramref name="neutralText"/>; never throws.</summary>
    /// <param name="neutralText">The rung's neutral text.</param>
    /// <param name="context">Descriptions and add-on instruction labels.</param>
    public static DrawnRung Draw(string neutralText, IRungContext context)
    {
        if (!RungStructure.TryParse(neutralText, out var nodes, out var error))
            return Raw(neutralText, $"rung text could not be drawn as ladder ({error}); shown as written");
        try
        {
            return Frame(nodes, context);
        }
        catch (DiagramTooDeepException)
        {
            return Raw(neutralText, "branches are nested too deeply to draw within 120 columns; shown as written");
        }
    }

    // ── Frame: rails, margins, continuation rows ─────────────────────────────

    private static DrawnRung Frame(IReadOnlyList<RungNode> nodes, IRungContext ctx)
    {
        var limit = MaxWidth - RailWidth - MarginWidth;
        var chunks = Sequence(nodes, limit, ctx);
        var inner = Math.Max(MinInner, chunks.Max(c => c.Width));
        var width = inner + MarginWidth;
        var lines = new List<string>();
        for (var ci = 0; ci < chunks.Count; ci++)
        {
            var c = chunks[ci];
            var continuedFromPrevious = ci > 0;
            var continuesOnNext = ci < chunks.Count - 1;
            if (ci > 0) lines.Add("║" + new string(' ', width) + "║");
            for (var r = 0; r < c.Lines.Count; r++)
            {
                var body = c.Lines[r];
                if (r == c.Wire)
                {
                    var left = continuedFromPrevious ? '…' : '╟';
                    var right = continuesOnNext ? '…' : '╢';
                    lines.Add(left + "─" + body.PadRight(inner, '─') + "─" + right);
                }
                else
                {
                    lines.Add("║ " + body.PadRight(inner) + " ║");
                }
            }
        }
        return new DrawnRung(lines, chunks.Count > 1, true);
    }

    private static DrawnRung Raw(string text, string note)
    {
        var lines = new List<string> { "[" + note + "]" };
        lines.AddRange(WrapMarked(text, MaxWidth - 2).Select(l => "  " + l));
        return new DrawnRung(lines, false, false);
    }

    // ── Cells ────────────────────────────────────────────────────────────────

    /// <summary>A rectangle of text with one row marked as the wire row.</summary>
    private sealed class Cell
    {
        public List<string> Lines { get; }
        public int Wire { get; }
        public int Width => Lines.Count == 0 ? 0 : Lines[0].Length;
        public Cell(List<string> lines, int wire) { Lines = lines; Wire = wire; }
    }

    private sealed class DiagramTooDeepException : Exception { }

    /// <summary>Lays cells side by side, aligned on their wire rows and joined by one wire character.</summary>
    private static Cell HCat(IReadOnlyList<Cell> cells)
    {
        if (cells.Count == 0) return new Cell(new List<string> { "" }, 0);
        var above = cells.Max(c => c.Wire);
        var below = cells.Max(c => c.Lines.Count - c.Wire - 1);
        var rows = new List<string>();
        for (var r = 0; r <= above + below; r++)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                if (i > 0) sb.Append(r == above ? '─' : ' ');
                var idx = r - (above - c.Wire);
                sb.Append(idx >= 0 && idx < c.Lines.Count ? c.Lines[idx] : new string(' ', c.Width));
            }
            rows.Add(sb.ToString());
        }
        return new Cell(rows, above);
    }

    /// <summary>
    /// Draws a series of elements, packing them left to right and starting a new chunk (a continuation
    /// row) whenever the next element would pass <paramref name="limit"/> columns.
    /// </summary>
    private static List<Cell> Sequence(IReadOnlyList<RungNode> nodes, int limit, IRungContext ctx)
    {
        var chunks = new List<Cell>();
        var current = new List<Cell>();
        var used = 0;
        foreach (var node in nodes)
        {
            var cell = Element(node, limit, ctx);
            var need = current.Count == 0 ? cell.Width : used + 1 + cell.Width;
            if (current.Count > 0 && need > limit)
            {
                chunks.Add(HCat(current));
                current.Clear();
                need = cell.Width;
            }
            current.Add(cell);
            used = need;
        }
        chunks.Add(HCat(current));
        return chunks;
    }

    private static Cell Element(RungNode node, int limit, IRungContext ctx) => node switch
    {
        BranchNode b => Branch(b, limit, ctx),
        InstructionNode i => Instruction(i, ctx),
        _ => throw new InvalidOperationException("unknown rung node"),
    };

    // ── Branches ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Stacks the legs of a branch. Each leg is drawn between a left and a right connector column;
    /// a leg too wide for the line wraps into continuation rows of its own (marked <c>…</c>), so a
    /// wide nested branch is split, never cut.
    /// </summary>
    private static Cell Branch(BranchNode branch, int limit, IRungContext ctx)
    {
        var legLimit = limit - 4;
        if (legLimit < MinBranchLimit) throw new DiagramTooDeepException();

        var legs = branch.Legs.Select(l => Sequence(l, legLimit, ctx)).ToList();
        var inner = Math.Max(1, legs.SelectMany(l => l).Max(c => c.Width));

        var rows = new List<(string Body, char LeftMargin, char RightMargin, bool IsWire)>();
        var firstWire = new List<int>();   // row index of each leg's first chunk wire row
        var lastWire = new List<int>();    // row index of each leg's last chunk wire row

        for (var li = 0; li < legs.Count; li++)
        {
            var chunks = legs[li];
            var aboveWire = chunks[0].Wire;
            // A blank row keeps the next leg's descriptions from running into the previous leg.
            if (li > 0 && aboveWire > 0) rows.Add((new string(' ', inner), ' ', ' ', false));
            for (var ci = 0; ci < chunks.Count; ci++)
            {
                var c = chunks[ci];
                for (var r = 0; r < c.Lines.Count; r++)
                {
                    if (r == c.Wire)
                    {
                        if (ci == 0) firstWire.Add(rows.Count);
                        if (ci == chunks.Count - 1) lastWire.Add(rows.Count);
                        rows.Add((c.Lines[r].PadRight(inner, '─'),
                                  ci == 0 ? '─' : '…', ci == chunks.Count - 1 ? '─' : '…', true));
                    }
                    else
                    {
                        rows.Add((c.Lines[r].PadRight(inner), ' ', ' ', false));
                    }
                }
            }
        }

        var lines = new List<string>(rows.Count);
        var multi = legs.Count > 1;
        for (var r = 0; r < rows.Count; r++)
        {
            var (body, lm, rm, isWire) = rows[r];
            var left = ' ';
            var right = ' ';
            if (multi)
            {
                var ls = firstWire[0];
                var le = firstWire[^1];
                if (r >= ls && r <= le) left = r == ls ? '┬' : r == le ? '└' : firstWire.Contains(r) ? '├' : '│';
                var rs = lastWire[0];
                var re = lastWire[^1];
                if (r >= rs && r <= re) right = r == rs ? '┬' : r == re ? '┘' : lastWire.Contains(r) ? '┤' : '│';
            }
            else if (isWire)
            {
                left = '─';
                right = '─';
            }
            lines.Add($"{left}{lm}{body}{rm}{right}");
        }
        return new Cell(lines, firstWire[0]);
    }

    // ── Instructions ─────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["XIC"] = "┤ ├", ["XIO"] = "┤/├",
        ["OTE"] = "( )", ["OTL"] = "(L)", ["OTU"] = "(U)",
        ["ONS"] = "[ONS]", ["OSR"] = "[OSR]", ["OSF"] = "[OSF]",
    };

    /// <summary>
    /// Operand labels keyed by canonical mnemonic (<see cref="InstructionMnemonics.Canonical"/>), so the pre-v36
    /// and v36+ spellings (<c>MOV</c>/<c>MOVE</c>, <c>SQR</c>/<c>SQRT</c>, <c>LES</c>/<c>LT</c>, ...) label alike.
    /// </summary>
    private static readonly Dictionary<string, string[]> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TON"] = new[] { "Timer", "Preset", "Accum" },
        ["TOF"] = new[] { "Timer", "Preset", "Accum" },
        ["RTO"] = new[] { "Timer", "Preset", "Accum" },
        ["CTU"] = new[] { "Counter", "Preset", "Accum" },
        ["CTD"] = new[] { "Counter", "Preset", "Accum" },
        ["RES"] = new[] { "Structure" },
        ["CLR"] = new[] { "Dest" },
        ["MOVE"] = new[] { "Source", "Dest" },
        ["MVM"] = new[] { "Source", "Mask", "Dest" },
        ["NEG"] = new[] { "Source", "Dest" },
        ["ABS"] = new[] { "Source", "Dest" },
        ["NOT"] = new[] { "Source", "Dest" },
        ["TRUNC"] = new[] { "Source", "Dest" },
        ["ASIN"] = new[] { "Source", "Dest" },
        ["ACOS"] = new[] { "Source", "Dest" },
        ["ATAN"] = new[] { "Source", "Dest" },
        ["TO_BCD"] = new[] { "Source", "Dest" },
        ["BCD_TO"] = new[] { "Source", "Dest" },
        ["EXPT"] = new[] { "Source A", "Source B", "Dest" },
        ["SQRT"] = new[] { "Source", "Dest" },
        ["COP"] = new[] { "Source", "Dest", "Length" },
        ["CPY"] = new[] { "Source", "Dest", "Length" },
        ["FLL"] = new[] { "Source", "Dest", "Length" },
        ["CPT"] = new[] { "Dest", "Expression" },
        ["CMP"] = new[] { "Expression" },
        ["ADD"] = new[] { "Source A", "Source B", "Dest" },
        ["SUB"] = new[] { "Source A", "Source B", "Dest" },
        ["MUL"] = new[] { "Source A", "Source B", "Dest" },
        ["DIV"] = new[] { "Source A", "Source B", "Dest" },
        ["MOD"] = new[] { "Source A", "Source B", "Dest" },
        ["AND"] = new[] { "Source A", "Source B", "Dest" },
        ["OR"] = new[] { "Source A", "Source B", "Dest" },
        ["XOR"] = new[] { "Source A", "Source B", "Dest" },
        ["EQ"] = new[] { "Source A", "Source B" },
        ["NE"] = new[] { "Source A", "Source B" },
        ["LT"] = new[] { "Source A", "Source B" },
        ["LE"] = new[] { "Source A", "Source B" },
        ["GT"] = new[] { "Source A", "Source B" },
        ["GE"] = new[] { "Source A", "Source B" },
        ["LIMIT"] = new[] { "Low Limit", "Test", "High Limit" },
        ["JSR"] = new[] { "Routine" },
        ["MSG"] = new[] { "Message Control" },
        ["GSV"] = new[] { "Class", "Instance", "Attribute", "Dest" },
        ["SSV"] = new[] { "Class", "Instance", "Attribute", "Source" },
    };

    private static readonly Regex TagReference = new(
        @"^\\?(Program:)?[A-Za-z_][A-Za-z0-9_:]*(\.[A-Za-z0-9_]+|\[[^\]]*\])*$", RegexOptions.Compiled);

    private static bool IsTagReference(string operand) => TagReference.IsMatch(operand);

    private static Cell Instruction(InstructionNode node, IRungContext ctx)
    {
        if (Symbols.TryGetValue(node.Key, out var symbol)) return SymbolCell(node, symbol, ctx);
        return BoxCell(node, ctx);
    }

    /// <summary>A contact, coil or one-shot: description lines, tag name lines, then the symbol on the wire row.</summary>
    private static Cell SymbolCell(InstructionNode node, string symbol, IRungContext ctx)
    {
        var operands = node.Args.Where(a => a.Length > 0).ToList();
        var nameWidth = Math.Max(symbol.Length + 4, Math.Min(MaxNameColumn, operands.Select(o => o.Length).DefaultIfEmpty(0).Max()));
        var nameLines = operands.SelectMany(o => Wrap(o, nameWidth)).ToList();

        var descriptions = operands.Where(IsTagReference).SelectMany(ctx.Describe).ToList();
        var descWidth = descriptions.Count == 0 ? nameWidth : Math.Max(nameWidth, MinDescColumn);
        var descLines = descriptions.SelectMany(d => Wrap(d, descWidth)).ToList();

        var width = Math.Max(nameWidth, descLines.Select(l => l.Length).DefaultIfEmpty(0).Max());
        var lines = new List<string>();
        lines.AddRange(descLines.Select(l => Center(l, width)));
        lines.AddRange(nameLines.Select(l => Center(l, width)));
        var left = (width - symbol.Length) / 2;
        lines.Add(new string('─', left) + symbol + new string('─', width - symbol.Length - left));
        return new Cell(lines, lines.Count - 1);
    }

    /// <summary>
    /// Any other instruction (timers, MOV/COP/CPT, compares, AOI calls, unknown mnemonics) as a labelled
    /// box. The wire enters and leaves on the title row; descriptions of its tag operands sit above.
    /// </summary>
    private static Cell BoxCell(InstructionNode node, IRungContext ctx)
    {
        var aoi = ctx.AoiLabels(node.Key);
        var title = aoi is null ? node.Key : node.Key + " (AOI)";
        string[] known = Labels.TryGetValue(InstructionMnemonics.Canonical(node.Key), out var k) ? k : Array.Empty<string>();

        var labels = new List<string>();
        for (var i = 0; i < node.Args.Count; i++)
        {
            string label;
            if (aoi is not null) label = i < aoi.Count ? aoi[i] : $"Arg {i + 1}";
            else if (node.Key.Equals("JSR", StringComparison.OrdinalIgnoreCase) && i > 0) label = $"Input {i}";
            else label = i < known.Length ? known[i] : $"Arg {i + 1}";
            labels.Add(label);
        }

        var labelWidth = labels.Select(l => l.Length).DefaultIfEmpty(0).Max();
        var valueWidest = node.Args.Select(a => a.Length).DefaultIfEmpty(0).Max();
        var inner = Math.Min(MaxBoxInner, Math.Max(title.Length, labelWidth + 1 + valueWidest));
        inner = Math.Max(inner, labelWidth + 1 + 8);
        var valueColumn = inner - labelWidth - 1;

        var body = new List<string>();
        for (var i = 0; i < node.Args.Count; i++)
        {
            var parts = Wrap(node.Args[i].Length == 0 ? "(none)" : node.Args[i], valueColumn);
            for (var p = 0; p < parts.Count; p++)
                body.Add((p == 0 ? labels[i].PadRight(labelWidth) : new string(' ', labelWidth)) + " " + parts[p]);
        }

        var boxWidth = inner + 4;
        var lines = new List<string>();

        // Descriptions of tag operands, above the box.
        var described = new List<string>();
        for (var i = 0; i < node.Args.Count; i++)
        {
            if (!IsTagReference(node.Args[i])) continue;
            foreach (var d in ctx.Describe(node.Args[i])) described.Add($"{node.Args[i]}: {d}");
        }
        foreach (var d in described) lines.AddRange(Wrap(d, boxWidth).Select(l => l.PadRight(boxWidth)));

        lines.Add("┌" + new string('─', boxWidth - 2) + "┐");
        var wire = lines.Count;
        lines.Add("┤ " + title.PadRight(inner) + " ├");
        foreach (var b in body) lines.Add("│ " + b.PadRight(inner) + " │");
        lines.Add("└" + new string('─', boxWidth - 2) + "┘");
        return new Cell(lines, wire);
    }

    // ── Text helpers ─────────────────────────────────────────────────────────

    private static string Center(string text, int width)
    {
        var left = (width - text.Length) / 2;
        return new string(' ', left) + text + new string(' ', width - text.Length - left);
    }

    /// <summary>
    /// Word-wraps <paramref name="text"/> to <paramref name="width"/>. A single word longer than the
    /// width is split across rows; no character is ever dropped.
    /// </summary>
    internal static List<string> Wrap(string text, int width)
    {
        var rows = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word;
            while (w.Length > width)
            {
                if (current.Length > 0) { rows.Add(current.ToString()); current.Clear(); }
                rows.Add(w[..width]);
                w = w[width..];
            }
            if (current.Length == 0) current.Append(w);
            else if (current.Length + 1 + w.Length <= width) current.Append(' ').Append(w);
            else { rows.Add(current.ToString()); current.Clear(); current.Append(w); }
        }
        if (current.Length > 0) rows.Add(current.ToString());
        if (rows.Count == 0) rows.Add(string.Empty);
        return rows;
    }

    /// <summary>
    /// Splits one long line into rows of at most <paramref name="width"/> characters; every row but the
    /// last ends in <c>…</c> so a reader can see the line continues.
    /// </summary>
    internal static List<string> WrapMarked(string text, int width)
    {
        var rows = new List<string>();
        var rest = text;
        while (rest.Length > width)
        {
            var cut = rest.LastIndexOf(' ', width - 1);
            if (cut <= 0) cut = width - 1;
            rows.Add(rest[..cut].TrimEnd() + "…");
            rest = rest[cut..].TrimStart();
        }
        rows.Add(rest);
        return rows;
    }
}

// ── SVG ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Renders the diagram text as SVG. The layout is the text layout: every character keeps its grid cell.
/// Box-drawing characters become vector strokes, everything else becomes text. Only
/// <c>currentColor</c> (and <c>none</c>) is ever used as a colour, so the drawing follows the host theme.
/// </summary>
internal static class RungDiagramSvg
{
    private const int CellWidth = 8;
    private const int CellHeight = 18;
    private const int Padding = 8;

    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    // Which sides of a cell a box-drawing character reaches: Left, Right, Up, Down.
    private static readonly Dictionary<char, (bool L, bool R, bool U, bool D)> Glyphs = new()
    {
        ['─'] = (true, true, false, false), ['│'] = (false, false, true, true),
        ['┌'] = (false, true, false, true), ['┐'] = (true, false, false, true),
        ['└'] = (false, true, true, false), ['┘'] = (true, false, true, false),
        ['├'] = (false, true, true, true), ['┤'] = (true, false, true, true),
        ['┬'] = (true, true, false, true), ['┴'] = (true, true, true, false),
        ['┼'] = (true, true, true, true),
        ['║'] = (false, false, true, true),
        ['╟'] = (false, true, true, true), ['╢'] = (true, false, true, true),
    };

    /// <summary>Builds the SVG document for <paramref name="lines"/>.</summary>
    /// <param name="lines">Diagram text rows.</param>
    /// <param name="title">Accessible title of the drawing.</param>
    public static string Render(IReadOnlyList<string> lines, string title)
    {
        var columns = Math.Max(1, lines.Select(l => l.Length).DefaultIfEmpty(0).Max());
        var width = columns * CellWidth + 2 * Padding;
        var height = Math.Max(1, lines.Count) * CellHeight + 2 * Padding;

        var path = new StringBuilder();
        var texts = new List<XElement>();

        for (var row = 0; row < lines.Count; row++)
        {
            var line = lines[row];
            var y0 = Padding + row * CellHeight;
            var cy = y0 + CellHeight / 2;
            var runStart = -1;
            for (var col = 0; col <= line.Length; col++)
            {
                var ch = col < line.Length ? line[col] : '\0';
                var isGlyph = Glyphs.ContainsKey(ch);
                var inRun = col < line.Length && !isGlyph;
                if (inRun && runStart < 0) runStart = col;
                if (!inRun && runStart >= 0)
                {
                    AddRun(texts, line, runStart, col, y0);
                    runStart = -1;
                }
                if (!isGlyph) continue;

                var x0 = Padding + col * CellWidth;
                var cx = x0 + CellWidth / 2;
                var (l, r, u, d) = Glyphs[ch];
                var double_ = ch is '║' or '╟' or '╢';
                var offsets = double_ ? new[] { -2, 2 } : new[] { 0 };
                foreach (var o in offsets)
                {
                    if (u) path.Append($"M{cx + o} {y0}V{cy}");
                    if (d) path.Append($"M{cx + o} {cy}V{y0 + CellHeight}");
                }
                if (l) path.Append($"M{x0} {cy}H{cx}");
                if (r) path.Append($"M{cx} {cy}H{x0 + CellWidth}");
            }
        }

        var svg = new XElement(Ns + "svg",
            new XAttribute("width", width),
            new XAttribute("height", height),
            new XAttribute("viewBox", $"0 0 {width} {height}"),
            new XAttribute("role", "img"),
            new XAttribute("font-family", "monospace"),
            new XAttribute("font-size", "13"),
            new XElement(Ns + "title", title),
            new XElement(Ns + "path",
                new XAttribute("d", path.Length == 0 ? "M0 0" : path.ToString()),
                new XAttribute("fill", "none"),
                new XAttribute("stroke", "currentColor"),
                new XAttribute("stroke-width", "1")),
            new XElement(Ns + "g",
                new XAttribute("fill", "currentColor"),
                new XAttribute("stroke", "none"),
                texts));
        return new XDocument(svg).ToString(SaveOptions.DisableFormatting);
    }

    private static void AddRun(List<XElement> texts, string line, int start, int end, int y0)
    {
        var text = line[start..end].TrimEnd();
        var lead = line[start..end].Length - line[start..end].TrimStart().Length;
        text = text.TrimStart();
        if (text.Length == 0) return;
        texts.Add(new XElement(Ns + "text",
            new XAttribute("x", Padding + (start + lead) * CellWidth),
            new XAttribute("y", y0 + (int)(CellHeight * 0.72)),
            new XAttribute("textLength", text.Length * CellWidth),
            new XAttribute("lengthAdjust", "spacingAndGlyphs"),
            new XAttribute(XNamespace.Xml + "space", "preserve"),
            text));
    }
}
