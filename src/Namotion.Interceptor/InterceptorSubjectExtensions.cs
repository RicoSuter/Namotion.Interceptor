namespace Namotion.Interceptor;

public static class InterceptorSubjectExtensions
{
    public static void SetData(this IInterceptorSubject subject, string key, object? value)
    {
        subject.Data[key] = value;
    }

    public static bool TryGetData(this IInterceptorSubject subject, string key, out object? value)
    {
        return subject.Data.TryGetValue(key, out value);
    }
}