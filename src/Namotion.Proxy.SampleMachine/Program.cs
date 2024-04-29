using Microsoft.AspNetCore.Mvc;
using Namotion.Proxy;
using Namotion.Proxy.AspNetCore.Controllers;
using Namotion.Proxy.OpcUa.Annotations;
using Namotion.Proxy.Sources.Attributes;
using NSwag.Annotations;

namespace Namotion.Trackable.SampleMachine
{
    [GenerateProxy]
    public class RootBase
    {
        [ProxySourcePath("opc", "Machines")]
        [OpcUaReferenceType("Organizes")]
        [OpcUaBrowseName("Machines", "http://opcfoundation.org/UA/Machinery/")]
        public virtual Machines Machines { get; } = new Machines();
    }

    [GenerateProxy]
    [OpcUaTypeDefinition("FolderType")]
    public class MachinesBase
    {
        [ProxySourcePath("opc", "MyMachine")]
        [OpcUaReferenceType("Organizes")]
        public virtual MyMachine MyMachine { get; } = new MyMachine();
    }

    [GenerateProxy]
    [OpcUaTypeDefinition("BaseObjectType")]
    public class MyMachineBase
    {
        [ProxySourcePath("opc", "Identification")]
        [OpcUaBrowseName("Identification", "http://opcfoundation.org/UA/DI/")]
        [OpcUaReferenceType("HasAddIn")]
        public virtual Identification Identification { get; }

        [ProxySourcePath("opc", "MachineryBuildingBlocks")]
        [OpcUaBrowseName("MachineryBuildingBlocks", "http://opcfoundation.org/UA/")]
        [OpcUaReferenceType("HasComponent")]
        public virtual MachineryBuildingBlocks MachineryBuildingBlocks { get; }

        public MyMachineBase()
        {
            Identification = new Identification();
            MachineryBuildingBlocks = new MachineryBuildingBlocks(Identification);
        }
    }

    [GenerateProxy]
    [OpcUaTypeDefinition("FolderType")]
    public class MachineryBuildingBlocksBase
    {
        [ProxySourcePath("opc", "Identification")]
        [OpcUaReferenceType("HasAddIn")]
        [OpcUaBrowseName("Identification", "http://opcfoundation.org/UA/DI/")]
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
        [ProxySource("opc", "Manufacturer")]
        [OpcUaBrowseName("Manufacturer", "http://opcfoundation.org/UA/DI/")]
        public virtual string? Manufacturer { get; set; } = "My Manufacturer";

        [ProxySource("opc", "SerialNumber")]
        [OpcUaBrowseName("SerialNumber", "http://opcfoundation.org/UA/DI/")]
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

            var car = new Root(context);

            // trackable
            builder.Services.AddSingleton(car);

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