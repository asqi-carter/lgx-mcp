// ── RoutineRungSelector — which <Rung> elements of a routine export belong to the target routine (N1) ──
//
// A routine export (and a caller's export-shaped fragment) is a whole-controller envelope: besides the
// target routine it can carry <AddOnInstructionDefinitions> (whose <Logic>/<RLLContent> holds rungs that
// legitimately use the AOI's own parameter names such as In1/Out1) and other routines' RLLContent. The
// pre-check, mnemonic check, census, copy check and write scan must see ONLY the target routine's rungs,
// i.e. Programs/Program/Routines/Routine[@Name=target]/RLLContent/Rung.
using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>Selects the rungs of the target routine from a routine export or import fragment.</summary>
public static class RoutineRungSelector
{
    /// <summary>
    /// Resolves <c>Controller/Programs/Program[@Name='P']/Routines/Routine[@Name='R']</c> (single or double
    /// quotes) to its program and routine names. Returns <c>false</c> for any other shape.
    /// </summary>
    /// <param name="routineXPath">The routine XPath.</param>
    /// <param name="program">The program name, or empty.</param>
    /// <param name="routine">The routine name, or empty.</param>
    /// <returns><c>true</c> when the XPath names a routine.</returns>
    public static bool TryParseRoutineXPath(string? routineXPath, out string program, out string routine)
    {
        var m = Regex.Match(
            routineXPath ?? string.Empty,
            @"Program\[@Name=['""]([^'""]+)['""]\]/Routines/Routine\[@Name=['""]([^'""]+)['""]\]");
        program = m.Success ? m.Groups[1].Value : string.Empty;
        routine = m.Success ? m.Groups[2].Value : string.Empty;
        return m.Success;
    }

    /// <summary>
    /// The <c>Rung</c> elements of the target routine: <c>Programs/Program/Routines/Routine[@Name=target]/RLLContent/Rung</c>
    /// (names compared ignoring case). When <paramref name="routineXPath"/> is not a routine XPath, or the
    /// document holds no such routine (a bare <c>RLLContent</c> fragment), every <c>Rung</c> (bare or in <c>RLLContent</c>) that is
    /// not inside an <c>AddOnInstructionDefinition</c> is returned; AOI logic is never part of the result.
    /// </summary>
    /// <param name="doc">The parsed export or fragment.</param>
    /// <param name="routineXPath">The target routine XPath, or <c>null</c>.</param>
    /// <returns>The rungs in document order.</returns>
    public static List<XElement> Select(XDocument doc, string? routineXPath)
    {
        if (TryParseRoutineXPath(routineXPath, out var program, out var routine))
        {
            var matching = doc.Descendants("Program")
                .Where(p => string.Equals((string?)p.Attribute("Name"), program, StringComparison.OrdinalIgnoreCase))
                .SelectMany(p => p.Elements("Routines").Elements("Routine"))
                .Where(r => string.Equals((string?)r.Attribute("Name"), routine, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matching.Count > 0)
                return matching.SelectMany(r => r.Elements("RLLContent").Elements("Rung")).ToList();
        }

        return doc.Descendants("Rung")
            .Where(r => !r.Ancestors().Any(a =>
                a.Name.LocalName is "AddOnInstructionDefinition" or "AddOnInstructionDefinitions"))
            .ToList();
    }

    /// <summary>
    /// The <c>RLLContent</c> element holding the target routine's rungs (the first one), or <c>null</c> when
    /// the document has none outside an Add-On Instruction definition.
    /// </summary>
    /// <param name="doc">The parsed export or fragment.</param>
    /// <param name="routineXPath">The target routine XPath, or <c>null</c>.</param>
    /// <returns>The content element, or <c>null</c>.</returns>
    public static XElement? SelectContent(XDocument doc, string? routineXPath)
    {
        if (TryParseRoutineXPath(routineXPath, out var program, out var routine))
        {
            var content = doc.Descendants("Program")
                .Where(p => string.Equals((string?)p.Attribute("Name"), program, StringComparison.OrdinalIgnoreCase))
                .SelectMany(p => p.Elements("Routines").Elements("Routine"))
                .Where(r => string.Equals((string?)r.Attribute("Name"), routine, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Element("RLLContent"))
                .FirstOrDefault(c => c is not null);
            if (content is not null) return content;
        }

        return doc.Descendants("RLLContent").FirstOrDefault(c => !c.Ancestors().Any(a =>
            a.Name.LocalName is "AddOnInstructionDefinition" or "AddOnInstructionDefinitions"));
    }
}
