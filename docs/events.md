# World events and bodies

*Since 5.12.0. Actor/cause, shot attribution, `FruitBodyControl` and `FruitInventory.HeldName` since 5.13.0. Running, following, approaching and strikes between bodies since 5.14.0; strikes with the hands since 5.15.0.*

Two halves that work together:

- **`FruitEvents`**: a bus for things that happen in the world. FruitLib, the game and any mod
  publish to it; any mod listens. Each event with a position is tagged with the tracked bodies
  near it, so a publisher never needs to know where anyone is.
- **`FruitBodies`**: every humanoid in the scene with its vitals, behaviour state, posture and
  limbs, sampled a few times a second from the game's own LVA values. Changes in those values
  become events too (fell, agony, lost a limb, bleeding, died).

## Listening

```csharp
FruitBodies.Require("MyMod");          // once, at init: turns body tracking on (off by default, costs nothing then)

FruitEvents.Raised += e =>
{
    if (e.Kind == "Landed" && e.Concerns(someBody.Id, out float metres)) { ... }
};
```

Handlers run on the main thread from FruitLib's `OnUpdate`, a frame or less after the event was
published. An event raised inside the game's physics is never handled inside it. A handler that
throws is logged once and keeps receiving.

`FruitEvents.CopySince(seq, list)` reads the last 256 events without subscribing.

## Publishing

```csharp
FruitEvents.Publish("MyMod", "Landed", "frag grenade landed", position,
                    radius: 12f,        // bodies within 12 m get it in e.Near
                    magnitude: 0.8f);   // 0..1: how hard it is to ignore
```

| Field | Meaning |
|---|---|
| `Source` | `"Game"`, `"FruitLib"`, or your mod's name |
| `Kind` | One PascalCase word: what happened |
| `Text` | One readable line. Write it so it reads on its own in a log |
| `Position`, `Radius` | Where, and how far away it still matters (0 = 15 m) |
| `Body`, `Limb` | Who it happened to (`FruitBody.Id`, `FruitLimb.Index`), if anyone |
| `Magnitude` | 0..1 salience |
| `Detail` | Free text: numbers, ids |
| `Actor` | Who did it: `"player"` for anything the player caused, `"body#3"` for a tracked body (since 5.14.0; read the id back with `e.ActorBody`, build one with `FruitEvents.BodyActor(id)`), empty when nobody did |
| `Cause` | With what, in plain words: `"Viper 17"`, `"frag grenade"` |
| `Near` | Filled in by FruitLib: bodies within `Radius`, nearest first |

Publish from the main thread only, because `Time.time` is read there. To stay compatible with a
FruitLib older than 5.12, put the call in its own `[MethodImpl(NoInlining)]` method behind
`FruitVersion.AtLeast(5, 12, 0)`. BombsAway's `Core/WorldEvents.cs` shows the pattern.

## What FruitLib raises

