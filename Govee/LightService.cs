using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.Govee.Govee;

/// <summary>
/// The plugin's model of the user's lights. Keys and dials read from it synchronously (cached state
/// only, never the network); controls go through a small queue per light that keeps only the latest
/// value for each setting, paces requests to what Govee allows and sends the final value when a dial
/// stops. State is read back only for lights whose keys are on screen.
/// </summary>
internal sealed class LightService : IDisposable
{
    /// <summary>Gap between two cloud controls to one light: Govee allows 2 per second per device.</summary>
    private static readonly TimeSpan CloudInterval = TimeSpan.FromMilliseconds(550);

    /// <summary>Gap between two LAN controls; cheap Wi-Fi modules drop bursts of packets.</summary>
    private static readonly TimeSpan LanInterval = TimeSpan.FromMilliseconds(60);

    /// <summary>How long a burst of dial ticks is gathered before the first send.</summary>
    private static readonly TimeSpan GatherDelay = TimeSpan.FromMilliseconds(80);

    /// <summary>After the last cloud control, wait this long before reading the state back.</summary>
    private static readonly TimeSpan CloudConfirmDelay = TimeSpan.FromSeconds(2.5);

    private static readonly TimeSpan LanConfirmDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan LanPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LanScanInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DemandWindow = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ChangedCoalesce = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan SceneCacheAge = TimeSpan.FromMinutes(15);

    private static readonly HashSet<string> LanInstances =
        [Capability.PowerSwitch, Capability.Brightness, Capability.ColorRgb, Capability.ColorTemperatureK];

