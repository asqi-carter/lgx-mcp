#if LOGIX_SDK
using System.Xml.Linq;

/// <summary>
/// Builds the L5X import fragment for <c>logix_propose_rung_edit</c> from typed
/// <see cref="RungInput"/>/<see cref="TagInput"/> values,
/// so agents never hand-write an <c>RSLogix5000Content</c> envelope.
///
/// <para>
/// <b>Online</b> (<see cref="BuildOnline"/>): the envelope is the real SDK export of the
/// target routine with its rungs stripped (see
/// <c>ProposeRungEditHandler.ExportAndStripRungsAsync</c>), so every SDK-required root
/// attribute (<c>SchemaRevision</c>, <c>SoftwareRevision</c>, <c>TargetName</c>, …) and
/// <c>Use</c> marker is preserved exactly as exported. The builder only writes rungs into
/// the routine's <c>RLLContent</c> and tags into <c>Controller &gt; Tags Use="Context"</c>.
/// </para>
/// <para>
/// <b>Offline</b> (<see cref="BuildOffline"/>): no SDK is involved, so the minimal
/// shape the offline path parses is built, taking the root attributes from the open L5X
/// file's <c>RSLogix5000Content</c> element.
/// </para>
/// </summary>
public static class RungFragmentBuilder
{
    /// <summary>
    /// Builds an import fragment from an SDK export envelope.
    /// </summary>
    /// <param name="exportEnvelope">
    /// L5X text of a routine export (normally already rung-stripped). Any
    /// <c>Rung</c> elements it still carries in <c>RLLContent</c> are removed.
    /// </param>
    /// <param name="insertPosition">Zero-based position of the first typed rung; rungs are numbered from it.</param>
    /// <param name="rungs">Typed rungs (text already validated/normalized); may be empty for a tag-only fragment.</param>
    /// <param name="tags">Typed tags; may be <c>null</c> or empty.</param>
    /// <returns>The fragment XML text, with an XML declaration.</returns>
    /// <exception cref="InvalidOperationException">
    /// The envelope is not valid XML or has no <c>Controller</c>/<c>RLLContent</c> to write into.
    /// </exception>
    public static string BuildOnline(
        string                    exportEnvelope,
        uint                      insertPosition,
        IReadOnlyList<RungInput>  rungs,
        IReadOnlyList<TagInput>?  tags)
    {
        XDocument doc;
        try
        {
            doc = SafeXml.ParseDocument(exportEnvelope);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidOperationException(
                $"The exported routine envelope is not valid XML: {ex.Message}", ex);
        }

        var controller = doc.Root?.Element("Controller")
            ?? throw new InvalidOperationException(
                "The exported routine envelope has no <Controller> element.");

        // The target routine's RLLContent, never the first one in the document: an export with
        // context AOIs lists their Logic routines first, and writing there replaces the AOI's
        // logic (online: XMLSrv_E_ONLINE_OVERWRITE_UDI). A routine export marks the target
        // routine Use="Target"; without the marker, take the first RLLContent outside an AOI.
        var rll = TargetRllContent(doc)
            ?? throw new InvalidOperationException(
                "The exported routine envelope has no <RLLContent> element in its target routine " +
                "(only RLL routines support typed rungs).");

        rll.Elements("Rung").Remove();
        AddRungs(rll, insertPosition, rungs);

        if (tags is { Count: > 0 })
            AddTags(controller, tags);

        return SerializeWithDeclaration(doc);
    }

    private static XElement? TargetRllContent(XDocument doc)
    {
        var target = doc.Descendants("Routine")
            .FirstOrDefault(r => string.Equals((string?)r.Attribute("Use"), "Target", StringComparison.OrdinalIgnoreCase));
        if (target is not null)
            return ProposeRungEditHandler.EnsureRllContent(target);

        return doc.Descendants("RLLContent")
            .FirstOrDefault(e => !e.Ancestors("AddOnInstructionDefinition").Any());
    }

