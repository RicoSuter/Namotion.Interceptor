using HomeBlaze.E2E.Tests.Infrastructure;

namespace HomeBlaze.Game.Tests.E2E;

/// <summary>
/// The game tests share one server and browser; xUnit only finds collection definitions in the test assembly itself.
/// </summary>
[CollectionDefinition(nameof(GameE2ECollection))]
public class GameE2ECollection : ICollectionFixture<PlaywrightFixture>
{
}
