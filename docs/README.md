# FruitLib

Shared library for FRUKT mods running under MelonLoader. It provides the things
every mod ends up needing and nobody wants to write twice: a settings menu built
from the game's own controls, a HUD, a performance overlay, custom inventory items,
the game's sound effects, shared ballistics on the game's wound model, and custom
models, shaders and decals through Unity AssetBundles.

FruitLib is itself a MelonMod. Your mod references it at build time and expects
it in the `Mods` folder at runtime.

**Current version: 5.10.2**. Starting a new mod? Copy the files in [`Templates/`](../Templates/README.md).

## Guides

| Guide | What it covers |
|---|---|
| [Config & menu](config-and-menu.md) | Config classes, the in-game settings page, ini files, input gating |
| [HUD](hud.md) | On-screen readouts, stacked and positioned for you |
| [Performance monitor](perfmon.md) | A per-mod debug overlay: counters, timers and live values |
| [Inventory](inventory.md) | Custom items in the inventory window, on the game's shelves or your own |
| [Sound](sound.md) | The game's own sound effects, and its interface sounds |
| [Ballistics](ballistics.md) | Projectiles and explosions through the game's wound model, multiplayer-ready: surface penetration and ricochet, physical blast overpressure and cover, fragments, shaped-charge jets |
| [AssetBundles](BUNDLES.md) | Custom models, shaders, animation and decals built in the Unity editor |
| [World menu](world-menu.md) | Know when the player used the terminal's World > SCENE resets, and add buttons of your own to that page |
| [Utilities](utilities.md) | `FruitLog`, `FruitPaths`, `FruitScene`, `FruitForces`, `FruitUpdateCheck` |
| [Meshes](meshes.md) | The older `*_mesh.json` loader (schema in [`MESH_FORMAT.md`](MESH_FORMAT.md)). Prefer bundles for new work |

Each guide notes the version that added a feature, so you can pick the minimum
FruitLib your mod needs.

## Setup

Reference the DLL without copying it, MelonLoader loads the real one from the
`Mods` folder, and a second copy next to your mod causes type identity problems:

```xml
<Reference Include="FruitLib">
  <HintPath>..\..\ModRefs\FruitLib.dll</HintPath>
  <Private>false</Private>
</Reference>
```

Tell your users FruitLib is a required dependency, and state the minimum version
you need.

## Requiring a version

Call something a user's older FruitLib doesn't have, or start without FruitLib at
all, and they get a `MissingMethodException` or `TypeLoadException` at startup:
accurate, and useless to them. Copy [`Templates/FruitGate.cs`](../Templates/FruitGate.cs)
into your mod (change only the namespace) and check before anything touches FruitLib:

```csharp
[assembly: MelonOptionalDependencies("FruitLib")]

private bool _active;

public override void OnInitializeMelon()
{
    _active = FruitGate.Check("MyMod", 5, 5, 0);   // the oldest FruitLib you build against
    if (_active) Init();
}

public override void OnLateInitializeMelon()
{
    if (_active) return;
    try { Unregister(FruitGate.FailureReason, silent: true); } catch { }
}

[MethodImpl(MethodImplOptions.NoInlining)]
private void Init()
{
    FruitMenu.Register("MyMod", ConfigLoader.IniPath, typeof(Config), ConfigLoader.Write);
    _hud = FruitHud.Register("MyMod", BuildHud);
}
```

If FruitLib is missing or too old, the log names your mod, the version it needs and
what it found, and your mod unregisters instead of crashing.

**The split into two methods is load-bearing, not style.** A method's member
references are resolved when the JIT compiles it, so a check sitting in the same
method as the API it guards never runs: the whole method fails to compile first, and
you get the raw exception anyway. `[MethodImpl(NoInlining)]` stops the JIT folding
`Init` back in and recreating the problem. The same goes for every other method that
touches FruitLib and runs before the gate has passed (an `OnUpdate` body, say): give
it its own `NoInlining` method, as `Templates/Core.cs` does. `FruitGate` itself reads
the version by reflection and references no FruitLib type, which is why it works
against any install.

