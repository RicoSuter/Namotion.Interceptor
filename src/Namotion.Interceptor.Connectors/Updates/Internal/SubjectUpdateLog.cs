using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Resolves the logger subject update warnings are reported through.
/// </summary>
internal static class SubjectUpdateLog
{
    private const string LoggerCategory = "Namotion.Interceptor.Connectors.Updates";

    /// <summary>
    /// The logger to report a diagnostic of <paramref name="rootSubject"/>'s update through, or
    /// <c>null</c> when nothing is listening for warnings.
    /// </summary>
    public static ILogger? TryGetWarningLogger(IInterceptorSubject rootSubject)
    {
        var logger = rootSubject.Context.TryGetService<ILoggerFactory>()?.CreateLogger(LoggerCategory);
        return logger?.IsEnabled(LogLevel.Warning) == true ? logger : null;
    }
}
