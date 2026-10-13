using Xunit;

namespace LogixMcp.Tests;

/// <summary>Tag-name validator is ASCII-only and refuses a trailing newline.</summary>
public class TagNameValidationTests
{
    [Theory]
    [InlineData("Motor1")]
    [InlineData("Motor1.Speed")]
    [InlineData("Arr[3]")]
    [InlineData("Arr[1,2,3].Member[4]")]
    public void ValidNames_Accepted(string name) => Assert.True(WriteAllowlist.IsValidTagName(name));

    [Theory]
    [InlineData("Motor1\n")]
    [InlineData("Arr[3]\n")]
    [InlineData("Arr[٣]")]            // ARABIC-INDIC DIGIT THREE
    [InlineData("Arr[１]")]            // FULLWIDTH DIGIT ONE
    [InlineData("Arr[1,٢]")]
    public void TrailingNewlineAndUnicodeDigits_Refused(string name)
    {
        Assert.False(WriteAllowlist.IsValidTagName(name));
    }

    [Theory]
    [InlineData("Recipe_\n")]
    [InlineData("Arr[٣")]
    public void PrefixVariants_Refused(string name)
    {
        // Prefix with trailing newline is refused; Unicode digits are not in [A-Za-z0-9_.[],].
        Assert.False(WriteAllowlist.IsValidTagPrefix(name));
    }

    [Theory]
    [InlineData("Cmd_Free\n")]
    [InlineData("Cmd_Free\r\n")]
    [InlineData("Arr[3]\n")]
    [InlineData("Arr[٣]")]
    [InlineData("Arr[１]")]
    [InlineData("Motörs")]
    public void ReadPath_TrailingNewlineAndUnicodeDigits_Refused(string name)
    {
        Assert.False(TagNameValidator.IsValid(name));

        var ex = Assert.Throws<TagServiceException>(() => TagNameValidator.ThrowIfInvalid(name, "Read"));
        Assert.Equal(TagServiceErrorKind.InvalidTagName, ex.Error.Kind);

        var session = new TagSessionService();
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)> { ["Cmd_Free"] = (false, "BOOL") }), isVirtual: true);

        var read = Assert.IsType<ReadTagError>(ReadTagHandler.ExecuteAsync(name, session).GetAwaiter().GetResult());
        Assert.Equal(TagServiceErrorKind.InvalidTagName.ToString(), read.Kind);

        var poll = Assert.IsType<PollError>(PollTagsHandler.ExecuteAsync([name], 1, 100, session).GetAwaiter().GetResult());
        Assert.Equal(TagServiceErrorKind.InvalidTagName.ToString(), poll.Kind);
    }

    [Theory]
    [InlineData("Cmd_Free")]
    [InlineData("Program:Main.Arr[1,2].Bit.3")]
    public void ReadPath_AsciiNames_Accepted(string name) => Assert.True(TagNameValidator.IsValid(name));
}
