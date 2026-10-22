using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>
/// Cascaded to a page that is shown next to its editor. A click on a widget there asks the editor to show where the
/// widget is defined instead of selecting it.
/// </summary>
public sealed class SubjectRevealer
{
    private readonly Func<IInterceptorSubject, Task<bool>> _reveal;

    public SubjectRevealer(Func<IInterceptorSubject, Task<bool>> reveal)
    {
        _reveal = reveal;
    }

    /// <summary>Asks the editor to show where the subject is defined and returns whether it found it.</summary>
    public Task<bool> RevealAsync(IInterceptorSubject subject) => _reveal(subject);
}
