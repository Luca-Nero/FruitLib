using System;
using Il2CppSpawnables.Bullets;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// How a projectile tears through a body. Every field maps onto the game's own bullet
    /// (Spawnables.Bullets.Bullet); FruitLib walks the voxels the same way the game
    /// does and hands the result to the game's own cavitation and exit-tear builders, so a
    /// profile with native values produces a native wound.
    ///
    /// Units are voxel steps unless stated. A human voxel is about 23 mm.
    /// </summary>
    public sealed class WoundProfile
    {
        /// <summary>Voxels around each step destroyed outright. Both native calibres use 0: the
        /// channel is one voxel wide, and width comes from spread and cavitation.</summary>
        public int CrushRadius = 0;

        /// <summary>Steps before the channel starts to spread and the cavity opens - the "neck"
        /// of a real wound track. Stretched automatically for oblique entries, and only applied
        /// to the first body a round enters, as the game does.</summary>
        public int CleanEntryDepth = 2;

        /// <summary>Chance, per neighbouring voxel per step past the clean entry, that the
        /// channel takes it too. Native: 0.3 for 9mm, 0.5 for 7.62.</summary>
        public float SpreadChance = 0.5f;

        /// <summary>Peak radius of the temporary cavity in voxels. 0 = none, which is the
        /// native 9mm and physically right for handgun rounds.</summary>
        public int CavitationPeakRadius = 4;

        /// <summary>Signal strength across the cavity. Native 7.62: -350.</summary>
        public float CavitationDamage = -350f;

        /// <summary>Exit tear radius range in voxels; the leftover power ratio picks within it.</summary>
        public float TearMinRadius = 1.5f;
        public float TearMaxRadius = 3f;
        public float ExitTearDamage = -350f;

        /// <summary>Steps through one body before the round gives up. Native: 40 / 80.</summary>
        public int MaxDepth = 80;

        /// <summary>
        /// Scales what hard tissue costs beyond what soft tissue would. 1 = native. The game's
        /// limb bone costs about 74x muscle per voxel, which stops a 7.62 dead in a thigh - a
        /// real one goes through a femur. Around 0.1 gives that back. Only FruitLib rounds are
        /// affected; the game's own bullets keep their behaviour.
        /// </summary>
        public float HardTissueScale = 1f;

        /// <summary>Impulse on the struck limb at full power, N·s, scaled by the power the round
        /// arrived with. The native formula; physically honest bullets would push far less.</summary>
        public float ImpactImpulse = 65f;

        /// <summary>Throw tissue-coloured chunks and blood decals out of exit wounds. Also gated
        /// by the Ballistics settings, so a player can turn it off for every mod at once.</summary>
        public bool Ejecta = true;

        public WoundProfile Clone() => (WoundProfile)MemberwiseClone();
    }

    /// <summary>
    /// A round. Flight is real external ballistics - quadratic air drag from mass, calibre and
    /// drag coefficient, plus gravity and any FruitForces field - and the round's power in
    /// tissue follows its kinetic energy, so a round that has slowed down, ricocheted or
    /// already passed through someone does proportionally less.
    /// </summary>
    public sealed class ProjectileSpec
    {
        /// <summary>Stable id, e.g. "GunsGunsGuns.AK". Multiplayer sends this, not the spec, so
        /// both ends must register the same id.</summary>
        public string Id;

        public float MassGrams      = 7.9f;
        public float CaliberMm      = 7.62f;
        /// <summary>Cd against air. Spitzer rifle bullets ~0.25-0.3, round-nose handgun ~0.4-0.5, buckshot ~0.47.
        /// Constant at every speed; set <see cref="BallisticCoefficientG7"/> for drag that follows Mach.</summary>
        public float DragCoefficient = 0.29f;
        /// <summary>
        /// G7 ballistic coefficient in lb/in², as printed on ammunition boxes and in load data
        /// (7.62×39 FMJ ~0.14, 5.56 M855 ~0.15, .308 175 gr ~0.24, .50 BMG ~0.5). Above 0 it
        /// replaces <see cref="DragCoefficient"/> with the G7 drag curve, which rises sharply
        /// through the sound barrier - so a supersonic round sheds speed fast until it goes
        /// subsonic, and a subsonic one barely slows. 0 = constant Cd.
        /// </summary>
        public float BallisticCoefficientG7 = 0f;
        public float MuzzleVelocity  = 715f;
        public float GravityScale    = 1f;
        public float Lifetime        = 4f;
        public bool  ExternalForces  = true;

        /// <summary>Wound power per joule of kinetic energy. The game's calibres work out at
        /// roughly 7-10; 7.5 puts a real 7.62x39 at the native 7.62's 15000.</summary>
        public float PowerPerJoule = 7.5f;

        /// <summary>Muzzle power used instead of energy x PowerPerJoule when above 0. Power
        /// still falls with the square of speed from there.</summary>
        public int PowerOverride = 0;

        /// <summary>Below this fraction of muzzle power the round is spent.</summary>
        public float KillPowerRatio = 0.02f;

        /// <summary>Random yaw on leaving a body, degrees.</summary>
        public float PenetrationDeflect = 1f;

        /// <summary>Incidence from the surface normal beyond which concrete deflects it. Other
        /// surfaces shift this (<see cref="SurfaceMaterial.RicochetAngleShift"/>), and there is a
        /// few degrees of chance either side of it.</summary>
        public float RicochetAngle      = 70f;
        public int   MaxBounces         = 1;
        /// <summary>Energy lost to a ricochet, before the surface scales it (<see cref="SurfaceMaterial.RicochetLossScale"/>).</summary>
        public float RicochetEnergyLoss = 0.5f;
        public float RicochetScatter    = 3f;

        /// <summary>
        /// Impulse on a non-limb rigidbody for a round at muzzle speed that stops dead in it,
        /// N·s. What it actually pushes follows the speed the round lost there - a round that
        /// passes through, or glances off, pushes less. Set it to mass (kg) × muzzle velocity
        /// for a physically exact push; games usually want more.
        /// </summary>
        public float WorldImpulse = 10f;

        /// <summary>
        /// How well it goes through surfaces, as a multiple of a full metal jacket of the same
        /// mass and calibre: above 1 for a hard or steel core, below 1 for soft and hollow
        /// points or lead shot, which flatten. 0 = never penetrates a surface. Everything else
        /// comes from the round itself - mass over frontal area, and speed.
        /// </summary>
        public float PenetrationScale = 1f;

        public WoundProfile Wound = new WoundProfile();

        // ── Derived ──────────────────────────────────────────────────────────────

        internal float MassKg => Mathf.Max(0.0001f, MassGrams * 0.001f);

        internal float AreaM2
        {
            get
            {
                float r = CaliberMm * 0.0005f;
                return Mathf.Max(1e-7f, Mathf.PI * r * r);
            }
        }

        /// <summary>Sectional density, kg/m²: what drives penetration.</summary>
        internal float SectionalDensity => MassKg / AreaM2;

        private const float AirDensity   = 1.225f;
        private const float SpeedOfSound = 343f;

        /// <summary>k in a = -k|v|v, per metre, at <paramref name="speed"/> through sea-level air:
        /// ½ρ·Cd·A / m, or from the G7 curve when a ballistic coefficient is set.</summary>
        internal float DragKAt(float speed)
        {
            if (BallisticCoefficientG7 > 0f)
            {
                // Retardation = (π/8)·ρ·v²·Cd_G7(M) / BC, with BC converted from lb/in² to kg/m².
                float bc = BallisticCoefficientG7 * 703.0696f;
                return Mathf.PI / 8f * AirDensity * G7(speed / SpeedOfSound) / bc;
            }
            return 0.5f * AirDensity * DragCoefficient * AreaM2 / MassKg;
        }

        // The G7 standard projectile's drag coefficient against Mach number.
        private static readonly float[] G7Mach = { 0f,     0.7f,   0.8f,   0.85f,  0.9f,   0.95f,  1.0f,   1.05f,  1.1f,   1.2f,   1.3f,   1.5f,   1.75f,  2.0f,   2.5f,   3.0f,   4.0f   };
        private static readonly float[] G7Cd   = { 0.1198f,0.1196f,0.1242f,0.1350f,0.1510f,0.2091f,0.3803f,0.4014f,0.3884f,0.3605f,0.3380f,0.3070f,0.2800f,0.2596f,0.2299f,0.2084f,0.1800f };

        private static float G7(float mach)
        {
            if (mach <= G7Mach[0]) return G7Cd[0];
            for (int i = 1; i < G7Mach.Length; i++)
                if (mach <= G7Mach[i])
                    return Mathf.Lerp(G7Cd[i - 1], G7Cd[i], (mach - G7Mach[i - 1]) / (G7Mach[i] - G7Mach[i - 1]));
            return G7Cd[G7Cd.Length - 1];
        }

        internal int MuzzlePower => PowerOverride > 0
            ? PowerOverride
            : Mathf.Max(1, Mathf.RoundToInt(PowerPerJoule * 0.5f * MassKg * MuzzleVelocity * MuzzleVelocity));

        public ProjectileSpec Clone()
        {
            var c = (ProjectileSpec)MemberwiseClone();
            c.Wound = Wound?.Clone() ?? new WoundProfile();
            return c;
        }

        // ── Presets ──────────────────────────────────────────────────────────────

        /// <summary>A real cartridge with native-feeling wound values scaled to its energy.</summary>
        public static ProjectileSpec Cartridge(string id, float massGrams, float caliberMm,
                                               float muzzleVelocity, float dragCoefficient)
            => new ProjectileSpec
            {
                Id = id, MassGrams = massGrams, CaliberMm = caliberMm,
                MuzzleVelocity = muzzleVelocity, DragCoefficient = dragCoefficient,
            };

        /// <summary>The game's own 7.62, value for value, read from the running game: same
        /// power, same wound, and the 400 m/s the game launches it at.</summary>
        public static ProjectileSpec Native762(string id) => FromNative(id, 7.9f, 7.62f, 0.29f, 762);

        /// <summary>The game's own 9mm, value for value.</summary>
        public static ProjectileSpec Native9mm(string id) => FromNative(id, 8f, 9f, 0.45f, 9);

        private static ProjectileSpec FromNative(string id, float mass, float cal, float cd, int which)
        {
            var s = new ProjectileSpec { Id = id, MassGrams = mass, CaliberMm = cal, DragCoefficient = cd, MuzzleVelocity = 400f };
            try
            {
                bool rifle = which == 762;
                s.PowerOverride               = rifle ? Bullet762.INITIAL_POWER          : Bullet9mm.INITIAL_POWER;
                s.Wound.CrushRadius           = rifle ? Bullet762.DESTRUCTION_RADIUS     : Bullet9mm.DESTRUCTION_RADIUS;
                s.Wound.CleanEntryDepth       = rifle ? Bullet762.CLEAN_ENTRY_DEPTH      : Bullet9mm.CLEAN_ENTRY_DEPTH;
                s.Wound.SpreadChance          = rifle ? Bullet762.CHANNEL_SPREAD_CHANCE  : Bullet9mm.CHANNEL_SPREAD_CHANCE;
                s.Wound.CavitationPeakRadius  = rifle ? Bullet762.CAVITATION_PEAK_RADIUS : Bullet9mm.CAVITATION_PEAK_RADIUS;
                s.Wound.CavitationDamage      = rifle ? Bullet762.CAVITATION_DAMAGE      : Bullet9mm.CAVITATION_DAMAGE;
                s.Wound.TearMinRadius         = rifle ? Bullet762.TEAR_MIN_RADIUS        : Bullet9mm.TEAR_MIN_RADIUS;
                s.Wound.TearMaxRadius         = rifle ? Bullet762.TEAR_MAX_RADIUS        : Bullet9mm.TEAR_MAX_RADIUS;
                s.Wound.ExitTearDamage        = rifle ? Bullet762.EXIT_TEAR_DAMAGE       : Bullet9mm.EXIT_TEAR_DAMAGE;
                s.Wound.MaxDepth              = rifle ? Bullet762.MAX_PENETRATION_DEPTH  : Bullet9mm.MAX_PENETRATION_DEPTH;
                s.Wound.ImpactImpulse         = rifle ? Bullet762.IMPACT_FORCE           : Bullet9mm.IMPACT_FORCE;
                s.KillPowerRatio              = Bullet.SELF_DESTRUCT_POWER_RATIO;
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitBallistics] native constants unreadable for '{id}', using defaults: {e.Message}"); }
            return s;
        }
    }

    /// <summary>
    /// The parts of a detonation, each on its own switch (<see cref="ExplosionSpec.Features"/>).
    /// A part that is off is skipped outright, costing nothing, whatever the spec's numbers say.
    /// </summary>
    [Flags]
    public enum ExplosionFeatures
    {
        None = 0,
        /// <summary>The blast pushing rigidbodies about.</summary>
        Shockwave        = 1 << 0,
        /// <summary>Blast wounds on limbs in range: the old radius model, or with a charge, the
        /// physical one (skin, then the whole limb).</summary>
        Overpressure     = 1 << 1,
        /// <summary>With a charge, bruised and torn organs inside limbs the blast reaches
        /// (<see cref="FruitBlastInjury"/>). Needs <see cref="Overpressure"/>.</summary>
        OrganInjury      = 1 << 2,
        /// <summary>Walls between the charge and a body shield it from the shockwave and
        /// overpressure. Needs <see cref="ExplosionSpec.BlastOcclusion"/> and the player setting too.</summary>
        BlastCover       = 1 << 3,
        /// <summary>The fragment field (<see cref="ExplosionSpec.FragCount"/>).</summary>
        Fragments        = 1 << 4,
        /// <summary>Anything the explosion throws - fragments, jet, spall, bone - going through
        /// walls. Off, every wall stops them; the jet's free metres included.</summary>
        WallPenetration  = 1 << 5,
        /// <summary>Anything it throws glancing off surfaces at shallow angles.</summary>
        Ricochet         = 1 << 6,
        /// <summary>Anything it throws carrying on out of a limb it went through. Off, whatever
        /// wounds a limb (or reaches one past the wound budget) stays in it.</summary>
        LimbPassThrough  = 1 << 7,
        /// <summary>Hits pushing what they hit (<see cref="ExplosionSpec.FragImpulse"/>).</summary>
        FragmentPush     = 1 << 8,
        /// <summary>The shaped-charge jet (<see cref="ExplosionSpec.JetRays"/>).</summary>
        Jet              = 1 << 9,
        /// <summary>Spall the jet blows off the back of walls (<see cref="ExplosionSpec.JetSpallCount"/>).</summary>
        Spall            = 1 << 10,
        /// <summary>Bone thrown out of exit wounds (<see cref="ExplosionSpec.BoneFragments"/>).</summary>
        BoneFragments    = 1 << 11,
        /// <summary>The <see cref="FruitBallistics.DebrisArc"/> event, for a mod's debris visuals.</summary>
        Debris           = 1 << 12,

        /// <summary>Blast only: push, wounds and organs, with cover. No fragments.</summary>
        BlastOnly = Shockwave | Overpressure | OrganInjury | BlastCover,
        /// <summary>Fragments that fly straight and stop at the first thing they hit.</summary>
        SimpleFragments = Fragments | FragmentPush | Debris,
        All = (1 << 13) - 1,
    }

    /// <summary>
    /// An explosive. The physics and wounds of BombsAway's detonation, with fragments now
    /// wounding through the same channel as bullets: each fragment is a small, fast round
    /// whose power falls off with distance.
    ///
    /// Fragments meet the world the way rounds do: a steel chunk of <see cref="FragMassGrams"/>
    /// at the speed its <see cref="FragPower"/> works out to, glancing off surfaces at shallow
    /// angles, going through what it has the energy for (Poncelet, per <see cref="SurfaceMaterial"/>),
    /// and carrying on out of limbs it passes through. The shockwave and overpressure are
    /// shielded by cover (<see cref="BlastOcclusion"/>).
    ///
    /// Visuals stay with the mod that owns them: listen to <see cref="FruitBallistics.Exploded"/>
    /// and <see cref="FruitBallistics.DebrisArc"/>.
    /// </summary>
    public sealed class ExplosionSpec
    {
        public string Id;

        /// <summary>
        /// Which parts of the detonation run; all by default. Clear a flag to leave that part out,
        /// e.g. <c>Features &amp;= ~(ExplosionFeatures.WallPenetration | ExplosionFeatures.BoneFragments)</c>.
        /// A single detonation can leave out more through the mask passed to
        /// <see cref="FruitBallistics.SpawnExplosion(string, Vector3, Vector3, int, ExplosionFeatures)"/>.
        /// </summary>
        public ExplosionFeatures Features = ExplosionFeatures.All;

        /// <summary>Full sphere at 360 x 360; anything less is an elliptical cone along the
        /// forward direction given at detonation (a shaped charge, a claymore).</summary>
        public float HSpreadDeg = 360f;
        public float VSpreadDeg = 360f;

        // Shockwave: velocity change on every rigidbody in range, linear falloff.
        public float BlastRadius = 6f;
        public float BlastForce  = 5f;
        public float BlastUpward = 2f;

        /// <summary>Walls between the charge and a body cut its shockwave and overpressure by
        /// their <see cref="SurfaceMaterial.BlastTransmission"/>. Small things (under ~1 m) don't
        /// count: a blast wraps round them.</summary>
        public bool  BlastOcclusion  = true;
        /// <summary>What still reaches something behind full cover, 0..1: a blast spills round
        /// corners and over walls.</summary>
        public float BlastDiffraction = 0.15f;

        // Overpressure: noisy surface damage on limbs close to the charge.
        public float OverpressureRadius     = 3.5f;
        public float OverpressureFalloffExp = 1f;
        public int   OverpressurePoints     = 12;
        public int   OverpressureWoundRadius = 2;
        public float OverpressureDamage     = -2500f;

        /// <summary>
        /// The charge, as kg of TNT (Comp B ~1.1x its mass, C4 ~1.3x). Above 0 overpressure is
        /// physical: the blast wave's peak pressure at each limb from the charge's weight and the
        /// distance (<see cref="FruitBlast"/>), hurting organs, then skin, then the whole limb as it
        /// rises (<see cref="Injury"/>). <see cref="OverpressureRadius"/>, falloff and damage
        /// scale then no longer apply; <see cref="OverpressurePoints"/>, <see cref="OverpressureWoundRadius"/>
        /// and <see cref="OverpressureDamage"/> shape the visible part. 0 = the old radius model.
        /// </summary>
        public float ChargeKgTNT = 0f;
        /// <summary>On or against a surface the charge's wave is thrown back off it: this many times
        /// the charge (1.8 is the usual figure for a burst on the ground).</summary>
        public float SurfaceBurstFactor = 1.8f;
        /// <summary>
        /// A burst this low over the ground also counts as a surface burst, as a scaled height,
        /// m/kg^⅓ (5.10.0). For a big charge a few metres up is ground level: the wave off the
        /// ground merges with the direct one at once. 0.15 makes it ~3 m for 11 t of TNT and
        /// under a metre for 200 kg. 0 = only on or against something (within 0.35 m), as before.
        /// </summary>
        public float SurfaceBurstScaledHeight = 0f;
        /// <summary>With a charge, the shockwave push is the blast wave's impulse over each body's
        /// frontal area and mass (<see cref="BlastForce"/> and <see cref="BlastRadius"/> no longer
        /// apply); this scales it. 1 = physical.</summary>
        public float BlastPushScale = 1f;
        /// <summary>With a charge, the farthest the blast wave pushes anything, metres. The push
        /// reaches out to ~3 kPa, which for a big bomb is hundreds of metres (a 2000 lb bomb's
        /// ~270 m); this keeps the sweep for bodies to the arena that matters. Raise it for big
        /// charges (5.7.0).</summary>
        public float MaxPushRange = 40f;
        /// <summary>With a charge, the farthest the blast wave is checked for injuries, metres
        /// (the lowest injury threshold sets the reach below this; 5.7.0).</summary>
        public float MaxInjuryRange = 60f;
        /// <summary>Pressure thresholds for each kind of injury. <see cref="DamageScale"/> scales the
        /// pressure the body is judged at.</summary>
        public BlastInjuryProfile Injury = new BlastInjuryProfile();

        // Fragments.
        public int   FragCount   = 2000;
        /// <summary>Speed used for the fragment arc's shape, m/s. Kept separate from wounding
        /// power so trajectories and debris visuals can stay readable.</summary>
        public float FragSpeed   = 15f;
        public float FragMaxTime = 4f;
        public float FragImpulse = 0.8f;
        /// <summary>Wound power of a fragment at the charge. A 2 g fragment at 600 m/s is ~2700.</summary>
        public int   FragPower   = 2700;
        /// <summary>Fraction of power a fragment keeps per metre of flight, as exp(-x·d). Small
        /// irregular fragments shed speed fast.</summary>
        public float FragPowerFalloff = 0.08f;
        /// <summary>
        /// One fragment, grams. With <see cref="FragPower"/> (at 7.5 power per joule, as rounds)
        /// it sets the fragment's real speed - 2 g at 2700 is ~600 m/s - and with a tumbling steel
        /// chunk's frontal area, how well it goes through things. Lighter is faster but
        /// penetrates less for the same power; a claymore's steel balls are ~0.7 g.
        /// </summary>
        public float FragMassGrams = 2f;
        /// <summary>Through surfaces, relative to a steel chunk of that mass. 0 = never goes
        /// through; FruitLib's own penetration settings apply on top.</summary>
        public float FragPenetrationScale = 1f;
        /// <summary>Incidence from the normal on concrete beyond which a fragment glances off,
        /// shifted per material as for rounds. Irregular fragments skip at steeper angles than bullets.</summary>
        public float FragRicochetAngle = 65f;
        /// <summary>Ricochets per fragment. Each adds a sweep for the fragments that make one.</summary>
        public int   FragMaxBounces = 1;
        /// <summary>Energy lost to a ricochet, before the surface scales it.</summary>
        public float FragRicochetEnergyLoss = 0.6f;
        /// <summary>Random deflection off a ricochet, degrees. Fragments are not round.</summary>
        public float FragRicochetScatter = 10f;

        /// <summary>
        /// Targeted fragments (5.8.0, full-sphere specs only): the case's real fragment count.
        /// Above 0, fragments are no longer flown blind. Every limb in reach gets its expected
        /// share of this many, count x (its area seen from the charge) / (4π r²) shaped by the
        /// belt below, rounded with a random draw, and that many are aimed at it and flown
        /// with the usual physics: walls, cover, ricochets and pass-through all still apply.
        /// A fragment only counts on the first limb it meets (until it ricochets), so one
        /// standing behind another isn't hit twice. Coverage stops depending on a ray count,
        /// and the cost follows the hits. <see cref="FragCount"/> then becomes the untargeted
        /// rays for the scenery (walls, props, debris, the debug draw), which go through
        /// bodies without wounding them. 0 = off: <see cref="FragCount"/> rays as before.
        /// </summary>
        public int   FragTargeted = 0;
        /// <summary>Targeted fragments: how far out limbs get their share, metres. 0 = where the
        /// fragments are down to 5 % of their power (from <see cref="FragPowerFalloff"/>), at most 200.</summary>
        public float FragTargetRange = 0f;
        /// <summary>
        /// Most wound walks one limb gets from one detonation; 0 = no limit. Near the charge a limb
        /// can draw dozens of hits, and every walk costs. Past this many the extra hits still push,
        /// and add their power to the walks it does get (up to 4x each).
        /// </summary>
        public int   MaxWalksPerLimb = 0;
        /// <summary>
        /// A side-spray belt (5.8.0): a cased bomb throws most of its case out square to its axis.
        /// Above 0, <see cref="FragBeltShare"/> of the fragments leave within a band this many
        /// degrees thick (in total) round the plane square to the axis given at detonation, the
        /// rest over the whole sphere. Full-sphere specs only. 0 = an even sphere.
        /// </summary>
        public float FragBeltDeg = 0f;
        /// <summary>Share of the fragments in the belt, 0..1; the rest are nose and tail spray.</summary>
        public float FragBeltShare = 0.8f;

        // Shaped-charge jet (HEAT). Off at 0 rays.
        /// <summary>Extra fragments fired straight down <see cref="JetConeDeg"/> around the forward
        /// direction: a shaped charge's jet. 0 = no jet.</summary>
        public int   JetRays = 0;
        /// <summary>Full angle of the jet, degrees.</summary>
        public float JetConeDeg = 3f;
        /// <summary>
        /// Metres of surface each jet ray goes through at no cost - what a HEAT warhead is for.
        /// Spent across every wall it meets; past it the ray penetrates like any fragment. The
        /// jet also never ricochets while it has any left, and flies flat.
        /// </summary>
        public float JetPenetration = 0.8f;
        /// <summary>Wound power of one jet ray; 0 = <see cref="FragPower"/>. A jet carries far
        /// more than any fragment: 30000 is two rifle rounds.</summary>
        public int   JetPower = 30000;
        /// <summary>How the jet tears through a body: a crushed core and a big temporary cavity,
        /// through bone. Null = <see cref="FragWound"/>.</summary>
        public WoundProfile JetWound = new WoundProfile
        {
            CrushRadius = 1, CleanEntryDepth = 0, SpreadChance = 0.6f,
            CavitationPeakRadius = 5, CavitationDamage = -600f,
            TearMinRadius = 2f, TearMaxRadius = 4f, ExitTearDamage = -600f,
            MaxDepth = 120, HardTissueScale = 0.05f, ImpactImpulse = 40f,
        };

        /// <summary>
        /// Behind-armour spall: fragments of wall blown out of the back of every wall the jet
        /// goes through (once per wall), scaled by the wall's <see cref="SurfaceMaterial.Spall"/>.
        /// They come off a ragged patch round the exit: fast and along the jet from the middle,
        /// slower and splayed out to <see cref="JetSpallConeDeg"/> from the rim. What actually
        /// kills behind the armour. 0 = none.
        /// </summary>
        public int   JetSpallCount = 60;
        public float JetSpallConeDeg = 60f;
        /// <summary>Wound power of one spall fragment, scaled by the power the jet still had.</summary>
        public int   JetSpallPower = 2500;
        /// <summary>Average spall fragment, grams; chunks run from about a third of it to three
        /// times, mostly small. A bigger chunk carries proportionally more power at the same speed.</summary>
        public float JetSpallMassGrams = 3f;
        /// <summary>
        /// Scales the scab: the ragged patch of back face that comes away round the exit hole.
        /// At 1 its radius is the hole plus ~0.8x the wall's thickness (the shear cone), so a
        /// 100 mm wall sheds a ~11 cm patch and a 300 mm one ~27 cm.
        /// </summary>
        public float JetSpallCraterScale = 1f;
        /// <summary>Wound budget for the jet, its spall and bone fragments, on top of
        /// <see cref="MaxWounds"/>: they are traced last, and would otherwise only get what the
        /// fragment field left.</summary>
        public int   SecondaryMaxWounds = 120;

        /// <summary>
        /// Secondary fragments of bone: a fragment (or jet, or spall) that perforates bone blows
        /// up to this many pieces out of the exit wound, in a cone along its path, so a body at
        /// the charge wounds whoever is next to it. 0 = none. More bone crossed, more pieces.
        /// </summary>
        public int   BoneFragments = 4;
        /// <summary>Wound power of one bone fragment from a full-power hit; scales with the power
        /// that went into the bone. Bone is light and irregular, so they don't carry far.</summary>
        public int   BoneFragmentPower = 900;
        public float BoneFragmentConeDeg = 50f;
        public int   ArcSteps    = 12;
        public float DebrisRatio = 0.04f;
        public int   MaxDebris   = 24;

        /// <summary>Scales every wound this explosion makes.</summary>
        public float DamageScale = 1f;
        /// <summary>Wound budget per detonation; fragments past it still push, but do not cut.</summary>
        public int   MaxWounds   = 240;
        /// <summary>Most of <see cref="MaxWounds"/> the overpressure may use (0..1), spread evenly
        /// over every limb in range; the rest is kept for fragments.</summary>
        public float OverpressureBudgetShare = 0.4f;
        /// <summary>Scale fragment and overpressure counts down under frame pressure (FruitPerfMon).</summary>
        public bool  AdaptiveQuality = true;
        public float MinQuality      = 0.25f;

        public int LayerMask = ~(1 << 2);

        public WoundProfile FragWound = new WoundProfile
        {
            CrushRadius = 0, CleanEntryDepth = 0, SpreadChance = 0.3f,
            CavitationPeakRadius = 0, TearMinRadius = 0.5f, TearMaxRadius = 1.5f,
            ExitTearDamage = -250f, MaxDepth = 30, ImpactImpulse = 2f,
        };

        internal bool IsFullSphere => HSpreadDeg >= 360f && VSpreadDeg >= 360f;

        /// <summary>Power per joule, the same scale rounds use (<see cref="ProjectileSpec.PowerPerJoule"/>).</summary>
        internal const float FragPowerPerJoule = 7.5f;

        internal float FragMassKg => Mathf.Max(0.00005f, FragMassGrams * 0.001f);

        /// <summary>A fragment's real speed at the charge, m/s, from its power and mass.</summary>
        internal float FragVelocity => Mathf.Sqrt(2f * Mathf.Max(1, FragPower) / (FragPowerPerJoule * FragMassKg));

        /// <summary>
        /// Sectional density, kg/m². A tumbling fragment presents a quarter of its surface on
        /// average; for a steel cube that is 1.5·(m/ρ)^⅔, so a 2 g chunk comes to ~33 kg/m² -
        /// a fifth of a 7.62x39's, which is why fragments stop in walls rounds go through.
        /// </summary>
        internal float FragSectionalDensity
        {
            get
            {
                float m = FragMassKg;
                float area = 1.5f * Mathf.Pow(m / 7850f, 2f / 3f);
                return m / Mathf.Max(1e-8f, area);
            }
        }

        public ExplosionSpec Clone()
        {
            var c = (ExplosionSpec)MemberwiseClone();
            c.FragWound = FragWound?.Clone() ?? new WoundProfile();
            c.Injury    = Injury?.Clone();
            c.JetWound  = JetWound?.Clone();
            return c;
        }
    }
}
