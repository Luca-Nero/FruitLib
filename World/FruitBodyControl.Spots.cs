using System;
using System.Collections.Generic;
using UnityEngine;

namespace FruitLib
{
    public static partial class FruitBodyControl
    {
        // ── Spreading out ──
        //
        // Send five bodies to "the back room" and every one of them walks to the same point, the
        // middle of its floor, and they shove each other about it until they give up. So a goto
        // order to a point someone already stands on, or that another order has claimed, gets
        // the nearest free spot instead:
        //
        //   - in the same room or area as the point (or within a few metres outside any),
        //   - with room to stand (not in a doorway or a passage) and clear of doors,
        //   - an arm's length (PersonalSpace) from anyone standing or lying there and from every
        //     other order's spot, on the side nearest the walker.
        //
        // An order's spot is its claim. Walking up, a body checks every half second whether its
        // spot has been taken (someone else stopped there) and, if so, picks another. FruitNav's
        // cover search skips taken spots too, so bodies hiding from the same threat hide apart.
        //
        // The claim is where the body will stand, not the point it was sent to: an order that
        // stops a metre short stands a metre short, on its way in. Before 5.25.0 the point was the
        // claim, so the next body's "free" spot could be right where the first one stopped. A body
        // given a spot walks onto it (SpotStop), and on arrival turns to the others standing about
        // (FaceTheGroup), so a crowd stands as a group instead of in a queue facing the wall.

        /// <summary>Goto orders pick a free spot near a point that is taken. Since 5.22.0.</summary>
        public static bool SpreadOut = true;
        /// <summary>Flat metres a spot keeps from a body standing or about to stand there, and from a
        /// body lying there. Since 5.22.0.</summary>
        public static float PersonalSpace = 0.9f, LyingSpace = 1.2f;
        /// <summary>How far from the point a free spot is looked for when the point is in no room or
        /// area, and at most inside one. Since 5.22.0.</summary>
        public static float SpreadRadius = 3f, SpreadRadiusMax = 7f;
        /// <summary>How close a body walks to a spot it was given (it stands on it). Was 0.7 m. Since 5.25.0.</summary>
        public static float SpotStop = 0.4f;
        /// <summary>On arrival, a body turns to the others standing within <see cref="GroupRadius"/>,
        /// or, with nobody there, to the point it was sent to when its spot is off to one side.
        /// Since 5.25.0.</summary>
        public static bool FaceTheGroup = true;
        public static float GroupRadius = 3f;

        private const float ReslotEvery = 0.5f, ReslotWithin = 4f;
        private const int MaxReslots = 4;

        /// <summary>
        /// Whether a point is taken: someone stands or lies within <see cref="PersonalSpace"/>
        /// (<see cref="LyingSpace"/>) of it, or another goto order will stand that close (since
        /// 5.25.0 where it will stop, before that the point it was sent to). Bodies walking past
        /// don't count. <paramref name="except"/> is left out. Since 5.22.0; since 5.26.0 the slots
        /// orders to a body have taken round it count as well.
        /// </summary>
        public static bool Taken(Vector3 point, FruitBody except = null) => Taken(point, except, null);

        /// <summary>As above, with the body a slot is round left out too (it is the ring's centre).</summary>
        private static bool Taken(Vector3 point, FruitBody except, FruitBody centre, float slack = 0f)
        {
            var all = FruitBodies.All;
            for (int i = 0; i < all.Count; i++)
            {
                var b = all[i];
                if (b == except || b == centre || !b.HasPosition) continue;
                if (Math.Abs(b.Position.y - point.y) > 1.6f) continue;   // another storey
                bool down = !Upright(b);
                // A body on its way somewhere isn't where it stands; its spot is checked below.
                if (!down && OnItsWay(b)) continue;
                float space = (down ? LyingSpace : PersonalSpace) + slack;
                float dx = b.Position.x - point.x, dz = b.Position.z - point.z;
                if (dx * dx + dz * dz < space * space) return true;
            }
            foreach (var o in _orders.Values)
            {
                if (o.Body == except || o.Body == centre) continue;
                Vector3 at;
                if (o.HasDest) at = o.Stand;
                else if (o.Slotted && o.Target != null) at = o.Slot;
                else continue;
                float dx = at.x - point.x, dz = at.z - point.z;
                float space = PersonalSpace + slack;
                if (dx * dx + dz * dz < space * space && Math.Abs(at.y - point.y) < 1.6f) return true;
            }
            return false;
        }

