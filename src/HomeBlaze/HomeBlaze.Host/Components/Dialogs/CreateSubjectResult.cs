using Namotion.Interceptor;

namespace HomeBlaze.Host.Components.Dialogs;

/// <summary>
/// Result from the CreateSubjectWizard containing the created subject and chosen name.
/// </summary>
public record CreateSubjectResult(IInterceptorSubject Subject, string Name);