| Source | Kind | When |
|---|---|---|
| Game | `Appeared`, `Gone` | A body starts or stops being tracked |
| Game | `Shot` | A pain spike within 0.5 s of a native gunshot, on a body in the gun's line of fire. Actor `player`, cause the gun |
| FruitLib | `Explosion`, `Wounded`, `NearMiss` | A FruitBallistics detonation, a body hit by its round or fragments, a round striking within 1.2 m of a head. Actor `player`, or `body#<id>` when the spawn named an instigator (5.34.0, [ballistics.md](ballistics.md)) |
| Game | `PainSpike` | Any other pain spike the game sent the body (hits within 0.25 s are pooled; the event carries the strongest) |
| Game | `Flung`, `Slammed` | The body left the ground faster than 6 m/s (a blast, or the player's hand), and hit it again |
| Game | `Fell`, `Agony`, `Down`, `GettingUp`, `Recovered` | The body's behaviour state changed |
| Game | `LimbLost` | A limb is no longer attached |
| Game | `Bleeding` | A limb started bleeding |
| Game | `BloodLoss` | Blood fell below 75 / 50 / 25 % |
| Game | `Unconscious`, `Conscious` | Cognition fell to 25 / rose past 40 |
| Game | `Died` | Cognition reached 0 (the game counts the kill off this alone). `Actor` is whoever last hurt the body within two minutes (a wound, a shot or a blow: `player` or `body#<id>`), empty if nobody is known (5.34.0; `FruitBody.LastHarmedBy`) |
| Game | `Gunshot` | A native gun fired (bursts pooled): at its muzzle, radius 60 m. Hooked on `Firearm.ShootLogic`; `ShotBus.Open` is inlined and never fires |
| *spec owner* | `Explosion` | A FruitLib explosion (radius scales with the charge) |
| *spec owner* | `Wounded` | FruitLib rounds or fragments wounded a body (pooled per 0.2 s) |
| *spec owner* | `NearMiss` | A FruitLib round struck within 1.2 m of a living head |
| *caller* | `Attacked` | A `FruitBodyControl` strike landed. `Body` is the victim, `Actor` `body#<attacker>`, `Cause` `punch` or `shove`, `Limb` the limb struck. The victim's pain spike from the blow is folded into it (no separate `PainSpike` within 0.6 s) |
| *caller* | `SwungAt` | A strike whose victim got out of reach during the wind-up. Same fields as `Attacked`, no limb |

*Spec owner* is the part of the spec id before the dot: `BombsAway.Frag` → `BombsAway`. *Caller* is the `source` the mod passed to `Strike`/`Attack` (default `FruitLib`).

## Bodies

`FruitBodies.All` holds every tracked body. A `FruitBody` carries:

- **Vitals** (the game's LVA parameters, 0..100): `Cognition` (consciousness; 0 = dead),
  `Pain`, `Nociception`, `Blood` (percent left), `Balance`, `Muscle`; `Alive`, `Conscious`.
- **Behaviour** from the game's humanoid state machine: `Standing`, `Moving`, `Falling`,
  `Agony`, `Lying`, `GettingUp`, `Dead`, plus `AgonyStage` (Slackening / Writhing / Leaving /
  HandingOff), `AgonyDepth`, `AgonyLimb` and `StateName` (the raw state classes, for debugging).
- **Posture**: `Upright`, `OnBack`, `FaceDown`, `OnSide`, `Airborne`; `Grounded`, `Speed`,
  `Position` (pelvis), `HeadPosition`, `Facing`.
- **Limbs**: fifteen `FruitLimb`s in the game's order (pelvis, left thigh ... right hand), each
  with `Present`, `Pain`, `Wholeness`, `Innervation`, `Bleeds`, `Position`.
- **The last pain spike**: `LastSpikeTime`, `LastSpikeIntensity`, `LastSpikeLimb`.
- **The last blow from another body** (5.14.0): `LastStruckBy` (body id), `LastStruckAt`.

`Describe()` returns all of it as a few lines of text. `Bearing(point)` returns a point relative
to the body, for example "3.2 m behind you to the left".

Lookups: `Get(id)`, `Of(creature)`, `OfObject(gameObject, out limb)` (a limb collider or
anything under a limb), `Nearest(point)`. The player's view is in `ViewPosition` / `ViewForward`.

A body's `Id` follows the game's puppeteer. If the body is cut apart and the game hands the
subject to another piece, the id stays the same and `Creature` changes.

Everything is read, never written. A value that moves in a game update is reported once in the
log and stops updating. It does not stop the sample.

## Making bodies walk, run, follow and fight

```csharp
FruitBodyControl.WalkAway(body, dangerPosition, seconds: 4f);
FruitBodyControl.Walk(body, worldDirection, seconds, run: true);       // run overloads since 5.14.0
FruitBodyControl.Approach(body, other, stopDistance: 1.4f, seconds: 12f);  // ends on arrival
FruitBodyControl.Follow(body, other, stopDistance: 1.4f, seconds: 20f);    // stays with it
FruitBodyControl.Attack(body, other, FruitStrike.Punch, seconds: 12f, run: true, source: "MyMod");
FruitBodyControl.Strike(body, other, FruitStrike.Shove, "MyMod");      // now, if within Reach
FruitBodyControl.Stop(body);
```

Queries: `IsWalking`, `IsRunning`, `IsAttacking`, `TargetOf`, `Heading`, `Distance(a, b)`.

This drives the game's own locomotion. It places a walk request on the puppeteer's
`WalkInteraction`, the same one the game's test walking uses, and steers the target yaw and
move direction every LateUpdate. A body only walks while it is on its feet. One that is down
gets up by itself first, then walks.

**Going to a body.** `Approach`, `Follow` and `Attack` re-aim at the other body's live pelvis
every frame. `Follow` stands by while within `stopDistance` and sets off again once the other is
half a metre further away.

**Running.** The game has no run: its locomotion blend tree is Idle plus eight `Moving_*` clips
at walking pace, and `HumanoidMoveControl` rewrites the clip-speed parameter every FixedUpdate.
A run is the puppeteer's Animator played `RunAnimationSpeed` (1.6) times faster while the body
is on its feet, so the steps come quicker and the physical body follows as well as its balance
allows. Expect more stumbling than a walk. Stand-up and agony animations play at normal speed.

**Strikes** (reworked in 5.15.0). The game has no melee either, so a blow uses three of its own
parts:

- **The attacker's hands** go through the hand IK the game's wound covering uses
  (`IKReferences.Left/RightHandEffectorPlacer`). A punch chambers the hand by the shoulder for
  `WindUp` (0.15 s), then darts it through the struck point at `PunchHandSpeed` (5; the covering
  flies at 1). A shove puts both hands on the chest at `ShoveHandSpeed` (3). Targets are placed
  against the IK rig's pelvis, because the physical body copies the rig's pose. The right hand
  punches unless the covering is holding it to a wound.
- **The blow lands** when the hand reaches the point, or after `MaxExtendTime` (0.35 s). If the
  victim is no longer within `Reach` + 0.35 m it is a `SwungAt` instead.
- **The victim flinches** through its own FinalIK hit reaction on the struck limb, the same hit
  points a pain spike drives (`PunchReaction` 1, `ShoveReaction` 1.5).
- **The push** is scaled by the victim's real mass (its limb rigidbodies together): `PunchSpeed`
  (0.6 m/s of whole-body speed; 70 % into the struck limb, 30 % into the torso) and `ShoveSpeed`
  (1.8 m/s; 55 % torso, 45 % pelvis). The fixed 5.14.0 impulses were absorbed by the balance.
- **A punch also bruises**: `PunchBruise` (35) per voxel on a small surface patch, through the
  game's damage signal. In testing this produced no pain on its own.

The `Attacked` event's `Detail` carries `impulse`, `mass`, `reach`, `hands` (`ik` or `none`) and
`flinch` (`yes` or `no`), for checking each part in the log.

`FruitBodyControl.PlaceHand(body, left, worldPoint, speed)` and `ReleaseHand(body, left)` expose the
hand IK on its own.

Every value here is a public static field. `strength` scales impulse and bruise per call.

`FruitInventory.HeldName` is what the player has in hand ("Viper 17", "Cutter", a mod item's
name), or empty.

## Diagnostics

FruitLib settings → Diagnostics:

- **Log world events to file**: `UserData/FruitEvents.log`, every event plus a snapshot of every
  body every 5 s (`EventLogSnapshotSeconds`).
- **World events panel**: a HUD panel with a line per body and the newest events.

Either one turns body tracking on while it is set. `BodySampleHz` (ini only, default 5) sets how
often each body is read.
