namespace HomeBlaze.Game.View;

/// <summary>
/// The entities that came into view or changed, and the ids of those that left it.
/// </summary>
internal sealed record GameViewUpdate(IReadOnlyList<GameViewEntry> Upserted, IReadOnlyList<string> Removed)
{
    public bool IsEmpty => Upserted.Count == 0 && Removed.Count == 0;
}
