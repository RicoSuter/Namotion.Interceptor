using Microsoft.AspNetCore.Mvc;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change.Attributes;
using Namotion.Interceptor.Validation;
using Namotion.Proxy.AspNetCore.Controllers;
using Namotion.Proxy.Sources.Attributes;
using NSwag.Annotations;

namespace Namotion.Interceptor.SampleWeb
{
    [InterceptorSubject]
    public partial class Car
    {
        [SourceName("mqtt", "name")]
        [SourceName("opc", "Name")]
        public partial string Name { get; set; }

        [SourcePath("mqtt", "tires")]
        [SourcePath("opc", "Tires")]
        public partial Tire[] Tires { get; set; }

        [Derived]
        [SourceName("mqtt", "averagePressure")]
        [SourceName("opc", "AveragePressure")]
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
    public partial class Tire
    {
        [SourceName("mqtt", "pressure")]
        [SourceName("opc", "Pressure")]
        [Unit("bar")]
        public partial decimal Pressure { get; set; }

        [Unit("bar")]
        [PropertyAttribute(nameof(Pressure), "Minimum")]
        public partial decimal Pressure_Minimum { get; set; }

        [Derived]
        [PropertyAttribute(nameof(Pressure), "Maximum")]
        public decimal Pressure_Maximum => 4 * Pressure;

        public Tire()
        {
            Pressure_Minimum = 0.0m;
        }
    }

    public class UnitAttribute : Attribute, ISubjectPropertyInitializer
    {
        private readonly string _unit;

        public UnitAttribute(string unit)
        {
            _unit = unit;
        }

        public void InitializeProperty(RegisteredSubjectProperty property, object? index)
        {
            property.AddAttribute("Unit", typeof(string), () => _unit, null);
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

            var car = new Car(context);

            // trackable
            builder.Services.AddSingleton(car);
            builder.Services.AddSingleton(context);

            // trackable api controllers
            builder.Services.AddProxyControllers<Car, ProxyController<Car>>();

            // trackable UPC UA
            builder.Services.AddOpcUaServerProxy<Car>("opc", rootName: "Root");

            // trackable mqtt
            builder.Services.AddMqttServerProxySource<Car>("mqtt");

            // trackable GraphQL
            builder.Services
                .AddGraphQLServer()
                .AddInMemorySubscriptions()
                .AddGraphQLProxy<Car>();

            // other asp services
            builder.Services.AddOpenApiDocument();
            builder.Services.AddAuthorization();

            builder.Services.AddHostedService<Simulator>();

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
        public class ProxyController<TProxy> : ProxyControllerBase<TProxy> where TProxy : IInterceptorSubject
        {
            public ProxyController(TProxy subject) : base(subject)
            {
            }
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