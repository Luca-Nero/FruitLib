# Poses

Hold a body in a pose (crouched, kneeling, hands up, your own) apart from what the game
thinks of its stance. Since 5.31.0.

```csharp
FruitPose.Hold(body, FruitPoseShape.Crouch);                 // blends in over DefaultBlend (0.45 s)
FruitPose.Hold(body, FruitPoseShape.Kneel, blendSeconds: 0.8f, tension: 1.2f, support: 1f, source: "MyMod");
FruitPose.Release(body);                                     // blends back to the animation
FruitPose.Ended += (body, shape, why) => { ... };           // "released", "knocked", "fell", "left its feet", "went limp", "gone"
```

`Hold` fails (returns false) if the body isn't standing in the game's move state: down,
getting up or in agony. Asking for another pose while one is held blends from one to the
other. A pose on the legs stops a walking order, and a walking order started while one is
held makes it give way (0.25 s).

## Why poses are separate from the stance

In the game one number does two jobs. The puppeteer's Balance rises while a ray along the
pelvis's own down axis finds the floor and drops fast (12/s) when it doesn't. A pelvis
tilt of 26 degrees or more caps it at 62.5. That same Balance is the strength of the
world-anchored drive that holds the pelvis upright. GeneralMuscleForce, which feeds every
limb's joint drive, sags the same way while "not grounded". A separate check drops the body
into the fall state once the pelvis leans past about 37 degrees. So a body that bends down
to the floor loses its hold and its muscle, and folds.

A held pose splits that into three things set apart:

| | What it is | Set by |
|---|---|---|
| **Pose** | per-bone targets the muscles follow | the shape, through the game's own rig override layer (the one the agony writhe uses) |
| **Tension** | a multiple on every limb's joint drive | `tension` (0-2). The game's muscle value is multiplied by it, so a wounded or bloodless limb stays weak |
| **Support** | how hard the pelvis is held upright | `support` (0-1), written where the puppeteer's Balance goes. A lost leg still costs what it costs |

While a pose is held, the body isn't judged fallen over its tilt. It is only dropped back
to the game if it is knocked fast (`KnockedVelocitySqr`), or if it lies over past
`FallenUpDot` for `FallenSeconds`. The rig's foot placement is held off while the legs are
posed, because it would pull bent legs straight under a rig that still stands at full height.
Leaving the move state drops the pose at once, before the game's agony wants those bones.

## Momentum (5.31.1)

A physical body thrown into a pose quickly carries on past it. Three things keep it steady:
- **Pace:** no bone turns faster than `DegreesPerSecond` (160). A blend asked for quicker
  than that takes longer.
- **Damping:** while posed, joint damping is `Damping` (2) times the game's full-strength
  value, so limbs settle instead of swinging past their targets.
- **Balance assist** (`BalanceAssist`): a pull on everything above the knees keeps the
  centre of mass over the feet, and over a shin lying at their level when kneeling. It works
  against both the drift and its speed, up to `AssistMax` (3.5 m/s²) times the support. Within
  `AssistSlack` (4 cm) of the support's middle, only the speed is damped. The game holds the
  pelvis's rotation; this holds its place. After a release it runs on for up to
  `SettleSeconds` (0.8 s) while the body rises, with the stance still held.

## Shapes

A shape is a list of turns. Each turn moves a bone's direction from one body-frame
direction (x right, y up, z forward) toward another by some degrees. The turn is about the
axis that was that body axis in the parent bone when the pose began, so a knee bends about
the thigh's own hinge however the thigh is turned. Every turn starts from where the
animation has the bone.

```csharp
var wave = new FruitPoseShape("wave")
    .Bend(FruitBone.RightUpperArm, Vector3.down, Vector3.right, 80f)    // arm out to the side
    .Bend(FruitBone.RightForearm,  Vector3.down, Vector3.right, 90f);   // forearm up

var squat = new FruitPoseShape("squat")
    .BendBoth(FruitBone.RightThigh, Vector3.down, Vector3.forward, 95f) // mirrored on the left
    .BendBoth(FruitBone.RightShin,  Vector3.down, Vector3.back, 135f)
    .BendBoth(FruitBone.RightFoot,  Vector3.down, Vector3.forward, 40f); // back to flat
```

Built in:
- `Crouch`: a deep squat, leaning in, low enough to reach the floor.
- `Kneel`: down on the right knee.
- `HandsUp`: both hands raised.
- `Steady`: no bones, only the stance taken over.

The hand IK (reaching, carrying, aiming) still runs after the pose, so arms can reach from a
crouch.

## Picking up

`FruitBodyControl.PickUp` crouches for an item more than `CrouchBelow` (0.3 m) below the
pelvis. The hand then has to come within `CrouchMagnet` (0.3 m) of the grip before the item
is pulled in, and the body stands back up as it takes it. Turn it off with
`FruitBodyControl.CrouchToPickUp = false`.
