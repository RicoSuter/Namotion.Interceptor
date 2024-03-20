using Namotion.Proxy;
using Namotion.Proxy.Handlers;

namespace ConsoleApp1
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var context = new ProxyContext([
                new LogPropertyChangesHandler("1"), 
                new LogPropertyChangesHandler("2")]);

            var person = new Person(context);
            person.FirstName = "Rico";
            person.LastName = "Suter";

            Console.WriteLine(person.FirstName);
        }
    }

    [GenerateProxy]
    public abstract class PersonBase
    {
        public virtual string FirstName { get; set; }

        public virtual string? LastName { get; set; }

        public virtual Person? Parent { get; set; }

        public virtual Person? Mother { get; set; }
    }

    public class LogPropertyChangesHandler : IProxyWriteHandler, IProxyReadHandler
    {
        private readonly string _prefix;

        public LogPropertyChangesHandler(string prefix)
        {
            _prefix = prefix;
        }

        public object? GetProperty(ProxyReadHandlerContext context, Func<ProxyReadHandlerContext, object?> next)
        {
            Console.WriteLine($"{_prefix}: Reading {context.PropertyName}...");
            var result = next.Invoke(context);
            Console.WriteLine($"{_prefix}: Read {context.PropertyName} to {result}");
            return result;
        }

        public void SetProperty(ProxyWriteHandlerContext context, Action<ProxyWriteHandlerContext> next)
        {
            Console.WriteLine($"{_prefix}: Setting {context.PropertyName} to {context.NewValue}");
            next.Invoke(context);
            Console.WriteLine($"{_prefix}: Set {context.PropertyName} to {context.NewValue}");
        }
    }
}
