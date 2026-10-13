namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="TagXPathTranslator"/> — the tag-name → XPath
/// translation seam used by the Logix Designer SDK service.
///
/// Coverage areas:
///   * Null / empty / whitespace-only input guards
///   * Controller-scoped bare tag names (atomic, no member path)
///   * Program-scoped bare tag names (atomic, no member path)
///   * Documented XPath forms for controller- and program-scoped atomic tags
///   * Member-path inputs — must throw and name the byte-array route
///   * Case-insensitive "Program:" prefix recognition
///   * Malformed program-scoped names (missing program or tag name)
///   * XPath literal escaping for tag names containing apostrophes
/// </summary>
public class TagXPathTranslatorTests
{
    // ── Guard tests ───────────────────────────────────────────────────────────

    [Fact]
    public void Translate_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => TagXPathTranslator.Translate(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Translate_EmptyOrWhitespace_ThrowsArgumentException(string tagName)
    {
        Assert.Throws<ArgumentException>(() => TagXPathTranslator.Translate(tagName));
    }

    // ── Controller-scoped bare tag names ──────────────────────────────────────

    [Fact]
    public void Translate_ControllerScopedBareTag_ReturnsTagXPath()
    {
        var xpath = TagXPathTranslator.Translate("Cmd_MotorStart");

        Assert.Equal("Controller/Tags/Tag[@Name='Cmd_MotorStart']", xpath);
    }

    [Theory]
    [InlineData("Cmd_MotorStart",    "Controller/Tags/Tag[@Name='Cmd_MotorStart']")]
    [InlineData("Cmd_SpeedSetpoint", "Controller/Tags/Tag[@Name='Cmd_SpeedSetpoint']")]
    [InlineData("Sim_LineSpeed",     "Controller/Tags/Tag[@Name='Sim_LineSpeed']")]
    [InlineData("Safety_OK",         "Controller/Tags/Tag[@Name='Safety_OK']")]
    [InlineData("Motor_Run",         "Controller/Tags/Tag[@Name='Motor_Run']")]
    [InlineData("Cmd_Motor",         "Controller/Tags/Tag[@Name='Cmd_Motor']")]
    [InlineData("SomeRandomTag",     "Controller/Tags/Tag[@Name='SomeRandomTag']")]
    public void Translate_DemoControllerTags_ReturnExpectedXPath(
        string tagName, string expected)
    {
        Assert.Equal(expected, TagXPathTranslator.Translate(tagName));
    }

    // ── Documented atomic XPath forms (acceptance criteria) ──────────────────

    /// <summary>
    /// Verifies that a controller-scoped atomic tag produces exactly the
    /// documented XPath form: <c>Controller/Tags/Tag[@Name='…']</c>.
    /// </summary>
    [Fact]
    public void ControllerScoped_Atomic_TranslatesToDocumentedForm()
    {
        var xpath = TagXPathTranslator.Translate("Cmd_MotorStart");

        Assert.Equal("Controller/Tags/Tag[@Name='Cmd_MotorStart']", xpath);
    }

    /// <summary>
    /// Verifies that a program-scoped atomic tag produces exactly the
    /// documented XPath form:
    /// <c>Controller/Programs/Program[@Name='…']/Tags/Tag[@Name='…']</c>.
    /// </summary>
    [Fact]
    public void ProgramScoped_Atomic_TranslatesToDocumentedForm()
    {
        var xpath = TagXPathTranslator.Translate("Program:MainProgram.LocalTag");

        Assert.Equal(
            "Controller/Programs/Program[@Name='MainProgram']/Tags/Tag[@Name='LocalTag']",
            xpath);
    }

    // ── Member-path inputs must throw (byte-array route required) ────────────

    /// <summary>
    /// Verifies that a dot-notation member path is rejected with an
    /// <see cref="ArgumentException"/> whose message names the byte-array
    /// read route.
    /// </summary>
    [Fact]
    public void MemberPath_Throws_NamesByteArrayRoute()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => TagXPathTranslator.Translate("Cmd_Motor.Speed"));

