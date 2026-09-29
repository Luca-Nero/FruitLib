using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
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
    ///   map. With Shift held it also runs FruitLib's wound channel with the native 7.62
    ///   profile down that path - crush channel, exit tear, cavitation. That one is
    ///   destructive; aim at something expendable.
    ///
    /// Only concrete scalars are read off the feedback handler (Count, TotalAbsorbedInfluence
    /// and so on). The per-voxel Feedbacks list is a ReadOnlyNativeList&lt;T&gt;, and generic-
    /// parameter-typed members read garbage through Il2CppInterop rather than throwing.
    /// </summary>
    internal static class FruitBallisticsProbe
    {
        private const string Tag = "[BallisticsProbe]";

        internal static bool Enabled => FruitLibConfig.BallisticsProbe;

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

            var key = FruitLibConfig.BallisticsProbeKey;
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
                                $"drag k {p.Spec.DragKAt(p.Spec.MuzzleVelocity):0.00000}/m");
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
                if (limb == null) { SurfaceReport(sb, hit, ray.direction); return; }

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

                if (destructive) WoundTest(sb, limb, hit, dir);
            }
            catch (Exception e) { sb.AppendLine($"  aim test threw: {e}"); }
            finally { MelonLogger.Msg(sb.ToString()); }
        }

        /// <summary>
        /// What a FruitLib round would make of the surface under the crosshair: which material it
        /// resolves to and why, how thick it is along the aim, and how far three reference
        /// rounds get into it. This is how the keyword table in FruitSurfaces gets calibrated
        /// against the game's real maps.
        /// </summary>
        private static void SurfaceReport(StringBuilder sb, RaycastHit hit, Vector3 dir)
        {
            var c = hit.collider;
            var path = new StringBuilder(c.name);
            var t = c.transform.parent;
            for (int i = 0; t != null && i < 4; i++, t = t.parent) path.Insert(0, t.name + "/");

            sb.AppendLine($"  surface '{path}' ({c.GetIl2CppType().Name}), layer {c.gameObject.layer} '{LayerMask.LayerToName(c.gameObject.layer)}', {hit.distance:0.00} m away");

            var rb = c.attachedRigidbody;
            sb.AppendLine(rb != null ? $"    rigidbody: {rb.mass:0.##} kg, kinematic {rb.isKinematic}" : "    no rigidbody (static)");
            try
            {
                var rend = c.GetComponent<Renderer>();
                var mat  = rend != null ? rend.sharedMaterial : null;
                sb.AppendLine($"    renderer material: {(mat != null ? mat.name : "none")}");
            }
            catch (Exception e) { sb.AppendLine($"    renderer material unreadable: {e.Message}"); }

            var m = FruitSurfaces.Explain(c, out string why);
            float incidence = Vector3.Angle(-dir, hit.normal);
            sb.AppendLine($"    resolves to {m.Name} ({why}); incidence {incidence:0} deg, " +
                          $"ricochet from ~{Mathf.Clamp(70f + m.RicochetAngleShift, 0f, 89.5f):0} deg for a round with the default RicochetAngle 70");

            const float probe = 2f;
            float thickness = -1f;
            if (c.Raycast(new Ray(hit.point + dir * probe, -dir), out RaycastHit exit, probe))
                thickness = probe - exit.distance;
            sb.AppendLine(thickness > 0f
                ? $"    thickness along the aim: {thickness * 100f:0.#} cm"
                : $"    thickness along the aim: over {probe} m, or an open mesh (a round would stop here)");

            foreach (var (name, spec) in new[]
            {
                ("9 mm FMJ ",   ProjectileSpec.Cartridge("probe.9mm", 8f,  9f,    360f, 0.45f)),
                ("7.62x39  ",   ProjectileSpec.Cartridge("probe.762", 7.9f, 7.62f, 715f, 0.29f)),
                (".50 BMG  ",   ProjectileSpec.Cartridge("probe.50",  42f, 12.7f, 890f, 0.62f)),
            })
            {
                float sd    = spec.SectionalDensity * Mathf.Max(0f, FruitLibConfig.PenetrationScale);
                float depth = m.Depth(sd, spec.MuzzleVelocity);
                string line = $"    {name} at {spec.MuzzleVelocity:0} m/s: gets {depth * 100f:0.#} cm in";
                if (thickness > 0f && thickness <= Mathf.Min(depth, m.MaxThickness))
                    line += $", exits at {m.ExitSpeed(sd, spec.MuzzleVelocity, thickness):0} m/s";
                else if (thickness > 0f)
                    line += ", stops inside";
                sb.AppendLine(line);
            }
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
        /// FruitLib's own wound channel (<see cref="FruitWounds.Channel"/>), driven with the
        /// game's 7.62 values down the aimed path, for comparison against a native 7.62 fired
        /// through the same body with the shot trace on.
        /// </summary>
        private static void WoundTest(StringBuilder sb, LimbEffectorReceiver limb, RaycastHit hit, Vector3 dir)
        {
            sb.AppendLine("  FruitWounds.Channel with the native 7.62 profile:");
            try
            {
                var spec = ProjectileSpec.Native762("FruitLib.Probe762");
                spec.Wound.Ejecta = false;
                int initial = spec.MuzzlePower;

                var res = FruitWounds.Channel(limb, FruitWounds.BodyOf(hit.collider), hit.point, hit.normal, dir,
                                              initial, initial, spec.Wound, FruitWounds.NextBlastHit(),
                                              new System.Random(), firstBody: true, cosmetic: false);

                if (!res.Touched) { sb.AppendLine("    no voxels on the path"); return; }

                float ratio = res.PowerOut / (float)initial;
                sb.AppendLine($"    {res.Steps} step(s), power {initial} -> {res.PowerOut} (spent {initial - res.PowerOut}), " +
                              (res.Exited ? $"exits at ratio {ratio:0.###}" : "STOPPED inside"));
                if (res.Exited)
                    sb.AppendLine($"    native would exit at {ratio * 400f:0.#} m/s from 400 (speed scales linearly with power)" +
                                  (ratio < spec.KillPowerRatio ? ", and then self-destruct (below SELF_DESTRUCT_POWER_RATIO)" : ""));
            }
            catch (Exception e) { sb.AppendLine($"    wound test threw: {e}"); }
        }
    }
}
