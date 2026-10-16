using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// A custom renderer is the only way to control when a batch completes and in which async flow a batch renders.
#pragma warning disable BL0006

namespace Namotion.Interceptor.Blazor.Tests;

public enum TestRenderMode
{
    /// <summary>A render request renders at once and the batch completes synchronously, as on WebAssembly.</summary>
    WebAssembly,

    /// <summary>
    /// A batch completes when the client acknowledges it, as on a server. While a batch is unacknowledged, a render
    /// request is only queued, and the queue renders when the acknowledgement arrives, in the async flow of the
    /// acknowledgement, which shares nothing with the flow that asked for the render.
    /// </summary>
    Server,

    /// <summary>A batch never completes, so <c>OnAfterRender</c> never runs, as when a page is prerendered.</summary>
    Prerender
}

/// <summary>
/// Renders components as the given <see cref="TestRenderMode"/> does and counts the render requests it defers.
/// </summary>
internal sealed class TestRenderer : Renderer
{
    private readonly TestRenderMode _mode;

    // Accessed on the dispatcher only.
    private readonly Queue<TaskCompletionSource> _unacknowledgedBatches = new();
    private bool _hasDeferredRender;

    private volatile bool _holdAcknowledgements;
    private int _unacknowledgedBatchCount;
    private int _deferredRenderRequests;

    public TestRenderer(TestRenderMode mode)
        : base(CreateServices(), NullLoggerFactory.Instance)
    {
        _mode = mode;
    }

    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    public List<Exception> Exceptions { get; } = [];

    /// <summary>Gets the number of render requests that waited for an acknowledgement.</summary>
    public int DeferredRenderRequests => Volatile.Read(ref _deferredRenderRequests);

    /// <summary>Gets the number of batches the client has not acknowledged yet.</summary>
    public int UnacknowledgedBatchCount => Volatile.Read(ref _unacknowledgedBatchCount);

    public async Task<TComponent> RenderAsync<TComponent>(Dictionary<string, object?> parameters)
        where TComponent : IComponent, new()
    {
        var component = new TComponent();
        await Dispatcher.InvokeAsync(() =>
        {
            var componentId = AssignRootComponentId(component);
            _ = RenderRootComponentAsync(componentId, ParameterView.FromDictionary(parameters));
        });

        return component;
    }

    /// <summary>Disposes the renderer and with it every component, as when the circuit or the prerendering ends.</summary>
    public ValueTask EndAsync() => DisposeAsync();

    /// <summary>Stops acknowledging the batches that render from now on, until <see cref="ReleaseAcknowledgements"/>.</summary>
    public void HoldAcknowledgements() => _holdAcknowledgements = true;

    /// <summary>Acknowledges the held batches, in another async flow, and every later batch as it renders.</summary>
    public void ReleaseAcknowledgements()
    {
        _holdAcknowledgements = false;
        AcknowledgeInOtherFlow(null);
    }

    protected override void ProcessPendingRender()
    {
        if (_mode == TestRenderMode.Server && _unacknowledgedBatches.Count > 0)
        {
            _hasDeferredRender = true;
            Interlocked.Increment(ref _deferredRenderRequests);
            return;
        }

        base.ProcessPendingRender();
    }

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
    {
        switch (_mode)
        {
            case TestRenderMode.WebAssembly:
                return Task.CompletedTask;

            case TestRenderMode.Prerender:
                // A canceled display update is how a prerendering renderer tells that the batch never completes.
                return Task.FromCanceled(new CancellationToken(canceled: true));

            default:
                var acknowledgement = new TaskCompletionSource();
                _unacknowledgedBatches.Enqueue(acknowledgement);
                Interlocked.Increment(ref _unacknowledgedBatchCount);
                if (!_holdAcknowledgements)
                {
                    AcknowledgeInOtherFlow(acknowledgement);
                }

                return acknowledgement.Task;
        }
    }

    protected override void HandleException(Exception exception)
    {
        lock (Exceptions)
        {
            Exceptions.Add(exception);
        }
    }

    private void AcknowledgeInOtherFlow(TaskCompletionSource? lastBatch)
    {
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(() => Dispatcher.InvokeAsync(() => Acknowledge(lastBatch)));
        }
    }

    // Acknowledges the batches in order up to the given one, or all of them, and then renders what waited for them.
    private void Acknowledge(TaskCompletionSource? lastBatch)
    {
        if (lastBatch?.Task.IsCompleted == true)
        {
            return;
        }

        // Completing a batch runs its OnAfterRender, which can render a new batch that must wait for its own acknowledgement.
        var batchCount = _unacknowledgedBatches.Count;
        for (var index = 0; index < batchCount; index++)
        {
            var batch = _unacknowledgedBatches.Dequeue();
            Interlocked.Decrement(ref _unacknowledgedBatchCount);
            batch.TrySetResult();
            if (batch == lastBatch)
            {
                break;
            }
        }

        if (_hasDeferredRender && _unacknowledgedBatches.Count == 0)
        {
            _hasDeferredRender = false;
            base.ProcessPendingRender();
        }
    }

    private static ServiceProvider CreateServices()
    {
        return new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
    }
}
