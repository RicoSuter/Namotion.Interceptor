using Xunit;

// The tests count renders that throttles of a few milliseconds coalesce, which the load of tests running beside them
// would change.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
