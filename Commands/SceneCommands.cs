using LoupixDeck.Plugin.Govee.Govee;
using LoupixDeck.Plugin.Govee.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Govee.Commands;

/// <summary>
/// Starts one of a light's scenes, DIY scenes or snapshots. Kind is the Govee capability
/// (<c>lightScene</c>, <c>diyScene</c> or <c>snapshot</c>) and Scene the encoded value the device
/// menu fills in; Name is only the key's caption. Govee never reports which scene is running, so
/// the key lights up for the scene last started from the deck.
/// </summary>
internal sealed class SceneCommand(GoveePlugin plugin) : GoveeCommandBase(plugin), IDisplayImageCommand
{
    public const string Name = "Govee.Scene";

    public override CommandDescriptor Descriptor { get; } = Describe(Name, "Start scene", Glyphs.Scene,
        "Starts a scene, DIY scene or snapshot. Pick it from the light's menu.",
        Device(), new CommandParameter("Kind", typeof(string)) { DefaultValue = Capability.LightScene },
        new CommandParameter("Scene", typeof(string)), new CommandParameter("Name", typeof(string)));

    public override Task Execute(CommandContext ctx)
    {
        var kind = Param(ctx, 1) ?? Capability.LightScene;
        var token = Param(ctx, 2);
        var value = ParamCodec.DecodeValue(token);
        if (value is null)
        {
            Hint(ctx, "Pick a scene from the menu");
            return Task.CompletedTask;
        }

        return ForEachLight(ctx, device =>
        {
            // Prefer the device's own name for the scene; fall back to the key's caption.
            var option = ParamCodec.FindOption(Lights.CachedScenes(device, kind), token)
                         ?? new NamedOption(Param(ctx, 3) ?? "Scene", value);
            Lights.StartScene(device, kind, option);
        });
    }

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        var devices = Targets(ctx);
        if (DrawUnavailable(canvas, devices)) return true;

        var name = Param(ctx, 3) ?? "Scene";
        var state = CombinedState(devices);
        var symbol = Param(ctx, 1) switch
        {
            Capability.Snapshot => "camera-iris",
            Capability.DiyScene => "palette-advanced",
            _ => "movie-open-star"
        };
        LightKey.DrawAction(canvas, symbol, name, state.On == true && state.Scene == name, state.DisplayColor, state.Error);
        return true;
    }
}

/// <summary>Starts a music mode (the light follows the sound its microphone hears).</summary>
internal sealed class MusicCommand(GoveePlugin plugin) : GoveeCommandBase(plugin), IDisplayImageCommand
{
    public const string Name = "Govee.Music";

    public override CommandDescriptor Descriptor { get; } = Describe(Name, "Music mode", Glyphs.Music,
        "Starts a music mode: the light follows sound through its microphone. Sensitivity 0–100.",
        Device(), new CommandParameter("Mode", typeof(string)), new CommandParameter("Name", typeof(string)),
        new CommandParameter("Sensitivity", typeof(int)) { DefaultValue = "100" });

    public override Task Execute(CommandContext ctx)
    {
        var token = Param(ctx, 1);
        var sensitivity = IntParam(ctx, 3, 100);
        return ForEachLight(ctx, device =>
        {
            var mode = ParamCodec.FindOption(device.MusicModes, token)
                       ?? (ParamCodec.DecodeValue(token) is { } value ? new NamedOption(Param(ctx, 2) ?? "Music", value) : null);
            if (mode is not null) Lights.SetMusicMode(device, mode, sensitivity);
        });
    }

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        var devices = Targets(ctx);
        if (DrawUnavailable(canvas, devices)) return true;

        var name = Param(ctx, 2) ?? "Music";
        var state = CombinedState(devices);
        LightKey.DrawAction(canvas, "music-note", name, state.On == true && state.Scene == name, state.DisplayColor, state.Error);
        return true;
    }
}

