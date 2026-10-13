using System.Text;
using System.Xml;
using System.Xml.Linq;
using L5Sharp.Core;

/// <summary>
/// Thrown by <see cref="SafeXml"/> when input is refused. Derives from
/// <see cref="XmlException"/> so existing <c>catch (XmlException)</c> fail-safe paths
/// keep working. The message never quotes document content beyond what the XML
/// parser itself reports (line/position).
/// </summary>
public sealed class SafeXmlException : XmlException
{
    /// <summary>Error kind: <see cref="SafeXml.InputTooLarge"/> or <see cref="SafeXml.InvalidL5x"/>.</summary>
    public string Kind { get; }

    /// <summary>Creates a refusal with the given kind and message.</summary>
    public SafeXmlException(string kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }
}

/// <summary>
/// The single choke point for parsing untrusted XML / L5X. DTDs are prohibited, no
/// external resolver is used, entity expansion is disabled and input is capped at
/// <see cref="MaxBytes"/>. Every <c>XDocument.Load/Parse</c> and <c>L5X.Load/Parse</c>
/// in production code must go through here.
/// </summary>
public static class SafeXml
{
    /// <summary>Error kind for input exceeding <see cref="MaxBytes"/>.</summary>
    public const string InputTooLarge = ErrorKinds.InputTooLarge;

    /// <summary>Error kind for malformed XML or XML containing a DTD.</summary>
    public const string InvalidL5x = ErrorKinds.InvalidL5x;

    /// <summary>Maximum accepted input size: 64 MB.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    private static XmlReaderSettings CreateSettings() => new()
    {
        DtdProcessing              = DtdProcessing.Prohibit,
        XmlResolver                = null,
        MaxCharactersFromEntities  = 0,
        MaxCharactersInDocument    = MaxBytes,
        // Matches the default behaviour of XDocument.Load/Parse (LoadOptions.None).
        IgnoreWhitespace           = true,
    };

    /// <summary>Safely loads an XML document from <paramref name="path"/>.</summary>
    /// <param name="path">File path.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="SafeXmlException">Oversize (<see cref="InputTooLarge"/>) or DTD/malformed (<see cref="InvalidL5x"/>).</exception>
    public static XDocument LoadDocument(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (new FileInfo(path).Length > MaxBytes)
            throw TooLarge();
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, CreateSettings());
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (SafeXmlException) { throw; }
        catch (XmlException ex) { throw Invalid(ex); }
    }

    /// <summary>Safely parses an XML document from <paramref name="xml"/>.</summary>
    /// <param name="xml">XML text.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="SafeXmlException">Oversize (<see cref="InputTooLarge"/>) or DTD/malformed (<see cref="InvalidL5x"/>).</exception>
    public static XDocument ParseDocument(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        // UTF-8 never uses fewer bytes than chars; only count exactly when it could matter.
        if (xml.Length > MaxBytes ||
            (xml.Length > MaxBytes / 3 && Encoding.UTF8.GetByteCount(xml) > MaxBytes))
            throw TooLarge();
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), CreateSettings());
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (SafeXmlException) { throw; }
        catch (XmlException ex) { throw Invalid(ex); }
    }

    /// <summary>Safely loads an L5X project from <paramref name="path"/>.</summary>
    /// <param name="path">File path.</param>
    /// <returns>The parsed project.</returns>
    public static L5X LoadL5x(string path) => ToL5x(LoadDocument(path));

    /// <summary>Safely parses an L5X project from <paramref name="xml"/>.</summary>
    /// <param name="xml">L5X text.</param>
    /// <returns>The parsed project.</returns>
    public static L5X ParseL5x(string xml) => ToL5x(ParseDocument(xml));

    internal static L5X ToL5x(XDocument doc)
    {
        if (doc.Root is null)
            throw new SafeXmlException(InvalidL5x, "Document has no root element.");
        return new L5X(new LogixContent(doc.Root));
    }

    private static SafeXmlException TooLarge() =>
        new(InputTooLarge, $"Input exceeds the {MaxBytes / (1024 * 1024)} MB limit.");

    private static SafeXmlException Invalid(XmlException ex) =>
        new(InvalidL5x, ex.Message, ex);
}
