using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.WebSocket.Mapping;

/// <summary>
/// Maps properties for WebSocket inclusion using a path provider.
/// </summary>
public class WebSocketPathProviderMapper : DelegateMapper<WebSocketPropertyMapping>
{
    public WebSocketPathProviderMapper(PathProviderBase pathProvider)
        : base((property, _) => pathProvider.IsPropertyIncluded(property) ? new WebSocketPropertyMapping() : null) { }
}
