# Ballistics

`FruitBallistics` flies rounds and detonates explosives for every mod, through the game's own
wound model. You register a spec once, spawn it by id, and hang your visuals and sounds off
its events. FruitLib owns the physics, the wounds, the pushes, and (for exit wounds) the
tissue ejecta; your mod owns what it looks and sounds like.

Added in **3.1.0**. Gate on it with `FruitVersion.Require("MyMod", 3, 1)`. Surface materials and
penetration, the `BeforeSurfaceHit` / `ProjectileStep` hooks, round owners and per-round data came
in **5.3.0** (`FruitVersion.Require("MyMod", 5, 3)`). Fragments that ricochet and go through walls,
blast cover, and the `FragmentTraced` / `BlastTraced` events came in **5.4.0**
(`FruitVersion.Require("MyMod", 5, 4)`, or `FruitGate.Check("MyMod", 5, 4, 0)` from the
[FruitGate template](../Templates/README.md)). Per-part switches for explosions
(`ExplosionSpec.Features`, the `SpawnExplosion` mask) came in **5.5.0**.

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

Rounds in flight are capped, every mod's together, at `MaxLiveRounds` in `FruitLibConfig.ini` (4096; a fixed 512 before 5.9.0). Firing past it ends the oldest round. A flechette rocket throws over a thousand darts at once, which is why the cap went up; a mod that fires that many in one go can set `Velocity` on each round after spawning it (its power follows the speed), so one spec covers darts thrown at different speeds.

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
`MaxThickness`, stops it. A far-side hit whose normal faces against the travel is not an exit but
the entry face seen from behind (a one-sided mesh, or terrain hit from below), and is rejected, so
a round cannot pass through a surface of no thickness.

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
surface nothing gets through, and `BlastTransmission` ([below](#blast-cover)). `Depth(sd, speed)` and `ExitSpeed(sd, speed, thickness)` are public
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
    BlastRadius = 6f, FragCount = 2000, FragPower = 2700, FragMassGrams = 2f,
    HSpreadDeg = 360f, VSpreadDeg = 360f,     // less than 360 makes a cone along `forward`
});

FruitBallistics.SpawnExplosion("MyMod.Grenade", position, forward);
```

The physics and wounds of BombsAway's detonation: a blast push, an overpressure shell that
wounds what's close, and fragments that fly real arcs and wound through the native model with
power falling off over distance. `MaxWounds` caps the wounds per detonation, and
`AdaptiveQuality` scales fragment counts down under [frame pressure](perfmon.md). Big cased
charges can aim their fragments at the limbs in reach instead of flying them blind
([targeted fragments](#targeted-fragments-and-the-side-spray-belt-580)).

### Choosing what an explosion does (5.5.0)

Every part of a detonation has a switch in `ExplosionSpec.Features` (an `ExplosionFeatures`
flags enum, all on by default). A part that is off is skipped outright, whatever the spec's
numbers say:

| Flag | Part |
|---|---|
| `Shockwave` | The push on rigidbodies |
| `Overpressure` | Blast wounds on limbs in range |
| `OrganInjury` | With a charge (`ChargeKgTNT > 0`), organ bruising and tearing. Needs `Overpressure` |
| `BlastCover` | Walls shielding bodies from the blast (the spec's `BlastOcclusion` and the player setting still apply) |
| `Fragments` | The fragment field |
| `WallPenetration` | Anything thrown going through walls: fragments, jet (its free metres too), spall, bone |
| `Ricochet` | Anything thrown glancing off surfaces |
| `LimbPassThrough` | Anything thrown carrying on out of a limb it wounded |
| `FragmentPush` | Hits pushing what they hit (`FragImpulse`) |
| `Jet` | The shaped-charge jet (`JetRays`) |
| `Spall` | Spall off the back of walls the jet goes through |
| `BoneFragments` | Bone thrown out of exit wounds |
| `Debris` | The `DebrisArc` event, for your debris visuals |

`BlastOnly` and `SimpleFragments` are ready-made combinations, and `All` is everything.

```csharp
// Fragments that never go through walls and throw no bone:
spec.Features &= ~(ExplosionFeatures.WallPenetration | ExplosionFeatures.BoneFragments);

