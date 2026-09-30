# Config & menu

`FruitMenu` gives your mod a page in the game's own pause menu. You point it at a
class of static fields; it renders and edits them using the game's own controls.

## Where your settings appear

Pause the game and the rail reads **CONTINUE / SETTINGS / MODS / QUIT**. MODS is
FruitLib's, built from a copy of the game's SETTINGS screen, and it rides the same
wipe transition as everything else in that menu.

```
pause /                          MODS            every registered mod
pause / mods /                   MYMOD           that mod's categories
pause / mods / mymod /           TUNING          that category's settings
```

The middle page is skipped when a mod has only one category, the same way the
game goes straight to the resolution table rather than listing one entry. ESC
steps back one page at a time.

## Registering

```csharp
FruitMenu.Register("MyMod", ConfigLoader.IniPath, typeof(Config), ConfigLoader.Write);
```

- `displayName` → the line in the MODS list, and the page heading.
- `iniFilePath` → your ini.
- `configType` → a class whose `public static` fields are your settings.
- `save` → your own ini writer, called when the user edits a value (5.2). Without
  it (the three-argument overload) FruitMenu writes `iniFilePath` itself, as a bare
  `key = value` list.

## The config class

```csharp
internal static class Config
{
    [MenuCategory("Controls"), MenuLabel("Fire key")]
    public static KeyCode DoThingKey = KeyCode.G;

    [MenuCategory("Controls"), MenuLabel("Enabled")]
    public static bool Enabled = true;

    [MenuCategory("Tuning"), MenuLabel("Muzzle velocity"), MenuRange(100, 1200)]
    public static float Velocity = 600f;

    [MenuCategory("Tuning"), MenuLabel("Magazine size"), MenuRange(1, 60)]
    public static int MaxCount = 30;
}
```

Fields must be `public static`. A field with no `[MenuCategory]` is ini-only and
never drawn.

### Supported types

| Type | Native control | Notes |
|---|---|---|
| `bool` | Toggle | The game's own ON/OFF switch |
| `float` | Slider | Wants a `[MenuRange]`; see below |
| `int` | Slider | Same, rounded to whole numbers |
| `KeyCode` | Keybind table | Click the row, press a key |
| `string` | — | Panel only, read-only |
| momentary `bool` | — | Panel only; it is an action, not a setting |

Anything else is ignored entirely. If you need an enum, store it as an `int` and
convert at the use site.

## `[MenuLabel]` — what the setting is called

```csharp
[MenuCategory("Tuning"), MenuLabel("Muzzle velocity")]
public static float AK_MuzzleVelocityMs = 715f;
```

Without it the field name is shown, which is rarely what you would say out loud.

**The field name still keys the ini file**, so adding, changing or removing a
label never touches anyone's saved config.

Keep labels short. Rows put the label on the left and the control on the right at
a fixed split, and a long one is clipped or scrolled rather than pushing the
control aside.

## `[MenuRange]` — what a slider can reach

```csharp
[MenuCategory("Tuning"), MenuRange(0, 1)]
public static float Opacity = 0.55f;
```

A slider has to have ends. Declare them and the slider is exactly right.

**Without one FruitLib guesses** from the field's *default* value — not its
current value, which would make the range move as you drag. The guess is
deliberately generous (`0` to four times the default), because a slider that
cannot reach a value is worse than one that is coarse. It is a fallback, not a
feature: declare the range on anything you care about.

## What is not on the native page

Key bindings are, in a table at the bottom of the page. Free text and momentary
buttons are not — the game has no control for either.

Every page carries an **ALL SETTINGS** line that opens FruitLib's own panel,
which draws *everything*: strings, action buttons, per-field step buttons and
click-to-type numeric entry. Nothing is unreachable by being left off the native
page.

That panel is also the fallback if the native menu cannot be built at all. If a
game update moves the pause menu out from under FruitLib, the MODS button opens
the panel directly and your settings keep working.

## Action buttons

A `bool` marked momentary renders as a one-shot button that sets the field
`true`. Your code does the work and sets it back to `false`. These are never
written to the ini, and they live in the panel rather than on the native page —
a toggle would misrepresent an action as state that sticks.

