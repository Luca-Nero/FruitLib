using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2CppEffectors;
using Il2CppEffectors.ReceiveMethods;
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
    /// Answers, from a running game, the questions the shared projectile / explosion API
    /// has to be designed around. Every mod that wounds today sends a flat -10000 per voxel;
    /// the game's own Bullet spends a power budget against what each voxel reports back, and
    /// the plan is for FruitLib to drive that model instead. Whether it can is not something
    /// the interop stubs can say.
    ///
    /// Three things, all off by default:
    ///
    /// - <b>Constants</b>, once per scene: the native calibre values (Bullet9mm, Bullet762)
    ///   and the cavitation envelope. The dnSpy export only has stubs, so this is the only
    ///   place the numbers come from short of Ghidra.
    ///
    /// - <b>Native shot trace</b>, passive: patches on Bullet and its BodyWoundWalker log
    ///   power on entry and exit,
    ///   the channel it walked, and the exit tear it chose. Fire the game's pistol at a body
    ///   and this is the reference every FruitLib projectile gets calibrated against.
    ///
    /// - <b>Aim test</b>, on the key: walks the voxel path under the crosshair and asks the
    ///   limb what each voxel would absorb, without applying anything. That settles whether
    ///   TryGetFeedback is a dry run and gives a resistance-per-voxel profile - the tissue
    ///   map. With Shift held it also runs a replica of the game's own bullet walk down that
    ///   path - power spent per step, crush channel, exit tear, cavitation - which is the
    ///   prototype of FruitLib's wound channel. That one is destructive; aim at something
    ///   expendable.
    ///
    /// Only concrete scalars are read off the feedback handler (Count, TotalAbsorbedInfluence
    /// and so on). The per-voxel Feedbacks list is a ReadOnlyNativeList&lt;T&gt;, and generic-
    /// parameter-typed members read garbage through Il2CppInterop rather than throwing.
    /// </summary>
    internal static class FruitBallisticsProbe
    {
        private const string Tag = "[BallisticsProbe]";

        internal static bool Enabled => FruitHudConfig.BallisticsProbe;

        /// <summary>Voxels walked by the aim test. A limb is tens of voxels across.</summary>
        private const int MaxPathSteps = 256;

        /// <summary>Voxels given their own resistance query. One il2cpp round trip each.</summary>
        private const int MaxResistanceSamples = 48;

        /// <summary>Voxels in the dry-run check's shared signal set.</summary>
        private const int DryRunVoxels = 8;

        /// <summary>
        /// Influence per voxel for the queries. Same sign convention the mods use; smaller
        /// than their -10000 so that a voxel's absorption is not simply capped at "all of it".
        /// </summary>
        private const float ProbeInfluence = -1000f;

        private static bool _dumpedThisScene;

        internal static void ResetForScene()
        {
            _dumpedThisScene = false;
            _powerAtEntry.Clear();
            _paths.Clear();   // their objects went with the scene
        }

        internal static void Tick()
        {
            if (!Enabled) return;

            EnsurePatched();

            if (!_dumpedThisScene)
            {
                _dumpedThisScene = true;
                DumpConstants();
            }

            var key = FruitHudConfig.BallisticsProbeKey;
            if (key != KeyCode.None && Input.GetKeyDown(key))
            {
                bool shift = Input.GetKey(KeyCode.LeftShift)   || Input.GetKey(KeyCode.RightShift);
                bool ctrl  = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                bool alt   = Input.GetKey(KeyCode.LeftAlt)     || Input.GetKey(KeyCode.RightAlt);

                if (ctrl)     FireTestRound(shift ? NativeRoundId : RealRoundId);
                else if (alt) DetonateTestCharge();
                else          AimTest(shift);
            }

            TracePaths();
        }

        // ── FruitBallistics test rig ─────────────────────────────────────────────
        //
        // Ctrl+key        a real 7.62x39 from the camera: 7.9 g at 715 m/s with drag, power
        //                 from its energy (~15100)
        // Ctrl+Shift+key  the game's own 7.62, value for value (15000 power, 400 m/s) - fire it
        //                 and the Lynx at the same body part and the power lines should agree
        // Alt+key         a fragmentation charge at the crosshair
        //
        // Every round's path is drawn while the probe is on, and every wound it makes is logged.

        private const string RealRoundId   = "FruitLib.Test.762x39";
        private const string NativeRoundId = "FruitLib.Test.Native762";
        private const string ChargeId      = "FruitLib.Test.Frag";

        private static bool _rigReady;

        private static void EnsureRig()
        {
            if (_rigReady) return;
            _rigReady = true;

            FruitBallistics.Register(ProjectileSpec.Cartridge(RealRoundId, 7.9f, 7.62f, 715f, 0.29f));
            FruitBallistics.Register(ProjectileSpec.Native762(NativeRoundId));
            FruitBallistics.Register(new ExplosionSpec { Id = ChargeId });

            FruitBallistics.LimbWounded += w =>
            {
                if (!Enabled) return;
                string who = w.Projectile != null ? $"{w.Projectile.Spec.Id}#{w.Projectile.Id}" : "fragment";
                MelonLogger.Msg($"{Tag} {who} wounded {w.Limb?.name}: power {w.PowerIn} -> {w.PowerOut} " +
                                $"(spent {w.PowerIn - w.PowerOut}) over {w.Steps} step(s), " +
                                (w.Exited ? "exited" + (w.Projectile != null ? $", now {w.Projectile.Velocity.magnitude:0} m/s" : "")
                                          : "STOPPED inside") +
                                (w.Exited && w.Steps >= FruitEjecta.MinDepth ? " [perforation: ejecta]" : ""));
            };
            FruitBallistics.SurfaceHit += h =>
            {
                if (!Enabled) return;
                MelonLogger.Msg($"{Tag} {h.Projectile.Spec.Id}#{h.Projectile.Id} hit {h.Collider?.name} at {h.Incidence:0} deg, " +
                                $"power {h.PowerRatio:P0}, {(h.Ricocheted ? "ricocheted" : "stopped")}");
            };
            FruitBallistics.Exploded += x =>
            {
                if (Enabled) MelonLogger.Msg($"{Tag} {x.Spec.Id} detonated at {x.Origin}, ground {(x.HasGround ? x.Ground.distance.ToString("0.0") + " m below" : "none")}");
            };
        }

        private static void FireTestRound(string id)
        {
            EnsureRig();
            var cam = Camera.main;
            if (cam == null) return;
            var p = FruitBallistics.SpawnProjectile(id, cam.transform.position + cam.transform.forward * 0.3f, cam.transform.forward);
            if (p != null)
                MelonLogger.Msg($"{Tag} fired {id}#{p.Id}: {p.Spec.MuzzleVelocity:0} m/s, muzzle power {p.Power}, " +
                                $"drag k {p.Spec.DragK:0.00000}/m");
        }

        private static void DetonateTestCharge()
        {
            EnsureRig();
            var cam = Camera.main;
            if (cam == null) return;
            if (!Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, 200f, ~(1 << 2), QueryTriggerInteraction.Ignore))
            { MelonLogger.Msg($"{Tag} nothing under the crosshair to put the charge on"); return; }
            FruitBallistics.SpawnExplosion(ChargeId, hit.point + hit.normal * 0.1f, hit.normal);
        }

        private sealed class Path { public LineRenderer Line; public float DieAt; }
        private static readonly Dictionary<int, Path> _paths = new Dictionary<int, Path>();
        private static readonly List<int> _finished = new List<int>();
        private static Shader _lineShader;

        /// <summary>Draws every live FruitLib round's path, and keeps it a few seconds after.</summary>
        private static void TracePaths()
        {
            try
            {
                foreach (var r in FruitProjectiles.Live)
                {
                    if (!_paths.TryGetValue(r.Id, out var path))
                    {
                        path = new Path { Line = NewLine(r.Position), DieAt = float.MaxValue };
                        _paths[r.Id] = path;
                    }
                    var lr = path.Line;
                    if (lr == null || lr.positionCount >= 512) continue;
                    lr.positionCount++;
                    lr.SetPosition(lr.positionCount - 1, r.Position);
                }

                _finished.Clear();
                foreach (var kv in _paths)
                {
                    bool live = false;
                    foreach (var r in FruitProjectiles.Live) if (r.Id == kv.Key) { live = true; break; }
                    if (!live && kv.Value.DieAt == float.MaxValue) kv.Value.DieAt = Time.time + 6f;
                    if (Time.time >= kv.Value.DieAt) _finished.Add(kv.Key);
                }
                foreach (var id in _finished)
                {
                    if (_paths[id].Line != null) UnityEngine.Object.Destroy(_paths[id].Line.gameObject);
                    _paths.Remove(id);
                }
            }
            catch (Exception e) { MelonLogger.Warning($"{Tag} path drawing failed: {e.Message}"); }
        }

        private static LineRenderer NewLine(Vector3 start)
        {
            var lr = new GameObject("FruitLib_ProbePath").AddComponent<LineRenderer>();
            _lineShader ??= Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            if (_lineShader != null) lr.material = new Material(_lineShader);
            lr.useWorldSpace = true;
            lr.widthMultiplier = 0.01f;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.startColor = lr.endColor = new Color(0.2f, 1.6f, 2.2f, 1f);
            lr.positionCount = 1;
            lr.SetPosition(0, start);
            return lr;
        }

        // ── Constants ────────────────────────────────────────────────────────────

        private static void DumpConstants()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{Tag} ===== native ballistics constants =====");

            sb.AppendLine("  Bullet (shared)");
            Row(sb, "MAX_LIFETIME",              () => Bullet.MAX_LIFETIME);
            Row(sb, "SELF_DESTRUCT_POWER_RATIO", () => Bullet.SELF_DESTRUCT_POWER_RATIO);
            Row(sb, "HEADING_EPSILON",           () => Bullet.HEADING_EPSILON);

            sb.AppendLine("  BodyWoundWalker (shared)");
            Row(sb, "MIN_ENTRY_COSINE",          () => BodyWoundWalker.MIN_ENTRY_COSINE);

            sb.AppendLine("  calibre                   9mm        7.62");
            Pair(sb, "INITIAL_POWER",         () => Bullet9mm.INITIAL_POWER,         () => Bullet762.INITIAL_POWER);
            Pair(sb, "DESTRUCTION_RADIUS",    () => Bullet9mm.DESTRUCTION_RADIUS,    () => Bullet762.DESTRUCTION_RADIUS);
            Pair(sb, "MAX_PENETRATION_DEPTH", () => Bullet9mm.MAX_PENETRATION_DEPTH, () => Bullet762.MAX_PENETRATION_DEPTH);
            Pair(sb, "CLEAN_ENTRY_DEPTH",     () => Bullet9mm.CLEAN_ENTRY_DEPTH,     () => Bullet762.CLEAN_ENTRY_DEPTH);
            Pair(sb, "CHANNEL_SPREAD_CHANCE", () => Bullet9mm.CHANNEL_SPREAD_CHANCE, () => Bullet762.CHANNEL_SPREAD_CHANCE);
            Pair(sb, "CAVITATION_PEAK_RADIUS",() => Bullet9mm.CAVITATION_PEAK_RADIUS,() => Bullet762.CAVITATION_PEAK_RADIUS);
            Pair(sb, "CAVITATION_DAMAGE",     () => Bullet9mm.CAVITATION_DAMAGE,     () => Bullet762.CAVITATION_DAMAGE);
            Pair(sb, "TEAR_MIN_RADIUS",       () => Bullet9mm.TEAR_MIN_RADIUS,       () => Bullet762.TEAR_MIN_RADIUS);
            Pair(sb, "TEAR_MAX_RADIUS",       () => Bullet9mm.TEAR_MAX_RADIUS,       () => Bullet762.TEAR_MAX_RADIUS);
            Pair(sb, "EXIT_TEAR_DAMAGE",      () => Bullet9mm.EXIT_TEAR_DAMAGE,      () => Bullet762.EXIT_TEAR_DAMAGE);
            Pair(sb, "IMPACT_FORCE",          () => Bullet9mm.IMPACT_FORCE,          () => Bullet762.IMPACT_FORCE);

            sb.AppendLine("  cavitation envelope (BulletWoundEffectorSignalsSamples)");
            Row(sb, "OPENING_DEPTH",       () => BulletWoundEffectorSignalsSamples.OPENING_DEPTH);
            Row(sb, "CLOSING_DEPTH",       () => BulletWoundEffectorSignalsSamples.CLOSING_DEPTH);
            Row(sb, "PLATEAU_POWER_RATIO", () => BulletWoundEffectorSignalsSamples.PLATEAU_POWER_RATIO);
            Row(sb, "ENTRY_WIDTH_RATIO",   () => BulletWoundEffectorSignalsSamples.ENTRY_WIDTH_RATIO);
            Row(sb, "END_WIDTH_RATIO",     () => BulletWoundEffectorSignalsSamples.END_WIDTH_RATIO);
            Row(sb, "NOISE_FREQUENCY",     () => BulletWoundEffectorSignalsSamples.NOISE_FREQUENCY);

            // The envelope's shape for a representative track, straight from the game's own
            // helpers: which step opens, where it peaks, where it closes. This is what a
            // FruitLib round's neck length and peak radius have to be mapped onto.
            try
            {
                const int track = 24;
                int peak = Bullet762.CAVITATION_PEAK_RADIUS;
                var shape = new StringBuilder();
                for (int s = 0; s < track; s++)
                {
                    int   r = BulletWoundEffectorSignalsSamples.GetSphereRadius(track, s, peak);
                    float w = BulletWoundEffectorSignalsSamples.GetEnvelopeWidth(track, s);
                    shape.Append($"{s}:{r}/{w:0.##} ");
                }
                sb.AppendLine($"  envelope, {track}-step track, 7.62 peak {peak}  (step:radius/width)");
                sb.AppendLine("    " + shape);
                sb.AppendLine($"    open spheres from step 0: " +
                              BulletWoundEffectorSignalsSamples.CountOpenSpheres(track, 0, peak));
            }
            catch (Exception e) { sb.AppendLine($"  envelope helpers threw: {e.Message}"); }

            MelonLogger.Msg(sb.ToString());
        }

        private static void Row(StringBuilder sb, string name, Func<object> read) =>
            sb.AppendLine($"    {name,-26} {Read(read)}");

        private static void Pair(StringBuilder sb, string name, Func<object> a, Func<object> b) =>
            sb.AppendLine($"    {name,-24} {Read(a),-10} {Read(b)}");

        private static string Read(Func<object> read)
        {
            try { return Convert.ToString(read(), System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception e) { return "!" + e.GetType().Name; }
        }

        // ── Native shot trace ────────────────────────────────────────────────────
        //
        // Patched by hand rather than by attribute, and only once the probe is switched on:
        // FruitLib's PatchAll runs for every player, and one of these failing to resolve on
        // some future build must not take the menu and HUD patches down with it.

        private static bool _patchTried;
        private static readonly Dictionary<IntPtr, int> _powerAtEntry = new Dictionary<IntPtr, int>();

        private static void EnsurePatched()
        {
            if (_patchTried) return;
            _patchTried = true;

            // Since the release the wound layers are sent by the round's BodyWoundWalker, not
            // by Bullet, and the body exit / stop handlers take the walk's BodyWalkResult.
            var harmony = new HarmonyLib.Harmony("FruitLib.Debug.BallisticsProbe");
            Patch(harmony, typeof(Bullet),          nameof(Bullet.OnCollisionEnter),        nameof(HitPrefix), nameof(HitPostfix));
            Patch(harmony, typeof(BodyWoundWalker), nameof(BodyWoundWalker.SendCavitation), null, nameof(CavitationPostfix));
            Patch(harmony, typeof(BodyWoundWalker), nameof(BodyWoundWalker.SendExitTear),   null, nameof(ExitTearPostfix));
            Patch(harmony, typeof(Bullet),          nameof(Bullet.HandleBodyExit),          null, nameof(BodyExitPostfix));
            Patch(harmony, typeof(Bullet),          nameof(Bullet.HandlePowerExhausted),    null, nameof(PowerExhaustedPostfix));
        }

        private static void Patch(HarmonyLib.Harmony harmony, Type type, string method, string prefix, string postfix)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null) { MelonLogger.Warning($"{Tag} {type.Name}.{method} not found; not traced"); return; }

                harmony.Patch(target,
                    prefix:  prefix  == null ? null : new HarmonyMethod(typeof(FruitBallisticsProbe), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(FruitBallisticsProbe), postfix));
            }
            catch (Exception e) { MelonLogger.Warning($"{Tag} could not patch {type.Name}.{method}: {e.Message}"); }
        }

        private static string Id(Bullet b) => $"{Calibre(b)}#{b.GetInstanceID()}";

        /// <summary>Each round owns its walker (Bullet.OnAwake builds it), so the walker's
        /// postfixes find their round through this, filled on every hit.</summary>
        private static readonly Dictionary<IntPtr, string> _roundOfWalker = new Dictionary<IntPtr, string>();

        private static string Id(BodyWoundWalker w) =>
            w != null && _roundOfWalker.TryGetValue(w.Pointer, out var id) ? id : "walker";

        private static string Calibre(Bullet b) =>
            b.TryCast<Bullet762>() != null ? "7.62" :
            b.TryCast<Bullet9mm>() != null ? "9mm"  : "bullet";

        private static void HitPrefix(Bullet __instance)
        {
            if (!Enabled || __instance == null) return;
            try
            {
                _powerAtEntry[__instance.Pointer] = __instance.m_currentPower;
                var walker = __instance.m_walker;
                if (walker != null) _roundOfWalker[walker.Pointer] = Id(__instance);
            }
            catch { }
        }

        private static void HitPostfix(Bullet __instance, Collision collision)
        {
            if (!Enabled || __instance == null) return;
            try
            {
                if (!_powerAtEntry.TryGetValue(__instance.Pointer, out int before)) return;
                _powerAtEntry.Remove(__instance.Pointer);

                int after = __instance.m_currentPower;
                if (after == before) return;   // a wall, or something it passed without cost

                string what = collision?.collider != null ? collision.collider.name : "?";
                MelonLogger.Msg($"{Tag} {Id(__instance)} hit {what}: power {before} -> {after} " +
                                $"(spent {before - after}), impact {__instance.m_impactSpeed:0.#} m/s, " +
                                $"initial {__instance.m_initialSpeed:0.#} m/s, exit pending " +
                                $"{__instance.m_exitSpeedPending} at {__instance.m_exitSpeed:0.#} m/s");
            }
            catch (Exception e) { MelonLogger.Warning($"{Tag} hit trace failed: {e.Message}"); }
        }

        private static void CavitationPostfix(BodyWoundWalker __instance, BulletChannel channel)
        {
            if (!Enabled || __instance == null) return;
            MelonLogger.Msg($"{Tag} {Id(__instance)} cavitation: {DescribeChannel(channel)}");
        }

        private static void ExitTearPostfix(BodyWoundWalker __instance, BulletChannel channel, float leftoverPowerRatio)
        {
            if (!Enabled || __instance == null) return;
            MelonLogger.Msg($"{Tag} {Id(__instance)} exit tear: leftover ratio {leftoverPowerRatio:0.###}, " +
                            DescribeChannel(channel));
        }

        private static void BodyExitPostfix(Bullet __instance, BodyWalkResult walk)
        {
            if (!Enabled || __instance == null || walk == null) return;
            try
            {
                MelonLogger.Msg($"{Tag} {Id(__instance)} left the body with {walk.LeftoverPower} power " +
                                $"(entered with {walk.PowerOnEntry}), " + DescribeChannel(walk.Channel));
            }
            catch (Exception e) { MelonLogger.Warning($"{Tag} body exit trace failed: {e.Message}"); }
        }

        private static void PowerExhaustedPostfix(Bullet __instance, BodyWalkResult walk)
        {
            if (!Enabled || __instance == null || walk == null) return;
            try
            {
                MelonLogger.Msg($"{Tag} {Id(__instance)} stopped inside the body " +
                                $"(entered with {walk.PowerOnEntry}), " + DescribeChannel(walk.Channel));
            }
            catch (Exception e) { MelonLogger.Warning($"{Tag} power exhausted trace failed: {e.Message}"); }
        }

        private static string DescribeChannel(BulletChannel channel)
        {
            if (channel == null) return "no channel";
            try
            {
                int steps = channel.m_steps != null ? channel.m_steps.Count : -1;
                var e = channel.Exit;
                var d = channel.DirectionNormalized;
                return $"channel {steps} step(s), clean entry {channel.CleanEntrySteps}, " +
                       $"exit ({e.x},{e.y},{e.z}), dir ({d.x:0.##},{d.y:0.##},{d.z:0.##})";
            }
            catch (Exception ex) { return $"channel unreadable ({ex.Message})"; }
        }

        // ── Aim test ─────────────────────────────────────────────────────────────

        private struct PathStep
        {
            public int3  Index;
            public bool  Enabled;
            public Color32 Color;
        }

        private static void AimTest(bool destructive)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{Tag} ===== aim test{(destructive ? " (DESTRUCTIVE)" : "")} =====");
            try
            {
                var cam = Camera.main;
                if (cam == null) { sb.AppendLine("  no main camera"); return; }

                var ray = new Ray(cam.transform.position, cam.transform.forward);
                if (!Physics.Raycast(ray, out RaycastHit hit, 200f, ~0, QueryTriggerInteraction.Ignore))
                { sb.AppendLine("  nothing under the crosshair"); return; }

                var comp = hit.collider.GetComponentInParent(Il2CppType.Of<LimbEffectorReceiver>());
                var limb = comp != null ? comp.TryCast<LimbEffectorReceiver>() : null;
                if (limb == null) { sb.AppendLine($"  {hit.collider.name} is not a limb"); return; }

                var mesh = limb.VoxelMesh;
                if (mesh == null) { sb.AppendLine("  limb has no voxel mesh"); return; }

                Vector3 dir = ray.direction;
                sb.AppendLine($"  limb {limb.gameObject.name}, hit {hit.collider.name} at {hit.distance:0.00} m");

                DescribeMesh(sb, mesh);

                var path = WalkPath(mesh, hit.point, dir);
                DescribePath(sb, path);
                if (path.Count == 0) return;

                DryRunCheck(sb, limb, mesh, path);
                ResistanceProfile(sb, limb, path);

                if (destructive) NativeWoundTest(sb, limb, path, dir, hit.normal);
            }
            catch (Exception e) { sb.AppendLine($"  aim test threw: {e}"); }
            finally { MelonLogger.Msg(sb.ToString()); }
        }

        private static void DescribeMesh(StringBuilder sb, VoxelMesh mesh)
        {
            try
            {
                var size  = mesh.Data.Size;
                var scale = mesh.transform.lossyScale;
                float step = VoxelTools.GetRayStep(scale);
                var off   = VoxelTools.GetVoxelSizeOffset(mesh);
                sb.AppendLine($"  mesh {size.x}x{size.y}x{size.z} voxels, lossy scale " +
                              $"({scale.x:0.####},{scale.y:0.####},{scale.z:0.####}), ray step {step:0.#####} m, " +
                              $"size offset ({off.x:0.####},{off.y:0.####},{off.z:0.####})");

                // The distance between two neighbouring voxel centres is the voxel's world size,
                // whatever the scale convention turns out to be.
                var a = VoxelTools.VoxelIndexToWorldPosition(mesh, new int3(0, 0, 0));
                var b = VoxelTools.VoxelIndexToWorldPosition(mesh, new int3(1, 0, 0));
                sb.AppendLine($"  voxel pitch {Vector3.Distance(a, b) * 1000f:0.##} mm");
            }
            catch (Exception e) { sb.AppendLine($"  mesh description threw: {e.Message}"); }
        }

        /// <summary>
        /// The path the game's own bullet would walk: a VoxelRayStepper from the entry point.
        /// Consecutive repeats are collapsed, since a ray step shorter than a voxel returns the
        /// same index more than once, and whether it reports disabled voxels at all is one of
        /// the things being found out - so they are kept and flagged rather than skipped.
        /// </summary>
        private static List<PathStep> WalkPath(VoxelMesh mesh, Vector3 entry, Vector3 dir)
        {
            var path = new List<PathStep>();
            var stepper = new VoxelRayStepper(mesh, entry, dir);
            var data = mesh.Data;
            bool havePrev = false;
            int3 prev = default;

            for (int i = 0; i < MaxPathSteps; i++)
            {
                if (!stepper.TryGetNext(out VoxelPositionData p)) break;

                var idx = new int3(p.Index.x, p.Index.y, p.Index.z);
                if (havePrev && idx.Equals(prev)) continue;
                havePrev = true;
                prev = idx;

                var step = new PathStep { Index = idx };
                try
                {
                    if (!data.IsIndexOutOfRange(idx))
                    {
                        var v = data[idx];
                        step.Enabled = v.enabled;
                        step.Color   = v.color.value;
                    }
                }
                catch { }
                path.Add(step);
            }
            return path;
        }

        private static void DescribePath(StringBuilder sb, List<PathStep> path)
        {
            int enabled = 0;
            var buckets = new Dictionary<int, int>();
            foreach (var s in path)
            {
                if (!s.Enabled) continue;
                enabled++;
                int key = Quantise(s.Color);
                buckets[key] = buckets.TryGetValue(key, out int n) ? n + 1 : 1;
            }

            sb.AppendLine($"  path: {path.Count} distinct voxel(s), {enabled} enabled");

            var sorted = new List<KeyValuePair<int, int>>(buckets);
            sorted.Sort((x, y) => y.Value.CompareTo(x.Value));
            var tissue = new StringBuilder("  colours along path (quantised, count): ");
            for (int i = 0; i < sorted.Count && i < 8; i++)
                tissue.Append($"#{sorted[i].Key:X6}x{sorted[i].Value} ");
            sb.AppendLine(tissue.ToString());
        }

        /// <summary>Colour to a 24-bit key, low bits dropped so shading noise groups together.</summary>
        private static int Quantise(Color32 c) =>
            ((c.r & 0xF0) << 16) | ((c.g & 0xF0) << 8) | (c.b & 0xF0);

        private static int EnabledCount(VoxelMesh mesh, List<PathStep> path, int limit)
        {
            int n = 0;
            var data = mesh.Data;
            for (int i = 0; i < path.Count && i < limit; i++)
            {
                try
                {
                    var idx = path[i].Index;
                    if (!data.IsIndexOutOfRange(idx) && data[idx].enabled) n++;
                }
                catch { }
            }
            return n;
        }

        private static IndexEffectorSignalsHandler<Destruction> Signals(List<PathStep> path, int from, int count)
        {
            var list = new IndexEffectorSignalsList(count, false);
            for (int i = from; i < path.Count && i < from + count; i++)
                if (path[i].Enabled)
                    list.Add(new IndexEffectorSignal(path[i].Index, ProbeInfluence, InfluenceProcessType.Sum));
            return new IndexEffectorSignalsHandler<Destruction>(list);
        }

        /// <summary>
        /// Does TryGetFeedback apply the damage it reports? Asked twice with the same signals:
        /// a dry run reports the same thing both times and leaves the voxels alone, a real
        /// application reports less the second time (the voxels are already damaged) or
        /// flips some of them off.
        /// </summary>
        private static void DryRunCheck(StringBuilder sb, LimbEffectorReceiver limb, VoxelMesh mesh, List<PathStep> path)
        {
            try
            {
                int before = EnabledCount(mesh, path, DryRunVoxels);

                var handler = Signals(path, 0, DryRunVoxels);
                bool ok1 = limb.TryGetFeedback(handler, out IReadOnlyIndexEffectorFeedbacksHandler fb1);
                string r1 = Describe(fb1);
                bool ok2 = limb.TryGetFeedback(handler, out IReadOnlyIndexEffectorFeedbacksHandler fb2);
                string r2 = Describe(fb2);

                int after = EnabledCount(mesh, path, DryRunVoxels);

                sb.AppendLine($"  TryGetFeedback x2 on first {DryRunVoxels} voxels at {ProbeInfluence}:");
                sb.AppendLine($"    1st returned {ok1}: {r1}");
                sb.AppendLine($"    2nd returned {ok2}: {r2}");
                sb.AppendLine($"    enabled before {before}, after {after}  ->  " +
                              (r1 == r2 && before == after ? "looks like a DRY RUN" : "looks like it APPLIED"));
            }
            catch (Exception e) { sb.AppendLine($"  dry-run check threw: {e}"); }
        }

        /// <summary>
        /// What each voxel along the path would absorb on its own. Differences between tissue
        /// types show up here or nowhere; if every voxel absorbs the same, resistance has to be
        /// modelled from colour instead.
        /// </summary>
        private static void ResistanceProfile(StringBuilder sb, LimbEffectorReceiver limb, List<PathStep> path)
        {
            try
            {
                var line = new StringBuilder();
                int sampled = 0;
                for (int i = 0; i < path.Count && sampled < MaxResistanceSamples; i++)
                {
                    if (!path[i].Enabled) continue;
                    sampled++;

                    var handler = Signals(path, i, 1);
                    if (!limb.TryGetFeedback(handler, out IReadOnlyIndexEffectorFeedbacksHandler fb) || fb == null)
                    { line.Append($"{i}:- "); continue; }

                    line.Append($"{i}:{fb.TotalAbsorbedInfluence:0.#}/{fb.TotalProgressesChange:0.###}" +
                                $"#{Quantise(path[i].Color):X6} ");
                }
                sb.AppendLine($"  resistance, one voxel at a time (step:absorbed/progress#colour), {sampled} sampled:");
                sb.AppendLine("    " + line);
            }
            catch (Exception e) { sb.AppendLine($"  resistance profile threw: {e}"); }
        }

        private static string Describe(IReadOnlyIndexEffectorFeedbacksHandler fb)
        {
            if (fb == null) return "null handler";
            try
            {
                return $"count {fb.Count}, zero-progress {fb.ZeroProgressesCount}, " +
                       $"absorbed {fb.TotalAbsorbedInfluence:0.###}, progress {fb.TotalProgressesChange:0.###}";
            }
            catch (Exception e) { return $"unreadable ({e.Message})"; }
        }

        /// <summary>
        /// A step-for-step replica of Bullet.WalkThroughBody and SendWoundLayers (v0_17L), driven
        /// with 7.62 values down the aimed path. This is the prototype of FruitLib's wound channel:
        /// if a native 7.62 and this spend about the same power on the same body, any FruitLib
        /// projectile can be handed the game's wound model with nothing more than a power value.
        ///
        /// What the game does, per voxel step along a VoxelRayStepper:
        ///   1. Add the step to a BulletChannel.
        ///   2. Signal the crush offsets around it (a single voxel for both calibres: radius 0),
        ///      plus - once past the clean entry - each spread offset with ChannelSpreadChance.
        ///      Every signal carries the bullet's whole remaining power as its influence. A
        ///      shared visited set keeps a voxel from being paid for twice.
        ///   3. TryGetFeedback (a dry run) and subtract every voxel's absorbed influence from
        ///      power. At or below zero the bullet is spent.
        /// Then Receive the accumulated crush signals with an IndexEffectorDescription carrying
        /// the direction, an ExitTear at the last step if it came out (sized by leftover power
        /// ratio), and a Cavitation along the channel if the calibre has one (9mm does not).
        ///
        /// The previous version of this test sent only the last two, which is why the torso
        /// showed a cavity and an exit but no entry: the entry hole IS the crush layer, and the
        /// envelope is deliberately zero-width for its first steps.
        ///
        /// One deliberate difference: the game stops spending partway through a step, voxel by
        /// voxel. That needs the per-voxel Feedbacks list, which is a generic native list and
        /// not safe to read from here, so this spends a whole step at a time off the totals.
        /// </summary>
        private static void NativeWoundTest(StringBuilder sb, LimbEffectorReceiver limb,
                                            List<PathStep> path, Vector3 dir, Vector3 normal)
        {
            sb.AppendLine("  FruitLib replica of the native 7.62 wound:");
            try
            {
                var offsets = NativeOffsets(sb);
                var crush  = offsets.crush  ?? new List<int3> { int3.zero };
                var spread = offsets.spread ?? FaceNeighbours;

                // GetCleanEntrySteps: CleanEntryDepth, stretched for an oblique entry and for a
                // diagonal path (which crosses voxels faster than an axis-aligned one).
                float cos    = Mathf.Max(Mathf.Abs(Vector3.Dot(normal, dir)), BodyWoundWalker.MIN_ENTRY_COSINE);
                float maxAbs = Mathf.Max(Mathf.Abs(dir.x), Mathf.Max(Mathf.Abs(dir.y), Mathf.Abs(dir.z)));
                int   clean  = Mathf.RoundToInt(Bullet762.CLEAN_ENTRY_DEPTH / (cos / maxAbs));

                int   initial     = Bullet762.INITIAL_POWER;
                float spreadOdds  = Bullet762.CHANNEL_SPREAD_CHANCE;
                int   maxSteps    = Bullet762.MAX_PENETRATION_DEPTH;
                int   power       = initial;

                var channel = new BulletChannel(dir, clean, path.Count);
                var hit     = NextProbeHit();   // one shot: every layer carries the same hit
                var result  = new IndexEffectorSignalsList(64, false);
                var visited = new HashSet<int3>();
                var spend   = new StringBuilder();
                bool exhausted = false;
                int  step      = 0;

                foreach (var s in path)
                {
                    if (step > maxSteps) { exhausted = true; break; }
                    channel.AddStep(s.Index);

                    var stepSignals = new List<int3>();
                    foreach (var o in crush)
                        if (visited.Add(s.Index + o)) stepSignals.Add(s.Index + o);
                    if (step >= clean)
                        foreach (var o in spread)
                            if (UnityEngine.Random.value < spreadOdds && visited.Add(s.Index + o))
                                stepSignals.Add(s.Index + o);

                    step++;
                    if (stepSignals.Count == 0) continue;

                    var list = new IndexEffectorSignalsList(stepSignals.Count, false);
                    foreach (var v in stepSignals)
                        list.Add(new IndexEffectorSignal(v, -power, InfluenceProcessType.Sum));

                    if (!limb.TryGetFeedback(new IndexEffectorSignalsHandler<Destruction>(list),
                                             out IReadOnlyIndexEffectorFeedbacksHandler fb) || fb == null)
                        continue;   // nothing there to pay for - the game skips these too

                    int cost = Mathf.RoundToInt(Mathf.Abs(fb.TotalAbsorbedInfluence));
                    power -= cost;
                    spend.Append($"{step - 1}:{cost} ");

                    foreach (var v in stepSignals)
                        result.Add(new IndexEffectorSignal(v, -(power + cost), InfluenceProcessType.Sum));

                    if (power < 1) { exhausted = true; break; }
                }

                float ratio = Mathf.Max(0, power) / (float)initial;
                sb.AppendLine($"    clean entry {clean} (cos {cos:0.##}), {step} step(s), power {initial} -> {Mathf.Max(0, power)} " +
                              $"(spent {initial - Mathf.Max(0, power)}), {(exhausted ? "STOPPED inside" : $"exits at ratio {ratio:0.###}")}");
                sb.AppendLine($"    spend per step (step:cost): {spend}");
                if (!exhausted)
                    sb.AppendLine($"    native would exit at {ratio * 400f:0.#} m/s from 400 (speed scales linearly with power)" +
                                  (ratio < Bullet.SELF_DESTRUCT_POWER_RATIO ? ", and then self-destruct (below SELF_DESTRUCT_POWER_RATIO)" : ""));

                // SendWoundLayers, in the game's order.
                var crushHandler = new IndexEffectorSignalsHandler<Destruction>(result, new IndexEffectorDescription(dir, hit));
                limb.Receive(crushHandler);
                sb.AppendLine($"    crush layer: {visited.Count} voxel(s) signalled");

                if (!exhausted)
                {
                    var tear = BulletWoundEffectorSignalsSamples.ExitTear(
                        channel.Exit, Bullet762.TEAR_MIN_RADIUS, Bullet762.TEAR_MAX_RADIUS,
                        Bullet762.EXIT_TEAR_DAMAGE, ratio, dir, hit);
                    bool okTear = limb.TryReceive(tear, out IReadOnlyIndexEffectorFeedbacksHandler fbTear);
                    sb.AppendLine($"    exit tear -> {okTear}: {Describe(fbTear)}");
                }

                if (Bullet762.CAVITATION_PEAK_RADIUS > 0)
                {
                    var cav = BulletWoundEffectorSignalsSamples.Cavitation(
                        channel.Steps, channel.CleanEntrySteps,
                        Bullet762.CAVITATION_PEAK_RADIUS, Bullet762.CAVITATION_DAMAGE, dir, hit);
                    bool okCav = limb.TryReceive(cav, out IReadOnlyIndexEffectorFeedbacksHandler fbCav);
                    sb.AppendLine($"    cavitation -> {okCav}: {Describe(fbCav)}");
                }
            }
            catch (Exception e) { sb.AppendLine($"    replica threw: {e}"); }
        }

        private static Il2CppSystem.Object _probeSource;
        private static int _probeShots;

        /// <summary>
        /// The game stamps a shot's layers with EffectorHit(launcher, shot); pain reuses its
        /// anchor for a repeated hit and never matches one with a null Source. The replica has
        /// no launcher, so a sentinel of its own stands in (FruitWounds does the same).
        /// </summary>
        private static EffectorHit NextProbeHit()
        {
            if (_probeSource == null) _probeSource = new Il2CppSystem.Object();
            return new EffectorHit(_probeSource, ++_probeShots);
        }

        private static readonly List<int3> FaceNeighbours = new List<int3>
        {
            new int3( 1, 0, 0), new int3(-1, 0, 0),
            new int3( 0, 1, 0), new int3( 0,-1, 0),
            new int3( 0, 0, 1), new int3( 0, 0,-1),
        };

        /// <summary>
        /// The crush and spread shapes a live 7.62 was given by the voxel shapes provider. Since
        /// the release they sit on the round's BodyWoundWalker, which fetches them when it takes
        /// its wound dials, so only a round that has been fired at least once has them - the
        /// pooled prefab does not. Null when none is found; the caller falls back to a single
        /// voxel and the six face neighbours, and says so.
        /// </summary>
        private static (List<int3> crush, List<int3> spread) NativeOffsets(StringBuilder sb)
        {
            try
            {
                foreach (var b in Resources.FindObjectsOfTypeAll<Bullet762>())
                {
                    var w = b != null ? b.m_walker : null;
                    if (w == null || w.m_crushOffsets == null || w.m_spreadOffsets == null) continue;
                    var crush  = ReadOffsets(w.m_crushOffsets);
                    var spread = ReadOffsets(w.m_spreadOffsets);
                    sb.AppendLine($"    offsets from a live 7.62: crush {Format(crush)}, spread {Format(spread)}");
                    return (crush, spread);
                }
                sb.AppendLine("    no fired 7.62 in the scene - using one-voxel crush and face-neighbour spread. " +
                              "Fire the Lynx once first for the game's real shapes.");
            }
            catch (Exception e) { sb.AppendLine($"    reading native offsets threw: {e.Message}"); }
            return (null, null);
        }

        private static List<int3> ReadOffsets(Il2CppSystem.Collections.Generic.IReadOnlyList<int3> list)
        {
            int n = list.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<int3>>().Count;
            var result = new List<int3>(n);
            for (int i = 0; i < n; i++) result.Add(list[i]);
            return result;
        }

        private static string Format(List<int3> offsets)
        {
            var sb = new StringBuilder($"{offsets.Count}[");
            for (int i = 0; i < offsets.Count && i < 30; i++)
                sb.Append($"({offsets[i].x},{offsets[i].y},{offsets[i].z})");
            if (offsets.Count > 30) sb.Append("...");
            return sb.Append(']').ToString();
        }
    }
}
