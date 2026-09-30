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
        /// <summary>Who fired it: 0 = this machine / not said. FruitNet puts peer ids here, and
        /// force fields and surface hooks can use it to leave a player's own rounds alone.</summary>
        public int     Owner;
        /// <summary>Explosions only: parts of the spec's <see cref="ExplosionSpec.Features"/> left
        /// out of this one detonation. None (the default) runs the spec as it is.</summary>
        public ExplosionFeatures Disabled;
    }

    /// <summary>
    /// A round in flight. FruitLib moves it; other mods may steer it from
    /// <see cref="FruitBallistics.ProjectileStep"/> or <see cref="FruitBallistics.BeforeSurfaceHit"/>
    /// by setting <see cref="Position"/> or <see cref="Velocity"/>, scaling <see cref="PowerScale"/>,
    /// or calling <see cref="Kill"/>.
    /// </summary>
    public sealed class Projectile
    {
        public int            Id       { get; internal set; }
        public ProjectileSpec Spec     { get; internal set; }
        /// <summary>Settable: the round carries on from here (a portal, a teleport).</summary>
        public Vector3        Position { get; set; }
        /// <summary>Settable: direction and speed from here on. Speed is what the round's power
        /// follows, so slowing a round also weakens it.</summary>
        public Vector3        Velocity { get; set; }
        public float          Age      { get; internal set; }
        public bool           Alive    { get; internal set; }
        /// <summary>Who fired it, from <see cref="BallisticsCommand.Owner"/>.</summary>
        public int            Owner    { get; internal set; }
        /// <summary>Surfaces it has gone through.</summary>
        public int            Penetrations { get; internal set; }
        /// <summary>Knocked off its axis by something it passed through: more drag, and a wider
        /// wound in the next body it meets.</summary>
        public bool           Tumbling { get; internal set; }
        /// <summary>Multiplies the wound power it carries, without changing its flight. 1 = as fired.</summary>
        public float          PowerScale = 1f;
        /// <summary>Flies, hits and reports, but damages and pushes nothing - a remote player's
        /// shot being drawn on this machine while the host does the real work.</summary>
        public bool           Cosmetic { get; internal set; }
        /// <summary>Free for the spawner, e.g. to find its own rounds for tracers.</summary>
        public object         Tag;

        /// <summary>Current wound power, from speed: muzzle power x (v / v0)² x <see cref="PowerScale"/>.</summary>
        public int Power => Mathf.RoundToInt(MuzzlePower * PowerRatio * PowerScale);
        public float PowerRatio
        {
            get
            {
                float v0 = Mathf.Max(1f, Spec.MuzzleVelocity);
                float r  = Velocity.magnitude / v0;
                return r * r;
            }
        }

        /// <summary>Ends the round at the start of its next step (or now, inside a hit).</summary>
        public void Kill() => Killed = true;

        // ── Per-mod data, so several mods can each hang something on the same round ──

        private Dictionary<string, object> _data;

        /// <summary>Stores a value on this round under your own key, e.g. "MyMod.Charge".</summary>
        public void Set(string key, object value) { if (key != null) (_data ??= new Dictionary<string, object>())[key] = value; }

        public T Get<T>(string key, T fallback = default)
            => key != null && _data != null && _data.TryGetValue(key, out var v) && v is T t ? t : fallback;

        public bool Has(string key) => key != null && _data != null && _data.ContainsKey(key);

        internal int                 MuzzlePower;
        internal int                 Bounces;
        internal bool                WalkedBody;
        internal bool                Killed;
        internal System.Random       Rng;
        internal readonly HashSet<IntPtr> IgnoredBodies = new HashSet<IntPtr>();
        internal HashSet<int>        IgnoredColliders;
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
        /// <summary>What the surface was taken to be.</summary>
        public SurfaceMaterial Material;
        /// <summary>Went through: it left at <see cref="Exit"/>, <see cref="Thickness"/> metres further on.</summary>
        public bool       Penetrated;
        public Vector3    Exit;
        public float      Thickness;
        /// <summary>m/s on arrival and on leaving (0 if it stopped).</summary>
        public float      SpeedIn, SpeedOut;
        /// <summary>What was decided, by physics or by a <see cref="FruitBallistics.BeforeSurfaceHit"/> handler.</summary>
        public SurfaceOutcome Outcome;
    }

    /// <summary>What happens at a surface. <see cref="Physics"/> lets FruitLib decide.</summary>
    public enum SurfaceOutcome
    {
        /// <summary>Material, angle and energy decide: ricochet, go through, or stop.</summary>
        Physics,
        /// <summary>The round ignores this collider for the rest of its flight and carries on unchanged.</summary>
        PassThrough,
        /// <summary>The round stops here.</summary>
        Stop,
        /// <summary>The round ricochets here, even past its bounce limit or below the angle.</summary>
        Ricochet,
        /// <summary>The handler dealt with it: moved the round (<see cref="Projectile.Position"/> /
        /// <see cref="Projectile.Velocity"/>) or killed it. FruitLib only reports the hit. A round
        /// left where it was is stopped, since it would hit the same thing again.</summary>
        Handled,
    }

    /// <summary>A surface hit before anything is done about it. Handlers may change
    /// <see cref="Material"/> and <see cref="Outcome"/>.</summary>
    public struct SurfaceDecision
    {
        public Projectile Projectile;
        public Collider   Collider;
        public Vector3    Point, Normal, Direction;
        /// <summary>Degrees from the surface normal; 90 is a perfect graze.</summary>
        public float      Incidence;
        public SurfaceMaterial Material;
        public SurfaceOutcome  Outcome;
    }

    public delegate void SurfaceDecisionHandler(ref SurfaceDecision d);

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
        /// <summary>Who set it off, from <see cref="BallisticsCommand.Owner"/>.</summary>
        public int           Owner;
        /// <summary>The parts that ran: the spec's <see cref="ExplosionSpec.Features"/> less
        /// whatever the call left out. Visuals can follow it (no fragments, no debris).</summary>
        public ExplosionFeatures Features;
    }

    /// <summary>How one leg of a fragment's flight ended.</summary>
    public enum FragmentEnd
    {
        /// <summary>Flew out of time or landed nowhere in particular: nothing was hit.</summary>
        Spent,
        /// <summary>Went into a limb and stopped there.</summary>
        Lodged,
        /// <summary>Went through a limb and carries on (the next leg starts at the exit wound).</summary>
        PassedThrough,
        /// <summary>Glanced off a surface (the next leg starts there).</summary>
        Ricocheted,
        /// <summary>Went through a surface (the next leg starts at <see cref="FragmentTrace.Exit"/>).</summary>
        Penetrated,
        /// <summary>Stopped in a surface.</summary>
        Stopped,
        /// <summary>Hit a limb after the explosion's wound budget ran out: it pushed but did not
        /// cut, and carries on through (next leg from <see cref="FragmentTrace.Exit"/>) if it had
        /// the power for that much tissue.</summary>
        OverBudget,
        /// <summary>Too slow to matter any more.</summary>
        TooWeak,
    }

    /// <summary>
    /// One leg of one fragment: from the charge (or its last ricochet, wall or wound) to where
    /// it ended. The arc is p(t) = <see cref="Start"/> + <see cref="ArcVelocity"/>·t + ½·g·t²
    /// for t in [0, <see cref="Time"/>], which reaches <see cref="End"/> - the shape the hit test
    /// swept, for drawing. <see cref="SpeedIn"/> and <see cref="SpeedOut"/> are the fragment's
    /// physical speed, which is what penetration and wounds follow.
    /// </summary>
    public struct FragmentTrace
    {
        public ExplosionSpec Spec;
        public int        Owner;
        public bool       Cosmetic;
        /// <summary>Which fragment of this detonation, and which leg of it (0 = from the charge).</summary>
        public int        Fragment, Leg;
        /// <summary>One of the spec's shaped-charge jet rays (<see cref="ExplosionSpec.JetRays"/>).</summary>
        public bool       Jet;
        /// <summary>Behind-armour spall a jet blew off the back of a wall (<see cref="ExplosionSpec.JetSpallCount"/>).</summary>
        public bool       Spall;
        /// <summary>A piece of bone thrown out of an exit wound (<see cref="ExplosionSpec.BoneFragments"/>).</summary>
        public bool       Bone;
        public Vector3    Start, ArcVelocity, End;
        public float      Time;
        public FragmentEnd EndedBy;
        /// <summary>What it hit, if anything; null for <see cref="FragmentEnd.Spent"/>.</summary>
        public Collider   Collider;
        public Vector3    Normal;
        /// <summary>Degrees from the surface normal; 90 is a perfect graze.</summary>
        public float      Incidence;
        /// <summary>The surface it met; null for limbs and misses.</summary>
        public SurfaceMaterial Material;
        /// <summary>Far side of a wall or a limb, for <see cref="FragmentEnd.Penetrated"/> and <see cref="FragmentEnd.PassedThrough"/>.</summary>
        public Vector3    Exit;
        public float      Thickness;
        /// <summary>m/s on arriving at <see cref="End"/> and on leaving it (0 when it stopped there).</summary>
        public float      SpeedIn, SpeedOut;
    }

    /// <summary>
    /// How much of a detonation's shockwave and overpressure reached one body or limb, after
    /// whatever stands between it and the charge. Raised once per target.
    /// </summary>
    public struct BlastTrace
    {
        public ExplosionSpec Spec;
        public int       Owner;
        public Vector3   Origin, Target;
        /// <summary>True for an overpressure (wound) check on a limb, false for the shockwave push on a body.</summary>
        public bool      Overpressure;
        /// <summary>0..1: the fraction that got through. 1 = open line of sight.</summary>
        public float     Transmission;
        /// <summary>The first thing in the way, or null.</summary>
        public Collider  Occluder;
        public SurfaceMaterial OccluderMaterial;
        /// <summary>Reflected peak overpressure on the target after cover, kPa, for charges with a
        /// <see cref="ExplosionSpec.ChargeKgTNT"/>; 0 otherwise.</summary>
        public float     PeakKPa;
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

        /// <summary>
        /// Raised for every round, every frame, before it moves - the place to steer, slow,
        /// teleport or <see cref="Projectile.Kill"/> it. For a plain acceleration a
        /// <see cref="FruitForces"/> field is simpler and composes with other mods'.
        /// </summary>
        public static event Action<Projectile, float> ProjectileStep;

        /// <summary>
        /// Raised when a round meets a surface that is not a body, before FruitLib decides what
        /// happens: set <see cref="SurfaceDecision.Outcome"/> for shields, portals and armour, or
        /// swap <see cref="SurfaceDecision.Material"/>. Handlers run in subscription order and
        /// each sees what the one before left.
        /// </summary>
        public static event SurfaceDecisionHandler BeforeSurfaceHit;
        public static event Action<WoundInfo>      LimbWounded;
        public static event Action<ExplosionInfo>  Exploded;
        /// <summary>A sampled fragment's arc, for debris visuals: whose explosion, start,
        /// velocity, flight time. Visuals belong to the mod that owns the spec - filter on it.</summary>
        public static event Action<ExplosionSpec, Vector3, Vector3, float> DebrisArc;

        /// <summary>
        /// Every leg of every fragment of every explosion: where it flew, what it hit, and whether
        /// it ricocheted, went through or stopped. Thousands per detonation, so keep handlers
        /// cheap and filter on <see cref="FragmentTrace.Spec"/> first. Only built while someone
        /// listens. Fragments raise this instead of <see cref="SurfaceHit"/>, whose handlers
        /// expect a <see cref="Projectile"/>.
        /// </summary>
        public static event Action<FragmentTrace> FragmentTraced;

        /// <summary>What each body and limb in range got of the blast, after cover. Only built
        /// while someone listens; cosmetic detonations push nothing and raise none.</summary>
        public static event Action<BlastTrace> BlastTraced;

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
            if (spec.ChargeKgTNT > 0f) FruitBlastInjury.Wanted = true;
        }

        public static bool TryGetProjectile(string id, out ProjectileSpec spec) => _projectiles.TryGetValue(id ?? "", out spec);
        public static bool TryGetExplosion(string id, out ExplosionSpec spec)   => _explosions.TryGetValue(id ?? "", out spec);

        // ── Spawning ────────────────────────────────────────────────────────────

        /// <summary>Fires a registered round. Returns it, or null if it was intercepted or unknown.</summary>
        public static Projectile SpawnProjectile(string specId, Vector3 origin, Vector3 direction)
            => SpawnProjectile(specId, origin, direction, 0);

        /// <summary>Fires a registered round on behalf of <paramref name="owner"/> (see <see cref="BallisticsCommand.Owner"/>).</summary>
        public static Projectile SpawnProjectile(string specId, Vector3 origin, Vector3 direction, int owner)
            => Request(new BallisticsCommand
            {
                Kind = BallisticsCommandKind.Projectile, SpecId = specId,
                Origin = origin, Direction = direction.normalized, Seed = NextSeed(), Owner = owner,
            }) as Projectile;

        /// <summary>Detonates a registered explosive. <paramref name="forward"/> aims a cone;
        /// a full sphere ignores it.</summary>
        public static void SpawnExplosion(string specId, Vector3 origin, Vector3 forward)
            => SpawnExplosion(specId, origin, forward, 0);

        /// <summary>Detonates a registered explosive on behalf of <paramref name="owner"/>.</summary>
        public static void SpawnExplosion(string specId, Vector3 origin, Vector3 forward, int owner)
            => SpawnExplosion(specId, origin, forward, owner, ExplosionFeatures.All);

        /// <summary>
        /// Detonates a registered explosive with only <paramref name="features"/> of it: parts the
        /// spec has on but the mask leaves out are skipped for this detonation alone. The mask
        /// can't turn on what the spec has off. E.g. a charge that doesn't go through walls:
        /// <c>SpawnExplosion(id, at, fwd, 0, ExplosionFeatures.All &amp; ~ExplosionFeatures.WallPenetration)</c>.
        /// </summary>
        public static void SpawnExplosion(string specId, Vector3 origin, Vector3 forward, int owner, ExplosionFeatures features)
            => Request(new BallisticsCommand
            {
                Kind = BallisticsCommandKind.Explosion, SpecId = specId,
                Origin = origin, Direction = forward.sqrMagnitude > 0f ? forward.normalized : Vector3.up,
                Seed = NextSeed(), Owner = owner, Disabled = ExplosionFeatures.All & ~features,
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

            if (!cosmetic) Raise(Issued, cmd, _issuedList);
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
                    foreach (Action h in _movedList.Of(moved))
                        try { h(); } catch (Exception e) { Report(h, e); }
            }
            FruitEjecta.Tick();
            FruitBlastInjury.Tick();
        }

        internal static void ResetForScene()
        {
            FruitProjectiles.Clear();
            FruitWounds.ResetForScene();
            FruitEjecta.ResetForScene();
            FruitSurfaces.ResetForScene();
            FruitBlastInjury.ResetForScene();
        }

        // ── Event raising. One subscriber throwing must not stop the rest, or the round. ──

        /// <summary>
        /// One event's subscribers as an array, rebuilt only when the event's delegate changes
        /// (someone subscribed or unsubscribed). GetInvocationList allocates every call, and
        /// fragments raise thousands of times per detonation.
        /// </summary>
        private sealed class ListenerCache
        {
            private sealed class Snapshot { public Delegate Source; public Delegate[] List; }
            private Snapshot _snap;

            public Delegate[] Of(Delegate d)
            {
                var s = _snap;
                if (s != null && ReferenceEquals(s.Source, d)) return s.List;
                s = new Snapshot { Source = d, List = d.GetInvocationList() };
                _snap = s;
                return s.List;
            }
        }

        private static readonly ListenerCache _movedList = new ListenerCache();
        private static readonly ListenerCache _spawnedList = new ListenerCache();
        private static readonly ListenerCache _endedList = new ListenerCache();
        private static readonly ListenerCache _surfaceHitList = new ListenerCache();
        private static readonly ListenerCache _stepList = new ListenerCache();
        private static readonly ListenerCache _beforeSurfaceList = new ListenerCache();
        private static readonly ListenerCache _woundedList = new ListenerCache();
        private static readonly ListenerCache _explodedList = new ListenerCache();
        private static readonly ListenerCache _debrisList = new ListenerCache();
        private static readonly ListenerCache _fragmentList = new ListenerCache();
        private static readonly ListenerCache _blastList = new ListenerCache();
        private static readonly ListenerCache _issuedList = new ListenerCache();

        internal static void RaiseSpawned(Projectile p)       => Raise(ProjectileSpawned, p, _spawnedList);
        internal static void RaiseEnded(Projectile p)         => Raise(ProjectileEnded, p, _endedList);
        internal static void RaiseSurfaceHit(SurfaceHitInfo h) => Raise(SurfaceHit, h, _surfaceHitList);

        internal static bool WantsStep => ProjectileStep != null;
        internal static void RaiseStep(Projectile p, float dt)
        {
            var evt = ProjectileStep;
            if (evt == null) return;
            foreach (Action<Projectile, float> h in _stepList.Of(evt))
                try { h(p, dt); } catch (Exception e) { Report(h, e); }
        }

        internal static void RaiseBeforeSurfaceHit(ref SurfaceDecision d)
        {
            var evt = BeforeSurfaceHit;
            if (evt == null) return;
            foreach (SurfaceDecisionHandler h in _beforeSurfaceList.Of(evt))
                try { h(ref d); } catch (Exception e) { Report(h, e); }
        }
        internal static void RaiseWounded(WoundInfo w)        => Raise(LimbWounded, w, _woundedList);
        internal static void RaiseExploded(ExplosionInfo x)   => Raise(Exploded, x, _explodedList);

        internal static bool WantsDebris => DebrisArc != null;
        internal static void RaiseDebris(ExplosionSpec s, Vector3 p0, Vector3 v, float t)
        {
            var d = DebrisArc;
            if (d == null) return;
            foreach (Action<ExplosionSpec, Vector3, Vector3, float> h in _debrisList.Of(d))
                try { h(s, p0, v, t); } catch (Exception e) { Report(h, e); }
        }

        internal static bool WantsFragments => FragmentTraced != null;
        internal static void RaiseFragment(in FragmentTrace t) => Raise(FragmentTraced, t, _fragmentList);

        internal static bool WantsBlast => BlastTraced != null;
        internal static void RaiseBlast(in BlastTrace t) => Raise(BlastTraced, t, _blastList);

        private static void Raise<T>(Action<T> evt, T arg, ListenerCache cache)
        {
            if (evt == null) return;
            foreach (Action<T> h in cache.Of(evt))
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
