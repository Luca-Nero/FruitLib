# Gait

How a body walks, apart from where it walks. Since 5.32.0 (diagnostics only; the one
behaviour change, `PersistentYaw`, is off by default).

## What drives a walk (Release, Ghidra 2026-10-11)

- **Nothing pushes the body.** `HumanoidMoveControl` only computes animator parameters: the
  blend-tree direction (`ActualAnimationDirection`, lerped toward `TargetMoveDirection` at
  9/s) and a clip speed (`moveSpeed`) taken from the pelvis's *measured* velocity squared.
  The body moves because the legs it animates push on the floor.
- **Friction picks the pushing foot.** `CalculateDominateLeg` compares both feet's per-frame
  movement with the animation direction (turned by the COM transform's yaw). The foot moving
  with the direction gets zero friction (swinging), the other gets the default (stance).
  `FootFrictionControl` swaps the physics materials every step.
- **Balance coefficient.** `min(|dv^2|/cap, 1) + 0.65 * rotationFactor`. While it is high,
  the animation direction is pulled back toward the direction at the last activation, so a
  body that is stopped or turned keeps stepping its old way until momentum has gone.
- **Yaw.** `DynamicYTargetRotation` writes the pelvis's own yaw into `PuppeteerTargetYRot` every
  Update, but only while nothing walks: the first gait run showed the target staying put while a
  walking pelvis moved (so a command built as "turn rate x frame time from the target" does
  integrate, and `PersistentYaw` makes no difference).

## What the first run found (2026-10-11, FruitLib 5.32.0, one body, 9 m line)

- **Steering is not the cause.** raw (nothing steering it), steer and yaw look the same:
  speed, lateral sway (2-3 cm at the stride frequency, natural), foot lag behind the rig (10-40 ms,
  corr 0.94-0.95) and foot skating in stance (0.1-0.3 m/s). While a body walks,
  `PuppeteerTargetYRot` is *not* rewritten to the pelvis's yaw each Update (in raw it stayed
  constant while the pelvis moved), so the "yaw carrot" worry was wrong; `PersistentYaw` changes
  nothing and stays off.
- **The pelvis rings in yaw at 3.2 Hz,** 2.2 degrees amplitude (7-10 degrees peak to peak), three
  times the stride frequency (1.06 Hz) and six times the yaw the stride itself causes (0.3
  degrees). Same in raw. This is the visible wobble.
- **The walk lurches on starting:** speed peaks at 2.1-2.6 m/s about a second in, falls to 0.7-1.0,
  then settles at 1.5. Cadence stays constant (0.94 s a stride) while speed changes by a factor of
  three, so the clip speed (`moveSpeed` 0.70-0.79) does not follow the body.
- Not measurable yet: where the 3.2 Hz comes from (the rig or the pelvis's yaw drive). 5.32.0's
  second version logs the rig pelvis, torso and head yaw to tell.

## What the second run found (same day, 14 m lines, raw / +D / +R / +DR / steer+DR x2)

- **The yaw damper does nothing.** 16 rad/s² rms of damping torque on the pelvis left the
  3.2 Hz yaw wag where it was (2.3 degrees amplitude, fast yaw rate 0.61 -> 0.61 rad/s rms). The
  ringing is driven by the gait, not a free oscillation of the pelvis a torque can damp.
- **The start ramp does nothing to the lurch.** The move direction did grow 0.4 -> 1 (and the
  animation direction followed), the speed peak (2.3 m/s) only came later. It is a transient
  of the friction propulsion, not of the command's size.
- **The twist grows up the body, all in phase:** pelvis 2.3, torso 3.4, head 3.6 degrees at 3.2 Hz
  (correlation 0.97). The spine passes it up and enlarges it.
- The steer runs (our GoTo, with its footing changes) peaked a little lower (1.8 against 2.1-2.4
  m/s); only two runs, not conclusive.
- `YawDamping` and `StartRampSeconds` stay off. They are kept for now so the probe can show
  that again if the gait changes; they can go.

## Turning on the spot (5.32.0)

The game has no turn-in-place animation: the controller is Idle plus eight Moving_* clips. A
standing body given a new yaw turns its pelvis with the feet planted, so they drag.
`FruitBodyControl.StepTurns` (config "Step when turning on the spot", on since the run below)
makes a turn of more than `StepTurnFrom` (30 degrees) also hold a walk request at a small
forward move direction (`StepTurnPace`, default 0.6) while the yaw sweeps: the body walks a
tight arc on its own steps, and lets go within `StepTurnStop` (15 degrees) of the heading. It
applies to `Face`, `LookAround` and the pivots of a walk.

First turn run (2026-10-11, 120-degree turns, body #2):

| | time | feet lifted (cm) | steps | drift |
|---|---|---|---|---|
| drag (game's way) | 3.4-4.5 s, yaw rate decaying 1.5 -> 0.1 rad/s | 1-5 | 0-1 | 12-23 cm |
| pace 0.25 | 1.3 s | 2-5 | 0 | 17-23 cm |
| pace 0.40 | 1.2 s | 3-6 | 1 | 10 cm |
| pace 0.60 | 1.2 s | 8-9 | 3 | 19 cm |

The "feet slid" figure is mostly the arc a foot has to travel about the pelvis (about 50-60 cm
per 90 degrees for both), so it cannot tell dragging from stepping; the lift can. Next run adds
pace 0.8.

## The probe

`GaitProbe` (Diagnostics) + `GaitProbeKey` (Home). On the key the body under the crosshair
walks straight ahead of where it faces, up to 14 m, from standing, then turns round and does it
again:

| condition | what it is |
|---|---|
| stand | 4 s standing still: the sway baseline |
| raw | a walk request and nothing else: the game's own gait |
| raw+D | with the pelvis's fast yaw damped (`FruitBodyControl.YawDamping`) |
| raw+R | with the start eased in (`StartRampSeconds`) |
| raw+DR | both |
| steer+DR | `FruitBodyControl.GoTo` to a point straight ahead, with both |

Each runs twice. Every physics step goes to `UserData/FruitGait.csv` (frame: f ahead, l right, y
up, from the run's start): pelvis and both feet (physical `lf*/rf*`), the animator's own armature
(`o*`) and the armature the muscles follow (`r*`), yaw of the pelvis, rig, torso and head, yaw
rate, the yaw command, the move direction asked for and given, animation speed, balance
coefficient and the damping torque. A one-line summary per run goes to the console.

Analyse with `Frukt/Tools/gait_analyze.py FruitGait.csv`.

Turn NeuroFrukt's "Bodies walk, run and follow" off while it runs.

## The two switches

- `YawDamping` (default 0, off): a torque on the pelvis against its yaw rate with the slow part
  (`YawDampingTau` 0.35 s, an intended turn) taken out. 28 is about a damping ratio of 0.7 at 3 Hz.
- `StartRampSeconds` (default 0, off): the move direction asked for grows from `StartRampFrom`
  (0.4) to 1 over this long.