    private readonly Action<string> _info;
    private readonly Action<string, Exception?> _error;
    private readonly ConcurrentDictionary<string, DeviceState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Channel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _demand = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextPoll = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string, string), (DateTimeOffset At, IReadOnlyList<NamedOption> Options)> _scenes = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _changedLock = new();

    private ILightBackend _backend;
    private GoveeLanClient? _lan;
    private IReadOnlyList<IPAddress> _lanAddresses = [];
    private IReadOnlyList<GoveeDevice> _devices = [];
    private bool _changedScheduled;
    private Task? _poller;

    public LightService(ILightBackend backend, Action<string> info, Action<string, Exception?> error)
    {
        _backend = backend;
        _info = info;
        _error = error;
    }

    /// <summary>Raised (coalesced) whenever devices or state changed, so keys can redraw.</summary>
    public event Action? Changed;

    /// <summary>Raised when the device list was fetched, so the plugin can cache it.</summary>
    public event Action<IReadOnlyList<GoveeDevice>>? DevicesFetched;

    public ILightBackend Backend => _backend;

    public GoveeLanClient? Lan => _lan;

    public IReadOnlyList<GoveeDevice> Devices => _devices;

    public IEnumerable<GoveeDevice> Lights => _devices.Where(d => d.IsLight);

    /// <summary>The last account-level problem (bad key, no network), or null.</summary>
    public string? Problem { get; private set; }

    public DateTimeOffset? DevicesFetchedAt { get; private set; }

    /// <summary>How often a light whose key is on screen is read back through the cloud.</summary>
    public TimeSpan CloudPollInterval { get; set; } = TimeSpan.FromSeconds(60);

    // ---- lifecycle ----

    public void Start()
    {
        _poller ??= Task.Run(() => PollLoopAsync(_cts.Token));
        _ = RefreshDevicesQuietlyAsync();
    }

    /// <summary>Switches between the cloud and the demo lights; state and queues start afresh.</summary>
    public void UseBackend(ILightBackend backend)
    {
        var old = _backend;
        _backend = backend;
        _devices = [];
        _states.Clear();
        // Controls queued for the old backend's lights must not reach the new one.
        foreach (var channel in _channels.Values)
        {
            lock (channel.Lock) channel.Items.Clear();
        }
        _channels.Clear();
        _scenes.Clear();
        _nextPoll.Clear();
        Problem = null;
        DevicesFetchedAt = null;
        old.Dispose();
        _ = RefreshDevicesQuietlyAsync();
        RaiseChanged();
    }

    /// <summary>Turns LAN control on (with extra addresses to scan) or off (null).</summary>
    public void UseLan(IReadOnlyList<IPAddress>? addresses)
    {
        if (addresses is null)
        {
            if (_lan is null) return;
            _lan.StateReceived -= OnLanState;
            _lan.DeviceFound -= OnLanDevice;
            _lan.Dispose();
            _lan = null;
            return;
        }

        _lanAddresses = addresses;
        if (_lan is null)
        {
            _lan = new GoveeLanClient(_error);
            _lan.StateReceived += OnLanState;
            _lan.DeviceFound += OnLanDevice;
            _lan.Start();
        }
        _ = ScanLanAsync();
    }

    public Task ScanLanAsync() => _lan?.ScanAsync(_lanAddresses) ?? Task.CompletedTask;

    /// <summary>Restores the device list saved by an earlier run, so keys work before the first fetch.</summary>
    public void LoadCachedDevices(IReadOnlyList<GoveeDevice> devices)
    {
        if (_devices.Count == 0 && devices.Count > 0)
        {
            _devices = devices;
            RaiseChanged();
        }
    }

    // ---- reading ----

    public GoveeDevice? Find(string? id) =>
        id is null ? null : _devices.FirstOrDefault(d => string.Equals(d.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The lights a command's Device parameter names: one device id, several joined with "+", or
    /// "*" for every light on the account. Unknown ids are skipped.
    /// </summary>
    public IReadOnlyList<GoveeDevice> Resolve(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return [];
        if (selector.Trim() == DeviceSelector.All) return Lights.ToList();
        return selector.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Find).OfType<GoveeDevice>().Distinct().ToList();
    }

    public DeviceState GetState(GoveeDevice device) => _states.GetValueOrDefault(device.Id) ?? DeviceState.Unknown;

    /// <summary>Marks a light as on screen, so it is kept up to date. Cheap; call it from every render.</summary>
    public void Touch(GoveeDevice device)
    {
        var now = DateTimeOffset.UtcNow;
        var previous = _demand.GetValueOrDefault(device.Id);
        _demand[device.Id] = now;
        // A light that just came on screen is read straight away rather than at the next interval.
        if (now - previous > DemandWindow) _nextPoll[device.Id] = DateTimeOffset.MinValue;
    }

    public bool IsOnLan(GoveeDevice device) => _lan is { IsRunning: true } lan && lan.TryGetAddress(device.Id, out _);

    /// <summary>A light's scenes (or DIY scenes) from the cache; empty until <see cref="GetScenesAsync"/> ran.</summary>
    public IReadOnlyList<NamedOption> CachedScenes(GoveeDevice device, string instance)
    {
        if (_scenes.TryGetValue((device.Id, instance), out var cached)) return cached.Options;
        return device.Find(instance)?.Options ?? [];
    }

    /// <summary>
    /// A light's scenes: the full list from Govee's scene endpoint, falling back to the (often
    /// shorter) list in the device's capabilities. Cached for a quarter of an hour.
    /// </summary>
    public async Task<IReadOnlyList<NamedOption>> GetScenesAsync(GoveeDevice device, string instance, CancellationToken ct)
    {
        if (instance is not (Capability.LightScene or Capability.DiyScene)) return device.Find(instance)?.Options ?? [];
        if (_scenes.TryGetValue((device.Id, instance), out var cached) && DateTimeOffset.UtcNow - cached.At < SceneCacheAge)
            return cached.Options;

        IReadOnlyList<NamedOption> options;
        try
        {
            options = await _backend.GetScenesAsync(device, instance, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GoveeException or OperationCanceledException)
        {
            _error($"Could not list {instance} for {device}: {ex.Message}", null);
            options = [];
        }
        if (options.Count == 0) options = device.Find(instance)?.Options ?? [];
        _scenes[(device.Id, instance)] = (DateTimeOffset.UtcNow, options);
        return options;
    }

    public async Task<IReadOnlyList<GoveeDevice>> RefreshDevicesAsync(CancellationToken ct)
    {
        try
        {
            var devices = await _backend.GetDevicesAsync(ct).ConfigureAwait(false);
            _devices = devices;
            DevicesFetchedAt = DateTimeOffset.Now;
            Problem = null;
            _info($"{_backend.Name}: {devices.Count} device(s): {string.Join(", ", devices.Select(d => $"{d.Name} ({d.Sku})"))}");
            DevicesFetched?.Invoke(devices);
            return devices;
        }
        catch (GoveeException ex)
        {
            Problem = ex.Message;
            throw;
        }
        finally
        {
            RaiseChanged();
        }
    }

    /// <summary>Reads one light's state now, through the LAN when it is there, else the backend.</summary>
    public async Task RefreshStateAsync(GoveeDevice device, CancellationToken ct)
    {
        if (IsOnLan(device))
        {
            await _lan!.RequestStatusAsync(device.Id).ConfigureAwait(false);
            return;
        }

        try
        {
            var state = await _backend.GetStateAsync(device, ct).ConfigureAwait(false);
            Merge(device.Id, state);
        }
        catch (GoveeException ex)
        {
            Fail(device, ex);
        }
    }

    // ---- controls (fire and forget; failures land on the light's state and in the log) ----

    public void SetPower(GoveeDevice device, bool on)
    {
        if (!device.CanSwitch) return;
        Update(device, s => s with { On = on });
        Enqueue(device, Capability.PowerSwitch, Capability.OnOff, Capability.PowerSwitch, JsonValue.Create(on ? 1 : 0),
            lan => lan.SetPowerAsync(device.Id, on));
    }

    public void SetBrightness(GoveeDevice device, int percent)
    {
        if (!device.CanDim) return;
        var (min, max) = device.BrightnessRange;
        var value = Math.Clamp(percent, min, max);
        Update(device, s => s with { Brightness = value, On = true });
        Enqueue(device, Capability.Brightness, Capability.Range, Capability.Brightness, JsonValue.Create(value),
            lan => lan.SetBrightnessAsync(device.Id, value));
    }

    public void SetColor(GoveeDevice device, Rgb color)
    {
        if (!device.CanColor) return;
        Update(device, s => s with { Color = color, Kelvin = 0, Scene = null, On = true });
        // Colour, white and scenes share one slot: only the last of them matters.
        Enqueue(device, "look", Capability.ColorSetting, Capability.ColorRgb, JsonValue.Create(color.ToInt()),
            lan => lan.SetColorAsync(device.Id, color));
    }

    public void SetKelvin(GoveeDevice device, int kelvin)
    {
        if (!device.CanWhite) return;
        var (min, max) = device.KelvinRange;
        var value = Math.Clamp(kelvin, min, max);
        Update(device, s => s with { Kelvin = value, Scene = null, On = true });
        Enqueue(device, "look", Capability.ColorSetting, Capability.ColorTemperatureK, JsonValue.Create(value),
            lan => lan.SetKelvinAsync(device.Id, value));
    }

    public void SetSegmentColor(GoveeDevice device, IReadOnlyList<int> segments, Rgb color)
    {
        if (!device.Has(Capability.SegmentedColorRgb) || segments.Count == 0) return;
        Update(device, s => s with { Scene = null, On = true });
        var value = new JsonObject { ["segment"] = new JsonArray(segments.Select(i => (JsonNode)i).ToArray()), ["rgb"] = color.ToInt() };
        Enqueue(device, $"segment:{string.Join('+', segments)}", Capability.SegmentColorSetting, Capability.SegmentedColorRgb, value, null);
    }

    public void SetSegmentBrightness(GoveeDevice device, IReadOnlyList<int> segments, int percent)
    {
        if (!device.Has(Capability.SegmentedBrightness) || segments.Count == 0) return;
        var value = new JsonObject { ["segment"] = new JsonArray(segments.Select(i => (JsonNode)i).ToArray()), ["brightness"] = Math.Clamp(percent, 0, 100) };
        Enqueue(device, $"segmentBrightness:{string.Join('+', segments)}", Capability.SegmentColorSetting, Capability.SegmentedBrightness, value, null);
    }

    /// <summary>Starts a scene, DIY scene or snapshot. <paramref name="instance"/> picks which.</summary>
    public void StartScene(GoveeDevice device, string instance, NamedOption scene)
    {
        var capability = device.Find(instance);
        if (capability is null) return;
        Update(device, s => s with { Scene = scene.Name, On = true });
        Enqueue(device, "look", capability.Type, instance, scene.Value, null);
    }

    public void SetMusicMode(GoveeDevice device, NamedOption mode, int sensitivity)
    {
        var capability = device.Find(Capability.MusicMode);
        if (capability is null) return;
        Update(device, s => s with { Scene = mode.Name, On = true });
        var value = new JsonObject { ["musicMode"] = mode.Value.DeepClone(), ["sensitivity"] = Math.Clamp(sensitivity, 0, 100) };
        Enqueue(device, "look", capability.Type, Capability.MusicMode, value, null);
    }

    /// <summary>Switches one of a light's extra features (gradient, nightlight…).</summary>
    public void SetFeature(GoveeDevice device, string instance, bool on)
    {
        var capability = device.Find(instance);
        if (capability is null) return;
        Update(device, s => s with { Features = s.Features.SetItem(instance, on) });
        Enqueue(device, instance, capability.Type, instance, JsonValue.Create(on ? 1 : 0), null);
    }

    // ---- queue ----

    private sealed class Channel
    {
        public readonly Lock Lock = new();
        public readonly List<Pending> Items = [];
        public bool Pumping;
        public DateTimeOffset LastSend;
        public DateTimeOffset HoldUntil;
        public int Sends;
    }

    private sealed record Pending(string Slot, string Type, string Instance, JsonNode Value, Func<GoveeLanClient, Task>? Lan);

    private void Enqueue(GoveeDevice device, string slot, string type, string instance, JsonNode value, Func<GoveeLanClient, Task>? lan)
    {
        var channel = _channels.GetOrAdd(device.Id, _ => new Channel());
        lock (channel.Lock)
        {
            // Latest value wins, and moves to the back so the order of different settings is kept.
            channel.Items.RemoveAll(p => p.Slot == slot);
            channel.Items.Add(new Pending(slot, type, instance, value, lan));
            if (channel.Pumping) return;
            channel.Pumping = true;
        }
        _ = PumpAsync(device, channel);
    }

    private async Task PumpAsync(GoveeDevice device, Channel channel)
    {
        var usedCloud = false;
        try
        {
            await Task.Delay(GatherDelay, _cts.Token).ConfigureAwait(false);
            while (true)
            {
                bool viaLan;
                DateTimeOffset due;
                lock (channel.Lock)
                {
                    if (channel.Items.Count == 0)
                    {
                        channel.Pumping = false;
                        break;
                    }
                    viaLan = UsesLan(device, channel.Items[0]);
                    var gap = viaLan ? LanInterval : CloudInterval;
                    due = channel.LastSend + gap > channel.HoldUntil ? channel.LastSend + gap : channel.HoldUntil;
                }

                var wait = due - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _cts.Token).ConfigureAwait(false);

                Pending item;
                lock (channel.Lock)
                {
                    if (channel.Items.Count == 0) continue;
                    item = channel.Items[0];
                    channel.Items.RemoveAt(0);
                    channel.LastSend = DateTimeOffset.UtcNow;
                    channel.Sends++;
                }

                viaLan = UsesLan(device, item);
                usedCloud |= !viaLan;
                try
                {
                    if (viaLan) await item.Lan!(_lan!).ConfigureAwait(false);
                    else await _backend.ControlAsync(device, item.Type, item.Instance, item.Value, _cts.Token).ConfigureAwait(false);
                    Update(device, s => s.Error is null ? s : s with { Error = null });
                }
                catch (GoveeException ex)
                {
                    if (ex.RetryAfter is { } hold)
                    {
                        lock (channel.Lock) channel.HoldUntil = DateTimeOffset.UtcNow + hold;
                    }
                    Fail(device, ex);
                    _error($"{device.Name}: {item.Instance} failed: {ex.Message}", null);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            lock (channel.Lock) channel.Pumping = false;
            _error($"{device.Name}: control queue stopped", ex);
            return;
        }

        _ = ConfirmAsync(device, channel, usedCloud ? CloudConfirmDelay : LanConfirmDelay);
    }

    private bool UsesLan(GoveeDevice device, Pending item) =>
        item.Lan is not null && LanInstances.Contains(item.Instance) && IsOnLan(device);

    /// <summary>Reads the state back once the queue has been quiet for a moment, to confirm or correct it.</summary>
    private async Task ConfirmAsync(GoveeDevice device, Channel channel, TimeSpan delay)
    {
        int sends;
        lock (channel.Lock) sends = channel.Sends;
        try
        {
            await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        lock (channel.Lock)
        {
            // Something else was sent meanwhile; its own confirmation will follow.
            if (channel.Pumping || channel.Sends != sends) return;
        }
        _nextPoll[device.Id] = DateTimeOffset.UtcNow + CloudPollInterval;
        try
        {
            await RefreshStateAsync(device, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _error($"Reading {device.Name} back failed", ex);
        }
    }

    private bool IsBusy(string deviceId)
    {
        if (!_channels.TryGetValue(deviceId, out var channel)) return false;
        // A read taken just after a control can still show the old value (the cloud lags more than the LAN).
        var settle = _lan is { IsRunning: true } lan && lan.TryGetAddress(deviceId, out _)
            ? TimeSpan.FromMilliseconds(300)
            : TimeSpan.FromSeconds(2);
        lock (channel.Lock)
        {
            return channel.Pumping || DateTimeOffset.UtcNow - channel.LastSend < settle;
        }
    }

    // ---- polling ----

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var nextScan = DateTimeOffset.MinValue;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                if (_lan is not null && now >= nextScan)
                {
                    nextScan = now + LanScanInterval;
                    await ScanLanAsync().ConfigureAwait(false);
                }

                foreach (var device in _devices)
                {
                    if (now - _demand.GetValueOrDefault(device.Id) > DemandWindow) continue;
                    if (now < _nextPoll.GetValueOrDefault(device.Id) || IsBusy(device.Id)) continue;

                    _nextPoll[device.Id] = now + (IsOnLan(device) ? LanPollInterval : CloudPollInterval);
                    try
                    {
                        await RefreshStateAsync(device, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _error($"Reading {device.Name} failed", ex);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task RefreshDevicesQuietlyAsync()
    {
        try
        {
            await RefreshDevicesAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (GoveeException ex)
        {
            _info($"{_backend.Name}: device list not loaded: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    // ---- state ----

    private void OnLanDevice(string id, string sku, IPAddress address)
    {
        var known = Find(id);
        _info($"LAN: {(known?.Name ?? sku)} at {address}");
        RaiseChanged();
    }

    private void OnLanState(string id, DeviceState state) => Merge(id, state);

    /// <summary>Applies a state that was read back, unless controls are in flight (it would undo them).</summary>
    private void Merge(string id, DeviceState read)
    {
        _states.AddOrUpdate(id, read, (_, old) =>
        {
            if (IsBusy(id)) return old with { Online = read.Online ?? old.Online };
            return read with
            {
                // Govee reports neither the scene nor (on the LAN) the extra features: keep our own record,
                // but forget the scene once the light shows a colour of its own.
                Scene = read.Color == old.Color && read.Kelvin == old.Kelvin ? old.Scene : null,
                Features = old.Features.SetItems(read.Features),
                Error = null
            };
        });
        RaiseChanged();
    }

    private void Update(GoveeDevice device, Func<DeviceState, DeviceState> change)
    {
        _states.AddOrUpdate(device.Id, _ => change(DeviceState.Unknown), (_, old) => change(old));
        RaiseChanged();
    }

    private void Fail(GoveeDevice device, GoveeException ex)
    {
        if (ex.Status is 401 or 403) Problem = ex.Message;
        Update(device, s => s with { Error = ex.Message });
    }

    private void RaiseChanged()
    {
        lock (_changedLock)
        {
            if (_changedScheduled) return;
            _changedScheduled = true;
        }
        _ = Task.Delay(ChangedCoalesce).ContinueWith(_ =>
        {
            lock (_changedLock) _changedScheduled = false;
            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                _error("Refreshing keys failed", ex);
            }
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        _cts.Cancel();
        UseLan(null);
        _backend.Dispose();
        _cts.Dispose();
    }
}

/// <summary>Values of the Device parameter that are not a single device id.</summary>
internal static class DeviceSelector
{
    public const string All = "*";
    public const char Separator = '+';
}