        Assert.Contains("byte-array", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Cmd_Motor.Speed")]
    [InlineData("Cmd_Motor.Enable")]
    [InlineData("MyTag.Outer.Inner")]
    [InlineData("TopTag.Level1.Level2.Leaf")]
    public void Translate_ControllerScopedMemberPath_Throws(string tagPath)
    {
        Assert.Throws<ArgumentException>(() => TagXPathTranslator.Translate(tagPath));
    }

    [Theory]
    [InlineData("Program:MainProgram.UdtTag.Speed")]
    [InlineData("Program:MyProg.Tag1.Outer.Inner")]
    public void Translate_ProgramScopedMemberPath_Throws(string tagPath)
    {
        Assert.Throws<ArgumentException>(() => TagXPathTranslator.Translate(tagPath));
    }

    // ── Program-scoped bare tag names ─────────────────────────────────────────

    [Fact]
    public void Translate_ProgramScopedBareTag_ReturnsCorrectXPath()
    {
        var xpath = TagXPathTranslator.Translate("Program:MainProgram.LocalTag");

        Assert.Equal(
            "Controller/Programs/Program[@Name='MainProgram']" +
            "/Tags/Tag[@Name='LocalTag']",
            xpath);
    }

    [Theory]
    [InlineData("Program:MainProgram.LocalTag",
        "Controller/Programs/Program[@Name='MainProgram']/Tags/Tag[@Name='LocalTag']")]
    [InlineData("Program:SubRoutineProgram.Counter",
        "Controller/Programs/Program[@Name='SubRoutineProgram']/Tags/Tag[@Name='Counter']")]
    public void Translate_ProgramScopedTags_ReturnExpectedXPath(
        string tagName, string expected)
    {
        Assert.Equal(expected, TagXPathTranslator.Translate(tagName));
    }

    // ── Case-insensitive "Program:" prefix ───────────────────────────────────

    [Theory]
    [InlineData("program:MainProgram.LocalTag")]
    [InlineData("PROGRAM:MainProgram.LocalTag")]
    [InlineData("Program:MainProgram.LocalTag")]
    public void Translate_ProgramPrefixCaseInsensitive_Recognized(string tagName)
    {
        var xpath = TagXPathTranslator.Translate(tagName);

        // Regardless of prefix casing, the result should include the program XPath axis.
        Assert.Contains("Controller/Programs/Program", xpath);
        Assert.Contains("Tags/Tag", xpath);
    }

    // ── Malformed program-scoped names ────────────────────────────────────────

    [Fact]
    public void Translate_ProgramPrefixWithNoDot_ThrowsArgumentException()
    {
        // "Program:MainProgram" — missing the dot-separator and tag name.
        Assert.Throws<ArgumentException>(
            () => TagXPathTranslator.Translate("Program:MainProgram"));
    }

    [Fact]
    public void Translate_ProgramPrefixWithEmptyProgramName_ThrowsArgumentException()
    {
        // "Program:.LocalTag" — empty program name.
        Assert.Throws<ArgumentException>(
            () => TagXPathTranslator.Translate("Program:.LocalTag"));
    }

    [Fact]
    public void Translate_ProgramPrefixWithTrailingDotOnly_ThrowsArgumentException()
    {
        // "Program:MainProgram." — missing the tag name after the dot.
        Assert.Throws<ArgumentException>(
            () => TagXPathTranslator.Translate("Program:MainProgram."));
    }

    // ── XPath literal escaping ────────────────────────────────────────────────

    [Fact]
    public void EscapeXPathLiteral_NoApostrophe_ReturnsUnchanged()
    {
        var result = TagXPathTranslator.EscapeXPathLiteral("Cmd_MotorStart");

        Assert.Equal("Cmd_MotorStart", result);
    }

    [Fact]
    public void EscapeXPathLiteral_WithApostrophe_ReturnsConcatExpression()
    {
        // Tag names with apostrophes are not valid in Logix, but the escaper
        // must handle them robustly.
        var result = TagXPathTranslator.EscapeXPathLiteral("it's");

        // Must not contain a bare apostrophe that would break the XPath predicate.
        Assert.StartsWith("concat(", result);
        Assert.DoesNotContain("'it's'", result);
    }

    [Fact]
    public void Translate_TagNameContainingApostrophe_ProducesSafeXPath()
    {
        // Apostrophes are illegal in Logix tag names but the translator must
        // not produce malformed XPath in any case.
        var xpath = TagXPathTranslator.Translate("Tag'Name");

        // The XPath string must not contain an unescaped ' inside a predicate.
        // A valid output uses concat() to embed the apostrophe safely.
        Assert.Contains("concat(", xpath);
    }

    // ── XPath structural invariants ───────────────────────────────────────────

    [Fact]
    public void Translate_AllOutputs_StartWithController()
    {
        string[] tagNames =
        [
            "Cmd_MotorStart",
            "Program:MainProgram.LocalTag",
        ];

        foreach (var name in tagNames)
        {
            var xpath = TagXPathTranslator.Translate(name);
            Assert.StartsWith("Controller/", xpath);
        }
    }

    [Fact]
    public void Translate_ControllerScopedTag_PathContainsTagsAxis()
    {
        var xpath = TagXPathTranslator.Translate("Cmd_MotorStart");

        Assert.Contains("/Tags/Tag[", xpath);
    }

    [Fact]
    public void Translate_ProgramScopedTag_PathContainsProgramsAxis()
    {
        var xpath = TagXPathTranslator.Translate("Program:MainProgram.LocalTag");

        Assert.Contains("/Programs/Program[", xpath);
        Assert.Contains("/Tags/Tag[", xpath);
    }
}
