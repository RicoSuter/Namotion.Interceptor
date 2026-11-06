using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>The automation a rule runs in, as the runner sees it.</summary>
internal interface IRuleRunHost
{
    /// <summary>The title of the automation, for logs.</summary>
    string Name { get; }

    IInterceptorSubject Automation { get; }

    string? CurrentState { get; }

    /// <summary>
    /// Enters the state after the checkpoint with it was written; returns why it did not, or null. Entering the current
    /// state does nothing.
    /// </summary>
    string? ChangeState(string targetState);
}
