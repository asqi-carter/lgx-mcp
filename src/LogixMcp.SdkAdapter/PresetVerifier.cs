#if LOGIX_SDK
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>
/// A preset read back from the tag and found equal to the literal the published rung carried
/// (<c>presetsVerified</c>).
/// </summary>
/// <param name="Tag">The timer or counter tag as written in the rung.</param>
/// <param name="Pre">The <c>.PRE</c> value read from the tag.</param>
public sealed record PresetVerified(
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("pre")] long   Pre);

/// <summary>
/// A preset read back from the tag that differs from the literal the published rung carried
/// (<c>presetMismatches</c>). Reported in the census result; it is NOT a <c>PublishMismatch</c> and
/// never sets <c>PublishMismatchUnresolved</c>, because the rung text itself matched.
/// </summary>
/// <param name="Tag">The timer or counter tag as written in the rung.</param>
/// <param name="Expected">The preset literal in the published rung text.</param>
/// <param name="Found">The <c>.PRE</c> value read from the tag, as text.</param>
/// <param name="Kind">Always <c>"PresetMismatch"</c>.</param>
public sealed record PresetMismatch(
    [property: JsonPropertyName("tag")]      string Tag,
    [property: JsonPropertyName("expected")] long   Expected,
    [property: JsonPropertyName("found")]    string Found,
    [property: JsonPropertyName("kind")]     string Kind = "PresetMismatch");

/// <summary>
/// Outcome of <see cref="PresetVerifier.VerifyAsync"/>.
/// </summary>
/// <param name="Verified">Presets read back and equal to the rung's literal.</param>
/// <param name="Mismatches">Presets read back and different.</param>
/// <param name="Unread">Tags whose <c>.PRE</c> could not be read (neither verified nor a mismatch).</param>
public sealed record PresetVerification(
    IReadOnlyList<PresetVerified> Verified,
    IReadOnlyList<PresetMismatch> Mismatches,
    IReadOnlyList<string>         Unread);

/// <summary>
/// Post-publish preset verification. A routine export writes a timer's or
/// counter's preset as <c>?</c>, so the rung text cannot prove the preset landed: the verifier reads
/// <c>&lt;tag&gt;.PRE</c> and compares it to the literal in the expected rung text.
/// </summary>
internal static class PresetVerifier
{
    private static readonly Regex IntegerLiteral =
        new(@"\A-?[0-9]+\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Finds every (tag, literal preset) pair carried by <paramref name="rungTexts"/> for an instruction in
    /// <see cref="RungTextComparer.PlaceholderOperands"/> whose preset operand (operand 2) is an integer
    /// literal. A preset that is a tag reference is skipped. Duplicate pairs are listed once.
    /// </summary>
    /// <param name="rungTexts">Expected rung neutral texts (new or replaced rungs).</param>
    /// <returns>Distinct (tag, preset) pairs in first-seen order.</returns>
    internal static IReadOnlyList<(string Tag, long Preset)> ExtractLiteralPresets(IEnumerable<string> rungTexts)
    {
        var seen   = new HashSet<(string, long)>();
        var result = new List<(string Tag, long Preset)>();
        foreach (var text in rungTexts)
        {
            foreach (var (mnemonic, operands) in RungTextComparer.Instructions(text))
            {
                if (!RungTextComparer.PlaceholderOperands.ContainsKey(mnemonic) || operands.Count < 2)
                    continue;
                var tag    = operands[0].Trim();
                var preset = operands[1].Trim();
                if (tag.Length == 0 || !IntegerLiteral.IsMatch(preset)
                    || !long.TryParse(preset, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                    continue;
                if (seen.Add((tag, value)))
                    result.Add((tag, value));
            }
        }
        return result;
    }

    /// <summary>
    /// Reads <c>&lt;tag&gt;.PRE</c> for each pair of <paramref name="expected"/> and compares it to the
    /// literal. A tag declared in <paramref name="programName"/> is read as <c>Program:P.tag.PRE</c>
    /// first (the rung resolves program scope before controller scope), then as <c>tag.PRE</c> when that
    /// tag is not found. A read that fails is reported as unread, never as a mismatch.
    /// </summary>
    /// <param name="reader">The tag service to read through (the SDK route for a just-created tag).</param>
    /// <param name="programName">Program of the published routine, or empty for none.</param>
    /// <param name="expected">The (tag, preset) pairs to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The verified, mismatched and unread presets.</returns>
    internal static async Task<PresetVerification> VerifyAsync(
        ITagService reader,
        string programName,
        IReadOnlyList<(string Tag, long Preset)> expected,
        CancellationToken ct)
    {
        var verified   = new List<PresetVerified>();
        var mismatches = new List<PresetMismatch>();
        var unread     = new List<string>();

        foreach (var (tag, preset) in expected)
        {
            ct.ThrowIfCancellationRequested();
            var found = await TryReadPreAsync(reader, programName, tag, ct).ConfigureAwait(false);
            if (found is null)
            {
                if (!unread.Contains(tag, StringComparer.Ordinal))
                    unread.Add(tag);
                continue;
            }

            if (TryAsLong(found, out var value) && value == preset)
                verified.Add(new PresetVerified(tag, value));
            else
                mismatches.Add(new PresetMismatch(tag, preset, Convert.ToString(found, CultureInfo.InvariantCulture) ?? string.Empty));
        }
        return new PresetVerification(verified, mismatches, unread);
    }

    private static async Task<object?> TryReadPreAsync(
        ITagService reader, string programName, string tag, CancellationToken ct)
    {
        var candidates = new List<string>();
        if (programName.Length > 0 && !tag.StartsWith("Program:", StringComparison.OrdinalIgnoreCase))
            candidates.Add($"Program:{programName}.{tag}.PRE");
        candidates.Add($"{tag}.PRE");

        foreach (var path in candidates)
        {
            try
            {
                return (await reader.ReadAsync(path, ct).ConfigureAwait(false)).Value;
            }
            catch (TagServiceException ex) when (ex.Error.Kind == TagServiceErrorKind.TagNotFound)
            {
                // Try the next scope.
            }
            catch (TagServiceException)
            {
                return null;
            }
        }
        return null;
    }

    private static bool TryAsLong(object value, out long result)
    {
        switch (value)
        {
            case long l:   result = l; return true;
            case int i:    result = i; return true;
            case short s:  result = s; return true;
            case byte b:   result = b; return true;
            case uint u:   result = u; return true;
            case ushort us: result = us; return true;
            case ulong ul when ul <= long.MaxValue: result = (long)ul; return true;
            case string str:
                return long.TryParse(str, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
            default:
                result = 0;
                return false;
        }
    }
}
#endif
