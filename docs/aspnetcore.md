# Namotion.Interceptor.AspNetCore

ASP.NET Core integration for exposing interceptor subjects as REST APIs with automatic JSON serialization and validation.

## Getting Started

### Installation

Add the `Namotion.Interceptor.AspNetCore` package to your ASP.NET Core project:

```xml
<PackageReference Include="Namotion.Interceptor.AspNetCore" Version="0.1.0" />
```

### Basic Usage

Map your subject to REST endpoints using `MapSubjectWebApis`:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register your subject
builder.Services.AddSingleton(sp =>
{
    var context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking();

    return new Sensor(context);
});

var app = builder.Build();

// Map REST endpoints
app.MapSubjectWebApis<Sensor>("/api/sensor");

app.Run();
```

This creates three endpoints:

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/sensor` | Returns the subject as JSON |
| `POST` | `/api/sensor` | Updates properties via JSON path |
| `GET` | `/api/sensor/structure` | Returns the subject structure as `SubjectUpdate` |

## Features

### GET - Read Subject State

Returns the entire subject graph as JSON, using camelCase property names:

```http
GET /api/sensor
```

Response:
```json
{
  "temperature": 25.5,
  "humidity": 60,
  "location": {
    "building": "A",
    "room": "101"
  }
}
```

Nested subjects and collections are serialized recursively.

### POST - Update Properties

Update one or more properties using JSON paths:

```http
POST /api/sensor
Content-Type: application/json

{
  "temperature": 26.0,
  "location.room": "102"
}
```

The endpoint:
1. Resolves JSON paths to properties (supports dot notation and array indexing)
2. Validates that paths exist and properties are writable
3. Runs registered `IPropertyValidator` validators
4. Applies all updates atomically

#### Error Responses

**Unknown paths:**
```json
{
  "detail": "Unknown property paths.",
  "unknownPaths": ["invalidProperty"]
}
```

**Read-only properties:**
```json
{
  "detail": "Attempted to change read only property.",
  "readOnlyPaths": ["derivedValue"]
}
```

**Validation errors:**
```json
{
  "detail": "Property updates have invalid values.",
  "errors": {
    "temperature": ["Value must be between -40 and 100"]
  }
}
```

### GET /structure - Subject Metadata

Returns a `SubjectUpdate` with complete type information and structure:

```http
GET /api/sensor/structure
```

Useful for clients that need to discover the subject schema dynamically.

## API Reference

### MapSubjectWebApis

```csharp
// Use registered service
app.MapSubjectWebApis<TSubject>(string path);

// Use custom selector
app.MapSubjectWebApis<TSubject>(
    Func<IServiceProvider, IInterceptorSubject> subjectSelector,
    string path);
```

### Extension Methods

#### ToJsonObject

Converts a subject to a `JsonObject`:

```csharp
var json = subject.ToJsonObject(jsonSerializerOptions);
```

#### GetJsonPath

Gets the JSON path for a property reference:

```csharp
var path = propertyReference.GetJsonPath(jsonSerializerOptions);
// Returns: "location.room" or "motors[0].speed"
```

Requires `WithParents()` to be configured on the context.

#### FindPropertyFromJsonPath

Resolves a JSON path to a property:

```csharp
var (subject, property) = rootSubject.FindPropertyFromJsonPath("location.room");
```

## Validation Integration

Register `IPropertyValidator` implementations to validate incoming updates:

```csharp
builder.Services.AddSingleton<IPropertyValidator, DataAnnotationPropertyValidator>();
```

The `DataAnnotationPropertyValidator` from `Namotion.Interceptor.Validation` validates against data annotations like `[Range]`, `[Required]`, etc.

## JSON Serialization

Property names are converted using the configured `JsonSerializerOptions.PropertyNamingPolicy`. By default, ASP.NET Core uses camelCase.

Configure custom serialization:

```csharp
builder.Services.Configure<JsonOptions>(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});
```

## OpenAPI Integration

Endpoints are automatically tagged with the subject type name for Swagger/OpenAPI grouping:

```csharp
app.MapSubjectWebApis<Sensor>("/api/sensor");
// Swagger shows endpoints under "Sensor" tag
```
