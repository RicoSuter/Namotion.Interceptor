using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Tests.Testing;

/// <summary>An authorizer that denies every operation call and property write, naming the member.</summary>
internal sealed class DenyingAuthorizer : IAutomationAuthorizer
{
    public string? GetOperationDenial(IInterceptorSubject automation, RegisteredSubjectProperty operation) =>
        $"The automation may not call {operation.Name}";

    public string? GetPropertyWriteDenial(IInterceptorSubject automation, RegisteredSubjectProperty property) =>
        $"The automation may not write {property.Name}";
}
