using Microsoft.AspNetCore.Mvc;
using Namotion.Proxy;
using Namotion.Proxy.Sources.Attributes;
using NSwag.Annotations;

namespace Namotion.Trackable.SampleWeb
{
    [GenerateProxy]
    public abstract class CarBase
    {
        public CarBase()
        {
            Tires = new Tire[]
            {
                new(),
                new(),
                new(),
                new()
            };
        }

        [TrackableSource("mqtt", "name")]
        public virtual string Name { get; set; } = "My Car";

        [TrackableSourcePath("mqtt", "tires")]
        public virtual Tire[] Tires { get; set; }

        [TrackableSource("mqtt", "averagePressure")]
        public virtual decimal AveragePressure => Tires.Average(t => t.Pressure);
    }

    [GenerateProxy]
    public abstract class TireBase
    {
        [TrackableSource("mqtt", "pressure")]
        //[Unit("bar")]
        public virtual decimal Pressure { get; set; }

        //[Unit("bar")]
        //[AttributeOfTrackable(nameof(Pressure), "Minimum")]
        public virtual decimal Pressure_Minimum { get; set; } = 0.0m;

        //[AttributeOfTrackable(nameof(Pressure), "Maximum")]
        public virtual decimal Pressure_Maximum => 4 * Pressure;
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var context = ProxyContext
                .CreateBuilder()
                .WithFullPropertyTracking()
                .WithRegistry()
                .WithProxyLifecycle()
                .Build();

            var car = new Car(context);

            //// trackable
            builder.Services.AddSingleton<IProxyContext>(context);
            builder.Services.AddSingleton(car);

            //// trackable api controllers
            //builder.Services.AddTrackableControllers<Car, TrackablesController<Car>>();

            //// trackable UPC UA
            //builder.Services.AddOpcUaServerTrackableSource<Car>("mqtt");

            // trackable mqtt
            builder.Services.AddMqttServerTrackableSource<Car>("mqtt");

            //// trackable graphql
            //builder.Services
            //    .AddGraphQLServer()
            //    .AddInMemorySubscriptions()
            //    .AddTrackedGraphQL<Car>();

            // other asp services
            builder.Services.AddHostedService<Simulator>();
            builder.Services.AddOpenApiDocument();
            builder.Services.AddAuthorization();

            var app = builder.Build();

            app.UseHttpsRedirection();
            app.UseAuthorization();

            //app.MapGraphQL();

            //app.UseOpenApi();
            //app.UseSwaggerUi();

            //app.MapControllers();
            app.Run();
        }

        //public class UnitAttribute : Attribute, ITrackablePropertyInitializer
        //{
        //    private readonly string _unit;

        //    public UnitAttribute(string unit)
        //    {
        //        _unit = unit;
        //    }

        //    public void InitializeProperty(TrackedProperty property, object? parentCollectionKey, ITrackableContext context)
        //    {
        //        property.Parent.AddProperty(
        //            TrackedProperty<string>.CreateAttribute(property, "Unit", _unit, context));
        //    }
        //}

        //[OpenApiTag("Car")]
        //[Route("/api/car")]
        //public class TrackablesController<TTrackable> : TrackablesControllerBase<TTrackable>
        //    where TTrackable : class
        //{
        //    public TrackablesController(TrackableContext<TTrackable> trackableContext)
        //        : base(trackableContext)
        //    {
        //    }
        //}

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
                    _car.Tires[3].Pressure = Random.Shared.Next(0, 40) / 10m;

                    await Task.Delay(1000, stoppingToken);
                }
            }
        }
    }
}