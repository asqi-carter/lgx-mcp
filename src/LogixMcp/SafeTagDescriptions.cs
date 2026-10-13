using L5Sharp.Core;

/// <summary>
/// Reads tag descriptions the way <c>Tag.Description</c> does, but survives a project containing a
/// rung L5Sharp cannot tokenise (for example <c>MOVE(\P2.Src,Dst)</c>). <c>Tag.Description</c> builds
/// L5Sharp's whole-project index, which parses every rung and throws on such a rung; once that happens
/// this reader stops trying it (it would only fail again, slowly, for every tag) and reads each tag's own
/// XML instead (<see cref="GateDescriptions.TagDescription"/>). Alias tags then show their own description, not the alias target's.
/// Not thread-safe; create one per request.
/// </summary>
internal sealed class SafeTagDescriptions
{
    private bool _indexUnavailable;

    /// <summary>The description of <paramref name="tag"/>, or <c>null</c> when none is declared.</summary>
    /// <param name="tag">The tag to describe.</param>
    /// <returns>The description text, or <c>null</c>.</returns>
    public string? Read(Tag tag)
    {
        if (!_indexUnavailable)
        {
            try { return tag.Description; }
            catch (ArgumentException) { _indexUnavailable = true; }
        }
        return GateDescriptions.TagDescription(tag);
    }
}
