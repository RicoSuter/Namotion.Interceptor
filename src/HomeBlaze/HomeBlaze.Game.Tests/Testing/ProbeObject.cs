using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// An object that runs a callback the first time the game reads its visual, which lets a test act in the middle of the
/// tick that takes the object in.
/// </summary>
[InterceptorSubject]
public partial class ProbeObject : GameObject
{
    public Action? OnFirstRead { get; set; }

    public override GameVisual Visual
    {
        get
        {
            var onFirstRead = OnFirstRead;
            OnFirstRead = null;
            onFirstRead?.Invoke();
            return DefaultVisual;
        }
    }
}
