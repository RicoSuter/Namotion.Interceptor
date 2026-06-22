using HomeBlaze.History.Abstractions;
using HomeBlaze.History.InMemory;

namespace HomeBlaze.History.InMemory.Tests;

public class PropertyBufferTests
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private static Sample LongSample(int secondsFromBase, long value) =>
        new(Base.AddSeconds(secondsFromBase), value, null, null);

    private static PropertyBuffer NewBuffer(int capacity = 1000) =>
        new(capacity, ValueColumn.Long, isUlong: false);

    [Fact]
    public void WhenAppendedInOrder_ThenRangeReturnsAscending()
    {
        // Arrange
        var buffer = NewBuffer();
        buffer.Append(LongSample(0, 10));
        buffer.Append(LongSample(1, 11));
        buffer.Append(LongSample(2, 12));

        // Act
        var range = buffer.Range(Base, Base.AddSeconds(10));

        // Assert
        Assert.Equal(new long?[] { 10, 11, 12 }, range.Select(sample => sample.Long).ToArray());
    }

    [Fact]
    public void WhenAppendedOutOfOrder_ThenRangeStaysAscending()
    {
        // Arrange
        var buffer = NewBuffer();
        buffer.Append(LongSample(0, 10));
        buffer.Append(LongSample(2, 12));
        buffer.Append(LongSample(1, 11)); // late arrival

        // Act
        var range = buffer.Range(Base, Base.AddSeconds(10));

        // Assert
        Assert.Equal(new long?[] { 10, 11, 12 }, range.Select(sample => sample.Long).ToArray());
    }

    [Fact]
    public void WhenCapacityExceeded_ThenOldestEvictedAndCountReported()
    {
        // Arrange
        var buffer = NewBuffer(capacity: 3);

        // Act - append five into a capacity-three ring
        for (var i = 0; i < 5; i++)
        {
            buffer.Append(LongSample(i, 100 + i));
        }

        // Assert - newest three retained in order, two evicted
        var range = buffer.Range(Base, Base.AddSeconds(10));
        Assert.Equal(new long?[] { 102, 103, 104 }, range.Select(sample => sample.Long).ToArray());
        Assert.Equal(2, buffer.EvictedCount);
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void WhenEvictOlderThan_ThenLeadingOldSamplesDropped()
    {
        // Arrange
        var buffer = NewBuffer();
        for (var i = 0; i < 5; i++)
        {
            buffer.Append(LongSample(i, 100 + i));
        }

        // Act - drop everything strictly older than Base+2s
        var dropped = buffer.EvictOlderThan(Base.AddSeconds(2));

        // Assert - samples at 0s and 1s gone; 2s..4s remain
        Assert.Equal(2, dropped);
        var range = buffer.Range(Base, Base.AddSeconds(10));
        Assert.Equal(new long?[] { 102, 103, 104 }, range.Select(sample => sample.Long).ToArray());
    }

    [Fact]
    public void WhenRangeIsHalfOpen_ThenToBoundIsExclusive()
    {
        // Arrange
        var buffer = NewBuffer();
        buffer.Append(LongSample(0, 10));
        buffer.Append(LongSample(1, 11));
        buffer.Append(LongSample(2, 12));

        // Act - [0s, 2s): excludes the 2s sample
        var range = buffer.Range(Base, Base.AddSeconds(2));

        // Assert
        Assert.Equal(new long?[] { 10, 11 }, range.Select(sample => sample.Long).ToArray());
    }

    [Fact]
    public void WhenAtOrBefore_ThenReturnsMostRecentNotAfter()
    {
        // Arrange
        var buffer = NewBuffer();
        buffer.Append(LongSample(0, 10));
        buffer.Append(LongSample(2, 12));
        buffer.Append(LongSample(4, 14));

        // Act
        var atGap = buffer.AtOrBefore(Base.AddSeconds(3)); // between 2s and 4s
        var beforeAll = buffer.AtOrBefore(Base.AddSeconds(-1));

        // Assert
        Assert.Equal(12, atGap!.Value.Long);
        Assert.Null(beforeAll);
    }

    [Fact]
    public void WhenEmpty_ThenOldestAndNewestAreNull()
    {
        // Arrange
        var buffer = NewBuffer();

        // Act & Assert
        Assert.Null(buffer.Oldest);
        Assert.Null(buffer.Newest);
    }
}
