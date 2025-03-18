using System.Text.Json;

namespace Namotion.Interceptor.Sources.Extensions;

public static class SubjectUpdateJsonExtensions
{
    public static SubjectUpdate ConvertPropertyNames(this SubjectUpdate update, JsonSerializerOptions options)
    {
        return new SubjectUpdate
        {
            Type = update.Type,
            Properties = update.Properties.ToDictionary(
                p => options.PropertyNamingPolicy?.ConvertName(p.Key) ?? p.Key,
                p => p.Value.ConvertPropertyNames(options))
        };
    }

    public static SubjectPropertyUpdate ConvertPropertyNames(this SubjectPropertyUpdate update, JsonSerializerOptions options)
    {
        return new SubjectPropertyUpdate
        {
            Type = update.Type,
            Attributes = update.Attributes?.ToDictionary(
                a => options.PropertyNamingPolicy?.ConvertName(a.Key) ?? a.Key,
                a => a.Value.ConvertPropertyNames(options)),

            Kind = update.Kind,

            Value = update.Value,
            Item = update.Item?.ConvertPropertyNames(options),
            Collection = update.Collection?
                .Select(i => new SubjectPropertyCollectionUpdate
                {
                    Index = i.Index,
                    Item = i.Item?.ConvertPropertyNames(options)
                })
                .ToList()
        };
    }
}