using L5Sharp.Core;

/// <summary>
/// Follows <c>AliasFor</c> for a tag path that names an alias tag (controller scope, or
/// <c>Program:P.Alias</c>) to the path of a non-alias base, so the tag can be typed, read and gated
/// by what it stands for.
/// </summary>
/// <remarks>
/// An alias tag declares no <c>DataType</c>; its type and value are the base's. The base may be a
/// controller or program tag, a member path (<c>Tag.Member</c>) or a bit (<c>Tag.5</c>). Names in an
/// <c>AliasFor</c> resolve in the alias's own program first, then controller scope. At most
/// <see cref="MaxDepth"/> aliases are followed; a cycle, a missing target, module I/O or a longer chain is
/// reported as a failure that names the chain. The lookup reads the project's own tag collections and
/// never L5Sharp's project-wide index.
/// </remarks>
public static class AliasReadResolver
{
    private const string ProgramPrefix = "Program:";

    /// <summary>The most alias tags a chain may pass through before it is refused.</summary>
    public const int MaxDepth = 4;

    /// <summary>Outcome of <see cref="Resolve"/> for a path that names an alias.</summary>
    /// <param name="Target">The base path to read instead (e.g. <c>_ChangeProfile.Mandrel</c>); <c>null</c> when unresolved.</param>
    /// <param name="AliasFor">The alias's own <c>AliasFor</c> text, as declared.</param>
    /// <param name="Failure">Why the alias cannot be followed (names the chain); <c>null</c> when resolved.</param>
    /// <param name="Chain">The alias tags visited, outermost first (a cycle ends with the tag that closes it).</param>
    /// <param name="ModuleIoTarget">
    /// Set (with <see cref="Failure"/>) when the chain ends at module I/O (<c>Local:1:I.Data.3</c>): the project
    /// declares no tag for it, so the alias is not "resolved" here, but a type resolver may type that path from the
    /// module definitions.
    /// </param>
    public sealed record Resolution(
        string? Target, string AliasFor, string? Failure, IReadOnlyList<string>? Chain = null, string? ModuleIoTarget = null)
    {
        /// <summary>True when <see cref="Target"/> is set.</summary>
        public bool IsResolved => Failure is null && Target is not null;

        /// <summary>The resolved base path, reported to callers as <c>resolvedFrom</c>; <c>null</c> when unresolved.</summary>
        public string? ResolvedFrom => Target;
    }

    /// <summary>
    /// Resolves <paramref name="tagPath"/> when it names an alias tag, optionally with a
    /// member, index or bit suffix (<c>Alias.Member</c>, <c>Program:P.Alias.5</c>).
    /// </summary>
    /// <param name="project">The project holding the declarations.</param>
    /// <param name="tagPath">The tag path being read, typed or written.</param>
    /// <returns>
    /// <c>null</c> when the path is not an alias (including an unknown program or tag, which
    /// the backend reports as not found); otherwise the resolution or its failure.
    /// </returns>
    public static Resolution? Resolve(L5X project, string tagPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(tagPath)) return null;
        tagPath = tagPath.Trim();

        string? programName = null;
        string aliasName, suffix;
        if (tagPath.StartsWith(ProgramPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var afterPrefix = tagPath[ProgramPrefix.Length..];
            var dot = afterPrefix.IndexOf('.');
            if (dot <= 0) return null;
            programName = afterPrefix[..dot];
            SplitBase(afterPrefix[(dot + 1)..], out aliasName, out suffix);
        }
        else
        {
            SplitBase(tagPath, out aliasName, out suffix);
        }

        Tag? alias;
        string? scope = null;
        if (programName is not null)
        {
            var program = project.Programs.FirstOrDefault(p => Same(p.Name.ToString(), programName));
            alias = program?.Tags.FirstOrDefault(t => Same(t.Name.ToString(), aliasName));
            scope = program?.Name.ToString();
        }
        else
        {
            alias = project.Tags.FirstOrDefault(t => Same(t.Name.ToString(), aliasName));
        }
        if (alias is null || !IsAlias(alias)) return null;

        var declared = AliasForOf(alias);
        var chain    = new List<string> { Display(scope, alias.Name.ToString()) };
        Resolution Fail(string why) => new(null, declared, why, chain);
        string ChainText() => string.Join(" -> ", chain);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Key(scope, alias.Name.ToString()) };
        var text = declared + suffix;
        if (declared.Length == 0)
            return Fail($"alias '{tagPath}' has no AliasFor (chain {ChainText()})");

        while (true)
        {
            SplitBase(text, out var baseName, out var rest);
            if (baseName.Length == 0)
                return Fail($"alias '{tagPath}' has an empty or malformed AliasFor '{text}' (chain {ChainText()})");
            if (baseName.Contains(':'))
                return new Resolution(null, declared,
                    $"alias '{tagPath}' points at module I/O '{text}', whose type the project does not declare (chain {ChainText()})",
                    chain, text);

            Tag? next = null;
            string? nextScope = null;
            if (scope is not null)
            {
                var prog = project.Programs.FirstOrDefault(p => Same(p.Name.ToString(), scope));
                next = prog?.Tags.FirstOrDefault(t => Same(t.Name.ToString(), baseName));
                if (next is not null) nextScope = scope;
            }
            next ??= project.Tags.FirstOrDefault(t => Same(t.Name.ToString(), baseName));
            if (next is null)
                return Fail($"alias '{tagPath}' points at '{text}', but tag '{baseName}' does not exist (chain {ChainText()})");

            var nextName = next.Name.ToString();
            if (!seen.Add(Key(nextScope, nextName)))
            {
                chain.Add(Display(nextScope, nextName));
                return Fail($"alias '{tagPath}' is part of a circular alias chain: {ChainText()}");
            }

            if (!IsAlias(next))
                return new Resolution(nextScope is null ? text : $"{ProgramPrefix}{nextScope}.{text}", declared, null, chain);

            chain.Add(Display(nextScope, nextName));
            if (chain.Count > MaxDepth)
                return Fail($"alias '{tagPath}' chain is deeper than {MaxDepth} aliases: {ChainText()}");

            var hopFor = AliasForOf(next);
            if (hopFor.Length == 0)
                return Fail($"alias '{tagPath}' passes through '{baseName}', which has no AliasFor (chain {ChainText()})");
            text  = hopFor + rest;
            scope = nextScope;
        }
    }

    private static string Display(string? scope, string name) => scope is null ? name : $"{ProgramPrefix}{scope}.{name}";

    private static string Key(string? scope, string name) => $"{scope}\u0001{name}";

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsAlias(Tag tag) =>
        string.Equals(tag.Serialize().Attribute("TagType")?.Value, "Alias", StringComparison.OrdinalIgnoreCase);

    private static string AliasForOf(Tag tag) => (tag.Serialize().Attribute("AliasFor")?.Value ?? string.Empty).Trim();

    /// <summary>Splits <c>Name[1].A.B</c> into <c>Name</c> and <c>[1].A.B</c>.</summary>
    private static void SplitBase(string text, out string baseName, out string rest)
    {
        var end  = text.IndexOfAny(['.', '[']);
        baseName = end < 0 ? text : text[..end];
        rest     = end < 0 ? string.Empty : text[end..];
    }
}
