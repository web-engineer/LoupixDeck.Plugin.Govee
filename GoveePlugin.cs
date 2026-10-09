using System.Net;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Govee.Commands;
using LoupixDeck.Plugin.Govee.Govee;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Govee;

/// <summary>
/// LoupixDeck plugin entry point. Owns the <see cref="LightService"/> (Govee cloud or the demo lights,
/// with LAN control as a fast path), exposes a fixed set of commands that take the light as a
/// parameter, and lists the user's lights as a menu so the parameter is filled in for them.
/// </summary>
public sealed class GoveePlugin : LoupixPlugin, IPluginSettingsPage, IMenuContributor
{
    public const string GroupName = "Govee";

    private const string ApiKeyKey = "api_key";
    private const string DemoKey = "demo";
    private const string LanKey = "lan";
    private const string LanAddressesKey = "lan_addresses";
    private const string PollSecondsKey = "poll_seconds";
    private const string DeviceCacheKey = "device_cache";

    private const string SupportUrl = "https://web-engineer.co.uk/contact";
    private const string IssuesUrl = "https://github.com/web-engineer/govee-loupixdeck/issues";
    private const string ApiKeyHelpUrl = "https://developer.govee.com/reference/apply-you-govee-api-key";

    private const int DefaultPollSeconds = 60;
    private static readonly TimeSpan MenuSceneTimeout = TimeSpan.FromSeconds(4);

    private readonly Lazy<IReadOnlyList<IPluginCommand>> _commands;
    private IPluginHost _host = null!;
    private LightService _lights = null!;
    private Settings _applied = null!;

