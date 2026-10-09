using LoupixDeck.Plugin.Govee.Govee;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Govee.Rendering;

/// <summary>
/// How Govee keys look: dark keys where a lit light glows in its real colour, scaled by its
/// brightness, with the light's name (or the running scene) underneath. Every key and folder slot
/// draws through here so they read as one set. Drawing uses cached state only.
/// </summary>
internal static class LightKey
{
    public static readonly PluginColor Background = new(18, 18, 18);
    public static readonly PluginColor Text = new(235, 235, 235);
    public static readonly PluginColor Faded = new(150, 150, 150);
    public static readonly PluginColor Dim = new(70, 70, 70);
    public static readonly PluginColor Problem = new(232, 72, 72);
    public static readonly PluginColor Ring = new(255, 255, 255);

    /// <summary>A light (or group) and what it is doing: the main power key.</summary>
    public static void DrawLight(IRenderCanvas canvas, string caption, DeviceState state, string? badge = null)
    {
        canvas.Clear(Background);
        var (w, h) = (canvas.Width, canvas.Height);
        int cx = w / 2, cy = (int)(h * 0.42), r = (int)(w * 0.27);

        if (state.Online == false)
        {
            canvas.DrawSymbol("cloud-off-outline", cx - r, cy - r, r * 2, r * 2, Problem);
            Caption(canvas, caption, "Offline", Problem);
            return;
        }

        if (!state.HasData)
        {
            canvas.DrawCircle(cx, cy, r, 2, Dim);
            canvas.DrawSymbol("lightbulb-outline", cx - r / 2 - 4, cy - r / 2 - 4, r + 8, r + 8, Faded);
            Caption(canvas, caption, state.Error, Problem);
            return;
        }

        if (state.On == true)
        {
            var color = state.DisplayColor;
            Glow(canvas, cx, cy, r, color, state.Brightness ?? 100);
            canvas.DrawSymbol("lightbulb-on", cx - r / 2 - 2, cy - r / 2 - 2, r + 4, r + 4, InkOn(color));
            if (state.Brightness is { } brightness)
            {
                canvas.DrawText($"{brightness}%", w - 34, 2, 32, 14, Faded, 10, TextHAlign.Right, TextVAlign.Top);
            }
        }
        else
        {
            canvas.DrawCircle(cx, cy, r, 2, Dim);
            canvas.DrawSymbol("lightbulb-off-outline", cx - r / 2 - 2, cy - r / 2 - 2, r + 4, r + 4, Faded);
        }

        if (badge is not null)
        {
            canvas.DrawText(badge, 2, 2, w / 2, 14, Faded, 10, TextHAlign.Left, TextVAlign.Top);
        }
        if (state.Error is not null) canvas.FillCircle(7, 7, 4, Problem);

        Caption(canvas, state.On == true && state.Scene is { } scene ? scene : caption, state.Error, Problem);
    }

    /// <summary>A colour or white the key sets; ringed while the light shows it.</summary>
    public static void DrawSwatch(IRenderCanvas canvas, Rgb color, string caption, bool active, string? symbol = null, string? error = null)
    {
        canvas.Clear(Background);
        var (w, h) = (canvas.Width, canvas.Height);
        int cx = w / 2, cy = (int)(h * 0.42), r = (int)(w * 0.25);

        canvas.FillCircle(cx, cy, r, Of(color));
        if (active) canvas.DrawCircle(cx, cy, r + 5, 3, Ring);
        if (symbol is not null) canvas.DrawSymbol(symbol, cx - r / 2, cy - r / 2, r, r, InkOn(color));
        if (error is not null) canvas.FillCircle(7, 7, 4, Problem);
        Caption(canvas, caption, null, Problem);
    }

