namespace LogixMcp.Tests;

/// <summary>
/// CIP-backend pre-wire declared-type check: a BOOL accepts
/// only true/false, with the same TypeMismatch message as the SDK backend, and
/// known libplctag statuses never map to Unknown.
/// </summary>
public class CipWriteTypeTests
{
    internal static readonly CipTypeResolution BoolResolution =
        new(CipTypeKind.Atomic, "BOOL", CipTypeSource.Project, Array.Empty<string>());

    /// <summary>Write buffer of the right size for a BOOL that ignores all writes.</summary>
    internal sealed class NullWriteBuffer : LibPlcTagService.ITagWriteBuffer
    {
        public int GetSize() => 1;
        public void SetBit(int o, bool v) { }
        public void SetInt8(int o, sbyte v) { }
        public void SetUInt8(int o, byte v) { }
        public void SetInt16(int o, short v) { }
        public void SetUInt16(int o, ushort v) { }
        public void SetInt32(int o, int v) { }
        public void SetUInt32(int o, uint v) { }
        public void SetInt64(int o, long v) { }
        public void SetUInt64(int o, ulong v) { }
        public void SetFloat32(int o, float v) { }
        public void SetFloat64(int o, double v) { }
    }

    /// <summary>Mirrors what WriteTagHandler.ParseValue produces for "1"/"0".</summary>
    [Theory]
    [InlineData("1")]
    [InlineData(1)]
    [InlineData("0")]
    [InlineData(0)]
    public void BoolGivenOneOrZero_TypeMismatch_SameAsSdk(object value)
    {
        // The handler parses the string "1" to int 1 before the service sees it.
        var written = value is string s ? WriteTagHandler.ParseValue(s) : value;

        var cip = Assert.Throws<TagServiceException>(
            () => LibPlcTagService.EncodeValue("Cmd_MotorStart", BoolResolution, written, new NullWriteBuffer()));

        // Same rule and message as the SDK backend (shared WriteTypeRule).
        var sdk = Assert.Throws<TagServiceException>(
            () => WriteTypeRule.Validate("Cmd_MotorStart", "BOOL", written));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, cip.Error.Kind);
        Assert.Equal(sdk.Error, cip.Error);
        Assert.Contains("exact declared type", cip.Error.Message);
        Assert.Contains("declared as BOOL", cip.Error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BoolGivenBool_Accepted(bool value) =>
        WriteTypeRule.Validate("Cmd_MotorStart", "BOOL", value);

    [Fact]
    public void NonBoolType_NotCheckedAsBool() =>
        WriteTypeRule.Validate("Some_Dint", "DINT", 1);

    [Fact]
    public void LibplctagOutOfBounds_KindOutOfBounds_NotUnknown()
    {
        var status = libplctag.Status.ErrorOutOfBounds;
        var ex = LibPlcTagService.MapException(
            new libplctag.LibPlcTagException(status), status, "Cmd_MotorStart");

        Assert.Equal(TagServiceErrorKind.OutOfBounds, ex.Error.Kind);
        Assert.NotEqual(TagServiceErrorKind.Unknown, ex.Error.Kind);
        Assert.Contains("ErrorOutOfBounds", ex.Error.Message);
        Assert.Equal("Cmd_MotorStart", ex.Error.TagName);
    }

    [Theory]
    [InlineData(libplctag.Status.ErrorNoMem)]
    [InlineData(libplctag.Status.ErrorRead)]
    [InlineData(libplctag.Status.ErrorWrite)]
    [InlineData(libplctag.Status.ErrorTooLarge)]
    public void OtherKnownStatus_NamedKind_NotUnknown(libplctag.Status status)
    {
        var ex = LibPlcTagService.MapException(
            new libplctag.LibPlcTagException(status), status, "T");

        Assert.NotEqual(TagServiceErrorKind.Unknown, ex.Error.Kind);
        Assert.Contains(status.ToString(), ex.Error.Message);
    }
}
