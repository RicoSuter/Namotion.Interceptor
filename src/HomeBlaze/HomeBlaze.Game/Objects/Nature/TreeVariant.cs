using HomeBlaze.Game.Abstractions;

namespace HomeBlaze.Game.Objects.Nature;

/// <summary>
/// A tree model under <c>wwwroot/objects/nature/models/</c> and its sizes in tiles at a scale of 100 percent.
/// </summary>
/// <param name="File">The file name of the model.</param>
/// <param name="Height">The height in tiles it is shown at, at a scale of 100 percent.</param>
/// <param name="TrunkSize">The width and depth of the trunk's footprint, an even number of at least 2.</param>
/// <param name="CrownSize">The diameter of the circle around the trunk that holds the whole crown.</param>
internal sealed record TreeVariant(string File, int Height, int TrunkSize, int CrownSize)
{
    public string Url { get; } = GameAssets.Url(typeof(TreeVariant), $"objects/nature/models/{File}");
}
