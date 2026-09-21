using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Text;
using HarmonyLib;
using Il2CppData.Icons;
using Il2CppData.Objects;
using Il2CppData.Player.Inventory.God;
using Il2CppInfrastructure.Project.AssetsHandlers.SFX;
using Il2CppInfrastructure.Project.Registration.Native;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppPlayer.Appearances.God.InventoryItems;
using Il2CppPlayer.Appearances.God.Toolbar;
using Il2CppTripledoseLibs.Primitives.Patterns;
using Il2CppViews.Toolbar;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    public class FruitToolbarItem
    {
        public string Id;

        public string Name = "Custom Item";
        public Sprite Icon;
        public Action<int> OnSelected;
        public Action<int> OnDeselected;

        internal int       SlotIndex = -1;
        internal IIconData IconData;   // native icon payload, used when drawing the slot
        internal bool      Drawn;      // guards the draw pass — see EnsureSlotsVisible
        internal bool      DrawBroken; // the view refused this slot; stop retrying it
        internal int       Settling;   // frames the slot view has been live — see EnsureSlotsVisible
        internal bool      IconLogged; // the icon has been described in the log once

        internal SerializedObjectDescriptorWithIcon Descriptor;

        public void SetDisplay(string name, Sprite icon = null)
        {
            if (!string.IsNullOrEmpty(name)) Name = name;
            if (icon != null) Icon = icon;
            FruitToolbar.RefreshDisplay(this);
        }
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

        public static readonly List<string> PreferredOrder = new List<string>();

        /// <summary>
        /// Force the first slot custom items may use, instead of working it out.
        ///
        /// Leave at -1 unless FruitLib has guessed wrong - the log line tagged
        /// "native base" says what it decided and why. Set this before the toolbar
        /// initialises, so from your mod's OnInitializeMelon.
        /// </summary>
        public static int NativeBaseOverride = -1;

        public static int GetSlot(string id)
        {
            foreach (var item in _items) if (item.Id == id) return item.SlotIndex;
            return -1;
        }

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
        /// A sprite from a PNG embedded in your mod, for a toolbar slot.
        ///
        /// Mirrors how meshes are shipped: add the file to your csproj as an
        /// <c>EmbeddedResource</c> and name it here. Matching is by suffix, so
        /// "AK.png" finds "MyMod.Icons.AK.png" without you having to know how the
        /// compiler mangled the folder into the resource name.
        ///
        /// <code>
        /// Icon = FruitToolbar.LoadIcon(Assembly.GetExecutingAssembly(), "Icons/AK.png");
        /// </code>
        ///
        /// Returns null and logs if the resource is missing or is not a readable image,
        /// which leaves the slot on FruitLib's placeholder disc rather than undrawn.
        /// </summary>
        public static Sprite LoadIcon(Assembly assembly, string resourceName,
                                      FilterMode filter = FilterMode.Bilinear)
        {
            if (assembly == null || string.IsNullOrEmpty(resourceName)) return null;

            try
            {
                // Folder separators become dots in a manifest name, so the caller can write
                // the path the way it appears in their project and still be found.
                string wanted = resourceName.Replace('/', '.').Replace('\\', '.');

                string found = null;
                int    hits  = 0;
                foreach (var name in assembly.GetManifestResourceNames())
                {
                    if (!name.Equals(wanted, StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith("." + wanted, StringComparison.OrdinalIgnoreCase)) continue;

                    if (hits++ == 0) found = name;
                }

                if (found == null)
                {
                    MelonLogger.Warning($"[FruitToolbar] no embedded resource matching '{resourceName}' in " +
                                        $"{assembly.GetName().Name}. Is it marked as an EmbeddedResource?");
                    return null;
                }

                if (hits > 1)
                    MelonLogger.Warning($"[FruitToolbar] '{resourceName}' matches {hits} resources in " +
                                        $"{assembly.GetName().Name}; using '{found}'. Give the name more of its path.");

                byte[] png;
                using (var stream = assembly.GetManifestResourceStream(found))
                {
                    if (stream == null) return null;

                    // Read to the end rather than trusting one Read to fill the buffer - a
                    // manifest stream is free to hand back less than asked for.
                    using (var buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        png = buffer.ToArray();
                    }
                }

                return LoadIcon(png, filter, found);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FruitToolbar] loading icon '{resourceName}' failed: {e.Message}");
                return null;
            }
        }

        /// <summary>A sprite from PNG bytes you already have.</summary>
        public static Sprite LoadIcon(byte[] png, FilterMode filter = FilterMode.Bilinear,
                                      string name = "FruitLib icon")
        {
            if (png == null || png.Length == 0) return null;

            Texture2D tex = null;
            try
            {
                // Size does not matter here; LoadImage replaces the texture with the PNG's
                // own dimensions and format.
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                if (!ImageConversion.LoadImage(tex, png))
                {
                    MelonLogger.Warning($"[FruitToolbar] '{name}' is not a readable PNG.");
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }

                tex.filterMode = filter;
                tex.wrapMode   = TextureWrapMode.Clamp;

                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                           new Vector2(0.5f, 0.5f), 100f);

                tex.name    = name;
                sprite.name = name;

                // Both marked to survive, for the reason described on MakeSolidIcon: a mod
                // loads its icons at startup, and the first scene load would otherwise be
                // free to collect them.
                tex.hideFlags    = HideFlags.HideAndDontSave;
                sprite.hideFlags = HideFlags.HideAndDontSave;

                return sprite;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FruitToolbar] decoding '{name}' failed: {e.Message}");
                if (tex != null) UnityEngine.Object.Destroy(tex);
                return null;
            }
        }

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
                    tex.SetPixel(x, y, d > edge ? clear : (d > edge - 4f ? color * 0.6f : color));
                }
            }

            tex.Apply();

            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            tex.name    = $"FruitLib icon {ColorUtility.ToHtmlStringRGB(color)}";
            sprite.name = tex.name;

            // Both marked to survive. A texture and a sprite built at runtime belong to no
            // scene and no asset bundle, so the first load that runs UnloadUnusedAssets is
            // free to collect them - and a mod registers its icon at startup, long before the
            // first scene. What reaches the toolbar afterwards is a destroyed object, which
            // compares equal to null and reads as "the mod never supplied one".
            tex.hideFlags    = HideFlags.HideAndDontSave;
            sprite.hideFlags = HideFlags.HideAndDontSave;

            return sprite;
        }

        internal static IGodInventoryItemData SpareItem;

        // On 0.1 there was also a `Sfx` property here, handing out the SFX service read off
        // a toolbar slot view. 0.14 removed that field from ToolbarItemSlotView along with
        // the setter that filled it, so the route is gone; FruitSfx now finds the service
        // on the cursor tool by itself and nothing else ever called this.

        internal static GodToolbarService Service;
        internal static int NativeBase = -1;
        internal static bool Audited;

        private static ToolbarView _view;
        private static int _rebindCountdown;

        internal static void Tick()
        {
            if (_items.Count == 0) return;

            if (_view == null)
            {
                if (--_rebindCountdown > 0) return;
                _rebindCountdown = 30;
                _view = FruitScene.First<ToolbarView>();
                if (_view == null) return;
            }

            EnsureSlotsVisible();
            PollSelectionKeys();
            LogServiceOnce();
        }

        private static void PollSelectionKeys()
        {
            // Number keys typed in a menu are not slot changes.
            if (FruitMenu.BlocksGameplayInput) return;

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
            FruitSfx.Reset();
            _lastSelected = -1;
            _loggedResolution = false;
            _slotViewGrowthFailed = false;
            _loggedStrip = false;
            Audited = false;
            foreach (var item in _items)
            {
                item.Drawn = false;
                item.DrawBroken = false;
                item.Settling = 0;
                item.IconLogged = false;

                // The descriptor is deliberately kept. It used to be cleared here so a new
                // scene would rebuild it, which was wrong twice over: it is marked
                // HideAndDontSave and survives a scene load perfectly well, and this runs
                // AFTER the toolbar has initialised, not before. GodToolbarService.OnInitialize
                // fires while the scene's objects are waking; OnSceneWasInitialized fires once
                // they all have. So clearing it here threw away the descriptor the prefix had
                // just built, and SetDisplay then had nothing to write a new name or icon into
                // for the rest of that scene.
            }
        }

        private static int  _lastSelected = -1;
        private static bool _loggedResolution;
        private static void LogServiceOnce()
        {
            if (_loggedResolution) return;
            ResolveService();   // logs once on the first successful resolve
        }

        private static void SetSelected(int now, string source)
        {
            if (now == _lastSelected) return;
            int previous = _lastSelected;
            _lastSelected = now;

            var left  = FindBySlot(previous);
            var enter = FindBySlot(now);
            if (left != null || enter != null)
                MelonLogger.Msg($"[FruitToolbar] selection {previous} -> {now} (via {source})");

            if (enter != null)
            {
                // The sound the game plays when you change tools. The visual side of a switch
                // belongs to the item's 3D model, which a FruitLib item does not have - but
                // the sound is the toolbar's, not the model's, and it costs one call.
                FruitSfx.PlayUI(UISFXType.ToolbarItemSwitch);
                SelectNatively(now);
            }

            left?.OnDeselected?.Invoke(previous);
            enter?.OnSelected?.Invoke(now);
        }

        private static void SelectNatively(int slot)
        {
            var svc = ResolveService();
            if (svc == null) return;
            try { svc.TrySelect(slot); }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] TrySelect({slot}) failed: {e.Message}"); }
        }

        /// <summary>
        /// The live GodToolbarService.
        ///
        /// On 0.1 this preferred a back-reference off ToolbarView. 0.14 dropped that field,
        /// leaving the scene search and the OnInitialize capture — which is the one that
        /// actually always works, because the patch runs before anything can select a slot.
        /// </summary>
        private static GodToolbarService ResolveService()
        {
            var svc = FruitScene.First<GodToolbarService>();
            if (svc == null) svc = Service;          // OnInitialize capture, last resort
            if (svc != null && !_loggedResolution) LogResolution(svc);

            return svc;
        }

        private static void LogResolution(GodToolbarService svc)
        {
            _loggedResolution = true;
            try
            {
                int count = FruitScene.Count<GodToolbarService>();
                string viewBound = FruitScene.First<GodToolbarService>() != null
                    ? "FindObjectOfType" : "OnInitialize capture";

                string slotIdx;
                try
                {
                    var s = svc.SelectedSlotGodInventoryItemData;
                    slotIdx = s == null ? "selectedSlot=null" : s.SlotIndex.ToString();
                }
                catch (Exception e) { slotIdx = $"threw:{e.Message}"; }

                string capacity = "n/a", selected = "n/a";
                try
                {
                    var model = svc.m_toolbarModel;
                    if (model != null)
                    {
                        capacity = model.m_capacity.ToString();
                        selected = model.SelectedSlotIndex.ToString();
                    }
                }
                catch { }

                MelonLogger.Msg($"[FruitToolbar] service via {viewBound} id={svc.GetInstanceID()} " +
                                $"instancesInScene={count} selectedSlot.SlotIndex={slotIdx} " +
                                $"Capacity={svc.Capacity} SelectedSlotIndex={svc.SelectedSlotIndex} " +
                                $"model.capacity={capacity} model.selected={selected}");
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] resolution log failed: {e.Message}"); }
        }

        private static bool _slotViewGrowthFailed;
        private static bool _loggedStrip;

        /// <summary>
        /// Widen the toolbar strip to <paramref name="needed"/> slots.
        ///
        /// Two separate things have to be true and only one of them is an array. m_slotsViews
        /// has to hold that many views, which on a short strip means cloning one. And the
        /// view's own Count - what SetCapacity sets, and what DrawSlotData measures an index
        /// against before it throws - has to have been raised to match.
        ///
        /// The second is the one that bites on the Steam demo build. Its strip is already
        /// built long enough for a custom slot, so the cloning half never runs, but the
        /// presenter had only ever told the view about the five slots the game itself uses -
        /// so drawing into the sixth threw once a frame and drew nothing.
        /// </summary>
        private static void EnsureSlotViewCapacity(int needed)
        {
            if (_slotViewGrowthFailed) return;

            var slots = _view.m_slotsViews;
            if (slots == null) return;

            if (!_loggedStrip)
            {
                _loggedStrip = true;
                MelonLogger.Msg($"[FruitToolbar] strip: views={slots.Length} count={StripCount()} needed={needed}");
            }

            if (slots.Length < needed && !GrowSlotViews(slots, needed)) return;

            try
            {
                int count = StripCount();
                if (count >= 0 && count < needed)
                {
                    _view.SetCapacity(needed);
                    MelonLogger.Msg($"[FruitToolbar] strip capacity {count} -> {needed}");
                }
            }
            catch (Exception e)
            {
                _slotViewGrowthFailed = true;
                MelonLogger.Warning($"[FruitToolbar] SetCapacity({needed}) failed: {e.Message} — extra slots will not render.");
            }
        }

        /// <summary>The view's live slot count, or -1 if it will not say.</summary>
        private static int StripCount()
        {
            try { return _view.Count; }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FruitToolbar] reading ToolbarView.Count failed: {e.Message}");
                return -1;
            }
        }

        private static bool GrowSlotViews(Il2CppReferenceArray<ToolbarItemSlotView> slots, int needed)
        {
            // m_selectedSlot when there is one, otherwise the last slot: either is a fully
            // configured ToolbarItemSlotView, which is all the clone needs.
            var proto = _view.m_selectedSlot != null
                ? _view.m_selectedSlot
                : (slots.Length > 0 ? slots[slots.Length - 1] : null);

            if (proto == null)
            {
                _slotViewGrowthFailed = true;
                MelonLogger.Warning($"[FruitToolbar] need {needed} slot views but have {slots.Length} and no prototype to clone — extra slots will not render.");
                return false;
            }

            try
            {
                int old = slots.Length;
                var parent = proto.transform.parent != null ? proto.transform.parent : slots[old - 1].transform.parent;

                // 0.1 also pushed the SFX service into each cloned view here, because
                // selecting a view without one threw. 0.14 took that field off the view
                // entirely, so a clone needs nothing beyond the clone.
                var newSlots = new Il2CppReferenceArray<ToolbarItemSlotView>(needed);
                for (int i = 0; i < old; i++) newSlots[i] = slots[i];

                for (int i = old; i < needed; i++)
                {
                    var clone = UnityEngine.Object.Instantiate(proto, parent, false);
                    clone.gameObject.name = $"ToolbarItemSlotView ({i})";
                    newSlots[i] = clone;
                }
                _view.m_slotsViews = newSlots;

                var slotsData = _view.m_slotsData;
                if (slotsData != null && slotsData.Length < needed)
                {
                    var grown = new Il2CppReferenceArray<ToolbarView.ToolbarViewItemSlotData>(needed);
                    for (int i = 0; i < slotsData.Length; i++) grown[i] = slotsData[i];
                    for (int i = slotsData.Length; i < needed; i++)
                        grown[i] = new ToolbarView.ToolbarViewItemSlotData(new SlotDrawData(string.Empty, null));
                    _view.m_slotsData = grown;
                }

                RecentreSlotRow(parent, slots[0], needed - old);

                MelonLogger.Msg($"[FruitToolbar] grew slot views {old} -> {needed}");
                return true;
            }
            catch (Exception e)
            {
                _slotViewGrowthFailed = true;
                MelonLogger.Warning($"[FruitToolbar] could not grow slot views: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set false if you'd rather position the widened toolbar yourself, or nudge it with
        /// <see cref="SlotRowOffset"/>.
        /// </summary>
        public static bool AutoCentreSlotRow = true;

        /// <summary>Extra nudge applied to the slot row after auto-centring.</summary>
        public static Vector2 SlotRowOffset = Vector2.zero;

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

        private static void CacheSlotDrawData(int idx, FruitToolbarItem item)
        {
            try
            {
                var slotsData = _view.m_slotsData;
                if (slotsData == null || idx >= slotsData.Length) return;

                // The Steam demo build cut ToolbarViewItemSlotData down to its draw data and
                // made that read-only, so a changed slot is a fresh entry rather than an edit.
                // It no longer holds the slot view either - m_slotsData and m_slotsViews pair
                // up by index, which is how every caller already treated them.
                slotsData[idx] = new ToolbarView.ToolbarViewItemSlotData(new SlotDrawData(item.Name, item.IconData));
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] caching draw data for slot {idx} failed: {e.Message}"); }
        }

        internal static void RefreshDisplay(FruitToolbarItem item)
        {
            if (item == null) return;

            try
            {
                if (item.Descriptor != null)
                {
                    item.Descriptor.m_objectName = item.Name;

                    if (item.Icon != null)
                    {
                        var src  = item.Descriptor.m_iconData;
                        var icon = ScriptableObject.CreateInstance<SerializedIconData>();
                        icon.m_sprite = item.Icon;
                        icon.m_offset = src != null ? src.m_offset : Vector2.zero;
                        icon.m_scale  = src != null ? src.m_scale  : Vector2.one;
                        icon.m_color  = src != null ? src.m_color  : Color.white;
                        icon.name      = item.Name + " Icon";
                        icon.hideFlags = HideFlags.HideAndDontSave;

                        item.Descriptor.m_iconData = icon;
                    }

                    item.IconData = item.Descriptor.IconData;
                }
                else MelonLogger.Warning($"[FruitToolbar] '{item.Name}' has no descriptor to refresh; " +
                                         "its name and icon will not change until the scene reloads.");

                item.Drawn = false;

                var sprite = item.Icon;
                MelonLogger.Msg($"[FruitToolbar] refreshed '{item.Name}' " +
                                $"(icon={(sprite == null ? "none" : sprite.name)}, redraw queued)");
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] refreshing '{item.Name}' failed: {e.Message}"); }
        }

        private static FruitToolbarItem FindBySlot(int slot)
        {
            if (slot < 0) return null;
            foreach (var item in _items)
                if (item.SlotIndex == slot) return item;
            return null;
        }

        /// <summary>
        /// Draws a custom item into its slot, and gives up on that slot rather than on the
        /// frame if the view will not have it.
        ///
        /// EnsureSlotsVisible runs every update and retries anything not yet Drawn, so a
        /// throw here repeats for as long as the game is open and buries every other line in
        /// the log. The icon is dropped first and the name tried on its own, because a slot
        /// with a label and no picture is still a usable slot.
        /// </summary>
        private static bool TryDraw(FruitToolbarItem item, int idx)
        {
            try
            {
                _view.DrawSlotData(new SlotDrawData(item.Name, item.IconData), idx);
                return true;
            }
            catch (Exception viaView)
            {
                // The view's own draw is all-or-nothing: it sets the icon as part of the same
                // call, so one icon it will not accept costs the whole slot. The slot view can
                // be asked directly instead, which splits the two apart - a bad icon then
                // costs the icon and nothing else.
                LogIcon(item);

                try
                {
                    var slot = _view.m_slotsViews[idx];

                    if (item.IconData != null) slot.SetItemIcon(item.IconData);
                    else                       slot.RemoveItemIcon();

                    MelonLogger.Warning($"[FruitToolbar] the view refused slot {idx} for '{item.Name}' " +
                                        $"({viaView.Message}); drawn through the slot instead.");
                    return true;
                }
                catch (Exception viaSlot)
                {
                    try
                    {
                        // Last try: the slot without an icon at all. A named, pickable slot
                        // with no picture still works; a missing one does not.
                        _view.m_slotsViews[idx].RemoveItemIcon();
                        MelonLogger.Warning($"[FruitToolbar] slot {idx} ('{item.Name}') would not take an icon " +
                                            $"({viaSlot.Message}); left without one.");
                        return true;
                    }
                    catch (Exception bare)
                    {
                        item.DrawBroken = true;
                        MelonLogger.Warning($"[FruitToolbar] slot {idx} for '{item.Name}' could not be drawn at all " +
                                            $"({bare.Message}); leaving it alone.");
                        return false;
                    }
                }
            }
        }

        /// <summary>
        /// Pushes a redrawn slot into the display that shows the item in hand.
        ///
        /// The strip is not the only place a slot appears. ToolbarView keeps a separate
        /// m_selectedSlot for whatever is currently held, and DrawSlotData only writes to the
        /// strip entry - so an item that changes its own icon while it is the selected one
        /// updates in the strip and not in the player's hand. Which is the half they are
        /// looking at.
        /// </summary>
        private static void RedrawSelected(int idx)
        {
            try { _view.DrawSlotTitle(idx); }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] DrawSlotTitle({idx}) failed: {e.Message}"); }

            try { _view.SelectSlot(idx); }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] SelectSlot({idx}) failed: {e.Message}"); }
        }

        /// <summary>Sets the number that picks this slot. Keys run 1..n over slots 0..n-1.</summary>
        private static void SetKeyLabel(ToolbarItemSlotView slot, int idx)
        {
            try
            {
                if (slot.m_keySlot != null) slot.m_keySlot.SetKeyTitle((idx + 1).ToString());
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] key label for slot {idx} failed: {e.Message}"); }
        }

        /// <summary>
        /// Says what is actually in an item's icon, once, when the view has refused it.
        ///
        /// The slot dump showed the drawer on every view fully wired, image and pivot both -
        /// so the refusal is about what is being handed to it, not about where it is going.
        /// </summary>
        private static void LogIcon(FruitToolbarItem item)
        {
            if (item.IconLogged) return;
            item.IconLogged = true;

            try
            {
                if (item.IconData == null) { MelonLogger.Msg($"[FruitToolbar] '{item.Name}' has no icon data at all."); return; }

                var sprite = item.IconData.Sprite;
                MelonLogger.Msg($"[FruitToolbar] '{item.Name}' icon: sprite={(sprite == null ? "null" : sprite.name)} " +
                                $"scale={item.IconData.Scale} colour={item.IconData.Color} " +
                                $"mod-supplied={(item.Icon != null)}");
            }
            catch (Exception e) { MelonLogger.Msg($"[FruitToolbar] '{item.Name}' icon could not be read: {e.Message}"); }
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
                if (idx < 0 || idx >= slots.Length || item.DrawBroken) continue;

                var slot = slots[idx];
                if (slot == null) continue;

                if (item.Drawn && slot.gameObject.activeInHierarchy) continue;

                if (!slot.gameObject.activeSelf) slot.gameObject.SetActive(true);

                // Activate now, draw a frame later. ToolbarItemSlotView is a ManagedBehaviour
                // that builds its icon drawer in OnStart, and Unity does not run Start when an
                // object is activated - it runs it just before the next Update. Drawing into
                // the slot in the same frame reaches a view whose drawer does not exist yet,
                // and the game throws out of IconDataDrawer.SetIcon.
                //
                // The slots the game itself uses have been awake since the scene loaded, which
                // is why this only ever showed up on a slot past the native ones. The counter
                // restarts whenever the strip is hidden, because a slot under an inactive
                // parent has not run Start either however long it has been switched on.
                if (!slot.gameObject.activeInHierarchy) { item.Settling = 0; continue; }
                if (item.Settling < 1) { item.Settling++; continue; }

                // Before the draw, not after it. The label is what tells the player which
                // number picks this slot, and it has nothing to do with the icon - but it
                // used to sit past an early return, so a slot whose icon the view refused
                // kept whatever number the strip was authored with. That is how slot 5 came
                // to show the same 5 as the slot before it.
                SetKeyLabel(slot, idx);

                if (!TryDraw(item, idx)) continue;

                CacheSlotDrawData(idx, item);

                if (idx == _lastSelected) RedrawSelected(idx);

                item.Drawn = true;
                MelonLogger.Msg($"[FruitToolbar] Activated + drew slot {idx} ('{item.Name}')");
            }
        }
    }

    // ── Capture a reusable native item to back the injected slots ────────────────
    [HarmonyPatch(typeof(NativeGIITools), nameof(NativeGIITools.Register))]
    internal static class FruitToolbar_ToolsCapture
    {
        static void Postfix(NativeGIITools __instance)
        {
            try
            {
                if (__instance.HumanSpawner == null) return;
                FruitToolbar.SpareItem = __instance.HumanSpawner;
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] HumanSpawner capture failed: {e.Message}"); }
        }
    }

    // ── After the game has populated: say what it actually claimed ───────────────

    /// <summary>
    /// Reports the real slot layout once the populator has finished, and complains if a
    /// FruitLib item landed on top of something.
    ///
    /// <see cref="FruitToolbar_OnInitializePatch"/> has to decide where custom items go
    /// before any of this exists, from the serialized arrays alone. This is the check on
    /// that decision, and it is the line to read first when an item renders over a native
    /// tool: it names the occupied slots, so the number NativeBaseOverride wants is right
    /// there rather than something to work out from a screenshot.
    /// </summary>
    [HarmonyPatch(typeof(GodToolbarItemsPopulator), nameof(GodToolbarItemsPopulator.Start))]
    internal static class FruitToolbar_PopulateAudit
    {
        static void Postfix(GodToolbarItemsPopulator __instance)
        {
            if (FruitToolbar.Items.Count == 0 || FruitToolbar.Audited) return;
            FruitToolbar.Audited = true;

            try
            {
                var svc = __instance.m_godToolbarService;
                if (svc == null) { MelonLogger.Warning("[FruitToolbar] audit: populator has no service."); return; }

                int capacity = svc.Capacity;
                var occupied = new List<int>();
                for (int i = 0; i < capacity && i < 64; i++)
                {
                    bool taken;
                    try { taken = svc.IsSlotOccupied(i); }
                    catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] audit: IsSlotOccupied({i}) threw: {e.Message}"); break; }
                    if (taken) occupied.Add(i);
                }

                MelonLogger.Msg($"[FruitToolbar] audit: capacity={capacity} occupied=[{string.Join(",", occupied)}] " +
                                $"nativeBase={FruitToolbar.NativeBase}");

                DumpSlots();

                foreach (var item in FruitToolbar.Items)
                {
                    if (item.SlotIndex < 0 || !occupied.Contains(item.SlotIndex)) continue;
                    if (item.SlotIndex >= FruitToolbar.NativeBase) continue;   // ours, filled by us

                    MelonLogger.Warning(
                        $"[FruitToolbar] '{item.Name}' is on slot {item.SlotIndex}, which the game " +
                        $"already claimed. Set FruitToolbar.NativeBaseOverride = {occupied[occupied.Count - 1] + 1} " +
                        $"before the toolbar initialises to move custom items clear of it.");
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] audit failed: {e.Message}"); }
        }

        /// <summary>
        /// What the strip's slot views actually say and carry.
        ///
        /// Two questions this answers. The key label the game puts on each slot, which says
        /// what number a custom slot ought to show rather than leaving FruitLib to guess from
        /// its index - the cursor takes slot 0 without taking a key, so index and key parted
        /// company. And whether each slot's icon drawer is wired, which is the thing that
        /// throws out of DrawSlotData on the slots past the ones the game itself uses.
        /// </summary>
        private static void DumpSlots()
        {
            var view = FruitScene.First<ToolbarView>();
            if (view == null) { MelonLogger.Msg("[FruitToolbar] slots: no ToolbarView yet."); return; }

            var slots = view.m_slotsViews;
            if (slots == null) { MelonLogger.Msg("[FruitToolbar] slots: the view has no slot array."); return; }

            var sb = new StringBuilder();
            sb.AppendLine($"[FruitToolbar] slots: {slots.Length} view(s), ToolbarView.Count={SafeCount(view)}");

            for (int i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                if (slot == null) { sb.AppendLine($"    [{i}] null"); continue; }

                string key    = "?";
                string drawer = "none";
                try
                {
                    var keySlot = slot.m_keySlot;
                    if (keySlot != null)
                    {
                        key = keySlot.m_keyTitle != null ? $"'{keySlot.m_keyTitle.text}'" : "no label";

                        var icon = keySlot.m_iconDataDrawer;
                        drawer = icon == null
                            ? "missing"
                            : $"image={icon.m_iconImage != null} pivot={icon.m_iconOffsetPivot != null}";
                    }
                }
                catch (Exception e) { key = $"threw:{e.Message}"; }

                sb.AppendLine($"    [{i}] active={slot.gameObject.activeInHierarchy} key={key} drawer={drawer}");
            }

            MelonLogger.Msg(sb.ToString());
        }

        private static int SafeCount(ToolbarView view)
        {
            try { return view.Count; } catch { return -1; }
        }
    }

    // ── Grow the toolbar: registration entries, capacity, and the slot container ──
    [HarmonyPatch(typeof(GodToolbarService), nameof(GodToolbarService.OnInitialize))]
    internal static class FruitToolbar_OnInitializePatch
    {
        static void Prefix(GodToolbarService __instance)
        {
            try
            {
                if (FruitToolbar.Items.Count == 0) return;

                var populator = FruitScene.First<GodToolbarItemsPopulator>();
                if (populator == null) return;

                var items = populator.m_itemsToPopulate;
                if (items == null || items.Length == 0) return;

                if (FruitToolbar.NativeBase < 0) FruitToolbar.NativeBase = DiscoverNativeBase(populator);

                int baseLen = FruitToolbar.NativeBase;
                int target  = baseLen + FruitToolbar.Items.Count;
                FruitToolbar.AssignSlots(baseLen);

                __instance.m_capacity = target;

                // 0.1 called a resize method on the model here. 0.14 has no equivalent — the
                // model's capacity is a plain field, and the Postfix below rebuilds the slot
                // array to match, which is the part that actually mattered.
                var model = __instance.m_toolbarModel;
                if (model != null) model.m_capacity = target;

                // Custom items are deliberately NOT appended to m_itemsToPopulate.
                //
                // On 0.1 and 0.14 they had to be: Populate() was what turned a registration
                // entry into a filled slot. The Steam demo build put a TryResolve() in front
                // of that, and TryResolve dereferences the entry's prefab - which for a
                // FruitLib entry is null, because there is no behaviour to spawn. That
                // NullReferenceException came back out through the populator's Start and
                // aborted the whole native pass part-way, taking PopulateCursor with it.
                //
                // Nothing was lost by dropping the entries. What actually makes a custom
                // slot work is the Postfix below, which widens the model's slot array, and
                // EnsureSlotsVisible, which draws the slot itself - neither goes through
                // Populate. The entry only ever existed to carry a descriptor, so that is
                // all we build now.
                SerializedGIIRegistrationData template = null;
                foreach (var e in items)
                    if (e != null && e.m_category != null) { template = e; break; }
                if (template == null) template = items[items.Length - 1];

                foreach (var fi in FruitToolbar.Items)
                {
                    // OnInitialize fires more than once per scene. Rebuilding would leak a
                    // ScriptableObject pair each time and swap the icon out from under a slot
                    // that is already drawing it. A descriptor destroyed with its scene
                    // compares equal to null, so a genuinely new scene still rebuilds.
                    if (fi.Descriptor != null) continue;

                    var desc = BuildDescriptor(template, fi);
                    if (desc == null) continue;

                    fi.IconData = desc.IconData;
                    MelonLogger.Msg($"[FruitToolbar] Registered '{fi.Name}' (id '{fi.Id}') at slot {fi.SlotIndex}");
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] OnInitialize prefix failed: {e}"); }
        }

        static void Postfix(GodToolbarService __instance)
        {
            try
            {
                if (FruitToolbar.Items.Count == 0 || FruitToolbar.NativeBase < 0) return;

                FruitToolbar.Service = __instance;

                if (FruitToolbar.SpareItem == null)
                {
                    MelonLogger.Warning("[FruitToolbar] no spare IGodInventoryItemData captured — slots will draw but not select.");
                    return;
                }

                var model = __instance.m_toolbarModel;
                var slots = model?.m_itemsSlots;
                if (slots == null) return;

                int target = FruitToolbar.NativeBase + FruitToolbar.Items.Count;
                if (slots.Length >= target) return;

                var arr = new Il2CppReferenceArray<GenericObjectHandler<IGodInventoryItemData>>(target);
                for (int i = 0; i < slots.Length; i++) arr[i] = slots[i];
                for (int i = slots.Length; i < target; i++)
                    arr[i] = new GenericObjectHandler<IGodInventoryItemData>
                    {
                        HeldObject = FruitToolbar.SpareItem,
                        Occupied   = false,
                    };

                model.m_itemsSlots = arr;
                model.m_capacity   = target;

                MelonLogger.Msg($"[FruitToolbar] Slot container expanded {slots.Length} -> {target}.");
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] OnInitialize postfix failed: {e}"); }
        }

        /// <summary>
        /// The first toolbar slot the game has not claimed for itself.
        ///
        /// m_itemsToPopulate used to be the whole story, and on 0.14 it was: four entries,
        /// four slots, custom items started at four. The Steam demo build gave the cursor a
        /// toolbar slot of its own - m_cursor, PopulateCursor and CURSOR_SLOT_INDEX are all
        /// new there - and the cursor is not in that array. Counting the array alone is how
        /// the first custom item ended up sitting on top of it.
        ///
        /// This counts claims rather than reading indexes, so it does not care whether the
        /// cursor sits at the head of the strip or the tail - only that it takes a slot. The
        /// CURSOR_SLOT_INDEX clamp afterwards covers a cursor parked past the populated
        /// entries with a gap in between, which counting alone would miss.
        /// </summary>
        private static int DiscoverNativeBase(GodToolbarItemsPopulator populator)
        {
            if (FruitToolbar.NativeBaseOverride >= 0)
            {
                MelonLogger.Msg($"[FruitToolbar] native base {FruitToolbar.NativeBaseOverride} (NativeBaseOverride)");
                return FruitToolbar.NativeBaseOverride;
            }

            int entries = populator.m_itemsToPopulate != null ? populator.m_itemsToPopulate.Length : 0;

            bool hasCursor = false;
            try { hasCursor = populator.m_cursor != null; }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] reading m_cursor failed: {e.Message}"); }

            int claimed = entries + (hasCursor ? 1 : 0);

            // Bounded on purpose. CURSOR_SLOT_INDEX is a static read out of il2cpp, and a
            // sentinel value there - or a cursor parked far off the end of the strip - would
            // otherwise push the base somewhere that needs dozens of cloned slot views.
            int cursorSlot = -1;
            try { cursorSlot = GodToolbarService.CURSOR_SLOT_INDEX; }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] reading CURSOR_SLOT_INDEX failed: {e.Message}"); }

            string clamp = "";
            if (cursorSlot >= claimed && cursorSlot < claimed + 8)
            {
                clamp = $", clamped up to cursor slot {cursorSlot}";
                claimed = cursorSlot + 1;
            }

            MelonLogger.Msg($"[FruitToolbar] native base {claimed} " +
                            $"(m_itemsToPopulate={entries}, cursor={(hasCursor ? "yes" : "no")}, " +
                            $"CURSOR_SLOT_INDEX={cursorSlot}{clamp})");
            return claimed;
        }

        private static Sprite _placeholder;

        /// <summary>
        /// A plain disc, for a slot whose mod supplied no icon.
        ///
        /// The drawer will not take a null sprite - it throws rather than drawing an empty
        /// slot - and borrowing one off a native registration entry does not help, because
        /// those carry no sprite either on this build. So there is always something to hand
        /// it, even if it is only a grey circle.
        /// </summary>
        private static Sprite Placeholder()
        {
            if (_placeholder != null) return _placeholder;

            try { _placeholder = FruitToolbar.MakeSolidIcon(new Color(0.65f, 0.65f, 0.65f, 1f)); }
            catch (Exception e) { MelonLogger.Warning($"[FruitToolbar] could not build a placeholder icon: {e.Message}"); }

            return _placeholder;
        }

        /// <summary>
        /// The name and icon a custom slot draws with, modelled on a native entry's.
        ///
        /// Held on the item rather than handed to the game: SetDisplay rewrites it later,
        /// and EnsureSlotsVisible reads it every time it draws the slot.
        /// </summary>
        private static SerializedObjectDescriptorWithIcon BuildDescriptor(
            SerializedGIIRegistrationData template, FruitToolbarItem fi)
        {
            try
            {
                var srcIcon = template != null && template.m_objectDescriptor != null
                    ? template.m_objectDescriptor.m_iconData
                    : null;

                var icon = ScriptableObject.CreateInstance<SerializedIconData>();
                icon.m_sprite = fi.Icon != null ? fi.Icon
                              : (srcIcon != null && srcIcon.m_sprite != null ? srcIcon.m_sprite : Placeholder());
                icon.m_offset = srcIcon != null ? srcIcon.m_offset : Vector2.zero;
                icon.m_scale  = srcIcon != null ? srcIcon.m_scale  : Vector2.one;
                icon.m_color  = srcIcon != null ? srcIcon.m_color  : Color.white;
                icon.name      = fi.Name + " Icon";
                icon.hideFlags = HideFlags.HideAndDontSave;

                var desc = ScriptableObject.CreateInstance<SerializedObjectDescriptorWithIcon>();
                desc.m_objectName = fi.Name;
                desc.m_description = string.Empty;
                desc.m_iconData    = icon;
                desc.name          = fi.Name + " Descriptor";
                desc.hideFlags     = HideFlags.HideAndDontSave;
                fi.Descriptor = desc;   // kept so SetDisplay can rewrite label/icon later
                return desc;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FruitToolbar] building the descriptor for '{fi.Name}' failed: {e}");
                return null;
            }
        }

    }
}
