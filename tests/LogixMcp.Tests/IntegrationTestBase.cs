namespace LogixMcp.Tests;

/// <summary>
/// Abstract base class for integration tests that require a live
/// Allen-Bradley controller.
/// </summary>
/// <remarks>
/// Derives from this class automatically:
/// <list type="bullet">
///   <item>Joins the <c>Integration</c> xunit collection so that
///     <see cref="IntegrationFixture"/> is shared across all tests.</item>
///   <item>Applies the <c>Category=Integration</c> trait, enabling isolated
///     execution with <c>dotnet test --filter Category=Integration</c>.</item>
///   <item>Skips every test method with a clear human-readable reason when
///     <c>AB_SERVER_HOST</c> is not set in the environment.</item>
/// </list>
///
/// Test methods in subclasses must be annotated with
/// <see cref="SkippableFactAttribute"/> (or <see cref="SkippableTheoryAttribute"/>)
/// rather than the plain <c>[Fact]</c> / <c>[Theory]</c> attributes so that
/// the runtime skip signalled by this base class constructor is recognised by
/// the xunit test runner.
/// </remarks>
[Collection("Integration")]
[Trait("Category", "Integration")]
public abstract class IntegrationTestBase
{
    /// <summary>
    /// Shared fixture that exposes the live-controller connection parameters
    /// read from environment variables.
    /// </summary>
    protected IntegrationFixture Fixture { get; }

    /// <summary>
    /// Initialises the base class with the shared <see cref="IntegrationFixture"/>
    /// and skips the current test when <c>AB_SERVER_HOST</c> is not configured.
    /// </summary>
    /// <param name="fixture">
    /// The collection-scoped fixture injected by xunit.
    /// </param>
    protected IntegrationTestBase(IntegrationFixture fixture)
    {
        Fixture = fixture;
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);
    }
}
