/// <summary>Which spelling of the renamed Logix instruction mnemonics a controller revision uses.</summary>
public enum MnemonicSet
{
    /// <summary>Studio 5000 v35 and earlier: <c>LES</c>, <c>MOV</c>, <c>SQR</c> and so on.</summary>
    Legacy,

    /// <summary>Studio 5000 v36 and later (IEC 61131-3 names): <c>LT</c>, <c>MOVE</c>, <c>SQRT</c> and so on.</summary>
    Iec,
}

/// <summary>
/// The one catalogue of Logix instruction mnemonics that were renamed in v36.
/// <para>
/// Source: the Logix Designer v38 local help page "Updated instruction mnemonics", and the v36 and v37 release
/// notes. Logix Designer v36 and later renamed 16 relay-ladder mnemonics to IEC 61131-3 names. There is no
/// aliasing: content labelled v35 or earlier is converted on import, but content labelled v36+ that uses the old
/// names is not. Seven of the renames are also the function names used inside CMP/CPT/FAL/FSC expressions.
/// Structured Text is unaffected.
/// </para>
/// <para>
/// Every analyzer or table that switches on a mnemonic goes through this class: call <see cref="Canonical"/> on
/// the instruction key and switch on the result, so both spellings behave identically. The canonical key is the
/// upper-case IEC (v36+) name; mnemonics that were not renamed (<c>XIC</c>, <c>OTE</c>, <c>ADD</c>, ...) are their
/// own canonical key.
/// </para>
/// </summary>
public static class InstructionMnemonics
{
    /// <summary>One renamed mnemonic: its v35-and-earlier spelling and its v36+ spelling.</summary>
    /// <param name="Legacy">The v35-and-earlier spelling (upper case).</param>
    /// <param name="Iec">The v36+ spelling (upper case); also the canonical key.</param>
    public readonly record struct Pair(string Legacy, string Iec);

    /// <summary>The 16 renamed instruction mnemonics.</summary>
    public static IReadOnlyList<Pair> InstructionPairs { get; } =
    [
        new("ACS", "ACOS"),
        new("ASN", "ASIN"),
        new("ATN", "ATAN"),
        new("TOD", "TO_BCD"),
        new("FRD", "BCD_TO"),
        new("EQU", "EQ"),
        new("GRT", "GT"),
        new("GEQ", "GE"),
        new("LEQ", "LE"),
        new("LES", "LT"),
        new("NEQ", "NE"),
        new("LIM", "LIMIT"),
        new("MOV", "MOVE"),
        new("SQR", "SQRT"),
        new("TRN", "TRUNC"),
        new("XPY", "EXPT"),
    ];

    /// <summary>
    /// The 7 renamed functions that also appear inside CMP/CPT/FAL/FSC expressions (a subset of
    /// <see cref="InstructionPairs"/>).
    /// </summary>
    public static IReadOnlyList<Pair> ExpressionFunctionPairs { get; } =
    [
        new("SQR", "SQRT"),
        new("TRN", "TRUNC"),
        new("ACS", "ACOS"),
        new("ASN", "ASIN"),
        new("ATN", "ATAN"),
        new("FRD", "BCD_TO"),
        new("TOD", "TO_BCD"),
    ];

    private static readonly Dictionary<string, Pair> ByName = BuildIndex();

    private static Dictionary<string, Pair> BuildIndex()
    {
        var map = new Dictionary<string, Pair>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in InstructionPairs)
        {
            map[pair.Legacy] = pair;
            map[pair.Iec] = pair;
        }
        return map;
    }

    /// <summary>
    /// Maps either spelling of a renamed mnemonic to one key: the upper-case v36+ name. A name that was not
    /// renamed comes back upper-cased and otherwise unchanged.
    /// </summary>
    /// <param name="name">An instruction or expression-function name, in either spelling and any case.</param>
    /// <returns>The canonical key, or an empty string when <paramref name="name"/> is null or empty.</returns>
    public static string Canonical(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        return ByName.TryGetValue(name, out var pair) ? pair.Iec : name.ToUpperInvariant();
    }

    /// <summary>Returns the spelling of <paramref name="canonical"/> that <paramref name="set"/> uses.</summary>
    /// <param name="set">The mnemonic set of the target revision.</param>
    /// <param name="canonical">A canonical key (either spelling is accepted).</param>
    /// <returns>The mnemonic in that set's spelling, upper case.</returns>
    public static string For(MnemonicSet set, string canonical)
    {
        if (string.IsNullOrEmpty(canonical)) return string.Empty;
        if (!ByName.TryGetValue(canonical, out var pair)) return canonical.ToUpperInvariant();
        return set == MnemonicSet.Legacy ? pair.Legacy : pair.Iec;
    }

    /// <summary><c>true</c> when <paramref name="name"/> is one of the renamed mnemonics, in either spelling.</summary>
    /// <param name="name">An instruction or expression-function name.</param>
    public static bool IsRenamed(string? name) => !string.IsNullOrEmpty(name) && ByName.ContainsKey(name);

    /// <summary>
    /// The set a renamed mnemonic's spelling belongs to, or <c>null</c> when <paramref name="name"/> is not a
    /// renamed mnemonic (it is spelled the same in both sets).
    /// </summary>
    /// <param name="name">An instruction or expression-function name.</param>
    public static MnemonicSet? SetOf(string? name)
    {
        if (string.IsNullOrEmpty(name) || !ByName.TryGetValue(name, out var pair)) return null;
        return string.Equals(name, pair.Legacy, StringComparison.OrdinalIgnoreCase) ? MnemonicSet.Legacy : MnemonicSet.Iec;
    }

    /// <summary>
    /// Both spellings for agent-facing text, such as <c>"LT (LES before v36)"</c>. A mnemonic that was not renamed
    /// is returned as is.
    /// </summary>
    /// <param name="canonical">A canonical key (either spelling is accepted).</param>
    public static string Describe(string canonical)
    {
        var iec = For(MnemonicSet.Iec, canonical);
        var legacy = For(MnemonicSet.Legacy, canonical);
        return iec == legacy ? iec : $"{iec} ({legacy} before v36)";
    }
}
