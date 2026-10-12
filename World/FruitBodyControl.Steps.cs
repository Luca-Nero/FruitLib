using System;
using UnityEngine;

namespace FruitLib
{
    public static partial class FruitBodyControl
    {
        // ── Step assist ──
        //
        // FRUKT's walk barely lifts the feet (spike S3, 2026-10-10: a 17 cm stair stops a body
        // dead; a 15 degree ramp is fine). So while a body walks under an order, each physics
        // step looks a short way ahead of both feet for a riser: something it walks into at ankle
        // height whose top is a step up (1.2 to 32 cm since 5.24.0; 4 cm before) with room above it. Then, for a moment:
        //
        //   - the foot is lifted up and forward over the edge (its velocity, and the lower leg's),
        //   - the whole body is raised by what the step needs to put the pelvis back at standing
        //     height over the new floor: the same upward acceleration on every limb, so the pose
        //     is kept and nothing is pulled apart, eased in and out, upward only.
        //
        // The trailing foot then meets the same riser with the pelvis already up: it gets the foot
        // lift and no rise. Off with FruitLibConfig.StepAssist (FruitNav then treats steps as walls).

        /// <summary>Highest step the assist takes, metres.</summary>
        public static float MaxStep = 0.32f;

        /// <summary>Lowest ledge the assist lifts a foot over, metres. 5.19-5.23 took nothing under
        /// 4 cm and looked for risers 5 cm up, so a 3 cm kerb or threshold, which still stubs a foot
        /// that barely lifts, was never seen. Since 5.24.0.</summary>
        public static float MinStep = 0.012f;

        /// <summary>Raised when the assist lifts a body up a step: body, step height, foot limb. For diagnostics. Since 5.19.0.</summary>
        public static event Action<FruitBody, float, int> Stepped;

        private const float StepSeconds = 0.42f, FootPhase = 0.55f, FootCooldown = 0.3f;
        private const float StandPelvis = 0.95f;
        private const int LeftFoot = 3, LeftShin = 2, RightFoot = 6, RightShin = 5;

        /// <summary>From FruitLibMod.OnFixedUpdate.</summary>
        internal static void FixedTick()
        {
            GaitFixedTick();   // FruitBodyControl.Gait.cs
            if (_orders.Count == 0 || !FruitLibConfig.StepAssist) return;
            float now = Time.fixedTime;
            foreach (var kv in _orders)
            {
                var o = kv.Value;
                try
                {
                    if (now < o.StepUntil) { Lift(o, now); continue; }
                    o.StepFoot = -1;
                    if (!o.Requested || o.Paused || o.Turn || !Upright(o.Body)) continue;
                    if (!Limbs(o)) continue;
                    if (now >= o.LeftFootReady && Probe(o, LeftFoot, now)) continue;
                    if (now >= o.RightFootReady) Probe(o, RightFoot, now);
                }
                catch (Exception e) { FruitBodies.ReportOnce("step assist", e); }
            }
        }

        /// <summary>The body's limb rigidbodies, cached per binding.</summary>
        private static bool Limbs(Order o)
        {
            var b = o.Body;
            if (o.RbsFor == b.CreaturePtr && o.Rbs[FruitBodies.Pelvis] != null && !o.Rbs[FruitBodies.Pelvis].WasCollected) return true;
            for (int i = 0; i < FruitBodies.LimbCount; i++) o.Rbs[i] = Rb(b.Limbs[i]);
            o.RbsFor = b.CreaturePtr;
            return o.Rbs[FruitBodies.Pelvis] != null && o.Rbs[LeftFoot] != null && o.Rbs[RightFoot] != null;
        }

