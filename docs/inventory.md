# FruitLib inventory items

`FruitLib.FruitInventory` adds items to the release build's inventory window (the
Terminal). A FruitLib item is a real inventory item, the same as the native ones:

- it is listed on its category shelf, with icon, name, description and stat card;
- the player sends it to any toolbar slot with the number keys, like any other item;
- it is spawned into the player's hand as a real item;
- the game delivers the mouse input to it while it is held.

This replaces the old toolbar-slot API. See [Migrating from FruitToolbar](#migrating-from-fruittoolbar).

## Usage

Register from your mod's `OnInitializeMelon`:

```csharp
using FruitLib;

public override void OnInitializeMelon()
{
    FruitInventory.AddItem("MyMod:Flare", "Flare", FruitItemCategory.Tool);
    FruitInventory.AddItem("MyMod:Crate", "Crate", "prop");   // by name
    FruitInventory.AddItem("MyMod:Knife", "Knife", 0);        // by number: 0 = Weapon
}
```

All three calls take an optional `icon` and `description` after the category, and all
three return the `FruitItem`. Keep a reference to it if you want callbacks or to change it
later.

### Categories

| Number | Enum | Accepted names | Shelf in game |
|---|---|---|---|
| 0 | `FruitItemCategory.Weapon` | weapon, weapons, gun, guns | Weapons |
| 1 | `FruitItemCategory.Tool` | tool, tools | Tools |
| 2 | `FruitItemCategory.Etc` / `.Misc` | etc, misc, miscellaneous, other | Etc |
| 3 | `FruitItemCategory.Prop` | prop, props | Props |

Names are matched loosely: case, spaces, punctuation and a trailing "Category" are
ignored. So `"Weapons"`, `"weapon"` and `"WeaponCategory"` all find the same shelf. A
string that names a category a future build adds works without a FruitLib update. An
unknown name is filed under Etc, and the log names the categories that do exist.
`FruitInventory.Categories` lists them at runtime.

### Full control

```csharp
var flare = FruitInventory.AddItem(new FruitItem
{
    Id          = "MyMod:Flare",
    Name        = "Flare",
    Description = "Lights up the dark. Briefly.",
    Category    = "tool",
    Icon        = FruitIcons.Load(Assembly.GetExecutingAssembly(), "Icons/Flare.png"),
    Model       = myFlareModel,          // optional, cloned into the item
    OnSelected  = item => { /* item.Held is the in-hand GameObject */ },
    OnDeselected = item => { },
    WhileHeld   = item => { },           // every frame while in hand
    OnPrimary   = item => Ignite(item.Held.transform),
    OnSecondary = item => { },
}.AddStat("Burn time", "30 s").AddStat("Colour", "Red"));
```

Input callbacks: `OnPrimary`, `OnPrimaryHold(item, seconds)`, `OnPrimaryRelease`,
`OnSecondary`, `OnSecondaryHold`, `OnSecondaryRelease`, `OnMiddle`, `OnScroll(item, notches)`.
They come from the game's own input dispatch, so they only fire while the item is held
and gameplay has input. They do not fire while the inventory window or a menu is open,
and your code does not need to check for that.

Every callback runs inside a try/catch. An exception is logged, not fatal.

### Ids

`Id` should be stable, like `"MyMod:Flare"`. If you leave it empty, FruitLib uses
`"{YourAssemblyName}:{Name}"`. Registering an id twice logs a warning and returns the
first item. Two mods can use the same display `Name`: the game only ever sees an internal
name derived from the id.

### The card

The inventory window shows a card for the hovered item: the name, key/value rows, then the
`Description` as flavor text. The game types the card by what the item is, and FruitLib
builds the same descriptor types:

```csharp
new FruitItem { Name = "AK-47", Category = "weapon", Description = "Full-auto assault rifle." }
    .WeaponCard("7.62 mm", "automatic")          // required rows, drawn by the game
    .AddStat("rate", "600 rounds per minute");   // free rows follow
```

- `WeaponCard(caliber, fireMode)` gives a `SerializedWeaponDescriptor`, whose caliber and
  fire mode rows open the card, as on the native LYNX-F. A missing half shows `-`.
- `PropCard(size)` gives a `SerializedPropDescriptor` with a size row.
- Neither: a plain `SerializedItemDescriptor` with only your rows.

`AddStat(key, value)` adds free rows after the typed ones. The game rejects the whole item
if any row lacks a key or a value, or if a key appears twice (a free row repeating
caliber, fire mode or size counts). FruitLib checks for this first and drops only the
offending row, with a warning.

### Changing name or icon later

```csharp
flare.SetDisplay("Flare (spent)", FruitIcons.Solid(Color.grey));
```

The inventory window shows the change the next time it draws. An entry already on the
toolbar keeps its old name and icon until it is replaced.

## Icons

`FruitIcons.Load(assembly, "Icons/X.png")` loads a PNG embedded in your mod (mark it as
`EmbeddedResource` in your csproj). Matching is by suffix. `FruitIcons.Load(bytes)` takes
raw PNG bytes. `FruitIcons.Solid(Color)` draws a disc for prototyping. An item without an
icon gets a grey disc: the game's icon drawer throws on a missing sprite, so it is never
left empty.

If you build a sprite yourself, set `hideFlags = HideFlags.HideAndDontSave` on both the
texture and the sprite. Otherwise the first scene load's `UnloadUnusedAssets` collects them,
and the item silently loses its icon.

## How it works

For whoever has to fix this after a game update. The bodies are in
`GameData/RELEASE_Ghidra` (exported headless by `Tools/ghidra_export_decomp.py`, group
`inventory`).

- **Registration.** `NativeGIIGroupHandler.RegisterGIIPrefab(SerializedGIIRegistrationData)`
  registers the prefab under `PrefabID("Native", ObjectName + "GII")`. It snapshots the
  registration into a `GodInventoryItemData` and adds that to the group's `HashSet`.
  `NativeGodInventoryItemsRegistration.GodInventoryItemsData` is a lazy `SelectMany` over
  those sets, so a late addition is live everywhere. FruitLib calls it from a postfix on
  `NativeGIIProps.Register`. That is the last group to register at boot, so every native
  category exists by then.
- **Categories are matched by reference.** `TerminalItemsService.ItemsOf(category)` compares
  `item.Category == category`. `RefuseLayoutThatCannotBeDrawn` throws if any registered
  item's category is missing from the layout, and that takes down the whole window. FruitLib
  therefore only ever uses the game's own category assets. It harvests them from the native
  items at boot, then swaps to the layout's own list in a prefix on
  `RefuseLayoutThatCannotBeDrawn`, correcting any registered item there before the check
  runs.
- **Prefab names.** `PrefabID.Validate` rejects `_`. FruitLib registers under
  `FruitLib` + the id's letters and digits, then writes the display name into the
  descriptor and into the data's snapshot.
- **The item itself.** `GodInventoryItem` is abstract (`SfxPlayerService`,
  `OnDestroyInternal`). FruitLib injects `FruitHeldItem : GodInventoryItem` with
  Il2CppInterop's `ClassInjector`. It implements both, and it overrides the mouse virtuals
  that forward to your callbacks. Unity only runs the managed `(IntPtr)` constructor on an
  injected component, so `FruitHeldItem` does by hand what every native concrete
  constructor does: it creates the `Features` list, which activation, deactivation,
  enable/disable and destroy all iterate. Do not invoke `GodInventoryItem`'s native `.ctor`
  instead: on a Unity-created object, that hard-crashes the game on equip. Do not Harmony-patch those virtuals on `GodInventoryItem`:
  their empty bodies are one folded IL2CPP stub (`CallerCount 12446`), and a patch would
  hook every method sharing it.
- **Selection.** Postfixes on `GAToolbarGIIItemsHandler.AddItem` / `RemoveItem` map live
  instances to items. Postfixes on `GAToolbarSelectedItemProcessor.OnItemSelect` /
  `OnItemDeselect` drive `OnSelected` / `OnDeselected`. Selection by number key, scroll
  wheel or click all go through there.

FruitLib logs each step under `[FruitInventory]`: the categories it found, each
registration with its prefab name, toolbar placements, and selections. Read that first
when something breaks.

## Limits

- **No native behaviour beyond holding.** A FruitLib item does not spawn a prop or fire a
  native weapon. It is a held object with your callbacks. Use FruitBallistics for shooting.
- **No shelf position control.** Items the game gives a manual shelf priority come first,
  then the rest by name. FruitLib items are in the second group.
- **Items cannot be removed** once registered, only renamed.

## Migrating from FruitToolbar

`FruitToolbar.Register` still works. It is now a thin, `[Obsolete]` layer over
`FruitInventory`, and it files the item on the Tools shelf. What changed:

- The item is not on the toolbar until the player sends it there from the inventory
  window. The `int` your `OnSelected` / `OnDeselected` receive is the slot the player
  chose.
- `NativeBaseOverride`, `PreferredOrder`, `AutoCentreSlotRow` and `SlotRowOffset` do
  nothing. FruitLib no longer adds slots.
- `FruitToolbar.LoadIcon` / `MakeSolidIcon` forward to `FruitIcons.Load` / `FruitIcons.Solid`.

New code should use `FruitInventory` directly: it adds categories, stats, a model and
input callbacks, none of which the old API could express.
