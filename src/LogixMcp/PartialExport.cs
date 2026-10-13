using System.Xml.Linq;

/// <summary>
/// Describes an L5X that is a partial (routine, program, ...) export rather than a full
/// controller export. A partial export opens for reads and analysis, but it never derives
/// a write gate: the file does not contain the logic of the rest of the controller, so
/// "no program writes this tag" cannot be established from it.
/// </summary>
/// <param name="ExportTarget">The root <c>TargetType</c> (e.g. <c>"Routine"</c>, <c>"Program"</c>).</param>
public sealed record PartialExportInfo(string ExportTarget)
{
    /// <summary>Why a partial export has no write gate; shown in errors and the gate report.</summary>
    public const string Reason =
        "the open project is a partial (routine/program) export; " +
        "open a full controller export to derive the write gate";

    /// <summary>
    /// Returns a partial-export description when <paramref name="root"/> (the
    /// <c>RSLogix5000Content</c> element) is not a full controller export, otherwise <c>null</c>.
    /// A file is partial when the root <c>TargetType</c> is present and not <c>Controller</c>,
    /// when <c>ContainsContext="true"</c>, or when the <c>Controller</c> has <c>Use="Context"</c>.
    /// </summary>
    /// <param name="root">The root element of the L5X document.</param>
    /// <returns>The partial-export info, or <c>null</c> for a full controller export.</returns>
    public static PartialExportInfo? Detect(XElement? root)
    {
        if (root is null) return null;

        var targetType = (string?)root.Attribute("TargetType");
        var containsContext = string.Equals(
            ((string?)root.Attribute("ContainsContext"))?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        var controllerUse = (string?)root.Element("Controller")?.Attribute("Use");

        var targetPartial = !string.IsNullOrWhiteSpace(targetType) &&
            !string.Equals(targetType.Trim(), "Controller", StringComparison.OrdinalIgnoreCase);
        var contextUse = string.Equals(controllerUse?.Trim(), "Context", StringComparison.OrdinalIgnoreCase);

        if (!targetPartial && !containsContext && !contextUse) return null;

        return new PartialExportInfo(
            string.IsNullOrWhiteSpace(targetType) ? "Controller" : targetType.Trim());
    }
}