        /// <summary>Walking a goto order (a body turning on the spot stands where it is).</summary>
        private static bool OnItsWay(FruitBody b)
            => _orders.TryGetValue(b.Id, out var o) && IsGoto(o) && !o.Paused && (o.Requested || o.Pivot);

        /// <summary>
        /// The nearest free spot to <paramref name="want"/> for <paramref name="body"/> to stand on
        /// (see the notes above), or <paramref name="want"/> itself if it is free. False if there is
        /// none within reach. Needs a navmesh for anything but the point itself. Since 5.22.0.
        /// </summary>
        public static bool FreeSpot(FruitBody body, Vector3 want, out Vector3 spot)
        {
            var place = SpreadPlace(want, out float radius);
            return FreeSpot(body, want, radius, place, out spot);
        }

        /// <summary>The room or area a point is in (rooms first), and how far a spot may be from it.</summary>
        private static FruitPlace SpreadPlace(Vector3 want, out float radius)
        {
            var place = FruitPlaces.Where(want);
            if (place != null && place.Kind == FruitPlaceKind.Building) place = null;
            radius = place != null
                ? Mathf.Clamp(0.5f * Mathf.Sqrt(place.Size.x * place.Size.x + place.Size.z * place.Size.z), SpreadRadius, SpreadRadiusMax)
                : SpreadRadius;
            return place;
        }

        private static readonly List<Vector3> _ring = new List<Vector3>(32);

        private static bool FreeSpot(FruitBody body, Vector3 want, float radius, FruitPlace place, out Vector3 spot)
        {
            spot = want;
            // A walker may stop SpotStop short of its claim. Leave that margin when allocating
            // another spot, otherwise distinct claims can still put shoulders in the same place.
            if (!Taken(want, body, null, SpotStop) && !NearDoor(want)) return true;
            if (!FruitNav.Ready) return false;
            Vector3 from = body != null && body.HasPosition ? body.Position : want;
            if (!FruitNav.Sample(want, 1.5f, out Vector3 centre)) return false;

            for (float r = 0.8f; r <= radius + 0.01f; r += 0.6f)
            {
                int n = Math.Clamp((int)Math.Round(2.0 * Math.PI * r / 0.6), 6, 32);
                float a0 = MathF.Atan2(from.z - centre.z, from.x - centre.x);   // start on the walker's side
                _ring.Clear();
                for (int k = 0; k < n; k++)
                {
                    float a = a0 + k * (MathF.PI * 2f / n);
                    Vector3 p = centre;
                    p.x += MathF.Cos(a) * r; p.z += MathF.Sin(a) * r;
                    if (place != null && !place.Contains(p, -0.35f)) continue;
                    if (!FruitNav.Sample(p, 0.6f, out Vector3 q)) continue;
                    if (FruitNav.Flat(q - p) > 0.4f) continue;              // pulled off through a wall or round a car
                    if (Math.Abs(q.y - centre.y) > 0.6f) continue;
                    if (place != null && !place.Contains(q, -0.35f)) continue;
                    if (FruitNav.Room(q) < 0.12f) continue;                 // a doorway or a squeeze: keep it clear
                    if (NearDoor(q) || Taken(q, body, null, SpotStop)) continue;
                    if (!FruitNav.StraightClear(centre, q)) continue;   // keep the gathering on this side of walls/furniture
                    _ring.Add(q);
                }
                if (_ring.Count == 0) continue;
                // Nearest the walker of the ring: no crossing the room to stand on the far side.
                float best = float.MaxValue;
                foreach (var q in _ring)
                {
                    float d = FruitNav.Flat(q - from) + ArrivalObstruction(body, from, q);
                    if (d < best) { best = d; spot = q; }
                }
                return true;
            }
            return false;
        }

