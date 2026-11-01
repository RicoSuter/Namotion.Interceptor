using HomeBlaze.Game.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Transactions;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// Reports writes to games and their objects to the game's simulation; one instance serves every game of a context.
/// Never waits for a simulation, so a write from a change observer cannot deadlock with the simulation's own writes.
/// </summary>
/// <remarks>
/// Runs before the transaction interceptor, so a refused write fails where it is made, not when a transaction commits.
/// </remarks>
[RunsBefore(typeof(SubjectTransactionInterceptor))]
internal sealed class GameWriteInterceptor : IWriteInterceptor
{
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var subject = context.Property.Subject;
        if (subject is not (GameObject or Game))
        {
            next(ref context);
            return;
        }

        var game = subject as Game ?? subject.TryGetFirstParent<Game>();
        if (game is null)
        {
            next(ref context);
            return;
        }

        var name = context.Property.Name;
        var isOnSimulation = game.IsOnSimulationThread;
        if (!isOnSimulation && subject is GamePlayer && IsRefused(name))
        {
            throw new InvalidOperationException($"Only the game sets {name}.");
        }

        next(ref context);
        if (context.IsWritten)
        {
            game.OnWritten(subject, name, context.NewValue, isOnSimulation);
        }
    }

    // The states the simulation owns; the game's own hook on Players assigns the client ids on any thread.
    private static bool IsRefused(string name)
    {
        switch (name)
        {
            case nameof(GamePlayer.ClientId):
                return !Game.IsAssigningClientIds;
            case nameof(GamePlayer.ClientTitle):
            case nameof(GamePlayer.IsConnected):
            case nameof(GamePlayer.IsMicrophoneOn):
            case nameof(GamePlayer.IsAudioOn):
                return true;
            default:
                return false;
        }
    }
}
