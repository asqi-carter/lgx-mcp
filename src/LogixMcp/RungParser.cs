using L5Sharp.Core;

/// <summary>
/// A tag operand of a rung instruction, with a cross-program qualifier kept apart from the tag.
/// <c>\P2.Src</c> (also written <c>\Program:P2.Src</c> or <c>Program:P2.Src</c>) has
/// <see cref="Program"/> <c>P2</c> and <see cref="Tag"/> <c>Src</c>; an unqualified operand has a
/// <c>null</c> <see cref="Program"/> and the operand text as <see cref="Tag"/>.
/// </summary>
/// <param name="Program">The program that owns the tag, or <c>null</c> when the operand is not program-qualified.</param>
/// <param name="Tag">The tag expression within that program (members and array indexes included), or the whole operand when unqualified.</param>
internal sealed record RungOperand(string? Program, string Tag)
{
    /// <summary>True when the operand names a tag in another program.</summary>
    public bool IsProgramQualified => Program is not null;

    /// <summary>The operand in rung-text form: <c>\Program.Tag</c> when qualified, otherwise the tag.</summary>
    public string Qualified => Program is null ? Tag : $"\\{Program}.{Tag}";

    /// <summary>
    /// Splits operand text into program and tag. Accepts <c>\Prog.Tag</c>, <c>\Program:Prog.Tag</c> and
    /// <c>Program:Prog.Tag</c>; anything else (including a qualifier with no tag after it) is returned
    /// whole as an unqualified tag.
    /// </summary>
    /// <param name="text">Operand text, e.g. <c>\P2.Src</c>.</param>
    /// <returns>The parsed operand.</returns>
    public static RungOperand Parse(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        var rest = value;
        var qualified = rest.StartsWith('\\');
        if (qualified) rest = rest[1..];
        if (rest.StartsWith(RungParser.ProgramScopePrefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = rest[RungParser.ProgramScopePrefix.Length..];
            qualified = true;
        }
        if (!qualified) return new RungOperand(null, value);

        var dot = rest.IndexOf('.');
        if (dot <= 0 || dot == rest.Length - 1) return new RungOperand(null, value);
        return new RungOperand(rest[..dot], rest[(dot + 1)..]);
    }
}

/// <summary>
/// A rung that could not be parsed, reported as <c>unparsedRungs</c> by every tool that reads rung logic.
/// The tool carries on with every other rung; this entry names the one it skipped.
/// </summary>
/// <param name="Program">Program containing the rung.</param>
/// <param name="Routine">Routine containing the rung.</param>
/// <param name="Rung">Zero-based rung number within the routine (<c>-1</c> when it could not be read).</param>
/// <param name="Error">First line of why the rung could not be parsed.</param>
public sealed record UnparsedRungEntry(
    [property: System.Text.Json.Serialization.JsonPropertyName("program")] string Program,
    [property: System.Text.Json.Serialization.JsonPropertyName("routine")] string Routine,
    [property: System.Text.Json.Serialization.JsonPropertyName("rung")]    int    Rung,
    [property: System.Text.Json.Serialization.JsonPropertyName("error")]   string Error)
{
    /// <summary>Raw rung text (not serialised), kept so analyses can tell whether the rung mentions a tag.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Text { get; init; }

    /// <summary>The rung's location as <c>program/routine/rung</c>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Location => $"{Program}/{Routine}/{Rung}";

    /// <summary>
    /// True when the rung's raw text names <paramref name="tag"/> as a whole identifier (case-insensitive),
    /// so the rung may read or write it even though it could not be parsed.
    /// </summary>
    /// <param name="tag">Tag name; any member or index suffix is ignored.</param>
    /// <returns><c>true</c> when the rung text mentions the tag.</returns>
    public bool Mentions(string tag)
    {
        var name = WriteClassifier.ExtractBaseName(RungOperand.Parse(tag).Tag);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(Text)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(
            Text, $@"(?<![A-Za-z0-9_]){System.Text.RegularExpressions.Regex.Escape(name)}(?![A-Za-z0-9_])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}

/// <summary>
/// The one place that turns rung neutral text into L5Sharp instructions and tokens. L5Sharp's
/// tokenizer rejects the <c>\</c> that introduces a cross-program reference (<c>\Program.Tag</c>) and the
/// spaces / <c>%</c> of motion-instruction enumerations, so every direct use of <c>new Rung(...)</c>,
/// <c>Rung.Instructions()</c> or <c>NeutralText.Tokenize()</c> elsewhere would either throw on those rungs
/// or repeat ad hoc workarounds. Nothing outside this class calls them.
/// </summary>
internal static class RungParser
{
    /// <summary>Prefix of a program-scoped tag in the form L5Sharp accepts (<c>Program:Prog.Tag</c>).</summary>
    internal const string ProgramScopePrefix = "Program:";

    /// <summary>
    /// Parses the instructions of rung text, tolerating <c>\Program.Tag</c> operands and enumerated
    /// motion operands.
    /// </summary>
    /// <param name="text">Rung neutral text, e.g. <c>MOVE(\P2.Src,Dst);</c>.</param>
    /// <returns>The instructions, or <c>null</c> when the text cannot be parsed in any accepted form.</returns>
    public static IReadOnlyList<Instruction>? TryParse(string text) => TryParse(text, out _);

    /// <summary>
    /// <see cref="TryParse(string)"/> that also says why parsing failed.
    /// </summary>
    /// <param name="text">Rung neutral text.</param>
    /// <param name="error">First line of the parser's complaint about the original text; <c>null</c> on success.</param>
    /// <returns>The instructions, or <c>null</c> when the text cannot be parsed in any accepted form.</returns>
    public static IReadOnlyList<Instruction>? TryParse(string text, out string? error)
    {
        error = ValidateText(text);
        if (error is not null) return null;
        try { return InstructionsOf(new Rung(text, "")); }
        catch (ArgumentException ex) { error = FirstLine(ex); }

        var rewritten = System.Text.RegularExpressions.Regex.Replace(
            text, @"\\(?:Program:)?(?=[A-Za-z_])", ProgramScopePrefix);
        if (rewritten != text)
        {
            try { var r = InstructionsOf(new Rung(rewritten, "")); error = null; return r; }
            catch (ArgumentException) { }
        }

        // Enumerated operands (motion instructions: "Units per sec2", "% of Time") carry spaces
        // and '%', which L5Sharp's tokenizer rejects. Rewrite them to plain identifiers and retry.
        var sanitized = SanitizeEnumeratedOperands(rewritten);
        if (sanitized == rewritten) return null;
        try { var r = InstructionsOf(new Rung(sanitized, "")); error = null; return r; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Same as <see cref="TryParse(string)"/> for an L5Sharp rung (its text is re-parsed when L5Sharp's own parse fails).</summary>
    /// <param name="rung">The rung to parse.</param>
    /// <returns>The instructions, or <c>null</c> when the rung cannot be parsed in any accepted form.</returns>
    public static IReadOnlyList<Instruction>? TryParse(Rung rung) => TryParse(rung, out _);

    /// <summary>
    /// <see cref="TryParse(Rung)"/> that also says why parsing failed.
    /// </summary>
    /// <param name="rung">The rung to parse.</param>
    /// <param name="error">First line of why the rung cannot be parsed; <c>null</c> on success.</param>
    /// <returns>The instructions, or <c>null</c> when the rung cannot be parsed in any accepted form.</returns>
    public static IReadOnlyList<Instruction>? TryParse(Rung rung, out string? error)
    {
        error = null;
        IReadOnlyList<Instruction>? direct = null;
        try { direct = InstructionsOf(rung); }
        catch (ArgumentException) { }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            error = FirstLine(ex);
            return null;
        }

        string text;
        try { text = rung.Text.ToString(); }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            error = FirstLine(ex);
            return null;
        }

        // L5Sharp silently drops whatever follows a malformed tail (e.g. "XIC(a)OTE(b;" yields only XIC(a)),
        // so a successful parse is not enough: the text itself must be well formed.
        error = ValidateText(text);
        if (error is not null) return null;
        return direct ?? TryParse(text, out error);
    }

    /// <summary>
    /// Parses a rung for a tool that must carry on past a rung it cannot read. A rung that cannot be
    /// parsed (or read at all) yields no instructions and is added to <paramref name="unparsed"/>; it never throws.
    /// </summary>
    /// <param name="rung">The rung to parse.</param>
    /// <param name="program">Name of the program that contains the rung (for the report).</param>
    /// <param name="routine">Name of the routine that contains the rung (for the report).</param>
    /// <param name="unparsed">Receives an <see cref="UnparsedRungEntry"/> when the rung fails.</param>
    /// <returns>The rung's instructions; empty when it could not be parsed.</returns>
    public static IReadOnlyList<Instruction> ParseContained(
        Rung rung, string program, string routine, ICollection<UnparsedRungEntry> unparsed)
    {
        var parsed = TryParse(rung, out var error);
        if (parsed is not null) return parsed;

        var number = -1;
        try { number = rung.Number; } catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { }
        string? text = null;
        try { text = rung.Text?.ToString(); } catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { }
        unparsed.Add(new UnparsedRungEntry(program, routine, number, error ?? "rung text could not be parsed") { Text = text });
        return Array.Empty<Instruction>();
    }

    /// <summary>
    /// Builds the note for a tag that no parsed rung references but that unparsed rungs name, or
    /// <c>null</c> when it has parsed references or no unparsed rung mentions it.
    /// </summary>
    /// <param name="tag">Tag name as the caller asked for it.</param>
    /// <param name="hasParsedReference">Whether any parsed rung references the tag.</param>
    /// <param name="unparsed">Rungs that could not be parsed.</param>
    /// <returns>The note text, or <c>null</c>.</returns>
    internal static string? OnlyInUnparsedNote(
        string tag, bool hasParsedReference, IReadOnlyCollection<UnparsedRungEntry> unparsed)
    {
        if (hasParsedReference || unparsed.Count == 0) return null;
        var where = unparsed.Where(u => u.Mentions(tag)).Select(u => u.Location).ToList();
        return where.Count == 0
            ? null
            : $"Tag '{tag}' appears only in rungs that could not be parsed ({string.Join(", ", where)}); " +
              "its references and writers are unknown. See unparsedRungs.";
    }

    private static readonly System.Text.RegularExpressions.Regex ProgramQualifier =
        new(@"\G\\(?:Program:)?[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Checks the rung text for damage L5Sharp tolerates by silently dropping the rest of the rung:
    /// unbalanced parentheses or brackets and a malformed <c>\Program.</c> qualifier (a space or missing
    /// name or dot). Quoted text is ignored.
    /// </summary>
    /// <param name="text">Rung neutral text.</param>
    /// <returns>First line of what is wrong, or <c>null</c> when the text is well formed.</returns>
    internal static string? ValidateText(string text)
    {
        var parens = 0;
        var brackets = 0;
        char quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == '$') i++;                       // $' and $$ escapes inside a string literal
                else if (c == quote) quote = '\0';
                continue;
            }
            switch (c)
            {
                case '\'' or '"': quote = c; break;
                case '(': parens++; break;
                case ')':
                    if (--parens < 0) return $"unbalanced parentheses: unexpected ')' at position {i}";
                    break;
                case '[': brackets++; break;
                case ']':
                    if (--brackets < 0) return $"unbalanced brackets: unexpected ']' at position {i}";
                    break;
                case '\\':
                    if (!ProgramQualifier.IsMatch(text, i))
                        return $"invalid program qualifier at position {i}: expected \\Program.Tag with no spaces";
                    break;
            }
        }
        if (parens > 0) return "unbalanced parentheses: missing ')'";
        if (brackets > 0) return "unbalanced brackets: missing ']'";
        return null;
    }

    private static string FirstLine(Exception ex)
    {
        var message = (ex.Message ?? string.Empty).Split('\n', 2)[0].Trim();
        return message.Length > 0 ? message : ex.GetType().Name;
    }

    /// <summary>
    /// Parses the instructions of an L5Sharp rung, throwing when it cannot be parsed. Use where a bad
    /// rung must surface (the caller reports it); use <see cref="TryParse(Rung)"/> where it is skipped.
    /// </summary>
    /// <param name="rung">The rung to parse.</param>
    /// <returns>The instructions.</returns>
    /// <exception cref="ArgumentException">The rung text cannot be parsed in any accepted form.</exception>
    public static IReadOnlyList<Instruction> Parse(Rung rung) =>
        TryParse(rung) ?? throw new ArgumentException($"Rung text could not be parsed: {rung.Text}", nameof(rung));

    /// <summary>
    /// Tokenises rung text. The <c>\</c> of a cross-program reference is masked with a same-length
    /// character so every token index stays valid against the original text.
    /// </summary>
    /// <param name="text">Rung neutral text.</param>
    /// <returns>The tokens, ending in an EOF token.</returns>
    /// <exception cref="ArgumentException">The text cannot be tokenised.</exception>
    public static IReadOnlyList<NeutralToken> Tokenize(string text) =>
        new NeutralText(text.Replace('\\', '_')).Tokenize().ToList();

    /// <summary>
    /// The operands of an instruction with any program qualifier kept apart from the tag.
    /// </summary>
    /// <param name="instruction">An instruction returned by this parser.</param>
    /// <returns>One operand per argument, in order.</returns>
    public static IReadOnlyList<RungOperand> Operands(Instruction instruction) =>
        instruction.Arguments.Select(a => RungOperand.Parse(a.ToString())).ToList();

    private static List<Instruction> InstructionsOf(Rung rung) => rung.Instructions().ToList();

    /// <summary>Instructions whose operands are expressions, where spaces are legitimate operators.</summary>
    private static readonly HashSet<string> ExpressionInstructions =
        new(StringComparer.OrdinalIgnoreCase) { "CMP", "CPT" };

    /// <summary>An operand that is an enumeration keyword: words / '%' / digits separated by spaces, never a tag.</summary>
    private static readonly System.Text.RegularExpressions.Regex EnumeratedOperand =
        new(@"^[A-Za-z%][A-Za-z0-9%./]*(?: [A-Za-z0-9%./]+)*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Rewrites enumerated operands of rung neutral text (<c>Units per sec2</c>, <c>% of Time</c>, as
    /// MAS/MAJ/MAM/MAH/MCD/MAG and similar motion instructions take) into identifiers
    /// (<c>Units_per_sec2</c>, <c>Pct_of_Time</c>) that L5Sharp's tokenizer accepts. Operand positions
    /// are unchanged. Only a top-level operand that contains a space or '%' and matches
    /// <see cref="EnumeratedOperand"/> is rewritten; <c>CMP</c>/<c>CPT</c> expressions are left alone.
    /// </summary>
    /// <param name="text">Rung neutral text.</param>
    /// <returns>The rewritten text, or <paramref name="text"/> when nothing needed rewriting.</returns>
    internal static string SanitizeEnumeratedOperands(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var parenDepth = 0;
        var bracketDepth = 0;           // '[' inside an operand list (array index)
        var skipExpression = false;
        var segStart = -1;              // start of the current top-level operand in `text`
        var changed = false;

        void Flush(int end)
        {
            if (segStart < 0) return;
            var seg = text[segStart..end];
            var trimmed = seg.Trim();
            if (!skipExpression && (trimmed.Contains(' ') || trimmed.Contains('%'))
                && EnumeratedOperand.IsMatch(trimmed))
            {
                var lead = seg[..(seg.Length - seg.TrimStart().Length)];
                var trail = seg[seg.TrimEnd().Length..];
                sb.Append(lead)
                  .Append(trimmed.Replace("%", "Pct").Replace(' ', '_'))
                  .Append(trail);
                changed = true;
            }
            else sb.Append(seg);
            segStart = -1;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (parenDepth == 0)
            {
                sb.Append(c);
                if (c == '(')
                {
                    parenDepth = 1;
                    var j = i - 1;
                    while (j >= 0 && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j--;
                    skipExpression = ExpressionInstructions.Contains(text[(j + 1)..i]);
                    segStart = i + 1;
                }
                continue;
            }

            if (c == '[') bracketDepth++;
            else if (c == ']' && bracketDepth > 0) bracketDepth--;

            if (bracketDepth == 0 && c == '(') { parenDepth++; continue; }
            if (bracketDepth == 0 && c == ')')
            {
                parenDepth--;
                if (parenDepth == 0) { Flush(i); sb.Append(c); }
                continue;
            }
            if (bracketDepth == 0 && parenDepth == 1 && c == ',')
            {
                Flush(i);
                sb.Append(c);
                segStart = i + 1;
            }
        }
        if (segStart >= 0) sb.Append(text[segStart..]);   // unbalanced text: copy the rest verbatim
        return changed ? sb.ToString() : text;
    }
}
