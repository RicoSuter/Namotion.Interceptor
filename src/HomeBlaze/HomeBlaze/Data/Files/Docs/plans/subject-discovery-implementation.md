---
title: Subject Discovery Implementation Plan
navTitle: Discovery Plan
status: Planned
---

# Subject Discovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Discover page in the create dialog that scans the local network, lists the devices found per subject type, and hands a picked device to that type's Setup or Edit component, for FRITZ!Box, Hue, Nuki and Shelly.

**Architecture:** A typed `SubjectDiscoverer<TSubject, TResult>` per subject type (in the device library) scans through shared SSDP and mDNS services that a `DiscoveryScan` session shares between discoverers. `SubjectDiscovererRegistry` finds discoverers among the loaded types, `SubjectDiscoveryService` runs them in parallel with a 30 second cap, and `SubjectDiscoveryPage` shows the results. A picked result reaches the component through `SubjectEditPanel` when the component implements `ISubjectDiscoveryComponent`.

**Tech Stack:** .NET 10, C# 14, Blazor with MudBlazor 9, xUnit, Playwright, Rssdp (SSDP), an own minimal mDNS client.

**Spec:** [Subject Discovery](subject-discovery.md). Read it first.

**Conventions for every task** (from `AGENTS.md`):

- Tests are named `When<Condition>_Then<ExpectedBehavior>` and use `// Arrange`, `// Act`, `// Assert` comments (`// Act & Assert` for exception tests).
- No `Task.Delay` or `Thread.Sleep` in tests; use `FakeTimeProvider`, `TaskCompletionSource` or `AsyncTestHelpers.WaitUntilAsync`.
- US English, no abbreviations in names, no em dashes in docs, no AI attribution in commits.
- Warnings are errors. An event that is never raised needs explicit empty `add { } remove { }` accessors.
- Unit tests: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`.

---

## File structure

**HomeBlaze.Abstractions/Discovery/** (contract, no sockets)
- `IDiscoveryResult.cs`: what a found device shows
- `DiscoveredSubject.cs`: a found device with its subject type and matching subject
- `ISubjectDiscoverer.cs`: the non-generic contract the registry uses
- `SubjectDiscoverer.cs`: the typed base class discoverers derive from
- `DiscoveryScan.cs`: one scan session with shared services
- `SharedQueries.cs`: per-session sharing of in-flight queries (internal)
- `ISsdpSearch.cs`, `SsdpResponse.cs`: SSDP contract
- `IMulticastDnsBrowser.cs`, `MulticastDnsService.cs`: mDNS contract

**HomeBlaze.Components.Abstractions/**
- `ISubjectDiscoveryComponent.cs`: opt-in for Setup and Edit components

**HomeBlaze.Services/Discovery/**
- `MulticastDns/DnsRecord.cs`, `MulticastDns/DnsMessage.cs`: DNS message codec
- `MulticastDns/MulticastDnsServiceCollector.cs`: combines records into services
- `MulticastDns/MulticastDnsBrowser.cs`: sockets
- `NetworkAdapters.cs`: IPv4 adapters that support multicast
- `SsdpSearch.cs`: Rssdp wrapper
- `SubjectDiscovererRegistry.cs`: finds and creates discoverers
- `SubjectDiscoveryService.cs`: runs a scan

**HomeBlaze.Components/**
- `Dialogs/SubjectDiscoveryList.cs`: the page's ordered rows and failures
- `Dialogs/SubjectDiscoveryPage.razor`: the Discover page
- Modify `Dialogs/SubjectSetupDialog.razor`, `Dialogs/SubjectNameOptions.cs`, `Editors/SubjectEditPanel.razor`

**Device libraries**
- FRITZ!Box: `FritzBoxDiscovery.cs` (modify), `FritzBoxDiscoverer.cs`, `FritzBox.cs` (`Udn`), `FritzBoxSetupComponent.razor`
- Hue: `HueDiscoveredBridge.cs`, `HueBridgeDiscoverer.cs`, `HueBridge.cs` (case-insensitive ID), `HueBridgeSetupComponent.razor`
- Nuki: `NukiDiscoveredBridge.cs` (modify), `NukiBridgeDiscoverer.cs`, `NukiBridgeSetupComponent.razor`
- Shelly: `ShellyDiscoveredDevice.cs`, `ShellyDeviceDiscoverer.cs`, `ShellyDevice.cs` (`DeviceId`), `ShellyDeviceEditComponent.razor`

**Tests**
- `HomeBlaze.Services.Tests/Discovery/*`: contract, codec, collector, SSDP mapping, registry, scan service
- `HomeBlaze.Components.Tests`: `SubjectDiscoveryListTests.cs`, additions to `SubjectNameOptionsTests.cs` and `SubjectEditPanelParametersTests.cs`
- Device test projects: one `*DiscovererTests.cs` each
- `HomeBlaze.E2E.Tests/Models/Discoverable*.cs` and one test in `SubjectSetupDialogTests.cs`

---

### Task 1: Align the spec with the plan

Writing the plan surfaced four simplifications. Record them in the spec so spec and code agree.

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/plans/subject-discovery.md`

- [ ] **Step 1: Apply the edits**

In the "Scan session and shared services" section, replace the bullet

`- `SsdpResponse` carries the location, unique service name, search target and headers.`

with

`- `SsdpResponse` carries the location, unique service name and search target of an answer for exactly the searched target.`

In the "Discover page" section, replace the bullet that starts with "When every multicast query failed because no adapter supports multicast" with

`- The page always shows a hint that a container on a bridge network receives no multicast, so devices there are added by address or HomeBlaze runs with host networking. A container still has an adapter that claims multicast support, so the case cannot be detected reliably.`

In the "Error handling" section, replace "it is logged with the discoverer's type and shown as one line on the page, such as "Nuki: lookup failed"" with "it is logged with the discoverer's type and shown as one line on the page, such as "Nuki Bridge: search failed"", and replace the bullet "A `Matches` call that throws leaves that result out and is logged like a discoverer failure." with "A `Matches` call that throws ends its discoverer like any other exception."

In the "v1 discoverers" section:

- In the Hue row, append to the Matches cell: "; `HueBridge` itself compares bridge IDs ignoring case as well".
- In the Nuki row, replace the Matches cell with "The host address and port. The bridge ID the discovery service returns is compared with the runtime `ServerId` of a real bridge during manual verification, and a match on it is added only if they agree".
- Replace the bullet that starts with "`FritzBox.Udn` and `ShellyDevice.DeviceId` are new" with "`FritzBox.Udn` and `ShellyDevice.DeviceId` are new `[Configuration]` properties, written by the component that receives the result. Shelly also matches on the device ID the running subject read from the device, so a Shelly created before this change is recognized while it is connected. Subjects do not write their own configuration for discovery."

In the "Testing" section, replace the paragraph that starts with "Component tests for the Discover page" with "This repository has no component test framework, so the Discover page keeps its logic in `SubjectDiscoveryList` (ordering, failures), `SubjectNameOptions.SuggestName` (the name) and `SubjectEditPanel.CreateHostedComponentParameters` (passing the result), which unit tests cover. The E2E test covers the page itself."

In the "Documentation" section, replace "The `create-homeblaze-library` skill describes writing a discoverer." with "The `create-homeblaze-library` command (`.claude/commands/create-homeblaze-library.md`) describes writing a discoverer."

- [ ] **Step 2: Commit**

```bash
git add src/HomeBlaze/HomeBlaze/Data/Files/Docs/plans/subject-discovery.md
git commit -m "docs: align the subject discovery plan with its implementation plan"
```

---

### Task 2: Discovery contract in HomeBlaze.Abstractions

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Discovery/IDiscoveryResult.cs`
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Discovery/DiscoveredSubject.cs`
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Discovery/ISubjectDiscoverer.cs`
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Discovery/SubjectDiscoverer.cs`
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Discovery/DiscoveryScan.cs`
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Discovery/SharedQueries.cs`
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Discovery/ISsdpSearch.cs`
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Discovery/SsdpResponse.cs`
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Discovery/IMulticastDnsBrowser.cs`
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Discovery/MulticastDnsService.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services.Tests/Discovery/DiscoveryTestDoubles.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services.Tests/Discovery/SubjectDiscovererTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services.Tests/Discovery/DiscoveryScanTests.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Abstractions.Tests/VerifyChecksTests.PublicApi.verified.txt`

The tests live in `HomeBlaze.Services.Tests` because they need generated subjects, and only that project references the generator and `HomeBlaze.Abstractions` together.

- [ ] **Step 1: Write the contract types**

`IDiscoveryResult.cs`:

```csharp
namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// A device that a <see cref="SubjectDiscoverer{TSubject, TResult}"/> found, with what the user is shown for it.
/// </summary>
public interface IDiscoveryResult
{
    /// <summary>
    /// Identifies the device within the discoverer that found it, the same in every scan.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// The name the user is shown for the device.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// A second line for the user, such as the host address, or null.
    /// </summary>
    string? Detail { get; }
}
```

`DiscoveredSubject.cs`:

```csharp
using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// A found device, with the subject type that represents it.
/// </summary>
/// <param name="SubjectType">The type of subject to create for the device.</param>
/// <param name="Result">The device, as its discoverer describes it.</param>
/// <param name="ExistingSubject">The subject that already represents the device by the rules of its discoverer, or null.</param>
public sealed record DiscoveredSubject(Type SubjectType, IDiscoveryResult Result, IInterceptorSubject? ExistingSubject);
```

`ISubjectDiscoverer.cs`:

```csharp
using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// Finds devices of one subject type. Derive from <see cref="SubjectDiscoverer{TSubject, TResult}"/> instead of
/// implementing this interface.
/// </summary>
public interface ISubjectDiscoverer
{
    /// <summary>
    /// The type of subject that represents the devices this discoverer finds.
    /// </summary>
    Type SubjectType { get; }

    /// <summary>
    /// Scans and yields each device once, as soon as it is known, with the subject of
    /// <paramref name="existingSubjects"/> that already represents it.
    /// </summary>
    IAsyncEnumerable<DiscoveredSubject> DiscoverAsync(
        DiscoveryScan scan, IReadOnlyCollection<IInterceptorSubject> existingSubjects, CancellationToken cancellationToken);
}
```

`SubjectDiscoverer.cs`:

```csharp
using System.Runtime.CompilerServices;
using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// Finds devices that a <typeparamref name="TSubject"/> represents. HomeBlaze finds every non-abstract derived type
/// among the loaded types and creates it through dependency injection.
/// </summary>
/// <remarks>
/// A discoverer only reads the network and creates no subject. The create dialog passes the result the user picks to
/// the Setup component of <typeparamref name="TSubject"/>, or its Edit component when it has none, which must
/// implement <c>ISubjectDiscoveryComponent</c>; otherwise HomeBlaze does not use the discoverer. One instance serves
/// overlapping scans, so it keeps no state between calls.
/// </remarks>
/// <typeparam name="TSubject">The type of subject that represents a found device.</typeparam>
/// <typeparam name="TResult">The type of result that describes a found device.</typeparam>
public abstract class SubjectDiscoverer<TSubject, TResult> : ISubjectDiscoverer
    where TSubject : class, IInterceptorSubject
    where TResult : class, IDiscoveryResult
{
    Type ISubjectDiscoverer.SubjectType => typeof(TSubject);

    async IAsyncEnumerable<DiscoveredSubject> ISubjectDiscoverer.DiscoverAsync(
        DiscoveryScan scan,
        IReadOnlyCollection<IInterceptorSubject> existingSubjects,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var candidates = existingSubjects.OfType<TSubject>().ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var result in DiscoverAsync(scan, cancellationToken).WithCancellation(cancellationToken))
        {
            // A device that answers on several adapters, or to several queries, is listed once.
            if (!keys.Add(result.Key))
            {
                continue;
            }

            TSubject? existingSubject = null;
            foreach (var candidate in candidates)
            {
                if (Matches(candidate, result))
                {
                    existingSubject = candidate;
                    break;
                }
            }

            yield return new DiscoveredSubject(typeof(TSubject), result, existingSubject);
        }
    }

    /// <summary>
    /// Scans and yields each device as soon as it is known. A device may be yielded more than once.
    /// </summary>
    /// <param name="scan">The scan, whose shared services answer each query once.</param>
    /// <param name="cancellationToken">Canceled when the user leaves the page or the scan reaches its time limit.</param>
    protected abstract IAsyncEnumerable<TResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="existing"/> already represents the device of <paramref name="result"/>.
    /// </summary>
    protected abstract bool Matches(TSubject existing, TResult result);
}
```

`SharedQueries.cs`:

```csharp
using System.Collections.Concurrent;

namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// Runs each distinct question once and gives every caller that asks it the same answer.
/// </summary>
internal sealed class SharedQueries<TAnswer>
{
    private readonly Func<string, CancellationToken, Task<TAnswer>> _query;
    private readonly CancellationToken _scanCancellationToken;
    private readonly ConcurrentDictionary<string, Lazy<Task<TAnswer>>> _answers = new(StringComparer.OrdinalIgnoreCase);

    public SharedQueries(Func<string, CancellationToken, Task<TAnswer>> query, CancellationToken scanCancellationToken)
    {
        _query = query;
        _scanCancellationToken = scanCancellationToken;
    }

    public Task<TAnswer> GetAsync(string question, CancellationToken cancellationToken)
    {
        // The query runs with the token of the scan: a caller that stops waiting must not cancel it for the others.
        var answer = _answers.GetOrAdd(
            question,
            static (key, queries) => new Lazy<Task<TAnswer>>(() => queries._query(key, queries._scanCancellationToken)),
            this);

        return answer.Value.WaitAsync(cancellationToken);
    }
}
```

`DiscoveryScan.cs`:

```csharp
namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// One scan for devices. Its services send each distinct query once and give every discoverer that asks the same
/// question the same answer.
/// </summary>
public sealed class DiscoveryScan
{
    /// <param name="ssdpSearch">The SSDP search to share.</param>
    /// <param name="multicastDnsBrowser">The mDNS browser to share.</param>
    /// <param name="cancellationToken">Ends the shared queries; canceled when the scan ends.</param>
    public DiscoveryScan(ISsdpSearch ssdpSearch, IMulticastDnsBrowser multicastDnsBrowser, CancellationToken cancellationToken)
    {
        Ssdp = new SharedSsdpSearch(new SharedQueries<IReadOnlyList<SsdpResponse>>(ssdpSearch.SearchAsync, cancellationToken));
        MulticastDns = new SharedMulticastDnsBrowser(
            new SharedQueries<IReadOnlyList<MulticastDnsService>>(multicastDnsBrowser.BrowseAsync, cancellationToken));
    }

    /// <summary>
    /// Searches over SSDP, once per search target and scan.
    /// </summary>
    public ISsdpSearch Ssdp { get; }

    /// <summary>
    /// Browses over mDNS, once per service type and scan.
    /// </summary>
    public IMulticastDnsBrowser MulticastDns { get; }

    private sealed class SharedSsdpSearch(SharedQueries<IReadOnlyList<SsdpResponse>> queries) : ISsdpSearch
    {
        public Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken) =>
            queries.GetAsync(searchTarget, cancellationToken);
    }

    private sealed class SharedMulticastDnsBrowser(SharedQueries<IReadOnlyList<MulticastDnsService>> queries) : IMulticastDnsBrowser
    {
        public Task<IReadOnlyList<MulticastDnsService>> BrowseAsync(string serviceType, CancellationToken cancellationToken) =>
            queries.GetAsync(serviceType, cancellationToken);
    }
}
```

`ISsdpSearch.cs`:

```csharp
namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// Searches the local network over SSDP.
/// </summary>
public interface ISsdpSearch
{
    /// <summary>
    /// Searches every IPv4 adapter for <paramref name="searchTarget"/> and returns the answers for exactly that target
    /// that arrive within a few seconds. Returns no answers instead of throwing when no adapter can search.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken);
}
```

`SsdpResponse.cs`:

```csharp
namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// An answer to an SSDP search.
/// </summary>
/// <param name="Location">The absolute address of the device description.</param>
/// <param name="UniqueServiceName">The USN of the answer, or null.</param>
/// <param name="SearchTarget">The search target the device answered for.</param>
public sealed record SsdpResponse(Uri Location, string? UniqueServiceName, string SearchTarget);
```

`IMulticastDnsBrowser.cs`:

```csharp
namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// Browses the local network over mDNS.
/// </summary>
public interface IMulticastDnsBrowser
{
    /// <summary>
    /// Asks every IPv4 adapter for the instances of <paramref name="serviceType"/>, such as <c>_hue._tcp</c>, and
    /// returns those that answer within a few seconds. Returns no instances instead of throwing when no adapter can
    /// ask.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task<IReadOnlyList<MulticastDnsService>> BrowseAsync(string serviceType, CancellationToken cancellationToken);
}
```

`MulticastDnsService.cs`:

```csharp
using System.Net;

namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// An instance of a service type that answered an mDNS query.
/// </summary>
/// <param name="InstanceName">The instance label, such as <c>shellyplus1pm-a8032ab12345</c>, without the service type.</param>
/// <param name="HostName">The target of its SRV record, such as <c>shellyplus1pm-a8032ab12345.local</c>, or null.</param>
/// <param name="Port">The port of its SRV record, or 0.</param>
/// <param name="Addresses">Its IPv4 addresses, or the address its answer came from when it named none; never empty.</param>
/// <param name="Properties">Its TXT entries by key, ignoring case; a key without a value maps to an empty string.</param>
public sealed record MulticastDnsService(
    string InstanceName,
    string? HostName,
    int Port,
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyDictionary<string, string> Properties);
```

- [ ] **Step 2: Write the test doubles**

`src/HomeBlaze/HomeBlaze.Services.Tests/Discovery/DiscoveryTestDoubles.cs`:

```csharp
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Discovery;

[InterceptorSubject]
public partial class DiscoveryTestSubject
{
    public partial string? Address { get; set; }
}

[InterceptorSubject]
public partial class OtherDiscoveryTestSubject
{
    public partial string? Address { get; set; }
}

public sealed record TestDiscoveryResult(string Key, string DisplayName = "Device", string? Detail = null) : IDiscoveryResult;

/// <summary>
/// A discoverer whose scan and matching the test passes in.
/// </summary>
internal sealed class LambdaDiscoverer<TSubject> : SubjectDiscoverer<TSubject, TestDiscoveryResult>
    where TSubject : class, IInterceptorSubject
{
    private readonly Func<DiscoveryScan, CancellationToken, IAsyncEnumerable<TestDiscoveryResult>> _scan;
    private readonly Func<TSubject, TestDiscoveryResult, bool> _matches;

    public LambdaDiscoverer(
        Func<DiscoveryScan, CancellationToken, IAsyncEnumerable<TestDiscoveryResult>> scan,
        Func<TSubject, TestDiscoveryResult, bool>? matches = null)
    {
        _scan = scan;
        _matches = matches ?? ((_, _) => false);
    }

    protected override IAsyncEnumerable<TestDiscoveryResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken) =>
        _scan(scan, cancellationToken);

    protected override bool Matches(TSubject existing, TestDiscoveryResult result) => _matches(existing, result);
}

internal static class TestScans
{
    public static async IAsyncEnumerable<TestDiscoveryResult> Yield(params TestDiscoveryResult[] results)
    {
        foreach (var result in results)
        {
            await Task.Yield();
            yield return result;
        }
    }

    public static async IAsyncEnumerable<TestDiscoveryResult> YieldThenThrow(TestDiscoveryResult result)
    {
        await Task.Yield();
        yield return result;
        throw new InvalidOperationException("The search failed.");
    }

    public static async IAsyncEnumerable<TestDiscoveryResult> Hang([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    public static DiscoveryScan CreateScan() => new(new FakeSsdpSearch(), new FakeMulticastDnsBrowser(), CancellationToken.None);
}

internal sealed class FakeSsdpSearch : ISsdpSearch
{
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult<IReadOnlyList<SsdpResponse>>([]);
    }
}

/// <summary>
/// Answers every browse with the services passed to <see cref="Answer"/>, once the test calls it.
/// </summary>
internal sealed class FakeMulticastDnsBrowser : IMulticastDnsBrowser
{
    private readonly TaskCompletionSource<IReadOnlyList<MulticastDnsService>> _answer =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public void Answer(params MulticastDnsService[] services) => _answer.TrySetResult(services);

    public Task<IReadOnlyList<MulticastDnsService>> BrowseAsync(string serviceType, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return _answer.Task;
    }
}
```

- [ ] **Step 3: Write the failing tests**

`SubjectDiscovererTests.cs`:

```csharp
using HomeBlaze.Abstractions.Discovery;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class SubjectDiscovererTests
{
    [Fact]
    public async Task WhenAResultMatchesAnExistingSubject_ThenItCarriesThatSubject()
    {
        // Arrange
        var existing = new DiscoveryTestSubject { Address = "a" };
        var otherType = new OtherDiscoveryTestSubject { Address = "b" };
        ISubjectDiscoverer discoverer = new LambdaDiscoverer<DiscoveryTestSubject>(
            (_, _) => TestScans.Yield(new TestDiscoveryResult("a"), new TestDiscoveryResult("b")),
            (subject, result) => subject.Address == result.Key);

        // Act
        var discovered = await discoverer
            .DiscoverAsync(TestScans.CreateScan(), [otherType, existing], CancellationToken.None)
            .ToListAsync();

        // Assert
        Assert.Equal(2, discovered.Count);
        Assert.Same(existing, discovered[0].ExistingSubject);
        Assert.Null(discovered[1].ExistingSubject);
        Assert.All(discovered, item => Assert.Equal(typeof(DiscoveryTestSubject), item.SubjectType));
    }

    [Fact]
    public async Task WhenADeviceIsYieldedTwice_ThenItIsReportedOnce()
    {
        // Arrange
        ISubjectDiscoverer discoverer = new LambdaDiscoverer<DiscoveryTestSubject>(
            (_, _) => TestScans.Yield(new TestDiscoveryResult("a"), new TestDiscoveryResult("a", "Same device again")));

        // Act
        var discovered = await discoverer
            .DiscoverAsync(TestScans.CreateScan(), [], CancellationToken.None)
            .ToListAsync();

        // Assert
        var single = Assert.Single(discovered);
        Assert.Equal("Device", single.Result.DisplayName);
    }
}
```

`DiscoveryScanTests.cs`:

```csharp
using System.Net;
using HomeBlaze.Abstractions.Discovery;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class DiscoveryScanTests
{
    private static readonly MulticastDnsService Bridge = new(
        "Philips Hue - 1A2B3C", "hue.local", 443, [IPAddress.Parse("192.168.1.20")], new Dictionary<string, string>());

    [Fact]
    public async Task WhenTwoDiscoverersBrowseTheSameServiceType_ThenOneQueryIsSent()
    {
        // Arrange
        var browser = new FakeMulticastDnsBrowser();
        var scan = new DiscoveryScan(new FakeSsdpSearch(), browser, CancellationToken.None);

        // Act
        var first = scan.MulticastDns.BrowseAsync("_hue._tcp", CancellationToken.None);
        var second = scan.MulticastDns.BrowseAsync("_HUE._tcp", CancellationToken.None);
        browser.Answer(Bridge);

        // Assert
        Assert.Same(await first, await second);
        Assert.Equal(1, browser.CallCount);
    }

    [Fact]
    public async Task WhenOneCallerStopsWaiting_ThenTheOtherStillGetsTheAnswer()
    {
        // Arrange
        var browser = new FakeMulticastDnsBrowser();
        var scan = new DiscoveryScan(new FakeSsdpSearch(), browser, CancellationToken.None);
        using var stoppedWaiting = new CancellationTokenSource();
        var first = scan.MulticastDns.BrowseAsync("_hue._tcp", stoppedWaiting.Token);
        var second = scan.MulticastDns.BrowseAsync("_hue._tcp", CancellationToken.None);

        // Act
        await stoppedWaiting.CancelAsync();
        browser.Answer(Bridge);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Same(Bridge, Assert.Single(await second));
    }
}
```

- [ ] **Step 4: Run the tests to see them pass**

The contract was written in Step 1, so the tests should pass on the first run. If they don't compile, fix the contract, not the tests.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~Discovery"`
Expected: 4 tests pass.

- [ ] **Step 5: Accept the public API snapshot**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Abstractions.Tests --filter "FullyQualifiedName~PublicApi"`
Expected: FAIL with a diff that adds the `HomeBlaze.Abstractions.Discovery` namespace.

Review the diff: only the new types should appear. Then accept it:

```bash
mv src/HomeBlaze/HomeBlaze.Abstractions.Tests/VerifyChecksTests.PublicApi.received.txt src/HomeBlaze/HomeBlaze.Abstractions.Tests/VerifyChecksTests.PublicApi.verified.txt
dotnet test src/HomeBlaze/HomeBlaze.Abstractions.Tests
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Abstractions src/HomeBlaze/HomeBlaze.Abstractions.Tests src/HomeBlaze/HomeBlaze.Services.Tests/Discovery
git commit -m "feat: add the subject discovery contract"
```

---

### Task 3: ISubjectDiscoveryComponent

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components.Abstractions/ISubjectDiscoveryComponent.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Components.Abstractions/HomeBlaze.Components.Abstractions.csproj`

- [ ] **Step 1: Reference HomeBlaze.Abstractions**

In `HomeBlaze.Components.Abstractions.csproj`, add to the `ItemGroup` with the project references:

```xml
<ProjectReference Include="..\HomeBlaze.Abstractions\HomeBlaze.Abstractions.csproj" />
```

`HomeBlaze.Abstractions` references only `Namotion.Interceptor`, so this creates no cycle.

- [ ] **Step 2: Write the interface**

```csharp
using HomeBlaze.Abstractions.Discovery;

namespace HomeBlaze.Components.Abstractions;

/// <summary>
/// A Setup or Edit component that takes a device the user picked on the Discover page of the create dialog. A
/// subject discoverer is only used when the component the create dialog shows for its subject type implements this.
/// </summary>
public interface ISubjectDiscoveryComponent
{
    /// <summary>
    /// The result the user picked, which the component casts to the result type of its discoverer, or null when the
    /// type was picked directly. Declare it as a <c>[Parameter]</c>.
    /// </summary>
    IDiscoveryResult? DiscoveryResult { get; set; }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components.Abstractions
git commit -m "feat: let setup and edit components take a discovered device"
```

---

### Task 4: DNS message codec

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Services/Discovery/MulticastDns/DnsRecord.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services/Discovery/MulticastDns/DnsMessage.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services.Tests/Discovery/DnsMessageBuilder.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services.Tests/Discovery/DnsMessageTests.cs`

- [ ] **Step 1: Write the test packet builder**

`DnsMessageBuilder.cs`:

```csharp
using System.Buffers.Binary;
using System.Net;
using System.Text;
using HomeBlaze.Services.Discovery.MulticastDns;

namespace HomeBlaze.Services.Tests.Discovery;

/// <summary>
/// Builds mDNS answers the way a responder sends them, without name compression unless a test writes it itself.
/// </summary>
internal sealed class DnsMessageBuilder
{
    private readonly List<byte[]> _records = [];

    public DnsMessageBuilder AddPointer(string name, string target) =>
        AddRecord(EncodeName(name), DnsMessage.PointerType, EncodeName(target));

    public DnsMessageBuilder AddService(string name, ushort port, string target)
    {
        var data = new byte[6].Concat(EncodeName(target)).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), port);
        return AddRecord(EncodeName(name), DnsMessage.ServiceType, data);
    }

    public DnsMessageBuilder AddText(string name, params string[] entries) =>
        AddRecord(EncodeName(name), DnsMessage.TextType, entries.SelectMany(EncodeCharacterString).ToArray());

    public DnsMessageBuilder AddAddress(string name, string address) =>
        AddRecord(EncodeName(name), DnsMessage.AddressType, IPAddress.Parse(address).GetAddressBytes());

    public DnsMessageBuilder AddRecord(byte[] encodedName, ushort type, byte[] data)
    {
        var fixedPart = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart, type);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(fixedPart.AsSpan(4), 120);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(8), (ushort)data.Length);
        _records.Add([.. encodedName, .. fixedPart, .. data]);
        return this;
    }

    public byte[] Build()
    {
        var header = new byte[12];
        header[2] = 0x84; // a response, authoritative
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)_records.Count);
        return [.. header, .. _records.SelectMany(record => record)];
    }

    public static byte[] EncodeName(string name) =>
        [.. name.Split('.').SelectMany(EncodeCharacterString), 0];

    private static IEnumerable<byte> EncodeCharacterString(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return [(byte)bytes.Length, .. bytes];
    }
}
```

`EncodeName` yields the labels followed by the root label; `"a.b"` becomes `1 a 1 b 0`.

- [ ] **Step 2: Write the failing tests**

`DnsMessageTests.cs`:

```csharp
using System.Buffers.Binary;
using System.Net;
using HomeBlaze.Services.Discovery.MulticastDns;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class DnsMessageTests
{
    [Fact]
    public void WhenAQueryIsCreated_ThenItAsksForThePointerRecordsOfTheName()
    {
        // Act
        var query = DnsMessage.CreatePointerQuery("_hue._tcp.local");

        // Assert
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4)));
        Assert.Equal(DnsMessageBuilder.EncodeName("_hue._tcp.local"), query[12..^4]);
        Assert.Equal(DnsMessage.PointerType, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(query.Length - 4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(query.Length - 2)));
    }

    [Fact]
    public void WhenAMessageIsAQuery_ThenItHoldsNoRecords()
    {
        // Act
        var isParsed = DnsMessage.TryParse(DnsMessage.CreatePointerQuery("_hue._tcp.local"), out var records);

        // Assert
        Assert.True(isParsed);
        Assert.Empty(records);
    }

    [Fact]
    public void WhenAnAnswerHoldsPointerServiceTextAndAddressRecords_ThenAllAreRead()
    {
        // Arrange
        var message = new DnsMessageBuilder()
            .AddPointer("_hue._tcp.local", "Bridge._hue._tcp.local")
            .AddService("Bridge._hue._tcp.local", 443, "hue.local")
            .AddText("Bridge._hue._tcp.local", "bridgeid=001788fffe1a2b3c", "modelid=BSB002")
            .AddAddress("hue.local", "192.168.1.20")
            .AddRecord(DnsMessageBuilder.EncodeName("hue.local"), 28, new byte[16]) // AAAA, skipped
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out var records);

        // Assert
        DnsRecord[] expected =
        [
            new PointerRecord("_hue._tcp.local", "Bridge._hue._tcp.local"),
            new ServiceRecord("Bridge._hue._tcp.local", 443, "hue.local"),
            new AddressRecord("hue.local", IPAddress.Parse("192.168.1.20"))
        ];
        Assert.True(isParsed);
        Assert.Equal(expected, records.Where(record => record is not TextRecord));
        var text = Assert.Single(records.OfType<TextRecord>());
        Assert.Equal(["bridgeid=001788fffe1a2b3c", "modelid=BSB002"], text.Entries);
    }

    [Fact]
    public void WhenANameIsCompressed_ThenItIsExpanded()
    {
        // Arrange - the name of the second record points to the name of the first, which starts at offset 12.
        var message = new DnsMessageBuilder()
            .AddPointer("_hue._tcp.local", "Bridge._hue._tcp.local")
            .AddRecord([0xC0, 0x0C], DnsMessage.AddressType, [192, 168, 1, 21])
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out var records);

        // Assert
        Assert.True(isParsed);
        Assert.Equal(new AddressRecord("_hue._tcp.local", IPAddress.Parse("192.168.1.21")), records[1]);
    }

    [Fact]
    public void WhenANamePointsToItself_ThenTheMessageIsRejected()
    {
        // Arrange - the first name, at offset 12, is a pointer to offset 12.
        var message = new DnsMessageBuilder()
            .AddRecord([0xC0, 0x0C], DnsMessage.AddressType, [192, 168, 1, 21])
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out _);

        // Assert
        Assert.False(isParsed);
    }

    [Fact]
    public void WhenTheMessageIsTruncated_ThenItIsRejected()
    {
        // Arrange
        var message = new DnsMessageBuilder()
            .AddService("Bridge._hue._tcp.local", 443, "hue.local")
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message[..^3], out _);

        // Assert
        Assert.False(isParsed);
    }
}
```

- [ ] **Step 3: Run the tests to see them fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~DnsMessageTests"`
Expected: build error, `DnsMessage` does not exist.

- [ ] **Step 4: Implement the codec**

`DnsRecord.cs`:

```csharp
using System.Net;

namespace HomeBlaze.Services.Discovery.MulticastDns;

/// <summary>
/// A record of a DNS answer, with its owner name without the trailing dot.
/// </summary>
internal abstract record DnsRecord(string Name);

internal sealed record PointerRecord(string Name, string Target) : DnsRecord(Name);

internal sealed record ServiceRecord(string Name, int Port, string Target) : DnsRecord(Name);

internal sealed record TextRecord(string Name, IReadOnlyList<string> Entries) : DnsRecord(Name);

internal sealed record AddressRecord(string Name, IPAddress Address) : DnsRecord(Name);
```

`DnsMessage.cs`:

```csharp
using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace HomeBlaze.Services.Discovery.MulticastDns;

/// <summary>
/// Writes mDNS queries and reads the records of mDNS answers (RFC 1035, RFC 6762). Any host on the network can
/// answer, so reading never throws and bounds every loop.
/// </summary>
internal static class DnsMessage
{
    internal const ushort AddressType = 1;
    internal const ushort PointerType = 12;
    internal const ushort TextType = 16;
    internal const ushort ServiceType = 33;

    private const ushort InternetClass = 1;
    private const int HeaderLength = 12;
    private const int MaximumLabelLength = 63;
    private const int MaximumNameLength = 255;
    private const int MaximumPointerJumps = 16;
    private const int MaximumRecords = 256;

    /// <summary>
    /// Creates a query for the PTR records of <paramref name="name"/>, such as <c>_hue._tcp.local</c>.
    /// </summary>
    public static byte[] CreatePointerQuery(string name)
    {
        var labels = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var length = HeaderLength + 1 + 4;
        foreach (var label in labels)
        {
            var labelLength = Encoding.UTF8.GetByteCount(label);
            if (labelLength > MaximumLabelLength)
            {
                throw new ArgumentException($"The label '{label}' is longer than {MaximumLabelLength} bytes.", nameof(name));
            }

            length += 1 + labelLength;
        }

        var message = new byte[length];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), 1);

        var offset = HeaderLength;
        foreach (var label in labels)
        {
            var written = Encoding.UTF8.GetBytes(label, message.AsSpan(offset + 1));
            message[offset] = (byte)written;
            offset += 1 + written;
        }

        // The root label is the zero byte the array already holds.
        offset++;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset), PointerType);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset + 2), InternetClass);
        return message;
    }

    /// <summary>
    /// Reads the PTR, SRV, TXT and A records from every section of an answer and skips records of other types.
    /// </summary>
    /// <returns>False when the message is malformed.</returns>
    public static bool TryParse(ReadOnlySpan<byte> message, out List<DnsRecord> records)
    {
        records = [];
        if (message.Length < HeaderLength)
        {
            return false;
        }

        // Queries of other hosts to the same group carry no answers.
        if ((message[2] & 0x80) == 0)
        {
            return true;
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var recordCount =
            BinaryPrimitives.ReadUInt16BigEndian(message[6..]) +
            BinaryPrimitives.ReadUInt16BigEndian(message[8..]) +
            BinaryPrimitives.ReadUInt16BigEndian(message[10..]);

        var offset = HeaderLength;
        for (var index = 0; index < questionCount; index++)
        {
            if (!TryReadName(message, ref offset, out _) || offset + 4 > message.Length)
            {
                return false;
            }

            offset += 4;
        }

        for (var index = 0; index < recordCount && index < MaximumRecords; index++)
        {
            if (!TryReadName(message, ref offset, out var name) || offset + 10 > message.Length)
            {
                return false;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 8)..]);
            var dataOffset = offset + 10;
            if (dataOffset + dataLength > message.Length)
            {
                return false;
            }

            offset = dataOffset + dataLength;
            if (!TryReadRecord(message, name, type, dataOffset, dataLength, records))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadRecord(
        ReadOnlySpan<byte> message, string name, ushort type, int dataOffset, int dataLength, List<DnsRecord> records)
    {
        switch (type)
        {
            case PointerType:
            {
                // Names in the data may point anywhere in the message, so they are read from the whole message.
                var targetOffset = dataOffset;
                if (!TryReadName(message, ref targetOffset, out var target))
                {
                    return false;
                }

                records.Add(new PointerRecord(name, target));
                return true;
            }
            case ServiceType:
            {
                if (dataLength < 7)
                {
                    return false;
                }

                var port = BinaryPrimitives.ReadUInt16BigEndian(message[(dataOffset + 4)..]);
                var targetOffset = dataOffset + 6;
                if (!TryReadName(message, ref targetOffset, out var target))
                {
                    return false;
                }

                records.Add(new ServiceRecord(name, port, target));
                return true;
            }
            case TextType:
                records.Add(new TextRecord(name, ReadTextEntries(message.Slice(dataOffset, dataLength))));
                return true;
            case AddressType when dataLength == 4:
                records.Add(new AddressRecord(name, new IPAddress(message.Slice(dataOffset, 4))));
                return true;
            default:
                return true;
        }
    }

    private static List<string> ReadTextEntries(ReadOnlySpan<byte> data)
    {
        var entries = new List<string>();
        var offset = 0;
        while (offset < data.Length)
        {
            var length = data[offset];

            // An entry that runs past the record ends it; the entries before it are kept.
            if (offset + 1 + length > data.Length)
            {
                break;
            }

            if (length > 0)
            {
                entries.Add(Encoding.UTF8.GetString(data.Slice(offset + 1, length)));
            }

            offset += 1 + length;
        }

        return entries;
    }

    private static bool TryReadName(ReadOnlySpan<byte> message, ref int offset, out string name)
    {
        name = "";
        var builder = new StringBuilder();
        var position = offset;
        var hasJumped = false;
        var jumpCount = 0;
        while (true)
        {
            if (position >= message.Length)
            {
                return false;
            }

            var length = message[position];
            if (length == 0)
            {
                if (!hasJumped)
                {
                    offset = position + 1;
                }

                name = builder.ToString();
                return true;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= message.Length || ++jumpCount > MaximumPointerJumps)
                {
                    return false;
                }

                if (!hasJumped)
                {
                    offset = position + 2;
                    hasJumped = true;
                }

                position = ((length & 0x3F) << 8) | message[position + 1];
                continue;
            }

            if ((length & 0xC0) != 0 || position + 1 + length > message.Length)
            {
                return false;
            }

            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            builder.Append(Encoding.UTF8.GetString(message.Slice(position + 1, length)));
            if (builder.Length > MaximumNameLength)
            {
                return false;
            }

            position += 1 + length;
        }
    }
}
```

- [ ] **Step 5: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~DnsMessageTests"`
Expected: 6 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/Discovery src/HomeBlaze/HomeBlaze.Services.Tests/Discovery
git commit -m "feat: read and write mDNS messages"
```

---

### Task 5: mDNS service collector and browser

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Services/Discovery/MulticastDns/MulticastDnsServiceCollector.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services/Discovery/MulticastDns/MulticastDnsBrowser.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services/Discovery/NetworkAdapters.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services.Tests/Discovery/MulticastDnsServiceCollectorTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using HomeBlaze.Services.Discovery.MulticastDns;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class MulticastDnsServiceCollectorTests
{
    private static readonly IPAddress Sender = IPAddress.Parse("192.168.1.99");

    private static List<DnsRecord> Parse(DnsMessageBuilder builder)
    {
        Assert.True(DnsMessage.TryParse(builder.Build(), out var records));
        return records;
    }

    [Theory]
    [InlineData("_hue._tcp", "_hue._tcp.local")]
    [InlineData("_hue._tcp.local", "_hue._tcp.local")]
    [InlineData(" _hue._tcp.local. ", "_hue._tcp.local")]
    public void WhenAServiceTypeIsGiven_ThenItsNameEndsInLocal(string serviceType, string expected)
    {
        // Act
        var name = MulticastDnsServiceCollector.GetServiceName(serviceType);

        // Assert
        Assert.Equal(expected, name);
    }

    [Fact]
    public void WhenTheRecordsOfAnInstanceArrive_ThenTheyAreCombined()
    {
        // Arrange
        var collector = new MulticastDnsServiceCollector();
        collector.Add(Parse(new DnsMessageBuilder()
            .AddPointer("_hue._tcp.local", "Philips Hue - 1A2B3C._hue._tcp.local")
            .AddService("Philips Hue - 1A2B3C._hue._tcp.local", 443, "hue.local")
            .AddText("Philips Hue - 1A2B3C._hue._tcp.local", "bridgeid=001788fffe1a2b3c", "BridgeId=ignored", "flag")
            .AddAddress("hue.local", "192.168.1.20")), Sender);

        // Act
        var services = collector.GetServices("_hue._tcp");

        // Assert
        var service = Assert.Single(services);
        Assert.Equal("Philips Hue - 1A2B3C", service.InstanceName);
        Assert.Equal("hue.local", service.HostName);
        Assert.Equal(443, service.Port);
        Assert.Equal([IPAddress.Parse("192.168.1.20")], service.Addresses);
        Assert.Equal("001788fffe1a2b3c", service.Properties["BRIDGEID"]);
        Assert.Equal("", service.Properties["flag"]);
    }

    [Fact]
    public void WhenAnInstanceNamesNoAddress_ThenTheSenderOfItsAnswerIsItsAddress()
    {
        // Arrange
        var collector = new MulticastDnsServiceCollector();
        collector.Add(Parse(new DnsMessageBuilder()
            .AddPointer("_shelly._tcp.local", "shellyplus1pm-a8032ab12345._shelly._tcp.local")), Sender);

        // Act
        var service = Assert.Single(collector.GetServices("_shelly._tcp"));

        // Assert
        Assert.Equal([Sender], service.Addresses);
        Assert.Null(service.HostName);
    }

    [Fact]
    public void WhenAPointerBelongsToAnotherServiceType_ThenItIsIgnored()
    {
        // Arrange
        var collector = new MulticastDnsServiceCollector();
        collector.Add(Parse(new DnsMessageBuilder()
            .AddPointer("_http._tcp.local", "Printer._http._tcp.local")
            .AddPointer("_hue._tcp.local", "Printer._http._tcp.local")), Sender);

        // Act
        var services = collector.GetServices("_hue._tcp");

        // Assert
        Assert.Empty(services);
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~MulticastDnsServiceCollectorTests"`
Expected: build error, `MulticastDnsServiceCollector` does not exist.

- [ ] **Step 3: Implement the collector**

```csharp
using System.Net;
using HomeBlaze.Abstractions.Discovery;

namespace HomeBlaze.Services.Discovery.MulticastDns;

/// <summary>
/// Collects the records of mDNS answers and combines them into the instances of a service type. Thread-safe.
/// </summary>
internal sealed class MulticastDnsServiceCollector
{
    private const int MaximumRecords = 4096;
    private const int MaximumServices = 64;

    private readonly Lock _lock = new();
    private readonly List<(DnsRecord Record, IPAddress Sender)> _records = [];

    /// <summary>
    /// Gets the DNS name of a service type, such as <c>_hue._tcp.local</c> for <c>_hue._tcp</c>.
    /// </summary>
    public static string GetServiceName(string serviceType)
    {
        var name = serviceType.Trim().TrimEnd('.');
        return name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name : name + ".local";
    }

    /// <summary>
    /// Adds the records of one answer, up to a total that bounds what the hosts of a network can make it hold.
    /// </summary>
    public void Add(IReadOnlyList<DnsRecord> records, IPAddress sender)
    {
        lock (_lock)
        {
            var count = Math.Min(records.Count, MaximumRecords - _records.Count);
            for (var index = 0; index < count; index++)
            {
                _records.Add((records[index], sender));
            }
        }
    }

    /// <summary>
    /// Combines the collected records into the instances of <paramref name="serviceType"/>.
    /// </summary>
    public IReadOnlyList<MulticastDnsService> GetServices(string serviceType)
    {
        (DnsRecord Record, IPAddress Sender)[] records;
        lock (_lock)
        {
            records = _records.ToArray();
        }

        var serviceName = GetServiceName(serviceType);
        var suffix = "." + serviceName;
        var instances = records
            .Where(entry => entry.Record is PointerRecord pointer &&
                IsName(pointer.Name, serviceName) &&
                pointer.Target.Length > suffix.Length &&
                pointer.Target.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(entry => ((PointerRecord)entry.Record).Target, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumServices);

        var services = new List<MulticastDnsService>();
        foreach (var (record, sender) in instances)
        {
            var instanceName = ((PointerRecord)record).Target;
            var service = records.Select(entry => entry.Record).OfType<ServiceRecord>()
                .FirstOrDefault(candidate => IsName(candidate.Name, instanceName));

            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in records.Select(item => item.Record).OfType<TextRecord>()
                .Where(text => IsName(text.Name, instanceName))
                .SelectMany(text => text.Entries))
            {
                var separator = entry.IndexOf('=');
                var key = separator < 0 ? entry : entry[..separator];

                // RFC 6763 section 6.4: the first occurrence of a key wins.
                if (key.Length > 0)
                {
                    properties.TryAdd(key, separator < 0 ? "" : entry[(separator + 1)..]);
                }
            }

            IPAddress[] addresses = service is null
                ? []
                : records.Select(entry => entry.Record).OfType<AddressRecord>()
                    .Where(address => IsName(address.Name, service.Target))
                    .Select(address => address.Address)
                    .Distinct()
                    .ToArray();

            services.Add(new MulticastDnsService(
                instanceName[..^suffix.Length],
                service?.Target,
                service?.Port ?? 0,
                addresses.Length > 0 ? addresses : [sender],
                properties));
        }

        return services;
    }

    private static bool IsName(string name, string expected) =>
        string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~MulticastDnsServiceCollectorTests"`
Expected: 6 tests pass (3 theory cases plus 3 facts).

- [ ] **Step 5: Implement the adapters helper and the browser**

`NetworkAdapters.cs`:

```csharp
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HomeBlaze.Services.Discovery;

internal static class NetworkAdapters
{
    /// <summary>
    /// Gets the IPv4 addresses of the adapters that are up, are no loopback and support multicast, or none when the
    /// adapters cannot be listed.
    /// </summary>
    public static IReadOnlyList<IPAddress> GetMulticastIPv4Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.SupportsMulticast &&
                    adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(unicastAddress => unicastAddress.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Distinct()
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
```

`MulticastDnsBrowser.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Services.Discovery.MulticastDns;

/// <summary>
/// Browses over mDNS with legacy unicast queries (RFC 6762 section 6.7): queries leave from an ephemeral port, so
/// responders answer that port directly and port 5353, which Avahi holds on most Linux hosts, is not bound.
/// </summary>
internal sealed class MulticastDnsBrowser : IMulticastDnsBrowser
{
    private static readonly IPEndPoint MulticastEndPoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    // RFC 6762 section 5.2 spaces repeated queries at least one second apart, doubling each time.
    private static readonly TimeSpan[] RepeatDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];
    private static readonly TimeSpan ListeningWindow = TimeSpan.FromSeconds(4);
    private const int MaximumMessagesPerAdapter = 512;

    private readonly ILogger<MulticastDnsBrowser> _logger;

    public MulticastDnsBrowser(ILogger<MulticastDnsBrowser> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<MulticastDnsService>> BrowseAsync(string serviceType, CancellationToken cancellationToken)
    {
        var query = DnsMessage.CreatePointerQuery(MulticastDnsServiceCollector.GetServiceName(serviceType));
        var collector = new MulticastDnsServiceCollector();

        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(ListeningWindow);
        await Task.WhenAll(NetworkAdapters.GetMulticastIPv4Addresses()
            .Select(address => QueryAdapterAsync(address, query, collector, window.Token)));

        cancellationToken.ThrowIfCancellationRequested();
        return collector.GetServices(serviceType);
    }

    private async Task QueryAdapterAsync(
        IPAddress localAddress, byte[] query, MulticastDnsServiceCollector collector, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new UdpClient(new IPEndPoint(localAddress, 0));
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());

            _ = SendQueriesAsync(client, query, cancellationToken);
            for (var messageCount = 0; messageCount < MaximumMessagesPerAdapter; messageCount++)
            {
                var received = await client.ReceiveAsync(cancellationToken);
                if (DnsMessage.TryParse(received.Buffer, out var records))
                {
                    collector.Add(records, received.RemoteEndPoint.Address);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The listening window ended, or the caller canceled, which BrowseAsync rethrows.
        }
        catch (SocketException exception)
        {
            _logger.LogDebug(exception, "The mDNS query on {Address} failed.", localAddress);
        }
    }

    private static async Task SendQueriesAsync(UdpClient client, byte[] query, CancellationToken cancellationToken)
    {
        // Runs unobserved next to the receive loop, which disposes the client when the window ends, so every
        // failure ends it quietly.
        try
        {
            await client.SendAsync(query, MulticastEndPoint, cancellationToken);
            foreach (var delay in RepeatDelays)
            {
                await Task.Delay(delay, cancellationToken);
                await client.SendAsync(query, MulticastEndPoint, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }
}
```

- [ ] **Step 6: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded, 0 warnings. The socket code is checked against real devices in Task 18.

- [ ] **Step 7: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/Discovery src/HomeBlaze/HomeBlaze.Services.Tests/Discovery
git commit -m "feat: browse for mDNS services"
```

---

### Task 6: SSDP search

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Services/HomeBlaze.Services.csproj`
- Create: `src/HomeBlaze/HomeBlaze.Services/Discovery/SsdpSearch.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services.Tests/Discovery/SsdpSearchTests.cs`

- [ ] **Step 1: Reference Rssdp**

In `HomeBlaze.Services.csproj`, add next to the other package references:

```xml
<PackageReference Include="Rssdp" />
```

The version is pinned in `src/Directory.Packages.props` already.

- [ ] **Step 2: Write the failing test**

```csharp
using HomeBlaze.Services.Discovery;
using Rssdp;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class SsdpSearchTests
{
    private const string SearchTarget = "urn:dslforum-org:device:InternetGatewayDevice:1";

    [Fact]
    public void WhenDevicesAnswer_ThenOnlyAnswersForTheTargetWithAnAbsoluteLocationAreKept()
    {
        // Arrange
        DiscoveredSsdpDevice[] devices =
        [
            new() { NotificationType = SearchTarget, Usn = "uuid:router", DescriptionLocation = new Uri("http://192.168.178.1:49000/tr64desc.xml") },
            new() { NotificationType = "upnp:rootdevice", Usn = "uuid:other", DescriptionLocation = new Uri("http://192.168.178.2/desc.xml") },
            new() { NotificationType = SearchTarget, Usn = "uuid:relative", DescriptionLocation = new Uri("desc.xml", UriKind.Relative) }
        ];

        // Act
        var responses = SsdpSearch.ToResponses(devices, SearchTarget);

        // Assert
        var response = Assert.Single(responses);
        Assert.Equal("192.168.178.1", response.Location.Host);
        Assert.Equal("uuid:router", response.UniqueServiceName);
        Assert.Equal(SearchTarget, response.SearchTarget);
    }
}
```

- [ ] **Step 3: Run the test to see it fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~SsdpSearchTests"`
Expected: build error, `SsdpSearch` does not exist.

- [ ] **Step 4: Implement**

```csharp
using System.Net.Sockets;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging;
using Rssdp;

namespace HomeBlaze.Services.Discovery;

/// <summary>
/// Searches over SSDP on every IPv4 adapter.
/// </summary>
internal sealed class SsdpSearch : ISsdpSearch
{
    private static readonly TimeSpan SearchWindow = TimeSpan.FromSeconds(3);

    // Bounds what a hostile or malfunctioning host can make one search return.
    private const int MaximumResponses = 64;

    private readonly ILogger<SsdpSearch> _logger;

    public SsdpSearch(ILogger<SsdpSearch> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken)
    {
        try
        {
            using var locator = new AggregateSsdpDeviceLocator(includeIpv4: true, includeIpv6: false, adapterFilter: null, logger: null);
            var devices = await locator.SearchAsync(searchTarget, SearchWindow, cancellationToken);
            return ToResponses(devices, searchTarget);
        }
        catch (Exception exception) when (exception is ArgumentException or SocketException or AggregateException)
        {
            // No adapter can search, which is a normal case in a container.
            _logger.LogDebug(exception, "The SSDP search for {SearchTarget} failed.", searchTarget);
            return [];
        }
    }

    internal static IReadOnlyList<SsdpResponse> ToResponses(IEnumerable<DiscoveredSsdpDevice> devices, string searchTarget) =>
        devices
            // Other devices can answer a search with any notification type.
            .Where(device => device.NotificationType == searchTarget && device.DescriptionLocation is { IsAbsoluteUri: true })
            .Take(MaximumResponses)
            .Select(device => new SsdpResponse(device.DescriptionLocation, device.Usn, device.NotificationType))
            .ToArray();
}
```

- [ ] **Step 5: Run the test to see it pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~SsdpSearchTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services src/HomeBlaze/HomeBlaze.Services.Tests/Discovery
git commit -m "feat: search for SSDP devices"
```

---

### Task 7: SubjectDiscovererRegistry

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Services/Discovery/SubjectDiscovererRegistry.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services.Tests/Discovery/SubjectDiscovererRegistryTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Components.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Services.Components;
using HomeBlaze.Services.Discovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class SubjectDiscovererRegistryTests
{
    private static (SubjectDiscovererRegistry Registry, TypeProvider TypeProvider) CreateRegistry(params Type[] types)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(DiscoveryTestSetupComponent), typeof(OtherDiscoveryTestEditComponent), .. types]);
        var serviceProvider = new ServiceCollection().AddSingleton<TestDependency>().BuildServiceProvider();
        var registry = new SubjectDiscovererRegistry(
            typeProvider, new SubjectComponentRegistry(typeProvider), serviceProvider, NullLogger<SubjectDiscovererRegistry>.Instance);
        return (registry, typeProvider);
    }

    [Fact]
    public void WhenTheComponentOfTheSubjectTypeTakesResults_ThenTheDiscovererIsCreated()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(AcceptedTestDiscoverer));

        // Act
        var discoverers = registry.GetDiscoverers();

        // Assert
        Assert.IsType<AcceptedTestDiscoverer>(Assert.Single(discoverers));
    }

    [Fact]
    public void WhenTheComponentOfTheSubjectTypeTakesNoResults_ThenTheDiscovererIsLeftOut()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(RejectedTestDiscoverer));

        // Act
        var discoverers = registry.GetDiscoverers();

        // Assert
        Assert.Empty(discoverers);
    }

    [Fact]
    public void WhenTheConstructorOfADiscovererThrows_ThenItIsLeftOut()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(ThrowingTestDiscoverer), typeof(AcceptedTestDiscoverer));

        // Act
        var discoverers = registry.GetDiscoverers();

        // Assert
        Assert.IsType<AcceptedTestDiscoverer>(Assert.Single(discoverers));
    }

    [Fact]
    public void WhenADiscovererNeedsAService_ThenItIsInjected()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(InjectedTestDiscoverer));

        // Act
        var discoverer = Assert.IsType<InjectedTestDiscoverer>(Assert.Single(registry.GetDiscoverers()));

        // Assert
        Assert.NotNull(discoverer.Dependency);
    }

    [Fact]
    public void WhenTypesAreAddedLater_ThenTheirDiscoverersAreCreated()
    {
        // Arrange
        var (registry, typeProvider) = CreateRegistry();
        Assert.Empty(registry.GetDiscoverers());

        // Act
        typeProvider.AddTypes([typeof(AcceptedTestDiscoverer)]);

        // Assert
        Assert.IsType<AcceptedTestDiscoverer>(Assert.Single(registry.GetDiscoverers()));
    }
}

[SubjectComponent(SubjectComponentType.Setup, typeof(DiscoveryTestSubject))]
public sealed class DiscoveryTestSetupComponent : ISubjectDiscoveryComponent
{
    public IDiscoveryResult? DiscoveryResult { get; set; }
}

[SubjectComponent(SubjectComponentType.Edit, typeof(OtherDiscoveryTestSubject))]
public sealed class OtherDiscoveryTestEditComponent;

public sealed class TestDependency;

public sealed class AcceptedTestDiscoverer : SubjectDiscoverer<DiscoveryTestSubject, TestDiscoveryResult>
{
    protected override IAsyncEnumerable<TestDiscoveryResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken) => TestScans.Yield();

    protected override bool Matches(DiscoveryTestSubject existing, TestDiscoveryResult result) => false;
}

public sealed class RejectedTestDiscoverer : SubjectDiscoverer<OtherDiscoveryTestSubject, TestDiscoveryResult>
{
    protected override IAsyncEnumerable<TestDiscoveryResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken) => TestScans.Yield();

    protected override bool Matches(OtherDiscoveryTestSubject existing, TestDiscoveryResult result) => false;
}

public sealed class ThrowingTestDiscoverer : SubjectDiscoverer<DiscoveryTestSubject, TestDiscoveryResult>
{
    public ThrowingTestDiscoverer() => throw new InvalidOperationException("The discoverer is broken.");

    protected override IAsyncEnumerable<TestDiscoveryResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken) => TestScans.Yield();

    protected override bool Matches(DiscoveryTestSubject existing, TestDiscoveryResult result) => false;
}

public sealed class InjectedTestDiscoverer(TestDependency dependency) : SubjectDiscoverer<DiscoveryTestSubject, TestDiscoveryResult>
{
    public TestDependency Dependency => dependency;

    protected override IAsyncEnumerable<TestDiscoveryResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken) => TestScans.Yield();

    protected override bool Matches(DiscoveryTestSubject existing, TestDiscoveryResult result) => false;
}
```

`TestScans` is `internal`, and public discoverer types call it from members only, which is allowed.

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~SubjectDiscovererRegistryTests"`
Expected: build error, `SubjectDiscovererRegistry` does not exist.

- [ ] **Step 3: Implement**

```csharp
using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Components.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Services.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Services.Discovery;

/// <summary>
/// Creates the subject discoverers among the loaded types that the create dialog can use.
/// </summary>
public sealed class SubjectDiscovererRegistry
{
    private readonly TypeProvider _typeProvider;
    private readonly SubjectComponentRegistry _componentRegistry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubjectDiscovererRegistry> _logger;
    private readonly Lock _lock = new();

    private IReadOnlyCollection<Type>? _scannedTypes;
    private IReadOnlyList<ISubjectDiscoverer> _discoverers = [];

    public SubjectDiscovererRegistry(
        TypeProvider typeProvider,
        SubjectComponentRegistry componentRegistry,
        IServiceProvider serviceProvider,
        ILogger<SubjectDiscovererRegistry> logger)
    {
        _typeProvider = typeProvider;
        _componentRegistry = componentRegistry;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Gets one instance of every non-abstract <see cref="ISubjectDiscoverer"/> type whose subject type has a Setup or
    /// Edit component that implements <see cref="ISubjectDiscoveryComponent"/>. A discoverer that cannot be created,
    /// or has no such component, is left out and logged as an error.
    /// </summary>
    public IReadOnlyList<ISubjectDiscoverer> GetDiscoverers()
    {
        var types = _typeProvider.Types;
        lock (_lock)
        {
            // The type provider publishes a new collection whenever types are added, such as by a plugin.
            if (!ReferenceEquals(types, _scannedTypes))
            {
                _discoverers = CreateDiscoverers(types);
                _scannedTypes = types;
            }

            return _discoverers;
        }
    }

    private List<ISubjectDiscoverer> CreateDiscoverers(IReadOnlyCollection<Type> types)
    {
        var discoverers = new List<ISubjectDiscoverer>();
        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsGenericTypeDefinition || !typeof(ISubjectDiscoverer).IsAssignableFrom(type))
            {
                continue;
            }

            ISubjectDiscoverer discoverer;
            try
            {
                discoverer = (ISubjectDiscoverer)ActivatorUtilities.CreateInstance(_serviceProvider, type);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The subject discoverer {Discoverer} could not be created.", type);
                continue;
            }

            if (!AcceptsDiscoveryResults(discoverer.SubjectType))
            {
                _logger.LogError(
                    "The subject discoverer {Discoverer} is not used: the component the create dialog shows for {SubjectType} does not implement {Interface}.",
                    type, discoverer.SubjectType, nameof(ISubjectDiscoveryComponent));
                continue;
            }

            discoverers.Add(discoverer);
        }

        return discoverers;
    }

    private bool AcceptsDiscoveryResults(Type subjectType)
    {
        // The create dialog shows the Setup component, or the Edit component when there is none.
        var registration =
            _componentRegistry.GetComponent(subjectType, SubjectComponentType.Setup) ??
            _componentRegistry.GetComponent(subjectType, SubjectComponentType.Edit);

        return registration is not null && typeof(ISubjectDiscoveryComponent).IsAssignableFrom(registration.ComponentType);
    }
}
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~SubjectDiscovererRegistryTests"`
Expected: 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/Discovery src/HomeBlaze/HomeBlaze.Services.Tests/Discovery
git commit -m "feat: find the subject discoverers among the loaded types"
```

---

### Task 8: SubjectDiscoveryService and registration

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Services/Discovery/SubjectDiscoveryService.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services.Tests/Discovery/SubjectDiscoveryServiceTests.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/ServiceCollectionExtensions.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Host.Services/ServiceCollectionExtensions.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Collections.Concurrent;
using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Services.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class SubjectDiscoveryServiceTests
{
    private static SubjectDiscoveryService CreateService(TimeProvider timeProvider, params ISubjectDiscoverer[] discoverers) =>
        new(() => discoverers, new FakeSsdpSearch(), new FakeMulticastDnsBrowser(), timeProvider, NullLogger<SubjectDiscoveryService>.Instance);

    [Fact]
    public async Task WhenDiscoverersYieldResults_ThenEveryResultIsReported()
    {
        // Arrange
        var service = CreateService(
            TimeProvider.System,
            new LambdaDiscoverer<DiscoveryTestSubject>((_, _) => TestScans.Yield(new TestDiscoveryResult("a"), new TestDiscoveryResult("b"))),
            new LambdaDiscoverer<OtherDiscoveryTestSubject>((_, _) => TestScans.Yield(new TestDiscoveryResult("c"))));
        var results = new ConcurrentQueue<DiscoveredSubject>();

        // Act
        await service.ScanAsync(
            [typeof(DiscoveryTestSubject), typeof(OtherDiscoveryTestSubject)], [], results.Enqueue, _ => { }, CancellationToken.None);

        // Assert
        Assert.Equal(["a", "b", "c"], results.Select(result => result.Result.Key).Order());
    }

    [Fact]
    public async Task WhenADiscovererThrows_ThenItsFailureIsReportedAndTheOthersCarryOn()
    {
        // Arrange
        var service = CreateService(
            TimeProvider.System,
            new LambdaDiscoverer<DiscoveryTestSubject>((_, _) => TestScans.YieldThenThrow(new TestDiscoveryResult("a"))),
            new LambdaDiscoverer<OtherDiscoveryTestSubject>((_, _) => TestScans.Yield(new TestDiscoveryResult("c"))));
        var results = new ConcurrentQueue<DiscoveredSubject>();
        var failures = new ConcurrentQueue<Type>();

        // Act
        await service.ScanAsync(
            [typeof(DiscoveryTestSubject), typeof(OtherDiscoveryTestSubject)], [], results.Enqueue, failures.Enqueue, CancellationToken.None);

        // Assert
        Assert.Equal(["a", "c"], results.Select(result => result.Result.Key).Order());
        Assert.Equal([typeof(DiscoveryTestSubject)], failures);
    }

    [Fact]
    public async Task WhenTheTimeLimitPasses_ThenAHangingDiscovererEndsWithoutAFailure()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var service = CreateService(timeProvider, new LambdaDiscoverer<DiscoveryTestSubject>((_, cancellationToken) => TestScans.Hang(cancellationToken)));
        var failures = new ConcurrentQueue<Type>();
        var scan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], _ => { }, failures.Enqueue, CancellationToken.None);

        // Act
        timeProvider.Advance(SubjectDiscoveryService.ScanTimeout);
        await scan.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Empty(failures);
    }

    [Fact]
    public async Task WhenTheCallerCancels_ThenTheScanIsCanceled()
    {
        // Arrange
        var service = CreateService(TimeProvider.System, new LambdaDiscoverer<DiscoveryTestSubject>((_, cancellationToken) => TestScans.Hang(cancellationToken)));
        using var cancellation = new CancellationTokenSource();
        var scan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], _ => { }, _ => { }, cancellation.Token);

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(TestTimeouts.Default));
    }

    [Fact]
    public async Task WhenATypeIsNotAsked_ThenItsDiscovererDoesNotRun()
    {
        // Arrange
        var service = CreateService(
            TimeProvider.System,
            new LambdaDiscoverer<OtherDiscoveryTestSubject>((_, _) => TestScans.Yield(new TestDiscoveryResult("c"))));
        var results = new ConcurrentQueue<DiscoveredSubject>();

        // Act
        await service.ScanAsync([typeof(DiscoveryTestSubject)], [], results.Enqueue, _ => { }, CancellationToken.None);

        // Assert
        Assert.Empty(results);
        Assert.False(service.HasDiscoverers([typeof(DiscoveryTestSubject)]));
        Assert.True(service.HasDiscoverers([typeof(OtherDiscoveryTestSubject)]));
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~SubjectDiscoveryServiceTests"`
Expected: build error, `SubjectDiscoveryService` does not exist.

- [ ] **Step 3: Implement**

```csharp
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Discovery;

/// <summary>
/// Runs the subject discoverers for the create dialog.
/// </summary>
public sealed class SubjectDiscoveryService
{
    /// <summary>
    /// The longest a scan runs. It ends earlier once every discoverer has completed.
    /// </summary>
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<IReadOnlyList<ISubjectDiscoverer>> _getDiscoverers;
    private readonly ISsdpSearch _ssdpSearch;
    private readonly IMulticastDnsBrowser _multicastDnsBrowser;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubjectDiscoveryService> _logger;

    public SubjectDiscoveryService(
        SubjectDiscovererRegistry registry,
        ISsdpSearch ssdpSearch,
        IMulticastDnsBrowser multicastDnsBrowser,
        TimeProvider timeProvider,
        ILogger<SubjectDiscoveryService> logger)
        : this(registry.GetDiscoverers, ssdpSearch, multicastDnsBrowser, timeProvider, logger)
    {
    }

    internal SubjectDiscoveryService(
        Func<IReadOnlyList<ISubjectDiscoverer>> getDiscoverers,
        ISsdpSearch ssdpSearch,
        IMulticastDnsBrowser multicastDnsBrowser,
        TimeProvider timeProvider,
        ILogger<SubjectDiscoveryService> logger)
    {
        _getDiscoverers = getDiscoverers;
        _ssdpSearch = ssdpSearch;
        _multicastDnsBrowser = multicastDnsBrowser;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Whether a discoverer finds devices of one of <paramref name="subjectTypes"/>.
    /// </summary>
    public bool HasDiscoverers(IReadOnlyCollection<Type> subjectTypes) =>
        _getDiscoverers().Any(discoverer => subjectTypes.Contains(discoverer.SubjectType));

    /// <summary>
    /// Runs the discoverers of <paramref name="subjectTypes"/> in parallel and reports what they find, until all have
    /// completed or <see cref="ScanTimeout"/> has passed. A discoverer that throws is logged and reported to
    /// <paramref name="onFailure"/> with its subject type; the others carry on.
    /// </summary>
    /// <remarks>
    /// The callbacks run on thread pool threads, concurrently for different discoverers.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task ScanAsync(
        IReadOnlyCollection<Type> subjectTypes,
        IReadOnlyCollection<IInterceptorSubject> existingSubjects,
        Action<DiscoveredSubject> onResult,
        Action<Type> onFailure,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(ScanTimeout, _timeProvider);
        using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var scan = new DiscoveryScan(_ssdpSearch, _multicastDnsBrowser, scanCancellation.Token);
        var discoverers = _getDiscoverers().Where(discoverer => subjectTypes.Contains(discoverer.SubjectType));
        await Task.WhenAll(discoverers.Select(discoverer => Task.Run(
            () => RunAsync(discoverer, scan, existingSubjects, onResult, onFailure, scanCancellation.Token),
            CancellationToken.None)));

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task RunAsync(
        ISubjectDiscoverer discoverer,
        DiscoveryScan scan,
        IReadOnlyCollection<IInterceptorSubject> existingSubjects,
        Action<DiscoveredSubject> onResult,
        Action<Type> onFailure,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var discovered in discoverer
                .DiscoverAsync(scan, existingSubjects, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                onResult(discovered);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Canceled by the caller or at the time limit, which is no failure of the discoverer.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The subject discoverer {Discoverer} failed.", discoverer.GetType());
            onFailure(discoverer.SubjectType);
        }
    }
}
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~SubjectDiscoveryServiceTests"`
Expected: 5 tests pass.

- [ ] **Step 5: Register the services**

In `src/HomeBlaze/HomeBlaze.Services/ServiceCollectionExtensions.cs`, add `using HomeBlaze.Abstractions.Discovery;`, `using HomeBlaze.Services.Discovery;` and `using HomeBlaze.Services.Discovery.MulticastDns;`, and add after the `ClientConnectionService` registration in `AddHomeBlazeServices`:

```csharp
        services.AddSingleton<ISsdpSearch>(serviceProvider => new SsdpSearch(
            serviceProvider.GetService<ILogger<SsdpSearch>>() ?? NullLogger<SsdpSearch>.Instance));
        services.AddSingleton<IMulticastDnsBrowser>(serviceProvider => new MulticastDnsBrowser(
            serviceProvider.GetService<ILogger<MulticastDnsBrowser>>() ?? NullLogger<MulticastDnsBrowser>.Instance));
```

In `src/HomeBlaze/HomeBlaze.Host.Services/ServiceCollectionExtensions.cs`, add `using HomeBlaze.Services.Discovery;` and add after `services.TryAddSingleton(TimeProvider.System);`:

```csharp
        // The registry needs the component registry above, which only the host registers.
        services.AddSingleton<SubjectDiscovererRegistry>();
        services.AddSingleton<SubjectDiscoveryService>();
```

- [ ] **Step 6: Accept the public API snapshot**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~PublicApi"`
Expected: FAIL with a diff that adds `SubjectDiscovererRegistry` and `SubjectDiscoveryService`. Review it, then:

```bash
mv src/HomeBlaze/HomeBlaze.Services.Tests/VerifyChecksTests.PublicApi.received.txt src/HomeBlaze/HomeBlaze.Services.Tests/VerifyChecksTests.PublicApi.verified.txt
dotnet test src/HomeBlaze/HomeBlaze.Services.Tests
```
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services src/HomeBlaze/HomeBlaze.Host.Services src/HomeBlaze/HomeBlaze.Services.Tests
git commit -m "feat: run the subject discoverers of a scan in parallel"
```

---

### Task 9: Name suggestion for a found device

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Components/Dialogs/SubjectNameOptions.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Components.Tests/SubjectNameOptionsTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `SubjectNameOptionsTests`:

```csharp
    [Fact]
    public void WhenThePreferredNameIsValid_ThenItIsSuggested()
    {
        // Arrange
        var options = new SubjectNameOptions("File name", ".json", ["Other.json"]);

        // Act
        var name = options.SuggestName("  Philips Hue - 1A2B3C ", typeof(SubjectNameOptionsTests));

        // Assert
        Assert.Equal("Philips Hue - 1A2B3C", name);
    }

    [Fact]
    public void WhenThePreferredNameIsTaken_ThenTheTypeNameIsSuggested()
    {
        // Arrange
        var options = new SubjectNameOptions("File name", ".json", ["Living room.json"]);

        // Act
        var name = options.SuggestName("Living room", typeof(SubjectNameOptionsTests));

        // Assert
        Assert.Equal(nameof(SubjectNameOptionsTests), name);
    }
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests --filter "FullyQualifiedName~SubjectNameOptionsTests"`
Expected: build error, no `SuggestName` overload takes two arguments.

- [ ] **Step 3: Implement**

Add below `SuggestName(Type type)` in `SubjectNameOptions.cs`:

```csharp
    /// <summary>
    /// Suggests a name for a new subject that represents a found device: <paramref name="preferredName"/> without
    /// surrounding whitespace when <see cref="Validate"/> accepts it, otherwise <see cref="SuggestName(Type)"/>.
    /// </summary>
    public string SuggestName(string preferredName, Type type)
    {
        var trimmedName = preferredName.Trim();
        return Validate(trimmedName) is null ? trimmedName : SuggestName(type);
    }
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests --filter "FullyQualifiedName~SubjectNameOptionsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components/Dialogs/SubjectNameOptions.cs src/HomeBlaze/HomeBlaze.Components.Tests/SubjectNameOptionsTests.cs
git commit -m "feat: suggest the name of a found device for its subject"
```

---

### Task 10: Pass the result through SubjectEditPanel

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Components/Editors/SubjectEditPanel.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Components.Tests/SubjectEditPanelParametersTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `SubjectEditPanelParametersTests` (with `using HomeBlaze.Abstractions.Discovery;`):

```csharp
    [Fact]
    public void WhenADiscoveryResultIsGiven_ThenItIsPassedUnderItsParameterName()
    {
        // Arrange
        var result = new FoundDevice();

        // Act
        var parameters = SubjectEditPanel.CreateHostedComponentParameters(true, null, result);

        // Assert
        Assert.Equal(2, parameters.Count);
        Assert.Same(result, parameters["DiscoveryResult"]);
        Assert.True((bool)parameters["IsCreating"]!);
    }

    private sealed class FoundDevice : IDiscoveryResult
    {
        public string Key => "device";

        public string DisplayName => "Device";

        public string? Detail => null;
    }
```

- [ ] **Step 2: Run the test to see it fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests --filter "FullyQualifiedName~SubjectEditPanelParametersTests"`
Expected: build error, `CreateHostedComponentParameters` takes two arguments.

- [ ] **Step 3: Implement**

In `SubjectEditPanel.razor`:

Add `@using HomeBlaze.Abstractions.Discovery` below `@using HomeBlaze.Services`.

Below `private const string IsCreatingParameterName = "IsCreating";` add:

```csharp
    private const string DiscoveryResultParameterName = nameof(ISubjectDiscoveryComponent.DiscoveryResult);
```

Below the `EditorParameters` parameter add:

```csharp
    /// <summary>
    /// Gets or sets the device the user picked on the Discover page, or null. It reaches the hosted component only
    /// when that implements <see cref="ISubjectDiscoveryComponent"/>.
    /// </summary>
    [Parameter] public IDiscoveryResult? DiscoveryResult { get; set; }
```

Next to `_mergedIsCreating` add:

```csharp
    private IDiscoveryResult? _mergedDiscoveryResult;
```

Replace `GetHostedComponentParameters` and `CreateHostedComponentParameters` with:

```csharp
    private Dictionary<string, object?> GetHostedComponentParameters()
    {
        var discoveryResult = GetAcceptedDiscoveryResult();
        if (EditorParameters is null && discoveryResult is null)
        {
            return CreateHostedComponentParameters(IsCreating, null);
        }

        if (_mergedParameters is null ||
            !ReferenceEquals(_mergedEditorParameters, EditorParameters) ||
            _mergedIsCreating != IsCreating ||
            !ReferenceEquals(_mergedDiscoveryResult, discoveryResult))
        {
            _mergedEditorParameters = EditorParameters;
            _mergedIsCreating = IsCreating;
            _mergedDiscoveryResult = discoveryResult;
            _mergedParameters = CreateHostedComponentParameters(IsCreating, EditorParameters, discoveryResult);
        }

        return _mergedParameters;
    }

    private IDiscoveryResult? GetAcceptedDiscoveryResult()
    {
        // Blazor throws for a parameter the hosted component does not declare.
        if (DiscoveryResult is null || Subject is null || HostedComponentType is not { } componentType)
        {
            return null;
        }

        var registration = ComponentRegistry.GetComponent(Subject.GetType(), componentType);
        return registration is not null && typeof(ISubjectDiscoveryComponent).IsAssignableFrom(registration.ComponentType)
            ? DiscoveryResult
            : null;
    }

    /// <summary>
    /// Creates the parameters of the hosted component: the <c>IsCreating</c> entry, the given editor parameters and
    /// the discovery result when one is given. Returns a shared instance when there are neither.
    /// </summary>
    internal static Dictionary<string, object?> CreateHostedComponentParameters(
        bool isCreating, IReadOnlyDictionary<string, object?>? editorParameters, IDiscoveryResult? discoveryResult = null)
    {
        if (editorParameters is null && discoveryResult is null)
        {
            return isCreating ? CreatingParameters : EditingParameters;
        }

        var parameters = new Dictionary<string, object?>((editorParameters?.Count ?? 0) + 2);
        if (editorParameters is not null)
        {
            foreach (var (name, value) in editorParameters)
            {
                parameters[name] = value;
            }
        }

        if (discoveryResult is not null)
        {
            parameters[DiscoveryResultParameterName] = discoveryResult;
        }

        parameters[IsCreatingParameterName] = isCreating;
        return parameters;
    }
```

Update the comment above `_mergedEditorParameters` to say "kept until EditorParameters, IsCreating or the discovery result change".

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests --filter "FullyQualifiedName~SubjectEditPanelParametersTests"`
Expected: PASS, including the existing tests.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components/Editors/SubjectEditPanel.razor src/HomeBlaze/HomeBlaze.Components.Tests/SubjectEditPanelParametersTests.cs
git commit -m "feat: pass a discovered device to the component that takes it"
```

---

### Task 11: SubjectDiscoveryList

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/Dialogs/SubjectDiscoveryList.cs`
- Create: `src/HomeBlaze/HomeBlaze.Components.Tests/SubjectDiscoveryListTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Services;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class SubjectDiscoveryListTests
{
    private static readonly SubjectTypeDescriptor Hue = new(typeof(string), "Hue Bridge", null, "Lighting", "Hub");
    private static readonly SubjectTypeDescriptor Shelly = new(typeof(int), "Shelly device", null, "Switches", "Power");

    private static DiscoveredSubject Found(SubjectTypeDescriptor descriptor, string name) =>
        new(descriptor.Type, new FoundDevice(name), null);

    [Fact]
    public void WhenResultsArriveInAnyOrder_ThenRowsAreOrderedByTypeAndThenName()
    {
        // Arrange
        var list = new SubjectDiscoveryList();

        // Act
        list.Add(Found(Shelly, "shellyplus1pm-b"), Shelly);
        list.Add(Found(Hue, "Bridge"), Hue);
        list.Add(Found(Shelly, "shellyplus1pm-a"), Shelly);

        // Assert
        Assert.Equal(["Bridge", "shellyplus1pm-a", "shellyplus1pm-b"], list.Rows.Select(row => row.Discovered.Result.DisplayName));
    }

    [Fact]
    public void WhenADiscovererFails_ThenItsTypeIsNamedInAFailureLine()
    {
        // Arrange
        var list = new SubjectDiscoveryList();

        // Act
        list.AddFailure(Hue);

        // Assert
        Assert.Equal(["Hue Bridge: search failed"], list.Failures);
    }

    [Fact]
    public void WhenTheListIsCleared_ThenRowsAndFailuresAreGone()
    {
        // Arrange
        var list = new SubjectDiscoveryList();
        list.Add(Found(Hue, "Bridge"), Hue);
        list.AddFailure(Shelly);

        // Act
        list.Clear();

        // Assert
        Assert.Empty(list.Rows);
        Assert.Empty(list.Failures);
    }

    private sealed record FoundDevice(string DisplayName) : IDiscoveryResult
    {
        public string Key => DisplayName;

        public string? Detail => null;
    }
}
```

`SubjectTypeDescriptor`'s positional order is `(Type, DisplayName, Description, Group, IconName)`, as `SubjectTypeCatalog.Describe` constructs it.

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests --filter "FullyQualifiedName~SubjectDiscoveryListTests"`
Expected: build error, `SubjectDiscoveryList` does not exist.

- [ ] **Step 3: Implement**

```csharp
using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Services;

namespace HomeBlaze.Components.Dialogs;

/// <summary>
/// The rows of the Discover page, ordered by type and then by name so the list moves little while results arrive,
/// and a line for each discoverer that failed. Not thread-safe: the page changes it on the renderer's dispatcher.
/// </summary>
internal sealed class SubjectDiscoveryList
{
    private static readonly Comparer<SubjectDiscoveryRow> RowComparer = Comparer<SubjectDiscoveryRow>.Create(CompareRows);

    private readonly List<SubjectDiscoveryRow> _rows = [];
    private readonly List<string> _failures = [];

    public IReadOnlyList<SubjectDiscoveryRow> Rows => _rows;

    public IReadOnlyList<string> Failures => _failures;

    public void Add(DiscoveredSubject discovered, SubjectTypeDescriptor descriptor)
    {
        var row = new SubjectDiscoveryRow(discovered, descriptor);
        var index = _rows.BinarySearch(row, RowComparer);
        _rows.Insert(index < 0 ? ~index : index, row);
    }

    public void AddFailure(SubjectTypeDescriptor descriptor)
    {
        _failures.Add($"{descriptor.DisplayName}: search failed");
    }

    public void Clear()
    {
        _rows.Clear();
        _failures.Clear();
    }

    private static int CompareRows(SubjectDiscoveryRow? first, SubjectDiscoveryRow? second)
    {
        if (first is null || second is null)
        {
            return first is null ? (second is null ? 0 : -1) : 1;
        }

        var byType = StringComparer.OrdinalIgnoreCase.Compare(first.Descriptor.DisplayName, second.Descriptor.DisplayName);
        if (byType != 0)
        {
            return byType;
        }

        var byName = StringComparer.OrdinalIgnoreCase.Compare(first.Discovered.Result.DisplayName, second.Discovered.Result.DisplayName);
        return byName != 0
            ? byName
            : StringComparer.Ordinal.Compare(first.Discovered.Result.Key, second.Discovered.Result.Key);
    }
}

/// <summary>
/// A found device with the description of its subject type.
/// </summary>
internal sealed record SubjectDiscoveryRow(DiscoveredSubject Discovered, SubjectTypeDescriptor Descriptor);
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests --filter "FullyQualifiedName~SubjectDiscoveryListTests"`
Expected: 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components/Dialogs/SubjectDiscoveryList.cs src/HomeBlaze/HomeBlaze.Components.Tests/SubjectDiscoveryListTests.cs
git commit -m "feat: order the devices of a scan for the Discover page"
```

---

### Task 12: Discover page and dialog integration

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/Dialogs/SubjectDiscoveryPage.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Components/Dialogs/SubjectSetupDialog.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.verified.txt`

No unit test for this task: its logic is in Tasks 9 to 11, and Task 17 covers the page end to end.

- [ ] **Step 1: Write the page**

`SubjectDiscoveryPage.razor`:

```razor
@using HomeBlaze.Abstractions.Discovery
@using HomeBlaze.Services.Discovery
@using HomeBlaze.Services.Navigation

@inject SubjectDiscoveryService DiscoveryService
@inject SubjectPathResolver PathResolver

@implements IDisposable

<div class="subject-discovery-page" data-testid="subject-discovery-page">
    <div class="subject-discovery-header">
        @if (_isScanning)
        {
            <MudProgressCircular Size="Size.Small" Indeterminate="true" />
            <MudText Typo="Typo.body2" data-testid="discovery-status">Searching your network...</MudText>
        }
        else if (_hasScanned)
        {
            <MudText Typo="Typo.body2" data-testid="discovery-status">@GetFoundText()</MudText>
        }
        <MudSpacer />
        <MudButton Variant="Variant.Text"
                   StartIcon="@Icons.Material.Filled.Refresh"
                   OnClick="StartScan"
                   data-testid="discovery-scan-again-button">Scan again</MudButton>
    </div>

    <div class="subject-type-list">
        @foreach (var row in _list.Rows)
        {
            var result = row.Discovered.Result;
            @if (row.Discovered.ExistingSubject is { } existingSubject)
            {
                <div class="subject-type-row subject-discovery-row-existing" data-testid="discovery-row-existing">
                    <MudIcon Icon="@MudBlazorIconResolver.Resolve(row.Descriptor.IconName)" />
                    <span class="subject-type-row-text">
                        <span>@result.DisplayName</span>
                        <span class="subject-type-row-description">@GetCaption(row), already added</span>
                    </span>
                    @if (GetBrowserUrl(existingSubject) is { } url)
                    {
                        <MudIconButton Href="@url"
                                       Icon="@Icons.Material.Filled.OpenInNew"
                                       Size="Size.Small"
                                       aria-label="Open the subject of this device"
                                       OnClick="OnNavigating" />
                    }
                </div>
            }
            else
            {
                <button type="button"
                        class="subject-type-row"
                        @onclick="() => OnSelected.InvokeAsync(row.Discovered)"
                        data-testid="discovery-row">
                    <MudIcon Icon="@MudBlazorIconResolver.Resolve(row.Descriptor.IconName)" />
                    <span class="subject-type-row-text">
                        <span>@result.DisplayName</span>
                        <span class="subject-type-row-description">@GetCaption(row)</span>
                    </span>
                </button>
            }
        }

        @foreach (var failure in _list.Failures)
        {
            <MudText Typo="Typo.caption" Class="mud-text-secondary" data-testid="discovery-failure">@failure</MudText>
        }

        @if (_hasScanned && !_isScanning && _list.Rows.Count == 0)
        {
            <div class="subject-type-empty" role="status" data-testid="discovery-empty-state">
                <MudText Typo="Typo.body2">No devices found</MudText>
            </div>
        }
    </div>

    <MudText Typo="Typo.caption" Class="mud-text-secondary subject-discovery-hint">
        Missing a device? Pick its type under "All types" and enter its address. In a container on a bridge network,
        multicast does not reach the devices: run HomeBlaze with host networking or add them by address. Battery
        devices are only found while they are awake.
    </MudText>
</div>

<style>
    .subject-discovery-page {
        display: flex;
        flex-direction: column;
        flex: 1 1 auto;
        min-height: 0;
        gap: 8px;
    }

    .subject-discovery-header {
        display: flex;
        align-items: center;
        gap: 8px;
    }

    .subject-discovery-row-existing {
        opacity: 0.6;
        cursor: default;
    }
</style>

@code {
    [Parameter, EditorRequired]
    public IReadOnlyList<SubjectTypeDescriptor> Types { get; set; } = [];

    [Parameter, EditorRequired]
    public IReadOnlyCollection<IInterceptorSubject> ExistingSubjects { get; set; } = [];

    /// <summary>
    /// Whether the page is shown. The first time it is, the page starts a scan.
    /// </summary>
    [Parameter]
    public bool IsActive { get; set; }

    [Parameter]
    public EventCallback<DiscoveredSubject> OnSelected { get; set; }

    /// <summary>
    /// Raised when the user follows the link to an existing subject, before the browser navigates.
    /// </summary>
    [Parameter]
    public EventCallback OnNavigating { get; set; }

    private readonly SubjectDiscoveryList _list = new();
    private Dictionary<Type, SubjectTypeDescriptor> _descriptors = [];
    private IReadOnlyList<SubjectTypeDescriptor>? _describedTypes;
    private CancellationTokenSource? _scanCancellation;
    private bool _isScanning;
    private bool _hasScanned;

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_describedTypes, Types))
        {
            _descriptors = Types.ToDictionary(descriptor => descriptor.Type);
            _describedTypes = Types;
        }

        if (IsActive && !_hasScanned && !_isScanning)
        {
            StartScan();
        }
    }

    private void StartScan()
    {
        // Canceled but not disposed: the running scan still reads the token after this.
        _scanCancellation?.Cancel();
        var scanCancellation = new CancellationTokenSource();
        _scanCancellation = scanCancellation;

        _list.Clear();
        _isScanning = true;
        _ = RunScanAsync(scanCancellation.Token);
    }

    private async Task RunScanAsync(CancellationToken cancellationToken)
    {
        var descriptors = _descriptors;
        try
        {
            await DiscoveryService.ScanAsync(
                descriptors.Keys,
                ExistingSubjects,
                discovered => _ = InvokeAsync(() =>
                {
                    if (!cancellationToken.IsCancellationRequested && descriptors.TryGetValue(discovered.SubjectType, out var descriptor))
                    {
                        _list.Add(discovered, descriptor);
                        StateHasChanged();
                    }
                }),
                subjectType => _ = InvokeAsync(() =>
                {
                    if (!cancellationToken.IsCancellationRequested && descriptors.TryGetValue(subjectType, out var descriptor))
                    {
                        _list.AddFailure(descriptor);
                        StateHasChanged();
                    }
                }),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A new scan or the disposal of the page took over.
            return;
        }

        // Posted after every result of the scan, so the dispatcher runs it after them.
        await InvokeAsync(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _isScanning = false;
                _hasScanned = true;
                StateHasChanged();
            }
        });
    }

    private string GetFoundText()
    {
        var count = _list.Rows.Count;
        return count == 1 ? "Found 1 device" : $"Found {count} devices";
    }

    private static string GetCaption(SubjectDiscoveryRow row) =>
        row.Discovered.Result.Detail is { } detail
            ? $"{row.Descriptor.DisplayName}, {detail}"
            : row.Descriptor.DisplayName;

    private string? GetBrowserUrl(IInterceptorSubject subject) =>
        PathResolver.GetPath(subject, PathStyle.Route) is { } routePath ? BrowserUrls.FromRoutePath(routePath) : null;

    public void Dispose()
    {
        _scanCancellation?.Cancel();
    }
}
```

Check that `SubjectPathResolver.GetPath(IInterceptorSubject, PathStyle)` is the overload `MetadataLinkResolver` uses (`HomeBlaze.Services/Metadata/MetadataLinkResolver.cs:88`), and that `MudBlazorIconResolver.Resolve` is the method `SubjectTypePicker.razor` uses for row icons. Adjust the calls if the signatures differ.

- [ ] **Step 2: Integrate the page into the dialog**

In `SubjectSetupDialog.razor`:

Add below the existing `@using` lines:

```razor
@using HomeBlaze.Abstractions.Discovery
@using HomeBlaze.Services.Discovery
```

Add below `@inject IJSRuntime JsRuntime`:

```razor
@inject SubjectDiscoveryService DiscoveryService
```

Replace the picker page `div` with:

```razor
        @* The picker stays in the tree while the configure page is shown, so its search and group survive "Back".
           The Discover page stays as well, so its results survive "Back". *@
        <div class="subject-setup-picker-page" hidden="@(_selectedType is not null)">
            @if (_isDiscoverAvailable)
            {
                <MudToggleGroup T="PickerMode"
                                Value="_pickerMode"
                                ValueChanged="OnPickerModeChanged"
                                SelectionMode="SelectionMode.SingleSelection"
                                Size="Size.Small"
                                Class="mb-2"
                                data-testid="picker-mode-toggle">
                    <MudToggleItem Value="PickerMode.Types" Text="All types" data-testid="picker-mode-types" />
                    <MudToggleItem Value="PickerMode.Discover" Text="Discover" data-testid="picker-mode-discover" />
                </MudToggleGroup>
            }
            <div class="subject-setup-picker-mode" hidden="@(_pickerMode != PickerMode.Types)">
                <SubjectTypePicker @ref="_typePicker" Types="_types" OnTypeSelected="SelectType" />
            </div>
            @if (_isDiscoverAvailable)
            {
                <div class="subject-setup-picker-mode" hidden="@(_pickerMode != PickerMode.Discover)">
                    <SubjectDiscoveryPage Types="_types"
                                          ExistingSubjects="_existingSubjects"
                                          IsActive="@(_pickerMode == PickerMode.Discover)"
                                          OnSelected="SelectDiscovered"
                                          OnNavigating="Cancel" />
                </div>
            }
        </div>
```

Add the discovery result to `SubjectEditPanel`:

```razor
                <SubjectEditPanel @key="_createdSubject"
                                  @ref="_editPanel"
                                  Subject="_createdSubject"
                                  IsCreating="true"
                                  DiscoveryResult="_discoveredSubject?.Result"
                                  IsValidChanged="OnEditPanelIsValidChanged" />
```

In the `<style>` block, add after the `.subject-setup-picker-content > .subject-setup-picker-page` rule:

```css
    /* :not([hidden]) keeps display: flex from overriding the hidden attribute. */
    .subject-setup-picker-page > .subject-setup-picker-mode:not([hidden]) {
        display: flex;
        flex-direction: column;
        flex: 1 1 auto;
        min-height: 0;
    }