Once the gate has passed, `FruitVersion.Current`, `.Major`, `.Minor`, `.Patch` and
`AtLeast(major, minor, patch)` are safe to use if you'd rather degrade a feature than
bail out. `FruitVersion.Require` still compiles but is `[Obsolete]`: it is itself a
FruitLib call, so it can't report a FruitLib that isn't there.

## A minimal mod

```csharp
using FruitLib;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(MyMod.Core), "MyMod", "1.0.0", "You")]
[assembly: MelonGame]

namespace MyMod
{
    public class Core : MelonMod
    {
        private static HudHandle _hud;

        public override void OnInitializeMelon()
        {
            FruitMenu.CaptureDefaults(typeof(Config));   // before the ini loads, for Reset to Defaults
            ConfigLoader.Load();                    // your own ini reader, see the config guide
            FruitMenu.Register("MyMod", ConfigLoader.IniPath, typeof(Config), ConfigLoader.Write);
            _hud = FruitHud.Register("MyMod", BuildHud);
            FruitPerfMon.For("MyMod").Counter("Things", () => _things.Count);
        }

        public override void OnUpdate()
        {
            // Skip input while paused or in a menu, or keys pressed there fire game actions
            if (FruitMenu.BlocksGameplayInput) return;
            if (Input.GetKeyDown(Config.DoThingKey)) DoThing();
        }

        private static void BuildHud(HudPanel p) =>
            p.Line($"[{Config.DoThingKey}] Do Thing");
    }
}
```

Shown without the version gate for brevity. Wrap it as described in
[Requiring a version](#requiring-a-version), or start from `Templates/Core.cs`,
which already is, before shipping to anyone whose FruitLib you don't control.

Registration order between mods doesn't matter, every `Register` call is just a
list append, so it's safe whether FruitLib's own `OnInitializeMelon` has run yet
or not. Don't call FruitLib from a static field initializer, though: that can run
before MelonLoader has finished setting up.

## What your settings look like

Your mod's settings are drawn with the game's own controls, on pages in the game's
own pause menu. A `bool` becomes the game's toggle, a number becomes its slider, a
`KeyCode` becomes a row in its keybind table.

```
CONTINUE
SETTINGS
MODS        ← yours, and everyone else's
QUIT
```

Declaring a range and a readable name is what makes the difference between a
slider that works and one that annoys:

```csharp
[MenuCategory("Tuning"), MenuLabel("Muzzle velocity"), MenuRange(100, 1200)]
public static float Velocity = 600f;
```

See [Config & menu](config-and-menu.md) for the whole surface, including what
falls back to FruitLib's own panel and why.

## Where files land

Ini files live in MelonLoader's `UserData` folder, not next to the DLLs: `Mods`
stays a folder of mods, and settings survive a reinstall. Build your path with
[`FruitPaths.Config`](utilities.md#fruitpaths), which also moves a file an older
build of your mod left beside its DLL. FruitLib's own settings are in
`UserData/FruitLibConfig.ini`, editable in-game on FruitLib's page under **MODS**.
Bundles for testing go in `UserData/FruitBundles`.

## Keys FruitLib binds

Avoid these in your own defaults (all rebindable by the player):

| Key | What | Default state |
|---|---|---|
| F8 | Hide / show all mod HUD panels | on |
| F11 | Performance overlay; Shift: next view, Ctrl: reset peaks | on |
| F6 / F7 / F10 | Ballistics / menu / bundle diagnostics (F10 also with Shift and Ctrl) | only when their `*Probe` setting is on |

## Conventions worth matching

- **Gate on `FruitMenu.BlocksGameplayInput`, not `IsOpen`**. It also covers the
  pause screen, the game's own menus (terminal, context menus), and the frame any
  of them closes on.
- **Don't call `PatchAll`**. MelonLoader already applies every `[HarmonyPatch]` in
  your assembly; calling it again installs each hook twice.
- **Routine log lines go through [`FruitLog.Info`](utilities.md#fruitlog)**, so they
  stay quiet unless the player turned on *Verbose log*.
- **Don't implement `OnGUI`**. Use [`FruitHud`](hud.md) so your readout stacks
  with everyone else's instead of fighting for a corner.
- **Keep per-frame callbacks cheap**. HUD builders and PerfMon counter getters
  both run every frame.
