# Toolbar

`FruitToolbar` adds items to the game's native God-mode toolbar — real slots with
native rendering, layout, key badges, highlighting and selection events.

> **Currently non-functional — do not build against this.** The game-behaviour
> findings below were each established empirically in-game and are recorded
> because they're expensive to rediscover, but FruitLib's packaged version of the
> injection does not presently work end to end. Treat this page as research
> notes, not as an API. The code narrates every step it takes to the log, which
> is where to start if you pick the work up.

## Using it

Register before scene load, i.e. from `OnInitializeMelon`:

```csharp
FruitToolbar.Register(new FruitToolbarItem
{
    Name         = "My Weapon",
    Icon         = mySprite,               // optional; falls back to the template's icon
    OnSelected   = idx => EquipMyThing(),
    OnDeselected = idx => HolsterMyThing(),
});
```

That's the whole API. The item lands in the next free slot, and its number key
(slot index + 1) selects it. Only `Alpha1`–`Alpha9` are wired.

`OnSelected` / `OnDeselected` fire off the game's own selection hook, so they're
consistent with how native slots behave — including when the player switches away
via any other route.

## Why it works the way it does

Useful if you're extending this, because most of the obvious approaches are dead
ends that cost real time to rule out:

1. **The scene already contains a 5th slot view**, inactive, complete with a key
   badge. No UI cloning or manual layout is needed.
2. **Appending to `m_itemsToPopulate` does not create a working item**, and can't
   be made to. `Start()` resolves entries through a name-keyed registry whose real
   implementation has exactly one hardcoded field — a closed catalog. A clone
   always reports as the item it was cloned from. The array entry is still used
   here, but only to carry a correctly-shaped icon payload.
3. **Raising `m_capacity` alone changes nothing** — though the extra slots do have
   to fit inside it, since the service bound-checks against it.
4. **The data model accepts a 5th item fine.** Registering one returns success
   with a real, fully-working native item. That result is what ruled out building
   a custom item type: item construction was never the blocker.
5. **The blocker is the view.** Neither the rebuild nor the draw-one-slot path
   activates the spare — the native builder only ever activates slots it knew
   about at build time. The slot GameObject simply has to be `SetActive(true)`'d.

So the working recipe is: register in the data model, activate the pre-existing
slot GameObject, draw it, and route the number key through the service's own
selector. Everything visible stays native; the only custom code is the activation
and the keypress.

The registered payload is the game's own weapon item used purely as a stand-in
handle — the closed catalog makes a distinct custom one impossible. What the
player sees and gets is yours: the icon and label come from the draw call, the
behaviour from your callbacks.

## Limitations

- Slot indices are assigned in registration order across all mods. Two mods each
  adding an item both work, but neither controls which number key it gets.
- None of this is readable from the dnSpy export — IL2CPP interop assemblies
  expose signatures, never method bodies. Any further work here means probing
  in-game.
