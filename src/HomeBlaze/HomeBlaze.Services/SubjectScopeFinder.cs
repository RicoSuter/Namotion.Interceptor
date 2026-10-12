using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Services;

internal static class SubjectScopeFinder
{
    /// <summary>
    /// Returns the subject itself when it is a <typeparamref name="T"/>, otherwise its nearest parent that is one, or null.
    /// </summary>
    public static T? FindNearest<T>(IInterceptorSubject subject)
        where T : class
    {
        return subject as T ?? subject.TryGetFirstParent<T>();
    }
}
