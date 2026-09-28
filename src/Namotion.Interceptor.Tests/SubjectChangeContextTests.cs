namespace Namotion.Interceptor.Tests;

public class SubjectChangeContextTests
{
    [Fact]
    public void ResolveChangedTimestamp_WithNoScope_ReturnsUtcNowTicks()
    {
        // Arrange
        var before = DateTime.UtcNow.Ticks;

        // Act
        var ticks = SubjectChangeContext.Current.ResolveChangedTimestamp();

        // Assert
        var after = DateTime.UtcNow.Ticks;
        Assert.InRange(ticks, before, after);
    }

    [Fact]
    public void ResolveChangedTimestamp_WithRealTimestamp_ReturnsProvidedTicks()
    {
        // Arrange
        var expected = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        // Act & Assert
        using (SubjectChangeContext.WithChangedTimestamp(expected))
        {
            Assert.Equal(expected.UtcTicks, SubjectChangeContext.Current.ResolveChangedTimestamp());
        }
    }

    [Fact]
    public void ResolveChangedTimestamp_WithNullTimestamp_ReturnsZero()
    {
        // Act & Assert
        using (SubjectChangeContext.WithChangedTimestamp(null))
        {
            Assert.Equal(0, SubjectChangeContext.Current.ResolveChangedTimestamp());
        }
    }

    [Fact]
    public void WithTimestamps_SetsChangedAndReceived()
    {
        // Arrange
        var changed = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var received = new DateTimeOffset(2025, 6, 15, 12, 0, 1, TimeSpan.Zero);

        // Act & Assert
        using (SubjectChangeContext.WithTimestamps(changed, received))
        {
            Assert.Equal(changed.UtcTicks, SubjectChangeContext.Current.ResolveChangedTimestamp());
            Assert.Equal(received, SubjectChangeContext.Current.ReceivedTimestamp);
        }
    }

    [Fact]
    public void WithTimestamps_NullReceived_PreservesAmbientReceived()
    {
        // Arrange
        var received = new DateTimeOffset(2025, 6, 15, 12, 0, 1, TimeSpan.Zero);
        var changed = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        // Act & Assert - a null received argument preserves the ambient received timestamp
        using (SubjectChangeContext.WithTimestamps(changed, received))
        {
            using (SubjectChangeContext.WithTimestamps(changed, null))
            {
                Assert.Equal(received, SubjectChangeContext.Current.ReceivedTimestamp);
            }
        }
    }

    [Fact]
    public void WithTimestamps_NoScope_ReceivedIsNull()
    {
        // Act & Assert
        using (SubjectChangeContext.WithTimestamps(null, null))
        {
            Assert.Null(SubjectChangeContext.Current.ReceivedTimestamp);
        }
    }

    [Fact]
    public void WithTimestamps_NullChanged_ReturnsZeroTicks()
    {
        // Act & Assert
        using (SubjectChangeContext.WithTimestamps(null, null))
        {
            Assert.Equal(0, SubjectChangeContext.Current.ResolveChangedTimestamp());
        }
    }

    [Fact]
    public void ReceivedTimestamp_WithNoScope_ReturnsNull()
    {
        Assert.Null(SubjectChangeContext.Current.ReceivedTimestamp);
    }

    [Fact]
    public void Scope_RestoresPreviousStateOnDispose()
    {
        // Arrange
        var outerTimestamp = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var innerTimestamp = new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero);

        using (SubjectChangeContext.WithChangedTimestamp(outerTimestamp))
        {
            // Act
            using (SubjectChangeContext.WithChangedTimestamp(innerTimestamp))
            {
                Assert.Equal(innerTimestamp.UtcTicks, SubjectChangeContext.Current.ResolveChangedTimestamp());
            }

            // Assert - outer scope restored
            Assert.Equal(outerTimestamp.UtcTicks, SubjectChangeContext.Current.ResolveChangedTimestamp());
        }

        // No scope - back to default (returns UtcNow ticks)
        var beforeTicks = DateTime.UtcNow.Ticks;
        var resolved = SubjectChangeContext.Current.ResolveChangedTimestamp();
        var afterTicks = DateTime.UtcNow.Ticks;
        Assert.InRange(resolved, beforeTicks, afterTicks);
    }

    [Fact]
    public void WithChangedTimestamp_PreservesExistingReceivedTimestamp()
    {
        // Arrange
        var received = new DateTimeOffset(2025, 6, 15, 12, 0, 1, TimeSpan.Zero);
        var changed = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        // Act & Assert
        using (SubjectChangeContext.WithTimestamps(null, received))
        {
            using (SubjectChangeContext.WithChangedTimestamp(changed))
            {
                Assert.Equal(received, SubjectChangeContext.Current.ReceivedTimestamp);
                Assert.Equal(changed.UtcTicks, SubjectChangeContext.Current.ResolveChangedTimestamp());
            }
        }
    }

    [Fact]
    public void WithTimestamps_PreservesExistingChangedTimestamp_WhenChangedProvided()
    {
        // Arrange
        var changed = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        // Act & Assert
        using (SubjectChangeContext.WithChangedTimestamp(changed))
        {
            using (SubjectChangeContext.WithTimestamps(changed, null))
            {
                Assert.Equal(changed.UtcTicks, SubjectChangeContext.Current.ResolveChangedTimestamp());
            }
        }
    }
}
