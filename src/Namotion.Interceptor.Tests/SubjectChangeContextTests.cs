namespace Namotion.Interceptor.Tests;

public class SubjectChangeContextTests
{
    [Fact]
    public void ChangedTimestamp_WithNoScope_ReturnsUtcNow()
    {
        // Arrange
        var before = DateTimeOffset.UtcNow;

        // Act
        var timestamp = SubjectChangeContext.Current.ChangedTimestamp;

        // Assert
        var after = DateTimeOffset.UtcNow;
        Assert.InRange(timestamp, before, after);
    }

    [Fact]
    public void ChangedTimestampUtcTicks_WithNoScope_ReturnsUtcNowTicks()
    {
        // Arrange
        var before = DateTimeOffset.UtcNow.UtcTicks;

        // Act
        var ticks = SubjectChangeContext.Current.ChangedTimestampUtcTicks;

        // Assert
        var after = DateTimeOffset.UtcNow.UtcTicks;
        Assert.InRange(ticks, before, after);
    }

    [Fact]
    public void ChangedTimestamp_WithRealTimestamp_ReturnsProvidedTimestamp()
    {
        // Arrange
        var expected = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        // Act & Assert
        using (SubjectChangeContext.WithChangedTimestamp(expected))
        {
            Assert.Equal(expected, SubjectChangeContext.Current.ChangedTimestamp);
        }
    }

    [Fact]
    public void ChangedTimestampUtcTicks_WithRealTimestamp_ReturnsProvidedTicks()
    {
        // Arrange
        var expected = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        // Act & Assert
        using (SubjectChangeContext.WithChangedTimestamp(expected))
        {
            Assert.Equal(expected.UtcTicks, SubjectChangeContext.Current.ChangedTimestampUtcTicks);
        }
    }

    [Fact]
    public void ChangedTimestamp_WithNullTimestamp_FallsBackToUtcNow()
    {
        // Arrange
        var before = DateTimeOffset.UtcNow;

        // Act & Assert
        using (SubjectChangeContext.WithChangedTimestamp(null))
        {
            var timestamp = SubjectChangeContext.Current.ChangedTimestamp;
            var after = DateTimeOffset.UtcNow;
            Assert.InRange(timestamp, before, after);
        }
    }

    [Fact]
    public void ChangedTimestampUtcTicks_WithNullTimestamp_ReturnsZero()
    {
        // Act & Assert
        using (SubjectChangeContext.WithChangedTimestamp(null))
        {
            Assert.Equal(0, SubjectChangeContext.Current.ChangedTimestampUtcTicks);
        }
    }

    [Fact]
    public void Source_WithSourceScope_ReturnsProvidedSource()
    {
        // Arrange
        var source = new object();

        // Act & Assert
        using (SubjectChangeContext.WithSource(source))
        {
            Assert.Same(source, SubjectChangeContext.Current.Source);
        }
    }

    [Fact]
    public void Source_WithNoScope_ReturnsNull()
    {
        Assert.Null(SubjectChangeContext.Current.Source);
    }

    [Fact]
    public void WithState_SetsAllFields()
    {
        // Arrange
        var source = new object();
        var changed = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var received = new DateTimeOffset(2025, 6, 15, 12, 0, 1, TimeSpan.Zero);

        // Act & Assert
        using (SubjectChangeContext.WithState(source, changed, received))
        {
            Assert.Same(source, SubjectChangeContext.Current.Source);
            Assert.Equal(changed, SubjectChangeContext.Current.ChangedTimestamp);
            Assert.Equal(received, SubjectChangeContext.Current.ReceivedTimestamp);
        }
    }

    [Fact]
    public void WithState_NullChanged_ReturnsZeroTicks()
    {
        // Act & Assert
        using (SubjectChangeContext.WithState(null, null, null))
        {
            Assert.Equal(0, SubjectChangeContext.Current.ChangedTimestampUtcTicks);
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
                Assert.Equal(innerTimestamp, SubjectChangeContext.Current.ChangedTimestamp);
            }

            // Assert - outer scope restored
            Assert.Equal(outerTimestamp, SubjectChangeContext.Current.ChangedTimestamp);
        }

        // No scope - back to default
        var now = DateTimeOffset.UtcNow;
        Assert.InRange(SubjectChangeContext.Current.ChangedTimestamp, now.AddSeconds(-1), now.AddSeconds(1));
    }

    [Fact]
    public void WithChangedTimestamp_PreservesExistingSourceAndReceivedTimestamp()
    {
        // Arrange
        var source = new object();
        var received = new DateTimeOffset(2025, 6, 15, 12, 0, 1, TimeSpan.Zero);
        var changed = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        // Act & Assert
        using (SubjectChangeContext.WithState(source, null, received))
        {
            using (SubjectChangeContext.WithChangedTimestamp(changed))
            {
                Assert.Same(source, SubjectChangeContext.Current.Source);
                Assert.Equal(received, SubjectChangeContext.Current.ReceivedTimestamp);
                Assert.Equal(changed, SubjectChangeContext.Current.ChangedTimestamp);
            }
        }
    }

    [Fact]
    public void WithSource_PreservesExistingTimestamps()
    {
        // Arrange
        var changed = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var source = new object();

        // Act & Assert
        using (SubjectChangeContext.WithChangedTimestamp(changed))
        {
            using (SubjectChangeContext.WithSource(source))
            {
                Assert.Same(source, SubjectChangeContext.Current.Source);
                Assert.Equal(changed, SubjectChangeContext.Current.ChangedTimestamp);
            }
        }
    }
}
