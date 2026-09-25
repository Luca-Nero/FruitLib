using System;
using System.Collections.Generic;
using Il2CppEffectors;
using Il2CppEffectors.ReceiveMethods.Index;
using Il2CppEffectors.Types;
using Il2CppInterop.Runtime;
using Il2CppLVA.Organs.EffectorsPerception.Collectors;
using Il2CppSpawnables.Bullets;
using Il2CppSpawnables.Wounds;
using Il2CppVoxelMeshGeneration;
using Il2CppVoxelMeshGeneration.Tools;
using MelonLoader;
using Unity.Mathematics;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// The game's bullet wound, driven from outside the game's bullet.
    ///
    /// This is BodyWoundWalker.WalkThroughBody and SendWoundLayers re-expressed (the release
    /// moved them out of Bullet, where v0_17L had them):
    ///
    /// 1. Step through the limb's voxels along the path with the game's own VoxelRayStepper.
    ///    It yields only voxels that are still there, so an existing wound is crossed free.
    /// 2. At each step, signal the crush shape around it and - once past the clean entry -
    ///    each neighbour with SpreadChance, every signal carrying the round's whole remaining
    ///    power. A visited set means no voxel is paid for twice.
    /// 3. Ask the limb what that would absorb (TryGetFeedback: a dry run, verified in game) and
    ///    subtract it from power. Out of power: the round stops inside.
    /// 4. Send the collected crush signals (this is the entry hole and the permanent channel),
    ///    then the game's ExitTear if the round came out, then its Cavitation if the profile
    ///    has one.
    ///
    /// Checked against the game: a native 7.62 through a torso spent 3563 power and left at
    /// 305 m/s; this, on a comparable path, spent 3397 and would leave at 309.
    ///
    /// Differences, both deliberate: power is spent a whole step at a time (the game stops
    /// voxel by voxel, which needs its per-voxel feedback list - a generic native list that
    /// Il2CppInterop cannot read safely), and hard tissue can be made cheaper through
    /// <see cref="WoundProfile.HardTissueScale"/>.
    /// </summary>
    internal static class FruitWounds
    {
        private const string Tag = "[FruitWounds]";

        internal struct Result
        {
            /// <summary>False when the path found no voxels at all - a hole, or a collider
            /// wider than the mesh. The round should carry on as if the limb were not there.</summary>
            public bool    Touched;
            public bool    Exited;
            public int     PowerOut;
            public int     Steps;
            public Vector3 Exit;
        }

        // ── Limbs ────────────────────────────────────────────────────────────────

        private static readonly Il2CppSystem.Type LimbType = Il2CppType.Of<LimbEffectorReceiver>();
        private static readonly Dictionary<int, LimbEffectorReceiver> _limbByCollider = new Dictionary<int, LimbEffectorReceiver>();

        internal static void ResetForScene()
        {
            _limbByCollider.Clear();
            _spreadResolved = false;
            _nextSpreadLookup = 0f;
        }

        internal static LimbEffectorReceiver LimbOf(Collider c)
        {
            if (c == null) return null;
            int id = c.GetInstanceID();
            if (_limbByCollider.TryGetValue(id, out var cached) && cached != null) return cached;

            var comp = c.GetComponentInParent(LimbType);
            var limb = comp != null ? comp.TryCast<LimbEffectorReceiver>() : null;
            _limbByCollider[id] = limb;
            return limb;
        }

        internal static bool IsLimb(GameObject go) => go != null && go.GetComponentInParent(LimbType) != null;

        internal static Rigidbody BodyOf(Collider c) =>
            c == null ? null : (c.attachedRigidbody != null ? c.attachedRigidbody : c.GetComponentInParent<Rigidbody>());

        // ── Hits ─────────────────────────────────────────────────────────────────
        //
        // Since the release every wound signal carries an EffectorHit (Source, Number). The
        // game's bullet stamps all of one shot's layers - crush, exit tear, cavitation - with
        // EffectorHit(launcher, shot): the ShotBus that fired it and the number ShotBus.Open
        // handed out, which every pellet of a shotgun shot shares. Pain reads it: an organ
        // that sees the same hit again reuses the pain anchor it already chose instead of
        // picking a new one per layer. EffectorHit.Equals is false whenever Source is null,
        // so EffectorHit.None would make every layer look like a fresh hit.
        //
        // FruitLib has no launcher object, so one sentinel per kind stands in for it. A
        // round is one shot; a detonation is one shot and its fragments are its rounds.

        private static Il2CppSystem.Object _roundSource;
        private static Il2CppSystem.Object _blastSource;
        private static int _blasts;

        internal static EffectorHit RoundHit(int roundId)
        {
            if (_roundSource == null) _roundSource = new Il2CppSystem.Object();
            return new EffectorHit(_roundSource, roundId);
        }

        internal static EffectorHit NextBlastHit()
        {
            if (_blastSource == null) _blastSource = new Il2CppSystem.Object();
            return new EffectorHit(_blastSource, ++_blasts);
        }

        // ── Shapes ───────────────────────────────────────────────────────────────

        private static readonly Dictionary<int, List<int3>> _spheres = new Dictionary<int, List<int3>>();

        /// <summary>Offsets inside a voxel sphere. Radius 0 is the single centre voxel.</summary>
        internal static List<int3> Sphere(int radius)
        {
            radius = Mathf.Max(0, radius);
            if (_spheres.TryGetValue(radius, out var s)) return s;

            s = new List<int3>();
            int rr = radius * radius;
            for (int x = -radius; x <= radius; x++)
            for (int y = -radius; y <= radius; y++)
            for (int z = -radius; z <= radius; z++)
                if (x * x + y * y + z * z <= rr) s.Add(new int3(x, y, z));
            _spheres[radius] = s;
            return s;
        }

        private static readonly List<int3> FaceNeighbours = new List<int3>
        {
            new int3( 1, 0, 0), new int3(-1, 0, 0),
            new int3( 0, 1, 0), new int3( 0,-1, 0),
            new int3( 0, 0, 1), new int3( 0, 0,-1),
        };

        private static List<int3> _spread = FaceNeighbours;
        private static bool  _spreadResolved;
        private static float _nextSpreadLookup;

        /// <summary>
        /// The spread shape the game gives its own rounds, once one has been fired this scene.
        /// Since the release it lives on the round's BodyWoundWalker, which only fetches it
        /// from the voxel shapes provider when it takes its wound dials, so the pooled prefab
        /// never has it. Until then the six face neighbours, which matched the game within 5%
        /// in testing.
        /// </summary>
        private static List<int3> SpreadOffsets()
        {
            if (_spreadResolved || Time.unscaledTime < _nextSpreadLookup) return _spread;
            _nextSpreadLookup = Time.unscaledTime + 10f;
            try
            {
                foreach (var b in Resources.FindObjectsOfTypeAll<Bullet762>())
                {
                    var walker = b != null ? b.m_walker : null;
                    var list = walker != null ? walker.m_spreadOffsets : null;
                    if (list == null) continue;

                    int n = list.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<int3>>().Count;
                    var offsets = new List<int3>(n);
                    for (int i = 0; i < n; i++) offsets.Add(list[i]);
                    if (offsets.Count == 0) continue;

                    _spread = offsets;
                    _spreadResolved = true;
                    MelonLogger.Msg($"{Tag} using the game's own spread shape ({offsets.Count} offsets)");
                    break;
                }
            }
            catch (Exception e)
            {
                _spreadResolved = true;   // do not keep paying for a lookup that throws
                MelonLogger.Warning($"{Tag} could not read the game's spread shape, keeping face neighbours: {e.Message}");
            }
            return _spread;
        }

        // ── The channel ─────────────────────────────────────────────────────────

        private static readonly HashSet<int3> _visited     = new HashSet<int3>();
        private static readonly List<int3>    _step        = new List<int3>();
        private static readonly List<int3>    _path        = new List<int3>();
        private static readonly List<int3>    _crushIdx    = new List<int3>();
        private static readonly List<float>   _crushForce  = new List<float>();
        private static readonly List<Color>   _ejectColors = new List<Color>();

        /// <param name="initialPower">What the round started with. Exit tear size is the
        /// leftover as a fraction of it, and the impulse scales with arrival power over it.</param>
        /// <param name="hit">Stamped on every layer; see <see cref="RoundHit"/>.</param>
        /// <param name="firstBody">Only the first body a round enters gets a clean entry.</param>
        internal static Result Channel(LimbEffectorReceiver limb, Rigidbody body, Vector3 entry, Vector3 normal,
                                       Vector3 dir, int power, int initialPower, WoundProfile w, EffectorHit hit,
                                       System.Random rng, bool firstBody, bool cosmetic)
        {
            var result = new Result { PowerOut = power };
            var mesh = limb != null ? limb.VoxelMesh : null;
            if (mesh == null || power < 1 || w == null) return result;

            FruitPerfMon.Begin("FruitWounds");
            try
            {
                int powerIn = power;
                dir = dir.normalized;

                int clean = 0;
                if (firstBody)
                {
                    // BodyWoundWalker.GetCleanEntrySteps: stretched for an oblique entry, and for a
                    // diagonal path, which crosses voxels faster than an axis-aligned one.
                    float cos    = Mathf.Max(Mathf.Abs(Vector3.Dot(normal, dir)), 0.2f);
                    float maxAbs = Mathf.Max(Mathf.Abs(dir.x), Mathf.Max(Mathf.Abs(dir.y), Mathf.Abs(dir.z)));
                    clean = Mathf.RoundToInt(w.CleanEntryDepth / (cos / Mathf.Max(0.0001f, maxAbs)));
                }

                var crush  = Sphere(w.CrushRadius);
                var spread = w.SpreadChance > 0f ? SpreadOffsets() : null;

                _visited.Clear(); _path.Clear(); _crushIdx.Clear(); _crushForce.Clear();
                bool exhausted = false;
                int steps = 0;

                var stepper = new VoxelRayStepper(mesh, entry, dir);
                while (stepper.TryGetNext(out VoxelPositionData p))
                {
                    if (steps > w.MaxDepth) { exhausted = true; break; }

                    var idx = new int3(p.Index.x, p.Index.y, p.Index.z);
                    _path.Add(idx);

                    _step.Clear();
                    foreach (var o in crush)
                        if (_visited.Add(idx + o)) _step.Add(idx + o);
                    if (spread != null && steps >= clean)
                        foreach (var o in spread)
                            if (rng.NextDouble() < w.SpreadChance && _visited.Add(idx + o)) _step.Add(idx + o);

                    steps++;
                    if (_step.Count == 0) continue;

                    int cost = Price(limb, _step, power, w.HardTissueScale);
                    if (cost < 0) continue;   // nothing there to pay for; the game skips these too

                    foreach (var v in _step) { _crushIdx.Add(v); _crushForce.Add(-power); }
                    power -= cost;
                    if (power < 1) { exhausted = true; break; }
                }

                result.Steps = steps;
                if (_path.Count == 0) return result;   // Touched stays false

                result.Touched  = true;
                result.Exited   = !exhausted;
                result.PowerOut = Mathf.Max(0, power);
                var last = _path[_path.Count - 1];
                result.Exit = VoxelTools.VoxelIndexToWorldPosition(mesh, last);

                // Only a perforation throws anything out. "Exited" alone also covers a round
                // that nicked one voxel off a corner - in testing, fragments did that all the
                // time - and an indent or a graze does not blow tissue out of the far side.
                bool ejecta = w.Ejecta && result.Exited && FruitEjecta.Enabled
                              && result.Steps >= FruitEjecta.MinDepth;
                if (ejecta) CollectExitColours(mesh, FruitEjecta.CountFor(result.PowerOut));

                if (!cosmetic)
                {
                    SendLayers(limb, w, dir, clean, result, initialPower, hit);

                    if (body != null && w.ImpactImpulse > 0f)
                    {
                        float scale = powerIn / (float)Mathf.Max(1, initialPower);
                        try { body.AddForceAtPosition(dir * (w.ImpactImpulse * scale), entry, ForceMode.Impulse); } catch { }
                    }
                }

                if (ejecta && _ejectColors.Count > 0)
                    FruitEjecta.Spawn(result.Exit, dir, _ejectColors, body);

                return result;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"{Tag} channel failed: {e.Message}");
                return result;
            }
            finally { FruitPerfMon.End("FruitWounds"); }
        }

        /// <summary>
        /// What a step would cost, from the limb's own dry-run answer. -1 when the limb reports
        /// nothing absorbable there.
        ///
        /// Absorbed influence is the full price; progress is how much voxel actually goes. For
        /// soft tissue they are equal, for bone absorbed is ~74x progress. Scaling only the
        /// excess leaves soft tissue at native cost and lets <paramref name="hardScale"/> thin
        /// out just the bone.
        /// </summary>
        private static int Price(LimbEffectorReceiver limb, List<int3> voxels, int power, float hardScale)
        {
            var list = new IndexEffectorSignalsList(voxels.Count, false);
            foreach (var v in voxels) list.Add(new IndexEffectorSignal(v, -power, InfluenceProcessType.Sum));
            var handler = new IndexEffectorSignalsHandler<Destruction>(list);
            try
            {
                if (!limb.TryGetFeedback(handler, out IReadOnlyIndexEffectorFeedbacksHandler fb) || fb == null) return -1;
                try
                {
                    float absorbed = Mathf.Abs(fb.TotalAbsorbedInfluence);
                    float progress = Mathf.Abs(fb.TotalProgressesChange);
                    float cost = absorbed <= progress ? absorbed : progress + (absorbed - progress) * hardScale;
                    return Mathf.Max(0, Mathf.RoundToInt(cost));
                }
                finally
                {
                    // TryGetFeedback hands ownership to the caller: the limb disposes an empty
                    // one itself and returns the rest.
                    try { fb.TryCast<IndexEffectorFeedbacksHandler>()?.Dispose(); } catch { }
                }
            }
            finally { handler.Dispose(); }
        }

        /// <summary>SendWoundLayers, in the game's order: crush, exit tear, cavitation.</summary>
        private static void SendLayers(LimbEffectorReceiver limb, WoundProfile w, Vector3 dir, int clean,
                                       Result r, int initialPower, EffectorHit hit)
        {
            if (_crushIdx.Count > 0)
            {
                var list = new IndexEffectorSignalsList(_crushIdx.Count, false);
                for (int i = 0; i < _crushIdx.Count; i++)
                    list.Add(new IndexEffectorSignal(_crushIdx[i], _crushForce[i], InfluenceProcessType.Sum));

                // The description carries the direction, which the blood system reads for
                // where the wound faces, and the hit (BodyWoundWalker.SendSignals does the same).
                var handler = new IndexEffectorSignalsHandler<Destruction>(list, new IndexEffectorDescription(dir, hit));
                try { limb.Receive(handler); } finally { handler.Dispose(); }
            }

            if (r.Exited && w.TearMaxRadius > 0f)
            {
                float ratio = r.PowerOut / (float)Mathf.Max(1, initialPower);
                var tear = BulletWoundEffectorSignalsSamples.ExitTear(
                    _path[_path.Count - 1], w.TearMinRadius, w.TearMaxRadius, w.ExitTearDamage, ratio, dir, hit);
                try { limb.Receive(tear); } finally { tear.Dispose(); }
            }

            if (w.CavitationPeakRadius > 0 && _path.Count > 0)
            {
                // Cavitation wants the steps as an il2cpp list; BulletChannel is the game's own
                // container for exactly that.
                var channel = new BulletChannel(dir, clean, _path.Count);
                foreach (var s in _path) channel.AddStep(s);
                var cav = BulletWoundEffectorSignalsSamples.Cavitation(
                    channel.Steps, channel.CleanEntrySteps, w.CavitationPeakRadius, w.CavitationDamage, dir, hit);
                try { limb.Receive(cav); } finally { cav.Dispose(); }
            }
        }

        /// <summary>Tissue colours at the exit, read before the damage lands and they are gone.</summary>
        private static void CollectExitColours(VoxelMesh mesh, int max)
        {
            _ejectColors.Clear();
            var data = mesh.Data;
            for (int i = _crushIdx.Count - 1; i >= 0 && _ejectColors.Count < max; i--)
            {
                try
                {
                    var v = _crushIdx[i];
                    if (data.IsIndexOutOfRange(v)) continue;
                    var vox = data[v];
                    if (!vox.enabled) continue;
                    _ejectColors.Add(FruitEjecta.TissueColour(vox.color));
                }
                catch { }
            }
        }

        // ── Area damage ─────────────────────────────────────────────────────────

        private static float[] _noise;
        private const int NoiseSize = 256;

        /// <summary>
        /// A ragged sphere of damage just under a limb's surface - blast overpressure, not a
        /// channel. Perlin-masked so it reads as torn rather than as a clean ball.
        /// </summary>
        internal static void Burst(LimbEffectorReceiver limb, Vector3 surfacePoint, Vector3 inward,
                                   int radius, float damage, float coverage, EffectorHit shot, System.Random rng)
        {
            var mesh = limb != null ? limb.VoxelMesh : null;
            if (mesh == null || radius < 0) return;

            if (_noise == null)
            {
                _noise = new float[NoiseSize * NoiseSize];
                for (int y = 0; y < NoiseSize; y++)
                for (int x = 0; x < NoiseSize; x++)
                    _noise[y * NoiseSize + x] = Mathf.PerlinNoise(x * 0.12f, y * 0.12f);
            }

            try
            {
                if (!VoxelTools.TryGetEnabledVoxelPositionDataByDirection(mesh, surfacePoint, inward, 0, out var hit)) return;
                var centre = new int3(hit.Index.x, hit.Index.y, hit.Index.z);

                float threshold = Mathf.Lerp(0.62f, 0.2f, Mathf.Clamp01(coverage));
                int ox = rng.Next(NoiseSize), oy = rng.Next(NoiseSize);

                var shape = Sphere(radius);
                var list = new IndexEffectorSignalsList(shape.Count, false);
                foreach (var o in shape)
                {
                    var v = centre + o;
                    int nx = ((v.x + ox) % NoiseSize + NoiseSize) % NoiseSize;
                    int ny = ((v.z + oy) % NoiseSize + NoiseSize) % NoiseSize;
                    if (_noise[ny * NoiseSize + nx] < threshold) continue;
                    list.Add(new IndexEffectorSignal(v, damage, InfluenceProcessType.Sum));
                }
                if (list.Count == 0) { list.Dispose(); return; }

                var handler = new IndexEffectorSignalsHandler<Destruction>(list, new IndexEffectorDescription(inward, shot));
                try { limb.Receive(handler); } finally { handler.Dispose(); }
            }
            catch (Exception e) { MelonLogger.Warning($"{Tag} burst failed: {e.Message}"); }
        }
    }
}
