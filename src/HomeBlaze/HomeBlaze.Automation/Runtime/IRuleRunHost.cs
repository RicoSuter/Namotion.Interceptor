using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>The automation a rule runs in, as the runner sees it.</summary>
internal interface IRuleRunHost
{
    /// <summary>The title of the automation, for logs.</summary>
    string Name { get; }

    IInterceptorSubject Automation { get; }

    string? CurrentState { get; }

    /// <summary>The roles of the applied configuration, which decide what the actions may call and write.</summary>
    IReadOnlyList<string> Roles { get; }

    /// <summary>Runs the write unless a stop abandoned the worker, which then writes nothing anymore; returns whether it ran.</summary>
    bool RunUnlessAbandoned(Action write);

    /// <summary>
    /// Enters the state after the checkpoint with it was written; returns why it did not, or null. Entering the current
    /// state does nothing.
    /// </summary>
    string? ChangeState(string targetState);
}
