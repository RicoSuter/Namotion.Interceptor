using HomeBlaze.Services.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class RecentLogLoggerProviderTests
{
    [Fact]
    public void WhenWarningIsLogged_ThenItIsKeptWithCategoryEventAndRenderedMessage()
    {
        // Arrange
        var store = new RecentLogStore();
        using var provider = new RecentLogLoggerProvider(store);
        var logger = provider.CreateLogger("Namotion.Devices.Sonos.SonosSpeaker");

        // Act
        logger.LogWarning(new EventId(3, "Offline"), "Speaker {Name} went offline", "Kitchen");

        // Assert
        var entry = Assert.Single(store.GetEntries(null, null, null, null, 100));
        Assert.Equal("Namotion.Devices.Sonos.SonosSpeaker", entry.Category);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(3, entry.EventId);
        Assert.Equal("Offline", entry.EventName);
        Assert.Equal("Speaker Kitchen went offline", entry.Message);
    }

    [Fact]
    public void WhenEntryIsBelowMinimumLevel_ThenFormatterIsNotCalledAndNothingIsAllocated()
    {
        // Arrange
        var store = new RecentLogStore();
        using var provider = new RecentLogLoggerProvider(store);
        var logger = provider.CreateLogger("Category");
        var formatterCalls = 0;
        string Formatter(int state, Exception? exception)
        {
            formatterCalls++;
            return "Message";
        }

        Func<int, Exception?, string> formatter = Formatter;
        logger.Log(LogLevel.Information, default, 1, null, formatter);

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        logger.Log(LogLevel.Information, default, 1, null, formatter);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Equal(0, formatterCalls);
        Assert.Equal(0, allocated);
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.Empty(store.GetEntries(null, null, null, null, 100));
    }

    [Fact]
    public void WhenFormatterThrows_ThenLogDoesNotThrow()
    {
        // Arrange
        var store = new RecentLogStore();
        using var provider = new RecentLogLoggerProvider(store);
        var logger = provider.CreateLogger("Category");

        // Act
        var exception = Record.Exception(() => logger.Log<int>(LogLevel.Error, default, 1, null, (_, _) => throw new InvalidOperationException()));

        // Assert
        Assert.Null(exception);
        Assert.Empty(store.GetEntries(null, null, null, null, 100));
    }

    [Fact]
    public void WhenServicesAreAdded_ThenTheProviderFillsTheRegisteredStore()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHomeBlazeServices();
        using var serviceProvider = services.BuildServiceProvider();
        var store = serviceProvider.GetRequiredService<RecentLogStore>();
        var logger = serviceProvider.GetServices<ILoggerProvider>().OfType<RecentLogLoggerProvider>().Single().CreateLogger("Category");

        // Act
        logger.LogError("Failed");

        // Assert
        Assert.Equal("Failed", Assert.Single(store.GetEntries(null, null, null, null, 100)).Message);
    }
}
