using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>What a force field is asked about: one moving thing, this step.</summary>
    public struct ForceQuery
    {
        public Vector3 Position;
        public Vector3 Velocity;
        /// <summary>kg. 0 when the caller has no mass to give (<see cref="FruitForces.SampleAt"/>).</summary>
        public float   MassKg;
        public float   Dt;
        /// <summary>The FruitLib round being moved, or null for anything else.</summary>
        public Projectile Projectile;

        public string SpecId => Projectile?.Spec?.Id;
    }

    /// <summary>Acceleration (m/s²) a field applies to the queried thing. Return zero to leave it alone.</summary>
    public delegate Vector3 ForceField(ForceQuery q);

    /// <summary>Optional filters for a <see cref="ForceField"/>, checked before it is called.</summary>
    public sealed class ForceOptions
    {
        /// <summary>Only rounds whose spec id starts with this, e.g. "GunsGunsGuns.". Null = everything.</summary>
        public string SpecPrefix;
        /// <summary>Also bend cosmetic rounds (a remote player's shot drawn here). On by default, so
        /// what a client sees follows the same curve as what the host simulates.</summary>
        public bool IncludeCosmetic = true;
        /// <summary>Also asked for things that are not FruitLib rounds (<see cref="FruitForces.SampleAt"/>).</summary>
        public bool IncludeOther = true;
    }

    /// <summary>
    /// A shared registry of ambient force fields and winds, so one mod's physics can influence
    /// another's without either referencing the other.
    ///
    /// A mod that creates a force (a gravity well, a repulsor, a magnet) registers a
    /// <see cref="ForceField"/>; every FruitLib round adds it to its acceleration each step.
    /// A field is told which round it is looking at, so it can pick its targets, scale by
    /// mass, or push along the round's velocity. Anything else that moves under its own
    /// integration can ask <see cref="SampleAt"/>.
    ///
    /// Winds are different: they move the air, not the round. A round's drag is computed
    /// against the air around it, so a crosswind drifts a slow heavy round less than a light
    /// one, as it should.
    ///
    /// Called per moving object per frame, so keep fields cheap: no allocation, no scene
    /// queries. A field that throws is reported once and switched off.
    ///
    /// To do more than accelerate a round - redirect it, teleport it, stop it, change what a
    /// surface does to it - see <see cref="FruitBallistics.ProjectileStep"/> and
    /// <see cref="FruitBallistics.BeforeSurfaceHit"/>.
    /// </summary>
    public static class FruitForces
    {
        /// <summary>The pre-5.3 field shape: acceleration from position alone.</summary>
        public delegate Vector3 ForceSampler(Vector3 position);

        private sealed class Entry
        {
            public string       Id;
            public ForceField   Field;
            public ForceOptions Options;
            public bool         Failed;
        }

        private sealed class Wind
        {
            public string                 Id;
            public Func<Vector3, Vector3> At;
            public bool                   Failed;
        }

        private static readonly List<Entry> _fields = new List<Entry>();
        private static readonly List<Wind>  _winds  = new List<Wind>();

        /// <param name="id">Stable identifier, e.g. "Singularity:Wells". Re-registering replaces.</param>
        public static void Register(string id, ForceField field, ForceOptions options = null)
        {
            if (string.IsNullOrEmpty(id) || field == null) return;
            var entry = new Entry { Id = id, Field = field, Options = options };
            int i = _fields.FindIndex(e => e.Id == id);
            if (i >= 0) _fields[i] = entry; else _fields.Add(entry);
            FruitLog.Info($"[FruitForces] registered '{id}'");
        }

        /// <summary>A field that only needs the position. Kept for mods built before 5.3.</summary>
        public static void Register(string id, ForceSampler sampler)
        {
            if (sampler == null) return;
            Register(id, q => sampler(q.Position));
        }

        /// <summary>
        /// Air movement, m/s, at a world position. Rounds feel it through drag, so it bends a
        /// round by how much the air pushes on it - not by a fixed acceleration. Re-registering
        /// an id replaces it.
        /// </summary>
        public static void RegisterWind(string id, Func<Vector3, Vector3> windAt)
        {
            if (string.IsNullOrEmpty(id) || windAt == null) return;
            var wind = new Wind { Id = id, At = windAt };
            int i = _winds.FindIndex(w => w.Id == id);
            if (i >= 0) _winds[i] = wind; else _winds.Add(wind);
            FruitLog.Info($"[FruitForces] registered wind '{id}'");
        }

        /// <summary>Removes a field or a wind.</summary>
        public static void Unregister(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (_fields.RemoveAll(e => e.Id == id) + _winds.RemoveAll(w => w.Id == id) > 0)
                FruitLog.Info($"[FruitForces] unregistered '{id}'");
        }

        public static bool Any     => _fields.Count > 0;
        public static bool AnyWind => _winds.Count > 0;

        /// <summary>Summed acceleration from every field at a world position, for anything that is
        /// not a FruitLib round (debris, VFX motes). Fields see no velocity, mass or round.</summary>
        public static Vector3 SampleAt(Vector3 position)
            => _fields.Count == 0 ? Vector3.zero : Accelerate(new ForceQuery { Position = position });

        /// <summary>Summed acceleration from every field that accepts <paramref name="q"/>.</summary>
        public static Vector3 Accelerate(ForceQuery q)
        {
            Vector3 total = Vector3.zero;
            var p = q.Projectile;
            for (int i = 0; i < _fields.Count; i++)
            {
                var e = _fields[i];
                if (e.Failed) continue;

                var o = e.Options;
                if (o != null)
                {
                    if (p == null) { if (!o.IncludeOther) continue; }
                    else
                    {
                        if (p.Cosmetic && !o.IncludeCosmetic) continue;
                        if (o.SpecPrefix != null && (p.Spec?.Id == null || !p.Spec.Id.StartsWith(o.SpecPrefix, StringComparison.Ordinal))) continue;
                    }
                }

                try { total += e.Field(q); }
                catch (Exception ex)
                {
                    // Once, then off: this runs per round per frame, and a misbehaving field
                    // shouldn't take out everyone else's physics or bury the log.
                    e.Failed = true;
                    MelonLogger.Warning($"[FruitForces] '{e.Id}' threw and was switched off: {ex}");
                }
            }
            return total;
        }

        /// <summary>Summed air velocity from every wind at a world position, m/s.</summary>
        public static Vector3 WindAt(Vector3 position)
        {
            Vector3 total = Vector3.zero;
            for (int i = 0; i < _winds.Count; i++)
            {
                var w = _winds[i];
                if (w.Failed) continue;
                try { total += w.At(position); }
                catch (Exception ex)
                {
                    w.Failed = true;
                    MelonLogger.Warning($"[FruitForces] wind '{w.Id}' threw and was switched off: {ex}");
                }
            }
            return total;
        }
    }
}
