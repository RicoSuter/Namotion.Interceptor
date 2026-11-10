using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation;

/// <summary>
/// Decides which operations an automation may call and which properties it may write, as the principal the automation
/// runs as. Asked before every call and every write. An operation that requires confirmation is refused before the
/// authorizer is asked.
/// </summary>
public interface IAutomationAuthorizer
{
    /// <summary>Gets why the automation may not call the operation, or null when it may.</summary>
    /// <param name="automation">The automation whose rule runs the action.</param>
    /// <param name="operation">The registry property that holds the operation.</param>
    string? GetOperationDenial(IInterceptorSubject automation, RegisteredSubjectProperty operation);

    /// <summary>Gets why the automation may not write the property, or null when it may.</summary>
    /// <param name="automation">The automation whose rule runs the action.</param>
    /// <param name="property">The property to write.</param>
    string? GetPropertyWriteDenial(IInterceptorSubject automation, RegisteredSubjectProperty property);
}
