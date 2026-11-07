namespace HomeBlaze.Game.World;

/// <summary>
/// The chunks whose entities changed since the last <see cref="Clear"/>. Not thread-safe: only the simulation thread uses it.
/// </summary>
internal sealed class ChangedChunks
{
    private readonly HashSet<(long X, long Y)> _chunks = new();

    public IReadOnlyCollection<(long X, long Y)> Chunks => _chunks;

    /// <summary>
    /// Enumerates the chunks without allocating, unlike <see cref="Chunks"/>.
    /// </summary>
    public HashSet<(long X, long Y)>.Enumerator GetEnumerator() => _chunks.GetEnumerator();

    public int Count => _chunks.Count;

    public bool Contains((long X, long Y) chunk) => _chunks.Contains(chunk);

    public void Add((long X, long Y) chunk) => _chunks.Add(chunk);

    public void Clear() => _chunks.Clear();
}
