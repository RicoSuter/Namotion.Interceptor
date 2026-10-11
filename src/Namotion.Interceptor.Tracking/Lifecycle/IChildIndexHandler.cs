namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A handler that stores the key or position of a child and has to be told when a structural
/// write keeps the child in the property, since neither a detach nor an attach is raised for it.
/// </summary>
internal interface IChildIndexHandler
{
    /// <summary>
    /// Called for a subject that a structural write kept in the property or added to it, and
    /// that may sit under another key or position than before, while the lifecycle interceptor
    /// holds its lock. A subject the property holds more than once is refreshed once per key or
    /// position, last with the first of them, which is the one it was attached with.
    /// </summary>
    /// <param name="child">The subject the property holds.</param>
    /// <param name="property">The property that was written.</param>
    /// <param name="index">The key or position of the subject in the new value.</param>
    void RefreshChildIndex(IInterceptorSubject child, PropertyReference property, object? index);
}
