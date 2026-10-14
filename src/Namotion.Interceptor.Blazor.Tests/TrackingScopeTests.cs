using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;

// A custom renderer is the only way to control in which async flow a batch renders.
#pragma warning disable BL0006

namespace Namotion.Interceptor.Blazor.Tests;

public partial class TrackingScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenPropertyReadByADescendantChanges_ThenTheDescendantRendersTheNewValue(bool renderInOtherFlow)
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithReadPropertyRecorder()
            .WithFullPropertyTracking();

        var person = new Person(context) { FirstName = "Initial" };
        var log = new RenderLog();

        await using var renderer = new TestRenderer(renderInOtherFlow);
        await renderer.RenderAsync<Root>(new Dictionary<string, object?>
        {
            [nameof(Root.Person)] = person,
            [nameof(Root.Log)] = log
        });
        await AsyncTestHelpers.WaitUntilAsync(() => log.LastValue == "Initial");

        // Act
        person.FirstName = "First";
        await AsyncTestHelpers.WaitUntilAsync(() => log.LastValue == "First");
        person.FirstName = "Second";

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => log.LastValue == "Second");
        Assert.Empty(renderer.Exceptions);
    }

    [InterceptorSubject]
    public partial class Person
    {
        public partial string? FirstName { get; set; }
    }

    public sealed class RenderLog
    {
        private volatile string? _lastValue;

        public string? LastValue
        {
            get => _lastValue;
            set => _lastValue = value;
        }
    }

    public sealed class Root : ComponentBase
    {
        [Parameter]
        public Person Person { get; set; } = null!;

        [Parameter]
        public RenderLog Log { get; set; } = null!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<TrackingScope>(0);
            builder.AddComponentParameter(1, nameof(TrackingScope.Context), ((IInterceptorSubject)Person).Context);
            builder.AddComponentParameter(2, nameof(TrackingScope.ChildContent), (RenderFragment)(content =>
            {
                content.OpenComponent<Reader>(0);
                content.AddComponentParameter(1, nameof(Reader.Person), Person);
                content.AddComponentParameter(2, nameof(Reader.Log), Log);
                content.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    public sealed class Reader : ComponentBase
    {
        [Parameter]
        public Person Person { get; set; } = null!;

        [Parameter]
        public RenderLog Log { get; set; } = null!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            var value = Person.FirstName;
            builder.AddContent(0, value);
            Log.LastValue = value;
        }
    }

    /// <summary>
    /// With <c>renderInOtherFlow</c>, a render request only queues the render, as on a server whose client has not
    /// acknowledged enough batches, and the queue is rendered later in an async flow that shares nothing with the request.
    /// </summary>
    private sealed class TestRenderer : Renderer
    {
        private readonly bool _renderInOtherFlow;
        private bool _isRenderingQueue;

        public TestRenderer(bool renderInOtherFlow)
            : base(CreateServices(), NullLoggerFactory.Instance)
        {
            _renderInOtherFlow = renderInOtherFlow;
        }

        public List<Exception> Exceptions { get; } = [];

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public Task RenderAsync<TComponent>(Dictionary<string, object?> parameters)
            where TComponent : IComponent, new()
        {
            return Dispatcher.InvokeAsync(() =>
            {
                var componentId = AssignRootComponentId(new TComponent());
                _ = RenderRootComponentAsync(componentId, ParameterView.FromDictionary(parameters));
            });
        }

        protected override void ProcessPendingRender()
        {
            if (!_renderInOtherFlow || _isRenderingQueue)
            {
                base.ProcessPendingRender();
                return;
            }

            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(() => Dispatcher.InvokeAsync(RenderQueue));
            }
        }

        // Completes after the batch, as the acknowledgement of a client does, so that OnAfterRender runs in a continuation.
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.Run(static () => { });

        protected override void HandleException(Exception exception)
        {
            lock (Exceptions)
            {
                Exceptions.Add(exception);
            }
        }

        private void RenderQueue()
        {
            _isRenderingQueue = true;
            try
            {
                base.ProcessPendingRender();
            }
            finally
            {
                _isRenderingQueue = false;
            }
        }

        private static ServiceProvider CreateServices()
        {
            return new ServiceCollection()
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .BuildServiceProvider();
        }
    }
}
