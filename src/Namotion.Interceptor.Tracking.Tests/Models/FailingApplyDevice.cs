using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// Test model whose change hooks throw for the properties listed in <see cref="FailingProperties"/> once
/// <see cref="ThrowingEnabled"/> is set. Used to exercise the local apply failure path of a commit.
/// </summary>
[InterceptorSubject]
public partial class FailingApplyDevice
{
    /// <summary>Gets or sets whether the hooks throw. Set it after the changes are captured, just before the commit.</summary>
    public bool ThrowingEnabled { get; set; }

    /// <summary>Gets or sets the names of the properties whose hook throws.</summary>
    public HashSet<string> FailingProperties { get; } = [];

    public partial string? PropertyA { get; set; }

    public partial string? PropertyB { get; set; }

    partial void OnPropertyAChanging(ref string? newValue, ref bool cancel)
    {
        ThrowIfFailing(nameof(PropertyA));
    }

    partial void OnPropertyBChanging(ref string? newValue, ref bool cancel)
    {
        ThrowIfFailing(nameof(PropertyB));
    }

    private void ThrowIfFailing(string propertyName)
    {
        if (ThrowingEnabled && FailingProperties.Contains(propertyName))
        {
            throw new InvalidOperationException($"{propertyName} failed");
        }
    }
}
