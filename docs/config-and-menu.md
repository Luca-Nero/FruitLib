# Config & menu

`FruitMenu` gives your mod a tab in the in-game settings panel, reached by pausing
the game and clicking **Mod Settings**. You point it at a class of static fields;
it renders and edits them.

## Registering

```csharp
FruitMenu.Register("MyMod", ConfigLoader.IniPath, typeof(Config));
```

- `displayName` -> your tab label. Keep it short; tabs share the header width.
- `iniFilePath` -> where FruitMenu writes when the user edits a value.
- `configType` -> a class whose `public static` fields are your settings.

## The config class

```csharp
internal static class Config
{
    [FruitLib.MenuCategory("Controls")] public static KeyCode DoThingKey = KeyCode.G;
    [FruitLib.MenuCategory("Controls")] public static bool    Enabled    = true;

    [FruitLib.MenuCategory("Tuning")]   public static float   Force      = 12.5f;
    [FruitLib.MenuCategory("Tuning")]   public static int     MaxCount   = 20;

    [FruitLib.MenuCategory("Debug")]    public static bool    Dbg1;
}
```

Fields must be `public static`. `[MenuCategory]` groups them into sub-tabs;
anything without one lands in **General**. Category tabs appear in first-seen
order and wrap to two rows if they don't fit.

### Supported types

| Type | Control |
|---|---|
| `bool` | ON/OFF toggle |
| `int` | Six step buttons (±1×, ±10×, ±100× of a magnitude-appropriate base) plus a click-to-type box |
| `float` | Same, with finer steps |
| `KeyCode` | Click to rebind, then press a key |
| `string` | Read-only display |

Any other type is silently ignored, it won't appear and won't be written to the
ini. If you need an enum, store it as an `int` and convert at the use site.

### Action buttons

A `bool` marked momentary renders as a one-shot button that sets the field `true`.
Your code does the work and sets it back to `false`. These are never written to
the ini.

```csharp
[FruitLib.MenuCategory("Debug")]
[FruitLib.MenuButton(FruitLib.ButtonKind.Momentary)]
public static bool DumpShaders;
```

## Ini files: you own reading, FruitMenu owns writing

This is the part that catches people out.

**`FruitMenu.Register` never reads your ini.** It writes the file whenever the
user edits a value in-game, and that's all. Loading at startup, and writing the
commented default file on first run, is your mod's job.

The practical shape, which both shipped mods use, is a `ConfigLoader` class with
`IniPath`, `Load()` and `Write()`, where `Load()` parses `key = value` lines by
reflecting over the same config type. `Write()` re-emits the file with your
explanatory comments, which is what restores them after FruitMenu's bare
key/value rewrite. See `Mods/7_Singularity/ConfigLoader.cs` for a complete
example to copy.

Keep floats culture-invariant in both directions (`CultureInfo.InvariantCulture`)
or a comma-decimal locale will corrupt the file.

### Reset to Defaults, and why order matters

The footer button restores the values each field held **at the moment you called
`Register`** 
not the values written in your source.

```csharp
FruitMenu.Register("MyMod", path, typeof(Config));   // defaults captured here
ConfigLoader.Load();                                 // then user values applied
```

Register first, as above, and "Reset to Defaults" means your code defaults. Load
first and it means "whatever was in the user's ini at startup", which is rarely
what anyone wants. Both shipped mods currently load first; register-first is the
better order for new mods.

## Reacting to changes

```csharp
FruitMenu.OnConfigChanged += () => RebuildThings();
```

Fires on every edit, of any registered mod's config, including step-button
repeats, so treat it as "something changed, re-derive" rather than a fine-grained
signal. Cheap handlers only.

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
| `FruitMenu.IsInputSuppressed` | Either of the above so **use this one** |

`JustClosed` exists because the keypress that dismisses the menu would otherwise
fire a game action on the same frame. Checking only `IsOpen` reintroduces that
bug.

Simulation that should keep running while the menu is up, physics, lifetimes,
cleanup, should stay *outside* the gate. Only player input belongs inside it.
