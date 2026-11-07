namespace HomeBlaze.Game.View;

/// <summary>
/// An entity that came into a page's view or changed: its id, unique within the page, whether it is the page's own
/// player, and the payload that all pages share.
/// </summary>
internal readonly record struct GameViewEntry(string Id, bool IsSelf, GameViewPayload Payload);
