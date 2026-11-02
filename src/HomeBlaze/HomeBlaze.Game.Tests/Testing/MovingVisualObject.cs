using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// An object whose visual moves another object one tile east every time the game reads it, which lets a test write on
/// the game's thread while the game reads the changed objects.
/// </summary>
[InterceptorSubject]
public partial class MovingVisualObject : GameObject
{
    public GameObject? Moved { get; set; }

    public override GameVisual Visual
    {
        get
        {
            if (Moved is { } moved)
            {
                moved.X++;
            }

            return DefaultVisual;
        }
    }
}
