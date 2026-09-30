namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Collection for tests that assert an exact delta on the process-wide
/// <c>SubjectUpdateDiagnostics</c> counters. Parallelization is disabled for it so that no other
/// apply in this assembly can increment a counter between the reading before and the reading after.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SubjectUpdateDiagnosticsCollection
{
    public const string Name = "SubjectUpdateDiagnostics";
}
