using Namotion.Interceptor.Connectors.Mapping;

namespace Namotion.Interceptor.WebSocket.Mapping;

/// <summary>
/// Combines multiple WebSocket property mappers with last-wins merge semantics.
/// </summary>
public sealed class WebSocketCompositeMapper : CompositeMapper<WebSocketPropertyMapping>
{
    public WebSocketCompositeMapper(params IPropertyMapper<WebSocketPropertyMapping>[] mappers)
        : base(mappers) { }
}
