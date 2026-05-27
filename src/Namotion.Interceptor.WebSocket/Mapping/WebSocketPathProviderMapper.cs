using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.WebSocket.Mapping;

public class WebSocketPathProviderMapper : DelegateMapper<WebSocketPropertyMapping>
{
    public WebSocketPathProviderMapper(PathProviderBase pathProvider)
        : base(property => pathProvider.IsPropertyIncluded(property) ? new WebSocketPropertyMapping() : null) { }
}
