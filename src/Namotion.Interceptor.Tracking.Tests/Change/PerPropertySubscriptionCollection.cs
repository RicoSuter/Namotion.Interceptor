namespace Namotion.Interceptor.Tracking.Tests.Change;

// EVERY test that creates a per-property subscription OR asserts on the count / fast-path / build
// state must belong to this collection, because the PropertyChangeSubscriptions subscription count is process-wide
// and xUnit runs different collections in parallel. Membership is not only to stop per-property tests
// racing each other: a Subscribe in this (serialized) collection bumps the shared count while it runs,
// so a fast-path/count/allocation assertion in a DIFFERENT, parallel collection would read count != 0
// and take the listener-lookup branch, poisoning the assertion. Keeping every such assertion inside
// this one serialized collection means no per-property Subscribe can run concurrently with it.
[CollectionDefinition(Name, DisableParallelization = true)]
public class PerPropertySubscriptionCollection
{
    public const string Name = "PerPropertySubscription";
}
