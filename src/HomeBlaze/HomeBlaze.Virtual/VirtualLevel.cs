using System.Collections.Immutable;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Virtual;

/// <summary>
/// Materializes the entries of one level when its subject attaches, and stops waiting for missing properties on behalf of
/// its computed properties when it detaches. Called by the lifecycle handler of the subject, under the lifecycle lock.
/// </summary>
internal sealed class VirtualLevel(IInterceptorSubject subject, VirtualRuntime runtime, VirtualEntryPlace place)
{
    private PropertyReference[] _computed = [];

    public void Attach(IReadOnlyDictionary<string, VirtualProperty>? entries, string? prefix, ImmutableHashSet<string> rejected)
    {
        var registered = subject.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("A virtual subject needs a context with a registry.");
        var computed = new List<PropertyReference>();
        new VirtualPropertyMaterializer(runtime).Apply(registered, entries, place, prefix, rejected, computed);
        _computed = [.. computed];
    }

    public void Detach()
    {
        foreach (var property in _computed)
        {
            runtime.Awaiter.Forget(property);
        }

        _computed = [];
    }
}
