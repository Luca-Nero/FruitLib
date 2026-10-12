# Items and hands

Things lying about that bodies can pick up, carry, throw, use and hand over, and the game's own
right-click menu for things a mod puts in the world. Since 5.27.0.

## The registry: FruitItems

```csharp
FruitItems.Require("MyMod");                       // turns the 1 Hz scan on (and FruitBodies)
foreach (var it in FruitItems.All) { ... }
var gun   = FruitItems.Find("pistol", body.Position);       // by what a person would call it
var near  = FruitItems.Near(point, 3f);                     // nearest first, not held
var item  = FruitItems.Of(rigidbody);                       // or Of(gameObject), Of(key)
FruitBody who = FruitItems.HolderOf(firearm);
```

By default anything loose counts (`AutoMark`):
- a non-kinematic rigidbody with colliders;
- not part of a body;
- not in the player's hand (the game's `SpawnablePhysics.Interactable`);
- up to `AutoMaxMass` (25 kg) and `AutoMaxSize` (1.6 m).

What each `FruitWorldItem` affords is worked out from what it is:
- **Carry:** always.
- **Throw:** up to `ThrowMaxMass` (5 kg).
- **Swing:** long, thin and 0.3-8 kg.
- **Shoot:** the game's guns (`Firearm`). A gun is named model plus kind: "Lynx F pistol", "Grist 03 shotgun", "Viper 17 rifle".

A mod can change the default:

```csharp
FruitItems.Mark(rb, "live grenade", FruitAffords.Throw | FruitAffords.Explosive | FruitAffords.Use, "BombsAway");
FruitItems.Describers.Add(it => { if (IsMine(it.Rb)) it.Name = "C4 charge"; return true; });  // false keeps it out
FruitItems.Veto(rb);                                        // never an item
```

Fields a mod may set on an item:
- `Grip`: where the hand holds it, in item space.
- `CarryRotation`: how it sits relative to the body's facing.
- `OnUse` + `UseVerb`: a use of its own.
- `Tag`: anything the mod wants to keep with the item.

## Hands: FruitBodyControl

```csharp
FruitBodyControl.PickUp(body, item, run: false, source: "MyMod");
FruitBodyControl.Drop(body);
FruitBodyControl.Throw(body, targetPoint, strength: 1f);
FruitBodyControl.Use(body, targetBody, point: null);   // gun: aim + fire; swingable: go and hit; OnUse: the mod's
FruitBodyControl.Give(body, otherBody);
FruitWorldItem held = FruitBodyControl.Holding(body);
FruitBodyControl.HandsEnded += (body, item, result) => { ... };   // PickedUp, Unreachable, Thrown, Used, Lost...
```

**Picking up.**
1. The body walks to a **stance** beside the item (5.33.0), turns to it and reaches with the game's hand IK.
   - The stance is `PickupStance` (0.45 m) from the item's nearest point to the pelvis, or `PickupStanceLow`
     (0.45 m too; a separate setting) for an item it will crouch to (both ini). 5.33.0 had 0.55 and 0.7:
     too far, the hand IK strained at a floor grip out of reach and flipped the body over.
   - It is found on the side the body comes in from, on the navmesh, not taken by anyone, with nothing
     solid between it and the item. If none works on that side, it is searched for on rings round it.
   - The walk ends exactly on it: no spot-spreading and no turn to the group on arrival.
   - Before 5.33.0 the walk went to the item's own point and stopped 0.55 m short, or 0.4 m when
     the spot was taken or a door was near. A stepping turn to face the item then drifted it another
     20 cm, so bodies crouched with the item between their feet and the animation broke.
   - Once it has turned to face the item, the distance is checked again. Closer than the stance
     minus `StanceTooClose` (0.15) or further than it plus `StanceTooFar` (0.2), it walks to a
     stance once more (twice at most). A body that already stands in reach goes straight to the reach.
   - The hand is sent no further than `ArmReach` (0.62 m) from the shoulder, and a body whose Balance falls
     under `ReachAbortBalance` (60) while reaching lets go and ends Failed (5.33.1). `[FruitHands]` lines
     in the log give the distances at each step.
   - The item ignores the body's colliders from the start of the reach (it was only from the pull),
     so knees and elbows don't kick it about.
