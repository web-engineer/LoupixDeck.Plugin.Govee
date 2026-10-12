# LoupixDeck.Plugin.Govee

A [LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck) plugin for Govee
lights. It lets you switch, dim, colour and set the white of your lights from
the deck. You can also start scenes, DIY scenes, snapshots and music modes,
switch features such as gradient and nightlight, and colour segments. Keys
glow in each light's real colour and brightness, and dials adjust brightness,
colour, saturation and white temperature.

It is built against `LoupixDeck.PluginSdk` 1.30.0 and targets .NET 10. It runs
on macOS, Linux and Windows.

Made by [**web-engineer**](https://www.web-engineer.co.uk/)
([@web-engineer](https://github.com/web-engineer)).

This is an independent project and is not affiliated with or endorsed by
Govee. Govee is a trademark of its owner.

> **Tested on:** H6076 floor lamps, over the cloud and the LAN. Every key, dial
> and page is also tested against the built-in demo lights and a simulated Govee
> server. Govee models differ in what they offer, so please report anything
> that behaves differently on yours, and include the light's model (for
> example H6199).

## What you get

| | Key | Dial |
|---|---|---|
| Light on/off | Switches the light. The key glows in the light's colour, dimmed to its brightness, and shows the running scene. Can be set to on, off or toggle. | Press switches on/off |
| Brightness | Sets 10–100 %, or steps ±10 % | Turn to dim; the indicator shows the % |
| Colour | 8 preset colours or any `#RRGGBB`. Ringed while the light shows that colour. | **Colour dial** goes round the colour wheel; **Saturation dial** goes from pastel to full colour |
| White | 2200–6500 K, or steps ±500 K | Turn for warmer or cooler white |
| Scenes, DIY scenes, snapshots | One key per scene, listed from your light | **Scene dial** steps through the scenes, starting each one |
| Music modes | Energic, Rhythm, … (whatever the light offers) | **Music mode dial** steps through the modes |
| Features | Gradient, nightlight, a lamp's second light, … | — |
| Segments | Colours some segments of a strip, e.g. `0-4` or `0+2+4` | — |
| Controls page | Opens a page for one light: power, scenes, whites, colours, music and features. Pages page themselves; on this page dial 1 is brightness, dial 2 colour, dial 3 white and dial 4 saturation. | — |

Every command also works on a **group**. Choose *All lights* in the menu, or
join device ids with `+` in the Device field. A scene or music mode on a group
starts each light's own scene of that name. A light that doesn't have that
scene keeps what it is showing.

## Install

**From the Plugin Store.** Once the plugin is listed, it appears under
LoupixDeck → Plugins → Store and updates from there.

**Manually.** Download `govee-<version>-any.zip` from the
[Releases](https://github.com/web-engineer/LoupixDeck.Plugin.Govee/releases) page and
unzip it into the plugin folder:

| OS | Folder |
|---|---|
| macOS, Linux | `~/.config/LoupixDeck/plugins/govee/` |
| Windows | `%APPDATA%\LoupixDeck\plugins\govee\` |

Restart LoupixDeck, then enable the plugin for your device under
Plugins → Installed.

## Get a Govee API key (required)

The plugin uses Govee's own API to find your lights and control them. Govee
doesn't offer a "sign in with Govee" for other apps. Instead, each person asks
Govee for a personal **API key** and pastes it into the plugin. The key is
free, and you only need to get it once.

1. Open the **Govee Home** app on your phone and sign in with the account your
   lights are on.
2. Go to your **Profile**, then **Settings**.
3. Tap **Apply for API Key**.
4. Fill in the short form (Govee asks who you are and what the key is for;
   "Control my lights from a LoupixDeck controller" is fine), accept Govee's
   API terms and submit.
5. Govee sends you the key, usually by email. If it hasn't arrived, check
   your spam folder.

Govee's own (very brief) instructions are
[here](https://developer.govee.com/reference/apply-you-govee-api-key).

### Enter the key in LoupixDeck

1. Go to **Settings → Plugins → Govee Lights**.
2. Paste the key into **API key** and save.
3. Press **Test connection**. It should list your lights by name. If it
   says *API key rejected*, check you copied the whole key, and that it's
   your newest one (see below).

### Good to know

- **One key per account. A new key switches off the old one.** Since May 2026,
  asking Govee for another key immediately disables the previous one. That
  includes the key in Home Assistant, Homebridge or any other app using it.
  If you already use a key elsewhere, paste that same key here instead of
  applying again.
- **Your key stays on your computer.** It is stored in LoupixDeck's settings
  for this plugin and is only ever sent to Govee, never to us or anyone else.
  Treat it like a password: anyone with it can control your lights.
- **Which lights work:** the key reaches the lights on your Govee account
  that are connected over Wi-Fi; Bluetooth-only models can't be reached.
  Govee's list of supported models is
  [here](https://developer.govee.com/docs/support-product-model).

**No lights to hand?** Turn on **Demo lights**. Three pretend lights (a strip,
a floor lamp and a bulb) then behave like real ones, so you can set up and try
every key first.

## Add controls

Add a key or dial as usual and open the **Govee** section of the command
picker. At the top are controls for all your lights at once:

- **On a touch key or button:** *Scenes, all lights* and *Music modes, all
  lights*. Each lists every scene or mode any of your lights has.
- **On a dial:** *Scenes dial, all lights* and *Music modes dial, all lights*.

Below those, each light has its own folder. When two lights have the same name,
the end of each one's device id is added so you can tell them apart.

- **On a touch key or button:** Light on/off, Turn on, Turn off, Controls page,
  Brightness ›, Colours ›, Whites ›, Scenes ›, DIY scenes ›, Snapshots ›,
  Music modes ›, Features › and Segments ›.
- **On a dial:** Brightness, Colour, White, Saturation, Scenes and Music modes
  dials. Each fills the whole dial: turn left and right to adjust, press to
  switch the light on or off. The Scenes and Music modes dials start the next
  scene or mode with each click and show its name; a fast turn only sends the
  one it stops on. The same dials are offered as **dial presets**, one per
  light, plus Scenes and Music modes for all lights.

You can change a command's values in the button editor afterwards. For
example, set a custom colour, change the dial step, or turn a toggle key into
an on key.

## LAN control

Lights with **LAN Control** switched on (Govee Home app → the light → Settings)
are controlled directly over your network for power, brightness, colour and
white. This is faster, and it doesn't count against Govee's limits. The plugin
finds these lights by itself; **Find LAN lights** in the settings shows which
ones answered. Scenes, music, features and segments always go through Govee's
cloud.

- LAN control listens on UDP port 4002. If Home Assistant, Homebridge or
  another Govee tool on the **same computer** already uses that port, the
  plugin uses the cloud for everything and says so in the settings.
- Discovery uses multicast. If your network blocks it (VPNs, guest Wi-Fi,
  separate VLANs), list the lights' IP addresses under **Light addresses**.

## Limits worth knowing

- **Govee limits requests.** A control can be sent about twice a second per
  light, and older documentation mentions 10,000 requests a day per account.
  The plugin applies a dial turn to the key immediately, but only sends the
  latest value, at the pace Govee allows, and it always sends the final one.
  **Test connection** shows how many requests the plugin sent in the last 24
  hours.
- **State is read only while it is on screen.** Cloud lights are read every 60
  seconds (configurable) and LAN lights every 5 seconds. A change made in the
  Govee app shows up on the next read.
- **Govee doesn't report which scene is running.** A scene key lights up for
  the scene last started from the deck, and stops once the light shows a
  plain colour again.
- Segment colours are sent but never read back, and feature states only when
  Govee reports them, so those keys can show what was last set from the deck.

## Testing against real lights without LoupixDeck

`tools/GoveeProbe` is a developer tool, not something plugin users need. It is
a small command-line program that uses the same code as the plugin and prints
each light's state before and after every control. That makes it useful when
the lights are somewhere you can't see them, for example over a VPN.

It needs the same API key as the plugin. A command-line tool has no settings
page, so it reads the key from the `GOVEE_API_KEY` environment variable. That
also keeps the key out of your shell history and out of the command line other
processes can see.

```sh
read -rs GOVEE_API_KEY && export GOVEE_API_KEY   # paste the key; it isn't echoed
dotnet run --project tools/GoveeProbe -- list
dotnet run --project tools/GoveeProbe -- color "Desk strip" '#00FF00'
dotnet run --project tools/GoveeProbe -- dial desk          # a fast dial turn: watch how few requests it sends
dotnet run --project tools/GoveeProbe -- scene desk sunrise
dotnet run --project tools/GoveeProbe -- lan 192.168.1.40   # LAN discovery, plus an address to ask directly
dotnet run --project tools/GoveeProbe -- --demo list        # no key needed
```

`list` also names any capability the plugin does not use yet. Please include
that line when you request support for a light.

## Development

```sh
dotnet build                                    # builds and copies into ~/.config/LoupixDeck/plugins/govee
dotnet build -p:LoupixDebugHost=true            # …into a Debug host's plugin folder instead
dotnet test tests/LoupixDeck.Plugin.Govee.Tests
```

`LoupixDeck.PluginSdk` 1.30.0 is not on nuget.org yet, so `nuget.config`
restores it from a LoupixDeck checkout next to this repository. Build that
checkout once with
`dotnet build ../LoupixDeck/LoupixDeck.PluginSdk -c Release`.

Layout:

- `Govee/`: the Govee layer, which has no host dependency. It holds the cloud
  client, the LAN client, the demo lights, and `LightService` (state cache,
  request pacing, polling).
- `Commands/`: the commands and the controls page.
- `Rendering/LightKey.cs`: how the keys look.
- `GoveePlugin.cs`: settings, the device menu, dial presets and the device
  cache.

Command names (`Govee.Power`, `Govee.Brightness`, …), their parameter order and
the settings keys are stored in users' profiles. Never rename or reorder them.

## Support

- Help: our public Slack and our tracker are linked from
  [web-engineer.co.uk/contact](https://web-engineer.co.uk/contact).
- Bugs and feature requests:
  [GitHub issues](https://github.com/web-engineer/LoupixDeck.Plugin.Govee/issues).
- Security issues: please report them privately; see [SECURITY.md](SECURITY.md).

## Licence

MIT. Copyright (c) 2026 Web-Engine Limited, trading as web-engineer.
