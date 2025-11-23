using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.SampleServer;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.SamplesModel;
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

var root = Root.CreateWithPersons(context, 10_000);
context.AddService(root);

builder.Services.AddSingleton(root);
builder.Services.AddOpcUaSubjectServer<Root>("opc", rootName: "Root");
builder.Services.AddHostedService<Worker>();

using var performanceProfiler = new PerformanceProfiler(context, "Server");
var host = builder.Build();
host.Run();