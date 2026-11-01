using HomeBlaze.Game.Abstractions;

namespace HomeBlaze.Game.Nature;

/// <summary>
/// An animal model under <c>wwwroot/models/animals/</c>, the size it is shown at and the animations it plays.
/// </summary>
/// <param name="File">The file name of the model.</param>
/// <param name="Length">The length in tiles from nose to tail, which the model is scaled to.</param>
/// <param name="Height">The height in tiles of the box the model is fitted into, at least the height it is shown at.</param>
/// <param name="EatDuration">How long the eat animation plays, at least the length of the animation.</param>
internal sealed record AnimalModel(string File, int Length, int Height, TimeSpan EatDuration)
{
    /// <summary>
    /// The looping animation while the animal stands or steps.
    /// </summary>
    public const string IdleAnimation = "Idle";

    /// <summary>
    /// The animation played once while it eats.
    /// </summary>
    public const string EatAnimation = "Eating";

    public string Url { get; } = GameAssets.Url(typeof(AnimalModel), $"models/animals/{File}");
}
