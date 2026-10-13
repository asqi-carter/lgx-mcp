using System.Text.Json.Serialization;

/// <summary>
/// Typed error returned by <c>logix_connect</c> when <c>LGX_GATE_COMMAND_PREFIXES</c>
/// holds an entry that is not a valid tag-name prefix. The connect fails; no entry is
/// ever dropped silently.
/// </summary>
/// <param name="Error">Human-readable message naming the variable and the bad entry.</param>
/// <param name="Kind">Always <c>"GateConfigInvalid"</c>.</param>
/// <param name="Variable">The environment variable name.</param>
/// <param name="Entry">The offending entry, as written (trimmed).</param>
/// <param name="Hint">How to fix the value.</param>
public record GateConfigInvalidError(
    [property: JsonPropertyName("error")]    string Error,
    [property: JsonPropertyName("kind")]     string Kind,
    [property: JsonPropertyName("variable")] string Variable,
    [property: JsonPropertyName("entry")]    string Entry,
    [property: JsonPropertyName("hint")]     string Hint);

/// <summary>Thrown by <see cref="GateConfigResolver.Current"/> when the variable is invalid.</summary>
public sealed class GateConfigInvalidException : Exception
{
    /// <summary>The typed error describing the invalid value.</summary>
    public GateConfigInvalidError Error { get; }

    /// <summary>Creates the exception from its typed error.</summary>
    public GateConfigInvalidException(GateConfigInvalidError error) : base(error.Error) => Error = error;
}

/// <summary>
/// The single resolver of the write-gate configuration. Every enforcement
/// or reporting use of the command prefixes goes through <see cref="Current"/> or
/// <see cref="Resolve"/>; <c>GateConfig.Default</c> is not read anywhere else.
///
/// <para>
/// <c>LGX_GATE_COMMAND_PREFIXES</c>: unset, empty or whitespace gives the default
/// (<c>Cmd_,Sim_</c>). A comma-separated list gives those prefixes (trimmed,
/// case-insensitive). A lone <c>*</c> designates every tag name. It relaxes the
/// naming rule only: every other deny still applies. <c>*</c> mixed with other
/// entries is treated as <c>*</c> with a warning. Entries with characters outside
/// <c>[A-Za-z0-9_]</c> are invalid. The Safety prefix is not configurable.
/// </para>
/// </summary>
public static class GateConfigResolver
{
    /// <summary>Name of the environment variable.</summary>
    public const string EnvVar = "LGX_GATE_COMMAND_PREFIXES";

    /// <summary><c>commandPrefixesSource</c> when the variable is not in effect.</summary>
    public const string SourceDefault = "default";

    /// <summary>The wildcard entry that designates every tag name.</summary>
    public const string Wildcard = "*";

    // Test seam: a per-async-flow replacement for the environment value, so tests can set the
    // variable without racing other tests that run in parallel in the same process.
    private static readonly AsyncLocal<Override?> TestOverride = new();

    private sealed record Override(string? Value);

    /// <summary>
    /// Makes the resolver read <paramref name="value"/> instead of the environment in the
    /// current async flow until the returned scope is disposed. For tests.
    /// </summary>
    internal static IDisposable UseValueForTesting(string? value)
    {
        var previous = TestOverride.Value;
        TestOverride.Value = new Override(value);
        return new Scope(previous);
    }

    private sealed class Scope(Override? previous) : IDisposable
    {
        public void Dispose() => TestOverride.Value = previous;
    }

    private static string? RawValue() =>
        TestOverride.Value is { } o ? o.Value : Environment.GetEnvironmentVariable(EnvVar);

    /// <summary>The raw <c>LGX_GATE_COMMAND_PREFIXES</c> value, or <c>null</c> when unset. Read-only.</summary>
    public static string? RawFromEnvironment() => RawValue();

    /// <summary>Resolves the configuration from the process environment.</summary>
    /// <exception cref="GateConfigInvalidException">The variable holds an invalid entry.</exception>
    public static GateConfig Current
    {
        get
        {
            var result = Resolve(RawValue());
            if (result is GateConfigInvalidError invalid) throw new GateConfigInvalidException(invalid);
            return (GateConfig)result;
        }
    }

