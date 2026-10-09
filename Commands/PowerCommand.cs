using LoupixDeck.Plugin.Govee.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Govee.Commands;

/// <summary>
/// The main light key: switches the light (or group) and shows what it is doing, glowing in its
/// real colour and brightness, with the running scene when one was started from the deck.
/// Action: <c>toggle</c> (default), <c>on</c> or <c>off</c>.
/// </summary>
internal sealed class PowerCommand(GoveePlugin plugin) : GoveeCommandBase(plugin), IDisplayImageCommand
{
    public const string Name = "Govee.Power";
    public const string ActionParameter = "Action";

    public override CommandDescriptor Descriptor { get; } = Describe(Name, "Light on/off", Glyphs.LightbulbOn,
        "Switches the light and shows its colour, brightness and scene. Action: toggle, on or off.",
        Device(), new CommandParameter(ActionParameter, typeof(string)) { DefaultValue = "toggle" });

    public override async Task Execute(CommandContext ctx)
    {
        var devices = Targets(ctx);
        var action = Param(ctx, 1)?.ToLowerInvariant() ?? "toggle";
        if (action is not ("on" or "off")) await ReadUnknownAsync(devices).ConfigureAwait(false);
        // A group toggles together: off if any light is on, otherwise on.
        var on = action switch
        {
            "on" => true,
            "off" => false,
            _ => CombinedState(devices).On != true
        };
        await ForEachLight(ctx, device => Lights.SetPower(device, on)).ConfigureAwait(false);
    }

    /// <summary>A toggle needs to know where it starts; read lights whose state is not known yet (briefly).</summary>
    private async Task ReadUnknownAsync(IReadOnlyList<Govee.GoveeDevice> devices)
    {
        var unknown = devices.Where(d => Lights.GetState(d).On is null).ToList();
        if (unknown.Count == 0) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await Task.WhenAll(unknown.Select(d => Lights.RefreshStateAsync(d, cts.Token))).ConfigureAwait(false);
            // A LAN read answers asynchronously; give it a moment.
            if (unknown.Any(Lights.IsOnLan)) await Task.Delay(250, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Toggle from what is known.
        }
    }

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        var devices = Targets(ctx);
        if (DrawUnavailable(canvas, devices)) return true;

        var action = Param(ctx, 1)?.ToLowerInvariant();
        LightKey.DrawLight(canvas, Caption(ctx, devices), CombinedState(devices), action is "on" or "off" ? action.ToUpperInvariant() : null);
        return true;
    }
}
