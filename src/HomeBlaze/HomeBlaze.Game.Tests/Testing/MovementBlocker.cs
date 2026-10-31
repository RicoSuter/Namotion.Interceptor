using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// An object like a plant or a chair, which blocks movement but lets sound through.
/// </summary>
[InterceptorSubject]
public partial class MovementBlocker : GameObject;
