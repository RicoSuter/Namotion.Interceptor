using Namotion.Interceptor.Sources.Extensions;
using Namotion.Interceptor.Sources.Updates;

namespace Namotion.Interceptor.Sources.Paths;

public static class JsonCamelCaseSourcePathProviderExtensions
{
    public static SubjectUpdate ConvertToJsonCamelCasePath(this SubjectUpdate update)
    {
        return update.ConvertPathSegments(
            JsonCamelCaseSourcePathProvider.ConvertToSourcePath, 
            JsonCamelCaseSourcePathProvider.ConvertToSourcePath);
    }

    public static SubjectUpdate ConvertFromJsonCamelCasePath(this SubjectUpdate update)
    {
        return update.ConvertPathSegments(
            JsonCamelCaseSourcePathProvider.ConvertFromSourcePath, 
            JsonCamelCaseSourcePathProvider.ConvertFromSourcePath);
    }
}