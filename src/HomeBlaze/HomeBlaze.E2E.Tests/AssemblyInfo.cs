using Xunit;

// Assembly-level trait to mark all tests as E2E
[assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]
