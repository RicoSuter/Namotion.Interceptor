using System.Text.Json;
using HomeBlaze.Services.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class RecentLogStoreTests
{
    [Fact]
    public void WhenCreated_ThenKeepsWarningAndAboveWithDefaultCapacity()
    {
        // Act
        var store = new RecentLogStore();

        // Assert
        Assert.Equal(LogLevel.Warning, store.MinimumLevel);
        Assert.Equal(1000, store.Capacity);
        Assert.False(store.IsEnabled(LogLevel.Information));
        Assert.True(store.IsEnabled(LogLevel.Warning));
        Assert.True(store.IsEnabled(LogLevel.Critical));
        Assert.False(store.IsEnabled(LogLevel.None));
    }

    [Fact]
    public void WhenEntryIsBelowMinimumLevel_ThenItIsNotKept()
    {
        // Arrange
        var store = new RecentLogStore();

        // Act
        store.Add("Category", LogLevel.Information, default, "Information", null);
        store.Add("Category", LogLevel.Warning, default, "Warning", null);

        // Assert
        var entry = Assert.Single(store.GetEntries(null, null, null, null, 100));
        Assert.Equal("Warning", entry.Message);
    }

    [Fact]
    public void WhenMoreEntriesThanCapacity_ThenOldestAreReplacedAndNewestComeFirst()
    {
        // Arrange
        var store = new RecentLogStore();
        store.Configure(null, 3);

        // Act
        for (var index = 1; index <= 5; index++)
        {
            store.Add("Category", LogLevel.Warning, default, "Entry " + index, null);
        }

        // Assert
        Assert.Equal(3, store.Count);
        Assert.Equal(["Entry 5", "Entry 4", "Entry 3"], store.GetEntries(null, null, null, null, 100).Select(entry => entry.Message));
        Assert.Equal(5, store.WarningCount);
    }

    [Fact]
    public void WhenCapacityIsReduced_ThenNewestEntriesAreKept()
    {
        // Arrange
        var store = new RecentLogStore();
        store.Configure(null, 4);
        for (var index = 1; index <= 6; index++)
        {
            store.Add("Category", LogLevel.Warning, default, "Entry " + index, null);
        }

        // Act
        store.Configure(null, 2);
        store.Add("Category", LogLevel.Warning, default, "Entry 7", null);

        // Assert
        Assert.Equal(["Entry 7", "Entry 6"], store.GetEntries(null, null, null, null, 100).Select(entry => entry.Message));
    }

    [Fact]
    public void WhenCapacityIsZero_ThenNothingIsKept()
    {
        // Arrange
        var store = new RecentLogStore();
        store.Add("Category", LogLevel.Error, default, "Before", null);

        // Act
        store.Configure(null, 0);
        store.Add("Category", LogLevel.Error, default, "After", null);

        // Assert
        Assert.False(store.IsEnabled(LogLevel.Critical));
        Assert.Empty(store.GetEntries(null, null, null, null, 100));
        Assert.Equal(1, store.ErrorCount);
    }

    [Fact]
    public void WhenConfiguredWithNullLevelAndTooLargeCapacity_ThenDefaultLevelAndMaximumCapacityApply()
    {
        // Arrange
        var store = new RecentLogStore();
        store.Configure(LogLevel.Debug, 10);

        // Act
        store.Configure(null, 50_000);

        // Assert
        Assert.Equal(LogLevel.Warning, store.MinimumLevel);
        Assert.Equal(10_000, store.Capacity);
    }

    [Theory]
    [InlineData(LogLevel.None)]
    [InlineData((LogLevel)42)]
    public void WhenConfiguredWithNoneOrNoLevelAndNegativeCapacity_ThenDefaultLevelAndZeroCapacityApply(LogLevel level)
    {
        // Arrange
        var store = new RecentLogStore();

        // Act
        store.Configure(level, -1);

        // Assert
        Assert.Equal(LogLevel.Warning, store.MinimumLevel);
        Assert.Equal(0, store.Capacity);
    }

    [Fact]
    public void WhenMinimumLevelIsLowered_ThenLowerEntriesAreKept()
    {
        // Arrange
        var store = new RecentLogStore();

        // Act
        store.Configure(LogLevel.Debug, 10);
        store.Add("Category", LogLevel.Debug, default, "Debug", null);
        store.Add("Category", LogLevel.Trace, default, "Trace", null);

        // Assert
        Assert.Equal("Debug", Assert.Single(store.GetEntries(null, null, null, null, 100)).Message);
    }

    [Fact]
    public void WhenEntriesAreFiltered_ThenOnlyMatchingEntriesAreReturned()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new RecentLogStore(timeProvider);
        store.Add("Namotion.Devices.Sonos.SonosSpeaker", LogLevel.Warning, default, "Speaker offline", null);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var since = timeProvider.GetUtcNow();
        store.Add("Namotion.Devices.Shelly.ShellyDevice", LogLevel.Error, default, "Poll failed", new TimeoutException("Timed out"));
        store.Add("Namotion.Devices.Sonos.SonosEventListener", LogLevel.Error, default, "Listener failed", null);

        // Act
        var byLevel = Messages(store.GetEntries(LogLevel.Error, null, null, null, 100));
        var byTime = Messages(store.GetEntries(null, since, null, null, 100));
        var byExceptionMessage = Messages(store.GetEntries(null, null, "timed OUT", null, 100));
        var byExceptionType = Messages(store.GetEntries(null, null, "TimeoutException", null, 100));
        var byCategory = Messages(store.GetEntries(null, null, null, "namotion.devices.sonos", 100));
        var byCount = Messages(store.GetEntries(null, null, null, null, 1));

        // Assert
        Assert.Equal(["Listener failed", "Poll failed"], byLevel);
        Assert.Equal(["Listener failed", "Poll failed"], byTime);
        Assert.Equal(["Poll failed"], byExceptionMessage);
        Assert.Equal(["Poll failed"], byExceptionType);
        Assert.Equal(["Listener failed", "Speaker offline"], byCategory);
        Assert.Equal(["Listener failed"], byCount);
        Assert.Equal(since, store.GetEntries(null, null, "Poll", null, 100)[0].Timestamp);
    }

    [Fact]
    public void WhenEntryHasEventAndException_ThenTheyAreKept()
    {
        // Arrange
        var store = new RecentLogStore();
        var exception = new InvalidOperationException("First line" + Environment.NewLine + "Second line");

        // Act
        store.Add("Category", LogLevel.Error, new EventId(7, "Failed"), "Message", exception);

        // Assert
        var entry = Assert.Single(store.GetEntries(null, null, null, null, 100));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("Category", entry.Category);
        Assert.Equal(7, entry.EventId);
        Assert.Equal("Failed", entry.EventName);
        Assert.Equal("System.InvalidOperationException", entry.ExceptionType);
        Assert.Equal("First line", entry.ExceptionMessage);
    }

    [Fact]
    public void WhenTextIsLong_ThenItIsShortened()
    {
        // Arrange
        var store = new RecentLogStore();

        // Act
        store.Add("Category", LogLevel.Error, default, new string('m', 5000), new InvalidOperationException(new string('e', 1000)));

        // Assert
        var entry = Assert.Single(store.GetEntries(null, null, null, null, 100));
        Assert.Equal(RecentLogStore.MaximumMessageLength, entry.Message.Length);
        Assert.EndsWith("…", entry.Message);
        Assert.Equal(RecentLogStore.MaximumExceptionMessageLength, entry.ExceptionMessage!.Length);
    }

    [Fact]
    public void WhenTextIsShortenedWithinSurrogatePair_ThenThePairIsNotSplit()
    {
        // Arrange
        var text = new string('a', 8) + "\U0001F600" + "tail";

        // Act
        var shortened = RecentLogStore.Shorten(text, 10);

        // Assert
        Assert.Equal(new string('a', 8) + "…", shortened);
    }

    [Fact]
    public void WhenEntryHoldsSecret_ThenItIsRedacted()
    {
        // Arrange
        var store = new RecentLogStore();

        // Act
        store.Add("Category", LogLevel.Warning, default, "GET http://device/api?token=abc123 failed", new InvalidOperationException("password=hunter2"));

        // Assert
        var entry = Assert.Single(store.GetEntries(null, null, null, null, 100));
        Assert.Equal("GET http://device/api?token=*** failed", entry.Message);
        Assert.Equal("password=***", entry.ExceptionMessage);
    }

    [Fact]
    public void WhenUrlPasswordEndsAtTheLengthLimit_ThenItIsStillRedacted()
    {
        // Arrange
        var store = new RecentLogStore();
        var url = "http://user:hunter2@host/status";
        var padding = new string('x', RecentLogStore.MaximumMessageLength - url.IndexOf('@') - 1);

        // Act
        store.Add("Category", LogLevel.Warning, default, padding + url, null);

        // Assert
        var message = Assert.Single(store.GetEntries(null, null, null, null, 100)).Message;
        Assert.DoesNotContain("hunter2", message);
        Assert.True(message.Length <= RecentLogStore.MaximumMessageLength);
    }

    [Fact]
    public void WhenRedactionLengthensTextAtTheLimit_ThenItStaysWithinTheLimit()
    {
        // Act
        var shortened = RecentLogStore.Shorten(new string('x', RecentLogStore.MaximumMessageLength - 10) + " token=a b", RecentLogStore.MaximumMessageLength);

        // Assert
        Assert.Equal(RecentLogStore.MaximumMessageLength, shortened.Length);
    }

    [Fact]
    public void WhenEntryIsSerializedWithDefaultOptions_ThenLevelIsANameAndNullFieldsAreLeftOut()
    {
        // Arrange
        var entry = new RecentLogEntry(DateTimeOffset.UnixEpoch, LogLevel.Warning, "Category", 0, null, "Message", null, null);

        // Act
        var json = JsonSerializer.Serialize(entry);

        // Assert
        Assert.Contains("\"Level\":\"Warning\"", json);
        Assert.DoesNotContain("EventName", json);
        Assert.DoesNotContain("Exception", json);
    }

    [Fact]
    public void WhenWarningsErrorsAndCriticalsAreAdded_ThenCountersCountThem()
    {
        // Arrange
        var store = new RecentLogStore();

        // Act
        store.Add("Category", LogLevel.Warning, default, "Warning", null);
        store.Add("Category", LogLevel.Warning, default, "Warning", null);
        store.Add("Category", LogLevel.Error, default, "Error", null);
        store.Add("Category", LogLevel.Critical, default, "Critical", null);

        // Assert
        Assert.Equal(2, store.WarningCount);
        Assert.Equal(2, store.ErrorCount);
    }

    [Fact]
    public void WhenEntriesAreAddedConcurrently_ThenAllAreCountedAndCapacityIsKept()
    {
        // Arrange
        var store = new RecentLogStore();
        store.Configure(null, 100);

        // Act
        Parallel.For(0, 10_000, index => store.Add("Category", LogLevel.Warning, default, "Entry", null));

        // Assert
        Assert.Equal(10_000, store.WarningCount);
        Assert.Equal(100, store.Count);
        Assert.Equal(100, store.GetEntries(null, null, null, null, 1000).Count);
    }

    private static string[] Messages(IEnumerable<RecentLogEntry> entries) => entries.Select(entry => entry.Message).ToArray();
}
