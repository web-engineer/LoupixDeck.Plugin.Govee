using System.Net;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Govee.Govee;

// govee-probe: talk to Govee (or the demo lights) the way the plugin does, and print what happens.
// The API key comes from GOVEE_API_KEY so it stays out of shell history.

const string Usage = """
    Usage: govee-probe [--demo] <command> [arguments]

      list                          Lights on the account, with what each supports
      state <light>                 Read a light's state
      on|off <light>                Switch a light, then read it back
      brightness <light> <1-100>    Set brightness, then read it back
      color <light> <#RRGGBB>       Set a colour, then read it back
      white <light> <kelvin>        Set a white temperature, then read it back
      scenes <light>                List the light's scenes and DIY scenes
      scene <light> <name>          Start a scene by name
      segments <light> <0-4> <#RRGGBB>  Colour some segments
      dial <light>                  Sweep brightness like a fast dial turn (tests pacing)
      lan [ip ...]                  Look for lights on the LAN (multicast, plus listed addresses)

    <light> is a device id, a name, or the start of a name.
    Set GOVEE_API_KEY first, or pass --demo for the pretend lights.
    """;

var argv = args.ToList();
var demo = argv.Remove("--demo");
if (argv.Count == 0)
{
    Console.WriteLine(Usage);
    return 1;
}

if (argv[0] == "lan") return await Lan(argv.Skip(1).ToList());

var key = Environment.GetEnvironmentVariable("GOVEE_API_KEY");
if (!demo && string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("Set GOVEE_API_KEY (or use --demo).");
    return 1;
}

ILightBackend backend = demo ? new DemoBackend() : new GoveeCloudClient(() => key);
using var service = new LightService(backend, m => Console.WriteLine($"  · {m}"), (m, ex) => Console.Error.WriteLine($"  ! {m} {ex?.Message}"));

try
{
    var devices = await service.RefreshDevicesAsync(CancellationToken.None);
    if (argv[0] == "list")
    {
        foreach (var d in devices) Describe(d);
        return 0;
    }

    if (argv.Count < 2)
    {
        Console.WriteLine(Usage);
        return 1;
    }

    var device = FindLight(devices, argv[1]);
    if (device is null)
    {
        Console.Error.WriteLine($"No light matches \"{argv[1]}\". Try: govee-probe list");
        return 1;
    }

    switch (argv[0])
    {
        case "state":
            Print("state", await backend.GetStateAsync(device, CancellationToken.None));
            return 0;
        case "on" or "off":
            return await Control(device, () => service.SetPower(device, argv[0] == "on"));
        case "brightness" when argv.Count > 2 && int.TryParse(argv[2], out var percent):
            return await Control(device, () => service.SetBrightness(device, percent));
        case "color" when argv.Count > 2 && Rgb.TryParse(argv[2], out var color):
            return await Control(device, () => service.SetColor(device, color));
        case "white" when argv.Count > 2 && int.TryParse(argv[2], out var kelvin):
            return await Control(device, () => service.SetKelvin(device, kelvin));
        case "scenes":
            foreach (var instance in new[] { Capability.LightScene, Capability.DiyScene })
            {
                if (!device.Has(instance)) continue;
                var scenes = await service.GetScenesAsync(device, instance, CancellationToken.None);
                Console.WriteLine($"{instance} ({scenes.Count}): {string.Join(", ", scenes.Select(s => s.Name))}");
            }
            return 0;
        case "scene" when argv.Count > 2:
            var wanted = string.Join(' ', argv.Skip(2));
            var scene = (await service.GetScenesAsync(device, Capability.LightScene, CancellationToken.None))
                .FirstOrDefault(s => s.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
            if (scene is null)
            {
                Console.Error.WriteLine($"No scene starts with \"{wanted}\". Try: govee-probe scenes {argv[1]}");
                return 1;
            }
            Console.WriteLine($"Starting {scene.Name} ({scene.Value.ToJsonString()}). Govee does not report scenes back; check the light or the app.");
            return await Control(device, () => service.StartScene(device, Capability.LightScene, scene));
        case "segments" when argv.Count > 3 && device.SegmentRange is { } range && Rgb.TryParse(argv[3], out var segmentColor):
            var segments = ParamCodec.ParseSegments(argv[2], range);
            Console.WriteLine($"Segments {string.Join(",", segments)} of {range.Min}-{range.Max}");
            return await Control(device, () => service.SetSegmentColor(device, segments, segmentColor));
        case "dial":
            return await Dial(device);
        default:
            Console.WriteLine(Usage);
            return 1;
    }
}
catch (GoveeException ex)
{
    Console.Error.WriteLine($"Govee: {ex.Message}" + (ex.Status is { } s ? $" ({s})" : ""));
    return 2;
}
finally
{
    if (backend is GoveeCloudClient cloud)
    {
        Console.WriteLine($"Requests sent: {cloud.RequestsLast24Hours}" + (cloud.LastRateLimit is { } rl ? $"; rate limit: {rl}" : ""));
    }
}

async Task<int> Control(GoveeDevice device, Action control)
{
    Print("before", await backend.GetStateAsync(device, CancellationToken.None));
    control();
    // Let the queue send, then give Govee a moment before reading back (as the plugin does).
    await Task.Delay(3500);
    var error = service.GetState(device).Error;
    if (error is not null) Console.Error.WriteLine($"Control failed: {error}");
    Print("after ", await backend.GetStateAsync(device, CancellationToken.None));
    return error is null ? 0 : 2;
}

async Task<int> Dial(GoveeDevice device)
{
    var start = DateTimeOffset.Now;
    Console.WriteLine("Turning brightness 10 → 90 in 80 ticks over ~2 s (a quick dial turn)…");
    for (var value = 10; value <= 90; value++)
    {
        service.SetBrightness(device, value);
        await Task.Delay(25);
    }
    await Task.Delay(3500);
    Console.WriteLine($"Done in {(DateTimeOffset.Now - start).TotalSeconds:0.0}s; expect only a handful of requests below.");
    Print("after ", await backend.GetStateAsync(device, CancellationToken.None));
    return service.GetState(device).Error is null ? 0 : 2;
}

static GoveeDevice? FindLight(IReadOnlyList<GoveeDevice> devices, string text) =>
    devices.FirstOrDefault(d => string.Equals(d.Id, text, StringComparison.OrdinalIgnoreCase))
    ?? devices.FirstOrDefault(d => string.Equals(d.Name, text, StringComparison.OrdinalIgnoreCase))
    ?? devices.FirstOrDefault(d => d.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase));

