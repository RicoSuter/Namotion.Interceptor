using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

/// <summary>
/// A comprehensive model for testing cycle detection with all subject property types:
/// - Value properties (string)
/// - Subject references
/// - Subject collections
/// - Subject dictionaries
/// - Attributes on properties
/// </summary>
[InterceptorSubject]
public partial class CycleTestNode
{
    public CycleTestNode()
    {
        Items = new List<CycleTestNode>();
        Lookup = new Dictionary<string, CycleTestNode>();
        Name_Status = "active";
    }

    /// <summary>
    /// Simple value property.
    /// </summary>
    public partial string? Name { get; set; }

    /// <summary>
    /// Attribute on Name property for testing attribute changes.
    /// </summary>
    [PropertyAttribute(nameof(Name), "Status")]
    public partial string Name_Status { get; set; }

    /// <summary>
    /// Subject reference - can create direct cycles (e.g., Self = this).
    /// </summary>
    public partial CycleTestNode? Self { get; set; }

    /// <summary>
    /// Subject reference - can create parent/child cycles.
    /// </summary>
    public partial CycleTestNode? Parent { get; set; }

    /// <summary>
    /// Subject reference - another ref for testing multiple ref paths.
    /// </summary>
    public partial CycleTestNode? Child { get; set; }

    /// <summary>
    /// Subject collection - items can reference back to ancestors.
    /// </summary>
    public partial List<CycleTestNode> Items { get; set; }

    /// <summary>
    /// Subject dictionary - values can reference back to ancestors.
    /// </summary>
    public partial Dictionary<string, CycleTestNode> Lookup { get; set; }
}
