using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>
/// Structured error returned by <see cref="ConnectHandler.ExecuteAsync"/> when the
/// file named by <see cref="WriteAllowlist.EnvVar"/> cannot be read or contains an
/// invalid entry. Connect fails closed: the allowlist is never silently ignored.
/// </summary>
/// <param name="Error">Human-readable description including the file path and line number.</param>
/// <param name="Kind">Always <c>"AllowlistInvalid"</c>.</param>
/// <param name="Path">The allowlist file path that was configured.</param>
/// <param name="Line">1-based line number of the offending entry, or <c>null</c> when the file itself is unreadable.</param>
/// <param name="Hint">Actionable fix suggestion.</param>
public record AllowlistInvalidError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("kind")]  string Kind,
    [property: JsonPropertyName("path")]  string Path,
    [property: JsonPropertyName("line")]  int?   Line,
    [property: JsonPropertyName("hint")]  string Hint);

/// <summary>
/// An optional write allowlist loaded from the file named by
/// <see cref="EnvVar"/>. It can only narrow what the write gate permits; it never
/// widens it. File format: one entry per line; an entry is a tag name or a tag
/// name with a trailing <c>*</c> (prefix match); <c>#</c> starts a comment.
/// </summary>
public sealed class WriteAllowlist
{
    /// <summary>Environment variable holding the path to the allowlist file.</summary>
    public const string EnvVar = "LGX_WRITE_ALLOWLIST";