// Or for one detonation only. The mask can leave parts out, never add what the spec has off:
FruitBallistics.SpawnExplosion("MyMod.Grenade", position, forward, owner: 0,
    ExplosionFeatures.All & ~ExplosionFeatures.WallPenetration);
```

The mask travels in `BallisticsCommand.Disabled`, so a networked detonation does the same on every
machine. `ExplosionInfo.Features` tells your `Exploded` handler which parts ran.

### Fragments

A fragment is a steel chunk of `FragMassGrams`. Its real speed follows from `FragPower` at 7.5
power per joule, the same scale as rounds: 2 g at 2700 is about 600 m/s. Its sectional density
comes from the average area a tumbling steel cube presents, about 33 kg/m² for 2 g, roughly a
fifth of a 7.62x39's. So fragments go through drywall and thin wood but stop in concrete, where a
rifle round would carry on. A lighter fragment is faster but penetrates less for the same power.

At a surface a fragment follows the rules rounds do: it may **ricochet**, else **penetrate**
(Poncelet, with the same [`SurfaceMaterial`](#what-a-surface-is-made-of) as rounds), else
**stop**. Out of a limb it carries on with the speed the wound walk left it. Each of those starts a
new *leg* of its flight, up to 4 legs per fragment. Wound power follows speed squared along the
whole path, so a fragment that has been through a wall wounds less on the other side. Roughly how
far a 2 g fragment at 2700 gets in, straight on: concrete 1 cm, brick 2 cm, steel 0.2 cm, wood
6 cm, drywall 9 cm.

| Field | What it does |
|---|---|
| `FragPower`, `FragMassGrams` | Wound power at the charge, and one fragment's mass. Together they set its speed. A claymore's steel balls are about 0.7 g |
| `FragPenetrationScale` | Through surfaces, relative to a steel chunk of that mass. 0 = fragments never go through |
| `FragRicochetAngle` | Angle **on concrete** beyond which a fragment glances off, shifted per material as for rounds. Default 65: irregular fragments skip at steeper angles than bullets |
| `FragMaxBounces` | Ricochets per fragment (default 1). Each adds a sweep for the fragments that make one |
| `FragRicochetEnergyLoss`, `FragRicochetScatter` | Energy lost to a ricochet (0.6) and random deflection in degrees (10) |
| `FragPowerFalloff` | Power kept per metre of flight, as exp(-x·d) |
| `FragImpulse` | Push on a body a fragment hits, scaled by the velocity it actually lost there: a fragment that goes through pushes less than one that stops |
| `JetRays`, `JetConeDeg`, `JetPenetration` | A shaped charge's jet (HEAT). Since 5.10.0 a full-sphere spec may carry one too (a cluster bomblet: a fragmenting case, targeted fragments and a belt, round a shaped charge); it is always aimed down the detonation's forward. `JetRays` extra fragments (default 0 = none) fly flat down a `JetConeDeg` cone (3°) round the forward direction. Each goes through `JetPenetration` metres of wall (0.8) at no cost, spread over every wall it meets, and never ricochets while any is left. Past that it penetrates like any fragment. Up to 8 legs. `FragmentTrace.Jet` marks them |
| `JetPower`, `JetWound` | What a jet ray does in a body: its own wound power (30000, two rifle rounds; 0 = `FragPower`) and profile (a crushed core, a big cavity, through bone) |
| `JetSpallCount`, `JetSpallConeDeg`, `JetSpallPower`, `JetSpallMassGrams`, `JetSpallCraterScale` | Behind-armour spall (scabbing), what kills behind cover. The back of each wall the jet goes through (once per wall) sheds `JetSpallCount` chunks (60) × the material's `SurfaceMaterial.Spall`: concrete 1, brick 1.1, glass 1.2, steel 0.6, wood 0.5, drywall 0.3, soil and water 0. The chunks come off a ragged patch round the exit, whose radius is the hole plus ~0.8× the wall's thickness, times `JetSpallCraterScale`. Chunks from the middle fly fast along the jet; chunks from the rim are slower and splay out to the 60° cone. Sizes are skewed small, averaging `JetSpallMassGrams` (3 g), and power (2500) scales with size and with the power the jet still had. `FragmentTrace.Spall` marks them |
| `JetMaxWounds` | Wound budget for the jet and its spall (120), kept apart from `MaxWounds`. They are traced last and would otherwise get only what the fragment field left |
| `BoneFragments`, `BoneFragmentPower`, `BoneFragmentConeDeg` | Secondary fragments of bone. Anything that perforates bone throws up to this many pieces (4) out of the exit wound, in a cone (50°), with power (900) scaled by how hard the hit was. They come out of `SecondaryMaxWounds`, as do the jet and spall |
| `ChargeKgTNT`, `SurfaceBurstFactor`, `Injury` | Physical overpressure (5.4). The charge in kg of TNT; above 0 it replaces the old radius model. `FruitBlast` works out the blast wave's reflected peak pressure at each limb's nearest point (Hopkinson-Cranz scaling, the Mills fit to Kingery-Bulmash), after cover and the charge's cone. A charge on a surface counts `SurfaceBurstFactor` (1.8) times over. "On a surface" is within 0.35 m of something solid; with `SurfaceBurstScaledHeight` above 0 (5.10.0, off by default) a burst that many m/kg^⅓ over the ground counts too, since for a big charge a few metres up is ground level (0.15: about 3 m for 11 t of TNT). What that pressure does follows `Injury`, a `BlastInjuryProfile`: organ thresholds in kPa (lung 150, stomach 250, brain 450, heart 500, liver 700) bruise the organ's own voxels, and tear it past twice the threshold. Visible damage on the side facing the charge starts at `SurfaceKPa` (1200); past `DisruptionKPa` (6000, contact range) the limb comes apart from inside. Thresholds rise for small charges, whose short blast the body tolerates better. Organ damage goes through the game's own pain and cognition. With the probe on, each organ injury is logged with its durability before and after. With a charge the shockwave push is physical too: the blast wave's reflected impulse times each body's frontal area, divided by its mass, scaled by `BlastPushScale` (1). `BlastForce`, `BlastUpward` and `BlastRadius` then no longer apply. Organs are looked up in the background, one per frame, once any spec with a charge is registered, so the first blast near a ragdoll doesn't hitch |
| `MaxPushRange`, `MaxInjuryRange` | With a charge, how far out the blast wave is swept for bodies to push (40 m) and limbs to injure (60 m), metres (5.7.0). The push reaches out to about 3 kPa, which for a 2000 lb bomb is about 270 m: raise these per spec for big charges, keep them at the arena that matters |
| `OverpressureBudgetShare` | Most of `MaxWounds` the overpressure may use (0.4), spread point by point over every limb in range, so a crowd can't use up the whole budget before a single fragment cuts |

The player's `WallPenetration` and `PenetrationScale` settings ([below](#player-settings)) apply
to fragments as they do to rounds; with walls off, a fragment only ricochets or stops.

Fragments do not raise `SurfaceHit`, whose handlers expect a `Projectile`, and `BeforeSurfaceHit`
does not steer them. Use `FragmentTraced` ([below](#events-where-your-visuals-go)) to see where
they went.

### Targeted fragments and the side-spray belt (5.8.0)

A plain fragment field is a set of evenly spread rays. Even spreading still leaves gaps: at 2,000
rays neighbouring rays are about 0.8 m apart at 20 m and 2 m apart at 50 m, so a body there is
often missed outright, and every ray that does land costs a wound walk (about 1 ms each). Big
cased charges (bombs) need thousands of real fragments, and flying them all blind is both patchy
and slow.

With `FragTargeted` above 0 (full-sphere specs only), fragments are aimed instead. Every limb
within reach gets the share of `FragTargeted` real fragments that the case would put into it:
the area its bounding box shows the charge (× 0.6, for the limb inside its box) over 4πr², shaped
by the belt below. The fraction is settled by a random draw, so a limb expecting 0.3 hits is hit
three times in ten. That many fragments are aimed at points on the limb, along the flat arc that
reaches it at `FragSpeed`, and flown with the usual physics: walls, cover, ricochets and
pass-through all still apply. Until it ricochets, a fragment only counts on the first limb it
meets. If something else is in the way it is dropped, because that limb's own share already
covers the direction. Coverage no longer depends on a ray count, and the cost follows the real
number of hits.

`FragCount` then sets the untargeted **scenery rays**: they hit walls and props, throw debris and
draw in debug views, but go through bodies without wounding or pushing them.

| Field | What it does |
|---|---|
| `FragTargeted` | The case's real fragment count. 0 = off (plain rays, as before) |
| `FragTargetRange` | How far out limbs get their share, metres. 0 = where fragments are down to 5 % of their power (from `FragPowerFalloff`), at most 200, and never past `FragSpeed × FragMaxTime` |
| `MaxWalksPerLimb` | Most wound walks one limb gets from one detonation (0 = no limit), any explosion. Near a big charge a limb can draw dozens of hits. Past the cap the extra hits still push, and add their power to the walks it gets (up to 4× each). A limb the blast is already blowing apart (ten times its disruption pressure) gets one targeted walk at most. Under [frame pressure](perfmon.md) `AdaptiveQuality` lowers this cap, not the coverage |
| `FragBeltDeg`, `FragBeltShare` | A side-spray belt. A cased bomb throws most of its case out square to its long axis. Above 0, `FragBeltShare` (0.8) of the fragments leave within a band this many degrees thick (in total) round the plane square to the axis; the rest are spread over the whole sphere (nose and tail spray). Applies to targeted fragments and plain rays alike. Full-sphere specs only. The axis comes with the detonation (below) |

The axis is the casing's long axis, passed separately from `forward` so that effects can stay
aimed by the surface normal:

```csharp
FruitBallistics.SpawnExplosion("MyMod.Bomb", at, groundNormal, bombVelocity, owner: 0, ExplosionFeatures.All);
```

It travels in `BallisticsCommand.Axis` (zero = `forward`). A bomb coming in steeply lays its belt
nearly flat across its path and tilts it along the path, so its lethal area is wider across than
along, as a real one's is (the Mk 82's is about 80 × 30 m).

#### Where the time went: `FruitBallistics.LastExplosion`

Every detonation fills an `ExplosionStats`, readable right after the spawn call returns: total,
blast, organ, fragment and wound-walk milliseconds, organ injuries applied and skipped, walks, and
for targeted fragments the limbs in reach, expected hits, fragments aimed, dropped and folded,
plus scenery rays. Organ injury is skipped on a limb at ten times its disruption pressure, where
the disruption bursts take its core anyway (`OrgansSkipped`).

### Blast cover

The shockwave push and the overpressure damage on each target are multiplied by the
`BlastTransmission` (0..1) of every wall on the straight line from the charge to it, and the
result is floored at the spec's `BlastDiffraction` (default 0.15), because a blast spills round
corners and over walls. Only objects at least 1 m across count as walls, so crates, bodies and
small props do not shield, and neither does the target's own body.

| Material | Concrete | Brick | Steel | Wood | Drywall | Glass | Soil | Water |
|---|---|---|---|---|---|---|---|---|
| `BlastTransmission` | 0.03 | 0.05 | 0.02 | 0.25 | 0.50 | 0.60 | 0.00 | 0.10 |

These are guesses by kind of wall, like the keyword table. A material you register defaults to
0.05. `ExplosionSpec.BlastOcclusion = false` turns cover off for one spec, and the player can turn
it off for every mod (`FruitLibConfig.BlastOcclusion`, "Walls shield from blasts"). Fragments are
not affected: their own physics decides what stops them.

## Events: where your visuals go

```csharp
FruitBallistics.SurfaceHit  += OnSurface;   // SurfaceHitInfo: collider, point, normal, material, outcome, penetrated, exit, speeds
FruitBallistics.LimbWounded += OnWound;     // WoundInfo: limb, entry, exit, power in/out, exited
FruitBallistics.Exploded    += OnExploded;  // ExplosionInfo: spec, origin, ground hit, owner
FruitBallistics.FragmentTraced += OnFragment;  // FragmentTrace: one leg of one fragment (5.4.0)
FruitBallistics.BlastTraced    += OnBlast;     // BlastTrace: what a body or limb got of the blast (5.4.0)
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
| `FragmentTraced` | Every leg of every fragment: where it flew, what it hit, and how the leg ended (`EndedBy`). Thousands per detonation, and only built while someone is subscribed, so keep the handler cheap |
| `BlastTraced` | Once per body (shockwave) or limb (overpressure) in range: the `Transmission` that got through, and the `Occluder` and its material. Not raised for cosmetic detonations, which push nothing |

