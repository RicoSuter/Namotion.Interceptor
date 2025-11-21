namespace Namotion.Interceptor.Sources;

/// <summary>
/// Represents a source that can provide and synchronize data for an <see cref="IInterceptorSubject"/> and acts as a client of the data.
/// </summary>
public interface ISubjectClientSource : ISubjectSource
{
    /// <summary>
    /// Loads the complete state of the source and returns a delegate that applies the loaded state to the associated subject.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// A delegate that applies the loaded state to the subject. Returns <c>null</c> if there is no state to apply.
    /// </returns>
    Task<Action?> LoadCompleteSourceStateAsync(CancellationToken cancellationToken);
}