using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.Govee.Govee;

/// <summary>
/// Govee's LAN API: JSON over UDP, no key and no rate limit, for lights that have "LAN Control"
/// switched on in the Govee Home app. Scans go to the multicast group 239.255.255.250:4001 (and to
/// any addresses the user listed, for networks that drop multicast), commands go to the light's
/// port 4003, and every reply comes back to port 4002 on this machine. Only power, brightness,
/// colour and white temperature exist on the LAN; everything else stays on the cloud.
/// </summary>
/// <remarks>
/// Controls are not acknowledged, so the client follows each one with a status request. Status
/// replies do not name the device, so they are matched to it by the sender's address.
/// </remarks>
internal sealed class GoveeLanClient : IDisposable
{
    public const int ScanPort = 4001;
    public const int ListenPort = 4002;
    public const int CommandPort = 4003;
    public static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.255.250");

    private readonly ConcurrentDictionary<string, IPAddress> _addresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string, Exception?> _log;
    private UdpClient? _socket;
    private CancellationTokenSource? _cts;

    public GoveeLanClient(Action<string, Exception?> log) => _log = log;

    /// <summary>Raised for each scan reply: device id, SKU and address.</summary>
    public event Action<string, string, IPAddress>? DeviceFound;

    /// <summary>Raised for each status reply, with the device it was matched to.</summary>
    public event Action<string, DeviceState>? StateReceived;

    /// <summary>Why the client is not listening, e.g. another app holds port 4002. Null while it works.</summary>
    public string? Problem { get; private set; }

    public bool IsRunning => _socket is not null;

    public IReadOnlyDictionary<string, IPAddress> Addresses => _addresses;

    public bool TryGetAddress(string deviceId, out IPAddress address) => _addresses.TryGetValue(deviceId, out address!);

    public void Start()
    {
        if (_socket is not null) return;
        try
        {
            var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, ListenPort));
            _socket = socket;
            _cts = new CancellationTokenSource();
            Problem = null;
            _ = ReceiveLoopAsync(socket, _cts.Token);
        }
        catch (SocketException ex)
        {
            Problem = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"Port {ListenPort} is in use by another app (Home Assistant, Homebridge…); using the cloud only"
                : $"Cannot listen on UDP port {ListenPort}: {ex.Message}";
            _log(Problem, ex);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _socket?.Dispose();
        _socket = null;
    }

    /// <summary>Asks every light on the network, and each listed address, to announce itself.</summary>
    public async Task ScanAsync(IEnumerable<IPAddress> extraAddresses)
    {
        var socket = _socket;
        if (socket is null) return;

        var scan = Message("scan", new JsonObject { ["account_topic"] = "reserve" });
        await SendQuietly(socket, scan, new IPEndPoint(MulticastGroup, ScanPort)).ConfigureAwait(false);
        foreach (var address in extraAddresses)
        {
            await SendQuietly(socket, scan, new IPEndPoint(address, ScanPort)).ConfigureAwait(false);
        }
    }

    public Task RequestStatusAsync(string deviceId) =>
        Send(deviceId, Message("devStatus", new JsonObject()));

    public Task SetPowerAsync(string deviceId, bool on) =>
        Send(deviceId, Message("turn", new JsonObject { ["value"] = on ? 1 : 0 }));

    public Task SetBrightnessAsync(string deviceId, int percent) =>
        Send(deviceId, Message("brightness", new JsonObject { ["value"] = Math.Clamp(percent, 1, 100) }));

    public Task SetColorAsync(string deviceId, Rgb color) =>
        Send(deviceId, Message("colorwc", new JsonObject
        {
            ["color"] = new JsonObject { ["r"] = color.R, ["g"] = color.G, ["b"] = color.B },
            ["colorTemInKelvin"] = 0
        }));

    public Task SetKelvinAsync(string deviceId, int kelvin) =>
        Send(deviceId, Message("colorwc", new JsonObject
        {
            ["color"] = new JsonObject { ["r"] = 0, ["g"] = 0, ["b"] = 0 },
            ["colorTemInKelvin"] = kelvin
        }));

    private async Task Send(string deviceId, string message)
    {
        var socket = _socket ?? throw new GoveeException("LAN control is not running");
        if (!_addresses.TryGetValue(deviceId, out var address)) throw new GoveeException("Light not found on the LAN");
        var bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(bytes, new IPEndPoint(address, CommandPort)).ConfigureAwait(false);
    }

    private async Task SendQuietly(UdpClient socket, string message, IPEndPoint target)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await socket.SendAsync(bytes, target).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            // No route for multicast (VPN, no network yet) is normal; listed addresses still work.
            _log($"LAN scan to {target} failed: {ex.Message}", null);
        }
    }

    private async Task ReceiveLoopAsync(UdpClient socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // Windows reports an ICMP "port unreachable" from an earlier send as a receive error.
                if (ct.IsCancellationRequested) return;
                _log($"LAN receive error: {ex.SocketErrorCode}", null);
                continue;
            }

            try
            {
                Handle(result);
            }
            catch (Exception ex)
            {
                _log("Ignoring a LAN message that could not be read", ex);
            }
        }
    }

    private void Handle(UdpReceiveResult result)
    {
        if (JsonNode.Parse(result.Buffer) is not JsonObject json || json["msg"] is not JsonObject msg) return;
        var data = msg["data"] as JsonObject;

        switch (msg["cmd"]?.ToString())
        {
            case "scan" when data is not null && data["device"]?.ToString() is { Length: > 0 } id:
                // Some firmware leaves the address out of the reply; the sender's address is the light.
                if (!IPAddress.TryParse(data["ip"]?.ToString(), out var address)) address = result.RemoteEndPoint.Address;
                _addresses[id] = address;
                DeviceFound?.Invoke(id, data["sku"]?.ToString() ?? string.Empty, address);
                break;

            case "devStatus" when data is not null:
                var device = _addresses.FirstOrDefault(pair => pair.Value.Equals(result.RemoteEndPoint.Address)).Key;
                if (device is not null) StateReceived?.Invoke(device, ParseStatus(data));
                break;
        }
    }

    internal static DeviceState ParseStatus(JsonObject data)
    {
        var kelvin = data["colorTemInKelvin"]?.GetValue<int>();
        Rgb? color = data["color"] is JsonObject c
            ? new Rgb((byte)(c["r"]?.GetValue<int>() ?? 0), (byte)(c["g"]?.GetValue<int>() ?? 0), (byte)(c["b"]?.GetValue<int>() ?? 0))
            : null;
        return new DeviceState
        {
            Online = true,
            On = data["onOff"] is JsonValue on ? on.GetValue<int>() != 0 : null,
            Brightness = data["brightness"]?.GetValue<int>(),
            Color = color,
            Kelvin = kelvin,
            ReadAt = DateTimeOffset.Now
        };
    }

    private static string Message(string cmd, JsonObject data) =>
        new JsonObject { ["msg"] = new JsonObject { ["cmd"] = cmd, ["data"] = data } }.ToJsonString();

    public void Dispose() => Stop();
}
