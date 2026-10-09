using LoupixDeck.Plugin.Govee.Govee;
using LoupixDeck.Plugin.Govee.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Govee.Commands;

/// <summary>
/// Sets brightness: an absolute percentage ("50") or a step ("+10", "-10"), which makes a pair of
/// keys a dimmer on devices without dials. Shows the level as a ring.
/// </summary>
internal sealed class SetBrightnessCommand(GoveePlugin plugin) : GoveeCommandBase(plugin), IDisplayImageCommand
{
    public const string Name = "Govee.SetBrightness";

    public override CommandDescriptor Descriptor { get; } = Describe(Name, "Set brightness", Glyphs.Brightness,
        "Sets brightness to a percentage (50), or steps it (+10 / -10).",
        Device(), new CommandParameter("Percent", typeof(string)) { DefaultValue = "50" });

    public override Task Execute(CommandContext ctx)
    {
        var (value, relative) = Amount.Parse(Param(ctx, 1), 50);
        return ForEachLight(ctx, device =>
        {
            var current = Lights.GetState(device).Brightness ?? 50;
            Lights.SetBrightness(device, relative ? current + value : value);
        });
    }

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        var devices = Targets(ctx);
        if (DrawUnavailable(canvas, devices)) return true;

        var state = CombinedState(devices);
        var (value, relative) = Amount.Parse(Param(ctx, 1), 50);
        if (relative)
        {
            LightKey.DrawAction(canvas, value >= 0 ? "brightness-7" : "brightness-5",
                $"{(value >= 0 ? "+" : "")}{value}%  {Caption(ctx, devices)}", false, error: state.Error);
            return true;
        }

        var active = state.On == true && state.Brightness == value;
        LightKey.DrawLevel(canvas, value / 100.0, $"{value}%", Caption(ctx, devices), state.DisplayColor, active);
        return true;
    }
}

/// <summary>Sets a colour (#RRGGBB). The swatch is ringed while the light shows that colour.</summary>
internal sealed class SetColorCommand(GoveePlugin plugin) : GoveeCommandBase(plugin), IDisplayImageCommand
{
    public const string Name = "Govee.SetColor";

    public override CommandDescriptor Descriptor { get; } = Describe(Name, "Set colour", Glyphs.Palette,
        "Sets the light to a colour, given as #RRGGBB.",
        Device(), new CommandParameter("Color", typeof(string)) { DefaultValue = "#FF551C" });

    public override Task Execute(CommandContext ctx)
    {
        if (!Rgb.TryParse(Param(ctx, 1), out var color))
        {
            Hint(ctx, "Colour must be #RRGGBB");
            return Task.CompletedTask;
        }
        return ForEachLight(ctx, device => Lights.SetColor(device, color));
    }

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        var devices = Targets(ctx);
        if (DrawUnavailable(canvas, devices)) return true;

        var color = Rgb.TryParse(Param(ctx, 1), out var c) ? c : Rgb.White;
        var state = CombinedState(devices);
        var active = state.On == true && state.Kelvin is not > 0 && state.Scene is null && state.Color == color;
        LightKey.DrawSwatch(canvas, color, Caption(ctx, devices), active, error: state.Error);
        return true;
    }
}

/// <summary>Sets a white temperature in kelvin ("2700"), or steps it ("+500").</summary>
internal sealed class SetWhiteCommand(GoveePlugin plugin) : GoveeCommandBase(plugin), IDisplayImageCommand
{
    public const string Name = "Govee.SetWhite";

    public override CommandDescriptor Descriptor { get; } = Describe(Name, "Set white", Glyphs.WhiteBalance,
        "Sets the light to white at a colour temperature in kelvin (2700 warm … 6500 cool), or steps it (+500 / -500).",
        Device(), new CommandParameter("Kelvin", typeof(string)) { DefaultValue = "4000" });

    public override Task Execute(CommandContext ctx)
    {
        var (value, relative) = Amount.Parse(Param(ctx, 1), 4000);
        return ForEachLight(ctx, device =>
        {
            var (min, max) = device.KelvinRange;
            var current = Lights.GetState(device).Kelvin is int k && k > 0 ? k : (min + max) / 2;
            Lights.SetKelvin(device, relative ? current + value : value);
        });
    }

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        var devices = Targets(ctx);
        if (DrawUnavailable(canvas, devices)) return true;

        var state = CombinedState(devices);
        var (value, relative) = Amount.Parse(Param(ctx, 1), 4000);
        if (relative)
        {
            LightKey.DrawSwatch(canvas, Rgb.FromKelvin(value >= 0 ? 6500 : 2200), $"{(value >= 0 ? "+" : "")}{value}K",
                false, "white-balance-sunny", state.Error);
            return true;
        }

        var active = state.On == true && state.Kelvin is { } kelvin && Math.Abs(kelvin - value) < 50 && state.Scene is null;
        LightKey.DrawSwatch(canvas, Rgb.FromKelvin(value), $"{value}K", active, "white-balance-sunny", state.Error);
        return true;
    }
}

/// <summary>Reads "50" as an absolute amount and "+10" / "-10" as a step.</summary>
internal static class Amount
{
    public static (int Value, bool Relative) Parse(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return (fallback, false);
        var trimmed = text.Trim().TrimEnd('%', 'K', 'k');
        var relative = trimmed.StartsWith('+') || trimmed.StartsWith('-');
        return int.TryParse(trimmed, out var value) ? (value, relative) : (fallback, false);
    }
}
