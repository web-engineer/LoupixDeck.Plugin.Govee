using LoupixDeck.Plugin.Govee.Govee;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Govee.Commands;

/// <summary>
/// A dial for one property of a light (or group): brightness, white temperature, hue or saturation.
/// Each turn moves the cached value at once, so the dial's indicator follows the hand; the light
/// service sends only the latest value at the pace Govee allows. Pressing the dial switches the
/// light on or off. Step sets how far one tick moves (default 2 %, 100 K, 5° or 5 %).
/// </summary>
internal sealed class DialCommand : GoveeCommandBase, IAdjustmentCommand
{
    public enum Property
    {
        Brightness,
        Temperature,
        Hue,
        Saturation
    }

    public const string BrightnessName = "Govee.Brightness";
    public const string TemperatureName = "Govee.Temperature";
    public const string HueName = "Govee.Hue";
    public const string SaturationName = "Govee.Saturation";

    private readonly Property _property;

    public DialCommand(GoveePlugin plugin, Property property) : base(plugin)
    {
        _property = property;
        Descriptor = property switch
        {
            Property.Brightness => Describe(BrightnessName, "Brightness dial", Glyphs.Brightness,
                "Turn to dim or brighten; press to switch the light on or off.", Device(), Step("2")),
            Property.Temperature => Describe(TemperatureName, "White temperature dial", Glyphs.WhiteBalance,
                "Turn for warmer or cooler white; press to switch the light on or off.", Device(), Step("100")),
            Property.Hue => Describe(HueName, "Colour dial", Glyphs.Hue,
                "Turn to move round the colour wheel; press to switch the light on or off.", Device(), Step("5")),
            _ => Describe(SaturationName, "Saturation dial", Glyphs.Saturation,
                "Turn from pastel to full colour; press to switch the light on or off.", Device(), Step("5"))
        };
    }

    public static string NameOf(Property property) => property switch
    {
        Property.Brightness => BrightnessName,
        Property.Temperature => TemperatureName,
        Property.Hue => HueName,
        _ => SaturationName
    };

    public override CommandDescriptor Descriptor { get; }

    public override ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder;

    /// <summary>Used from a key or a macro rather than a dial: the same as pressing the dial.</summary>
    public override Task Execute(CommandContext ctx) => ApplyReset(ctx);

    public Task ApplyReset(CommandContext ctx)
    {
        var on = CombinedState(Targets(ctx)).On != true;
        return ForEachLight(ctx, device => Lights.SetPower(device, on));
    }

    public Task ApplyAdjustment(CommandContext ctx, int ticks)
    {
        if (ticks == 0) return Task.CompletedTask;
        var step = Math.Max(1, IntParam(ctx, 1, DefaultStep));
        var devices = Targets(ctx);
        if (devices.Count == 0) return ForEachLight(ctx, _ => { });

        // A group follows its first light, so the lights converge on one value.
        var lead = CombinedState(devices);
        return ForEachLight(ctx, device =>
        {
            switch (_property)
            {
                case Property.Brightness:
                    var brightness = lead.On == false ? 0 : lead.Brightness ?? 50;
                    Lights.SetBrightness(device, brightness + ticks * step);
                    break;

                case Property.Temperature:
                    var (min, max) = device.KelvinRange;
                    var kelvin = lead.Kelvin is int k && k > 0 ? k : (min + max) / 2;
                    Lights.SetKelvin(device, kelvin + ticks * step);
                    break;

                case Property.Hue:
                    var (hue, saturation, _) = CurrentColor(lead).ToHsv();
                    Lights.SetColor(device, Rgb.FromHsv(hue + ticks * step, saturation < 0.05 ? 1 : saturation, 1));
                    break;

                case Property.Saturation:
                    var (h, s, _) = CurrentColor(lead).ToHsv();
                    Lights.SetColor(device, Rgb.FromHsv(h, s + ticks * step / 100.0, 1));
                    break;
            }
        });
    }

    public AdjustmentValue? GetValue(CommandContext ctx)
    {
        var devices = Targets(ctx);
        if (devices.Count == 0) return new AdjustmentValue(0, Plugin.NoLightsReason);

        var state = CombinedState(devices);
        if (state.Online == false) return new AdjustmentValue(0, "Offline");
        if (!state.HasData) return new AdjustmentValue(0, state.Error ?? "…");
        if (state.On == false) return new AdjustmentValue(0, "Off");

        switch (_property)
        {
            case Property.Brightness:
                return state.Brightness is { } b ? new AdjustmentValue(b / 100.0, $"{b}%") : new AdjustmentValue(0, "—");

            case Property.Temperature:
                var (min, max) = devices[0].KelvinRange;
                return state.Kelvin is int k && k > 0
                    ? new AdjustmentValue((k - min) / (double)Math.Max(1, max - min), $"{k}K")
                    : new AdjustmentValue(0, "Colour");

            case Property.Hue:
                var (hue, _, _) = CurrentColor(state).ToHsv();
                return new AdjustmentValue(hue / 360.0, $"{hue:0}°");

            default:
                var (_, saturation, _) = CurrentColor(state).ToHsv();
                return new AdjustmentValue(saturation, $"{saturation * 100:0}%");
        }
    }

    private int DefaultStep => _property switch
    {
        Property.Brightness => 2,
        Property.Temperature => 100,
        _ => 5
    };

    /// <summary>The colour to turn from; white light starts the colour wheel at red, full saturation.</summary>
    private static Rgb CurrentColor(DeviceState state) =>
        state.Kelvin is > 0 || state.Color is null ? new Rgb(255, 0, 0) : state.Color.Value;

    private static CommandParameter Step(string defaultValue) => new("Step", typeof(int)) { DefaultValue = defaultValue };
}
