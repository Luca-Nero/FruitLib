# Ballistics

`FruitBallistics` flies rounds and detonates explosives for every mod, through the game's own
wound model. You register a spec once, spawn it by id, and hang your visuals and sounds off
its events. FruitLib owns the physics, the wounds, the pushes, and (for exit wounds) the
tissue ejecta; your mod owns what it looks and sounds like.

Added in **3.1.0**. Gate on it with `FruitVersion.Require("MyMod", 3, 1)`. Surface materials and
penetration, the `BeforeSurfaceHit` / `ProjectileStep` hooks, round owners and per-round data came
in **5.3.0** (`FruitVersion.Require("MyMod", 5, 3)`).

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
gravity, and any [`FruitForces`](utilities.md#fruitforces) fields in play. Drag is measured
against the air, so a [wind](utilities.md#fruitforces) drifts a light round further than a heavy
one. Wound power comes from kinetic energy (`PowerPerJoule`), so a round that has slowed down
wounds less, and it goes through bodies, loses power per voxel, and exits or stops the way the
game's own bullets do. Walls, crates and props are a separate question, answered by
[surfaces](#surfaces-and-penetration).

A spec you don't touch is a 7.62x39 FMJ (7.9 g, 7.62 mm, 715 m/s), and penetrates like one.

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
| `BallisticCoefficientG7` | G7 ballistic coefficient in lb/in² (7.62x39 ~0.14, .308 175 gr ~0.24, .50 BMG ~0.5). Above 0 it replaces `DragCoefficient` with the G7 curve, which climbs through the sound barrier. 0 = constant Cd |
| `PowerOverride` | Fixed muzzle power instead of energy × `PowerPerJoule` |
| `PenetrationScale` | Through surfaces, relative to an FMJ of the same mass and calibre: above 1 for a hard core, below 1 for soft points and lead shot, 0 = never goes through |
| `RicochetAngle`, `MaxBounces`, `RicochetEnergyLoss`, `RicochetScatter` | Glancing hits. `RicochetAngle` is the angle **on concrete**; other materials shift it |
| `PenetrationDeflect` | Random yaw on leaving a body or a surface, degrees |
| `WorldImpulse` | Push on a non-limb rigidbody for a round that stops dead in it at muzzle speed. Scaled by the speed the round actually lost there, so a round that goes through, or glances off, pushes less |
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

## Surfaces and penetration

Anything a round hits that isn't a body is a *surface*, and a surface is made of a
`SurfaceMaterial`. FruitLib decides what happens in this order:

1. **`BeforeSurfaceHit`** handlers may settle it ([below](#steering-a-hit)).
2. **Ricochet**, by chance. The critical angle is the spec's `RicochetAngle` plus the material's
   `RicochetAngleShift` (steel deflects earlier, wood and soil later), with the chance ramping up
   over ±4° around it, and only while the round has bounces left (`MaxBounces`).
3. **Penetration**, if the round has the energy. Otherwise it **stops**.

Penetration is the Poncelet model: a material resists with a strength term (it has to be crushed)
plus an inertial term (it has to be pushed aside), so how deep a round gets follows its sectional
density (mass over frontal area) and its speed, not just its energy. The far side is found by
casting back at the collider, so the real thickness of the wall decides whether the round comes
out. A round that gets through loses speed, scatters a little (`ExitScatter`), and pushes the
surface by the speed it lost. A wall thicker than the round can reach, or than the material's
`MaxThickness`, stops it.

Approximate depth of a full metal jacket at muzzle velocity, straight on:

| Material | 9 mm | 7.62x39 | .50 BMG | Solid beyond |
|---|---|---|---|---|
| Concrete | 2.5 cm | 7.1 cm | 16.2 cm | 50 cm |
| Brick | 3.7 cm | 10.1 cm | 22.7 cm | 50 cm |
| Steel | 0.2 cm | 0.9 cm | 2.4 cm | 10 cm |
| Wood | 12.9 cm | 36.2 cm | 82.4 cm | 100 cm |
| Drywall | 26.5 cm | 53.0 cm | 111.7 cm | 50 cm |
| Glass | 5.1 cm | 11.4 cm | 24.6 cm | 10 cm |
| Soil | 10.6 cm | 21.7 cm | 46.1 cm | 200 cm |
| Water | 45.1 cm | 74.0 cm | 148.8 cm | 200 cm |

Concrete and wood are calibrated against published box tests of 9 mm and 7.62x39; treat the rest
as starting points. "Solid beyond" is the material's `MaxThickness`: a surface thicker than that
is never probed, whatever the round.

A round that loses more than 15% of its speed getting through a surface starts **tumbling**: three
times the drag, half the sectional density at the next surface, and a wider wound in the next
body (no clean entry, more spread, and one voxel more cavitation radius if it has any).
`Projectile.Tumbling` and `Projectile.Penetrations` tell you.

### What a surface is made of

The game has no surface types of its own, so `FruitSurfaces` works it out. The first answer wins,
and it is cached per collider until the scene changes:

1. Resolvers you add with `AddResolver`, newest first.
2. Keywords found in the collider's object name, its parents' names (three levels up), or its
   renderer's material name. Your `AddKeyword` entries come first, in the order added, then the
   built-in ones (`brick`, `steel` / `metal` / `container`, `wood` / `plank` / `crate`,
   `glass`, `terrain` / `dirt` / `grass`, `concrete` / `asphalt`, and so on).
3. A terrain is Soil, a loose rigidbody is Wood (a prop), anything else is Concrete.

**The built-in keyword table is a starting guess, still to be calibrated with the probe against the
game's maps.** To see what a surface resolves to, turn on **Diagnostics > Log ballistics** in
FruitLib's settings and press the aim key (F6) at it. The log gives the material and the rule that
chose it, the thickness along your aim, and how far a 9 mm, a 7.62x39 and a .50 BMG get in.

```csharp
public override void OnInitializeMelon()
{
    // A new material. Names are shared between mods, so prefix yours.
    FruitSurfaces.Register(new SurfaceMaterial
    {
        Name = "MyMod.Ceramic",
        Density = 2400f,             // kg/m³, the inertial resistance
        Strength = 4e8f,             // Pa, how hard it is to crush
        RicochetAngleShift = -3f,    // deflects a little earlier than concrete
        MaxThickness = 0.05f,        // solid beyond 5 cm
    });

    // Objects with "tile" in their name (or a parent's, or their material's) are ceramic.
    FruitSurfaces.AddKeyword("tile", "MyMod.Ceramic");

    // Or decide from what you know about the collider. Return null for everything else.
    FruitSurfaces.AddResolver(c => c.name.StartsWith("MyMod.Plate") ? FruitSurfaces.Get("Steel") : null);
}
```

`Register` with an existing name replaces that material, so registering `"Wood"` retunes it for
every mod. A resolver's answer is cached per collider, so base it on the collider itself, not on
the shot; `RemoveResolver` takes the same delegate back out. The other `SurfaceMaterial` fields:
`Restitution` and `Grip` (how much of the speed into and along the surface a ricochet keeps),
`RicochetLossScale` (scales the spec's `RicochetEnergyLoss`), `ExitScatter` (degrees of random
deflection leaving the far side, scaled by the speed lost), and `Penetrable = false` for a
surface nothing gets through. `Depth(sd, speed)` and `ExitSpeed(sd, speed, thickness)` are public
if you want the same numbers for your own use; `FruitSurfaces.Resolve(collider)` gives the
material, and `Explain(collider, out why)` also the rule.

### Steering a hit

`BeforeSurfaceHit` fires when a round meets a surface, before anything is done about it. Set
`Outcome` to override the physics, or swap `Material` to change what it is made of:

| `SurfaceOutcome` | Effect |
|---|---|
| `Physics` (default) | FruitLib decides, as above |
| `PassThrough` | The round ignores this collider for the rest of its flight and carries on unchanged |
| `Stop` | The round ends here |
| `Ricochet` | It ricochets, even past `MaxBounces` or below the angle |
| `Handled` | You dealt with it: moved the round (`Position` / `Velocity`) or killed it. A round left where it was is stopped, since it would hit the same thing again |

A shield that lets its owner's rounds through and stops everyone else's:

```csharp
FruitBallistics.BeforeSurfaceHit += OnBeforeSurface;

void OnBeforeSurface(ref SurfaceDecision d)
{
    if (!d.Projectile.Spec.Id.StartsWith("MyMod.")) return;          // every mod's rounds arrive here
    if (!MyShields.TryGetOwner(d.Collider, out int shieldOwner)) return;
    d.Outcome = d.Projectile.Owner == shieldOwner ? SurfaceOutcome.PassThrough : SurfaceOutcome.Stop;
}
```

Handlers run in subscription order, and each sees what the one before left. The owner comes from
the spawn: `FruitBallistics.SpawnProjectile(id, origin, dir, owner)`, where 0 means this machine or
not said (a networking mod puts peer ids there; it is `BallisticsCommand.Owner` on the wire).
`SpawnExplosion` has the same overload, and `ExplosionInfo.Owner` carries it through.

## Steering a round

`ProjectileStep` fires for every round, every frame, before it moves, with the frame's `dt`. Set
`Position` or `Velocity`, scale `PowerScale` (wound power without changing the flight), or call
`Kill()`. For a plain acceleration a [`FruitForces`](utilities.md#fruitforces) field is simpler,
and composes with other mods'.

```csharp
FruitBallistics.ProjectileStep += OnStep;

void OnStep(Projectile p, float dt)
{
    if (!p.Spec.Id.StartsWith("MyMod.Seeker")) return;               // runs per round per frame: bail early
    if (p.Age > 6f) { p.Kill(); return; }

    Vector3 toTarget = MyTargets.Nearest(p.Position) - p.Position;
    p.Velocity = Vector3.RotateTowards(p.Velocity, toTarget, 25f * Mathf.Deg2Rad * dt, 0f);   // turns, keeps its speed
}
```

Speed is what a round's power follows, so slowing a round also weakens it. Keep your own state on
the round with `p.Set("MyMod.Fuse", 0.5f)`, `p.Get<float>("MyMod.Fuse")` and `p.Has(...)`: several
mods can each hang data on the same round without fighting over `Tag`.

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
FruitBallistics.SurfaceHit  += OnSurface;   // SurfaceHitInfo: collider, point, normal, material, outcome, penetrated, exit, speeds
FruitBallistics.LimbWounded += OnWound;     // WoundInfo: limb, entry, exit, power in/out, exited
FruitBallistics.Exploded    += OnExploded;  // ExplosionInfo: spec, origin, ground hit, owner
```

| Event | Fires |
|---|---|
| `ProjectileSpawned` / `ProjectileEnded` | A round starts or stops existing |
| `ProjectilesMoved` | Once a frame, after every round has moved: **update tracers here**, not in your own `OnUpdate`, or they lag a frame (12 m at 700 m/s) |
| `ProjectileStep` | Every round, every frame, before it moves: steer it ([above](#steering-a-round)) |
| `BeforeSurfaceHit` | A round met a surface, before it is decided ([above](#steering-a-hit)) |
| `SurfaceHit` | A round struck something that isn't a body, once it is decided. `Ricocheted` and `Penetrated` say which; a penetration also has `Exit`, `Thickness`, `SpeedIn` and `SpeedOut`, so you can mark both sides of a wall |
| `LimbWounded` | A round or explosion fragment wounded a limb (`Projectile` is null for fragments). Overpressure wounds raise nothing |
| `Exploded` | A detonation happened |
| `DebrisArc` | One sampled fragment's flight, for debris visuals |

**Every mod hears every event.** Draw only your own: filter on the spec id prefix.

```csharp
void OnSurface(SurfaceHitInfo hit)
{
    if (!hit.Projectile.Spec.Id.StartsWith("MyMod.")) return;
    SpawnSpark(hit.Point, hit.Normal);
    if (hit.Penetrated) SpawnSpark(hit.Exit, -hit.Direction);
}
```

`ProjectileStep` and `BeforeSurfaceHit` change what happens rather than just observing it, so the
same rule applies twice over: return early for rounds that aren't yours.

`Projectile.Tag` is free for you, e.g. to find your own tracer object. `Projectile.Cosmetic`
is true for a shot drawn on this machine that another machine is simulating (below). Draw
it, but don't count it.

## Multiplayer

Designed in from the start, so nothing that fires has to know the network exists. A
spawn is a `BallisticsCommand` (spec id, origin, direction, seed, owner), and that's all that
travels.

- A client sets `FruitBallistics.Intercept` to forward its commands to the host instead of
  simulating them.
- The host listens to `FruitBallistics.Issued` and broadcasts every command it ran for real.
- Clients call `FruitBallistics.Execute(cmd, cosmetic: true)` to draw them. Wounds arrive through
  the voxel sync that already exists.

Only a networking mod touches these. Everyone else just calls `SpawnProjectile`.

## Player settings

Under **Ballistics** in FruitLib's settings (and `FruitLibConfig.ini`): whether rounds go through
walls at all (`WallPenetration`) and a multiplier on how well they do (`PenetrationScale`, 1 =
physical), exit-wound ejecta and its thresholds, chunk count and speed, blood decals, and a target
frame rate below which the oldest chunks and splats are cleared early. They apply to every mod's
rounds, so don't build on penetration being on: with it off, a round only ricochets or stops.
