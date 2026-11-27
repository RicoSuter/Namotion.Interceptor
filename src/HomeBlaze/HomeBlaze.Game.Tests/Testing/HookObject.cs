using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// An object that counts its calls of <see cref="OnAddedToWorld"/> and records whether its world resolved in them.
/// </summary>
[InterceptorSubject]
public partial class HookObject : GameObject
{
    public int AddedCount { get; private set; }

    public bool HadWorldInHook { get; private set; }

    protected override void OnAddedToWorld()
    {
        AddedCount++;
        HadWorldInHook = TryGetWorld(out _);
    }
}
