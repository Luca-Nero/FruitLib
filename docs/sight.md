# Sight

What a body can see, in words and in pictures. Built for language-model minds
(NeuroFrukt), and usable by any mod that needs to ask whether one body can see
something. Since 5.18.0.

## Eyes and line of sight

```csharp
FruitSight.Eye(body, out Vector3 eye, out Vector3 forward, out Vector3 up);
bool seen = FruitSight.CanSee(body, point, target: otherBody);
FruitSightZone z = FruitSight.ZoneOf(angleOffGaze);   // InView (≤ 60°), Corner (≤ 100°), Behind
```

- The eyes come from the head rigidbody: +Z forward and +Y up on the Release rig.
  A body on its back looks at the sky. A body without a head falls back to the
  pelvis facing.
- Glass and windows don't block line of sight. `CanSee` also ignores the body's
  own colliders, the target's colliders and triggers. Other bodies do block it.
- `FruitSight.Clear(from, to, self, target, targetRb)` is the bare test, with no
  angle check.

## A look

```csharp
FruitView v = FruitSight.Look(body);              // full: layout + structures, ~85 rays, 1–1.5 ms
FruitView g = FruitSight.Look(body, full: false); // glance: bodies, player, loose things
foreach (var s in v.Seen) if (s.Visible) ...      // visible first, then nearest
string where = v.Bearing(s.Position);             // "3.2 m ahead to your left", from the gaze
```

`FruitSeen` covers four kinds of thing:
- `body`: with `Body`.
- `player`: the camera.
- `thing`: a loose rigidbody, with `Mass`, `Size` and `Moving`.
- `structure`: a building or fixture in the gaze fan.

Each one has a stable `Key`, `Distance`, `Angle` and `Zone`, plus two flags:
- `Clear`: nothing solid is in between.
- `Visible`: `Clear`, and not behind the body.

A full look also fills in the layout:

| Field | Meaning |
|---|---|
| `Indoors` | A ceiling within 6 m, and walls within 8 m on at least 7 of 12 level bearings |
| `Inside`, `Storey` | Which building, and which floor (0 = ground floor) |
| `Ground` | What the body stands or lies on, e.g. `"asphalt road"` |
| `Open`, `Closed` | Which way is clear (`"ahead"`, `"to your left"`…) and which is walled |
| `Setting` | The place in one line, see below |

## Names

`FruitSight.Name(go)` returns a `FruitThingName`:
- `Part`: what the ray hit.
- `Object`: the whole thing that part belongs to.
- `Storey`: which floor the part is on.
- `SeeThrough`: glass and windows.
- `Ground`: something walked on, such as a road or a floor.

Maps that name GameObjects `Material_Thing` get readable names without any extra
work. FruitCity does this:

| GameObject | Name |
|---|---|
| `Concrete_Wall_F0_S3#…` under `Concrete_Apartment` | part "concrete wall", object "concrete apartment block", storey 0 |
| `Steel_Pole` under `Steel_Streetlight` | object "streetlight" |
| `Wood_Crate`, `Steel_Barrel`, `Steel_Car` | "wooden crate", "steel barrel", "car" |
| `Barrel(Clone)` | "barrel" |

How a name is cleaned up:
- Storey (`_F1`) and section (`_S3`) tags, direction suffixes and everything
  after `#` or `(` are dropped.
- CamelCase is split into words.
- The material becomes an adjective.

Anything a name can't express goes in a describer. Describers are asked newest
first:

```csharp
FruitSight.Describers.Add(go => go.name.StartsWith("BA_Grenade")
    ? new FruitThingName { Part = "live grenade", Object = "live grenade", Storey = -1 }
    : (FruitThingName?)null);
```

A part called `Physics`, `Collider`, `Root` and the like says nothing. Its name
becomes that of the nearest ancestor with a real one, up to five levels up
(logged once per name when Verbose log is on).

## What is close

Since 5.35.0. What lies within a few steps is known without looking at it, so
every look also marks what is `Close` (`FruitSight.CloseRange`, 4 m from the eyes
or the body, in any direction):

```csharp
foreach (var s in v.Seen)
{
    if (!s.Close || s.Kind != "thing") continue;
    // s.Item          the FruitWorldItem, if FruitItems knows it ("Lynx F rifle")
    // s.On            what it rests on ("wooden shop counter"), "" for the floor (full look only)
    // s.AboveFloor    metres from the floor to its underside (full look only)
    // s.Visible       false for something at the body's back or at its feet
}
```

- Loose things are the union of the visual scan and `FruitItems`: anything
  FruitItems has registered is seen under its own name, whatever its size or
  object name (a grenade is under 15 cm and a gun's rigidbody is named
  `Physics`; both were left out of the scan before 5.35.0).
- `FruitView.FloorY` is the height of the floor under the body.
- The *on* ray is one cast under each of the nearest eight close things; a floor
  or ground name counts as no support.

## Setting

`FruitSight.Setting` picks the first of these that is set:
1. A mod's `FruitSight.SettingOverride`.
2. A custom map's `FruitMapDef.Setting`.
3. The first sentence of the map's `Blurb`.
4. A built-in line for the game's own map.

## Pictures

```csharp
byte[] rgba = FruitEyeCamera.Capture(body);   // main thread, 384 × 256, bottom row first
byte[] png  = FruitPng.FromRgba(rgba, FruitEyeCamera.Width, FruitEyeCamera.Height);   // any thread
```

- Each capture is one shared camera, rendered with `Camera.Render()` and read
  back with `ReadPixels`: about 1 ms to render and 1–2 ms to read.
- `FruitPng` encodes the pixels in plain C# (about 3.5 ms), so run it on a
  worker thread. The game's `EncodeToPNG` returns through an out-array wrapper,
  which is not safe to call.
- `FruitEyeCamera.ShowPlayerOrb` draws a glowing sphere where the player's camera
  is. It appears only in captures: it is switched on for the render and off right
  after, so no other camera ever sees it.

## Turning

`FruitBodyControl.Face(body, direction)` and `FruitBodyControl.LookAround(body, seconds)`
turn a standing body on the spot. If the target yaw alone hasn't turned it after
a second, it turns with small steps instead. The log reports which of the two
happened (`turn check`).

## Debug

The FruitLib Diagnostics option **Sight test** captures the body under the
crosshair two ways, on F10 (Shift+F10 for every body). It times each capture and
logs what rays from the body's eyes hit. The log goes to `UserData/FruitSight.log`
and the pictures to `UserData/FruitSight/`.
