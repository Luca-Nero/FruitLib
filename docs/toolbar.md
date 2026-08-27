# FruitLib toolbar slots

`FruitLib.FruitToolbar` adds real slots to the game's God toolbar, beside the four vanilla ones
(Cursor / Cutter / Gun / Spawner). An added slot is a genuine native slot — it gets the next
number key, the native highlight, its own icon and label, and it is selectable like any other.

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
        Icon         = mySprite,         // optional; or FruitToolbar.MakeSolidIcon(Color.cyan)
        OnSelected   = idx => { /* slot became active   */ },
        OnDeselected = idx => { /* slot became inactive */ },
    });
}
```

Slots are handed out after the vanilla four, so the first registered item becomes slot index 4
(the 5th slot, number key `5`). `idx` in the callbacks is that slot index.

### Ids and ordering

`Id` should be a stable string like `"BombsAway:Frag"` — **not** anything derived from position.
Slot assignments are keyed off it, so they survive load-order changes, mod updates, and a mod
being temporarily removed. An index would silently point at the wrong item the moment anything
shifted. If you omit it, FruitLib derives `"{YourAssemblyName}:{Name}"`; registering the same id
twice logs a warning and ignores the second.

By default slots are assigned in registration order, i.e. mod load order.
`FruitToolbar.PreferredOrder` (a `List<string>` of ids) overrides that: listed ids come first in
the order given, everything else follows. It's an *ordering*, not absolute indices, so the slot
range always stays contiguous — a future assignment UI or config page only has to write that list.
`FruitToolbar.GetSlot(id)` returns an item's current index.

### Icons without art assets

`FruitToolbar.MakeSolidIcon(Color)` generates a coloured disc sprite at runtime — useful for
prototyping or for mods that don't ship textures. Any `Sprite` works.

Nothing else is required — FruitLib handles growing the toolbar, activating the slot view,
drawing the icon/label, and dispatching selection.

## Callback contract

- `OnSelected` fires on the frame FruitLib notices the selection changed to your slot.
- `OnDeselected` fires when selection moves away from your slot.
- Both are invoked inside a try/catch; an exception in your callback is logged, not fatal.
- **Selection fires before the slot's item exists in the world** (it spawns ~250ms later). If you
  need `SelectedItemPivot`, don't act directly in `OnSelected` — set a flag and retry each frame
  from `OnUpdate` until it appears. `AkWeapon` in `2_GunsGunsGunsRefractored/AkWeapon.cs` is a
  worked example of a fully custom slot: hitscan firing on held LMB, its own sound and crosshair,
  and no native weapon involved at all.

## What the slot does and doesn't do

- **No native behaviour.** The slot's registration entry is built from scratch and its `m_prefab`
  points at an inert no-op inventory item (`km`), so selecting it spawns nothing functional — no
  weapon model, no shooting, no sounds. The slot is yours to fill entirely via the callbacks.
- **The name is real data,** not an overlay: it's set as `m_objectName` on a descriptor FruitLib
  owns, so the game displays it exactly as it does a vanilla item's.
- **Nothing is cloned or mutated.** The icon, descriptor and category are all freshly created
  assets, so a custom `Icon` cannot corrupt the real gun's icon.

## Current limitations

- **Nine slots total.** Four vanilla plus five custom, because selection is read from number keys
  `1`–`9`. The scene only ships one spare slot view; beyond that FruitLib clones
  `ToolbarView.ptf` and re-centres the row (`AutoCentreSlotRow` / `SlotRowOffset` if you need to
  adjust it). Five custom slots are verified working.
- **You supply the model.** Because nothing native spawns, an added slot is empty-handed until
  your `OnSelected` parents something to `SelectedItemPivot`.
- **All injected slots share one item payload** in the toolbar's slot container (a single spare
  native `bji`). Callbacks are dispatched by **slot index**, so multiple slots work and stay
  distinguishable, but they are not independent at that level. (`bjh` is publicly constructible
  and would remove this; not wired up yet.)
- **Selection is detected from the number keys**, so changing slots by scroll wheel or by clicking
  a slot is not seen. See below.

## Version fragility

The toolbar internals are heavily obfuscated and the names shift between game builds. Two notes
for whoever maintains this next:

- Selection is deliberately **polled** off `jt<bji>.SlotIndex` (via `GodToolbarService.xat`), not
  hooked off the `fav`/`faw`/`fax` events. `jt<a>` is an interface, so `Item` and `SlotIndex` kept
  real names through obfuscation and are the most stable handles available.
- Those events are genuinely unreliable across versions: an older build had `faw` = selected,
  while on FRUKT 0.01 the observed behaviour is `fav` = item added, `fax` = selected, and `faw`
  never fires at all. Do not build on them.

If a game update breaks the toolbar, FruitLib logs one diagnostic line at startup naming the
resolved service, how many exist in the scene, and every candidate index value — start there.
