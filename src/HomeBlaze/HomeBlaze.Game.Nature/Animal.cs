using System.Collections.Immutable;
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Game.Nature;

/// <summary>
/// An animal that wanders around: every 2 to 6 seconds it steps one tile in a random direction when nothing blocks it,
/// and turns toward it. It eats when fed and does not step meanwhile. Its body covers a square of tiles that blocks
/// movement; its head and tail reach beyond it.
/// </summary>
[Category("Game")]
[Description("An animal that wanders around and eats when fed")]
[DisplayName("Animal")]
[SubjectIcon("Pets")]
[InterceptorSubject]
public partial class Animal : GameObject, ITitleProvider, IIconProvider
{
    private const int DirectionCount = 8;
    private const int DegreesPerDirection = 360 / DirectionCount;

    private static readonly ImmutableArray<AnimalModel> Models =
    [
        new("deer.glb", 6, 6, TimeSpan.FromSeconds(6.5)),
        new("stag.glb", 7, 8, TimeSpan.FromSeconds(12.5))
    ];

    private readonly Random _random;
    private readonly GameVisualCache<(AnimalVariant Variant, GameDirection Facing, bool IsEating)> _visual = new();

    // Only the game's thread replaces it, in OnAddedToWorld and in the step it schedules.
    private IDisposable? _pendingStep;

    // The latest end of eating; replacing or clearing it disposes the previous one, so at most one is live.
    private IDisposable? _pendingEatEnd;

    /// <summary>
    /// The width and depth of the body's footprint in tiles, 1 m.
    /// </summary>
    public const int Size = 4;

    /// <summary>
    /// The shortest time between two steps.
    /// </summary>
    public static readonly TimeSpan MinimumStepDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The longest time between two steps.
    /// </summary>
    public static readonly TimeSpan MaximumStepDelay = TimeSpan.FromSeconds(6);

    /// <summary>
    /// The model; an undefined value, possible in a loaded file, shows a deer.
    /// </summary>
    [Configuration]
    public partial AnimalVariant Variant { get; set; }

    /// <summary>
    /// The direction of the last step, which the model faces.
    /// </summary>
    [State]
    public partial GameDirection Facing { get; set; }

    /// <summary>
    /// Whether the animal eats, which plays its eat animation once; it ends by itself after the animation.
    /// </summary>
    [State]
    public partial bool IsEating { get; set; }

    public override int Width => Size;

    public override int Depth => Size;

    public override int Height => GetModel(Variant).Height;

    public string? Title => Variant switch
    {
        AnimalVariant.Stag => "Stag",
        _ => "Deer"
    };

    public string? IconName => "Pets";

    public override GameVisual Visual => _visual.Get((Variant, Facing, IsEating),
        static state => CreateVisual(state.Variant, state.Facing, state.IsEating));

    [Derived]
    [PropertyAttribute("Feed", KnownAttributes.IsEnabled)]
    public bool Feed_IsEnabled => !IsEating;

    public Animal()
        : this(Random.Shared)
    {
    }

    /// <param name="random">Chooses the step delays and directions; tests pass a predictable one.</param>
    internal Animal(Random random)
    {
        _random = random;
        Variant = AnimalVariant.Deer;
        Facing = GameDirection.North;
        IsEating = false;
    }

    partial void OnIsEatingChanged(bool newValue)
    {
        if (!newValue)
        {
            Interlocked.Exchange(ref _pendingEatEnd, null)?.Dispose();
        }
        else if (TryGetWorld(out var world))
        {
            ScheduleEatEnd(world);
        }
    }

    protected override void OnAddedToWorld()
    {
        if (IsEating)
        {
            ScheduleEatEnd(World);
        }

        ScheduleNextStep(World);
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

    private static AnimalModel GetModel(AnimalVariant variant) => variant == AnimalVariant.Stag ? Models[1] : Models[0];

    private void ScheduleEatEnd(IGameWorld world)
    {
        var schedule = world.Schedule(this, GetModel(Variant).EatDuration, () => IsEating = false);
        Interlocked.Exchange(ref _pendingEatEnd, schedule)?.Dispose();
    }

    private void ScheduleNextStep(IGameWorld world)
    {
        var delay = TimeSpan.FromMilliseconds(_random.Next((int)MinimumStepDelay.TotalMilliseconds, (int)MaximumStepDelay.TotalMilliseconds + 1));
        _pendingStep?.Dispose();
        _pendingStep = world.Schedule(this, delay, Step);
    }

    // Runs on the game's thread; a blocked step is skipped and tried again with the next one.
    private void Step()
    {
        var world = World;
        if (!IsEating)
        {
            var direction = (GameDirection)_random.Next(DirectionCount);
            if (Area.TryMove(direction.GetOffsetX(), direction.GetOffsetY(), out var next) && world.IsFree(next, this))
            {
                X = next.X;
                Y = next.Y;
                Facing = direction;
            }
        }

        ScheduleNextStep(world);
    }

    // The box is as wide and deep as the model is long and centered on the body, so the model fits it lengthwise for
    // every facing; the box's height is at least the model's, so its length sets the scale.
    private static GameVisual CreateVisual(AnimalVariant variant, GameDirection facing, bool isEating)
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
            Rotation = (((int)facing % DirectionCount) + DirectionCount) % DirectionCount * DegreesPerDirection,
            Animation = isEating ? AnimalModel.EatAnimation : AnimalModel.IdleAnimation,
            AnimationMode = isEating ? GameVisualAnimationMode.Once : GameVisualAnimationMode.Loop
        }]);
    }
}
