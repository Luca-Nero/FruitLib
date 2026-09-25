using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2CppData.Icons;
using Il2CppData.Player.Inventory.God;
using Il2CppInfrastructure.Project.Registration.Native;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Il2CppPlayer.Appearances.God.InventoryItems;
using Il2CppPlayer.Appearances.God.Toolbar;
using Il2CppServices.Audio;
using Il2CppUI.Terminal;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>
    /// How a FruitItem becomes a real inventory item on the release build.
    ///
    /// The game's own pipeline, read out of the Ghidra export (GameData/RELEASE_Ghidra):
    ///
    ///   NativeGIIGroupHandler.RegisterGIIPrefab(SerializedGIIRegistrationData)
    ///     registers the prefab under PrefabID("Native", ObjectName + "GII"), snapshots the
    ///     registration into a GodInventoryItemData, and adds that to the group's HashSet.
    ///   NativeGodInventoryItemsRegistration.GodInventoryItemsData is a lazy SelectMany over
    ///     those sets, so anything added is live everywhere - there is no cache to refresh.
    ///   TerminalItemsService.ItemsOf(category) keeps items whose Category is that category
    ///     BY REFERENCE, and RefuseLayoutThatCannotBeDrawn throws if any registered item's
    ///     category is not one of the layout's. So a FruitItem must carry the game's own
    ///     category asset, never a look-alike - one foreign category takes the whole window
    ///     down.
    ///   TerminalSendKeys -> GodToolbarService.TryAddItemAt -> GAToolbarGIIItemsHandler.AddItem
    ///     -> GodInventoryItemFactory.Create(data.ID) instantiates the prefab per slot.
    ///
    /// GodInventoryItem is abstract, so the prefab carries <see cref="FruitHeldItem"/>, a
    /// subclass injected into IL2CPP that implements the two abstract members and forwards
    /// the mouse virtuals to the FruitItem's callbacks.
    /// </summary>
    internal static class FruitInventoryNative
    {
        private static NativeGodInventoryItemsRegistration _registry;

        // The game's category assets. Harvested from the native items at boot; replaced by the
        // layout's own list as soon as a TerminalItemsService shows us one, since that list is
        // the one the window validates against.
        private static readonly List<SerializedGodInventoryCategoryData> _categories =
            new List<SerializedGodInventoryCategoryData>();
        private static bool _layoutKnown;
        private static bool _loggedCategories;

        private static GameObject _templateRoot;
        private static bool _injected;
        private static bool _injectionFailed;

        /// <summary>Live in-hand instances, by native pointer.</summary>
        private sealed class Live
        {
            public FruitItem        Item;
            public GodInventoryItem Instance;
            public int              Slot;
        }
        private static readonly Dictionary<IntPtr, Live> _live = new Dictionary<IntPtr, Live>();

        // ── Registry and categories ───────────────────────────────────────────────

        internal static void OnRegistryReady(NativeGodInventoryItemsRegistration registry)
        {
            if (registry == null) return;
            _registry = registry;
            HarvestNativeCategories();
            RegisterPending();
        }

        internal static void OnLayout(TerminalItemsService terminal)
        {
            if (terminal == null) return;

            if (_registry == null)
            {
                try { _registry = terminal.RegisteredItems?.TryCast<NativeGodInventoryItemsRegistration>(); }
                catch (Exception e) { MelonLogger.Warning($"[FruitInventory] reading the terminal's registry failed: {e.Message}"); }
            }

            SerializedGodInventoryLayout layout = null;
            try { layout = terminal.Layout; }
            catch (Exception e) { MelonLogger.Warning($"[FruitInventory] reading the terminal's layout failed: {e.Message}"); }

            var cats = layout?.m_categories;
            if (cats != null && cats.Length > 0)
            {
                _categories.Clear();
                foreach (var c in cats) AddCategory(c);
                if (!_layoutKnown) _loggedCategories = false;   // say it again, now authoritative
                _layoutKnown = true;
                LogCategories("layout");
            }

            RepairCategories();
            RegisterPending();
        }

        private static void HarvestNativeCategories()
        {
            try
            {
                var w = _registry.m_weapons;
                if (w != null) { Harvest(w.m_viper17); Harvest(w.m_lynxF); Harvest(w.m_grist03); }

                var t = _registry.m_tools;
                if (t != null) { Harvest(t.m_cursor); Harvest(t.m_cutter); Harvest(t.m_humanSpawner); }

                var p = _registry.m_props?.m_props;
                if (p != null) foreach (var r in p) Harvest(r);
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitInventory] reading native categories failed: {e.Message}"); }

            // Anything already loaded, which picks up a shelf no native item happens to sit on.
            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<SerializedGodInventoryCategoryData>());
                if (all != null)
                    foreach (var o in all) AddCategory(o?.TryCast<SerializedGodInventoryCategoryData>());
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitInventory] scanning category assets failed: {e.Message}"); }

            LogCategories("native items");
        }

        private static void Harvest(SerializedGIIRegistrationData r)
        {
            try { if (r != null) AddCategory(r.m_category); }
            catch { /* one broken native entry is not our problem */ }
        }

        private static void AddCategory(SerializedGodInventoryCategoryData c)
        {
            if (c == null) return;
            foreach (var known in _categories) if (known.Pointer == c.Pointer) return;
            _categories.Add(c);
        }

        private static void LogCategories(string source)
        {
            if (_loggedCategories) return;
            _loggedCategories = true;

            var sb = new StringBuilder($"[FruitInventory] {_categories.Count} categor{(_categories.Count == 1 ? "y" : "ies")} from {source}:");
            foreach (var c in _categories) sb.Append($" [{Label(c)} = {FruitInventory.Canonical(Label(c))}]");
            MelonLogger.Msg(sb.ToString());
        }

        internal static IReadOnlyList<string> CategoryNames()
        {
            var names = new List<string>();
            foreach (var c in _categories) names.Add(Label(c));
            return names;
        }

        /// <summary>The shelf's display name, falling back to its asset name.</summary>
        private static string Label(SerializedGodInventoryCategoryData c)
        {
            try
            {
                string shown = c.ObjectName;
                if (!string.IsNullOrEmpty(shown)) return shown;
            }
            catch { }

            try { return c.name; } catch { return "?"; }
        }

        /// <summary>
        /// Every spelling a category answers to: its display name, its asset name and its id,
        /// each folded by <see cref="FruitInventory.Canonical"/>. On the release build the
        /// assets are WeaponCategory / ToolsCategory / PropsCategory / EtcCategory with display
        /// names Weapons / Tools / Props / Etc; the ids ship empty, so they are the last resort.
        /// </summary>
        private static IEnumerable<string> KeysOf(SerializedGodInventoryCategoryData c)
        {
            string shown = null, asset = null, id = null;
            try { shown = c.ObjectName; } catch { }
            try { asset = c.name;       } catch { }
            try { id    = c.m_id;       } catch { }

            if (!string.IsNullOrEmpty(shown)) yield return FruitInventory.Canonical(shown);
            if (!string.IsNullOrEmpty(asset)) yield return FruitInventory.Canonical(asset);
            if (!string.IsNullOrEmpty(id))    yield return FruitInventory.Canonical(id);
        }

        private static SerializedGodInventoryCategoryData Resolve(string wanted)
        {
            string key = FruitInventory.Canonical(wanted);
            if (key.Length == 0) return null;

            foreach (var c in _categories)
                foreach (var k in KeysOf(c))
                    if (k == key) return c;

            return null;
        }

        // ── Registration ──────────────────────────────────────────────────────────

        internal static void RegisterPending()
        {
            if (_registry == null) return;

            foreach (var item in FruitInventory.Items)
                if (!item.IsRegistered && !item.Failed) TryRegister(item);
        }

        private static void TryRegister(FruitItem item)
        {
            var category = Resolve(item.Category);
            if (category == null)
            {
                // Before the layout is known an unmatched name may just be a shelf that is not
                // loaded yet - wait for the window. Once it is known, the name is wrong.
                if (!_layoutKnown) return;

                category = Resolve(nameof(FruitItemCategory.Etc));
                if (category == null && _categories.Count > 0) category = _categories[_categories.Count - 1];
                if (category == null) return;

                MelonLogger.Warning($"[FruitInventory] {item} asks for category '{item.Category}', which this build " +
                                    $"does not have (it has: {string.Join(", ", CategoryNames())}). Filed under {Label(category)}.");
            }

            try
            {
                string key = FruitInventory.Canonical(Label(category));

                // Which group holds the item changes nothing the player sees - the window
                // filters by category - but it keeps the registry tidy for anyone reading it.
                NativeGIIGroupHandler group =
                    key == "weapon" ? (NativeGIIGroupHandler)_registry.m_weapons :
                    key == "tool"   ? (NativeGIIGroupHandler)_registry.m_tools   : null;
                if (group == null) group = _registry.m_props;
                if (group == null) { MelonLogger.Warning($"[FruitInventory] {item}: the registry has no item groups."); item.Failed = true; return; }

                item.InternalName = InternalName(item);

                var template = BuildTemplate(item);
                if (template == null) { item.Failed = true; return; }

                var descriptor = BuildDescriptor(item);

                var registration = ScriptableObject.CreateInstance<SerializedGIIRegistrationData>();
                registration.m_objectDescriptor = descriptor;
                registration.m_category         = category;
                registration.m_prefab           = template;
                registration.name               = item.InternalName;
                registration.hideFlags          = HideFlags.HideAndDontSave;

                // Registered under the internal name, which becomes the PrefabID and so must be
                // unique and free of '_' (PrefabID.Validate refuses underscores). The display
                // name goes in afterwards, so two mods can both call an item "Frag".
                var data = group.RegisterGIIPrefab(registration);
                if (data == null) { MelonLogger.Warning($"[FruitInventory] {item}: the game returned no item data."); item.Failed = true; return; }

                item.Data         = data;
                item.Concrete     = data.TryCast<GodInventoryItemData>();
                item.Descriptor   = descriptor;
                item.Registration = registration;
                item.ResolvedCategory = Label(category);

                ApplyName(item);

                MelonLogger.Msg($"[FruitInventory] registered {item} on '{item.ResolvedCategory}' " +
                                $"(prefab {item.InternalName}GII, {descriptor.m_freeRows?.Count ?? 0} card row(s))");
            }
            catch (Exception e)
            {
                item.Failed = true;
                MelonLogger.Warning($"[FruitInventory] registering {item} failed: {e}");
            }
        }

        private static string InternalName(FruitItem item)
        {
            var sb = new StringBuilder("FruitLib");
            foreach (char c in item.Id) if (c < 128 && char.IsLetterOrDigit(c)) sb.Append(c);

            string name = sb.ToString(), candidate = name;
            for (int n = 2; FruitInventory.ByInternalName(candidate) != null; n++) candidate = name + n;
            return candidate;
        }

        private static SerializedItemDescriptor BuildDescriptor(FruitItem item)
        {
            var icon = ScriptableObject.CreateInstance<SerializedIconData>();
            icon.m_sprite  = item.Icon != null ? item.Icon : FruitIcons.Placeholder();
            icon.m_offset  = Vector2.zero;
            icon.m_scale   = Vector2.one;
            icon.m_color   = Color.white;
            icon.name      = item.InternalName + " Icon";
            icon.hideFlags = HideFlags.HideAndDontSave;

            var reserved   = new HashSet<string>();
            var descriptor = BuildTypedDescriptor(item, reserved);
            descriptor.m_objectName  = item.InternalName;
            descriptor.m_description = item.Description ?? "";
            descriptor.m_iconData    = icon;
            descriptor.m_freeRows    = BuildRows(item, reserved);
            descriptor.name          = item.InternalName + " Descriptor";
            descriptor.hideFlags     = HideFlags.HideAndDontSave;
            return descriptor;
        }

        /// <summary>
        /// The descriptor type the item's card asks for, built the way native items are.
        ///
        /// SerializedWeaponDescriptor and SerializedPropDescriptor supply their rows (caliber,
        /// fire mode / size) as RequiredRows, ahead of the free ones, and the game throws on a
        /// required row without a value - so a half-filled weapon card gets "-" rather than
        /// costing the item. The required keys go into <paramref name="reserved"/>: a free row
        /// repeating one of them is the other thing the game throws over.
        /// </summary>
        private static SerializedItemDescriptor BuildTypedDescriptor(FruitItem item, HashSet<string> reserved)
        {
            bool weapon = !string.IsNullOrWhiteSpace(item.Caliber) || !string.IsNullOrWhiteSpace(item.FireMode);

            if (weapon)
            {
                var d = ScriptableObject.CreateInstance<SerializedWeaponDescriptor>();
                d.m_caliber  = Required(item, item.Caliber,  "caliber");
                d.m_fireMode = Required(item, item.FireMode, "fire mode");
                Reserve(reserved, () => SerializedWeaponDescriptor.CALIBER_KEY);
                Reserve(reserved, () => SerializedWeaponDescriptor.FIRE_MODE_KEY);
                return d;
            }

            if (!string.IsNullOrWhiteSpace(item.Size))
            {
                var d = ScriptableObject.CreateInstance<SerializedPropDescriptor>();
                d.m_size = item.Size;
                Reserve(reserved, () => SerializedPropDescriptor.SIZE_KEY);
                return d;
            }

            return ScriptableObject.CreateInstance<SerializedItemDescriptor>();
        }

        private static string Required(FruitItem item, string value, string what)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
            MelonLogger.Warning($"[FruitInventory] {item} has a weapon card without a {what}; showing '-'.");
            return "-";
        }

        private static void Reserve(HashSet<string> reserved, Func<string> key)
        {
            try
            {
                string k = key();
                if (!string.IsNullOrEmpty(k)) reserved.Add(k);
            }
            catch { /* a stripped constant costs the duplicate check for that key, nothing else */ }
        }

        /// <summary>
        /// The card rows, pre-checked against the rules ItemCardRows enforces by throwing:
        /// a key and a value on every row, no key twice. A bad row costs that row here rather
        /// than the whole item there.
        /// </summary>
        private static Il2CppSystem.Collections.Generic.List<ItemCardRow> BuildRows(FruitItem item, HashSet<string> reserved)
        {
            var rows = new Il2CppSystem.Collections.Generic.List<ItemCardRow>();
            var seen = new HashSet<string>(reserved);

            foreach (var kv in item.Stats)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                {
                    MelonLogger.Warning($"[FruitInventory] {item}: card row '{kv.Key}' = '{kv.Value}' needs both a key and a value; dropped.");
                    continue;
                }
                if (!seen.Add(kv.Key))
                {
                    MelonLogger.Warning($"[FruitInventory] {item}: card row '{kv.Key}' appears twice " +
                                        "(or repeats a caliber / fire mode / size row); keeping the first.");
                    continue;
                }
                rows.Add(new ItemCardRow(kv.Key, kv.Value));
            }

            return rows;
        }

        /// <summary>
        /// The prefab the factory clones into a toolbar slot. It lives under an inactive,
        /// never-unloaded root, so the template itself never wakes up; the clones the factory
        /// parents into the player's rig do.
        /// </summary>
        private static FruitHeldItem BuildTemplate(FruitItem item)
        {
            if (!EnsureInjected()) return null;

            if (_templateRoot == null)
            {
                _templateRoot = new GameObject("FruitLib Inventory Templates");
                _templateRoot.SetActive(false);
                Object.DontDestroyOnLoad(_templateRoot);
            }

            var go = new GameObject(item.InternalName);
            go.transform.SetParent(_templateRoot.transform, false);
            var held = go.AddComponent<FruitHeldItem>();
            FruitInventoryAudit.AuditOnce(held);

            if (item.Model != null)
            {
                try
                {
                    var model = Object.Instantiate(item.Model, go.transform, false);
                    model.name = item.Model.name;
                    model.SetActive(true);
                }
                catch (Exception e) { MelonLogger.Warning($"[FruitInventory] {item}: cloning its model failed: {e.Message}"); }
            }

            return held;
        }

        private static bool EnsureInjected()
        {
            if (_injected) return true;
            if (_injectionFailed) return false;

            try
            {
                if (!ClassInjector.IsTypeRegisteredInIl2Cpp<FruitHeldItem>())
                    ClassInjector.RegisterTypeInIl2Cpp<FruitHeldItem>();
                FruitInventoryGc.FixClass();   // before the first instance is allocated
                _injected = true;
                return true;
            }
            catch (Exception e)
            {
                _injectionFailed = true;
                MelonLogger.Error($"[FruitInventory] could not inject the held-item type; no custom item can be registered: {e}");
                return false;
            }
        }

        // ── Display ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes the display name everywhere the game reads it from. GodInventoryItemData
        /// copies the registration's fields when it is built, so the copy has to be written
        /// as well as the descriptor.
        /// </summary>
        private static void ApplyName(FruitItem item)
        {
            if (item.Descriptor != null) item.Descriptor.m_objectName = item.Name;
            if (item.Concrete   != null) item.Concrete._ObjectName_k__BackingField = item.Name;
        }

        internal static void RefreshDisplay(FruitItem item)
        {
            try
            {
                ApplyName(item);

                if (item.Icon != null && item.Descriptor != null)
                {
                    var icon = item.Descriptor.m_iconData;
                    if (icon != null) icon.m_sprite = item.Icon;
                    if (item.Concrete != null) item.Concrete._IconData_k__BackingField = item.Descriptor.IconData;
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitInventory] refreshing {item} failed: {e.Message}"); }
        }

        /// <summary>
        /// Makes sure every registered item's category is one of the layout's own instances,
        /// before the window validates. A category resolved at boot could in principle be a
        /// second copy of the same asset; the window would then refuse to draw at all.
        /// </summary>
        private static void RepairCategories()
        {
            if (!_layoutKnown) return;

            foreach (var item in FruitInventory.Items)
            {
                if (!item.IsRegistered || item.Registration == null) continue;

                try
                {
                    var current = item.Registration.m_category;
                    bool inLayout = false;
                    if (current != null)
                        foreach (var c in _categories) if (c.Pointer == current.Pointer) { inLayout = true; break; }
                    if (inLayout) continue;

                    var fixedUp = Resolve(item.ResolvedCategory ?? item.Category) ?? Resolve(nameof(FruitItemCategory.Etc));
                    if (fixedUp == null) continue;

                    item.Registration.m_category = fixedUp;
                    if (item.Concrete != null)
                        item.Concrete._Category_k__BackingField = new IGodInventoryCategoryData(fixedUp.Pointer);
                    item.ResolvedCategory = Label(fixedUp);

                    MelonLogger.Msg($"[FruitInventory] {item} re-pointed at the layout's '{item.ResolvedCategory}' category.");
                }
                catch (Exception e) { MelonLogger.Warning($"[FruitInventory] checking {item}'s category failed: {e.Message}"); }
            }
        }

        // ── In-hand instances ─────────────────────────────────────────────────────

        internal static void OnAdded(GAToolbarGIIItemsHandler handler, IGodInventoryItemData data, int index)
        {
            var item = FruitInventory.ByData(data);
            if (item == null) return;

            GodInventoryItem instance = null;
            try { handler.TryGetItem(index, out instance); }
            catch (Exception e) { MelonLogger.Warning($"[FruitInventory] finding {item} in slot {index} failed: {e.Message}"); }
            if (instance == null) return;

            FruitHeldItem.EnsureConstructed(instance);
            FruitInventoryGc.Pin(instance, "on toolbar");
            _live[instance.Pointer] = new Live { Item = item, Instance = instance, Slot = index };
            MelonLogger.Msg($"[FruitInventory] {item} put on toolbar slot {index}");
        }

        internal static void OnRemoving(GAToolbarGIIItemsHandler handler, int index)
        {
            GodInventoryItem instance = null;
            try { handler.TryGetItem(index, out instance); }
            catch { return; }
            if (instance == null) return;

            Forget(instance);
        }

        internal static void Forget(GodInventoryItem instance)
        {
            if (!_live.TryGetValue(instance.Pointer, out var live)) return;
            _live.Remove(instance.Pointer);

            if (live.Item.HeldInstance == instance.Pointer) FruitInventory.Deselected(live.Item);
        }

        internal static void OnSelect(GodInventoryItem instance)
        {
            var live = Find(instance);
            if (live != null) FruitInventory.Selected(live.Item, instance.gameObject, instance.Pointer, live.Slot);
        }

        internal static void OnDeselect(GodInventoryItem instance)
        {
            var live = Find(instance);
            if (live != null && live.Item.HeldInstance == instance.Pointer) FruitInventory.Deselected(live.Item);
        }

        /// <summary>
        /// The FruitItem behind an instance. Normally a dictionary hit from the AddItem hook;
        /// the name is the fallback for an instance that reached the hand some other way. The
        /// factory names a clone "&lt;InternalName&gt;(Clone)_&lt;n&gt;", and internal names hold
        /// neither '(' nor '_', so the prefix up to either is the internal name exactly.
        /// </summary>
        internal static FruitItem ItemFor(GodInventoryItem instance) => Find(instance)?.Item;

        private static Live Find(GodInventoryItem instance)
        {
            if (instance == null) return null;
            if (_live.TryGetValue(instance.Pointer, out var live)) return live;

            string name;
            try { name = instance.gameObject.name; } catch { return null; }
            int cut = name.IndexOfAny(new[] { '(', '_' });
            var item = FruitInventory.ByInternalName(cut < 0 ? name : name.Substring(0, cut));
            if (item == null) return null;

            live = new Live { Item = item, Instance = instance, Slot = -1 };
            _live[instance.Pointer] = live;
            return live;
        }

        internal static void PruneDeadInstances()
        {
            var dead = new List<IntPtr>();
            foreach (var kv in _live) if (kv.Value.Instance == null) dead.Add(kv.Key);
            foreach (var ptr in dead) _live.Remove(ptr);
        }
    }

    /// <summary>
    /// The component on every FruitLib inventory item. Injected into IL2CPP so it can derive
    /// from the abstract GodInventoryItem: the injector matches these overrides to the base
    /// vtable by name, which is also how the game's input reaches the mouse handlers - it
    /// calls them on the held item only, so nothing here has to check for menus or the
    /// inventory window being open.
    /// </summary>
    public class FruitHeldItem : GodInventoryItem
    {
        public FruitHeldItem(IntPtr ptr) : base(ptr) => EnsureConstructed(this);

        /// <summary>
        /// What every native item's constructor does, and an injected one does not.
        ///
        /// When Unity creates this component - AddComponent on the template, Instantiate for
        /// every toolbar copy - Il2CppInterop runs only the managed (IntPtr) constructor.
        /// GodInventoryItem has no constructor of its own worth running: each concrete class
        /// (LynxFGodInventoryItem..ctor, PropGodInventoryItem..ctor) creates the Features list
        /// itself, next to a few defaults of its own. ActivateLogic, DeactivateLogic,
        /// OnLateEnable, OnLateDisable and BeforeDestroy all iterate that list, so without it
        /// equip threw and unequip crashed. ManagedBehaviour and RegistrableBehaviour
        /// initialise nothing in theirs.
        ///
        /// Do not invoke GodInventoryItem's native .ctor here instead: running it on an object
        /// Unity has already created hard-crashes the game on equip.
        /// </summary>
        [HideFromIl2Cpp]
        internal static void EnsureConstructed(GodInventoryItem item)
        {
            try
            {
                if (item != null && item._Features_k__BackingField == null)
                {
                    item._Features_k__BackingField = new Il2CppSystem.Collections.Generic.List<GIIFeature>();
                    FruitTrace.Mark($"FruitHeldItem {item.Pointer.ToInt64():X}: constructed, Features created");
                    FruitInventoryGc.Pin(item, "constructed");
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitInventory] initialising a held item failed: {e.Message}"); }
        }

        public override ISFXPlayerService SfxPlayerService
        {
            get
            {
                FruitTrace.Mark("FruitHeldItem.SfxPlayerService read");
                return FruitSfx.AsInterface;
            }
        }

        public override void OnDestroyInternal()
        {
            FruitTrace.Mark($"FruitHeldItem {Pointer.ToInt64():X}: OnDestroyInternal");
            FruitInventoryNative.Forget(this);
        }

        public override void OnLeftMouseButtonClick()             => Fire(i => i.OnPrimary,          nameof(FruitItem.OnPrimary));
        public override void OnLeftMouseButtonHold(float time)    => Fire(i => i.OnPrimaryHold, time, nameof(FruitItem.OnPrimaryHold));
        public override void OnLeftMouseButtonRelease()           => Fire(i => i.OnPrimaryRelease,   nameof(FruitItem.OnPrimaryRelease));
        public override void OnRightMouseButtonClick()            => Fire(i => i.OnSecondary,        nameof(FruitItem.OnSecondary));
        public override void OnRightMouseButtonHold(float time)   => Fire(i => i.OnSecondaryHold, time, nameof(FruitItem.OnSecondaryHold));
        public override void OnRightMouseButtonRelease()          => Fire(i => i.OnSecondaryRelease, nameof(FruitItem.OnSecondaryRelease));
        public override void OnMiddleMouseButtonClick()           => Fire(i => i.OnMiddle,           nameof(FruitItem.OnMiddle));
        public override void OnMiddleMouseButtonScroll(float n)   => Fire(i => i.OnScroll, n,        nameof(FruitItem.OnScroll));

        [HideFromIl2Cpp]
        private void Fire(Func<FruitItem, Action<FruitItem>> pick, string what)
        {
            var item = FruitInventoryNative.ItemFor(this);
            if (item != null) FruitInventory.Invoke(pick(item), item, what);
        }

        [HideFromIl2Cpp]
        private void Fire(Func<FruitItem, Action<FruitItem, float>> pick, float value, string what)
        {
            var item = FruitInventoryNative.ItemFor(this);
            if (item != null) FruitInventory.Invoke(pick(item), item, value, what);
        }
    }

    // ── Hooks ─────────────────────────────────────────────────────────────────────
    //
    // Each target has a body of its own. That matters here: the empty GodInventoryItem mouse
    // virtuals are one folded IL2CPP stub shared by ~12k methods (CallerCount 12446), and
    // patching one of those would hook all of them. FruitHeldItem overrides them instead.

    /// <summary>
    /// Boot: the props group registers last (GetObjectsGroups is weapons, tools, props), so
    /// every native item and its category exists when this returns.
    /// </summary>
    [HarmonyPatch(typeof(NativeGIIProps), nameof(NativeGIIProps.Register))]
    internal static class FruitInventory_BootPatch
    {
        static void Postfix(NativeGIIProps __instance)
        {
            try
            {
                NativeGodInventoryItemsRegistration registry = null;
                try { registry = __instance.m_spawnablesRegistration?.TryCast<NativeGodInventoryItemsRegistration>(); }
                catch { }
                if (registry == null) registry = FruitScene.First<NativeGodInventoryItemsRegistration>();

                if (registry == null) { MelonLogger.Warning("[FruitInventory] props registered but no item registry found; waiting for the inventory window."); return; }
                FruitInventoryNative.OnRegistryReady(registry);
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitInventory] boot hook failed: {e}"); }
        }
    }

    /// <summary>
    /// The inventory window's own check, run before it draws anything. The prefix is the
    /// last moment an item can be added or have its category corrected before the window
    /// decides whether it can draw at all.
    /// </summary>
    [HarmonyPatch(typeof(TerminalItemsService), nameof(TerminalItemsService.RefuseLayoutThatCannotBeDrawn))]
    internal static class FruitInventory_LayoutPatch
    {
        static void Prefix(TerminalItemsService __instance)
        {
            try { FruitInventoryNative.OnLayout(__instance); }
            catch (Exception e) { MelonLogger.Warning($"[FruitInventory] layout hook failed: {e}"); }
        }
    }

    [HarmonyPatch(typeof(GAToolbarGIIItemsHandler), nameof(GAToolbarGIIItemsHandler.AddItem))]
    internal static class FruitInventory_AddItemPatch
    {
        static void Postfix(GAToolbarGIIItemsHandler __instance, IGodInventoryItemData data, int index)
        {
            try { FruitInventoryNative.OnAdded(__instance, data, index); }
            catch (Exception e) { MelonLogger.Warning($"[FruitInventory] AddItem hook failed: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(GAToolbarGIIItemsHandler), nameof(GAToolbarGIIItemsHandler.RemoveItem))]
    internal static class FruitInventory_RemoveItemPatch
    {
        static void Prefix(GAToolbarGIIItemsHandler __instance, int index)
        {
            try { FruitInventoryNative.OnRemoving(__instance, index); }
            catch (Exception e) { MelonLogger.Warning($"[FruitInventory] RemoveItem hook failed: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(GAToolbarSelectedItemProcessor), nameof(GAToolbarSelectedItemProcessor.OnItemSelect))]
    internal static class FruitInventory_SelectPatch
    {
        static void Postfix(GodInventoryItem item)
        {
            try { FruitInventoryNative.OnSelect(item); }
            catch (Exception e) { MelonLogger.Warning($"[FruitInventory] select hook failed: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(GAToolbarSelectedItemProcessor), nameof(GAToolbarSelectedItemProcessor.OnItemDeselect))]
    internal static class FruitInventory_DeselectPatch
    {
        static void Postfix(GodInventoryItem item)
        {
            try { FruitInventoryNative.OnDeselect(item); }
            catch (Exception e) { MelonLogger.Warning($"[FruitInventory] deselect hook failed: {e.Message}"); }
        }
    }

    // ── Pin before every enable / disable ─────────────────────────────────────────
    //
    // Appear and disappear are where SetActive runs, and with it OnEnable / OnDisable, which
    // invoke the delegates and walk the Features list FruitInventoryGc keeps alive. Pinning
    // again just before picks up anything the game assigned since the last time.

    [HarmonyPatch(typeof(GAToolbarSelectedItemProcessor), nameof(GAToolbarSelectedItemProcessor.OnAppearItem))]
    internal static class FruitInventory_PinOnAppear
    {
        static void Prefix(GodInventoryItem item) { if (FruitInventoryNative.ItemFor(item) != null) FruitInventoryGc.Pin(item, "appear"); }
    }

    [HarmonyPatch(typeof(GAToolbarSelectedItemProcessor), nameof(GAToolbarSelectedItemProcessor.OnDisappearItem))]
    internal static class FruitInventory_PinOnDisappear
    {
        static void Prefix(GodInventoryItem item) { if (FruitInventoryNative.ItemFor(item) != null) FruitInventoryGc.Pin(item, "disappear"); }
    }

    [HarmonyPatch(typeof(GAToolbarSelectedItemProcessor), nameof(GAToolbarSelectedItemProcessor.OnItemDeselect))]
    internal static class FruitInventory_PinOnDeselect
    {
        static void Prefix(GodInventoryItem item) { if (FruitInventoryNative.ItemFor(item) != null) FruitInventoryGc.Pin(item, "deselect"); }
    }
}
