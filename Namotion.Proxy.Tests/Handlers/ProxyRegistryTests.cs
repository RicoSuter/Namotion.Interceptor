using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Tests.Handlers
{
    public class ProxyRegistryTests
    {
        public class MyProxyPropertyRegistryHandler : IProxyLifecycleHandler
        {
            private readonly List<ProxyLifecycleContext> _attaches;
            private readonly List<ProxyLifecycleContext> _detaches;

            public MyProxyPropertyRegistryHandler(
                List<ProxyLifecycleContext> attaches,
                List<ProxyLifecycleContext> detaches)
            {
                _attaches = attaches;
                _detaches = detaches;
            }

            public void OnProxyAttached(ProxyLifecycleContext context)
            {
                _attaches.Add(context);
            }

            public void OnProxyDetached(ProxyLifecycleContext context)
            {
                _detaches.Add(context);
            }
        }

        [Fact]
        public void WhenTwoChildrenAreAttachedSequentially_ThenWeHaveThreeAttaches()
        {
            // Arrange
            var attaches = new List<ProxyLifecycleContext>();
            var detaches = new List<ProxyLifecycleContext>();

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
            var attaches = new List<ProxyLifecycleContext>();
            var detaches = new List<ProxyLifecycleContext>();

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
            var attaches = new List<ProxyLifecycleContext>();
            var detaches = new List<ProxyLifecycleContext>();

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
