using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

/// <summary>
/// A node whose <see cref="Name"/> property carries a subject-valued attribute. Used to reach the
/// apply path where processing one subject's deferred attributes queues another subject's.
/// </summary>
[InterceptorSubject]
public partial class AttributeReferenceNode
{
    public partial string? Name { get; set; }

    [PropertyAttribute(nameof(Name), "Ref")]
    public partial AttributeReferenceTarget? Name_Ref { get; set; }

    public partial AttributeReferenceNode? Child { get; set; }
}

/// <summary>
/// The target of <see cref="AttributeReferenceNode.Name_Ref"/>, itself carrying an attribute so that
/// creating it during a deferred attribute pass queues a further deferred entry.
/// </summary>
[InterceptorSubject]
public partial class AttributeReferenceTarget
{
    public AttributeReferenceTarget()
    {
        Label_Unit = "none";
    }

    public partial string? Label { get; set; }

    [PropertyAttribute(nameof(Label), "Unit")]
    public partial string Label_Unit { get; set; }
}
