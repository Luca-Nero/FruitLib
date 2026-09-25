# Ballistics

`FruitBallistics` flies rounds and detonates explosives for every mod, through the game's own
wound model. You register a spec once, spawn it by id, and hang your visuals and sounds off
its events. FruitLib owns the physics, the wounds, the pushes, and (for exit wounds) the
tissue ejecta; your mod owns what it looks and sounds like.

Added in **3.1.0**. Gate on it with `FruitVersion.Require("MyMod", 3, 1)`.

## A round

```csharp
public override void OnInitializeMelon()
{
    // id, mass (g), calibre (mm), muzzle velocity (m/s), drag coefficient
    FruitBallistics.Register(ProjectileSpec.Cartridge("MyMod.Rifle", 7.9f, 7.62f, 715f, 0.29f));
}

void Fire(Vector3 muzzle, Vector3 forward)
{
    Projectile p = FruitBallistics.SpawnProjectile("MyMod.Rifle", muzzle, forward);
    // null if the id is unknown, or a networking mod intercepted the shot
}
```

Flight is real external ballistics: quadratic air drag from mass, calibre and `DragCoefficient`,
gravity, and any [`FruitForces`](utilities.md#fruitforces) fields in play. Wound power comes from
kinetic energy (`PowerPerJoule`), so a round that has slowed down wounds less, and it goes
through bodies, loses power per voxel, and exits or stops the way the game's own bullets do.

**Presets:**

| Call | Gives you |
|---|---|
| `ProjectileSpec.Cartridge(id, g, mm, m/s, Cd)` | A real cartridge, wound values scaled to its energy |
| `ProjectileSpec.Native762(id)` | The game's own 7.62, value for value, read from the running game |
| `ProjectileSpec.Native9mm(id)` | The game's own 9 mm |

Everything on the spec is a public field; tweak after creating it. The ones worth knowing:

| Field | What it does |
|---|---|
| `Lifetime`, `GravityScale`, `ExternalForces` | Flight: seconds before it's culled, gravity multiplier, whether `FruitForces` bend it |
| `PowerOverride` | Fixed muzzle power instead of energy × `PowerPerJoule` |
| `RicochetAngle`, `MaxBounces`, `RicochetEnergyLoss`, `RicochetScatter` | Glancing hits off hard surfaces |
| `PenetrationDeflect` | Random yaw on leaving a body, degrees |
| `WorldImpulse` | Push on non-limb rigidbodies at full power |
| `Wound` | A `WoundProfile`: how it tears through tissue (below) |

`spec.Clone()` copies one, e.g. to make a subsonic variant.

### WoundProfile

Maps onto the game's own bullet constants, in voxel steps (a human voxel is ~23 mm):
`CleanEntryDepth` (the neck before the channel spreads), `SpreadChance`,
`CavitationPeakRadius` / `CavitationDamage` (the temporary cavity), `TearMinRadius` /
`TearMaxRadius` / `ExitTearDamage`, `MaxDepth`, `ImpactImpulse` on the struck limb, and
`HardTissueScale`: how much extra bone costs. Use 0.1 for jacketed rounds and fragments, and 1
for soft lead such as buckshot, which flattens on bone. `Ejecta` turns exit-wound chunks and
blood on or off for this round (the player can also switch them off globally).

## An explosion

```csharp
FruitBallistics.Register(new ExplosionSpec
{
    Id = "MyMod.Grenade",
    BlastRadius = 6f, FragCount = 2000, FragPower = 2700,
    HSpreadDeg = 360f, VSpreadDeg = 360f,     // less than 360 makes a cone along `forward`
});

FruitBallistics.SpawnExplosion("MyMod.Grenade", position, forward);
```

The physics and wounds of BombsAway's detonation: a blast push, an overpressure shell that
wounds what's close, and fragments that fly real arcs and wound through the native model with
power falling off over distance. `MaxWounds` caps the wounds per detonation, and
`AdaptiveQuality` scales fragment counts down under [frame pressure](perfmon.md).

## Events: where your visuals go

```csharp
FruitBallistics.SurfaceHit  += OnSurface;   // SurfaceHitInfo: collider, point, normal, incidence, ricocheted
FruitBallistics.LimbWounded += OnWound;     // WoundInfo: limb, entry, exit, power in/out, exited
FruitBallistics.Exploded    += OnExploded;  // ExplosionInfo: spec, origin, ground hit
```

| Event | Fires |
|---|---|
| `ProjectileSpawned` / `ProjectileEnded` | A round starts or stops existing |
| `ProjectilesMoved` | Once a frame, after every round has moved: **update tracers here**, not in your own `OnUpdate`, or they lag a frame (12 m at 700 m/s) |
| `SurfaceHit` | A round struck something that isn't a body |
| `LimbWounded` | A round or explosion fragment wounded a limb (`Projectile` is null for fragments). Overpressure wounds raise nothing |
| `Exploded` | A detonation happened |
| `DebrisArc` | One sampled fragment's flight, for debris visuals |

**Every mod hears every event.** Draw only your own: filter on the spec id prefix.

```csharp
void OnSurface(SurfaceHitInfo hit)
{
    if (!hit.Projectile.Spec.Id.StartsWith("MyMod.")) return;
    SpawnSpark(hit.Point, hit.Normal);
}
```

`Projectile.Tag` is free for you, e.g. to find your own tracer object. `Projectile.Cosmetic`
is true for a shot drawn on this machine that another machine is simulating (below). Draw
it, but don't count it.

## Multiplayer

Designed in from the start, so nothing that fires has to know the network exists. A
spawn is a `BallisticsCommand` (spec id, origin, direction, seed), and that's all that travels.

- A client sets `FruitBallistics.Intercept` to forward its commands to the host instead of
  simulating them.
- The host listens to `FruitBallistics.Issued` and broadcasts every command it ran for real.
- Clients call `FruitBallistics.Execute(cmd, cosmetic: true)` to draw them. Wounds arrive through
  the voxel sync that already exists.

Only a networking mod touches these. Everyone else just calls `SpawnProjectile`.

## Player settings

Under **Ballistics** in FruitLib's settings (and `FruitLibConfig.ini`): exit-wound ejecta and
its thresholds, chunk count and speed, blood decals, and a target frame rate below which the
oldest chunks and splats are cleared early. They apply to every mod's rounds.
