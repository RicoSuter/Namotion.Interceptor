using System.Linq.Expressions;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

internal static class PropertyPathHelper
{
    internal static string GetPathFromExpression(Expression expression)
    {
        var parts = new List<string>();
        var current = expression;
        while (current is MemberExpression member)
        {
            parts.Insert(0, member.Member.Name);
            current = member.Expression;
        }
        return string.Join(".", parts);
    }

    internal static string GetPathFromProperty(RegisteredSubjectProperty property)
    {
        var parts = new List<string> { property.Name };
        var currentSubject = property.Parent;

        while (currentSubject.Parents.Length > 0)
        {
            var parent = currentSubject.Parents[0];
            parts.Insert(0, parent.Property.Name);
            currentSubject = parent.Property.Parent;
        }

        return string.Join(".", parts);
    }
}