**Every mod hears every event.** Draw only your own: filter on the spec id prefix.

```csharp
void OnSurface(SurfaceHitInfo hit)
{
    if (!hit.Projectile.Spec.Id.StartsWith("MyMod.")) return;
    SpawnSpark(hit.Point, hit.Normal);
    if (hit.Penetrated) SpawnSpark(hit.Exit, -hit.Direction);
}
```

A `FragmentTrace` is one leg of a fragment's flight. `Start`, `ArcVelocity`, `Time` and `End`
describe the arc (`p(t) = Start + ArcVelocity·t + ½·g·t²`, for drawing), `Fragment` and `Leg` say
which one it is, and `SpeedIn` / `SpeedOut` are its physical speed at the end. `EndedBy` is one of
`Spent` (hit nothing), `Lodged` (stopped in a limb), `PassedThrough` (out of a limb), `Ricocheted`,
`Penetrated`, `Stopped` (in a surface), `OverBudget` (hit a limb after the wound budget ran out)
or `TooWeak`. `Material`, `Incidence`, `Exit` and `Thickness` are filled in where they apply, and
the next leg starts at `End`, or at `Exit` after a wall or a limb. On a cosmetic detonation a
fragment that hits a limb is drawn `Lodged`: what the wound would have left is the host's to know.

