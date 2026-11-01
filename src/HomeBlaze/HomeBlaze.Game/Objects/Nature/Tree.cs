using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Objects.Nature;

/// <summary>
/// A tree that sways in the wind. Its trunk covers a square of tiles that depends on its variant and scale and blocks
/// movement but not sound; its crown reaches beyond it.
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
    /// The smallest width and depth of the trunk's footprint in tiles, 50 cm, like a player.
    /// </summary>
    public const int MinimumSize = 2;

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
        new("common_tree_1.glb", 24, 6, 26),
        new("common_tree_2.glb", 21, 4, 19),
        new("common_tree_3.glb", 24, 4, 17),
        new("common_tree_1_autumn.glb", 22, 4, 24),
        new("pine_tree_1.glb", 22, 4, 13),
        new("pine_tree_2.glb", 24, 2, 12),
        new("pine_tree_3.glb", 16, 2, 8)
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

    /// <summary>
    /// The trunk size of the variant times the scale, rounded to an even number of at least <see cref="MinimumSize"/>.
    /// </summary>
    public override int Width => GetTrunkSize(Variant, Scale);

    /// <summary>
    /// The same as <see cref="Width"/>.
    /// </summary>
    public override int Depth => GetTrunkSize(Variant, Scale);

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

    // Even, so that the footprint's center, where the model's trunk stands, is a tile corner like a player's center, and a
    // tree planted in front of a player is centered on it.
    private static int GetTrunkSize(int variant, int scale) =>
        Math.Max(MinimumSize, 2 * (int)Math.Round(GetVariant(variant).TrunkSize * GetScaleFactor(scale) / 2, MidpointRounding.AwayFromZero));

    // The crown is at least as wide and deep as the model, so fitting the model into the box scales it to the variant's
    // height, and the box, centered on the trunk, holds the whole crown for every rotation.
    private static GameVisual CreateVisual(int variant, int rotation, int scale)
    {
        var model = GetVariant(variant);
        var factor = GetScaleFactor(scale);
        var height = model.Height * factor;
        var crown = model.CrownSize * factor;
        var trunk = GetTrunkSize(variant, scale);
        var offset = (trunk - crown) / 2;
        return new GameVisual([new GameVisualPart
        {
            Shape = GameVisualShape.Model,
            Model = model.Url,
            Offset = new GameVisualVector(offset, 0, offset),
            Size = new GameVisualVector(crown, height, crown),
            Pivot = new GameVisualPoint(trunk / 2.0, trunk / 2.0),
            Rotation = ((rotation % 360) + 360) % 360,
            Effect = GameVisualEffect.Wind
        }]);
    }
}
