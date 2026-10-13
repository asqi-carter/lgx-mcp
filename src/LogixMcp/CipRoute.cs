using System.Globalization;
using System.Text.RegularExpressions;
using L5Sharp.Core;

/// <summary>The CIP target derived from an SDK comm path: the gateway host and the CIP route path.</summary>
/// <param name="Gateway">Controller IP address (the EtherNet/IP gateway).</param>
/// <param name="Path">CIP path, e.g. <c>1,0</c> (backplane port 1, slot 0).</param>
public sealed record CipTarget(string Gateway, string Path);

/// <summary>
/// Table-driven parser from an SDK comm path (<c>sdkCommPath</c>) to a <see cref="CipTarget"/>.
/// Only single-hop EtherNet/IP forms are accepted; USB, serial, multi-hop and unknown-driver paths
/// are parse failures, which send tag traffic over the SDK instead.
/// </summary>
public static class CipCommPath
{
    private const string Ip      = @"(?<ip>\d{1,3}(?:\.\d{1,3}){3})";
    private const string Driver  = @"(?:EmulateEthernet|AB_ETH(?:IP)?-\d+)";
    private const string Comma   = @"(?<route>\d+(?:,\d+)+)";
    private const string Default = "1,0";

    private sealed record Form(string Name, Regex Pattern, Func<Match, string> PathOf);

    private static Regex Rx(string body) =>
        new($"^{body}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // One row per accepted form; the first matching row wins.
    private static readonly Form[] Forms =
    [
        new("bare ip",                  Rx(Ip),                                         _ => Default),
        new("bare ip + CIP route",      Rx($@"{Ip},{Comma}"),                           m => m.Groups["route"].Value),
        new("driver\\ip",               Rx($@"{Driver}\\{Ip}"),                         _ => Default),
        new("driver\\ip + CIP route",   Rx($@"{Driver}\\{Ip},{Comma}"),                 m => m.Groups["route"].Value),
        new("driver\\ip\\Backplane\\n", Rx($@"{Driver}\\{Ip}\\Backplane\\(?<slot>\d+)"),
            m => $"1,{int.Parse(m.Groups["slot"].Value, CultureInfo.InvariantCulture)}"),
    ];

    /// <summary>Parses a comm path.</summary>
    /// <param name="commPath">The SDK comm path, e.g. <c>EmulateEthernet\192.0.2.220</c>.</param>
    /// <param name="target">The CIP target when the method returns <c>true</c>.</param>
    /// <param name="failure">Why no target could be derived, when the method returns <c>false</c>.</param>
    /// <returns><c>true</c> when the path is a supported single-hop EtherNet/IP form with a valid IPv4 address.</returns>
    public static bool TryParse(string? commPath, out CipTarget? target, out string? failure)
    {
        target  = null;
        failure = null;
        if (string.IsNullOrWhiteSpace(commPath))
        {
            failure = "the comm path is empty";
            return false;
        }

        var trimmed = commPath.Trim();
        foreach (var form in Forms)
        {
            var m = form.Pattern.Match(trimmed);
            if (!m.Success) continue;
            if (!System.Net.IPAddress.TryParse(m.Groups["ip"].Value, out _))
            {
                failure = $"'{m.Groups["ip"].Value}' in the comm path '{trimmed}' is not a valid IPv4 address";
                return false;
            }
            target = new CipTarget(m.Groups["ip"].Value, form.PathOf(m));
            return true;
        }

        failure = $"could not derive a CIP gateway from the comm path '{trimmed}' " +
                  "(supported: <ip>, <ip>,<route>, EmulateEthernet|AB_ETH-n|AB_ETHIP-n\\<ip>[\\Backplane\\<slot>|,<route>])";
        return false;
    }
}

/// <summary>What identifies a controller: its name and serial number. A field is <c>null</c> when the source does not report it.</summary>
/// <param name="Name">Controller name.</param>
/// <param name="Serial">Controller serial number.</param>
public sealed record ControllerIdentity(string? Name, uint? Serial)
{
    /// <summary>
    /// Identity from an uploaded project: the controller name and the <c>ProjectSN</c> attribute
    /// (serial, written <c>16#xxxx_xxxx</c>). <c>null</c> when <paramref name="project"/> is <c>null</c>.
    /// </summary>
    /// <param name="project">The uploaded project, or <c>null</c>.</param>
    public static ControllerIdentity? FromProject(L5X? project)
    {
        if (project is null) return null;
        var controller = project.Controller;
        var raw = controller.Serialize().Attribute("ProjectSN")?.Value;
        return new ControllerIdentity(controller.Name, ParseSerial(raw));
    }

