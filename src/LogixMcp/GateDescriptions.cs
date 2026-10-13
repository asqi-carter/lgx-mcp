using System.Xml.Linq;
using L5Sharp.Core;

/// <summary>
/// Reads tag and member descriptions straight from the element's own XML. The gate's range
/// parsing uses this instead of <c>Tag.Description</c> / <c>Member.Description</c>, which
/// resolve definition comments through L5Sharp's whole-project index and throw on projects
/// containing content the index cannot tokenise (for example an AOI rung using
/// <c>Units per sec2</c> or <c>% of Time</c>). Nothing here touches that index.
/// </summary>
internal static class GateDescriptions
{
    /// <summary>
    /// Description of a tag. Precedence: the tag's own <c>Description</c> element, then its own
    /// whole-tag <c>Comments/Comment</c> (one with no <c>Operand</c>). Alias targets are not
    /// followed here; the gate resolves alias bounds separately.
    /// </summary>
    /// <param name="tag">The tag to describe.</param>
    /// <returns>The description text, or <c>null</c> when none is declared.</returns>
    internal static string? TagDescription(Tag tag)
    {
        var xml = tag.Serialize();
        return OwnDescription(xml) ?? CommentFor(xml, operand: null);
    }

    /// <summary>
    /// Description of a data type member. Precedence: the member's own <c>Description</c>
    /// element, then a <c>Comments/Comment[@Operand]</c> naming the member on the member or
    /// on the enclosing definition.
    /// </summary>
    /// <param name="member">The <c>Member</c> element from the data type definition.</param>
    /// <returns>The description text, or <c>null</c> when none is declared.</returns>
    internal static string? MemberDescription(XElement member)
    {
        var own = OwnDescription(member);
        if (own is not null)
            return own;

        var name = member.Attribute("Name")?.Value;
        if (string.IsNullOrEmpty(name))
            return null;

        return CommentFor(member, name) ?? CommentFor(member.Parent?.Parent, name);
    }

    private static string? OwnDescription(XElement element)
    {
        var text = element.Element("Description")?.Value;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// The text of the <c>Comments/Comment</c> under <paramref name="element"/> whose
    /// <c>Operand</c> names <paramref name="operand"/> (with or without a leading dot). A null
    /// operand selects the whole-element comment (no <c>Operand</c> attribute).
    /// </summary>
    private static string? CommentFor(XElement? element, string? operand)
    {
        var comments = element?.Element("Comments")?.Elements("Comment");
        if (comments is null)
            return null;

        var wanted = operand?.TrimStart('.');
        foreach (var c in comments)
        {
            var have = c.Attribute("Operand")?.Value?.TrimStart('.');
            var match = wanted is null
                ? string.IsNullOrEmpty(have)
                : string.Equals(have, wanted, StringComparison.OrdinalIgnoreCase);
            if (match && !string.IsNullOrWhiteSpace(c.Value))
                return c.Value;
        }

        return null;
    }
}
