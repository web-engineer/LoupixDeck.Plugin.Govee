using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.Govee.Govee;

/// <summary>
/// Where devices, state and controls come from: Govee's cloud API, or the built-in demo lights.
/// LAN control is not a backend of its own; <see cref="LightService"/> uses it as a fast path for
/// the controls it supports and falls back to the backend for everything else.
/// </summary>
internal interface ILightBackend : IDisposable
{
    /// <summary>Short name for logs and the settings page, e.g. "Govee cloud".</summary>
    string Name { get; }

    Task<IReadOnlyList<GoveeDevice>> GetDevicesAsync(CancellationToken ct);

    Task<DeviceState> GetStateAsync(GoveeDevice device, CancellationToken ct);

    Task ControlAsync(GoveeDevice device, string type, string instance, JsonNode value, CancellationToken ct);

    /// <summary>The device's full scene list for <see cref="Capability.LightScene"/> or <see cref="Capability.DiyScene"/>.</summary>
    Task<IReadOnlyList<NamedOption>> GetScenesAsync(GoveeDevice device, string instance, CancellationToken ct);
}

/// <summary>A failure the user should see in words, e.g. "Rate limited" or "API key rejected".</summary>
internal sealed class GoveeException(string message, int? status = null, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? Status { get; } = status;

    /// <summary>Set on a rate-limit response: how long to hold further requests.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