    /// <summary>
    /// Resolves the configuration from the environment without throwing.
    /// </summary>
    /// <returns>A <see cref="GateConfig"/>, or a <see cref="GateConfigInvalidError"/>.</returns>
    public static object FromEnvironment() => Resolve(RawValue());

    /// <summary>Resolves the configuration from a raw variable value.</summary>
    /// <param name="raw">The raw value, or <c>null</c> when unset.</param>
    /// <returns>A <see cref="GateConfig"/>, or a <see cref="GateConfigInvalidError"/>.</returns>
    public static object Resolve(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return GateConfig.Default;

        var entries = raw.Split(',').Select(e => e.Trim()).ToList();
        var seen = new List<string>();
        var sawWildcard = false;
        foreach (var entry in entries)
        {
            if (entry == Wildcard) { sawWildcard = true; seen.Add(entry); continue; }
            if (entry.Length == 0 || !entry.All(IsPrefixChar))
                return new GateConfigInvalidError(
                    $"{EnvVar} contains the invalid entry '{entry}'. Entries must use only " +
                    "letters, digits and underscore (A-Z, a-z, 0-9, _), or be a lone '*'.",
                    "GateConfigInvalid", EnvVar, entry,
                    $"Fix or unset {EnvVar} (comma-separated prefixes such as 'Cmd_,Sim_,HMI_', or '*'), then reconnect.");
            seen.Add(entry);
        }

        if (sawWildcard)
        {
            var warnings = seen.Count > 1
                ? new[] { $"{EnvVar} mixes '*' with other entries; treated as '*' (every tag name designated)." }
                : Array.Empty<string>();
            return GateConfig.Default with
            {
                CommandPrefixes       = new[] { string.Empty },
                CommandPrefixesSource = EnvVar,
                AllNamesDesignated    = true,
                Warnings              = warnings,
            };
        }

        return GateConfig.Default with
        {
            CommandPrefixes       = seen.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            CommandPrefixesSource = EnvVar,
        };
    }

    /// <summary>
    /// Active prefixes for refusal text, e.g. <c>Cmd_ or Sim_ (set by LGX_GATE_COMMAND_PREFIXES)</c>
    /// (the variable is named only when it is in effect). Never throws.
    /// </summary>
    public static string TryDescribePrefixes() =>
        FromEnvironment() is GateConfig c ? DescribePrefixes(c) : $"(invalid {EnvVar})";

    /// <summary>
    /// The enforced command prefixes as display text, e.g. <c>['Cmd_', 'Sim_']</c> or <c>'*'</c>,
    /// with the variable named when it is in effect. Never throws.
    /// </summary>
    public static string TryDescribeEnforced()
    {
        var r = FromEnvironment();
        if (r is not GateConfig c) return $"(invalid {EnvVar})";
        var list = c.AllNamesDesignated ? "'*'" : "[" + string.Join(", ", c.CommandPrefixes.Select(p => $"'{p}'")) + "]";
        return c.CommandPrefixesSource == SourceDefault ? list : $"{list} (set by {EnvVar})";
    }

    /// <summary>
    /// Safety-tag name prefixes. Not configurable: no variable removes or changes the Safety deny.
    /// </summary>
    public static IReadOnlyList<string> SafetyPrefixes => GateConfig.Default.SafetyPrefixes;

    /// <summary>
    /// Configuration for a partial export gated by a write allowlist: every tag is a candidate
    /// so the name-independent denies are all reported. This is its own meaning, independent of
    /// <c>LGX_GATE_COMMAND_PREFIXES</c>.
    /// </summary>
    public static GateConfig PartialExport => GateConfig.Default with { CommandPrefixes = new[] { string.Empty } };

    private static bool IsPrefixChar(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_';

    /// <summary>
    /// Text naming the active prefixes for a refusal or hint, for example
    /// <c>Cmd_ or Sim_ (set by LGX_GATE_COMMAND_PREFIXES)</c>. The variable is named
    /// only when it is in effect.
    /// </summary>
    /// <param name="config">The configuration in force.</param>
    public static string DescribePrefixes(GateConfig config)
    {
        var list = config.AllNamesDesignated ? Wildcard : string.Join(" or ", config.CommandPrefixes);
        return config.CommandPrefixesSource == SourceDefault ? list : $"{list} (set by {EnvVar})";
    }
}
