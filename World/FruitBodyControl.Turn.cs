using System;
using UnityEngine;

namespace FruitLib
{
    public static partial class FruitBodyControl
    {
        // ── Turning on the spot ──
        //
        // A standing body turns by the same target yaw a walk steers with, but without a walk
        // request. Whether the puppeteer turns a body that isn't walking is not known yet: if a
        // turn has made no headway after TurnCheckSeconds, the body turns with small steps instead
        // (walk request held, moving along the new heading), and the log says which one it was.

        private const float TurnCheckSeconds = 1f;
        private static bool _turnLogged;

        /// <summary>
        /// Turns with steps instead of dragging the feet. The game has no turn-on-the-spot
        /// animation (the controller holds Idle and eight Moving_* clips), so a standing body that
        /// is only given a new yaw turns its pelvis with both feet planted: the feet drag across
        /// the floor. With this on, a turn of more than <see cref="StepTurnFrom"/> degrees also
        /// holds a walk request at a small forward move direction (<see cref="StepTurnPace"/>)
        /// while the yaw sweeps, so the body walks a tight arc on its own steps; at less than
        /// <see cref="StepTurnStop"/> degrees to go it lets go and the last of the turn is
        /// dragged. Applies to Face and the pivots of a walk. On by default since the
        /// gait probe's turn runs (2026-10-11): at a pace of 0.6 a 120-degree turn takes 1.2 s
        /// with three real steps (feet up 9 cm) and 19 cm of drift, against 3.4-4.5 s of
        /// dragging with the feet up 1-5 cm. Since 5.32.0.
        /// </summary>
        public static bool StepTurns { get => FruitLibConfig.StepTurns; set => FruitLibConfig.StepTurns = value; }

        /// <summary>Forward move direction a stepping turn holds, 0..1 (FruitLibConfig.StepTurnPace, 0.6).</summary>
        public static float StepTurnPace { get => FruitLibConfig.StepTurnPace; set => FruitLibConfig.StepTurnPace = value; }

        public static float StepTurnFrom = 30f, StepTurnStop = 15f;

        /// <summary>One frame of a turn with steps: the yaw sweeps as ever; a big enough error also walks a small arc.</summary>
        private static void StepTurnTick(Order o, float desired, float rate, float dt, float now)
        {
            var rt = o.Runtime;
            float err = Mathf.Abs(Mathf.DeltaAngle(rt.CreatureYRotation, desired));
            bool step = err > (o.StepTurning ? StepTurnStop : StepTurnFrom);
            step = step && TurnStepClear(o);
            o.StepTurning = step;
            rt.UpdateTargetYRot(SteerYaw(o, desired, rate, dt));
            if (step)
            {
                if (!o.Requested)
                {
                    if (!o.Walk.HoldsWalkRequest(Holder)) o.Walk.AddWalkRequest(Holder);
                    o.Requested = true;
                    o.StartAt = now;
                }
                rt.SetTargetMoveDirection(new Vector3(0f, 0f, Mathf.Clamp01(StepTurnPace)));
            }
            else if (o.Requested) Release(o);
        }

        private static bool TurnStepClear(Order o)
        {
            Vector3 centre = LiveCentre(o.Body), forward = o.Body.Facing;
            forward.y = 0f; forward.Normalize();
            if (!TrafficClear(o.Body, o.Target, centre, forward, 1.5f)) return false;
            return GeometryClear(centre, forward, 0.65f);
        }

        private static bool GeometryClear(Vector3 centre, Vector3 forward, float distance)
        {
            forward.y = 0f; forward.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, forward) * 0.3f;
            // The arc moves along the current heading, never sideways toward the eventual heading.
            // Check both shoulders against map geometry; this also works before a navmesh is ready.
            for (int i = -1; i <= 1; i++)
                if (Physics.Raycast(centre + side * i, forward, distance, FruitNav.Layers, QueryTriggerInteraction.Ignore)) return false;
            return true;
        }

        /// <summary>Turns a body on the spot to face a world direction (y ignored). Done when it
        /// faces it, or after <paramref name="seconds"/>. Since 5.18.0.</summary>
        public static bool Face(FruitBody body, Vector3 worldDirection, float seconds = 3f)
        {
            if (body == null || !body.Alive) return false;
            worldDirection.y = 0f;
            if (worldDirection.sqrMagnitude < 1e-4f) return false;
            var o = OrderFor(body, seconds, false);
            o.Dir = worldDirection.normalized;
            StartTurn(o, body, 0f, seconds);
            return Bind(o);
        }

        /// <summary>Looks left, then right, sampling each pause with the feet planted.
        /// Returns the head to centre; does not request walking or body rotation.</summary>
        public static bool LookAround(FruitBody body, float seconds = 4f)
            => StartLookScan(body, seconds);

        /// <summary>Whether a body is turning on the spot (Face). Since 5.18.0.</summary>
        public static bool IsTurning(FruitBody body) => body != null && _orders.TryGetValue(body.Id, out var o) && o.Turn;

        private static void StartTurn(Order o, FruitBody body, float sweep, float seconds)
        {
            o.Turn = true;
            o.TurnSweep = sweep;
            o.TurnFrom = Mathf.Atan2(body.Facing.x, body.Facing.z) * Mathf.Rad2Deg;
            o.TurnStart = Time.time;
            o.TurnTime = Mathf.Max(0.5f, seconds);
            o.TurnStepping = false;
            o.TurnCheckYaw = float.NaN;
        }

        /// <summary>One frame of a turn. False when it is done.</summary>
        private static bool TickTurn(Order o, float now, float dt)
        {
            var b = o.Body;
            var rt = o.Runtime;
            float facing = rt.CreatureYRotation;
            float desired;
            if (o.TurnSweep != 0f)
            {
                float t = Mathf.Clamp01((now - o.TurnStart) / o.TurnTime);
                if (now >= o.TurnStart + o.TurnTime + 0.3f) return false;
                desired = o.TurnFrom + o.TurnSweep * t;
            }
            else
            {
                desired = Mathf.Atan2(o.Dir.x, o.Dir.z) * Mathf.Rad2Deg;
                if (Mathf.Abs(Mathf.DeltaAngle(facing, desired)) < 12f)
                {
                    // Settle the command at the measured heading so a following raw walk cannot
                    // inherit the last few degrees of an unfinished standing turn.
                    rt.UpdateTargetYRot(facing);
                    return false;
                }
            }

            // The headway check: a second in, has the body turned at all?
            if (float.IsNaN(o.TurnCheckYaw)) o.TurnCheckYaw = facing;
            else if (!o.TurnStepping && now - o.TurnStart >= TurnCheckSeconds && o.TurnCheckYaw < 1000f)
            {
                float turned = Mathf.Abs(Mathf.DeltaAngle(o.TurnCheckYaw, facing));
                if (turned < 15f) o.TurnStepping = true;
                if (!_turnLogged)
                {
                    _turnLogged = true;
                    FruitLog.Info($"[FruitBodyControl] turn check: {turned:0} deg in {now - o.TurnStart:0.0} s from the target yaw alone; " +
                                  (o.TurnStepping ? "turning with steps instead" : "standing turns work"));
                }
                o.TurnCheckYaw = 9999f;   // checked
            }

            if ((StepTurns || o.TurnStepping) && o.Walk != null)
            {
                StepTurnTick(o, desired, TurnRate * 1.5f, dt, now);
                return true;
            }
            o.StepTurning = false;

            float yaw = SteerYaw(o, desired, TurnRate * 1.5f, dt);
            rt.UpdateTargetYRot(yaw);

            return true;
        }
    }
}
