using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Objects.Buildings;

/// <summary>
/// Builds walls in front of the player.
/// </summary>
[Category("Game")]
[Description("Builds walls of a chosen length, height and material in front of the player")]
[DisplayName("Wall tool")]
[SubjectIcon("AddBox")]
[InterceptorSubject]
public partial class WallTool : GameTool, ITitleProvider, IIconProvider
{
    /// <summary>
    /// The longest wall the tool builds in tiles, 10 m.
    /// </summary>
    public const int MaximumLength = 40;

    /// <summary>
    /// The tallest wall the tool builds in tiles, 5 m.
    /// </summary>
    public const int MaximumHeight = 20;

    public string? Title => "Wall tool";

    public string? IconName => "AddBox";

    /// <summary>
    /// Builds a wall one tile thick right in front of the player, across the direction it faces and centered on it.
    /// </summary>
    /// <param name="length">The length in tiles, 1 to <see cref="MaximumLength"/>; null for <see cref="Wall.DefaultLength"/>.</param>
    /// <param name="height">The height in tiles, 1 to <see cref="MaximumHeight"/>; null for <see cref="Wall.DefaultHeight"/>.</param>
    /// <param name="material">The material; null for <see cref="WallMaterial.Bricks"/>.</param>
    /// <param name="caller">The acting player.</param>
    /// <param name="cancellationToken">Cancels saving.</param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is outside its range.</exception>
    /// <exception cref="InvalidOperationException">There is no player, it faces diagonally, something is in the way, or
    /// the tool is not part of a game.</exception>
    [Operation(Title = "Build wall", Icon = "AddBox", Position = 1,
        Description = "Builds a wall 25 cm thick right in front of you, across the direction you face and centered on you. Empty fields build 1 m long, 2.5 m tall bricks.")]
    public Task BuildWallAsync(
        [Range(1, MaximumLength)] int? length,
        [Range(1, MaximumHeight)] int? height,
        WallMaterial? material,
        [FromServices] GameCaller? caller,
        CancellationToken cancellationToken)
    {
        var wallLength = length ?? Wall.DefaultLength;
        var wallHeight = height ?? Wall.DefaultHeight;
        var wallMaterial = material ?? WallMaterial.Bricks;
        ValidateParameters(wallLength, wallHeight, wallMaterial);

        var spot = World.GetSpotInFront(caller, across: wallLength, along: 1);
        var wall = new Wall
        {
            X = spot.X,
            Y = spot.Y,
            Width = spot.Width,
            Depth = spot.Depth,
            Height = wallHeight,
            Material = wallMaterial
        };
        return World.PlaceAsync(wall, cancellationToken);
    }

    private static void ValidateParameters(int length, int height, WallMaterial material)
    {
        if (length is < 1 or > MaximumLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), $"A wall is 1 to {MaximumLength} tiles long.");
        }

        if (height is < 1 or > MaximumHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(height), $"A wall is 1 to {MaximumHeight} tiles tall.");
        }

        if (!Enum.IsDefined(material))
        {
            throw new ArgumentOutOfRangeException(nameof(material), "The material is unknown.");
        }
    }
}
