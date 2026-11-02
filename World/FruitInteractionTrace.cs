using System;
using System.Globalization;
using UnityEngine;

namespace FruitLib
{
    /// <summary>Shared diagnostics for pickup, combat, tools and custom interactions.</summary>
    public static class FruitInteractionTrace
    {
        private static long _next;
        public static string NewId(string kind) => kind + ":" + (++_next).ToString(CultureInfo.InvariantCulture);

        // One record on transitions and at most once a second during an active interaction.
        // Do not infer reach/contact from the body's centre: record both measurements.
        public static void Write(string id, string stage, FruitBody actor, Vector3 target,
                                 float reach, bool left = false, string detail = "", Vector3? stance = null)
        {
            if (!FruitLibConfig.InteractionDiagnostics || actor == null) return;
            try
            {
                var hand = actor.Limbs[left ? 11 : 14];
                Vector3 delta = target - actor.Position;
                float error = hand.Present ? Vector3.Distance(hand.LivePosition, target) : -1f;
                string pose = FruitPose.Current(actor)?.Name ?? "standing";
                string move = FruitBodyControl.IsWalking(actor) ? "moving" : "stopped";
                FruitLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "[FruitInteraction] {0} body=#{1} stage={2} flat={3:0.000} dy={4:0.000} distance={5:0.000} hand={6:0.000} reach={7:0.000} pose={8} weight={9:0.00} balance={10:0.0} move={11} target=({12:0.000},{13:0.000},{14:0.000}) stance={15} {16}",
                    id, actor.Id, stage, FruitNav.Flat(delta), delta.y, delta.magnitude, error, reach,
                    pose, FruitPose.Weight(actor), actor.Balance, move, target.x, target.y, target.z,
                    stance.HasValue ? stance.Value.ToString("F3") : "none", detail));
            }
            catch (Exception e) { FruitBodies.ReportOnce("interaction trace", e); }
        }
    }
}
