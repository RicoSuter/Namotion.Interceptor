# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Namotion.Interceptor is a .NET library for creating trackable object models through automatic property interception using C# 13 partial properties and source generation. It enables property change tracking, derived property updates, and object graph management with zero runtime reflection.

## Development Commands

### Build and Test
- `dotnet test src/Namotion.Interceptor.sln` - Run all unit tests
- `dotnet build src/Namotion.Interceptor.sln` - Build entire solution
- `dotnet pack src/Namotion.Interceptor.sln` - Create NuGet packages

### Running Samples
- `dotnet run --project src/Namotion.Interceptor.SampleConsole` - Run console sample
- `dotnet run --project src/Extensions/Namotion.Interceptor.SampleBlazor` - Run Blazor sample

### Performance Testing
- `dotnet run --project src/Namotion.Interceptor.Benchmarks -c Release` - Run performance benchmarks

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
├── Namotion.Interceptor/           # Core library with base interfaces
├── Namotion.Interceptor.Generator/ # Source generator for [InterceptorSubject]
├── Namotion.Interceptor.{Feature}/ # Extension libraries (Tracking, Registry, etc.)
├── Extensions/                     # Integration packages (AspNetCore, Blazor, etc.)
├── Samples/                        # Example applications
└── Tests/                          # Unit test projects
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
- **Package Version**: 0.0.2 (early development)
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

## Performance Considerations

- All interception logic generated at compile-time (no runtime reflection)
- Dedicated benchmarking with BenchmarkDotNet
- Recent performance optimizations focused on allocation reduction
- Observable streams for efficient change propagation

## Coding Style

- **Avoid abbreviations** in variable and parameter names unless the name is very long. Use descriptive names (e.g., `location` not `loc`, `result` not `res`, `configuration` not `config`).