// ── WriteTypeRule — shared declared-type check for logix_write ───────────────

/// <summary>
/// Single source of truth for the "no silent coercion" write rule, shared by
/// the SDK backend (<c>TypedSdkTagService</c>) and the CIP backend
/// (<see cref="LibPlcTagService"/>) so both return an identical
/// <see cref="TagServiceErrorKind.TypeMismatch"/> message and fix.
/// </summary>
public static class WriteTypeRule
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/>'s .NET type is exactly
    /// the one the Logix <paramref name="declaredType"/> maps to.  Unknown / UDT
    /// types cannot be checked at the .NET-value level and are accepted.
    /// A BOOL accepts only <see cref="bool"/> (<c>true</c>/<c>false</c>);
    /// <c>1</c>, <c>0</c>, <c>"1"</c> and <c>"0"</c> are not widened.
    /// </summary>
    /// <param name="declaredType">Logix type string (e.g. <c>"BOOL"</c>).</param>
    /// <param name="value">The value to check.</param>
    public static bool IsCompatible(string declaredType, object value) =>
        declaredType.ToUpperInvariant() switch
        {
            "BOOL"   => value is bool,
            "SINT"   => value is sbyte,
            "INT"    => value is short,
            "DINT"   => value is int,
            "LINT"   => value is long,
            "USINT"  => value is byte,
            "UINT"   => value is ushort,
            "UDINT"  => value is uint,
            "ULINT"  => value is ulong,
            "REAL"   => value is float,
            "LREAL"  => value is double,
            "STRING" => value is string,
            _        => true,
        };

    /// <summary>
    /// Builds the <see cref="TagServiceErrorKind.TypeMismatch"/> exception for
    /// a value that does not fit <paramref name="declaredType"/>.
    /// </summary>
    /// <param name="tagName">Tag name used in the message and error payload.</param>
    /// <param name="declaredType">Logix type string.</param>
    /// <param name="value">The offending value.</param>
    public static TagServiceException TypeMismatch(
        string tagName, string declaredType, object value)
    {
        var message =
            $"Cannot write value of type '{value.GetType().Name}' to tag " +
            $"'{tagName}' declared as {declaredType}. " +
            $"Silent coercion is prohibited — supply a value of the exact " +
            $"declared type (e.g. float for REAL, int for DINT).";
        if (declaredType.Equals("BOOL", StringComparison.OrdinalIgnoreCase))
            message += " " + BoolHint;
        return new(new TagServiceError(
            TagServiceErrorKind.TypeMismatch, message, tagName));
    }

    /// <summary>Gate-decision reason recorded when a non-finite value is refused.</summary>
    public const string NonFiniteReason = "NonFiniteValue";

    /// <summary>
    /// Recognises the textual spellings of NaN and ±Infinity: <c>NaN</c>, <c>-NaN</c>,
    /// <c>nan</c>, <c>Infinity</c>, <c>-Infinity</c>, <c>inf</c>, <c>∞</c> (any case, optional
    /// sign, surrounding whitespace ignored).
    /// </summary>
    /// <param name="raw">The caller's raw string.</param>
    /// <param name="value">The matching <c>double.NaN</c> or ±<c>double.Infinity</c>.</param>
    /// <returns><c>true</c> when <paramref name="raw"/> is a non-finite token.</returns>
    public static bool TryParseNonFiniteToken(string raw, out double value)
    {
        value = 0;
        var s = raw.Trim();
        var negative = false;
        if (s.StartsWith('-')) { negative = true; s = s[1..]; }
        else if (s.StartsWith('+')) s = s[1..];

        if (s.Equals("nan", StringComparison.OrdinalIgnoreCase))
        {
            value = double.NaN;
            return true;
        }
        if (s == "\u221E" || s.Equals("inf", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            value = negative ? double.NegativeInfinity : double.PositiveInfinity;
            return true;
        }
        return false;
    }

    /// <summary>
    /// <c>true</c> when <paramref name="value"/> is a <see cref="float"/> or <see cref="double"/>
    /// that is NaN or ±Infinity, or a string spelling one (see <see cref="TryParseNonFiniteToken"/>).
    /// </summary>
    /// <param name="value">The value to test.</param>
    public static bool IsNonFinite(object value) => value switch
    {
        double d => !double.IsFinite(d),
        float f  => !float.IsFinite(f),
        string s => TryParseNonFiniteToken(s, out _),
        _        => false,
    };

    /// <summary>Human-readable refusal message for a non-finite write.</summary>
    /// <param name="tagName">Tag the write targeted.</param>
    /// <param name="value">The offending value.</param>
    public static string NonFiniteMessage(string tagName, object value) =>
        $"Write refused for tag '{tagName}': value " +
        $"{Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)} is not a finite " +
        "number (NaN and ±Infinity are never written); nothing was written.";

    /// <summary>
    /// Refuses a NaN or ±Infinity write: logs the refusal via
    /// <see cref="AuditLogger.LogGateDecision"/> (reason <see cref="NonFiniteReason"/>, with
    /// <paramref name="source"/> as provenance) and throws
    /// <see cref="TagServiceErrorKind.InvalidValue"/>.  No-op for finite or non-numeric values.
    /// </summary>
    /// <param name="tagName">Tag the write targeted.</param>
    /// <param name="value">The value about to be written.</param>
    /// <param name="source">Which service refused it (for the audit provenance).</param>
    /// <exception cref="TagServiceException">When the value is non-finite.</exception>
    public static void RejectNonFinite(string tagName, object value, string source)
    {
        if (!IsNonFinite(value)) return;
        var message = NonFiniteMessage(tagName, value);
        AuditLogger.LogGateDecision(tagName, "denied", NonFiniteReason, $"{source}: {message}");
        throw new TagServiceException(
            new TagServiceError(TagServiceErrorKind.InvalidValue, message, tagName));
    }

    /// <summary>Hint appended to TypeMismatch messages for BOOL tags.</summary>
    public const string BoolHint = "BOOL accepts true or false (JSON booleans).";

    /// <summary>
    /// Throws the shared <see cref="TagServiceErrorKind.TypeMismatch"/> when
    /// <paramref name="value"/> is not compatible with <paramref name="declaredType"/>.
    /// </summary>
    /// <param name="tagName">Tag name used in the error payload.</param>
    /// <param name="declaredType">Logix type string.</param>
    /// <param name="value">The value to validate.</param>
    /// <exception cref="TagServiceException">On mismatch.</exception>
    public static void Validate(string tagName, string declaredType, object value)
    {
        if (!IsCompatible(declaredType, value))
            throw TypeMismatch(tagName, declaredType, value);
    }
}