2. Ragdolls can't bend to the floor. So once the hand is within `MagnetReach` (0.6 m), or right above the item within `MagnetAbove` (1.1 m), or the body stands at the item after `ReachSeconds`, the item is pulled into the hand over a quarter of a second.
3. That pull stands in until there is an animation for reaching down.

**Holding.**
- A FixedJoint ties the item to the hand's rigidbody, breaking at `HoldBreakForce`.
- The item ignores every collider of its holder; without that it knocked against the body and sat 13 cm off.
- A held item weighs `HeldMassScale` (0.5) of its mass, at most `HeldMassCap` (8 kg), so carrying a gun
  doesn't pull the holder's balance off while walking (5.35.2; before, only items over 8 kg were lightened).
  The mass comes back when the item leaves the hand.
- The hand IK keeps a carry pose. The body drops the item when knocked down for a second (`DropWhenDown`), when it passes out, or when it loses the hand.

**Guns.**
1. `Use` raises the gun for `AimSeconds`, then fires: `ShotsPerUse` shots for a single-shot gun, or a `BurstSeconds` burst at the gun's own rate for an automatic, shot by shot (5.27.1).
2. The hand IK can't turn the hand, so the gun is re-seated about its grip to point at the target before every shot. Up close the arm is held back so the muzzle stays 0.35 m short of the target. `AimTarget(body)` says where a body is aiming.
3. `Shake(body)` (pain, blood loss, balance) puts each shot a few degrees off.
4. Shots are credited to the holder. The Gunshot and Shot events carry `actor = "body#N"` and say "Howie fired a gun", not "the player".

**How an item is handled (5.30.0).** `Use` follows the item's own recipe, if it has one:
steps FruitLib runs in order, without asking the mod or the mind again.

```csharp
it.Handling = FruitHandling.ArmAndThrow;        // Use (pull the pin), then Throw at the target
it.Handling = FruitHandling.AimAndFire;         // Aim (Muzzle, or Breech-to-Muzzle), then Use
it.Handling = FruitHandling.PlaceAndArm;        // PutDown facing the target, Use, Retreat
it.Handling = FruitHandling.PlaceAndDetonate;   // PutDown, Retreat, then Use from afar
it.Handling = new[] { FruitUseStep.Use, FruitUseStep.Wait, FruitUseStep.Throw };   // your own
it.HandlingPause = 0.35f;  it.RetreatDistance = 10f;
it.Muzzle = tube.Find("Muzzle"); it.Breech = tube.Find("Breech");
```

- `Use` steps call `OnUse` wherever the item is: in the hand, or where it was set down.
- `PutDown` sets the item upright with its forward toward the target. `PutDownRotation` turns it if the model's front isn't +Z.
- A recipe that uses the item before throwing it also runs when a body is told to `Throw` it. So a grenade is never thrown with the pin in, nor held once the pin is out.
- `FruitItemProp.Handling` (with `HandlingPause` and `RetreatDistance`) passes the recipe to props put down with spawn mode.
- `FruitHandling.Describe(steps, useVerb)` gives "pull the pin, then throw it" for prompts. `FruitBodyControl.IsHandling(body)` says whether a recipe is running.
- A `PutDown` step publishes a "PutDown" event.

**Events** (FruitEvents): `PickedUp`, `Dropped`, `Thrown` (with direction), `Gave`. Each has the body as `actor` and the item's name as `cause`.

## The game's right-click menu: FruitPropMenu, FruitProps

The game finds a menu by asking the rigidbody under the cursor for a `ContextMenuActionsHandler`.
FruitLib injects a subclass of that handler, and subclasses of the game's
`SimpleContextMenuAction` and `ToggleableContextMenuAction`. The menu that opens is the game's own.

