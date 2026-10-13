namespace LogixMcp.Tests;

/// <summary>
/// logix_write / logix_run_test TypeMismatch messaging for BOOL tags:
/// the message names the accepted JSON booleans; behaviour is unchanged (no coercion).
/// </summary>
public class TagWriteTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void BoolTypeMismatch_MessageNamesTrueFalse(int value)
    {
        // CIP backend (libplctag, 1-byte tag => BOOL) and SDK backend (declared type)
        // both route through WriteTypeRule.
        var cip = Assert.Throws<TagServiceException>(
            () => LibPlcTagService.EncodeValue("Cmd_MotorStart", CipWriteTypeTests.BoolResolution, value, new CipWriteTypeTests.NullWriteBuffer()));
        var sdk = Assert.Throws<TagServiceException>(
            () => WriteTypeRule.Validate("Cmd_MotorStart", "BOOL", value));

        foreach (var ex in new[] { cip, sdk })
        {
            Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
            Assert.Contains("BOOL accepts true or false (JSON booleans)", ex.Error.Message);
        }
        Assert.Equal(sdk.Error, cip.Error);
    }

    [Fact]
    public void IntegerToBool_StillRejected_NoCoercion()
    {
        Assert.False(WriteTypeRule.IsCompatible("BOOL", 1));
        Assert.False(WriteTypeRule.IsCompatible("BOOL", 0));
        Assert.True(WriteTypeRule.IsCompatible("BOOL", true));
    }

    [Fact]
    public void NonBoolMismatch_HasNoBoolHint()
    {
        var ex = Assert.Throws<TagServiceException>(
            () => WriteTypeRule.Validate("Speed", "REAL", 42));
        Assert.DoesNotContain("BOOL accepts", ex.Error.Message);
    }
}
