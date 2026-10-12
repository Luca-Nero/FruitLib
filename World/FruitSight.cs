using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>Where a point lies relative to a body's gaze.</summary>
    public enum FruitSightZone
    {
        /// <summary>Within <see cref="FruitSight.InViewAngle"/> of the gaze.</summary>
        InView,
        /// <summary>Out of the corner of the eye: up to <see cref="FruitSight.CornerAngle"/>.</summary>
        Corner,
        /// <summary>Not seen without turning.</summary>
        Behind,
    }

    /// <summary>What a GameObject is called, as a person would say it.</summary>
    public struct FruitThingName
    {
        /// <summary>The part that was hit: "concrete wall", "glass pane", "steel barrel".</summary>
        public string Part;
        /// <summary>The whole thing the part belongs to: "concrete apartment block", "streetlight".
        /// The same as <see cref="Part"/> for a thing that is one piece.</summary>
        public string Object;
        /// <summary>Which storey the part is on (0 = ground floor), or -1 if its name doesn't say.</summary>
        public int Storey;
        /// <summary>Glass and windows: seen through, not blocking.</summary>
        public bool SeeThrough;
        /// <summary>Something walked on (road, floor, grass) rather than looked at.</summary>
        public bool Ground;
    }

    /// <summary>One thing a body sees, or would see if it turned.</summary>
    public sealed class FruitSeen
    {
        /// <summary>"body", "player", "thing" (loose, has a rigidbody) or "structure".</summary>
        public string Kind = "";
        /// <summary>What it is ("steel barrel", "brick townhouse"); empty for a body and the player.</summary>
        public string Name = "";
        /// <summary>For a body.</summary>
        public FruitBody Body;
        /// <summary>Stable across looks: "body:4", "player", "thing:&lt;pointer&gt;", "structure:brick townhouse".</summary>
        public string Key = "";
        public Vector3 Position;
        /// <summary>Metres from the eyes.</summary>
        public float Distance;
        /// <summary>Degrees off the gaze.</summary>
        public float Angle;
        public FruitSightZone Zone;
        /// <summary>In view or in the corner of the eye, with nothing solid between.</summary>
        public bool Visible;
        /// <summary>Line of sight is clear, whatever the angle (it would be seen on turning).</summary>
        public bool Clear;
        public float Mass;
        public Vector3 Size;
        public bool Moving;
        /// <summary>Within <see cref="FruitSight.CloseRange"/> of the body, in any direction, seen or
        /// not: what lies at its feet or behind its back is known without looking.</summary>
        public bool Close;
        /// <summary>For a loose thing: the item it is, if <see cref="FruitItems"/> knows it (a gun
        /// is "Lynx F rifle" whatever its object is called). Null for a body or a structure.</summary>
        public FruitWorldItem Item;
        /// <summary>For a close loose thing, what it rests on ("wooden shop counter"), or empty for
        /// the floor or ground, or when nothing is under it.</summary>
        public string On = "";
        /// <summary>Metres above the floor under the body (a full look only).</summary>
        public float AboveFloor;
    }

    /// <summary>One look from one body's eyes: the place, and what is in it.</summary>
    public sealed class FruitView
    {
        public FruitBody Body;
        public float Time;
        public Vector3 Eye, Forward, Up;
        /// <summary>Flat forward for bearings: the gaze, or the pelvis facing when looking straight up or down.</summary>
        public Vector3 Flat;
        /// <summary>The map, in a line: <see cref="FruitSight.Setting"/>.</summary>
        public string Setting = "";
        /// <summary>Only for a full look: a ceiling above and walls on most sides.</summary>
        public bool Indoors;
        /// <summary>The thing it is inside ("concrete apartment block"), or empty.</summary>
        public string Inside = "";
        public int Storey = -1;
        /// <summary>Metres from the eyes to what is overhead, or -1 for open sky.</summary>
        public float Ceiling = -1f;
        /// <summary>What it stands (or lies) on: "asphalt road".</summary>
        public string Ground = "";
        /// <summary>Height of the floor under the body, metres (a full look only).</summary>
        public float FloorY = float.NaN;
        /// <summary>Of 12 level bearings, how many are blocked within 8 m, and which way is open
        /// ("ahead", "to your left", "to your right", "behind you").</summary>
        public int Walls;
        public readonly List<string> Open = new List<string>(4);
        public readonly List<string> Closed = new List<string>(4);
        /// <summary>Everything considered, visible first, then nearest first.</summary>
        public readonly List<FruitSeen> Seen = new List<FruitSeen>(24);
        /// <summary>Whether this was a full look (layout and structures) or a glance (bodies, player, loose things).</summary>
        public bool Full;

        /// <summary>"3.2 m ahead to your left" from these eyes, by the flat gaze.</summary>
        public string Bearing(Vector3 point)
        {
            Vector3 d = point - Eye;
            float dist = d.magnitude;
            Vector3 flat = new Vector3(d.x, 0f, d.z);
            if (flat.sqrMagnitude < 0.04f) return dist < 0.3f ? "right on you" : d.y > 0 ? $"{dist:0.0} m above you" : $"{dist:0.0} m below you";
            float ang = Vector3.SignedAngle(Flat, flat, Vector3.up);
            return $"{dist:0.0} m {FruitSight.Side(ang)}";
        }
    }

    /// <summary>
    /// What a body can see: its eyes (from the head rigidbody), line of sight (glass doesn't
    /// block it), what things are called, and a whole look around (<see cref="Look"/>): the place
    /// (indoors or out, what it stands on, which ways are open) and the bodies, the player and the
    /// things in it, with what is in view and what is behind. For language-model minds and for
    /// anything else that asks "can this body see that?". See docs/sight.md.
    ///
    /// Names come from the scene. A map following the <c>Material_Thing</c> convention
    /// (FruitCity's <c>Brick_Townhouse</c>, <c>Steel_Barrel</c>, <c>Concrete_Wall_F1_S2</c>) is named without
    /// help; anything else gets its cleaned GameObject name, or whatever a mod's
    /// <see cref="Describers"/> say.
    /// </summary>
    /// <remarks>Since 5.18.0. Measured on the 2026-10-10 sight test: a full look is 85 rays, 1–1.5 ms.</remarks>
    public static class FruitSight
    {
        /// <summary>Degrees off the gaze that count as in view, and as the corner of the eye.</summary>
        public static float InViewAngle = 60f, CornerAngle = 100f;
        /// <summary>How far a look reaches, metres.</summary>
        public static float Range = 30f;
        /// <summary>How far from the body something counts as <see cref="FruitSeen.Close"/>
        /// (metres): within a few steps, whichever way it is facing. Since 5.35.0.</summary>
        public static float CloseRange = 4f;

        /// <summary>Asked first, newest first; return null to pass. Lets a mod name its own things
        /// ("a live grenade") where the GameObject name would not.</summary>
        public static readonly List<Func<GameObject, FruitThingName?>> Describers = new List<Func<GameObject, FruitThingName?>>();

        /// <summary>A mod's own line for the current place; overrides the map's (cleared on scene load).</summary>
        public static string SettingOverride;

        // ── The setting ──

        /// <summary>
        /// The place in a line, for a prompt: a mod's override, else the custom map's
        /// <see cref="FruitMapDef.Setting"/> (or the first sentence of its blurb), else a line for the
        /// game's own map.
        /// </summary>
        public static string Setting
        {
            get
            {
                if (!string.IsNullOrEmpty(SettingOverride)) return SettingOverride;
                var map = FruitMaps.Current;
                if (map != null)
                {
                    if (!string.IsNullOrEmpty(map.Setting)) return map.Setting;
                    if (!string.IsNullOrEmpty(map.Blurb))
                    {
                        int dot = map.Blurb.IndexOf(". ", StringComparison.Ordinal);
                        return (dot > 0 ? map.Blurb.Substring(0, dot) : map.Blurb.TrimEnd('.')).Trim();
                    }
                }
                string scene = "";
                try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
                switch (scene)
                {
                    case "Flatland": return "the small flat top of a huge grassy hill, with a little house on it and open sky all around";
                    default:         return "a strange, empty test area";
                }
            }
        }

        // ── Eyes ──

        // The head rigidbody's axes on the Release rig (sight test 2026-10-10: +Z forward 1.00, +Y up 1.00).
        private static readonly Vector3 HeadForward = Vector3.forward, HeadUp = Vector3.up;

        /// <summary>Where a body's eyes are and which way they look: from the head rigidbody, so a
        /// body on its back looks at the sky. Without a head, from the pelvis facing.</summary>
        public static void Eye(FruitBody b, out Vector3 eye, out Vector3 forward, out Vector3 up)
        {
            Rigidbody rb = null;
            var h = b.Limbs[FruitBodies.Head];
            if (h.Present && h.Physics != null && !h.Physics.WasCollected) { try { rb = h.Physics.m_rb; } catch { } }
            Vector3 head = b.LiveHeadPosition;
            if (rb != null)
            {
                Quaternion q = rb.rotation;
                forward = (q * HeadForward).normalized;
                up = (q * HeadUp).normalized;
            }
            else
            {
                forward = b.Facing.sqrMagnitude > 0.01f ? b.Facing.normalized : Vector3.forward;
                up = Vector3.up;
            }
            eye = head + forward * 0.11f + up * 0.04f;
        }

        public static FruitSightZone ZoneOf(float angle)
            => angle <= InViewAngle ? FruitSightZone.InView : angle <= CornerAngle ? FruitSightZone.Corner : FruitSightZone.Behind;

        /// <summary>Whether a body could see a point now: within the corner of its eye, and nothing
        /// solid between. <paramref name="target"/>'s own colliders don't block.</summary>
        public static bool CanSee(FruitBody b, Vector3 point, FruitBody target = null)
        {
            if (b == null || !b.HasPosition) return false;
            Eye(b, out Vector3 eye, out Vector3 fwd, out _);
            if (Vector3.Angle(fwd, point - eye) > CornerAngle) return false;
            return Clear(eye, point, b, target, null);
        }

        /// <summary>Nothing solid between two points: <paramref name="self"/>'s body, the
        /// <paramref name="target"/> body or rigidbody, glass and triggers don't count.</summary>
        public static bool Clear(Vector3 from, Vector3 to, FruitBody self, FruitBody target = null, Rigidbody targetRb = null)
        {
            Vector3 d = to - from;
            float dist = d.magnitude;
            if (dist < 0.05f) return true;
            Il2CppStructArray<RaycastHit> hits;
            try { hits = Physics.RaycastAll(from, d / dist, dist, ~0, QueryTriggerInteraction.Ignore); }
            catch { return true; }
            for (int i = 0; i < hits.Length; i++)
            {
                var c = hits[i].collider;
                if (c == null || hits[i].distance >= dist - 0.08f) continue;
                if (targetRb != null) { try { if (c.attachedRigidbody == targetRb) continue; } catch { } }
                if (IsBodyCollider(c, self) || (target != null && IsBodyCollider(c, target))) continue;
                if (Name(c.gameObject).SeeThrough) continue;
                return false;
            }
            return true;
        }

        // Body colliders, by GameObject pointer, rebuilt as bodies come and go.
        private static readonly Dictionary<IntPtr, int> _bodyGos = new Dictionary<IntPtr, int>();
        private static int _bodyGosFrame = -1;

        private static void RefreshBodyGos()
        {
            if (_bodyGosFrame == Time.frameCount) return;
            _bodyGosFrame = Time.frameCount;
            _bodyGos.Clear();
            foreach (var b in FruitBodies.All)
                foreach (var l in b.Limbs)
                {
                    if (!l.Present) continue;
                    if (l.GoPtr != IntPtr.Zero) _bodyGos[l.GoPtr] = b.Id;
                    if (l.PhysGoPtr != IntPtr.Zero) _bodyGos[l.PhysGoPtr] = b.Id;
                }
        }

        /// <summary>The body a collider belongs to (0 for none): the limb objects first, then up to
        /// three parents, then FruitBodies' own search.</summary>
        public static int BodyOf(Collider c)
        {
            if (c == null) return 0;
            RefreshBodyGos();
            var t = c.transform;
            for (int i = 0; t != null && i < 4; i++, t = t.parent)
                if (_bodyGos.TryGetValue(t.gameObject.Pointer, out int id)) return id;
            if (c.gameObject.layer != 7) return 0;   // the puppet layer; everything else is not a body
            var b = FruitBodies.OfObject(c.gameObject, out _);
            return b?.Id ?? 0;
        }

        private static bool IsBodyCollider(Collider c, FruitBody b) => b != null && BodyOf(c) == b.Id;

        // ── Names ──

        private static readonly Dictionary<IntPtr, FruitThingName> _names = new Dictionary<IntPtr, FruitThingName>();
        private static readonly Regex MaterialThing = new Regex("^[A-Z][a-z]+_[A-Z]", RegexOptions.Compiled);
        private static readonly Regex StoreyTag = new Regex("_F(\\d+)(_|#|$)", RegexOptions.Compiled);
        private static readonly HashSet<string> Directions = new HashSet<string> { "n", "s", "e", "w", "ne", "nw", "se", "sw", "x", "y", "z" };
        private static readonly string[] GroundWords = { "road", "sidewalk", "pavement", "pavers", "floor", "slab", "ground", "terrain", "grass", "field", "plane", "street", "crossing", "lot", "parking", "path", "kerb", "curb" };
        private static readonly Dictionary<string, string> Adjectives = new Dictionary<string, string>
            { ["wood"] = "wooden", ["metal"] = "metal", ["steel"] = "steel", ["concrete"] = "concrete", ["brick"] = "brick", ["glass"] = "glass", ["asphalt"] = "asphalt", ["plastic"] = "plastic", ["stone"] = "stone" };
        // Names that say nothing: a part called "Physics" is not a thing anyone would point at.
        private static readonly HashSet<string> Meaningless = new HashSet<string> { "physics", "collider", "colliders", "root", "object", "mesh", "model", "trigger", "something", "body", "pivot", "holder", "container",
              "props", "environment", "level", "world", "scene", "objects", "spawned", "items" };
        private static readonly HashSet<string> _renamed = new HashSet<string>();

        // Things whose material is not worth saying ("a steel car").
        private static readonly HashSet<string> Plain = new HashSet<string> { "car", "streetlight", "pole", "road", "sidewalk", "bus shelter", "fence body",
            "sofa", "bed", "fridge", "drinks fridge", "stove", "bathtub", "washbasin", "toilet", "washing machine", "tv", "safe", "mailboxes", "bins" };
        private static readonly Dictionary<string, string> Nicer = new Dictionary<string, string>
            { ["concrete apartment"] = "concrete apartment block", ["concrete shop"] = "concrete shop with big windows", ["wooden fence body"] = "wooden fence" };

        /// <summary>What a GameObject is called (cached per object).</summary>
        public static FruitThingName Name(GameObject go)
        {
            if (go == null) return new FruitThingName { Part = "something", Object = "something", Storey = -1 };
            IntPtr key = go.Pointer;
            if (_names.TryGetValue(key, out var n)) return n;

            for (int i = Describers.Count - 1; i >= 0; i--)
            {
                try { var d = Describers[i](go); if (d.HasValue) { _names[key] = d.Value; return d.Value; } }
                catch { }
            }

            string own = go.name ?? "";
            n.Storey = -1;
            n.Part = Clean(own);
            if (MaterialThing.IsMatch(own))
            {
                // Climb while the parent is named the same way: Concrete_Wall_F0 < Concrete_Apartment < TestBlock.
                var t = go.transform;
                while (t.parent != null && MaterialThing.IsMatch(t.parent.name ?? "")) t = t.parent;
                n.Object = Clean(t.name);
                for (var s = go.transform; s != null; s = s.parent)
                {
                    string sn = s.name ?? "";
                    var m = StoreyTag.Match(sn);
                    if (m.Success) { n.Storey = int.Parse(m.Groups[1].Value); break; }
                    if (sn.IndexOf("GroundSlab", StringComparison.Ordinal) >= 0) { n.Storey = 0; break; }
                    if (s == t) break;
                }
            }
            else n.Object = n.Part;

            // A part called "Physics" or "Collider" says nothing; the thing it belongs to does.
            if (Meaningless.Contains(n.Object))
            {
                var up = go.transform.parent;
                for (int i = 0; up != null && i < 5; i++, up = up.parent)
                {
                    string c = Clean(up.name ?? "");
                    if (c.Length == 0 || Meaningless.Contains(c)) continue;
                    if (_renamed.Add(own)) FruitLog.Info($"[FruitSight] \"{own}\" says nothing; calling it \"{c}\", after its parent.");
                    if (Meaningless.Contains(n.Part)) n.Part = c;
                    n.Object = c;
                    break;
                }
            }

            string lower = own.ToLowerInvariant();
            string mat = "";
            try { var r = go.GetComponent<Renderer>(); if (r != null && r.sharedMaterial != null) mat = r.sharedMaterial.name.ToLowerInvariant(); } catch { }
            n.SeeThrough = lower.Contains("glass") || lower.Contains("window") || mat.Contains("glass");
            foreach (var w in GroundWords) if (n.Part.Contains(w)) { n.Ground = true; break; }
            if (Nicer.TryGetValue(n.Object, out var nicer)) n.Object = nicer;
            if (Nicer.TryGetValue(n.Part, out nicer)) n.Part = nicer;
            _names[key] = n;
            return n;
        }

        /// <summary>"Concrete_Wall_F0_S3#21x6#..." → "concrete wall"; "BusShelter" → "bus shelter";
        /// "Barrel(Clone)" → "barrel". Material words become adjectives ("wooden crate") and are
        /// dropped where they say nothing ("car").</summary>
        public static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "something";
            int cut = raw.IndexOfAny(new[] { '#', '(' });
            if (cut >= 0) raw = raw.Substring(0, cut);
            var words = new List<string>(4);
            foreach (var token in raw.Split('_', ' ', '.', '-'))
            {
                if (token.Length == 0) continue;
                if (token.Length >= 2 && (token[0] == 'F' || token[0] == 'S') && char.IsDigit(token[1])) continue;
                // CamelCase apart: BusShelter → Bus, Shelter; RoadZ → Road, Z.
                var sb = new StringBuilder();
                for (int i = 0; i < token.Length; i++)
                {
                    char c = token[i];
                    if (i > 0 && char.IsUpper(c) && !char.IsUpper(token[i - 1])) { Add(words, sb.ToString()); sb.Clear(); }
                    sb.Append(c);
                }
                Add(words, sb.ToString());
            }
            if (words.Count == 0) return "something";
            if (words.Count > 1 && Adjectives.TryGetValue(words[0], out var adj))
            {
                string rest = string.Join(" ", words.GetRange(1, words.Count - 1));
                return Plain.Contains(rest) ? rest : adj + " " + rest;
            }
            return string.Join(" ", words);
        }

        private static void Add(List<string> words, string w)
        {
            w = w.ToLowerInvariant();
            if (w.Length == 0 || Directions.Contains(w)) return;
            bool digits = true;
            foreach (char c in w) if (!char.IsDigit(c)) { digits = false; break; }
            if (!digits) words.Add(w);
        }

        // ── Loose things ──

        private sealed class Thing
        {
            public Rigidbody Rb;
            public Renderer Renderer;
            public FruitWorldItem Item;
            public string Name, Key;
            public float Mass;
            public Vector3 Size;
        }

        /// <summary>Where a loose thing is: its collider bounds if it is an item (where a hand goes),
        /// else its renderer's.</summary>
        private static bool Centre(Thing t, out Vector3 c)
        {
            c = default;
            if (t.Item != null && t.Item.Valid) { c = t.Item.Position; return true; }
            if (t.Renderer == null) return false;
            try { c = t.Renderer.bounds.center; return true; } catch { return false; }
        }

        private static readonly List<Thing> _things = new List<Thing>(64);
        private static float _thingsAt = -100f;

        /// <summary>Loose rigidbodies with something to see, not part of a body, rescanned once a
        /// second at most (0.1 ms for 120 rigidbodies).</summary>
        private static void ScanThings()
        {
            if (Time.time - _thingsAt < 1f) return;
            _thingsAt = Time.time;
            _things.Clear();
            RefreshBodyGos();
            Il2CppArrayBase<Rigidbody> all;
            try { all = Object.FindObjectsOfType<Rigidbody>(false); } catch { return; }
            foreach (var rb in all)
            {
                if (rb == null) continue;
                var go = rb.gameObject;
                if (_bodyGos.ContainsKey(go.Pointer)) continue;
                string raw = go.name ?? "";
                if (raw.StartsWith("FruitLib_", StringComparison.Ordinal) || raw.StartsWith("FruitSight_", StringComparison.Ordinal)) continue;
                if (go.layer == 7) continue;   // puppet layer: bits of bodies
                Renderer r = null;
                try { r = go.GetComponentInChildren<Renderer>(); } catch { }
                Vector3 size;
                string name;
                // What FruitItems knows by name is always seen, whatever size or object name it has:
                // a grenade is under a fist, a gun's rigidbody is called "Physics", and a body that
                // can be told to pick either up has to be able to see it. (Until 5.35.0 the two
                // lists disagreed, and a rifle at a body's feet was in one and not the other.)
                var item = FruitItems.Active ? FruitItems.Of(rb) : null;
                if (item != null)
                {
                    name = item.Name;
                    size = item.Size;
                }
                else
                {
                    if (r == null) continue;
                    try { size = r.bounds.size; } catch { continue; }
                    // Shell casings, ejecta chunks, shards: smaller than a fist is not worth a line.
                    if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) < 0.15f) continue;
                    name = Name(go).Object;
                    if (Meaningless.Contains(name)) continue;
                }
                _things.Add(new Thing { Rb = rb, Renderer = r, Item = item, Name = name, Key = "thing:" + go.Pointer.ToInt64(), Mass = rb.mass, Size = size });
            }
        }

        // ── A look ──

        /// <summary>
        /// Everything a body can see from where its eyes are now. A glance
        /// (<paramref name="full"/> false) covers bodies, the player and loose things; a full look
        /// adds the layout (indoors, ground, open ways) and the buildings and fixtures in view.
        /// </summary>
        public static FruitView Look(FruitBody b, bool full = true)
        {
            var v = new FruitView { Body = b, Time = Time.time, Full = full, Setting = Setting };
            if (b == null || !b.HasPosition) return v;
            Eye(b, out v.Eye, out v.Forward, out v.Up);
            v.Flat = new Vector3(v.Forward.x, 0f, v.Forward.z);
            if (v.Flat.sqrMagnitude < 0.09f) v.Flat = b.Facing;
            v.Flat.y = 0f;
            v.Flat = v.Flat.sqrMagnitude > 1e-4f ? v.Flat.normalized : Vector3.forward;

            // Bodies: head, chest or pelvis in sight is enough.
            foreach (var o in FruitBodies.All)
            {
                if (o == b || !o.HasPosition) continue;
                Vector3 head = o.LiveHeadPosition;
                float dist = Vector3.Distance(head, v.Eye);
                if (dist > Range) continue;
                var s = Seen(v, "body", "", "body:" + o.Id, head);
                s.Body = o;
                s.Moving = o.Speed > 0.6f;
                s.Clear = Clear(v.Eye, head, b, o)
                       || (o.Limbs[FruitBodies.Torso].Present && Clear(v.Eye, o.Limbs[FruitBodies.Torso].LivePosition, b, o))
                       || (o.Limbs[FruitBodies.Pelvis].Present && Clear(v.Eye, o.Limbs[FruitBodies.Pelvis].LivePosition, b, o));
                s.Visible = s.Clear && s.Zone != FruitSightZone.Behind;
                s.Close = Near(b, v.Eye, head);
            }

            // The player, as whatever stands in for it (NeuroFrukt's orb).
            if (FruitBodies.HasView && Vector3.Distance(FruitBodies.ViewPosition, v.Eye) <= Range * 1.5f)
            {
                var s = Seen(v, "player", "", "player", FruitBodies.ViewPosition);
                // Stopped short of the camera: the player's held item has colliders right there.
                Vector3 toView = FruitBodies.ViewPosition - v.Eye;
                s.Clear = toView.magnitude < 0.4f || Clear(v.Eye, FruitBodies.ViewPosition - toView.normalized * 0.3f, b);
                s.Visible = s.Clear && s.Zone != FruitSightZone.Behind;
                s.Close = Near(b, v.Eye, FruitBodies.ViewPosition);
            }

            // Loose things.
            ScanThings();
            foreach (var t in _things)
            {
                if (t.Rb == null || t.Rb.WasCollected) continue;
                if (!Centre(t, out Vector3 c)) continue;
                if (Vector3.Distance(c, v.Eye) > Range) continue;
                var s = Seen(v, "thing", t.Name, t.Key, c);
                s.Mass = t.Mass; s.Size = t.Size; s.Item = t.Item;
                try { s.Moving = t.Rb.linearVelocity.sqrMagnitude > 0.25f; } catch { }
                s.Close = Near(b, v.Eye, c);
                s.Clear = Clear(v.Eye, c, b, null, t.Rb) || Clear(v.Eye, c + Vector3.up * t.Size.y * 0.45f, b, null, t.Rb);
                s.Visible = s.Clear && s.Zone != FruitSightZone.Behind;
            }

            if (full) { Layout(v, b); Surroundings(v, b); }

            v.Seen.Sort((x, y) => x.Visible != y.Visible ? (x.Visible ? -1 : 1) : x.Distance.CompareTo(y.Distance));
            return v;
        }

        private static FruitSeen Seen(FruitView v, string kind, string name, string key, Vector3 at)
        {
            Vector3 d = at - v.Eye;
            float angle = Vector3.Angle(v.Forward, d);
            var s = new FruitSeen { Kind = kind, Name = name, Key = key, Position = at, Distance = d.magnitude, Angle = angle, Zone = ZoneOf(angle) };
            v.Seen.Add(s);
            return s;
        }

        /// <summary>The first solid thing along a ray that isn't the body itself, glass, or another body.</summary>
        private static bool FirstSolid(Vector3 from, Vector3 dir, float dist, FruitBody self, out RaycastHit hit, out FruitThingName name)
        {
            hit = default; name = default;
            Il2CppStructArray<RaycastHit> hits;
            try { hits = Physics.RaycastAll(from, dir, dist, ~0, QueryTriggerInteraction.Ignore); } catch { return false; }
            float best = float.PositiveInfinity;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                var c = h.collider;
                if (c == null || h.distance >= best) continue;
                if (BodyOf(c) != 0) continue;
                var n = Name(c.gameObject);
                if (n.SeeThrough) continue;
                best = h.distance; hit = h; name = n; found = true;
            }
            return found;
        }

        private static void Layout(FruitView v, FruitBody b)
        {
            // Level ring at eye height (a body that is down gets its ring at standing height).
            Vector3 ringAt = v.Eye;
            if (b.Posture != FruitPosture.Upright) ringAt = new Vector3(b.Position.x, Mathf.Max(v.Eye.y, b.Position.y + 0.8f), b.Position.z);
            var inside = new Dictionary<string, int>();
            bool[] blocked = new bool[12];
            for (int i = 0; i < 12; i++)
            {
                Vector3 dir = Quaternion.AngleAxis(i * 30f, Vector3.up) * v.Flat;
                if (FirstSolid(ringAt, dir, 25f, b, out var h, out var n) && h.distance < 8f && !n.Ground)
                {
                    blocked[i] = true;
                    v.Walls++;
                    if (h.distance < 12f) inside[n.Object] = inside.TryGetValue(n.Object, out int c) ? c + 1 : 1;
                }
            }
            if (FirstSolid(ringAt, Vector3.up, 25f, b, out var up, out var upName))
            {
                v.Ceiling = up.distance;
                inside[upName.Object] = inside.TryGetValue(upName.Object, out int c) ? c + 3 : 3;
            }
            if (FirstSolid(ringAt, Vector3.down, 6f, b, out var downHit, out var down))
            {
                v.FloorY = downHit.point.y;
                v.Ground = down.Part;
                if (down.Storey >= 0) v.Storey = down.Storey;
            }
            v.Indoors = v.Ceiling >= 0f && v.Ceiling < 6f && v.Walls >= 7;
            if (v.Indoors)
            {
                int best = 0;
                foreach (var kv in inside) if (kv.Value > best) { best = kv.Value; v.Inside = kv.Key; }
            }

            // Which ways are open: ahead (330..30), right (60..120), behind (150..210), left (240..300).
            Quarter(v, blocked, 11, 0, 1, "ahead");
            Quarter(v, blocked, 2, 3, 4, "to your right");
            Quarter(v, blocked, 5, 6, 7, "behind you");
            Quarter(v, blocked, 8, 9, 10, "to your left");

            // Structures in view: a fan across the gaze, grouped by what they belong to.
            var nearest = new Dictionary<string, (float d, Vector3 p)>();
            Vector3 right = Vector3.Cross(Vector3.up, v.Flat);
            foreach (float elev in new[] { 18f, 2f, -12f })
            {
                for (int i = -6; i <= 6; i++)
                {
                    Vector3 dir = Quaternion.AngleAxis(i * 8f, Vector3.up) * Quaternion.AngleAxis(-elev, right) * v.Flat;
                    if (!FirstSolid(v.Eye, dir, Range, b, out var h, out var n) || n.Ground) continue;
                    try { var rb = h.collider.attachedRigidbody; if (rb != null && !rb.isKinematic) continue; } catch { }   // loose things are listed already
                    if (!nearest.TryGetValue(n.Object, out var have) || h.distance < have.d) nearest[n.Object] = (h.distance, h.point);
                }
            }
            foreach (var kv in nearest)
            {
                if (v.Indoors && kv.Key == v.Inside) continue;   // the walls around it are "inside", not a thing in view
                var s = Seen(v, "structure", kv.Key, "structure:" + kv.Key, kv.Value.p);
                s.Distance = kv.Value.d;
                s.Clear = s.Visible = true;
            }
        }

        /// <summary>Whether a point lies within <see cref="CloseRange"/> of the eyes or the body.</summary>
        private static bool Near(FruitBody b, Vector3 eye, Vector3 point)
        {
            float r2 = CloseRange * CloseRange;
            if ((point - eye).sqrMagnitude <= r2) return true;
            return b.HasPosition && (point - b.Position).sqrMagnitude <= r2;
        }

        /// <summary>What the close loose things rest on and how high they are: one ray under each
        /// (nearest eight), so a grenade is "on the wooden shop counter" and not just "ahead".</summary>
        private static void Surroundings(FruitView v, FruitBody b)
        {
            if (float.IsNaN(v.FloorY)) v.FloorY = b.Position.y - 0.9f;
            var close = new List<FruitSeen>(8);
            foreach (var s in v.Seen) if (s.Kind == "thing" && s.Close) close.Add(s);
            close.Sort((x, y) => x.Distance.CompareTo(y.Distance));
            int rays = 0;
            foreach (var s in close)
            {
                s.AboveFloor = s.Position.y - s.Size.y * 0.5f - v.FloorY;
                if (rays++ >= 8) continue;
                Vector3 from = s.Position - Vector3.up * (s.Size.y * 0.5f + 0.03f);
                if (FirstSolid(from, Vector3.down, 1.6f, b, out _, out var under) && !under.Ground) s.On = under.Object;
            }
        }

        private static void Quarter(FruitView v, bool[] blocked, int a, int b, int c, string label)
        {
            int n = (blocked[a] ? 1 : 0) + (blocked[b] ? 1 : 0) + (blocked[c] ? 1 : 0);
            if (n == 0) v.Open.Add(label);
            else if (n >= 2) v.Closed.Add(label);
        }

        /// <summary>A signed flat angle (degrees, positive = right) as words.</summary>
        public static string Side(float ang)
            => ang > -35f && ang < 35f ? "in front of you"
             : ang >= 145f || ang <= -145f ? "behind you"
             : ang > 0f ? (ang < 90f ? "ahead to your right" : "behind you to the right")
                        : (ang > -90f ? "ahead to your left" : "behind you to the left");

        internal static void ResetForScene()
        {
            _names.Clear();
            _things.Clear();
            _thingsAt = -100f;
            _bodyGos.Clear();
            _bodyGosFrame = -1;
            SettingOverride = null;
        }
    }
}
