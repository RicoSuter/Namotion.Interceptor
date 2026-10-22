using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>An editor that can show where a subject is defined, such as the block of a widget in the text of its page.</summary>
public interface ISubjectRevealTarget
{
    /// <summary>Shows where the subject is defined and returns whether it found it.</summary>
    Task<bool> RevealAsync(IInterceptorSubject subject);
}
