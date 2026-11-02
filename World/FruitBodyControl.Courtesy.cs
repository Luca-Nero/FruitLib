using System;
using System.Collections.Generic;
using UnityEngine;

namespace FruitLib
{
    public static partial class FruitBodyControl
    {
        private sealed class RoomRequest
        {
            public Order Walker;
            public FruitBody Blocker;
            public Vector3 Spot, Forward, Origin, Waypoint;
            public bool Retreat;
        }
        private static readonly Dictionary<int, RoomRequest> _roomRequests = new Dictionary<int, RoomRequest>();
        private static readonly Dictionary<int, float> _roomCooldown = new Dictionary<int, float>();

        private static bool CanMakeRoom(FruitBody b, Order walker)
        {
            if (!b.Alive || !b.Conscious || !Upright(b) || HandsBusyForRoom(b) || NativeCareBusy(b) || Swinging(b) || FruitPose.IsHolding(b)) return false;
            if (!_orders.TryGetValue(b.Id, out var other)) return true;
            if (other.Courtesy || other.Strikes || other.Stance || other.Raw || other.Turn || other.Run) return false;
            // A following body at rest may yield. For two stalled walkers, the lower ID keeps
            // the lane; the other's complete order is suspended and later restored.
            return other.Paused || (other.TrafficBlocked && other.Body.Id > walker.Body.Id);
        }

        private static void QueueRoom(Order o, FruitBody target, Vector3 me, Vector3 forward, float now)
        {
            if (o.Courtesy || now - o.TrafficSince < 0.6f || now < o.RoomCheckAt || !FruitNav.Ready) return;
            o.RoomCheckAt = now + 0.8f;
            if (o.RoomForward.sqrMagnitude > 0.5f) forward = o.RoomForward;
            FruitBody blocker = null;
            float nearest = 2.8f;
            foreach (var b in FruitBodies.All)
            {
                if (b == o.Body || b == target || !b.HasPosition || Math.Abs(b.Position.y - me.y) > 1.6f) continue;
                Vector3 rel = b.Position - me;
                float ahead = rel.x * forward.x + rel.z * forward.z;
                float across = Math.Abs(rel.x * forward.z - rel.z * forward.x);
                if (ahead < 0f || ahead > nearest || across > 1.1f || !CanMakeRoom(b, o)) continue;
                if (_roomCooldown.TryGetValue(b.Id, out float until) && now < until) continue;
                blocker = b; nearest = ahead;
            }
            if (blocker == null) return;
            if (_roomRequests.TryGetValue(blocker.Id, out var pending) && pending.Walker.Body.Id <= o.Body.Id) return;
            if (!RoomSpot(o, blocker, me, forward, out Vector3 spot, out Vector3 waypoint, out bool retreat)) return;
            _roomRequests[blocker.Id] = new RoomRequest { Walker = o, Blocker = blocker, Spot = spot,
                Forward = forward, Origin = LiveCentre(blocker), Waypoint = waypoint, Retreat = retreat };
        }

        private static bool RoomSpot(Order walker, FruitBody blocker, Vector3 me, Vector3 forward,
            out Vector3 spot, out Vector3 waypoint, out bool retreat)
        {
            waypoint = default; retreat = false;
            Vector3 origin = LiveCentre(blocker), right = new Vector3(forward.z, 0f, -forward.x);
            // Search beside the lane, then farther along it for a pocket outside a doorway.
            for (int row = 0; row < 5; row++)
            {
                float along = row == 0 ? 0f : ((row + 1) / 2) * 1.2f * (row % 2 == 1 ? 1f : -1f);
                for (int side = 1; side >= -1; side -= 2)
                {
                    Vector3 aim = origin + forward * along + right * (side * (Math.Max(PersonalSpace, 1f) + 0.6f));
                    if (!PassPoint(aim, blocker, out Vector3 floor)) continue;
                    bool direct = LocalLegClear(blocker, origin, floor);
                    Vector3 first = floor;
                    if (!direct)
                    {
                        // A diagonal shortcut through a doorway wall is not a passing pocket.
                        // Move away from the waiting walker along the corridor, then sideways.
                        // Do not retreat towards the walker or pick a point still in the throat.
                        if (along <= 0f) continue;
                        first = origin + forward * along;
                        if (!FruitNav.Under(first, 0.5f, out Vector3 junction) || FruitNav.Flat(junction - first) > 0.15f) continue;
                        first = junction;
                        if (!LocalLegClear(blocker, origin, first) ||
                            !LocalLegClear(blocker, first + new Vector3(0f, origin.y - junction.y, 0f), floor)) continue;
                    }
                    Vector3 delta = first - origin;
                    if (!TrafficClear(blocker, null, origin, FlatDirection(delta), Math.Min(0.9f, FruitNav.Flat(delta)))) continue;
                    bool taken = false;
                    foreach (var request in _roomRequests.Values)
                        if (request.Blocker != blocker && FruitNav.Flat(request.Spot - floor) < PersonalSpace + 0.5f) { taken = true; break; }
                    if (taken) continue;
                    // Keeping a full body width outside the requested lane is essential: a tiny
                    // scoot that still blocks the doorway merely starts another negotiation.
                    Vector3 rel = floor - me;
                    if (Math.Abs(rel.x * right.x + rel.z * right.z) < 1.3f) continue;
                    spot = floor;
                    waypoint = first; retreat = !direct;
                    return true;
                }
            }
            spot = default;
            return false;
        }

