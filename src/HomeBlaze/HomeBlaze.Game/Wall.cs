using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game;

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

    public string? Title => "Wall";

    public string? IconName => "ViewColumn";

    public override bool IsSoundBlocking => true;

    public Wall()
    {
        Width = DefaultLength;
        Depth = 1;
        Height = DefaultHeight;
    }

    [Operation(Title = "Demolish", Icon = "Delete", Position = 1, Description = "Removes this wall from the game.")]
    public Task DemolishAsync(CancellationToken cancellationToken) =>
        (ParentGame ?? throw new InvalidOperationException("The wall is not part of a game.")).RemoveObjectAsync(this, cancellationToken);
}
