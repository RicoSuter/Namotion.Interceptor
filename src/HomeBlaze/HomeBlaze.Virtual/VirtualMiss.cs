using Namotion.Interceptor;

namespace HomeBlaze.Virtual;

/// <summary>A path that led to <see cref="Subject"/>, which has no property <see cref="Name"/> yet.</summary>
internal readonly record struct VirtualMiss(IInterceptorSubject Subject, string Name);
