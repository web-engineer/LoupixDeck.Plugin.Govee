using LoupixDeck.Plugin.Govee.Govee;
using LoupixDeck.Plugin.Govee.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Govee.Commands;

/// <summary>
/// Shared plumbing for Govee commands. Every command takes the light(s) as its first parameter,
/// <c>Device</c>: a device id, several joined with "+", or "*" for all lights. The device menu fills
/// it in, so users never type an id unless they want a custom group.
/// </summary>
internal abstract class GoveeCommandBase(GoveePlugin plugin) : IPluginCommand
{
    public const string DeviceParameter = "Device";

    protected GoveePlugin Plugin { get; } = plugin;

    protected LightService Lights => Plugin.Lights;

    public abstract CommandDescriptor Descriptor { get; }

    public virtual ButtonTargets SupportedTargets => ButtonTargets.All;

    public abstract Task Execute(CommandContext ctx);

    /// <summary>How often keys redraw from the cache; state changes also redraw them at once.</summary>
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(5);

    protected static string? Param(CommandContext ctx, int index) =>
        ctx.Parameters.Length > index && !string.IsNullOrWhiteSpace(ctx.Parameters[index]) ? ctx.Parameters[index].Trim() : null;

    protected static int IntParam(CommandContext ctx, int index, int fallback) =>
        int.TryParse(Param(ctx, index), out var value) ? value : fallback;

    /// <summary>The lights the command acts on, marked as on screen so their state stays fresh.</summary>
    protected IReadOnlyList<GoveeDevice> Targets(CommandContext ctx)
    {
        var devices = Lights.Resolve(Param(ctx, 0));
        foreach (var device in devices) Lights.Touch(device);
        return devices;
    }

    /// <summary>Runs a control on every target light, or reports why there is none.</summary>
    protected Task ForEachLight(CommandContext ctx, Action<GoveeDevice> action)
    {
        var devices = Targets(ctx);
        if (devices.Count == 0)
        {
            Hint(ctx, Plugin.NoLightsReason);
            ctx.Host.Logger.Warn($"{Descriptor.CommandName}: no light matches \"{Param(ctx, 0)}\"");
            return Task.CompletedTask;
        }

        foreach (var device in devices)
        {
            try
            {
                action(device);
            }
            catch (Exception ex)
            {
                ctx.Host.Logger.Error($"{Descriptor.CommandName} on {device.Name} failed", ex);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>One state for a group of lights: on if any is on, otherwise the first light's.</summary>
    protected DeviceState CombinedState(IReadOnlyList<GoveeDevice> devices)
    {
        if (devices.Count == 1) return Lights.GetState(devices[0]);
        var states = devices.Select(Lights.GetState).ToList();
        var lit = states.FirstOrDefault(s => s.On == true);
        var first = lit ?? states.FirstOrDefault(s => s.HasData) ?? states[0];
        return first with
        {
            On = states.Any(s => s.On == true) ? true : states.All(s => s.On == false) ? false : first.On,
            Online = states.Any(s => s.Online != false),
            Error = states.Select(s => s.Error).FirstOrDefault(e => e is not null)
        };
    }

    /// <summary>A name for the target: the light's name, "All lights", or "3 lights".</summary>
    protected string Caption(CommandContext ctx, IReadOnlyList<GoveeDevice> devices) =>
        Param(ctx, 0) == DeviceSelector.All ? "All lights"
        : devices.Count == 1 ? devices[0].Name
        : $"{devices.Count} lights";

    /// <summary>Draws the reason a key cannot show a light (no key, unknown id), or returns false when there is a light.</summary>
    protected bool DrawUnavailable(IRenderCanvas canvas, IReadOnlyList<GoveeDevice> devices)
    {
        if (devices.Count > 0) return false;
        LightKey.DrawMessage(canvas, "lightbulb-question-outline", Plugin.NoLightsReason, LightKey.Faded);
        return true;
    }

    /// <summary>Short on-key feedback for a touch button; silent elsewhere.</summary>
    protected static void Hint(CommandContext ctx, string text)
    {
        if (ctx.Target == ButtonTargets.TouchButton && ctx.SourceIndex is { } slot)
        {
            ctx.Host.OverlayTouchText(slot, text, TimeSpan.FromSeconds(2));
        }
    }

    protected static CommandParameter Device() => new(DeviceParameter, typeof(string));

    protected static CommandDescriptor Describe(string name, string displayName, string icon, string description,
        params CommandParameter[] parameters) => new()
    {
        CommandName = name,
        DisplayName = displayName,
        Group = GoveePlugin.GroupName,
        Icon = icon,
        Description = description,
        ParameterTemplate = "(" + string.Join(",", parameters.Select(p => $"{{{p.Name}}}")) + ")",
        Parameters = parameters,
        // Listed per light through the device menu, which fills in the Device parameter.
        HiddenFromMenu = true,
        ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
    };
}

/// <summary>Material Design Icons code points for the command picker.</summary>
internal static class Glyphs
{
    public const string LightbulbOn = "\U000F06E8";
    public const string LightbulbGroup = "\U000F1253";
    public const string Brightness = "\U000F00DF";
    public const string Palette = "\U000F03D8";
    public const string WhiteBalance = "\U000F05A8";
    public const string Scene = "\U000F170D";
    public const string Music = "\U000F0387";
    public const string Toggle = "\U000F0521";
    public const string Segments = "\U000F1051";
    public const string Controls = "\U000F0570";
    public const string Hue = "\U000F0266";
    public const string Saturation = "\U000F174A";
}