```csharp
FruitBallistics.FragmentTraced += OnFragment;
FruitBallistics.BlastTraced    += OnBlast;

void OnFragment(FragmentTrace t)
{
    if (!t.Spec.Id.StartsWith("MyMod.")) return;                    // every mod's fragments arrive here
    if (t.Fragment % 20 != 0) return;                               // a sample, not thousands

    Vector3 prev = t.Start;
    for (int i = 1; i <= 8; i++)                                    // the arc, in short segments
    {
        float s = t.Time * i / 8f;
        Vector3 p = t.Start + t.ArcVelocity * s + 0.5f * Physics.gravity * s * s;
        Debug.DrawLine(prev, p, t.Leg == 0 ? Color.yellow : Color.red, 5f);
        prev = p;
    }

    if (t.EndedBy == FragmentEnd.Penetrated) SpawnHole(t.End, t.Exit, t.Thickness);
    if (t.EndedBy == FragmentEnd.Ricocheted) SpawnSpark(t.End, t.Normal);
}

void OnBlast(BlastTrace t)
{
    if (!t.Spec.Id.StartsWith("MyMod.")) return;
    if (t.Transmission < 1f)
        MelonLogger.Msg($"blast at {t.Target}: {t.Transmission:P0} through {t.OccluderMaterial?.Name}");
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

Under **Ballistics** in FruitLib's settings (and `FruitLibConfig.ini`): whether rounds and explosion
fragments go through walls at all (`WallPenetration`), a multiplier on how well they do
(`PenetrationScale`, 1 = physical), whether walls shield from blasts (`BlastOcclusion`, "Walls
shield from blasts", 5.4.0), exit-wound ejecta and its thresholds, chunk count and speed, blood decals, and a target
frame rate below which the oldest chunks and splats are cleared early. They apply to every mod's
rounds and explosions, so don't build on penetration or cover being on: with penetration off, a round
or fragment only ricochets or stops, and with `BlastOcclusion` off a blast reaches everything in range.
