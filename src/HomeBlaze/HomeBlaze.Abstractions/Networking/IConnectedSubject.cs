using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Networking;

/// <summary>
/// Interface for subjects that maintain a connection to a remote resource.
/// </summary>
[SubjectAbstraction]
[Description("Subject with connection state tracking for remote resources.")]
public interface IConnectedSubject : IConnectionState
{
}
