using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.SampleClient;
using Namotion.Interceptor.OpcUa.SampleModel;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Opc.Ua;

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithParents()
    .WithLifecycle()
    .WithDataAnnotationValidation()
    .WithHostedServices(builder.Services);

Utils.SetTraceMask(Utils.TraceMasks.All);

builder.Services.AddSingleton(new Root(context));
builder.Services.AddOpcUaSubjectClient<Root>("opc.tcp://localhost:4840", "opc", rootName: "Root");
builder.Services.AddHostedService<Worker>();

context.GetPropertyChangedObservable().Subscribe(x =>
{
    if (x.Property.Name == "FirstName")
    {
        var y = DateTimeOffset.Parse(x.NewValue?.ToString() ?? "");
        var z = DateTimeOffset.Now - y;
        Console.WriteLine($"DIFF {z.TotalMilliseconds} ms");
    }
});

var host = builder.Build();
host.Run();