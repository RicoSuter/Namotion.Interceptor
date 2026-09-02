using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class WriteInterceptorFactory<TProperty>
{
    public static WriteAction<TProperty> Create(ImmutableArray<IWriteInterceptor> interceptors)
    {
        if (interceptors.Length == 0)
        {
            return ExecuteTerminal;
        }

        var chain = new WriteInterceptorChain<TProperty>(interceptors, ExecuteTerminal);
        return chain.Execute;
    }

    private static void ExecuteTerminal(
        ref PropertyWriteContext<TProperty> context,
        Action<IInterceptorSubject, TProperty> writeValue)
    {
        var terminalValue = context.FreezeNewValue();
        context.PrepareTerminalState();
        if (context.TerminalCoordinator is { } coordinator)
        {
            coordinator.ExecuteTerminal(ref context, context.ReadValue, writeValue);
            return;
        }

        lock (context.Executor.SyncRoot)
        {
            if (context.ReadValue is { } readValue)
            {
                context.SetTerminalPredecessor(readValue(context.Property.Subject));
            }

            context.Executor.CommitRawWriteLocked(ref context, terminalValue, writeValue);
        }
    }
}