        private static bool NearDoor(Vector3 p)
        {
            foreach (var d in FruitPlaces.Doors)
                if (Math.Abs(d.Position.y - p.y) < 1.6f && FruitNav.Flat(d.Position - p) < 1.0f) return true;
            return false;
        }

        private static readonly FruitPath _probe = new FruitPath();

        /// <summary>For a goto order to a point: claims where it will stand, or a free spot if that
        /// is taken (smooth walking only).</summary>
        private static void Claim(Order o, Vector3 want)
        {
            o.Want = want;
            o.WantStop = o.StopDistance;
            o.Stand = want;
            o.Reslots = 0;
            o.ReslotAt = Time.time + ReslotEvery;
            if (!SpreadOut || !Smooth(o)) return;
            var place = SpreadPlace(want, out float radius);
            o.SpreadRadius = Math.Max(radius, o.StopDistance + 1f);
            o.SpreadPlace = place;
            o.Stand = StopPoint(o, want);
            if (!Taken(o.Stand, o.Body, null, SpotStop) && !NearDoor(o.Stand) && !NearDoor(want)) return;
            if (FreeSpot(o.Body, want, o.SpreadRadius, place, out Vector3 spot))
            {
                // Its own spot (perhaps the point itself, when only the way in was taken): stand on
                // it, not a metre short where the next one stands.
                o.Dest = o.Stand = spot;
                o.StopDistance = Math.Min(o.StopDistance, SpotStop);
            }
        }

        /// <summary>Where an order to <paramref name="want"/> will stop: <c>StopDistance</c> short of
        /// it along the way there (where the body already is, if that is close enough).</summary>
        private static Vector3 StopPoint(Order o, Vector3 want)
        {
            float stop = o.StopDistance;
            if (stop <= SpotStop + 0.05f || o.Body == null || !o.Body.HasPosition) return want;
            Vector3 from = LiveCentre(o.Body);
            if (FruitNav.Flat(want - from) <= stop) return from;
            if (FruitNav.Ready && FruitNav.FindPath(from, want, _probe, 3f, false, false) && _probe.Status == FruitPathStatus.Complete)
                return _probe.PointAt(Math.Max(0f, _probe.Length - stop));
            Vector3 back = from - want; back.y = 0f;
            return want + back.normalized * stop;
        }

        /// <summary>Every half second near the end of the walk: has someone taken this order's spot?
        /// Then another. False when there is none left and it is close enough to call it there.</summary>
        private static bool Reslot(Order o, float now)
        {
            if (!SpreadOut || !Smooth(o) || o.Target != null || o.Stance || now < o.ReslotAt) return true;
            o.ReslotAt = now + ReslotEvery;
            Vector3 me = LiveCentre(o.Body);
            float left = FruitNav.Flat(o.Stand - me);
            if (left > ReslotWithin || !TakenByStander(o.Stand, o.Body)) return true;
            if (o.Reslots >= MaxReslots || !FreeSpot(o.Body, o.Want, o.SpreadRadius, o.SpreadPlace, out Vector3 spot))
            {
                if (left < 2f) { o.Result = FruitMoveResult.Arrived; return false; }   // as close as the crowd allows
                return true;
            }
            o.Reslots++;
            if (o.Stats != null) o.Stats.Reslots++;
            o.Dest = o.Stand = spot;
            o.StopDistance = Math.Min(o.StopDistance, SpotStop);
            o.PathAt = -100f;   // find the way to the new one
            o.PassStage = 0;
            o.ProgressLeft = float.MaxValue;
            o.ProgressAt = now + ProgressWindow;
            FruitLog.Info($"[FruitBodyControl] #{o.Body.Id}'s spot was taken; going {FruitNav.Flat(spot - me):0.0} m to another.");
            return true;
        }