    /// <summary>
    /// Builds the minimal import fragment the offline path parses, taking the envelope
    /// attributes from the open L5X file's root element.
    /// </summary>
    /// <param name="projectRoot">The open project file's <c>RSLogix5000Content</c> root element.</param>
    /// <param name="programName">Target program name (from the routine XPath).</param>
    /// <param name="routineName">Target routine name (from the routine XPath).</param>
    /// <param name="insertPosition">Zero-based position of the first typed rung.</param>
    /// <param name="rungs">Typed rungs.</param>
    /// <param name="tags">Typed tags; may be <c>null</c> or empty.</param>
    /// <returns>The fragment XML text, with an XML declaration.</returns>
    public static string BuildOffline(
        XElement                  projectRoot,
        string                    programName,
        string                    routineName,
        uint                      insertPosition,
        IReadOnlyList<RungInput>  rungs,
        IReadOnlyList<TagInput>?  tags)
    {
        var rll = new XElement("RLLContent");
        AddRungs(rll, insertPosition, rungs);

        var controller = new XElement("Controller");
        var sourceController = projectRoot.Element("Controller");
        if (sourceController?.Attribute("Name") is { } controllerName)
            controller.SetAttributeValue("Name", controllerName.Value);

        if (tags is { Count: > 0 })
            AddTags(controller, tags);

        controller.Add(
            new XElement("Programs",
                new XElement("Program",
                    new XAttribute("Name", programName),
                    new XElement("Routines",
                        new XElement("Routine",
                            new XAttribute("Name", routineName),
                            new XAttribute("Type", "RLL"),
                            rll)))));

        // Root name and attributes are taken from the open file's own root element.
        var root = new XElement(projectRoot.Name,
            projectRoot.Attributes()
                .Where(a => !a.IsNamespaceDeclaration)
                .Select(a => new XAttribute(a.Name, a.Value)),
            controller);

        return SerializeWithDeclaration(new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root));
    }

    /// <summary>
    /// Default <c>Radix</c> for a data type, or <c>null</c> when none applies (structures,
    /// timers, counters, strings, … — the SDK accepts a tag without it).
    /// </summary>
    /// <param name="dataType">The tag's data type name, e.g. <c>DINT</c>.</param>
    public static string? DefaultRadix(string dataType) =>
        dataType.Trim().ToUpperInvariant() switch
        {
            "BOOL" or "SINT" or "INT" or "DINT" or "LINT" or
            "USINT" or "UINT" or "UDINT" or "ULINT" => "Decimal",
            "REAL" or "LREAL"                       => "Float",
            _                                       => null,
        };

    // ── External Access policy ────────────────────────────────────────────────

    /// <summary>External Access value written on a created tag whose type has no special rule.</summary>
    internal const string DefaultExternalAccess = "Read Only";

    /// <summary>External Access value written on created tags whose type Logix rejects as Read Only.</summary>
    internal const string ReadWriteExternalAccess = "Read/Write";

    /// <summary>
    /// Data types Logix refuses to import as <c>Read Only</c> ("Incompatible External Access
    /// Value"), mapped to the reason reported in <c>externalAccessForced</c>. HMIBC, ALARM_DIGITAL and
    /// ALARM_ANALOG are confirmed on the bench; a further type the importer names is reported by
    /// <see cref="DetectReadOnlyRejectedTypes"/> as a typed error until it is added here.
    /// </summary>
    private static readonly Dictionary<string, string> ReadWriteRequiredReasons =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["HMIBC"] = "HMIBC tags cannot be imported External Access Read Only " +
                        "(Logix rejects it as 'Incompatible External Access Value')",
            ["ALARM_DIGITAL"] = "ALARM_DIGITAL tags cannot be imported External Access Read Only " +
                                "(Logix rejects it as 'Incompatible External Access Value')",
            ["ALARM_ANALOG"] = "ALARM_ANALOG tags cannot be imported External Access Read Only " +
                               "(Logix rejects it as 'Incompatible External Access Value')",
        };

    private static readonly System.Text.RegularExpressions.Regex IncompatibleExternalAccess = new(
        @"Incompatible\s+External\s+Access", System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex NamedDataType = new(
        @"(?:data\s*type|type)\s*[:=]?\s*['""`]?(?<t>[A-Za-z_][A-Za-z0-9_]*)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True when an importer message says a tag's External Access value is incompatible with its type.</summary>
    /// <param name="text">A reject reason's text.</param>
    internal static bool IsIncompatibleExternalAccess(string? text) =>
        !string.IsNullOrEmpty(text) && IncompatibleExternalAccess.IsMatch(text);

    /// <summary>
    /// The data types behind an "Incompatible External Access Value" rejection (G26): for each such message, the
    /// declared tag it names (its type is looked up in <paramref name="declaredTags"/>), else a type the message
    /// names itself, else - when nothing is named - the types of the declared tags this server created Read Only.
    /// Types already given Read/Write by <see cref="ReadWriteRequiredReason"/> are not listed: they cannot be the cause.
    /// </summary>
    /// <param name="messages">The reject reasons' texts.</param>
    /// <param name="declaredTags">Tag name to data type, for the tags the rejected import declared.</param>
    /// <returns>Distinct type names in upper case; empty when no message is an External Access rejection.</returns>
    internal static IReadOnlyList<string> DetectReadOnlyRejectedTypes(
        IEnumerable<string?> messages, IReadOnlyDictionary<string, string> declaredTags)
    {
        var types = new List<string>();
        void AddType(string? t)
        {
            if (string.IsNullOrWhiteSpace(t) || ReadWriteRequiredReason(t) is not null) return;
            var u = t.Trim().ToUpperInvariant();
            if (!types.Contains(u)) types.Add(u);
        }

        foreach (var message in messages.Where(IsIncompatibleExternalAccess))
        {
            int before = types.Count;
            foreach (var (name, type) in declaredTags)
                if (System.Text.RegularExpressions.Regex.IsMatch(message!,
                        $@"(?<![A-Za-z0-9_]){System.Text.RegularExpressions.Regex.Escape(name)}(?![A-Za-z0-9_])"))
                    AddType(type);
            if (types.Count == before)
                foreach (System.Text.RegularExpressions.Match m in NamedDataType.Matches(message!))
                    if (!m.Groups["t"].Value.Equals("Access", StringComparison.OrdinalIgnoreCase)
                        && !m.Groups["t"].Value.Equals("Value", StringComparison.OrdinalIgnoreCase))
                        AddType(m.Groups["t"].Value);
            if (types.Count == before)
                foreach (var type in declaredTags.Values)
                    AddType(type);
        }
        return types;
    }

    /// <summary>
    /// The reason a tag of <paramref name="dataType"/> must be created <c>Read/Write</c>,
    /// or <c>null</c> when it takes the default <c>Read Only</c>.
    /// </summary>
    /// <param name="dataType">Tag data type name (surrounding whitespace ignored).</param>
    internal static string? ReadWriteRequiredReason(string? dataType) =>
        dataType is not null && ReadWriteRequiredReasons.TryGetValue(dataType.Trim(), out var reason)
            ? reason
            : null;

    /// <summary>
    /// The External Access a server-created tag of <paramref name="dataType"/> is given:
    /// <c>Read/Write</c> where Logix rejects Read Only, otherwise <c>Read Only</c>.
    /// </summary>
    /// <param name="dataType">Tag data type name.</param>
    internal static string ExternalAccessFor(string? dataType) =>
        ReadWriteRequiredReason(dataType) is null ? DefaultExternalAccess : ReadWriteExternalAccess;

    /// <summary>
    /// Formats an <c>externalAccessForced</c> entry for a tag given a non-default access.
    /// </summary>
    /// <param name="tagName">Tag name.</param>
    /// <param name="reason">Why the default was not used.</param>
    internal static string DescribeForcedReadWrite(string tagName, string reason) =>
        $"{tagName}: {ReadWriteExternalAccess} ({reason})";

    /// <summary>
    /// Entries for <c>externalAccessForced</c> for typed tags that <see cref="AddTags"/> creates
    /// as <c>Read/Write</c>: those of a Read/Write-required type whose name is not in
    /// <paramref name="census"/> (pre-existing tags are never modified). Deduplicated by name.
    /// </summary>
    /// <param name="tags">Typed tags passed to the builder; may be <c>null</c>.</param>
    /// <param name="census">Names of tags that already exist, compared case-insensitively.</param>
    /// <returns>Entries in input order; empty when none.</returns>
    internal static List<string> ForcedReadWriteEntries(
        IEnumerable<TagInput>? tags, IReadOnlyCollection<string> census)
    {
        var result = new List<string>();
        if (tags is null)
            return result;
        var known = new HashSet<string>(census, StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            if (ReadWriteRequiredReason(tag.DataType) is not { } reason || !known.Add(tag.Name))
                continue;
            result.Add(DescribeForcedReadWrite(tag.Name, reason));
        }
        return result;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void AddRungs(XElement rllContent, uint insertPosition, IReadOnlyList<RungInput> rungs)
    {
        for (int i = 0; i < rungs.Count; i++)
        {
            var rung = new XElement("Rung",
                new XAttribute("Number", (long)insertPosition + i),
                new XAttribute("Type", "N"));
            if (!string.IsNullOrEmpty(rungs[i].Comment))
                // The raw string goes to the XElement (not a CDATA section): the writer then escapes every character
                // the target encoding cannot hold as a numeric reference, so a non-BMP character (an emoji) round-trips.
                rung.Add(new XElement("Comment", rungs[i].Comment!));
            rung.Add(new XElement("Text", CdataNodes(rungs[i].Text)));
            rllContent.Add(rung);
        }
    }

    private static void AddTags(XElement controller, IReadOnlyList<TagInput> tags)
    {
        var context = controller.Elements("Tags").FirstOrDefault(e =>
            string.Equals((string?)e.Attribute("Use"), "Context", StringComparison.OrdinalIgnoreCase));

        if (context is null)
        {
            // Same position the exporter (and MergeBufferedTagsIntoFragment) uses:
            // after DataTypes when present, otherwise before Programs, otherwise last.
            context = new XElement("Tags", new XAttribute("Use", "Context"));
            if (controller.Element("DataTypes") is { } dataTypes)
                dataTypes.AddAfterSelf(context);
            else if (controller.Element("Programs") is { } programs)
                programs.AddBeforeSelf(context);
            else
                controller.Add(context);
        }

        var declared = context.Elements("Tag")
            .Select(t => (string?)t.Attribute("Name"))
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in tags)
        {
            // An export's own context declaration of the same tag is the controller's
            // real definition; keep it rather than duplicating the name.
            if (!declared.Add(tag.Name))
                continue;

            var el = new XElement("Tag",
                new XAttribute("Name", tag.Name),
                new XAttribute("TagType", "Base"),
                new XAttribute("DataType", tag.DataType.Trim()));

            var radix = string.IsNullOrWhiteSpace(tag.Radix) ? DefaultRadix(tag.DataType) : tag.Radix.Trim();
            if (radix is not null)
                el.SetAttributeValue("Radix", radix);
            if (!string.IsNullOrWhiteSpace(tag.Dimensions))
                el.SetAttributeValue("Dimensions", tag.Dimensions.Trim());
            el.SetAttributeValue("Constant", "false");
            // External Access is the engineer-owned write grant; created tags are Read Only,
            // except data types Logix rejects as Read Only (see ReadWriteRequiredReason).
            el.SetAttributeValue("ExternalAccess", ExternalAccessFor(tag.DataType));

            if (!string.IsNullOrEmpty(tag.Description))
                el.Add(new XElement("Description", CdataNodes(tag.Description!)));

            context.Add(el);
        }
    }

    /// <summary>
    /// CDATA content for <paramref name="text"/>. A literal <c>]]&gt;</c> (which cannot appear
    /// inside one CDATA section) is split across two adjacent sections.
    /// </summary>
    private static IEnumerable<XCData> CdataNodes(string text)
    {
        var parts = text.Split("]]>");
        for (int i = 0; i < parts.Length; i++)
        {
            var piece = (i > 0 ? ">" : "") + parts[i] + (i < parts.Length - 1 ? "]]" : "");
            yield return new XCData(piece);
        }
    }

    private static string SerializeWithDeclaration(XDocument doc) =>
        ProposeRungEditHandler.SerializeWithDeclaration(doc);
}
#endif
