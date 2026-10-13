// ── MnemonicSetChecker — pre-publish mnemonic check for logix_propose_rung_edit ──
//
// Logix Designer v36 renamed 16 relay-ladder mnemonics to IEC names (see InstructionMnemonics) and does not
// alias the old spellings on import. Before anything is imported or staged, rung text is scanned here and
// refused when it uses the other set's spelling, or an instruction the target revision dropped.
// The scan is a tokenizer, not a substring search: a name counts only in instruction position (depth 0,
// directly followed by '(') or as a function call inside a CMP/CPT/FAL/FSC expression. Tag and AOI names
// that merely contain a mnemonic (MOV_Count, LES_Timer) are different tokens and never match.
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;

/// <summary>The <c>Kind</c> values the mnemonic check reports.</summary>
public static class MnemonicCheckKinds
{
    /// <summary>A rung uses an instruction or expression function from the other mnemonic set.</summary>
    public const string WrongMnemonicSet = "WrongMnemonicSet";

    /// <summary>A rung uses something the target revision no longer supports (the ASCII serial instructions).</summary>
    public const string UnsupportedOnRevision = "UnsupportedOnRevision";
}

/// <summary>One offending rung.</summary>
/// <param name="RungIndex">Zero-based index of the rung in the submitted list.</param>
/// <param name="Found">The offending names as written (distinct, in order of appearance).</param>
/// <param name="Expected">What each should be on the target (same order as <paramref name="Found"/>); for an unsupported construct, what to use instead (empty when there is no replacement).</param>
/// <param name="TargetMajorRevision">The target's major revision, when known.</param>
/// <param name="SuggestedText">The rung with only those names replaced; <c>null</c> when there is no mechanical replacement, including when the same rung still holds an unsupported instruction.</param>
/// <param name="Kind">Which <see cref="MnemonicCheckKinds"/> value this finding is.</param>
/// <param name="Reason">Why no <paramref name="SuggestedText"/> is offered, when that is because the rung needs a manual rewrite; otherwise <c>null</c>.</param>
public sealed record MnemonicFinding(
    [property: JsonPropertyName("rungIndex")]           int RungIndex,
    [property: JsonPropertyName("found")]               IReadOnlyList<string> Found,
    [property: JsonPropertyName("expected")]            IReadOnlyList<string> Expected,
    [property: JsonPropertyName("targetMajorRevision")] int? TargetMajorRevision,
    [property: JsonPropertyName("suggestedText"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SuggestedText,
    [property: JsonPropertyName("kind")]                string Kind = MnemonicCheckKinds.WrongMnemonicSet,
    [property: JsonPropertyName("reason"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

/// <summary>Outcome of <see cref="MnemonicSetChecker.Check"/>.</summary>
/// <param name="Kind"><see cref="MnemonicCheckKinds.WrongMnemonicSet"/> when any finding is a wrong-set one, else <see cref="MnemonicCheckKinds.UnsupportedOnRevision"/>; <c>null</c> when nothing was refused. Each finding carries its own kind.</param>
/// <param name="Findings">One entry per kind of problem per offending rung (a rung can have a wrong-set and an unsupported finding); empty when nothing was refused.</param>
/// <param name="Message">Agent-facing refusal message; <c>null</c> when nothing was refused.</param>
/// <param name="Warning">Set (and nothing refused) when the target's mnemonic set is unknown.</param>
public sealed record MnemonicCheckResult(
    string? Kind,
    IReadOnlyList<MnemonicFinding> Findings,
    string? Message,
    string? Warning)
{
    /// <summary><c>true</c> when the rungs must be refused.</summary>
    public bool Refused => Kind is not null;

    /// <summary>Nothing to refuse and nothing to warn about.</summary>
    public static MnemonicCheckResult Pass { get; } = new(null, [], null, null);
}

/// <summary>Scans rung text for mnemonics the target revision does not accept.</summary>
public static class MnemonicSetChecker
{
    // NOTE (2026-10-08 SDK probe, as reported in task logix-mcp-vazn.6; not independently reproduced in this
    // repo, no probe log is checked in): CMP(dA=>dB) and CMP(dA=<dB) are reported to import, verify and go
    // live on v38.02, so the "=>" / "=<" UnsupportedOnRevision refusal from an earlier change was removed. The ASCII
    // serial instruction refusal (v37 and later) is unaffected.

    /// <summary>First major revision without the ASCII serial instructions.</summary>
    public const int FirstRevisionWithoutAscii = 37;

    private static readonly HashSet<string> ExpressionInstructions =
        new(["CMP", "CPT", "FAL", "FSC"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AsciiInstructions =
        new(["AWT", "AWA", "ARD", "ARL", "ABL", "ACB", "ACL", "AHL"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ExpressionFunctionNames = BuildExpressionFunctionNames();

    /// <summary>
    /// The six comparison mnemonics (canonical IEC key) and the operator each stands for inside a CMP/CPT
    /// expression. Written as a function call (<c>LES(a,b)</c>) they are not valid expression syntax in either set.
    /// </summary>
    private static readonly Dictionary<string, string> ComparisonOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EQ"] = "=", ["NE"] = "<>", ["LT"] = "<", ["LE"] = "<=", ["GT"] = ">", ["GE"] = ">=",
    };

    /// <summary>The operator for a comparison mnemonic in either spelling, or <c>null</c> when it is not one.</summary>
    private static string? ComparisonOperatorOf(string name) =>
        InstructionMnemonics.IsRenamed(name) &&
        ComparisonOperators.TryGetValue(InstructionMnemonics.Canonical(name), out var op) ? op : null;

    private static HashSet<string> BuildExpressionFunctionNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in InstructionMnemonics.ExpressionFunctionPairs)
        {
            set.Add(pair.Legacy);
            set.Add(pair.Iec);
        }
        return set;
    }

    /// <summary>
    /// Reads the rung texts of an L5X import fragment (<c>RLLContent/Rung/Text</c>). A fragment that does not
    /// parse yields none; the importer reports that failure itself.
    /// </summary>
    /// <param name="l5xFragment">The raw fragment, or <c>null</c>.</param>
    /// <param name="routineXPath">
    /// The target routine; only its rungs are read (never an Add-On Instruction's <c>Logic</c> or another
    /// routine's). When <c>null</c> or not found, every rung outside an AOI definition is read.
    /// </param>
    /// <returns>The rung texts in document order.</returns>
    public static IReadOnlyList<string> RungTextsFromFragment(string? l5xFragment, string? routineXPath = null)
    {
        if (string.IsNullOrWhiteSpace(l5xFragment)) return [];
        try
        {
            return RoutineRungSelector.Select(SafeXml.ParseDocument(l5xFragment), routineXPath)
                .Select(r => r.Element("Text")?.Value ?? string.Empty)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>Checks rung texts against the target revision.</summary>
    /// <param name="rungTexts">The rung texts as the caller submitted them.</param>
    /// <param name="target">The target revision; <c>null</c> is treated as unknown.</param>
    /// <param name="aoiNames">
    /// Lazily supplies the project's Add-On Instruction names, so an AOI that happens to be spelled like a
    /// mnemonic of the other set is not flagged. Only called when a name would otherwise be flagged.
    /// </param>
    /// <returns>The outcome; <see cref="MnemonicCheckResult.Refused"/> when anything must be refused.</returns>
    public static MnemonicCheckResult Check(
        IReadOnlyList<string> rungTexts,
        TargetRevisionInfo? target,
        Func<IReadOnlySet<string>>? aoiNames = null)
    {
        if (rungTexts.Count == 0) return MnemonicCheckResult.Pass;

        if (target?.Set is not { } set)
            return new MnemonicCheckResult(null, [], null,
                "The target's mnemonic set is unknown, so rung text was not checked against the legacy/IEC " +
                "instruction names (LES/LT, MOV/MOVE, ...). Connect or open a project with a known revision " +
                "to enable the check.");

        IReadOnlySet<string>? aois = null;
        bool IsAoi(string name)
        {
            // Logix normalises AOI name case, so match whatever comparer the supplied set was built with.
            aois ??= new HashSet<string>(aoiNames?.Invoke() ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
            return aois.Contains(name);
        }

        var wrongSet = new List<MnemonicFinding>();
        var unsupported = new List<MnemonicFinding>();
        var findings = new List<MnemonicFinding>();
        var withheld = 0;
        var compoundWithheld = 0;

        for (int i = 0; i < rungTexts.Count; i++)
        {
            var scan = Scan(rungTexts[i] ?? string.Empty);

            // Unsupported on this revision. Scanned on every rung, including rungs with a wrong-set name.
            var foundUnsupported = new List<string>();

            if (target.MajorRevision >= FirstRevisionWithoutAscii)
            {
                foreach (var n in scan.Names.Where(n => n.InstructionPosition && AsciiInstructions.Contains(n.Name)))
                {
                    if (!foundUnsupported.Contains(n.Name, StringComparer.OrdinalIgnoreCase)) foundUnsupported.Add(n.Name);
                }
            }

            // Wrong mnemonic set.
            var wrong = scan.Names
                .Where(n => InstructionMnemonics.SetOf(n.Name) is { } s && s != set)
                .Where(n => !n.InstructionPosition || !IsAoi(n.Name))
                .Where(n => n.InstructionPosition || ExpressionFunctionNames.Contains(n.Name))
                .ToList();

            // Comparison mnemonics used as functions inside CMP/CPT/FAL/FSC are refused in either set: the
            // expression syntax has operators (a<b), not LES(a,b) / LT(a,b).
            var comparisonCalls = scan.Names
                .Where(n => !n.InstructionPosition && ComparisonOperatorOf(n.Name) is not null)
                .ToList();

            if (wrong.Count > 0 || comparisonCalls.Count > 0)
            {
                var entries = wrong.Select(n => (n.Start, n.Name, Expected: InstructionMnemonics.For(set, n.Name)))
                    .Concat(comparisonCalls.Select(n => (n.Start, n.Name, Expected: ComparisonOperatorOf(n.Name)!)))
                    .OrderBy(e => e.Start)
                    .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(e => e.Start)
                    .ToList();
                var found = entries.Select(e => e.Name).ToList();
                var expected = entries.Select(e => e.Expected).ToList();
                // Renaming alone cannot make this rung acceptable when an unsupported instruction remains,
                // so no suggestedText is offered for it.
                // Logix also rejects a CMP whose expression is anything but one bare comparison
                // (RxCMP_E_OPERAND_INVALIDEXPR), so a compound CMP gets no suggestion either.
                var compound = foundUnsupported.Count == 0 && HasCompoundComparison(rungTexts[i]);
                string? suggested = foundUnsupported.Count > 0 || compound
                    ? null
                    : RewriteComparisonCalls(
                        Replace(rungTexts[i], wrong.Select(n => (n.Start, n.Length, InstructionMnemonics.For(set, n.Name)))));
                if (suggested is null) withheld++;
                if (compound) compoundWithheld++;
                var finding = new MnemonicFinding(i, found, expected, target.MajorRevision, suggested,
                    MnemonicCheckKinds.WrongMnemonicSet, compound ? CompoundCmpReason : null);
                wrongSet.Add(finding);
                findings.Add(finding);
            }

            if (foundUnsupported.Count > 0)
            {
                var finding = new MnemonicFinding(i, foundUnsupported, [], target.MajorRevision, null,
                    MnemonicCheckKinds.UnsupportedOnRevision);
                unsupported.Add(finding);
                findings.Add(finding);
            }
        }

        if (findings.Count == 0) return MnemonicCheckResult.Pass;

        const string AsciiNote = "The ASCII serial instructions (AWT, AWA, ARD, ARL, ABL, ACB, ACL, AHL) have " +
                                 "no replacement here, so remove them from the rung.";

        if (wrongSet.Count > 0)
        {
            var setName = set == MnemonicSet.Iec ? "IEC (v36+)" : "legacy (v35 and earlier)";
            var message =
                $"The target is revision {target.MajorRevision} and uses the {setName} instruction names; " +
                $"{wrongSet.Count} rung(s) use the other set ({Describe(wrongSet)}). Nothing was imported, " +
                "staged or rewritten. ";
            if (unsupported.Count > 0)
                message +=
                    $"{unsupported.Count} rung(s) also use instructions this revision no longer supports " +
                    $"({Describe(unsupported)}). {AsciiNote} " +
                    (withheld > 0
                        ? $"No suggestedText is given for the {withheld - compoundWithheld} rung(s) that still contain an " +
                          "unsupported instruction, because a rename alone would not make them acceptable; " +
                          "remove the unsupported instruction and rename the wrong-set names yourself. "
                        : string.Empty);
            if (compoundWithheld > 0)
                message += $"No suggestedText is given for {compoundWithheld} rung(s) with a compound CMP " +
                           $"expression: {CompoundCmpReason} ";
            if (wrongSet.Count > withheld)
                message += "Resend each rung that has a suggestedText with it (only the names were replaced) " +
                           "in the same call; the text you send is the text that lands.";
            return new MnemonicCheckResult(MnemonicCheckKinds.WrongMnemonicSet, findings, message.TrimEnd(), null);
        }

        return new MnemonicCheckResult(MnemonicCheckKinds.UnsupportedOnRevision, findings,
            $"The target is revision {target.MajorRevision}, which no longer supports " +
            $"{Describe(unsupported)} in {unsupported.Count} rung(s). Nothing was imported, staged or " +
            $"rewritten. {AsciiNote}",
            null);
    }

    private static string Describe(IReadOnlyList<MnemonicFinding> findings) =>
        string.Join(", ", findings.SelectMany(f => f.Found).Distinct(StringComparer.Ordinal));

    private static string Replace(string text, IEnumerable<(int Start, int Length, string With)> edits)
    {
        var sb = new StringBuilder(text);
        foreach (var (start, length, with) in edits.OrderByDescending(e => e.Start))
            sb.Remove(start, length).Insert(start, with);
        return sb.ToString();
    }

    private const string CompoundCmpReason =
        "Logix rejects a CMP expression that is not a single comparison (RxCMP_E_OPERAND_INVALIDEXPR), " +
        "e.g. CMP((a>b) AND (b<100)), CMP(NOT (a<b)) or CMP((a<b)=1); split it into one CMP per " +
        "comparison in series, or use CPT into a tag and test that.";

    /// <summary>Index of the bracket closing the one at <paramref name="open"/>, or -1 when unbalanced.</summary>
    private static int FindClose(string text, int open)
    {
        int depth = 0;
        for (int j = open; j < text.Length; j++)
        {
            char c = text[j];
            if (c is '\'' or '"')
            {
                int q = text.IndexOf(c, j + 1);
                if (q < 0) return -1;
                j = q;
            }
            else if (c is '(' or '[') depth++;
            else if (c is ')' or ']')
            {
                depth--;
                if (depth == 0) return j;
            }
        }
        return -1;
    }

    /// <summary>
    /// True when a CMP instruction holds comparison-mnemonic calls but is not exactly one such call
    /// (more than one comparison, or the call is only part of the expression, as in <c>NOT LES(a,b)</c>).
    /// </summary>
    private static bool HasCompoundComparison(string text)
    {
        var names = Scan(text).Names;
        foreach (var cmp in names.Where(n => n.InstructionPosition &&
                                             InstructionMnemonics.Canonical(n.Name).Equals("CMP", StringComparison.OrdinalIgnoreCase)))
        {
            int open = cmp.Start + cmp.Length;
            while (open < text.Length && text[open] != '(') open++;
            int close = open < text.Length ? FindClose(text, open) : -1;
            if (close < 0) continue;

            var calls = names.Where(n => !n.InstructionPosition && n.Start > open && n.Start < close &&
                                         ComparisonOperatorOf(n.Name) is not null).ToList();
            if (calls.Count == 0) continue;
            if (calls.Count > 1) return true;

            var call = calls[0];
            int callOpen = call.Start + call.Length;
            while (callOpen < text.Length && text[callOpen] != '(') callOpen++;
            int callClose = callOpen < text.Length ? FindClose(text, callOpen) : -1;
            if (callClose < 0) continue;
            if (!string.IsNullOrWhiteSpace(text[(open + 1)..call.Start]) ||
                !string.IsNullOrWhiteSpace(text[(callClose + 1)..close]))
                return true;
        }
        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex NeedsParens =
        new(@"[<>=]|\b(AND|OR|XOR)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Rewrites every comparison-mnemonic call inside an expression instruction to its operator form
    /// (<c>CMP(LES(a,b))</c> becomes <c>CMP(a&lt;b)</c>), rightmost call first so nested calls resolve.
    /// Returns <c>null</c> when a call cannot be rewritten mechanically (not exactly two arguments, unbalanced).
    /// </summary>
    private static string? RewriteComparisonCalls(string text)
    {
        for (int guard = 0; guard < 256; guard++)
        {
            var call = Scan(text).Names
                .Where(n => !n.InstructionPosition && ComparisonOperatorOf(n.Name) is not null)
                .OrderByDescending(n => n.Start)
                .Cast<NameToken?>()
                .FirstOrDefault();
            if (call is null) return text;

            var tok = call.Value;
            int open = tok.Start + tok.Length;
            while (open < text.Length && char.IsWhiteSpace(text[open])) open++;

            var args = new List<(int Start, int End)>();
            int depth = 0, argStart = open + 1, close = -1;
            for (int j = open; j < text.Length && close < 0; j++)
            {
                char c = text[j];
                if (c is '\'' or '"')
                {
                    int q = text.IndexOf(c, j + 1);
                    if (q < 0) return null;
                    j = q;
                }
                else if (c is '(' or '[') depth++;
                else if (c is ')' or ']')
                {
                    depth--;
                    if (depth == 0) { args.Add((argStart, j)); close = j; }
                }
                else if (c == ',' && depth == 1) { args.Add((argStart, j)); argStart = j + 1; }
            }
            if (close < 0 || args.Count != 2) return null;

            string Operand(int s, int e)
            {
                var t = text[s..e].Trim();
                return NeedsParens.IsMatch(t) ? $"({t})" : t;
            }

            int before = tok.Start - 1;
            while (before >= 0 && char.IsWhiteSpace(text[before])) before--;
            int after = close + 1;
            while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
            bool whole = before >= 0 && text[before] is '(' or ',' && after < text.Length && text[after] is ')' or ',';

            var rewritten = Operand(args[0].Start, args[0].End) + ComparisonOperatorOf(tok.Name) +
                            Operand(args[1].Start, args[1].End);
            if (!whole) rewritten = $"({rewritten})";
            text = text[..tok.Start] + rewritten + text[(close + 1)..];
        }
        return null;
    }

    // ── Tokenizer ────────────────────────────────────────────────────────────

    private readonly record struct NameToken(string Name, int Start, int Length, bool InstructionPosition);

    private sealed record ScanResult(List<NameToken> Names);

    private static bool IsWordStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Walks rung text once. Branch brackets exist only at paren depth 0; inside parentheses a bracket is an
    /// array index. A word followed by '(' at depth 0 is an instruction; inside CMP/CPT/FAL/FSC it is a
    /// function call; everywhere else it is an operand and is ignored. Quoted text is skipped.
    /// </summary>
    private static ScanResult Scan(string text)
    {
        var names = new List<NameToken>();
        int depth = 0;
        bool inExpressionInstruction = false;

        for (int i = 0; i < text.Length;)
        {
            char c = text[i];

            if (c is '\'' or '"')
            {
                int close = text.IndexOf(c, i + 1);
                i = close < 0 ? text.Length : close + 1;
                continue;
            }

            if (IsWordChar(c))
            {
                int start = i;
                while (i < text.Length && IsWordChar(text[i])) i++;

                // A digit-led word is a number; a word after '.', ':' or '\' is a member or scope part.
                if (!IsWordStart(text[start]) || start > 0 && text[start - 1] is '.' or ':' or '\\')
                    continue;

                int next = i;
                while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                bool isCall = next < text.Length && text[next] == '(';
                if (!isCall) continue;

                string word = text[start..i];
                if (depth == 0)
                {
                    names.Add(new NameToken(word, start, word.Length, InstructionPosition: true));
                    inExpressionInstruction = ExpressionInstructions.Contains(word);
                }
                else if (inExpressionInstruction)
                {
                    names.Add(new NameToken(word, start, word.Length, InstructionPosition: false));
                }
                continue;
            }

            if (c == '(') depth++;
            else if (c == ')')
            {
                if (depth > 0) depth--;
                if (depth == 0) inExpressionInstruction = false;
            }

            i++;
        }

        return new ScanResult(names);
    }
}
