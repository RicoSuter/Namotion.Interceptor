using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation;

/// <summary>What an automation with given roles may do, as of the moment its <see cref="IAutomationAuthorizer"/> was asked.</summary>
public interface IAutomationPermissions
{
    /// <summary>
    /// Gets why the automation may call no operation and write no property, as a phrase that follows the path of a target,
    /// such as "the roles of the automation cannot be used: No roles are assigned.", or null. While it is set, the
    /// denials are not asked.
    /// </summary>
    string? Error { get; }

    /// <summary>Gets why the automation may not call the operation, as a phrase that follows its path, or null when it may.</summary>
    /// <param name="operation">The registry property that holds the operation.</param>
    string? GetOperationDenial(RegisteredSubjectProperty operation);

    /// <summary>Gets why the automation may not write the property, as a phrase that follows its path, or null when it may.</summary>
    /// <param name="property">The property to write.</param>
    string? GetPropertyWriteDenial(RegisteredSubjectProperty property);
}
