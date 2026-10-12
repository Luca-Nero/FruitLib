# Pathing

*FruitNav since 5.19.0; FruitPlaces, cover and hiding since 5.20.0; shaped paths, smooth walking and the walking course since 5.21.0; crowds since 5.22.0 (groups since 5.25.0); spots since 5.23.0; footing and doors by name since 5.24.0.*

FRUKT ships no navmesh, and its build keeps none of Unity's managed navmesh building. The
engine side survives, so `FruitNav` builds a navmesh at runtime through the icalls
(`FruitNavNative`), in a box around the player and every tracked body. The build runs in the
background: 12–30 ms off the main thread for an 80 m box, under 1 ms on it.

```csharp
FruitNav.Require("MyMod");

var path = new FruitPath();
if (FruitNav.FindPath(body.Position, shopDoor, path) && path.Status == FruitPathStatus.Complete) ...

FruitBodyControl.GoTo(body, shopDoor, stopDistance: 1f, seconds: 30f);   // walks it there
FruitBodyControl.MoveEnded += (b, result) => ...;                        // Arrived, Blocked, NoRoute...
```

`FruitBodyControl`'s goto orders (`GoTo`, `WalkTo`, `Approach`, `Follow`, `Attack`, `WalkAway`)
follow paths on their own once FruitNav is running.

## The agent

A FRUKT body is a 0.38 m radius, 1.8 m tall agent. It can take slopes up to 20 degrees: a
15 degree ramp is walkable and a 25 degree one is not. With the step assist on it can take steps
up to `NavClimb` (ini, default 0.25 m since 5.33.0, was 0.3); with it off, steps count as walls.
Gaps under about 0.8 m are closed, because bodies sway and their shoulders catch.

The climb stays below the assist's own 0.32 m on purpose. The navmesh is built from 10 cm cells
and 5 cm height steps, so a ledge's height is known to within a cell: with a climb of 0.30,
30-35 cm ledges (a low wall's foot, a plinth) were walkable on the navmesh and not for the body,
which walked up to them and stood against them. A body that is stuck against a "32 cm ledge" in
the log is this.

The box is rebuilt in four cases:

- when a body leaves it (the box snaps to a 16 m grid),
- every 4 s while anyone walks a path, and every 16 s otherwise,
- 2.5 s after an explosion,
- on `FruitNav.MarkDirty()`.

## Queries

| Call | Gives |
|---|---|
| `FindPath(from, to, path, snap, measureRoom, shape)` | A route as corners, its length, and the room along it. `shape` keeps it off walls (below) |
| `Shape(path)` | Reshapes a found path in place |
| `FindAway(from, threat, distance, path)` | A reachable spot away from a threat |
| `FindCover(from, threat, maxDistance, path)` | The nearest reachable spot hidden from a threat at chest and head height |
| `Hidden(eye, spot)`, `StraightClear(a, b)`, `Room(p)`, `Sample(p, r, out onMesh)` | Single checks |

`FruitPath` has `Corners`, `Along` (the distance to each corner), `Length`, `Room` (the room
either side, sampled every 0.5 m), and `PointAt(d)` / `DirectionAt(d)`.

## Shaped paths (5.21.0)

The navmesh's own path is the shortest one. It is pulled tight round every corner and touches the
navmesh edge at each one, which leaves only the agent radius between the body and the wall. A
body can't walk that line: it sways a hand's width with every step, swings wide on turns, and lags
behind its heading.

`Shape` treats the path as an elastic band:

1. It places a point every 0.3 m.
2. Each point is relaxed toward its neighbours, which rounds the corners off.
3. Each point is pushed away from the nearest navmesh edge until it has `PathMargin` of room.
   That keeps it off walls and runs it down the middle of anything narrower than twice the margin.
4. Every move is checked against the navmesh. If the shaped line would leave the navmesh anywhere,
   the plain path is kept.

`PathMargin` is set by `NavPathMargin` (ini, default 0.3). With it, a pelvis stays 0.38 + 0.3 m
from a wall.

## Smooth walking (5.21.0)

When `NavSmoothWalking` is on (ini, the default), goto orders do the following:

- They walk shaped paths.
- They steer from the body's centre (pelvis and torso), smoothed over `CentreSmoothing` (0.3 s,
  about one step) and led by its smoothed velocity. The body no longer chases each sway of its
  pelvis.
