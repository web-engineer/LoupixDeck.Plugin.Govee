using System.Globalization;

namespace LoupixDeck.Plugin.Govee.Govee;

/// <summary>
/// A 24-bit colour as Govee encodes it: one integer, <c>(R &lt;&lt; 16) | (G &lt;&lt; 8) | B</c>, plus the
/// conversions the dials need (hue and saturation) and an approximation of a white temperature.
/// </summary>
internal readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb White = new(255, 255, 255);

    public int ToInt() => (R << 16) | (G << 8) | B;

    public static Rgb FromInt(int value) => new((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => ToHex();

    /// <summary>Parses <c>#RRGGBB</c>, <c>RRGGBB</c> or <c>#RGB</c>.</summary>
    public static bool TryParse(string? text, out Rgb rgb)
    {
        rgb = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return false;
        rgb = FromInt(value);
        return true;
    }

    /// <summary>Hue in degrees (0–360), saturation and value (0–1).</summary>
    public (double Hue, double Saturation, double Value) ToHsv()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * ((g - b) / delta % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }
        if (hue < 0) hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }

    public static Rgb FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return new Rgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    /// <summary>
    /// The colour a white temperature looks like, for drawing it on a key (Tanner Helland's fit of
    /// the blackbody curve). Only an approximation; the light itself does the real thing.
    /// </summary>
    public static Rgb FromKelvin(int kelvin)
    {
        var t = Math.Clamp(kelvin, 1000, 40000) / 100.0;
        double r, g, b;

        if (t <= 66)
        {
            r = 255;
            g = 99.4708025861 * Math.Log(t) - 161.1195681661;
            b = t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        }
        else
        {
            r = 329.698727446 * Math.Pow(t - 60, -0.1332047592);
            g = 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
            b = 255;
        }
        return new Rgb(ToByte(r / 255), ToByte(g / 255), ToByte(b / 255));
    }

    private static byte ToByte(double unit) => (byte)Math.Clamp(Math.Round(unit * 255), 0, 255);
}
