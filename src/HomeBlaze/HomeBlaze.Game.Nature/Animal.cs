using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Game.Nature;

/// <summary>
/// An animal that wanders around its home in walking bouts: after a pause of 3 to 8 seconds it walks 4 to 12 tiles in a
/// row in a direction close to its facing, one tile every 250 ms, until a step is blocked or would leave the
/// <see cref="WanderRadius"/>. It turns by at most 90 degrees at once. It eats when fed and does not walk meanwhile. Its
/// body covers a square of tiles that blocks movement; its head and tail reach beyond it.
/// </summary>
[Category("Game")]
[Description("An animal that wanders around and eats when fed")]
[DisplayName("Animal")]
[SubjectIcon("Pets")]
[InterceptorSubject]
public partial class Animal : GameObject, ITitleProvider, IIconProvider
{
    private const int DirectionCount = 8;

    // The turns a bout tries, in steps of 45 degrees and multiplied by a random side, after the one it chose: straight,
    // then the smallest turns first. Turning around is left out; a boxed-in animal turns by 90 degrees instead.
    private static readonly int[] FallbackTurns = [0, 1, -1, 2, -2, 3, -3];

    private static readonly AnimalModel DeerModel = new("deer.glb", 6, 6, TimeSpan.FromSeconds(6.5));
    private static readonly AnimalModel StagModel = new("stag.glb", 7, 8, TimeSpan.FromSeconds(12.5));

    private readonly Random _random;
    private readonly GameVisualCache<(AnimalVariant Variant, GameDirection Facing, bool IsEating, bool IsWalking)> _visual = new();

    // Only the game's thread touches these, in OnAddedToWorld and in the actions they schedule: the pending pause or step,
    // and the steps left in the current bout.
    private IDisposable? _pendingStep;
    private int _remainingSteps;

    // The latest end of eating; replacing or clearing it disposes the previous one, so at most one is live.
    private IDisposable? _pendingEatEnd;

    /// <summary>
    /// The width and depth of the body's footprint in tiles, 1 m.
    /// </summary>
    public const int Size = 4;

    /// <summary>
    /// The default <see cref="WanderRadius"/> in tiles, 6 m.
    /// </summary>
    public const int DefaultWanderRadius = 24;

    /// <summary>
    /// The fewest tiles a bout walks unless a step is blocked.
    /// </summary>
    public const int MinimumBoutSteps = 4;

    /// <summary>
    /// The most tiles a bout walks.
    /// </summary>
    public const int MaximumBoutSteps = 12;

    /// <summary>
    /// The shortest pause before a bout.
    /// </summary>
    public static readonly TimeSpan MinimumPause = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The longest pause before a bout.
    /// </summary>
    public static readonly TimeSpan MaximumPause = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The time per tile while walking, about 1 m/s.
    /// </summary>
    public static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The model; an undefined value, possible in a loaded file, shows a deer.
    /// </summary>
    [Configuration]
    public partial AnimalVariant Variant { get; set; }

    /// <summary>
    /// The position the animal wanders around, the south-west corner of its footprint there; set to where it stands when
    /// it enters the world without one.
    /// </summary>
    [Configuration]
    public partial int? HomeX { get; set; }

    /// <summary>
    /// See <see cref="HomeX"/>.
    /// </summary>
    [Configuration]
    public partial int? HomeY { get; set; }

    /// <summary>
    /// How far in tiles the animal wanders from its home; zero or less lets it wander anywhere. A step that would leave the
    /// radius counts as blocked unless it comes closer to home, so an animal outside the radius walks back.
    /// </summary>
    [Configuration]
    public partial int WanderRadius { get; set; }

    /// <summary>
    /// The direction the animal faces and walks in.
    /// </summary>
    [State]
    public partial GameDirection Facing { get; set; }

    /// <summary>
    /// Whether the animal eats, which plays its eat animation once; it ends by itself after the animation.
    /// </summary>
    [State]
    public partial bool IsEating { get; set; }