- They use pure pursuit with a lookahead that grows with speed: `LookAheadBase` + `LookAheadPerSpeed`
  per m/s, up to `LookAheadMax`.
- They slow down through tight spots (`TightPace`) and into corners (`CornerPace` at 90 degrees).
  Slowing plays the animation slower, the same way a run plays it faster. A body never runs into
  either.
- At a heading error over `PivotEnter` (75 degrees) the body stops and turns on the spot. It sets
  off again once the error is under `PivotExit`.
- A body that is still turning walks an arc, no more than `MaxSideStep` (40 degrees) off its
  facing, rather than crabbing sideways on the wobbly side-step clips.

With the setting off, bodies walk as they did in 5.20: the shortest path, steered from the pelvis.

## Crowds (5.22.0)

Before this, sending five bodies to "the back room" sent all five to the middle of its floor.
Smooth-walking goto orders now share space in these ways:

- **Free spots.** An order to a point that is taken goes to the nearest free spot instead. A point
  is taken when someone stands within `PersonalSpace` (0.9 m) of it, someone lies within
  `LyingSpace` (1.2 m), or another order has claimed a spot that close. The free spot is:
  - in the same room or area as the point, or within `SpreadRadius` (3 m) outside any,
  - out of doorways and passages, and at least 1 m from any door,
  - favoring a clear final approach, then proximity to the walker.

  Since 5.35.1, new claims leave an extra `SpotStop` of clearance to account for stopping short.
  Sampled spots stay on the same floor and on the same side of walls and furniture. A spot
  behind someone already standing (or claiming a spot) is less desirable than an open approach.

  `FruitBodyControl.Taken(point)` and `FreeSpot(body, point, out spot)` are public.
- **Where it will stand (5.25.0).** The claim is where the body will stop, not the point it was
  sent to: an order that stops 1.2 m short claims the spot 1.2 m short along its way in. Before,
  the point was the claim, so a second body's "free" spot could be right where the first one
  stopped. When that stand is taken, the order gets the point itself if it is free, else a free
  spot, and walks onto it (`SpotStop`, 0.4 m; was 0.7).
- **Taken on the way.** Within 4 m of its spot, a body checks every half second whether someone has
  stopped on the spot. If so it picks another. When none is left, it counts as arrived once
  within 2 m. A body stuck for 2 s within 1 m of its spot (or within its stop distance of the
  point) has arrived instead of backing off into the crowd (5.25.0).
