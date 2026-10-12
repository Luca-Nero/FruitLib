using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppLVA.Limbs;
using MelonLoader;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>
    /// Test harness for bodies' hands (FruitItems, FruitBodyControl hands, FruitPropMenu) and a
    /// look at playing extra animations on a body. Off by default.
    ///
    /// Key: the item under the crosshair is fetched by the nearest standing body (it walks over,
    ///      reaches and takes it); a body under the crosshair that holds something drops it.
    /// Shift: a steel bar for the body under the crosshair, 0.5 / 2 / 5 / 15 kg in turn, put down
    ///      in front of it as a FruitLib prop (with the game's right-click menu), which it picks up.
    /// Ctrl: every holder uses what it holds on the crosshair (a body there is the target): a gun
    ///      is aimed and fired, anything else is swung.
    /// Ctrl+Shift: every holder throws what it holds at the crosshair.
    /// Ctrl+Alt: every holder hands what it holds to the nearest other body.
    /// Alt: the body under the crosshair goes into the next FruitPose (crouch, kneel, hands up),
    ///      then lets go; traced at 4 Hz.
    /// Ctrl+Alt+Shift: plays the next animation clip the game has loaded on the body under the crosshair.
    /// Alt+Shift: everything dropped, the bars removed.
    ///
    /// While on, every item's right-click menu (the game's guns and props too) gets
    /// "Hand to &lt;nearest body&gt;" and, for a held item, "Make &lt;holder&gt; drop it".
    ///
    /// Logs to UserData/FruitHands.log: each hand action's outcome, a 2 Hz trace per holder (grip
    /// drift, balance, posture, pain, blood), each shot's aim error, and the item touching its
    /// holder or others.
    /// </summary>
    internal static class FruitHandsProbe
    {
        private const string Tag = "[HandsProbe]";
        private const int LeftHand = 11, RightHand = 14;
        private static readonly float[] BarMasses = { 0.5f, 2f, 5f, 15f };

        internal static bool Enabled => FruitLibConfig.HandsProbe;
        private static string LogPath => Path.Combine(FruitPaths.UserData, "FruitHands.log");

        private sealed class Trace
        {
            public FruitBody Body;
            public FruitWorldItem Item;
            public float Since, Next, MaxDrift, MinBalance = 100f;
            public Quaternion InHand;
            public Vector3 Grip;
            public bool Known;
            public int OwnHits, OtherHits;
        }

        private static readonly Dictionary<int, Trace> _traces = new Dictionary<int, Trace>();
        private static readonly List<GameObject> _bars = new List<GameObject>();
        private static readonly Dictionary<int, Vector3> _shotTargets = new Dictionary<int, Vector3>();
        private static int _bar, _anim;
        private static bool _required;
        private static StreamWriter _log;
        private static string _scene = "";
        private static float _lastShotSeen = -10f;

        internal static void ResetForScene()
        {
            _traces.Clear();
            _bars.Clear();
            _shotTargets.Clear();
            _poseNext.Clear();
            _poseTraceAt.Clear();
            _poseTail.Clear();
            StopAnim("scene changed");
            try { _scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { _scene = "scene"; }
        }

        // ── Update ──

        internal static void Tick()
        {
            if (!Enabled) return;
            if (!_required)
            {
                _required = true;
                FruitItems.Require("HandsProbe");
                FruitBodyControl.HandsEnded += OnHandsEnded;
                FruitPose.Ended += OnPoseEnded;
                FruitPropMenu.Extend(MenuLines);
                EnsurePatched();
                Write($"\n===== hands probe on, {_scene}, {DateTime.Now:HH:mm:ss} =====\n");
            }

            float now = Time.time;
            foreach (var b in FruitBodies.All)
            {
                var it = FruitBodyControl.Holding(b);
                _traces.TryGetValue(b.Id, out var tr);
                if (it == null) { if (tr != null) { Summary(tr, "let go"); _traces.Remove(b.Id); } continue; }
                if (tr == null || tr.Item != it) { if (tr != null) Summary(tr, "swapped"); _traces[b.Id] = tr = new Trace { Body = b, Item = it, Since = now }; }
                Sample(tr, now);
            }
            CountShots();
            TracePoses(now);
            if (_animMode == 1) EvaluateAnim();

            var key = FruitLibConfig.HandsProbeKey;
            if (key == KeyCode.None || FruitMenu.IsInputSuppressed || !Input.GetKeyDown(key)) return;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            try
            {
                if (alt && shift && ctrl) PlayNextAnim();
                else if (alt && shift) Clear();
                else if (ctrl && alt) GiveAll();
                else if (alt) NextPose();
                else if (ctrl && shift) ThrowAll();
                else if (ctrl) UseAll();
                else if (shift) GiveBar();
                else Fetch();
            }
            catch (Exception e) { Note($"{Tag} key action threw: {e}"); }
        }

        /// <summary>From FruitLibMod.OnLateUpdate, after the animator has run.</summary>
        internal static void LateTick()
        {
            if (!Enabled) return;
            if (_animMode == 0) EvaluateAnim();
        }

        // ── Keys ──

        private static void Fetch()
        {
            if (!Aim(out RaycastHit hit)) { Note($"{Tag} nothing under the crosshair"); return; }
            var hitBody = FruitBodies.OfObject(hit.collider.gameObject, out _);
            if (hitBody != null)
            {
                if (FruitBodyControl.Holding(hitBody) != null) FruitBodyControl.Drop(hitBody, "HandsProbe");
                else Note($"{Tag} {hitBody.Name} holds nothing");
                return;
            }
            var rb = hit.collider.attachedRigidbody;
            var it = FruitItems.Of(rb) ?? (rb != null && !rb.isKinematic ? FruitItems.Mark(rb, null, FruitAffords.None, "HandsProbe") : null);
            if (it == null) { Note($"{Tag} {hit.collider.gameObject.name} is not something to pick up"); return; }
            var taker = NearestFree(it.Position, 20f, null);
            if (taker == null) { Note($"{Tag} no standing body within 20 m"); return; }
            Log($"#{taker.Id} {taker.Name} goes for {it} ({Vector3.Distance(taker.Position, it.Position):0.0} m)");
            FruitBodyControl.PickUp(taker, it, false, "HandsProbe");
        }

        private static void GiveBar()
        {
            var b = LookedAtBody();
            if (b == null) { Note($"{Tag} no body under the crosshair"); return; }
            float mass = BarMasses[_bar++ % BarMasses.Length];
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Steel_Bar";
            go.transform.localScale = new Vector3(0.05f, 0.05f, 0.75f);
            go.transform.position = b.Position + b.Facing * 1.2f + Vector3.up * 0.3f;
            go.transform.rotation = Quaternion.LookRotation(Vector3.Cross(Vector3.up, b.Facing));
            var r = go.GetComponent<Renderer>();
            if (r != null) r.material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Unlit/Color"))
                                        { color = Color.Lerp(new Color(0.8f, 0.8f, 0.85f), new Color(0.9f, 0.3f, 0.1f), mass / 15f) };
            var it = FruitProps.Adopt(go, $"steel bar ({mass:0.#} kg)", mass, FruitAffords.None, "HandsProbe");
            _bars.Add(go);
            if (it == null) { Note($"{Tag} the bar did not register"); return; }
            Log($"#{b.Id} {b.Name} goes for a {it}");
            FruitBodyControl.PickUp(b, it, false, "HandsProbe");
        }

        private static void UseAll()
        {
            Vector3 at = Aim(out RaycastHit hit) ? hit.point : FruitBodies.ViewPosition + FruitBodies.ViewForward * 20f;
            var target = hit.collider != null ? FruitBodies.OfObject(hit.collider.gameObject, out _) : null;
            int n = 0;
            foreach (var b in FruitBodies.All)
            {
                var it = FruitBodyControl.Holding(b);
                if (it == null || b == target) continue;
                var victim = target ?? (!it.IsGun ? NearestOther(b, 8f) : null);
                _shotTargets[b.Id] = victim != null ? victim.Position + Vector3.up * 0.5f : at;
                bool ok = FruitBodyControl.Use(b, victim, victim == null ? at : (Vector3?)null, "HandsProbe");
                Log($"#{b.Id} {b.Name} uses {it.Name} on {(victim != null ? victim.Name : $"a point {Vector3.Distance(b.Position, at):0.0} m away")}: {(ok ? "started" : "refused")}");
                n++;
            }
            if (n == 0) Note($"{Tag} nobody holds anything");
        }

        private static void ThrowAll()
        {
            if (!Aim(out RaycastHit hit)) { Note($"{Tag} nothing under the crosshair to throw at"); return; }
            foreach (var b in FruitBodies.All)
            {
                var it = FruitBodyControl.Holding(b);
                if (it == null) continue;
                bool ok = FruitBodyControl.Throw(b, hit.point, 1f, "HandsProbe");
                Log($"#{b.Id} {b.Name} throws {it.Name} {Vector3.Distance(b.Position, hit.point):0.0} m: {(ok ? "started" : "refused")}");
            }
        }

        private static void GiveAll()
        {
            foreach (var b in FruitBodies.All)
            {
                var it = FruitBodyControl.Holding(b);
                if (it == null) continue;
                var to = NearestFree(b.Position, 15f, b);
                if (to == null) { Log($"#{b.Id} {b.Name}: nobody free to hand {it.Name} to"); continue; }
                bool ok = FruitBodyControl.Give(b, to, false, "HandsProbe");
                Log($"#{b.Id} {b.Name} hands {it.Name} to #{to.Id} {to.Name}: {(ok ? "started" : "refused")}");
            }
        }

        private static void Clear()
        {
            foreach (var b in FruitBodies.All) FruitBodyControl.Drop(b, "HandsProbe");
            foreach (var go in _bars) try { if (go != null) Object.Destroy(go); } catch { }
            _bars.Clear();
            StopAnim("cleared");
        }

        // ── The game's right-click menu ──

        private static IEnumerable<FruitMenuEntry> MenuLines(Il2CppPlayer.ContextMenu.ContextMenuActionsHandler handler, Rigidbody rb)
        {
            if (!Enabled || rb == null) return null;
            var it = FruitItems.Of(rb);
            if (it == null) return null;
            return new[]
            {
                new FruitMenuEntry
                {
                    Name = "Hand to the nearest body", Priority = 600,
                    Label = () => { var b = NearestFree(it.Position, 20f, it.Holder); return b != null ? $"Hand to {b.Name}" : "Hand to the nearest body"; },
                    Available = () => it.Valid && it.Holder == null && NearestFree(it.Position, 20f, null) != null,
                    OnClick = () => { var b = NearestFree(it.Position, 20f, null); if (b != null) FruitBodyControl.PickUp(b, it, false, "HandsProbe"); },
                },
                new FruitMenuEntry
                {
                    Name = "Make the holder drop it", Priority = 590,
                    Label = () => it.Holder != null ? $"Make {it.Holder.Name} drop it" : "Make the holder drop it",
                    Available = () => it.Holder != null,
                    OnClick = () => { if (it.Holder != null) FruitBodyControl.Drop(it.Holder, "HandsProbe"); },
                },
            };
        }

        // ── Logging ──

        private static void OnHandsEnded(FruitBody b, FruitWorldItem it, FruitHandsResult r)
        {
            if (!Enabled) return;
            Log($"#{b.Id} {b.Name}: {r} {(it != null ? it.Name : "")}");
        }

        // ── Poses ──

        private static readonly FruitPoseShape[] Poses = { FruitPoseShape.Crouch, FruitPoseShape.Kneel, FruitPoseShape.HandsUp };
        private static readonly Dictionary<int, int> _poseNext = new Dictionary<int, int>();
        private static readonly Dictionary<int, float> _poseTraceAt = new Dictionary<int, float>();
        private static readonly List<int> _poseDone = new List<int>();
        private static readonly Dictionary<int, int> _poseTail = new Dictionary<int, int>();

        /// <summary>The body under the crosshair: the next pose, or back to standing after the last.</summary>
        private static void NextPose()
        {
            var b = LookedAtBody();
            if (b == null) { Note($"{Tag} no body under the crosshair"); return; }
            _poseNext.TryGetValue(b.Id, out int i);
            if (i >= Poses.Length)
            {
                _poseNext[b.Id] = 0;
                FruitPose.Release(b, 0.6f);
                Write($"  {Time.time,7:0.0}  #{b.Id} {b.Name}: release\n");
                Note($"{Tag} {b.Name}: release");
                return;
            }
            var shape = Poses[i];
            bool ok = FruitPose.Hold(b, shape, 0.6f, source: "HandsProbe");
            _poseNext[b.Id] = ok ? i + 1 : i;
            _poseTraceAt[b.Id] = 0f;
            _poseTail[b.Id] = 0;
            Write($"\n===== pose: {shape.Name} on #{b.Id} {b.Name} ({b.StateName}), {(ok ? "held" : "REFUSED")}, {DateTime.Now:HH:mm:ss} =====\n" +
                  "     t  pose        weight  balance  muscle  upDot  pelvis-h  l-knee  r-knee  l-hip  r-hip  spine  com-off  pull   state / rig drift\n");
            Note($"{Tag} {b.Name}: {shape.Name}{(ok ? "" : " refused (not standing?)")}");
        }

        private static void OnPoseEnded(FruitBody b, FruitPoseShape shape, string why)
        {
            if (!Enabled) return;
            Write($"  {Time.time,7:0.0}  #{b.Id} {b.Name}: {shape?.Name} ended, {why}\n");
            if (why != "released") Note($"{Tag} {b.Name}: {shape?.Name} dropped ({why})");
        }

        /// <summary>4 Hz while a body holds a pose: is it holding, how low it got, how bent its legs are.</summary>
        private static void TracePoses(float now)
        {
            if (_poseTraceAt.Count == 0) return;
            _poseDone.Clear();
            _poseDone.AddRange(_poseTraceAt.Keys);
            foreach (int id in _poseDone)
            {
                var b = FruitBodies.Get(id);
                if (b == null) { _poseTraceAt.Remove(id); continue; }
                if (now < _poseTraceAt[id]) continue;
                _poseTraceAt[id] = now + 0.25f;
                var shape = FruitPose.Current(b);
                float up = -1f;
                try { up = b.Bind.Runtime?.CreaturePelvisUpToGlobalUpDot ?? -1f; } catch { }
                Vector3 pelvis = b.Limbs[FruitBodies.Pelvis].LivePosition;
                float feet = Mathf.Min(b.Limbs[3].LivePosition.y, b.Limbs[6].LivePosition.y);
                FruitPose.AssistState(b, out Vector3 pull, out Vector3 off);
                string drift = FruitPose.RigDrift(b);
                Write($"  {now,7:0.0}  {(shape != null ? shape.Name : "-"),-10} {FruitPose.Weight(b),6:0.00} {LiveBalance(b),8:0} {LiveMuscle(b),7:0} {up,6:0.00} {pelvis.y - feet,9:0.00} " +
                      $"{Bend(b, 1, 2, 3),7:0} {Bend(b, 4, 5, 6),7:0} {Hip(b, 1, 2),6:0} {Hip(b, 4, 5),6:0} {Lean(b),6:0} {off.magnitude,8:0.00} {pull.magnitude,5:0.0}   {b.StateName}{(drift.Length > 0 ? "  rig: " + drift : "")}\n");
                // Two more seconds after it is fully out, to see it stand back up.
                if (shape == null) { if (++_poseTail[id] >= 8) { _poseTraceAt.Remove(id); _poseTail.Remove(id); } }
                else _poseTail[id] = 0;
            }
        }

        private static float LiveBalance(FruitBody b) { try { return b.Bind.Balance?.Value ?? b.Balance; } catch { return b.Balance; } }
        private static float LiveMuscle(FruitBody b) { try { return b.Bind.Muscle?.Value ?? b.Muscle; } catch { return b.Muscle; } }

        /// <summary>Knee bend from the physical thigh, shin and foot centres, degrees (0 = straight).</summary>
        private static float Bend(FruitBody b, int thigh, int shin, int foot)
        {
            var l = b.Limbs;
            if (!l[thigh].Present || !l[shin].Present || !l[foot].Present) return -1f;
            Vector3 a = l[shin].LivePosition - l[thigh].LivePosition, c = l[foot].LivePosition - l[shin].LivePosition;
            return Vector3.Angle(a, c);
        }

        /// <summary>Thigh forward of hanging straight down, degrees.</summary>
        private static float Hip(FruitBody b, int thigh, int shin)
        {
            var l = b.Limbs;
            if (!l[thigh].Present || !l[shin].Present) return -1f;
            return Vector3.Angle(Vector3.down, l[shin].LivePosition - l[thigh].LivePosition);
        }

        /// <summary>Spine forward of upright (pelvis to torso), degrees.</summary>
        private static float Lean(FruitBody b)
        {
            var l = b.Limbs;
            if (!l[FruitBodies.Pelvis].Present || !l[FruitBodies.Torso].Present) return -1f;
            return Vector3.Angle(Vector3.up, l[FruitBodies.Torso].LivePosition - l[FruitBodies.Pelvis].LivePosition);
        }

        private static void Sample(Trace tr, float now)
        {
            var b = tr.Body;
            var hand = LimbRb(b.Limbs[RightHand].Present ? b.Limbs[RightHand] : b.Limbs[LeftHand]);
            if (hand == null || !tr.Item.Valid) return;
            var rb = tr.Item.Rb;
            if (!tr.Known)
            {
                tr.Known = true;
                tr.InHand = Quaternion.Inverse(hand.rotation) * rb.rotation;
                tr.Grip = Quaternion.Inverse(rb.rotation) * (hand.position - rb.position);
            }
            float drift = Vector3.Distance(rb.position + rb.rotation * tr.Grip, hand.position);
            tr.MaxDrift = Mathf.Max(tr.MaxDrift, drift);
            tr.MinBalance = Mathf.Min(tr.MinBalance, b.Balance);
            if (now < tr.Next) return;
            tr.Next = now + 0.5f;
            float angle = Quaternion.Angle(tr.InHand, Quaternion.Inverse(hand.rotation) * rb.rotation);
            Write($"  {now - tr.Since,5:0.0}  #{b.Id} {b.Name,-10} {tr.Item.Name,-22} drift {drift:0.000} angle {angle,3:0}  balance {b.Balance,3:0} {b.PostureText(),-9} pain {b.Pain,3:0} blood {b.Blood,3:0} {(b.Grounded ? "      " : "NOT-GR")} {(FruitPose.IsHolding(b) ? FruitPose.Current(b)?.Name : "")} {(FruitBodyControl.IsWalking(b) ? "walking" : "")}\n");
        }

        private static void Summary(Trace tr, string why)
            => Log($"#{tr.Body.Id} {tr.Body.Name} held {tr.Item.Name} {Time.time - tr.Since:0.0} s ({why}): max drift {tr.MaxDrift:0.000} m, min balance {tr.MinBalance:0}, hit its holder {tr.OwnHits}x, hit others {tr.OtherHits}x");

        private static void CountShots()
        {
            float t = FruitWorldEvents.LastShotTime;
            if (t == _lastShotSeen) return;
            _lastShotSeen = t;
            int by = FruitWorldEvents.LastShotBody;
            if (by == 0) return;
            string err = "";
            // The point the shooter itself aims at (5.27.0 measured against the probe's own guess,
            // half a metre lower: 14 degrees of error up close that wasn't there).
            var aimed = FruitBodyControl.AimTarget(FruitBodies.Get(by));
            if (aimed.HasValue || _shotTargets.TryGetValue(by, out _))
            {
                Vector3 target = aimed ?? _shotTargets[by];
                Vector3 want = target - FruitWorldEvents.LastShotFrom;
                err = $", {Vector3.Angle(want, FruitWorldEvents.LastShotDir):0.0} deg off the target at {want.magnitude:0.0} m";
            }
            var b = FruitBodies.Get(by);
            Log($"#{by} {(b != null ? b.Name : "?")} fired{err} (shake {(b != null ? FruitBodyControl.Shake(b) : 0f):0.0} deg)");
        }

        // ── Collisions of held items ──

        private static bool _patchTried;

        private static void EnsurePatched()
        {
            if (_patchTried) return;
            _patchTried = true;
            try
            {
                var target = AccessTools.Method(typeof(LimbPhysics), "OnCollisionEnter");
                if (target == null) return;
                new HarmonyLib.Harmony("FruitLib.Debug.HandsProbe").Patch(target,
                    postfix: new HarmonyMethod(typeof(FruitHandsProbe), nameof(CollisionPostfix)));
            }
            catch (Exception e) { Note($"{Tag} could not patch LimbPhysics.OnCollisionEnter: {e.Message}"); }
        }

        private static void CollisionPostfix(LimbPhysics __instance, Collision other)
        {
            if (_traces.Count == 0 || other == null) return;
            try
            {
                var rb = other.rigidbody;
                if (rb == null) return;
                var it = FruitItems.Of(rb);
                if (it?.Holder == null || !_traces.TryGetValue(it.Holder.Id, out var tr) || tr.Item != it) return;
                var body = FruitBodies.OfObject(__instance.gameObject, out int limb);
                string part = limb >= 0 ? FruitBodies.LimbNames[limb] : "limb";
                if (body == it.Holder) { if (tr.OwnHits++ < 6) Write($"  {Time.time - tr.Since,5:0.0}  {it.Name} hit its holder's {part}: impulse {other.impulse.magnitude:0.00}\n"); }
                else { tr.OtherHits++; Write($"  {Time.time - tr.Since,5:0.0}  {it.Name} hit {(body != null ? $"#{body.Id} {body.Name}'s" : "a")} {part}: impulse {other.impulse.magnitude:0.00}, speed {other.relativeVelocity.magnitude:0.0}\n"); }
            }
            catch (Exception e) { FruitBodies.ReportOnce("hands probe collision", e); }
        }

        // ── Animation ──

        private static PlayableGraph _graph;
        private static bool _graphLive;
        private static AnimationClipPlayable _clipPlayable;
        private static Animator _animator;
        private static FruitBody _animBody;
        private static float _animStart, _animLength;
        private static int _animMode = -1;   // 0 = evaluate in LateUpdate, 1 = in Update
        private static string _animClip = "";
        private static Quaternion _armRot0, _legRot0;
        private static Vector3 _handOff0;
        private static bool _animSampled;
        private static List<AnimationClip> _clips;

        private static void PlayNextAnim()
        {
            StopAnim("next");
            var b = LookedAtBody();
            if (b == null) { Note($"{Tag} no body under the crosshair"); return; }
            var anim = b.Bind.Refs?.Animation?.Animator;
            if (anim == null) { Note($"{Tag} {b.Name}: no animator found"); return; }

            if (_clips == null)
            {
                _clips = new List<AnimationClip>();
                var sb = new StringBuilder($"\n===== animation clips loaded, {_scene} =====\n");
                foreach (var c in Resources.FindObjectsOfTypeAll<AnimationClip>())
                {
                    if (c == null) continue;
                    _clips.Add(c);
                    float len = 0f; bool human = false;
                    try { len = c.length; } catch { }
                    try { human = c.humanMotion; } catch { }
                    sb.Append($"  {c.name}  {len:0.00} s{(human ? "  humanoid" : "")}\n");
                }
                _clips.Sort((x, y) => string.CompareOrdinal(x.name, y.name));
                string ctrl = "";
                try { ctrl = anim.runtimeAnimatorController?.name ?? ""; } catch { }
                string avatar = "";
                try { avatar = anim.avatar?.name ?? ""; } catch { }
                sb.Append($"  animator: isHuman {anim.isHuman}, avatar '{avatar}', controller '{ctrl}', layers {anim.layerCount}\n");
                Write(sb.ToString());
            }
            if (_clips.Count == 0) { Note($"{Tag} no animation clips loaded"); return; }

            int i = _anim++;
            var clip = _clips[(i / 2) % _clips.Count];
            _animMode = i % 2;
            try
            {
                _graph = PlayableGraph.Create("FruitHandsProbe");
                _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var output = AnimationPlayableOutput.Create(_graph, "probe", anim);
                _clipPlayable = AnimationClipPlayable.Create(_graph, clip);
                output.GetHandle().SetSourcePlayable(_clipPlayable.GetHandle(), 0);
                output.GetHandle().SetWeight(1f);
                _graphLive = true;
            }
            catch (Exception e) { Note($"{Tag} building the graph threw: {e}"); StopAnim("graph failed"); return; }

            _animator = anim;
            _animBody = b;
            _animStart = Time.time;
            _animClip = clip.name;
            try { _animLength = Mathf.Max(0.1f, clip.length); } catch { _animLength = 1f; }
            _animSampled = false;
            _armRot0 = Bone(HumanBodyBones.RightUpperArm)?.localRotation ?? Quaternion.identity;
            _legRot0 = Bone(HumanBodyBones.LeftUpperLeg)?.localRotation ?? Quaternion.identity;
            _handOff0 = HandOffset(b);
            Write($"\n===== animation: '{clip.name}' on #{b.Id} {b.Name}, evaluated in {(_animMode == 0 ? "LateUpdate" : "Update")}, {DateTime.Now:HH:mm:ss} =====\n");
            Note($"{Tag} playing '{clip.name}' on {b.Name} ({(_animMode == 0 ? "LateUpdate" : "Update")})");
        }

        private static void EvaluateAnim()
        {
            if (!_graphLive) return;
            float t = Time.time - _animStart;
            if (_animator == null || _animator.WasCollected || _animBody == null) { StopAnim("animator gone"); return; }
            try
            {
                _clipPlayable.GetHandle().SetTime(t % _animLength);
                _graph.Evaluate(Time.deltaTime);
            }
            catch (Exception e) { Note($"{Tag} evaluate threw: {e.Message}"); StopAnim("evaluate failed"); return; }

            if (!_animSampled && t >= 0.6f)
            {
                _animSampled = true;
                float arm = Quaternion.Angle(_armRot0, Bone(HumanBodyBones.RightUpperArm)?.localRotation ?? _armRot0);
                float leg = Quaternion.Angle(_legRot0, Bone(HumanBodyBones.LeftUpperLeg)?.localRotation ?? _legRot0);
                float hand = Vector3.Distance(_handOff0, HandOffset(_animBody));
                Write($"  at 0.6 s: animated rig right upper arm turned {arm:0} deg, left thigh {leg:0} deg; physical right hand moved {hand:0.000} m against the pelvis\n");
            }
            if (t >= 3f) StopAnim("done");
        }

        private static Transform Bone(HumanBodyBones bone)
        {
            try { return _animator != null && _animator.isHuman ? _animator.GetBoneTransform(bone) : null; } catch { return null; }
        }

        private static Vector3 HandOffset(FruitBody b)
        {
            var hand = b.Limbs[RightHand];
            var pelvis = b.Limbs[FruitBodies.Pelvis];
            return hand.Present && pelvis.Present ? hand.LivePosition - pelvis.LivePosition : Vector3.zero;
        }

        private static void StopAnim(string why)
        {
            if (_graphLive)
            {
                try { _graph.Destroy(); } catch { }
                Write($"  stopped after {Time.time - _animStart:0.0} s: {why}\n");
            }
            _graphLive = false;
            _animMode = -1;
            _animator = null;
            _animBody = null;
        }


        // ── Helpers ──

        /// <summary>The nearest conscious standing body within range that holds nothing and isn't
        /// <paramref name="except"/>.</summary>
        private static FruitBody NearestFree(Vector3 at, float within, FruitBody except)
        {
            FruitBody best = null;
            float bestD = within;
            foreach (var b in FruitBodies.All)
            {
                if (b == except || !b.HasPosition || !b.Alive || !b.Conscious) continue;
                if (b.Behaviour != FruitBehaviour.Standing && b.Behaviour != FruitBehaviour.Moving) continue;
                if (FruitBodyControl.HandsOn(b) != null) continue;
                float d = Vector3.Distance(b.Position, at);
                if (d < bestD) { bestD = d; best = b; }
            }
            return best;
        }

        private static FruitBody NearestOther(FruitBody b, float within)
        {
            FruitBody best = null;
            float bestD = within;
            foreach (var o in FruitBodies.All)
            {
                if (o == b || !o.Alive || !o.HasPosition) continue;
                float d = Vector3.Distance(o.Position, b.Position);
                if (d < bestD) { bestD = d; best = o; }
            }
            return best;
        }

        /// <summary>The nearest hit under the crosshair past the player's own held item.</summary>
        private static bool Aim(out RaycastHit hit)
        {
            hit = default;
            Il2CppStructArray<RaycastHit> hits;
            try { hits = Physics.RaycastAll(FruitBodies.ViewPosition, FruitBodies.ViewForward, 60f, ~0, QueryTriggerInteraction.Ignore); }
            catch { return false; }
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h.collider == null || h.distance < 0.5f || h.distance >= best) continue;
                best = h.distance; hit = h; found = true;
            }
            return found;
        }

        private static FruitBody LookedAtBody()
        {
            if (!FruitBodies.HasView) return null;
            FruitBody best = null;
            float bestA = 12f;
            foreach (var b in FruitBodies.All)
            {
                if (!b.HasPosition) continue;
                float a = Vector3.Angle(FruitBodies.ViewForward, b.HeadPosition - FruitBodies.ViewPosition);
                if (a < bestA) { bestA = a; best = b; }
            }
            return best;
        }

        private static Rigidbody LimbRb(FruitLimb l)
        {
            if (l == null || !l.Present || l.Physics == null || l.Physics.WasCollected) return null;
            try { return l.Physics.m_rb; } catch { return null; }
        }

        private static void Log(string s)
        {
            Write($"  {Time.time,7:0.0}  {s}\n");
            Note($"{Tag} {s}");
        }

        private static void Write(string s)
        {
            try
            {
                if (_log == null)
                {
                    _log = new StreamWriter(LogPath, append: false, Encoding.UTF8) { AutoFlush = true };
                    _log.WriteLine($"# FruitHands probe, FruitLib {FruitVersion.Current}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                }
                _log.Write(s);
            }
            catch { }
        }

        private static void Note(string s) => MelonLogger.Msg(s);
    }
}
