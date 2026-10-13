using System.Text;

/// <summary>
/// The one comparator for rung neutral text from the caller or the session's staged buffer against
/// rung text read back from a Logix export.
/// <para>
/// A routine export writes some instructions' operands as <c>?</c> because the value lives in a
/// structure tag, not in the rung: <c>TON(T,3000,0)</c> is exported as <c>TON(T,?,?)</c>. Every
/// comparison of expected text against exported text therefore goes through
/// <see cref="Matches"/>, <see cref="ContainsMatch"/> or <see cref="FindKey"/>, so the
/// rule is defined exactly once. Everything outside <see cref="PlaceholderOperands"/> still has to
/// match exactly (after <see cref="Normalize"/>).
/// </para>
/// <para>
/// Whitespace rule: <see cref="Normalize"/> removes all whitespace OUTSIDE operand text, that is
/// between <c>)</c> and the next mnemonic, around branch <c>[</c>, <c>,</c> and <c>]</c>, before
/// <c>;</c>, and at the ends of the text. Studio's export spaces branches as
/// <c>[XIC(a) XIO(b),XIC(c)]</c>, which therefore equals <c>[XIC(a)XIO(b),XIC(c)]</c>. Whitespace
/// INSIDE an instruction's parentheses is operand text and is kept exactly (string literals,
/// <c>CPT(x,a + b)</c> and CMP expressions); only line endings are unified to <c>\n</c> there.
/// </para>
/// </summary>
internal static class RungTextComparer
{
    /// <summary>
    /// Instructions whose exported operands can be <c>?</c>, with the zero-based operand indexes that
    /// may be exported that way.
    /// <para>
    /// Why: Logix exports a timer's or counter's preset and accumulator as <c>?</c> in the rung text
    /// (the values live in the tag's <c>.PRE</c> / <c>.ACC</c>), so <c>TON(T,3000,0)</c> reads back as
    /// <c>TON(T,?,?)</c>. For TON, TOF, RTO, CTU and CTD that is operands 2 and 3 (indexes 1 and 2).
    /// </para>
    /// <para>
    /// The <c>?</c> is a wildcard ONLY for an instruction and operand listed here, and only when it
    /// is in the exported text. <c>XIC(A)</c> does not match <c>XIC(?)</c>. Add an instruction only
    /// with evidence (an export captured from a controller); "Out of scope".
    /// </para>
    /// <para>
    /// Suspected but NOT added (no export evidence yet): instructions that keep their values in a
    /// control structure tag, such as FFL/FFU/LFL/LFU, FAL/FSC, SRT and SQI/SQO/SQL (length, position
    /// and mode operands).
    /// </para>
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlySet<int>> PlaceholderOperands =
        new Dictionary<string, IReadOnlySet<int>>(StringComparer.OrdinalIgnoreCase)
        {
            ["TON"] = new HashSet<int> { 1, 2 },
            ["TOF"] = new HashSet<int> { 1, 2 },
            ["RTO"] = new HashSet<int> { 1, 2 },
            ["CTU"] = new HashSet<int> { 1, 2 },
            ["CTD"] = new HashSet<int> { 1, 2 },
        };

    /// <summary>The placeholder an export writes for an operand held in a structure tag.</summary>
    internal const string Placeholder = "?";