- **Facing the group (5.25.0).** On arrival, a body turns to the others standing within
  `GroupRadius` (3 m). With nobody there and its spot off to one side, it faces the point it was
  sent to. `FaceTheGroup = false` turns this off. A `MoveEnded` handler that turns the body
  itself (to a spot's sofa) runs after it and wins.
- **Slots round a body (5.26.0).** `Approach` and `Follow` used to aim at the other body's centre
  and stop short on the way in, so several sent to the same body stopped on the same side, on
  top of each other. Within `SlotWithin` (2.5 m) of its stop distance, an order to a body now takes
  a slot on the ring `StopDistance` round it:
  - its own bearing from the target first, then `SlotStep` (20 degrees) either way at a time, up
    to `SlotSweep` (120 degrees),
  - the first one `PersonalSpace` from anyone standing there and from every other order's slot
    or spot, on the navmesh and not behind a wall from the target.

  The slot is a bearing, so it moves with the target and the path leads to it. Every half second
  the body checks that its slot is still free and still on its side (within `SlotSweep`). If not,
  it picks again. It arrives within `SpotStop` of the slot, or sooner once it is within its stop
  distance of the target with nobody within `PersonalSpace` of where it is. With the ring full it
  stops in a second rank, `PersonalSpace` further out. On arrival it turns to the target, and a
  follower standing by keeps its slot and keeps turning to the target. Slots count as claims for
  `Taken`. `Attack` takes no slot: a blow has to get within `Reach`. `TargetSlots = false` turns
  slots off.
- **Passing.** A body standing or lying within 2 m ahead is passed on the side the walker is
  already on. If it is dead ahead, the walker passes on its right. The body a walker is going to
  (`Approach`, `Follow`, `Attack`) is not avoided, unless its slot is round the far side of it.
  Since 5.35.1, stationary people and stalled walkers within 2.8 m also trigger a short route
  beside and beyond them. That route persists until the walker is clear, so path pursuit does
  not pull it back into the queue. Both legs must be clear on the navmesh and of stationary
  bodies; the opposite side is tried if necessary. Tight passages keep the yielding rules.
  Moving traffic retains ordinary avoidance. The route expires after six seconds if unfinished.
- **Giving way.** At a door or in a passage, when two bodies meet head-on, one waits
  `YieldSeconds` (1.2 s) and looks again. The one already in the door keeps going. If neither is
  in it yet, the higher id waits.

`FindCover` also skips taken spots, so bodies hiding from the same threat hide apart. Set
`FruitBodyControl.SpreadOut = false` to turn off free spots, slots and the cover search's skipping.

## Footing (5.24.0)

The game's walk barely lifts the feet, so even a 2-3 cm ledge (a kerb, a threshold) can stop a
body dead: the physical foot stubs against it and the walk stalls. Two things now handle that
while a body walks under a smooth-walking order.

**The rig's grounder.** A body's muscles follow an animated rig, and that rig has a FinalIK
`GrounderIK` (`IKReferences.Grounder`). Every frame it casts under both feet and puts them on the
ground, looking ahead along each foot's motion. For as long as the walk lasts, it gets:

- FruitNav's layers (Default, Environment, Spawnable) added to its `layers`, so map ground on any
  of them counts,
- at least `WalkPrediction` (0.3) look-ahead, `WalkFootSpeed` (5) and `WalkMaxStep` (0.4 m).

The game's own values come back when the walk ends. The first body's settings are logged once,
before and while walking (`[FruitBodyControl] grounder ...`, VerboseLog). `TuneGrounder = false`
turns this off.

**The step assist** (`StepAssist`) now also lifts a foot over ledges from `MinStep` (1.2 cm). It
looks for risers just over the floor as well as 5 cm up, further ahead the faster the body goes.
Before, it took nothing under 4 cm and looked only 5 cm up, so a 3 cm kerb was invisible to it.

A body that gets stuck says what it is against. A ledge at its feet comes with its height:
`stuck 2.1 m short at (x, y, z), against a 3 cm ledge ("Kerb") 0.21 m ahead of its feet`.
Since 5.33.0, when there is nothing on the centre line it looks to the sides as well:
`beside it: "Door_Frame" (layer 8) 0.31 m to its left at 1.4 m` (a shoulder catching).

## Width-aware steering (5.33.0)

The navmesh keeps its edge 0.38 m from every wall, and shaped paths keep `PathMargin` beyond
that. A body is about 0.45 m across (a shoulder is 0.22 m off its centre), so on the line it
has about 15 cm to spare on the navmesh's edge. It was losing that at bends.

Pure pursuit heads for a point a lookahead (1.1-2.2 m) along the path. That chord cuts the
inside of every bend, and round a 90 degree corner it passes about half a metre inside the
path, which is where the path's margin was. So there are two guards (`NavWidthAware`, ini,
default on):

- **A cut guard.** The lookahead is shortened until the chord from the body to the point
  stays within the room the path has there: `allowed = clamp(room - CutSlack, MinCut, MaxCut)`
  (0.05, 0.06, 0.4 m). In the open it is 0.4 m, round a corner with the usual 0.3 m margin
  0.25 m, and in a door 0.06 m, where the body just follows the path (never under
  `MinLookAhead`, 0.5 m). It shortens at once and grows back at `LookRegrow` (1.5 m/s). A body
  well off the line also counts the offset as a cut, so it heads for a nearer point and rejoins.
- **Back onto the mesh.** The navmesh point under the body's smoothed centre is looked up each
  frame. When the centre is more than 4 cm outside the walkable area (inside the 0.38 m, with a
  shoulder about to catch), the heading gets a push back toward it, up to 0.9 at 15 cm out.

The agent radius itself stays 0.38: with 10 cm cells it is rounded up to whole cells, 0.42
would become 0.5 and close every 1 m door.

## Places

`FruitPlaces` holds named rooms, buildings, areas, spots and doors. Map mods register them from
markers with `FruitPlaces.ScanMarkers(source, root)` and drop them again with `Clear(source)`.
See `World/FruitPlaces.cs`.

