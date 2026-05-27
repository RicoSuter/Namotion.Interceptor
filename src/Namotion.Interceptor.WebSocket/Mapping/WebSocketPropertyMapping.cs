using Namotion.Interceptor.Connectors.Mapping;

namespace Namotion.Interceptor.WebSocket.Mapping;

public sealed record WebSocketPropertyMapping() : IPropertyMapping<WebSocketPropertyMapping>
{
    public static WebSocketPropertyMapping Merge(WebSocketPropertyMapping primary, WebSocketPropertyMapping fallback)
        => primary;
}
