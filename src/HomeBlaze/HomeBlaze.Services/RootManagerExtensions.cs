using HomeBlaze.Storage.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Services;

/// <summary>
/// Extension methods for resolving configuration writers via RootManager.
/// </summary>
public static class RootManagerExtensions
{
    /// <summary>
    /// Finds the nearest <see cref="IConfigurationWriter"/> in the subject's parent chain,
    /// falling back to <paramref name="rootManager"/> if none is found.
    /// </summary>
    public static IConfigurationWriter GetConfigurationWriter(
        this RootManager rootManager,
        IInterceptorSubject subject)
    {
        return subject.TryGetFirstParent<IConfigurationWriter>() ?? rootManager;
    }
}
