using Namotion.Interceptor.Attributes;

namespace Coffee;

#region CoffeeMachine
[InterceptorSubject]
public partial class CoffeeMachine
{
    public partial string Name { get; set; }

    public partial string SerialNumber { get; set; }

    public partial CoffeeMachineState State { get; set; }

    public partial string? ActiveRecipeName { get; set; }

    public partial int CupsBrewed { get; set; }

    public partial Boiler Boiler { get; set; }

    public partial Pump Pump { get; set; }

    public partial WaterTank WaterTank { get; set; }

    public partial BeanHopper BeanHopper { get; set; }

    public partial IReadOnlyDictionary<string, Recipe> Recipes { get; set; }

    #region Derived
    [Derived]
    public bool IsReady => State == CoffeeMachineState.Idle && Boiler.IsHot && !WaterTank.IsLow;

    [Derived]
    public string Status => State switch
    {
        CoffeeMachineState.Brewing => $"Brewing {ActiveRecipeName}",
        CoffeeMachineState.Descaling => "Descaling",
        CoffeeMachineState.Fault => "Fault",
        _ when WaterTank.IsLow => "Refill water",
        _ when !Boiler.IsHot => $"Heating {Boiler.Temperature:0} °C",
        _ => "Ready"
    };
    #endregion

    public CoffeeMachine()
    {
        Name = "Coffee Machine";
        SerialNumber = "CM-0001";
        Boiler = new Boiler();
        Pump = new Pump();
        WaterTank = new WaterTank();
        BeanHopper = new BeanHopper();
        Recipes = new Dictionary<string, Recipe>
        {
            ["Espresso"] = new() { Name = "Espresso", WaterAmount = 40, Temperature = 93 },
            ["Lungo"] = new() { Name = "Lungo", WaterAmount = 110, Temperature = 92 }
        };
    }

    #region Brew
    public void Brew(string recipeName)
    {
        if (!IsReady)
        {
            throw new InvalidOperationException($"The machine is not ready: {Status}.");
        }

        var recipe = Recipes[recipeName];
        State = CoffeeMachineState.Brewing;
        ActiveRecipeName = recipe.Name;
        Boiler.TargetTemperature = recipe.Temperature;
        Pump.IsRunning = true;
    }
    #endregion
}
#endregion
