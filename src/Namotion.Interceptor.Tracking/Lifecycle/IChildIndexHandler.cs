namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A handler that stores the key or position of a child and has to be told when a structural
/// write keeps the child in the property, since neither a detach nor an attach is raised for it.
/// </summary>
internal interface IChildIndexHandler
{
    /// <summary>
    /// Called after a structural write that kept at least one subject in the property, while the
    /// lifecycle interceptor holds its lock: once for every subject of the new value that may sit
    /// under another key or position than before, and for no subject twice.
    /// </summary>
    /// <param name="child">The subject the property holds.</param>
    /// <param name="property">The property that was written.</param>
    /// <param name="index">
    /// The key or position of the subject in the new value: the first one where the value holds the
    /// subject more than once, and <c>null</c> where the value is the subject itself.
    /// </param>
    void RefreshChildIndex(IInterceptorSubject child, PropertyReference property, object? index);
}
