using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.Nature.Tests;

public class AnimalTests
{
    private static readonly int TicksPerStep = (int)(Animal.StepInterval / GameSimulationSettings.TickInterval);

    private readonly RecordingLogger _logger = new();
    private readonly GameTestContext _context;

    public AnimalTests()
    {
        _context = new GameTestContext(_logger);
    }

    [Fact]
    public void WhenAnAnimalIsCreated_ThenItIsAStandingDeerFacingNorthThatBlocksMovement()
    {
        // Act
        var animal = new Animal();

        // Assert
        Assert.Equal(AnimalVariant.Deer, animal.Variant);
        Assert.Equal(GameDirection.North, animal.Facing);
        Assert.False(animal.IsEating);
        Assert.False(animal.IsWalking);
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
        Assert.Equal(("Idle", GameVisualAnimationMode.Loop), (part.Animation, part.AnimationMode));
    }

    [Theory]
    [InlineData(false, false, "Idle", GameVisualAnimationMode.Loop)]
    [InlineData(false, true, "Walk", GameVisualAnimationMode.Loop)]
    [InlineData(true, false, "Eating", GameVisualAnimationMode.Once)]
    [InlineData(true, true, "Eating", GameVisualAnimationMode.Once)]
    public void WhenTheAnimalEatsOrWalks_ThenItsModelPlaysTheMatchingAnimation(bool isEating, bool isWalking, string animation, GameVisualAnimationMode mode)
    {
        // Arrange
        var animal = new Animal { IsEating = isEating, IsWalking = isWalking };

        // Act
        var part = Assert.Single(animal.Visual.Parts);

        // Assert
        Assert.Equal((animation, mode), (part.Animation, part.AnimationMode));
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
    public void WhenAnAnimalIsAddedToTheWorld_ThenItStartsWalkingWithinTheLongestPause()
    {
        // Arrange
        var animal = new Animal { X = 10, Y = 10 };
        var maximumTicks = (int)(Animal.MaximumPause / GameSimulationSettings.TickInterval) + 2;

        // Act
        _context.AddObject("animal", animal);
        for (var tick = 0; tick < maximumTicks && !animal.IsWalking; tick++)
        {
            _context.Tick();
        }

        // Assert
        Assert.True(animal.IsWalking);
        Assert.Equal((10 + animal.Facing.GetOffsetX(), 10 + animal.Facing.GetOffsetY()), (animal.X, animal.Y));
    }

    [Fact]
    public void WhenABoutStarts_ThenItWalksSeveralTilesInARowAndThenStands()
    {
        // Arrange
        var animal = _context.AddObject("animal", new Animal(new ScriptedRandom()) { X = 10, Y = 10 });

        // Act
        StartBout();
        var whileWalking = Assert.Single(animal.Visual.Parts);
        var positions = new List<int> { animal.Y };
        for (var step = 1; step < Animal.MinimumBoutSteps; step++)
        {
            NextStep();
            positions.Add(animal.Y);
        }

        var isWalkingAfterTheLastStep = animal.IsWalking;
        NextStep();

        // Assert
        Assert.Equal([11, 12, 13, 14], positions);
        Assert.Equal(10, animal.X);
        Assert.Equal(GameDirection.North, animal.Facing);
        Assert.Equal(("Walk", GameVisualAnimationMode.Loop), (whileWalking.Animation, whileWalking.AnimationMode));
        Assert.True(isWalkingAfterTheLastStep);
        Assert.False(animal.IsWalking);
        Assert.Equal(14, animal.Y);
        Assert.Equal(("Idle", GameVisualAnimationMode.Loop), (Assert.Single(animal.Visual.Parts).Animation, Assert.Single(animal.Visual.Parts).AnimationMode));
    }

    [Fact]
    public void WhenAStepOfABoutIsBlocked_ThenTheBoutEnds()
    {
        // Arrange
        var animal = _context.AddObject("animal", new Animal(new ScriptedRandom()) { X = 10, Y = 10 });
        _context.AddWall(10, 15, width: 4);

        // Act
        StartBout();
        var walkedTo = animal.Y;
        NextStep();

        // Assert
        Assert.Equal(11, walkedTo);
        Assert.Equal(11, animal.Y);
        Assert.False(animal.IsWalking);
    }

    [Theory]
    [InlineData(0, GameDirection.North)]
    [InlineData(49, GameDirection.North)]
    [InlineData(60, GameDirection.NorthEast)]
    [InlineData(80, GameDirection.NorthWest)]
    [InlineData(90, GameDirection.East)]
    [InlineData(99, GameDirection.West)]
    public void WhenABoutChoosesItsDirection_ThenItTurnsFromItsFacingByTheRoll(int roll, GameDirection expected)
    {
        // Arrange
        var animal = _context.AddObject("animal", new Animal(new ScriptedRandom(roll)) { X = 10, Y = 10 });

        // Act
        StartBout();

        // Assert
        Assert.Equal(expected, animal.Facing);
        Assert.Equal((10 + expected.GetOffsetX(), 10 + expected.GetOffsetY()), (animal.X, animal.Y));
    }

    [Fact]
    public void WhenTheTurnsOfAllRollsAreCounted_ThenMostKeepWithin45DegreesAndNoneExceeds90()
    {
        // Act
        var turns = Enumerable.Range(0, 100).Select(Animal.GetTurn).ToArray();

        // Assert
        Assert.Equal(50, turns.Count(turn => turn == 0));
        Assert.Equal(36, turns.Count(turn => Math.Abs(turn) == 1));
        Assert.Equal(14, turns.Count(turn => Math.Abs(turn) == 2));
        Assert.Equal(turns.Count(turn => turn == 1), turns.Count(turn => turn == -1));
        Assert.Equal(turns.Count(turn => turn == 2), turns.Count(turn => turn == -2));
    }

    [Theory]
    [InlineData(0, GameDirection.East)]
    [InlineData(1, GameDirection.West)]
    public void WhenTheChosenDirectionIsBlocked_ThenItTakesTheSmallestFreeTurn(int side, GameDirection expected)
    {
        // Arrange
        // Blocks north, north-east and north-west.
        var animal = _context.AddObject("animal", new Animal(new ScriptedRandom(0, side)) { X = 10, Y = 10 });
        _context.AddWall(9, 14, width: 6);

        // Act
        StartBout();

        // Assert
        Assert.Equal(expected, animal.Facing);
        Assert.Equal((10 + expected.GetOffsetX(), 10), (animal.X, animal.Y));
        Assert.True(animal.IsWalking);
    }

    [Fact]
    public void WhenOnlyTheWayBackIsFree_ThenItTurns90DegreesAndWalksBackInTheNextBout()
    {
        // Arrange
        var animal = _context.AddObject("animal", new Animal(new ScriptedRandom()) { X = 10, Y = 10 });
        _context.AddWall(9, 14, width: 6);
        _context.AddWall(9, 9, width: 1, depth: 5);
        _context.AddWall(14, 9, width: 1, depth: 5);

        // Act
        StartBout();
        var afterTheFirstBout = (animal.X, animal.Y, animal.Facing, animal.IsWalking);
        StartBout();

        // Assert
        Assert.Equal((10, 10, GameDirection.East, false), afterTheFirstBout);
        Assert.Equal((10, 9, GameDirection.South), (animal.X, animal.Y, animal.Facing));
        Assert.True(animal.IsWalking);
    }

    [Fact]
    public void WhenEverySideIsBlocked_ThenItStaysAndTurnsAtMost90DegreesPerBout()
    {
        // Arrange
        var animal = new Animal { X = 10, Y = 10 };
        _context.AddWall(9, 9, width: 6);
        _context.AddWall(9, 14, width: 6);
        _context.AddWall(9, 10, width: 1, depth: 4);
        _context.AddWall(14, 10, width: 1, depth: 4);
        var facings = new List<GameDirection>();

        // Act
        _context.AddObject("animal", animal);
        for (var second = 0; second < 60; second++)
        {
            facings.Add(animal.Facing);
            _context.TimeProvider.Advance(TimeSpan.FromSeconds(1));
            _context.Tick();
        }

        // Assert
        Assert.Equal((10, 10), (animal.X, animal.Y));
        Assert.False(animal.IsWalking);
        Assert.All(facings.Zip(facings.Skip(1)), pair =>
        {
            var turn = (((int)pair.Second - (int)pair.First) % 8 + 8) % 8;
            Assert.Contains(turn, new[] { 0, 2, 6 });
        });
        Assert.DoesNotContain(_logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public void WhenTwoAnimalsWalkTowardEachOtherInTheSameTick_ThenTheyNeverOverlap()
    {
        // Arrange
        // One tile apart; the second checks its step after the first already took that tile in the same tick.
        var first = _context.AddObject("first", new Animal(new ScriptedRandom()) { X = 10, Y = 10, Facing = GameDirection.East });
        var second = _context.AddObject("second", new Animal(new ScriptedRandom()) { X = 15, Y = 10, Facing = GameDirection.West });
        var overlaps = 0;

        // Act
        StartBout();
        var firstAfterItsStep = (first.X, first.Y);
        for (var tick = 0; tick < 400; tick++)
        {
            overlaps += first.Area.Intersects(second.Area) ? 1 : 0;
            _context.Tick();
        }

        // Assert
        Assert.Equal((11, 10), firstAfterItsStep);
        Assert.Equal(0, overlaps);
        Assert.False(first.Area.Intersects(second.Area));
    }

    [Fact]
    public void WhenAddedWithoutAHome_ThenItsHomeIsWhereItStands()
    {
        // Arrange
        var animal = new Animal { X = 7, Y = -3 };

        // Act
        _context.AddObject("animal", animal);

        // Assert
        Assert.Equal((7, -3), (animal.HomeX, animal.HomeY));
        Assert.Equal(Animal.DefaultWanderRadius, animal.WanderRadius);
    }

    [Fact]
    public void WhenAStepWouldLeaveTheWanderRadius_ThenItCountsAsBlocked()
    {
        // Arrange
        var animal = _context.AddObject("animal", new Animal(new ScriptedRandom()) { X = 10, Y = 10, WanderRadius = 2 });

        // Act
        StartBout();
        NextStep();
        var positionAtTheEdge = (animal.X, animal.Y, animal.IsWalking);
        NextStep();

        // Assert
        Assert.Equal((10, 12, true), positionAtTheEdge);
        Assert.Equal((10, 12), (animal.X, animal.Y));
        Assert.False(animal.IsWalking);
    }

    [Fact]
    public void WhenOutsideTheWanderRadius_ThenItOnlyStepsTowardHome()
    {
        // Arrange
        // Faces away from its home 10 tiles south, so only the steps that turn by 135 degrees come closer.
        var animal = _context.AddObject("animal", new Animal(new ScriptedRandom()) { X = 10, Y = 10, HomeX = 10, HomeY = 0, WanderRadius = 2 });

        // Act
        StartBout();

        // Assert
        Assert.Equal((11, 9, GameDirection.SouthEast), (animal.X, animal.Y, animal.Facing));
        Assert.True(animal.IsWalking);
    }

    [Fact]
    public void WhenTheWanderRadiusIsZero_ThenItWandersAnywhere()
    {
        // Arrange
        var animal = _context.AddObject("animal", new Animal(new ScriptedRandom()) { X = 10, Y = 10, HomeX = 10, HomeY = -100, WanderRadius = 0 });

        // Act
        StartBout();

        // Assert
        Assert.Equal((10, 11, GameDirection.North), (animal.X, animal.Y, animal.Facing));
    }

    [Fact]
    public void WhenFed_ThenTheVisualPlaysEatOnceAndReturnsToIdle()
    {
        // Arrange
        var animal = _context.AddObject("animal", new Animal(new ScriptedRandom()) { X = 10, Y = 10 });

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
    public void WhenFedDuringABout_ThenItStopsWalkingAndWalksAgainAfterEatingAndAPause()
    {
        // Arrange
        var animal = _context.AddObject("animal", new Animal(new ScriptedRandom()) { X = 10, Y = 10 });
        StartBout();

        // Act
        animal.Feed();
        NextStep();
        var whileEating = (animal.Y, animal.IsWalking);
        _context.TimeProvider.Advance(Animal.GetEatDuration(AnimalVariant.Deer));
        _context.Tick();
        var afterEating = (animal.Y, animal.IsEating, animal.IsWalking);
        StartBout();

        // Assert
        Assert.Equal((11, false), whileEating);
        Assert.Equal((11, false, false), afterEating);
        Assert.Equal(12, animal.Y);
        Assert.True(animal.IsWalking);
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
    public void WhenOnAddedToWorldRunsAgain_ThenTheAnimalKeepsOneBout()
    {
        // Arrange
        var animal = _context.AddObject("animal", new Animal(new ScriptedRandom()) { X = 10, Y = 10 });
        _context.Game.Schedule(animal, TimeSpan.Zero, () => ((IGameObjectLifecycle)animal).OnAddedToWorld());
        _context.Tick(2);

        // Act
        StartBout();
        var positions = new List<int> { animal.Y };
        for (var step = 0; step < Animal.MinimumBoutSteps; step++)
        {
            NextStep();
            positions.Add(animal.Y);
        }

        // Assert
        Assert.Equal([11, 12, 13, 14, 14], positions);
        Assert.False(animal.IsWalking);
    }

    [Fact]
    public async Task WhenRemovedDuringABout_ThenItStopsWalking()
    {
        // Arrange
        var random = new ScriptedRandom();
        var animal = _context.AddObject("animal", new Animal(random) { X = 10, Y = 10 });
        StartBout();

        // Act
        await _context.RunAsync(_context.Game.RemoveAsync(animal, CancellationToken.None));
        var position = (animal.X, animal.Y);
        var callsBefore = random.CallCount;
        for (var second = 0; second < 30; second++)
        {
            _context.TimeProvider.Advance(TimeSpan.FromSeconds(1));
            _context.Tick();
        }

        // Assert
        Assert.Equal(position, (animal.X, animal.Y));
        Assert.Equal(callsBefore, random.CallCount);
        Assert.DoesNotContain(_logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    // Lets the shortest pause pass and runs the tick in which the next bout starts.
    private void StartBout()
    {
        _context.TimeProvider.Advance(Animal.MinimumPause);
        _context.Tick();
    }

    private void NextStep() => _context.Tick(TicksPerStep);

    // Returns the queued values from Next(maxValue), 0 once they ran out, and the minimum from Next(minValue, maxValue):
    // the shortest pause, the shortest bout, the roll for walking straight and the first side to try turning to.
    private sealed class ScriptedRandom(params int[] values) : Random
    {
        private readonly Queue<int> _values = new(values);

        public int CallCount { get; private set; }

        public override int Next(int maxValue)
        {
            CallCount++;
            return _values.TryDequeue(out var value) ? value : 0;
        }

        public override int Next(int minValue, int maxValue)
        {
            CallCount++;
            return minValue;
        }
    }
}
