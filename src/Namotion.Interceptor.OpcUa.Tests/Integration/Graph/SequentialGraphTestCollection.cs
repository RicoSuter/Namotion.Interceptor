using Xunit;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

/// <summary>
/// Collection for multi-step graph tests that depend on sequential model change event processing.
/// Tests in this collection run sequentially to avoid event interleaving with parallel tests.
/// Simple add/remove tests can run in parallel without this collection.
/// </summary>
[CollectionDefinition("SequentialGraphTests", DisableParallelization = true)]
public class SequentialGraphTestCollection { }