    // Identifier segments joined by '.', each optionally followed by [n] / [n,m] indices.
    private static readonly Regex NamePattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*(\[[0-9]+(,[0-9]+){0,2}\])*(\.[A-Za-z0-9_]+(\[[0-9]+(,[0-9]+){0,2}\])*)*\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Lenient form for '*' prefix entries, which may end mid-identifier or after '.'.
    private static readonly Regex PrefixPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_\.\[\],]*\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="name"/> is a valid exact tag-name entry (ASCII only, no trailing newline).</summary>
    /// <param name="name">Candidate tag name.</param>
    internal static bool IsValidTagName(string? name) => name is not null && NamePattern.IsMatch(name);

    /// <summary>True when <paramref name="name"/> is a valid tag-name prefix (the part before a trailing '*').</summary>
    /// <param name="name">Candidate prefix.</param>
    internal static bool IsValidTagPrefix(string? name) => name is not null && PrefixPattern.IsMatch(name);

    private readonly string[] _exact;
    private readonly string[] _prefixes;

    private WriteAllowlist(string path, string sha256, string[] exact, string[] prefixes)
    {
        Path     = path;
        Sha256   = sha256;
        _exact   = exact;
        _prefixes = prefixes;
    }

    /// <summary>Path the allowlist was loaded from.</summary>
    public string Path { get; }

    /// <summary>Lower-case hex SHA-256 of the raw file bytes.</summary>
    public string Sha256 { get; }

    /// <summary>Number of entries (exact names plus prefix patterns).</summary>
    public int EntryCount => _exact.Length + _prefixes.Length;

    /// <summary>The exact tag-name entries, in file order.</summary>
    public IReadOnlyList<string> ExactEntries => _exact;

    /// <summary>The prefix entries (the text before the trailing <c>*</c>), in file order.</summary>
    public IReadOnlyList<string> PrefixEntries => _prefixes;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="tagName"/> equals an exact entry or starts
    /// with a prefix entry (comparison is case-insensitive, as Logix tag names are).
    /// </summary>
    public bool Matches(string tagName)
    {
        foreach (var e in _exact)
            if (string.Equals(e, tagName, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var p in _prefixes)
            if (tagName.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="tagName"/> (or, for a member path or array
    /// element such as <c>Tag.Member</c> / <c>Tag[2]</c>, its base tag) matches an entry.
    /// </summary>
    /// <param name="tagName">Tag reference as given to the write.</param>
    public bool Permits(string tagName)
    {
        if (Matches(tagName)) return true;
        var cut = tagName.IndexOfAny(['.', '[']);
        return cut > 0 && Matches(tagName[..cut]);
    }

    /// <summary>
    /// Returns the <see cref="DenyReason.NotOnAllowlist"/> message for
    /// <paramref name="tagName"/> naming the allowlist file, or <c>null</c> when the tag is listed.
    /// </summary>
    /// <param name="tagName">Tag reference as given to the write.</param>
    /// <param name="gated">
    /// <c>true</c> (default) when a <see cref="GatedTagService"/> also enforces the write gate;
    /// <c>false</c> for a session with no project open, where the allowlist is the only write grant.
    /// </param>
    public string? DenialMessage(string tagName, bool gated = true) =>
        Permits(tagName)
            ? null
            : $"Write denied for tag '{tagName}': {DenyReason.NotOnAllowlist} — the tag is not listed in the " +
              $"write allowlist file '{Path}' ({EnvVar}). " +
              (gated
                  ? "The allowlist only narrows what the write gate grants."
                  : "With no project open, the allowlist is the only write grant.");

    /// <summary>
    /// Reads the allowlist path from the process environment. Returns <c>null</c> when
    /// the variable is unset or empty.
    /// </summary>
    public static string? PathFromEnvironment()
    {
        var v = Environment.GetEnvironmentVariable(EnvVar);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    /// <summary>
    /// Loads and validates the allowlist at <paramref name="path"/>.
    /// Returns a <see cref="WriteAllowlist"/> on success or an
    /// <see cref="AllowlistInvalidError"/> (kind <c>AllowlistInvalid</c>) when the file is
    /// unreadable or any line holds an invalid entry.
    /// </summary>
    public static object Load(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            return new AllowlistInvalidError(
                Error: $"Cannot read write allowlist file '{path}' ({EnvVar}): {ex.Message}",
                Kind:  "AllowlistInvalid",
                Path:  path,
                Line:  null,
                Hint:  $"Point {EnvVar} at a readable text file, or unset {EnvVar} to run without an allowlist.");
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (ArgumentException)
        {
            return new AllowlistInvalidError(
                Error: $"Write allowlist file '{path}' is not valid UTF-8 text.",
                Kind:  "AllowlistInvalid",
                Path:  path,
                Line:  null,
                Hint:  "Save the file as UTF-8 plain text with one tag name or prefix* per line.");
        }

        var exact    = new List<string>();
        var prefixes = new List<string>();
        var lines    = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw   = lines[i];
            var hash  = raw.IndexOf('#');
            var entry = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (entry.Length == 0) continue;

            var lineNo = i + 1;
            var isPrefix = entry.EndsWith('*');
            var name = isPrefix ? entry[..^1] : entry;

            if (name.Length == 0)
                return Invalid(path, lineNo, entry,
                    "a bare '*' would allow every tag",
                    "Give a tag-name prefix before the '*' (e.g. 'Recipe_*') or list tag names explicitly.");
            if (name.Contains('*'))
                return Invalid(path, lineNo, entry,
                    "'*' is only allowed as the final character",
                    "Remove the inner '*'; wildcards are trailing prefix matches only (e.g. 'Motor1_*').");
            // A prefix may end mid-identifier or after '.', so validate it leniently.
            var ok = isPrefix ? PrefixPattern.IsMatch(name) : NamePattern.IsMatch(name);
            if (!ok)
                return Invalid(path, lineNo, entry,
                    "not a valid tag name",
                    "Use letters, digits and underscores (starting with a letter or underscore), optionally with '.member' or '[index]' parts, and an optional trailing '*'.");

            (isPrefix ? prefixes : exact).Add(name);
        }

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new WriteAllowlist(path, sha, exact.ToArray(), prefixes.ToArray());
    }

    private static AllowlistInvalidError Invalid(string path, int line, string entry, string why, string fix) =>
        new(Error: $"Invalid write allowlist entry '{entry}' at line {line} of '{path}': {why}.",
            Kind:  "AllowlistInvalid",
            Path:  path,
            Line:  line,
            Hint:  $"Fix line {line} of '{path}': {fix}");
}
