using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Simulation;

namespace HomeBlaze.Game.World;

/// <summary>
/// An entity whose indexed state changed, with the area and visibility it had in the index before its first change
/// since the last <see cref="ChangedEntities.Clear"/>.
/// </summary>
/// <param name="Entity">The entity; it may have left the index since.</param>
/// <param name="OldArea">The area it had in the index before, meaningless unless <paramref name="WasVisible"/>.</param>
/// <param name="WasVisible">Whether it was in the index and visible before.</param>
internal readonly record struct GameEntityChange(GameEntity Entity, GameArea OldArea, bool WasVisible);
