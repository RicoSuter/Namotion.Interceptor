using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class FileEventCoalescerTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);
    private static readonly string BasePath = Path.Combine(Path.GetTempPath(), "homeblaze-coalescer");

    private readonly ManualTimeProvider _timeProvider = new();
    private readonly List<FileSystemEventArgs[]> _batches = [];

    [Fact]
    public void WhenEventsForOnePathArriveWithinWindow_ThenTheyAreDeliveredAsOneBatch()
    {
        // Arrange
        using var coalescer = CreateCoalescer();
        var deleted = Event(WatcherChangeTypes.Deleted, "Home.md");
        var created = Event(WatcherChangeTypes.Created, "Home.md");
        var changed = Event(WatcherChangeTypes.Changed, "Home.md");

        // Act
        coalescer.Add(deleted);
        _timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        coalescer.Add(created);
        _timeProvider.Advance(TimeSpan.FromMilliseconds(299));
        coalescer.Add(changed);
        var batchCountBeforeWindowCloses = _batches.Count;
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.Equal(0, batchCountBeforeWindowCloses);
        Assert.Equal([deleted, created, changed], Assert.Single(_batches));
    }

    [Fact]
    public void WhenPathsDifferOnlyInCase_ThenEventsShareOneBatch()
    {
        // Arrange
        using var coalescer = CreateCoalescer();
        var upper = Event(WatcherChangeTypes.Deleted, "HOME.md");
        var lower = Event(WatcherChangeTypes.Created, "home.md");

        // Act
        coalescer.Add(upper);
        coalescer.Add(lower);
        _timeProvider.Advance(Window);

        // Assert
        Assert.Equal([upper, lower], Assert.Single(_batches));
    }

    [Fact]
    public void WhenEventsForDifferentPathsArrive_ThenEachPathHasItsOwnWindow()
    {
        // Arrange
        using var coalescer = CreateCoalescer();
        var first = Event(WatcherChangeTypes.Changed, "First.md");
        var second = Event(WatcherChangeTypes.Changed, "Second.md");

        // Act
        coalescer.Add(first);
        _timeProvider.Advance(TimeSpan.FromMilliseconds(300));
        coalescer.Add(second);
        _timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        var batchesAfterFirstWindow = _batches.ToArray();
        _timeProvider.Advance(TimeSpan.FromMilliseconds(299));
        var batchCountBeforeSecondWindowCloses = _batches.Count;
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.Equal([first], Assert.Single(batchesAfterFirstWindow));
        Assert.Equal(1, batchCountBeforeSecondWindowCloses);
        Assert.Equal(2, _batches.Count);
        Assert.Equal([second], _batches[1]);
    }

    [Fact]
    public void WhenEventArrivesAfterWindowClosed_ThenNewBatchIsStarted()
    {
        // Arrange
        using var coalescer = CreateCoalescer();
        var first = Event(WatcherChangeTypes.Changed, "Home.md");
        var second = Event(WatcherChangeTypes.Changed, "Home.md");

        // Act
        coalescer.Add(first);
        _timeProvider.Advance(Window);
        coalescer.Add(second);
        _timeProvider.Advance(Window);

        // Assert
        Assert.Equal(2, _batches.Count);
        Assert.Equal([first], _batches[0]);
        Assert.Equal([second], _batches[1]);
    }

    [Fact]
    public void WhenEventArrivesWhileBatchIsDelivered_ThenItStartsNewBatchWithFullWindow()
    {
        // Arrange
        var first = Event(WatcherChangeTypes.Changed, "Home.md");
        var second = Event(WatcherChangeTypes.Changed, "Home.md");

        FileEventCoalescer? coalescer = null;
        coalescer = new FileEventCoalescer(
            Window,
            events =>
            {
                _batches.Add(events.ToArray());
                if (_batches.Count == 1)
                {
                    coalescer!.Add(second);
                }
            },
            _timeProvider);

        using var _ = coalescer;

        // Act
        coalescer.Add(first);
        _timeProvider.Advance(Window);
        var batchCountAfterFirstWindow = _batches.Count;
        _timeProvider.Advance(Window - TimeSpan.FromMilliseconds(1));
        var batchCountBeforeSecondWindowCloses = _batches.Count;
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.Equal(1, batchCountAfterFirstWindow);
        Assert.Equal(1, batchCountBeforeSecondWindowCloses);
        Assert.Equal(2, _batches.Count);
        Assert.Equal([second], _batches[1]);
    }

    [Fact]
    public void WhenManyPathsGoIdle_ThenNoPathOrTimerStaysActive()
    {
        // Arrange
        const int pathCount = 1000;
        using var coalescer = CreateCoalescer();

        // Act
        for (var index = 0; index < pathCount; index++)
        {
            coalescer.Add(Event(WatcherChangeTypes.Changed, $"File{index}.md"));
        }

        var pendingPathCount = coalescer.PendingPathCount;
        var armedTimerCountWhilePending = _timeProvider.ArmedTimerCount;
        _timeProvider.Advance(Window);

        var timerCallbackCount = _timeProvider.TimerCallbackCount;
        _timeProvider.Advance(Window * 20);

        // Assert
        Assert.Equal(pathCount, pendingPathCount);
        Assert.Equal(1, armedTimerCountWhilePending);
        Assert.Equal(pathCount, _batches.Count);
        Assert.Equal(0, coalescer.PendingPathCount);
        Assert.Equal(0, _timeProvider.ArmedTimerCount);
        Assert.Equal(timerCallbackCount, _timeProvider.TimerCallbackCount);
    }

    [Fact]
    public void WhenDisposedWithOpenWindow_ThenNothingIsDelivered()
    {
        // Arrange
        var coalescer = CreateCoalescer();
        coalescer.Add(Event(WatcherChangeTypes.Changed, "Home.md"));

        // Act
        coalescer.Dispose();
        coalescer.Add(Event(WatcherChangeTypes.Changed, "Other.md"));
        _timeProvider.Advance(Window);

        // Assert
        Assert.Empty(_batches);
        Assert.Equal(0, coalescer.PendingPathCount);
        Assert.Equal(0, _timeProvider.ArmedTimerCount);
    }

    private FileEventCoalescer CreateCoalescer()
        => new(Window, events => _batches.Add(events.ToArray()), _timeProvider);

    private static FileSystemEventArgs Event(WatcherChangeTypes changeType, string name)
        => new(changeType, BasePath, name);
}
