using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Connectors.Paths.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;

namespace Namotion.Interceptor.SampleWeb
{
    [InterceptorSubject]
    public partial class Car
    {
        [SourcePath("mqtt", "name")]
        [SourcePath("opc", "Name")]
        public partial string Name { get; set; }

        [SourcePath("mqtt", "tires")]
        [SourcePath("opc", "Tires")]
        public partial Tire[] Tires { get; set; }

        [Derived]
        [SourcePath("mqtt", "averagePressure")]
        [SourcePath("opc", "AveragePressure")]
        public decimal AveragePressure => Tires.Average(t => t.Pressure);

        public Car()
        {
            Tires = Enumerable
                .Range(1, 4)
                .Select(_ => new Tire())
                .ToArray();

            Name = "My Car";
        }
    }

    [InterceptorSubject]
    public partial class Tire //: BackgroundService
    {
        [SourcePath("mqtt", "pressure")]
        [SourcePath("opc", "Pressure")]
        [Unit("bar")]
        public partial decimal Pressure { get; set; }

        [Unit("bar")]
        [SourcePath("mqtt", "minimum")]
        [PropertyAttribute(nameof(Pressure), "Minimum")]
        public partial decimal Pressure_Minimum { get; set; }

        [Derived]
        [SourcePath("mqtt", "maximum")]
        [SourcePath("opc", "Maximum")]
        [PropertyAttribute(nameof(Pressure), "Maximum")]
        public decimal Pressure_Maximum => 4 * Pressure;

        public Tire()
        {
            Pressure_Minimum = 0.0m;
        }

        // protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        // {
        //     // This is automatically started by .WithHostedServices()
        //     while (!stoppingToken.IsCancellationRequested)
        //     {
        //         await Task.Delay(1000, stoppingToken);
        //         Console.WriteLine("Current pressure: " + Pressure);
        //     }
        // }
    }

    public class UnitAttribute : Attribute, ISubjectPropertyInitializer
    {
        private readonly string _unit;

        public UnitAttribute(string unit)
        {
            _unit = unit;
        }

        public void InitializeProperty(RegisteredSubjectProperty property)
        {
            property.AddAttribute("Unit", typeof(string),
                _ => _unit, null,
                new SourcePathAttribute("mqtt", "unit"));
        }
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry()
                .WithParents()
                .WithLifecycle()
                .WithDataAnnotationValidation()
                .WithHostedServices(builder.Services);

            var car = new Car(context);

            // register subject and context
            builder.Services.AddSingleton(car);
            builder.Services.AddSingleton(context);

            // expose subject via OPC UA
            builder.Services.AddOpcUaServerConnector<Car>("opc", rootName: "Root");
            // builder.Services.AddOpcUaClientConnector<Car>("opc.tcp://localhost:4840", "opc", rootName: "Root");

            // expose subject via MQTT
            builder.Services.AddMqttServerConnector<Car>("mqtt");
            //builder.Services.AddMqttServerConnector<Tire>(sp => sp.GetRequiredService<Car>().Tires[2], "mqtt");

            // expose subject via GraphQL
            builder.Services
                .AddGraphQLServer()
                .AddInMemorySubscriptions()
                .AddSubjectGraphQL<Car>();

            // other asp services
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddOpenApiDocument();
            builder.Services.AddAuthorization();

            builder.Services.AddHostedService<Simulator>();

            var app = builder.Build();

            // expose subject via HTTP web api
            app.MapSubjectWebApis<Car>("api/car");
            
            app.UseHttpsRedirection();
            app.UseAuthorization();
            
            app.MapGraphQL();

            app.UseOpenApi();
            app.UseSwaggerUi();

            app.Run();
        }

        public class Simulator : BackgroundService
        {
            private readonly Car _car;

            public Simulator(Car car)
            {
                _car = car;
            }

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    _car.Tires[0].Pressure = Random.Shared.Next(0, 40) / 10m;
                    _car.Tires[1].Pressure = Random.Shared.Next(0, 40) / 10m;
                    _car.Tires[2].Pressure = Random.Shared.Next(0, 40) / 10m;

                    await Task.Delay(1000, stoppingToken);
                }
            }
        }
    }
}