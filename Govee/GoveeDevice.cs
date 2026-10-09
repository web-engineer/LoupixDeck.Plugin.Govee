using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.Govee.Govee;

/// <summary>One capability as Govee lists it: a type, an instance and the parameters that bound its value.</summary>
internal sealed record Capability(string Type, string Instance, JsonObject? Parameters)
{
    public const string OnOff = "devices.capabilities.on_off";
    public const string Toggle = "devices.capabilities.toggle";
    public const string Range = "devices.capabilities.range";
    public const string ColorSetting = "devices.capabilities.color_setting";
    public const string SegmentColorSetting = "devices.capabilities.segment_color_setting";
    public const string DynamicScene = "devices.capabilities.dynamic_scene";
    public const string DiyColorSetting = "devices.capabilities.diy_color_setting";
    public const string MusicSetting = "devices.capabilities.music_setting";
    public const string Mode = "devices.capabilities.mode";

    public const string PowerSwitch = "powerSwitch";
    public const string Brightness = "brightness";
    public const string ColorRgb = "colorRgb";
    public const string ColorTemperatureK = "colorTemperatureK";
    public const string SegmentedColorRgb = "segmentedColorRgb";
    public const string SegmentedBrightness = "segmentedBrightness";
    public const string LightScene = "lightScene";
    public const string DiyScene = "diyScene";
    public const string Snapshot = "snapshot";
    public const string MusicMode = "musicMode";

    /// <summary>The <c>range</c> of an INTEGER capability, or null.</summary>
    public (int Min, int Max)? IntegerRange =>
        Parameters?["range"] is JsonObject range && range["min"] is JsonValue min && range["max"] is JsonValue max
            ? (min.GetValue<int>(), max.GetValue<int>())
            : null;

    /// <summary>The <c>options</c> of an ENUM capability (a scene list, a snapshot list…), or empty.</summary>
    public IReadOnlyList<NamedOption> Options =>
        Parameters?["options"] is JsonArray options
            ? options.OfType<JsonObject>()
                .Where(o => o["name"] is not null && o["value"] is not null)
                .Select(o => new NamedOption(o["name"]!.ToString(), o["value"]!.DeepClone()))
                .ToList()
            : [];

    /// <summary>A field of a STRUCT capability, e.g. <c>segment</c> or <c>musicMode</c>.</summary>
    public JsonObject? Field(string name) =>
        Parameters?["fields"] is JsonArray fields
            ? fields.OfType<JsonObject>().FirstOrDefault(f => f["fieldName"]?.ToString() == name)
            : null;
}

/// <summary>A choice Govee offers for a capability: a display name and the exact value to send back.</summary>
internal sealed record NamedOption(string Name, JsonNode Value);

/// <summary>
/// A device from the account's device list. The capability list is kept as Govee sent it, so the
/// plugin builds its controls from what each model says it supports instead of guessing by SKU.
/// </summary>
internal sealed class GoveeDevice
{
    public required string Id { get; init; }
    public required string Sku { get; init; }
    public required string Name { get; init; }
    public string Type { get; init; } = string.Empty;
    public IReadOnlyList<Capability> Capabilities { get; init; } = [];

    /// <summary>The device object as received, so the cache can be written back unchanged.</summary>
    public JsonObject Raw { get; init; } = new();

    public bool IsLight => Type is "devices.types.light" || Has(Capability.ColorRgb) || Has(Capability.Brightness);

    public Capability? Find(string instance) =>
        Capabilities.FirstOrDefault(c => string.Equals(c.Instance, instance, StringComparison.OrdinalIgnoreCase));

    public bool Has(string instance) => Find(instance) is not null;

    public bool CanSwitch => Has(Capability.PowerSwitch);
    public bool CanDim => Has(Capability.Brightness);
    public bool CanColor => Has(Capability.ColorRgb);
    public bool CanWhite => Has(Capability.ColorTemperatureK);

    public (int Min, int Max) BrightnessRange => Find(Capability.Brightness)?.IntegerRange ?? (1, 100);

    public (int Min, int Max) KelvinRange => Find(Capability.ColorTemperatureK)?.IntegerRange ?? (2000, 9000);

    /// <summary>Segment indices the device accepts (0-based), or empty when it has no segment control.</summary>
    public (int Min, int Max)? SegmentRange
    {
        get
        {
            var segment = Find(Capability.SegmentedColorRgb)?.Field("segment");
            if (segment is null) return null;
            if (segment["elementRange"] is JsonObject range && range["min"] is JsonValue min && range["max"] is JsonValue max)
                return (min.GetValue<int>(), max.GetValue<int>());
            if (segment["options"] is JsonArray options && options.Count > 0)
            {
                var values = options.OfType<JsonObject>().Select(o => o["value"]?.GetValue<int>() ?? 0).ToList();
                return (values.Min(), values.Max());
            }
            return null;
        }
    }

    /// <summary>On/off features beyond the main power switch: gradient, nightlight, a second light…</summary>
    public IEnumerable<Capability> Toggles => Capabilities.Where(c => c.Type == Capability.Toggle);

    /// <summary>Music modes, from the <c>musicMode</c> field of the music capability.</summary>
    public IReadOnlyList<NamedOption> MusicModes =>
        Find(Capability.MusicMode)?.Field("musicMode")?["options"] is JsonArray options
            ? options.OfType<JsonObject>()
                .Where(o => o["name"] is not null && o["value"] is not null)
                .Select(o => new NamedOption(o["name"]!.ToString(), o["value"]!.DeepClone()))
                .ToList()
            : [];

    public static GoveeDevice? FromJson(JsonObject json)
    {
        var id = json["device"]?.ToString();
        var sku = json["sku"]?.ToString();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(sku)) return null;

        var capabilities = json["capabilities"] is JsonArray array
            ? array.OfType<JsonObject>()
                .Select(c => new Capability(
                    c["type"]?.ToString() ?? string.Empty,
                    c["instance"]?.ToString() ?? string.Empty,
                    c["parameters"] as JsonObject))
                .Where(c => c.Instance.Length > 0)
                .ToList()
            : [];

        var name = json["deviceName"]?.ToString();
        return new GoveeDevice
        {
            Id = id,
            Sku = sku,
            Name = string.IsNullOrWhiteSpace(name) ? sku : name,
            Type = json["type"]?.ToString() ?? string.Empty,
            Capabilities = capabilities,
            Raw = json
        };
    }

    public override string ToString() => $"{Name} ({Sku} {Id})";
}
