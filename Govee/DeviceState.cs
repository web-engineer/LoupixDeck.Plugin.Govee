using System.Collections.Immutable;

namespace LoupixDeck.Plugin.Govee.Govee;

/// <summary>
/// What the plugin believes a light is doing. Values are null until known. A control sets the
/// matching fields straight away (so a dial follows the hand rather than the network), and the next
/// state read confirms or corrects them.
/// </summary>
internal sealed record DeviceState
{
    public static readonly DeviceState Unknown = new();

    public bool? Online { get; init; }
    public bool? On { get; init; }
    public int? Brightness { get; init; }

    /// <summary>The colour, when the light is in colour mode.</summary>
    public Rgb? Color { get; init; }

    /// <summary>The white temperature, when the light is in white mode (Govee reports 0 otherwise).</summary>
    public int? Kelvin { get; init; }

    /// <summary>
    /// The scene, DIY scene, snapshot or music mode last started from the deck. Govee does not
    /// report these back, so it is only our own record and is cleared by any colour change.
    /// </summary>
    public string? Scene { get; init; }

    /// <summary>Extra on/off features by instance (gradientToggle, nightlightToggle…), as far as known.</summary>
    public ImmutableDictionary<string, bool> Features { get; init; } = ImmutableDictionary<string, bool>.Empty;

    /// <summary>The last control or read error for this light, cleared by the next success.</summary>
    public string? Error { get; init; }

    public DateTimeOffset? ReadAt { get; init; }

    public bool HasData => On is not null || Brightness is not null || Color is not null || Kelvin is not null;

    /// <summary>What the light looks like right now, for drawing it on a key.</summary>
    public Rgb DisplayColor => Kelvin is > 0 ? Rgb.FromKelvin(Kelvin.Value) : Color ?? Rgb.White;
}
