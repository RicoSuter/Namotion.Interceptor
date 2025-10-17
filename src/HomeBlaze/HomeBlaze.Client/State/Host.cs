using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Client.State;

[InterceptorSubject]
public partial class Host : BackgroundService
{
    private Host()
    {
        Things = ImmutableArray<Thing>.Empty;
    }
    
    public partial ImmutableArray<Thing> Things { get; set; }
    
    public void AddThing()
    {
        Things = Things.Add(new Thing
        {
            Date = DateOnly.FromDateTime(DateTime.Now),
            TemperatureC = Random.Shared.Next(-20, 55)
        });
    }
    
    public void Clear()
    {
        Things = Things.Clear();
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            try
            {
                var json = await File.ReadAllTextAsync("Data/Configuration.json", stoppingToken);
                var document = JsonDocument.Parse(json);
                PopulateFromJson(document.RootElement);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                foreach (var thing in Things)
                {
                    thing.TemperatureC = Random.Shared.Next(-20, 55);
                }
            }
        }
        catch (TaskCanceledException)
        {

        }
        finally
        {
            var json = JsonSerializer.Serialize(this);
            await File.WriteAllTextAsync("Data/Configuration.json", json, CancellationToken.None);
        }
    }

    private void PopulateFromJson(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            var propertyInfo = GetType().GetProperty(property.Name, 
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            
            if (propertyInfo != null && propertyInfo.CanWrite)
            {
                try
                {
                    var value = JsonSerializer.Deserialize(property.Value.GetRawText(), propertyInfo.PropertyType);
                    propertyInfo.SetValue(this, value);
                }
                catch
                {
                    // Skip properties that can't be set
                }
            }
        }
    }
}

[InterceptorSubject]
public partial class Thing
{
    public partial DateOnly Date { get; set; }

    public partial int TemperatureC { get; set; }

    public partial string? Summary { get; set; }
    
    [Derived]
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}