using System.Text.Json.Serialization;

// ── Result types ──────────────────────────────────────────────────────────────

/// <summary>A tag a test run wrote (or tried to) that ordinary <c>logix_write</c> would deny.</summary>
/// <param name="Tag">Tag name as given in the test case.</param>
/// <param name="Reason">Why ordinary <c>logix_write</c> denies it (a <see cref="DenyReason"/> name).</param>
/// <param name="Provenance">Human-readable explanation from the gate policy.</param>
public sealed record TestWriteDisclosure(
    [property: JsonPropertyName("tag")]        string Tag,
    [property: JsonPropertyName("reason")]     string Reason,
    [property: JsonPropertyName("provenance")] string Provenance);

/// <summary>A tag a test run refuses to write at all.</summary>
/// <param name="Tag">Tag name as given in the test case.</param>
/// <param name="Reason">Refusal reason: <c>ProgramWritten</c>, <c>ExternalAccessNotReadWrite</c>, <c>CreatedByThisSession</c>, <c>ProgramScoped</c>, <c>NotInPolicy</c> or <c>NotOnAllowlist</c>.</param>
/// <param name="Provenance">Human-readable explanation.</param>
public sealed record TestWriteRefusal(
    [property: JsonPropertyName("tag")]        string Tag,
    [property: JsonPropertyName("reason")]     string Reason,
    [property: JsonPropertyName("provenance")] string Provenance);

/// <summary>Outcome of <see cref="TestWriteRules.Evaluate"/> for one tag.</summary>
/// <param name="Refusal">Non-null when a test may not write the tag.</param>
/// <param name="OrdinaryDenial">Non-null when the tag is allowed in a test but ordinary <c>logix_write</c> would deny it.</param>
public sealed record TestWriteDecision(TestWriteRefusal? Refusal, TestWriteDisclosure? OrdinaryDenial)
{
    /// <summary>True when a test may write the tag.</summary>
    public bool Allowed => Refusal is null;
}

// ── Rules ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Write rules for <c>logix_run_test</c>. They apply only after the emulator gate has
/// passed, and are looser than ordinary <c>logix_write</c> in exactly one way: tags the write-gate
/// denies as <c>SafetyTag</c>, <c>NotDesignated</c> or <c>UnknownWriteContext</c> may be driven, and
/// tags outside the command prefixes may be set, because testing an interlock means driving inputs.
///
/// <para>Always refused:</para>
/// <list type="bullet">
///   <item><description><c>ProgramWritten</c> tags (driven by program logic);</description></item>
///   <item><description>tags whose External Access is not Read/Write, which includes the Read Only tags
///     lgx-mcp creates: the engineer's grant is never bypassed;</description></item>
///   <item><description>tags created by this session (<c>CreatedByThisSession</c>);</description></item>
///   <item><description>program-scoped names (<c>Program:X.Tag</c>) and tags absent from the policy,
///     because "not written by program logic" cannot be established for them.</description></item>
/// </list>
/// </summary>
public static class TestWriteRules
{
    private const string ReadWrite = "Read/Write";

    /// <summary>
    /// Classifies a test write to <paramref name="tagName"/>.
    /// </summary>
    /// <param name="tagName">Tag name as given in the test case; member paths and array indices resolve to the base tag.</param>
    /// <param name="policy">The session's write-gate policy.</param>
    /// <param name="session">Active session, for the session-created tag registry.</param>
    /// <returns>A refusal, or an allow decision carrying the disclosure when ordinary <c>logix_write</c> would deny.</returns>
    /// <param name="allowlist">Optional write allowlist; when active, a test may only set tags matching it.</param>
    public static TestWriteDecision Evaluate(string tagName, WriteGatePolicy policy, TagSessionService session,
        WriteAllowlist? allowlist = null)
    {
        var decision = EvaluateRules(tagName, policy, session);
        if (decision.Allowed && allowlist is not null && !allowlist.Permits(tagName))
            return Refuse(tagName, nameof(DenyReason.NotOnAllowlist),
                $"Tag '{BaseName(tagName)}' is not listed in the write allowlist file '{allowlist.Path}' " +
                $"({WriteAllowlist.EnvVar}); with an allowlist active a test may only set listed tags.");
        return decision;
    }

    private static TestWriteDecision EvaluateRules(string tagName, WriteGatePolicy policy, TagSessionService session)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(session);

        if (tagName.StartsWith("Program:", StringComparison.OrdinalIgnoreCase))
            return Refuse(tagName, "ProgramScoped",
                "Program-scoped tags and aliases are never written by a test: whether program logic drives them cannot be established.");

        var baseName = BaseName(tagName);

        if (session.TryGetSessionCreatedTagEntry(baseName, out var created))
            return Refuse(tagName, nameof(DenyReason.CreatedByThisSession),
                $"Tag '{baseName}' was created by this session via proposal '{created!.ProposalId}'; " +
                "a test does not bypass the engineer's External Access grant.");

        var deny = policy.Denies.FirstOrDefault(d => string.Equals(d.Tag, baseName, StringComparison.OrdinalIgnoreCase));
        if (deny is not null)
        {
            if (deny.Reason == DenyReason.ProgramWritten)
                return Refuse(tagName, nameof(DenyReason.ProgramWritten), deny.Provenance);

            if (!deny.ExternalAccess.Equals(ReadWrite, StringComparison.OrdinalIgnoreCase))
                return Refuse(tagName, nameof(DenyReason.ExternalAccessNotReadWrite),
                    $"Tag '{baseName}' has External Access '{deny.ExternalAccess}'; " +
                    "a test does not bypass the engineer's write grant.");

            // SafetyTag, NotDesignated, UnknownWriteContext: allowed in a test, disclosed.
            return new TestWriteDecision(null,
                new TestWriteDisclosure(tagName, deny.Reason.ToString(), deny.Provenance));
        }

        var grant = policy.Grants.FirstOrDefault(g => string.Equals(g.Tag, baseName, StringComparison.OrdinalIgnoreCase));
        if (grant is not null)
        {
            return grant.ExternalAccess.Equals(ReadWrite, StringComparison.OrdinalIgnoreCase)
                ? new TestWriteDecision(null, null)
                : Refuse(tagName, nameof(DenyReason.ExternalAccessNotReadWrite),
                    $"Tag '{baseName}' has External Access '{grant.ExternalAccess}'.");
        }

        return Refuse(tagName, "NotInPolicy",
            $"Tag '{baseName}' is not in the write-gate policy, so it cannot be shown that program logic does not write it.");
    }

    /// <summary>Resolves a member path or array element (<c>Tag.Member</c>, <c>Tag[2]</c>) to its base tag name.</summary>
    /// <param name="tagName">Tag reference.</param>
    /// <returns>The base tag name.</returns>
    public static string BaseName(string tagName)
    {
        var cut = tagName.IndexOfAny(['.', '[']);
        return cut >= 0 ? tagName[..cut] : tagName;
    }

    private static TestWriteDecision Refuse(string tag, string reason, string provenance) =>
        new(new TestWriteRefusal(tag, reason, provenance), null);
}
