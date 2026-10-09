using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.Govee.Govee;

/// <summary>
/// Govee's cloud API (the "OpenAPI" at openapi.api.govee.com, v1 router). Every call needs the
/// user's own API key. Rate limits that matter here: device list 30/min per account, state and
/// scene lists 30/min per device, control 2/s per device and 12/s per account. Older documents
/// also mention 10,000 requests a day per account, so the client counts what it sends.
/// </summary>
internal sealed class GoveeCloudClient : ILightBackend
{
    public const string BaseUrl = "https://openapi.api.govee.com/router/api/v1/";

    private static readonly TimeSpan DefaultBackoff = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly Queue<DateTimeOffset> _sent = new();
    private readonly Lock _sentLock = new();

    public GoveeCloudClient(Func<string?> apiKey, HttpMessageHandler? handler = null)
    {
        _apiKey = apiKey;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LoupixDeck-Govee", "0.1"));
    }

    public string Name => "Govee cloud";

    /// <summary>Requests sent in the last 24 hours, for the settings page.</summary>
    public int RequestsLast24Hours
    {
        get
        {
            lock (_sentLock)
            {
                Trim(DateTimeOffset.UtcNow);
                return _sent.Count;
            }
        }
    }

    /// <summary>The rate-limit headers of the last response, when Govee sends them.</summary>
    public string? LastRateLimit { get; private set; }

    public async Task<IReadOnlyList<GoveeDevice>> GetDevicesAsync(CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "user/devices", null, ct).ConfigureAwait(false);
        return json["data"] is JsonArray data
            ? data.OfType<JsonObject>().Select(GoveeDevice.FromJson).OfType<GoveeDevice>().ToList()
            : [];
    }

    public async Task<DeviceState> GetStateAsync(GoveeDevice device, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, "device/state", Payload(device), ct).ConfigureAwait(false);
        return ParseState(json);
    }

    public async Task ControlAsync(GoveeDevice device, string type, string instance, JsonNode value, CancellationToken ct)
    {
        var body = Payload(device);
        body["payload"]!["capability"] = new JsonObject
        {
            ["type"] = type,
            ["instance"] = instance,
            ["value"] = value.DeepClone()
        };
        await SendAsync(HttpMethod.Post, "device/control", body, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NamedOption>> GetScenesAsync(GoveeDevice device, string instance, CancellationToken ct)
    {
        var path = instance == Capability.DiyScene ? "device/diy-scenes" : "device/scenes";
        var json = await SendAsync(HttpMethod.Post, path, Payload(device), ct).ConfigureAwait(false);
        if (json["payload"]?["capabilities"] is not JsonArray capabilities) return [];

        return capabilities.OfType<JsonObject>()
            .Where(c => string.Equals(c["instance"]?.ToString(), instance, StringComparison.OrdinalIgnoreCase))
            .SelectMany(c => new Capability(string.Empty, instance, c["parameters"] as JsonObject).Options)
            .ToList();
    }

    /// <summary>Turns a <c>device/state</c> response into a <see cref="DeviceState"/>. An empty string means "cannot be queried".</summary>
    internal static DeviceState ParseState(JsonObject json)
    {
        var state = new DeviceState { ReadAt = DateTimeOffset.Now };
        if (json["payload"]?["capabilities"] is not JsonArray capabilities) return state;

        foreach (var capability in capabilities.OfType<JsonObject>())
        {
            if (capability["state"]?["value"] is not JsonValue value) continue;
            var instance = capability["instance"]?.ToString();
            if (capability["type"]?.ToString() == Capability.Toggle && instance is not null && AsInt(value) is { } toggle)
            {
                state = state with { Features = state.Features.SetItem(instance, toggle != 0) };
                continue;
            }
            state = instance switch
            {
                "online" => state with { Online = AsBool(value) },
                Capability.PowerSwitch => state with { On = AsInt(value) is { } on ? on != 0 : null },
                Capability.Brightness => state with { Brightness = AsInt(value) },
                Capability.ColorRgb => AsInt(value) is { } rgb ? state with { Color = Rgb.FromInt(rgb) } : state,
                Capability.ColorTemperatureK => state with { Kelvin = AsInt(value) },
                _ => state
            };
        }
        return state;
    }

    private static JsonObject Payload(GoveeDevice device) => new()
    {
        ["requestId"] = Guid.NewGuid().ToString(),
        ["payload"] = new JsonObject { ["sku"] = device.Sku, ["device"] = device.Id }
    };

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        var key = _apiKey()?.Trim();
        if (string.IsNullOrEmpty(key)) throw new GoveeException("No API key");

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Govee-API-Key", key);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        Count();
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GoveeException("Govee did not answer in time");
        }
        catch (HttpRequestException ex)
        {
            throw new GoveeException("Cannot reach Govee", inner: ex);
        }

        using (response)
        {
            RememberRateLimit(response);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    throw new GoveeException("API key rejected", (int)response.StatusCode);
                case HttpStatusCode.TooManyRequests:
                    throw new GoveeException("Rate limited", 429, RetryAfter(response));
            }

            JsonObject? json = null;
            try
            {
                json = text.Length > 0 ? JsonNode.Parse(text) as JsonObject : null;
            }
            catch (System.Text.Json.JsonException)
            {
                // Reported below with the status code.
            }

            // Govee also reports errors inside a 200 response, as {"code": 4xx, "msg"|"message": "..."}.
            var code = json?["code"] is JsonValue c && c.TryGetValue<int>(out var number) ? number : (int)response.StatusCode;
            if (!response.IsSuccessStatusCode || code is < 200 or >= 300)
            {
                var message = json?["msg"]?.ToString() ?? json?["message"]?.ToString();
                if (code == 429) throw new GoveeException("Rate limited", 429, RetryAfter(response));
                throw new GoveeException(string.IsNullOrWhiteSpace(message) ? $"Govee error {code}" : message, code);
            }
            return json ?? new JsonObject();
        }
    }

    private void Count()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_sentLock)
        {
            _sent.Enqueue(now);
            Trim(now);
        }
    }

    private void Trim(DateTimeOffset now)
    {
        while (_sent.Count > 0 && now - _sent.Peek() > TimeSpan.FromHours(24)) _sent.Dequeue();
    }

    private void RememberRateLimit(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("API-RateLimit-Remaining", out var remaining))
        {
            var limit = response.Headers.TryGetValues("API-RateLimit-Limit", out var l) ? l.FirstOrDefault() : "?";
            LastRateLimit = $"{remaining.FirstOrDefault()} of {limit} left";
        }
    }

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return delta;
        if (response.Headers.TryGetValues("API-RateLimit-Reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), out var reset))
        {
            // Seen as a Unix timestamp in seconds on the older API.
            var wait = DateTimeOffset.FromUnixTimeSeconds(reset) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero && wait < TimeSpan.FromHours(1)) return wait;
        }
        return DefaultBackoff;
    }

    private static int? AsInt(JsonValue value) =>
        value.TryGetValue<int>(out var i) ? i
        : value.TryGetValue<long>(out var l) ? (int)l
        : value.TryGetValue<double>(out var d) ? (int)Math.Round(d)
        : value.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p
        : null;

    private static bool? AsBool(JsonValue value) =>
        value.TryGetValue<bool>(out var b) ? b
        : AsInt(value) is { } i ? i != 0
        : value.TryGetValue<string>(out var s) && bool.TryParse(s, out var p) ? p
        : null;

    public void Dispose() => _http.Dispose();
}
