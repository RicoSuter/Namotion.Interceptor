using Microsoft.AspNetCore.Mvc;
using Namotion.Proxy;
using Namotion.Proxy.AspNetCore.Controllers;
using Namotion.Proxy.Registry.Abstractions;
using Namotion.Proxy.Sources.Attributes;
using NSwag.Annotations;

namespace Namotion.Trackable.SampleMachine
{
    public class OpcUaReferenceAttribute : Attribute, IProxyPropertyInitializer
    {
        private readonly string _type;

        public OpcUaReferenceAttribute(string type)
        {
            _type = type;
        }

        public void InitializeProperty(RegisteredProxyProperty property, object? index, IProxyContext context)
        {
            property.AddAttribute("ReferenceType", typeof(string), () => _type, null);
        }
    }

    public class OpcUaTypeDefinitionAttribute : Attribute, IProxyPropertyInitializer
    {
        private readonly string _type;

        public OpcUaTypeDefinitionAttribute(string type)
        {
            _type = type;
        }

        public void InitializeProperty(RegisteredProxyProperty property, object? index, IProxyContext context)
        {
            property.AddAttribute("TypeDefinition", typeof(string), () => _type, null);
        }
    }

    [GenerateProxy]
    public class RootBase
    {
        [ProxySourcePath("opc", "Machines")]
        [OpcUaReference("Organizes")]
        public virtual Machines Machines { get; } = new Machines();
    }

    [GenerateProxy]
    [OpcUaTypeDefinition("FolderType")]
    public class MachinesBase
    {
        [ProxySourcePath("opc", "MyMachine")]
        [OpcUaReference("Organizes")]
        public virtual MyMachine MyMachine { get; } = new MyMachine();
    }

    [GenerateProxy]
    [OpcUaTypeDefinition("BaseObjectType")]
    public class MyMachineBase
    {
        [ProxySourcePath("opc", "Identification")]
        [OpcUaReference("HasAddIn")]
        public virtual Identification Identification { get; }

        [OpcUaReference("HasComponent")]
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
        [OpcUaReference("HasAddIn")]
        public virtual Identification Identification { get; }

        public MachineryBuildingBlocksBase(Identification identification)
        {
            Identification = identification;
        }
    }

    [GenerateProxy]
    [OpcUaTypeDefinition("MachineIdentificationType")]
    public class IdentificationBase
    {
        [ProxySource("opc", "Manufacturer")]
        public virtual string? Manufacturer { get; set; } = "My Manufacturer";

        [ProxySource("opc", "SerialNumber")]
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