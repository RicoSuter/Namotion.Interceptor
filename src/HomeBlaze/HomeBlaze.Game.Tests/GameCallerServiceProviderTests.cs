using HomeBlaze.Game.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Game.Tests;

public class GameCallerServiceProviderTests
{
    [Fact]
    public void WhenTheCallerIsRequested_ThenThePageCallerIsReturnedAndOtherServicesComeFromTheInnerProvider()
    {
        // Arrange
        var inner = new ServiceCollection().AddSingleton("inner service").BuildServiceProvider();
        var caller = new GameCaller("tab-1");
        var provider = new GameCallerServiceProvider(inner, caller);

        // Act
        var resolvedCaller = provider.GetService(typeof(GameCaller));
        var resolvedString = provider.GetService(typeof(string));

        // Assert
        Assert.Same(caller, resolvedCaller);
        Assert.Equal("inner service", resolvedString);
    }
}