```

In `@code`, add the fields below `private IReadOnlyList<SubjectTypeDescriptor> _types = [];`:

```csharp
    private IReadOnlyCollection<IInterceptorSubject> _existingSubjects = [];
    private bool _isDiscoverAvailable;
    private PickerMode _pickerMode = PickerMode.Types;
    private DiscoveredSubject? _discoveredSubject;
```

Replace the start of `OnInitialized` up to and including `_types = TypeCatalog.GetCreatableTypes(existingSubjects);` with:

```csharp
        _existingSubjects =
            RootManager.Root?.Context.TryGetService<ISubjectRegistry>()?.KnownSubjects.Keys.ToArray()
            ?? [];

        _types = TypeCatalog.GetCreatableTypes(_existingSubjects);
        _isDiscoverAvailable = Options.Type is null && DiscoveryService.HasDiscoverers(_types.Select(descriptor => descriptor.Type).ToArray());
```

Replace `SelectType` with:

```csharp
    private void SelectType(SubjectTypeDescriptor descriptor)
    {
        ShowConfigurePage(descriptor, null);
    }

    private void SelectDiscovered(DiscoveredSubject discoveredSubject)
    {
        if (_types.FirstOrDefault(descriptor => descriptor.Type == discoveredSubject.SubjectType) is { } descriptor)
        {
            ShowConfigurePage(descriptor, discoveredSubject);
        }
    }

    private void ShowConfigurePage(SubjectTypeDescriptor descriptor, DiscoveredSubject? discoveredSubject)
    {
        DiscardCreatedSubject();

        try
        {
            _createdSubject = SubjectFactory.CreateSubject(descriptor.Type);
        }
        catch (Exception exception)
        {
            Snackbar.Add($"Failed to create subject: {exception.Message}", Severity.Error);
            return;
        }

        _selectedType = descriptor;
        _discoveredSubject = discoveredSubject;
        _isEditPanelValid = false;

        if (Options.NameField is { } nameOptions && !_isNameEdited)
        {
            _subjectName = discoveredSubject is null
                ? nameOptions.SuggestName(descriptor.Type)
                : nameOptions.SuggestName(discoveredSubject.Result.DisplayName, descriptor.Type);
        }

        _nameError = ValidateName(_subjectName);
        _isNameFocusPending = Options.NameField is not null;
        RefreshDialog();
    }