    /// <summary>
    /// Whether the animal is in a walking bout, which loops its walk animation unless it eats.
    /// </summary>
    [State]
    public partial bool IsWalking { get; set; }

    public override int Width => Size;

    public override int Depth => Size;

    public override int Height => GetModel(Variant).Height;

    public string? Title => Variant switch
    {
        AnimalVariant.Stag => "Stag",
        _ => "Deer"
    };

    public string? IconName => "Pets";

    public override GameVisual Visual => _visual.Get((Variant, Facing, IsEating, IsWalking),
        static state => CreateVisual(state.Variant, state.Facing, state.IsEating, state.IsWalking));

    [Derived]
    [PropertyAttribute("Feed", KnownAttributes.IsEnabled)]
    public bool Feed_IsEnabled => !IsEating;

    public Animal()
        : this(Random.Shared)
    {
    }

    /// <param name="random">Chooses the pauses, turns and bout lengths; tests pass a predictable one.</param>
    internal Animal(Random random)
    {
        _random = random;
        Variant = AnimalVariant.Deer;
        HomeX = null;
        HomeY = null;
        WanderRadius = DefaultWanderRadius;
        Facing = GameDirection.North;
        IsEating = false;
        IsWalking = false;
    }

    partial void OnIsEatingChanged(bool newValue)
    {
        if (newValue)
        {
            if (TryGetWorld(out var world))
            {
                ScheduleEatEnd(world);
            }

            return;
        }

        // The handle is read before IsEating: feeding on another thread after this write stores a new handle, and only the
        // handle of this end may be cleared, never the new one.
        var pending = Volatile.Read(ref _pendingEatEnd);
        if (!IsEating && pending is not null && Interlocked.CompareExchange(ref _pendingEatEnd, null, pending) == pending)
        {
            pending.Dispose();
        }
    }

    protected override void OnAddedToWorld()
    {
        var world = World;
        if (HomeX is null || HomeY is null)
        {
            (HomeX, HomeY) = (X, Y);
        }

        if (IsEating)
        {
            ScheduleEatEnd(world);
        }

        EndBout(world);
    }

    /// <exception cref="InvalidOperationException">The animal is already eating.</exception>
    [Operation(Title = "Feed", Icon = "Restaurant", Position = 1, Description = "Gives this animal something to eat.")]
    public void Feed()
    {
        if (IsEating)
        {
            throw new InvalidOperationException("The animal is already eating.");
        }

        IsEating = true;
    }

    internal static TimeSpan GetEatDuration(AnimalVariant variant) => GetModel(variant).EatDuration;

    /// <summary>
    /// Maps a roll of 0 to 99 to the turn of a new bout in steps of 45 degrees, clockwise positive: straight half of the
    /// time, 45 degrees to either side 36 percent and 90 degrees 14 percent of the time.
    /// </summary>
    internal static int GetTurn(int roll) => roll switch
    {
        < 50 => 0,
        < 68 => 1,
        < 86 => -1,
        < 93 => 2,
        _ => -2
    };

    private static AnimalModel GetModel(AnimalVariant variant) => variant switch
    {
        AnimalVariant.Stag => StagModel,
        _ => DeerModel
    };

    private static GameDirection Turn(GameDirection direction, int turn) =>
        (GameDirection)(((((int)direction + turn) % DirectionCount) + DirectionCount) % DirectionCount);

    private void ScheduleEatEnd(IGameWorld world)
    {
        var schedule = world.Schedule(this, GetModel(Variant).EatDuration, EndEating);
        Interlocked.Exchange(ref _pendingEatEnd, schedule)?.Dispose();
    }

    // Runs on the game's thread; the animal pauses after eating before it walks again.
    private void EndEating()
    {
        IsEating = false;
        EndBout(World);
    }

    private void Schedule(IGameWorld world, TimeSpan delay, Action action)
    {
        _pendingStep?.Dispose();
        _pendingStep = world.Schedule(this, delay, action);
    }