```csharp
// A mod's thing as a prop: a rigidbody, colliders, listed in FruitItems, with the game's menu.
var it = FruitProps.Spawn(prefab, at, rotation, "smoke grenade", mass: 0.6f,
                          FruitAffords.Throw | FruitAffords.Use, "BombsAway", "weapons",
                          new[] { new FruitMenuEntry { Name = "Pull the pin", OnClick = () => Arm(it) } });

// Any GameObject with a rigidbody: a menu of its own.
FruitPropMenu.Attach(go, "Crate", "props", entries);

// Extra lines in the game's own menus (guns, props, limbs), refreshed each time one opens.
FruitPropMenu.Extend((handler, rb) => FruitItems.Of(rb) is { } it
    ? new[] { new FruitMenuEntry { Name = "Weigh", Label = () => $"Weighs {it.Mass:0.#} kg", Available = () => true, OnClick = () => {} } }
    : null);
```

Before 5.28.1 none of this injected. Il2CppInterop 1.5.1 names a base vtable's game types as
"..., FRUKT", but the managed assembly is Il2CppFRUKT. `ContextMenuAction`'s `CompareTo` hit that.
`FruitInterop.Register` maps the name during injection.

`FruitMenuEntry` makes a plain line (`OnClick`) or a switch (`IsOn` and `OnToggle`).
- `Label` and `Available` are asked again each time the menu opens.
- `Priority` orders the lines: the game's Delete is 999-1000.
- `FruitProps` adds a Delete line itself.
- `FruitProps.Spawn(model, at, rotation, name, mass, affords, source, category, it => lines)` (5.28.0)
  makes the lines for the new item, so they can act on it.

## Putting an inventory item down: spawn mode

Since 5.28.0 a FruitLib inventory item can be put down in the world by the player, with the game's
own placement: the same key, hologram, surface/free choice, rotation and "can't place here"
sound as the native guns and props.

```csharp
var frag = FruitInventory.AddItem(new FruitItem { Id = "BombsAway:Frag", Name = "Frag", Model = fragInHand, ... });
frag.Placeable(fragProp, mass: 0.4f, FruitAffords.Throw | FruitAffords.Explosive);   // like the game's guns
frag.Prop.Menu     = it => new[] { new FruitMenuEntry { Name = "Pull the pin", OnClick = () => Arm(it) } };
frag.Prop.OnPlaced = (item, it) => { /* it is the FruitWorldItem now in the world */ };

crate.Placeable(cratePrefab, mass: 12f);
crate.Prop.PlaceOnClick = true;                                                        // like the game's props
```

`FruitItem.Prop` (a `FruitItemProp`) holds:
- `Model`: what is put down and what the hologram shows. Null uses the item's `Model`.
  Its root lands where the hologram's root stood.
- `MakeModel` (5.29.0): makes `Model` the first time it is needed, for a model that needs the
  loaded game (a shader, an asset bundle). Returning null means "not yet": placement waits and
  asks again at the next selection.
- `OnUse`, `OnUseHeld`, `OnUseReleased`, `UseVerb` (5.29.0): the placed prop's use, copied onto
  it and wired to the game's interaction key (see below).
- `Name`, `Mass`, `Affords`, `MenuCategory`, `Menu`: passed to `FruitProps.Spawn`. So the
  placed thing is an ordinary FruitLib prop: a rigidbody, colliders, listed in `FruitItems`,
  with the game's right-click menu (Delete plus the item's lines).
- `OnPlaced(item, worldItem)` after each placement; `OnSpawnMode(item, entered)` as the mode
  starts and ends. `FruitItem.InSpawnMode` says whether it is on.
- `ModelTurn`: the hologram's starting turn, degrees.
- `PlaceOnClick`:
  - **false** (default), like the game's guns. The item works in the hand as usual, and the
    spawn-mode key brings up the hologram. A left click places it. While the mode is on, the
    game sends the mouse to its placer, so the item's own callbacks pause.
  - **true**, like the game's props. The hologram shows whenever the item is held. A left
    click places, and the wheel moves the hologram nearer or further in free placement.
    `OnPrimary` and `OnScroll` never fire.
- `Broken`: set if the game's placement could not be built. The item then stays hand-only,
  with a warning in the log.

**How it works.** The game's own item builds its placement in `SpawnableGodInventoryItem.OnAwake`.
It asks the scene's `ISpawnPlacementFactory` for a `PlacementSession`, and for a
`SpawnModeFeature` (guns) or `PropPlacementFeature` (props) built round the item as
`ISpawnModeHost` / `IPlacementHost`. The feature goes into the item's `Features`.