    /// <summary>
    /// Normalises rung text for comparison: unifies line endings to <c>\n</c> and removes all whitespace
    /// outside operand text (between instructions, around branch <c>[</c> <c>,</c> <c>]</c>, and at the
    /// ends). Whitespace inside an instruction's parentheses is operand text and is preserved exactly.
    /// <c>null</c> becomes the empty string.
    /// </summary>
    /// <param name="text">The raw rung text, or <c>null</c>.</param>
    /// <returns>The normalised text.</returns>
    internal static string Normalize(string? text)
    {
        var src = (text ?? string.Empty).Replace("\r\n", "\n");
        var sb = new StringBuilder(src.Length);
        int depth = 0;
        foreach (char c in src)
        {
            if (depth == 0)
            {
                if (char.IsWhiteSpace(c))
                    continue;
                if (c == '(')
                    depth = 1;
            }
            else if (c == '(')
                depth++;
            else if (c == ')')
                depth--;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// True when <paramref name="expected"/> (caller or staged text) matches <paramref name="exported"/>
    /// (text read back from an export). Texts are equal after <see cref="Normalize"/>, or equal except
    /// that an operand which is <c>?</c> in <paramref name="exported"/> stands for any operand of the
    /// same position in <paramref name="expected"/>, for instructions in <see cref="PlaceholderOperands"/> only.
    /// </summary>
    /// <param name="expected">The text the caller or staged buffer holds.</param>
    /// <param name="exported">The text from the controller export.</param>
    /// <returns>Whether the two texts describe the same rung.</returns>
    internal static bool Matches(string? expected, string? exported)
    {
        var e = Normalize(expected);
        var x = Normalize(exported);
        if (string.Equals(e, x, StringComparison.Ordinal))
            return true;

        var et = Tokenize(e);
        var xt = Tokenize(x);
        if (et is null || xt is null || et.Count != xt.Count)
            return false;

        for (int i = 0; i < et.Count; i++)
            if (!TokenMatches(et[i], xt[i]))
                return false;
        return true;
    }

    /// <summary>True when any text in <paramref name="exportedTexts"/> matches <paramref name="expected"/>.</summary>
    /// <param name="exportedTexts">Texts read from an export.</param>
    /// <param name="expected">The expected text.</param>
    /// <returns>Whether a match exists.</returns>
    internal static bool ContainsMatch(IEnumerable<string> exportedTexts, string expected) =>
        exportedTexts.Any(x => Matches(expected, x));

    /// <summary>True when any text in <paramref name="expectedTexts"/> matches <paramref name="exported"/>.</summary>
    /// <param name="expectedTexts">Caller or staged texts.</param>
    /// <param name="exported">The exported text.</param>
    /// <returns>Whether a match exists.</returns>
    internal static bool AnyExpectedMatches(IEnumerable<string> expectedTexts, string exported) =>
        expectedTexts.Any(e => Matches(e, exported));

    /// <summary>
    /// Finds the key of <paramref name="budget"/> (expected texts mapped to a remaining count) that
    /// matches <paramref name="exported"/> and still has a positive count. An exact key wins over a
    /// <c>?</c>-tolerant one.
    /// </summary>
    /// <param name="budget">Expected text → remaining count.</param>
    /// <param name="exported">The exported text.</param>
    /// <returns>The matching key, or <c>null</c>.</returns>
    internal static string? FindKey(IReadOnlyDictionary<string, int> budget, string exported)
    {
        if (budget.TryGetValue(exported, out var exact) && exact > 0)
            return exported;
        foreach (var (key, remaining) in budget)
            if (remaining > 0 && Matches(key, exported))
                return key;
        return null;
    }

    /// <summary>
    /// Lists the instructions of <paramref name="text"/> (including those inside branches) in order,
    /// each with its mnemonic and operands. Returns an empty list when the text has unbalanced parentheses.
    /// Used to find the literal presets a rung carries.
    /// </summary>
    /// <param name="text">Rung neutral text.</param>
    /// <returns>Mnemonic and operand list per instruction.</returns>
    internal static IReadOnlyList<(string Mnemonic, IReadOnlyList<string> Operands)> Instructions(string? text)
    {
        var tokens = Tokenize(Normalize(text));
        if (tokens is null)
            return Array.Empty<(string, IReadOnlyList<string>)>();
        return tokens.Where(t => t.Mnemonic is not null)
                     .Select(t => (t.Mnemonic!, t.Operands!))
                     .ToList();
    }

    // ── Tokenizer ─────────────────────────────────────────────────────────────

    /// <summary>One rung-text token: a literal character, or an instruction with its operands.</summary>
    private sealed record Token(string Literal, string? Mnemonic, IReadOnlyList<string>? Operands);

    /// <summary>
    /// True when an operand is a literal value (number in any radix, signed or decimal, or a quoted
    /// string, or anything containing a quote) and so must be compared exactly. Every other operand is
    /// an identifier (tag name, member path, JSR routine name) or an expression of them, and Logix
    /// treats those case-insensitively. The <c>?</c> placeholder is not an identifier, so it is exact.
    /// </summary>
    /// <param name="operand">One operand's text.</param>
    /// <returns>Whether the operand is compared with <see cref="StringComparison.Ordinal"/>.</returns>
    private static bool IsLiteralOperand(string operand)
    {
        if (operand.Length == 0 || operand == Placeholder)
            return true;
        if (operand.Contains('\'') || operand.Contains('"') || operand.Contains('$'))
            return true;
        char c = operand[0];
        if (char.IsDigit(c))
            return true;
        return (c == '-' || c == '+' || c == '.') && operand.Length > 1 && (char.IsDigit(operand[1]) || operand[1] == '.');
    }

    private static bool TokenMatches(Token expected, Token exported)
    {
        if (expected.Mnemonic is null || exported.Mnemonic is null)
            return expected.Mnemonic is null && exported.Mnemonic is null
                && string.Equals(expected.Literal, exported.Literal, StringComparison.Ordinal);

        // Logix normalises instruction and AOI name case (benchaoi is stored as BenchAoi), so mnemonics
        // compare case-insensitively like identifiers do.
        if (!string.Equals(expected.Mnemonic, exported.Mnemonic, StringComparison.OrdinalIgnoreCase)
            || expected.Operands!.Count != exported.Operands!.Count)
            return false;

        PlaceholderOperands.TryGetValue(exported.Mnemonic, out var wildcardIndexes);
        for (int i = 0; i < expected.Operands.Count; i++)
        {
            var comparison = IsLiteralOperand(expected.Operands[i]) || IsLiteralOperand(exported.Operands[i])
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            if (string.Equals(expected.Operands[i], exported.Operands[i], comparison))
                continue;
            if (wildcardIndexes is not null && wildcardIndexes.Contains(i)
                && string.Equals(exported.Operands[i], Placeholder, StringComparison.Ordinal))
                continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Splits rung text into literal characters (branch brackets, commas, semicolons, whitespace) and
    /// <c>MNEMONIC(op,op,...)</c> instructions. Operands split on top-level commas only, so commas inside
    /// array subscripts or nested parentheses stay in one operand. Returns <c>null</c> when parentheses
    /// are unbalanced, in which case no placeholder tolerance applies.
    /// </summary>
    private static List<Token>? Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                if (i < text.Length && text[i] == '(')
                {
                    var mnemonic = text[start..i];
                    var ops = ReadOperands(text, ref i);
                    if (ops is null)
                        return null;
                    tokens.Add(new Token(string.Empty, mnemonic, ops));
                }
                else
                {
                    for (int k = start; k < i; k++)
                        tokens.Add(new Token(text[k].ToString(), null, null));
                }
                continue;
            }
            tokens.Add(new Token(c.ToString(), null, null));
            i++;
        }
        return tokens;
    }

    /// <summary>Reads the operand list starting at the <c>(</c> at <paramref name="i"/>; leaves <paramref name="i"/> after the matching <c>)</c>.</summary>
    private static List<string>? ReadOperands(string text, ref int i)
    {
        var operands = new List<string>();
        var current = new StringBuilder();
        int paren = 0, bracket = 0;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(')
            {
                if (paren++ == 0) continue;
            }
            else if (c == ')')
            {
                if (--paren == 0)
                {
                    operands.Add(current.ToString());
                    i++;
                    return operands;
                }
            }
            else if (c == '[') bracket++;
            else if (c == ']') bracket--;
            else if (c == ',' && paren == 1 && bracket == 0)
            {
                operands.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        return null;
    }
}
