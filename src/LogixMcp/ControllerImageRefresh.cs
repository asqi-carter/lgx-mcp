using System.Xml.Linq;
using L5Sharp.Core;

/// <summary>
/// A live source that can export parts of the controller as L5X text, so the session's controller image can be
/// refreshed after a change without a full upload. The SDK service implements it with a
/// partial export of the affected programs.
/// </summary>
public interface IControllerImageSource
{
    /// <summary>Exports one whole program (tags and routines, pending edits included) as L5X text.</summary>
    /// <param name="program">Program name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The L5X text.</returns>
    /// <exception cref="InvalidOperationException">The export failed; the message says why.</exception>
    Task<string> ExportProgramL5xAsync(string program, CancellationToken ct = default);

    /// <summary>Exports the controller-scoped tags as L5X text.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The L5X text.</returns>
    /// <exception cref="InvalidOperationException">The export failed; the message says why.</exception>
    Task<string> ExportControllerTagsL5xAsync(CancellationToken ct = default);
}

/// <summary>Pure helpers that merge a partial export into a controller image and derive the accepted image.</summary>
public static class ControllerImageRefresh
{
    /// <summary>
    /// Rung <c>Type</c> codes of rungs that exist only as staged or pending content: they are not part of the
    /// controller's accepted logic. Insert (<c>e</c>, <c>I</c>) and replacement (<c>er</c>, <c>IR</c>) rungs.
    /// </summary>
    private static readonly HashSet<string> PendingOnlyCodes = new(StringComparer.Ordinal) { "e", "er", "I", "IR" };

    /// <summary>
    /// Rung <c>Type</c> codes of originals that a pending edit displaces or removes: they are still accepted
    /// logic until the edit is published, so the accepted image keeps them as ordinary rungs.
    /// </summary>
    private static readonly HashSet<string> DisplacedOriginalCodes = new(StringComparer.Ordinal) { "rN", "dN", "R", "D" };

    /// <summary>
    /// Replaces the named program of <paramref name="image"/> with the same-named program of the export, or adds it.
    /// </summary>
    /// <param name="image">The image to update in place.</param>
    /// <param name="exportL5x">L5X text of a program export.</param>
    /// <param name="program">Program name that was exported.</param>
    /// <returns><c>true</c> when the export held the program and the image now carries it.</returns>
    public static bool MergeProgram(L5X image, string exportL5x, string program)
    {
        ArgumentNullException.ThrowIfNull(image);
        var fresh = SafeXml.ParseL5x(exportL5x).Programs
            .FirstOrDefault(p => string.Equals(p.Name.ToString(), program, StringComparison.OrdinalIgnoreCase));
        if (fresh is null) return false;
        image.Programs.RemoveIf(p => string.Equals(p.Name.ToString(), program, StringComparison.OrdinalIgnoreCase));
        image.Programs.Add(fresh);
        return true;
    }

    /// <summary>Replaces (or adds) each controller-scoped tag of <paramref name="image"/> with the exported one.</summary>
    /// <param name="image">The image to update in place.</param>
    /// <param name="exportL5x">L5X text of a controller-tags export.</param>
    /// <returns>The number of tags merged.</returns>
    public static int MergeControllerTags(L5X image, string exportL5x)
    {
        ArgumentNullException.ThrowIfNull(image);
        var fresh = SafeXml.ParseL5x(exportL5x).Tags.ToList();
        foreach (var tag in fresh)
        {
            var name = tag.Name.ToString();
            image.Tags.RemoveIf(t => string.Equals(t.Name.ToString(), name, StringComparison.OrdinalIgnoreCase));
            image.Tags.Add(tag);
        }
        return fresh.Count;
    }

    /// <summary>
    /// Derives an image holding only the pending-only rungs (staged or pending-zone inserts and replacements) of a
    /// session image, keeping their numbers as the session image shows them. Used to find writers that the accepted
    /// image leaves out. Returns <c>null</c> when the session image holds no pending-only rung.
    /// </summary>
    /// <param name="sessionImage">The session image, staged rungs included.</param>
    /// <returns>An image whose routines hold only the pending-only rungs, or <c>null</c> when there are none.</returns>
    public static L5X? PendingOnly(L5X sessionImage)
    {
        ArgumentNullException.ThrowIfNull(sessionImage);
        var doc = SafeXml.ParseDocument(sessionImage.ToString());
        var any = false;
        foreach (var rung in doc.Descendants("Rung").ToList())
        {
            var code = (string?)rung.Attribute("Type");
            if (code is not null && PendingOnlyCodes.Contains(code)) any = true;
            else rung.Remove();
        }
        return any ? SafeXml.ToL5x(doc) : null;
    }

    /// <summary>
    /// Derives the controller's accepted image from a session image: staged and pending-zone rungs are dropped and
    /// the originals they displace are restored to ordinary rungs. Returns <paramref name="sessionImage"/> itself
    /// when it holds nothing pending.
    /// </summary>
    /// <param name="sessionImage">The session image, staged rungs included.</param>
    /// <returns>The accepted image.</returns>
    public static L5X Accepted(L5X sessionImage)
    {
        ArgumentNullException.ThrowIfNull(sessionImage);
        var doc = SafeXml.ParseDocument(sessionImage.ToString());
        var rungs = doc.Descendants("Rung").ToList();
        var touched = false;
        foreach (var rung in rungs)
        {
            var code = (string?)rung.Attribute("Type");
            if (code is null) continue;
            if (PendingOnlyCodes.Contains(code)) { rung.Remove(); touched = true; }
            else if (DisplacedOriginalCodes.Contains(code)) { rung.SetAttributeValue("Type", "N"); touched = true; }
        }
        if (!touched) return sessionImage;

        // Renumber the surviving rungs of every routine so numbers stay contiguous.
        foreach (var content in doc.Descendants("RLLContent"))
        {
            var n = 0;
            foreach (var rung in content.Elements("Rung")) rung.SetAttributeValue("Number", n++);
        }
        return SafeXml.ToL5x(doc);
    }
}
