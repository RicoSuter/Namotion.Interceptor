using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class SoundPathSearchTests
{
    [Theory]
    [InlineData(GameObject.MaximumSize + 1, 2)]
    [InlineData(2, 0)]
    public void WhenTheSourceHasAnInvalidSize_ThenNoTargetIsReached(int width, int depth)
    {
        // Arrange
        var search = new SoundPathSearch();

        // Act
        search.Prepare(new GameArea(0, 0, width, depth), new SoundMap());
        var length = search.GetLength(new GameArea(4, 0, 2, 2));

        // Assert
        Assert.Equal(SoundPathSearch.NoPath, length);
    }

    [Fact]
    public void WhenTheTargetHasAnInvalidSize_ThenItIsNotReached()
    {
        // Arrange
        var search = new SoundPathSearch();
        search.Prepare(new GameArea(0, 0, 2, 2), new SoundMap());

        // Act
        var length = search.GetLength(new GameArea(int.MinValue, 0, int.MaxValue, 2));

        // Assert
        Assert.Equal(SoundPathSearch.NoPath, length);
    }

    [Fact]
    public void WhenNothingBlocks_ThenTheLengthIsTheOpenLength()
    {
        // Arrange
        var search = new SoundPathSearch();
        var source = new GameArea(0, 0, 2, 2);
        var target = new GameArea(13, 5, 2, 2);
        search.Prepare(source, new SoundMap());

        // Act
        var length = search.GetLength(target);

        // Assert
        Assert.Equal(SoundPathSearch.GetOpenLength(source, target), length);
    }

    [Fact]
    public void WhenNoSourceIsPrepared_ThenGettingALengthThrows()
    {
        // Arrange
        var search = new SoundPathSearch();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => search.GetLength(new GameArea(0, 0, 2, 2)));
    }

    [Fact]
    public void WhenTheSourceIsWiderThanTheLongestPath_ThenATargetBesideItsCenterGetsTheOpenLength()
    {
        // Arrange
        // The tiles at the ends of the source lie farther from its center than any path may run.
        var search = new SoundPathSearch();
        var source = new GameArea(0, 0, 200, 2);
        var target = new GameArea(100, 6, 2, 2);
        search.Prepare(source, new SoundMap());

        // Act
        var length = search.GetLength(target);

        // Assert
        Assert.Equal(SoundPathSearch.GetOpenLength(source, target), length);
    }

    [Fact]
    public void WhenASourceIsPreparedAgainWhileTheVersionStays_ThenItsLengthsComeFromTheCache()
    {
        // Arrange
        var index = new GameWorldIndex();
        index.AddOrUpdate(EntityFactory.Object(new Wall { X = -10, Y = 5, Width = 20, Depth = 1 }));
        var map = SoundMapFactory.Create(index);
        var search = new SoundPathSearch();
        var source = new GameArea(0, 0, 2, 2);
        var target = new GameArea(0, 10, 2, 2);
        search.Prepare(source, map);
        var length = search.GetLength(target);

        // Act
        search.Prepare(new GameArea(20, 0, 2, 2), map);
        search.Prepare(source, map);
        var cachedLength = search.GetLength(target);

        // Assert
        Assert.Equal(2, search.SearchCount);
        Assert.Equal(length, cachedLength);
        Assert.True(length > SoundPathSearch.GetOpenLength(source, target));
    }

    [Fact]
    public void WhenTheSoundVersionChanges_ThenTheSourceIsSearchedAgain()
    {
        // Arrange
        var index = new GameWorldIndex();
        var door = EntityFactory.Object(new Door { X = -10, Y = 5, Width = 20, Depth = 1, IsOpen = true });
        index.AddOrUpdate(door);
        var snapshot = SoundSnapshot.Empty.TakeChanges(index);
        var map = new SoundMap();
        map.Update(snapshot);
        var search = new SoundPathSearch();
        var source = new GameArea(0, 0, 2, 2);
        var target = new GameArea(0, 10, 2, 2);
        search.Prepare(source, map);
        var openLength = search.GetLength(target);

        // Act
        ((Door)door.Subject).IsOpen = false;
        door.ReadFromSubject();
        index.AddOrUpdate(door);
        map.Update(snapshot.TakeChanges(index));
        search.Prepare(source, map);
        var closedLength = search.GetLength(target);

        // Assert
        Assert.Equal(2, search.SearchCount);
        Assert.Equal(SoundPathSearch.GetOpenLength(source, target), openLength);
        Assert.True(closedLength > openLength);
    }

    [Fact]
    public void WhenMoreSourcesArePreparedThanTheCacheHolds_ThenTheRecentOnesStayCached()
    {
        // Arrange
        var map = new SoundMap();
        var search = new SoundPathSearch();
        for (var x = 0; x < 300; x++)
        {
            search.Prepare(new GameArea(x * 3, 0, 2, 2), map);
        }

        var searchCount = search.SearchCount;

        // Act
        search.Prepare(new GameArea(299 * 3, 0, 2, 2), map);
        search.Prepare(new GameArea(298 * 3, 0, 2, 2), map);
        var recentSearchCount = search.SearchCount;
        search.Prepare(new GameArea(0, 0, 2, 2), map);

        // Assert
        Assert.Equal(300, searchCount);
        Assert.Equal(300, recentSearchCount);
        Assert.Equal(301, search.SearchCount);
    }

    [Fact]
    public void WhenWallsAreRandom_ThenTheLengthsMatchAReferenceSearchWithAHeapCachedOrNot()
    {
        // Arrange
        var random = new Random(1234);
        var index = new GameWorldIndex();
        var blocked = new HashSet<(long X, long Y)>();
        for (var i = 0; i < 160; i++)
        {
            var isHorizontal = random.Next(2) == 0;
            var length = random.Next(1, 14);
            var wall = new GameArea(random.Next(-40, 40), random.Next(-40, 40), isHorizontal ? length : random.Next(1, 3),
                isHorizontal ? random.Next(1, 3) : length);
            index.AddOrUpdate(EntityFactory.Object(new Wall { X = wall.X, Y = wall.Y, Width = wall.Width, Depth = wall.Depth }));
            AddTiles(wall, blocked);
        }

        var map = SoundMapFactory.Create(index);
        var search = new SoundPathSearch();
        var sources = Enumerable.Range(0, 40)
            .Select(_ => new GameArea(random.Next(-30, 30), random.Next(-30, 30), random.Next(1, 7), random.Next(1, 7)))
            .ToList();
        var targets = Enumerable.Range(0, 40)
            .Select(_ => new GameArea(random.Next(-45, 45), random.Next(-45, 45), random.Next(1, 6), random.Next(1, 6)))
            .ToList();

        // Act
        // The second pass prepares every source again, from the cache unless it was evicted.
        var mismatches = new List<string>();
        var reachedCount = 0;
        foreach (var source in sources.Concat(sources.AsEnumerable().Reverse()))
        {
            search.Prepare(source, map);
            reachedCount += Compare(source, targets, search, new ReferenceSearch(source, blocked), mismatches);
        }

        // Assert
        Assert.Empty(mismatches);
        Assert.True(reachedCount > 500);
        Assert.True(search.SearchCount < 2 * sources.Count);
    }

    private static void AddTiles(GameArea area, HashSet<(long X, long Y)> tiles)
    {
        for (var y = area.Y; y <= area.Top; y++)
        {
            for (var x = area.X; x <= area.Right; x++)
            {
                tiles.Add((x, y));
            }
        }
    }

    // Returns how many targets the reference reaches and adds each length that differs to the mismatches.
    private static int Compare(GameArea source, List<GameArea> targets, SoundPathSearch search, ReferenceSearch reference, List<string> mismatches)
    {
        var reachedCount = 0;
        foreach (var target in targets)
        {
            var expected = reference.GetLength(target);
            var actual = search.GetLength(target);
            reachedCount += expected != SoundPathSearch.NoPath ? 1 : 0;
            if (expected != actual)
            {
                mismatches.Add($"{source} to {target}: {actual} instead of {expected}");
            }
        }

        return reachedCount;
    }

    // A plain Dijkstra with a binary heap over the rules of SoundPathSearch, without its buffers, bounds or caches.
    private sealed class ReferenceSearch
    {
        private const int MaximumLength = (HearingRange.HearingRadius + 2) * SoundPathSearch.Unit;
        private const int Margin = HearingRange.HearingRadius + 4;

        private static readonly (int X, int Y, int Length, int FirstX, int FirstY, int SecondX, int SecondY)[] Steps =
        [
            (1, 0, 1000, 0, 0, 0, 0), (-1, 0, 1000, 0, 0, 0, 0), (0, 1, 1000, 0, 0, 0, 0), (0, -1, 1000, 0, 0, 0, 0),
            (1, 1, 1414, 1, 0, 0, 1), (1, -1, 1414, 1, 0, 0, -1), (-1, 1, 1414, -1, 0, 0, 1), (-1, -1, 1414, -1, 0, 0, -1),
            (2, 1, 2236, 1, 0, 1, 1), (2, -1, 2236, 1, 0, 1, -1), (-2, 1, 2236, -1, 0, -1, 1), (-2, -1, 2236, -1, 0, -1, -1),
            (1, 2, 2236, 0, 1, 1, 1), (1, -2, 2236, 0, -1, 1, -1), (-1, 2, 2236, 0, 1, -1, 1), (-1, -2, 2236, 0, -1, -1, -1),
        ];

        private readonly Dictionary<(long X, long Y), long> _lengths = new();
        private readonly GameArea _source;
        private readonly HashSet<(long X, long Y)> _blocked;

        public ReferenceSearch(GameArea source, HashSet<(long X, long Y)> blocked)
        {
            _source = source;
            _blocked = blocked;
            var queue = new PriorityQueue<(long X, long Y), long>();
            for (var y = (long)source.Y; y <= source.Top; y++)
            {
                for (var x = (long)source.X; x <= source.Right; x++)
                {
                    var length = GetCenterLength(source, x, y);
                    _lengths[(x, y)] = length;
                    queue.Enqueue((x, y), length);
                }
            }

            while (queue.TryDequeue(out var tile, out var length))
            {
                if (length == _lengths[tile])
                {
                    Expand(tile, length, queue);
                }
            }
        }

        public int GetLength(GameArea target)
        {
            var shortest = long.MaxValue;
            for (var y = (long)target.Y; y <= target.Top; y++)
            {
                for (var x = (long)target.X; x <= target.Right; x++)
                {
                    if (_lengths.TryGetValue((x, y), out var length))
                    {
                        shortest = Math.Min(shortest, length + GetCenterLength(target, x, y));
                    }
                }
            }

            return shortest <= MaximumLength ? (int)shortest : SoundPathSearch.NoPath;
        }

        private void Expand((long X, long Y) tile, long length, PriorityQueue<(long X, long Y), long> queue)
        {
            foreach (var step in Steps)
            {
                var next = (tile.X + step.X, tile.Y + step.Y);
                var nextLength = length + step.Length;
                if (nextLength > MaximumLength || nextLength >= _lengths.GetValueOrDefault(next, long.MaxValue)
                    || !IsOpen(tile.X + step.FirstX, tile.Y + step.FirstY) || !IsOpen(tile.X + step.SecondX, tile.Y + step.SecondY))
                {
                    continue;
                }

                _lengths[next] = nextLength;
                if (IsOpen(next.Item1, next.Item2))
                {
                    queue.Enqueue(next, nextLength);
                }
            }
        }

        private bool IsOpen(long x, long y) => IsInside(x, y) && (_source.Contains(x, y) || !_blocked.Contains((x, y)));

        private bool IsInside(long x, long y) =>
            Math.Abs(2 * x + 1 - (2L * _source.X + _source.Width)) <= _source.Width + 2 * Margin
            && Math.Abs(2 * y + 1 - (2L * _source.Y + _source.Depth)) <= _source.Depth + 2 * Margin;

        private static long GetCenterLength(GameArea footprint, long x, long y)
        {
            var deltaX = Math.Abs(2 * x + 1 - (2L * footprint.X + footprint.Width));
            var deltaY = Math.Abs(2 * y + 1 - (2L * footprint.Y + footprint.Depth));
            var longer = Math.Max(deltaX, deltaY);
            var shorter = Math.Min(deltaX, deltaY);
            var doubled = 2 * shorter <= longer
                ? shorter * 2236 + (longer - 2 * shorter) * 1000
                : (2 * shorter - longer) * 1414 + (longer - shorter) * 2236;
            return doubled / 2;
        }
    }
}
