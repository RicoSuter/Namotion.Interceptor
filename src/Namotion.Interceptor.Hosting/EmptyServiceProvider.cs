namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Resolves nothing. Handed to <see cref="ISubjectHostedServiceFactory.CreateHostedService"/> while no
/// provider exists yet.
/// </summary>
internal sealed class EmptyServiceProvider : IServiceProvider
{
    public static EmptyServiceProvider Instance { get; } = new();

    public object? GetService(Type serviceType) => null;
}