    /// <summary>A brightness level (or any 0–1 amount) as a ring around a value.</summary>
    public static void DrawLevel(IRenderCanvas canvas, double normalized, string value, string caption, Rgb accent, bool active)
    {
        canvas.Clear(Background);
        var (w, h) = (canvas.Width, canvas.Height);
        int cx = w / 2, cy = (int)(h * 0.42), r = (int)(w * 0.27);

        canvas.DrawArc(cx - r, cy - r, r * 2, r * 2, 0, 360, 5, Dim);
        canvas.DrawArc(cx - r, cy - r, r * 2, r * 2, -90, (float)(360 * Math.Clamp(normalized, 0, 1)), 5,
            active ? Of(accent) : Faded);
        canvas.DrawText(value, cx - r, cy - 10, r * 2, 20, Text, 14, TextHAlign.Center, TextVAlign.Middle, bold: true);
        Caption(canvas, caption, null, Problem);
    }

    /// <summary>A named action (scene, feature, page) with a symbol; highlighted while active.</summary>
    public static void DrawAction(IRenderCanvas canvas, string symbol, string caption, bool active, Rgb? accent = null, string? error = null)
    {
        canvas.Clear(Background);
        var (w, h) = (canvas.Width, canvas.Height);
        int cx = w / 2, cy = (int)(h * 0.40), r = (int)(w * 0.25);

        var tint = active ? Of(accent ?? Rgb.White) : Faded;
        if (active) Glow(canvas, cx, cy, r, accent ?? Rgb.White, 40);
        canvas.DrawSymbol(symbol, cx - r + 4, cy - r + 4, r * 2 - 8, r * 2 - 8, active ? InkOn(accent ?? Rgb.White) : tint);
        if (error is not null) canvas.FillCircle(7, 7, 4, Problem);
        Caption(canvas, caption, null, Problem);
    }

    /// <summary>A plain message, e.g. "No API key" or "Unknown light".</summary>
    public static void DrawMessage(IRenderCanvas canvas, string symbol, string message, PluginColor color)
    {
        canvas.Clear(Background);
        var (w, h) = (canvas.Width, canvas.Height);
        var s = (int)(w * 0.4);
        canvas.DrawSymbol(symbol, (w - s) / 2, (int)(h * 0.18), s, s, color);
        Caption(canvas, message, null, color);
    }

    public static PluginColor Of(Rgb color, byte alpha = 255) => new(color.R, color.G, color.B, alpha);

    private static void Glow(IRenderCanvas canvas, int cx, int cy, int r, Rgb color, int brightness)
    {
        // Brightness 1–100 maps to 35–100 % opacity, so a dim light still reads as "on".
        var alpha = (byte)Math.Clamp(90 + brightness * 165 / 100, 90, 255);
        canvas.FillCircle(cx, cy, r + 6, Of(color, (byte)(alpha / 4)));
        canvas.FillCircle(cx, cy, r, Of(color, alpha));
    }

    /// <summary>Dark ink on a light colour, white ink on a dark one.</summary>
    private static PluginColor InkOn(Rgb color)
    {
        var luminance = 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
        return luminance > 150 ? new PluginColor(20, 20, 20, 220) : new PluginColor(255, 255, 255, 235);
    }

    /// <summary>The bottom line: the caption, or the problem in red when there is one.</summary>
    private static void Caption(IRenderCanvas canvas, string caption, string? problem, PluginColor problemColor)
    {
        var (w, h) = (canvas.Width, canvas.Height);
        var text = Fit(canvas, problem ?? caption, w - 6, 11);
        canvas.DrawText(text, 3, h - 22, w - 6, 18, problem is null ? Text : problemColor, 11, TextHAlign.Center, TextVAlign.Middle);
    }

    private static string Fit(IRenderCanvas canvas, string text, int width, float size)
    {
        if (canvas.MeasureText(text, size) <= width) return text;
        for (var length = text.Length - 1; length > 1; length--)
        {
            var candidate = text[..length].TrimEnd() + "…";
            if (canvas.MeasureText(candidate, size) <= width) return candidate;
        }
        return text[..1];
    }
}