    /// <summary>Parses a serial such as <c>16#1234_ABCD</c>, <c>0x1234ABCD</c> or plain hex digits; <c>null</c> when unparseable.</summary>
    /// <param name="raw">The raw text.</param>
    public static uint? ParseSerial(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Replace("_", "");
        if (s.StartsWith("16#", StringComparison.Ordinal)) s = s[3..];
        else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Human-readable form for route reasons.</summary>
    public string Describe() =>
        $"serial {(Serial is { } s ? $"0x{s:X8}" : "n/a")}, name {(Name is null ? "n/a" : $"'{Name}'")}";
}

/// <summary>The same-controller check between the CIP session and the SDK session.</summary>
public static class CipRouteVerifier
{
    /// <summary>The <c>identityCheck</c> value when only the serial numbers decided.</summary>
    public const string BySerial = "serial";

    /// <summary>The <c>identityCheck</c> value when the controller names decided (the project has no serial).</summary>
    public const string ByName = "name";

    /// <summary>The <c>identityCheck</c> value when the serials matched and the names matched too.</summary>
    public const string BySerialAndName = "serial+name";

    /// <summary>
    /// Verifies that a CIP identity and an SDK identity describe the same controller; see
    /// <see cref="TryVerify(ControllerIdentity, ControllerIdentity?, out string, out string?)"/>.
    /// </summary>
    /// <param name="cip">Identity read over CIP.</param>
    /// <param name="sdk">Identity the SDK session reports; <c>null</c> when unknown.</param>
    /// <param name="reason">The evidence when verified, or what mismatched or was missing.</param>
    /// <returns><c>true</c> when verified.</returns>
    public static bool TryVerify(ControllerIdentity cip, ControllerIdentity? sdk, out string reason) =>
        TryVerify(cip, sdk, out reason, out _);

    /// <summary>
    /// Verifies that a CIP identity and an SDK identity describe the same controller.
    /// <list type="bullet">
    /// <item>SDK serial non-zero: the serials must match; when both sides also report a name, the names must match too
    /// (case-insensitive). Method: <c>serial</c>, or <c>serial+name</c> when the names were compared.</item>
    /// <item>SDK serial zero or missing: the CIP controller name must equal the SDK controller name
    /// (case-insensitive). Method: <c>name</c>. An unreadable CIP name refuses the route.</item>
    /// </list>
    /// A match on the endpoint alone is never accepted: anything that cannot be established counts as <b>not</b> verified.
    /// </summary>
    /// <param name="cip">Identity read over CIP (ListIdentity serial plus the class-0x64 controller name).</param>
    /// <param name="sdk">Identity the SDK session reports; <c>null</c> when unknown.</param>
    /// <param name="reason">The evidence when verified, or what mismatched or was missing.</param>
    /// <param name="identityCheck"><c>serial</c>, <c>name</c> or <c>serial+name</c> when verified; <c>null</c> otherwise.</param>
    /// <returns><c>true</c> when verified.</returns>
    public static bool TryVerify(ControllerIdentity cip, ControllerIdentity? sdk, out string reason, out string? identityCheck)
    {
        identityCheck = null;
        if (sdk is null)
        {
            reason = "the SDK session reports no controller identity to compare with";
            return false;
        }

        if (sdk.Serial is { } ss and not 0u)
        {
            if (cip.Serial is not { } cs)
            {
                reason = $"serial number cannot be compared (CIP: {cip.Describe()}; SDK: {sdk.Describe()})";
                return false;
            }
            if (cs != ss)
            {
                reason = $"controller serial mismatch (CIP: {cip.Describe()}; SDK: {sdk.Describe()})";
                return false;
            }
            var namesCompared = !string.IsNullOrWhiteSpace(cip.Name) && !string.IsNullOrWhiteSpace(sdk.Name);
            if (namesCompared && !NamesEqual(cip.Name, sdk.Name))
            {
                reason = $"controller name mismatch (CIP: {cip.Describe()}; SDK: {sdk.Describe()})";
                return false;
            }
            identityCheck = namesCompared ? BySerialAndName : BySerial;
            reason = $"identity verified (identityCheck: {identityCheck}; {cip.Describe()})";
            return true;
        }

        // The project carries no serial (ProjectSN is zero unless "Match Project to Controller" is set): the name decides.
        if (string.IsNullOrWhiteSpace(cip.Name))
        {
            reason = "the project has no controller serial and the controller name could not be read over CIP, " +
                     $"so the controller is unverified (CIP: {cip.Describe()}; SDK: {sdk.Describe()})";
            return false;
        }
        if (string.IsNullOrWhiteSpace(sdk.Name))
        {
            reason = "the project has no controller serial and no controller name to compare with " +
                     $"(CIP: {cip.Describe()}; SDK: {sdk.Describe()})";
            return false;
        }
        if (!NamesEqual(cip.Name, sdk.Name))
        {
            reason = $"controller name mismatch (CIP: {cip.Describe()}; SDK: {sdk.Describe()})";
            return false;
        }
        identityCheck = ByName;
        reason = $"identity verified (identityCheck: {identityCheck}; {cip.Describe()})";
        return true;
    }

    /// <summary>Logix controller names are case-insensitive.</summary>
    private static bool NamesEqual(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
