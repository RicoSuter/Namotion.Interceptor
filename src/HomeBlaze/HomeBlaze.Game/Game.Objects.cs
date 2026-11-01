using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game;

public partial class Game
{
    private const string NotPartOfGameMessage = "The object is not part of this game.";

    /// <inheritdoc />
    public async Task RemoveAsync(GameObject gameObject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        lock (_lock)
        {
            var objects = CurrentObjects;
            var name = FindObjectName(objects, gameObject) ?? throw new InvalidOperationException(NotPartOfGameMessage);
            var next = new Dictionary<string, GameObject>(objects);
            next.Remove(name);
            ReplaceObjects(next);
            GetIndex().Remove(gameObject);
        }

        await WriteOrRequestSaveAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReplaceAsync(GameObject gameObject, IReadOnlyList<GameObject> replacements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        ArgumentNullException.ThrowIfNull(replacements);
        lock (_lock)
        {
            var objects = CurrentObjects;
            var name = FindObjectName(objects, gameObject) ?? throw new InvalidOperationException(NotPartOfGameMessage);
            var index = GetIndex();
            ValidateReplacements(objects, index, gameObject, replacements);
            var next = new Dictionary<string, GameObject>(objects);
            next.Remove(name);
            foreach (var replacement in replacements)
            {
                next[CreateObjectName(next, replacement)] = replacement;
            }

            ReplaceObjects(next);
            index.Remove(gameObject);
            foreach (var replacement in replacements)
            {
                index.AddOrUpdate(replacement);
            }
        }

        await WriteOrRequestSaveAsync(cancellationToken);
    }

    // Callers hold _lock.
    private static void ValidateReplacements(
        Dictionary<string, GameObject> objects,
        GameWorldIndex index,
        GameObject replaced,
        IReadOnlyList<GameObject> replacements)
    {
        for (var i = 0; i < replacements.Count; i++)
        {
            var replacement = replacements[i];
            ValidateNewObject(objects, replacement, nameof(replacements));
            if (replacement.IsMovementBlocking && index.IsBlocked(replacement.Area, replaced))
            {
                throw new InvalidOperationException(GameMessages.Blocked);
            }

            ValidateAgainstEarlierReplacements(replacements, i);
        }
    }

    private static void ValidateAgainstEarlierReplacements(IReadOnlyList<GameObject> replacements, int position)
    {
        var replacement = replacements[position];
        for (var i = 0; i < position; i++)
        {
            var earlier = replacements[i];
            if (ReferenceEquals(earlier, replacement))
            {
                throw new ArgumentException("A replacement is listed twice.", nameof(replacements));
            }

            if (AreBothMovementBlocking(earlier, replacement) && earlier.Area.Intersects(replacement.Area))
            {
                throw new InvalidOperationException(GameMessages.Blocked);
            }
        }
    }

    private static bool AreBothMovementBlocking(GameObject first, GameObject second) =>
        first.IsMovementBlocking && second.IsMovementBlocking;

    private static void ValidateNewObject(Dictionary<string, GameObject> objects, GameObject gameObject, string parameterName)
    {
        if (gameObject is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (FindObjectName(objects, gameObject) is not null)
        {
            throw new ArgumentException("The object is already part of this game.", parameterName);
        }

        if (!GameWorldIndex.HasValidSize(gameObject))
        {
            throw new ArgumentException($"The width, depth and height of an object are from 1 to {GameObject.MaximumSize} tiles.", parameterName);
        }
    }

    private static string? FindObjectName(Dictionary<string, GameObject> objects, GameObject gameObject)
    {
        foreach (var (name, candidate) in objects)
        {
            if (ReferenceEquals(candidate, gameObject))
            {
                return name;
            }
        }

        return null;
    }

    // The type name in lowercase, a dash and 8 random hex digits.
    private static string CreateObjectName(Dictionary<string, GameObject> objects, GameObject gameObject)
    {
        var prefix = gameObject.GetType().Name.ToLowerInvariant();
        string name;
        do
        {
            name = $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];
        }
        while (objects.ContainsKey(name));

        return name;
    }
}
