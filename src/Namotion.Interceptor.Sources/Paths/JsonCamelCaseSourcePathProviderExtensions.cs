using Namotion.Interceptor.Sources.Updates;

namespace Namotion.Interceptor.Sources.Paths;

public static class JsonCamelCaseSourcePathProviderExtensions
{
    public static SubjectUpdate ConvertToJsonCamelCasePath(this SubjectUpdate update)
    {
        // Use in-place conversion to reduce allocations during frequent conversions
        return update.ConvertPathSegmentsInPlace(
            JsonCamelCaseSourcePathProvider.ConvertToSourcePath, 
            JsonCamelCaseSourcePathProvider.ConvertToSourcePath);
    }

    public static SubjectUpdate ConvertFromJsonCamelCasePath(this SubjectUpdate update)
    {
        // Use in-place conversion to reduce allocations during frequent conversions
        return update.ConvertPathSegmentsInPlace(
            JsonCamelCaseSourcePathProvider.ConvertFromSourcePath, 
            JsonCamelCaseSourcePathProvider.ConvertFromSourcePath);
    }
}