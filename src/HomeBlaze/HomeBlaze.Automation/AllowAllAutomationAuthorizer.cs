using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation;

/// <summary>Allows every operation call and property write; the default until an authorization library decides.</summary>
internal sealed class AllowAllAutomationAuthorizer : IAutomationAuthorizer
{
    public string? GetOperationDenial(IInterceptorSubject automation, RegisteredSubjectProperty operation) => null;

    public string? GetPropertyWriteDenial(IInterceptorSubject automation, RegisteredSubjectProperty property) => null;
}
