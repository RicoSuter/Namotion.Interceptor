namespace HomeBlaze.Game.Components;

/// <summary>
/// Provides the page's <see cref="GameCaller"/> to the operations it invokes and every other service from the circuit.
/// </summary>
internal sealed class GameCallerServiceProvider(IServiceProvider inner, GameCaller caller) : IServiceProvider
{
    public object? GetService(Type serviceType) =>
        serviceType == typeof(GameCaller) ? caller : inner.GetService(serviceType);
}
