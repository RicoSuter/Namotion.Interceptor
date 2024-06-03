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
        [OpcUaNode("Stages", "http://jf.com")]
        public virtual Stages Stages { get; } = new Stages();
    }

    [GenerateProxy]
    public class StagesBase
    {
        [OpcUaNode("PreWashStage", "http://jf.com")]
        public virtual PreWashStage PreWashStage { get; } = new PreWashStage();

        [OpcUaNode("WashStage", "http://jf.com")]
        public virtual WashStage WashStage { get; } = new WashStage();

        [OpcUaNode("CareStage", "http://jf.com")]
        public virtual CareStage CareStage { get; } = new CareStage();

        [OpcUaNode("DryStage", "http://jf.com")]
        public virtual DryStage DryStage { get; } = new DryStage();
    }

    [GenerateProxy]
    public class PreWashStageBase
    {

    }

    [GenerateProxy]
    public class CareStageBase
    {

    }

    [GenerateProxy]
    public class DryStageBase
    {

    }

    [GenerateProxy]
    public class WashStageBase
    {
        [OpcUaNode("BrushLeft", "http://jf.com")]
        public virtual Brush BrushLeft { get; } = new Brush();

        [OpcUaNode("BrushRight", "http://jf.com")]
        public virtual Brush BrushRight { get; } = new Brush();

        [OpcUaNode("BrushTop", "http://jf.com")]
        public virtual Brush BrushTop { get; } = new Brush();
    }

    [GenerateProxy]
    public class BrushBase
    {
        [OpcUaNode("MainMotor", "http://jf.com")]
        public virtual Motor MainMotor { get; } = new Motor();

        [OpcUaNode("ArmMotor", "http://jf.com")]
        public virtual Motor ArmMotor { get; } = new Motor();

        [OpcUaNode("Valve", "http://jf.com")]
        public virtual Valve Valve { get; } = new Valve();

        [OpcUaNode("DistanceSensor", "http://jf.com")]
        public virtual DistanceSensor DistanceSensor { get; } = new DistanceSensor();

        [OpcUaNode("PressureSensor", "http://jf.com")]
        public virtual PressureSensor PressureSensor { get; } = new PressureSensor();
    }

    [GenerateProxy]
    public class MotorBase
    {
        [OpcUaVariable("State", "http://jf.com")]
        public virtual int State { get; protected set; }

        [OpcUaVariable("ActualSpeed", "http://jf.com")]
        public virtual int ActualSpeed { get; protected set; } // value

        [OpcUaVariable("DesiredSpeed", "http://jf.com")]
        public virtual int DesiredSpeed { get; set; } // parameter

        [OpcUaVariable("Forward", "http://jf.com")]
        public virtual bool Forward { get; set; } // digital out

        [OpcUaVariable("RunningForward", "http://jf.com")]
        public virtual bool RunningForward { get; protected set; } // digital in

        [OpcUaVariable("Reverse", "http://jf.com")]
        public virtual bool Reverse { get; set; } // digital out

        [OpcUaVariable("RunningForward", "http://jf.com")]
        public virtual bool RunningReverse { get; protected set; } // digital in

        [OpcUaNode("StartForwardCommand", "http://jf.com")]
        public virtual Command StartForwardCommand { get; } = new Command();

        [OpcUaNode("StartReverseCommand", "http://jf.com")]
        public virtual Command StartReverseCommand { get; } = new Command();

        [OpcUaNode("StopCommand", "http://jf.com")]
        public virtual Command StopCommand { get; } = new Command();
    }

    [GenerateProxy]
    public class ValveBase
    {

    }

    [GenerateProxy]
    public class DistanceSensorBase
    {
        [OpcUaVariable("Distance", "http://jf.com")]
        public virtual double Distance { get; protected set; } // analog in
    }

    [GenerateProxy]
    public class PressureSensorBase
    {
        [OpcUaVariable("Pressure", "http://jf.com")]
        public virtual double Pressure { get; protected set; } // analog in
    }

    [GenerateProxy]
    public class CommandBase
    {
        [OpcUaVariable("Execute", "http://jf.com")]
        public virtual bool Execute { get; set; }

        [OpcUaVariable("Enabled", "http://jf.com")]
        public virtual bool IsEnabled { get; set; }
    }

    [GenerateProxy]
    [OpcUaTypeDefinition("BaseObjectType")]
    public class MachineBase
    {
        [OpcUaNode("Identification", "http://opcfoundation.org/UA/DI/")]
        [OpcUaNodeReferenceType("HasAddIn")]
        public virtual Identification Identification { get; }

        [OpcUaNode("MachineryBuildingBlocks", "http://opcfoundation.org/UA/")]
        [OpcUaNodeReferenceType("HasComponent")]
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
        [OpcUaNode("Identification", "http://opcfoundation.org/UA/DI/")]
        [OpcUaNodeReferenceType("HasAddIn")]
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
                //Machines = new Dictionary<string, Machine>
                //{
                //    {
                //        "MyMachine", new Machine
                //        {
                //            Identification =
                //            {
                //                SerialNumber = "Hello world!"
                //            }
                //        }
                //    }
                //}
            };

            // trackable
            builder.Services.AddSingleton(root);

            // trackable api controllers
            builder.Services.AddProxyControllers<Root, TrackablesController<Root>>();

            // OPC UA server
            builder.Services.AddOpcUaServerProxySource<Root>("opc");

            //builder.Services.AddOpcUaClientProxySource<Root>("opc", "opc.tcp://localhost:4840");

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

        [OpenApiTag("Root")]
        [Route("/api/root")]
        public class TrackablesController<TProxy> : ProxyControllerBase<TProxy> where TProxy : IProxy
        {
            public TrackablesController(TProxy proxy) : base(proxy)
            {
            }
        }
    }
}