using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.SamplesModel;
using Namotion.Interceptor.SamplesModel.Workers;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;

const int personCount = 10_000;

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithParents()
    .WithLifecycle()
    .WithDataAnnotationValidation()
    .WithHostedServices(builder.Services);

var root = Root.CreateWithPersons(context, personCount);
context.AddService(root);

builder.Services.AddSingleton(root);
builder.Services.AddHostedService<ServerWorker>();
builder.Services.AddMqttSubjectServer(
    _ => root,
    _ => new MqttServerConfiguration
    {
        BrokerHost = "localhost",
        BrokerPort = 1883,
        PathProvider = new AttributeBasedSourcePathProvider("mqtt", "/")
    });

using var performanceProfiler = new PerformanceProfiler(context, "Server");
var host = builder.Build();
host.Run();
