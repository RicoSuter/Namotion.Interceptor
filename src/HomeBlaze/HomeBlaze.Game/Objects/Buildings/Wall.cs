using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Visuals;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Objects.Buildings;

/// <summary>
/// A wall segment that blocks the tiles it stands on.
/// </summary>
[Category("Game")]
[Description("A wall segment that blocks the tiles it stands on")]
[DisplayName("Wall")]
[SubjectIcon("ViewColumn")]
[InterceptorSubject]
public partial class Wall : GameObject, ITitleProvider, IIconProvider
{
    // 8 tiles (2 m) is above head height, so lower walls let sound pass over them.
    private const int SoundBlockingHeight = 8;

    private readonly GameVisualCache<(int Width, int Height, int Depth, WallMaterial Material)> _visual = new();

    /// <summary>
    /// The length of a built wall in tiles, 1 m.
    /// </summary>
    public const int DefaultLength = 4;

    /// <summary>
    /// The height of a built wall in tiles, 2.5 m.
    /// </summary>
    public const int DefaultHeight = 10;

    [Configuration]
    [Range(1, MaximumSize)]
    public override partial int Width { get; set; }

    [Configuration]
    [Range(1, MaximumSize)]
    public override partial int Depth { get; set; }

    [Configuration]
    [Range(1, MaximumSize)]
    public override partial int Height { get; set; }

    [Configuration]
    public partial WallMaterial Material { get; set; }

    public string? Title => "Wall";

    public string? IconName => "ViewColumn";

    public override bool IsSoundBlocking => Height >= SoundBlockingHeight;

    public override GameVisual Visual => _visual.Get((Width, Height, Depth, Material),
        static state => new GameVisual([WallTextures.CreateBox(state.Material, default, new GameVisualVector(state.Width, state.Height, state.Depth))]));

    public Wall()
    {
        Width = DefaultLength;
        Depth = 1;
        Height = DefaultHeight;
        Material = WallMaterial.Bricks;
    }

    /// <exception cref="InvalidOperationException">The wall is not part of a game.</exception>
    [Operation(Title = "Demolish", Icon = "Delete", Position = 1, Description = "Removes this wall from the game.")]
    public Task DemolishAsync(CancellationToken cancellationToken) => GetGame().RemoveAsync(this, cancellationToken);
}
