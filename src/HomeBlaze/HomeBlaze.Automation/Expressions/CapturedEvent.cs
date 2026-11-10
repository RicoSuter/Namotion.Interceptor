using HomeBlaze.Abstractions.Messaging;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Expressions;

/// <summary>An event as it was published: its payload and the subject that published it.</summary>
internal sealed record CapturedEvent(IInterceptorSubject Source, IEvent Payload);
