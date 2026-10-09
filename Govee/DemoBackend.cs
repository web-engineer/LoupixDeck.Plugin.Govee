using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.Govee.Govee;

/// <summary>
/// Pretend lights for trying the plugin without a Govee account or any hardware. The device list
/// uses the same capability shapes Govee sends, so every key, dial, menu and page works exactly as
/// it does against real lights; controls change the pretend state after a short delay.
/// </summary>
internal sealed class DemoBackend : ILightBackend
{
    private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(120);

    private static readonly string[] SceneNames =
        ["Sunrise", "Sunset", "Aurora", "Forest", "Ocean", "Candlelight", "Rainbow", "Fireworks", "Movie", "Party",
         "Breathe", "Starry sky", "Fire", "Snowflake", "Cherry blossom", "Reading", "Romantic", "Dreamland"];

    private readonly IReadOnlyList<GoveeDevice> _devices;
    private readonly ConcurrentDictionary<string, DeviceState> _states = new();

    public DemoBackend()
    {
        _devices =
        [
            Build("DE:MO:00:00:00:00:00:01", "H6199", "Desk strip", segments: 15, scenes: true, toggles: ["gradientToggle"]),
            Build("DE:MO:00:00:00:00:00:02", "H6076", "Floor lamp", segments: 7, scenes: true, toggles: ["nightlightToggle"]),
            Build("DE:MO:00:00:00:00:00:03", "H6008", "Hall bulb", segments: 0, scenes: false, toggles: [])
        ];
        _states["DE:MO:00:00:00:00:00:01"] = new DeviceState { Online = true, On = true, Brightness = 80, Color = new Rgb(255, 85, 28), Kelvin = 0 };
        _states["DE:MO:00:00:00:00:00:02"] = new DeviceState { Online = true, On = true, Brightness = 45, Kelvin = 2700, Color = Rgb.White };
        _states["DE:MO:00:00:00:00:00:03"] = new DeviceState { Online = true, On = false, Brightness = 100, Kelvin = 4000, Color = Rgb.White };
    }

    public string Name => "Demo lights";

    public async Task<IReadOnlyList<GoveeDevice>> GetDevicesAsync(CancellationToken ct)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        return _devices;
    }

    public async Task<DeviceState> GetStateAsync(GoveeDevice device, CancellationToken ct)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        return (_states.GetValueOrDefault(device.Id) ?? DeviceState.Unknown) with { ReadAt = DateTimeOffset.Now, Scene = null };
    }

    public async Task ControlAsync(GoveeDevice device, string type, string instance, JsonNode value, CancellationToken ct)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        if (!device.Has(instance)) throw new GoveeException($"{device.Sku} has no {instance}", 400);

        _states.AddOrUpdate(device.Id, _ => DeviceState.Unknown, (_, state) => instance switch
        {
            Capability.PowerSwitch => state with { On = value.GetValue<int>() != 0 },
            Capability.Brightness => state with { Brightness = value.GetValue<int>(), On = true },
            Capability.ColorRgb => state with { Color = Rgb.FromInt(value.GetValue<int>()), Kelvin = 0, On = true },
            Capability.ColorTemperatureK => state with { Kelvin = value.GetValue<int>(), On = true },
            Capability.SegmentedColorRgb or Capability.SegmentedBrightness or
                Capability.LightScene or Capability.DiyScene or Capability.Snapshot or Capability.MusicMode => state with { On = true },
            _ => state
        });
    }

    public async Task<IReadOnlyList<NamedOption>> GetScenesAsync(GoveeDevice device, string instance, CancellationToken ct)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        return device.Find(instance)?.Options ?? [];
    }

    private static GoveeDevice Build(string id, string sku, string name, int segments, bool scenes, string[] toggles)
    {
        var capabilities = new JsonArray
        {
            Cap(Capability.OnOff, Capability.PowerSwitch, Enum(("on", 1), ("off", 0))),
            Cap(Capability.Range, Capability.Brightness, new JsonObject
            {
                ["dataType"] = "INTEGER", ["unit"] = "unit.percent",
                ["range"] = new JsonObject { ["min"] = 1, ["max"] = 100, ["precision"] = 1 }
            }),
            Cap(Capability.ColorSetting, Capability.ColorRgb, new JsonObject
            {
                ["dataType"] = "INTEGER",
                ["range"] = new JsonObject { ["min"] = 0, ["max"] = 16777215, ["precision"] = 1 }
            }),
            Cap(Capability.ColorSetting, Capability.ColorTemperatureK, new JsonObject
            {
                ["dataType"] = "INTEGER",
                ["range"] = new JsonObject { ["min"] = 2000, ["max"] = 9000, ["precision"] = 1 }
            })
        };

        if (segments > 0)
        {
            capabilities.Add(Cap(Capability.SegmentColorSetting, Capability.SegmentedColorRgb, new JsonObject
            {
                ["dataType"] = "STRUCT",
                ["fields"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["fieldName"] = "segment", ["dataType"] = "Array",
                        ["elementRange"] = new JsonObject { ["min"] = 0, ["max"] = segments - 1 },
                        ["size"] = new JsonObject { ["min"] = 1, ["max"] = segments }
                    },
                    new JsonObject { ["fieldName"] = "rgb", ["dataType"] = "INTEGER" }
                }
            }));
        }

        if (scenes)
        {
            var sceneOptions = SceneNames.Select((n, i) => ((string, int))(n, 3000 + i)).ToArray();
            capabilities.Add(Cap(Capability.DynamicScene, Capability.LightScene, Enum(sceneOptions)));
            capabilities.Add(Cap(Capability.DynamicScene, Capability.DiyScene, Enum(("My gradient", 8216567), ("Club", 8216568))));
            capabilities.Add(Cap(Capability.DynamicScene, Capability.Snapshot, Enum(("Evening", 0), ("Work", 1))));
            capabilities.Add(Cap(Capability.MusicSetting, Capability.MusicMode, new JsonObject
            {
                ["dataType"] = "STRUCT",
                ["fields"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["fieldName"] = "musicMode", ["dataType"] = "ENUM",
                        ["options"] = new JsonArray
                        {
                            new JsonObject { ["name"] = "Energic", ["value"] = 5 },
                            new JsonObject { ["name"] = "Rhythm", ["value"] = 3 },
                            new JsonObject { ["name"] = "Spectrum", ["value"] = 6 },
                            new JsonObject { ["name"] = "Rolling", ["value"] = 4 }
                        }
                    },
                    new JsonObject
                    {
                        ["fieldName"] = "sensitivity", ["dataType"] = "INTEGER",
                        ["range"] = new JsonObject { ["min"] = 0, ["max"] = 100, ["precision"] = 1 }
                    }
                }
            }));
        }

        foreach (var toggle in toggles)
        {
            capabilities.Add(Cap(Capability.Toggle, toggle, Enum(("on", 1), ("off", 0))));
        }

        var json = new JsonObject
        {
            ["sku"] = sku, ["device"] = id, ["deviceName"] = name, ["type"] = "devices.types.light",
            ["capabilities"] = capabilities
        };
        return GoveeDevice.FromJson(json)!;
    }

    private static JsonObject Cap(string type, string instance, JsonObject parameters) =>
        new() { ["type"] = type, ["instance"] = instance, ["parameters"] = parameters };

    private static JsonObject Enum(params (string Name, int Value)[] options) => new()
    {
        ["dataType"] = "ENUM",
        ["options"] = new JsonArray(options.Select(o => (JsonNode)new JsonObject { ["name"] = o.Name, ["value"] = o.Value }).ToArray())
    };

    public void Dispose()
    {
    }
}
