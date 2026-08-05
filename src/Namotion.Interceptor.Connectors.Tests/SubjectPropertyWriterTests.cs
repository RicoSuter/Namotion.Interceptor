using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;

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

        var writer = new SubjectPropertyWriter(sourceMock.Object, NullLogger.Instance);
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
    public async Task WhenInitialStateProvided_ThenOrderIsInitialStateThenBuffered()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        var order = new List<string>();

        sourceMock
            .Setup(c => c.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { order.Add("InitialState"); });

        var writer = new SubjectPropertyWriter(sourceMock.Object, NullLogger.Instance);

        // Act
        writer.StartBuffering();
        writer.Write(order, o => o.Add("BufferedUpdate"));
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert - order: initial state first, then buffered
        Assert.Equal(2, order.Count);
        Assert.Equal("InitialState", order[0]);
        Assert.Equal("BufferedUpdate", order[1]);
    }

    [Fact]
    public async Task WhenUpdateThrows_ThenErrorIsLoggedAndOtherUpdatesApplied()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock
            .Setup(c => c.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action?)null);

        var writer = new SubjectPropertyWriter(sourceMock.Object, NullLogger.Instance);
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

        var writer = new SubjectPropertyWriter(sourceMock.Object, NullLogger.Instance);

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

        var writer = new SubjectPropertyWriter(sourceMock.Object, NullLogger.Instance);
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

        var writer = new SubjectPropertyWriter(sourceMock.Object, NullLogger.Instance);

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
        var writer = new SubjectPropertyWriter(sourceMock.Object, NullLogger.Instance);
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
        var writer = new SubjectPropertyWriter(sourceMock.Object, NullLogger.Instance);
        var updates = new List<string>();

        // Act
        writer.Write(updates, u => u.Add("Update"));

        // Assert - buffered because _updates starts as [] (not null)
        Assert.Empty(updates);
    }

    [Fact]
    public async Task WhenAStaleLoadCompletesAfterANewerCycleHasStartedBuffering_ThenTheStaleCycleIsDiscarded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);

        var staleLoadEntered = new TaskCompletionSource();
        var releaseStaleLoad = new TaskCompletionSource();
        var staleApplied = false;

        var source = new TestSubjectSource(person, context, NullLogger.Instance)
        {
            LoadInitialStateOverride = async _ =>
            {
                staleLoadEntered.TrySetResult();
                await releaseStaleLoad.Task;
                return (Action?)(() => staleApplied = true);
            },
        };

        // A standalone writer, separate from the source's own internal pump (never started here):
        // ReportConnecting/ReportSynchronized still drive the same source's real state machine,
        // since TransitionTo operates on the source instance, not on any one writer.
        var writer = new SubjectPropertyWriter(source, NullLogger.Instance);

        // Act
        writer.StartBuffering();                                       // cycle A (stale), generation 1
        var staleTask = writer.LoadInitialStateAndResumeAsync(CancellationToken.None);
        await staleLoadEntered.Task;                                   // cycle A is now blocked inside LoadInitialStateAsync

        writer.StartBuffering();                                       // cycle B supersedes A, generation 2

        releaseStaleLoad.SetResult();
        await staleTask;

        // Assert
        Assert.False(staleApplied, "The superseded cycle's stale snapshot must never be applied.");
        Assert.Equal(SourceState.Connecting, source.State);
    }
}
