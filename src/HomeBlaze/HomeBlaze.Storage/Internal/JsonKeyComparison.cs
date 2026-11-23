using System.Text.Json;

namespace HomeBlaze.Storage.Internal;

internal static class JsonKeyComparison
{
    /// <summary>
    /// Gets the paths of the properties of <paramref name="original"/> that <paramref name="replacement"/> does not have
    /// at the same path, such as <c>child.host</c> or <c>items[0].name</c>. Values may differ. A property whose value is
    /// null may be missing. The elements of arrays are compared up to the shorter length.
    /// </summary>
    /// <param name="original">The original JSON.</param>
    /// <param name="replacement">The JSON that replaces it.</param>
    /// <exception cref="JsonException">Either text is not valid JSON.</exception>
    public static IReadOnlyList<string> FindRemovedKeys(string original, string replacement)
    {
        using var originalDocument = JsonDocument.Parse(original);
        using var replacementDocument = JsonDocument.Parse(replacement);
        List<string>? removed = null;
        Compare(originalDocument.RootElement, replacementDocument.RootElement, "", ref removed);
        return removed ?? [];
    }

    private static void Compare(JsonElement original, JsonElement replacement, string path, ref List<string>? removed)
    {
        if (original.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in original.EnumerateObject())
            {
                var propertyPath = path.Length == 0 ? property.Name : path + "." + property.Name;
                if (replacement.ValueKind == JsonValueKind.Object && replacement.TryGetProperty(property.Name, out var replacementValue))
                {
                    Compare(property.Value, replacementValue, propertyPath, ref removed);
                }
                else if (property.Value.ValueKind != JsonValueKind.Null)
                {
                    (removed ??= []).Add(propertyPath);
                }
            }
        }
        else if (original.ValueKind == JsonValueKind.Array && replacement.ValueKind == JsonValueKind.Array)
        {
            var originalElements = original.EnumerateArray();
            var replacementElements = replacement.EnumerateArray();
            for (var index = 0; originalElements.MoveNext() && replacementElements.MoveNext(); index++)
            {
                Compare(originalElements.Current, replacementElements.Current, $"{path}[{index}]", ref removed);
            }
        }
    }
}
