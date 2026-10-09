using Coffee;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;

/// <summary>
/// Variations shown in the video. They compile with the sample but are never called.
/// </summary>
public static class Steps
{
    public static CoffeeMachine CreateMachine()
    {
        #region Context
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithDataAnnotationValidation();

        var machine = new CoffeeMachine(context);
        #endregion

        return machine;
    }

    public static void DrainChanges(IInterceptorSubjectContext context, CancellationToken cancellationToken)
    {
        #region Queue
        using var subscription = context.CreatePropertyChangeQueueSubscription();

        while (subscription.TryDequeue(out var change, cancellationToken))
        {
            Console.WriteLine($"{change.Property.Name} = {change.GetNewValue<object?>()}");
        }
        #endregion
    }
}
