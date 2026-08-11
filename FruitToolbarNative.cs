using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppData.Icons;
using Il2CppData.Objects;
using Il2CppData.Player.Inventory.God.Categories;
using Il2CppData.Player.Inventory.God.Items;
using Il2CppInfrastructure.Project.Registration.Native;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppPlayer.Toolbar.Concrete;
using Il2CppViews.Toolbar;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    // ┌─────────────────────────────────────────────────────────────────────────────┐
    // │  FruitToolbar — extra native toolbar slots (5th and beyond)                 │
    // │                                                                             │
    // │  The dnSpy export of Assembly-CSharp only exposes IL2CPP interop            │
    // │  *signatures*, never method bodies, so the mechanism below was recovered    │
    // │  from a decompile of the original working GunsGunsGuns build (kept at       │
    // │  GameData\OldGunsGunsGunsDnspyExport) plus in-game probing. The             │
    // │  non-obvious parts, worth not rediscovering:                                │
    // │                                                                             │
    // │  1. GodToolbarService.qgj (a jr<bji>) is the REAL slot container:           │
    // │        qgj.qfy : Il2CppReferenceArray<hq<bji>>   — the slot array           │
    // │        qgj.qfx : int                             — the slot count           │
    // │     Each hq<bji> holds .qbg (the item) and .qbf (a flag).                   │
    // │     Growing this is what makes a slot selectable. Everything else —         │
    // │     m_itemsToPopulate, m_capacity, ToolbarView.m_slotsViews, even a         │
    // │     successful fad() — leaves qfy at 4 and produces a slot that draws       │
    // │     but can never be selected.                                             │
    // │                                                                             │
    // │  2. The item catalog is NOT a wall. No custom bji is needed:                │
    // │     NativeGIITools.sxb is a spare, real tool bji that can serve as the      │
    // │     payload for injected slots. (NativeGIIWeapons only has a hardcoded      │
    // │     m_glock17, which is misleading if you look there first.)                │
    // │                                                                             │
    // │  3. Selection is POLLED off jt<bji>.SlotIndex (GodToolbarService.xat), NOT  │
    // │     hooked off the fav/faw/fax events. Those are obfuscated and their       │
    // │     meanings differ between game builds — the original GGG decompile had    │
    // │     faw=selected, but on FRUKT 0.01 the observed truth is:                  │
    // │        fav = item added · fax = SELECTED · faw = never fires                │
    // │     jt<a> is an interface, so its Item/SlotIndex members escaped            │
    // │     obfuscation and are the stable thing to build on.                       │
    // │                                                                             │
    // │  3b. What a slot SPAWNS is decided by the registration entry's m_prefab     │
    // │     (type kf), not by the bji payload. That is why an injected slot         │
    // │     currently equips a Viper — we clone the gun entry's prefab. Replacing   │
    // │     m_prefab is the lever for genuine custom behaviour.                     │
    // │                                                                             │
    // │  4. Anchor the injection on GodToolbarService.faf() (the service's own      │
    // │     init), not GodToolbarItemsPopulator.Start() — faf runs at the right     │
    // │     moment relative to qgj being built.                                     │
    // │                                                                             │
    // │  5. The spare slot view already exists in the scene                         │
    // │     (ToolbarView.m_slotsViews.Length == 5, with a KeySlot badge) but is     │
    // │     inactive: the native builder only activates slots it populated. It      │
    // │     has to be SetActive(true)'d and drawn via dtj(). No UI cloning or       │
    // │     manual layout is needed.                                                │
    // │                                                                             │
    // │  Caveat: the reused bji is one of the native tools, so selecting an         │
    // │  injected slot also equips that tool underneath. The original mod had the   │
    // │  same behaviour and worked around it in its WeaponSwapper.                  │
    // │                                                                             │
    // │  Usage (call from any mod's OnInitializeMelon, before scene load):          │
    // │                                                                             │
    // │    FruitToolbar.Register(new FruitToolbarItem {                             │
    // │        Name         = "My Weapon",                                          │
    // │        Icon         = mySprite,        // optional                          │
    // │        OnSelected   = idx => { /* slot selected */ },                       │
    // │        OnDeselected = idx => { /* slot left    */ },                        │
    // │    });                                                                      │
    // └─────────────────────────────────────────────────────────────────────────────┘

    public class FruitToolbarItem
    {
        /// <summary>
        /// Stable identifier, e.g. "BombsAway:Frag". Survives load-order changes, mod updates
        /// and a mod being temporarily removed — which an index does not. Slot assignments are
        /// keyed off this, so a future assignment UI can persist them meaningfully.
        /// Defaults to "{AssemblyName}:{Name}" if left unset.
        /// </summary>
        public string Id;

        public string Name = "Custom Item";
        public Sprite Icon;
        public Action<int> OnSelected;
        public Action<int> OnDeselected;

        internal int  SlotIndex = -1;
        internal bjo  IconData;     // native icon payload, used when drawing the slot
        internal bool Drawn;        // guards the draw pass — see EnsureSlotsVisible
    }

    public static class FruitToolbar
    {
        private static readonly List<FruitToolbarItem> _items = new List<FruitToolbarItem>();

        public static void Register(FruitToolbarItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            if (string.IsNullOrEmpty(item.Id))
            {
                string owner;
                try { owner = System.Reflection.Assembly.GetCallingAssembly().GetName().Name; }
                catch { owner = "Unknown"; }
                item.Id = owner + ":" + item.Name;
            }

            foreach (var existing in _items)
            {
                if (existing.Id != item.Id) continue;
                MelonLogger.Warning($"[FruitToolbar] duplicate id '{item.Id}' — ignoring the second registration.");
                return;
            }

            _items.Add(item);
            MelonLogger.Msg($"[FruitToolbar] Queued '{item.Name}' (id '{item.Id}')");
        }

        /// <summary>
        /// Optional ordering override, by item id. Ids listed here are placed first, in the order
        /// given; everything else follows in load order. Ordering rather than absolute indices,
        /// so the slot range always stays contiguous — a future assignment UI (or config page)
        /// only has to write this list.
        /// </summary>
        public static readonly List<string> PreferredOrder = new List<string>();

        /// <summary>Current slot index for an item id, or -1 if it has none yet.</summary>
        public static int GetSlot(string id)
        {
            foreach (var item in _items) if (item.Id == id) return item.SlotIndex;
            return -1;
        }

        // Deterministic and gap-free: PreferredOrder first, then registration (load) order.
        internal static void AssignSlots(int baseLen)
        {
            var ordered = new List<FruitToolbarItem>();

            foreach (var id in PreferredOrder)
                foreach (var item in _items)
                    if (item.Id == id && !ordered.Contains(item)) ordered.Add(item);

            foreach (var item in _items)
                if (!ordered.Contains(item)) ordered.Add(item);

            for (int i = 0; i < ordered.Count; i++) ordered[i].SlotIndex = baseLen + i;
        }

        internal static List<FruitToolbarItem> Items => _items;

        /// <summary>
        /// Builds a simple round icon sprite at runtime, for mods that don't ship art.
        /// Handy for prototyping — pass the result as <see cref="FruitToolbarItem.Icon"/>.
        /// </summary>
        public static Sprite MakeSolidIcon(Color color, int size = 64)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;

            float r = size * 0.5f, edge = r - 2f;
            var clear = new Color(0f, 0f, 0f, 0f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - r + 0.5f, dy = y - r + 0.5f;
                    float d  = Mathf.Sqrt(dx * dx + dy * dy);
                    // Slightly darker rim so the shape reads against any background.
                    tex.SetPixel(x, y, d > edge ? clear : (d > edge - 4f ? color * 0.6f : color));
                }
            }

            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>Spare native tool bji, captured from NativeGIITools.sxb.</summary>
        internal static bji SpareItem;

        /// <summary>Live service, captured during faf — used to poll the selected slot.</summary>
        internal static GodToolbarService Service;

        /// <summary>Number of vanilla slots, latched the first time we see the array.</summary>
        internal static int NativeBase = -1;

        private static ToolbarView _view;
        private static int _rebindCountdown;

        /// <summary>Called every frame from FruitLibMod.OnUpdate.</summary>
        internal static void Tick()
        {
            if (_items.Count == 0) return;

            if (_view == null)
            {
                // The toolbar UI is built during scene load, some frames after our
                // injection runs — poll for it rather than assuming it exists yet.
                if (--_rebindCountdown > 0) return;
                _rebindCountdown = 30;
                _view = UnityEngine.Object.FindObjectOfType<ToolbarView>(true);
                if (_view == null) return;
            }

            EnsureSlotsVisible();
            PollSelectionKeys();
            LogServiceOnce();
        }

        // Belt-and-braces selection source: watch the number keys directly.
        //
        // Reading selection out of the game has now failed three ways (faw patch, fax patch,
        // xaj/xat polling — the last produced not even a diagnostic line). But pressing 5
        // demonstrably selects the slot on screen, so the keypress itself is a signal we know
        // is real and that no amount of obfuscation churn can take away.
        private static void PollSelectionKeys()
        {
            for (int i = 0; i < 9; i++)
            {
                if (!Input.GetKeyDown(KeyCode.Alpha1 + i)) continue;
                SetSelected(i, "key");
                break;
            }
        }

        /// <summary>Drop cached scene objects so the next scene re-finds its own.</summary>
        internal static void ResetForScene()
        {
            _view = null;
            _rebindCountdown = 0;
            Service = null;
            _lastSelected = -1;
            _loggedResolution = false;
            _slotViewGrowthFailed = false;
            foreach (var item in _items) item.Drawn = false;
        }

        private static int  _lastSelected = -1;
        private static bool _loggedResolution;

        // Why selection is read from the KEYBOARD and not from the game:
        //
        //   - fav/faw/fax are obfuscated and their meanings differ between builds. An older
        //     decompile had faw=selected; on FRUKT 0.01 the observed truth is fav=item-added,
        //     fax=SELECTED, faw=never fires. Anything built on those names is a time bomb.
        //   - GodToolbarService.xat (jt<bji>.SlotIndex) looked ideal — jt<a> is an interface, so
        //     Item/SlotIndex escaped obfuscation. But in practice it reports 0 forever, even with
        //     slot 5 visibly selected, and actively fought the keyboard signal (dispatching
        //     4 -> 0 in the same millisecond as the keypress). Combined with every toolbar event
        //     firing twice, that means there is MORE THAN ONE GodToolbarService and neither
        //     ToolbarView.pth nor FindObjectOfType reliably yields the one driving the toolbar.
        //
        // Pressing 5 demonstrably selects the slot on screen, so the keypress is a signal we know
        // is real. Known gap: selection changed by other means (scroll wheel, clicking a slot)
        // is not detected. Fix that by identifying the correct service instance, not by
        // reinstating the xat poll.
        private static void LogServiceOnce()
        {
            if (_loggedResolution) return;
            ResolveService();   // logs once on the first successful resolve
        }

        // Single funnel for both selection sources, so whichever notices first wins and the
        // other is a no-op rather than a double-dispatch.
        private static void SetSelected(int now, string source)
        {
            if (now == _lastSelected) return;
            int previous = _lastSelected;
            _lastSelected = now;

            var left  = FindBySlot(previous);
            var enter = FindBySlot(now);
            if (left != null || enter != null)
                MelonLogger.Msg($"[FruitToolbar] selection {previous} -> {now} (via {source})");

            // Ask the game to select it for real. The scene's own spare slot (index 4) has a
            // working key binding so the game follows along by itself, but a slot view we cloned
            // inherits the prototype's binding — the game never sees its key, so it never selects
            // it, and without a real selection there is no highlight and no title. fac() is the
            // service's own "select slot" entry point, so the game does all of that natively.
            if (enter != null) SelectNatively(now);

            left?.OnDeselected?.Invoke(previous);
            enter?.OnSelected?.Invoke(now);
        }

        private static void SelectNatively(int slot)
        {
            var svc = ResolveService();
            if (svc == null) return;
            try { svc.fac(slot); }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] fac({slot}) failed: {e.Message}"); }
        }

        // Prefer the service the *visible* toolbar is bound to. Diagnostics showed every toolbar
        // event firing twice, which points at more than one GodToolbarService; caching whichever
        // one faf happened to see last risks polling a service nobody is driving.
        private static GodToolbarService ResolveService()
        {
            GodToolbarService svc = null;

            if (_view != null)
            {
                try { svc = _view.pth; } catch { }
            }
            if (svc == null) svc = UnityEngine.Object.FindObjectOfType<GodToolbarService>(true);
            if (svc == null) svc = Service;          // faf capture, last resort
            if (svc != null && !_loggedResolution) LogResolution(svc);

            return svc;
        }

        // One line, once — enough to diagnose a silent failure without another blind test round.
        private static void LogResolution(GodToolbarService svc)
        {
            _loggedResolution = true;
            try
            {
                int count = UnityEngine.Object.FindObjectsOfType<GodToolbarService>(true)?.Length ?? -1;
                string viewBound = _view != null && _view.pth != null ? "view.pth" : "FindObjectOfType/faf";

                string slotIdx = "n/a";
                try { var s = svc.xat; slotIdx = s == null ? "xat=null" : s.SlotIndex.ToString(); } catch (Exception e) { slotIdx = $"threw:{e.Message}"; }

                string qfx = "n/a", qga = "n/a";
                try { var q = svc.qgj; if (q != null) { qfx = q.qfx.ToString(); qga = q.qga.ToString(); } } catch { }

                MelonLogger.Msg($"[FruitToolbar] service via {viewBound} id={svc.GetInstanceID()} " +
                                $"instancesInScene={count} xat.SlotIndex={slotIdx} xai={svc.xai} xaj={svc.xaj} qfx={qfx} qga={qga}");
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] resolution log failed: {e.Message}"); }
        }

        // The scene ships exactly 5 ToolbarItemSlotView objects (4 vanilla + 1 spare), so one
        // extra slot is free. Beyond that we have to build the views ourselves — which is what
        // ToolbarView.ptf exists for: it is the prefab source for a slot view. Cloning it into
        // the same parent lets the existing layout group position it like any other slot.
        //
        // ptg (ToolbarView.ev[]) is grown alongside m_slotsViews because the view indexes the two
        // in parallel; ev is publicly constructible as ev(SlotDrawData, ToolbarItemSlotView).
        private static bool _slotViewGrowthFailed;

        private static void EnsureSlotViewCapacity(int needed)
        {
            if (_slotViewGrowthFailed) return;

            var slots = _view.m_slotsViews;
            if (slots == null || slots.Length >= needed) return;

            var proto = _view.ptf != null ? _view.ptf : (slots.Length > 0 ? slots[slots.Length - 1] : null);
            if (proto == null)
            {
                _slotViewGrowthFailed = true;
                MelonLogger.Warning($"[FruitToolbar] need {needed} slot views but have {slots.Length} and no prototype to clone — extra slots will not render.");
                return;
            }

            try
            {
                int old = slots.Length;
                var parent = proto.transform.parent != null ? proto.transform.parent : slots[old - 1].transform.parent;

                // ToolbarItemSlotView.ptc is a `bfc` — the SFX service (Whoosh/Impact/Weapon/Tools),
                // dependency-injected at runtime rather than serialized. A clone therefore has it
                // null, and ToolbarItemSlotView.dtf (the highlight setter) dereferences it, so
                // selecting a cloned slot threw an NRE deep in the view controller: the highlight
                // was left stale and the name display never updated. It's a shared service, not a
                // per-object component, so handing clones the same instance is the correct wiring.
                bfc sfx = null;
                for (int i = 0; i < old && sfx == null; i++)
                    if (slots[i] != null) sfx = slots[i].ptc;
                if (sfx == null) MelonLogger.Warning("[FruitToolbar] no SFX service found to seed cloned slot views — selecting them may throw.");

                var newSlots = new Il2CppReferenceArray<ToolbarItemSlotView>(needed);
                for (int i = 0; i < old; i++) newSlots[i] = slots[i];

                for (int i = old; i < needed; i++)
                {
                    var clone = UnityEngine.Object.Instantiate(proto, parent, false);
                    clone.gameObject.name = $"ToolbarItemSlotView ({i})";
                    if (sfx != null) clone.dtc(sfx);
                    newSlots[i] = clone;
                }
                _view.m_slotsViews = newSlots;

                var ptg = _view.ptg;
                if (ptg != null && ptg.Length < needed)
                {
                    var newPtg = new Il2CppReferenceArray<ToolbarView.ev>(needed);
                    for (int i = 0; i < ptg.Length; i++) newPtg[i] = ptg[i];
                    for (int i = ptg.Length; i < needed; i++)
                        newPtg[i] = new ToolbarView.ev(new ToolbarView.SlotDrawData(string.Empty, null), newSlots[i]);
                    _view.ptg = newPtg;
                }

                RecentreSlotRow(parent, slots[0], needed - old);

                MelonLogger.Msg($"[FruitToolbar] grew slot views {old} -> {needed}");
            }
            catch (Exception e)
            {
                _slotViewGrowthFailed = true;
                MelonLogger.Warning($"[FruitToolbar] could not grow slot views: {e.Message}");
            }
        }

        /// <summary>
        /// Set false if you'd rather position the widened toolbar yourself, or nudge it with
        /// <see cref="SlotRowOffset"/>.
        /// </summary>
        public static bool AutoCentreSlotRow = true;

        /// <summary>Extra nudge applied to the slot row after auto-centring.</summary>
        public static Vector2 SlotRowOffset = Vector2.zero;

        // The row grows rightward from a fixed left edge rather than expanding about its centre,
        // so every added slot pushes the whole toolbar off-centre by one slot width. Shift the
        // container back by half the width we added. A layout rebuild runs first so the group's
        // own spacing/sizing is settled before we measure.
        private static void RecentreSlotRow(Transform parent, ToolbarItemSlotView sample, int addedCount)
        {
            if (addedCount <= 0) return;

            var rt = parent != null ? parent.TryCast<RectTransform>() : null;
            if (rt == null) return;

            try
            {
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rt);

                float slotWidth = 0f;
                var sampleRt = sample != null ? sample.transform.TryCast<RectTransform>() : null;
                if (sampleRt != null) slotWidth = sampleRt.rect.width;
                if (slotWidth <= 0f) slotWidth = 64f;   // sane fallback

                float spacing = 0f;
                var group = parent.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
                if (group != null) spacing = group.spacing;

                float added = addedCount * (slotWidth + spacing);

                if (AutoCentreSlotRow) rt.anchoredPosition -= new Vector2(added * 0.5f, 0f);
                if (SlotRowOffset != Vector2.zero) rt.anchoredPosition += SlotRowOffset;

                MelonLogger.Msg($"[FruitToolbar] re-centred slot row: slotW={slotWidth} spacing={spacing} " +
                                $"shift={-added * 0.5f} anchoredPos={rt.anchoredPosition}");
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] re-centring slot row failed: {e.Message}"); }
        }

        private static FruitToolbarItem FindBySlot(int slot)
        {
            if (slot < 0) return null;
            foreach (var item in _items)
                if (item.SlotIndex == slot) return item;
            return null;
        }

        private static void EnsureSlotsVisible()
        {
            int highest = -1;
            foreach (var item in _items) if (item.SlotIndex > highest) highest = item.SlotIndex;
            if (highest >= 0) EnsureSlotViewCapacity(highest + 1);

            var slots = _view.m_slotsViews;
            if (slots == null) return;

            foreach (var item in _items)
            {
                int idx = item.SlotIndex;
                if (idx < 0 || idx >= slots.Length) continue;

                var slot = slots[idx];
                if (slot == null) continue;

                // Guard on "have we drawn it", NOT on activeInHierarchy. The scene's spare slot
                // view starts inactive, but a view we cloned from ptf starts ACTIVE — so an
                // active-check skipped cloned slots entirely and they rendered blank: no icon,
                // no label.
                if (item.Drawn && slot.gameObject.activeInHierarchy) continue;

                if (!slot.gameObject.activeInHierarchy) slot.gameObject.SetActive(true);
                _view.dtj(new ToolbarView.SlotDrawData(item.Name, item.IconData), idx);

                // A cloned view inherits the prototype's key label ("1"), so set the real one.
                try
                {
                    if (slot.m_keySlot != null) slot.m_keySlot.dvk((idx + 1).ToString());
                }
                catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] key label for slot {idx} failed: {e.Message}"); }

                item.Drawn = true;
                MelonLogger.Msg($"[FruitToolbar] Activated + drew slot {idx} ('{item.Name}')");
            }
        }
    }

    // ── Capture a reusable native item to back the injected slots ────────────────
    [HarmonyPatch(typeof(NativeGIITools), nameof(NativeGIITools.isj))]
    internal static class FruitToolbar_ToolsCapture
    {
        static void Postfix(NativeGIITools __instance)
        {
            try
            {
                if (__instance.sxb == null) return;
                FruitToolbar.SpareItem = __instance.sxb;
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] sxb capture failed: {e.Message}"); }
        }
    }

    // ── Grow the toolbar: registration entries, capacity, and the slot container ──
    [HarmonyPatch(typeof(GodToolbarService), nameof(GodToolbarService.faf))]
    internal static class FruitToolbar_FafPatch
    {
        static void Prefix(GodToolbarService __instance)
        {
            try
            {
                if (FruitToolbar.Items.Count == 0) return;

                var populator = UnityEngine.Object.FindObjectOfType<GodToolbarItemsPopulator>(true);
                if (populator == null) return;

                var items = populator.m_itemsToPopulate;
                if (items == null || items.Length == 0) return;

                // Latch the vanilla count once; on later runs the array may already
                // include our entries, which would corrupt the baseline.
                if (FruitToolbar.NativeBase < 0) FruitToolbar.NativeBase = items.Length;

                int baseLen = FruitToolbar.NativeBase;
                int target  = baseLen + FruitToolbar.Items.Count;

                // Assign unconditionally so indices stay valid even when the array work below is
                // skipped as already-done. Keyed off stable ids — see FruitToolbar.AssignSlots.
                FruitToolbar.AssignSlots(baseLen);

                __instance.m_capacity = target;
                var qgj = __instance.qgj;
                if (qgj != null) qgj.ezg(target);

                if (items.Length >= target) return; // entries already present

                // Only used now for sensible icon defaults — the entry itself is built from
                // scratch rather than cloned.
                SerializedGIIRegistrationData template = null;
                foreach (var e in items)
                    if (e != null && e.m_category != null) { template = e; break; }
                if (template == null) template = items[items.Length - 1];

                var arr = new Il2CppReferenceArray<SerializedGIIRegistrationData>(target);
                for (int i = 0; i < items.Length; i++) arr[i] = items[i];

                foreach (var fi in FruitToolbar.Items)
                {
                    var entry = BuildEntry(template, fi);
                    if (entry == null) continue;

                    if (entry.m_objectDescriptor != null) fi.IconData = entry.m_objectDescriptor.tch;
                    arr[fi.SlotIndex] = entry;

                    MelonLogger.Msg($"[FruitToolbar] Registered '{fi.Name}' (id '{fi.Id}') at slot {fi.SlotIndex}");
                }

                populator.m_itemsToPopulate = arr;
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] faf prefix failed: {e}"); }
        }

        static void Postfix(GodToolbarService __instance)
        {
            try
            {
                if (FruitToolbar.Items.Count == 0 || FruitToolbar.NativeBase < 0) return;

                FruitToolbar.Service = __instance;

                if (FruitToolbar.SpareItem == null)
                {
                    MelonLogger.Warning("[FruitToolbar] no spare bji captured — slots will draw but not select.");
                    return;
                }

                var qgj = __instance.qgj;
                var slots = qgj?.qfy;
                if (slots == null) return;

                int target = FruitToolbar.NativeBase + FruitToolbar.Items.Count;
                if (slots.Length >= target) return;

                // THE fix: qfy is the collection the service actually selects from.
                // Without this the extra slots exist visually but are unreachable.
                var arr = new Il2CppReferenceArray<hq<bji>>(target);
                for (int i = 0; i < slots.Length; i++) arr[i] = slots[i];
                for (int i = slots.Length; i < target; i++)
                    arr[i] = new hq<bji> { qbg = FruitToolbar.SpareItem, qbf = false };

                qgj.qfy = arr;
                qgj.qfx = target;

                MelonLogger.Msg($"[FruitToolbar] Slot container expanded {slots.Length} -> {target}.");
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] faf postfix failed: {e}"); }
        }

        // Build the whole registration entry from scratch. Nothing here is cloned from a
        // native asset, so there is no shared-state to corrupt and the name is genuinely ours
        // (SerializedGIIRegistrationData.xnz reads through to the descriptor's m_objectName).
        //
        // The important field is m_prefab: it is the GodInventoryItem (kf) the game instantiates
        // when the slot is selected, and cloning the gun's is why an injected slot has been
        // spawning a fully working Viper. We point it at an inert kf instead — see InertPrefab.
        private static SerializedGIIRegistrationData BuildEntry(
            SerializedGIIRegistrationData template, FruitToolbarItem fi)
        {
            try
            {
                var srcIcon = template != null && template.m_objectDescriptor != null
                    ? template.m_objectDescriptor.m_iconData
                    : null;

                var icon = ScriptableObject.CreateInstance<SerializedIconData>();
                icon.m_sprite = fi.Icon != null ? fi.Icon : (srcIcon != null ? srcIcon.m_sprite : null);
                icon.m_offset = srcIcon != null ? srcIcon.m_offset : Vector2.zero;
                icon.m_scale  = srcIcon != null ? srcIcon.m_scale  : Vector2.one;
                icon.m_color  = srcIcon != null ? srcIcon.m_color  : Color.white;
                icon.name     = fi.Name + " Icon";

                var desc = ScriptableObject.CreateInstance<SerializedObjectDescriptorWithIcon>();
                desc.m_objectName = fi.Name;
                desc.m_description = string.Empty;
                desc.m_iconData    = icon;
                desc.name          = fi.Name + " Descriptor";

                var category = ScriptableObject.CreateInstance<SerializedGodInventoryCategoryData>();
                category.m_id         = "FruitLib";
                category.m_descriptor = desc;
                category.name         = "FruitLib Category";

                var entry = ScriptableObject.CreateInstance<SerializedGIIRegistrationData>();
                entry.m_objectDescriptor = desc;
                entry.m_category         = category;
                entry.m_prefab           = InertPrefab();
                entry.name               = fi.Name;

                return entry;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FruitToolbar] building entry for '{fi.Name}' failed: {e}");
                return null;
            }
        }

        // `km : kf` is the only kf subclass in the assembly with no overrides, no fields and no
        // behaviour — a genuine no-op inventory item. (Cursor/Cutter are the obfuscated jw/jz,
        // both of which carry real behaviour.) Using it as m_prefab means selecting the slot
        // spawns something that does nothing, instead of a working weapon we then have to hunt
        // down and suppress every frame.
        private static km   _inert;
        private static bool _inertBuilt;

        private static km InertPrefab()
        {
            // Guard on a flag, not on `_inert != null`: the log showed this running once per
            // registered item, so the null check wasn't holding and every item leaked its own
            // template GameObject.
            if (_inertBuilt) return _inert;
            _inertBuilt = true;

            var go = new GameObject("FruitLib_InertGII");
            go.SetActive(false);                       // template, never itself in the scene
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;

            _inert = go.AddComponent<km>();

            // Worth knowing which of two things is actually giving us "nothing spawns": an inert
            // km, or a null m_prefab because AddComponent didn't take. Both produce the desired
            // result, but they are not the same mechanism.
            MelonLogger.Msg(_inert == null
                ? "[FruitToolbar] AddComponent<km> returned null — m_prefab stays null (nothing spawns)"
                : "[FruitToolbar] created inert GII prefab (km)");

            return _inert;
        }
    }

}