Markers are empty GameObjects named:

- `Place|area|<name>|0`, `Place|building|<name>|0`, `Place|room|<name>|<storey>|<building>`. The
  box is the marker's position, rotation and local scale.
- `Place|spot|<name>|<storey>|<building>` (5.23.0). Leave the building empty outdoors.
- `Door|<building>|<name>|<storey>`. The marker's position is the middle of the opening at floor
  level, and its forward points out of the building. A name containing "front door", "back door"
  or "side door" marks a door to the outside.

### Spots (5.23.0)

A spot is somewhere to stand inside a room or area, named after what is there: "sofa",
"kitchen table", "behind the counter", "bus stop".

- **Marker:** the box is where a body stands, about 1.4 m square, and its forward faces the
  thing. If the marker is a child of an object with a Rigidbody, the spot moves with that
  object. It keeps its offset in the object's yaw and its own height, so a sofa knocked over
  still has its spot on the floor in front of it.
- **Room:** `In` is the spot's room or area, the smallest place holding it, worked out at scan
  time. `LookAt` is the point a body standing there faces.
- **Label:** `Short` is "the sofa" or "behind the counter". `Label` adds the room: "the sofa in
  the townhouse's living room".

How the queries treat spots:

| Query | Spots |
|---|---|
| `Where(point)` | never: a body by the sofa is in the living room |
| `SpotAt(point)` | the spot whose box holds the point, nearest middle first |
| `SpotsIn(place, near, list)` | a room's or area's own spots, or all of a building's; nearest first |
| `Near(point, r, list)` | left out; `Near(point, r, list, spots: true)` lists spots only |
| `FindDoor(said, near)` (5.24.0) | doors only: "the front door", "the shop's back door", "the doorway to the back room", "the exit". The building said, else the one `near` is in, comes first |
| `Find(said, near)` | matched by the spot's own name only. Since 5.24.0 a name of only short words ("TV") must be said in full; before, it matched anything, so "the front door" found a TV across the street. Saying its room or building too adds to the score ("the sofa in the living room"). Spots of the same name in the room, then the building, that `near` is in come first. A longer exact name wins: "behind the counter" beats "counter". |
| `BuildingAt`, `DoorsOf` | unaffected |

Crowds spread from a spot the same way they spread from a room's middle. A goto order to a
spot that is taken gets a free spot in the spot's room, because the spread uses `Where`.

## Diagnostics

FruitLib settings → Diagnostics:

- **Show navmesh and paths** (`NavDraw`) draws the navmesh within 50 m and every walking body's
  path. The path is yellow, orange where it is tight, and red where it is partial.
- **Pathing test** (`NavProbe`) draws the same. It also measures every goto order and writes all of
  this to `UserData/FruitNav.log`. The measurements are:
  - how far the smoothed centre strays off the path,
  - how much the pelvis sways across it,
  - the nearest wall at hip and shoulder height,
  - wall contacts, falls, back-offs and pivots,
  - turn rate and speed.

  The key (F5) does the following:
  - **F5** logs every body's path to the crosshair, plain and shaped.
  - **Shift+F5** sends every body there, with a trace of each body's walk.
  - **Ctrl+F5**, the first time, builds the walking course on open ground at the crosshair. It
    takes about 15 x 36 m ahead of you and to the right, and has:
    - corridors 1.8, 1.4, 1.1 and 1.0 m wide, with 90 and 45 degree bends,
    - a 1 m door,
    - ledges of 2, 4, 7 and 12 cm across the corridors (5.24.0),
    - a room with a hairpin chicane.

    Each later press gathers every body at the start and sends them through 7 s apart. Half of
    them walk with the 5.23 footing and half with the 5.24 footing, swapped on each run (5.21-5.23
    compared 5.20 and 5.21 walking). The table counts each body's step-assist lifts. The log gets a trace of
    each walk and a table comparing the two ways. **Ctrl+Shift+F5** removes the course. Turn off
    other mods' walking while it runs (NeuroFrukt: Actions → "Bodies walk, run and follow"),
    because their orders replace the course's.
  - **Alt+F5** is the stair and ramp climb test (spike S3). **Alt+Shift+F5** removes it.
