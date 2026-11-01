using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.Nature.Tests;

public class AnimalTests
{
    private readonly RecordingLogger _logger = new();
    private readonly GameTestContext _context;

    public AnimalTests()
    {
        _context = new GameTestContext(_logger);
    }

    [Fact]
    public void WhenAnAnimalIsCreated_ThenItIsADeerFacingNorthThatBlocksMovement()
    {
        // Act
        var animal = new Animal();

        // Assert
        Assert.Equal(AnimalVariant.Deer, animal.Variant);
        Assert.Equal(GameDirection.North, animal.Facing);
        Assert.False(animal.IsEating);
        Assert.Equal((Animal.Size, Animal.Size), (animal.Width, animal.Depth));
        Assert.True(animal.IsMovementBlocking);
        Assert.False(animal.IsSoundBlocking);
        Assert.Equal("Deer", animal.Title);
    }

    [Theory]
    [InlineData(AnimalVariant.Deer, "deer.glb", 6, 6)]
    [InlineData(AnimalVariant.Stag, "stag.glb", 7, 8)]
    public void WhenAVariantIsShown_ThenItsModelIsScaledToItsLengthAndCenteredOnTheBody(AnimalVariant variant, string file, int length, int height)
    {
        // Arrange
        var animal = new Animal { Variant = variant };

        // Act
        var part = Assert.Single(animal.Visual.Parts);

        // Assert
        var offset = (Animal.Size - length) / 2.0;
        Assert.Equal(GameVisualShape.Model, part.Shape);
        Assert.Equal($"_content/HomeBlaze.Game.Nature/models/animals/{file}", part.Model);
        Assert.Equal(new GameVisualVector(length, height, length), part.Size);
        Assert.Equal(new GameVisualVector(offset, 0, offset), part.Offset);
        Assert.Equal(new GameVisualPoint(Animal.Size / 2.0, Animal.Size / 2.0), part.Pivot);
        Assert.Equal(height, animal.Height);
        Assert.Equal("Idle", part.Animation);
        Assert.Equal(GameVisualAnimationMode.Loop, part.AnimationMode);
    }

    [Fact]
    public void WhenTheVariantIsUndefined_ThenTheAnimalLooksLikeADeer()
    {
        // Arrange
        var unknown = new Animal { Variant = (AnimalVariant)99 };
        var deer = new Animal { Variant = AnimalVariant.Deer };

        // Act
        var visual = unknown.Visual;

        // Assert
        Assert.Equal(deer.Visual, visual);
        Assert.Equal("Deer", unknown.Title);
    }

    [Fact]
    public void WhenTheModelsAreListed_ThenEveryModelFileExists()
    {
        // Act
        var models = Enum.GetValues<AnimalVariant>().Select(variant => Assert.Single(new Animal { Variant = variant }.Visual.Parts).Model!).ToArray();

        // Assert
        Assert.All(models, model => Assert.True(
            File.Exists(RepositoryPaths.Get($"src/HomeBlaze/HomeBlaze.Game.Nature/wwwroot/{model["_content/HomeBlaze.Game.Nature/".Length..]}")), model));
    }

    [Fact]
    public void WhenAnAnimalIsAddedToTheWorld_ThenItStepsWithinTheStepDelay()
    {
        // Arrange
        var animal = new Animal { X = 10, Y = 10 };
        var maximumTicks = (int)(Animal.MaximumStepDelay / GameSimulationSettings.TickInterval) + 2;

        // Act
        _context.AddObject("animal", animal);
        for (var tick = 0; tick < maximumTicks && (animal.X, animal.Y) == (10, 10); tick++)
        {
            _context.Tick();
        }

        // Assert
        Assert.NotEqual((10, 10), (animal.X, animal.Y));
        Assert.InRange(animal.X, 9, 11);
        Assert.InRange(animal.Y, 9, 11);
        Assert.Equal(animal.X - 10, animal.Facing.GetOffsetX());
        Assert.Equal(animal.Y - 10, animal.Facing.GetOffsetY());
    }

    [Fact]
    public void WhenItStepsEast_ThenItsRotationFacesEast()
    {
        // Arrange
        var animal = new Animal(new FixedRandom(GameDirection.East)) { X = 10, Y = 10 };

        // Act
        _context.AddObject("animal", animal);
        _context.TimeProvider.Advance(Animal.MinimumStepDelay);
        _context.Tick();

        // Assert
        Assert.Equal((11, 10), (animal.X, animal.Y));
        Assert.Equal(GameDirection.East, animal.Facing);
        Assert.Equal(90, Assert.Single(animal.Visual.Parts).Rotation);
    }

