using System.Collections.Concurrent;
using LoupixDeck.Plugin.Govee.Govee;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Govee.Commands;

/// <summary>
/// A dial that steps through a light's (or group's) scenes or music modes: each click starts the next
/// one, and the indicator names it. Scenes share one slot in a light's queue, so a fast turn sends only
/// the one it stops on. A group steps through every name any of its lights has; a light without that
/// scene keeps what it shows. Pressing the dial switches the light on or off.
/// </summary>
internal sealed class StepDialCommand : GoveeCommandBase, IAdjustmentCommand
{
    public const string SceneName = "Govee.SceneDial";
    public const string MusicName = "Govee.MusicDial";

    private readonly bool _music;

    /// <summary>The scene each dial last started, so it carries on from there once the light has moved on.</summary>
    private readonly ConcurrentDictionary<string, string> _last = new(StringComparer.OrdinalIgnoreCase);

    public StepDialCommand(GoveePlugin plugin, bool music) : base(plugin)
    {
        _music = music;
        Descriptor = music
            ? Describe(MusicName, "Music mode dial", Glyphs.Music,
                "Turn to step through the music modes; press to switch the light on or off. Sensitivity 0–100.",
                Device(), new CommandParameter("Sensitivity", typeof(int)) { DefaultValue = "100" })
            : Describe(SceneName, "Scene dial", Glyphs.Scene,
                "Turn to step through the scenes; press to switch the light on or off. Kind: lightScene, diyScene or snapshot.",
                Device(), new CommandParameter("Kind", typeof(string)) { DefaultValue = Capability.LightScene });
    }

    public override CommandDescriptor Descriptor { get; }

    public override ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder;

    /// <summary>Used from a key or a macro rather than a dial: the same as pressing the dial.</summary>
    public override Task Execute(CommandContext ctx) => ApplyReset(ctx);

    public Task ApplyReset(CommandContext ctx)
    {
        var on = CombinedState(Targets(ctx)).On != true;
        return ForEachLight(ctx, device => Lights.SetPower(device, on));
    }

    public async Task ApplyAdjustment(CommandContext ctx, int ticks)
    {
        if (ticks == 0) return;
        var devices = Targets(ctx);
        if (devices.Count == 0)
        {
            await ForEachLight(ctx, _ => { }).ConfigureAwait(false);
            return;
        }

        var kind = Kind(ctx);
        await Lights.LoadOptionsAsync(devices, kind, CancellationToken.None).ConfigureAwait(false);
        var options = Lights.SharedOptions(devices, kind);
        if (options.Count == 0) return;

        // Carry on from the running scene, else from the one this dial last started; from nothing,
        // the first click right is the first scene and the first click left the last.
        var key = $"{Param(ctx, 0)}|{kind}";
        var from = IndexOf(options, CombinedState(devices).Scene ?? _last.GetValueOrDefault(key));
        var index = Wrap((from < 0 ? (ticks > 0 ? -1 : 0) : from) + ticks, options.Count);
        var name = options[index].Name;
        _last[key] = name;

        var sensitivity = IntParam(ctx, 1, 100);
        await ForEachLight(ctx, device =>
        {
            if (ParamCodec.FindByName(Lights.CachedOptions(device, kind), name) is not { } option) return;
            if (_music) Lights.SetMusicMode(device, option, sensitivity);
            else Lights.StartScene(device, kind, option);
        }).ConfigureAwait(false);
    }

    public AdjustmentValue? GetValue(CommandContext ctx)
    {
        var devices = Targets(ctx);
        if (devices.Count == 0) return new AdjustmentValue(0, Plugin.NoLightsReason);

        var state = CombinedState(devices);
        if (state.Online == false) return new AdjustmentValue(0, "Offline");

        var kind = Kind(ctx);
        var options = Lights.SharedOptions(devices, kind);
        if (options.Count == 0)
        {
            // Lists come from the network; ask for them so the next redraw has them.
            _ = Lights.LoadOptionsAsync(devices, kind, CancellationToken.None);
            return new AdjustmentValue(0, _music ? "No music modes" : "No scenes");
        }
        if (state.On == false) return new AdjustmentValue(0, "Off");

        var index = IndexOf(options, state.Scene);
        if (index < 0) return new AdjustmentValue(0, _music ? "Turn for music" : "Turn for scenes");
        return new AdjustmentValue(options.Count == 1 ? 1 : index / (double)(options.Count - 1), options[index].Name);
    }

    private string Kind(CommandContext ctx) => _music ? Capability.MusicMode : Param(ctx, 1) ?? Capability.LightScene;

    private static int IndexOf(IReadOnlyList<NamedOption> options, string? name)
    {
        if (name is null) return -1;
        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static int Wrap(int index, int count) => (index % count + count) % count;
}
