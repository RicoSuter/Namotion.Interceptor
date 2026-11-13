using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// An object whose visual a test sets directly.
/// </summary>
[InterceptorSubject]
public partial class VisualObject : GameObject
{
    public GameVisual CustomVisual { get; set; } = GameVisual.Box(1, 1, 1);

    public override GameVisual Visual => CustomVisual;
}
