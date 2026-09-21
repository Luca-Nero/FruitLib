using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    public enum BallisticsCommandKind { Projectile, Explosion }

    /// <summary>
    /// Everything needed to reproduce one shot or detonation somewhere else: which registered
    /// spec, where, which way, and the seed every random choice is drawn from. Small, flat and
    /// value-typed on purpose - it is what goes over the wire.
    /// </summary>
    public struct BallisticsCommand
    {
        public BallisticsCommandKind Kind;
        public string  SpecId;
        public Vector3 Origin;
        public Vector3 Direction;
        public int     Seed;
    }

    /// <summary>A round in flight. Read it; FruitLib moves it.</summary>
    public sealed class Projectile
    {
        public int            Id       { get; internal set; }
        public ProjectileSpec Spec     { get; internal set; }
        public Vector3        Position { get; internal set; }
        public Vector3        Velocity { get; internal set; }
        public float          Age      { get; internal set; }
        public bool           Alive    { get; internal set; }
        /// <summary>Flies, hits and reports, but damages and pushes nothing - a remote player's
        /// shot being drawn on this machine while the host does the real work.</summary>
        public bool           Cosmetic { get; internal set; }
        /// <summary>Free for the spawner, e.g. to find its own rounds for tracers.</summary>
        public object         Tag;

        /// <summary>Current wound power, from speed: muzzle power x (v / v0)².</summary>
        public int Power => Mathf.RoundToInt(MuzzlePower * PowerRatio);
        public float PowerRatio
        {
            get
            {
                float v0 = Mathf.Max(1f, Spec.MuzzleVelocity);
                float r  = Velocity.magnitude / v0;
                return r * r;
            }
        }

        internal int                 MuzzlePower;
        internal int                 Bounces;
        internal bool                WalkedBody;
        internal System.Random       Rng;
        internal readonly HashSet<IntPtr> IgnoredBodies = new HashSet<IntPtr>();
    }

    public struct SurfaceHitInfo
    {
        public Projectile Projectile;
        public Collider   Collider;
        public Vector3    Point, Normal, Direction;
        /// <summary>Degrees from the surface normal; 90 is a perfect graze.</summary>
        public float      Incidence;
        public bool       Ricocheted;
        public float      PowerRatio;
    }

    public struct WoundInfo
    {
        /// <summary>Null for fragment and overpressure wounds.</summary>
        public Projectile Projectile;
        public GameObject Limb;
        public Vector3    Entry, Exit, Direction;
        public int        PowerIn, PowerOut;
        public bool       Exited;
        public int        Steps;
        public bool       Cosmetic;
    }

    public struct ExplosionInfo
    {
        public ExplosionSpec Spec;
        public Vector3       Origin, Forward;
        public bool          HasGround;
        public RaycastHit    Ground;
        public bool          Cosmetic;
    }

    /// <summary>
    /// One place for every projectile and explosion in every mod.
    ///
    /// Register a spec once at start-up, then spawn it by id:
    /// <code>
    /// FruitBallistics.Register(ProjectileSpec.Cartridge("MyMod.AK", 7.9f, 7.62f, 715f, 0.29f));
    /// FruitBallistics.SpawnProjectile("MyMod.AK", muzzle, forward);
    /// </code>
    /// FruitLib flies it, walks it through bodies with the game's own wound model, ricochets it,
    /// pushes what it hits, and reports through the events below - tracers, marks and sounds
    /// belong to the mod that fired, and hang off those.
    ///
    /// <b>Multiplayer.</b> Spawns are commands, and a command is the only thing that needs to
    /// travel: a networking mod sets <see cref="Intercept"/> on a client to forward the command
    /// to the host instead of simulating it, listens to <see cref="Issued"/> on the host to
    /// broadcast what actually happened, and calls <see cref="Execute"/> with cosmetic = true on
    /// clients to draw it. Wounds then arrive through the voxel sync that already exists, and
    /// no mod that fires anything has to know the network is there.
    /// </summary>
    public static class FruitBallistics
    {
        private const string Tag = "[FruitBallistics]";

        private static readonly Dictionary<string, ProjectileSpec> _projectiles = new Dictionary<string, ProjectileSpec>();
        private static readonly Dictionary<string, ExplosionSpec>  _explosions  = new Dictionary<string, ExplosionSpec>();
        private static readonly System.Random _seeds = new System.Random();

        // ── Events ──────────────────────────────────────────────────────────────

        public static event Action<Projectile>     ProjectileSpawned;
        public static event Action<Projectile>     ProjectileEnded;
        /// <summary>
        /// Raised once a frame, right after every round has moved. Visuals that follow rounds
        /// (tracers, trails) should update here rather than in their own OnUpdate: MelonLoader
        /// does not order one mod's update against another's, and a frame late at 700 m/s is
        /// a tracer twelve metres behind its round.
        /// </summary>
        public static event Action ProjectilesMoved;
        public static event Action<SurfaceHitInfo> SurfaceHit;
        public static event Action<WoundInfo>      LimbWounded;
        public static event Action<ExplosionInfo>  Exploded;
        /// <summary>A sampled fragment's arc, for debris visuals: whose explosion, start,
        /// velocity, flight time. Visuals belong to the mod that owns the spec - filter on it.</summary>
        public static event Action<ExplosionSpec, Vector3, Vector3, float> DebrisArc;

        // ── Multiplayer seam ────────────────────────────────────────────────────

        /// <summary>
        /// Consulted before a locally requested spawn is simulated. Return true to take it over
        /// (a client forwarding it to the host); FruitLib then does nothing more with it.
        /// </summary>
        public static Func<BallisticsCommand, bool> Intercept;

        /// <summary>Raised for every command simulated for real on this machine - the host's
        /// cue to broadcast it.</summary>
        public static event Action<BallisticsCommand> Issued;

        // ── Registry ────────────────────────────────────────────────────────────

        public static void Register(ProjectileSpec spec)
        {
            if (spec == null || string.IsNullOrEmpty(spec.Id)) { MelonLogger.Warning($"{Tag} projectile spec needs an Id"); return; }
            _projectiles[spec.Id] = spec;
        }

        public static void Register(ExplosionSpec spec)
        {
            if (spec == null || string.IsNullOrEmpty(spec.Id)) { MelonLogger.Warning($"{Tag} explosion spec needs an Id"); return; }
            _explosions[spec.Id] = spec;
        }

        public static bool TryGetProjectile(string id, out ProjectileSpec spec) => _projectiles.TryGetValue(id ?? "", out spec);
        public static bool TryGetExplosion(string id, out ExplosionSpec spec)   => _explosions.TryGetValue(id ?? "", out spec);

        // ── Spawning ────────────────────────────────────────────────────────────

        /// <summary>Fires a registered round. Returns it, or null if it was intercepted or unknown.</summary>
        public static Projectile SpawnProjectile(string specId, Vector3 origin, Vector3 direction)
            => Request(new BallisticsCommand
            {
                Kind = BallisticsCommandKind.Projectile, SpecId = specId,
                Origin = origin, Direction = direction.normalized, Seed = NextSeed(),
            }) as Projectile;

        /// <summary>Detonates a registered explosive. <paramref name="forward"/> aims a cone;
        /// a full sphere ignores it.</summary>
        public static void SpawnExplosion(string specId, Vector3 origin, Vector3 forward)
            => Request(new BallisticsCommand
            {
                Kind = BallisticsCommandKind.Explosion, SpecId = specId,
                Origin = origin, Direction = forward.sqrMagnitude > 0f ? forward.normalized : Vector3.up,
                Seed = NextSeed(),
            });

        private static object Request(BallisticsCommand cmd)
        {
            try { if (Intercept != null && Intercept(cmd)) return null; }
            catch (Exception e) { MelonLogger.Warning($"{Tag} Intercept threw, simulating locally: {e.Message}"); }
            return Execute(cmd, cosmetic: false);
        }

        /// <summary>
        /// Runs a command as given, bypassing <see cref="Intercept"/>. For a networking mod
        /// applying a command that arrived: the host runs a client's request for real, a client
        /// runs the host's broadcast as cosmetic.
        /// </summary>
        public static object Execute(BallisticsCommand cmd, bool cosmetic)
        {
            object result = null;
            try
            {
                if (cmd.Kind == BallisticsCommandKind.Projectile)
                {
                    if (!_projectiles.TryGetValue(cmd.SpecId ?? "", out var spec))
                    { MelonLogger.Warning($"{Tag} no projectile registered as '{cmd.SpecId}'"); return null; }
                    result = FruitProjectiles.Spawn(spec, cmd, cosmetic);
                }
                else
                {
                    if (!_explosions.TryGetValue(cmd.SpecId ?? "", out var spec))
                    { MelonLogger.Warning($"{Tag} no explosion registered as '{cmd.SpecId}'"); return null; }
                    FruitExplosions.Detonate(spec, cmd, cosmetic);
                }
            }
            catch (Exception e) { MelonLogger.Warning($"{Tag} {cmd.Kind} '{cmd.SpecId}' failed: {e}"); return null; }

            if (!cosmetic) Raise(Issued, cmd);
            return result;
        }

        private static int NextSeed() { lock (_seeds) return _seeds.Next(); }

        // ── Lifecycle (FruitLibMod) ─────────────────────────────────────────────

        internal static void Tick()
        {
            bool any = FruitProjectiles.Live.Count > 0;
            FruitProjectiles.Tick();
            if (any)
            {
                var moved = ProjectilesMoved;
                if (moved != null)
                    foreach (Action h in moved.GetInvocationList())
                        try { h(); } catch (Exception e) { Report(h, e); }
            }
            FruitEjecta.Tick();
        }

        internal static void ResetForScene()
        {
            FruitProjectiles.Clear();
            FruitWounds.ResetForScene();
        }

        // ── Event raising. One subscriber throwing must not stop the rest, or the round. ──

        internal static void RaiseSpawned(Projectile p)       => Raise(ProjectileSpawned, p);
        internal static void RaiseEnded(Projectile p)         => Raise(ProjectileEnded, p);
        internal static void RaiseSurfaceHit(SurfaceHitInfo h) => Raise(SurfaceHit, h);
        internal static void RaiseWounded(WoundInfo w)        => Raise(LimbWounded, w);
        internal static void RaiseExploded(ExplosionInfo x)   => Raise(Exploded, x);

        internal static bool WantsDebris => DebrisArc != null;
        internal static void RaiseDebris(ExplosionSpec s, Vector3 p0, Vector3 v, float t)
        {
            var d = DebrisArc;
            if (d == null) return;
            foreach (Action<ExplosionSpec, Vector3, Vector3, float> h in d.GetInvocationList())
                try { h(s, p0, v, t); } catch (Exception e) { Report(h, e); }
        }

        private static void Raise<T>(Action<T> evt, T arg)
        {
            if (evt == null) return;
            foreach (Action<T> h in evt.GetInvocationList())
                try { h(arg); } catch (Exception e) { Report(h, e); }
        }

        private static readonly HashSet<string> _reported = new HashSet<string>();
        private static void Report(Delegate h, Exception e)
        {
            string who = h.Method.DeclaringType?.FullName + "." + h.Method.Name;
            if (_reported.Add(who)) MelonLogger.Warning($"{Tag} listener {who} threw (reported once): {e}");
        }
    }
}
