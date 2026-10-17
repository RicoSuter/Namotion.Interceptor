using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Namotion.Interceptor.Blazor.ComponentTracking;
using Namotion.Interceptor.Tracking;

// The benchmarks drive a custom renderer, which is the only way to render components without a host.
#pragma warning disable BL0006
#pragma warning disable CS8618

namespace Namotion.Interceptor.Benchmark;

/// <summary>
/// Measures the renders that component tracking changes beyond a plain re-render: content that a component which is not
/// a ComponentBase renders for its creator, children that skip a render of their parent with unchanged parameters, and
/// children that are replaced on every render, so that each render subscribes and releases properties.
/// </summary>
[MemoryDiagnoser]
public class ComponentTrackingScenarioBenchmark
{
    private const int ComponentCount = 100;

    private BenchmarkRenderer _renderer;
    private Action _renderAgain;

    [Params(false, true)]
    public bool Tracking { get; set; }

    [GlobalSetup(Target = nameof(RenderContentOfCascadingValue))]
    public void SetupContentOfCascadingValue() => Setup<CascadedContentView>();

    [GlobalSetup(Target = nameof(RenderParentOfSkippingChildren))]
    public void SetupParentOfSkippingChildren() => Setup<SkippingChildrenView>();

    [GlobalSetup(Target = nameof(RenderReplacedChildren))]
    public void SetupReplacedChildren() => Setup<ReplacedChildrenView>();

    /// <summary>A parent whose content in a cascading value reads three properties of each of 100 cars.</summary>
    [Benchmark]
    public void RenderContentOfCascadingValue() => Render();

    /// <summary>A parent of 100 children whose parameters do not change; with tracking, the children skip the render.</summary>
    [Benchmark]
    public void RenderParentOfSkippingChildren() => Render();

    /// <summary>A parent whose 100 children are new components reading other cars on every render.</summary>
    [Benchmark]
    public void RenderReplacedChildren() => Render();

    [GlobalCleanup]
    public void Cleanup()
    {
        _renderer.VerifyAndDispose();
    }

    private void Render() => _renderer.Dispatcher.InvokeAsync(_renderAgain).GetAwaiter().GetResult();

    private void Setup<TView>()
        where TView : CarsView
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithReadPropertyRecorder();
        var cars = Enumerable.Range(0, ComponentCount * 2).Select(_ => new Car(context)).ToArray();
        _renderer = new BenchmarkRenderer(Tracking ? services => services.AddComponentTracking() : null);
        var view = _renderer.RenderRoot<TView>(new Dictionary<string, object?> { [nameof(CarsView.Cars)] = cars });
        _renderAgain = view.RenderAgain;
    }
}

/// <summary>
/// Measures one change that one of 100 components shows, until that component has rendered again. With tracking the
/// write alone renders it; without, the writer asks the component to render, as an application that subscribes itself does.
/// </summary>
[MemoryDiagnoser]
public class ComponentTrackingChangeBenchmark
{
    private const int ComponentCount = 100;

    private BenchmarkRenderer _renderer;
    private Car _car;
    private CarView _view;
    private bool _toggle;

    [Params(false, true)]
    public bool Tracking { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithReadPropertyRecorder();
        var cars = Enumerable.Range(0, ComponentCount).Select(_ => new Car(context)).ToArray();
        _car = cars[ComponentCount / 2];
        _renderer = new BenchmarkRenderer(Tracking
            ? services => services.AddComponentTracking(options => options.ThrottleMilliseconds = 0)
            : null);
        var list = _renderer.RenderRoot<ChangeTargetsView>(new Dictionary<string, object?> { [nameof(CarsView.Cars)] = cars });
        _view = list.Views[ComponentCount / 2];
    }

    [Benchmark]
    public void ChangeAndRenderOneOfManyComponents()
    {
        var renders = _view.RenderCount;
        _toggle = !_toggle;
        _car.Name = _toggle ? "a" : "b";
        if (!Tracking)
        {
            _renderer.Dispatcher.InvokeAsync(_view.RenderAgain).GetAwaiter().GetResult();
        }

        var spinner = default(SpinWait);
        while (_view.RenderCount == renders)
        {
            spinner.SpinOnce();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _renderer.VerifyAndDispose();
    }
}

public abstract class CarsView : ComponentBase
{
    [Parameter]
    public Car[] Cars { get; set; }

    public void RenderAgain() => StateHasChanged();
}

public sealed class CascadedContentView : CarsView
{
    private RenderFragment? _content;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        _content ??= content =>
        {
            for (var i = 0; i < 100; i++)
            {
                content.AddContent(0, Cars[i].Name);
                content.AddContent(1, Cars[i].Name_MaxLength);
                content.AddContent(2, Cars[i].Name_MaxLength_Unit);
            }
        };

        builder.OpenComponent<CascadingValue<int>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<int>.Value), 1);
        builder.AddComponentParameter(2, nameof(CascadingValue<int>.ChildContent), _content);
        builder.CloseComponent();
    }
}

public sealed class SkippingChildrenView : CarsView
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        for (var i = 0; i < 100; i++)
        {
            builder.OpenComponent<SkippingCarView>(0);
            builder.SetKey(i);
            builder.AddComponentParameter(1, nameof(SkippingCarView.Car), Cars[i]);
            builder.CloseComponent();
        }
    }
}

public sealed class SkippingCarView : TrackedComponentBase
{
    [Parameter]
    public Car Car { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.AddContent(0, Car.Name);
        builder.AddContent(1, Car.Name_MaxLength);
        builder.AddContent(2, Car.Name_MaxLength_Unit);
    }
}

public sealed class ReplacedChildrenView : CarsView
{
    private int _generation;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        // A new key and another car on every render, so that every child is disposed and a new one created.
        _generation++;
        var offset = (_generation % 2) * 100;
        for (var i = 0; i < 100; i++)
        {
            builder.OpenComponent<CarView>(0);
            builder.SetKey((_generation, i));
            builder.AddComponentParameter(1, nameof(CarView.Car), Cars[offset + i]);
            builder.CloseComponent();
        }
    }
}

public sealed class ChangeTargetsView : CarsView
{
    public List<CarView> Views { get; } = [];

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        for (var i = 0; i < Cars.Length; i++)
        {
            var index = i;
            builder.OpenComponent<CarView>(0);
            builder.SetKey(i);
            builder.AddComponentParameter(1, nameof(CarView.Car), Cars[i]);
            builder.AddComponentReferenceCapture(2, component =>
            {
                if (Views.Count <= index)
                {
                    Views.Add((CarView)component);
                }
            });
            builder.CloseComponent();
        }
    }
}
