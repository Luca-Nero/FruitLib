using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>A body an event happened near: its <see cref="FruitBody.Id"/> and how far away.</summary>
    public struct NearBody
    {
        public int   Body;
        public float Distance;
    }

    /// <summary>
    /// One thing that happened in the world, from the game (a pain spike, a fall, a death),
    /// from FruitLib (an explosion, a wound) or from a mod (a grenade landing). Read-only once
    /// it is raised.
    /// </summary>
    public sealed class FruitEvent
    {
        /// <summary>Order of raising, from 1. Use it to read only what is new.</summary>
        public long    Seq;
        /// <summary><c>Time.time</c> when it was published.</summary>
        public float   Time;
        /// <summary>"Game", "FruitLib", or the publishing mod's name.</summary>
        public string  Source;
        /// <summary>What kind of thing: "Explosion", "PainSpike", "Landed"... One word, PascalCase.</summary>
        public string  Kind;
        /// <summary>One readable line, as it would go in a log.</summary>
        public string  Text;
        public bool    HasPosition;
        public Vector3 Position;
        /// <summary>How far from <see cref="Position"/> a body still counts as near it, metres.
        /// 0 = <see cref="FruitEvents.DefaultRadius"/>.</summary>
        public float   Radius;
        /// <summary>The body it happened to (<see cref="FruitBody.Id"/>), 0 = none in particular.</summary>
        public int     Body;
        /// <summary>The limb it happened to (<see cref="FruitLimb.Index"/>), -1 = none.</summary>
        public int     Limb = -1;
        /// <summary>Rough salience, 0..1: how hard it is to ignore.</summary>
        public float   Magnitude;
        /// <summary>Anything else, free text.</summary>
        public string  Detail;
        /// <summary>Who caused it: "player" for anything the player did (shot, threw, called in),
        /// "body#3" for a tracked body (a punch, since 5.14.0), empty when nobody did (a body
        /// falling over). Since 5.13.0.</summary>
        public string  Actor = "";
        /// <summary>With what, in plain words: "Glock 17", "frag grenade". Since 5.13.0.</summary>
        public string  Cause = "";
        /// <summary>Which way it went, for things with a line: a gunshot's muzzle direction. Since 5.13.1.</summary>
        public bool    HasDirection;
        public Vector3 Direction;
        /// <summary>Tracked bodies within <see cref="Radius"/> of <see cref="Position"/>, nearest
        /// first, filled in as the event is raised. Empty while body tracking is off.</summary>
        public readonly List<NearBody> Near = new List<NearBody>(4);

        /// <summary>The body that did it, when <see cref="Actor"/> is "body#N"; 0 otherwise. Since 5.14.0.</summary>
        public int ActorBody
            => Actor != null && Actor.StartsWith(FruitEvents.BodyActorPrefix, StringComparison.Ordinal)
               && int.TryParse(Actor.Substring(FruitEvents.BodyActorPrefix.Length), out int id) ? id : 0;

        /// <summary>Whether this concerns a body: it happened to it, or near it.</summary>
        public bool Concerns(int body, out float distance)
        {
            if (Body == body && body != 0) { distance = 0f; return true; }
            for (int i = 0; i < Near.Count; i++)
                if (Near[i].Body == body) { distance = Near[i].Distance; return true; }
            distance = -1f;
            return false;
        }

        public override string ToString()
        {
            string where = HasPosition ? $" @({Position.x:0.0},{Position.y:0.0},{Position.z:0.0})" : "";
            string who   = (Body != 0 ? $" body#{Body}" : "") + (Actor.Length > 0 ? $" by:{Actor}" : "") + (Cause.Length > 0 ? $" with:{Cause}" : "");
            string near  = "";
            if (Near.Count > 0)
            {
                var sb = new System.Text.StringBuilder(" near");
                for (int i = 0; i < Near.Count && i < 4; i++) sb.Append($" #{Near[i].Body}:{Near[i].Distance:0.0}m");
                if (Near.Count > 4) sb.Append($" +{Near.Count - 4}");
                near = sb.ToString();
            }
            return $"[{Time,8:0.00}] {Source}.{Kind}{who}{where}{near} - {Text}";
        }
    }

    /// <summary>
    /// The world's event bus: what happened, where, and which bodies it happened near.
    ///
    /// <b>Listening:</b>
    /// <code>FruitEvents.Raised += e => { if (e.Kind == "Explosion") ... };</code>
    /// Handlers run on the main thread in <c>OnUpdate</c>, a frame or less after publishing, so
    /// an event raised inside the game's physics is never handled inside it.
    ///
    /// <b>Publishing</b> (any mod; this is how "a grenade landed 2 m from body #3" exists):
    /// <code>FruitEvents.Publish("MyMod", "Landed", "frag grenade landed", position, radius: 8f, magnitude: 0.7f);</code>
    /// Give a position and every tracked body within the radius is listed in
    /// <see cref="FruitEvent.Near"/> - publishers don't need to know where anyone is.
    ///
    /// What FruitLib raises itself is listed in docs/events.md.
    /// </summary>
    /// <remarks>Since 5.12.0.</remarks>
    public static class FruitEvents
    {
        /// <summary>Raised once per event, main thread. A handler that throws is reported once and keeps receiving.</summary>
        public static event Action<FruitEvent> Raised;

        /// <summary>Radius used when a publisher gives none, metres.</summary>
        public const float DefaultRadius = 15f;

        internal const string BodyActorPrefix = "body#";

        /// <summary>The <see cref="FruitEvent.Actor"/> for something a tracked body did: "body#3".
        /// Read it back with <see cref="FruitEvent.ActorBody"/>. Since 5.14.0.</summary>
        public static string BodyActor(int bodyId) => BodyActorPrefix + bodyId;

        private const int RingSize = 256;
        private static readonly FruitEvent[] _ring = new FruitEvent[RingSize];
        private static long _seq;
        private static readonly object _gate = new object();
        private static readonly List<FruitEvent> _pending = new List<FruitEvent>(32);
        private static readonly List<FruitEvent> _dispatch = new List<FruitEvent>(32);
        private static readonly HashSet<string> _reported = new HashSet<string>();

        /// <summary>The Seq of the newest event raised so far.</summary>
        public static long LastSeq => _seq;

        /// <summary>Events published and raised since start.</summary>
        public static long Count => _seq;

        /// <summary>
        /// Queues an event; it is raised next <c>OnUpdate</c>. Main thread only: Unity's clock
        /// is read here. Returns the event so a publisher can keep it.
        /// </summary>
        public static FruitEvent Publish(string source, string kind, string text,
                                         Vector3? position = null, float radius = 0f, int body = 0,
                                         float magnitude = 0f, int limb = -1, string detail = null,
                                         string actor = "", string cause = "", Vector3? direction = null)
        {
            var e = new FruitEvent
            {
                Source = source ?? "?", Kind = kind ?? "?", Text = text ?? "",
                HasPosition = position.HasValue, Position = position ?? default,
                Radius = radius, Body = body, Limb = limb,
                Magnitude = Mathf.Clamp01(magnitude), Detail = detail,
                Actor = actor ?? "", Cause = cause ?? "",
                HasDirection = direction.HasValue, Direction = direction ?? default,
            };
            return Publish(e);
        }

        /// <summary>Queues a filled-in event; see the other overload.</summary>
        public static FruitEvent Publish(FruitEvent e)
        {
            if (e == null) return null;
            e.Time = UnityEngine.Time.time;
            lock (_gate) _pending.Add(e);
            return e;
        }

        /// <summary>Appends the raised events newer than <paramref name="afterSeq"/> (oldest first)
        /// to <paramref name="into"/>; only the last 256 are kept.</summary>
        public static void CopySince(long afterSeq, List<FruitEvent> into)
        {
            if (into == null) return;
            long first = Math.Max(afterSeq + 1, _seq - RingSize + 1);
            for (long s = Math.Max(first, 1); s <= _seq; s++)
            {
                var e = _ring[(int)(s % RingSize)];
                if (e != null && e.Seq == s) into.Add(e);
            }
        }

        /// <summary>The newest <paramref name="max"/> events, oldest first. Allocates: for panels and tools.</summary>
        public static List<FruitEvent> Recent(int max = 32)
        {
            var list = new List<FruitEvent>(max);
            CopySince(Math.Max(0, _seq - max), list);
            return list;
        }

        internal static void Tick()
        {
            lock (_gate)
            {
                if (_pending.Count == 0) return;
                _dispatch.AddRange(_pending);
                _pending.Clear();
            }

            try
            {
                for (int i = 0; i < _dispatch.Count; i++)
                {
                    var e = _dispatch[i];
                    TagNear(e);
                    e.Seq = ++_seq;
                    _ring[(int)(e.Seq % RingSize)] = e;
                    FruitEventLog.Write(e);
                    Deliver(e);
                }
            }
            finally { _dispatch.Clear(); }
        }

        private static void Deliver(FruitEvent e)
        {
            var handlers = Raised;
            if (handlers == null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<FruitEvent>)d).Invoke(e); }
                catch (Exception ex)
                {
                    string who = d.Method.DeclaringType?.FullName + "." + d.Method.Name;
                    if (_reported.Add(who))
                        MelonLogger.Warning($"[FruitEvents] a handler threw ({who}); reported once, it keeps receiving: {ex}");
                }
            }
        }

        private static void TagNear(FruitEvent e)
        {
            if (!e.HasPosition || !FruitBodies.Active) return;
            float r = e.Radius > 0f ? e.Radius : DefaultRadius;
            var bodies = FruitBodies.All;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (!b.HasPosition) continue;
                float d = Mathf.Min(Vector3.Distance(b.Position, e.Position), Vector3.Distance(b.HeadPosition, e.Position));
                if (d > r) continue;
                e.Near.Add(new NearBody { Body = b.Id, Distance = d });
            }
            if (e.Near.Count > 1) e.Near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }

        /// <summary>A map reset or new scene: pending events are about a world that is gone.</summary>
        internal static void ResetForScene()
        {
            lock (_gate) _pending.Clear();
        }
    }
}