```

`OnInitialized` still calls `SelectType(SubjectTypeCatalog.Describe(type))` for a fixed type, which now forwards to `ShowConfigurePage`.

In `GoBack`, add `_discoveredSubject = null;` after `_selectedType = null;`, and replace `_isSearchFocusPending = _hasFinePointer;` with:

```csharp
        // Back returns to the page the type was picked on; only the type list has a search to focus.
        _isSearchFocusPending = _hasFinePointer && _pickerMode == PickerMode.Types;
```

In `OnAfterRenderAsync`, replace `_isSearchFocusPending = _hasFinePointer && _selectedType is null;` with:

```csharp
            _isSearchFocusPending = _hasFinePointer && _selectedType is null && _pickerMode == PickerMode.Types;
```

Add before `Dispose`:

```csharp
    private void OnPickerModeChanged(PickerMode pickerMode)
    {
        _pickerMode = pickerMode;
        _isSearchFocusPending = _hasFinePointer && pickerMode == PickerMode.Types;
    }

    private enum PickerMode
    {
        Types,
        Discover
    }
```

- [ ] **Step 3: Build and run the Components tests**

Run: `dotnet build src/Namotion.Interceptor.slnx && dotnet test src/HomeBlaze/HomeBlaze.Components.Tests`
Expected: build succeeds; tests pass except `PublicApi`, which shows `SubjectDiscoveryPage` as new. Review the diff and accept it:

```bash
mv src/HomeBlaze/HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.received.txt src/HomeBlaze/HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.verified.txt
dotnet test src/HomeBlaze/HomeBlaze.Components.Tests
```
Expected: PASS.

- [ ] **Step 4: Check the existing dialog E2E tests**

The dialog changed for every type, so its E2E tests must still pass.

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~SubjectSetupDialogTests"`
Expected: PASS. The toggle appears now, because the device libraries register discoverers in Tasks 13 to 16. Until then it stays hidden, so run this again after Task 16.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components src/HomeBlaze/HomeBlaze.Components.Tests
git commit -m "feat: add a Discover page to the create dialog"
```

---

### Task 13: FRITZ!Box discoverer

**Files:**
- Modify: `src/HomeBlaze/Namotion.Devices.FritzBox/FritzBoxDiscovery.cs`
- Modify: `src/HomeBlaze/Namotion.Devices.FritzBox/Namotion.Devices.FritzBox.csproj`
- Modify: `src/HomeBlaze/Namotion.Devices.FritzBox/FritzBox.cs`
- Create: `src/HomeBlaze/Namotion.Devices.FritzBox/FritzBoxDiscoverer.cs`
- Modify: `src/HomeBlaze/Namotion.Devices.FritzBox.HomeBlaze/FritzBoxSetupComponent.razor`
- Create: `src/HomeBlaze/Namotion.Devices.FritzBox.Tests/FritzBoxDiscovererTests.cs`
- Modify: `src/HomeBlaze/Namotion.Devices.FritzBox.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] **Step 1: Write the failing tests**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FritzBoxDiscovererTests
{
    private static FritzBox CreateRouter(string? hostAddress, string? udn) =>
        new(NullLogger<FritzBox>.Instance) { HostAddress = hostAddress, Udn = udn };

    private static FritzBoxDiscoveryResult Found(string hostAddress, string? udn) =>
        new(hostAddress, "FRITZ!Box 7590", "FRITZ!Box 7590", udn, IsRepeater: false);

    [Fact]
    public void WhenBothHaveAUdn_ThenTheUdnDecides()
    {
        // Act & Assert
        Assert.True(FritzBoxDiscoverer.IsSameRouter(CreateRouter("192.168.178.1", "uuid:a"), Found("192.168.178.2", "UUID:A")));
        Assert.False(FritzBoxDiscoverer.IsSameRouter(CreateRouter("192.168.178.1", "uuid:a"), Found("192.168.178.1", "uuid:b")));
    }

    [Fact]
    public void WhenTheRouterHasNoUdn_ThenTheHostAddressDecides()
    {
        // Act & Assert
        Assert.True(FritzBoxDiscoverer.IsSameRouter(CreateRouter(" Fritz.Box ", null), Found("fritz.box", "uuid:a")));
        Assert.False(FritzBoxDiscoverer.IsSameRouter(CreateRouter("192.168.178.1", null), Found("192.168.178.2", "uuid:a")));
    }

    [Fact]
    public void WhenAResultDescribesARepeater_ThenItsDetailSaysSo()
    {
        // Arrange
        var result = new FritzBoxDiscoveryResult("192.168.178.3", null, "FRITZ!Repeater 1200", "uuid:r", IsRepeater: true);

        // Act & Assert
        Assert.Equal("uuid:r", result.Key);
        Assert.Equal("FRITZ!Repeater 1200", result.DisplayName);
        Assert.Equal("192.168.178.3 (repeater)", result.Detail);
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.FritzBox.Tests --filter "FullyQualifiedName~FritzBoxDiscovererTests"`
Expected: build error, `Udn`, `FritzBoxDiscoverer` and `Key` do not exist.

- [ ] **Step 3: Implement**

In `FritzBoxDiscovery.cs`:

- Replace `using Rssdp;` with `using HomeBlaze.Abstractions.Discovery;` and remove `using System.Net.Sockets;` if nothing else uses it.
- Make the result a discovery result:

```csharp
public sealed record FritzBoxDiscoveryResult(string HostAddress, string? FriendlyName, string? Model, string? Udn, bool IsRepeater)
    : IDiscoveryResult
{
    /// <inheritdoc />
    public string Key => Udn ?? HostAddress;

    /// <inheritdoc />
    public string DisplayName => FriendlyName ?? Model ?? FritzBox.DefaultName;

    /// <inheritdoc />
    public string? Detail => IsRepeater ? $"{HostAddress} (repeater)" : HostAddress;
}
```

- Change `DiscoverAsync` to take the search. Replace its documentation and the start of its body up to `using var httpClient` with:

```csharp
    /// <summary>
    /// Searches with <paramref name="ssdpSearch"/> and asks <c>fritz.box</c>, then describes each router found in
    /// parallel. Never throws for a location that does not answer or does not describe a FRITZ!Box: it is left out
    /// of the result. Cancellation still propagates.
    /// </summary>
    /// <param name="ssdpSearch">The SSDP search to use.</param>
    /// <param name="timeout">How long to wait for each description request.</param>
    /// <param name="cancellationToken">Cancels the search and every description request.</param>
    /// <returns>
    /// The routers found, de-duplicated by unique device name, or by host address when that is absent, with
    /// repeaters last. A router found by both SSDP and the <c>fritz.box</c> fallback is kept once, under its SSDP
    /// host address.
    /// </returns>
    public static async Task<IReadOnlyList<FritzBoxDiscoveryResult>> DiscoverAsync(
        ISsdpSearch ssdpSearch, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var hosts = new List<string>();
        foreach (var response in await ssdpSearch.SearchAsync(SearchTarget, cancellationToken))
        {
            hosts.Add(response.Location.Host);
        }
```

  The rest of the method (from `using var httpClient`) stays unchanged.

- Run `grep -rn "Rssdp" src/HomeBlaze/Namotion.Devices.FritzBox --include=*.cs`. If it finds nothing, remove `<PackageReference Include="Rssdp" />` from `Namotion.Devices.FritzBox.csproj`.

In `FritzBox.cs`, add after the `HostAddress` property:

```csharp
    /// <summary>
    /// The unique device name from the TR-064 description of the router, which discovery recognizes it by, or null.
    /// </summary>
    [Configuration]
    public partial string? Udn { get; set; }
```

`FritzBoxDiscoverer.cs`:

```csharp
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// Finds FRITZ!Box routers and repeaters over SSDP and at <c>fritz.box</c>.
/// </summary>
public sealed class FritzBoxDiscoverer : SubjectDiscoverer<FritzBox, FritzBoxDiscoveryResult>
{
    private static readonly TimeSpan DescriptionTimeout = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    protected override async IAsyncEnumerable<FritzBoxDiscoveryResult> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var result in await FritzBoxDiscovery.DiscoverAsync(scan.Ssdp, DescriptionTimeout, cancellationToken))
        {
            yield return result;
        }
    }

    /// <inheritdoc />
    protected override bool Matches(FritzBox existing, FritzBoxDiscoveryResult result) => IsSameRouter(existing, result);

    internal static bool IsSameRouter(FritzBox existing, FritzBoxDiscoveryResult result) =>
        existing.Udn is { } udn && result.Udn is not null
            ? string.Equals(udn, result.Udn, StringComparison.OrdinalIgnoreCase)
            : string.Equals(existing.HostAddress?.Trim(), result.HostAddress, StringComparison.OrdinalIgnoreCase);
}
```

In `FritzBoxSetupComponent.razor`:

- Change the header to:

```razor
@using HomeBlaze.Abstractions.Discovery
@attribute [SubjectComponent(SubjectComponentType.Setup, typeof(FritzBox))]
@implements ISubjectEditComponent
@implements ISubjectDiscoveryComponent
@implements IDisposable
@inject ISsdpSearch SsdpSearch
```

- Add the parameter next to `IsCreating`:

```csharp
    [Parameter]
    public IDiscoveryResult? DiscoveryResult { get; set; }
```

- Replace the `try` block of `OnInitializedAsync` with:

```csharp
        // Picked on the Discover page, which has searched already.
        if (DiscoveryResult is FritzBoxDiscoveryResult discovered)
        {
            _routers = [discovered];
            _selectedRouter = discovered;
            _hostAddress = discovered.HostAddress;
            return;
        }

        try
        {
            _routers = (await FritzBoxDiscovery.DiscoverAsync(SsdpSearch, TimeSpan.FromSeconds(3), _cancellationTokenSource.Token)).ToList();
        }
```

  The two `catch` clauses after it stay unchanged.

- In `SaveAsync`, after `RouterSubject.PublicKeyPin = result.PublicKeyPin;` add:

```csharp
            // Every edit of the address clears the selection, so a router picked from the list is the checked one.
            RouterSubject.Udn = _selectedRouter?.Udn;
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.FritzBox.Tests --filter "FullyQualifiedName~FritzBoxDiscovererTests|FullyQualifiedName~FritzBoxDiscoveryTests"`
Expected: PASS. `FritzBoxDiscoveryTests` only calls `DescribeAsync`, which did not change.

- [ ] **Step 5: Accept the public API snapshot**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.FritzBox.Tests --filter "FullyQualifiedName~PublicApi"`
Expected: FAIL with `FritzBoxDiscoverer`, `Udn`, the `IDiscoveryResult` members and the new `DiscoverAsync` signature. Review, then:

```bash
mv src/HomeBlaze/Namotion.Devices.FritzBox.Tests/VerifyChecksTests.PublicApi.received.txt src/HomeBlaze/Namotion.Devices.FritzBox.Tests/VerifyChecksTests.PublicApi.verified.txt
dotnet test src/HomeBlaze/Namotion.Devices.FritzBox.Tests
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.FritzBox src/HomeBlaze/Namotion.Devices.FritzBox.HomeBlaze src/HomeBlaze/Namotion.Devices.FritzBox.Tests
git commit -m "feat: discover FRITZ!Box routers in the create dialog"
```

---

### Task 14: Hue discoverer

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Philips.Hue/HueDiscoveredBridge.cs`
- Create: `src/HomeBlaze/Namotion.Devices.Philips.Hue/HueBridgeDiscoverer.cs`
- Modify: `src/HomeBlaze/Namotion.Devices.Philips.Hue/HueBridge.cs`
- Modify: `src/HomeBlaze/Namotion.Devices.Philips.Hue.HomeBlaze/HueBridgeSetupComponent.razor`
- Create: `src/HomeBlaze/Namotion.Devices.Philips.Hue.Tests/HueBridgeDiscovererTests.cs`
- Modify: `src/HomeBlaze/Namotion.Devices.Philips.Hue.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HueBridgeDiscovererTests
{
    private static MulticastDnsService Announcement(string? bridgeId) => new(
        "Philips Hue - 1A2B3C",
        "001788fffe1a2b3c.local",
        443,
        [IPAddress.Parse("192.168.1.20")],
        bridgeId is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["bridgeid"] = bridgeId });

    [Fact]
    public async Task WhenABridgeIsAnnounced_ThenItIsFoundAndMatchedToItsSubject()
    {
        // Arrange
        var browser = new AnsweringBrowser(Announcement("001788FFFE1A2B3C"), Announcement(null));
        var existing = new HueBridge(NullLogger<HueBridge>.Instance) { BridgeId = "001788fffe1a2b3c" };
        ISubjectDiscoverer discoverer = new HueBridgeDiscoverer();
        var scan = new DiscoveryScan(new EmptySsdpSearch(), browser, CancellationToken.None);

        // Act
        var discovered = await discoverer.DiscoverAsync(scan, [existing], CancellationToken.None).ToListAsync();

        // Assert
        var single = Assert.Single(discovered);
        var bridge = Assert.IsType<HueDiscoveredBridge>(single.Result);
        Assert.Equal("001788fffe1a2b3c", bridge.BridgeId);
        Assert.Equal("192.168.1.20", bridge.HostAddress);
        Assert.Equal("Philips Hue - 1A2B3C", bridge.DisplayName);
        Assert.Same(existing, single.ExistingSubject);
        Assert.Equal([HueBridgeDiscoverer.ServiceType], browser.ServiceTypes);
    }

    private sealed class AnsweringBrowser(params MulticastDnsService[] services) : IMulticastDnsBrowser
    {
        public List<string> ServiceTypes { get; } = [];

        public Task<IReadOnlyList<MulticastDnsService>> BrowseAsync(string serviceType, CancellationToken cancellationToken)
        {
            ServiceTypes.Add(serviceType);
            return Task.FromResult<IReadOnlyList<MulticastDnsService>>(services);
        }
    }

    private sealed class EmptySsdpSearch : ISsdpSearch
    {
        public Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SsdpResponse>>([]);
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Philips.Hue.Tests --filter "FullyQualifiedName~HueBridgeDiscovererTests"`
Expected: build error, `HueBridgeDiscoverer` does not exist.

- [ ] **Step 3: Implement**

`HueDiscoveredBridge.cs`:

```csharp
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// A Hue bridge that announced itself on the local network.
/// </summary>
/// <param name="BridgeId">The bridge ID in lower case.</param>
/// <param name="HostAddress">The IP address of the bridge.</param>
/// <param name="Name">The name the bridge announces, such as "Philips Hue - 1A2B3C".</param>
public sealed record HueDiscoveredBridge(string BridgeId, string HostAddress, string Name) : IDiscoveryResult
{
    /// <inheritdoc />
    public string Key => BridgeId;

    /// <inheritdoc />
    public string DisplayName => Name;

    /// <inheritdoc />
    public string? Detail => HostAddress;
}
```

`HueBridgeDiscoverer.cs`:

```csharp
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// Finds Hue bridges over mDNS, without asking the Hue cloud.
/// </summary>
public sealed class HueBridgeDiscoverer : SubjectDiscoverer<HueBridge, HueDiscoveredBridge>
{
    internal const string ServiceType = "_hue._tcp";

    /// <inheritdoc />
    protected override async IAsyncEnumerable<HueDiscoveredBridge> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var service in await scan.MulticastDns.BrowseAsync(ServiceType, cancellationToken))
        {
            if (service.Properties.TryGetValue("bridgeid", out var bridgeId) && !string.IsNullOrWhiteSpace(bridgeId))
            {
                yield return new HueDiscoveredBridge(
                    bridgeId.Trim().ToLowerInvariant(), service.Addresses[0].ToString(), service.InstanceName);
            }
        }
    }

    /// <inheritdoc />
    protected override bool Matches(HueBridge existing, HueDiscoveredBridge result) =>
        string.Equals(existing.BridgeId, result.BridgeId, StringComparison.OrdinalIgnoreCase);
}
```

In `HueBridge.cs`, in `DiscoverBridgeAsync`, replace

```csharp
                bridge = bridges.FirstOrDefault(locatedBridge => locatedBridge.BridgeId == BridgeId);
