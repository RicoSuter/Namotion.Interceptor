using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet.Protocol;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Mqtt;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.SamplesModel;
using Namotion.Interceptor.SamplesModel.Workers;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithParents()
    .WithLifecycle()
    .WithDataAnnotationValidation()
    .WithHostedServices(builder.Services);

var root = Root.CreateWithPersons(context);
context.AddService(root);

builder.Services.AddSingleton(root);
builder.Services.AddHostedService<ClientWorker>();
builder.Services.AddMqttSubjectClientSource(
    _ => root,
    _ => new MqttClientConfiguration
    {
        BrokerHost = "localhost",
        BrokerPort = 1883,
        PathProvider = new AttributeBasedSourcePathProvider("mqtt", "/", null),
        DefaultQualityOfService = MqttQualityOfServiceLevel.AtLeastOnce,
        UseRetainedMessages = true
    });

using var performanceProfiler = new PerformanceProfiler(context, "Client");
var host = builder.Build();
host.Run();
