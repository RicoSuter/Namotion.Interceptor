using Namotion.Interceptor.Connectors.Mapping;

namespace Namotion.Interceptor.WebSocket.Mapping;

public sealed class WebSocketCompositeMapper : CompositeMapper<WebSocketPropertyMapping>
{
    public WebSocketCompositeMapper(params IPropertyMapper<WebSocketPropertyMapping>[] mappers)
        : base(mappers) { }
}
