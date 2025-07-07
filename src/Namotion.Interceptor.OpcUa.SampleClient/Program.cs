using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.SampleClient;
using Namotion.Interceptor.OpcUa.SampleModel;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Parent;
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
        var laterTimestamp = Stopwatch.GetTimestamp();
        var beforeTimestamp = long.Parse(x.NewValue?.ToString() ?? "0");

        var ticksElapsed = laterTimestamp - beforeTimestamp;
        var secondsElapsed = (double)ticksElapsed / Stopwatch.Frequency;

        Console.WriteLine(x.Property.Subject.GetParents().First().Index + $": Elapsed time: {secondsElapsed * 1000} ms");
    }
});

var host = builder.Build();
host.Run();