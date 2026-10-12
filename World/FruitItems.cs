using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppLVA.Limbs;
using Il2CppSpawnables.Bodies;
using Il2CppSpawnables.Weapons;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>What a loose thing can be used for. Since 5.27.0.</summary>
    [Flags]
    public enum FruitAffords
    {
        None      = 0,
        /// <summary>Can be picked up and carried in one hand.</summary>
        Carry     = 1,
        /// <summary>Light enough to throw.</summary>
        Throw     = 2,
        /// <summary>Long and solid enough to hit someone with.</summary>
        Swing     = 4,
        /// <summary>A gun: <see cref="FruitBodyControl.Use"/> aims and fires it.</summary>
        Shoot     = 8,
        /// <summary>Goes off: a mod's grenade or charge.</summary>
        Explosive = 16,
        /// <summary>Has a use of its own (<see cref="FruitWorldItem.OnUse"/>).</summary>
        Use       = 32,
    }

    /// <summary>
    /// One step of how a body handles an item when it uses it (<see cref="FruitWorldItem.Handling"/>).
    /// FruitLib runs the steps in order, by itself, without asking anyone again. Since 5.30.0.
    /// </summary>
    public enum FruitUseStep
    {
        /// <summary>Raise it and point it at the target: its <see cref="FruitWorldItem.Muzzle"/>
        /// (or the Breech-to-Muzzle line) is turned onto the target, for <see cref="FruitBodyControl.AimSeconds"/>.</summary>
        Aim,
        /// <summary>Run its <see cref="FruitWorldItem.OnUse"/>, wherever it is (in the hand, or
        /// where it was put down or thrown), then wait <see cref="FruitWorldItem.HandlingPause"/>.</summary>
        Use,
        /// <summary>Throw it at the target (ahead, if there is none).</summary>
        Throw,
        /// <summary>Set it down in front of the body, upright, facing the target.</summary>
        PutDown,
        /// <summary>Run from where the item is to <see cref="FruitWorldItem.RetreatDistance"/>.</summary>
        Retreat,
        /// <summary>Wait <see cref="FruitWorldItem.HandlingPause"/> seconds.</summary>
        Wait,
    }

    /// <summary>Ready-made <see cref="FruitWorldItem.Handling"/> recipes. Since 5.30.0.</summary>
    public static class FruitHandling
    {
        /// <summary>A hand grenade: pull the pin, then throw it at the target.</summary>
        public static readonly FruitUseStep[] ArmAndThrow = { FruitUseStep.Use, FruitUseStep.Throw };
        /// <summary>A launcher: point it at the target, then fire it.</summary>
        public static readonly FruitUseStep[] AimAndFire = { FruitUseStep.Aim, FruitUseStep.Use };
        /// <summary>A mine: set it down facing the target, arm it, get clear.</summary>
        public static readonly FruitUseStep[] PlaceAndArm = { FruitUseStep.PutDown, FruitUseStep.Use, FruitUseStep.Retreat };
        /// <summary>A charge set off from afar: set it down, get clear, then set it off.</summary>
        public static readonly FruitUseStep[] PlaceAndDetonate = { FruitUseStep.PutDown, FruitUseStep.Retreat, FruitUseStep.Use };
        /// <summary>Used where it is held (the default for an item with an OnUse).</summary>
        public static readonly FruitUseStep[] InHand = { FruitUseStep.Use };

        /// <summary>Whether a recipe needs someone or somewhere to aim or throw at.</summary>
        public static bool NeedsTarget(FruitUseStep[] steps)
        {
            if (steps == null) return false;
            foreach (var s in steps) if (s == FruitUseStep.Aim || s == FruitUseStep.Throw) return true;
            return false;
        }

        /// <summary>Whether a recipe uses the item before throwing it (a throw then means a live one).</summary>
        public static bool ArmsBeforeThrow(FruitUseStep[] steps)
        {
            if (steps == null) return false;
            bool used = false;
            foreach (var s in steps)
            {
                if (s == FruitUseStep.Use) used = true;
                if (s == FruitUseStep.Throw) return used;
            }
            return false;
        }

        /// <summary>"pull the pin, then throw it", for prompts.</summary>
        public static string Describe(FruitUseStep[] steps, string useVerb)
        {
            if (steps == null || steps.Length == 0) return useVerb ?? "";
            var parts = new List<string>();
            foreach (var s in steps)
            {
                switch (s)
                {
                    case FruitUseStep.Aim:     parts.Add("aim it"); break;
                    case FruitUseStep.Use:     parts.Add(string.IsNullOrEmpty(useVerb) ? "use it" : useVerb); break;
                    case FruitUseStep.Throw:   parts.Add("throw it"); break;
                    case FruitUseStep.PutDown: parts.Add("set it down"); break;
                    case FruitUseStep.Retreat: parts.Add("get clear"); break;
                }
            }
            return string.Join(", then ", parts);
        }
    }

    /// <summary>
    /// A mod's gun lying in the world (<see cref="FruitWorldItem.ModGun"/>). FruitLib aims it
    /// for a body that holds it, re-seating it so <see cref="Muzzle"/> points at the target, and
    /// asks the mod to fire it. Since 5.29.0.
    /// </summary>
    public sealed class FruitModGun
    {
        /// <summary>Where rounds leave; its forward is the barrel.</summary>
        public Transform Muzzle;
        /// <summary>Keeps firing while the trigger is held: every <see cref="ShotGap"/> seconds.</summary>
        public bool Automatic;
        /// <summary>Seconds between shots.</summary>
        public float ShotGap = 0.3f;
        /// <summary>Fire one shot out of <see cref="Muzzle"/> along its forward: the round, sound
        /// and flash. Called with the item and the body firing it, null for the player.</summary>
        public Action<FruitWorldItem, FruitBody> Shoot;
    }

    /// <summary>
    /// A loose thing a body can pick up: a prop, a gun lying about, a crate, a mod's item. Found
    /// by <see cref="FruitItems"/> or marked by a mod. Since 5.27.0.
    /// </summary>
    public sealed class FruitWorldItem
    {
        public Rigidbody Rb { get; internal set; }
        /// <summary>"Lynx F pistol", "wooden crate". A mod may rename it.</summary>
        public string Name = "";
        public FruitAffords Affords;
        /// <summary>Marked by a mod rather than found by the default rule.</summary>
        public bool Explicit { get; internal set; }
        /// <summary>Which mod marked or described it ("BombsAway"); empty for the default rule.</summary>
        public string Source = "";
        /// <summary>The game's own gun, if it is one.</summary>
        public Firearm Gun { get; internal set; }
        /// <summary>A mod's gun: set by the mod that put it in the world. Bodies aim and fire it
        /// as they do the game's guns, and the player fires it with the interaction key. Since 5.29.0.</summary>
        public FruitModGun ModGun;
        /// <summary>The game's gun or a mod's. Since 5.29.0.</summary>
        public bool IsGun => Gun != null || ModGun != null;
        /// <summary>Its mass when found (a held item may be made lighter while held).</summary>
        public float Mass { get; internal set; }
        /// <summary>World size of its colliders when found.</summary>
        public Vector3 Size { get; internal set; }
        /// <summary>Where a hand holds it, in the item's own space (position relative to the
        /// rigidbody, rotation-only). Null: FruitLib picks one (a gun's grip, a long thing's end,
        /// else the nearest surface).</summary>
        public Vector3? Grip;
        /// <summary>How the item sits in a carrying body's hands, relative to the body's facing
        /// (forward, up). Null: FruitLib picks one.</summary>
        public Quaternion? CarryRotation;
        /// <summary>A use of its own (pull a pin, light a fuse). Called with the user, the item,
        /// and the body or point it is used on (either may be null); return true if it did
        /// something. Set <see cref="FruitAffords.Use"/> with it. When the player uses it with the
        /// interaction key (<see cref="FruitPropUse"/>), the user and target are both null.</summary>
        public Func<FruitBody, FruitWorldItem, FruitBody, Vector3?, bool> OnUse;
        /// <summary>Every frame while the player holds the interaction key on it, with the seconds
        /// since the press: an automatic gun's trigger. Since 5.29.0.</summary>
        public Action<FruitWorldItem, float> OnUseHeld;
        /// <summary>The player let go of the interaction key, or looked away. Since 5.29.0.</summary>
        public Action<FruitWorldItem> OnUseReleased;
        /// <summary>The use in a few words, for prompts: "pull the pin and throw it".</summary>
        public string UseVerb = "";
        /// <summary>
        /// How a body handles it when told to use it: steps FruitLib runs in order by itself
        /// (<see cref="FruitHandling"/> has the usual ones). Null: the default (a gun is aimed and
        /// fired, an OnUse runs in the hand, a thing to swing is swung, a throwable is thrown). A
        /// recipe that uses the item before throwing it also runs when a body is told to throw
        /// it, so a grenade is never thrown with the pin in, nor held once it is out. Since 5.30.0.
        /// </summary>
        public FruitUseStep[] Handling;
        /// <summary>Seconds after a Use step before the next step, and the length of a Wait. Since 5.30.0.</summary>
        public float HandlingPause = 0.35f;
        /// <summary>How far a Retreat step runs from the item, metres. Since 5.30.0.</summary>
        public float RetreatDistance = 10f;
        /// <summary>Where it points from, for an Aim step: its forward is the bore, unless
        /// <see cref="Breech"/> is set (then the line from the breech to here is). Null: a gun's
        /// own muzzle, else the rigidbody. Since 5.30.0.</summary>
        public Transform Muzzle, Breech;
        /// <summary>How a PutDown step sets it down, relative to a frame facing the target
        /// (identity: its own up up, its own forward at the target). Since 5.30.0.</summary>
        public Quaternion PutDownRotation = Quaternion.identity;
        /// <summary>Anything a mod wants to keep with it.</summary>
        public object Tag;
        /// <summary>The body holding it, or null.</summary>
        public FruitBody Holder { get; internal set; }
        public float FoundAt { get; internal set; }

        internal IntPtr Ptr;
        internal Il2CppArrayBase<Collider> Colliders;

        /// <summary>Stable while the item lives: "item:&lt;pointer&gt;".</summary>
        public string Key => "item:" + Ptr.ToInt64();

        public bool Valid => Rb != null && !Rb.WasCollected;

        public GameObject Root => Valid ? Rb.gameObject : null;

        /// <summary>The middle of its colliders, or the rigidbody if it has none.</summary>
        public Vector3 Position
        {
            get
            {
                if (!Valid) return Vector3.zero;
                if (Colliders != null)
                {
                    bool any = false;
                    Bounds b = default;
                    foreach (var c in Colliders)
                    {
                        if (c == null || c.WasCollected || !c.enabled) continue;
                        if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
                    }
                    if (any) return b.center;
                }
                return Rb.position;
            }
        }

        public bool Can(FruitAffords a) => (Affords & a) == a;

        /// <summary>"a Lynx F pistol".</summary>
        public string WithArticle => ("aeiou".IndexOf(char.ToLowerInvariant(Name.Length > 0 ? Name[0] : 'x')) >= 0 ? "an " : "a ") + Name;

        public override string ToString() => $"{Name} ({Mass:0.#} kg, {Affords})";
    }

    /// <summary>
    /// The loose things lying about that bodies can pick up, carry, throw and use. Off until a
    /// mod calls <see cref="Require"/>; then it scans for loose rigidbodies once a second.
    ///
    /// By default anything loose counts (<see cref="AutoMark"/>): a non-kinematic rigidbody with
    /// colliders, not part of a body, not in the player's hand, up to <see cref="AutoMaxMass"/>
    /// and <see cref="AutoMaxSize"/>. A mod can mark its own things (<see cref="Mark"/>), refine
    /// what the rule finds (<see cref="Describers"/>), or keep a thing out (<see cref="Veto"/>).
    /// See docs/items.md. Since 5.27.0.
    /// </summary>
    public static class FruitItems
    {
        /// <summary>Whether loose things count without a mod marking them.</summary>
        public static bool AutoMark = true;
        /// <summary>Heaviest (kg) and largest (longest side, m) thing the default rule takes.</summary>
        public static float AutoMaxMass = 25f, AutoMaxSize = 1.6f;
        /// <summary>Heaviest thing that can be thrown, kg.</summary>
        public static float ThrowMaxMass = 5f;

        /// <summary>
        /// Asked once for each thing the default rule finds, newest first: rename it, change what
        /// it affords, give it a use or a grip. Return false to keep it out.
        /// </summary>
        public static readonly List<Func<FruitWorldItem, bool>> Describers = new List<Func<FruitWorldItem, bool>>();

        /// <summary>Raised when a thing is first found or marked.</summary>
        public static event Action<FruitWorldItem> Found;

        private static readonly HashSet<string> _requiredBy = new HashSet<string>();
        private static readonly Dictionary<IntPtr, FruitWorldItem> _items = new Dictionary<IntPtr, FruitWorldItem>();
        private static readonly List<FruitWorldItem> _list = new List<FruitWorldItem>();
        private static readonly HashSet<IntPtr> _vetoed = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> _rejected = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> _seen = new HashSet<IntPtr>();
        private static float _scanAt = -100f;

        public static bool Active => _requiredBy.Count > 0 || FruitLibConfig.HandsProbe;

        /// <summary>Turns the scan on (and FruitBodies with it).</summary>
        public static void Require(string mod)
        {
            if (string.IsNullOrEmpty(mod)) mod = "?";
            if (_requiredBy.Add(mod)) FruitBodies.Require("FruitItems");
        }

        public static IReadOnlyList<FruitWorldItem> All => _list;

        // ── Marking ──

        /// <summary>
        /// Makes a thing an item, whatever the default rule says, or updates it if it is one.
        /// <paramref name="affords"/> None: worked out from its mass and shape.
        /// </summary>
        public static FruitWorldItem Mark(Rigidbody rb, string name = null, FruitAffords affords = FruitAffords.None, string source = "")
        {
            if (rb == null) return null;
            _vetoed.Remove(rb.Pointer);
            _rejected.Remove(rb.Pointer);
            if (!_items.TryGetValue(rb.Pointer, out var it))
            {
                it = Build(rb);
                if (it == null) return null;
                Add(it);
            }
            it.Explicit = true;
            if (!string.IsNullOrEmpty(name)) it.Name = name;
            if (affords != FruitAffords.None) it.Affords = affords | FruitAffords.Carry;
            if (!string.IsNullOrEmpty(source)) it.Source = source;
            return it;
        }

        /// <summary>Takes a thing out of the list (it may be found again by the default rule).</summary>
        public static void Unmark(Rigidbody rb)
        {
            if (rb == null || !_items.TryGetValue(rb.Pointer, out var it)) return;
            if (it.Holder != null) FruitBodyControl.Drop(it.Holder, "FruitItems");
            Remove(it);
        }

        /// <summary>Keeps a thing out for good: never found, never picked up.</summary>
        public static void Veto(Rigidbody rb)
        {
            if (rb == null) return;
            Unmark(rb);
            _vetoed.Add(rb.Pointer);
        }

        // ── Asking ──

        public static FruitWorldItem Of(Rigidbody rb) => rb != null && _items.TryGetValue(rb.Pointer, out var it) && it.Valid ? it : null;

        /// <summary>The item a GameObject (or one of its colliders) belongs to, or null.</summary>
        public static FruitWorldItem Of(GameObject go)
        {
            if (go == null) return null;
            Rigidbody rb = null;
            try { rb = go.GetComponentInParent<Rigidbody>(); } catch { }
            return Of(rb);
        }

        /// <summary>The item with this <see cref="FruitWorldItem.Key"/>, or one keyed by its
        /// GameObject the way FruitSight keys things ("thing:&lt;pointer&gt;").</summary>
        public static FruitWorldItem Of(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            foreach (var it in _list)
            {
                if (!it.Valid) continue;
                if (it.Key == key) return it;
                try { if (key == "thing:" + it.Rb.gameObject.Pointer.ToInt64()) return it; } catch { }
            }
            return null;
        }

        /// <summary>Who holds the gun, or null (the player, or nobody).</summary>
        public static FruitBody HolderOf(Firearm gun)
        {
            if (gun == null) return null;
            foreach (var it in _list) if (it.Gun != null && it.Holder != null && it.Gun.Pointer == gun.Pointer) return it.Holder;
            return null;
        }

        /// <summary>
        /// Fires a mod's gun once (<see cref="FruitWorldItem.ModGun"/>): the mod's shot, then the
        /// Gunshot event, credited to <paramref name="by"/>, else to the body holding it, else to
        /// the player. FruitLib calls this for bodies and for the interaction key. Since 5.29.0.
        /// </summary>
        public static bool FireModGun(FruitWorldItem it, FruitBody by = null)
        {
            var gun = it?.ModGun;
            if (gun?.Shoot == null || !it.Valid) return false;
            by ??= it.Holder;
            try { gun.Shoot(it, by); }
            catch (Exception e) { FruitBodies.ReportOnce("mod gun shoot", e); return false; }
            Transform muzzle = gun.Muzzle != null ? gun.Muzzle : it.Rb.transform;
            try { FruitWorldEvents.OnShot(muzzle.position, muzzle.forward, it.Name, by); }
            catch (Exception e) { FruitBodies.ReportOnce("mod gun shot event", e); }
            return true;
        }

        /// <summary>Items within <paramref name="radius"/> of a point, nearest first.</summary>
        public static List<FruitWorldItem> Near(Vector3 point, float radius, List<FruitWorldItem> into = null, bool includeHeld = false)
        {
            into ??= new List<FruitWorldItem>();
            into.Clear();
            float r2 = radius * radius;
            foreach (var it in _list)
            {
                if (!it.Valid || (!includeHeld && it.Holder != null)) continue;
                if ((it.Position - point).sqrMagnitude <= r2) into.Add(it);
            }
            into.Sort((a, b) => (a.Position - point).sqrMagnitude.CompareTo((b.Position - point).sqrMagnitude));
            return into;
        }

        /// <summary>
        /// The item a person means by <paramref name="said"/> ("the pistol", "crowbar", "Lynx"),
        /// nearest to <paramref name="near"/> first; null if nothing within <paramref name="radius"/>
        /// matches. A word for what it is used for also matches: "gun" finds any gun.
        /// </summary>
        public static FruitWorldItem Find(string said, Vector3 near, float radius = 30f, bool includeHeld = false)
        {
            if (string.IsNullOrWhiteSpace(said)) return null;
            string want = said.Trim().ToLowerInvariant();
            foreach (var a in new[] { "the ", "a ", "an ", "that ", "this ", "my " })
                if (want.StartsWith(a)) { want = want.Substring(a.Length); break; }
            var words = want.Split(new[] { ' ', '-', ',' }, StringSplitOptions.RemoveEmptyEntries);

            FruitWorldItem best = null;
            float bestScore = 0f;
            foreach (var it in _list)
            {
                if (!it.Valid || (!includeHeld && it.Holder != null)) continue;
                float d = Vector3.Distance(it.Position, near);
                if (d > radius) continue;
                string name = it.Name.ToLowerInvariant();
                float score = 0f;
                if (name == want) score = 3f;
                else if (name.Contains(want)) score = 2f;
                else
                {
                    foreach (var w in words) if (w.Length > 2 && name.Contains(w)) score += 1f;
                    if (it.IsGun && (want.Contains("gun") || want.Contains("weapon") || want.Contains("firearm"))) score += 1.5f;
                    if (it.Can(FruitAffords.Swing) && (want.Contains("weapon") || want.Contains("club") || want.Contains("stick"))) score += 0.5f;
                }
                if (score <= 0f) continue;
                score -= d * 0.01f;
                if (score > bestScore) { bestScore = score; best = it; }
            }
            return best;
        }

        // ── Scan ──

        internal static void Tick()
        {
            if (!Active) return;
            float now = Time.time;
            if (now - _scanAt < 1f) return;
            _scanAt = now;

            for (int i = _list.Count - 1; i >= 0; i--)
                if (!_list[i].Valid) Remove(_list[i]);
            if (!AutoMark) return;

            Il2CppArrayBase<Rigidbody> all;
            try { all = Object.FindObjectsOfType<Rigidbody>(false); } catch { return; }
            foreach (var rb in all)
            {
                if (rb == null) continue;
                IntPtr p = rb.Pointer;
                if (_items.ContainsKey(p) || _vetoed.Contains(p)) continue;
                // Things turned down once are looked at again only if they could have changed
                // (a gun dropped by the player is Carried, then Live).
                if (_rejected.Contains(p) && !_seen.Add(p)) continue;
                var it = Qualify(rb);
                if (it == null) { _rejected.Add(p); continue; }
                bool keep = true;
                for (int d = Describers.Count - 1; d >= 0 && keep; d--)
                {
                    try { keep = Describers[d](it); }
                    catch (Exception e) { FruitBodies.ReportOnce("item describer", e); }
                }
                if (!keep) { _vetoed.Add(p); continue; }
                Add(it);
            }
            // A rejected thing gets one more look every 10 s (picked up, put down, spawned in hand).
            if ((int)now % 10 == 0) _seen.Clear();
        }

        /// <summary>The default rule: a FruitWorldItem for a loose thing, or null.</summary>
        private static FruitWorldItem Qualify(Rigidbody rb)
        {
            try
            {
                if (rb.isKinematic) return null;
                var go = rb.gameObject;
                if (go.layer == 7) return null;   // puppet layer: bits of bodies
                string raw = go.name ?? "";
                if (raw.StartsWith("FruitSight_", StringComparison.Ordinal) || raw.StartsWith("FruitNav", StringComparison.Ordinal)) return null;
                if (raw.StartsWith("FruitLib_", StringComparison.Ordinal) && !raw.StartsWith("FruitLib_Hands", StringComparison.Ordinal)) return null;
                if (go.GetComponentInParent<LimbPhysics>() != null) return null;
                // The game's spawnables say whether the player may act on them: a gun in the player's hand is not loose.
                var sp = go.GetComponent<SpawnablePhysics>();
                if (sp != null && !sp.Interactable) return null;
                if (rb.mass > AutoMaxMass) return null;
                var it = Build(rb);
                if (it == null) return null;
                float longest = Mathf.Max(it.Size.x, Mathf.Max(it.Size.y, it.Size.z));
                if (longest > AutoMaxSize || longest < 0.08f) return null;
                return it;
            }
            catch { return null; }
        }

        /// <summary>Name, size and what it affords, from what the thing is.</summary>
        private static FruitWorldItem Build(Rigidbody rb)
        {
            var it = new FruitWorldItem { Rb = rb, Ptr = rb.Pointer, Mass = rb.mass, FoundAt = Time.time };
            try { it.Colliders = rb.GetComponentsInChildren<Collider>(); } catch { }
            if (it.Colliders == null || it.Colliders.Length == 0) return null;

            bool any = false;
            Bounds b = default;
            foreach (var c in it.Colliders)
            {
                if (c == null || !c.enabled || c.isTrigger) continue;
                if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
            }
            if (!any) return null;
            it.Size = b.size;

            try { it.Gun = rb.GetComponentInParent<Firearm>(); } catch { }
            if (it.Gun != null)
            {
                it.Name = GunName(it.Gun, rb.mass);
                it.Affords = FruitAffords.Carry | FruitAffords.Shoot;
                return it;
            }

            it.Name = FruitSight.Name(rb.gameObject).Object;
            if (it.Name.StartsWith("fruit lib hands bar", StringComparison.Ordinal)) it.Name = "metal bar";
            it.Affords = FruitAffords.Carry;
            if (rb.mass <= ThrowMaxMass) it.Affords |= FruitAffords.Throw;
            float longest = Mathf.Max(it.Size.x, Mathf.Max(it.Size.y, it.Size.z));
            float shortest = Mathf.Min(it.Size.x, Mathf.Min(it.Size.y, it.Size.z));
            if (longest >= 0.35f && longest <= 1.6f && longest >= 3f * Mathf.Max(0.02f, shortest) && rb.mass >= 0.3f && rb.mass <= 8f)
                it.Affords |= FruitAffords.Swing;
            return it;
        }

        /// <summary>"Lynx F pistol", "Grist 03 shotgun", "Viper 17 rifle".</summary>
        private static string GunName(Firearm gun, float mass)
        {
            string model = FruitInventory.Prettify(FruitBodies.TypeNameOf(gun));
            string kind;
            bool pump = false, auto = false;
            try { pump = gun.TryCast<IPumpAction>() != null; } catch { }
            try { auto = gun.IsAutomatic; } catch { }
            kind = pump ? "shotgun" : auto || mass > 2.5f ? "rifle" : "pistol";
            return model.Length > 0 ? model + " " + kind : kind;
        }

        private static void Add(FruitWorldItem it)
        {
            _items[it.Ptr] = it;
            _list.Add(it);
            try { Found?.Invoke(it); } catch (Exception e) { FruitBodies.ReportOnce("item Found handler", e); }
        }

        private static void Remove(FruitWorldItem it)
        {
            _items.Remove(it.Ptr);
            _list.Remove(it);
        }

        internal static void ResetForScene()
        {
            _items.Clear();
            _list.Clear();
            _vetoed.Clear();
            _rejected.Clear();
            _seen.Clear();
            _scanAt = -100f;
        }
    }
}
