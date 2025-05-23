using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Annotations;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;

namespace Namotion.Interceptor.SampleMachine
{
    [InterceptorSubject]
    public partial class Root
    {
        [OpcUaNode("Machines", "http://opcfoundation.org/UA/Machinery/")]
        [OpcUaNodeReferenceType("Organizes")]
        [OpcUaNodeItemReferenceType("Organizes")]
        public partial IReadOnlyDictionary<string, Machine> Machines { get; set; }

        public Root()
        {
            Machines = new Dictionary<string, Machine>();
        }
    }

    [InterceptorSubject]
    [OpcUaTypeDefinition("BaseObjectType")]
    public partial class Machine
    {
        [OpcUaNode("Identification", "http://opcfoundation.org/UA/DI/")]
        [OpcUaNodeReferenceType("HasAddIn")]
        public partial Identification Identification { get; private set; }

        [OpcUaNode("MachineryBuildingBlocks", "http://opcfoundation.org/UA/")]
        [OpcUaNodeReferenceType("HasComponent")]
        public partial MachineryBuildingBlocks MachineryBuildingBlocks { get; private set; }

        [OpcUaNode("Monitoring", "http://opcfoundation.org/UA/")]
        [OpcUaNodeReferenceType("HasComponent")]
        [OpcUaNodeItemReferenceType("HasComponent")]
        public partial IReadOnlyDictionary<string, ProcessValueType> Monitoring { get; set; }

        public Machine()
        {
            Identification = new Identification();
            MachineryBuildingBlocks = new MachineryBuildingBlocks(Identification);
            Monitoring = new Dictionary<string, ProcessValueType>();
        }
    }

    [InterceptorSubject]
    [OpcUaTypeDefinition("ProcessValueType", "http://opcfoundation.org/UA/Machinery/ProcessValues/")]
    public partial class ProcessValueType
    {
        [OpcUaVariable("AnalogSignal", "http://opcfoundation.org/UA/PADIM/")]
        public partial AnalogSignalVariable AnalogSignal { get; private set; }

        [OpcUaVariable("SignalTag", "http://opcfoundation.org/UA/PADIM/")]
        public partial string? SignalTag { get; set; }

        public ProcessValueType()
        {
            AnalogSignal = new AnalogSignalVariable();
        }
    }

    [InterceptorSubject]
    [OpcUaTypeDefinition("AnalogSignalVariableType", "http://opcfoundation.org/UA/PADIM/")]
    public partial class AnalogSignalVariable
    {
        [OpcUaVariable("ActualValue", "http://opcfoundation.org/UA/")]
        public partial object? ActualValue { get; set; }

        [OpcUaVariable("EURange", "http://opcfoundation.org/UA/")]
        public partial object? EURange { get; set; }

        [OpcUaVariable("EngineeringUnits", "http://opcfoundation.org/UA/")]
        public partial object? EngineeringUnits { get; set; }

        public AnalogSignalVariable()
        {
            ActualValue = "My value";
            EURange = "My range";
            EngineeringUnits = "My units";
        }
    }

    [InterceptorSubject]
    [OpcUaTypeDefinition("FolderType")]
    public partial class MachineryBuildingBlocks
    {
        [OpcUaNode("Identification", "http://opcfoundation.org/UA/DI/")]
        [OpcUaNodeReferenceType("HasAddIn")]
        public partial Identification Identification { get; private set; }

        public MachineryBuildingBlocks(Identification identification)
        {
            Identification = identification;
        }
    }

    [InterceptorSubject]
    [OpcUaTypeDefinition("MachineIdentificationType", "http://opcfoundation.org/UA/Machinery/")]
    public partial class Identification
    {
        [OpcUaVariable("Manufacturer", "http://opcfoundation.org/UA/DI/")]
        public partial string? Manufacturer { get; set; }

        [OpcUaVariable("SerialNumber", "http://opcfoundation.org/UA/DI/")]
        public partial string? SerialNumber { get; set; }

        public Identification()
        {
            Manufacturer = "My Manufacturer";
            SerialNumber = "My Serial Number";
        }
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var context = InterceptorSubjectContext
                .Create()
                .WithRegistry()
                .WithFullPropertyTracking()
                .WithLifecycle()
                .WithDataAnnotationValidation();

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
                            },
                            Monitoring = new Dictionary<string, ProcessValueType>
                            {
                                {
                                    "MyProcess",
                                    new ProcessValueType
                                    {
                                        SignalTag = "MySignal",
                                        AnalogSignal =
                                        {
                                            ActualValue = 42,
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };

            // register subject
            builder.Services.AddSingleton(root);

            // expose subject via OPC UA
            builder.Services.AddOpcUaSubjectServer<Root>("opc");

            // expose subject via GraphQL
            builder.Services
                .AddGraphQLServer()
                .AddInMemorySubscriptions()
                .AddSubjectGraphQL<Root>();

            // other asp services
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddOpenApiDocument();
            builder.Services.AddAuthorization();

            builder.Services.AddHostedService<Simulator>();

            var app = builder.Build();

            app.UseHttpsRedirection();
            app.UseAuthorization();

            // expose subject via HTTP web api
            app.MapSubjectWebApis<Root>("api/root");

            app.MapGraphQL();

            app.UseOpenApi();
            app.UseSwaggerUi();

            app.MapControllers();
            app.Run();
        }

        public class Simulator : BackgroundService
        {
            private readonly Root _root;

            public Simulator(Root root)
            {
                _root = root;
            }

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var signal = _root.Machines.Single().Value.Monitoring.Single().Value.AnalogSignal;
                    signal.ActualValue = ((int)signal.ActualValue!) + 1;

                    await Task.Delay(1000, stoppingToken);
                }
            }
        }
    }
}