# FruitLib toolbar slots

`FruitLib.FruitToolbar` adds real slots to the God toolbar, beside the ones the
game ships. An added slot is a genuine native slot — it gets the next number key,
the native highlight, its own icon and label, the switch sound, and it is
selectable like any other.

## Usage

Register from your mod's `OnInitializeMelon`, before any scene loads:

```csharp
using FruitLib;

public override void OnInitializeMelon()
{
    FruitToolbar.Register(new FruitToolbarItem
    {
        Id           = "BombsAway:Frag", // stable identifier — see below
        Name         = "Frag",           // label shown when the slot is selected
        Icon         = mySprite,         // or FruitToolbar.MakeSolidIcon(Color.cyan)
        OnSelected   = idx => { /* slot became active   */ },
        OnDeselected = idx => { /* slot became inactive */ },
    });
}
```

`idx` in the callbacks is the slot index.

## Where your slot lands

Custom slots go after everything the game claimed. **That is not the same as the
number of tools.** The cursor occupies a toolbar slot of its own without taking a
number key, so on the current build the game claims five slots — cursor plus four
tools — and the first custom slot is index 5, reached with number key **6**.

FruitLib works this out at runtime rather than assuming, by counting what the
game populates plus the cursor, so it survives the developer adding a tool. The
number it settled on, and why, is logged at startup:

```
[FruitToolbar] native base 5 (m_itemsToPopulate=4, cursor=yes, CURSOR_SLOT_INDEX=0)
[FruitToolbar] audit: capacity=6 occupied=[0,1,2,3,4] nativeBase=5
```

If a future build outwits that, the audit line warns and names the number to use,
and `FruitToolbar.NativeBaseOverride` forces it. Set it before the toolbar
initialises — from your `OnInitializeMelon`.

### Ids and ordering

`Id` should be a stable string like `"BombsAway:Frag"` — **not** anything derived
from position. Slot assignments are keyed off it, so they survive load-order
changes, mod updates, and a mod being temporarily removed. If you omit it,
FruitLib derives `"{YourAssemblyName}:{Name}"`; registering the same id twice logs
a warning and ignores the second.

By default slots are assigned in registration order, i.e. mod load order.
`FruitToolbar.PreferredOrder` (a `List<string>` of ids) overrides that: listed ids
come first in the order given, everything else follows. It is an *ordering*, not
absolute indices, so the slot range always stays contiguous.
`FruitToolbar.GetSlot(id)` returns an item's current index.

## Icons

Any `Sprite` works. Two are provided.

**From a PNG in your mod**, shipped the same way meshes are — add it to your
csproj as an `EmbeddedResource` and name it:

```csharp
Icon = FruitToolbar.LoadIcon(Assembly.GetExecutingAssembly(), "Icons/AK.png");
```

Matching is by suffix, so `"AK.png"` finds `MyMod.Icons.AK.png` without you having
to know how the compiler mangled the folder into the resource name. Give more of
the path if two files share a name. There is also a `LoadIcon(byte[])` overload if
you already have the bytes.

**Or generated**: `FruitToolbar.MakeSolidIcon(Color)` draws a coloured disc at
runtime, for prototyping or for mods that ship no art.

**If you build a sprite yourself, mark it to survive.** A texture and a sprite
created at runtime belong to no scene and no asset bundle, so the first scene load
that runs `UnloadUnusedAssets` is free to collect them — and a mod registers its
icon at startup, long before the first scene. What reaches the toolbar afterwards
is a destroyed object, which compares equal to null and silently reads as "no icon
supplied":

```csharp
tex.hideFlags    = HideFlags.HideAndDontSave;
sprite.hideFlags = HideFlags.HideAndDontSave;
```

`MakeSolidIcon` does this for you. A slot whose icon is missing anyway gets a grey
placeholder disc rather than going undrawn, because the game's icon drawer throws
rather than drawing an empty slot.

## Changing the label or icon later

```csharp
item.SetDisplay("AS50", FruitToolbar.MakeSolidIcon(Color.red));
```

Updates both the strip entry and the held-item display, immediately. Useful for a
single slot that cycles between several things — GunsGunsGuns uses one slot for
three weapons this way.

## Callback contract

- `OnSelected` fires on the frame FruitLib notices the selection changed to your
  slot; `OnDeselected` when it moves away.
- Both are invoked inside a try/catch; an exception in your callback is logged,
  not fatal.
- **Selection fires before anything exists in the world to attach to.** If you
  need `SelectedItemPivot`, don't act directly in `OnSelected` — set a flag and
  retry each frame from `OnUpdate` until it appears. `AkWeapon` in
  `2_GunsGunsGunsRefractored/Core/AkWeapon.cs` is a worked example of a fully
  custom slot: hitscan firing on held LMB, its own sound and crosshair, and no
  native weapon involved at all.

## What the slot does and doesn't do

- **No native behaviour.** A custom slot has no inventory-item prefab, so
  selecting it spawns nothing — no model, no shooting. The slot is yours to fill
  entirely via the callbacks.
- **No switch animation.** The game's items dissolve in and out through a
  `SwitchEffectRequest`, which is applied to an item's 3D model. A custom slot has
  no model, so there is nothing to dissolve. The switch *sound* does play.
- **The name is real data**, not an overlay: it is set on a descriptor FruitLib
  owns, so the game displays it exactly as it does a native item's.
- **Nothing native is cloned or mutated.** The icon and descriptor are freshly
  created assets, so a custom icon cannot corrupt a real tool's.

## Limits

- **Selection is read from the number keys**, so changing slots by scroll wheel or
  by clicking a slot is not seen.
- **Nine slots**, because keys `1`–`9` are what is polled. The current build ships
  eight slot views, of which the game uses five; beyond that FruitLib clones a
  view and re-centres the row (`AutoCentreSlotRow` / `SlotRowOffset` to adjust).
- **All custom slots share one item payload** in the toolbar's slot container.
  Callbacks are dispatched by slot index, so multiple slots work and stay
  distinguishable, but they are not independent at that level.

## If a game update breaks it

FruitLib logs what it found at startup: the base it derived, the occupancy map the
game populated, and a dump of every slot view with its key label and whether its
icon drawer is wired. Start there — it usually names the problem directly.

Two things learned the hard way, worth knowing before changing this code:

- **A slot view has to be awake before you draw into it.** `ToolbarItemSlotView`
  builds its icon drawer in `OnStart`, and Unity runs `Start` just before the next
  `Update`, not on activation. Drawing into a slot the same frame you activate it
  reaches a view whose drawer does not exist yet. FruitLib waits a frame.
- **Do not put custom entries in `m_itemsToPopulate`.** The game resolves each
  entry through `TryResolve`, which dereferences the entry's prefab — and a custom
  entry has none. The exception comes back out through the populator and aborts
  the game's own population part-way.
