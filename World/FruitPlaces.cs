using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>What kind of place a <see cref="FruitPlace"/> is. Since 5.20.0.</summary>
    public enum FruitPlaceKind
    {
        /// <summary>Outdoors: a street, a yard, a car park.</summary>
        Area,
        /// <summary>A whole building.</summary>
        Building,
        /// <summary>A room on one storey of a building.</summary>
        Room,
        /// <summary>A spot in a room or area: where to stand to be at something ("sofa", "behind
        /// the counter", "bus stop"). Never what <see cref="FruitPlaces.Where"/> answers. Since 5.23.0.</summary>
        Spot,
    }

    /// <summary>A named box in the world: a room, a building, an outdoor area, a spot. Since 5.20.0.</summary>
    public sealed class FruitPlace
    {
        /// <summary>What people call it: "shop", "back room", "car park", "kitchen table".</summary>
        public string Name = "";
        public FruitPlaceKind Kind;
        /// <summary>The building it belongs to (a room's or spot's), or its own name (a building's); empty outdoors.</summary>
        public string Building = "";
        /// <summary>0 = ground floor; -1 = not on a storey (buildings, areas, spots outdoors).</summary>
        public int Storey = -1;
        public Vector3 Center;
        public Vector3 Size;
        public Quaternion Rotation = Quaternion.identity;
        /// <summary>The mod that registered it.</summary>
        public string Source = "";

        /// <summary>A spot's room or area (the smallest that holds it), or null. Since 5.23.0.</summary>
        public FruitPlace In;
        /// <summary>A spot's thing, when it is loose: the spot moves with it (<see cref="FruitPlaces"/>
        /// queries follow it). Null for a fixed spot. Since 5.23.0.</summary>
        public Transform Anchor;
        /// <summary>For a spot: the point a body standing there faces (the thing it is at). Since 5.23.0.</summary>
        public Vector3 LookAt;

        internal Vector3 AnchorOffset;
        internal Quaternion AnchorTurn = Quaternion.identity;

        /// <summary>Whether a point is inside the box, grown by <paramref name="pad"/> metres each way.</summary>
        public bool Contains(Vector3 p, float pad = 0f)
        {
            Vector3 l = Quaternion.Inverse(Rotation) * (p - Center);
            return Mathf.Abs(l.x) <= Size.x * 0.5f + pad && Mathf.Abs(l.y) <= Size.y * 0.5f + pad && Mathf.Abs(l.z) <= Size.z * 0.5f + pad;
        }

        /// <summary>The middle of its floor: where to walk to.</summary>
        public Vector3 Floor
        {
            get { Vector3 f = Center; f.y = Center.y - Size.y * 0.5f + 0.1f; return f; }
        }

        /// <summary>Its footprint, square metres.</summary>
        public float Area => Size.x * Size.z;

        /// <summary>"the shop's back room", "the townhouse", "the car park", "the sofa in the
        /// townhouse's living room".</summary>
        public string Label
        {
            get
            {
                if (Kind == FruitPlaceKind.Spot) return In != null ? $"{Short} in {In.Label}" : Short;
                if (Kind == FruitPlaceKind.Room && Building.Length > 0) return $"the {Building}'s {Name}";
                return "the " + Name;
            }
        }

        /// <summary>The name with its article: "the sofa", "behind the counter", "the back room". Since 5.23.0.</summary>
        public string Short
        {
            get
            {
                int sp = Name.IndexOf(' ');
                string first = sp > 0 ? Name.Substring(0, sp) : Name;
                return Prepositions.Contains(first) ? Name : "the " + Name;
            }
        }

        private static readonly HashSet<string> Prepositions = new HashSet<string>
            { "behind", "by", "at", "in", "on", "under", "next", "beside", "between", "near", "outside", "inside" };

        /// <summary>"ground floor", "first floor"...</summary>
        public string StoreyName => FruitPlaces.StoreyName(Storey);

        public override string ToString() => Kind == FruitPlaceKind.Room ? $"{Label}, {StoreyName}" : Label;
    }

    /// <summary>A doorway: a way into a building or between two rooms. Since 5.20.0.</summary>
    public sealed class FruitDoor
    {
        /// <summary>"front door", "back door", "doorway to the back room".</summary>
        public string Name = "";
        public string Building = "";
        public int Storey;
        /// <summary>Middle of the opening, at floor level.</summary>
        public Vector3 Position;
        /// <summary>Flat direction through it: out of the building for an outside door, from the
        /// front room to the back for an inner one.</summary>
        public Vector3 Through = Vector3.forward;
        /// <summary>True for a door to the outside.</summary>
        public bool Outside;
        public string Source = "";

        public string Label => $"the {Building}'s {Name}";
    }

    /// <summary>
    /// Named places: rooms, buildings, yards and streets, spots in them, and the doors between
    /// them. Map mods register them (FruitCity does, from markers in its prefab); anyone can ask
    /// where a point is, find a place by what someone called it, or list what is near.
    ///
    /// <code>
    /// FruitPlaces.ScanMarkers("MyMap", mapRoot);          // Place|room|back room|0, Door|shop|front door|0 ...
    /// var here = FruitPlaces.Where(body.Position);        // "the shop's back room, ground floor"
    /// var shop = FruitPlaces.Find("the shop", body.Position);
    /// var at   = FruitPlaces.SpotAt(body.Position);       // "the sofa in the townhouse's living room"
    /// </code>
    ///
    /// Markers are empty GameObjects anywhere under the root, named
    /// <c>Place|area,building,room,spot|name|storey[|building]</c> (box: the object's position,
    /// rotation and local scale as its size) or <c>Door|building|name|storey</c> (position: the
    /// middle of the opening at floor level; forward: out of the building, or from the front room
    /// to the back; a door whose name contains "front door", "back door" or "side door" is an
    /// outside door).
    ///
    /// A spot's box is where a body stands to be at something; its forward faces the thing. A
    /// spot marker parented to an object with a Rigidbody moves with it.
    /// </summary>
    /// <remarks>Since 5.20.0.</remarks>
    public static class FruitPlaces
    {
        private static readonly List<FruitPlace> _places = new List<FruitPlace>();
        private static readonly List<FruitDoor> _doors = new List<FruitDoor>();

        public static IReadOnlyList<FruitPlace> All => _places;
        public static IReadOnlyList<FruitDoor> Doors => _doors;

        public static void Register(FruitPlace p) { if (p != null) _places.Add(p); }
        public static void Register(FruitDoor d) { if (d != null) _doors.Add(d); }

        /// <summary>Drops everything one mod registered (before it registers again).</summary>
        public static void Clear(string source)
        {
            _places.RemoveAll(p => p.Source == source);
            _doors.RemoveAll(d => d.Source == source);
        }

        internal static void ClearAll() { _places.Clear(); _doors.Clear(); }

        /// <summary>Registers every Place| and Door| marker under <paramref name="root"/>. Returns how many.</summary>
        public static int ScanMarkers(string source, Transform root)
        {
            if (root == null) return 0;
            int n = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                string name = t.name;
                if (!name.StartsWith("Place|", StringComparison.Ordinal) && !name.StartsWith("Door|", StringComparison.Ordinal)) continue;
                var part = name.Split('|');
                if (part.Length < 4) continue;
                int.TryParse(part[3], out int storey);
                try
                {
                    if (part[0] == "Place")
                    {
                        var kind = part[1] == "room" ? FruitPlaceKind.Room : part[1] == "building" ? FruitPlaceKind.Building
                                 : part[1] == "spot" ? FruitPlaceKind.Spot : FruitPlaceKind.Area;
                        string building = part.Length > 4 ? part[4] : kind == FruitPlaceKind.Building ? part[2] : "";
                        var p = new FruitPlace
                        {
                            Name = part[2], Kind = kind,
                            Storey = kind == FruitPlaceKind.Room || (kind == FruitPlaceKind.Spot && building.Length > 0) ? storey : -1,
                            Building = building,
                            Center = t.position, Size = t.localScale, Rotation = t.rotation, Source = source,
                        };
                        if (kind == FruitPlaceKind.Spot) Pin(p, t);
                        Register(p);
                    }
                    else
                    {
                        Vector3 f = t.forward; f.y = 0f;
                        string dn = part[2];
                        Register(new FruitDoor
                        {
                            Building = part[1], Name = dn, Storey = storey, Position = t.position,
                            Through = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward,
                            Outside = dn.Contains("front door") || dn.Contains("back door") || dn.Contains("side door"),
                            Source = source,
                        });
                    }
                    n++;
                }
                catch (Exception e) { MelonLogger.Warning($"[FruitPlaces] marker \"{name}\" skipped: {e.Message}"); }
            }
            // Each spot's room or area: the place its middle is in.
            int spots = 0;
            foreach (var p in _places)
            {
                if (p.Kind != FruitPlaceKind.Spot || p.Source != source) continue;
                spots++;
                p.In = Where(p.Center);
                if (p.In != null && p.In.Kind == FruitPlaceKind.Building) p.In = null;
            }
            FruitLog.Info($"[FruitPlaces] {source}: {n} places and doors ({spots} spots).");
            return n;
        }

        // ── Spots on loose things ──

        /// <summary>A spot marker under a loose thing follows the thing: its offset is kept in the
        /// thing's yaw, its height stays (the thing may tip over; the floor doesn't).</summary>
        private static void Pin(FruitPlace p, Transform marker)
        {
            p.LookAt = marker.position + Flat(marker.forward);
            var parent = marker.parent;
            if (parent == null || parent.GetComponent<Rigidbody>() == null) return;
            p.Anchor = parent;
            var yaw = Yaw(parent);
            Vector3 off = Quaternion.Inverse(yaw) * (marker.position - parent.position);
            off.y = 0f;
            p.AnchorOffset = off;
            p.AnchorTurn = Quaternion.Inverse(yaw) * Yaw(marker);
            p.LookAt = parent.position;
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.forward;
        }

        /// <summary>Which way a thing faces, flat: its forward, or its up when it lies on its face or back.</summary>
        private static Quaternion Yaw(Transform t)
        {
            Vector3 f = t.forward; f.y = 0f;
            if (f.sqrMagnitude < 0.04f) { f = t.up; f.y = 0f; }
            return f.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(f.normalized, Vector3.up) : Quaternion.identity;
        }

        private static int _syncFrame = -1;

        /// <summary>Moves spots on loose things to where their things are now (once a frame).</summary>
        private static void Sync()
        {
            int frame = Time.frameCount;
            if (frame == _syncFrame) return;
            _syncFrame = frame;
            foreach (var p in _places)
            {
                if (p.Anchor == null) continue;
                try
                {
                    var a = p.Anchor;
                    var yaw = Yaw(a);
                    Vector3 c = a.position + yaw * p.AnchorOffset;
                    c.y = p.Center.y;
                    p.Center = c;
                    p.Rotation = yaw * p.AnchorTurn;
                    p.LookAt = a.position;
                }
                catch { p.Anchor = null; }   // destroyed with the map: stays where it was last
            }
        }

        /// <summary>The most specific place a point is in: a room before its building, a building
        /// before the area around it. Null when it is in none. Spots don't count: a body by the
        /// sofa is in the living room (<see cref="SpotAt"/> says it is at the sofa).</summary>
        public static FruitPlace Where(Vector3 point)
        {
            FruitPlace best = null;
            foreach (var p in _places)
            {
                if (p.Kind == FruitPlaceKind.Spot || !p.Contains(point, 0.05f)) continue;
                if (best == null || Rank(p) > Rank(best) || (Rank(p) == Rank(best) && p.Area < best.Area)) best = p;
            }
            return best;
        }

        /// <summary>The spot a point is at (inside its box), the nearest middle winning; null when
        /// at none. Since 5.23.0.</summary>
        public static FruitPlace SpotAt(Vector3 point)
        {
            Sync();
            FruitPlace best = null;
            float bestD = float.MaxValue;
            foreach (var p in _places)
            {
                if (p.Kind != FruitPlaceKind.Spot || !p.Contains(point, 0.05f)) continue;
                float d = FruitNav.Flat(p.Center - point);
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        /// <summary>
        /// The spots in a room or area (<paramref name="place"/>'s own), or in all of a building's
        /// rooms, nearest <paramref name="near"/> first. Since 5.23.0.
        /// </summary>
        public static void SpotsIn(FruitPlace place, Vector3 near, List<FruitPlace> into)
        {
            into.Clear();
            if (place == null) return;
            Sync();
            foreach (var p in _places)
            {
                if (p.Kind != FruitPlaceKind.Spot) continue;
                if (place.Kind == FruitPlaceKind.Building ? p.Building == place.Name : p.In == place) into.Add(p);
            }
            into.Sort((a, b) => FruitNav.Flat(a.Center - near).CompareTo(FruitNav.Flat(b.Center - near)));
        }

        /// <summary>The building a point is in, or null.</summary>
        public static FruitPlace BuildingAt(Vector3 point)
        {
            foreach (var p in _places) if (p.Kind == FruitPlaceKind.Building && p.Contains(point, 0.05f)) return p;
            return null;
        }

        private static int Rank(FruitPlace p) => p.Kind == FruitPlaceKind.Room ? 2 : p.Kind == FruitPlaceKind.Building ? 1 : 0;

        /// <summary>
        /// The place someone means by <paramref name="said"/> ("shop", "the townhouse's back room",
        /// "back room", "upstairs in the apartment block", "the sofa in the living room", "behind
        /// the counter"). Names and building names are matched as words; the best match wins, ties
        /// going to the nearest to <paramref name="near"/>, and to the ground floor. A spot is found
        /// by its own name only; its room or building said too makes it surer, and of spots with the
        /// same name, those in the room or building <paramref name="near"/> is in come first.
        /// </summary>
        public static FruitPlace Find(string said, Vector3 near)
        {
            if (string.IsNullOrWhiteSpace(said) || _places.Count == 0) return null;
            Sync();
            string s = " " + Clean(said) + " ";
            int floor = StoreyIn(s);
            FruitPlace nearRoom = null, nearBuilding = null;
            bool spotsLooked = false;
            FruitPlace best = null;
            float bestScore = 0f;
            foreach (var p in _places)
            {
                float score = 0f;
                string name = Clean(p.Name), building = Clean(p.Building);
                bool whole = name.Length > 0 && s.Contains(" " + name + " ");
                if (whole) score += 3f + name.Length * 0.01f;
                else if (name.Length > 0 && Words(name, s)) score += 1.5f;
                if (p.Kind == FruitPlaceKind.Spot)
                {
                    if (score <= 0f) continue;
                    if (!whole) score -= 1f;
                    string room = p.In != null ? Clean(p.In.Name) : "";
                    if (room.Length > 0 && room != name && s.Contains(" " + room + " ")) score += 2f;
                    if (building.Length > 0 && s.Contains(" " + building + " ")) score += 1f;
                    if (!spotsLooked) { spotsLooked = true; nearRoom = Where(near); nearBuilding = BuildingAt(near); }
                    if (p.In != null && p.In == nearRoom) score += 0.6f;
                    else if (nearBuilding != null && p.Building == nearBuilding.Name) score += 0.4f;
                }
                else
                {
                    if (p.Kind == FruitPlaceKind.Room && building.Length > 0 && s.Contains(" " + building + " ")) score += 2f;
                    if (score <= 0f) continue;
                    // A bare building name means the building, not one of its rooms.
                    if (p.Kind == FruitPlaceKind.Room && !whole) score -= 1f;
                }
                if (p.Kind == FruitPlaceKind.Room || (p.Kind == FruitPlaceKind.Spot && p.Storey >= 0))
                    score += floor < 0 ? (p.Storey == 0 ? 0.3f : 0f) : (p.Storey == floor ? 1f : -1f);
                score -= FruitNav.Flat(p.Center - near) * 0.002f;
                if (score > bestScore) { bestScore = score; best = p; }
            }
            return best;
        }

        private static bool Words(string name, string s)
        {
            // Every word of a multi-word name ("car park") somewhere in what was said. Short words
            // ("tv", "of") don't count either way, but at least one word must: before 5.24.0 a name
            // of only short words ("TV") matched anything at all, so "the front door" found the TV.
            int matched = 0;
            foreach (var w in name.Split(' '))
            {
                if (w.Length <= 2) continue;
                if (!s.Contains(" " + w + " ")) return false;
                matched++;
            }
            return matched > 0;
        }

        /// <summary>
        /// The door someone means by <paramref name="said"/> ("the front door", "the shop's back
        /// door", "the doorway to the back room", "the exit"), or null when what was said isn't
        /// about a door. A door named in full beats one whose words were said; front, back and side
        /// must agree; the building said, else the building <paramref name="near"/> is in, comes
        /// first; then the nearest, on the ground floor. Since 5.24.0.
        /// </summary>
        public static FruitDoor FindDoor(string said, Vector3 near)
        {
            if (string.IsNullOrWhiteSpace(said) || _doors.Count == 0) return null;
            string s = " " + Clean(said) + " ";
            bool exit = s.Contains(" exit ") || s.Contains(" way out ") || s.Contains(" outside ");
            if (!s.Contains(" door ") && !s.Contains(" doorway ") && !s.Contains(" doors ") && !exit) return null;
            var nearBuilding = BuildingAt(near);
            FruitDoor best = null;
            float bestScore = float.NegativeInfinity;
            foreach (var d in _doors)
            {
                string dn = Clean(d.Name), building = Clean(d.Building);
                bool skip = false;
                foreach (var w in new[] { "front", "back", "side" })
                    if (s.Contains(" " + w + " ") && !dn.Contains(w)) skip = true;
                if (skip || (exit && !d.Outside)) continue;
                float score = s.Contains(" " + dn + " ") ? 3f : Words(dn, s) ? 1.5f : 0.5f;
                if (building.Length > 0 && s.Contains(" " + building + " ")) score += 2f;
                else if (nearBuilding != null && d.Building == nearBuilding.Name) score += 1f;
                if (d.Storey == 0) score += 0.3f;
                score -= FruitNav.Flat(d.Position - near) * 0.01f;
                if (score > bestScore) { bestScore = score; best = d; }
            }
            return best;
        }

        private static int StoreyIn(string s)
        {
            if (s.Contains(" ground floor ") || s.Contains(" downstairs ")) return 0;
            if (s.Contains(" first floor ") || s.Contains(" upstairs ")) return 1;
            if (s.Contains(" second floor ")) return 2;
            if (s.Contains(" third floor ")) return 3;
            return -1;
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var c = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s.ToLowerInvariant()) c.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            string r = " " + c.ToString() + " ";
            foreach (var w in new[] { " the ", " a ", " an ", " to ", " into ", " inside ", " in ", " of ", " s " }) r = r.Replace(w, " ");
            while (r.Contains("  ")) r = r.Replace("  ", " ");
            return r.Trim();
        }

        /// <summary>Places whose box comes within <paramref name="radius"/> of a point, nearest first.
        /// Spots are left out unless <paramref name="spots"/> (then they are all there is).</summary>
        public static void Near(Vector3 point, float radius, List<FruitPlace> into, bool spots = false)
        {
            into.Clear();
            if (spots) Sync();
            foreach (var p in _places)
                if ((p.Kind == FruitPlaceKind.Spot) == spots && FlatGap(p, point) <= radius) into.Add(p);
            into.Sort((a, b) => FlatGap(a, point).CompareTo(FlatGap(b, point)));
        }

        /// <summary>Flat distance from a point to the edge of a place's box (0 inside).</summary>
        public static float FlatGap(FruitPlace p, Vector3 point)
        {
            Vector3 l = Quaternion.Inverse(p.Rotation) * (point - p.Center);
            float dx = Mathf.Max(0f, Mathf.Abs(l.x) - p.Size.x * 0.5f), dz = Mathf.Max(0f, Mathf.Abs(l.z) - p.Size.z * 0.5f);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>A building's doors (outside ones first).</summary>
        public static void DoorsOf(string building, List<FruitDoor> into)
        {
            into.Clear();
            foreach (var d in _doors) if (d.Building == building) into.Add(d);
            into.Sort((a, b) => b.Outside.CompareTo(a.Outside));
        }

        public static string StoreyName(int storey)
        {
            switch (storey)
            {
                case 0: return "ground floor";
                case 1: return "first floor";
                case 2: return "second floor";
                case 3: return "third floor";
                default: return storey < 0 ? "" : $"floor {storey}";
            }
        }
    }
}
