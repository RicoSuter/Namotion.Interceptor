namespace HomeBlaze.Game.View;

/// <summary>
/// An entity as the browser draws it, the same for every page: <see cref="GameViewEntry"/> adds what differs per page.
/// Immutable, so the pages of a tick share one instance per entity and serialize it on their own threads.
/// <see cref="Parts"/> is the entity's visual, or the default box when the visual is invalid. Players carry no parts,
/// because the page draws them itself.
/// </summary>
internal sealed record GameViewPayload(
    string Kind,
    int X,
    int Y,
    int Width,
    int Depth,
    int Height,
    int Facing,
    string Title,
    string Color,
    string Character,
    bool IsMovementBlocking,
    bool IsMicrophoneOn,
    bool IsAudioOn,
    string? Animation,
    int AnimationMode,
    int AnimationSequence,
    IReadOnlyList<GameViewPart> Parts)
{
    public const string PlayerKind = "player";
    public const string ObjectKind = "object";
}
