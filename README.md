# FruitLib

Shared library for FRUKT mods under MelonLoader: a settings menu built from the
game's own controls, HUD, performance monitor, inventory items, sound effects,
shared ballistics, and custom content through Unity AssetBundles.

**Third-party mod authors start here → [`docs/README.md`](docs/README.md)**

- [Config & menu](docs/config-and-menu.md)
- [HUD](docs/hud.md)
- [Performance monitor](docs/perfmon.md)
- [Inventory items](docs/inventory.md) (replaces the old [toolbar slots](docs/toolbar.md))
- [Sound](docs/sound.md)
- [Ballistics](docs/ballistics.md)
- [Utilities](docs/utilities.md): paths, scene lookups, force fields, update checks
- [Meshes](docs/meshes.md) (older JSON loader), file schema in [`MESH_FORMAT.md`](docs/MESH_FORMAT.md)

## Source layout

| Folder | What lives there |
|---|---|
| `Core/` | `FruitLibMod` (the MelonMod entry point, per-frame tick fan-out) and `FruitVersion` |
| `Menu/` | `FruitMenu` (config attributes, ini, fallback IMGUI panel) and the native pause-menu pages it builds |
| `Hud/` | `FruitHud` panels and the `FruitPerfMon` overlay |
| `Inventory/` | `FruitInventory` public API, the native bridge and Harmony patches, GC pinning, icons |
| `Ballistics/` | Specs, the public `FruitBallistics` facade, and projectile / explosion / wound / ejecta internals |
| `Content/` | AssetBundles (+ their icall layer), decals, sound effects, the older JSON mesh loader |
| `Utilities/` | Paths, scene lookups, stripped-API icalls, crash trace, force fields, update check |
| `Debug/` | Probes and in-game tests, all off unless their `*Probe` setting is on |
| `Deprecated/` | Kept for old mods to compile against. Nothing new goes here |
