using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Components;

/// <summary>
/// Interface for components that display or edit subjects.
/// Components implementing this interface receive the subject to display/edit.
/// </summary>
public interface ISubjectComponent
{
    /// <summary>
    /// Gets or sets the subject to display or edit.
    /// </summary>
    IInterceptorSubject? Subject { get; set; }
}
