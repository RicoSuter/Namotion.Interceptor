namespace HomeBlaze.Game.View;

/// <summary>
/// An object as the browser draws it. Ids are unique within one page.
/// </summary>
internal sealed record GameViewObject(
    string Id,
    string Kind,
    int X,
    int Y,
    int Width,
    int Depth,
    int Height,
    int Facing,
    string Title,
    string Color,
    bool IsBlocking,
    bool IsSelf)
{
    public const string PlayerKind = "player";
    public const string WallKind = "wall";
    public const string ObjectKind = "object";
}
