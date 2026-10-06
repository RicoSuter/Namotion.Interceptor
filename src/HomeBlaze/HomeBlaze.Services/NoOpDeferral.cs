namespace HomeBlaze.Services;

/// <summary>
/// A deferral that holds nothing, shared because releasing it has no effect.
/// </summary>
internal sealed class NoOpDeferral : IDisposable
{
    public static readonly NoOpDeferral Instance = new();

    private NoOpDeferral()
    {
    }

    public void Dispose()
    {
    }
}