```

with

```csharp
                // Locators and the mDNS announcement differ in the case of the ID.
                bridge = bridges.FirstOrDefault(locatedBridge =>
                    string.Equals(locatedBridge.BridgeId, BridgeId, StringComparison.OrdinalIgnoreCase));
```

In `HueBridgeSetupComponent.razor`:

- Change the header to:

```razor
@using HueApi
@using HueApi.BridgeLocator
@using HomeBlaze.Abstractions.Discovery

@attribute [SubjectComponent(SubjectComponentType.Setup, typeof(HueBridge))]
@implements ISubjectEditComponent
@implements ISubjectDiscoveryComponent
```

- Add the parameter next to `IsCreating`:

```csharp
    [Parameter]
    public IDiscoveryResult? DiscoveryResult { get; set; }
```

- At the start of `OnInitializedAsync` add:

```csharp
        // Picked on the Discover page, which has searched already; the bridge still needs its Link button.
        if (DiscoveryResult is HueDiscoveredBridge discovered)
        {
            _bridges = [new LocatedBridge { BridgeId = discovered.BridgeId, IpAddress = discovered.HostAddress }];
            _selectedBridge = _bridges[0];
            return;
        }
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Philips.Hue.Tests --filter "FullyQualifiedName~HueBridgeDiscovererTests"`
Expected: PASS.

- [ ] **Step 5: Accept the public API snapshot**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Philips.Hue.Tests --filter "FullyQualifiedName~PublicApi"`
Expected: FAIL with `HueBridgeDiscoverer` and `HueDiscoveredBridge`. Review, then:

