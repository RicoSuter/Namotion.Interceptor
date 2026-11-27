using HomeBlaze.Abstractions;
using HomeBlaze.Game.Abstractions;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// The simulation's view of something in the world: an object read from its subject, or a player whose state the
/// simulation owns. Touched only on the simulation thread.
/// </summary>
internal class GameEntity
{
    public GameEntity(GameObject subject)
    {
        Subject = subject;
    }

    /// <summary>
    /// The subject this entity belongs to; its facade.
    /// </summary>
    public GameObject Subject { get; }

    public GameArea Area { get; set; }

    public int Height { get; set; }

    public bool IsMovementBlocking { get; set; }

    public bool IsSoundBlocking { get; set; }

    public bool IsVisible { get; set; }

    /// <summary>
    /// The visual of an object, null for a player.
    /// </summary>
    public GameVisual? Visual { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The region of an object in the world, null for a player and for an object that left the world.
    /// </summary>
    public GameRegionState? Region { get; set; }

    /// <summary>
    /// Reads area, height, blocking, visibility, visual and title from the subject; returns whether anything changed.
    /// </summary>
    public bool ReadFromSubject()
    {
        var subject = Subject;
        var area = subject.Area;
        var height = subject.Height;
        var isMovementBlocking = subject.IsMovementBlocking;
        var isSoundBlocking = subject.IsSoundBlocking;
        var isVisible = subject.IsVisible;
        var visual = subject.Visual;
        var title = (subject as ITitleProvider)?.Title ?? string.Empty;
        if ((area, height, isMovementBlocking, isSoundBlocking, isVisible, visual, title) ==
            (Area, Height, IsMovementBlocking, IsSoundBlocking, IsVisible, Visual, Title))
        {
            return false;
        }

        (Area, Height, IsMovementBlocking, IsSoundBlocking, IsVisible, Visual, Title) =
            (area, height, isMovementBlocking, isSoundBlocking, isVisible, visual, title);
        return true;
    }
}
