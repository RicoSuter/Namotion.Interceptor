using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Blazor.ComponentTracking;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Recorder;

// A custom renderer is the only way to render components without a host.
#pragma warning disable BL0006
#pragma warning disable CS8618

namespace Namotion.Interceptor.Benchmark;

/// <summary>
/// Measures an intercepted read while a thread target of <see cref="ReadPropertyRecorder"/> is set, against the
/// same read with the recorder idle.
/// </summary>
[MemoryDiagnoser]
public class ReadPropertyRecorderThreadTargetBenchmark
{
    private const int Reads = 256;

    private Car _car;
    private readonly NoOpTarget _noOpTarget = new();
    private readonly TrackerLikeTarget _trackerLikeTarget = new();

    [GlobalSetup]
    public void Setup()
    {
        _car = new Car(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithReadPropertyRecorder());
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Reads)]
    public int ReadWithRecorderIdle() => ReadName();

    [Benchmark(OperationsPerInvoke = Reads)]
    public int ReadWithNoOpThreadTarget()
    {
        using (ReadPropertyRecorder.RecordOnCurrentThread(_noOpTarget))
        {
            return ReadName();
        }
    }

    // One dictionary lookup per read that marks the render number, as a component's read tracker does.
    [Benchmark(OperationsPerInvoke = Reads)]
    public int ReadWithTrackerLikeThreadTarget()
    {
        using (ReadPropertyRecorder.RecordOnCurrentThread(_trackerLikeTarget))
        {
            return ReadName();
        }
    }

    private int ReadName()
    {
        var car = _car;
        var sum = 0;
        for (var i = 0; i < Reads; i++)
        {
            sum += car.Name.Length;
        }

        return sum;
    }

    private sealed class NoOpTarget : IReadPropertyRecorderTarget
    {
        public void OnRead(PropertyReference property)
        {
        }
    }

    private sealed class TrackerLikeTarget : IReadPropertyRecorderTarget
    {
        private readonly Dictionary<PropertyReference, int> _acquired = [];
        private const int RenderNumber = 1;

        public void OnRead(PropertyReference property)
        {
            ref var render = ref CollectionsMarshal.GetValueRefOrAddDefault(_acquired, property, out _);
            render = RenderNumber;
        }
    }
}

/// <summary>
/// Measures a render of a parent with <see cref="ComponentCount"/> children that each read three properties, with and
/// without component tracking. The children render again with their parent because their parameter is a mutable object.
/// </summary>
[MemoryDiagnoser]
public class ComponentTrackingRenderBenchmark
{
    private BenchmarkRenderer _renderer;
    private CarListView _root;

    [Params(100)]
    public int ComponentCount { get; set; }

    [Params(false, true)]
    public bool Tracking { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var car = new Car(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithReadPropertyRecorder());
        _renderer = new BenchmarkRenderer(Tracking ? services => services.AddComponentTracking() : null);
        _root = _renderer.RenderRoot<CarListView>(new Dictionary<string, object?>
        {
            [nameof(CarListView.Car)] = car,
            [nameof(CarListView.Count)] = ComponentCount
        });
    }

    [Benchmark]
    public void RenderParentAndChildren()
    {
        _renderer.Dispatcher.InvokeAsync(_root.RenderAgain).GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _renderer.VerifyAndDispose();
    }
}

/// <summary>
/// Measures a property write on the writer thread while <see cref="CircuitCount"/> circuits each track the property
/// with one component. The renders that the writes cause are posted to the thread pool and are not part of a write.
/// </summary>
[MemoryDiagnoser]
public class ComponentTrackingWriteBenchmark
{
    private readonly List<BenchmarkRenderer> _renderers = [];
    private readonly List<CarView> _views = [];
    private Car _car;
    private bool _toggle;

    [Params(0, 1, 10)]
    public int CircuitCount { get; set; }

    [Params(0, 33)]
    public int ThrottleMilliseconds { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _car = new Car(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithReadPropertyRecorder());
        for (var i = 0; i < CircuitCount; i++)
        {
            var renderer = new BenchmarkRenderer(services => services.AddComponentTracking(options => options.ThrottleMilliseconds = ThrottleMilliseconds));
            _views.Add(renderer.RenderRoot<CarView>(new Dictionary<string, object?> { [nameof(CarView.Car)] = _car }));
            _renderers.Add(renderer);
        }
    }

    [Benchmark]
    public void Write()
    {
        _toggle = !_toggle;
        _car.Name = _toggle ? "a" : "b";
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var view in _views)
        {
            // The renders run on the thread pool after the writes, so the last of them may still be on its way.
            if (!SpinWait.SpinUntil(() => view.RenderCount >= 2, TimeSpan.FromSeconds(10)))
            {
                throw new InvalidOperationException("A write did not render the tracking component again.");
            }
        }

        foreach (var renderer in _renderers)
        {
            renderer.VerifyAndDispose();
        }
    }
}

public sealed class CarListView : ComponentBase
{
    [Parameter]
    public Car Car { get; set; }

    [Parameter]
    public int Count { get; set; }

    public void RenderAgain() => StateHasChanged();

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        for (var i = 0; i < Count; i++)
        {
            builder.OpenComponent<CarView>(0);
            builder.SetKey(i);
            builder.AddComponentParameter(1, nameof(CarView.Car), Car);
            builder.CloseComponent();
        }
    }
}

public sealed class CarView : ComponentBase
{
    [Parameter]
    public Car Car { get; set; }

    private int _renderCount;

    // Read by the cleanup on another thread than the renders.
    public int RenderCount => Volatile.Read(ref _renderCount);

    public void RenderAgain() => StateHasChanged();

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        Volatile.Write(ref _renderCount, _renderCount + 1);
        builder.AddContent(0, Car.Name);
        builder.AddContent(1, Car.Name_MaxLength);
        builder.AddContent(2, Car.Name_MaxLength_Unit);
    }
}

/// <summary>An interactive renderer whose batches complete synchronously, as on WebAssembly.</summary>
internal sealed class BenchmarkRenderer : Renderer
{
    private int _exceptionCount;

    public BenchmarkRenderer(Action<IServiceCollection>? configureServices)
        : base(CreateServices(configureServices), NullLoggerFactory.Instance)
    {
    }

    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    protected override RendererInfo RendererInfo { get; } = new("Benchmark", isInteractive: true);

    /// <summary>Throws if the renderer handled an exception, which makes the measurement invalid, and otherwise disposes it.</summary>
    public void VerifyAndDispose()
    {
        if (_exceptionCount != 0)
        {
            throw new InvalidOperationException("The renderer handled an exception.");
        }

        Dispose();
    }

    public TComponent RenderRoot<TComponent>(Dictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        TComponent component = default!;
        Dispatcher.InvokeAsync(() =>
        {
            component = (TComponent)InstantiateComponent(typeof(TComponent));
            var componentId = AssignRootComponentId(component);
            return RenderRootComponentAsync(componentId, ParameterView.FromDictionary(parameters));
        }).GetAwaiter().GetResult();
        return component;
    }

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

    protected override void HandleException(Exception exception) => _exceptionCount++;

    private static ServiceProvider CreateServices(Action<IServiceCollection>? configureServices)
    {
        var services = new ServiceCollection().AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        configureServices?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
