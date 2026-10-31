using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Tools;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Objects.Nature;

/// <summary>
/// Plants trees in front of the player.
/// </summary>
[Category("Game")]
[Description("Plants trees in front of the player")]
[DisplayName("Tree tool")]
[SubjectIcon("Park")]
[InterceptorSubject]
public partial class TreeTool : GameTool, ITitleProvider, IIconProvider
{
    private readonly Random _random;

    public TreeTool()
        : this(Random.Shared)
    {
    }

    /// <param name="random">Chooses variant, rotation and scale; tests pass a seeded one.</param>
    internal TreeTool(Random random)
    {
        _random = random;
    }

    public string? Title => "Tree tool";

    public string? IconName => "Park";

    /// <summary>
    /// Plants a tree with a random rotation and scale in front of the player, its whole trunk on the tiles right in front
    /// and centered on the player.
    /// </summary>
    /// <param name="variant">The model, 0 to <see cref="Tree.VariantCount"/> - 1; null picks one at random.</param>
    /// <param name="caller">The acting player.</param>
    /// <param name="cancellationToken">Cancels saving.</param>
    /// <exception cref="ArgumentOutOfRangeException">The variant is out of range.</exception>
    /// <exception cref="InvalidOperationException">There is no player, it faces diagonally, something is in the way, or
    /// the tool is not part of a game.</exception>
    [Operation(Title = "Plant tree", Icon = "Park", Position = 1,
        Description = "Plants a tree in front of you. An empty variant picks one at random.")]
    public Task PlantTreeAsync([Range(0, Tree.VariantCount - 1)] int? variant, [FromServices] GameCaller? caller, CancellationToken cancellationToken)
    {
        if (variant is < 0 or >= Tree.VariantCount)
        {
            throw new ArgumentOutOfRangeException(nameof(variant), variant, $"The variant is from 0 to {Tree.VariantCount - 1}.");
        }

        var game = GetGame();
        var tree = new Tree
        {
            Variant = variant ?? _random.Next(Tree.VariantCount),
            Rotation = _random.Next(360),
            Scale = _random.Next(Tree.MinimumScale, Tree.MaximumScale + 1)
        };
        var placement = game.GetPlacementInFront(caller, across: tree.Width, along: tree.Depth);
        tree.X = placement.X;
        tree.Y = placement.Y;
        return game.PlaceAsync(caller, "tree", tree, cancellationToken);
    }
}
