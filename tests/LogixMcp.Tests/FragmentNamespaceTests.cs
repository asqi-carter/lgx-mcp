#if LOGIX_SDK
namespace LogixMcp.Tests;

/// <summary>
/// A rung/tag fragment whose elements carry an XML namespace is refused up front: the
/// handler's un-namespaced lookups (RLLContent, Tags/Tag, External Access forcing) would
/// otherwise silently skip it.
/// </summary>
public class FragmentNamespaceTests
{
    private const string Body =
        "<Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" +
        "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[XIC(Start)OTE(Lamp);]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs>" +
        "<Tags><Tag Name=\"Lamp\" TagType=\"Base\" DataType=\"BOOL\"/></Tags></Controller>";

    [Fact]
    public void NamespacedFragment_Refused()
    {
        var fragment = "<RSLogix5000Content xmlns=\"http://example.com/l5x\" SchemaRevision=\"1.0\">" +
                       Body + "</RSLogix5000Content>";

        var error = ProposeRungEditHandler.ValidateFragmentEnvelope(fragment);

        Assert.NotNull(error);
        Assert.Equal(ErrorKinds.InvalidL5x, error!.Kind);
        Assert.Contains("namespace", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrefixedChildElement_Refused()
    {
        var fragment = "<RSLogix5000Content xmlns:x=\"http://example.com/l5x\" SchemaRevision=\"1.0\">" +
                       Body.Replace("<Tags>", "<x:Tags>").Replace("</Tags>", "</x:Tags>") +
                       "</RSLogix5000Content>";

        var error = ProposeRungEditHandler.ValidateFragmentEnvelope(fragment);

        Assert.NotNull(error);
        Assert.Equal(ErrorKinds.InvalidL5x, error!.Kind);
    }

    [Fact]
    public void UnnamespacedFragment_Accepted()
    {
        var fragment = "<RSLogix5000Content SchemaRevision=\"1.0\">" + Body + "</RSLogix5000Content>";

        Assert.Null(ProposeRungEditHandler.ValidateFragmentEnvelope(fragment));
    }
}
#endif
