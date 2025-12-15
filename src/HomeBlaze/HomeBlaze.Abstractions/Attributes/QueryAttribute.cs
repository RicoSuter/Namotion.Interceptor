namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a method as a query that returns a result without side effects.
/// Query UI support is deferred to V2.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class QueryAttribute : SubjectMethodAttribute
{
}