```csharp
[MenuCategory("Debug")]
[MenuButton(ButtonKind.Momentary)]
public static bool DumpShaders;
```

## Ini files: your mod reads and writes them

This is the part that catches people out.

**`FruitMenu.Register` never reads your ini.** Loading at startup, and writing the
commented default file on first run, is your mod's job. When the user edits a value
in-game, FruitMenu calls the `save` you registered, so the file has one writer.

The practical shape, which every shipped mod uses, is a `ConfigLoader` class with
`IniPath`, `Load()` and `Write()`, where `Load()` parses `key = value` lines by
reflecting over the same config type and `Write()` emits the file with your
explanatory comments. Pass `Write` as `save`. Copy
[`Templates/ConfigLoader.cs`](../Templates/ConfigLoader.cs).

Build `IniPath` with [`FruitPaths.Config`](utilities.md#fruitpaths), which puts the
file in `UserData` and migrates one an older build left next to the DLL:

```csharp
public static string IniPath => FruitPaths.Config("MyModConfig.ini", typeof(ConfigLoader).Assembly);
```

Keep floats culture-invariant in both directions (`CultureInfo.InvariantCulture`)
or a comma-decimal locale will corrupt the file.

Slider edits are written once the slider settles, not on every frame of a drag,
so `OnConfigChanged` fires once per adjustment rather than a hundred times. A
key binding is written the moment it is taken — it is one deliberate act, not a
value being dragged towards something.

### Reset to Defaults, and why order matters

The panel's footer button restores the values each field held when FruitLib
first saw it - by default **at the moment you called `Register`**, not the values
written in your source. Register runs after your ini has loaded, so that is the
player's own values, which is rarely what anyone wants.

**`FruitMenu.CaptureDefaults` (since 5.5.0)** fixes that. Call it before you load
your ini and "Reset to Defaults" means your code defaults, whatever order you load
and register in:

```csharp
FruitMenu.CaptureDefaults(typeof(Config));   // snapshot the code defaults...
ConfigLoader.Load();                         // ...then apply the user's ini
FruitMenu.Register("MyMod", ConfigLoader.IniPath, typeof(Config), ConfigLoader.Write);
```

It records every field `Register` would turn into a setting, once per type (repeat
calls are ignored). A mod that never calls it behaves as before: register before
loading, or Reset returns to the ini values from startup. New mods should call it
(`Templates/Core.cs` does).

## Reacting to changes

```csharp
FruitMenu.OnConfigChanged += () => RebuildThings();
```

Fires on every edit, of any registered mod's config, so treat it as "something
changed, re-derive" rather than a fine-grained signal. Cheap handlers only.

## Input gating

While the menu is open, the game is still running and still reading input. Gate
your own key handling:

```csharp
if (!FruitMenu.IsInputSuppressed)
{
    if (Input.GetKeyDown(Config.DoThingKey)) DoThing();
}
```

| Member | Meaning |
|---|---|
| `FruitMenu.IsOpen` | The menu is showing |
| `FruitMenu.JustClosed` | True for exactly one frame after it closes |
| `FruitMenu.GameMenuOpen` | One of the game's own menus is open: the terminal (items / world), a context menu (5.5.0) |
| `FruitMenu.IsInputSuppressed` | Any of the above, or a game menu closed this frame or the last — so **use this one**. Since 5.5.0 it covers the game's menus too, so clicking in the terminal no longer reaches a mod's held item |
| `FruitMenu.IsGamePaused` | The game's pause menu is up, ours or not |
| `FruitMenu.BlocksGameplayInput` | Paused, or in or just out of the mod menu |

`JustClosed` exists because the keypress that dismisses the menu would otherwise
fire a game action on the same frame. Checking only `IsOpen` reintroduces that
bug.

Simulation that should keep running while the menu is up — physics, lifetimes,
cleanup — should stay *outside* the gate. Only player input belongs inside it.

A binding being taken consumes the key press before anything else sees it, ESC
included. So a user can bind ESC if they want to, and your gate does not have to
special-case the rebinding state.
