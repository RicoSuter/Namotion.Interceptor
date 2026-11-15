using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Hosting;

/// <summary>
/// Finds the running total of writes a participant's source dropped, which the write-durability check reads to tell
/// a write the source's full retry queue dropped from a lost one.
/// </summary>
public static class DroppedWriteCountResolver
{
    /// <summary>
    /// Returns a function reading <c>OutboundRetries.TotalDropped</c> of the source among <paramref name="connectors"/>
    /// whose root is <paramref name="root"/>, or <c>null</c> for the server, which has no source.
    /// </summary>
    /// <exception cref="InvalidOperationException">A client has no source with that root.</exception>
    public static Func<long>? Resolve(
        IEnumerable<ISubjectConnector> connectors, TestNode root, string participantName, bool isServer)
    {
        if (isServer)
        {
            return null;
        }

        var source = connectors.OfType<ISubjectSource>().FirstOrDefault(connector => connector.RootSubject == root)
            ?? throw new InvalidOperationException(
                $"Participant '{participantName}' has no source for its root, so the write-durability check cannot count the writes it dropped.");
        return () => source.Diagnostics.OutboundRetries.TotalDropped;
    }
}
