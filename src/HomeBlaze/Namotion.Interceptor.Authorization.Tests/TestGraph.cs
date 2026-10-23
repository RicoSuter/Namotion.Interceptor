using Namotion.Interceptor.Authorization.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Authorization.Tests;

/// <summary>
/// The graph of every policy test: Home with Rooms[Kitchen] (Lights[Ceiling], Lights[Counter], Sensor),
/// Rooms[Bedroom] (Lights[Lamp]) and Wifi.
/// </summary>
internal static class TestGraph
{
    public static Home Create()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var home = new Home(context);

        var kitchen = new Room { Sensor = new Sensor() };
        kitchen.Lights = new Dictionary<string, Light> { ["Ceiling"] = new Light(), ["Counter"] = new Light() };

        var bedroom = new Room();
        bedroom.Lights = new Dictionary<string, Light> { ["Lamp"] = new Light() };

        home.Rooms = new Dictionary<string, Room> { ["Kitchen"] = kitchen, ["Bedroom"] = bedroom };
        home.Wifi = new Wifi();
        return home;
    }

    public static RegisteredSubject Registered(IInterceptorSubject subject)
        => subject.TryGetRegisteredSubject() ?? throw new InvalidOperationException($"{subject.GetType().Name} is not registered.");

    public static RegisteredSubjectProperty Member(IInterceptorSubject subject, string name)
        => Registered(subject).TryGetProperty(name) ?? throw new InvalidOperationException($"{subject.GetType().Name} has no member {name}.");

    public static RegisteredSubjectProperty Attribute(IInterceptorSubject subject, string propertyName, string attributeName)
        => Member(subject, propertyName).TryGetAttribute(attributeName) ?? throw new InvalidOperationException($"{propertyName} has no attribute {attributeName}.");
}
