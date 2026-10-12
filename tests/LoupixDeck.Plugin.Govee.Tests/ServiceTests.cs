using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Govee.Govee;
using Xunit;

namespace LoupixDeck.Plugin.Govee.Tests;

public class CloudClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static GoveeDevice Light() => GoveeDevice.FromJson(new JsonObject
    {
        ["sku"] = "H6199", ["device"] = "AA:BB", ["deviceName"] = "Strip",
        ["capabilities"] = new JsonArray()
    })!;

    [Fact]
    public async Task Sends_the_key_and_the_documented_control_body()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"code":200,"msg":"success"}"""));
        using var client = new GoveeCloudClient(() => "secret", handler);

        await client.ControlAsync(Light(), Capability.ColorSetting, Capability.ColorRgb, JsonValue.Create(0x0000FF), CancellationToken.None);

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://openapi.api.govee.com/router/api/v1/device/control", request.RequestUri!.ToString());
        Assert.Equal("secret", request.Headers.GetValues("Govee-API-Key").Single());
        var json = JsonNode.Parse(body!)!;
        Assert.False(string.IsNullOrEmpty(json["requestId"]?.ToString()));
        Assert.Equal("H6199", json["payload"]!["sku"]!.ToString());
        Assert.Equal("AA:BB", json["payload"]!["device"]!.ToString());
        Assert.Equal(255, json["payload"]!["capability"]!["value"]!.GetValue<int>());
        Assert.Equal(1, client.RequestsLast24Hours);
    }

    [Fact]
    public async Task Reports_a_rejected_key()
    {
        using var client = new GoveeCloudClient(() => "bad", new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var ex = await Assert.ThrowsAsync<GoveeException>(() => client.GetDevicesAsync(CancellationToken.None));
        Assert.Equal("API key rejected", ex.Message);
    }

    [Fact]
    public async Task Reports_rate_limiting_with_a_hold()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        using var client = new GoveeCloudClient(() => "k", new FakeHandler(_ => response));

        var ex = await Assert.ThrowsAsync<GoveeException>(() => client.GetStateAsync(Light(), CancellationToken.None));
        Assert.Equal(429, ex.Status);
        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
    }

    [Fact]
    public async Task Reports_errors_inside_a_200_response()
    {
        using var client = new GoveeCloudClient(() => "k",
            new FakeHandler(_ => Json(HttpStatusCode.OK, """{"code":400,"msg":"Parameter value out of range"}""")));
        var ex = await Assert.ThrowsAsync<GoveeException>(() =>
            client.ControlAsync(Light(), Capability.Range, Capability.Brightness, JsonValue.Create(500), CancellationToken.None));
        Assert.Equal("Parameter value out of range", ex.Message);
    }

    [Fact]
    public async Task Needs_a_key_before_calling_govee()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("should not be called"));
        using var client = new GoveeCloudClient(() => " ", handler);
        await Assert.ThrowsAsync<GoveeException>(() => client.GetDevicesAsync(CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Lists_devices()
    {
        using var client = new GoveeCloudClient(() => "k", new FakeHandler(_ => Json(HttpStatusCode.OK, """
            {"code":200,"message":"success","data":[{"sku":"H6008","device":"11:22","deviceName":"Bulb","type":"devices.types.light","capabilities":[]}]}
            """)));
        var device = Assert.Single(await client.GetDevicesAsync(CancellationToken.None));
        Assert.Equal("Bulb", device.Name);
    }
}

public class LightServiceTests
{
    /// <summary>Records controls and answers instantly; the demo lights' capabilities.</summary>
    private sealed class RecordingBackend : ILightBackend
    {
        private readonly DemoBackend _demo = new();
        public ConcurrentQueue<(string Instance, string Value, DateTimeOffset At)> Controls { get; } = new();
        public Exception? Fail { get; set; }

        public string Name => "Recording";
        public Task<IReadOnlyList<GoveeDevice>> GetDevicesAsync(CancellationToken ct) => _demo.GetDevicesAsync(ct);
        public Task<DeviceState> GetStateAsync(GoveeDevice device, CancellationToken ct) => Task.FromResult(DeviceState.Unknown);

        public Task ControlAsync(GoveeDevice device, string type, string instance, JsonNode value, CancellationToken ct)
        {
            Controls.Enqueue((instance, value.ToJsonString(), DateTimeOffset.UtcNow));
            return Fail is null ? Task.CompletedTask : Task.FromException(Fail);
        }

        /// <summary>The scene endpoint; returns nothing (so the capability list is used) unless set.</summary>
        public Func<GoveeDevice, CancellationToken, Task<IReadOnlyList<NamedOption>>>? Scenes { get; set; }

        public Task<IReadOnlyList<NamedOption>> GetScenesAsync(GoveeDevice device, string instance, CancellationToken ct) =>
            Scenes?.Invoke(device, ct) ?? Task.FromResult<IReadOnlyList<NamedOption>>([]);

        public void Dispose()
        {
        }
    }

    private static async Task<(LightService Service, RecordingBackend Backend, GoveeDevice Device)> Start()
    {
        var backend = new RecordingBackend();
        var service = new LightService(backend, _ => { }, (_, _) => { });
        var devices = await service.RefreshDevicesAsync(CancellationToken.None);
        return (service, backend, devices[0]);
    }

    [Fact]
    public async Task A_fast_dial_sends_few_requests_and_ends_on_the_final_value()
    {
        var (service, backend, device) = await Start();
        using var owned = service;

        for (var i = 1; i <= 40; i++)
        {
            service.SetBrightness(device, i);
            await Task.Delay(10); // a quick turn: 40 ticks in ~0.4 s
        }
        // The state follows the dial at once.
        Assert.Equal(40, service.GetState(device).Brightness);

        await Task.Delay(1500);
        var sent = backend.Controls.ToList();
        Assert.InRange(sent.Count, 1, 3);
        Assert.Equal("40", sent[^1].Value);
        // Never faster than Govee's 2 per second per device.
        for (var i = 1; i < sent.Count; i++) Assert.True(sent[i].At - sent[i - 1].At >= TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task Colour_white_and_scenes_share_a_slot_so_the_last_wins()
    {
        var (service, backend, device) = await Start();
        using var owned = service;

        service.SetColor(device, new Rgb(255, 0, 0));
        service.SetKelvin(device, 2700);
        service.SetPower(device, true);
        await Task.Delay(1500);

        Assert.Equal([Capability.ColorTemperatureK, Capability.PowerSwitch], backend.Controls.Select(c => c.Instance));
    }

    [Fact]
    public async Task Clamps_to_the_device_range()
    {
        var (service, backend, device) = await Start();
        using var owned = service;

        service.SetKelvin(device, 99999);
        service.SetBrightness(device, -20);
        await Task.Delay(1500);

        Assert.Contains(backend.Controls, c => c.Instance == Capability.ColorTemperatureK && c.Value == "9000");
        Assert.Contains(backend.Controls, c => c.Instance == Capability.Brightness && c.Value == "1");
    }

    [Fact]
    public async Task A_failed_control_shows_on_the_light()
    {
        var (service, backend, device) = await Start();
        using var owned = service;
        backend.Fail = new GoveeException("Rate limited", 429, TimeSpan.FromSeconds(30));

        service.SetPower(device, false);
        await Task.Delay(400);

        Assert.Equal("Rate limited", service.GetState(device).Error);
    }

    [Fact]
    public async Task Resolves_groups_and_all_lights()
    {
        var (service, _, device) = await Start();
        using var owned = service;

        Assert.Equal(3, service.Resolve("*").Count);
        Assert.Equal(2, service.Resolve($"{device.Id}+DE:MO:00:00:00:00:00:03").Count);
        Assert.Single(service.Resolve(device.Id.ToLowerInvariant()));
        Assert.Empty(service.Resolve("nope"));
    }

    [Fact]
    public async Task A_group_lists_each_scene_and_music_mode_once()
    {
        var (service, _, _) = await Start();
        using var owned = service;
        var lights = service.Resolve("*");

        var music = service.SharedOptions(lights, Capability.MusicMode);
        Assert.Equal(["Energic", "Rhythm", "Spectrum", "Rolling"], music.Select(m => m.Name));

        var scenes = service.SharedOptions(lights, Capability.LightScene);
        Assert.NotEmpty(scenes);
        Assert.Equal(scenes.Count, scenes.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task A_scene_list_cut_short_is_fetched_again_next_time()
    {
        var (service, backend, device) = await Start();
        using var owned = service;
        var full = new NamedOption[] { new("Sunrise", JsonValue.Create(1)), new("Sunset", JsonValue.Create(2)) };
        backend.Scenes = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return full;
        };

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            await service.GetScenesAsync(device, Capability.LightScene, cts.Token);
        }
        backend.Scenes = (_, _) => Task.FromResult<IReadOnlyList<NamedOption>>(full);

        var scenes = await service.GetScenesAsync(device, Capability.LightScene, CancellationToken.None);
        Assert.Equal(["Sunrise", "Sunset"], scenes.Select(s => s.Name));
    }

    [Fact]
    public async Task Demo_lights_change_when_controlled()
    {
        using var demo = new DemoBackend();
        var device = (await demo.GetDevicesAsync(CancellationToken.None))[2];

        await demo.ControlAsync(device, Capability.OnOff, Capability.PowerSwitch, JsonValue.Create(1), CancellationToken.None);
        await demo.ControlAsync(device, Capability.ColorSetting, Capability.ColorRgb, JsonValue.Create(0x00FF00), CancellationToken.None);

        var state = await demo.GetStateAsync(device, CancellationToken.None);
        Assert.True(state.On);
        Assert.Equal(new Rgb(0, 255, 0), state.Color);
        Assert.Equal(0, state.Kelvin);
    }
}
