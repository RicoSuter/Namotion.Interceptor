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
        if (update.Properties.Count > 0)
        {
            TransformDictionaryKeys(update.Properties);
        }

        return update;
    }

    public SubjectPropertyUpdate TransformSubjectPropertyUpdate(RegisteredSubjectProperty property, SubjectPropertyUpdate update)
    {
        if (update.Attributes is not null && update.Attributes.Count > 0)
        {
            TransformDictionaryKeys(update.Attributes);
        }
        
        return update;
    }

    private static void TransformDictionaryKeys(Dictionary<string, SubjectPropertyUpdate> dictionary)
    {
        var count = dictionary.Count;
        if (count == 0)
        {
            return;
        }

        var keyPairs = System.Buffers.ArrayPool<string>.Shared.Rent(count * 2);
        try
        {
            // Single pass: collect keys that need transformation
            var index = 0;
            foreach (var key in dictionary.Keys)
            {
                var transformedKey = JsonCamelCaseSourcePathProvider.ConvertToSourcePath(key);
                if (transformedKey != key)
                {
                    keyPairs[index++] = key;
                    keyPairs[index++] = transformedKey;
                }
            }

            // Early exit if no transformations needed
            if (index == 0)
            {
                return;
            }

            // Apply transformations
            for (int i = 0; i < index; i += 2)
            {
                var oldKey = keyPairs[i];
                var newKey = keyPairs[i + 1];
                var value = dictionary[oldKey];
                dictionary.Remove(oldKey);
                dictionary[newKey] = value;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<string>.Shared.Return(keyPairs);
        }
    }
}