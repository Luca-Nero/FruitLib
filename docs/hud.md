# HUD

`FruitHud` draws every mod's on-screen readout as one stack in a corner the user
picks. You supply lines; FruitLib owns the styling, the box, the position, the
width measurement and the menu gate.

**Do not implement `OnGUI` for a HUD.** That's how mods used to end up hardcoding
different corners and overlapping each other.

## How it's drawn

Since 5.0 panels are drawn with the game's own UI: TextMeshPro text in the same font,
material and letter spacing as the game's HUD (borrowed from the kill counter, or the
toolbar hint), on a canvas that scales with the screen as if it were 1080p. Before a
level has loaded there's no HUD text to borrow from, so panels use a stand-in font,
or IMGUI if there's no text at all, and switch over once the real one shows up.
If the native build fails, or the player turns off *Native HUD*, the same panels are
drawn with IMGUI. Your code is the same either way.

Two things follow from drawing with the game's font:

- **Stick to characters the font has.** A symbol the game's font lacks (e.g. `⬤`, box
  drawing, emoji) shows as a placeholder square. Plain ASCII is always safe.
- **Text is literal.** Rich-text tags are off, so a `<` in your text shows as `<`.


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
between panels, font size, background alpha, native or IMGUI drawing, a master enable, and a toggle key
(default **F8**) that hides all mod HUDs for the session. Your panel inherits all of it.

## Text input

`FruitTextInput` (since 5.16.0) is a one-line text box at the bottom of the screen, for
when the player has to type something: a line to an NPC, a command, a name.

```csharp
if (Input.GetKeyDown(KeyCode.U) && !FruitMenu.IsInputSuppressed)
    FruitTextInput.Open("MyMod", "Say:", (text, shift) => Say(text, loud: shift),
                        onCancel: null, maxLength: 160);
```

- **Enter** submits the trimmed line (an empty line submits nothing). The callback also
  gets whether Shift was held. **Esc** closes the box and calls `onCancel`.
- Left/Right/Home/End move the caret, Backspace and Delete edit, Ctrl+Backspace deletes a word.
- Characters come from IMGUI key events, so the keyboard layout and key repeat are the system's.
- While the box is open, the game's controls stand still. FruitLib takes the game's own input
  block (`ButtonsInputBlockService`, the same one the pause menu and the loading curtain take),
  so the camera, the held tool and hotkeys such as slow motion stop. `FruitMenu.IsInputSuppressed`
  is true while the box is open and for a frame after, so FruitLib-gated mod hotkeys stay quiet.
  Esc does not also open the pause menu.
- `Active`, `Owner`, `Text` and `JustClosed` tell you the state. `SetPrompt` changes the label
  while the box is open. `Close()` closes it without calling either callback.
- `HoldsGame` is false if the game's input block service hasn't been seen yet. In that case the
  camera may move while the player types. FruitLib picks the service up from the game's own
  calls to it, which happen as soon as a level has loaded.

Only one mod can have the box at a time: `Open` returns false while another mod's box is open.
