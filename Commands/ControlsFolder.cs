using LoupixDeck.Plugin.Govee.Govee;
using LoupixDeck.Plugin.Govee.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Govee.Commands;

/// <summary>Opens a light's controls page on the touch screen.</summary>
internal sealed class ControlsCommand(GoveePlugin plugin) : GoveeCommandBase(plugin), IDisplayImageCommand
{
    public const string Name = "Govee.Controls";

    public override CommandDescriptor Descriptor { get; } = Describe(Name, "Controls page", Glyphs.Controls,
        "Opens a page with the light's colours, whites, scenes and features; the dials control brightness, colour and white while it is open.",
        Device());

    public override Task Execute(CommandContext ctx)
    {
        var devices = Targets(ctx);
        if (devices.Count == 0)
        {
            Hint(ctx, Plugin.NoLightsReason);
            return Task.CompletedTask;
        }
        ctx.Host.OpenFolder(LightFolders.Controls(Plugin, ctx.Host, Param(ctx, 0)!, Caption(ctx, devices)));
        return Task.CompletedTask;
    }

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        var devices = Targets(ctx);
        if (DrawUnavailable(canvas, devices)) return true;
        var state = CombinedState(devices);
        LightKey.DrawAction(canvas, "view-grid-outline", Caption(ctx, devices), state.On == true, state.DisplayColor, state.Error);
        return true;
    }
}

/// <summary>One slot of a light page: how it looks and what a tap does.</summary>
internal sealed record FolderItem(Action<IRenderCanvas> Render, Func<Task>? OnPress = null, Func<IFolderProvider>? Opens = null);

/// <summary>
/// A page of slots that pages itself when there are more items than keys: the last key becomes
/// "More" and cycles through the pages. Redraws when any light changes.
/// </summary>
internal sealed class PagedFolder(
    IPluginHost host,
    LightService lights,
    string title,
    Func<IReadOnlyList<FolderItem>> items,
    IReadOnlyDictionary<int, RotaryOverride>? rotary = null,
    Func<Task>? onEnter = null) : FolderProviderBase
{
    private int _page;

    public override string Title => title;

    public override IReadOnlyDictionary<int, RotaryOverride> RotaryOverrides => rotary ?? base.RotaryOverrides;

    public override IReadOnlyList<FolderEntry> BuildEntries()
    {
        var all = items();
        var grid = host.FolderGrid;
        var capacity = grid.TotalSlots - 1; // one slot is the host's back key
        var perPage = all.Count > capacity ? capacity - 1 : capacity;
        var pages = Math.Max(1, (all.Count + perPage - 1) / perPage);
        _page %= pages;

        var entries = all.Skip(_page * perPage).Take(perPage)
            .Select((item, i) => new FolderEntry
            {
                SlotIndex = grid.SlotForIndex(i),
                Render = item.Render,
                OnPress = item.OnPress,
                OpensFolder = item.Opens?.Invoke()
            })
            .ToList();

        if (pages > 1)
        {
            var label = $"More {_page + 1}/{pages}";
            entries.Add(new FolderEntry
            {
                SlotIndex = grid.SlotForIndex(perPage),
                Render = canvas => LightKey.DrawAction(canvas, "chevron-right", label, false),
                OnPress = () =>
                {
                    _page = (_page + 1) % pages;
                    RaiseEntriesChanged();
                    return Task.CompletedTask;
                }
            });
        }
        return entries;
    }

    public override void OnEnter()
    {
        lights.Changed += RaiseEntriesChanged;
        if (onEnter is not null) _ = RunOnEnter(onEnter);
    }

    public override void OnExit() => lights.Changed -= RaiseEntriesChanged;

    private async Task RunOnEnter(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            host.Logger.Error($"Loading {title} failed", ex);
        }
        RaiseEntriesChanged();
    }
}

