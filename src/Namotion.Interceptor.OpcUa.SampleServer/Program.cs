using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.SampleModel;
using Namotion.Interceptor.OpcUa.SampleServer;
using Namotion.Interceptor.Registry;
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

var root = new Root(context)
{
    Person = new Person
    {
        FirstName = "John",
        LastName = "Doe"
    }
};

builder.Services.AddSingleton(root);
builder.Services.AddOpcUaSubjectServer<Root>("opc", rootName: "Root");
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();