# FruitLib

Shared library for FRUKT mods under MelonLoader: a settings menu built from the
game's own controls, HUD, performance monitor, inventory items and shelves, sound
effects, shared ballistics, scene-reset hooks, mod maps in the main menu, and custom
content through Unity AssetBundles.

**Third-party mod authors start here → [`docs/README.md`](docs/README.md)**, and copy [`Templates/`](Templates/README.md) for a new mod.

- [Config & menu](docs/config-and-menu.md)
- [HUD](docs/hud.md)
- [Performance monitor](docs/perfmon.md)
- [Inventory items and shelves](docs/inventory.md)
- [Sound](docs/sound.md)
- [Ballistics](docs/ballistics.md)
- [AssetBundles](docs/BUNDLES.md): models, shaders, animation, decals
- [Maps](docs/maps.md): a map of your own in the main menu's map list
- [World menu](docs/world-menu.md): the terminal's scene resets, and buttons of your own there
- [World events & bodies](docs/events.md): an event bus tagged with nearby bodies, and every humanoid's vitals, behaviour and limbs
- [Sight](docs/sight.md): what a body can see, in words and in pictures from its eyes
- [Pathing](docs/nav.md): a runtime navmesh, paths that keep off walls, and bodies that walk them
- [Items and hands](docs/items.md): things bodies pick up, carry, throw, use and hand over; the game's right-click menu for mod props
- [Utilities](docs/utilities.md): logging, paths, scene lookups, force fields, update checks
- [Meshes](docs/meshes.md) (older JSON loader), file schema in [`MESH_FORMAT.md`](docs/MESH_FORMAT.md)

## Source layout

| Folder | What lives there |
|---|---|
| `Core/` | `FruitLibMod` (the MelonMod entry point, per-frame tick fan-out) and `FruitVersion` |
| `Menu/` | `FruitMenu` (config attributes, ini, fallback IMGUI panel), the native pause-menu pages it builds, game-menu tracking, and `FruitWorldMenu` |
| `Hud/` | `FruitHud` panels (native TMP, IMGUI fallback) and the `FruitPerfMon` overlay |
| `Inventory/` | `FruitInventory` public API (items and shelves), the native bridge and Harmony patches, GC pinning, icons |
| `Ballistics/` | Specs, the public `FruitBallistics` facade, surface materials, blast physics, and projectile / explosion / wound / ejecta internals |
| `Maps/` | `FruitMaps`: mod maps in the main menu, built on a host map's scene |
| `Content/` | AssetBundles (+ their icall layer), decals, sound effects, the older JSON mesh loader |
| `Utilities/` | Logging, paths, scene lookups, stripped-API icalls, diagnostics and crash trace, force fields, update check |
| `Debug/` | Probes and in-game tests. Their hooks install only if *Debug hooks + crash trace* was on at launch; output only while their `*Probe` setting is on |
| `Templates/` | Starting files for a new mod (not compiled into FruitLib): see [`Templates/README.md`](Templates/README.md) |
