# FruitLib

Shared library for FRUKT mods running under MelonLoader. It provides the things
every mod ends up needing and nobody wants to write twice: a settings menu built
from the game's own controls, a HUD, a performance overlay, custom inventory items,
the game's sound effects, shared ballistics on the game's wound model, and custom
models, shaders and decals through Unity AssetBundles.

FruitLib is itself a MelonMod. Your mod references it at build time and expects
it in the `Mods` folder at runtime.

**Current version: 4.0.0**.

## Guides

| Guide | What it covers |
|---|---|
| [Config & menu](config-and-menu.md) | Config classes, the in-game settings tab, ini files, input gating |
| [HUD](hud.md) | On-screen readouts, stacked and positioned for you |
| [Performance monitor](perfmon.md) | Live counters and timing sections |
| [Inventory](inventory.md) | Custom items in the inventory window, by category |
| [Toolbar](toolbar.md) | Superseded by the inventory; migration notes |
| [Sound](sound.md) | The game's own sound effects, and its interface sounds |
| [Ballistics](ballistics.md) | Projectiles and explosions through the game's wound model, multiplayer-ready (3.1.0) |
| [AssetBundles](BUNDLES.md) | Custom models, shaders, animation and decals built in the Unity editor (3.2.0) |
| [Utilities](utilities.md) | `FruitPaths`, `FruitScene`, `FruitForces`, `FruitUpdateCheck` |
| [Meshes](meshes.md) | The older `*_mesh.json` loader (schema in [`MESH_FORMAT.md`](MESH_FORMAT.md)). Prefer bundles for new work |

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

Call something a user's older FruitLib doesn't have and they get a
`MissingMethodException` at startup accurate, and useless to them.
`FruitVersion.Require` turns that into a log line naming your mod, the version it
needs and the version present:

```csharp
public override void OnInitializeMelon()
{
    if (!FruitVersion.Require("MyMod", 1, 2)) return;
    Init();
}

[MethodImpl(MethodImplOptions.NoInlining)]
private void Init()
{
    FruitMenu.Register("MyMod", ConfigLoader.IniPath, typeof(Config));
    _hud = FruitHud.Register("MyMod", BuildHud);
}
```

**The split into two methods is load-bearing, not style.** A method's member
references are resolved when the JIT compiles it, so a `Require` call sitting in
the same method as the API it guards never runs the whole method fails to
compile first, and you get the raw exception anyway. `[MethodImpl(NoInlining)]`
stops the JIT folding `Init` back in and recreating the problem.

Also available: `FruitVersion.Current`, `.Major`, `.Minor`, `.Patch`, and
`AtLeast(major, minor, patch)` if you'd rather degrade a feature than bail out.

### The gap this doesn't close

`Require` is itself a FruitLib API, so it can't protect against a FruitLib older
than 1.2.0, on 1.1.0 the guard call is the thing that throws. It covers 1.2.0
and up.

To be robust against *any* installed version, ask MelonLoader instead and touch
no FruitLib type at all:

```csharp
var fl = MelonBase.FindMelon("FruitLib", "Luca_Nero");
if (fl == null) { LoggerInstance.Error("FruitLib is not installed."); return; }
// fl.Info.Version is the version string, e.g. "1.2.0"
```

Worth the extra few lines only if you expect users on much older installs.

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
            FruitMenu.Register("MyMod", ConfigLoader.IniPath, typeof(Config));
            ConfigLoader.Load();                    // your own ini reader, see the config guide
            _hud = FruitHud.Register("MyMod", BuildHud);
            FruitPerfMon.RegisterCounter("MyMod Things", () => _things.Count);
        }

        public override void OnUpdate()
        {
            // Skip input while the menu is open, or keys pressed in it fire game actions
            if (FruitMenu.IsInputSuppressed) return;
            if (Input.GetKeyDown(Config.DoThingKey)) DoThing();
        }

        private static void BuildHud(HudPanel p) =>
            p.Line($"[{Config.DoThingKey}] Do Thing");
    }
}
```

Shown without a version guard for brevity. Wrap the body as described in
[Requiring a version](#requiring-a-version) before shipping to anyone whose
FruitLib you don't control.

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
| F11 | Performance overlay (**R** resets peaks while it's up) | on |
| F6 / F7 / F10 | Ballistics / menu / bundle diagnostics | only when their `*Probe` setting is on |

## Conventions worth matching

- **Gate on `FruitMenu.IsInputSuppressed`, not `IsOpen`**. The former also covers
  the frame the menu closes on.
- **Don't implement `OnGUI`**. Use [`FruitHud`](hud.md) so your readout stacks
  with everyone else's instead of fighting for a corner.
- **Keep per-frame callbacks cheap**. HUD builders and PerfMon counter getters
  both run every frame.
