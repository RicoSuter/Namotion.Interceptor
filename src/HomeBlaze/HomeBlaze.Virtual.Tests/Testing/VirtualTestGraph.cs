using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Virtual.Tests.Testing;

/// <summary>A subject graph with a root of named children, the registry, parents, lifecycle, property attributes and path resolution.</summary>
internal sealed class VirtualTestGraph
{
    public VirtualTestGraph()
    {
        Context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(() => new PropertyAttributeInitializer(), handler => handler is PropertyAttributeInitializer);
        Root = new TestRoot(Context);
        Resolver = new SubjectPathResolver(() => Root);
        Context.AddService(Resolver);
        Awaiter = new VirtualPropertyAwaiter(Context);
    }

    public IInterceptorSubjectContext Context { get; }

    public TestRoot Root { get; }

    public SubjectPathResolver Resolver { get; }

    public VirtualPropertyAwaiter Awaiter { get; }

    public VirtualSubject CreateVirtualSubject(IReadOnlyDictionary<string, VirtualProperty> properties) =>
        new(Resolver, Awaiter, NullLogger<VirtualSubject>.Instance) { Properties = properties };

    /// <summary>Attaches the subject under <c>/name</c>, replacing the subject that had the name.</summary>
    public void Add(string name, IInterceptorSubject subject) =>
        Root.Children = new Dictionary<string, IInterceptorSubject>(Root.Children) { [name] = subject };

    public void Remove(string name)
    {
        var children = new Dictionary<string, IInterceptorSubject>(Root.Children);
        children.Remove(name);
        Root.Children = children;
    }
}
