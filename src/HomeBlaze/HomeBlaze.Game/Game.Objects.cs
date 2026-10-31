using HomeBlaze.Game.World;

namespace HomeBlaze.Game;

public partial class Game
{
    /// <summary>
    /// The message with which a change is refused because a movement blocking object stands in the way.
    /// </summary>
    internal const string BlockedMessage = "Something is in the way.";

    private const string NotPartOfGameMessage = "The object is not part of this game.";

    /// <summary>
    /// Removes the object from <see cref="Objects"/> and saves the game.
    /// Canceling only stops the write; the change stays applied and is saved later.
    /// </summary>
    /// <exception cref="InvalidOperationException">The object is not part of this game.</exception>
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

    /// <summary>
    /// Runs <paramref name="change"/> under the game's lock, updates the index and saves the game. When the change throws,
    /// or the object blocks movement afterwards and overlaps another movement blocking object, its configuration and state
    /// properties get back the values they had before.
    /// Canceling only stops the write; the change stays applied and is saved later.
    /// </summary>
    /// <remarks>
    /// The change runs under the game's lock: it may only set properties of the object and must not call into the game.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The object is not part of this game, or the change makes it overlap
    /// another movement blocking object.</exception>
    public async Task UpdateAsync(GameObject gameObject, Action change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            if (FindObjectName(CurrentObjects, gameObject) is null)
            {
                throw new InvalidOperationException(NotPartOfGameMessage);
            }

            ApplyChange(gameObject, change);
        }

        await WriteOrRequestSaveAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces the object by the replacements in one step under the game's lock, each under a new name of its prefix, a
    /// dash and 8 random hex digits, and saves the game; no other page sees an intermediate state.
    /// Canceling only stops the write; the change stays applied and is saved later.
    /// </summary>
    /// <exception cref="ArgumentException">A name prefix is empty, a replacement is listed twice or already part of this
    /// game, or its size is outside 1 to <see cref="GameObject.MaximumSize"/>.</exception>
    /// <exception cref="InvalidOperationException">The object is not part of this game, or a movement blocking replacement
    /// overlaps a movement blocking object other than the replaced one; nothing changes then.</exception>
    public async Task ReplaceAsync(
        GameObject gameObject,
        IReadOnlyList<(string NamePrefix, GameObject Replacement)> replacements,
        CancellationToken cancellationToken)
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
            foreach (var (namePrefix, replacement) in replacements)
            {
                next[CreateObjectName(next, namePrefix)] = replacement;
            }

            ReplaceObjects(next);
            index.Remove(gameObject);
            foreach (var (_, replacement) in replacements)
            {
                index.AddOrUpdate(replacement);
            }
        }

        await WriteOrRequestSaveAsync(cancellationToken);
    }

    // Callers hold _lock.
    private void ApplyChange(GameObject gameObject, Action change)
    {
        var snapshot = GameObjectSnapshot.Capture(gameObject);
        var isApplied = false;
        try
        {
            change();
            isApplied = true;
        }
        finally
        {
            if (!isApplied)
            {
                snapshot.Restore();
            }
        }

        var index = GetIndex();
        index.AddOrUpdate(gameObject);
        if (gameObject.IsMovementBlocking && index.IsBlocked(gameObject.Footprint, gameObject))
        {
            snapshot.Restore();
            index.AddOrUpdate(gameObject);
            throw new InvalidOperationException(BlockedMessage);
        }
    }

    // Callers hold _lock.
    private static void ValidateReplacements(
        Dictionary<string, GameObject> objects,
        GameWorldIndex index,
        GameObject replaced,
        IReadOnlyList<(string NamePrefix, GameObject Replacement)> replacements)
    {
        for (var i = 0; i < replacements.Count; i++)
        {
            var (namePrefix, replacement) = replacements[i];
            if (string.IsNullOrEmpty(namePrefix))
            {
                throw new ArgumentException("A name prefix is empty.", nameof(replacements));
            }

            ValidateNewObject(objects, replacement, nameof(replacements));
            if (replacement.IsMovementBlocking && index.IsBlocked(replacement.Footprint, replaced))
            {
                throw new InvalidOperationException(BlockedMessage);
            }

            ValidateAgainstEarlierReplacements(replacements, i);
        }
    }

    private static void ValidateAgainstEarlierReplacements(
        IReadOnlyList<(string NamePrefix, GameObject Replacement)> replacements, int position)
    {
        var replacement = replacements[position].Replacement;
        for (var i = 0; i < position; i++)
        {
            var earlier = replacements[i].Replacement;
            if (ReferenceEquals(earlier, replacement))
            {
                throw new ArgumentException("A replacement is listed twice.", nameof(replacements));
            }

            if (AreBothMovementBlocking(earlier, replacement) && earlier.Footprint.Intersects(replacement.Footprint))
            {
                throw new InvalidOperationException(BlockedMessage);
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

    private static string CreateObjectName(Dictionary<string, GameObject> objects, string prefix)
    {
        string name;
        do
        {
            name = $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];
        }
        while (objects.ContainsKey(name));

        return name;
    }
}
