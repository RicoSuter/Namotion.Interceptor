namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a method as a query that returns a result without side effects.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class QueryAttribute : SubjectMethodAttribute
{
}
