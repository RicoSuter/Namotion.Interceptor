using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Visuals;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Objects.Nature;

/// <summary>
/// A tree that sways in the wind. Its trunk covers 2 by 2 tiles, which block movement but not sound; its crown may reach
/// beyond them.
/// </summary>
[Category("Game")]
[Description("A tree that sways in the wind; its trunk blocks movement but not sound")]
[DisplayName("Tree")]
[SubjectIcon("Park")]
[InterceptorSubject]
public partial class Tree : GameObject, ITitleProvider, IIconProvider
{
    private readonly GameVisualCache<(int Variant, int Rotation, int Scale)> _visual = new();

    /// <summary>
    /// The width and depth of the trunk's footprint in tiles, 50 cm, like a player.
    /// </summary>
    public const int Size = 2;

    /// <summary>
    /// The number of tree models; <see cref="Variant"/> runs from 0 to one less.
    /// </summary>
    public const int VariantCount = 7;

    /// <summary>
    /// The smallest <see cref="Scale"/> in percent.
    /// </summary>
    public const int MinimumScale = 80;

    /// <summary>
    /// The largest <see cref="Scale"/> in percent.
    /// </summary>
    public const int MaximumScale = 120;

    internal static readonly ImmutableArray<TreeVariant> Variants =
    [
        new("common_tree_1.glb", 24),
        new("common_tree_2.glb", 21),
        new("common_tree_3.glb", 24),
        new("common_tree_1_autumn.glb", 22),
        new("pine_tree_1.glb", 22),
        new("pine_tree_2.glb", 24),
        new("pine_tree_3.glb", 16)
    ];

    /// <summary>
    /// The model, from 0 to <see cref="VariantCount"/> - 1; another value, possible in a loaded file, shows variant 0.
    /// </summary>
    [Configuration]
    [Range(0, VariantCount - 1)]
    public partial int Variant { get; set; }

    /// <summary>
    /// The turn in degrees clockwise seen from above.
    /// </summary>
    [Configuration]
    [Range(0, 359)]
    public partial int Rotation { get; set; }

    /// <summary>
    /// The size in percent of the model's size; a value outside <see cref="MinimumScale"/> to <see cref="MaximumScale"/>
    /// shows as the nearest limit.
    /// </summary>
    [Configuration]
    [Range(MinimumScale, MaximumScale)]
    public partial int Scale { get; set; }

    public string? Title => "Tree";

    public string? IconName => "Park";

    public override int Width => Size;

    public override int Depth => Size;

    public override int Height => (int)Math.Ceiling(GetVariant(Variant).Height * GetScaleFactor(Scale));

    public override GameVisual Visual => _visual.Get((Variant, Rotation, Scale),
        static state => CreateVisual(state.Variant, state.Rotation, state.Scale));

    public Tree()
    {
        Variant = 0;
        Rotation = 0;
        Scale = 100;
    }

    /// <exception cref="InvalidOperationException">The tree is not part of a game.</exception>
    [Operation(Title = "Demolish", Icon = "Delete", Position = 1, Description = "Removes this tree from the game.")]
    public Task DemolishAsync(CancellationToken cancellationToken) => GetGame().RemoveAsync(this, cancellationToken);

    private static TreeVariant GetVariant(int variant) => variant is >= 0 and < VariantCount ? Variants[variant] : Variants[0];

    private static double GetScaleFactor(int scale) => Math.Clamp(scale, MinimumScale, MaximumScale) / 100.0;

    // Every model is taller than wide, so fitting it into a cube of the tree's height scales it to exactly that height.
    private static GameVisual CreateVisual(int variant, int rotation, int scale)
    {
        var model = GetVariant(variant);
        var size = model.Height * GetScaleFactor(scale);
        var offset = (Size - size) / 2;
        return new GameVisual([new GameVisualPart
        {
            Shape = GameVisualShape.Model,
            Model = model.Url,
            Offset = new GameVisualVector(offset, 0, offset),
            Size = new GameVisualVector(size, size, size),
            Pivot = new GameVisualPoint(Size / 2.0, Size / 2.0),
            Rotation = ((rotation % 360) + 360) % 360,
            Effect = GameVisualEffect.Wind
        }]);
    }
}
