using HomeBlaze.Game.Visuals;

namespace HomeBlaze.Game.Objects.Nature;

/// <summary>
/// A tree model: its file under <c>wwwroot/objects/nature/models/</c> and its height in tiles at a scale of 100 percent.
/// </summary>
internal sealed record TreeVariant(string File, int Height)
{
    public string Url { get; } = GameAssets.Url(typeof(TreeVariant), $"objects/nature/models/{File}");
}
