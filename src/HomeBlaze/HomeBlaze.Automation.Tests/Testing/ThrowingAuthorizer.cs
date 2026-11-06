using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Tests.Testing;

/// <summary>An authorizer that fails on every question with the message "The policy store is offline".</summary>
internal sealed class ThrowingAuthorizer : IAutomationAuthorizer
{
    public const string Message = "The policy store is offline";

    public string? GetOperationDenial(IInterceptorSubject automation, RegisteredSubjectProperty operation) =>
        throw new InvalidOperationException(Message);

    public string? GetPropertyWriteDenial(IInterceptorSubject automation, RegisteredSubjectProperty property) =>
        throw new InvalidOperationException(Message);
}
