using System.Text;
using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.Govee.Govee;

/// <summary>
/// Turns values into command parameters and back. The host stores a command as
/// <c>Name(a,b,c)</c>, so a parameter must not contain commas or parentheses: a scene value, which
/// can be a JSON object, is stored as base64url, and segment lists use "+" and "-".
/// </summary>
internal static class ParamCodec
{
    public static string EncodeValue(JsonNode value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value.ToJsonString()))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static JsonNode? DecodeValue(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var base64 = token.Trim().Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The option whose value matches the token, or null.</summary>
    public static NamedOption? FindOption(IEnumerable<NamedOption> options, string? token)
    {
        var value = DecodeValue(token);
        return value is null ? null : options.FirstOrDefault(o => JsonNode.DeepEquals(o.Value, value));
    }

    /// <summary>
    /// Parses a segment list such as "0-4", "0+2+4" or "0-2+7" (0-based), keeping only indices
    /// within <paramref name="range"/>. Empty or "*" means every segment.
    /// </summary>
    public static IReadOnlyList<int> ParseSegments(string? text, (int Min, int Max) range)
    {
        var all = Enumerable.Range(range.Min, range.Max - range.Min + 1);
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == "*") return all.ToList();

        var result = new SortedSet<int>();
        foreach (var part in text.Split(['+', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', 2, StringSplitOptions.TrimEntries);
            if (!int.TryParse(bounds[0], out var from)) continue;
            var to = bounds.Length == 2 && int.TryParse(bounds[1], out var end) ? end : from;
            if (to < from) (from, to) = (to, from);
            for (var i = Math.Max(from, range.Min); i <= Math.Min(to, range.Max); i++) result.Add(i);
        }
        return result.ToList();
    }
}
