using System.Text.Json.Serialization;
using HomeBlaze.Game.Visuals;

namespace HomeBlaze.Game.View;

/// <summary>
/// An object as the browser draws it. Ids are unique within one page. <see cref="Visual"/> is what the tracker compares,
/// <see cref="Parts"/> what the browser draws: the visual, or the default box when the visual is invalid. Players carry
/// neither, because the page draws them itself.
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
    bool IsMovementBlocking,
    bool IsOpen,
    bool IsSelf,
    bool IsMicrophoneOn,
    bool IsAudioOn,
    [property: JsonIgnore] GameVisual? Visual,
    IReadOnlyList<GameViewPart> Parts)
{
    public const string PlayerKind = "player";
    public const string WallKind = "wall";
    public const string DoorKind = "door";
    public const string ObjectKind = "object";
}
