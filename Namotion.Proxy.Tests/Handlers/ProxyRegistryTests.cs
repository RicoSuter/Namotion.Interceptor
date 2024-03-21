using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Tests.Handlers
{
    public class ProxyRegistryTests
    {
        public class MyProxyPropertyRegistryHandler : IProxyLifecycleHandler
        {
            private readonly List<ProxyPropertyRegistryHandlerContext> _attaches;
            private readonly List<ProxyPropertyRegistryHandlerContext> _detaches;

            public MyProxyPropertyRegistryHandler(
                List<ProxyPropertyRegistryHandlerContext> attaches,
                List<ProxyPropertyRegistryHandlerContext> detaches)
            {
                _attaches = attaches;
                _detaches = detaches;
            }

            public void AttachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
            {
                _attaches.Add(context);
            }

            public void DetachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
            {
                _detaches.Add(context);
            }
        }

        [Fact]
        public void WhenTwoChildrenAreAttachedSequentially_ThenWeHaveThreeAttaches()
        {
            // Arrange
            var attaches = new List<ProxyPropertyRegistryHandlerContext>();
            var detaches = new List<ProxyPropertyRegistryHandlerContext>();

            var handler = new MyProxyPropertyRegistryHandler(attaches, detaches);
            var context = ProxyContext
                .CreateBuilder()
                .WithRegistry()
                .AddHandler(handler)
                .Build();

            // Act
            var person = new Person(context)
            {
                FirstName = "Child",
                Mother = new Person
                {
                    FirstName = "Susi",
                    Mother = new Person
                    {
                        FirstName = "Susi2"
                    }
                }
            };

            // Assert
            Assert.Equal(3, attaches.Count);
            Assert.Empty(detaches);

            var registry = context.GetHandlers<IProxyRegistry>().Single();
            Assert.Equal(3, registry.KnownProxies.Count());
        }

        [Fact]
        public void WhenTwoChildrenAreAttachedInOneBranch_ThenWeHaveThreeAttaches()
        {
            // Arrange
            var attaches = new List<ProxyPropertyRegistryHandlerContext>();
            var detaches = new List<ProxyPropertyRegistryHandlerContext>();

            var handler = new MyProxyPropertyRegistryHandler(attaches, detaches);
            var context = ProxyContext
                .CreateBuilder()
                .WithRegistry()
                .AddHandler(handler)
                .Build();

            // Act
            var person = new Person(context)
            {
                FirstName = "Child"
            };

            person.Mother = new Person
            {
                FirstName = "Susi",
                Mother = new Person
                {
                    FirstName = "Susi2"
                }
            };

            // Assert
            Assert.Equal(3, attaches.Count);
            Assert.Empty(detaches);

            var registry = context.GetHandlers<IProxyRegistry>().Single();
            Assert.Equal(3, registry.KnownProxies.Count());
        }

        [Fact]
        public void WhenProxyWithChildProxyIsRemoved_ThenWeHaveTwoDetaches()
        {
            // Arrange
            var attaches = new List<ProxyPropertyRegistryHandlerContext>();
            var detaches = new List<ProxyPropertyRegistryHandlerContext>();

            var handler = new MyProxyPropertyRegistryHandler(attaches, detaches);
            var context = ProxyContext
                .CreateBuilder()
                .WithRegistry()
                .AddHandler(handler)
                .Build();

            // Act
            var person = new Person(context)
            {
                FirstName = "Child",
                Mother = new Person
                {
                    FirstName = "Susi",
                    Mother = new Person
                    {
                        FirstName = "Susi2"
                    }
                }
            };

            person.Mother = null;

            // Assert
            Assert.Equal(3, attaches.Count);
            Assert.Equal(2, detaches.Count);

            var registry = context.GetHandlers<IProxyRegistry>().Single();
            Assert.Single(registry.KnownProxies);
        }
    }
}
