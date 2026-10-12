using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Govee.Govee;
using Xunit;

namespace LoupixDeck.Plugin.Govee.Tests;

public class RgbTests
{
    [Theory]
    [InlineData("#FF551C", 255, 85, 28)]
    [InlineData("ff551c", 255, 85, 28)]
    [InlineData("#0F0", 0, 255, 0)]
    public void Parses_hex(string text, byte r, byte g, byte b)
    {
        Assert.True(Rgb.TryParse(text, out var rgb));
        Assert.Equal(new Rgb(r, g, b), rgb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("orange")]
    public void Rejects_bad_hex(string text) => Assert.False(Rgb.TryParse(text, out _));

    [Fact]
    public void Encodes_like_govee() => Assert.Equal(0x0000FF, new Rgb(0, 0, 255).ToInt());

    [Fact]
    public void Hsv_round_trips()
    {
        var orange = new Rgb(255, 85, 28);
        var (h, s, v) = orange.ToHsv();
        Assert.Equal(orange, Rgb.FromHsv(h, s, v));
    }

    [Fact]
    public void Warm_white_is_warmer_than_cool_white()
    {
        var warm = Rgb.FromKelvin(2700);
        var cool = Rgb.FromKelvin(6500);
        Assert.True(warm.B < cool.B);
        Assert.Equal(255, warm.R);
    }
}

public class ParamCodecTests
{
    [Fact]
    public void Scene_objects_survive_without_command_syntax_characters()
    {
        var value = JsonNode.Parse("""{"paramId":4280,"id":3853}""")!;
        var token = ParamCodec.EncodeValue(value);

        Assert.DoesNotContain(',', token);
        Assert.DoesNotContain('(', token);
        Assert.DoesNotContain('+', token);
        Assert.True(JsonNode.DeepEquals(value, ParamCodec.DecodeValue(token)));
    }

    [Fact]
    public void Finds_the_option_by_token()
    {
        var options = new[] { new NamedOption("Sunrise", JsonValue.Create(3054)), new NamedOption("Aurora", JsonValue.Create(3055)) };
        Assert.Equal("Aurora", ParamCodec.FindOption(options, ParamCodec.EncodeValue(JsonValue.Create(3055)))?.Name);
        Assert.Null(ParamCodec.FindOption(options, "not-base64!"));
    }

    [Fact]
    public void Finds_the_option_by_name_as_a_key_stored_it()
    {
        var options = new[] { new NamedOption("Sunrise", JsonValue.Create(3054)), new NamedOption("Rain (soft), night", JsonValue.Create(3060)) };
        Assert.Equal("Sunrise", ParamCodec.FindByName(options, "sunrise")?.Name);
        Assert.Equal(3060, ParamCodec.FindByName(options, ParamCodec.Caption("Rain (soft), night"))?.Value.GetValue<int>());
        Assert.Null(ParamCodec.FindByName(options, "Aurora"));
        Assert.Null(ParamCodec.FindByName(options, null));
    }

    [Theory]
    [InlineData("0-4", new[] { 0, 1, 2, 3, 4 })]
    [InlineData("0+2+4", new[] { 0, 2, 4 })]
    [InlineData("4-2+9", new[] { 2, 3, 4, 9 })]
    [InlineData("12-20", new[] { 12, 13, 14 })]
    [InlineData("*", new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 })]
    public void Parses_segments_within_range(string text, int[] expected) =>
        Assert.Equal(expected, ParamCodec.ParseSegments(text, (0, 14)));
}

