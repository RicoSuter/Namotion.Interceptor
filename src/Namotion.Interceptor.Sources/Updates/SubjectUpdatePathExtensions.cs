namespace Namotion.Interceptor.Sources.Updates;

public static class SubjectUpdatePathExtensions
{
    public static SubjectUpdate ConvertPathSegments(this SubjectUpdate update, 
        Func<string, string?> convertPropertyName, 
        Func<string, string?> convertAttributeName)
    {
        return update with
        {
            Properties = update.Properties.ToDictionary(
                p => convertPropertyName(p.Key) ?? p.Key,
                p => p.Value.ConvertPathSegments(convertPropertyName, convertAttributeName))
        };
    }

    public static SubjectPropertyUpdate ConvertPathSegments(this SubjectPropertyUpdate update, 
        Func<string, string?> convertPropertyName, 
        Func<string, string?> convertAttributeName)
    {
        return update with
        {
            Item = update.Item?.ConvertPathSegments(convertPropertyName, convertAttributeName),
            Attributes = update.Attributes?.ToDictionary(
                a => convertAttributeName(a.Key) ?? a.Key,
                a => a.Value.ConvertPathSegments(convertPropertyName, convertAttributeName)),
            Collection = update.Collection?
                .Select(i => new SubjectPropertyCollectionUpdate
                {
                    Index = i.Index,
                    Item = i.Item?.ConvertPathSegments(convertPropertyName, convertAttributeName)
                })
                .ToList()
        };
    }
}