using System.Text.Json.Serialization;
using System.Xml.Linq;
using L5Sharp.Core;

/// <summary>
/// One I/O module in the tree returned by <see cref="ModuleTreeBuilder.Build"/>.
/// </summary>
/// <param name="Name">Module name (e.g. <c>Local</c>, <c>DI_Card</c>).</param>
/// <param name="CatalogNumber">Catalog number (e.g. <c>1756-IB16</c>), or empty.</param>
/// <param name="Revision">Major.Minor firmware revision string, or empty when not defined.</param>
/// <param name="Parent">Parent module name; <c>null</c> for the root (Local) module.</param>
/// <param name="Port">Id of the parent's port this module is attached to, when defined.</param>
/// <param name="SlotOrAddress">
/// Slot number (chassis/backplane port) or IP address / node address (Ethernet and other ports)
/// of the module's own upstream port, or <c>null</c> when the L5X does not define one.
/// </param>
/// <param name="Inhibited">True when the module is inhibited in the project.</param>
/// <param name="MajorFaultOnConnectionLoss">True when connection loss faults the controller (L5X <c>MajorFault</c>).</param>
/// <param name="Description">
/// Customer-authored description wrapped via <see cref="UntrustedWrapper"/>;
/// <c>null</c> when the module has no description.
/// </param>
/// <param name="TagPrefix">
/// Controller-scope tag prefix of the module's input data (<c>Local:&lt;slot&gt;:I</c> for chassis
/// modules, <c>&lt;name&gt;:I</c> for Ethernet modules) when the L5X defines an input connection
/// tag for the module; otherwise <c>null</c>.
/// </param>
/// <param name="Connection">
/// Live CIP connection state. Always <c>null</c> (omitted from JSON) for offline projects.
/// </param>
/// <param name="ConnectionDetail">
/// Why the connection state is what it is (e.g. <c>route type not supported</c> when no CIP route
/// can be built). <c>null</c> (omitted from JSON) when there is nothing to add or for offline projects.
/// </param>
/// <param name="UpstreamPortType">
/// L5X <c>Type</c> of the module's own upstream port (e.g. <c>ICP</c>, <c>Ethernet</c>); used to
/// build CIP routes and never serialized.
/// </param>
/// <param name="Identity">
/// CIP Identity read through the controller when the module answered a probe (<c>probe: true</c>);
/// otherwise <c>null</c> (omitted from JSON).
/// </param>
/// <param name="IdentityMismatch">
/// <c>true</c> when the configured major revision is inconsistent with the identity, <c>false</c> when
/// a comparison was possible and agreed, <c>null</c> (omitted) when nothing could be compared.
/// </param>
public record ModuleEntry(
    [property: JsonPropertyName("name")]                       string  Name,
    [property: JsonPropertyName("catalogNumber")]              string  CatalogNumber,
    [property: JsonPropertyName("revision")]                   string  Revision,
    [property: JsonPropertyName("parent")]                     string? Parent,
    [property: JsonPropertyName("port")]                       int?    Port,
    [property: JsonPropertyName("slotOrAddress")]              string? SlotOrAddress,
    [property: JsonPropertyName("inhibited")]                  bool    Inhibited,
    [property: JsonPropertyName("majorFaultOnConnectionLoss")] bool    MajorFaultOnConnectionLoss,
    [property: JsonPropertyName("description")]                string? Description,
    [property: JsonPropertyName("tagPrefix")]                  string? TagPrefix,
    [property: JsonPropertyName("connection"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Connection = null,
    [property: JsonPropertyName("connectionDetail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ConnectionDetail = null,
    [property: JsonIgnore]
    string? UpstreamPortType = null,
    [property: JsonPropertyName("identity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ModuleIdentity? Identity = null,
    [property: JsonPropertyName("identityMismatch"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? IdentityMismatch = null);

/// <summary>Identity object a module returned to a routed CIP Identity read.</summary>
/// <param name="VendorId">CIP vendor ID.</param>
/// <param name="DeviceType">CIP device type.</param>
/// <param name="ProductCode">CIP product code.</param>
/// <param name="Revision">Firmware revision as <c>major.minor</c>.</param>
/// <param name="RevisionMajor">Major firmware revision (not serialized; used for the mismatch check).</param>
/// <param name="Status">Identity status word.</param>
/// <param name="Serial">Serial number.</param>
/// <param name="ProductName">Product name.</param>
public record ModuleIdentity(
    [property: JsonPropertyName("vendorId")]    int    VendorId,
    [property: JsonPropertyName("deviceType")]  int    DeviceType,
    [property: JsonPropertyName("productCode")] int    ProductCode,
    [property: JsonPropertyName("revision")]    string Revision,
    [property: JsonIgnore]                      int    RevisionMajor,
    [property: JsonPropertyName("status")]      int    Status,
    [property: JsonPropertyName("serial")]      uint   Serial,
    [property: JsonPropertyName("productName")] string ProductName);

/// <summary>
/// Shared builder that turns the <c>&lt;Modules&gt;</c> section of a detailed L5X into an
/// ordered module tree. Used by <see cref="ListModulesHandler"/> (and intended for the
/// <c>logix_document</c> modules section). Read-only: it never touches the controller.
/// </summary>
public static class ModuleTreeBuilder
{
    /// <summary>
    /// Builds the module list ordered depth-first from the root (<c>Local</c>) module.
    /// Children of a module appear in L5X document order. Modules whose parent is
    /// missing or cyclic are appended after the rooted tree so nothing is silently dropped.
    /// </summary>
    /// <param name="project">The parsed L5X project.</param>
    /// <returns>Depth-first ordered module entries (empty when the project has no modules).</returns>
    public static IReadOnlyList<ModuleEntry> Build(L5X project)
    {
        var modules = project.Modules.ToList();
        var byName  = new Dictionary<string, Module>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in modules)
            byName.TryAdd(NameOf(m), m);

        var children = new Dictionary<string, List<Module>>(StringComparer.OrdinalIgnoreCase);
        var roots    = new List<Module>();
        foreach (var m in modules)
        {
            var parent = ParentNameOf(m);
            if (parent is null || !byName.ContainsKey(parent))
                roots.Add(m);
            else
            {
                if (!children.TryGetValue(parent, out var list))
                    children[parent] = list = new List<Module>();
                list.Add(m);
            }
        }

        var result  = new List<ModuleEntry>();
        var visited = new HashSet<Module>();

        void Visit(Module m)
        {
            if (!visited.Add(m)) return;
            result.Add(ToEntry(m, byName));
            if (children.TryGetValue(NameOf(m), out var kids))
                foreach (var k in kids) Visit(k);
        }

        // The controller's own module (Local) leads, then any other roots.
        foreach (var r in roots.OrderBy(r => string.Equals(NameOf(r), "Local", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            Visit(r);

        // Unreachable cycles: keep them visible.
        foreach (var m in modules)
            Visit(m);

        return result;
    }

    private static string NameOf(Module m) => m.Name?.ToString() ?? string.Empty;

    /// <summary>Parent name, or <c>null</c> when the module is a root (no parent, or its own parent).</summary>
    private static string? ParentNameOf(Module m)
    {
        var parent = m.ParentModule?.ToString();
        if (string.IsNullOrEmpty(parent)) return null;
        return string.Equals(parent, NameOf(m), StringComparison.OrdinalIgnoreCase) ? null : parent;
    }

    private static ModuleEntry ToEntry(Module m, IReadOnlyDictionary<string, Module> byName)
    {
        var parent = ParentNameOf(m);
        var port   = UpstreamPort(m);
        var desc   = m.Description;

        return new ModuleEntry(
            Name:                       NameOf(m),
            CatalogNumber:              m.CatalogNumber ?? string.Empty,
            Revision:                   m.Revision?.ToString() ?? string.Empty,
            Parent:                     parent,
            Port:                       parent is null ? null : m.ParentModPortId is { } pid ? (int)pid : null,
            SlotOrAddress:              AddressOf(port),
            Inhibited:                  m.Inhibited,
            MajorFaultOnConnectionLoss: m.MajorFault,
            Description:                string.IsNullOrWhiteSpace(desc) ? null : UntrustedWrapper.WrapTagDescription(desc),
            TagPrefix:                  TagPrefixOf(m, parent, port, byName),
            UpstreamPortType:           string.IsNullOrWhiteSpace(port?.Type) ? null : port.Type);
    }

    private static string? AddressOf(Port? port)
    {
        if (port is null) return null;
        try
        {
            var text = port.Address?.ToString();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (ArgumentException)
        {
            // L5Sharp rejects an empty or absent Address attribute; report it as "no address".
            return null;
        }
    }

    /// <summary>The module's own upstream port (the one facing its parent), falling back to its first port.</summary>
    private static Port? UpstreamPort(Module m)
    {
        var ports = m.Ports?.ToList();
        if (ports is null || ports.Count == 0) return null;
        return ports.FirstOrDefault(p => p.Upstream) ?? ports[0];
    }

    /// <summary>
    /// Tag prefix for the module's input data. Only reported when the L5X defines an input
    /// connection tag (<c>Communications/Connections/Connection/InputTag</c>) for the module.
    /// Chassis: <c>&lt;chassis owner&gt;:&lt;slot&gt;:I</c> (<c>Local:&lt;slot&gt;:I</c> for the local chassis);
    /// Ethernet: <c>&lt;name&gt;:I</c>.
    /// </summary>
    private static string? TagPrefixOf(
        Module m, string? parent, Port? port, IReadOnlyDictionary<string, Module> byName)
    {
        if (parent is null || port is null) return null;
        if (!DefinesInputTag(m)) return null;

        if (port.IsEthernet)
            return $"{NameOf(m)}:I";

        if (port.IsChassis && AddressOf(port) is { } slot)
        {
            var owner = byName.TryGetValue(parent, out var p) ? NameOf(p) : parent;
            return $"{owner}:{slot}:I";
        }

        return null;
    }

    private static bool DefinesInputTag(Module m) =>
        m.Serialize()
         .Elements("Communications")
         .Elements("Connections")
         .Elements("Connection")
         .Any(c => c.Element("InputTag") is not null);
}
