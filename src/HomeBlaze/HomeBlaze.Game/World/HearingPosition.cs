using HomeBlaze.Game.Abstractions;

namespace HomeBlaze.Game.World;

/// <summary>
/// The client id and area of a player who hears and is heard, an immutable copy that any thread may read.
/// </summary>
internal readonly record struct HearingPosition(string ClientId, GameArea Area);