/// <summary>Builds the pages for a light (or group).</summary>
internal static class LightFolders
{
    /// <summary>Colours offered on the controls page; the name is the slot's caption.</summary>
    public static readonly IReadOnlyList<(string Name, Rgb Color)> Palette =
    [
        ("Red", new Rgb(255, 0, 0)),
        ("Orange", new Rgb(255, 110, 0)),
        ("Yellow", new Rgb(255, 210, 0)),
        ("Green", new Rgb(0, 255, 40)),
        ("Cyan", new Rgb(0, 230, 255)),
        ("Blue", new Rgb(0, 40, 255)),
        ("Purple", new Rgb(150, 0, 255)),
        ("Pink", new Rgb(255, 0, 140))
    ];

    public static readonly IReadOnlyList<int> Whites = [2700, 4000, 6500];

    public static IFolderProvider Controls(GoveePlugin plugin, IPluginHost host, string selector, string caption)
    {
        var lights = plugin.Lights;
        IReadOnlyList<GoveeDevice> Devices() => lights.Resolve(selector);
        DeviceState State() => Combined(lights, Devices());

        IReadOnlyList<FolderItem> Items()
        {
            var devices = Devices();
            if (devices.Count == 0) return [];
            var items = new List<FolderItem>
            {
                new(c => LightKey.DrawLight(c, caption, State()),
                    () => Each(devices, d => lights.SetPower(d, State().On != true)))
            };

            if (devices.Any(d => d.CanWhite))
            {
                items.AddRange(Whites.Select(k => new FolderItem(
                    c =>
                    {
                        var s = State();
                        LightKey.DrawSwatch(c, Rgb.FromKelvin(k), $"{k}K", s.On == true && s.Kelvin is { } sk && Math.Abs(sk - k) < 50 && s.Scene is null, "white-balance-sunny");
                    },
                    () => Each(devices, d => lights.SetKelvin(d, k)))));
            }

            if (devices.Any(d => d.CanColor))
            {
                items.AddRange(Palette.Select(p => new FolderItem(
                    c =>
                    {
                        var s = State();
                        LightKey.DrawSwatch(c, p.Color, p.Name, s.On == true && s.Kelvin is not > 0 && s.Scene is null && s.Color == p.Color);
                    },
                    () => Each(devices, d => lights.SetColor(d, p.Color)))));
            }

            // Lists that differ per model are offered for a single light only, straight after power so
            // they stay on the first page.
            if (devices.Count == 1)
            {
                var pages = new List<FolderItem>();
                var device = devices[0];
                foreach (var (instance, label, symbol) in SceneKinds)
                {
                    if (!device.Has(instance)) continue;
                    pages.Add(new FolderItem(
                        c => LightKey.DrawAction(c, symbol, $"{label} ›", false),
                        Opens: () => Scenes(plugin, host, device, instance, label)));
                }
                if (device.MusicModes.Count > 0)
                {
                    pages.Add(new FolderItem(c => LightKey.DrawAction(c, "music-note", "Music ›", false),
                        Opens: () => Music(plugin, host, device)));
                }
                if (device.Toggles.Any())
                {
                    pages.Add(new FolderItem(c => LightKey.DrawAction(c, "toggle-switch", "Features ›", false),
                        Opens: () => Features(plugin, host, device)));
                }
                items.InsertRange(1, pages);
            }
            return items;
        }

        return new PagedFolder(host, lights, caption, Items, DialOverrides(plugin, host, selector));
    }

    /// <summary>(instance, page title, symbol) of the scene-like lists a light can have.</summary>
    public static readonly IReadOnlyList<(string Instance, string Label, string Symbol)> SceneKinds =
    [
        (Capability.LightScene, "Scenes", "movie-open-star"),
        (Capability.DiyScene, "DIY scenes", "palette-advanced"),
        (Capability.Snapshot, "Snapshots", "camera-iris")
    ];

    private static IFolderProvider Scenes(GoveePlugin plugin, IPluginHost host, GoveeDevice device, string instance, string label)
    {
        var lights = plugin.Lights;
        IReadOnlyList<FolderItem> Items() => lights.CachedScenes(device, instance)
            .Select(scene => new FolderItem(
                c =>
                {
                    var s = lights.GetState(device);
                    LightKey.DrawAction(c, "movie-open-star", scene.Name, s.On == true && s.Scene == scene.Name, s.DisplayColor);
                },
                () =>
                {
                    lights.StartScene(device, instance, scene);
                    return Task.CompletedTask;
                }))
            .ToList();

        return new PagedFolder(host, lights, $"{device.Name}: {label}", Items, DialOverrides(plugin, host, device.Id),
            () => lights.GetScenesAsync(device, instance, CancellationToken.None));
    }

