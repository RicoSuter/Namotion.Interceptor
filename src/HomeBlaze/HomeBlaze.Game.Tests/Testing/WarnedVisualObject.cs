using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// Like <see cref="VisualObject"/>, but a type of its own, because the game warns about an invalid visual once per type
/// and process.
/// </summary>
[InterceptorSubject]
public partial class WarnedVisualObject : GameObject
{
    public GameVisual CustomVisual { get; set; } = GameVisual.Box(1, 1, 1);

    public override GameVisual Visual => CustomVisual;
}
