using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Updates;

namespace Namotion.Interceptor.Sources.Paths;

public class JsonCamelCasePathProcessor : ISubjectUpdateProcessor
{
    public static JsonCamelCasePathProcessor Instance { get; } = new();

    public bool IsIncluded(RegisteredSubjectProperty property)
    {
        return true;
    }

    public SubjectUpdate TransformSubjectUpdate(IInterceptorSubject subject, SubjectUpdate update)
    {
        // TODO(perf): Avoid memory allocations in TransformSubjectUpdate
        if (update.Properties.Count > 0)
        {
            var updatedProperties = update
                .Properties
                .ToDictionary(p => JsonCamelCaseSourcePathProvider.ConvertToSourcePath(p.Key) ?? p.Key, p => p.Value);
      
            update.Properties.Clear();
            foreach (var y in updatedProperties)
            {
                update.Properties[y.Key] = y.Value;
            }
        }

        return update;
    }

    public SubjectPropertyUpdate TransformSubjectPropertyUpdate(RegisteredSubjectProperty property, SubjectPropertyUpdate update)
    {
        // TODO(perf): Avoid memory allocations in TransformSubjectUpdate
        if (update.Attributes is not null && update.Attributes.Count > 0)
        {
            var transformedAttributes = update.Attributes
                .ToDictionary(
                    a => JsonCamelCaseSourcePathProvider.ConvertToSourcePath(a.Key) ?? a.Key, 
                    a => a.Value);
            
            update.Attributes.Clear();
            foreach (var attr in transformedAttributes)
            {
                update.Attributes[attr.Key] = attr.Value;
            }
        }
        
        return update;
    }
}