```bash
mv src/HomeBlaze/Namotion.Devices.Philips.Hue.Tests/VerifyChecksTests.PublicApi.received.txt src/HomeBlaze/Namotion.Devices.Philips.Hue.Tests/VerifyChecksTests.PublicApi.verified.txt
dotnet test src/HomeBlaze/Namotion.Devices.Philips.Hue.Tests
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Philips.Hue src/HomeBlaze/Namotion.Devices.Philips.Hue.HomeBlaze src/HomeBlaze/Namotion.Devices.Philips.Hue.Tests
git commit -m "feat: discover Hue bridges in the create dialog"
```

---

### Task 15: Nuki discoverer

**Files:**
- Modify: `src/HomeBlaze/Namotion.Devices.Nuki/Namotion.Devices.Nuki.csproj`
- Modify: `src/HomeBlaze/Namotion.Devices.Nuki/NukiDiscoveredBridge.cs`
- Create: `src/HomeBlaze/Namotion.Devices.Nuki/NukiBridgeDiscoverer.cs`
- Modify: `src/HomeBlaze/Namotion.Devices.Nuki.HomeBlaze/NukiBridgeSetupComponent.razor`
- Create: `src/HomeBlaze/Namotion.Devices.Nuki.Tests/NukiBridgeDiscovererTests.cs`
- Modify: `src/HomeBlaze/Namotion.Devices.Nuki.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using System.Text;
using HomeBlaze.Abstractions.Discovery;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

public class NukiBridgeDiscovererTests
{
    [Theory]
    [InlineData("192.168.1.50", true)]
    [InlineData("192.168.1.50:8080", true)]
    [InlineData("http://192.168.1.50:8080", true)]
    [InlineData("192.168.1.50:8081", false)]
    [InlineData("192.168.1.51", false)]
    [InlineData(null, false)]
    public void WhenHostAddressesAreCompared_ThenHostAndPortDecide(string? hostAddress, bool expected)
    {
        // Arrange
        var result = new NukiDiscoveredBridge(2117604523, "192.168.1.50:8080");

        // Act
        var isSame = NukiBridgeDiscoverer.IsSameBridge(hostAddress, result);

        // Assert
        Assert.Equal(expected, isSame);
    }

    [Fact]
    public async Task WhenTheDiscoveryServiceListsABridge_ThenItIsFound()
    {
        // Arrange
        const string json = """{"bridges": [{"bridgeId": 2117604523, "ip": "192.168.1.50", "port": 8080}], "errorCode": 0}""";
        ISubjectDiscoverer discoverer = new NukiBridgeDiscoverer(new StubHttpClientFactory(json));
        var scan = new DiscoveryScan(new EmptySsdpSearch(), new EmptyMulticastDnsBrowser(), CancellationToken.None);

        // Act
        var discovered = await discoverer.DiscoverAsync(scan, [], CancellationToken.None).ToListAsync();

        // Assert
        var bridge = Assert.IsType<NukiDiscoveredBridge>(Assert.Single(discovered).Result);
        Assert.Equal("2117604523", bridge.Key);
        Assert.Equal("Nuki Bridge 2117604523", bridge.DisplayName);
        Assert.Equal("192.168.1.50:8080", bridge.Detail);
    }

    private sealed class StubHttpClientFactory(string json) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(json));
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private sealed class EmptySsdpSearch : ISsdpSearch
    {
        public Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SsdpResponse>>([]);
    }

    private sealed class EmptyMulticastDnsBrowser : IMulticastDnsBrowser
    {
        public Task<IReadOnlyList<MulticastDnsService>> BrowseAsync(string serviceType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MulticastDnsService>>([]);
    }
}
```

