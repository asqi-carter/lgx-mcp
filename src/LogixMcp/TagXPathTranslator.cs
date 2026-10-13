// ── TagXPathTranslator — tag-name to XPath translation ───────────────────────

/// <summary>
/// Translates bare Logix tag names (as supplied by callers) into the XPath
/// strings required by the Logix Designer SDK
/// (<c>RockwellAutomation.LogixDesigner.LogixProject</c>).
///
/// <para>
/// Two formats are recognised:
/// </para>
///
/// <list type="table">
///   <listheader>
///     <term>Input format</term>
///     <description>Example → XPath output</description>
///   </listheader>
///   <item>
///     <term>Controller-scoped bare tag</term>
///     <description>
///       <c>Cmd_MotorStart</c> →
///       <c>Controller/Tags/Tag[@Name='Cmd_MotorStart']</c>
///     </description>
///   </item>
///   <item>
///     <term>Program-scoped tag (<c>Program:ProgramName.TagName</c>)</term>
///     <description>
///       <c>Program:MainProgram.LocalTag</c> →
///       <c>Controller/Programs/Program[@Name='MainProgram']/Tags/Tag[@Name='LocalTag']</c>
///     </description>
///   </item>
/// </list>
///
/// <para>
/// Dot-notation member paths (e.g. <c>Cmd_Motor.Speed</c>) are explicitly
/// not supported by this translator.  Passing a dotted path throws
/// <see cref="ArgumentException"/> with a message that names the byte-array
/// read route as the correct alternative.  UDT member reads must go through
/// the byte-array decode path rather than through XPath.
/// </para>
///
/// <para>
/// This class is a pure string-manipulation utility — it does not depend on
/// the Logix Designer SDK or on any I/O — so it can be exercised in ordinary
/// unit tests without a controller connection.  Expose it at the call-site
/// rather than burying the translation logic inside a service implementation,
/// because the mapping is identified as the seam most likely to be wrong and
/// benefits from independent test coverage.
/// </para>
/// </summary>
public static class TagXPathTranslator
{
    /// <summary>
    /// The prefix that identifies a program-scoped tag name (case-insensitive).
    /// </summary>
    private const string ProgramPrefix = "Program:";

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Translates <paramref name="tagName"/> into the XPath string expected by
    /// the Logix Designer SDK.
    /// </summary>
    /// <param name="tagName">
    /// Caller-supplied tag name in one of two atomic forms:
    /// <list type="bullet">
    ///   <item><description>
    ///     Controller-scoped bare name — <c>"Cmd_MotorStart"</c>
    ///   </description></item>
    ///   <item><description>
    ///     Program-scoped name — <c>"Program:MainProgram.LocalTag"</c>
    ///   </description></item>
    /// </list>
    /// </param>
    /// <returns>
    /// The XPath string addressing the same element inside the L5X document
    /// tree that the Logix Designer SDK expects.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="tagName"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="tagName"/> is empty or whitespace-only,
    /// when a program-scoped name is missing the tag portion after the
    /// program name separator, or when a dot-notation member path is supplied
    /// (member reads require the byte-array read route, not XPath).
    /// </exception>
    public static string Translate(string tagName)
    {
        ArgumentNullException.ThrowIfNull(tagName);

        if (string.IsNullOrWhiteSpace(tagName))
            throw new ArgumentException(
                "Tag name must not be empty or whitespace.", nameof(tagName));

        return tagName.StartsWith(ProgramPrefix, StringComparison.OrdinalIgnoreCase)
            ? TranslateProgramScoped(tagName)
            : TranslateControllerScoped(tagName);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Translates a controller-scoped atomic tag name into an XPath string.
    /// </summary>
    /// <param name="tagPath">
    /// A bare controller-scoped tag name (e.g. <c>"Cmd_MotorStart"</c>).
    /// Dot-notation member paths are not accepted and will throw.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="tagPath"/> contains a dot (member-access
    /// notation).  Use the byte-array read route for UDT member reads.
    /// </exception>
    private static string TranslateControllerScoped(string tagPath)
    {
        if (tagPath.Contains('.'))
            throw new ArgumentException(
                $"Tag path '{tagPath}' contains a dot-notation member path. " +
                "UDT member reads are not supported by XPath translation; use the " +
                "byte-array read route instead.",
                nameof(tagPath));

        // Plain controller-scoped atomic tag.
        return $"Controller/Tags/Tag[@Name='{EscapeXPathLiteral(tagPath)}']";
    }

    /// <summary>
    /// Translates a program-scoped atomic tag name
    /// (<c>"Program:ProgramName.TagName"</c>) into an XPath string.
    /// </summary>
    /// <param name="tagPath">
    /// The full program-scoped tag name including the <c>"Program:"</c> prefix.
    /// Member paths after the tag name are not accepted and will throw.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when the program name or tag name portion cannot be parsed, or
    /// when a member path is detected after the tag name.
    /// </exception>
    private static string TranslateProgramScoped(string tagPath)
    {
        // Strip the "Program:" prefix (case-insensitive match already confirmed).
        string rest = tagPath[ProgramPrefix.Length..];

        int dotIndex = rest.IndexOf('.');
        if (dotIndex <= 0)
            throw new ArgumentException(
                $"Program-scoped tag name '{tagPath}' must include both a program name " +
                "and a tag name, separated by a dot (e.g. 'Program:MainProgram.LocalTag').",
                nameof(tagPath));

        string programName = rest[..dotIndex];
        string tagRest     = rest[(dotIndex + 1)..];

        if (string.IsNullOrEmpty(tagRest))
            throw new ArgumentException(
                $"Program-scoped tag name '{tagPath}' is missing the tag name after the " +
                "program name.",
                nameof(tagPath));

        // A second dot means a member path — not supported by XPath translation.
        if (tagRest.Contains('.'))
            throw new ArgumentException(
                $"Tag path '{tagPath}' contains a dot-notation member path after the " +
                "tag name. UDT member reads are not supported by XPath translation; " +
                "use the byte-array read route instead.",
                nameof(tagPath));

        return $"Controller/Programs/Program[@Name='{EscapeXPathLiteral(programName)}']" +
               $"/Tags/Tag[@Name='{EscapeXPathLiteral(tagRest)}']";
    }

    /// <summary>
    /// Escapes characters in <paramref name="literal"/> that have special
    /// meaning inside an XPath string literal delimited by single quotes.
    ///
    /// <para>
    /// XPath 1.0 string literals cannot mix quote styles inside a single
    /// literal; names that contain a single quote are represented using the
    /// <c>concat()</c> function.  However, Logix tag names may only contain
    /// alphanumeric characters and underscores (per Allen-Bradley naming rules),
    /// so apostrophes are not expected in practice.  The escape is included for
    /// robustness and to satisfy static-analysis requirements.
    /// </para>
    /// </summary>
    /// <param name="literal">The raw string value to embed in an XPath predicate.</param>
    /// <returns>
    /// Either the original string unchanged (when no apostrophe is present) or
    /// an XPath <c>concat()</c> expression that produces the same value.
    /// </returns>
    public static string EscapeXPathLiteral(string literal)
    {
        // Fast path — no apostrophe in the vast majority of tag names.
        if (!literal.Contains('\''))
            return literal;

        // Build a concat() call to handle embedded apostrophes.
        // e.g. "it's" → concat('it', "'", 's')
        var sb = new System.Text.StringBuilder("concat(");
        bool first = true;

        foreach (string segment in literal.Split('\''))
        {
            if (!first) sb.Append(", \"'\", ");
            first = false;
            sb.Append($"'{segment}'");
        }

        sb.Append(')');
        return sb.ToString();
    }
}
