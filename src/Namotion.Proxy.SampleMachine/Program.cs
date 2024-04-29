using Microsoft.AspNetCore.Mvc;
using Namotion.Proxy;
using Namotion.Proxy.AspNetCore.Controllers;
using Namotion.Proxy.OpcUa.Annotations;
using NSwag.Annotations;

namespace Namotion.Trackable.SampleMachine
{
    [GenerateProxy]
    public class RootBase
    {
        [OpcUaProperty("Machines", "http://opcfoundation.org/UA/Machinery/")]
        [OpcUaPropertyReferenceType("Organizes")]
        [OpcUaPropertyItemReferenceType("Organizes")]
        public virtual IReadOnlyDictionary<string, Machine> Machines { get; set; } = new Dictionary<string, Machine>();
    }

    [GenerateProxy]
    [OpcUaTypeDefinition("BaseObjectType")]
    public class MachineBase
    {
        [OpcUaProperty("Identification", "http://opcfoundation.org/UA/DI/")]
        [OpcUaPropertyReferenceType("HasAddIn")]
        public virtual Identification Identification { get; }

        [OpcUaProperty("MachineryBuildingBlocks", "http://opcfoundation.org/UA/")]
        [OpcUaPropertyReferenceType("HasComponent")]
        public virtual MachineryBuildingBlocks MachineryBuildingBlocks { get; }

        public MachineBase()
        {
            Identification = new Identification();
            MachineryBuildingBlocks = new MachineryBuildingBlocks(Identification);
        }
    }

    [GenerateProxy]
    [OpcUaTypeDefinition("FolderType")]
    public class MachineryBuildingBlocksBase
    {
        [OpcUaProperty("Identification", "http://opcfoundation.org/UA/DI/")]
        [OpcUaPropertyReferenceType("HasAddIn")]
        public virtual Identification Identification { get; }

        public MachineryBuildingBlocksBase(Identification identification)
        {
            Identification = identification;
        }
    }

    [GenerateProxy]
    [OpcUaTypeDefinition("MachineIdentificationType", "http://opcfoundation.org/UA/Machinery/")]
    public class IdentificationBase
    {
        [OpcUaVariable("Manufacturer", "http://opcfoundation.org/UA/DI/")]
        public virtual string? Manufacturer { get; set; } = "My Manufacturer";

        [OpcUaVariable("SerialNumber", "http://opcfoundation.org/UA/DI/")]
        public virtual string? SerialNumber { get; set; } = "My Serial Number";
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var context = ProxyContext
                .CreateBuilder()
                .WithRegistry()
                .WithFullPropertyTracking()
                .WithProxyLifecycle()
                .WithDataAnnotationValidation()
                .Build();

            var root = new Root(context)
            {
                Machines = new Dictionary<string, Machine>
                {
                    {
                        "MyMachine", new Machine
                        {
                            Identification =
                            {
                                SerialNumber = "Hello world!"
                            }
                        }
                    }
                }
            };

            // trackable
            builder.Services.AddSingleton(root);

            // trackable api controllers
            builder.Services.AddProxyControllers<Root, TrackablesController<Root>>();

            // trackable UPC UA
            builder.Services.AddOpcUaServerProxySource<Root>("opc");

            // trackable mqtt
            builder.Services.AddMqttServerProxySource<Root>("mqtt");

            // trackable GraphQL
            builder.Services
                .AddGraphQLServer()
                .AddInMemorySubscriptions()
                .AddTrackedGraphQL<Root>();

            // other asp services
            builder.Services.AddOpenApiDocument();
            builder.Services.AddAuthorization();

            var app = builder.Build();

            app.UseHttpsRedirection();
            app.UseAuthorization();

            app.MapGraphQL();

            app.UseOpenApi();
            app.UseSwaggerUi();

            app.MapControllers();
            app.Run();
        }

        [OpenApiTag("Car")]
        [Route("/api/car")]
        public class TrackablesController<TProxy> : ProxyControllerBase<TProxy> where TProxy : IProxy
        {
            public TrackablesController(TProxy proxy) : base(proxy)
            {
            }
        }
    }
}