        /// <summary>A body stuck for good this close to its spot (someone's elbow, a chair leg) has
        /// arrived: backing off and coming round again only shoves the crowd about. Since 5.25.0;
        /// within a metre of its slot round a body since 5.26.0.</summary>
        private static bool CloseEnough(Order o, Vector3 at)
        {
            if (!SpreadOut || !Smooth(o)) return false;
            if (o.Target != null) return o.Slotted && FruitNav.Flat(o.Slot - at) < 1f;
            if (!o.HasDest) return false;
            if (o.Stance) return FruitNav.Flat(o.Stand - at) < 0.6f;   // the reach is checked at the item
            return FruitNav.Flat(o.Stand - at) < 1f || FruitNav.Flat(o.Want - at) <= o.WantStop;
        }

        /// <summary>On arrival: turns to the others standing about (see <see cref="FaceTheGroup"/>), or
        /// to the body it went up to (since 5.26.0).</summary>
        private static void FaceGroup(Order o)
        {
            if (!FaceTheGroup || !Smooth(o) || o.Stance) return;   // a stance faces its item, not the group
            var self = o.Body;
            if (!self.Alive || !self.HasPosition || !Upright(self)) return;
            Vector3 me = LiveCentre(self);
            if (o.Target != null)
            {
                if (o.Strikes || !o.Target.HasPosition) return;   // a blow has its own turn
                Vector3 at = LiveCentre(o.Target) - me;
                at.y = 0f;
                if (at.sqrMagnitude > 0.04f && Vector3.Angle(self.Facing, at) >= 30f) Face(self, at, 2f);
                return;
            }
            if (!SpreadOut || !o.HasDest) return;
            float sx = 0f, sz = 0f;
            int n = 0;
            var all = FruitBodies.All;
            for (int i = 0; i < all.Count; i++)
            {
                var b = all[i];
                if (b == self || !b.HasPosition || !b.Alive || Math.Abs(b.Position.y - me.y) > 1.6f) continue;
                float dx = b.Position.x - me.x, dz = b.Position.z - me.z;
                if (dx * dx + dz * dz > GroupRadius * GroupRadius) continue;
                // Those standing there, or about to: not someone walking past.
                if (OnItsWay(b) && !(_orders.TryGetValue(b.Id, out var bo) && bo.HasDest && FruitNav.Flat(bo.Stand - b.Position) < 1.5f)) continue;
                sx += dx; sz += dz; n++;
            }
            Vector3 to;
            if (n > 0) to = new Vector3(sx / n, 0f, sz / n);
            else if (FruitNav.Flat(o.Want - me) > 0.6f) to = o.Want - me;
            else return;
            to.y = 0f;
            if (to.sqrMagnitude < 0.04f) return;
            if (Vector3.Angle(self.Facing, to) < 30f) return;   // near enough facing them already
            Face(self, to, 2f);
        }

        // ── Slots round a body ──
        //
        // Approach and Follow aim at the other body's centre and stop StopDistance short on the way
        // in, so three bodies sent to the same one stop on the same side of it, on top of each
        // other. Since 5.26.0, within SlotWithin of that ring an order to a body (not an attack: a
        // blow has to get within Reach) takes a slot on it: its own bearing from the target first,
        // then SlotStep either way at a time up to SlotSweep, the first an arm's length
        // (PersonalSpace) from anyone standing there and from every other order's slot or spot, on
        // the navmesh and not behind a wall from the target. A slot is a bearing, so it moves with
        // the target. Every half second the body checks that its slot is still free and still
        // within SlotSweep of its own bearing, else it picks again. It walks onto the slot
        // (SpotStop), or stops sooner where it is within StopDistance of the target with nobody
        // that close. With the ring full it stops in the second rank, PersonalSpace further out.
        // On arrival it turns to the target; a follower standing by keeps its slot and keeps
        // turning to it.

