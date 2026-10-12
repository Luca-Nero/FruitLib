# Maps

Added in **5.11.0**. `FruitMaps` puts a map of your own in the main menu's map list, next to
Yard and Flatland.

## How it works

The game has two maps, an enum (`MapID`), one `MapInfo` card each and one button each in the
menu's map list. Choosing one runs `SandboxState.Enter(map)`, which asks the `MapCatalogue` for
the scene name and loads it under the game's loading curtain.

A registered map gets an id of its own (100 and up), a card, and a button cloned from the game's.
When it is chosen the catalogue answers with its **host** map's scene, so the game loads that
scene with everything that makes it work: the player, the toolbar, the terminal, the Zenject
services. FruitLib then hides the host's own scenery and hands your mod the scene to build in.

Building a scene of our own from scratch would mean rebuilding the game's installers and UI
wiring by hand, and every game update could break it. Riding on a host keeps all of that the
game's.

### Hosts

| Host | What it is | Hidden for you | Hidden with `HideHostGround` |
|---|---|---|---|
| `Flatland` | A 1000 × 1000 m terrain that is one big hill (0 at the edges, ≈ 226 m at the top), a house on its ~30 m flat top, player start on the house | the house (`Level/DOM_bezshahtamaya`) | the terrain and the cube under it |

Anything bigger than the hilltop wants `HideHostGround` and a ground of its own (FruitCity brings a
1.6 km field). `Ground` is measured before the terrain is hidden, so it is still the hilltop's height.

**Put every collider on layer 8, "Environment".** That is what all of the game's maps use, and
what a bob's puppeteer looks for to stand on: on Default a bob falls over and lies there with no
pose. Debris that should lie on the ground without getting in the way can use layer 3, "Casing"
(the game's spent shells).

## Registering a map

```csharp
FruitMaps.Register(new FruitMapDef
{
    Id      = "MyMod.Harbour",        // unique, not shown
    Word    = "HARBOUR",              // the menu line
    Size    = "200 × 150 M",          // the preview's size line
    Blurb   = "Cranes, containers and a lot of water.",
    Preview = myTexture,              // optional Texture2D; no picture without one
    Host    = FruitHostMap.Flatland,
    Build   = ctx =>
    {
        var world = bundle.Spawn("Harbour", ctx.Root.position, ctx.Root.rotation);
        world.transform.SetParent(ctx.Root, true);
        ctx.MovePlayerStart(ctx.Ground + new Vector3(0, 3.5f, -10));
    },
    Teardown = () => { /* forget your own bookkeeping */ },
});
```

Call it from `OnInitializeMelon`; the menu reads the list when it is drawn. Registering the same
`Id` again replaces the definition.

### `Build(FruitMapContext ctx)`

Runs once the host scene has loaded: after its objects' `Awake`, before their `Start`, which is
when the player is placed and the first human spawns. If it throws, FruitLib puts the host back as
it was and logs the exception, so the player still gets a playable map.

| Member | |
|---|---|
| `Root` | An empty object at `Ground` in the host scene. **Parent everything to it.** While `Build` runs, Unity may still call the menu scene active, and anything instantiated loose would be unloaded with it |
| `Ground` | The host's ground straight under the player's start |
| `PlayerStart`, `HumanSpawn` | Where the player starts (eye height, ~3.5 m up) and where the game's first human appears |
| `MovePlayerStart(eye)`, `MoveHumanSpawn(ground)` | Move them, e.g. onto markers in your prefab |
| `HideHostObject(path)` | Hide more of the host by path (`"Level/Sun"`) |
| `Adopt(go)` | Move a loose root object into the host scene |
| `Scene`, `Map` | The host scene, and your definition |

### Leaving

When the player leaves (the scene unloads) your `Teardown` runs. Everything under `Root` goes
with the scene, so there's nothing to destroy, only your own lists and caches to clear.

`FruitMaps.Current` is your definition while your map is being played, and null otherwise.
`FruitMaps.Loaded` fires after any mod map's `Build` succeeds.

The terminal's RESET MAP only resets the game's own objects. Listen to
`FruitWorldMenu.MapReset` (see [World menu](world-menu.md)) and rebuild whatever of yours should
come back.

## Notes

- **Mouse only** in the map list for now: the cloned button is not added to the menu's
  keyboard/gamepad navigation.
- **Lighting is the host's**: Flatland's sun, fog and sky. A map can add its own lights and
  volumes under `Root`.
- Name colliders and materials with surface keywords (`Brick_`, `Concrete_`, `Glass_`, `Wood_`,
  `Steel_`, `Drywall_`) and [ballistics](ballistics.md) penetration and ricochet work on your map
  with no further code.
