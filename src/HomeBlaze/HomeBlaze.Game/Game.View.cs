using HomeBlaze.Game.View;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game;

public partial class Game
{
    private readonly List<GameObject> _viewObjects = [];

    /// <summary>
    /// Brings the tracker in line with the visible objects within <see cref="GameViewTracker.ViewRadius"/> of the viewer,
    /// or of the origin while there is none, and returns what changed.
    /// </summary>
    internal GameViewUpdate UpdateView(GameViewTracker tracker, GamePlayer? viewer)
    {
        lock (_lock)
        {
            _viewObjects.Clear();
            GetIndex().Query(GetViewArea(viewer?.X ?? 0, viewer?.Y ?? 0), _viewObjects);
            return tracker.Update(_viewObjects, viewer);
        }
    }

    private static Footprint GetViewArea(int centerX, int centerY)
    {
        var minimumX = Math.Max(int.MinValue, (long)centerX - GameViewTracker.ViewRadius);
        var maximumX = Math.Min(int.MaxValue, (long)centerX + GameViewTracker.ViewRadius);
        var minimumY = Math.Max(int.MinValue, (long)centerY - GameViewTracker.ViewRadius);
        var maximumY = Math.Min(int.MaxValue, (long)centerY + GameViewTracker.ViewRadius);
        return new Footprint((int)minimumX, (int)minimumY, (int)(maximumX - minimumX + 1), (int)(maximumY - minimumY + 1));
    }
}