static void Describe(GoveeDevice d)
{
    var features = new List<string>();
    if (d.CanSwitch) features.Add("power");
    if (d.CanDim) features.Add($"brightness {d.BrightnessRange.Min}-{d.BrightnessRange.Max}");
    if (d.CanColor) features.Add("colour");
    if (d.CanWhite) features.Add($"white {d.KelvinRange.Min}-{d.KelvinRange.Max}K");
    if (d.SegmentRange is { } r) features.Add($"segments {r.Min}-{r.Max}");
    if (d.Has(Capability.LightScene)) features.Add("scenes");
    if (d.Has(Capability.DiyScene)) features.Add("DIY scenes");
    if (d.Has(Capability.Snapshot)) features.Add("snapshots");
    if (d.MusicModes.Count > 0) features.Add("music");
    features.AddRange(d.Toggles.Select(t => t.Instance));
    Console.WriteLine($"{d.Name}  [{d.Sku}  {d.Id}]{(d.IsLight ? "" : "  (not a light)")}");
    Console.WriteLine($"    {string.Join(", ", features)}");
    var known = d.Capabilities.Select(c => c.Instance).Except(
        [Capability.PowerSwitch, Capability.Brightness, Capability.ColorRgb, Capability.ColorTemperatureK, Capability.SegmentedColorRgb,
         Capability.SegmentedBrightness, Capability.LightScene, Capability.DiyScene, Capability.Snapshot, Capability.MusicMode]);
    var other = known.Except(d.Toggles.Select(t => t.Instance)).ToList();
    if (other.Count > 0) Console.WriteLine($"    not used by the plugin yet: {string.Join(", ", other)}");
}

static void Print(string label, DeviceState s)
{
    string Or(object? value) => value?.ToString() ?? "?";
    Console.WriteLine($"{label}: online {Or(s.Online)}, on {Or(s.On)}, brightness {Or(s.Brightness)}, " +
                      $"colour {Or(s.Color)}, white {(s.Kelvin is > 0 ? s.Kelvin + "K" : "off")}" +
                      (s.Features.Count > 0 ? ", " + string.Join(", ", s.Features.Select(f => $"{f.Key} {f.Value}")) : ""));
}

static async Task<int> Lan(List<string> addressArgs)
{
    var addresses = addressArgs.Select(a => IPAddress.TryParse(a, out var ip) ? ip : null).OfType<IPAddress>().ToList();
    using var lan = new GoveeLanClient((m, ex) => Console.Error.WriteLine($"  ! {m} {ex?.Message}"));
    lan.DeviceFound += (id, sku, ip) =>
    {
        Console.WriteLine($"found {sku} {id} at {ip}");
        _ = lan.RequestStatusAsync(id);
    };
    lan.StateReceived += (id, s) => Print($"  {id}", s);
    lan.Start();
    if (lan.Problem is not null) return 2;

    Console.WriteLine($"Scanning 239.255.255.250:{GoveeLanClient.ScanPort}" +
                      (addresses.Count > 0 ? $" and {string.Join(", ", addresses)}" : "") + " for 4 s…");
    await lan.ScanAsync(addresses);
    await Task.Delay(TimeSpan.FromSeconds(4));
    if (lan.Addresses.Count == 0)
    {
        Console.WriteLine("No answers. Is LAN Control on for the light in the Govee Home app? Over a VPN, list the light's IP address.");
        return 1;
    }
    return 0;
}
