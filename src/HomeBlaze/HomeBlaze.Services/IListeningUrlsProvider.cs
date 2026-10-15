namespace HomeBlaze.Services;

/// <summary>
/// Provides the URLs the web server listens on, or none before it started.
/// </summary>
public interface IListeningUrlsProvider
{
    IReadOnlyList<string> GetListeningUrls();
}
