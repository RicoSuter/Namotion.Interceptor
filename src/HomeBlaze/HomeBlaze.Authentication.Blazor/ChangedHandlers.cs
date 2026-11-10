using Microsoft.Extensions.Logging;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>Raises a change event so that a failing handler, such as the one of another circuit, skips no other handler.</summary>
internal static partial class ChangedHandlers
{
    /// <summary>Invokes every handler of <paramref name="handlers"/>, logging the exception of each one that throws.</summary>
    public static void Raise(Action? handlers, ILogger logger, string source)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception exception)
            {
                LogHandlerFailed(logger, source, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A handler of the change of {Source} failed; the other handlers still ran.")]
    private static partial void LogHandlerFailed(ILogger logger, string source, Exception exception);
}