Before relying on the `http://` case, read `NukiBridgeClient.TryParseHostAddress`: if it rejects a scheme, change that `InlineData` to expect `false` only if `NukiBridge.HostAddress` documentation also rejects it (it says "optionally with a port (8080 by default) and an http scheme", so `true` is expected).

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Nuki.Tests --filter "FullyQualifiedName~NukiBridgeDiscovererTests"`
Expected: build error, `NukiBridgeDiscoverer` does not exist.

- [ ] **Step 3: Implement**

The discoverer takes an `IHttpClientFactory`, which the Nuki library does not reference yet. In `Namotion.Devices.Nuki.csproj`, add next to the other package references (the Shelly library references it the same way):

```xml
<PackageReference Include="Microsoft.Extensions.Http" />
```

Replace `NukiDiscoveredBridge.cs` with:

```csharp
using System.Globalization;
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Nuki;

/// <summary>
/// A bridge that the Nuki discovery service lists for the public IP address of the caller.
/// </summary>
/// <param name="BridgeId">The ID the discovery service lists the bridge under.</param>
/// <param name="HostAddress">The address of the bridge as host:port.</param>
public sealed record NukiDiscoveredBridge(long BridgeId, string HostAddress) : IDiscoveryResult
{
    /// <inheritdoc />
    public string Key => BridgeId.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public string DisplayName => $"Nuki Bridge {Key}";

