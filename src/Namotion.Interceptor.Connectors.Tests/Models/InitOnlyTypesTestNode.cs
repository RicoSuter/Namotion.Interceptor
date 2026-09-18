using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

/// <summary>
/// Model whose value property, subject reference, subject collection and subject dictionary are
/// init-only. The generator emits no setter lambda for an init accessor, so
/// <see cref="Registry.Abstractions.RegisteredSubjectProperty.HasSetter"/> is false for these
/// properties while their getters and their subtrees stay fully readable.
/// </summary>
[InterceptorSubject]
public partial class InitOnlyTypesTestNode
{
    public InitOnlyTypesTestNode()
    {
        Items = [];
        Lookup = [];
    }

    public partial string? Name { get; set; }

    public partial string? Label { get; init; }

    public partial InitOnlyTypesTestNode? Child { get; init; }

    public partial List<InitOnlyTypesTestNode> Items { get; init; }

    public partial Dictionary<string, InitOnlyTypesTestNode> Lookup { get; init; }
}
