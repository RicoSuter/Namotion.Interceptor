using Namotion.Interceptor;

namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// Result of the create subject dialog.
/// </summary>
/// <param name="Subject">The created subject.</param>
/// <param name="Name">The chosen name without file extension, or null when the caller asked for no name.</param>
public record CreateSubjectResult(IInterceptorSubject Subject, string? Name);
