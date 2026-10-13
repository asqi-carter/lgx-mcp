// ── ImportLogParser — turns captured SDK import callbacks into structured importLog entries ──
//
// The Logix Designer SDK streams the importer's log through the operation-event hook (Status and
// Error callbacks). Its entries look like
//   <Exception Severity="Error" Mnemonic="RxCMP_E_AUDIT_DEPRECATED_INSTRUCTION" HexCode="80042cae"><![CDATA[...]]></Exception>
// This parser extracts {severity, mnemonic, hex, text} from each one and, for the mnemonics the
// server knows, adds a plain hint (quoting the replacement Logix names, e.g. "Update instruction to LT.").
//
// Core assembly: no Rockwell types, so it is testable without the SDK.
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>One entry of the <c>importLog</c> array on a failed import.</summary>
/// <param name="Severity">The <c>Severity</c> attribute (e.g. <c>Error</c>, <c>Warning</c>); <c>null</c> for unstructured text.</param>
/// <param name="Mnemonic">The <c>Mnemonic</c> attribute (e.g. <c>RxCMP_E_AUDIT_DEPRECATED_INSTRUCTION</c>); <c>null</c> when absent.</param>
/// <param name="Hex">The <c>HexCode</c> attribute (e.g. <c>80042cae</c>); <c>null</c> when absent.</param>
/// <param name="Text">The element text (CDATA unwrapped), or the raw message for unstructured text.</param>
/// <param name="Hint">A plain-language hint for a mnemonic the server recognises; omitted otherwise.</param>
/// <param name="RungIndex">The rung the log names (<c>Rung N</c> or <c>&lt;Rung Number="N"</c>), or the default 0 for a single-rung fragment; omitted otherwise.</param>
public sealed record ImportLogEntry(
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("mnemonic")] string? Mnemonic,
    [property: JsonPropertyName("hex")]      string? Hex,
    [property: JsonPropertyName("text")]     string  Text,
    [property: JsonPropertyName("hint"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint = null,
    [property: JsonPropertyName("rungIndex"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RungIndex = null);

/// <summary>Parses captured import callbacks into <see cref="ImportLogEntry"/> values.</summary>
public static class ImportLogParser
{
    /// <summary>The importer refused an instruction that Logix has deprecated.</summary>
    public const string AuditDeprecatedInstruction = "RxCMP_E_AUDIT_DEPRECATED_INSTRUCTION";
    /// <summary>A deprecated instruction was used inside an expression.</summary>
    public const string DeprecatedInstrInExpression = "RxCMP_E_DEPRCATED_INSTR_IN_EXPRESSION";
    /// <summary>The instruction is not defined for this controller or revision.</summary>
    public const string AuditInstrNotDefined = "RxCMP_E_AUDIT_INSTRNOTDEFINED";

    private static readonly Regex ExceptionElement = new(
        @"<Exception\b(?<attrs>[^>]*?)(?:/>|>(?<body>.*?)</Exception\s*>|>(?<open>.*)$)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Attribute = new(
        @"(?<name>[A-Za-z_][\w.-]*)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CData = new(
        @"<!\[CDATA\[(?<t>.*?)\]\]>", RegexOptions.Singleline | RegexOptions.Compiled);

    // The "ImportLog" prefix the SDK puts on each streamed line, plus the whitespace after it.
    private static readonly Regex LogPrefix = new(
        @"^\s*ImportLog\b\s*", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // An XMLSrv_E_... / RxCMP_E_... mnemonic named in a plain exception or verify string (no <Exception> element).
    private static readonly Regex XmlSrvMnemonic = new(
        @"\b(?:XMLSrv|RxCMP)_E_\w+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // "Rung 3" in text, or <Rung Number="3" in an echoed fragment.
    private static readonly Regex RungReference = new(
        @"(?:\bRung\s+(?<n>\d+)|<Rung\b[^>]*?\bNumber\s*=\s*[""'](?<n>\d+)[""'])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // "Update instruction to LT." / "Update instruction to GRT or LES." — instruction names contain no dots.
    private static readonly Regex UpdateInstruction = new(
        @"Update\s+instruction\s+to\s+[^.\r\n]+\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Parses every captured callback. <paramref name="errorMessages"/> are always kept (structured when they carry an
    /// <c>&lt;Exception&gt;</c> element, otherwise as unstructured text); <paramref name="statusMessages"/> are kept only
    /// when they carry an <c>&lt;Exception&gt;</c> element, since the rest is progress chatter. Duplicates are dropped.
    /// </summary>
    /// <param name="errorMessages">Messages from the SDK's <c>Error</c> callback.</param>
    /// <param name="statusMessages">Messages from the SDK's <c>Status</c> callback.</param>
    /// <param name="fragmentRungCount">
    /// The number of rungs in the imported fragment, when known. An entry whose log names no rung gets
    /// <c>RungIndex</c> 0 when this is exactly 1.
    /// </param>
    /// <returns>The entries in order (errors first, then status); empty when nothing relevant was captured.</returns>
    public static IReadOnlyList<ImportLogEntry> Parse(
        IEnumerable<string> errorMessages, IEnumerable<string> statusMessages, int? fragmentRungCount = null)
    {
        var result = new List<ImportLogEntry>();
        foreach (var message in errorMessages)
            Add(result, message, keepUnstructured: true);
        foreach (var message in statusMessages)
            Add(result, message, keepUnstructured: false);

        if (fragmentRungCount == 1)
        {
            for (var i = 0; i < result.Count; i++)
            {
                if (result[i].RungIndex is null)
                    result[i] = result[i] with { RungIndex = 0 };
            }
        }

        return result;
    }

    /// <summary>The number of <c>&lt;Rung</c> elements in an import fragment.</summary>
    /// <param name="fragment">The fragment XML.</param>
    /// <returns>The rung count (0 when none).</returns>
    public static int CountRungs(string? fragment) =>
        string.IsNullOrEmpty(fragment)
            ? 0
            : Regex.Matches(fragment, @"<Rung(?=[\s/>])", RegexOptions.CultureInvariant).Count;

    /// <summary>The rung a log text names (<c>Rung N</c> or <c>&lt;Rung Number="N"</c>), or <c>null</c>.</summary>
    /// <param name="text">The entry text.</param>
    public static int? RungIndexFrom(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var m = RungReference.Match(text);
        return m.Success && int.TryParse(m.Groups["n"].Value, out var n) ? n : null;
    }

    /// <summary>Parses one callback message into zero or more entries.</summary>
    /// <param name="message">The raw message.</param>
    /// <returns>One entry per <c>&lt;Exception&gt;</c> element; empty when the message has none.</returns>
    public static IReadOnlyList<ImportLogEntry> ParseMessage(string? message)
    {
        var entries = new List<ImportLogEntry>();
        if (string.IsNullOrWhiteSpace(message))
            return entries;

        foreach (Match m in ExceptionElement.Matches(message))
        {
            string? severity = null, mnemonic = null, hex = null;
            foreach (Match a in Attribute.Matches(m.Groups["attrs"].Value))
            {
                var value = a.Groups["v"].Value;
                switch (a.Groups["name"].Value.ToLowerInvariant())
                {
                    case "severity": severity = value; break;
                    case "mnemonic": mnemonic = value; break;
                    case "hexcode":  hex      = value; break;
                }
            }

            // An open tag with no closing tag on the same callback (the SDK streams the element line by line):
            // whatever follows on that callback is its body; a later CDATA-only callback completes it (see Add).
            var body = m.Groups["body"].Success ? m.Groups["body"].Value : m.Groups["open"].Value;
            var text = FormatMessage(ElementText(body));
            entries.Add(new ImportLogEntry(severity, mnemonic, hex, text, HintFor(mnemonic, text), RungIndexFrom(text)));
        }

        return entries;
    }

    // A printf-style placeholder Logix leaves in a message template when it was not given the argument: %1!s!, %2!d!.
    private static readonly Regex Placeholder = new(
        @"%\d+!\w!", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The text shown for a message that was only an unformatted template.</summary>
    public const string NoDetailText = "Logix reported an error but gave no detail (its message template was not filled in).";

    /// <summary>True when <paramref name="text"/> contains an unformatted <c>%1!s!</c>-style placeholder.</summary>
    /// <param name="text">The message text.</param>
    public static bool HasPlaceholder(string? text) => !string.IsNullOrEmpty(text) && Placeholder.IsMatch(text);

    /// <summary>
    /// Formats an importer message: each <c>%N!s!</c> placeholder is replaced by <paramref name="args"/>[N-1] when
    /// given, otherwise removed. A message that is nothing but placeholders becomes <see cref="NoDetailText"/>, so
    /// a raw <c>%1!s!</c> is never shown.
    /// </summary>
    /// <param name="text">The message text.</param>
    /// <param name="args">Values for the placeholders, first placeholder first; may be empty.</param>
    public static string FormatMessage(string? text, params string?[] args)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (!Placeholder.IsMatch(text)) return text;
        var formatted = Placeholder.Replace(text, m =>
        {
            var digits = new string(m.Value.Skip(1).TakeWhile(char.IsAsciiDigit).ToArray());
            int n = int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
            return n >= 1 && n <= args.Length && !string.IsNullOrWhiteSpace(args[n - 1]) ? args[n - 1]! : string.Empty;
        });
        formatted = Regex.Replace(formatted, @"\s{2,}", " ").Trim();
        return IsMeaningful(formatted) ? formatted : NoDetailText;
    }

    /// <summary>
    /// True when <paramref name="text"/> says something: it has at least one word of three letters or more once
    /// placeholders are removed.
    /// </summary>
    /// <param name="text">The message text.</param>
    public static bool IsMeaningful(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text != NoDetailText &&
        Regex.IsMatch(Placeholder.Replace(text, " "), @"[A-Za-z]{3,}", RegexOptions.CultureInvariant);

    /// <summary>
    /// The plain hint for a recognised mnemonic, or <c>null</c>. Quotes the replacement Logix names
    /// ("Update instruction to LT.") when the entry text carries one.
    /// </summary>
    /// <param name="mnemonic">The entry's mnemonic.</param>
    /// <param name="text">The entry's text.</param>
    public static string? HintFor(string? mnemonic, string text)
    {
        if (string.IsNullOrEmpty(mnemonic))
            return null;

        var replacement = UpdateInstruction.Match(text ?? string.Empty);
        switch (mnemonic.Trim().ToUpperInvariant())
        {
            case "RXCMP_E_AUDIT_DEPRECATED_INSTRUCTION":
                return replacement.Success
                    ? $"The importer refused a deprecated instruction. Logix says: '{replacement.Value}' Rewrite the rung with the current instruction and import again."
                    : "The importer refused a deprecated instruction. Rewrite the rung with the current instruction form (see the lgx-instruction-reference skill) and import again.";
            case "RXCMP_E_DEPRCATED_INSTR_IN_EXPRESSION":
                return DeprecatedInExpressionHint(text ?? string.Empty, replacement);
            case "RXCMP_E_AUDIT_INSTRNOTDEFINED":
                return "Unknown instruction: this controller type or firmware revision does not define it. " +
                       "Check the mnemonic spelling and that the instruction exists on the target revision (see the lgx-instruction-reference skill).";
            case "RXCMP_E_DIRECT_WRITE_OUTPUT":
                return "A rung writes (OTE/OTL/OTU) another program's Output parameter, which only the owning program can write. " +
                       "Write it from its own program, or read it here with XIC/XIO.";
            default:
                return null;
        }
    }

    /// <summary>
    /// The hint for a deprecated function inside a CMP/CPT/FAL/FSC expression. Names the IEC function that replaces
    /// the old one (<c>SQR</c> becomes <c>SQRT</c>, from <see cref="InstructionMnemonics.ExpressionFunctionPairs"/>):
    /// the one the entry text names, or all of them when the text names none.
    /// </summary>
    private static string DeprecatedInExpressionHint(string text, Match replacement)
    {
        var named = InstructionMnemonics.ExpressionFunctionPairs
            .Where(p => Regex.IsMatch(text, $@"(?<![A-Za-z0-9_]){Regex.Escape(p.Legacy)}(?![A-Za-z0-9_])",
                                      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();

        var sb = new System.Text.StringBuilder("A deprecated function is used inside an expression. ");
        if (named.Count > 0)
            sb.Append("Use the IEC function name in the expression: ")
              .Append(string.Join(", ", named.Select(p => $"{p.Legacy} -> {p.Iec}")))
              .Append(". ");
        else
            sb.Append("Logix v36 and later want the IEC function names inside expressions (")
              .Append(string.Join(", ", InstructionMnemonics.ExpressionFunctionPairs.Select(p => $"{p.Legacy} -> {p.Iec}")))
              .Append("). ");
        if (replacement.Success)
            sb.Append($"Logix says: '{replacement.Value}' ");
        sb.Append("Replace it in the expression and import again.");
        return sb.ToString();
    }

    private static void Add(List<ImportLogEntry> result, string? message, bool keepUnstructured)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var parsed = ParseMessage(message);
        if (parsed.Count == 0)
        {
            // The SDK prefixes each streamed line with "ImportLog" and indents it; strip both before matching.
            var trimmed = LogPrefix.Replace(message, string.Empty).Trim();
            if (trimmed.Length == 0)
                return;

            // A CDATA-only callback is the text of the Exception element opened by the previous callback.
            if (trimmed.StartsWith("<![CDATA[", StringComparison.Ordinal) && CData.IsMatch(trimmed))
            {
                if (result.Count > 0 && result[^1].Text.Length == 0)
                {
                    var last = result[^1];
                    var text = FormatMessage(ElementText(trimmed));
                    result[^1] = last with
                    {
                        Text = text,
                        Hint = HintFor(last.Mnemonic, text),
                        RungIndex = RungIndexFrom(text),
                    };
                }
                return;
            }

            if (keepUnstructured)
            {
                // An exception string such as "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES - ..." carries its mnemonic in
                // the text: give it its own entry. Either way an error-channel entry is an Error, never all-null.
                var mnemonic = XmlSrvMnemonic.Match(trimmed);
                Append(result, new ImportLogEntry(
                    "Error",
                    mnemonic.Success ? mnemonic.Value : null,
                    null,
                    FormatMessage(trimmed),
                    mnemonic.Success ? HintFor(mnemonic.Value, trimmed) : null,
                    RungIndexFrom(trimmed)));
            }
            return;
        }

        foreach (var entry in parsed)
            Append(result, entry);
    }

    private static void Append(List<ImportLogEntry> result, ImportLogEntry entry)
    {
        // Never emit an entry with nothing in it.
        if (entry.Severity is null && entry.Mnemonic is null && entry.Hex is null && entry.Text.Length == 0)
            return;
        if (!result.Contains(entry))
            result.Add(entry);
    }

    private static string ElementText(string body)
    {
        if (string.IsNullOrEmpty(body))
            return string.Empty;
        var cdata = CData.Matches(body);
        if (cdata.Count > 0)
            return string.Concat(cdata.Select(c => c.Groups["t"].Value)).Trim();
        return System.Net.WebUtility.HtmlDecode(body).Trim();
    }
}