public class DeviceTests
{
    // Shaped like the user/devices example in Govee's API reference.
    private const string DeviceJson = """
    {
      "sku": "H6199", "device": "64:09:C5:32:37:36:2D:13", "deviceName": "Desk strip", "type": "devices.types.light",
      "capabilities": [
        {"type":"devices.capabilities.on_off","instance":"powerSwitch","parameters":{"dataType":"ENUM","options":[{"name":"on","value":1},{"name":"off","value":0}]}},
        {"type":"devices.capabilities.range","instance":"brightness","parameters":{"unit":"unit.percent","dataType":"INTEGER","range":{"min":1,"max":100,"precision":1}}},
        {"type":"devices.capabilities.color_setting","instance":"colorRgb","parameters":{"dataType":"INTEGER","range":{"min":0,"max":16777215,"precision":1}}},
        {"type":"devices.capabilities.color_setting","instance":"colorTemperatureK","parameters":{"dataType":"INTEGER","range":{"min":2700,"max":6500,"precision":1}}},
        {"type":"devices.capabilities.segment_color_setting","instance":"segmentedColorRgb","parameters":{"dataType":"STRUCT","fields":[
          {"fieldName":"segment","dataType":"Array","elementRange":{"min":0,"max":14},"size":{"min":1,"max":15},"required":true},
          {"fieldName":"rgb","dataType":"INTEGER","range":{"min":0,"max":16777215,"precision":1},"required":true}]}},
        {"type":"devices.capabilities.dynamic_scene","instance":"lightScene","parameters":{"dataType":"ENUM","options":[{"name":"Sunrise","value":3054},{"name":"Tudum","value":{"paramId":4280,"id":3853}}]}},
        {"type":"devices.capabilities.toggle","instance":"gradientToggle","parameters":{"dataType":"ENUM","options":[{"name":"on","value":1},{"name":"off","value":0}]}},
        {"type":"devices.capabilities.music_setting","instance":"musicMode","parameters":{"dataType":"STRUCT","fields":[
          {"fieldName":"musicMode","dataType":"ENUM","options":[{"name":"Energic","value":5},{"name":"Rhythm","value":3}],"required":true},
          {"fieldName":"sensitivity","dataType":"INTEGER","range":{"min":0,"max":100,"precision":1},"required":false}]}}
      ]
    }
    """;

    private static GoveeDevice Device() => GoveeDevice.FromJson((JsonObject)JsonNode.Parse(DeviceJson)!)!;

    [Fact]
    public void Reads_what_the_device_supports()
    {
        var device = Device();
        Assert.Equal("Desk strip", device.Name);
        Assert.True(device.IsLight && device.CanSwitch && device.CanDim && device.CanColor && device.CanWhite);
        Assert.Equal((2700, 6500), device.KelvinRange);
        Assert.Equal((0, 14), device.SegmentRange);
        Assert.Equal(["gradientToggle"], device.Toggles.Select(t => t.Instance));
        Assert.Equal(["Energic", "Rhythm"], device.MusicModes.Select(m => m.Name));
    }

    [Fact]
    public void Keeps_scene_values_exactly_as_sent()
    {
        var scenes = Device().Find(Capability.LightScene)!.Options;
        Assert.Equal(2, scenes.Count);
        Assert.Equal("""{"paramId":4280,"id":3853}""", scenes[1].Value.ToJsonString());
    }

    [Fact]
    public void Ignores_entries_without_an_id() =>
        Assert.Null(GoveeDevice.FromJson(new JsonObject { ["sku"] = "H6199" }));
}

public class StateTests
{
    [Fact]
    public void Parses_a_cloud_state()
    {
        var json = (JsonObject)JsonNode.Parse("""
        {"requestId":"x","msg":"success","code":200,"payload":{"sku":"H7143","device":"52:8B","capabilities":[
          {"type":"devices.capabilities.online","instance":"online","state":{"value":true}},
          {"type":"devices.capabilities.on_off","instance":"powerSwitch","state":{"value":1}},
          {"type":"devices.capabilities.range","instance":"brightness","state":{"value":42}},
          {"type":"devices.capabilities.color_setting","instance":"colorRgb","state":{"value":16733468}},
          {"type":"devices.capabilities.color_setting","instance":"colorTemperatureK","state":{"value":0}},
          {"type":"devices.capabilities.dynamic_scene","instance":"lightScene","state":{"value":""}},
          {"type":"devices.capabilities.toggle","instance":"gradientToggle","state":{"value":1}}]}}
        """)!;

        var state = GoveeCloudClient.ParseState(json);

        Assert.True(state.Online);
        Assert.True(state.On);
        Assert.Equal(42, state.Brightness);
        Assert.Equal(new Rgb(255, 85, 28), state.Color);
        Assert.Equal(new Rgb(255, 85, 28), state.DisplayColor);
        Assert.True(state.Features["gradientToggle"]);
    }

    [Fact]
    public void White_mode_draws_as_its_temperature()
    {
        var state = new DeviceState { On = true, Color = new Rgb(255, 0, 0), Kelvin = 2700 };
        Assert.Equal(Rgb.FromKelvin(2700), state.DisplayColor);
    }

    [Fact]
    public void Parses_a_lan_status()
    {
        var data = (JsonObject)JsonNode.Parse("""{"onOff":1,"brightness":100,"color":{"r":255,"g":0,"b":0},"colorTemInKelvin":0}""")!;
        var state = GoveeLanClient.ParseStatus(data);
        Assert.True(state.On);
        Assert.Equal(100, state.Brightness);
        Assert.Equal(new Rgb(255, 0, 0), state.Color);
    }
}
