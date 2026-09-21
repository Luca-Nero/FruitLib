using System;
using Il2CppSpawnables.Bullets;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// How a projectile tears through a body. Every field maps onto the game's own bullet
    /// (Spawnables.Bullets.Bullet, v0_17L); FruitLib walks the voxels the same way the game
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
        /// <summary>Cd against air. Spitzer rifle bullets ~0.25-0.3, round-nose handgun ~0.4-0.5, buckshot ~0.47.</summary>
        public float DragCoefficient = 0.29f;
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

        /// <summary>Incidence from the surface normal beyond which a hard surface deflects it.</summary>
        public float RicochetAngle      = 70f;
        public int   MaxBounces         = 1;
        public float RicochetEnergyLoss = 0.5f;
        public float RicochetScatter    = 3f;

        /// <summary>Impulse on a non-limb rigidbody at full power, N·s.</summary>
        public float WorldImpulse = 10f;

        public WoundProfile Wound = new WoundProfile();

        // ── Derived ──────────────────────────────────────────────────────────────

        internal float MassKg => Mathf.Max(0.0001f, MassGrams * 0.001f);

        /// <summary>k in a = -k|v|v, per metre: ½ρ·Cd·A / m, sea-level air.</summary>
        internal float DragK
        {
            get
            {
                float r = CaliberMm * 0.0005f;
                return 0.5f * 1.225f * DragCoefficient * Mathf.PI * r * r / MassKg;
            }
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
    /// An explosive. The physics and wounds of BombsAway's detonation, with fragments now
    /// wounding through the same channel as bullets: each fragment is a small, fast round
    /// whose power falls off with distance.
    ///
    /// Visuals stay with the mod that owns them: listen to <see cref="FruitBallistics.Exploded"/>
    /// and <see cref="FruitBallistics.DebrisArc"/>.
    /// </summary>
    public sealed class ExplosionSpec
    {
        public string Id;

        /// <summary>Full sphere at 360 x 360; anything less is an elliptical cone along the
        /// forward direction given at detonation (a shaped charge, a claymore).</summary>
        public float HSpreadDeg = 360f;
        public float VSpreadDeg = 360f;

        // Shockwave: velocity change on every rigidbody in range, linear falloff.
        public float BlastRadius = 6f;
        public float BlastForce  = 5f;
        public float BlastUpward = 2f;

        // Overpressure: noisy surface damage on limbs close to the charge.
        public float OverpressureRadius     = 3.5f;
        public float OverpressureFalloffExp = 1f;
        public int   OverpressurePoints     = 12;
        public int   OverpressureWoundRadius = 2;
        public float OverpressureDamage     = -2500f;

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
        public int   ArcSteps    = 12;
        public float DebrisRatio = 0.04f;
        public int   MaxDebris   = 24;

        /// <summary>Scales every wound this explosion makes.</summary>
        public float DamageScale = 1f;
        /// <summary>Wound budget per detonation; fragments past it still push, but do not cut.</summary>
        public int   MaxWounds   = 240;
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

        public ExplosionSpec Clone()
        {
            var c = (ExplosionSpec)MemberwiseClone();
            c.FragWound = FragWound?.Clone() ?? new WoundProfile();
            return c;
        }
    }
}
