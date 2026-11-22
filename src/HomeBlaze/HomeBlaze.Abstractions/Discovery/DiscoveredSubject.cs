using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// A found device, with the subject type that represents it.
/// </summary>
/// <param name="SubjectType">The type of subject to create for the device.</param>
/// <param name="Result">The device, as its discoverer describes it.</param>
/// <param name="ExistingSubject">The subject that already represents the device by the rules of its discoverer, or null.</param>
public sealed record DiscoveredSubject(Type SubjectType, IDiscoveryResult Result, IInterceptorSubject? ExistingSubject);
