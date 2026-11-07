using HomeBlaze.Automation.Expressions;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Where an action runs: its automation, its rule, the evaluation of its arguments, whether an event is captured, and the
/// roles that decide what it may call and write.
/// </summary>
internal sealed record ActionScope(
    IInterceptorSubject Automation, AutomationRule Rule, AutomationEvaluation Evaluation, bool AllowsEvent, IReadOnlyList<string> Roles);
