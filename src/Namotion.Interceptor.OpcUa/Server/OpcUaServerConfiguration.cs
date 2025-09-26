using Namotion.Interceptor.Sources.Paths;

namespace Namotion.Interceptor.OpcUa.Server;

public class OpcUaServerConfiguration
{
    public string? RootName { get; init; }
    
    public required ISourcePathProvider SourcePathProvider { get; init; }

    public required OpcUaDataValueConverter ValueConverter { get; init; }
}