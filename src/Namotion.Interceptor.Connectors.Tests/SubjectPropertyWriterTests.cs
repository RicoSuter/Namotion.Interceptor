using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectPropertyWriterTests
{
    [Fact]
    public async Task WhenAfterInit_ThenUpdatesAreAppliedImmediately()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock
            .Setup(c => c.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action?)null);

        var writer = new SubjectPropertyWriter(sourceMock.Object, null, NullLogger.Instance);
        var updates = new List<string>();

        writer.StartBuffering();
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Act - write after initialization
        writer.Write(updates, u => u.Add("Immediate"));

        // Assert - applied immediately
        Assert.Single(updates);
        Assert.Equal("Immediate", updates[0]);
    }

    [Fact]
    public async Task WhenCallbackProvided_ThenOrderIsFlushThenInitialStateThenBuffered()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        var order = new List<string>();

        sourceMock
            .Setup(c => c.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { order.Add("InitialState"); });

        var flushInvoked = false;
        var writer = new SubjectPropertyWriter(
            sourceMock.Object,
            ct =>
            {
                order.Add("Flush");
                flushInvoked = true;
                return ValueTask.FromResult(true);
            },
            NullLogger.Instance);

        // Act
        writer.StartBuffering();
        writer.Write(order, o => o.Add("BufferedUpdate"));
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert - order: flush first, then initial state, then buffered (avoids state toggle)
        Assert.True(flushInvoked);
        Assert.Equal(3, order.Count);
        Assert.Equal("Flush", order[0]);
        Assert.Equal("InitialState", order[1]);
        Assert.Equal("BufferedUpdate", order[2]);
    }

    [Fact]
    public async Task WhenUpdateThrows_ThenErrorIsLoggedAndOtherUpdatesApplied()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock
            .Setup(c => c.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action?)null);

        var writer = new SubjectPropertyWriter(sourceMock.Object, null, NullLogger.Instance);
        var updates = new List<string>();

        // Act
        writer.StartBuffering();
        writer.Write(updates, u => u.Add("Update1"));
        writer.Write(updates, _ => throw new Exception("Test error"));
        writer.Write(updates, u => u.Add("Update3"));

        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert - first and third updates applied, second error logged (not thrown)
        Assert.Equal(2, updates.Count);
        Assert.Equal("Update1", updates[0]);
        Assert.Equal("Update3", updates[1]);
    }

    [Fact]
    public async Task WhenImmediateUpdateThrows_ThenErrorIsLoggedNotThrown()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock
            .Setup(c => c.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action?)null);

        var writer = new SubjectPropertyWriter(sourceMock.Object, null, NullLogger.Instance);

        writer.StartBuffering();
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Act & Assert - should not throw
        writer.Write(0, _ => throw new Exception("Test error"));
    }

    [Fact]
    public async Task WhenStartBufferingCalledMultipleTimes_ThenOnlyLatestBufferIsReplayed()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock
            .Setup(c => c.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action?)null);

        var writer = new SubjectPropertyWriter(sourceMock.Object, null, NullLogger.Instance);
        var updates = new List<string>();

        // Act
        writer.StartBuffering();
        writer.Write(updates, u => u.Add("First"));

        writer.StartBuffering(); // Reset buffer
        writer.Write(updates, u => u.Add("Second"));

        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert - only "Second" replayed
        Assert.Single(updates);
        Assert.Equal("Second", updates[0]);
    }

    [Fact]
    public async Task WhenLoadInitialStateAndResumeCalledTwice_ThenSecondCallSkipsReplay()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        var loadCount = 0;
        var replayCount = 0;

        sourceMock
            .Setup(c => c.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { loadCount++; });

        var writer = new SubjectPropertyWriter(sourceMock.Object, null, NullLogger.Instance);

        // Act
        writer.StartBuffering();
        writer.Write(replayCount, _ => replayCount++);
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None); // Second call

        // Assert
        // LoadInitialStateAsync called twice (before null check), but replay only happens once
        Assert.Equal(2, loadCount);
        Assert.Equal(1, replayCount);
    }

    [Fact]
    public async Task WhenNotClientSource_ThenNoInitialStateLoaded()
    {
        // Arrange - using ISubjectSource (not ISubjectSource)
        var sourceMock = new Mock<ISubjectSource>();
        var writer = new SubjectPropertyWriter(sourceMock.Object, null, NullLogger.Instance);
        var updates = new List<string>();

        // Act
        writer.StartBuffering();
        writer.Write(updates, u => u.Add("Update"));
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert - update replayed without LoadInitialStateAsync call
        Assert.Single(updates);
        Assert.Equal("Update", updates[0]);
    }

    [Fact]
    public void WhenNoStartBufferingCalled_ThenUpdatesAreBuffered()
    {
        // Arrange - _updates starts as empty list (buffering by default)
        var sourceMock = new Mock<ISubjectSource>();
        var writer = new SubjectPropertyWriter(sourceMock.Object, null, NullLogger.Instance);
        var updates = new List<string>();

        // Act
        writer.Write(updates, u => u.Add("Update"));

        // Assert - buffered because _updates starts as [] (not null)
        Assert.Empty(updates);
    }

}
