using Moq;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Tests.Handlers
{
    public class ProxyChangedHandlerTestsBase
    {
        public static IProxyChangedHandler CreateMockProxyChangedHandler(List<ProxyChangedHandlerContext> changes)
        {
            var mock = new Mock<IProxyChangedHandler>();
           
            mock.Setup(h => h.RaisePropertyChanged(It.IsAny<ProxyChangedHandlerContext>()))
                .Callback<ProxyChangedHandlerContext>(changes.Add);
           
            return mock.Object;
        }
    }
}