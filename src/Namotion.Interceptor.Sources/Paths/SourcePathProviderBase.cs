using System.Text;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Paths;

public abstract class SourcePathProviderBase : ISourcePathProvider
{
    /// <inheritdoc />
    public virtual bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return true;
    }

    /// <inheritdoc />
    public virtual string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        return property.BrowseName;
    }

    /// <inheritdoc />
    public virtual string GetPropertyFullPath(IEnumerable<(RegisteredSubjectProperty property, object? index)> propertiesInPath)
    {
        StringBuilder? sb = null;
        foreach (var (property, index) in propertiesInPath)
        {
            sb ??= new StringBuilder();
            if (sb.Length > 0)
            {
                sb.Append('.');
            }

            sb.Append(property.BrowseName);
            if (index is not null)
            {
                sb.Append('[');
                sb.Append(index);
                sb.Append(']');
            }
        }

        return sb?.ToString() ?? string.Empty;
    }

    /// <inheritdoc />
    public virtual IEnumerable<(string segment, object? index)> ParsePathSegments(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return [];
        }

        var results = new List<(string segment, object? index)>();
        var start = 0;
        var length = path.Length;

        for (var i = 0; i <= length; i++)
        {
            if (i == length || path[i] == '.')
            {
                if (i > start)
                {
                    var segmentSpan = path.AsSpan(start, i - start);
                    results.Add(ParseSingleSegment(segmentSpan));
                }
                start = i + 1;
            }
        }

        return results;
    }

    private static (string segment, object? index) ParseSingleSegment(ReadOnlySpan<char> segment)
    {
        var bracketIndex = segment.IndexOf('[');
        if (bracketIndex < 0)
        {
            return (segment.ToString(), null);
        }

        var name = segment.Slice(0, bracketIndex).ToString();
        var closeBracket = segment.IndexOf(']');
        if (closeBracket <= bracketIndex + 1)
        {
            return (name, null);
        }

        var indexSpan = segment.Slice(bracketIndex + 1, closeBracket - bracketIndex - 1);
        object? index = int.TryParse(indexSpan, out var intIndex) ? intIndex : indexSpan.ToString();
        return (name, index);
    }

    /// <inheritdoc />
    public RegisteredSubjectProperty? TryGetAttributeFromSegment(RegisteredSubjectProperty property, string attributeSegment)
    {
        RegisteredSubjectProperty? result = null;
        foreach (var p in property.Parent.Properties)
        {
            if (p.IsAttribute &&
                p.AttributeMetadata.PropertyName == property.Name &&
                p.AttributeMetadata.AttributeName == attributeSegment)
            {
                if (result is not null)
                {
                    throw new InvalidOperationException("Multiple matching attribute properties found.");
                }
                result = p;
            }
        }
        return result;
    }

    /// <inheritdoc />
    public virtual RegisteredSubjectProperty? TryGetPropertyFromSegment(RegisteredSubject subject, string propertySegment)
    {
        RegisteredSubjectProperty? result = null;
        foreach (var property in subject.Properties)
        {
            if (TryGetPropertySegment(property) == propertySegment)
            {
                if (result is not null)
                {
                    throw new InvalidOperationException("Multiple matching properties found for segment.");
                }
                result = property;
            }
        }
        return result;
    }
}
