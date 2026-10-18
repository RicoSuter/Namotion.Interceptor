using Xunit;

namespace Namotion.Devices.Pushover.Tests.Testing;

/// <summary>
/// The test classes that hold requests open with <see cref="FakePushoverHandler.BeforeRespond"/>. They share the
/// process-wide connection limit of the Pushover client, so they run one after another.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PushoverConnectionsCollection
{
    public const string Name = "Pushover connections";

    private PushoverConnectionsCollection()
    {
    }
}
