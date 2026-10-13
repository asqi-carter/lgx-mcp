namespace LogixMcp.Tests;

/// <summary>
/// Defines the <c>Integration</c> xunit collection and wires up
/// <see cref="IntegrationFixture"/> as a shared collection-scoped fixture.
/// </summary>
/// <remarks>
/// All test classes that participate in live-controller integration testing
/// should be decorated with <c>[Collection("Integration")]</c> and inherit from
/// <see cref="IntegrationTestBase"/> to receive automatic environment-gating
/// (SKIPPED when <c>AB_SERVER_HOST</c> is absent).
/// </remarks>
[CollectionDefinition("Integration")]
public sealed class IntegrationTestCollection : ICollectionFixture<IntegrationFixture>
{
    // This class is intentionally empty.  Its sole purpose is to serve as an
    // xunit [CollectionDefinition] anchor that associates IntegrationFixture
    // with the "Integration" collection.
}
