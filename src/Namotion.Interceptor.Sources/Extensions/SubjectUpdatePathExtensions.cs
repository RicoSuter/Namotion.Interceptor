using Namotion.Interceptor.Sources.Paths;

namespace Namotion.Interceptor.Sources.Extensions;

public static class SubjectUpdatePathExtensions
{
    public static SubjectUpdate ConvertPathSegments(this SubjectUpdate update, 
        Func<string, string> convertPropertyName, 
        Func<string, string> convertAttributeName)
    {
        return new SubjectUpdate
        {
            Type = update.Type,
            Properties = update.Properties.ToDictionary(
                p => convertPropertyName(p.Key) ?? p.Key,
                p => p.Value.ConvertPathSegments(convertPropertyName, convertAttributeName))
        };
    }

    public static SubjectPropertyUpdate ConvertPathSegments(this SubjectPropertyUpdate update, 
        Func<string, string> convertPropertyName, 
        Func<string, string> convertAttributeName)
    {
        return new SubjectPropertyUpdate
        {
            Type = update.Type,
            Attributes = update.Attributes?.ToDictionary(
                a => convertAttributeName(a.Key) ?? a.Key,
                a => a.Value.ConvertPathSegments(convertPropertyName, convertAttributeName)),

            Kind = update.Kind,

            Value = update.Value,
            Item = update.Item?.ConvertPathSegments(convertPropertyName, convertAttributeName),
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