namespace Namotion.Interceptor.Sources;

/// <summary>
/// Registers, manages and dispatches mutation actions on subjects.
/// Used to delay, batch, sequence, or replay updates to subjects.
/// </summary>
public interface ISubjectMutationDispatcher
{
    /// <summary>
    /// Enqueues an update to be applied to the subject.
    /// </summary>
    /// <param name="update">The update action.</param>
    void EnqueueSubjectUpdate(Action update);
}