    private static IFolderProvider Music(GoveePlugin plugin, IPluginHost host, GoveeDevice device)
    {
        var lights = plugin.Lights;
        IReadOnlyList<FolderItem> Items() => device.MusicModes
            .Select(mode => new FolderItem(
                c =>
                {
                    var s = lights.GetState(device);
                    LightKey.DrawAction(c, "music-note", mode.Name, s.On == true && s.Scene == mode.Name, s.DisplayColor);
                },
                () =>
                {
                    lights.SetMusicMode(device, mode, 100);
                    return Task.CompletedTask;
                }))
            .ToList();
        return new PagedFolder(host, lights, $"{device.Name}: Music", Items, DialOverrides(plugin, host, device.Id));
    }

    private static IFolderProvider Features(GoveePlugin plugin, IPluginHost host, GoveeDevice device)
    {
        var lights = plugin.Lights;
        IReadOnlyList<FolderItem> Items() => device.Toggles
            .Select(toggle => new FolderItem(
                c =>
                {
                    var on = lights.GetState(device).Features.GetValueOrDefault(toggle.Instance);
                    LightKey.DrawAction(c, on ? "toggle-switch" : "toggle-switch-off-outline", FeatureNames.Label(toggle.Instance), on);
                },
                () =>
                {
                    lights.SetFeature(device, toggle.Instance, !lights.GetState(device).Features.GetValueOrDefault(toggle.Instance));
                    return Task.CompletedTask;
                }))
            .ToList();
        return new PagedFolder(host, lights, $"{device.Name}: Features", Items, DialOverrides(plugin, host, device.Id));
    }

    /// <summary>While a page is open, the dials are brightness, colour, white and saturation, in that order.</summary>
    private static IReadOnlyDictionary<int, RotaryOverride> DialOverrides(GoveePlugin plugin, IPluginHost host, string selector)
    {
        var properties = new[] { DialCommand.Property.Brightness, DialCommand.Property.Hue, DialCommand.Property.Temperature, DialCommand.Property.Saturation };
        var overrides = new Dictionary<int, RotaryOverride>();
        for (var index = 0; index < properties.Length; index++)
        {
            var dial = plugin.Dial(properties[index]);
            var rotaryIndex = index;
            CommandContext Context() => new()
            {
                Parameters = [selector],
                Target = ButtonTargets.RotaryEncoder,
                SourceIndex = rotaryIndex,
                Host = host
            };
            overrides[index] = new RotaryOverride
            {
                OnLeft = () => Turn(dial, Context(), -1),
                OnRight = () => Turn(dial, Context(), 1),
                OnPress = () => dial.ApplyReset(Context())
            };
        }
        return overrides;

        async Task Turn(DialCommand dial, CommandContext ctx, int ticks)
        {
            await dial.ApplyAdjustment(ctx, ticks).ConfigureAwait(false);
            // A folder has no dial indicator, so flash the value on the key next to the dial.
            var slot = host.GetTouchSlotForRotary(ctx.SourceIndex ?? 0);
            if (slot >= 0 && dial.GetValue(ctx)?.Text is { } text) host.OverlayTouchText(slot, text, TimeSpan.FromSeconds(1));
        }
    }

    private static DeviceState Combined(LightService lights, IReadOnlyList<GoveeDevice> devices)
    {
        if (devices.Count == 0) return DeviceState.Unknown;
        var states = devices.Select(lights.GetState).ToList();
        var lead = states.FirstOrDefault(s => s.On == true) ?? states[0];
        return lead with { On = states.Any(s => s.On == true) ? true : lead.On };
    }

    private static Task Each(IReadOnlyList<GoveeDevice> devices, Action<GoveeDevice> action)
    {
        foreach (var device in devices) action(device);
        return Task.CompletedTask;
    }
}