    /// <inheritdoc />
    public string? Detail => HostAddress;
}
```

Keep the existing XML documentation of the record's parameters if it says more than this.

`NukiBridgeDiscoverer.cs`:

```csharp
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;
using Namotion.Devices.Nuki.Client;

namespace Namotion.Devices.Nuki;

/// <summary>
/// Finds Nuki bridges through the Nuki discovery service, which lists the bridges seen from the public IP address of
/// the caller.
/// </summary>
public sealed class NukiBridgeDiscoverer : SubjectDiscoverer<NukiBridge, NukiDiscoveredBridge>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public NukiBridgeDiscoverer(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<NukiDiscoveredBridge> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpClient = _httpClientFactory.CreateClient();
        foreach (var bridge in await NukiBridgeDiscovery.DiscoverAsync(httpClient, cancellationToken))
        {
            yield return bridge;
        }
    }

    /// <inheritdoc />
    protected override bool Matches(NukiBridge existing, NukiDiscoveredBridge result) =>
        IsSameBridge(existing.HostAddress, result);

    internal static bool IsSameBridge(string? hostAddress, NukiDiscoveredBridge result) =>
        NukiBridgeClient.TryParseHostAddress(hostAddress, out var host, out var port, out _) &&
        NukiBridgeClient.TryParseHostAddress(result.HostAddress, out var resultHost, out var resultPort, out _) &&
        port == resultPort &&
        string.Equals(host, resultHost, StringComparison.OrdinalIgnoreCase);
}
```

In `NukiBridgeSetupComponent.razor`:

- Add `@using HomeBlaze.Abstractions.Discovery` and `@implements ISubjectDiscoveryComponent` to the header.
- Add the parameter next to `IsCreating`:

```csharp
    [Parameter]
    public IDiscoveryResult? DiscoveryResult { get; set; }
```

- At the start of `OnInitializedAsync` add:

```csharp
        // Picked on the Discover page, which has asked the discovery service already.
        if (DiscoveryResult is NukiDiscoveredBridge discovered)
        {
            _bridges = [discovered];
            _selectedBridge = discovered;
            _hostAddress = discovered.HostAddress;
            return;
        }
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Nuki.Tests --filter "FullyQualifiedName~NukiBridgeDiscovererTests|FullyQualifiedName~NukiBridgeDiscoveryTests"`
Expected: PASS.

- [ ] **Step 5: Accept the public API snapshot**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Nuki.Tests --filter "FullyQualifiedName~PublicApi"`
Expected: FAIL with `NukiBridgeDiscoverer` and the `IDiscoveryResult` members. Review, then:

```bash
mv src/HomeBlaze/Namotion.Devices.Nuki.Tests/VerifyChecksTests.PublicApi.received.txt src/HomeBlaze/Namotion.Devices.Nuki.Tests/VerifyChecksTests.PublicApi.verified.txt
dotnet test src/HomeBlaze/Namotion.Devices.Nuki.Tests
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Nuki src/HomeBlaze/Namotion.Devices.Nuki.HomeBlaze src/HomeBlaze/Namotion.Devices.Nuki.Tests
git commit -m "feat: discover Nuki bridges in the create dialog"
```

---

### Task 16: Shelly discoverer

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Shelly/ShellyDiscoveredDevice.cs`
- Create: `src/HomeBlaze/Namotion.Devices.Shelly/ShellyDeviceDiscoverer.cs`
- Modify: `src/HomeBlaze/Namotion.Devices.Shelly/ShellyDevice.cs`
- Modify: `src/HomeBlaze/Namotion.Devices.Shelly.HomeBlaze/ShellyDeviceEditComponent.razor`
- Create: `src/HomeBlaze/Namotion.Devices.Shelly.Tests/ShellyDeviceDiscovererTests.cs`
- Modify: `src/HomeBlaze/Namotion.Devices.Shelly.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Shelly.Model;
using Xunit;

namespace Namotion.Devices.Shelly.Tests;

public class ShellyDeviceDiscovererTests
{
    private static ShellyDevice CreateDevice() => new(new TestHttpClientFactory(), NullLogger<ShellyDevice>.Instance);

    private static ShellyDiscoveredDevice Found(string deviceId = "shellyplus1pm-a8032ab12345", string hostAddress = "192.168.1.41") =>
        new(deviceId, hostAddress, "Plus1PM");

    [Fact]
    public void WhenAShellyIsAnnounced_ThenItsDeviceIdAddressAndApplicationAreRead()
    {
        // Arrange
        var service = new MulticastDnsService(
            "ShellyPlus1PM-A8032AB12345",
            "ShellyPlus1PM-A8032AB12345.local",
            80,
            [IPAddress.Parse("192.168.1.41")],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["gen"] = "2", ["app"] = "Plus1PM" });

        // Act
        var device = ShellyDeviceDiscoverer.ToDevice(service);

        // Assert
        Assert.NotNull(device);
        Assert.Equal(Found(), device);
        Assert.Equal("Plus1PM at 192.168.1.41", device.Detail);
    }

    [Fact]
    public void WhenTheSubjectHasADeviceId_ThenTheDeviceIdDecides()
    {
        // Arrange
        var device = CreateDevice();
        device.HostAddress = "192.168.1.99";
        device.DeviceId = "ShellyPlus1PM-A8032AB12345";

        // Act & Assert
        Assert.True(ShellyDeviceDiscoverer.IsSameDevice(device, Found()));
        Assert.False(ShellyDeviceDiscoverer.IsSameDevice(device, Found(deviceId: "shellyplus1pm-other")));
    }

    [Fact]
    public void WhenTheRunningSubjectReadItsDeviceId_ThenThatDecides()
    {
        // Arrange
        var device = CreateDevice();
        device.HostAddress = "192.168.1.99";
        device.DeviceInfo = new ShellyDeviceInfo { Id = "shellyplus1pm-a8032ab12345" };

        // Act & Assert
        Assert.True(ShellyDeviceDiscoverer.IsSameDevice(device, Found()));
    }

    [Fact]
    public void WhenTheSubjectHasNoDeviceId_ThenTheHostAddressDecides()
    {
        // Arrange
        var device = CreateDevice();
        device.HostAddress = " 192.168.1.41 ";

        // Act & Assert
        Assert.True(ShellyDeviceDiscoverer.IsSameDevice(device, Found()));
        Assert.False(ShellyDeviceDiscoverer.IsSameDevice(device, Found(hostAddress: "192.168.1.42")));
    }
}
```

`TestHttpClientFactory` already exists in this test project (used by `ShellyDeviceComponentParsingTests`). `ShellyDeviceInfo` lives in `Namotion.Devices.Shelly.Model`, and `ShellyDevice.DeviceInfo` is an `internal` property with a setter, which the test project reaches through `InternalsVisibleTo`.

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Shelly.Tests --filter "FullyQualifiedName~ShellyDeviceDiscovererTests"`
Expected: build error, `ShellyDeviceDiscoverer` does not exist.

