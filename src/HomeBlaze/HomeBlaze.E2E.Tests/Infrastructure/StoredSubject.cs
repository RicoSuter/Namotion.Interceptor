using Namotion.Interceptor;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// A subject that a test stored as a JSON file of the root storage. Disposing it deletes the subject with its file, unless
/// it is gone already.
/// </summary>
public sealed class StoredSubject<T> : IAsyncDisposable
    where T : IInterceptorSubject
{
    private readonly PlaywrightFixture _fixture;

    internal StoredSubject(PlaywrightFixture fixture, string name, string filePath, T subject)
    {
        _fixture = fixture;
        Name = name;
        FilePath = filePath;
        Subject = subject;
    }

    /// <summary>The name of the subject under the root, which is its path.</summary>
    public string Name { get; }

    public string FilePath { get; }

    public T Subject { get; }

    public ValueTask DisposeAsync() => new(_fixture.DeleteStoredSubjectAsync(FilePath, Subject));
}
