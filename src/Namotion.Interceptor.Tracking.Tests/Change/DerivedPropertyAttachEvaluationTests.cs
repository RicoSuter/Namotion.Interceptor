using System.Reactive.Concurrency;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyAttachEvaluationTests
{
    [Fact]
    public async Task WhenObserverWritesGraphWhileAttachPublishesChangedDerivedValue_ThenBothWritesComplete()
    {
        // Arrange: the worker attaches before the configuration and reads its value. A handler writes that value
        // while the configuration attaches, which requests the worker's second evaluation. The second evaluation
        // waits until an observer of another thread holds the dispatch lock of the observable and writes the graph,
        // which takes the lifecycle lock; the evaluation then publishes, which takes the dispatch lock.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var source = new EvaluatorConfiguration(context);
        var machine = new EvaluatorMachine();
        var secondEvaluationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerWritesGraph = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evaluationCount = 0;
        machine.Worker.Evaluator = worker =>
        {
            var result = worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration.Value;
            if (++evaluationCount == 2)
            {
                secondEvaluationReached.SetResult();
                observerWritesGraph.Task.Wait(TestTimeouts.Default);
            }

            return result;
        };
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == machine.Configuration)
            {
                machine.Configuration.Value = 1;
            }
        }));

        var publishedResults = new List<int>();
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (ReferenceEquals(change.Property.Subject, source))
            {
                observerWritesGraph.SetResult();
                root.Spare = new EvaluatorConfiguration();
            }
            else if (ReferenceEquals(change.Property.Subject, machine.Worker))
            {
                publishedResults.Add(change.GetNewValue<int>());
            }
        });

        // Act
        var attach = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { root.Machine = machine; });
        await secondEvaluationReached.Task.WaitAsync(TestTimeouts.Default);
        var valueWrite = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { source.Value = 1; });
        await Task.WhenAll(valueWrite, attach).WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal([1], publishedResults);
        Assert.NotNull(root.Spare);
    }
}