        /// <summary>Approach and Follow take a free slot round the body instead of all stopping on its
        /// near side (with <see cref="SpreadOut"/>, smooth walking only). Since 5.26.0.</summary>
        public static bool TargetSlots = true;
        /// <summary>Metres beyond the stop distance within which a slot is taken; degrees between the
        /// slots tried, and the most either way of the walker's own bearing. Since 5.26.0.</summary>
        public static float SlotWithin = 2.5f, SlotStep = 20f, SlotSweep = 120f;

        /// <summary>Whether an order to a body walks to a slot round it.</summary>
        private static bool UsesSlot(Order o) => TargetSlots && SpreadOut && o.Target != null && !o.Strikes && Smooth(o);

        private static Vector3 SlotPoint(Order o, Vector3 centre)
        {
            centre.x += MathF.Cos(o.SlotAngle) * o.StopDistance;
            centre.z += MathF.Sin(o.SlotAngle) * o.StopDistance;
            return centre;
        }

        /// <summary>Steers an order to a body by its slot (see the notes above). False when it has
        /// arrived (approach).</summary>
        private static bool SteerToSlot(Order o, Vector3 centre, Vector3 me, float d, float now)
        {
            if (o.Paused)
            {
                // Standing by (follow): the slot stays its own until the target moves off.
                if (o.Slotted) o.Slot = SlotPoint(o, centre);
                if (d <= Math.Max(o.HoldDistance, o.StopDistance) + 0.5f) return true;
                o.Paused = false;
                o.Watching = false;
                o.SlotAt = 0f;   // and it is looked at again on the way
            }

            float reach = o.StopDistance + SlotWithin;
            if (d > reach + 1f) o.Slotted = false;   // well out again: let the slot go
            else if (d <= reach && now >= o.SlotAt) PickSlot(o, centre, me, now);

            if (!o.Slotted)
            {
                // Not near enough yet, or the ring is full and the second rank will do.
                if (d <= (d <= reach ? o.StopDistance + PersonalSpace : o.StopDistance)) return Arrive(o);
                Vector3 to = centre - me; to.y = 0f;
                o.Dir = to / d;
                return true;
            }

            Vector3 slot = o.Slot = SlotPoint(o, centre);
            Vector3 way = slot - me; way.y = 0f;
            float left = way.magnitude;
            if (left <= SpotStop || (d <= o.StopDistance && !Taken(me, o.Body, o.Target))) return Arrive(o);
            o.Dir = way / left;
            return true;
        }

        /// <summary>Keeps an order's slot while it is free and on the walker's side, else takes the
        /// first free one from the walker's own bearing out (or none).</summary>
        private static void PickSlot(Order o, Vector3 centre, Vector3 me, float now)
        {
            o.SlotAt = now + ReslotEvery;
            Vector3 floor = centre;
            bool nav = FruitNav.Ready && FruitNav.Sample(centre, 1.5f, out floor);
            float own = MathF.Atan2(me.z - centre.z, me.x - centre.x);
            if (o.Slotted && Math.Abs(Mathf.DeltaAngle(own * Mathf.Rad2Deg, o.SlotAngle * Mathf.Rad2Deg)) <= SlotSweep + 1f
                && SlotFree(o, centre, nav, floor)) return;

            bool had = o.Slotted;
            float was = o.SlotAngle;
            float stepDeg = Math.Max(5f, SlotStep);
            int n = (int)(Math.Max(0f, SlotSweep) / stepDeg + 0.01f);
            o.Slotted = false;
            for (int k = 0; k <= 2 * n; k++)
            {
                int i = (k + 1) / 2;
                o.SlotAngle = own + ((k & 1) == 1 ? i : -i) * stepDeg * Mathf.Deg2Rad;   // its own bearing, then either way
                if (SlotFree(o, centre, nav, floor)) { o.Slotted = true; break; }
            }
            if (!o.Slotted) { o.SlotAngle = was; return; }
            o.Slot = SlotPoint(o, centre);
            if (had && Math.Abs(Mathf.DeltaAngle(was * Mathf.Rad2Deg, o.SlotAngle * Mathf.Rad2Deg)) < 1f) return;
            o.PathAt = -100f;   // find the way to it
            if (had)
            {
                o.Reslots++;
                if (o.Stats != null) o.Stats.Reslots++;
            }
            FruitLog.Info($"[FruitBodyControl] #{o.Body.Id} takes a slot round #{o.Target.Id} " +
                          $"{Mathf.DeltaAngle(own * Mathf.Rad2Deg, o.SlotAngle * Mathf.Rad2Deg):0} degrees off its own side{(had ? " instead of its last (taken, or round the far side)" : "")}.");
        }

