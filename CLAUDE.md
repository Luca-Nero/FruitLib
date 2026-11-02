# FruitLib

Shared library for FRUKT mods under MelonLoader, and itself a MelonMod. Other mods reference it by name, so its public surface is what they build against. `README.md` and `docs/README.md` are the map; each guide in `docs/` covers one subsystem.

## Where things are
| Path | What |
|---|---|
| `Core/` | `FruitLibMod` (the entry point and tick fan-out), `FruitLibConfig` (settings and defaults), `FruitVersion` |
| `Ballistics/` | Projectiles, explosions, wounds, surfaces |
| `World/` | Bodies, body control, poses, nav, sight, events, items, props, places |
| `Inventory/` | Items and shelves in the game's inventory window |
| `Menu/` | Settings pages in the pause menu, the terminal's World page |
| `Hud/` | HUD panels, the performance overlay, text input |
| `Maps/`, `Content/`, `Utilities/` | Mod maps; bundles, decals, sound; logging, paths and the rest |
| `Debug/` | Probes. `FruitBundleTests`, `FruitBundleProbe` and `FruitMenuProbe` compile only with `-p:DevTools=true` |
| `Tests/` | Harnesses that link production files against a small world; `Tests/README.md` lists them and the live checks |
| `Templates/` | Starting files for a new mod, not compiled into FruitLib |

## Build & test
The real build needs the game's reference DLLs in `ModRefs`: `dotnet build FruitLib.csproj -c Release -p:SkipDeploy=true`. Without them, run the harnesses in `Tests/README.md`.

## Rules worth knowing
- MelonLoader applies every `[HarmonyPatch]` at load. Never call `PatchAll`, and a patch must not let an exception escape into native code.
- Never hook a method with a `Nullable<T>` argument passed in a register (see `World/FruitPose.Native.cs`).
- Don't change a public signature casually: a mod built against the old one fails at runtime with `MissingMethodException` or `TypeLoadException`.