FruitLib does the same for each FruitLib instance on the toolbar (`FruitSpawnMode`, in
`Inventory/`):
1. It resolves the factory from the scene's Zenject container and calls it. The feature therefore
   arrives wired by the game to its spawn-mode switch, key hints, input provider, update loop,
   open-menu check and hologram material.
2. The feature goes into `FruitHeldItem`'s `Features`, so the game's own activation drives it.
3. The host is `FruitSpawnHost`, a small injected object implementing both interfaces.
   `FruitHeldItem` can't be the host: the interface would have to be added to an already
   injected class.
4. The hologram is a copy of the prop model stripped to its mesh renderers (skinned meshes in
   bind pose), with the game's `MeshDataHandler` and a trigger box round them. The game swaps
   in its hologram material and reads the box to sit the hologram on surfaces.

Differences from a native placement:
- **No print-in dissolve.** The game's `WorldPrintService` freezes and thaws the placed body
  through `SpawnablePhysics`, which a FruitLib prop doesn't have. `Place` returns null, so the
  placer skips the print. FruitLib replays the hologram's appearance and marks the crosshair
  itself, as the placer does after a print.
- **One hologram material per renderer.** The game sets each hologram renderer to a single
  material. A renderer with several submeshes shows only its first as hologram.

Untested in game as of 5.29.0 (BombsAway 5.31.0 is the first mod to use it). The first test
should check, in order:
1. The log line `[FruitSpawnMode] ... can be placed`. If it's missing, check for a warning that
   the Zenject factory call failed: Zenject must accept the injected host as `ISpawnModeHost`.
2. The key hint and hologram appear.
3. The prop lands inside the hologram.

## Using a placed prop: the interaction key

Since 5.29.0. A placed game gun shows the interaction key when the player points at it, and
fires when it is pressed. `FruitPropUse` gives a mod's prop the same: the hint, the key and the
targeting are the game's.

```csharp
FruitPropUse.Attach(go, onPress: () => Fire(), onHold: held => { }, onRelease: () => { });  // any rigidbody GameObject
FruitPropUse.Attach(worldItem);   // its OnUse (press), OnUseHeld (every frame held), OnUseReleased
FruitPropUse.Detach(go);          // the hint goes
```

`FruitItemProp.OnUse` and friends do this for a placed inventory item, after `OnPlaced`. With the
player as the user, `OnUse` gets a null user and a null target. A body holding the prop uses it
through the same `OnUse` (`FruitBodyControl.Use`), so one delegate serves both.

**How it works** (Ghidra group `prop-use`). `PlayerInteraction.FindTarget` takes the rigidbody
under the cursor, or the one the player carries. That body needs:
- `SpawnablePhysics.MayPlayerAct`, which is always true without the game's `SpawnablePhysics`;
- an `InteractionReceiver` on the rigidbody's own GameObject;
- a subscriber on the receiver's Use channel (a `ContinuingAction`).

With such a target the game takes the hint bar's Interaction slot. Press starts the channel
(`OnStart`), each held frame fires `OnContinue`, and release or looking away ends it (`OnEnd`).
The game's `FirearmUse` is just two subscriptions: semi-automatic on start, automatic on
continue. `FruitPropUse` adds the game's receiver and subscribes to the same events.

## A mod's gun: FruitModGun

Since 5.29.0. `FruitWorldItem.ModGun` makes a mod's prop a gun, and `IsGun` is true for the
game's guns and for mods' guns:

```csharp
it.ModGun = new FruitModGun { Muzzle = muzzleTransform, Automatic = true, ShotGap = 0.1f,
                              Shoot = (item, by) => FireOneRound(item.ModGun.Muzzle) };
FruitPropUse.Attach(it);   // with no OnUse: press fires, an automatic keeps firing while held
```

A body holding it aims it as it aims a game gun: it re-seats the gun so `Muzzle.forward` points
at the target, fires shot by shot, and sends bursts for an automatic. Every shot goes through
`FruitItems.FireModGun`, which publishes the Gunshot event credited to the shooter.
