using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Paths;

public class JsonCamelCasePathProcessor : ISubjectUpdateProcessor
{
    public static JsonCamelCasePathProcessor Instance { get; } = new();

    public bool IsIncluded(RegisteredSubjectProperty property)
    {
        return true;
    }

    public SubjectUpdate TransformSubjectUpdate(IInterceptorSubject subject, SubjectUpdate update)
    {
        // Transform all property names in all subjects to camelCase
        foreach (var subjectProperties in update.Subjects.Values)
        {
            if (subjectProperties.Count > 0)
            {
                TransformDictionaryKeys(subjectProperties);
            }
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
            foreach (var kvp in dictionary)
            {
                var key = kvp.Key;
                if (key.Length > 0 && char.IsUpper(key[0]))
                {
                    var transformedKey = key.Length > 1
                        ? string.Create(key.Length, key, static (span, k) =>
                        {
                            span[0] = char.ToLowerInvariant(k[0]);
                            k.AsSpan(1).CopyTo(span[1..]);
                        })
                        : key.ToLowerInvariant();

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
