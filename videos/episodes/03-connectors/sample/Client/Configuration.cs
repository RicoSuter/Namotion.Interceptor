using Coffee;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.WebSocket;

namespace Connectors.Client;

/// <summary>
/// Variations shown in the video. They compile with the sample but are never called.
/// </summary>
public static class Configuration
{
    public static void AddMachineSourceWithRetries(IServiceCollection services)
    {
        #region RetryQueue
        services.AddWebSocketSubjectClientSource<CoffeeMachine>(configuration =>
        {
            configuration.ServerUri = new Uri("ws://localhost:5310/ws");
            configuration.BufferTime = TimeSpan.FromMilliseconds(8);
            configuration.WriteRetryQueueSize = 1000;
            configuration.RetryTime = TimeSpan.FromSeconds(10);
        });
        #endregion
    }

    public static async Task SetTargetTemperatureConfirmedAsync(CancellationToken cancellationToken)
    {
        #region ConfirmedWrite
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithTransactions()
            .WithSourceTransactions();
        var machine = new CoffeeMachine(context);

        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback);

        machine.Boiler.TargetTemperature = 95; // captured, not applied yet
        await transaction.CommitAsync(cancellationToken); // source first, then local
        #endregion
    }
}