        private static void StartRoomRequests(float now)
        {
            // Called after the walk loop: never change its dictionary while it is enumerated.
            foreach (var request in _roomRequests.Values)
            {
                var b = request.Blocker;
                if (!_orders.TryGetValue(request.Walker.Body.Id, out var current) || current != request.Walker ||
                    !current.TrafficBlocked || !CanMakeRoom(b, current) ||
                    !PassPoint(request.Spot, b, out Vector3 floor) ||
                    !LocalLegClear(b, LiveCentre(b), request.Retreat ? request.Waypoint : floor)) continue;
                _orders.TryGetValue(b.Id, out var original);
                var aside = new Order { Body = b, HasDest = true, Dest = floor, Stand = floor, Want = floor,
                    StopDistance = 0.25f, WantStop = 0.25f, Stance = true, Until = now + (request.Retreat ? 20f : 12f),
                    PathAt = -100f, ProgressLeft = float.MaxValue, ProgressAt = now + ProgressWindow,
                    Courtesy = true, CourtesyStart = now, CourtesyFor = current.Body, Suspended = original,
                    CourtesyOrigin = request.Origin, CourtesyForward = request.Forward,
                    CourtesyRetreat = request.Retreat, CourtesyWaypoint = request.Waypoint,
                    Dir = (floor - LiveCentre(b)).normalized };
                if (!Bind(aside) || !BeginLocalStep(aside, request.Retreat ? request.Waypoint : floor, now)) continue;
                if (original != null) Release(original);
                StopLookScan(b.Id, false);
                _orders[b.Id] = aside;
                _roomCooldown[b.Id] = now + 12f;
                current.RoomPartner = b;
                current.RoomWaitUntil = aside.Until;
                FruitLog.Info($"[FruitBodyControl] #{current.Body.Id} needs room; #{b.Id} {(request.Retreat ? "backing to a passing pocket" : "stepping aside")}.");
            }
            _roomRequests.Clear();
        }

        private static bool WaitingForRoom(Order o, float now)
        {
            if (o.RoomPartner == null || now >= o.RoomWaitUntil) return false;
            return _orders.TryGetValue(o.RoomPartner.Id, out var partner) && partner.Courtesy &&
                partner.CourtesyFor == o.Body &&
                (partner.LocalStep || FruitNav.Flat(partner.Dest - LiveCentre(partner.Body)) > partner.StopDistance);
        }

        private static bool CourtesyFinished(Order o, float now)
        {
            if (o.CourtesyFor == null || !_orders.ContainsKey(o.CourtesyFor.Id)) return true;
            Vector3 owner = LiveCentre(o.CourtesyFor), rel = owner - o.CourtesyOrigin;
            return FruitNav.Flat(rel) > 4f ||
                (rel.x * o.CourtesyForward.x + rel.z * o.CourtesyForward.z > 1.2f && FruitNav.Flat(owner - LiveCentre(o.Body)) > 1.2f);
        }

        private static void RestoreCourtesy(Order o, float now)
        {
            var original = o.Suspended;
            if (original == null || !o.Body.Alive || !o.Body.Conscious) return;
            original.Until += now - o.CourtesyStart;
            original.PathAt = -100f; original.ComInit = false; original.PassStage = 0;
            original.TrafficBlocked = false; original.TrafficCommitUntil = 0f; original.Pivot = false;
            original.LocalStep = false; original.LocalStandalone = false;
            original.YieldUntil = 0f; original.ProgressAt = now + ProgressWindow; original.ProgressLeft = float.MaxValue;
            _orders[o.Body.Id] = original;
        }

        private static Order CancelCourtesy(Order o)
        {
            if (!o.Courtesy) return o;
            var original = o.Suspended;
            o.Suspended = null;
            o.CourtesyFor = null;
            return original;
        }

        private static void ResetCourtesy() { _roomRequests.Clear(); _roomCooldown.Clear(); }
    }
}
