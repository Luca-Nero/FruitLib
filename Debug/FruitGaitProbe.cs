using System;
using System.IO;
using System.Text;
using Il2CppLVA.Puppeteers.Variants.Humanoid;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// Test harness for how a body walks, apart from where: why it wobbles, whether it is "out of
    /// beat", and what damps it. Off by default. Since 5.32.0.
    ///
    /// Key: the body under the crosshair (or the nearest) walks straight ahead of where it faces,
    /// about 14 m, from standing, then turns round and does it again, in these ways in turn:
    ///   raw     the game's own walk, nothing steering it;
    ///   raw+D   the same with the pelvis's fast yaw damped (<see cref="FruitBodyControl.YawDamping"/>);
    ///   raw+R   the same with the start eased in (<see cref="FruitBodyControl.StartRampSeconds"/>);
    ///   raw+DR  both;
    ///   steer+DR  FruitBodyControl.GoTo to a point straight ahead, with both.
    /// All twice, after 4 s standing still as the baseline. Shift + key stops it.
    ///
    /// Turn runs (turn:d, turn:s40, turn:s60, turn:s80): from standing, the body turns 120 degrees
    /// on the spot with FruitBodyControl.Face, left then right; d is the game's way (the feet
    /// dragged), sNN is <see cref="FruitBodyControl.StepTurns"/> at a forward pace of 0.NN. The
    /// summary gives the time, how far it moved, steps taken and the distance the planted feet
    /// slid.
    ///
    /// Every physics step of each run goes to UserData/FruitGait.csv: the pelvis and both feet in a
    /// frame laid along the run's line (f ahead, l to the right, y up), the same three bones of the
    /// animator's own armature (o*) and of the armature the muscles follow (r*), yaw and yaw rate
    /// of the pelvis, torso and head and of the rig's pelvis, the yaw command, the move direction
    /// asked for and the one the animator got, the animation speed, the game's balance
    /// coefficient, and the damping torque. A summary per run goes to the console; Frukt/Tools/
    /// gait_analyze.py reads the file.
    ///
    /// NeuroFrukt's walking orders replace the probe's: turn off "Bodies walk, run and follow"
    /// (Actions) while testing.
    /// </summary>
    internal static class FruitGaitProbe
    {
        private const string Tag = "[GaitProbe]";
        private const float Length = 14f, MinLength = 7f;
        private static readonly string[] Plan =
        {
            "stand",
            "turn:d", "turn:s40", "turn:s60", "turn:s80",
            "turn:d", "turn:s40", "turn:s60", "turn:s80",
            "raw", "steer",
        };

        /// <summary>The damping the probe's D runs use, 1/s (about a damping ratio of 0.7 at 3 Hz).</summary>
        private const float ProbeDamping = 28f;
        private const float ProbeRamp = 1.0f;

        internal static bool Enabled => FruitLibConfig.GaitProbe;
        private static string CsvPath => Path.Combine(FruitPaths.UserData, "FruitGait.csv");

        private enum Phase { Idle, WaitNav, Align, Settle, Stand, Run, Turn }

        private static Phase _phase;
        private static FruitBody _body;
        private static int _step, _run;
        private static bool _required, _haveLine, _aligned;
        private static Vector3 _o, _f, _r, _goal;
        private static float _len, _headingYaw;
        private static string _cond = "";
        private static float _phaseAt, _runStart, _calmSince;
        private static StreamWriter _csv;
        private static float _savedDamping, _savedRamp;
        private static bool _savedYaw, _savedStepTurns;
        private static float _savedStepPace;
        private static int _turnSign = 1;

        // The feet in a turn run: where they stood, how far they slid planted, how many steps.
        private static Vector3 _lPrev, _rPrev;
        private static float _floorL, _floorR, _dragL, _dragR, _maxDisp;
        private static int _liftL, _liftR;
        private static bool _upL, _upR, _feetInit;

        // Per-run running totals for the console summary.
        private static int _n;
        private static double _sumL2, _sumSpd, _sumYaw2;
        private static float _maxL, _peakSpd;
        private static float _prevYaw;
        private static bool _hasYaw;

        internal static void ResetForScene() => Abort("scene changed");

        // ── Update ──

        internal static void Tick()
        {
            if (!Enabled) { if (_phase != Phase.Idle) Abort("switched off"); return; }
            if (!_required)
            {
                _required = true;
                FruitBodies.Require("GaitProbe");
                FruitNav.Require("GaitProbe");
            }

            var key = FruitLibConfig.GaitProbeKey;
            if (key != KeyCode.None && !FruitMenu.IsInputSuppressed && Input.GetKeyDown(key))
            {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                try
                {
                    if (shift) Abort("stopped");
                    else if (_phase != Phase.Idle) Note($"{Tag} already running (Shift + key stops it)");
                    else Begin();
                }
                catch (Exception e) { Note($"{Tag} key action threw: {e}"); Abort("error"); }
            }

            if (_phase != Phase.Idle)
            {
                try { Advance(); }
                catch (Exception e) { Note($"{Tag} threw: {e}"); Abort("error"); }
            }
        }

        // ── Setting up ──

        private static void Begin()
        {
            var b = Looked();
            if (b == null) { Note($"{Tag} no body to walk"); return; }
            if (!b.Alive || !b.Conscious || b.Bind.Refs == null || b.Bind.Runtime == null) { Note($"{Tag} that body can't be driven"); return; }

            _body = b;
            _step = -1;
            _run = 0;
            _haveLine = false;
            _savedDamping = FruitBodyControl.YawDamping;
            _savedRamp = FruitBodyControl.StartRampSeconds;
            _savedYaw = FruitBodyControl.PersistentYaw;
            _savedStepTurns = FruitBodyControl.StepTurns;
            _savedStepPace = FruitBodyControl.StepTurnPace;
            _turnSign = 1;

            try
            {
                _csv = new StreamWriter(CsvPath, append: false, new UTF8Encoding(false)) { AutoFlush = false };
                _csv.WriteLine($"# FruitGait probe, FruitLib {FruitVersion.Current}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, body #{b.Id}, line up to {Length:0} m, fixedDeltaTime {Time.fixedDeltaTime:0.0000}, NavSmoothWalking {FruitLibConfig.NavSmoothWalking}, TurnRate {FruitBodyControl.TurnRate:0}, damping {ProbeDamping:0}, ramp {ProbeRamp:0.0} s");
                _csv.WriteLine("t,run,cond,aligned,pf,pl,py,vf,vl,yaw,tyaw,lmx,lmz,adx,adz,mspd,aspd,bal,animT,grounded," +
                               "lff,lfl,lfy,rff,rfl,rfy,opf,opl,opy,olff,olfl,olfy,orff,orfl,orfy,rpf,rpl,rpy,rlff,rlfl,rlfy,rrff,rrfl,rrfy," +
                               "wy,tqd,oyaw,ryaw,toryaw,hdyaw");
            }
            catch (Exception e) { Note($"{Tag} can't write {CsvPath}: {e.Message}"); _csv = null; return; }

            Note($"{Tag} body #{b.Id}: {Plan.Length} steps, up to {Length:0} m each. Turn NeuroFrukt's walking off while this runs. Shift + key stops.");
            NextStep();
        }

        private static void NextStep()
        {
            if (_csv != null) _csv.Flush();
            _step++;
            if (_step >= Plan.Length) { Finish("done"); return; }
            _cond = Plan[_step];
            _aligned = true;
            ApplyCondition();

            if (_cond == "stand")
            {
                SetFrame();   // heading from where it faces now
                StartRun();
                _phase = Phase.Stand;
                return;
            }

            if (_cond.StartsWith("turn:"))
            {
                // Standing still first; no walking to turn round from.
                _phase = Phase.Settle;
                _phaseAt = Time.time;
                _calmSince = -1f;
                return;
            }

            bool steer = _cond.StartsWith("steer");
            _phase = steer && !FruitNav.Ready ? Phase.WaitNav : Phase.Align;
            _phaseAt = Time.time;
            if (_phase == Phase.Align) BeginAlign();
        }

        /// <summary>Turns the body round (the line it just walked, backwards) so the next run has room.</summary>
        private static void BeginAlign()
        {
            if (_haveLine) FruitBodyControl.Face(_body, -_f, 4f);
        }

        private static void ApplyCondition()
        {
            int plus = _cond.IndexOf('+');
            string flags = plus >= 0 ? _cond.Substring(plus + 1) : "";
            FruitBodyControl.YawDamping = flags.Contains("D") ? ProbeDamping : 0f;
            FruitBodyControl.StartRampSeconds = flags.Contains("R") ? ProbeRamp : 0f;
            FruitBodyControl.PersistentYaw = false;
            FruitBodyControl.StepTurns = false;
            if (_cond.StartsWith("turn:s") && int.TryParse(_cond.Substring(6), out int pace))
            {
                FruitBodyControl.StepTurns = true;
                FruitBodyControl.StepTurnPace = pace / 100f;
            }
        }

        /// <summary>The run's frame: from the pelvis, along the way the body faces.</summary>
        private static void SetFrame()
        {
            var rt = _body.Bind.Runtime;
            _o = Pelvis();
            _headingYaw = rt.CreatureYRotation;
            float a = _headingYaw * Mathf.Deg2Rad;
            _f = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            _r = new Vector3(_f.z, 0f, -_f.x);
            _haveLine = true;
        }

        // ── Phases ──

        private static void Advance()
        {
            var b = _body;
            if (b == null || b.Creature == null || FruitBodies.Get(b.Id) != b || !b.Alive || !b.Conscious) { Abort("body gone"); return; }
            float now = Time.time;
            var rt = b.Bind.Runtime;

            switch (_phase)
            {
                case Phase.WaitNav:
                    if (FruitNav.Ready || now - _phaseAt > 6f)
                    {
                        _phase = Phase.Align;
                        _phaseAt = now;
                        BeginAlign();
                    }
                    break;

                case Phase.Align:
                {
                    // Turning round: Face lets go within 12 degrees of the way; that is near enough, the line is laid from where it ends up.
                    bool turned = !_haveLine || Mathf.Abs(Mathf.DeltaAngle(rt.CreatureYRotation, _headingYaw + 180f)) < 14f;
                    if (now - _phaseAt > 0.4f && turned) { _phase = Phase.Settle; _phaseAt = now; _calmSince = -1f; }
                    else if (now - _phaseAt > 5.5f) { _aligned = false; _phase = Phase.Settle; _phaseAt = now; _calmSince = -1f; Note($"{Tag} step {_step} ({_cond}): didn't turn round in 5 s, going anyway"); }
                    break;
                }

                case Phase.Settle:
                {
                    var rb = PelvisRb();
                    float speed = rb != null ? rb.linearVelocity.magnitude : 0f;
                    if (speed < 0.12f) { if (_calmSince < 0f) _calmSince = now; }
                    else _calmSince = -1f;
                    if ((_calmSince >= 0f && now - _calmSince > 0.7f) || now - _phaseAt > 4.5f)
                    {
                        FruitBodyControl.Stop(b);
                        SetFrame();
                        if (_cond.StartsWith("turn:"))
                        {
                            StartRun();
                            _feetInit = false;
                            _turnSign = -_turnSign;
                            float to = (_headingYaw + 120f * _turnSign) * Mathf.Deg2Rad;
                            FruitBodyControl.Face(b, new Vector3(Mathf.Sin(to), 0f, Mathf.Cos(to)), 7f);
                            _phase = Phase.Turn;
                            break;
                        }
                        _len = Length;
                        if (Physics.Raycast(_o + Vector3.up * 0.4f, _f, out RaycastHit hit, _len + 1f, FruitNav.Layers, QueryTriggerInteraction.Ignore))
                        {
                            _len = Mathf.Max(0f, hit.distance - 1f);
                            Note($"{Tag} something {hit.distance:0.0} m ahead (\"{(hit.collider != null ? hit.collider.name : "?")}\"): this run is {_len:0.0} m");
                        }
                        if (_len < MinLength) { Abort($"not enough room ahead ({_len:0.0} m)"); return; }
                        _goal = _o + _f * _len;
                        StartRun();
                        LaunchWalk();
                        _phase = Phase.Run;
                    }
                    break;
                }

                case Phase.Stand:
                    if (now - _runStart >= 4f) EndRun("stood");
                    break;

                case Phase.Turn:
                {
                    float t = now - _runStart;
                    float left = Mathf.Abs(Mathf.DeltaAngle(rt.CreatureYRotation, _headingYaw + 120f * _turnSign));
                    if (t > 0.6f && !FruitBodyControl.IsTurning(b)) EndRun($"turned (+{left:0} deg off)");
                    else if (t > 9f) EndRun("timed out");
                    break;
                }

                case Phase.Run:
                {
                    Vector3 p = Pelvis();
                    float s = Vector3.Dot(p - _o, _f);
                    float t = now - _runStart;
                    bool walking = FruitBodyControl.IsWalking(b);
                    if (s >= _len - (_cond.StartsWith("raw") ? 1.0f : 0.8f)) EndRun("arrived");
                    else if (t > 30f) EndRun("timed out");
                    else if (!walking && t > 0.5f) EndRun("order ended");
                    else if (b.Behaviour != FruitBehaviour.Standing && b.Behaviour != FruitBehaviour.Moving && t > 0.5f) EndRun("fell");
                    break;
                }
            }
        }

        private static void StartRun()
        {
            _run++;
            _runStart = Time.time;
            _n = 0; _sumL2 = _sumSpd = _sumYaw2 = 0.0; _maxL = 0f; _peakSpd = 0f; _hasYaw = false;
        }

        private static void LaunchWalk()
        {
            if (_cond.StartsWith("raw")) FruitBodyControl.RawWalk(_body, 35f);
            else FruitBodyControl.GoTo(_body, _goal, 0.7f, 35f);
        }

        private static void EndRun(string how)
        {
            FruitBodyControl.Stop(_body);
            float secs = Time.time - _runStart;
            if (_cond.StartsWith("turn:"))
            {
                float yawNow = Mathf.DeltaAngle(_headingYaw, _body.Bind.Runtime.CreatureYRotation);
                float drag = _dragL + _dragR;
                float per90 = Mathf.Abs(yawNow) > 5f ? drag * 100f / Mathf.Abs(yawNow) * 90f : 0f;
                Note($"{Tag} #{_run,2} {_cond,-9} {how,-22} {secs,4:0.0} s  turned {Mathf.Abs(yawNow):0} deg  moved {_maxDisp * 100:0} cm  steps {_liftL}+{_liftR}  feet slid {drag * 100:0} cm ({per90:0} per 90 deg)");
            }
            else if (_n > 0)
            {
                double rms = Math.Sqrt(_sumL2 / _n);
                Note($"{Tag} #{_run,2} {_cond,-9} {how,-11} {secs,5:0.0} s  speed {_sumSpd / _n:0.00} m/s peak {_peakSpd:0.00}  drift rms {rms * 100:0.0} cm max {_maxL * 100:0.0} cm  yaw wag rms {Math.Sqrt(_sumYaw2 / _n):0.0} deg");
            }
            if (_csv != null) _csv.WriteLine($"# run {_run} {_cond} {how} {secs:0.00} s");
            NextStep();
        }

        private static void Finish(string why)
        {
            Note($"{Tag} {why}. FruitGait.csv has every step; Frukt/Tools/gait_analyze.py reads it.");
            Cleanup();
        }

        private static void Abort(string why)
        {
            if (_phase == Phase.Idle && _csv == null) return;
            try { if (_body != null) FruitBodyControl.Stop(_body); } catch { }
            Note($"{Tag} {why}.");
            Cleanup();
        }

        private static void Cleanup()
        {
            FruitBodyControl.YawDamping = _savedDamping;
            FruitBodyControl.StartRampSeconds = _savedRamp;
            FruitBodyControl.PersistentYaw = _savedYaw;
            FruitBodyControl.StepTurns = _savedStepTurns;
            FruitBodyControl.StepTurnPace = _savedStepPace;
            try { _csv?.Flush(); _csv?.Dispose(); } catch { }
            _csv = null;
            _phase = Phase.Idle;
            _body = null;
        }

        // ── Sampling, every physics step of a run ──

        internal static void FixedTick()
        {
            if (_phase != Phase.Run && _phase != Phase.Stand && _phase != Phase.Turn) return;
            if (_csv == null || _body == null) return;
            var b = _body;
            try
            {
                var refs = b.Bind.Refs;
                var rt = b.Bind.Runtime;
                var mc = refs.MoveControl;
                var anim = refs.Animation;
                float t = Time.time - _runStart;

                Vector3 pel = Pelvis();
                var rb = PelvisRb();
                Vector3 vel = rb != null ? rb.linearVelocity : Vector3.zero;
                float wy = rb != null ? rb.angularVelocity.y : 0f;
                float yaw = rt.CreatureYRotation, tyaw = rt.PuppeteerTargetYRot;
                float yawRel = Mathf.DeltaAngle(_headingYaw, yaw);

                Vector3 lm = rt.TargetMoveDirection;
                Vector3 ad = mc != null ? mc.ActualAnimationDirection : Vector3.zero;
                float mspd = mc != null ? mc.m_currentAnimationSpeed : 0f;
                float bal = mc != null ? mc.m_balanceCoefficientInterpolated : 0f;
                float animT = mc != null ? mc.m_animationSpeedT : 0f;
                bool grounded = mc != null && mc.m_isGrounded;
                float aspd = anim != null && anim.Animator != null ? anim.Animator.speed : 0f;

                var sb = new StringBuilder(640);
                sb.Append(t.ToString("0.000")).Append(',').Append(_run).Append(',').Append(_cond).Append(',').Append(_aligned ? 1 : 0).Append(',');
                F(sb, pel); sb.Append(',').Append(Dot(vel, _f).ToString("0.000")).Append(',').Append(Dot(vel, _r).ToString("0.000")).Append(',');
                sb.Append(yawRel.ToString("0.00")).Append(',').Append(Mathf.DeltaAngle(_headingYaw, tyaw).ToString("0.00")).Append(',');
                sb.Append(lm.x.ToString("0.000")).Append(',').Append(lm.z.ToString("0.000")).Append(',');
                sb.Append(ad.x.ToString("0.000")).Append(',').Append(ad.z.ToString("0.000")).Append(',');
                sb.Append(mspd.ToString("0.000")).Append(',').Append(aspd.ToString("0.000")).Append(',');
                sb.Append(bal.ToString("0.000")).Append(',').Append(animT.ToString("0.000")).Append(',').Append(grounded ? 1 : 0);

                // Physical feet.
                sb.Append(','); F(sb, b.Limbs[3].LivePosition);
                sb.Append(','); F(sb, b.Limbs[6].LivePosition);

                // The animator's own armature and the armature the muscles follow.
                float oyaw = Bones(sb, anim?.ArmatureOriginal);
                float ryaw = Bones(sb, anim?.ArmatureReprojection);

                sb.Append(',').Append(wy.ToString("0.000"));
                sb.Append(',').Append(FruitBodyControl.DampTorque(b).ToString("0.00"));
                sb.Append(',').Append(oyaw.ToString("0.00")).Append(',').Append(ryaw.ToString("0.00"));
                sb.Append(',').Append(LimbYaw(FruitBodies.Torso).ToString("0.00"));
                sb.Append(',').Append(LimbYaw(FruitBodies.Head).ToString("0.00"));
                _csv.WriteLine(sb.ToString());

                if (_phase == Phase.Turn) TrackFeet(b, pel);

                if (_phase == Phase.Run)
                {
                    float l = Dot(pel - _o, _r);
                    float v = Dot(vel, _f);
                    _n++;
                    _sumL2 += l * l;
                    _maxL = Mathf.Max(_maxL, Mathf.Abs(l));
                    _sumSpd += v;
                    _peakSpd = Mathf.Max(_peakSpd, v);
                    // The wag: yaw against its own slow trend (about a third of a second).
                    if (!_hasYaw) { _prevYaw = yawRel; _hasYaw = true; }
                    _prevYaw += (yawRel - _prevYaw) * 0.06f;
                    if (t > 1.5f) { double e = yawRel - _prevYaw; _sumYaw2 += e * e; }
                }
            }
            catch (Exception e)
            {
                FruitBodies.ReportOnce("gait probe sample", e);
            }
        }

        /// <summary>A turn run's feet: planted-foot slide, steps (a foot rising 5 cm and coming back) and how far the pelvis went.</summary>
        private static void TrackFeet(FruitBody b, Vector3 pel)
        {
            Vector3 l = b.Limbs[3].LivePosition, r = b.Limbs[6].LivePosition;
            if (!_feetInit)
            {
                _feetInit = true;
                _lPrev = l; _rPrev = r; _floorL = l.y; _floorR = r.y;
                _dragL = _dragR = _maxDisp = 0f; _liftL = _liftR = 0; _upL = _upR = false;
                return;
            }
            _floorL = Mathf.Min(_floorL, l.y); _floorR = Mathf.Min(_floorR, r.y);
            float sl = Mathf.Sqrt((l.x - _lPrev.x) * (l.x - _lPrev.x) + (l.z - _lPrev.z) * (l.z - _lPrev.z));
            float sr = Mathf.Sqrt((r.x - _rPrev.x) * (r.x - _rPrev.x) + (r.z - _rPrev.z) * (r.z - _rPrev.z));
            if (l.y < _floorL + 0.02f) _dragL += sl;
            if (r.y < _floorR + 0.02f) _dragR += sr;
            if (!_upL && l.y > _floorL + 0.05f) { _upL = true; _liftL++; }
            else if (_upL && l.y < _floorL + 0.025f) _upL = false;
            if (!_upR && r.y > _floorR + 0.05f) { _upR = true; _liftR++; }
            else if (_upR && r.y < _floorR + 0.025f) _upR = false;
            _lPrev = l; _rPrev = r;
            Vector3 d = pel - _o; d.y = 0f;
            _maxDisp = Mathf.Max(_maxDisp, d.magnitude);
        }

        /// <summary>Appends a rig's pelvis and both feet; returns the pelvis bone's yaw against the run's heading.</summary>
        private static float Bones(StringBuilder sb, HumanoidPuppeteerBoneMapHandler h)
        {
            Transform pel = null, lf = null, rf = null;
            if (h != null)
            {
                try { pel = h.m_pelvis; lf = h.m_leftFoot; rf = h.m_rightFoot; } catch { }
            }
            sb.Append(','); F(sb, pel != null ? pel.position : Vector3.zero);
            sb.Append(','); F(sb, lf != null ? lf.position : Vector3.zero);
            sb.Append(','); F(sb, rf != null ? rf.position : Vector3.zero);
            // The bone's own axes need not be the body's, so only its change of yaw is meant to be
            // read: the wag's frequency and amplitude, not an absolute heading.
            return pel != null ? Mathf.DeltaAngle(_headingYaw, pel.eulerAngles.y) : 0f;
        }

        private static float LimbYaw(int limb)
        {
            try
            {
                var l = _body.Limbs[limb];
                if (!l.Present || l.Physics == null || l.Physics.WasCollected) return 0f;
                return Mathf.DeltaAngle(_headingYaw, l.Physics.m_rb.rotation.eulerAngles.y);
            }
            catch { return 0f; }
        }

        /// <summary>A world position as "f,l,y" in the run's frame.</summary>
        private static void F(StringBuilder sb, Vector3 p)
        {
            Vector3 d = p - _o;
            sb.Append(Dot(d, _f).ToString("0.000")).Append(',');
            sb.Append(Dot(d, _r).ToString("0.000")).Append(',');
            sb.Append(d.y.ToString("0.000"));
        }

        private static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.z * b.z;

        // ── Helpers ──

        private static Vector3 Pelvis()
        {
            var l = _body.Limbs[FruitBodies.Pelvis];
            return l.Present ? l.LivePosition : _body.Position;
        }

        private static Rigidbody PelvisRb()
        {
            var l = _body?.Limbs[FruitBodies.Pelvis];
            if (l == null || !l.Present || l.Physics == null || l.Physics.WasCollected) return null;
            try { return l.Physics.m_rb; } catch { return null; }
        }

        /// <summary>The body nearest the crosshair within 12 degrees, else the nearest to the camera.</summary>
        private static FruitBody Looked()
        {
            FruitBody best = null;
            float bestA = 12f;
            if (FruitBodies.HasView)
                foreach (var b in FruitBodies.All)
                {
                    if (!b.HasPosition || !b.Alive) continue;
                    float a = Vector3.Angle(FruitBodies.ViewForward, b.HeadPosition - FruitBodies.ViewPosition);
                    if (a < bestA) { bestA = a; best = b; }
                }
            if (best != null) return best;
            float bestD = float.MaxValue;
            foreach (var b in FruitBodies.All)
            {
                if (!b.HasPosition || !b.Alive) continue;
                float d = FruitBodies.HasView ? Vector3.Distance(FruitBodies.ViewPosition, b.Position) : 0f;
                if (d < bestD) { bestD = d; best = b; }
            }
            return best;
        }

        private static void Note(string s) => MelonLogger.Msg(s);
    }
}