/// <summary>
/// Switches one of a light's extra features: gradient, nightlight, a lamp's second light… Feature is
/// the Govee instance (e.g. <c>gradientToggle</c>); Action is toggle, on or off.
/// </summary>
internal sealed class FeatureCommand(GoveePlugin plugin) : GoveeCommandBase(plugin), IDisplayImageCommand
{
    public const string Name = "Govee.Feature";

    public override CommandDescriptor Descriptor { get; } = Describe(Name, "Feature on/off", Glyphs.Toggle,
        "Switches an extra feature of the light, such as gradient or nightlight. Action: toggle, on or off.",
        Device(), new CommandParameter("Feature", typeof(string)),
        new CommandParameter("Action", typeof(string)) { DefaultValue = "toggle" });

    public override Task Execute(CommandContext ctx)
    {
        var feature = Param(ctx, 1);
        if (feature is null) return Task.CompletedTask;
        var action = Param(ctx, 2)?.ToLowerInvariant() ?? "toggle";
        return ForEachLight(ctx, device =>
        {
            var on = action switch
            {
                "on" => true,
                "off" => false,
                _ => Lights.GetState(device).Features.GetValueOrDefault(feature) != true
            };
            Lights.SetFeature(device, feature, on);
        });
    }

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        var devices = Targets(ctx);
        if (DrawUnavailable(canvas, devices)) return true;

        var feature = Param(ctx, 1) ?? string.Empty;
        var state = CombinedState(devices);
        var on = state.Features.GetValueOrDefault(feature);
        LightKey.DrawAction(canvas, on ? "toggle-switch" : "toggle-switch-off-outline", FeatureNames.Label(feature), on, state.DisplayColor, state.Error);
        return true;
    }
}

/// <summary>
/// Colours some segments of a strip or lamp. Segments are 0-based: "0-4", "0+2+4", or "*" for all.
/// </summary>
internal sealed class SegmentColorCommand(GoveePlugin plugin) : GoveeCommandBase(plugin), IDisplayImageCommand
{
    public const string Name = "Govee.SegmentColor";

    public override CommandDescriptor Descriptor { get; } = Describe(Name, "Colour segments", Glyphs.Segments,
        "Colours some segments of a strip: Segments like 0-4 or 0+2+4 (counting from 0), Color as #RRGGBB.",
        Device(), new CommandParameter("Segments", typeof(string)) { DefaultValue = "0-4" },
        new CommandParameter("Color", typeof(string)) { DefaultValue = "#FF551C" });

    public override Task Execute(CommandContext ctx)
    {
        if (!Rgb.TryParse(Param(ctx, 2), out var color))
        {
            Hint(ctx, "Colour must be #RRGGBB");
            return Task.CompletedTask;
        }
        return ForEachLight(ctx, device =>
        {
            if (device.SegmentRange is not { } range) return;
            Lights.SetSegmentColor(device, ParamCodec.ParseSegments(Param(ctx, 1), range), color);
        });
    }

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        var devices = Targets(ctx);
        if (DrawUnavailable(canvas, devices)) return true;

        var color = Rgb.TryParse(Param(ctx, 2), out var c) ? c : Rgb.White;
        var state = CombinedState(devices);
        LightKey.DrawSwatch(canvas, color, $"Seg {Param(ctx, 1) ?? "*"}", false, "led-strip-variant", state.Error);
        return true;
    }
}

/// <summary>Readable names for Govee's toggle instances.</summary>
internal static class FeatureNames
{
    public static string Label(string instance) => instance switch
    {
        "gradientToggle" => "Gradient",
        "nightlightToggle" => "Nightlight",
        "dreamViewToggle" => "DreamView",
        "mainLightToggle" => "Main light",
        "backgroundLightToggle" => "Backlight",
        "sceneStageToggle" => "Scene stage",
        _ => Humanize(instance)
    };

    private static string Humanize(string instance)
    {
        var name = instance.EndsWith("Toggle", StringComparison.Ordinal) ? instance[..^6] : instance;
        if (name.Length == 0) return instance;
        var spaced = string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
        return char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }
}
