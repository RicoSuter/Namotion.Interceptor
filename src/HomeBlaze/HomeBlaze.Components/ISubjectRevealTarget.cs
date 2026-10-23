using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>An editor that can show where a subject is defined, such as the block of a widget in the text of its page.</summary>
public interface ISubjectRevealTarget
{
    /// <summary>
    /// The name of the cascading value of a page that is edited inline, which switches the page to an editor of its source
    /// to show the subject.
    /// </summary>
    const string SourceEditorName = "SourceEditor";

    /// <summary>Shows where the subject is defined and returns whether it found it.</summary>
    Task<bool> RevealAsync(IInterceptorSubject subject);
}
