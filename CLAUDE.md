# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Namotion.Interceptor is a .NET library for creating trackable object models through automatic property interception using C# 13 partial properties and source generation. It enables property change tracking, derived property updates, and object graph management with zero runtime reflection.

## Development Commands

### Build and Test
- `dotnet build src/Namotion.Interceptor.slnx` - Build entire solution
- `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"` - Run unit tests (default)
- `dotnet test src/Namotion.Interceptor.slnx` - Run all tests including integration
- `dotnet pack src/Namotion.Interceptor.slnx` - Create NuGet packages

Only run integration tests when changing connector implementations (OPC UA, MQTT, WebSocket, etc.) or HomeBlaze UI — run those targeted per project, e.g.:
- `dotnet test src/Namotion.Interceptor.OpcUa.Tests`
- `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`

### Performance Testing
- `dotnet run --project src/Namotion.Interceptor.Benchmark -c Release` - Run performance benchmarks

## Architecture

### Core Components
- **Core Library**: `Namotion.Interceptor` - Base interfaces and execution engine (.NET Standard 2.0)
- **Source Generator**: `Namotion.Interceptor.Generator` - Compile-time code generation for `[InterceptorSubject]` classes
- **Extension Libraries**: Tracking, Registry, Validation, Hosting, Sources, Dynamic (.NET 9.0)

### Key Design Patterns
- **Chain of Responsibility**: `IReadInterceptor`/`IWriteInterceptor` middleware chain
- **Service Container**: `IInterceptorSubjectContext` for dependency injection and coordination
- **Source Generation**: Zero runtime reflection through compile-time code generation
- **Observable Streams**: System.Reactive integration for change notifications

### Project Structure
```
src/
├── Namotion.Interceptor/              # Core library with base interfaces
├── Namotion.Interceptor.Generator/    # Source generator for [InterceptorSubject]
├── Namotion.Interceptor.{Feature}/    # Extension libraries (Tracking, Registry, Connectors, etc.)
├── Namotion.Interceptor.{Feature}.Tests/  # Tests colocated per feature
├── Namotion.Interceptor.{Protocol}/   # Integration packages (AspNetCore, Blazor, OpcUa, Mqtt, WebSocket, GraphQL)
├── Namotion.Interceptor.Benchmark/    # BenchmarkDotNet performance tests
├── Namotion.Interceptor.Sample*/      # Example applications
└── HomeBlaze/                         # Full application built on the library
docs/                                  # Feature and connector documentation (read before changing a feature)
├── design/                            # Internal design documents
```

## Language Requirements

This codebase requires **C# 13 preview features** for partial properties. The `[InterceptorSubject]` attribute triggers source generation that creates interception logic at compile-time.

### Basic Usage Pattern
```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }

    [Derived]
    public string FullName => $"{FirstName} {LastName}";
}

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking();

var person = new Person(context);
```

## Configuration Extensions

The library uses a fluent configuration API:
- `WithFullPropertyTracking()` - Enable complete change detection
- `WithRegistry()` - Enable object graph navigation
- `WithDataAnnotationValidation()` - Enable automatic validation
- `WithLifecycle()` - Enable attach/detach callbacks

## Build Configuration

- **Global Settings**: `Directory.Build.props` with nullable enabled, warnings as errors
- **Target Frameworks**: .NET Standard 2.0 (core), .NET 9.0 (extensions)
- **Maturity**: Early development — smaller breaking changes are acceptable
- **CI/CD**: GitHub Actions with xUnit testing, coverage reporting, and NuGet publishing

## Key Dependencies

- Microsoft.CodeAnalysis.CSharp 4.14.0 (source generator)
- System.Reactive 6.0.1 (change tracking observables)
- Microsoft.Extensions.DependencyInjection.Abstractions (hosting)
- OPCFoundation.NetStandard.Opc.Ua.* 1.5.376.244 (industrial integration)

## Industrial Integration Focus

The library has specialized support for:
- **OPC UA**: Industrial automation protocol integration
- **MQTT**: IoT messaging patterns
- **ASP.NET Core**: Web API exposure
- **GraphQL**: Real-time subscription support
- **Blazor**: UI data binding components

## Design Priorities

1. **Correctness** (non-negotiable) — Thread-safe, no race conditions, guaranteed correct under concurrent reads and writes. Never trade correctness for anything else.
2. **Performance** — Minimize allocations and maximize throughput. Any code path that runs during normal operation is a potential hot path — only error recovery, contention fallbacks, and rare race-resolution paths can assume cold execution. Less readable patterns (generics to avoid boxing, `Span<T>` in APIs, object pooling, etc.) are acceptable when justified by benchmarks.
3. **API usability** — Easy to use correctly. Performance-oriented API shapes are fine when they serve priority 2.
4. **Code simplicity** — Readable, low-abstraction code where priorities 1–3 allow.

Priority 1 is absolute. Priorities 2–4 are trade-offs — discuss to find the right balance per case.

## Coding Style

- **Avoid abbreviations** in variable and parameter names unless the name is very long. Use descriptive names (e.g., `attribute` not `attr`).

## Test Conventions

- **Naming**: `When<Condition>_Then<ExpectedBehavior>` (e.g., `WhenDepthIsZero_ThenReturnsNoChildren`)
- **Structure**: Explicit `// Arrange`, `// Act`, `// Assert` comments separating each phase (use `// Act & Assert` for exception tests)