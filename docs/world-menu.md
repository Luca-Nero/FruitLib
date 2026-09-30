# World menu

*Since 5.5.0.*

`FruitLib.FruitWorldMenu` hooks the SCENE page of the in-game terminal (World, then
SCENE): the six rows DELETE NPC, DELETE BODIES, CLEAR PARTICLES, RESET MAP, RESET ALL
and RESET PLAYER. It does two things: tells your mod when one of them ran, and lets you
put buttons of your own under them.

## Knowing when the player reset something

```csharp
FruitWorldMenu.SceneActionRan += action =>
{
    if (action == FruitSceneAction.DeleteBodies) ForgetMyCorpseDecals();
};

FruitWorldMenu.MapReset += () => ClearEverythingIPlaced();
```

| Event | Raised |
|---|---|
| `SceneActionRan(FruitSceneAction)` | After the game ran one row, once per button press. RESET ALL is one press, so you get `ResetAll` once, not the steps inside it. |
| `MapReset()` | After RESET MAP or RESET ALL, once per press. The map is back to how it was authored. |

`MapReset` is the one most mods want. The game's reset only knows about the game's
own objects; anything your mod spawned (a placed charge, a decal, a stuck grenade)
stays where it is unless you let go of it here.

Things to know:

- **Resets happen in place.** The scene is not reloaded, so `OnSceneWasInitialized`
  does not fire. These events are the only signal.
- **Destruction is deferred.** The game destroys the map's spawned objects at the end
  of the frame. Do not test for them being gone from inside the handler; clear your
  own list and your own objects.
- **A throwing subscriber does not stop the others.** Each handler is called on its
  own, and an exception is logged once per handler.

`FruitSceneAction` is `DeleteNpc`, `DeleteBodies`, `ClearParticles`, `ResetMap`,
`ResetAll`, `ResetPlayer`.

## Adding a button

```csharp
FruitWorldMenu.AddButton("mymod.clear", "Clear my things", () => ClearEverythingIPlaced());
FruitWorldMenu.RemoveButton("mymod.clear");
```

The button is a copy of one of the game's own rows, so it has the same plate, font,
hover flash and click sound, and it appears under the game's rows in the order you
added it. The label is shown in upper case like the others.

- **Call it any time.** Before the terminal exists is fine: the registration is kept
  and the button is built whenever the page is. If the page is already up, the button
  appears straight away.
- **Adding the same id again replaces the button**, label and click handler both.
  Ids only need to be unique among mods, so prefix yours with your mod's name.
- **Your click handler is wrapped.** An exception in it is logged, not thrown into
  the game.
- **Buttons are rebuilt with every scene.** You register once; FruitLib puts them
  back each time the page is built.

Only add buttons that mean something to the player at the terminal. A mod's settings
belong in the MODS menu (see [Config & menu](config-and-menu.md)); this page is for
scene actions, such as "remove everything this mod spawned".

## What it does not do

It does not add rows to the game's own list, and cannot: the terminal checks that list
against its row enum when it starts and refuses to run if they disagree. Your buttons
sit beside the list, so they are not part of `SceneActionRan`, and pressing one does
not raise it.
