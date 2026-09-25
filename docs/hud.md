# HUD

`FruitHud` draws every mod's on-screen readout as one stack in a corner the user
picks. You supply lines; FruitLib owns the styling, the box, the position, the
width measurement and the menu gate.

**Do not implement `OnGUI` for a HUD.** That's how mods used to end up hardcoding
different corners and overlapping each other.

## Registering

```csharp
private static HudHandle _hud;

public override void OnInitializeMelon()
{
    _hud = FruitHud.Register("MyMod", BuildHud, order: 10);
}

private static void BuildHud(HudPanel p)
{
    p.Line($"[{Config.DoThingKey}] Do Thing");
    if (_locked) p.Line("LOCK: LOCKED", HudPanel.Bad);
}
```

`Register(name, build, order, corner, minWidth, fontSize)`, only the first two
are required.

| Parameter | Default | Notes |
|---|---|---|
| `order` | `0` | Sort key. Panels stack by `(order, name)` |
| `corner` | `null` | `null` follows the user's setting. Only pass one if your panel is genuinely pinned |
| `minWidth` | `0` | Floor for panel width, in pixels |
| `fontSize` | `0` | Override the user's font size. Use sparingly |

Stacking is deliberately **not** registration order, so it doesn't shift with
MelonLoader's load order. Pick an `order` in the tens to leave room between mods.

Passing an explicit `corner` opts out of the user's layout choice, so reserve it for
overlays that would be disruptive if they moved, like FruitPerfMon pinning itself
top-right so a debug readout never shoves gameplay HUDs around when toggled.

## Building content

The callback runs **once per repaint**, so keep it cheap and avoid allocating
beyond the strings themselves. The `HudPanel` instance is reused between frames.

| Method | Result |
|---|---|
| `p.Line(text)` | A line in the default colour |
| `p.Line(text, color)` | A tinted line |
| `p.Header(text)` | Bold, slightly dimmed |
| `p.Header(text, color)` | Bold, tinted |
| `p.Separator()` | Horizontal rule across the panel |
| `p.Blank()` | Empty line |

Preset colours: `HudPanel.Normal`, `Dim`, `Good`, `Warn`, `Bad`. Any `Color`
works; the presets just keep mods looking like one system.

Panel width is measured from your longest line and only re-measured when the text
actually changes, so varying content is cheap but text that changes every frame
(a live coordinate, an unrounded float) re-measures every frame. Round to the
precision you actually display.

## Hiding

Two mechanisms, for two situations.

**Add no lines**, transient. Nothing to report right now; the panel vanishes for
that frame and the stack closes up.

```csharp
private static void BuildHud(HudPanel p)
{
    if (_things.Count == 0) return;   // panel disappears until there's something to say
    p.Line($"Things: {_things.Count}");
}
```

**`handle.Visible`** durable. While `false` the callback isn't invoked at all,
so a hidden panel costs nothing.

```csharp
_hud.Visible = false;
```

By name, if you didn't keep the handle:

```csharp
FruitHud.SetVisible("MyMod", false);
bool shown = FruitHud.IsVisible("MyMod");
HudHandle h = FruitHud.Find("MyMod");
```

### Hiding everything

```csharp
FruitHud.Visible = false;   // whole overlay, cutscene, photo mode, your own hide-UI key
```

The getter also accounts for the user's master switch, so `Visible = true` will
**not** force the HUD on if they turned it off in the menu. That's deliberate: a
mod shouldn't override a user setting.

## Lifecycle

`Register` with a name that's already registered replaces the existing panel
rather than stacking a duplicate. The previous handle is detached: setting
`Visible` on it does nothing. `Unregister(name)` or `handle.Unregister()` removes
a panel entirely.

If your build callback throws, FruitLib logs a warning once and permanently
disables **that panel only**, rather than letting one bad HUD take down the
overlay or spam the log every frame. A disabled panel stays disabled until the
game restarts, so check the log if your HUD silently vanishes.

## What the user controls

FruitLib's own settings page (pause → **MODS** → **FruitLib**, and
`UserData/FruitLibConfig.ini`) exposes corner, margins, gap
between panels, font size, background alpha, a master enable, and a toggle key
(default **F8**) that hides all mod HUDs for the session. Your panel inherits all of it.