        /// <summary>Whether the slot at an order's current SlotAngle is free: nobody standing or
        /// claimed within PersonalSpace (the target left out), and, on a navmesh, a place to stand
        /// that the target could walk to straight.</summary>
        private static bool SlotFree(Order o, Vector3 centre, bool nav, Vector3 floor)
        {
            if (Taken(SlotPoint(o, centre), o.Body, o.Target)) return false;
            if (!nav) return true;
            Vector3 p = SlotPoint(o, floor);   // at the target's feet, where the navmesh is
            if (!FruitNav.Sample(p, 0.6f, out Vector3 q) || FruitNav.Flat(q - p) > 0.4f) return false;   // in a wall, off a ledge
            return FruitNav.StraightClear(floor, q);   // not the other side of a wall or a car
        }

        /// <summary>At its slot (or as close as it gets): an approach is done, a follow stands by.</summary>
        private static bool Arrive(Order o)
        {
            if (!o.Keep) { o.Result = FruitMoveResult.Arrived; return false; }
            Hold(o);
            return true;
        }

        /// <summary>A follow standing by, until the target is half a metre further than it is now.</summary>
        private static void Hold(Order o)
        {
            o.Paused = true;
            o.HoldDistance = o.Target != null && o.Target.HasPosition ? FruitNav.Flat(LiveCentre(o.Target) - LiveCentre(o.Body)) : 0f;
        }

        /// <summary>A follower standing by keeps turning to the body it follows, once it is more than
        /// 30 degrees off, as a pivot turns (smooth walking only). Since 5.26.0.</summary>
        private static void WatchTarget(Order o, float dt)
        {
            if (!FaceTheGroup || !Smooth(o) || o.Strikes || o.Target == null || !o.Target.HasPosition) return;
            Vector3 to = LiveCentre(o.Target) - LiveCentre(o.Body);
            if (to.x * to.x + to.z * to.z < 0.04f) return;
            var rt = o.Runtime;
            float desired = MathF.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            float err = Math.Abs(Mathf.DeltaAngle(rt.CreatureYRotation, desired));
            if (!o.Watching) { if (err < 30f) return; o.Watching = true; }
            else if (err < 8f) { o.Watching = false; return; }
            rt.UpdateTargetYRot(Mathf.MoveTowardsAngle(rt.PuppeteerTargetYRot, desired, TurnRate * dt));
        }

        /// <summary>Taken by someone standing or lying there (not by another order's claim: claims never overlap).</summary>
        private static bool TakenByStander(Vector3 point, FruitBody except)
        {
            var all = FruitBodies.All;
            for (int i = 0; i < all.Count; i++)
            {
                var b = all[i];
                if (b == except || !b.HasPosition || Math.Abs(b.Position.y - point.y) > 1.6f) continue;
                bool down = !Upright(b);
                if (!down && OnItsWay(b)) continue;
                float space = (down ? LyingSpace : PersonalSpace) * 0.8f;
                float dx = b.Position.x - point.x, dz = b.Position.z - point.z;
                if (dx * dx + dz * dz < space * space) return true;
            }
            return false;
        }
    }
}
