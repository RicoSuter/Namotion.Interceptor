using HomeBlaze.Automation.Expressions;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>Where an action runs: its automation, its rule, the evaluation of its arguments and whether an event is captured.</summary>
internal sealed record ActionScope(IInterceptorSubject Automation, AutomationRule Rule, AutomationEvaluation Evaluation, bool AllowsEvent);