    private void EndBout(IGameWorld world)
    {
        _remainingSteps = 0;
        IsWalking = false;
        var pause = _random.Next((int)MinimumPause.TotalMilliseconds, (int)MaximumPause.TotalMilliseconds + 1);
        Schedule(world, TimeSpan.FromMilliseconds(pause), StartBout);
    }

    // Runs on the game's thread: takes the first step of a bout in the chosen direction, or in the one with the smallest
    // turn whose step is free.
    private void StartBout()
    {
        var world = World;
        var facing = Turn(Facing, 0);
        var chosenTurn = GetTurn(_random.Next(100));
        var side = _random.Next(2) == 0 ? 1 : -1;
        if (!IsEating)
        {
            for (var index = -1; index < FallbackTurns.Length; index++)
            {
                var turn = index < 0 ? chosenTurn : FallbackTurns[index] * side;
                if ((index < 0 || turn != chosenTurn) && TryStep(world, Turn(facing, turn)))
                {
                    _remainingSteps = _random.Next(MinimumBoutSteps, MaximumBoutSteps + 1) - 1;
                    IsWalking = true;
                    Schedule(world, StepInterval, Step);
                    return;
                }
            }

            // Only the way back may be free: turns halfway now, so that the next bout can walk back with another 90 degrees.
            Facing = Turn(facing, 2 * side);
        }

        EndBout(world);
    }

    // Runs on the game's thread; the bout ends one interval after its last step, so the walk animation lasts through it.
    private void Step()
    {
        var world = World;
        if (IsEating || _remainingSteps <= 0 || !TryStep(world, Turn(Facing, 0)))
        {
            EndBout(world);
            return;
        }

        _remainingSteps--;
        Schedule(world, StepInterval, Step);
    }

    private bool TryStep(IGameWorld world, GameDirection direction)
    {
        if (!Area.TryMove(direction.GetOffsetX(), direction.GetOffsetY(), out var next) || !IsNearHome(next) || !world.IsFree(next, this))
        {
            return false;
        }

        X = next.X;
        Y = next.Y;
        Facing = direction;
        return true;
    }

    private bool IsNearHome(GameArea next)
    {
        if (WanderRadius <= 0 || HomeX is not { } homeX || HomeY is not { } homeY)
        {
            return true;
        }

        var distance = GetSquaredDistance(next.X, next.Y, homeX, homeY);
        return distance <= (double)WanderRadius * WanderRadius || distance < GetSquaredDistance(X, Y, homeX, homeY);
    }

    // A double, because the square of an offset across the whole int range overflows a long.
    private static double GetSquaredDistance(int x, int y, int homeX, int homeY)
    {
        var offsetX = (double)x - homeX;
        var offsetY = (double)y - homeY;
        return (offsetX * offsetX) + (offsetY * offsetY);
    }

    // The box is as wide and deep as the model is long and centered on the body, so the model fits it lengthwise for
    // every facing; the box's height is at least the model's, so its length sets the scale.
    private static GameVisual CreateVisual(AnimalVariant variant, GameDirection facing, bool isEating, bool isWalking)
    {
        var model = GetModel(variant);
        var offset = (Size - model.Length) / 2.0;
        return new GameVisual([new GameVisualPart
        {
            Shape = GameVisualShape.Model,
            Model = model.Url,
            Offset = new GameVisualVector(offset, 0, offset),
            Size = new GameVisualVector(model.Length, model.Height, model.Length),
            Pivot = new GameVisualPoint(Size / 2.0, Size / 2.0),
            Rotation = facing.GetRotation(),
            Animation = GetAnimation(isEating, isWalking),
            AnimationMode = isEating ? GameVisualAnimationMode.Once : GameVisualAnimationMode.Loop
        }]);
    }

    private static string GetAnimation(bool isEating, bool isWalking)
    {
        if (isEating)
        {
            return AnimalModel.EatAnimation;
        }

        return isWalking ? AnimalModel.WalkAnimation : AnimalModel.IdleAnimation;
    }
}