- [ ] **Step 3: Implement**

`ShellyDiscoveredDevice.cs`:

```csharp
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Shelly;

/// <summary>
/// A Shelly device that announced itself on the local network.
/// </summary>
/// <param name="DeviceId">The device ID in lower case, such as <c>shellyplus1pm-a8032ab12345</c>.</param>
/// <param name="HostAddress">The IP address of the device.</param>
/// <param name="Application">The application the device announces, such as <c>Plus1PM</c>, or null.</param>
public sealed record ShellyDiscoveredDevice(string DeviceId, string HostAddress, string? Application) : IDiscoveryResult
{
    /// <inheritdoc />
    public string Key => DeviceId;

    /// <inheritdoc />
    public string DisplayName => DeviceId;

    /// <inheritdoc />
    public string? Detail => Application is null ? HostAddress : $"{Application} at {HostAddress}";
}
```

`ShellyDeviceDiscoverer.cs`:

```csharp
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Shelly;

/// <summary>
/// Finds Shelly devices of the second generation and later, which announce <c>_shelly._tcp</c> over mDNS.
/// </summary>
public sealed class ShellyDeviceDiscoverer : SubjectDiscoverer<ShellyDevice, ShellyDiscoveredDevice>
{
    internal const string ServiceType = "_shelly._tcp";

    /// <inheritdoc />
    protected override async IAsyncEnumerable<ShellyDiscoveredDevice> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var service in await scan.MulticastDns.BrowseAsync(ServiceType, cancellationToken))
        {
            if (ToDevice(service) is { } device)
            {
                yield return device;
            }
        }
    }

    /// <inheritdoc />
    protected override bool Matches(ShellyDevice existing, ShellyDiscoveredDevice result) => IsSameDevice(existing, result);

    internal static ShellyDiscoveredDevice? ToDevice(MulticastDnsService service)
    {
        if (string.IsNullOrWhiteSpace(service.InstanceName))
        {
            return null;
        }

        service.Properties.TryGetValue("app", out var application);
        return new ShellyDiscoveredDevice(
            service.InstanceName.Trim().ToLowerInvariant(),
            service.Addresses[0].ToString(),
            string.IsNullOrWhiteSpace(application) ? null : application);
    }

    internal static bool IsSameDevice(ShellyDevice existing, ShellyDiscoveredDevice result)
    {
        // A subject created before discovery existed has no stored ID, but a connected one read it from the device.
        var deviceId = existing.DeviceId ?? existing.DeviceInfo?.Id;
        return deviceId is not null
            ? string.Equals(deviceId, result.DeviceId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(existing.HostAddress?.Trim(), result.HostAddress, StringComparison.OrdinalIgnoreCase);
    }
}
```

In `ShellyDevice.cs`, add after the `HostAddress` property:

```csharp
    /// <summary>
    /// The device ID, such as <c>shellyplus1pm-a8032ab12345</c>, which discovery recognizes the device by, or null.
    /// </summary>
    [Configuration]
    public partial string? DeviceId { get; set; }
```

In `ShellyDeviceEditComponent.razor`:

- Change the header to:

```razor
@using Namotion.Interceptor
@using HomeBlaze.Abstractions.Discovery

@attribute [SubjectComponent(SubjectComponentType.Edit, typeof(ShellyDevice))]
@implements ISubjectEditComponent
@implements ISubjectDiscoveryComponent
```

- Add next to `IsCreating`:

```csharp
    [Parameter]
    public IDiscoveryResult? DiscoveryResult { get; set; }

    private string? _deviceId;
```

- At the end of the `if (DeviceSubject != null)` block in `OnInitialized`, after the `_original...` assignments, add:

```csharp
            // A device picked on the Discover page fills in the form; it is not dirty before the user saves.
            if (IsCreating && DiscoveryResult is ShellyDiscoveredDevice discovered)
            {
                _hostAddress = discovered.HostAddress;
                _deviceId = discovered.DeviceId;
            }
```

- In `SaveAsync`, after `DeviceSubject.HostAddress = _hostAddress;` add:

```csharp
            if (_deviceId is not null)
            {
                DeviceSubject.DeviceId = _deviceId;
            }
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Shelly.Tests --filter "FullyQualifiedName~ShellyDeviceDiscovererTests"`
Expected: 4 tests pass.

- [ ] **Step 5: Accept the public API snapshot**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Shelly.Tests --filter "FullyQualifiedName~PublicApi"`
Expected: FAIL with `ShellyDeviceDiscoverer`, `ShellyDiscoveredDevice` and `DeviceId`. Review, then:

```bash
mv src/HomeBlaze/Namotion.Devices.Shelly.Tests/VerifyChecksTests.PublicApi.received.txt src/HomeBlaze/Namotion.Devices.Shelly.Tests/VerifyChecksTests.PublicApi.verified.txt
dotnet test src/HomeBlaze/Namotion.Devices.Shelly.Tests
```
Expected: PASS.

- [ ] **Step 6: Run the unit tests and the dialog E2E tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS.

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~SubjectSetupDialogTests"`
Expected: PASS. The toggle is now shown; existing tests pick types from "All types", which stays the default.

- [ ] **Step 7: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Shelly src/HomeBlaze/Namotion.Devices.Shelly.HomeBlaze src/HomeBlaze/Namotion.Devices.Shelly.Tests
git commit -m "feat: discover Shelly devices in the create dialog"
```

---

### Task 17: E2E test

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.E2E.Tests/Models/DiscoverableTestSubject.cs`
- Create: `src/HomeBlaze/HomeBlaze.E2E.Tests/Models/DiscoverableTestEditComponent.cs`
- Create: `src/HomeBlaze/HomeBlaze.E2E.Tests/Models/DiscoverableTestDiscoverer.cs`
- Modify: `src/HomeBlaze/HomeBlaze.E2E.Tests/Infrastructure/PlaywrightFixture.cs`
- Modify: `src/HomeBlaze/HomeBlaze.E2E.Tests/SubjectSetupDialogTests.cs`

- [ ] **Step 1: Write the test types**

`DiscoverableTestSubject.cs`:

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// A subject that <see cref="DiscoverableTestDiscoverer"/> finds a device for.
/// </summary>
[DisplayName("Discoverable test device")]
[InterceptorSubject]
public partial class DiscoverableTestSubject : IConfigurable
{
    [Configuration]
    public partial string? HostAddress { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

`DiscoverableTestDiscoverer.cs`:

```csharp
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// Finds one fixed device, so that the Discover page has a result without a network.
/// </summary>
public sealed class DiscoverableTestDiscoverer : SubjectDiscoverer<DiscoverableTestSubject, DiscoverableTestResult>
{
    public const string DeviceName = "Discoverable test device 1";

    // From TEST-NET-1 (RFC 5737), which no real device uses.
    public const string HostAddress = "192.0.2.17";

    protected override async IAsyncEnumerable<DiscoverableTestResult> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new DiscoverableTestResult(DeviceName, HostAddress);
    }

    protected override bool Matches(DiscoverableTestSubject existing, DiscoverableTestResult result) =>
        existing.HostAddress == result.HostAddress;
}

public sealed record DiscoverableTestResult(string DisplayName, string HostAddress) : IDiscoveryResult
{
    public string Key => HostAddress;

    public string? Detail => HostAddress;
}
```

`DiscoverableTestEditComponent.cs`:

```csharp
using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Components.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Namotion.Interceptor;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// Shows the host address of a picked device and stores it. It is written without Razor, because the test project
/// has none.
/// </summary>
[SubjectComponent(SubjectComponentType.Edit, typeof(DiscoverableTestSubject))]
public class DiscoverableTestEditComponent : ComponentBase, ISubjectEditComponent, ISubjectDiscoveryComponent
{
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public bool IsCreating { get; set; }

    [Parameter]
    public IDiscoveryResult? DiscoveryResult { get; set; }

    private string? HostAddress =>
        (DiscoveryResult as DiscoverableTestResult)?.HostAddress ?? (Subject as DiscoverableTestSubject)?.HostAddress;

    public bool IsValid => !string.IsNullOrEmpty(HostAddress);

    public bool IsDirty => IsValid;

    // The validity is fixed by the parameters, so neither event is ever raised.
    public event Action<bool>? IsValidChanged { add { } remove { } }

    public event Action<bool>? IsDirtyChanged { add { } remove { } }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "data-testid", "discoverable-host");
        builder.AddContent(2, $"Host: {HostAddress}");
        builder.CloseElement();
    }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        if (Subject is DiscoverableTestSubject subject && HostAddress is { } hostAddress)
        {
            subject.HostAddress = hostAddress;
        }

        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Register the types**

In `PlaywrightFixture.InitializeAsync`, append to the type list passed to `WebTestingHostFactory<App>`:

```csharp
typeof(DiscoverableTestSubject), typeof(DiscoverableTestEditComponent), typeof(DiscoverableTestDiscoverer)
```

- [ ] **Step 3: Write the test**

Add to `SubjectSetupDialogTests`, next to the motor creation test:

```csharp
    [Fact]
    public async Task WhenAFoundDeviceIsPicked_ThenItsComponentStartsFromTheDeviceAndTheSubjectIsStored()
    {
        // The file is written to the test output directory, which later runs reuse, so it is removed again.
        var name = $"Discovered{Guid.NewGuid():N}";
        var filePath = Path.Combine(AppContext.BaseDirectory, "TestData", $"{name}.json");
        var isCompleted = false;
        try
        {
            // Arrange
            var page = await _fixture.CreatePageAsync();
            await OpenDialogAsync(page);
            await Dialog(page).GetByTestId("picker-mode-discover").ClickAsync();
            var row = Dialog(page).GetByTestId("discovery-row").Filter(new() { HasText = DiscoverableTestDiscoverer.DeviceName });
            await Assertions.Expect(row).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

            // Act
            await row.ClickAsync();
            await ExpectConfigurePageAsync(page);

            // Assert - the component and the name start from the device.
            await Assertions.Expect(Dialog(page).GetByTestId("discoverable-host"))
                .ToHaveTextAsync($"Host: {DiscoverableTestDiscoverer.HostAddress}", new() { Timeout = ElementVisibilityTimeout });
            await Assertions.Expect(NameInput(page)).ToHaveValueAsync(DiscoverableTestDiscoverer.DeviceName);

            // Act
            await NameInput(page).FillAsync(name);
            await Assertions.Expect(CreateButton(page)).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });
            await CreateButton(page).ClickAsync();

            // Assert
            await Assertions.Expect(Dialog(page)).Not.ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
            await _fixture.WaitForFileTextAsync(
                filePath,
                text => text.Contains(DiscoverableTestDiscoverer.HostAddress, StringComparison.Ordinal),
                "The created file does not contain the host address of the found device.");
            isCompleted = true;
        }
        finally
        {
            try
            {
                File.Delete(filePath);
                await _fixture.ReconcileStorageAsync();
            }
            catch (Exception exception) when (!isCompleted)
            {
                // The failure of the test itself is the one to report.
                _output.WriteLine($"Deleting {filePath} failed: {exception}");
            }
        }
    }
```

Add `using HomeBlaze.E2E.Tests.Models;` to the file if it is missing.

- [ ] **Step 4: Run the test**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~WhenAFoundDeviceIsPicked"`
Expected: PASS.

If the click on `picker-mode-discover` does not reach the toggle item, check the rendered HTML of `MudToggleItem` (it may not splat attributes onto a clickable element) and use `Dialog(page).GetByText("Discover", new() { Exact = true })` instead.

- [ ] **Step 5: Run the whole dialog suite**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~SubjectSetupDialogTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.E2E.Tests
git commit -m "test: create a subject from a device found on the Discover page"
```

---

### Task 18: Manual verification with real devices

Agreed while planning, as `AGENTS.md` asks for long-running and hardware checks. Run it before the pull request is finalized and report each item as passed, failed or not attempted.

- [ ] **Step 1: Run HomeBlaze directly on the host**

Run: `dotnet run --project src/HomeBlaze/HomeBlaze`

Open the create dialog, switch to Discover, and check:

1. A Shelly (Gen2 or later) appears. This settles the legacy unicast question from the spec. If none of the Shellys appear while `avahi-browse -rt _shelly._tcp` lists them, the devices do not answer legacy unicast queries: switch `MulticastDnsBrowser` to bind port 5353 with `SocketOptionName.ReuseAddress`, join `224.0.0.251` on each adapter, and send the queries from that socket. Add a commit for it.
2. The FRITZ!Box router appears; a repeater, if present, shows "(repeater)".
3. The Hue bridge appears under its announced name.
4. The Nuki bridge appears.
5. Picking each opens its page already filled in: FRITZ!Box and Nuki preselect the device, Hue shows the bridge waiting for the Link button, Shelly has the host address.
6. Creating each one works, and a scan afterwards shows it as "already added" with a working link.
7. A Shelly created before this change is shown as "already added" while it is connected.
8. Write down whether the Nuki discovery `bridgeId` equals the bridge's `ServerId` (state pane of the Nuki bridge).
9. A Hue bridge created through Discover connects (its runtime lookup compares IDs ignoring case).

- [ ] **Step 2: Run HomeBlaze in Docker on a bridge network**

Run: `docker compose -f src/HomeBlaze/docker-compose.yml up --build`

Check that the Discover page ends without devices (or with the Nuki bridge only, which needs no multicast), shows the hint, and that adding by address still works.

- [ ] **Step 3: Act on the Nuki finding**

If item 8 showed that `bridgeId` equals `ServerId`, add to `NukiBridgeDiscoverer.Matches` a match on `existing.ServerId == result.BridgeId`, with a test in `NukiBridgeDiscovererTests` that sets `ServerId` through the internal setter. Update the Nuki row of the spec. Commit:

```bash
git commit -am "feat: recognize a Nuki bridge by its ID in discovery"
```

If they differ, leave matching on the address and note the finding in the pull request.

---

### Task 19: Documentation

**Files:**
- Move: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/plans/subject-discovery.md` to `src/HomeBlaze/HomeBlaze/Data/Files/Docs/architecture/design/discovery.md`
- Delete: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/plans/subject-discovery-implementation.md`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/devices/FritzBox.md`, `PhilipsHue.md`, `Nuki.md`, `Shelly.md`
- Modify: `.claude/commands/create-homeblaze-library.md`

- [ ] **Step 1: Make the spec the design document**

```bash
git mv src/HomeBlaze/HomeBlaze/Data/Files/Docs/plans/subject-discovery.md src/HomeBlaze/HomeBlaze/Data/Files/Docs/architecture/design/discovery.md
git rm src/HomeBlaze/HomeBlaze/Data/Files/Docs/plans/subject-discovery-implementation.md
```

In `discovery.md`, set the front matter to `title: Subject Discovery`, `navTitle: Discovery`, `status: Implemented`, rename the "Scope" section to "Scope and follow-ups" and keep its follow-up and out-of-scope lists, and update any statement that Task 18 changed (legacy unicast, Nuki matching).

- [ ] **Step 2: Add a Discovery section to each device page**

Append to each device page a section like this, adapted per device:

`FritzBox.md`:

```markdown
## Discovery

The Discover page of the create dialog finds FRITZ!Box routers and repeaters over SSDP and at `fritz.box`. A router that is already added is recognized by its unique device name, or by its host address when it was added without discovery.
```

`PhilipsHue.md`:

```markdown
## Discovery

The Discover page of the create dialog finds Hue bridges over mDNS (`_hue._tcp`), without asking the Hue cloud. A bridge that is already added is recognized by its bridge ID. Its setup page still asks you to press the Link button.
```

`Nuki.md`:

```markdown
## Discovery

The Discover page of the create dialog asks the Nuki discovery service, which lists the bridges seen from your public IP address. It only does so when you open the Discover page. A bridge that is already added is recognized by its host address and port.
```

`Shelly.md`:

```markdown
## Discovery

The Discover page of the create dialog finds Shelly devices of the second generation and later over mDNS (`_shelly._tcp`). A device that is already added is recognized by its device ID, which a device added without discovery reports once it is connected, or by its host address.
```

Adjust the Nuki text if Task 18 added the bridge ID match.

- [ ] **Step 3: Describe discoverers in the library command**

In `.claude/commands/create-homeblaze-library.md`, add to the deliverables list after the Setup component item:

```markdown
   - Discoverer (`{SubjectName}Discoverer.cs` in the device library): only if the device announces itself (mDNS, SSDP) or a vendor service lists it. Derive from `SubjectDiscoverer<TSubject, TResult>`, let the result record implement `IDiscoveryResult`, query through `scan.Ssdp` or `scan.MulticastDns`, and implement the type's own `Matches` rule. The Setup component (or the Edit component when there is none) must implement `ISubjectDiscoveryComponent` and start from the picked result. See `docs/architecture/design/discovery.md` in the HomeBlaze docs.
```
- [ ] **Step 4: Commit**

```bash
git add -A src/HomeBlaze/HomeBlaze/Data/Files/Docs .claude/commands/create-homeblaze-library.md
git commit -m "docs: describe subject discovery"
```

---

### Task 20: Final verification

- [ ] **Step 1: Build and run the unit tests**

Run: `dotnet build src/Namotion.Interceptor.slnx && dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: build succeeds with 0 warnings; all tests pass.

- [ ] **Step 2: Run the HomeBlaze E2E tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: PASS.

- [ ] **Step 3: Confirm the manual verification ran**

Task 18 must be complete, with its results ready for the pull request description. The pull request lists the breaking change (`FritzBoxDiscovery.DiscoverAsync` takes an `ISsdpSearch`; migration: pass the `ISsdpSearch` from dependency injection) and the follow-up pull requests for Sonos, myStrom and Ecowitt.
