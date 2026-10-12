using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppData.Maps;
using Il2CppGame;
using Il2CppInfrastructure.Project.States.Variants;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppLVA;
using Il2CppPlayer;
using Il2CppPresenters.MainMenu;
using Il2CppViews.MainMenu;
using Il2CppViews.Generic;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>The game map a modded map is built on top of.</summary>
    public enum FruitHostMap
    {
        /// <summary>
        /// A 1000 × 1000 m terrain that is one big hill, from 0 at the edges to about 226 m at
        /// the top, with a house on a small (~30 m) flat top, which FruitLib hides. The player
        /// starts on the house and a human spawns 8 m from them. A map bigger than the top wants
        /// <see cref="FruitMapDef.HideHostGround"/> and ground of its own.
        /// </summary>
        Flatland,
    }

    /// <summary>A map a mod adds to the main menu's map list. See docs/maps.md.</summary>
    public sealed class FruitMapDef
    {
        /// <summary>Unique, e.g. "FruitCity.TestBlock". Not shown.</summary>
        public string Id;
        /// <summary>The menu line, upper case like the game's own ("CITY").</summary>
        public string Word;
        /// <summary>The preview's size line ("SMALL", "200 × 200 M").</summary>
        public string Size = "";
        /// <summary>The preview's description.</summary>
        public string Blurb = "";
        /// <summary>The preview picture. Optional: without one the picture is hidden, as the
        /// game does for a card with no sprite.</summary>
        public Texture2D Preview;

        public FruitHostMap Host = FruitHostMap.Flatland;

        /// <summary>
        /// The place in one line, as someone standing in it would put it ("a crossroads in a small
        /// town, with an apartment block, a brick townhouse, a shop and a bungalow"). Read by
        /// <see cref="FruitSight.Setting"/> for minds that need to know where they are; without it
        /// the first sentence of <see cref="Blurb"/> is used. Since 5.18.0.
        /// </summary>
        public string Setting = "";

        /// <summary>
        /// Also hide the host's ground (Flatland: the terrain and the cube under it). Only for a
        /// map that brings a ground of its own everywhere the player can reach; the terrain is
        /// also what stops things falling forever.
        /// </summary>
        public bool HideHostGround;

        /// <summary>
        /// Builds the map. Called once the host scene has loaded, after its objects' Awake and
        /// before their Start, so the player and the first human are placed after it runs.
        /// Throwing puts the host map back as it was.
        /// </summary>
        public Action<FruitMapContext> Build;

        /// <summary>Called when the player leaves the map (its scene unloads). The scene takes
        /// everything adopted into it along; this is for the mod's own bookkeeping.</summary>
        public Action Teardown;
    }

    /// <summary>What <see cref="FruitMapDef.Build"/> gets: where things are, and what it may move.</summary>
    public sealed class FruitMapContext
    {
        public FruitMapDef Map { get; internal set; }
        /// <summary>The host's scene. Things in it (<see cref="Root"/>, <see cref="Adopt"/>) go when it goes.</summary>
        public Scene Scene { get; internal set; }
        /// <summary>The host's ground straight under the player's start.</summary>
        public Vector3 Ground { get; internal set; }
        /// <summary>The player's start (eye position, about 3.5 m above the ground).</summary>
        public Vector3 PlayerStart => _startPose != null ? _startPose.transform.position : Ground;
        /// <summary>Where the game's first human appears.</summary>
        public Vector3 HumanSpawn => _humanSpawner != null ? _humanSpawner.transform.position : Ground;

        internal PlayerStartPose _startPose;
        internal StartHumanSpawner _humanSpawner;

        /// <summary>
        /// An empty object at <see cref="Ground"/> in the host scene, made for the map. Parent
        /// everything to it (<c>Object.Instantiate(prefab, ctx.Root)</c>): it goes when the scene
        /// goes, and the menu scene being unloaded is the one Unity still calls active while
        /// Build runs, so a bare Instantiate would land there and vanish with it.
        /// </summary>
        public Transform Root { get; internal set; }

        /// <summary>Moves a root object into the host scene, for anything not under <see cref="Root"/>.</summary>
        public void Adopt(GameObject go)
        {
            if (go == null) return;
            try
            {
                if (go.transform.parent != null) go.transform.SetParent(null, true);
                if (!(go.scene == Scene)) SceneManager.MoveGameObjectToScene(go, Scene);
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMaps] could not move '{go.name}' into the map scene: {e.Message}"); }
        }

        /// <summary>Moves the player's start. Give the eye position: ground + ~3.5 m.</summary>
        public void MovePlayerStart(Vector3 eye)
        {
            if (_startPose != null) _startPose.transform.position = eye;
            else MelonLogger.Warning("[FruitMaps] no PlayerStartPose in the host scene; the player starts where the game puts them.");
        }

        /// <summary>Moves where the game's first human appears. Give a point on the ground.</summary>
        public void MoveHumanSpawn(Vector3 ground)
        {
            if (_humanSpawner != null) _humanSpawner.transform.position = ground;
        }

        /// <summary>Hides one more host object by path ("Level/Sun"). True if it was found.</summary>
        public bool HideHostObject(string path) => FruitMaps.HideHost(path);
    }

    /// <summary>
    /// Mod maps in the main menu's map list.
    ///
    /// <b>How it works.</b> The game's maps are an enum (Yard, Flatland), a MapInfo card each, and
    /// one button each in MapSelectView. A pick goes MapButton → MainMenuPresenter.LoadMap →
    /// SandboxState.Enter(map), which asks the MapCatalogue for the scene name and the card's word
    /// (for the loading curtain) and loads the scene. A registered map gets an id of its own
    /// (100 and up), a card, and a button cloned from the game's last one; the catalogue answers
    /// for it with its host's scene. Building a whole scene of our own would mean rebuilding the
    /// game's Zenject installers, player and UI wiring; riding on a host keeps all of that the
    /// game's, and the mod only adds its world to it.
    /// </summary>
    public static class FruitMaps
    {
        private const string Tag = "[FruitMaps]";
        private const int FirstId = 100;

        private sealed class Entry
        {
            public FruitMapDef Def;
            public int Id;
            public MapInfo Card;
        }

        private sealed class Host
        {
            public string Scene;
            public string[] Hide;
            public string[] Ground;
        }

        private static readonly Dictionary<FruitHostMap, Host> _hosts = new Dictionary<FruitHostMap, Host>
        {
            [FruitHostMap.Flatland] = new Host
            {
                Scene  = "Flatland",
                Hide   = new[] { "Level/DOM_bezshahtamaya" },
                Ground = new[] { "Level/Terrain", "Level/GroundCube" },
            },
        };

        private static readonly List<Entry> _maps = new List<Entry>();
        private static readonly Dictionary<int, Entry> _byId = new Dictionary<int, Entry>();
        private static readonly List<GameObject> _hidden = new List<GameObject>();

        // Set by SandboxState.Enter: the map the next scene load is for, ours or not.
        private static Entry _entering;
        private static Entry _current;

        /// <summary>The mod map being played, or null on the game's own maps and in the menu.</summary>
        public static FruitMapDef Current => _current?.Def;

        /// <summary>Raised after a mod map's Build ran without throwing.</summary>
        public static event Action<FruitMapDef> Loaded;

        /// <summary>
        /// Adds a map to the main menu. Call from OnInitializeMelon: the menu reads the list when
        /// it is drawn. Registering an Id again replaces the earlier definition.
        /// </summary>
        public static void Register(FruitMapDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.Id) || string.IsNullOrEmpty(def.Word))
            {
                MelonLogger.Warning($"{Tag} a map needs an Id and a Word.");
                return;
            }
            if (def.Build == null) MelonLogger.Warning($"{Tag} '{def.Id}' has no Build; it will be the bare host map.");

            foreach (var e in _maps)
                if (e.Def.Id == def.Id) { e.Def = def; e.Card = null; return; }

            var entry = new Entry { Def = def, Id = FirstId + _maps.Count };
            _maps.Add(entry);
            _byId[entry.Id] = entry;
            FruitLog.Info($"{Tag} '{def.Id}' registered as map {entry.Id} on {def.Host}.");
        }

        private static bool TryGet(MapID map, out Entry entry) => _byId.TryGetValue((int)map, out entry);

        // ── Menu card ───────────────────────────────────────────────────────────

        private static MapInfo Card(Entry e)
        {
            if (e.Card != null) return e.Card;

            var card = ScriptableObject.CreateInstance<MapInfo>();
            card.name    = "FruitMap_" + e.Def.Id;
            card.m_map   = (MapID)e.Id;
            card.m_word  = e.Def.Word;
            card.m_size  = e.Def.Size ?? "";
            card.m_blurb = e.Def.Blurb ?? "";
            if (e.Def.Preview != null)
            {
                var tex = e.Def.Preview;
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                sprite.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                card.m_preview = sprite;
            }
            card.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            e.Card = card;
            return card;
        }

        // The presenter builds the card list from the catalogue's map ids each time it draws the
        // menu. Ours go on the end of that list rather than into the catalogue, which the
        // project context builds once at boot, before any mod has registered anything.
        [HarmonyPatch(typeof(MainMenuPresenter), "Cards")]
        private static class Patch_Cards
        {
            private static void Postfix(ref Il2CppSystem.Collections.Generic.IReadOnlyList<MapInfo> __result)
            {
                if (_maps.Count == 0 || __result == null) return;
                try { EnsureCards(__result); }
                catch (Exception ex) { MelonLogger.Warning($"{Tag} adding cards failed: {ex}"); }
            }
        }

        /// <summary>Adds each mod map's card to <paramref name="cards"/> unless it is there already.</summary>
        private static void EnsureCards(Il2CppSystem.Collections.Generic.IReadOnlyList<MapInfo> cards)
        {
            var list = cards.TryCast<Il2CppSystem.Collections.Generic.List<MapInfo>>();
            if (list == null) { MelonLogger.Warning($"{Tag} the card list is not a List; mod maps left out."); return; }

            var have = new HashSet<int>();
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c != null) have.Add((int)c.m_map);
            }
            foreach (var e in _maps)
                if (!have.Contains(e.Id)) list.Add(Card(e));
        }

        // The map lines are serialized on MapSelectView, one per game map, and its Draw labels each
        // from its card. IL2CPP inlined Draw into MainMenuView.DrawMaps, so a hook on Draw never
        // runs (0.1.0's first test): DrawMaps, reached through the IMainMenuView interface and so
        // never inlined, is where ours are cloned in, just before the inlined Draw labels them.
        // It runs from the presenter's Start, before the map screen is first opened, so the view's
        // OnEnable then subscribes ours along with the rest.
        [HarmonyPatch(typeof(MainMenuView), nameof(MainMenuView.DrawMaps))]
        private static class Patch_DrawMaps
        {
            private static void Prefix(MainMenuView __instance, Il2CppSystem.Collections.Generic.IReadOnlyList<MapInfo> cards)
            {
                if (_maps.Count == 0 || __instance == null) return;
                // The inlined Draw looks every line's card up and throws on a missing one, so ours
                // must be in this list before a line of ours exists.
                try { if (cards != null) EnsureCards(cards); }
                catch (Exception ex) { MelonLogger.Warning($"{Tag} adding cards failed, no map lines added: {ex}"); return; }
                try { AddButtons(__instance.m_mapSelect); }
                catch (Exception ex) { MelonLogger.Warning($"{Tag} adding map lines failed: {ex}"); }
            }
        }

        // Second chance, when the map screen is enabled: before the view subscribes to its lines.
        // On a menu scene load this runs first - OnEnable comes before the presenter's Start, which
        // is what draws the menu - so adding the lines here is the normal order, not a failure.
        // Lines added here are labelled and their cards entered by hand; Draw relabels them later.
        [HarmonyPatch(typeof(MapSelectView), nameof(MapSelectView.OnEnable))]
        private static class Patch_MapSelectEnable
        {
            private static void Prefix(MapSelectView __instance)
            {
                if (_maps.Count == 0 || __instance == null) return;
                try
                {
                    // No cycling here: we are inside OnEnable, and the original subscribes every line,
                    // ours included, as soon as this prefix returns. Cycling would run OnEnable
                    // within OnEnable and subscribe the view's handlers twice (5.11.0: it threw).
                    if (AddButtons(__instance, cycle: false) > 0)
                        FruitLog.Info($"{Tag} map lines added as the map screen was enabled.");
                }
                catch (Exception ex) { MelonLogger.Warning($"{Tag} adding map lines failed: {ex}"); }
            }
        }

        /// <summary>Clones a line per mod map that has none yet. Returns how many were added.</summary>
        private static int AddButtons(MapSelectView view, bool cycle = true)
        {
            if (view == null) { MelonLogger.Warning($"{Tag} no map list on the main menu."); return 0; }
            var buttons = view.m_mapButtons;
            if (buttons == null || buttons.Length == 0) { MelonLogger.Warning($"{Tag} the map list has no lines to copy."); return 0; }

            var have = new HashSet<int>();
            MapButton last = null, prev = null;
            foreach (var b in buttons)
            {
                if (b == null) continue;
                have.Add((int)b.m_map);
                prev = last;
                last = b;
            }
            if (last == null) return 0;

            // The lines are placed by hand, not by a layout group (Yard at y -422, Flatland at -527),
            // so a copy lands on top of its original. Continue the column at the same spacing.
            var lastRt = last.transform.TryCast<RectTransform>();
            var prevRt = prev != null ? prev.transform.TryCast<RectTransform>() : null;
            Vector2 step = lastRt != null && prevRt != null ? lastRt.anchoredPosition - prevRt.anchoredPosition : new Vector2(0f, -105f);
            if (step.sqrMagnitude < 1f) step = new Vector2(0f, -105f);

            var added = new List<MapButton>();
            int sibling = last.transform.GetSiblingIndex();
            Vector2 at = lastRt != null ? lastRt.anchoredPosition : Vector2.zero;
            foreach (var e in _maps)
            {
                if (have.Contains(e.Id)) continue;
                var clone = FruitMenuClone.Make(last, last.transform.parent, "FruitMap_" + e.Def.Id);
                clone.m_map = (MapID)e.Id;
                clone.transform.SetSiblingIndex(++sibling);
                at += step;
                var rt = clone.transform.TryCast<RectTransform>();
                if (rt != null) rt.anchoredPosition = at;
                try { clone.m_button?.SetWord(e.Def.Word); } catch { }
                clone.gameObject.SetActive(true);
                added.Add(clone);
            }
            if (added.Count == 0) return 0;

            var grown = new Il2CppReferenceArray<MapButton>(buttons.Length + added.Count);
            for (int i = 0; i < buttons.Length; i++) grown[i] = buttons[i];
            for (int i = 0; i < added.Count; i++) grown[buttons.Length + i] = added[i];
            view.m_mapButtons = grown;

            // Hovering a line shows its card from this table, which Draw fills from the card list.
            try
            {
                var cards = view.m_cards;
                if (cards != null)
                    foreach (var e in _maps)
                        if (!cards.ContainsKey((MapID)e.Id)) cards[(MapID)e.Id] = Card(e);
            }
            catch (Exception ex) { MelonLogger.Warning($"{Tag} could not add the preview cards: {ex.Message}"); }

            // The view subscribes to its lines in OnEnable. If it is already up, cycle it so the
            // new ones are subscribed too (OnDisable lets go of the old subscriptions).
            if (cycle && view.isActiveAndEnabled) { view.enabled = false; view.enabled = true; }

            MelonLogger.Msg($"{Tag} {added.Count} map line(s) added to the map list.");
            return added.Count;
        }

        // ── Catalogue ───────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(MapCatalogue), nameof(MapCatalogue.SceneName))]
        private static class Patch_SceneName
        {
            private static bool Prefix(MapID map, ref string __result)
            {
                if (!TryGet(map, out var e)) return true;
                __result = _hosts[e.Def.Host].Scene;
                return false;
            }
        }

        [HarmonyPatch(typeof(MapCatalogue), nameof(MapCatalogue.Info))]
        private static class Patch_Info
        {
            private static bool Prefix(MapID map, ref MapInfo __result)
            {
                if (!TryGet(map, out var e)) return true;
                __result = Card(e);
                return false;
            }
        }

        [HarmonyPatch(typeof(SandboxState), nameof(SandboxState.Enter))]
        private static class Patch_Enter
        {
            private static void Prefix(MapID map)
            {
                TryGet(map, out _entering);
                if (_entering != null) MelonLogger.Msg($"{Tag} entering '{_entering.Def.Id}' on {_entering.Def.Host}.");
            }
        }

        // ── Scene lifecycle (from FruitLibMod) ──────────────────────────────────

        internal static void OnSceneLoaded(string sceneName)
        {
            var e = _entering;
            if (e == null || _hosts[e.Def.Host].Scene != sceneName)
            {
                // Any other scene - the menu, or a game map - ends whatever we were on.
                if (_current != null && _hosts[_current.Def.Host].Scene != sceneName) Leave();
                return;
            }

            // A reload of the same map (the game's restart) loads the host again.
            if (_current != null) Leave();
            _current = e;
            Build(e, sceneName);
        }

        internal static void OnSceneUnloaded(string sceneName)
        {
            if (_current != null && _hosts[_current.Def.Host].Scene == sceneName) Leave();
        }

        private static void Leave()
        {
            var e = _current;
            _current = null;
            _hidden.Clear();
            if (e?.Def.Teardown == null) return;
            try { e.Def.Teardown(); }
            catch (Exception ex) { MelonLogger.Warning($"{Tag} '{e.Def.Id}' Teardown threw: {ex}"); }
        }

        private static void Build(Entry e, string sceneName)
        {
            var host = _hosts[e.Def.Host];
            var ctx = new FruitMapContext
            {
                Map = e.Def,
                _startPose = FruitScene.First<PlayerStartPose>(),
                _humanSpawner = FruitScene.First<StartHumanSpawner>(),
            };

            // SceneManager.GetSceneByName and SetActiveScene are stripped or go through the
            // string icalls that throw here; the scene is read off an object known to be in it.
            if (ctx._startPose != null) ctx.Scene = ctx._startPose.gameObject.scene;
            else { try { ctx.Scene = SceneManager.GetSceneByName(sceneName); } catch { } }

            // The house goes first so the probe finds the terrain under it; the terrain, if the map
            // asked, only after it has been measured.
            _hidden.Clear();
            foreach (var path in host.Hide) HideHost(path);
            ctx.Ground = GroundUnder(ctx.PlayerStart);
            if (e.Def.HideHostGround) foreach (var path in host.Ground) HideHost(path);

            var root = new GameObject("FruitMap_" + e.Def.Id);
            root.transform.position = ctx.Ground;
            ctx.Root = root.transform;
            ctx.Adopt(root);

            try
            {
                e.Def.Build?.Invoke(ctx);
                MelonLogger.Msg($"{Tag} '{e.Def.Id}' built on {sceneName}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"{Tag} '{e.Def.Id}' Build threw; the host map is put back as it was: {ex}");
                foreach (var go in _hidden) if (go != null) go.SetActive(true);
                _hidden.Clear();
                if (root != null) Object.Destroy(root);
                return;
            }

            try { Loaded?.Invoke(e.Def); }
            catch (Exception ex) { MelonLogger.Warning($"{Tag} a Loaded handler threw: {ex.Message}"); }
        }

        internal static bool HideHost(string path)
        {
            GameObject go = null;
            try { go = GameObject.Find(path); } catch { }
            if (go == null) { MelonLogger.Warning($"{Tag} host object '{path}' not found; left as it is."); return false; }
            go.SetActive(false);
            _hidden.Add(go);
            return true;
        }

        // The host terrain straight down from the player's start, now the house is out of the way.
        private static Vector3 GroundUnder(Vector3 from)
        {
            var origin = from + Vector3.up * 50f;
            try
            {
                var terrain = FruitScene.First<TerrainCollider>(false);
                if (terrain != null && terrain.Raycast(new Ray(origin, Vector3.down), out RaycastHit th, 2000f))
                    return th.point;
                if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 2000f))
                    return hit.point;
            }
            catch (Exception ex) { MelonLogger.Warning($"{Tag} ground probe failed: {ex.Message}"); }

            // Flatland's hilltop, read from its heightmap (12344 / 32766 × 600 m).
            return new Vector3(from.x, 226f, from.z);
        }
    }
}