    [Fact]
    public void WhenEverySideIsBlocked_ThenItStaysAndTriesAgain()
    {
        // Arrange
        var random = new FixedRandom(GameDirection.NorthEast);
        var animal = new Animal(random) { X = 10, Y = 10 };
        _context.AddWall(9, 9, width: 6);
        _context.AddWall(9, 14, width: 6);
        _context.AddWall(9, 10, width: 1, depth: 4);
        _context.AddWall(14, 10, width: 1, depth: 4);

        // Act
        _context.AddObject("animal", animal);
        for (var second = 0; second < 30; second++)
        {
            _context.TimeProvider.Advance(TimeSpan.FromSeconds(1));
            _context.Tick();
        }

        // Assert
        Assert.Equal((10, 10), (animal.X, animal.Y));
        Assert.Equal(GameDirection.North, animal.Facing);
        Assert.True(random.DirectionCount >= 10, $"It tried {random.DirectionCount} steps.");
        Assert.DoesNotContain(_logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public void WhenFed_ThenTheVisualPlaysEatOnceAndReturnsToIdle()
    {
        // Arrange
        var animal = _context.AddObject("animal", new Animal(new FixedRandom(GameDirection.East)) { X = 10, Y = 10 });

        // Act
        animal.Feed();
        _context.Tick();
        var whileEating = Assert.Single(animal.Visual.Parts);
        var isEnabledWhileEating = animal.Feed_IsEnabled;
        _context.TimeProvider.Advance(Animal.GetEatDuration(AnimalVariant.Deer));
        _context.Tick();

        // Assert
        Assert.Equal(("Eating", GameVisualAnimationMode.Once), (whileEating.Animation, whileEating.AnimationMode));
        Assert.False(isEnabledWhileEating);
        Assert.False(animal.IsEating);
        Assert.True(animal.Feed_IsEnabled);
        var afterEating = Assert.Single(animal.Visual.Parts);
        Assert.Equal(("Idle", GameVisualAnimationMode.Loop), (afterEating.Animation, afterEating.AnimationMode));
        Assert.Equal((10, 10), (animal.X, animal.Y));
    }

    [Fact]
    public void WhenFedWhileEating_ThenItThrows()
    {
        // Arrange
        var animal = _context.AddObject("animal", new Animal { X = 10, Y = 10 });
        animal.Feed();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(animal.Feed);
    }

    [Fact]
    public void WhenAnEatingAnimalIsAddedToTheWorld_ThenItStopsEatingAfterTheEatDuration()
    {
        // Arrange
        var animal = new Animal { X = 10, Y = 10, Variant = AnimalVariant.Stag, IsEating = true };

        // Act
        _context.AddObject("animal", animal);
        _context.TimeProvider.Advance(Animal.GetEatDuration(AnimalVariant.Stag));
        _context.Tick();

        // Assert
        Assert.False(animal.IsEating);
    }

    [Fact]
    public async Task WhenRemoved_ThenItStopsStepping()
    {
        // Arrange
        var random = new FixedRandom(GameDirection.East);
        var animal = _context.AddObject("animal", new Animal(random) { X = 10, Y = 10 });
        animal.Feed();
        _context.Tick();

        // Act
        await _context.RunAsync(_context.Game.RemoveAsync(animal, CancellationToken.None));
        var directionsBefore = random.DirectionCount;
        for (var second = 0; second < 30; second++)
        {
            _context.TimeProvider.Advance(TimeSpan.FromSeconds(1));
            _context.Tick();
        }

        // Assert
        Assert.Equal((10, 10), (animal.X, animal.Y));
        Assert.Equal(directionsBefore, random.DirectionCount);
        Assert.True(animal.IsEating);
        Assert.DoesNotContain(_logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    // Waits the shortest step delay and always picks the same direction.
    private sealed class FixedRandom(GameDirection direction) : Random
    {
        public int DirectionCount { get; private set; }

        public override int Next(int maxValue)
        {
            DirectionCount++;
            return (int)direction;
        }

        public override int Next(int minValue, int maxValue) => minValue;
    }
}
