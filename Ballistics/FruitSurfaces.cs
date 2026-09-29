using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// What a round meets when it hits something that is not a body.
    ///
    /// Penetration uses the Poncelet equation: the surface resists with a strength term (it has
    /// to be crushed) plus an inertial term (it has to be pushed aside), F = A·(σ + ρ·v²). That
    /// gives, for a round of sectional density SD = m / A entering at v:
    /// <code>
    ///   depth      = SD / (2ρ) · ln(1 + ρ·v² / σ)
    ///   exit speed : v'² = (v² + σ/ρ) · exp(−2ρ·t / SD) − σ/ρ   after thickness t
    /// </code>
    /// Two numbers per material, and a 9 mm FMJ comes out at ~13 cm of pine, a 7.62×39 at
    /// ~36 cm, and ~7 cm of concrete - close to published box tests. A pure energy model gets
    /// rifles about twice as deep as they really go.
    ///
    /// Ricochet is relative to the round's own <see cref="ProjectileSpec.RicochetAngle"/>, which
    /// is its angle on concrete: steel deflects rounds earlier, wood and soil later.
    /// </summary>
    public sealed class SurfaceMaterial
    {
        public string Name;

        /// <summary>kg/m³: the inertial part of the resistance.</summary>
        public float Density = 2400f;
        /// <summary>Pa: the strength part of the resistance, roughly how hard the material is to crush.</summary>
        public float Strength = 2e8f;

        /// <summary>Added to the round's <see cref="ProjectileSpec.RicochetAngle"/>. Negative = ricochets
        /// at steeper hits than on concrete (steel), positive = only at flatter ones (wood, soil).</summary>
        public float RicochetAngleShift = 0f;
        /// <summary>Of the speed into the surface, how much comes back out on a ricochet (0..1).
        /// Low values leave a ricochet flatter than the hit, as real ones do.</summary>
        public float Restitution = 0.2f;
        /// <summary>Of the speed along the surface, how much a ricochet keeps (0..1).</summary>
        public float Grip = 0.75f;
        /// <summary>Scales the round's <see cref="ProjectileSpec.RicochetEnergyLoss"/> on this surface.</summary>
        public float RicochetLossScale = 1f;

        /// <summary>Random deflection on leaving the far side, degrees at a total loss of speed;
        /// scaled by the fraction of speed the round lost getting through.</summary>
        public float ExitScatter = 8f;
        /// <summary>Thicker than this counts as solid, metres: not probed at all.</summary>
        public float MaxThickness = 1f;
        public bool  Penetrable = true;

        /// <summary>Of an explosion's shockwave and overpressure, the fraction that gets through
        /// a wall of this (0..1). Blast also wraps round cover, which the explosion's own
        /// <see cref="ExplosionSpec.BlastDiffraction"/> floors this at.</summary>
        public float BlastTransmission = 0.05f;

        /// <summary>How much of its back face a wall of this sheds when a jet goes through it,
        /// relative to concrete (1). Brittle things scab and shatter; steel spalls less, wood
        /// splinters, drywall crumbles, soil and water don't spall at all.</summary>
        public float Spall = 1f;

        public SurfaceMaterial Clone() => (SurfaceMaterial)MemberwiseClone();

        /// <summary>Chance (0..1) that something whose ricochet angle on concrete is
        /// <paramref name="angleOnConcrete"/> glances off this at <paramref name="incidence"/>
        /// degrees from the normal, ramping over <paramref name="band"/> degrees either side.</summary>
        internal float RicochetChance(float angleOnConcrete, float incidence, float band)
        {
            float critical = Mathf.Clamp(angleOnConcrete + RicochetAngleShift, 0f, 89.5f);
            return Mathf.InverseLerp(critical - band, critical + band, incidence);
        }

        /// <summary>
        /// The velocity off the surface: the part along it mostly survives (<see cref="Grip"/>),
        /// the part into it mostly does not (<see cref="Restitution"/>), so it leaves flatter
        /// than it came in. Speed is settled separately, by energy.
        /// </summary>
        internal Vector3 Bounce(Vector3 v, Vector3 normal)
        {
            Vector3 vn = Vector3.Project(v, normal);
            Vector3 outV = (v - vn) * Grip - vn * Restitution;
            return outV.sqrMagnitude < 1e-6f ? normal : outV;
        }

        /// <summary>How deep a round of sectional density <paramref name="sd"/> (kg/m²) entering at
        /// <paramref name="speed"/> m/s gets, in metres.</summary>
        public float Depth(float sd, float speed)
        {
            if (!Penetrable || sd <= 0f || Density <= 0f || Strength <= 0f) return 0f;
            return sd / (2f * Density) * Mathf.Log(1f + Density * speed * speed / Strength);
        }

        /// <summary>Speed after <paramref name="thickness"/> metres, or 0 if the round stops inside.</summary>
        public float ExitSpeed(float sd, float speed, float thickness)
        {
            if (!Penetrable || sd <= 0f || Density <= 0f) return 0f;
            float k = Strength / Density;
            float v2 = (speed * speed + k) * Mathf.Exp(-2f * Density * thickness / sd) - k;
            return v2 > 0f ? Mathf.Sqrt(v2) : 0f;
        }
    }

    /// <summary>
    /// Which <see cref="SurfaceMaterial"/> a collider is made of.
    ///
    /// Resolution, first answer wins, cached per collider until the scene changes:
    /// 1. resolvers added with <see cref="AddResolver"/>, newest first - a mod that knows its
    ///    own objects says so here;
    /// 2. keywords (<see cref="AddKeyword"/>) found in the collider's object name, its parents'
    ///    names, or its renderer's material name;
    /// 3. a terrain is Soil, a loose rigidbody is Wood (a prop), anything else is Concrete.
    ///
    /// The game has no surface types of its own, so the keyword table is a starting guess.
    /// FruitLib's ballistics probe (Diagnostics > Log ballistics, then the aim key on a wall)
    /// prints what a surface resolves to and why.
    /// </summary>
    public static class FruitSurfaces
    {
        private static readonly Dictionary<string, SurfaceMaterial> _materials =
            new Dictionary<string, SurfaceMaterial>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<(string keyword, string material)> _keywords = new List<(string, string)>();   // mods' own, checked first
        private static readonly List<(string keyword, string material)> _builtIn  = new List<(string, string)>();
        private static readonly List<Func<Collider, SurfaceMaterial>> _resolvers = new List<Func<Collider, SurfaceMaterial>>();
        private static readonly Dictionary<int, (SurfaceMaterial m, string why)> _cache = new Dictionary<int, (SurfaceMaterial, string)>();

        public static SurfaceMaterial Concrete => Get("Concrete");

        static FruitSurfaces()
        {
            // Strength calibrated against published penetration of 9 mm FMJ and 7.62×39 FMJ;
            // treat the rest as starting points.
            // BlastTransmission is a guess by kind of wall: masonry and steel stop a blast,
            // stud walls and glass mostly don't (they fail and let it through). Spall likewise:
            // brittle things scab, steel less, wood splinters, drywall crumbles, soil and water don't.
            Register(new SurfaceMaterial { Name = "Concrete", Density = 2400f, Strength = 2.0e8f, RicochetAngleShift =   0f, Restitution = 0.20f, Grip = 0.75f, RicochetLossScale = 1.0f, ExitScatter = 10f, MaxThickness = 0.5f, BlastTransmission = 0.03f, Spall = 1.0f });
            Register(new SurfaceMaterial { Name = "Brick",    Density = 1900f, Strength = 1.2e8f, RicochetAngleShift =   2f, Restitution = 0.15f, Grip = 0.70f, RicochetLossScale = 1.1f, ExitScatter = 10f, MaxThickness = 0.5f, BlastTransmission = 0.05f, Spall = 1.1f });
            Register(new SurfaceMaterial { Name = "Steel",    Density = 7850f, Strength = 3.0e9f, RicochetAngleShift =  -8f, Restitution = 0.30f, Grip = 0.85f, RicochetLossScale = 0.7f, ExitScatter =  6f, MaxThickness = 0.1f, BlastTransmission = 0.02f, Spall = 0.6f });
            Register(new SurfaceMaterial { Name = "Wood",     Density =  500f, Strength = 3.6e7f, RicochetAngleShift =  10f, Restitution = 0.10f, Grip = 0.60f, RicochetLossScale = 1.3f, ExitScatter =  6f, MaxThickness = 1.0f, BlastTransmission = 0.25f, Spall = 0.5f });
            Register(new SurfaceMaterial { Name = "Drywall",  Density =  700f, Strength = 5.0e6f, RicochetAngleShift =  12f, Restitution = 0.10f, Grip = 0.60f, RicochetLossScale = 1.4f, ExitScatter =  4f, MaxThickness = 0.5f, BlastTransmission = 0.50f, Spall = 0.3f });
            Register(new SurfaceMaterial { Name = "Glass",    Density = 2500f, Strength = 5.0e7f, RicochetAngleShift =   3f, Restitution = 0.25f, Grip = 0.80f, RicochetLossScale = 1.0f, ExitScatter =  3f, MaxThickness = 0.1f, BlastTransmission = 0.60f, Spall = 1.2f });
            Register(new SurfaceMaterial { Name = "Soil",     Density = 1600f, Strength = 1.5e7f, RicochetAngleShift =  12f, Restitution = 0.05f, Grip = 0.50f, RicochetLossScale = 1.5f, ExitScatter = 12f, MaxThickness = 2.0f, BlastTransmission = 0.00f, Spall = 0.0f });
            Register(new SurfaceMaterial { Name = "Water",    Density = 1000f, Strength = 1.0e5f, RicochetAngleShift =  14f, Restitution = 0.05f, Grip = 0.80f, RicochetLossScale = 0.9f, ExitScatter = 15f, MaxThickness = 2.0f, BlastTransmission = 0.10f, Spall = 0.0f });

            // Most specific first: the first keyword found wins. Substrings, so nothing that
            // hides inside common words ("iron" in "Environment", "door" in "outdoor", "tree" in "street").
            BuiltIn("Drywall",  "drywall", "plaster", "gypsum");
            BuiltIn("Glass",    "glass", "window");
            BuiltIn("Brick",    "brick");
            BuiltIn("Steel",    "steel", "metal", "container", "pipe", "girder", "barrel");
            BuiltIn("Wood",     "wood", "plank", "crate", "pallet", "fence", "chair");
            BuiltIn("Water",    "water");
            BuiltIn("Soil",     "terrain", "dirt", "soil", "sand", "mud", "grass");
            BuiltIn("Concrete", "concrete", "cement", "asphalt", "stone");
        }

        private static void BuiltIn(string material, params string[] keywords)
        {
            foreach (var k in keywords) _builtIn.Add((k, material));
        }

        /// <summary>Adds or replaces a material by name.</summary>
        public static void Register(SurfaceMaterial m)
        {
            if (m == null || string.IsNullOrEmpty(m.Name)) { MelonLogger.Warning("[FruitSurfaces] material needs a Name"); return; }
            _materials[m.Name] = m;
            _cache.Clear();
        }

        /// <summary>A registered material, or null.</summary>
        public static SurfaceMaterial Get(string name)
            => name != null && _materials.TryGetValue(name, out var m) ? m : null;

        /// <summary>Objects whose name (or parent's, or material's) contains <paramref name="keyword"/>
        /// are <paramref name="material"/>. Case-insensitive; checked in the order added, all before the built-in ones.</summary>
        public static void AddKeyword(string keyword, string material)
        {
            if (string.IsNullOrEmpty(keyword) || string.IsNullOrEmpty(material)) return;
            _keywords.Add((keyword.ToLowerInvariant(), material));
            _cache.Clear();
        }

        /// <summary>
        /// Asked before anything else, newest first. Return a material for colliders you know
        /// (your own props, armour plates) and null for the rest.
        /// </summary>
        public static void AddResolver(Func<Collider, SurfaceMaterial> resolver)
        {
            if (resolver == null) return;
            _resolvers.Insert(0, resolver);
            _cache.Clear();
        }

        public static void RemoveResolver(Func<Collider, SurfaceMaterial> resolver)
        {
            if (_resolvers.Remove(resolver)) _cache.Clear();
        }

        /// <summary>What <paramref name="c"/> is made of. Never null.</summary>
        public static SurfaceMaterial Resolve(Collider c) => Explain(c, out _);

        /// <summary><see cref="Resolve"/>, plus which rule decided it - for the probe.</summary>
        public static SurfaceMaterial Explain(Collider c, out string why)
        {
            if (c == null) { why = "no collider"; return Concrete; }

            int id = c.GetInstanceID();
            if (_cache.TryGetValue(id, out var hit)) { why = hit.why; return hit.m; }

            var m = Lookup(c, out why) ?? Concrete;
            _cache[id] = (m, why);
            return m;
        }

        internal static void ResetForScene() => _cache.Clear();

        /// <summary>
        /// The far side of <paramref name="c"/> along <paramref name="dir"/> from <paramref name="entry"/>,
        /// looking no deeper than <paramref name="reach"/> metres: a cast back at the collider from
        /// that depth. False when it is thicker than that, or the cast started inside it.
        /// </summary>
        internal static bool FarSide(Collider c, Vector3 entry, Vector3 dir, float reach,
                                     out Vector3 exit, out float thickness)
            => FarSide(c, entry, dir, reach, out exit, out thickness, out _);

        /// <param name="exitNormal">The far face's outward normal.</param>
        internal static bool FarSide(Collider c, Vector3 entry, Vector3 dir, float reach,
                                     out Vector3 exit, out float thickness, out Vector3 exitNormal)
        {
            exit = entry; thickness = 0f; exitNormal = dir;
            reach += 0.01f;
            if (!c.Raycast(new Ray(entry + dir * reach, -dir), out RaycastHit back, reach)) return false;
            // A far side faces along the travel. Anything else is the entry face seen from
            // behind - a terrain or one-sided mesh the query hit from below - and taking it
            // would let a round through a surface of no thickness at all.
            if (Vector3.Dot(back.normal, dir) <= 0f) return false;
            exit = back.point;
            exitNormal = back.normal;
            thickness = Mathf.Max(0.001f, reach - back.distance);
            return true;
        }

        private static SurfaceMaterial Lookup(Collider c, out string why)
        {
            foreach (var r in _resolvers)
            {
                try
                {
                    var m = r(c);
                    if (m != null) { why = $"resolver {r.Method.DeclaringType?.Name}.{r.Method.Name}"; return m; }
                }
                catch (Exception e) { MelonLogger.Warning($"[FruitSurfaces] resolver {r.Method.Name} threw: {e.Message}"); }
            }

            // Names: the collider's object and up to three parents, then its renderer's material.
            var t = c.transform;
            for (int depth = 0; t != null && depth < 4; depth++, t = t.parent)
                if (Keyword(t.name, out var m, out var kw)) { why = $"'{kw}' in '{t.name}'"; return m; }

            try
            {
                var rend = c.GetComponent<Renderer>();
                var mat  = rend != null ? rend.sharedMaterial : null;
                if (mat != null && Keyword(mat.name, out var m, out var kw)) { why = $"'{kw}' in material '{mat.name}'"; return m; }
            }
            catch { }

            if (c.TryCast<TerrainCollider>() != null) { why = "terrain"; return Get("Soil"); }

            var rb = c.attachedRigidbody;
            if (rb != null && !rb.isKinematic) { why = "loose rigidbody (prop)"; return Get("Wood"); }

            why = "default";
            return Concrete;
        }

        private static bool Keyword(string name, out SurfaceMaterial m, out string keyword)
        {
            m = null; keyword = null;
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            return Match(_keywords, lower, out m, out keyword) || Match(_builtIn, lower, out m, out keyword);
        }

        private static bool Match(List<(string keyword, string material)> table, string lower,
                                  out SurfaceMaterial m, out string keyword)
        {
            foreach (var (kw, mat) in table)
            {
                if (!lower.Contains(kw)) continue;
                m = Get(mat);
                if (m == null) continue;
                keyword = kw;
                return true;
            }
            m = null; keyword = null;
            return false;
        }
    }
}