    public GoveePlugin()
    {
        _commands = new Lazy<IReadOnlyList<IPluginCommand>>(BuildCommands, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "govee",
        Name = "Govee Lights",
        Version = new Version(0, 1, 0),
        SdkVersion = SdkInfo.Version,
        Author = "web-engineer",
        Description = "Control Govee lights from the deck: power, brightness, colour, white temperature, scenes and segments, with live state on the keys.",
        Icon = LoadEmbeddedIcon("LoupixDeck.Plugin.Govee.icon.png")
    };

    internal LightService Lights => _lights;

    /// <summary>Why a key has no light to show, in a few words.</summary>
    internal string NoLightsReason =>
        !_applied.Demo && string.IsNullOrWhiteSpace(_applied.ApiKey) ? "No API key"
        : _lights.Problem is { } problem ? problem
        : _lights.Devices.Count == 0 ? "No lights yet"
        : "Unknown light";

    internal DialCommand Dial(DialCommand.Property property) =>
        _commands.Value.OfType<DialCommand>().First(d => d.Descriptor.CommandName == DialCommand.NameOf(property));

    // ---- lifecycle ----

    public override void Initialize(IPluginHost host)
    {
        _host = host;
        _applied = ReadSettings();
        _lights = new LightService(CreateBackend(_applied), host.Logger.Info, (message, ex) => host.Logger.Error(message, ex))
        {
            CloudPollInterval = TimeSpan.FromSeconds(_applied.PollSeconds)
        };
        _lights.Changed += RefreshKeys;
        _lights.DevicesFetched += SaveDeviceCache;
        if (!_applied.Demo) _lights.LoadCachedDevices(LoadDeviceCache());
        _lights.UseLan(_applied.Lan ? _applied.LanAddresses : null);
        _lights.Start();

        host.Logger.Info($"Govee plugin started: {_lights.Backend.Name}, LAN {(_applied.Lan ? "on" : "off")}");
    }

    public override void Shutdown()
    {
        if (_lights is null) return;
        _lights.Changed -= RefreshKeys;
        _lights.DevicesFetched -= SaveDeviceCache;
        _lights.Dispose();
    }

    public override IEnumerable<IPluginCommand> GetCommands() => _commands.Value;

    private IReadOnlyList<IPluginCommand> BuildCommands() =>
    [
        new PowerCommand(this),
        new DialCommand(this, DialCommand.Property.Brightness),
        new DialCommand(this, DialCommand.Property.Temperature),
        new DialCommand(this, DialCommand.Property.Hue),
        new DialCommand(this, DialCommand.Property.Saturation),
        new SetBrightnessCommand(this),
        new SetColorCommand(this),
        new SetWhiteCommand(this),
        new SceneCommand(this),
        new MusicCommand(this),
        new FeatureCommand(this),
        new SegmentColorCommand(this),
        new ControlsCommand(this)
    ];

    public override IReadOnlyList<CommandGroupDescriptor> GetCommandGroups() =>
    [
        new CommandGroupDescriptor
        {
            Group = GroupName,
            Description = "Govee lights: power, brightness, colour, white, scenes and segments, with live state on the keys",
            Icon = Glyphs.LightbulbGroup
        }
    ];

    private void RefreshKeys()
    {
        foreach (var command in _commands.Value.Where(c => c is IDisplayImageCommand or IAdjustmentCommand))
        {
            _host.RequestButtonRefresh(command.Descriptor.CommandName);
        }
    }

    // ---- dial presets: one per light and property ----

    public override IEnumerable<DialPresetDescriptor> GetDialPresets()
    {
        if (_lights is null) yield break;
        foreach (var device in _lights.Lights)
        {
            foreach (var (property, label, glyph, supported) in DialKinds(device))
            {
                if (!supported) continue;
                yield return new DialPresetDescriptor
                {
                    Id = $"{property}:{device.Id}",
                    Name = $"{device.Name} · {label}",
                    Glyph = glyph,
                    Actions = WholeDial(DialCommand.NameOf(property), device.Id)
                };
            }
        }
    }

    private static IEnumerable<(DialCommand.Property Property, string Label, string Glyph, bool Supported)> DialKinds(GoveeDevice device) =>
    [
        (DialCommand.Property.Brightness, "Brightness", Glyphs.Brightness, device.CanDim),
        (DialCommand.Property.Hue, "Colour", Glyphs.Hue, device.CanColor),
        (DialCommand.Property.Temperature, "White", Glyphs.WhiteBalance, device.CanWhite),
        (DialCommand.Property.Saturation, "Saturation", Glyphs.Saturation, device.CanColor)
    ];

    private static Dictionary<RotaryAction, MenuCommandRef> WholeDial(string command, string selector) =>
        Enum.GetValues<RotaryAction>().ToDictionary(a => a, _ => new MenuCommandRef
        {
            CommandName = command,
            Parameters = new Dictionary<string, string> { [GoveeCommandBase.DeviceParameter] = selector }
        });

    // ---- IMenuContributor: Govee › light › action ----

    public async Task<IReadOnlyList<MenuNode>> GetMenuNodes(ButtonTargets target)
    {
        if (!_applied.Demo && string.IsNullOrWhiteSpace(_applied.ApiKey))
        {
            return [Info("Add your Govee API key in Settings › Plugins › Govee Lights, or turn on Demo lights")];
        }

        var lights = _lights.Lights.ToList();
        if (lights.Count == 0)
        {
            try
            {
                using var cts = new CancellationTokenSource(MenuSceneTimeout);
                lights = (await _lights.RefreshDevicesAsync(cts.Token).ConfigureAwait(false)).Where(d => d.IsLight).ToList();
            }
            catch (Exception ex) when (ex is GoveeException or OperationCanceledException)
            {
                return [Info($"Govee: {(ex is GoveeException ? ex.Message : "no answer")}. Check the plugin settings.")];
            }
            if (lights.Count == 0) return [Info("No Govee lights on this account")];
        }

        var rotary = target.HasFlag(ButtonTargets.RotaryEncoder);
        var nodes = new List<MenuNode>();
        foreach (var device in lights.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            nodes.Add(new MenuNode
            {
                Name = device.Name,
                Children = rotary ? DialNodes(device.Id, [device]) : await KeyNodesAsync(device).ConfigureAwait(false)
            });
        }

        if (lights.Count > 1)
        {
            nodes.Add(new MenuNode
            {
                Name = "All lights",
                Children = rotary ? DialNodes(DeviceSelector.All, lights) : GroupKeyNodes(DeviceSelector.All, lights)
            });
        }
        return nodes;
    }

    private static List<MenuNode> DialNodes(string selector, IReadOnlyList<GoveeDevice> devices)
    {
        var nodes = new List<MenuNode>();
        foreach (var (property, label, _, _) in DialKinds(devices[0]))
        {
            // Offered when at least one light of the group has it; the others ignore it.
            if (!devices.Any(d => DialKinds(d).Any(k => k.Property == property && k.Supported))) continue;
            nodes.Add(new MenuNode { Name = $"{label} dial (turn, press for on/off)", RotaryGroup = WholeDial(DialCommand.NameOf(property), selector) });
        }
        nodes.Add(Leaf("Light on/off (press)", PowerCommand.Name, selector, ("Action", "toggle")));
        return nodes;
    }

    private async Task<List<MenuNode>> KeyNodesAsync(GoveeDevice device)
    {
        var nodes = GroupKeyNodes(device.Id, [device]);

        foreach (var (instance, label, _) in LightFolders.SceneKinds)
        {
            if (!device.Has(instance)) continue;
            IReadOnlyList<NamedOption> scenes;
            try
            {
                using var cts = new CancellationTokenSource(MenuSceneTimeout);
                scenes = await _lights.GetScenesAsync(device, instance, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                scenes = _lights.CachedScenes(device, instance);
            }
            if (scenes.Count == 0) continue;
            nodes.Add(Folder(label, scenes.Select(s =>
                Leaf(s.Name, SceneCommand.Name, device.Id, ("Kind", instance), ("Scene", ParamCodec.EncodeValue(s.Value)), ("Name", Safe(s.Name))))));
        }

        if (device.MusicModes.Count > 0)
        {
            nodes.Add(Folder("Music modes", device.MusicModes.Select(m =>
                Leaf(m.Name, MusicCommand.Name, device.Id, ("Mode", ParamCodec.EncodeValue(m.Value)), ("Name", Safe(m.Name)), ("Sensitivity", "100")))));
        }

        if (device.Toggles.Any())
        {
            nodes.Add(Folder("Features", device.Toggles.Select(t =>
                Leaf(FeatureNames.Label(t.Instance), FeatureCommand.Name, device.Id, ("Feature", t.Instance), ("Action", "toggle")))));
        }

        if (device.SegmentRange is { } range)
        {
            var count = range.Max - range.Min + 1;
            var half = $"{range.Min}-{range.Min + count / 2 - 1}";
            nodes.Add(Folder($"Segments ({count})", LightFolders.Palette.Select(p =>
                Leaf($"{p.Name}, segments {half} (edit to change)", SegmentColorCommand.Name, device.Id, ("Segments", half), ("Color", p.Color.ToHex())))));
        }
        return nodes;
    }

    /// <summary>Keys that work for one light or a group: power, page, brightness, colours, whites.</summary>
    private static List<MenuNode> GroupKeyNodes(string selector, IReadOnlyList<GoveeDevice> devices)
    {
        var nodes = new List<MenuNode>
        {
            Leaf("Light on/off", PowerCommand.Name, selector, ("Action", "toggle")),
            Leaf("Turn on", PowerCommand.Name, selector, ("Action", "on")),
            Leaf("Turn off", PowerCommand.Name, selector, ("Action", "off")),
            Leaf("Controls page", ControlsCommand.Name, selector)
        };

        if (devices.Any(d => d.CanDim))
        {
            nodes.Add(Folder("Brightness", new[] { "10", "25", "50", "75", "100", "+10", "-10" }.Select(v =>
                Leaf(v.StartsWith('+') || v.StartsWith('-') ? $"Step {v}%" : $"{v}%", SetBrightnessCommand.Name, selector, ("Percent", v)))));
        }
        if (devices.Any(d => d.CanColor))
        {
            nodes.Add(Folder("Colours", LightFolders.Palette.Select(p =>
                Leaf(p.Name, SetColorCommand.Name, selector, ("Color", p.Color.ToHex())))
                .Append(Leaf("Custom colour (edit to change)", SetColorCommand.Name, selector, ("Color", "#FF551C")))));
        }
        if (devices.Any(d => d.CanWhite))
        {
            nodes.Add(Folder("Whites", new[] { "2200", "2700", "4000", "5000", "6500", "+500", "-500" }.Select(v =>
                Leaf(v.StartsWith('+') || v.StartsWith('-') ? $"Step {v}K" : $"{v}K", SetWhiteCommand.Name, selector, ("Kelvin", v)))));
        }
        return nodes;
    }

    private static MenuNode Leaf(string name, string command, string selector, params (string Key, string Value)[] parameters)
    {
        var values = new Dictionary<string, string> { [GoveeCommandBase.DeviceParameter] = selector };
        foreach (var (key, value) in parameters) values[key] = value;
        return new MenuNode { Name = name, CommandName = command, Parameters = values };
    }

    private static MenuNode Folder(string name, IEnumerable<MenuNode> children) => new() { Name = name, Children = children.ToList() };

    private static MenuNode Info(string text) => new() { Name = text };

    /// <summary>A caption that survives the host's Name(a,b) command syntax.</summary>
    private static string Safe(string text) => new string(text.Select(c => c is ',' or '(' or ')' ? ' ' : c).ToArray()).Trim();

    // ---- device cache ----

    private void SaveDeviceCache(IReadOnlyList<GoveeDevice> devices)
    {
        if (_applied.Demo) return;
        try
        {
            var array = new JsonArray(devices.Select(d => JsonNode.Parse(d.Raw.ToJsonString())).ToArray());
            _host.Settings.Set(DeviceCacheKey, array.ToJsonString());
            _host.Settings.Save();
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Could not save the device list", ex);
        }
    }

    private IReadOnlyList<GoveeDevice> LoadDeviceCache()
    {
        try
        {
            var text = _host.Settings.Get(DeviceCacheKey, string.Empty);
            if (string.IsNullOrWhiteSpace(text) || JsonNode.Parse(text) is not JsonArray array) return [];
            return array.OfType<JsonObject>().Select(GoveeDevice.FromJson).OfType<GoveeDevice>().ToList();
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Could not read the saved device list", ex);
            return [];
        }
    }

    // ---- settings ----

    private sealed record Settings(string ApiKey, bool Demo, bool Lan, IReadOnlyList<IPAddress> LanAddresses, int PollSeconds)
    {
        public bool SameBackend(Settings other) => ApiKey == other.ApiKey && Demo == other.Demo;
    }

    private Settings ReadSettings()
    {
        var addresses = (_host.Settings.Get(LanAddressesKey, string.Empty) ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => IPAddress.TryParse(a, out var ip) ? ip : null)
            .OfType<IPAddress>()
            .ToList();
        var poll = (int)Math.Clamp(_host.Settings.Get(PollSecondsKey, (long)DefaultPollSeconds), 10, 3600);
        return new Settings(
            (_host.Settings.Get(ApiKeyKey, string.Empty) ?? string.Empty).Trim(),
            _host.Settings.Get(DemoKey, false),
            _host.Settings.Get(LanKey, true),
            addresses,
            poll);
    }

    private ILightBackend CreateBackend(Settings settings) =>
        settings.Demo ? new DemoBackend() : new GoveeCloudClient(() => _applied.ApiKey);

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema =>
    [
        new PluginSettingDescriptor
        {
            Key = "account",
            Label = "Govee account",
            Kind = PluginSettingKind.Heading,
            Description = "Get a key in the Govee Home app: Profile › Settings › Apply for API Key; Govee emails it to you. " +
                          "Asking for a new key switches off your old one, including in other apps that use it."
        },
        new PluginSettingDescriptor
        {
            Key = ApiKeyKey,
            Label = "API key",
            Kind = PluginSettingKind.Password,
            DefaultValue = string.Empty,
            Description = "Stored only on this computer and sent only to Govee."
        },
        new PluginSettingDescriptor
        {
            Key = DemoKey,
            Label = "Demo lights",
            Kind = PluginSettingKind.Toggle,
            DefaultValue = false,
            Description = "Three pretend lights to try every key and dial without an account or hardware. Ignores the API key while on."
        },
        new PluginSettingDescriptor
        {
            Key = "local",
            Label = "LAN control",
            Kind = PluginSettingKind.Heading,
            Description = "Lights with \"LAN Control\" switched on in the Govee Home app are switched, dimmed and coloured directly over your network: " +
                          "faster, and not counted against Govee's limits. Scenes and features always go through Govee."
        },
        new PluginSettingDescriptor
        {
            Key = LanKey,
            Label = "Use LAN control",
            Kind = PluginSettingKind.Toggle,
            DefaultValue = true,
            Description = "Needs UDP port 4002 free on this computer (Home Assistant or Homebridge on the same machine may hold it)."
        },
        new PluginSettingDescriptor
        {
            Key = LanAddressesKey,
            Label = "Light addresses (optional)",
            Kind = PluginSettingKind.Text,
            DefaultValue = string.Empty,
            Description = "IP addresses to ask directly, separated by commas, for networks that block discovery (VPNs, separate Wi-Fi)."
        },
        new PluginSettingDescriptor
        {
            Key = "refresh",
            Label = "Keeping keys up to date",
            Kind = PluginSettingKind.Heading,
            Description = "Lights are read only while one of their keys is on screen. LAN lights are read every 5 seconds at no cost."
        },
        new PluginSettingDescriptor
        {
            Key = PollSecondsKey,
            Label = "Read cloud lights every (seconds)",
            Kind = PluginSettingKind.Number,
            DefaultValue = (long)DefaultPollSeconds,
            Description = "10 to 3600. Govee limits how often an account may ask; 60 keeps a few lights well inside it."
        },
        new PluginSettingDescriptor
        {
            Key = "about",
            Label = "About and support",
            Kind = PluginSettingKind.Heading,
            Description = "Made by web-engineer; not affiliated with Govee. For help, our public Slack and our tracker are on the contact page. " +
                          "Report bugs and request features on GitHub; please report security issues privately through the contact form."
        }
    ];

    public IReadOnlyList<PluginSettingAction> SettingsActions =>
    [
        new PluginSettingAction { Label = "Test connection", Invoke = TestConnectionAsync },
        new PluginSettingAction { Label = "Find LAN lights", Invoke = FindLanLightsAsync },
        new PluginSettingAction { Label = "How to get an API key", Invoke = () => OpenLink(ApiKeyHelpUrl) },
        new PluginSettingAction { Label = "Get support", Invoke = () => OpenLink(SupportUrl) },
        new PluginSettingAction { Label = "Report a bug or request a feature", Invoke = () => OpenLink(IssuesUrl) }
    ];

    public void OnSettingsSaved()
    {
        var next = ReadSettings();
        var previous = _applied;
        _applied = next;

        _lights.CloudPollInterval = TimeSpan.FromSeconds(next.PollSeconds);
        if (!next.SameBackend(previous))
        {
            _lights.UseBackend(CreateBackend(next));
            if (!next.Demo) _lights.LoadCachedDevices(LoadDeviceCache());
        }
        if (next.Lan != previous.Lan || !next.LanAddresses.SequenceEqual(previous.LanAddresses))
        {
            _lights.UseLan(null);
            _lights.UseLan(next.Lan ? next.LanAddresses : null);
        }
        RefreshKeys();
    }

    private async Task<string> TestConnectionAsync()
    {
        if (!_applied.Demo && string.IsNullOrWhiteSpace(_applied.ApiKey)) return "Enter your API key first (and save), or turn on Demo lights.";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var devices = await _lights.RefreshDevicesAsync(cts.Token).ConfigureAwait(false);
            var lights = devices.Where(d => d.IsLight).ToList();
            var names = lights.Count == 0 ? "no lights" : string.Join(", ", lights.Select(d => $"{d.Name} ({d.Sku})"));
            var others = devices.Count - lights.Count;
            var usage = _lights.Backend is GoveeCloudClient cloud
                ? $" {cloud.RequestsLast24Hours} request(s) to Govee in the last 24 hours" + (cloud.LastRateLimit is { } rl ? $" ({rl})." : ".")
                : string.Empty;
            return $"{_lights.Backend.Name} OK: {names}" + (others > 0 ? $"; {others} other device(s) ignored." : ".") + usage;
        }
        catch (GoveeException ex)
        {
            return $"Failed: {ex.Message}.";
        }
        catch (OperationCanceledException)
        {
            return "Failed: Govee did not answer within 15 seconds.";
        }
    }

    private async Task<string> FindLanLightsAsync()
    {
        if (!_applied.Lan) return "LAN control is off.";
        if (_lights.Lan is not { } lan) return "LAN control is not running.";
        if (lan.Problem is { } problem) return problem;

        await _lights.ScanLanAsync().ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        if (lan.Addresses.Count == 0)
        {
            return "No lights answered. Switch on LAN Control for each light in the Govee Home app, " +
                   "or list their IP addresses if your network blocks discovery.";
        }
        var found = lan.Addresses.Select(pair => $"{_lights.Find(pair.Key)?.Name ?? pair.Key} at {pair.Value}");
        return $"{lan.Addresses.Count} light(s) on the LAN: {string.Join(", ", found)}.";
    }

    private Task<string> OpenLink(string url) =>
        Task.FromResult(_host.OpenBrowser(url) ? $"Opened {url}" : $"Could not open a browser; visit {url}");

    private static byte[]? LoadEmbeddedIcon(string name)
    {
        try
        {
            using var stream = typeof(GoveePlugin).Assembly.GetManifestResourceStream(name);
            if (stream is null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
