using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// Evaluates a policy of about 30 rules against a graph of 50 floors with 40 lamps each (2050 subjects), the shape a
/// browse of a building would produce.
/// </summary>
[MemoryDiagnoser]
public class AuthorizationBenchmark
{
    private static readonly PermissionAction Execute = new("execute");

    private Building _building;
    private AuthorizationPolicy _policy;
    private RegisteredSubjectProperty _lampBrightness;
    private RegisteredSubjectProperty _restrictedLampBrightness;
    private RegisteredSubject[] _subjects;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        _building = new Building(context);
        var floors = new Dictionary<string, Floor>();
        for (var floor = 0; floor < 50; floor++)
        {
            var lamps = new Dictionary<string, Lamp>();
            for (var lamp = 0; lamp < 40; lamp++)
            {
                lamps[$"Lamp{lamp}"] = new Lamp();
            }

            floors[$"Floor{floor}"] = new Floor { Lamps = lamps };
        }

        _building.Floors = floors;

        var allow = new List<PermissionRule>
        {
            new() { Tags = ["state"], Actions = [PermissionAction.Read] },
            new() { Tags = ["subjects"], Actions = [PermissionAction.Read] },
            new() { Types = ["ILamp"], Members = ["Brightness", "IsOn"], Actions = [PermissionAction.Write, Execute] }
        };
        var deny = new List<PermissionRule>();
        for (var i = 0; i < 10; i++)
        {
            allow.Add(new PermissionRule { Paths = [$"/Floors[Floor{i}]"], Members = ["Set*"], Actions = [Execute] });
            deny.Add(new PermissionRule { Paths = [$"/Floors[Floor{40 + i}]"], Actions = [PermissionAction.Write, Execute] });
        }

        deny.Add(new PermissionRule { Tags = ["secret"], Actions = [PermissionAction.Read, PermissionAction.Write] });

        var role = new AuthorizationRole { Name = "Operator", Allow = [.. allow], Deny = [.. deny] };
        _policy = AuthorizationPolicy.Compile([role], ["Operator"], [], [PermissionAction.Read, PermissionAction.Write, Execute], new TagProvider(), SlashPathProvider.Instance);

        _lampBrightness = Member(_building.Floors["Floor5"].Lamps["Lamp3"], nameof(Lamp.Brightness));
        _restrictedLampBrightness = Member(_building.Floors["Floor45"].Lamps["Lamp3"], nameof(Lamp.Brightness));

        var subjects = new List<RegisteredSubject> { _building.TryGetRegisteredSubject()! };
        foreach (var floor in _building.Floors.Values)
        {
            subjects.Add(floor.TryGetRegisteredSubject()!);
            foreach (var lamp in floor.Lamps.Values)
            {
                subjects.Add(lamp.TryGetRegisteredSubject()!);
            }
        }

        _subjects = subjects.ToArray();
    }

    [Benchmark(Baseline = true)]
    public bool ReadByTag() => _policy.CanRead(_lampBrightness);

    /// <summary>The deny rules have only path selectors, so the subject's paths are enumerated to prove none matches; this is the cost of path-only deny rules.</summary>
    [Benchmark]
    public bool WriteNotUnderDeniedPath() => _policy.CanWrite(_lampBrightness);

    /// <summary>Stops at the first matching path deny rule.</summary>
    [Benchmark]
    public bool WriteDeniedByPath() => _policy.CanWrite(_restrictedLampBrightness);

    /// <summary>Reachability through readable links; no path rule applies to read, so nothing is enumerated.</summary>
    [Benchmark]
    public bool CanReadSubject() => _policy.CanRead(_lampBrightness.Parent);

    [Benchmark]
    public int BrowseShapedEvaluation()
    {
        var readable = 0;
        foreach (var subject in _subjects)
        {
            if (!_policy.CanRead(subject))
            {
                continue;
            }

            foreach (var member in subject.Properties)
            {
                if (_policy.CanRead(member))
                {
                    readable++;
                }
            }
        }

        return readable;
    }

    private static RegisteredSubjectProperty Member(IInterceptorSubject subject, string name)
        => subject.TryGetRegisteredSubject()!.TryGetProperty(name) ?? throw new InvalidOperationException(name);

    private sealed class TagProvider : IMemberTagProvider
    {
        private static readonly string[] State = ["state"];
        private static readonly string[] Subjects = ["subjects"];
        private static readonly string[] Secret = ["state", "secret"];

        public IReadOnlyCollection<string> GetTags(RegisteredSubjectProperty member)
        {
            if (member.CanContainSubjects)
            {
                return Subjects;
            }

            return member.Name == nameof(Lamp.Secret) ? Secret : State;
        }
    }

    private sealed class SlashPathProvider : PathProviderBase
    {
        public static readonly SlashPathProvider Instance = new();

        public override char PathSeparator => '/';
    }
}

public interface ILamp
{
    bool IsOn { get; set; }

    int Brightness { get; set; }
}

[InterceptorSubject]
public partial class Building
{
    public Building()
    {
        Floors = [];
    }

    public partial Dictionary<string, Floor> Floors { get; set; }
}

[InterceptorSubject]
public partial class Floor
{
    public Floor()
    {
        Lamps = [];
    }

    public partial Dictionary<string, Lamp> Lamps { get; set; }
}

[InterceptorSubject]
public partial class Lamp : ILamp
{
    public partial bool IsOn { get; set; }

    public partial int Brightness { get; set; }

    public partial string? Secret { get; set; }
}