        /// <summary>Looks for a riser ahead of one foot; starts a step if there is one.</summary>
        private static bool Probe(Order o, int foot, float now)
        {
            var rb = o.Rbs[foot];
            if (rb == null || rb.WasCollected) return false;
            Vector3 dir = o.Dir;
            Vector3 f = rb.position;

            // The floor under the foot.
            if (!Physics.Raycast(f + Vector3.up * 0.1f, Vector3.down, out RaycastHit floor, 0.45f, FruitNav.Layers, QueryTriggerInteraction.Ignore))
                return false;
            float floorY = floor.point.y;

            // Something at ankle height just ahead, standing up (a riser, not more floor). Since 5.24.0
            // also just over the floor, and further ahead the faster the body goes.
            bool old = o.OldFooting || !Smooth(o);
            float reach = old ? 0.3f : 0.3f + 0.25f * FlatSpeed(o);
            RaycastHit riser = default;
            Vector3 ankle = default;
            bool found = false;
            foreach (float up in old ? OldRiserRays : RiserRays)
            {
                ankle = new Vector3(f.x, floorY + up, f.z) - dir * 0.04f;
                if (Physics.Raycast(ankle, dir, out riser, reach, FruitNav.Layers, QueryTriggerInteraction.Ignore) && riser.normal.y <= 0.6f)
                { found = true; break; }
            }
            if (!found) return false;

            // Its top: down onto it from above, just past the edge.
            Vector3 over = riser.point + dir * 0.08f;
            over.y = floorY + MaxStep + 0.1f;
            if (!Physics.Raycast(over, Vector3.down, out RaycastHit top, MaxStep + 0.1f, FruitNav.Layers, QueryTriggerInteraction.Ignore)) return false;
            if (top.normal.y < 0.7f) return false;
            float h = top.point.y - floorY;
            if (h < (old ? 0.04f : MinStep) || h > MaxStep) return false;

            // Room to stand on it: nothing at knee to hip height over the step.
            Vector3 knee = new Vector3(ankle.x, top.point.y + 0.45f, ankle.z);
            if (Physics.Raycast(knee, dir, 0.45f, FruitNav.Layers, QueryTriggerInteraction.Ignore)) return false;

            var pelvis = o.Rbs[FruitBodies.Pelvis];
            float pelvisY = pelvis.position.y;
            o.StepFoot = foot;
            o.StepStart = now;
            o.StepUntil = now + StepSeconds;
            o.StepPelvisY0 = pelvisY;
            // Only what is needed to stand on the new floor: none for the second foot.
            o.StepRise = Mathf.Clamp(top.point.y + StandPelvis - pelvisY, 0f, h + 0.05f);
            o.StepH = h;
            if (foot == LeftFoot) o.LeftFootReady = now + StepSeconds * FootPhase + FootCooldown;
            else o.RightFootReady = now + StepSeconds * FootPhase + FootCooldown;

            if (Stepped != null) { try { Stepped(o.Body, h, foot); } catch { } }
            return true;
        }

        /// <summary>One physics step of a step in progress.</summary>
        private static void Lift(Order o, float now)
        {
            if (o.StepFoot < 0 || !Limbs(o)) return;
            float t = Mathf.Clamp01((now - o.StepStart) / StepSeconds);
            Vector3 dir = o.Dir;

            // The foot: up over the edge, then forward onto it.
            if (t < FootPhase)
            {
                float up = (o.StepH + 0.08f) / (StepSeconds * FootPhase) * 1.6f;
                int shin = o.StepFoot == LeftFoot ? LeftShin : RightShin;
                PushFoot(o.Rbs[o.StepFoot], up, dir, 1.1f);
                PushFoot(o.Rbs[shin], up * 0.6f, dir, 0.8f);
            }

            if (o.StepRise <= 0.01f) return;

            // The body: follow an eased rise with a damped spring, upward only.
            var pelvis = o.Rbs[FruitBodies.Pelvis];
            float s = t * t * (3f - 2f * t);
            float want = o.StepPelvisY0 + o.StepRise * s;
            float vy = pelvis.linearVelocity.y;
            float acc = 90f * (want - pelvis.position.y) - 10f * vy;
            acc = Mathf.Clamp(acc, 0f, 28f);
            if (acc <= 0f) return;
            Vector3 a = new Vector3(dir.x * 1.2f, acc, dir.z * 1.2f);
            for (int i = 0; i < FruitBodies.LimbCount; i++)
            {
                if (i == o.StepFoot) continue;
                var rb = o.Rbs[i];
                if (rb != null && !rb.WasCollected) rb.AddForce(a, ForceMode.Acceleration);
            }
        }

        private static readonly float[] RiserRays = { 0.015f, 0.05f }, OldRiserRays = { 0.05f };

        private static void PushFoot(Rigidbody rb, float up, Vector3 dir, float forward)
        {
            if (rb == null || rb.WasCollected) return;
            Vector3 v = rb.linearVelocity;
            if (v.y < up) v.y = up;
            float fwd = v.x * dir.x + v.z * dir.z;
            if (fwd < forward) { v.x += dir.x * (forward - fwd); v.z += dir.z * (forward - fwd); }
            rb.linearVelocity = v;
        }
    }
}
