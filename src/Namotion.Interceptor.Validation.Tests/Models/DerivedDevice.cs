using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Validation.Tests.Models;

/// <summary>
/// A device with a getter-only derived property, whose value "b" violates its data annotation, and a settable derived
/// property that another derived property reads.
/// </summary>
[InterceptorSubject]
public partial class DerivedDevice
{
    public partial int Input { get; set; }

    [Derived]
    [RegularExpression("a")]
    public string Computed => Input == 2 ? "b" : "a";

    [Derived]
    public partial string? Label { get; set; }

    [Derived]
    public string LabelWithPrefix => $"Label: {Label}";
}
